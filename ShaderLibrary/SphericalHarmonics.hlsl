#ifndef VISTA_SPHERICAL_HARMONICS_INCLUDED
#define VISTA_SPHERICAL_HARMONICS_INCLUDED

// ============================================================================
//  L2 实数球谐（SH9）。天空环境光、PRT 传输、体积雾的环境项共用这一份。
//
//  ---- 存的是什么 ----
//  Vista 的 SH 缓冲里存的是**原始辐射亮度矩**
//      L_i = ∫_S² L(ω)·Y_i(ω) dω      （单位 cd/m²·sr，与 SkyView LUT 同一套曝光）
//  而**不是**已经和余弦瓣卷积过的辐照度 SH。
//
//  这个取舍很重要，因为它决定了下游能不能复用：
//    · 漫反射环境光要的是 L 与**余弦瓣**的卷积（Â_l = π, 2π/3, π/4）；
//    · PRT relight 要的是 L 与**每个 surfel 自己的传输函数**的卷积，
//      那个函数不是余弦瓣（它含遮挡与 bent normal）；
//    · 体积雾要的是 L 与**相位函数**的卷积（各向同性时就是 L_00·Y00）。
//  预先折进余弦瓣的话，后两者就必须把它除回去 —— 而 Â_2/Â_0 = 1/4，
//  除回去会把 L2 段的量化误差放大 4 倍。所以存最原始的那一份，
//  每个消费者自己套自己的核。UE 的 SkyLight SH 与 HDRP 的 ambient probe
//  也都是这么分层的。
//
//  ---- 与 Unity SphericalHarmonicsL2 的关系 ----
//  Unity 那个类型的约定是**实测**出来的，不是照文档写的，
//  见 Editor/Atmosphere/VistaAmbientShSelfTest.cs 的 ProbeUnityConvention：
//  它的 Evaluate 用的是未归一化多项式基 {1, y, z, x, xy, yz, 3z²−1, xz, x²−y²}，
//  返回值语义是"albedo=1 的朗伯面出射亮度"。
//  所以 C# 侧写入时要乘 (Â_l/π)·Ŷ_i，见 k_RadianceToUnitySh。
//  本文件的 VistaShIrradiance 必须与那条链路给出同一个数 —— 自检里有交叉验证。
// ============================================================================

#define VISTA_SH_COEFF_COUNT 9

// 基函数归一化常数。与 VistaAmbientShSelfTest.k_ShNorm 逐项一致。
#define VISTA_SH_Y0  0.2820948
#define VISTA_SH_Y1  0.4886025
#define VISTA_SH_Y2A 1.0925484
#define VISTA_SH_Y2B 0.3153916
#define VISTA_SH_Y2C 0.5462742

// 余弦瓣卷积系数 Â_l / π。Â = {π, 2π/3, π/4}。
// 这三个数就是"把辐射亮度 SH 变成 albedo 1 的出射亮度 SH"的全部内容。
#define VISTA_SH_COS_A0 1.0
#define VISTA_SH_COS_A1 0.6666667
#define VISTA_SH_COS_A2 0.25

// dir 须归一化。槽位顺序与 Unity SphericalHarmonicsL2 的索引一致
// （0=DC，1..3 = y/z/x，4..8 = xy/yz/(3z²−1)/xz/(x²−y²)），
// 这样 C# 侧读回后可以逐槽位直接对应，不需要重排 —— 重排是这类代码最常见的错源。
void VistaShBasis(float3 dir, out float y[VISTA_SH_COEFF_COUNT])
{
    y[0] = VISTA_SH_Y0;
    y[1] = VISTA_SH_Y1 * dir.y;
    y[2] = VISTA_SH_Y1 * dir.z;
    y[3] = VISTA_SH_Y1 * dir.x;
    y[4] = VISTA_SH_Y2A * dir.x * dir.y;
    y[5] = VISTA_SH_Y2A * dir.y * dir.z;
    y[6] = VISTA_SH_Y2B * (3.0 * dir.z * dir.z - 1.0);
    y[7] = VISTA_SH_Y2A * dir.x * dir.z;
    y[8] = VISTA_SH_Y2C * (dir.x * dir.x - dir.y * dir.y);
}

// ----------------------------------------------------------------------------
//  球面均匀采样：Fibonacci 螺旋
//
//  为什么用它而不是照着 SkyView LUT 的纹素逐个累加：
//  那张表的 uv 打包**故意**把纹素往地平线堆（见 AtmosphereScattering.hlsl 的
//  参数化注释）。按纹素累加就等于按那个非均匀密度加权，地平线会被超额计入 ——
//  日落时地平线是全场最亮的一圈，超额的后果是环境光整体偏橙、偏亮，
//  而且**误差随 LUT 分辨率变化**，换个分级档环境光就变色。
//  按方向采样则让立体角权重天然正确（每个样本都是 4π/N），
//  同时把采样数与 LUT 分辨率解耦。UE 的 SkyAtmosphere 生成 SkyLight SH 时同样是
//  按方向重采样，不是按 LUT 纹素累加。
//
//  为什么用 Fibonacci 而不是 MS LUT 那套 8×8 网格（Marsaglia）：
//  8×8 只有 64 个方向且在方位上是规则栅格，投到 L2 上会留下与栅格对齐的残差；
//  Fibonacci 螺旋的球面差异度（spherical cap discrepancy）接近最优，
//  同样样本数下 L2 矩的误差小一个量级，而代价只是一次 frac()。
// ----------------------------------------------------------------------------

// 黄金角 π(3−√5)。写成常量而不是运行时算，避免 fp32 在大 i 上把 i·角度 的
// 有效位吃掉（i 到几千时 sin/cos 的参数已经损失精度，所以外面还要 frac 一次）。
#define VISTA_GOLDEN_ANGLE 2.39996323

float3 VistaFibonacciSphereDir(uint index, uint count)
{
    // z 在 (−1, 1) 上等距：均匀球面分布的充要条件是 z 均匀（Archimedes）。
    // +0.5 取格心，避免两极各压一个退化样本。
    float z = 1.0 - 2.0 * ((float)index + 0.5) / (float)count;
    float r = sqrt(saturate(1.0 - z * z));

    // frac 到 [0,1) 再乘 2π：index 到几千时 index·2.39996 的整数部分会吃掉
    // fp32 的有效位，直接送进 sincos 会让高索引的方位角出现可见抖动。
    float phi = 2.0 * PI * frac((float)index * (VISTA_GOLDEN_ANGLE / (2.0 * PI)));

    float s, c;
    sincos(phi, s, c);
    return float3(r * c, r * s, z);
}

// ----------------------------------------------------------------------------
//  求值
// ----------------------------------------------------------------------------

// 重建辐射亮度：L(dir) ≈ Σ L_i·Y_i(dir)。
// 只对低频有效 —— L2 截断后太阳附近会有明显 ringing（甚至负值），
// 所以这个函数**不用于画天空**（天空走 SkyView LUT），只给需要"某方向大致多亮"
// 的消费者用（体积雾的环境项、PRT 的入射估计）。
float3 VistaShRadiance(float3 sh[VISTA_SH_COEFF_COUNT], float3 dir)
{
    float y[VISTA_SH_COEFF_COUNT];
    VistaShBasis(dir, y);

    float3 acc = 0.0;
    [unroll]
    for (uint i = 0u; i < VISTA_SH_COEFF_COUNT; ++i)
        acc += sh[i] * y[i];
    return acc;
}

// 漫反射环境光：(1/π)·∫L(ω)·max(0, n·ω)dω，即 albedo=1 的朗伯面出射亮度。
// 与 Unity SphericalHarmonicsL2.Evaluate 的语义**完全一致**（自检里交叉验证过），
// 所以 shader 里这条路径与走 unity_SHAr 的那条会给出同一个数 ——
// 这很重要：Step 4 的 PRT 会在同一帧里混用两者。
float3 VistaShIrradiance(float3 sh[VISTA_SH_COEFF_COUNT], float3 n)
{
    float y[VISTA_SH_COEFF_COUNT];
    VistaShBasis(n, y);

    float3 acc = sh[0] * (y[0] * VISTA_SH_COS_A0);
    [unroll]
    for (uint i = 1u; i < 4u; ++i)
        acc += sh[i] * (y[i] * VISTA_SH_COS_A1);
    [unroll]
    for (uint j = 4u; j < VISTA_SH_COEFF_COUNT; ++j)
        acc += sh[j] * (y[j] * VISTA_SH_COS_A2);
    return acc;
}

// 各向同性相位下的平均入射亮度 = L_00·Y00。体积雾的环境项用这个：
// 雾的散射是准各向同性的，没必要按方向重建（重建还会引入 ringing）。
float3 VistaShAmbientMean(float3 sh[VISTA_SH_COEFF_COUNT])
{
    return sh[0] * VISTA_SH_Y0;
}

// ----------------------------------------------------------------------------
//  全局绑定（消费者侧）
//
//  用 StructuredBuffer 而不是 9 个 float4 uniform：uniform 要占 9 个全局槽位、
//  且每帧从 CPU 侧 SetGlobalVector 九次，而这份数据是 GPU 产出的 ——
//  走 buffer 可以完全不回 CPU（回 CPU 的那条是给 RenderSettings.ambientProbe 的
//  旁路，见 VistaAtmospherePass，两条路互不阻塞）。
//  w 通道空着，留给后续存"该帧的采样数 / 有效性标记"。
// ----------------------------------------------------------------------------
#ifndef VISTA_SKY_AMBIENT_SH_NO_DECL
StructuredBuffer<float4> _VistaSkyAmbientSh;

void VistaLoadSkyAmbientSh(out float3 sh[VISTA_SH_COEFF_COUNT])
{
    [unroll]
    for (uint i = 0u; i < VISTA_SH_COEFF_COUNT; ++i)
        sh[i] = _VistaSkyAmbientSh[i].rgb;
}

float3 VistaSkyAmbientIrradiance(float3 n)
{
    float3 sh[VISTA_SH_COEFF_COUNT];
    VistaLoadSkyAmbientSh(sh);
    return VistaShIrradiance(sh, n);
}
#endif

#endif // VISTA_SPHERICAL_HARMONICS_INCLUDED
