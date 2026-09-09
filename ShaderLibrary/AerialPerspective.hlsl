#ifndef VISTA_AERIAL_PERSPECTIVE_INCLUDED
#define VISTA_AERIAL_PERSPECTIVE_INCLUDED

// ============================================================================
//  Aerial Perspective froxel LUT
//
//  存的是「从相机到某个距离之间，大气对这条视线做了什么」，两个量：
//    散射   在这段路径上散射进视线的亮度（绝对光度量 cd/m²，未乘曝光）
//    透射率 这段路径对身后物体的衰减
//  采样端把两者按 final = geometry · transmittance + inScatter 合成。
//
//  ------------------------------------------------------------------ 为什么要这张表
//  逐像素做完整 raymarch 才是"正确"做法，但那是几十步 × 全屏。
//  AP 在屏幕空间和深度上都极其平滑（它是视线方向与距离的低频函数），
//  所以放进一张 32×32×32 的 3D 表里、用三线性插值取回来，视觉上无损而开销降两个数量级。
//  这是 Hillaire 2020 的核心手法，UE5 的 SkyAtmosphere 与 HDRP 的
//  PhysicallyBasedSky 都是这个结构。
//
//  ------------------------------------------------------------------ 分层：本表 vs 体积雾
//  近处（Step 3 的体积雾 froxel）与远处（本表）分开算，**分界的理由不是分辨率**，
//  而是"能不能负担逐 froxel 采样阴影贴图"：
//    近层  必须逐 froxel 采样级联阴影 —— 光柱（god rays）完全来自这一步；
//          范围短，可以做时间重投影把噪声摊掉。
//    远层  级联阴影根本铺不到几十 km，采了也是常量；本表直接用
//          Transmittance LUT + VistaEarthShadow 拿到太阳透射率，没有阴影查询。
//  推论：远层因此**不需要**时间抖动/重投影 —— 抖动的作用是掩盖欠采样的阴影噪声，
//  而本表按构造就没有阴影采样，值是平滑解析的。抖动只属于近层。
//  另一个推论：远层可以用任意切片分布，不受"必须和阴影级联对齐"的约束。
//
//  近层与本表是**两张纹理、一个采样函数**。
//
//  ⚠ 这里原本写的是「近层的输出按 transmittance 加权写进本表的近端切片
//  （合成进 AP LUT，UE 路线）」。那句话在**目标**上是对的，在**做法**上是错的，
//  错因是一个可以直接算出来的数：本表是 32×32×32，1080p 下每个 XY 纹素覆盖
//  60 × 33.75 像素，而一根光柱的边缘在屏幕上是 2~5 像素宽 —— 差 15~30 倍，
//  三线性插值只会把它抹成一团亮斑。
//  本表能用 32×32 恰恰因为 AP 在屏幕空间是低频的（见上面「为什么要这张表」），
//  而**体积阴影是那句话的反例**：分层的依据是"能不能采阴影"，
//  而"能采阴影"同时意味着"输出是高频的"，于是两层的 XY 分辨率需求也必然不同。
//  把整张表的 XY 抬到 160×90 也不行：froxel 数 32k → 460k，
//  而远端 32 km 的切片完全不需要这个分辨率，等于让远景为近段的光轴买单。
//
//  所以要保的不是「同一张表」，是「同一个采样函数」—— 后者才是真正的收益来源：
//  任何一处消费者（不透明物、半透明、水面）都调 VistaSampleAerialPerspective，
//  由它内部按距离在近层雾体与本表之间挑，不存在"各自实现一遍混合、
//  然后在交界处露缝"这类 bug。消费者的签名与调用点因此一行不用改。
//
//  ------------------------------------------------------------------ 彩色 vs 灰度透射率
//  论文把**灰度**透射率塞进 alpha，只用一张表、一次采样。
//  但空气的消光是强波长相关的：地表 Rayleigh 消光蓝 33.1e-3 /km、红 5.8e-3 /km，
//  相差 5.7 倍。12 km 处 T_蓝≈0.67 而 T_红≈0.93，灰度均值 0.82 对两端都错约 8%,
//  症状是远山丢掉暖色、整体偏冷 —— 恰好是本项目最核心的画面（远景层叠）。
//  所以 PC 路径存彩色透射率（第二张 3D 表，多一次采样）；
//  移动端路径回到论文的灰度打包（近平面通常 4 km 以内，误差 < 2%）。
//  两条路径的差值由自检数值量化（见 VistaAtmosphereSelfTest 的 AP grey-vs-colored 项），
//  这样"移动端这么省是可以接受的"是量出来的，不是猜的。
// ============================================================================

#include "Packages/com.kkiej.vista/ShaderLibrary/AtmosphereScattering.hlsl"
// 近层积分表（#25）。合成在本文件的 VistaSampleAerialPerspective 里，
// 近层不存在时那张表一次都不会被采（见它自己的开关位）。
#include "Packages/com.kkiej.vista/ShaderLibrary/FroxelComposite.hlsl"

TEXTURE3D(_VistaApScatterLut);
TEXTURE3D(_VistaApTransmittanceLut);

// ----------------------------------------------------------------------------
//  深度切片分布
//
//  约定（写在这里，因为写反的症状是"雾整体近了/远了一格"，极难反查）：
//    切片 i 存的是**从相机到距离 D(w_i) 的累积**，w_i = i / (depth - 1)。
//    于是 i = 0 对应最近端、i = depth-1 恰好对应 farKm，两端都是精确值，
//    不需要论文里那个"slice < 0.5 时手动淡出"的补丁。
//    代价是 Power 模式下切片 0 存的是全零（D(0) = 0），32 片里浪费 1 片换两端精确。
//
//  两种分布都实现了，因为哪种更好取决于场景尺度，应该量而不是猜
//  （自检里对同一条视线做高步数 ground truth 比较，报告两者的重建误差）：
//    Power  D(w) = far · w^k       k=2 时约一半切片落在 far/4 以内
//    Log    D(w) = near · (far/near)^w   切片间距恒为距离的固定百分比，
//                                        即**相对**分辨率处处相同
//  ER 这种"脚下几百米 + 远景几十 km 同屏"的场景里 Log 更均衡：
//  32 片、near=20 m、far=32 km 时，640 m 以内有 15 片，之外也有 16 片。
//  Power k=2 在同样配置下 512 m 以内只有 4 片。
// ----------------------------------------------------------------------------

#define VISTA_AP_DIST_POWER 0.0
#define VISTA_AP_DIST_LOG   1.0

// 归一化深度参数 w ∈ [0,1] -> 沿视线的距离 (km)
float VistaApSliceCoordToDistance(float w)
{
    if (_VistaApParams.w > 0.5)
        return _VistaApParams.x * pow(_VistaApParams.y / _VistaApParams.x, w);

    return _VistaApParams.y * pow(max(w, 0.0), _VistaApParams.z);
}

// 逆映射。返回值**不做 clamp**：调用方需要区分"比最近端还近"（负值，要淡出）
// 与"比最远端还远"（> 1，钉在最后一片）。
float VistaApDistanceToSliceCoord(float distanceKm)
{
    if (_VistaApParams.w > 0.5)
        return log(max(distanceKm, 1e-7) / _VistaApParams.x)
             / log(_VistaApParams.y / _VistaApParams.x);

    return pow(max(distanceKm, 0.0) / _VistaApParams.y, rcp(_VistaApParams.z));
}

// 归一化深度参数 -> 3D 纹理的 w 坐标（纹素中心对齐）。
// 切片 i 的中心在 (i + 0.5) / depth，而它代表 w = i / (depth - 1)，
// 于是 texW = (w · (depth - 1) + 0.5) / depth。
float VistaApSliceCoordToTexW(float w)
{
    float depth = _VistaApSize.z;
    return (saturate(w) * (depth - 1.0) + 0.5) / depth;
}

// ----------------------------------------------------------------------------
//  froxel 视线方向
//  未归一化的四角向量做双线性插值再归一化 —— 理由见 AtmosphereDef.hlsl 的注释。
// ----------------------------------------------------------------------------
float3 VistaApFroxelRayDirection(float2 uv)
{
    float3 bottom = lerp(_VistaApRayBL.xyz, _VistaApRayBR.xyz, uv.x);
    float3 top    = lerp(_VistaApRayTL.xyz, _VistaApRayTR.xyz, uv.x);
    return normalize(lerp(bottom, top, uv.y));
}

// ----------------------------------------------------------------------------
//  采样端
//
//  distanceKm: 着色点到相机的**径向**距离（不是 view-space Z）。
//  径向而非平面深度：积分是沿视线做的，用平面深度会让视野边缘的雾比中心薄，
//  广角下画面四角明显发清。
//
//  inScatter 是**预曝光**辐亮度（表里就存的预曝光值，见 AerialPerspectiveLut 里
//  为什么必须这样存）。也就是说它已经是渲染目标单位，合成端不再乘 VISTA_EXPOSURE。
//  需要绝对光度量的消费者（Step 5 的 SH 投影）乘 rcp(VISTA_EXPOSURE) 还原 ——
//  fp16 的相对精度与量级无关，还原不丢有效位。
// ----------------------------------------------------------------------------
void VistaSampleApTable(float2 screenUv, float distanceKm,
                        out float3 inScatter, out float3 transmittance)
{
    float w = VistaApDistanceToSliceCoord(distanceKm);

    // 比最近端还近：整段路径短于第一片，线性淡出到"什么都没发生"。
    // **按距离淡出而不是按 w 淡出**：Log 模式下 w 在近端变化极慢
    // （d = near/2 只让 w 退到 -0.09），照 w 淡出会把近端第一个半数量级的雾整段抹掉。
    // 按距离线性正好也是物理上对的：光学薄的近段里散射量近似正比于路径长度。
    // _VistaApFlags.y = 1/nearKm；Power 模式 near = 0，那里填一个大数，
    // 于是 d > 0 立刻取 1、d = 0 取 0（Power 模式 D(0) = 0，本来就无雾，一致）。
    float nearFade = saturate(distanceKm * _VistaApFlags.y);

    float3 uvw = float3(screenUv, VistaApSliceCoordToTexW(w));

    float4 scatter = SAMPLE_TEXTURE3D_LOD(_VistaApScatterLut, sampler_LinearClamp, uvw, 0);

    if (_VistaApFlags.x > 0.5)
        transmittance = SAMPLE_TEXTURE3D_LOD(_VistaApTransmittanceLut, sampler_LinearClamp, uvw, 0).rgb;
    else
        transmittance = scatter.aaa;

    inScatter     = scatter.rgb * nearFade;
    transmittance = lerp(1.0, transmittance, nearFade);
}

// ----------------------------------------------------------------------------
//  统一采样函数（#25）
//
//  这才是**消费者唯一该调的入口**。上面那个只读远层的表；两层怎么合成
//  是本函数的事，调用点不需要知道近层存不存在 —— 这正是文件头那句
//  「要保的不是同一张表，是同一个采样函数」的兑现处。
//
//  ------------------------------------------------------------------ 合成
//      T = T_near · T_ap
//      S = S_near + T_near · S_ap
//  近层在前：背景的光先被远层的介质衰减、再被近层衰减（乘法可交换，
//  所以顺序只影响 S 那一项）。S_ap 主要产生在 D 之外，要额外穿过近层 ⇒ ×T_near。
//
//  ------------------------------------------------------------------ 透射率是**精确**的，不是近似
//  这不是"凑得比较准"，是 VistaFogApWeight 那条互补分法的直接推论：
//      T_near·T_ap = exp(−∫₀^D σ_fog·(1−w)) · exp(−∫₀^d (σ_atm + σ_fog·w))
//                  = exp(−∫₀^d (σ_atm + σ_fog))
//  两层各认领一部分 σ_fog，指数一相乘就拼回完整的光学厚度，**一点都不重、一点都不漏**。
//  能摆这个等号的前提是「权重互补」而不是「近端硬切」—— 硬切在交接带里
//  两边都会算一遍（w 不是 0/1 的地方）。判据 (c) 就押在这条恒等式上。
//
//  这条恒等式在 #25 写下的那一刻其实**还不成立**：当时近层存的 σ_t 是
//  smp.extinction = σ_atm + σ_fog·(1−w)，上式左边多一个 exp(−∫₀^D σ_atm)，
//  也就是大气在 [0, D] 上被两层各积了一遍。它没被当场发现，正是因为这段推导
//  写的是**应该成立的式子**，而没人拿它去核注入端到底存了什么 ——
//  一条只写在注释里的恒等式不会自己失败。#25b 的修法 A 让注入端与它对齐，
//  并把它变成一道每帧自己算的判据（⑫c）。
//
//  ------------------------------------------------------------------ 内散射有两处已知近似
//  (1) S_ap 里含有**产生在 [0, D] 之内的大气内散射**（AP 恒从相机积分），
//  这部分本该只穿过近层的一部分，却被整个 T_near 衰减了一次。
//  偏差上界 = (1 − T_near(D)) × S_atm([0, D])：默认 D = 48 m，
//  那 48 m 里的大气内散射本身就是整条视线的 48/32000 = 0.15%，
//  再乘一个 (1 − T_near) < 1 ⇒ **远小于 fp16 的量化**。
//  它随 D 增大而增大，所以 D 被 shadow distance 夹住这件事同时也是这条近似的护栏。
//  修掉它需要 AP 表额外存一份「[0, D] 之内的内散射」—— 多一张表换 0.15% 以下的项，
//  没有付这个价的理由。
//
//  (2) S_near 里局部灯（#23）打在**空气**上的入散射，只被雾的透射率衰减，
//  少了 [0, D] 上 σ_atm 的那一点（D = 50 m ⇒ 9e-4，即 0.09%）。
//  这一项不能像大气的太阳项那样让给 AP —— AP 不知道局部灯的存在，让出去就是
//  丢掉而不是去重。所以它留在近层，代价是衰减少算了千分之一。
//  另一处相关的取舍：[0, D] 上大气的**太阳**入散射从此没有级联阴影
//  （AP 那份是无阴影的），也就是晴空里没有空气光柱 —— 量级同样在 0.1%，
//  理由与数值写在 VistaScatterSample.scatteredFog 上。
// ----------------------------------------------------------------------------
void VistaSampleAerialPerspective(float2 screenUv, float distanceKm,
                                  out float3 inScatter, out float3 transmittance)
{
    float3 apScatter, apTransmittance;
    VistaSampleApTable(screenUv, distanceKm, apScatter, apTransmittance);

    // 没有近层（档 D / 移动端 / froxel 关）时**整段短路**，逐位等于本改动之前。
    // 短路而不是"让近层返回 (0, 1) 再照常合成"：后者要多采一次 3D 表，
    // 而这一帧那张表可能压根没绑（见 FroxelComposite.hlsl 里那条注释）。
    if (!VistaNearVolumeEnabled())
    {
        inScatter     = apScatter;
        transmittance = apTransmittance;
        return;
    }

    float3 nearScatter, nearTransmittance;
    // km → m 用 CPU 下发的那一个缩放的倒数，不写 1000.0 字面量
    // （与 VistaFroxelSegmentLengthKm 同一条约定：这个换算只有一份来源）。
    VistaSampleNearVolume(screenUv, distanceKm / _VistaGround.w,
                          nearScatter, nearTransmittance);

    transmittance = nearTransmittance * apTransmittance;
    inScatter     = nearScatter + nearTransmittance * apScatter;
}

#endif // VISTA_AERIAL_PERSPECTIVE_INCLUDED
