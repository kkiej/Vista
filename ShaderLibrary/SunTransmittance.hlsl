#ifndef VISTA_SUN_TRANSMITTANCE_INCLUDED
#define VISTA_SUN_TRANSMITTANCE_INCLUDED

// 逐像素太阳透射率修正。
//
// 干什么：平行光的颜色是**一个**值，代表参考高度处的太阳色（大气把它染红/削弱了多少）。
// 但一个几公里落差的场景里，山顶与谷底看到的太阳并不同色 —— 谷底的阳光穿过更厚的
// 低层大气，更红更暗。这一层就是把那个差异补回来。
//
// 为什么它是一个**比值**而不是 T 本身：见 AtmosphereDef.hlsl 里
// _VistaSunTransmittanceRef 的注释（简版：T_ref 已经在 Light.color 里了，
// 再乘一次 T 就是乘两遍）。
//
// ── 这一层担保不到的地方 ──
//
// 太阳落到**参考高度**的地平线以下之后，CPU 侧的 VistaSunTransmittance.Evaluate
// 返回 0（见那里 k_MinMuSun 的注释），于是 Light.color 整个是 0。
// 乘任何比值都还是 0 —— 也就是说此时物理上还能看到太阳的山顶，拿不到直射光。
// 这不是可以修的疏漏，是「T 已经乘进灯里」这个选择的**固有代价**：信息在 CPU 侧
// 就被销毁了，下游没有任何办法恢复。量级不小：参考高度切线处 T 的红通道 ≈ 0.107，
// 折算 0.104 render units，高于本项目 1e-3 的绝对可见门限，所以它是**看得见**的。
//
// 为什么仍然选这条路：另一条是「CPU 不把 T 乘进灯，全交给逐像素」。那样上面这条
// 局限没有了，但代价是所有**不走 Vista/Lit** 的东西 —— URP 自带的 Lit、粒子、
// 透明物、任何第三方 shader —— 会彻底失去太阳衰减，变成「太阳永不变色」。
// 那是全画面的回归，比「日落最后一小段山顶偏暗」严重得多。
//
// 还有一条：只覆盖主平行光。点光/聚光不参与（它们不是天体，没有"穿过大气层"这回事，
// 真要做也是另一套体积衰减）。天空环境光与 bakedGI 同样不参与 ——
// 天光的透射率是 Sky-View LUT 自己积出来的，在这里再乘一遍又是乘两遍。

#include "Packages/com.kkiej.vista/ShaderLibrary/AtmosphereScattering.hlsl"

/// T_ref 的下限。
///
/// 怎么定的、不是随手取的：分母掉到 0 会给出 inf，而分子在同一时刻并不为 0
/// （地平线附近 T 的红通道还有 1e-1 量级），所以 0/0 式的巧合救不了，必须有下限。
/// 取 1e-6 的依据是**本项目自己的可见门限**：T_ref = 1e-6 时那一通道的直射光是
///   120000 lux × 1e-6 × 2.5431e-5 / π ≈ 9.7e-7 render units，
/// 比 1e-3 的绝对可见门限低 1000 倍。也就是说下限接管的那一段里，
/// 被夹住的那个通道对画面的贡献本来就在看不见的量级 —— 夹它不会造成可见误差。
/// 同时它把比值的上界锁在 1e6，杜绝 inf/NaN 顺着 Light.color 扩散到整个 BRDF。
#define VISTA_T_REF_FLOOR 1e-6

/// 比值的上界。
///
/// 为什么需要它，以及为什么它一定看不见：mainLight.color 是 half3，
/// half 的上限是 65504。上面那个下限允许比值最大到 1/1e-6 = 1e6 —— 溢出成 inf，
/// 再乘上此时 ≈ 0 的 Light.color 就是 **NaN**，而 NaN 会顺着 BRDF 污染整个像素。
/// half 只在移动端/REAL_IS_HALF 下才真是 16 位，所以这条在 PC 上量不出来，
/// 只会在移动端表现为「日落瞬间零星黑点」—— 正是最难归因的一类。
///
/// 它只在 T_ref 被下限接管（即 T_ref < 1e-6）时才生效。而实测的 T_ref 要么
/// ≥ 5e-5（地平线切线处的蓝通道，最小的一档），要么**恰好是 0**（太阳落到参考
/// 高度地平线以下，CPU 侧直接返回 0）。也就是说这条上界只在 T_ref == 0 那一侧生效，
/// 而那一侧 Light.color 也是 0 —— 上界乘的是零，改多少都看不见。
/// 取 1e4 而不是贴着 65504：留两个数量级给后面 BRDF 里的乘法，别把爆点推到别处。
#define VISTA_T_RATIO_MAX 1e4

/// 着色点相对参考高度的太阳透射率比值，逐通道。
/// 未启用逐像素修正时返回 1（不是返回 T —— 那样会乘两遍）。
///
/// 只有这一份实现：positionWS -> (r, muSun) 这一步一旦有第二份，
/// 两份的星球中心/单位换算迟早会分叉，症状是「光色随海拔变化的曲线对不上大气」。
float3 VistaSunTransmittanceRatio(float3 positionWS)
{
    // 灯里没有 T_ref（美术手填的裸颜色）时，除以任何东西都是错的
    if (_VistaSunTransmittanceRef.w < 0.5)
        return 1.0;

    float3 posKm = VistaWorldToAtmosphere(positionWS);
    // 星球中心处 up 无定义。阈值与 CPU 侧 VistaSunTransmittance.Evaluate 的
    // viewHeightKm 守卫同口径（1e-4 km = 0.1 m）
    float  r     = max(length(posKm), 1e-4);
    float3 up    = posKm / r;
    float3 sunDir = _VistaSunDirection.xyz;

    // r < 底半径（地面以下的洞、或 groundLevelWorldY 填高了）不必在这里钳 ——
    // VistaRMuToTransmittanceLutUv 里 rho 取了 max(0)、xR/xMu 都过了 saturate，
    // 已经退化成"地面那一行"。再钳一次只是把同一条判断写两遍。
    float muSun = dot(up, sunDir);

    // EarthShadow 在这里的作用**不是**物理遮挡，是堵 LUT 的垃圾值：
    // muSun < 0 且视线穿地时，Transmittance LUT 的参数化给出的是"穿过地心"的
    // 无意义结果（见 VistaSampleTransmittanceToSun 的警告）。
    //
    // 会不会因为它跟 CPU 侧的 muSun <= 0 不同口径而漏一段？会，但窗口极窄：
    // 两者的差异只来自"着色点的 up 与参考点的 up 不同"，而在星球尺度上
    // 水平几公里只让 up 转 ~1e-4 rad。落在那条缝里的像素本来就在 CPU 侧
    // 判定"太阳已落"的前后一瞬，不构成一个新的可见现象。
    float3 tPx = VistaSampleTransmittanceToSun(r, muSun) * VistaEarthShadow(posKm, sunDir);

    // 比值可以 > 1 —— 山顶比参考高度受光更强，这是增益而不是衰减。
    // 不钳到 1：钳了就等于宣布"参考高度是全场最亮处"，那不是物理。
    // 上界只为堵 half 溢出，理由见 VISTA_T_RATIO_MAX。
    return min(tPx / max(_VistaSunTransmittanceRef.xyz, VISTA_T_REF_FLOOR),
               VISTA_T_RATIO_MAX);
}

#endif // VISTA_SUN_TRANSMITTANCE_INCLUDED
