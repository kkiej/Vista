#ifndef VISTA_AERIAL_PERSPECTIVE_COMPOSITE_INCLUDED
#define VISTA_AERIAL_PERSPECTIVE_COMPOSITE_INCLUDED

// ============================================================================
//  Aerial Perspective 合成端
//
//  AerialPerspective.hlsl 负责「表里存了什么、怎么取回来」；
//  这一层负责「取回来的两个量怎么贴到画面上」。分开是因为前者也服务于不做
//  合成的消费者（Step 5 的 SH 投影要的是绝对光度量的散射项，不要合成）。
//
//  ------------------------------------------------------------------ 合成公式
//      final = shaded · transmittance + inScatter · exposure
//
//  两个乘子的单位制不同，这是最容易搞错的一处：
//    shaded       已经是渲染目标单位（曝光已经折进 Light.intensity 里了，见 #8）
//    inScatter    绝对光度量 cd/m²，**没有**乘曝光（见 VistaSampleAerialPerspective）
//  所以只有散射项要补 VISTA_EXPOSURE。反过来给 shaded 再乘一次曝光，
//  症状是画面整体暗掉四个数量级 —— 一眼能看出来；
//  而漏乘散射项则是「远山糊成一片纯白」，也很显眼。两种错误都不会静默，
//  但仍然把它写成一个函数，让全项目只有这一处做这个换算。
//
//  ------------------------------------------------------------------ 两条消费路径共用这里
//    变体 A  全屏合成 pass：从深度反投影出 positionWS，对不透明像素整屏合成一遍。
//    变体 B  Vista 自己的材质在着色末尾直接调用，用手里现成的 positionWS。
//  两者调的是同一个 VistaApplyAerialPerspective，所以「A 与 B 的画面必须逐像素
//  一致」这条验收标准才有意义 —— 若各写一遍合成，A/B 对比就只是在比两份笔误。
//
//  ------------------------------------------------------------------ 为什么距离在大气空间里算
//  径向距离本可以写成 length(positionWS - _WorldSpaceCameraPos)，但那样量的起点是
//  「引擎认为的相机」，而 LUT 的起点是 _VistaViewPosKm（大气 pass 当帧用来积分的
//  那个视点）。两者在正常情况下相等，不相等的场合（多相机、反射探针、
//  自检里手构的视图）恰恰是最需要一致的场合。用 LUT 自己的视点算，
//  距离参数与表的构造按定义对齐。
// ============================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.kkiej.vista/ShaderLibrary/AerialPerspective.hlsl"

// 着色点到视点的径向距离 (km)。全项目唯一一份实现。
float VistaApDistanceKm(float3 positionWS)
{
    return length(VistaWorldToAtmosphere(positionWS) - _VistaViewPosKm.xyz);
}

// 变体 B 的开关：材质是否应当自己合成 AP。
// 每帧无条件写，AP 关掉时为 0 —— 理由见 AtmosphereDef.hlsl 里 _VistaApConsumer 的注释。
bool VistaApInShaderEnabled()
{
    return _VistaApConsumer.x > 0.5;
}

// 原始设备深度是否落在远平面（即这个像素是天空，没有几何）。
//
// 天空**必须**排除在合成之外：Sky-View LUT 存的已经是整条视线的完整积分，
// 再叠一层 AP 等于把同一段大气算两遍，症状是天空整体发灰、且地平线附近
// 出现一条与 AP 最远切片对应的接缝。
#if UNITY_REVERSED_Z
    #define VISTA_AP_IS_SKY_DEPTH(rawDepth) ((rawDepth) <= 0.0)
#else
    #define VISTA_AP_IS_SKY_DEPTH(rawDepth) ((rawDepth) >= 1.0)
#endif

// 取合成用的两个乘子：final = shaded · mulTerm + addTerm。
// 分成两个量返回而不是直接合成，是因为全屏路径的两趟混合各只要其中一个
// （见合成 shader 的注释）。合成本身在下面那个函数里，只有一份。
void VistaGetAerialPerspectiveTerms(float2 screenUv, float3 positionWS,
                                    out float3 addTerm, out float3 mulTerm)
{
    float3 inScatter;
    VistaSampleAerialPerspective(screenUv, VistaApDistanceKm(positionWS),
                                 inScatter, mulTerm);
    addTerm = inScatter * VISTA_EXPOSURE;
}

// 合成。exposedColor 是渲染目标单位的着色结果。
void VistaApplyAerialPerspective(inout float3 exposedColor, float2 screenUv, float3 positionWS)
{
    float3 addTerm, mulTerm;
    VistaGetAerialPerspectiveTerms(screenUv, positionWS, addTerm, mulTerm);
    exposedColor = exposedColor * mulTerm + addTerm;
}

#endif // VISTA_AERIAL_PERSPECTIVE_COMPOSITE_INCLUDED
