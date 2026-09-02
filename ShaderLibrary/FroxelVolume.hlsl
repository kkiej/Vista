#ifndef VISTA_FROXEL_VOLUME_INCLUDED
#define VISTA_FROXEL_VOLUME_INCLUDED

// ============================================================================
//  近层体积雾的 froxel 体（Step 3 档 A，PC 主线）
//
//  三张 3D 表，屏幕比例 XY（默认 /8）× 64 深度切片：
//    注入（当前帧）  RGBA16F  (σ_s·L 的散射源项 rgb, σ_t)   —— **局部**量
//    注入（历史）    RGBA16F  同上，供 #22 的时间重投影
//    积分            RGBA16F  (累积内散射 rgb, 累积透射率)  —— **相机原点的累积**量
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
float VistaFroxelEncodeDistance(float distanceMeters)
{
    return log(max(distanceMeters, 1e-6) / max(VISTA_FROXEL_NEAR_M, 1e-6))
         * VISTA_FROXEL_RCP_LOG;
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

#endif // VISTA_FROXEL_VOLUME_INCLUDED
