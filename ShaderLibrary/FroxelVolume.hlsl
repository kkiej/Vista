#ifndef VISTA_FROXEL_VOLUME_INCLUDED
#define VISTA_FROXEL_VOLUME_INCLUDED

// ============================================================================
//  近层体积雾的 froxel 体（Step 3 档 A，PC 主线）
//
//  三张 3D 表，屏幕比例 XY（默认 /8）× 64 深度切片：
//    注入（当前帧）  RGBA16F  (σ_s·L 的散射源项 rgb, σ_t)   —— **局部**量
//    注入（历史）    RGBA16F  同上，供 #22 的时间重投影
//    积分            RGBA16F  (累积内散射 rgb, **1 − 累积透射率**) —— **相机原点的累积**量
//
//  ------------------------------------------------------------------ alpha 存 1 − T
//  HDRP（_VBufferLighting）与 UE5（IntegratedLightScattering）都存 T 本身，这里刻意相反。
//  理由是**清空态**：这张表在被写之前是全 0。
//    · 存 T ⇒ 读到 T = 0 ⇒ L_final = L_bg·0 + 0 = **全黑**。
//      也就是「表没被写」这个本来只该丢掉雾的故障，被升级成最坏的可见失败。
//    · 存 1 − T ⇒ 读到 1 − T = 0 ⇒ T = 1 ⇒ L_final = L_bg = **透传**。
//  读端多一次减法（L_final = L_bg·(1 − a) + rgb）。fp16 量化两者完全等价 ——
//  a ↦ 1 − a 是仿射映射，三线性插值也是仿射的，误差上界同为 4.88e-4
//  = 背景亮度的 0.049%，远在 Weber 1% 之下。
//
//  ------------------------------------------------------------------ 分段积分复用 AP 那一份
//  单段的解析内散射 ∫₀^dt S·e^{−σ·s} ds 由 `VistaSegmentIntegral`
//  （AtmosphereScattering.hlsl）计算，**不在这里再写一份** —— 它已经处理了
//  短步段的灾难性相消（x ≤ 1e-4 走截断展开）与 σ = 0 的 NaN 闸。
//  所以本文件只提供切片几何，不提供积分核。
//
//  为什么重投影必须发生在**积分之前**：注入表里的 σ_s / σ_t 是空间中某一点的局部
//  属性，换个相机位置它还是那个值，所以可重投影；而积分表是「从相机走到这里」的
//  累积量，把它重投影到另一个相机位置在物理上没有意义。HDRP 重投影的是
//  VBufferLighting（注入），不是积分结果。
//
//  ------------------------------------------------------------------ 与 AP LUT 的分工
//  近层 [0, handoff]，AP LUT (handoff, 32 km]。handoff = 最后一片存的距离，
//  由 CPU 侧的 VistaFroxelVolumeDesc.handoffMeters 推出来并写进 AP 的 near。
//  分界的依据不是分辨率，而是「能不能负担逐 froxel 采样阴影贴图」——
//  光柱完全来自那一步，而级联阴影铺不到几十 km。详见 AerialPerspective.hlsl 的头注。
//
//  ------------------------------------------------------------------ 切片分布：纯指数
//    e(d) = ln(d / near) · rcp(ln(far / near))          ← **e 直接就是纹理 w 坐标**
//    d(e) = near · exp(e · ln(far / near))
//  切片 i 的纹素中心在 w = (i + 0.5)/N，存的是「相机到 d_i 的累积」，
//  d_i = near · (far/near)^((i+0.5)/N)。
//
//  为什么把「存的距离」放在纹素中心上，而不是像 HDRP 那样存分段远平面：
//  读端是逐像素按深度采样，采样坐标就是 e(d)。存远平面的话
//  e(t_{i+1}) = (i+1)/N 而纹素中心在 (i+0.5)/N，读的时候必须回退半个纹素 ——
//  HDRP 那个已知的 half-slice bias 就是这么来的。这里一个偏移都不用记。
//
//  代价：体积实际的远端是 d_{N-1} = far·(far/near)^(-0.5/N)，不是 far。
//  默认档（near 0.3 m / far 64 m / N 64）下 61.374 m，比 far 少 2.6 m。
//  AP 的接手点必须是这个数。
//
//  ------------------------------------------------------------------ 没有「与级联对齐」
//  原计划里写过「切片对齐到级联边界」，那句话**没有定义** ——
//  URP 的级联剔除用的是以相机为心的**球**（_CascadeShadowSplitSpheres），
//  而切片是垂直于视线的**平面**。球与平面在视锥里不重合，「对齐」无从谈起。
//  HDRP 与 UE5 都不处理这道缝（逐 froxel 直接走 EvaluateShadow_Directional，
//  和不透明物同一条路），理由是体积雾本身是低频的。
// ============================================================================

// ----------------------------------------------------------------------------
//  常量
//
//  失能态：本 cbuffer 全零时 logRatio = 0、rcpLogRatio = 0，于是 e ≡ 0、d ≡ 0。
//  不是 NaN、不是随机值 —— 与 VistaFogCB 同一条「关掉 = 零态」的约定。
//  （CPU 侧保证 far > near·1.001，所以线上路径的 logRatio 恒 > 0；
//   这里说的是「一个字节都没下发」那种情况。）
// ----------------------------------------------------------------------------
CBUFFER_START(VistaFroxelCB)
    // x: near (m), y: far (m), z: ln(far/near), w: 1/ln(far/near)
    float4 _VistaFroxelRange;
    // xyz: 尺寸 (w, h, N), w: 1/N
    float4 _VistaFroxelSize;
    // xyz: 相机世界位置 (m)，w: 阴影贴图是否已绑定（1 = 是，0 = 否 ⇒ 阴影恒为 1）。
    // 为什么不复用 _VistaViewPosKm 或 URP 的 _WorldSpaceCameraPos，见 VistaShaderIDs 的注释。
    float4 _VistaFroxelCameraWS;
CBUFFER_END

// ----------------------------------------------------------------------------
//  时间重投影与抖动（#22）
//
//  失能态同样是全零：历史权重 0（纯本帧）、抖动幅度 0（恒在格心）、
//  上一帧范围全零（prevRcpLog = 0 ⇒ 历史片坐标恒 0，不是 NaN）。
//  历史权重那一位的零态**必须**是「不用历史」—— 反过来的话，一个没下发的帧
//  会去混一张未初始化的 fp16 显存，而那里面可能是 NaN。
//
//  单独一个 cbuffer 而不是并进 VistaFroxelCB：那一个由
//  VistaFroxelVolume.Prepare 每帧无条件下发（包括立即模式的自检），
//  而这一组只有注入核用、且只在有相机时才有意义。合并之后，
//  「自检路径拿到了一个上一帧主相机留下的矩阵」就成了一件看不出来的事 ——
//  那正是 _WorldSpaceCameraPos 被绕开的同一条理由。
// ----------------------------------------------------------------------------
CBUFFER_START(VistaFroxelReprojCB)
    // 上一帧的 viewProj。Unity 的 GL 风格 clip space：y 向上、w > 0 = 在相机前方。
    // 刻意**不**过 GL.GetGPUProjectionMatrix —— 这里算出来的 uv 是喂给
    // VistaApFroxelRayDirection 的（它以 lerp(bottom, top, uv.y) 结尾，uv.y 自下而上），
    // 不是用来采屏幕纹理，所以不需要那个按平台的 Y 翻转，也就不需要在这里补一个
    // 「D3D 上翻一下」的分支。那种分支写错的症状是重投影上下颠倒，
    // 而在低频的雾上它看起来只是「历史权重太高」。
    float4x4 _VistaFroxelPrevViewProj;
    // 上一帧的分片范围 (near, far, logRatio, 1/logRatio)，米。见 VistaShaderIDs 的注释：
    // 近远距离不进纹理重分配的脏检查，所以历史表里的片可能是另一套 near/far。
    float4 _VistaFroxelPrevRange;
    // xyz: 上一帧相机世界位置 (m)；w: **历史**的混合权重 ∈ [0,1]。
    float4 _VistaFroxelPrevCameraWS;
    // xyz: R3 塑性常数 Kronecker 序列的本帧相位 ∈ [0,1)³。
    float4 _VistaFroxelJitterPhase;
    // x: 横向抖动幅度（单位 = 一格宽），y: 深度抖动幅度（单位 = 一片厚）。
    float4 _VistaFroxelJitter;
    // x: 亮度死区下端，y: 1/(上端 − 下端)。宽度由 CPU 保证 > 0。
    float4 _VistaFroxelReprojParams;
CBUFFER_END

#define VISTA_FROXEL_HISTORY_WEIGHT  _VistaFroxelPrevCameraWS.w
#define VISTA_FROXEL_PREV_NEAR_M     _VistaFroxelPrevRange.x
#define VISTA_FROXEL_PREV_RCP_LOG    _VistaFroxelPrevRange.w

// 失效谓词的位掩码。探针把每一位的命中次数累加出来，判据据此证明
// 「这几条守卫真的被驱进过拒绝分支」——
// 「本轮无法失败的守卫要在报告里点名」的反面：让它能失败。
#define VISTA_REPROJ_OK             0u
#define VISTA_REPROJ_NO_HISTORY     1u   // CPU 侧就判了没历史（首帧 / 换相机 / 档位 Off）
#define VISTA_REPROJ_BEHIND         2u   // 在上一帧相机的后方
#define VISTA_REPROJ_OFF_SCREEN     4u   // 上一帧的画面外
#define VISTA_REPROJ_OUT_OF_RANGE   8u   // 上一帧的体积外（更近或更远）
#define VISTA_REPROJ_LUMINANCE     16u   // 亮度变化超出死区上端，权重压到 0
#define VISTA_REPROJ_NAN           32u   // 历史读数里有 NaN/Inf。见 VistaFroxelBlendHistory

#define VISTA_FROXEL_NEAR_M      _VistaFroxelRange.x
#define VISTA_FROXEL_FAR_M       _VistaFroxelRange.y
#define VISTA_FROXEL_LOG_RATIO   _VistaFroxelRange.z
#define VISTA_FROXEL_RCP_LOG     _VistaFroxelRange.w
#define VISTA_FROXEL_SLICES      _VistaFroxelSize.z
#define VISTA_FROXEL_RCP_SLICES  _VistaFroxelSize.w

// ----------------------------------------------------------------------------
//  距离 <-> 编码坐标
//
//  编码坐标 e 与纹理 w 坐标是**同一个变量**，所以这里没有第三个函数。
//  返回值不做 clamp：调用方要能区分「比近端更近」（e < 0，无雾）
//  与「比远端更远」（e > 1，交给 AP LUT），钳死了这两种情况就都变成
//  「贴在最后一片上」，症状是近处物体突然获得远处的雾量。
// ----------------------------------------------------------------------------
//  参数化出一份 `...In` 是为了让**历史帧**（另一套 near/logRatio）能复用同一个表达式。
//  各写一份的话，两处的 max(·, 1e-6) 下限、除法方向、有没有 clamp 都会各自漂移，
//  而「同一个量的第二份实现连 8 行的辅助函数也算」。
float VistaFroxelEncodeDistanceIn(float distanceMeters, float nearMeters, float rcpLogRatio)
{
    return log(max(distanceMeters, 1e-6) / max(nearMeters, 1e-6)) * rcpLogRatio;
}

float VistaFroxelEncodeDistance(float distanceMeters)
{
    return VistaFroxelEncodeDistanceIn(distanceMeters, VISTA_FROXEL_NEAR_M, VISTA_FROXEL_RCP_LOG);
}

// 历史表的编码坐标：用**上一帧那套** near/logRatio。
// 零态下 prevRcpLog = 0 ⇒ 恒返回 0，不是 NaN（log 的参数被 max 兜住了）。
float VistaFroxelEncodeDistancePrev(float distanceMeters)
{
    return VistaFroxelEncodeDistanceIn(distanceMeters,
                                       VISTA_FROXEL_PREV_NEAR_M, VISTA_FROXEL_PREV_RCP_LOG);
}

float VistaFroxelDecodeDistance(float e)
{
    return VISTA_FROXEL_NEAR_M * exp(e * VISTA_FROXEL_LOG_RATIO);
}

// ----------------------------------------------------------------------------
//  切片几何
//
//  四个量都有闭式解，且都只是同一条指数在不同指数点上的取值 ——
//  这是选纯指数分布（而不是 AP 那种可切 Power/Log）的直接收益：
//  「分段近端 / 远端 / 存储点 / 求值点」四者之间的关系是常量比，
//  #21 的积分递推里不需要任何逐片的除法。
// ----------------------------------------------------------------------------

// 切片 i 存的累积距离（米）。纹素中心 w = (i+0.5)/N 处的距离。
float VistaFroxelStoredDistance(float slice)
{
    return VistaFroxelDecodeDistance((slice + 0.5) * VISTA_FROXEL_RCP_SLICES);
}

// 分段 i 的近端（米）。分段 0 从相机（0）开始 —— 不是从 near：
// 相机与近裁剪面之间那一小段也有雾，跳掉它会让紧贴镜头的雾少一截。
float VistaFroxelSegmentNear(float slice)
{
    return slice < 0.5 ? 0.0 : VistaFroxelStoredDistance(slice - 1.0);
}

// 分段 i 的远端（米）。
float VistaFroxelSegmentFar(float slice)
{
    return VistaFroxelStoredDistance(slice);
}

// 分段 i 的介质/光照求值点（米）= 两个存储距离的几何均值，闭式解 e = i/N。
// 分段 0 例外：它的近端是 0，几何均值退化成 0，改用度量中点。
//
// 几何均值落在度量中点附近，偏差 = (√ρ − 1)/(ρ − 1) − 0.5（ρ 是相邻切片比）。
// 默认档 ρ = 1.0874 ⇒ 0.4895，比中点早 0.0105 个分段。
// 这与项目既有的「采样点重心律」是同一件事，由判据④按恒等式校验；
// 该偏差随切片数下降而增大（N = 32 时 0.479），移动端档位要重新读这个数。
float VistaFroxelSampleDistance(float slice)
{
    if (slice < 0.5)
        return 0.5 * VistaFroxelStoredDistance(0.0);

    return VistaFroxelDecodeDistance(slice * VISTA_FROXEL_RCP_SLICES);
}

// ----------------------------------------------------------------------------
//  重投影用的**标称距离**（米）
//
//  = 指数坐标上 e = i/N 处的距离，也就是 VistaFroxelSampleDistance 去掉
//  「第 0 片退化成度量中点」那个例外之后的形式。
//
//  为什么要一个第二个距离函数（这看起来像「同一个量的第二份实现」）：
//  它不是同一个量。求值点回答的是「介质在哪儿被采样」，标称距离回答的是
//  「这一格的值属于空间中哪个位置」，而后者必须**在 e 上落在片心** ——
//  重投影是一次反解：拿标称距离 d 去查历史，要求 encode(d) 精确等于 i/N，
//  否则历史坐标会带一个恒定偏移。
//  第 0 片的那个例外正好破坏这条：encode(0.5·d_0) = 0.5/N − ln2/logRatio，
//  默认档下是 −0.114，也就是它的历史查询**恒为负、永远被拒**。
//  为一片位于 0.3 m 处的雾破坏整套映射的一致性不值得；这里把它的标称位置
//  从 0.156 m 挪到 0.300 m，在 0.3 m 处不可见，换来「所有片的静止恒等性都精确成立」。
//
//  代价点名：本函数假定「格子的值属于 e = i/N 处」，而值实际是在
//  VistaFroxelSampleDistance 求的 —— 两者对 i ≥ 1 逐位相同，只有 i = 0 差 0.144 m。
// ----------------------------------------------------------------------------
float VistaFroxelReprojectDistance(float slice)
{
    return VistaFroxelDecodeDistance(slice * VISTA_FROXEL_RCP_SLICES);
}

// ----------------------------------------------------------------------------
//  分段长度（km）
//
//  注入表里的 σ_t 单位是 **1/km**（与大气那批表同一条约定），而上面四个函数
//  返回的全是**米**。两者相乘之前必须换算，而这个换算的来源只能是 CPU 下发的
//  那一个缩放（`_VistaGround.w`，见 VistaAtmosphereParameters 里的 worldToAtmosphere）——
//  在这里写字面量 0.001 就是同一个常量的第二份实现，而它写错的症状是
//  「雾整体浓 1000 倍或淡 1000 倍」，不报错。
//
//  为什么用形参而不是直接读 `_VistaGround.w`：那会让本文件依赖 AtmosphereDef.hlsl。
//  #21 的 debug view blit 只需要切片几何，不需要整套大气 cbuffer；
//  多一条 include 依赖等于让「只想看一张表」的着色器也被迫带上大气常量。
//  常量仍然只有一份（在 CPU 上），这里传的是对它的引用。
// ----------------------------------------------------------------------------
float VistaFroxelSegmentLengthKm(float slice, float kmPerMeter)
{
    return (VistaFroxelSegmentFar(slice) - VistaFroxelSegmentNear(slice)) * kmPerMeter;
}

// ----------------------------------------------------------------------------
//  纹理坐标
//
//  XY 用像素中心：froxel (x, y) 覆盖屏幕上一整块 divisor² 的方块，
//  它的代表方向取方块中心。视线方向直接复用 AP 的视锥四角插值
//  （VistaApFroxelRayDirection）—— 那是同一个相机的同一个视锥，
//  再写一份等于给「两处视锥推导漂移」留门，而那种漂移的症状是
//  「近雾与远雾在画面边缘对不上」，会被误判成分层接缝。
// ----------------------------------------------------------------------------
float3 VistaFroxelUvw(uint3 id)
{
    return float3((id.xy + 0.5) / _VistaFroxelSize.xy,
                  (id.z + 0.5) * VISTA_FROXEL_RCP_SLICES);
}

// ----------------------------------------------------------------------------
//  抖动（#22）
//
//  空间上一个整数 hash（PCG3D），时间上把 CPU 算好的 R3 相位加上去再取小数。
//  「加同一个相位」是平移变换，所以空间上的噪声性质逐帧保持 —— 这也是
//  #22b 换成蓝噪声纹理时**只需要换掉 hash 那一行**的原因。
//
//  hash 把 z（切片）算进去，于是同一列上每片的偏移互不相关。
//  代价：横向抖动逐片不同 ⇒ 积分那一趟走的是一条微微扭动的射线，
//  而不是严格的直线。60 m 处一格宽 0.94 m，扭动是亚格量级；
//  HDRP 与 UE5 的抖动同样是逐 voxel 三维的。换来的是同一列上 N 个样本
//  在横向也互相独立，方差按 N 降而不是按 1 降。
//
//  幅度为 0 时返回恒 0（不是「幅度很小」）—— 失能态就是零态，
//  于是 JitterMode.Off 不需要在 shader 里有任何分支。
// ----------------------------------------------------------------------------

// PCG3D（Jarzynski & Olano 2020）。选它而不是常见的 sin(dot(p, k)) * 43758.5453：
// 后者在 fp32 上依赖 sin 的低位，不同驱动/不同平台给出不同结果，
// 症状是「换台机器残影的形状变了」—— 一个查不出成因的差异。
uint3 VistaFroxelHash3(uint3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z;  v.y += v.z * v.x;  v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z;  v.y += v.z * v.x;  v.z += v.x * v.y;
    return v;
}

// 本 froxel 本帧的抖动偏移。xy 单位 = 一格宽，z 单位 = 一片厚，范围 ±幅度/2。
float3 VistaFroxelJitterOffset(uint3 id)
{
    // 右移 8 位再转 float：uint 全 32 位转 fp32 会丢低 8 位（尾数只有 24 位），
    // 于是「hash 的低位」这件事在不同编译器下取整方向可能不同。
    // 先丢掉再转，结果在 [0,1) 上精确可表示。
    float3 n = (float3)(VistaFroxelHash3(id) >> 8u) * (1.0 / 16777216.0);
    float3 u = frac(n + _VistaFroxelJitterPhase.xyz) - 0.5;
    return u * float3(_VistaFroxelJitter.x, _VistaFroxelJitter.x, _VistaFroxelJitter.y);
}

// ----------------------------------------------------------------------------
//  时间重投影（#22）
//
//  两个函数，两个调用点（线上的注入核 + 判据的探针核）共用**同一份** ——
//  探针核靠给 positionWS 加一个合成位移把三条拒绝分支逐条驱进去，
//  于是「这些守卫真的能失败」是量出来的，不是读代码读出来的。
// ----------------------------------------------------------------------------

// 把一个世界位置投到上一帧的 froxel 表坐标上。返回失效掩码（0 = 可用）。
//
// 传进来的 positionWS 必须是 froxel 的**格心**（标称位置），不是本帧抖动后的采样点：
// 历史表里存的是这个 froxel 的累积均值，其标称位置就是格心。拿抖动后的位置去反查
// 等于给历史加一个每帧变化的偏移，症状是静止画面也在抖。HDRP 同样用格心。
uint VistaFroxelReprojectUvw(float3 positionWS, out float3 uvwPrev)
{
    uvwPrev = float3(0.0, 0.0, 0.0);

    // CPU 侧已经判了没历史（首帧 / 换相机 / 档位 Off）。这一条先判，
    // 因为零态下 _VistaFroxelPrevViewProj 是单位矩阵、prevRange 全零，
    // 后面几条谓词在那种输入上的读数没有意义。
    if (VISTA_FROXEL_HISTORY_WEIGHT <= 0.0)
        return VISTA_REPROJ_NO_HISTORY;

    float4 clipPrev = mul(_VistaFroxelPrevViewProj, float4(positionWS, 1.0));

    // w ≤ 0 ⇒ 在上一帧相机的平面上或后方。**必须在除法之前判**：
    // w 过零时 xy/w 会飞到 ±inf，之后的 uv 范围判断在 inf 上是「出画」——
    // 看起来也能挡住，但 NaN（0/0）会让 any(uv < 0) 与 any(uv > 1) 同时为假，
    // 于是一个相机后方的点会被判成可用。
    if (clipPrev.w <= 1e-6)
        return VISTA_REPROJ_BEHIND;

    // Unity GL 风格 clip space：ndc.y 向上。转成 uv 之后 uv.y 自下而上，
    // 与 VistaApFroxelRayDirection（以 lerp(bottom, top, uv.y) 结尾）同一套约定，
    // 所以这里没有任何按平台的 Y 翻转 —— 见 VistaFroxelReprojCB 的头注。
    float2 uv = clipPrev.xy / clipPrev.w * 0.5 + 0.5;

    // 距离用「到上一帧相机的欧氏距离」，而不是投影出来的视空间 z ——
    // 注入表的分片本来就是按欧氏距离建的（VistaFroxelInject 里
    // posWS = camera + dMeters * rayDir），两者必须是同一个量。
    // 用 z 的症状是画面边缘（rayDir 与视线夹角大处）历史查偏一片，
    // 表现为四角的雾比中心糊。
    float dPrev = length(positionWS - _VistaFroxelPrevCameraWS.xyz);

    // ---- 那半个纹素 ----
    //  encode 给的是**编码坐标** e，而纹理 w 坐标上第 j 片的中心在 (j + 0.5)/N。
    //  标称距离的定义是 d(j) = decode(j/N)（见 VistaFroxelReprojectDistance），
    //  于是 encode(d(j)) = j/N，要落到第 j 片的中心还差 +0.5/N。
    //
    //  静止时的自洽检查（判据⑬就是它）：dPrev = d(i) ⇒ w = i/N + 0.5/N
    //  = 第 i 片的纹素中心，与 VistaFroxelUvw(id).z 逐位相同。
    //  漏掉这一项的症状是历史整体往近处偏半片 —— 相机静止时看不出来（每帧偏同样
    //  的半片，混出来还是那个值），只有相机沿视线移动时才露出一层拖影，
    //  正是 HDRP 那个已知 half-slice bias 的形状。
    //
    //  用**本帧**的 RCP_SLICES 而不是上一帧的：N 进纹理重分配的脏检查，
    //  而重分配会把 m_LastWrittenIndex 置 −1 ⇒ historyContentValid 为假
    //  ⇒ 历史权重 0 ⇒ 上面第一条就返回了。所以走到这里时两帧的 N 必然相等。
    float wPrev = VistaFroxelEncodeDistancePrev(dPrev) + 0.5 * VISTA_FROXEL_RCP_SLICES;

    uvwPrev = float3(uv, wPrev);

    // 先算完 uvwPrev 再判范围：判据⑮要拿到「被拒绝的那个坐标本身」，
    // 只回一个掩码的话「拒绝对不对」就无从复核。
    if (any(uv < 0.0) || any(uv > 1.0))
        return VISTA_REPROJ_OFF_SCREEN;

    // 不额外收紧到片心区间 [0.5/N, 1−0.5/N]：相机稍微前进时第一片的历史坐标
    // 就会落到 0.5/N 以下，采样器 CLAMP 给的是第 0 片的值，那几乎正是要的东西。
    // 在那里拒绝等于让相机一动就丢掉最近一片的累积。
    if (wPrev < 0.0 || wPrev > 1.0)
        return VISTA_REPROJ_OUT_OF_RANGE;

    return VISTA_REPROJ_OK;
}

// Rec.709 亮度。刻意不用 core 的 Luminance()：那要 include Color.hlsl，
// 而本文件被 #21 的 debug blit 也包着（它只需要切片几何）。
// 系数在这里只出现一次，两个调用点共用这一份。
float VistaFroxelLuminance(float3 rgb)
{
    return dot(rgb, float3(0.2126729, 0.7151522, 0.0721750));
}

// 亮度变化率降权。返回 [0,1] 的历史权重乘子。
//
// 分母取 max(两者) 而不是取历史或平均：取历史的话「从 0 亮到 1」的相对变化是
// 无穷大而「从 1 暗到 0」只有 1，同一个物理事件的两个方向被判成两种严重程度。
//
// **死区**是这条规则能不能用的关键：抖动本身就是一种逐帧亮度变化，
// 没有死区的话这条规则会把自己的抖动噪声当成「场景变了」而降权，
// 亲手毁掉它本该保护的累积。下端由美术填，且必须摆在**实测的**抖动散布之上 ——
// 状态日志里有一格量它。
float VistaFroxelLuminanceWeight(float3 histRgb, float3 nowRgb)
{
    float lumHist = VistaFroxelLuminance(histRgb);
    float lumNow  = VistaFroxelLuminance(nowRgb);

    // 有界到 [0,1]：两者同号非负（辐亮度），所以 |差| ≤ max。
    float rel = saturate(abs(lumHist - lumNow) / max(max(lumHist, lumNow), 1e-6));

    // x = 死区下端，y = 1/(上端 − 下端)。宽度由 CPU 保证 > 0
    // （VistaVolumetricFogSettings.ResolveLuminanceReject），所以这里不再兜底 ——
    // 兜底会让「两个滑条被拖成上端 ≤ 下端」变成一件看不出来的事。
    float t = saturate((rel - _VistaFroxelReprojParams.x) * _VistaFroxelReprojParams.y);
    return 1.0 - t;
}

// ----------------------------------------------------------------------------
//  把历史读数混进本帧
//
//  **不含纹理采样** —— hist 由调用方取进来。这么切的理由不是解耦，是可判性：
//  最后两条守卫（NaN 闸、亮度死区）没有任何位移能驱动它们，只有喂一个合成的
//  hist 才能让它们失败。切在这里，探针喂合成 hist 时走的仍然是**线上这一份**实现；
//  把采样折进来的话，那两条守卫就只能靠读代码相信，也就是
//  「一个默认关闭、又没有判据覆盖的开关」的同一种形状。
//
//  maskIn 是 VistaFroxelReprojectUvw 的返回值。非 OK 时一条乘法都不做、直接透传
//  本帧 —— 「失能态 = 零态」在这里的形式就是「没有历史就是纯本帧」。
//
//  out weight 是**历史**的最终权重（含亮度乘子）。它不是给渲染用的，
//  渲染只要返回值；它是给探针记读数用的，于是「权重到底是多少」不需要在
//  判据里重算一遍（那就是同一个量的第二份实现）。
// ----------------------------------------------------------------------------
float4 VistaFroxelBlendHistory(float4 current, float4 hist, uint maskIn,
                               out uint mask, out float weight)
{
    mask   = maskIn;
    weight = 0.0;

    if (mask != VISTA_REPROJ_OK)
        return current;

    // ---- NaN / Inf 闸 ----
    //  **必须早退**，不能只把 hist 清零再往下走 lerp：NaN·0 = NaN，
    //  lerp 会把被丢弃那一支里的 NaN 原样带回来 ——
    //  「一个 NaN 闸如果只在一支分支里，lerp 会把 NaN 从被丢弃的那一支带回来」。
    //
    //  用 core 的 AnyIsNaN/AnyIsInf（Common.hlsl:614 / :634）而不是手写 x != x：
    //  它们判的是位模式（(asuint(x) & 0x7FFFFFFF) > 0x7F800000），
    //  而 x != x 在开了快速数学的编译下会被直接折成 false —— 一道悄悄消失的闸。
    //
    //  为什么值得留这一闸：混合是**自反馈**的（本帧写进注入表的东西就是下一帧的历史），
    //  一个 NaN 进了表就永久驻留、并顺着三线性插值向邻格蔓延；
    //  而不混历史的话，坏的一帧下一帧就被冲掉。一条指令换掉「整张表永久 NaN」。
    if (AnyIsNaN(hist) || AnyIsInf(hist))
    {
        mask = VISTA_REPROJ_NAN;
        return current;
    }

    float lumWeight = VistaFroxelLuminanceWeight(hist.rgb, current.rgb);

    // 只有**完全**压到 0 才算一次拒绝。0 < lumWeight < 1 是降权，不是拒绝 ——
    // 把降权也记成拒绝的话，死区里那一段渐变会让拒绝计数变成一个无法解读的数。
    if (lumWeight <= 0.0)
    {
        mask = VISTA_REPROJ_LUMINANCE;
        return current;
    }

    weight = VISTA_FROXEL_HISTORY_WEIGHT * lumWeight;

    // 第三个参数是**历史**的权重（见 VistaFroxelReprojCB 里 prevCameraWS.w 的注释）。
    // 下发历史权重而不是新样本权重，就是为了让全零 cbuffer 的零态 = 纯本帧。
    return lerp(current, hist, weight);
}

#endif // VISTA_FROXEL_VOLUME_INCLUDED
