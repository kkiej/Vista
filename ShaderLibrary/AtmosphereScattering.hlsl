#ifndef VISTA_ATMOSPHERE_SCATTERING_INCLUDED
#define VISTA_ATMOSPHERE_SCATTERING_INCLUDED

// ============================================================================
//  共享 raymarch 积分器。
//
//  MS LUT / SkyView LUT / AerialPerspective LUT / 体积雾 全部复用这一个函数，
//  差异只体现在 VistaRaymarchSettings 上。之所以不为每张表各写一遍：
//  单次散射的能量计算一旦在某一处写歧了，天空和雾就会对不上（典型症状是
//  远山的雾色和天空色在地平线交界处有一条缝），而这种 bug 极难定位。
//
//  参考：Hillaire, "A Scalable and Production Ready Sky and Atmosphere
//  Rendering Technique", EGSR 2020。
// ============================================================================

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
#include "Packages/com.kkiej.vista/ShaderLibrary/AtmosphereDef.hlsl"
#include "Packages/com.kkiej.vista/ShaderLibrary/FogMedium.hlsl"

TEXTURE2D(_VistaTransmittanceLut);
TEXTURE2D(_VistaMultiScatteringLut);

// MS LUT 分辨率。这是**算法常量**而不是可调项：多次散射对 (muSun, 高度) 是极平滑的
// 二维函数，Hillaire 实测 32×32 已经看不出与 128×128 的差别。C# 侧
// VistaAtmosphereLuts.k_MultiScatteringSize 必须与此一致。
#define VISTA_MULTISCATTERING_LUT_RES 32.0

// 10 m。用来把采样点从"正好贴在地面上"挪开——r 恰好等于 bottomRadius 时
// 切线方向的 ray-sphere 判别式在 0 附近抖动，会在地平线出现一圈噪点。
#define VISTA_PLANET_RADIUS_OFFSET 0.01

// ----------------------------------------------------------------------------
//  几何：通用 ray-sphere（地面反弹点、太阳方向的地球遮挡都需要非 r/mu 形式）
// ----------------------------------------------------------------------------
// rayDir 须归一化。返回最近的非负交点距离，无交点返回 -1。
float VistaRaySphereIntersectNearest(float3 rayOrigin, float3 rayDir, float3 center, float radius)
{
    float3 oc = rayOrigin - center;
    float b = dot(oc, rayDir);
    float c = dot(oc, oc) - radius * radius;
    float discriminant = b * b - c;
    if (discriminant < 0.0)
        return -1.0;

    float sqrtDisc = sqrt(discriminant);
    float t0 = -b - sqrtDisc;
    float t1 = -b + sqrtDisc;
    if (t1 < 0.0)
        return -1.0;
    return (t0 < 0.0) ? t1 : t0;
}

// ----------------------------------------------------------------------------
//  相位函数
//
//  cosTheta 的约定：cosTheta = dot(rayDir, sunDir)，即**看向太阳时为 +1**。
//
//  推导（这个符号最容易搞反，搞反的症状是光晕出现在太阳的反侧）：
//  相位函数的 θ 是入射传播方向与散射传播方向的夹角。
//  光从太阳来 -> 入射传播方向 = -sunDir；光射向眼睛 -> 散射传播方向 = -rayDir。
//  cosθ = dot(-sunDir, -rayDir) = dot(sunDir, rayDir)。
//  前向散射（θ=0）即视线与阳光同向，也就是逆光看太阳——此时 Mie 光晕最强。✓
// ----------------------------------------------------------------------------
float VistaRayleighPhase(float cosTheta)
{
    return (3.0 / (16.0 * PI)) * (1.0 + cosTheta * cosTheta);
}

float VistaHenyeyGreensteinPhase(float g, float cosTheta)
{
    float g2 = g * g;
    // cosTheta = 1（看向太阳）时 denom 最小 -> 相位最大，即前向散射峰
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    return (1.0 - g2) / (4.0 * PI * denom * sqrt(max(1e-4, denom)));
}

#define VISTA_ISOTROPIC_PHASE (1.0 / (4.0 * PI))

// ----------------------------------------------------------------------------
//  LUT 采样
// ----------------------------------------------------------------------------

// 到太阳的透射率。muSun = dot(up, sunDir)。
// 注意：视线朝太阳但太阳在地平线下时，Transmittance LUT 的参数化会给出穿过地心的
// 无意义结果，必须靠 VistaEarthShadow 归零，不能只靠这张表。
float3 VistaSampleTransmittanceToSun(float r, float muSun)
{
    float2 uv = VistaRMuToTransmittanceLutUv(r, muSun);
    return SAMPLE_TEXTURE2D_LOD(_VistaTransmittanceLut, sampler_LinearClamp, uv, 0).rgb;
}

// 采样点 P 是否被星球本体挡住太阳。1 = 见光，0 = 在星球阴影里。
//
// 需要偏置的原因：贴地采样点（视线打到地面时的末端采样点就是）到太阳的射线是
// **切着球面离开**的，无偏置时 c = |P|² - Rb² = 0，判别式给出 t = 0 的退化根，
// 于是"正午的地面"会被判成全阴影。
//
// 偏置方向必须让采样点落在遮挡球**外面**，也就是把球心沿 up 推**远**：
//   c = (r + offset)² - Rb² > 0，测试退化成物理上正确的"太阳是否在当地地平线以下"。
// 反过来把球心朝采样点推近（up * offset，UE 的 SkyAtmosphere 是这么写的）会得到
//   c = (r - offset)² - Rb² < 0 —— 海拔不足 10 m 的采样点一律被判成"在星球内部"，
// 直射项被整体归零。它在 UE 里不显眼（近处切片本就贡献极小），但在本项目里是实测到的
// 硬伤：相机贴地时那个 10 m 的台阶正好落在 AP 第一片内部，
// 被积函数出现阶跃，2 步与 256 步的求积因此相差 24%（见 CHANGELOG 的坑）。
//
// 10 m 的容差同时还兜住 fp32：r 在 6360 km 上的 ulp 是 0.49 m，
// 海拔算出负值也不会误判。
float VistaEarthShadow(float3 posKm, float3 sunDir)
{
    float3 up = normalize(posKm);
    float t = VistaRaySphereIntersectNearest(
        posKm, sunDir, -up * VISTA_PLANET_RADIUS_OFFSET, VISTA_BOTTOM_RADIUS);
    return t >= 0.0 ? 0.0 : 1.0;
}

float2 VistaMultiScatteringLutUv(float r, float muSun)
{
    float thickness = VISTA_TOP_RADIUS - VISTA_BOTTOM_RADIUS;
    float2 unitRange = saturate(float2(muSun * 0.5 + 0.5, (r - VISTA_BOTTOM_RADIUS) / thickness));
    return float2(
        VistaTexCoordFromUnitRange(unitRange.x, VISTA_MULTISCATTERING_LUT_RES),
        VistaTexCoordFromUnitRange(unitRange.y, VISTA_MULTISCATTERING_LUT_RES));
}

// 多次散射的等效入射亮度。假定各向同性——这是这套方案最大的一处物理简化：
// 二次以上散射已经把方向性抹平，各向同性的误差在 1% 量级，
// 换来的是把"无限次散射"压成一张 32×32 的表 + 一次采样。
float3 VistaSampleMultiScattering(float r, float muSun)
{
    float2 uv = VistaMultiScatteringLutUv(r, muSun);
    return SAMPLE_TEXTURE2D_LOD(_VistaMultiScatteringLut, sampler_LinearClamp, uv, 0).rgb;
}

// ----------------------------------------------------------------------------
//  积分器
// ----------------------------------------------------------------------------
struct VistaRaymarchSettings
{
    float3 sunIlluminance;      // 建 MS LUT 时传 1
    float  sampleCount;         // variableSampleCount 时作为下界
    float  sampleCountMax;      // 仅 variableSampleCount 有效
    float  tMax;                // < 0 = 自动（到大气顶或地面）；用于深度缓冲截断
    bool   variableSampleCount; // 按视线长度自适应步数
    bool   applyPhase;          // false = 各向同性相位（建 MS LUT 用）
    bool   includeGroundBounce; // 计入地面反弹的一次漫反射
    bool   useMultiScattering;  // 采样 MS LUT（建 MS LUT 时必须 false，否则自引用）

    // 本积分器内部逐样本取雾（VistaSampleFogAlongRay）。**默认 false**。
    //
    // 为什么需要这个开关，而不是"反正 σ_t = 0 时雾是零态、一律取就好了"：
    // 本积分器同时服务 Transmittance / MS / SkyView 三张 LUT 与地面反弹，
    // 那些**永远**不能含雾（见 FogMedium.hlsl 的"与大气介质的关系"）；
    // 而 #7 的切片误差判据要的 256 步 ground truth（VistaApReference）走的也是本积分器，
    // 它**必须**含雾，否则 AP kernel 一接雾，判据立刻报一个纯属虚构的巨大误差 ——
    // 这类"尺子和被测对象用了不同的物理"的假失败，比真失败更贵。
    // 所以开关的语义不是性能，是"这条视线属于哪一类量"。
    //
    // 默认 false 还有一条回归性质：三张静态 LUT 与 SkyView 的调用方都不碰这个字段，
    // 于是它们走的仍是 VistaFogSampleNone() 那条常量折叠路径，逐位不变。
    bool   includeFog;

    // 雾的环境项入射亮度（绝对光度量 cd/m²）。必须由调用方用 VistaShAmbientMean 算，
    // 那是 SphericalHarmonics.hlsl 里唯一一份"各向同性相位下的平均入射亮度"。
    //
    // 为什么放在**逐视线**的 settings 里而不是逐样本算：
    // 天光环境探针是整个相机位置的一个 SH，沿视线不变，逐样本重算是纯浪费；
    // 更重要的是，把它抬到 settings 里让 AtmosphereScattering.hlsl **不依赖**
    // _VistaSkyAmbientSh 这个 StructuredBuffer —— 否则每个 include 本文件的
    // 片元着色器（Vista/Lit、天空、水面）都会白占一个 t 槽位，
    // 而移动端 GLES3 上片元着色器里的 SSBO 是个已知的雷区。
    // 需要它的只有两个 compute kernel（#18 的 AP、#20 的近层雾体），
    // 由它们各自在 setup 处调一次那个共享函数。
    //
    // 已知的近似：这一项**不含遮挡** —— 洞穴里的雾拿到的天光和空地一样多。
    // 正确的做法要么让雾体去查 Step 4 的 PRTGI，要么给雾体自己做一层
    // 天空可见度，两者都不在 Step 3 的范围里。
    float3 fogAmbientRadiance;
};

VistaRaymarchSettings VistaDefaultRaymarchSettings()
{
    VistaRaymarchSettings s;
    s.sunIlluminance      = _VistaSun.xyz;
    s.sampleCount         = 32.0;
    s.sampleCountMax      = 32.0;
    s.tMax                = -1.0;
    s.variableSampleCount = false;
    s.applyPhase          = true;
    s.includeGroundBounce = true;
    s.useMultiScattering  = true;
    s.includeFog          = false;
    s.fogAmbientRadiance  = 0.0;
    return s;
}

struct VistaScatteringResult
{
    float3 luminance;       // 沿视线累积的散射亮度
    float3 transmittance;   // 整段透射率
    float3 opticalDepth;
    float3 multiScatAs1;    // 建 MS LUT 用：入射亮度恒为 1 时的散射传递量
};

// ----------------------------------------------------------------------------
//  步段内的取样位置
//
//  提成宏而不是留在积分器里的局部 const：AP LUT 的行进循环在别的文件里，
//  两处若取不同的段内位置，天空与远山雾色在地平线交界处会差一个可见的台阶。
//
//  ---- 为什么是 0.5，而不是 Hillaire 论文里的 0.3 ----
//  这个值**不是**"在段内哪里采样更准"的自由参数，它由段内积分器的形式唯一确定。
//  VistaSegmentIntegral 用采到的 σ_s / σ_t 解析地算 ∫₀^dt σ_s·exp(-σ_t·s) ds，
//  也就是说**段内衰减已经被精确处理了**，采样点只负责给出"这一段的介质是什么"。
//  于是最优采样点是让常介质假设误差最小的那一点 = 透射率加权的质心：
//      s*/dt = [1 − (1+x)·e^(−x)] / [x·(1 − e^(−x))]，　x = σ_t·dt
//      x → 0 ⇒ 0.5　　x = 1 ⇒ 0.418　　x = 2 ⇒ 0.3435　　x = 3 ⇒ 0.281
//  Hillaire 的 0.3 是 x ≈ 2.8 处的质心 —— 他的段光学深度就在那个量级。
//  本项目的段不在那个区间：AP LUT 的 Log 分布 + VistaFogStepMaxKm 的收紧
//  把每段的 x 压得很小（雾那边 x ≤ 0.4·σ_t·efold，晴空更小），所以正确的值是 **0.5**。
//
//  ---- 抄那个 0.3 的代价（实测）----
//  段内取样偏离中心会让整条 march 从二阶退化成一阶（一阶偏差不再左右对称抵消）。
//  症状是 **4 倍步数只把误差降到 1/3.7**（一阶该 4 倍、二阶该 16 倍），
//  而当时我把剩下那部分读成了"三线性重建误差"，差点去改切片分布。
//  改 0.3 → 0.5 之后：
//      视角③ 切片误差 B/C/D/G　5.14 / 5.47 / 5.97 / 5.15 %　→　0.37 / 0.43 / 0.49 / 0.38 %
//      晴空 切片中心 vs 4096 步参照　0.25~0.29 %　→　0.07 %
//  一个从别人论文里抄来的经验常数，它的适用区间可能正好被你自己的另一处优化推出去。
//
//  ---- 为什么不做"按 τ 自适应的采样点" ----
//  按上面的公式逐段算 s*/dt 是死代码：两个会让 0.5 失准的区间**互斥**。
//    · x 大（段光学深度高）→ 只可能发生在均匀/准均匀介质里，
//      而常密度段被 VistaSegmentIntegral **精确**积分，采样点取哪儿都一样；
//    · 介质变化陡（雾）→ VistaFogStepMaxKm 把 dt 压到远小于 e 折长度，
//      于是 x ≪ 1，而 x → 0 的质心**就是** 0.5。
//  所以自适应能改善的区间是空集。这条要写下来，否则它看起来永远像一个待办优化。
//  与 VistaFogStepMaxKm 里那条 x²/24 的上限推导是耦合的：改这个宏必须重核那条。
// ----------------------------------------------------------------------------
#define VISTA_SAMPLE_SEGMENT_T 0.5

// ----------------------------------------------------------------------------
//  单个采样点的介质求值
//
//  为什么把它从积分器循环里抽出来：AP LUT 需要**一次行进、逐切片输出**的循环
//  （见 AerialPerspective.hlsl），那个循环结构与这里的不同，无法直接复用
//  VistaIntegrateScatteredLuminance。但两者必须共用同一份能量计算 ——
//  否则就回到了本文件开头警告的那个 bug：远山雾色与天空色在交界处对不上。
//  所以只复制循环，绝不复制物理。
//
//  ---- 雾为什么也从这里进 ----
//  雾是并列的第四个介质组分（见 FogMedium.hlsl 的"与大气介质的关系"），
//  它的相位、到太阳的透射率、消光都必须和 Rayleigh/Mie/臭氧在**同一个表达式**里合成，
//  否则档 A（近层 froxel）与档 D（AP LUT）在交界距离上会对不上 ——
//  那正是本文件开头警告的那类 bug，只是换了一条缝。
//  不需要雾的消费者（静态 LUT、MS LUT、SkyView LUT）传 VistaFogSampleNone()：
//  那是个全零字面量结构体，内联后常量折叠会把雾的三项连同
//  VistaFogTransmittanceToSun 一起消掉，所以它们的指令数与接雾之前一致，
//  #15 量到的 A/B 一致性也不受影响。
// ----------------------------------------------------------------------------
struct VistaScatterSample
{
    float3 scattered;   // 该点的散射源项（已含相位、到太阳的透射率、星球阴影、多次散射、雾）
    float3 msAs1;       // 入射亮度恒为 1 时的散射系数和（建 MS LUT 用，**不含雾**）
    float3 extinction;  // 已兜底 >= 1e-9，可直接作除数
};

// ----------------------------------------------------------------------------
//  雾的散射源项（每单位 σ_s 的入射亮度 J）
//
//  ---- 为什么从 VistaEvaluateScatterSample 里提出来 ----
//  天空像素不走 march（AP 排除它们，见 FogMedium.hlsl 的"天空像素的雾"），
//  但它需要**一模一样**的 J：同一个 HG 相位、同一份到太阳的大气透射率、
//  同一个星球阴影、同一个自遮蔽、同一个环境项强度。
//  在天空盒里重写一遍 J 就是"同一个量的第二份实现"——本项目已经点过名，
//  哪怕只有 8 行也算。而它分叉的症状是**地平线上远山的雾色与天空的雾色差一点**，
//  恰好长得像"AP 切片分布不够密"，会把人引到完全错的地方去查。
//
//  ---- 返回的是什么 ----
//  J，单位 cd/m²（绝对光度量）。真正的源项是 σ_s·J：
//    · march 里逐样本乘 fog.scattering（σ_s 随高度变）；
//    · 天空的闭式解里 σ_s 被积分约掉了，只剩 albedo（见 VistaApplyFogToSky）。
//  所以这个函数**不含** σ_s —— 含了的话第二个消费者就得把它除回去。
// ----------------------------------------------------------------------------
//  ---- 为什么自遮蔽是参数，而不是在这里算 ----
//  两个消费者要的**不是同一个数**：march 逐样本要局部密度下的 T_sun，而天空的
//  闭式解要它沿整条射线的入散射加权均值（VistaFogSunTransmittanceMean）。
//  在这里调 VistaFogTransmittanceToSun 就等于把 march 的那一份写死，
//  天空只能在外面再乘一个比值去纠正 —— 那是「同一个量的第二份实现」的变体，
//  而且症状（低太阳时天空的雾与远山的雾亮度差一截）与相位写错无法区分。
//  两个消费者各自算好自己的那一份传进来，J 的结构就仍然只有一处。
// ----------------------------------------------------------------------------
float3 VistaFogSourceRadiance(
    VistaFogSample fog, float3 rayDir, float3 sunDir,
    float3 sunTransmittance, float earthShadow, float3 fogSelfShadow,
    float3 sunIlluminance, float3 ambientRadiance, bool applyPhase)
{
    float phase = applyPhase
        ? VistaHenyeyGreensteinPhase(fog.phaseG, dot(rayDir, sunDir))
        : VISTA_ISOTROPIC_PHASE;

    // 雾自己对阳光的衰减只作用在雾这一项上（空气不会被雾的液滴挡住两次），
    // 所以 fogSelfShadow 在这里，而不是乘到公共的 sunTransmittance 上。
    float3 direct = sunIlluminance * (earthShadow * phase) * sunTransmittance
                  * fogSelfShadow;

    // 各向同性相位下 ∫p·L dω 就是平均入射亮度本身，所以这里是 L̄ 而**不是** L̄/4π ——
    // 那个 1/4π 已经在 VistaShAmbientMean 的推导里约掉了（(1/4π)·∫L dω = L̄）。
    // 写成后者的症状是阴影里的雾暗 12.6 倍，看起来像"环境项强度调小了"，很难反查。
    // 不套 fogSelfShadow：自遮蔽只挡直射太阳，不挡天光。
    return direct + ambientRadiance * _VistaFogHeight.z;
}

//  fog: 该点的雾介质。由调用方用 VistaSampleFog(VistaFogHeightMeters(t, rayDir.y)) 取 ——
//  **不在这里取**，因为雾的高度必须从 t 推、不能从 p 反算（fp32 在 6360 km 上的
//  ulp 是 0.49 m，理由见 FogMedium.hlsl）。让调用方传进来同时也让 #24 的局部雾体
//  可以在传入前往这个结构体里叠密度，不需要再改一次签名。
//
//  sunShadow: 阴影贴图给出的主光可见度，1 = 全亮，0 = 全遮（#20 起）。
//
//  ---- 为什么是参数，而不是在这里查阴影贴图 ----
//  这个函数有三个消费者，其中两个**没有**阴影坐标可用：SkyView LUT 的 march 是
//  球对称参数化的（没有世界位置），MS LUT 更是静态表。在这里查就得让那两条路径
//  也去 include URP 的 Shadows.hlsl 并绑一张贴图，而它们根本没有相机。
//
//  ---- 为什么只乘直射项 ----
//  下面 earthShadow 的位置就是它该在的位置：它只乘 phaseTimesScattering 与
//  VistaFogSourceRadiance 的直射项，**不乘** multiScattered、也不乘雾的天光环境项。
//  级联阴影扮演的是同一个物理角色（「这一点看不见太阳」），所以折进去的方式就是
//  earthShadow * sunShadow，一个字都不用改。
//  乘到多次散射上的症状是阴影里的雾变成纯黑（真实的阴影区靠 MS 与天光维持亮度），
//  那正是 Hillaire / HDRP / UE5 都只让体积阴影衰减单次散射的理由。
//
//  ---- 为什么不放进 VistaRaymarchSettings ----
//  那个结构体是**逐射线**的；阴影是**逐样本**的。放进去就得在循环里改结构体字段，
//  而它同时被别的字段共享，一次误写会污染整条射线的相位/多次散射开关。
//  也不放进 VistaFogSample：那样阴影只会作用到雾上，Rayleigh / Mie 两个组分
//  仍然是全亮的 —— 症状是「光柱只在浓雾里有，晴空的树影完全不投到空气上」。
VistaScatterSample VistaEvaluateScatterSample(
    float3 p, float3 rayDir, float3 sunDir, VistaFogSample fog, VistaRaymarchSettings s,
    float sunShadow)
{
    float  r  = length(p);
    float3 up = p / r;

    VistaMediumSample medium = VistaSampleMedium(r - VISTA_BOTTOM_RADIUS);

    float  muSun = dot(sunDir, up);
    float3 transmittanceToSun = VistaSampleTransmittanceToSun(r, muSun);
    // 星球阴影与级联阴影是同一个物理角色（「这一点看不见太阳」），一次乘完。
    // 合并在这里而不是分别乘到下面两处：分开写就有两个地方可能漏掉一项，
    // 而漏掉的症状（阴影里的空气偏亮）在两项之间无法区分。
    float  earthShadow = VistaEarthShadow(p, sunDir) * sunShadow;

    // 只剩大气两个组分。雾的相位/自遮蔽/环境项统一在 VistaFogSourceRadiance 里，
    // 见下面那一次累加。晴空时这里与 #18b 之前**逐位相同**（原来加的是 0.0，加 0 精确）。
    float3 phaseTimesScattering;
    if (s.applyPhase)
    {
        float cosTheta = dot(rayDir, sunDir);
        phaseTimesScattering =
              medium.scatteringRayleigh * VistaRayleighPhase(cosTheta)
            + medium.scatteringMie      * VistaHenyeyGreensteinPhase(_VistaMieExtinct.w, cosTheta);
    }
    else
    {
        phaseTimesScattering =
            (medium.scatteringRayleigh + medium.scatteringMie) * VISTA_ISOTROPIC_PHASE;
    }

    float3 multiScattered = 0.0;
    if (s.useMultiScattering)
    {
        multiScattered = VistaSampleMultiScattering(r, muSun)
                       * (medium.scatteringRayleigh + medium.scatteringMie);
    }

    VistaScatterSample o;
    o.scattered = s.sunIlluminance
                * (earthShadow * transmittanceToSun * phaseTimesScattering)
                + s.sunIlluminance * multiScattered;

    // 雾的直射 + 环境项，一次累加。
    // 与 #18b 之前不是逐位相同（雾那一项的结合顺序变了，fp32 相对扰动 ~1e-7），
    // 但晴空是逐位相同的 —— fog.scattering = 0 时这整项精确为 0。
    // 提取的收益（J 只有一份实现）远大于这个扰动，它比 fp16 的 LUT 存储精度还小两个数量级。
    o.scattered += fog.scattering * VistaFogSourceRadiance(
        fog, rayDir, sunDir, transmittanceToSun, earthShadow,
        VistaFogTransmittanceToSun(fog, sunDir.y),
        s.sunIlluminance, s.fogAmbientRadiance, s.applyPhase);

    // 不含雾：MS LUT 是静态的、球对称参数化的大气量，见 FogMedium.hlsl。
    o.msAs1 = medium.scatteringRayleigh + medium.scatteringMie;
    // 大气顶附近密度指数衰减到接近 0，除法要兜底
    o.extinction = max(medium.extinction + fog.extinction, 1e-9);
    return o;
}

// ----------------------------------------------------------------------------
//  天空像素的雾合成（#18b）
//
//  ---- 公式 ----
//      L' = L_sky·T_fog + albedo·J·(1 − T_fog)
//  右项**不是**凑出来的经验式，它是精确解。令 u(t) = ∫₀^t σ_t ds，则 σ_t dt = du，
//      ∫₀^∞ σ_s·ρ·J·e^(−u) dt = albedo·J·∫₀^{u_∞} e^(−u) du = albedo·J·(1 − T_∞)
//  逐通道成立（σ_t 是 RGB，三个通道各自独立积分）。σ_s 在换元里被约掉了，
//  所以这里乘的是无量纲的 albedo 而不是 fog.scattering —— 这也是
//  VistaFogAlbedo() 需要单独提出来的原因。
//
//  ---- 三个近似里剩下的两个（第一个已经被消掉了）----
//  ① J 沿视线取常数。**自遮蔽那一项已经不是近似了**：它随高度的变化有精确的
//     入散射加权闭式解，见 VistaFogSunTransmittanceMean。#18b 的第一版把它冻在
//     相机处，实测在「太阳 5° + 自遮蔽开」下相对差 34.8%（同一档关掉自遮蔽只有 1.7%），
//     所以那不是"小近似"而是主项，必须算准。
//     仍然取常数的是 T_atmSun 与 earthShadow 两项：相位是**精确**常数
//     （cosθ 沿直线不变），而这两项在雾的有效路径（≤ 一个 e 折，≤ 数十 km）上
//     几乎不变 —— 大气到太阳的透射率沿水平方向的变化尺度是数百 km。
//  ② 整个 L_sky 都被 T_fog 衰减，包括那些**在雾层内部**产生的大气内散射
//     （它们本该只被部分雾衰减）。误差 ≈「大气内散射有多大份额是在雾的沿线跨度内
//     产生的」×(1 − T_fog)，两个因子的乘积在 T_fog ≈ 0.3 附近取极大：
//       · 相机在雾里、非掠射：两者的沿线跨度分别是 H/dy 与 H_Rayleigh/dy，
//         份额 = H/H_R = 50 m / 8 km ≈ 0.6%。实测 ≤ 1.8%，对得上。
//       · 掠射（Chapman 区）：两个跨度**都**被曲率封顶成 sqrt(2πR·H)，
//         份额变成 sqrt(H/H_R) = 7.9% —— 是比值的**平方根**，不再是比值。
//         再乘上 O(1) 的分布因子，量级就到十几个百分点。
//         实测最差 16.4%（相机 300 m、雾层 50 m、σ_t = 10/km、仰角 0.06°）。
//     这个区间是两项式的**结构性上限**，不是可以调参调掉的东西：要修正它必须知道
//     大气内散射沿线的分布，而那需要第二张表 —— 也就是 #18b 一开始否决掉的方案②。
//     UE5 的 ExponentialHeightFog 叠在 SkyAtmosphere 上有同一个上限。
//     判据5 给这个区间单独一条**实测基线**（不是质量门），见 VistaSkyFogSelfTest。
//  ③ 雾的内散射不被大气透射率衰减。雾的有效路径 ~1 km 内大气透射率 >0.97。
//
//  ---- 为什么在曝光之前 ----
//  J 是绝对光度量（cd/m²），L_sky 在这里也还是绝对量。调用点必须在
//  `luminance *= VISTA_EXPOSURE` **之前**调这个函数，否则 (1−T) 那一项会缺一次曝光，
//  症状是雾在天空上亮 4 万倍 —— 那个是能一眼看见的，所以不给它加运行时保护。
//
//  fogAmbientRadiance 由调用方传（用 VistaShAmbientMean / VistaSkyAmbientMean），
//  理由与 VistaRaymarchSettings.fogAmbientRadiance 同：本文件不依赖那个 SSBO。
// ----------------------------------------------------------------------------
void VistaApplyFogToSky(
    inout float3 skyLuminance, float3 posKm, float3 rayDir, float3 sunDir,
    float3 sunIlluminance, float3 fogAmbientRadiance)
{
    // 相机处的雾。闭式解的 τ = σ_t·ρ(相机)·pathKm 用的就是这一份密度。
    VistaFogSample fog = VistaSampleFogAlongRay(0.0, rayDir.y);

    // τ 留在手上（不是只留 exp(−τ)）：下面那个加权均值必须用**同一个** τ，
    // 否则 I = albedo·J̄·(1 − T) 就从恒等式退化成两个近似的比较。
    float3 tauView      = VistaFogOpticalDepth(fog, VistaFogSkyRayPathKm(rayDir.y));
    float3 transmittance = exp(-tauView);

    // 自遮蔽沿射线是变的（爬高 ⇒ ρ 掉 ⇒ 遮挡变弱），而两项式只有一个 J̄。
    // 冻在相机处等于取 τ → ∞ 的极限，实测在中等厚度 + 低太阳下偏暗 34.8%；
    // 这里代进精确的入散射加权均值，见 VistaFogSunTransmittanceMean 的推导。
    float3 tauSun     = VistaFogOpticalDepth(fog, VistaFogSunPathKm(sunDir.y));
    float3 selfShadow = VistaFogSunTransmittanceMean(tauView, tauSun);

    float  r  = length(posKm);
    float3 up = posKm / r;

    float3 sourceRadiance = VistaFogSourceRadiance(
        fog, rayDir, sunDir,
        VistaSampleTransmittanceToSun(r, dot(sunDir, up)),
        VistaEarthShadow(posKm, sunDir),
        selfShadow,
        sunIlluminance, fogAmbientRadiance, true);

    // 雾关掉时 σ_t = 0 ⇒ transmittance 逐通道精确为 1 ⇒ x·1 + albedo·J·0 = x，
    // 逐位不变。这就是"失能态 = 零态"在这个缝上的表现，不需要 uniform 分支。
    skyLuminance = skyLuminance * transmittance
                 + VistaFogAlbedo() * (sourceRadiance * (1.0 - transmittance));
}

// ----------------------------------------------------------------------------
//  步段内的解析积分  ∫₀^dt S·exp(-σ·t) dt = S·(1 - exp(-σ·dt)) / σ
//
//  照着公式直写，在 σ·dt 很小时会**灾难性相消**：fp32 在 1.0 下方的间距是
//  5.96e-8，σ·dt 掉到 1e-7 量级时，exp(-σ·dt) 与 1.0 之间只剩一两个可表示的
//  台阶，相减出来的几乎全是舍入噪声（GPU 的 exp 本身还是近似实现，只会更糟）。
//
//  这不是边角情况。地表 σ ≈ 1e-2 /km，步长短于 ~1 cm 就会踩到，而"步长很短"
//  恰恰是两个真实场景：AP 的首片天生只有几米长；Step 1 把 s.tMax 设成近处
//  几何的深度，一面 1 m 外的墙就是这个量级。#7 的自检里 256 步参考解跑 1 m
//  路径时 σ·dt ≈ 4e-8，实测把 errCenter 抬到 126% —— 当时差点被误判成
//  "切片布得太近导致行进不准"，其实是**尺子自己坏了**。
//
//  σ·dt < 1e-4 时改用 (1 - e^{-x})/x = 1 - x/2 + x²/6 - … 的截断展开，
//  截断误差 < x³/24 ≈ 4e-14，比相消误差小七个数量级；且展开式整个不做除法，
//  σ 兜底到 1e-9 的大气顶附近也不再放大误差。
//  逐通道选择：RGB 三个方向的 σ 差 6 倍，同一步长可能一个通道相消一个不相消。
// ----------------------------------------------------------------------------
float3 VistaSegmentIntegral(float3 source, float3 extinction, float dt)
{
    float3 x = extinction * dt;

    float3 exact  = (source - source * exp(-x)) / extinction;
    float3 series = source * dt * (1.0 - x * 0.5 + x * x * (1.0 / 6.0));

    float3 useSeries = step(x, 1e-4);   // x <= 1e-4 时取 1
    return lerp(exact, series, useSeries);
}

// posKm / sunDir 都在"星球中心为原点"的大气空间（km）。rayDir、sunDir 须归一化。
VistaScatteringResult VistaIntegrateScatteredLuminance(
    float3 posKm, float3 rayDir, float3 sunDir, VistaRaymarchSettings s)
{
    VistaScatteringResult result;
    result.luminance     = 0.0;
    result.transmittance = 1.0;
    result.opticalDepth  = 0.0;
    result.multiScatAs1  = 0.0;

    const float3 planetCenter = float3(0.0, 0.0, 0.0);

    // ---- 积分上限 ----
    float tBottom = VistaRaySphereIntersectNearest(posKm, rayDir, planetCenter, VISTA_BOTTOM_RADIUS);
    float tTop    = VistaRaySphereIntersectNearest(posKm, rayDir, planetCenter, VISTA_TOP_RADIUS);

    float tMax;
    if (tBottom < 0.0)
    {
        if (tTop < 0.0)
            return result;      // 在大气外且视线不进入大气
        tMax = tTop;
    }
    else
    {
        tMax = (tTop > 0.0) ? min(tTop, tBottom) : tBottom;
    }

    bool hitGround = (tBottom > 0.0) && (tMax == tBottom);

    if (s.tMax >= 0.0 && s.tMax < tMax)
    {
        tMax = s.tMax;          // 被不透明物遮挡：积分到深度处为止，地面反弹不再成立
        hitGround = false;
    }
    if (tMax <= 0.0)
        return result;

    // ---- 步进分布 ----
    // 非均匀分布（t 的平方）把采样点堆在近处：航空透视的梯度几乎全在前几百米，
    // 均匀步进会把预算浪费在远处已经饱和的区间。
    float sampleCount      = s.sampleCount;
    float sampleCountFloor = s.sampleCount;
    float tMaxFloor        = tMax;
    if (s.variableSampleCount)
    {
        sampleCount      = lerp(s.sampleCount, s.sampleCountMax, saturate(tMax * 0.01));
        sampleCountFloor = floor(sampleCount);
        // 把 tMax 缩到最后一个完整步段，避免末段长度突变造成的带状
        tMaxFloor = tMax * sampleCountFloor / sampleCount;
    }

    // 步段内的取样位置见 VISTA_SAMPLE_SEGMENT_T。

    float3 throughput = 1.0;
    float t  = 0.0;
    float dt = tMax / sampleCount;

    for (float i = 0.0; i < sampleCount; i += 1.0)
    {
        if (s.variableSampleCount)
        {
            float t0 = i / sampleCountFloor;
            float t1 = (i + 1.0) / sampleCountFloor;
            t0 = t0 * t0;
            t1 = t1 * t1;
            t0 = tMaxFloor * t0;
            t1 = (t1 > 1.0) ? tMax : (tMaxFloor * t1);
            dt = t1 - t0;
            t  = t0 + dt * VISTA_SAMPLE_SEGMENT_T;
        }
        else
        {
            // 段边界是 [i·dt, (i+1)·dt]，取样点在段内 VISTA_SAMPLE_SEGMENT_T 处 ——
            // 和上面变步长分支同一个语义。曾经写成"dt = 相邻取样点之差"，那样第一段
            // 只有 segT·dt、末段整个丢掉，总覆盖变成 tMax·(N−1+segT)/N：
            // N=256、segT=0.5 时少积 0.195%（当时 segT=0.3，是 0.27%）。
            // ⚠ 这个 bug 曾被当成 #7 里 errCenter 在 20 组配置上都读到 0.25~0.29% 的
            // **全部**原因（0.27% 与那个区间吻合得太好，当时就没再往下查）。
            // 后来 segT 0.3→0.5 把同一个读数降到 0.07%，说明段内取样点偏离中心的
            // 一阶偏差至少也占一大块。两个原因的量级重叠（都在 0.2~0.3%），
            // 而我没有保留"dt 修好、segT 仍是 0.3"这个中间态的读数，
            // 所以**两者各占多少现在已经无法区分**。
            // 能确认的只有 segT 那一项（它有前后对照）。要补的话得把 dt 定义
            // 故意改回错的再测一次 —— 值不值得看 #27。
            dt = tMax / sampleCount;
            t  = (i + VISTA_SAMPLE_SEGMENT_T) * dt;
        }

        float3 p = posKm + t * rayDir;

        // includeFog 的默认值是 false，于是本积分器服务的 Transmittance / MS /
        // SkyView LUT 与地面反弹走的仍是全零字面量那条路 —— 那些都是静态或
        // 球对称/方位对称的大气量，雾不能进（见 FogMedium.hlsl）。
        // 天空像素的雾靠雾体远端的 transmittance/inScatter 覆盖，不靠这里。
        // true 的唯一使用者是 VistaApReference：判据的尺子必须和被测的 AP kernel
        // 用同一份物理，否则接雾当天就会报一个虚构的失败。
        //
        // 写成 if/else 而不是 ?: —— HLSL 的三元运算符只对标量/矢量/矩阵生效，
        // 两边是 struct 时 fxc 报 "type mismatch between conditional values"。
        VistaFogSample fog = VistaFogSampleNone();
        if (s.includeFog)
            fog = VistaSampleFogAlongRay(t, rayDir.y);

        // sunShadow = 1：这个积分器服务 SkyView / MS / 判据参考解三条路径，
        // 它们都是球对称参数化的，没有世界位置可以去查阴影贴图。
        // 显式写 1.0 而不是给形参一个默认值：默认值会让「忘了传阴影」
        // 悄悄编译过去，而那恰好是「整个场景没有光柱、且不报错」的成因。
        VistaScatterSample smp =
            VistaEvaluateScatterSample(p, rayDir, sunDir, fog, s, 1.0);

        float3 sampleOpticalDepth   = smp.extinction * dt;
        float3 sampleTransmittance  = exp(-sampleOpticalDepth);
        result.opticalDepth += sampleOpticalDepth;

        // 步段内的解析积分：∫ S·exp(-σt) dt = S·(1 - exp(-σ·dt)) / σ。
        // 直接用 S·dt（矩形法）在光学厚的步段会明显高估，是低步数下带状的主因。
        // 短步段的相消问题在 VistaSegmentIntegral 里处理，别在这儿照公式直写。
        result.luminance    += throughput * VistaSegmentIntegral(smp.scattered, smp.extinction, dt);

        // MS LUT 的输入项：入射亮度恒为 1、无相位、无遮挡时的散射传递量
        result.multiScatAs1 += throughput * VistaSegmentIntegral(smp.msAs1, smp.extinction, dt);

        throughput *= sampleTransmittance;
    }

    // ---- 地面反弹（Lambert 一次漫反射）----
    if (s.includeGroundBounce && hitGround)
    {
        float3 p = posKm + tBottom * rayDir;
        float  r = length(p);
        float3 up = p / r;
        float  muSun = dot(sunDir, up);
        float3 transmittanceToSun = VistaSampleTransmittanceToSun(r, muSun);
        float  nDotL = saturate(muSun);

        result.luminance += s.sunIlluminance * transmittanceToSun * throughput
                          * nDotL * _VistaGround.xyz * (1.0 / PI);
    }

    result.transmittance = throughput;
    return result;
}

// ============================================================================
//  Sky-View LUT 参数化
//
//  这张表能成立的前提是**绕 up 轴的方位对称性**：给定相机高度与太阳天顶角后，
//  天空亮度只取决于 (视线天顶角, 视线与太阳的方位夹角) 两个量。
//  于是本该是 4D 的天空被压成一张 2D 表 —— 这是整个方案里性价比最高的一步降维。
//  代价：一旦引入方位上不对称的东西（云、地形遮挡、局部雾），这张表就不再够用，
//  那些必须走别的通道（Step 7 的云、Step 3 的雾）。
//
//  uv.y 在**地平线处硬分段**：< 0.5 为地平线以上，> 0.5 为地平线以下，
//  两段内各做一次平方 warp 把纹素往地平线堆。
//  为什么必须这么做：地平线上下几度内是长路径 + Mie 前向峰 + 地面反弹三者的交界，
//  亮度梯度全场最大。线性映射下 108 行的表会在地平线留一条肉眼可见的横向台阶
//  （日落时最明显，因为那时地平线附近还叠加了强色相变化）。
//
//  uv.x -> lightViewCosAngle 同样做平方 warp：uv.x=1 对应正对太阳，
//  HG 相位在太阳周围几度内变化一个数量级，需要额外纹素。
// ============================================================================

TEXTURE2D(_VistaSkyViewLut);

void VistaSkyViewLutUvToParams(float viewHeight, float2 uv,
                               out float viewZenithCosAngle, out float lightViewCosAngle)
{
    // 抵消采样端的纹素中心内缩，保证正反映射严格互逆
    uv = float2(VistaUnitRangeFromTexCoord(uv.x, _VistaSkyViewLutSize.x),
                VistaUnitRangeFromTexCoord(uv.y, _VistaSkyViewLutSize.y));

    // beta: 从相机看星球边缘的张角；zenithHorizonAngle: 天顶到地平线的张角。
    // 相机越高，地平线越"往下沉"，所以这两个量必须随 viewHeight 变化，
    // 不能写成常量 PI/2 —— 否则相机爬山时地平线会在 LUT 里错位。
    float vHorizon = sqrt(max(0.0, viewHeight * viewHeight - VISTA_BOTTOM_RADIUS_2));
    float cosBeta  = vHorizon / max(viewHeight, 1e-4);
    float beta     = acos(clamp(cosBeta, -1.0, 1.0));
    float zenithHorizonAngle = PI - beta;

    if (uv.y < 0.5)
    {
        // 地平线以上：uv.y = 0 -> 天顶，uv.y = 0.5 -> 地平线
        float coord = 1.0 - 2.0 * uv.y;
        coord *= coord;
        coord = 1.0 - coord;
        viewZenithCosAngle = cos(zenithHorizonAngle * coord);
    }
    else
    {
        // 地平线以下：uv.y = 0.5 -> 地平线，uv.y = 1 -> 天底
        float coord = uv.y * 2.0 - 1.0;
        coord *= coord;
        viewZenithCosAngle = cos(zenithHorizonAngle + beta * coord);
    }

    float coord = uv.x;
    coord *= coord;
    // 负号：uv.x = 1 时 lightViewCosAngle = -1... 见下方 ParamsToUv 的对应式，
    // 两边是同一组约定，改一处必须改另一处。
    lightViewCosAngle = -(coord * 2.0 - 1.0);
}

float2 VistaSkyViewLutParamsToUv(float viewHeight, float viewZenithCosAngle,
                                 float lightViewCosAngle, bool intersectGround)
{
    float vHorizon = sqrt(max(0.0, viewHeight * viewHeight - VISTA_BOTTOM_RADIUS_2));
    float cosBeta  = vHorizon / max(viewHeight, 1e-4);
    float beta     = acos(clamp(cosBeta, -1.0, 1.0));
    float zenithHorizonAngle = PI - beta;

    float2 uv;
    if (!intersectGround)
    {
        float coord = acos(clamp(viewZenithCosAngle, -1.0, 1.0)) / max(zenithHorizonAngle, 1e-4);
        coord = 1.0 - coord;
        coord = sqrt(max(0.0, coord));   // warp 的逆：平方 <-> 开方
        coord = 1.0 - coord;
        uv.y = coord * 0.5;
    }
    else
    {
        float coord = (acos(clamp(viewZenithCosAngle, -1.0, 1.0)) - zenithHorizonAngle) / max(beta, 1e-4);
        coord = sqrt(max(0.0, coord));
        uv.y = coord * 0.5 + 0.5;
    }

    uv.x = sqrt(max(0.0, -lightViewCosAngle * 0.5 + 0.5));

    return float2(VistaTexCoordFromUnitRange(saturate(uv.x), _VistaSkyViewLutSize.x),
                  VistaTexCoordFromUnitRange(saturate(uv.y), _VistaSkyViewLutSize.y));
}

// 相机在大气之外时把积分起点推到大气顶。返回 false = 视线完全不进入大气。
// 本项目相机永远在大气内，但这几行是白送的正确性，且 Editor 里把 bottomRadius
// 调小做参数实验时会立刻用到。
bool VistaMoveToTopAtmosphere(inout float3 posKm, float3 rayDir)
{
    float viewHeight = length(posKm);
    if (viewHeight <= VISTA_TOP_RADIUS)
        return true;

    float tTop = VistaRaySphereIntersectNearest(posKm, rayDir, float3(0.0, 0.0, 0.0), VISTA_TOP_RADIUS);
    if (tTop < 0.0)
        return false;

    float3 up = posKm / viewHeight;
    // 往内推 10 m，避免起点正好落在边界上导致后续判别式在 0 附近抖动
    posKm = posKm + rayDir * tTop - up * VISTA_PLANET_RADIUS_OFFSET;
    return true;
}

// 采样端便捷函数。posKm 在大气空间；rayDir / sunDir 是单位矢量，
// 因为大气空间与世界空间同朝向，两者可以直接混用（见 AtmosphereDef.hlsl 的说明）。
float3 VistaSampleSkyViewLut(float3 posKm, float3 rayDir, float3 sunDir)
{
    float viewHeight = length(posKm);
    float3 up = posKm / max(viewHeight, 1e-4);

    float viewZenithCosAngle = dot(rayDir, up);

    // 构造以 up 为轴、在水平面内朝向太阳的正交基，取出"视线与太阳的方位夹角"余弦。
    // 这一步就是把 3D 压成 2D 的关键：只保留方位差，丢弃绝对方位。
    float3 sideRaw = cross(up, rayDir);
    float  sideLen = length(sideRaw);

    float lightViewCosAngle;
    if (sideLen < 1e-5)
    {
        // 视线与 up 平行（正看天顶/天底）：方位角无定义。
        // 此时 LUT 的整行都是同一个值（viewZenithCos = ±1 时 rayDir 与 uv.x 无关），
        // 所以取任意值都精确，取 0 即可，关键是不能让 normalize 产出 NaN。
        lightViewCosAngle = 0.0;
    }
    else
    {
        float3 sideVector    = sideRaw / sideLen;
        float3 forwardVector = normalize(cross(sideVector, up));
        float2 lightOnPlane  = float2(dot(sunDir, forwardVector),
                                      dot(sunDir, cross(forwardVector, up)));
        lightOnPlane = normalize(lightOnPlane + 1e-8);
        lightViewCosAngle = lightOnPlane.x;
    }

    bool intersectGround = VistaRayIntersectsGround(viewHeight, viewZenithCosAngle);
    float2 uv = VistaSkyViewLutParamsToUv(viewHeight, viewZenithCosAngle,
                                          lightViewCosAngle, intersectGround);
    return SAMPLE_TEXTURE2D_LOD(_VistaSkyViewLut, sampler_LinearClamp, uv, 0).rgb;
}

// 太阳圆盘。**不烘进 SkyView LUT**：192×108 的表上太阳只占不到一个纹素，
// 烘进去必然被抹成一团糊，而且相机转动时会随 LUT 的双线性插值抖动。
// 解析画法既锐利又稳定，且角半径可以自由调（大气散射用的 0.545° 是地球实测值）。
// limbDarkening: 太阳盘面边缘比中心暗，是肉眼可辨的细节，公式取 Hestroffer & Magnan 1998 的简化式。
float3 VistaSunDisc(float3 rayDir, float3 sunDir, float3 transmittanceToSun)
{
    float cosTheta = dot(rayDir, sunDir);
    float cosLimit = _VistaSun.w;               // cos(角半径)
    if (cosTheta < cosLimit)
        return 0.0;

    // 归一化到盘面内半径 [0,1]
    float sinLimit2 = max(1e-8, 1.0 - cosLimit * cosLimit);
    float rDisc = sqrt(saturate((1.0 - cosTheta * cosTheta) / sinLimit2));
    float mu = sqrt(saturate(1.0 - rDisc * rDisc));
    float limbDarkening = 0.397 + 0.603 * pow(max(mu, 1e-4), 0.4);

    // 从"总照度 lux"换算成"盘面亮度 cd/m²"：除以盘面立体角 2π(1-cosLimit)
    float solidAngle = 2.0 * PI * (1.0 - cosLimit);
    return _VistaSun.xyz / solidAngle * limbDarkening * transmittanceToSun;
}

#endif // VISTA_ATMOSPHERE_SCATTERING_INCLUDED
