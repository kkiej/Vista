#ifndef VISTA_LIGHTING_INCLUDED
#define VISTA_LIGHTING_INCLUDED

// ============================================================================
//  Vista 的前向着色累加
//
//  这个文件里 VistaComputeLighting 的函数体是 URP
//  ShaderLibrary/Lighting.hlsl 里 UniversalFragmentPBR(InputData, SurfaceData)
//  的**逐行拷贝**（对照版本：com.unity.render-pipelines.universal@13e5115b98bf，
//  Lighting.hlsl:282-373）。拷贝在本项目里是要给理由的，这里的理由是：
//
//  UniversalFragmentPBR 内部已经把光照拆成了 giColor / mainLightColor /
//  additionalLightsColor / vertexLightingColor 五项（URP 自己的 LightingData
//  结构），但它**不把这个结构返回出来** —— 返回的是已经加完、乘完 albedo 的
//  一个 half4。而 #12 的逐像素大气透射率只能乘在**直接光**上：
//  太阳到着色点这一路的透射率 T 影响的是太阳的辐照度，不影响天光/GI/探针。
//  乘在合成后的颜色上等于把 GI 也一起衰减，症状是背光面随太阳高度一起变暗，
//  而物理上那部分光根本没走太阳那条光路。
//
//  所以要么拿到那个拆分，要么放弃 #12。URP 没有给出拿到它的接口，
//  于是只剩「照抄一遍函数体，把 LightingData 交出来」这一条路。
//
//  ------------------------------------------------------------------ 拷贝的边界
//  拷贝**只到累加为止**。末尾的合成（加起来、乘 albedo、加自发光、
//  REAL_IS_HALF 的 HALF_MAX 截断）仍然调 URP 自己的 CalculateFinalColor ——
//  那一段没有拆分需求，抄过来只会多一份会走歧的算术。
//  也就是说本文件里没有任何一行是「URP 已经有、我又写了一遍」的**算术**，
//  拷贝的全部是控制流（哪些分支在什么宏下成立、以什么顺序调）。
//
//  ------------------------------------------------------------------ 怎么发现拷贝走歧
//  URP 升级后这个函数体可能变（新的光照特性、新的宏）。人眼 diff 不可靠，
//  所以 VistaLit.shader 带一个 VISTA_LIT_DIFF_DEBUG 变体：
//  同一次片元调用里同时算「我的累加」与 UniversalFragmentPBR，输出两者之差，
//  由 Editor 自检读回并给出最大偏差。走歧会立刻表现为那个数字变大。
//  这是「拷贝」这个决定唯一可接受的前提 —— 没有那条自检就不该抄。
//
//  ------------------------------------------------------------------ AP 挂在哪
//  VistaApplyApTail 是变体 B 的落点：URP 的 LitForwardPass 在这个位置调 MixFog，
//  Vista 换成 AP 合成。两者不能同时上，理由见
//  Shaders/Vista/VistaLitForwardPass.hlsl 的文件头。
// ============================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.kkiej.vista/ShaderLibrary/AerialPerspectiveComposite.hlsl"
#include "Packages/com.kkiej.vista/ShaderLibrary/SunTransmittance.hlsl"

// ----------------------------------------------------------------------------
//  累加
// ----------------------------------------------------------------------------

// 算出拆分好的光照，**不**合成。
//
// 返回 true 表示 URP 的调试视图已经接管了输出，调用方应当直接写 debugColor
// 而不再看 lightingData —— 这一条是照抄 UniversalFragmentPBR 里
// CanDebugOverrideOutputColor 那个提前 return 的语义。
// 用返回值而不是「让 debugColor 参与合成」，是因为调试视图要的是**替换**输出，
// 混进合成会让 Rendering Debugger 里的每种视图都叠上一层材质颜色。
//
// surfaceData 收 inout：URP 那个函数的签名是按值收，且注释写着
// "NOTE: can modify surfaceData"。实测 17.4 的 InitializeBRDFData 其实没改它
// （alpha 一路 inout 但只读，其余都是按值传下去的），所以两种收法今天等价。
// 取 inout 是为了**将来它真改了的时候仍然等价**：URP 内部改完后
// CalculateFinalColor 用的是改过的 alpha，按值收的话我的调用点会拿到没改的那份，
// 差异只出现在开了 _ALPHAPREMULTIPLY_ON 的透明材质上 —— 那正是最不容易注意到的地方。
bool VistaComputeLighting(InputData inputData, inout SurfaceData surfaceData,
                          out LightingData lightingData, out half4 debugColor)
{
    // out 参数必须在所有返回路径上都写过，先给个确定值。
    lightingData = (LightingData)0;
    debugColor = half4(0, 0, 0, 1);

    #if defined(_SPECULARHIGHLIGHTS_OFF)
    bool specularHighlightsOff = true;
    #else
    bool specularHighlightsOff = false;
    #endif
    BRDFData brdfData;

    // NOTE: can modify "surfaceData"...
    InitializeBRDFData(surfaceData, brdfData);

    #if defined(DEBUG_DISPLAY)
    if (CanDebugOverrideOutputColor(inputData, surfaceData, brdfData, debugColor))
    {
        return true;
    }
    #endif

    // Clear-coat calculation...
    BRDFData brdfDataClearCoat = CreateClearCoatBRDFData(surfaceData, brdfData);
    half4 shadowMask = CalculateShadowMask(inputData);
    AmbientOcclusionFactor aoFactor = CreateAmbientOcclusionFactor(inputData, surfaceData);
    uint meshRenderingLayers = GetMeshRenderingLayer();
    Light mainLight = GetMainLight(inputData, shadowMask, aoFactor);

    // NOTE: We don't apply AO to the GI here because it's done in the lighting calculation below...
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI);

    // ==================== #12 逐像素太阳透射率 ====================
    // 位置是**在 MixRealtimeAndBakedGI 之后**，这不是随手排的，理由如下。
    //
    // 那个函数唯一做的事是 subtractive 混合（URP GlobalIllumination.hlsl:592，
    // 只有一句 SubtractDirectMainLightFromLightmap；light 声明成 inout 但从头到尾
    // 没被写过，所以顺序对别的东西没有任何影响）。它的做法是先估一遍
    // 「烘焙器往 lightmap 里放了多少直接光」，再从 lightmap 里把被实时阴影挡住的
    // 那一份减掉。
    //
    // 关键在于 lightmap 是一张**已经烘死的贴图**：里面装的是烘焙时那个光色的能量，
    // 而烘焙器并不知道逐像素透射率这回事。要减的东西只能是**贴图里真有的那一份**。
    // 若把修正后的 mainLight.color 交给它，它会去减一个贴图里并不存在的量，
    // 误差随场景的海拔跨度放大：高处 T_px > T_ref ⇒ 过减，阴影区被
    // _SubtractiveShadowColor 夹住变死黑；低洼处 T_px < T_ref ⇒ 欠减，阴影不够暗。
    // 两种症状都只在「烘了 lightmap + Subtractive 混合 + 有落差」时出现，
    // 单看直射光完全正常 —— 典型的难查失效。
    //
    // 反过来说：放在之后，估算用的是与烘焙同源的那个光色，减法的口径是自洽的；
    // 而实时直射光拿到修正后的值。两边各自正确。
    mainLight.color *= VistaSunTransmittanceRatio(inputData.positionWS);
    // ==============================================================

    lightingData = CreateLightingData(inputData, surfaceData);

    lightingData.giColor = GlobalIllumination(brdfData, brdfDataClearCoat, surfaceData.clearCoatMask,
                                              inputData.bakedGI, aoFactor.indirectAmbientOcclusion, inputData.positionWS,
                                              inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);
#ifdef _LIGHT_LAYERS
    if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
#endif
    {
        lightingData.mainLightColor = LightingPhysicallyBased(brdfData, brdfDataClearCoat,
                                                              mainLight,
                                                              inputData.normalWS, inputData.viewDirectionWS,
                                                              surfaceData.clearCoatMask, specularHighlightsOff);
    }

    #if defined(_ADDITIONAL_LIGHTS)
    uint pixelLightCount = GetAdditionalLightsCount();

    #if USE_CLUSTER_LIGHT_LOOP
    [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
    {
        CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK

        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);

#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
        {
            lightingData.additionalLightsColor += LightingPhysicallyBased(brdfData, brdfDataClearCoat, light,
                                                                          inputData.normalWS, inputData.viewDirectionWS,
                                                                          surfaceData.clearCoatMask, specularHighlightsOff);
        }
    }
    #endif

    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);

#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
        {
            lightingData.additionalLightsColor += LightingPhysicallyBased(brdfData, brdfDataClearCoat, light,
                                                                          inputData.normalWS, inputData.viewDirectionWS,
                                                                          surfaceData.clearCoatMask, specularHighlightsOff);
        }
    LIGHT_LOOP_END
    #endif

    #if defined(_ADDITIONAL_LIGHTS_VERTEX)
    lightingData.vertexLightingColor += inputData.vertexLighting * brdfData.diffuse;
    #endif

    return false;
}

// ----------------------------------------------------------------------------
//  合成
// ----------------------------------------------------------------------------

// 把拆分的光照合回一个颜色。**这里没有 AP** —— 它必须与 UniversalFragmentPBR
// 的返回值逐位对得上，否则 VISTA_LIT_DIFF_DEBUG 那条自检量到的就不是「累加抄错了」
// 而是「累加抄错了 or 合成端不一样」两件事的和，一个数字担保两件事就没法定位。
half4 VistaResolveLighting(LightingData lightingData, half alpha)
{
#if REAL_IS_HALF
    // Clamp any half.inf+ to HALF_MAX
    return min(CalculateFinalColor(lightingData, alpha), HALF_MAX);
#else
    return CalculateFinalColor(lightingData, alpha);
#endif
}

// 变体 B 的落点：URP 在这个位置调 MixFog。
//
// 透明材质**不合成 AP**，这不是偷懒而是为了让两个变体的口径一致：
// 变体 A 的全屏 pass 挂在 AfterRenderingSkybox，透明物在它之后画，A 覆盖不到；
// 若 B 在这里给透明物上了 AP，「A 与 B 逐像素一致」这条验收标准就会在
// 每一个透明像素上必然失败 —— 而那不是 bug，是两条路的覆盖范围本来就不同。
// 让 B 也跳过，两条路在透明物上同为「无 AP」，标准就重新成立。
// 透明物的雾归 Step 3 的体积雾统一处理（那时两条路会一起改）。
half4 VistaApplyApTail(half4 color, InputData inputData)
{
#if defined(VISTA_AP_DEBUG_DISTANCE)
    // 自检档（#15 判据②a）：输出合成的**操作数** —— 插值出来的 positionWS
    // 折出的径向距离 (km) —— 而不是合成结果。
    //
    // 为什么要分解成操作数：A 与 B 的差异有两个来源，
    //   ① A 从深度图反投影出 positionWS，B 直接插值；
    //   ② 给定同一个距离，两者调的是同一个 VistaApplyAerialPerspective，
    //      但 A 是往一张已经量化成 fp16 的颜色上混合、B 是在寄存器里算完只量化一次。
    // 只比最终颜色，这两项混成一个数，出问题定不了位。这一档只暴露 ①。
    //
    // 与变体 A 的 Pass 2 调的是**同一个** VistaApDistanceKm（都在
    // AerialPerspectiveComposite.hlsl 里，全项目唯一一份），
    // 所以两边量到的差异只可能来自 positionWS 本身。
    //
    // 不受 VistaApInShaderEnabled() 与透明分支影响：这一档要的是这个操作数
    // 在任何配置下都能被读出来，而不是「B 这一帧会不会合成」。
    //
    // 返回类型是 half4（签名与非调试路径共用），但 D3D11 的 DXBC 里 half 就是 float，
    // 所以 km 量级的距离不在这里丢精度 —— 丢不丢取决于目标 RT 的格式，
    // 自检端拿一组**已知距离**去量它的分辨力，不假设。
    return half4(VistaApDistanceKm(inputData.positionWS).xxx, 1.0);
#else
#if !defined(_SURFACE_TYPE_TRANSPARENT)
    // uniform 分支而不是 shader keyword：合成方式要能运行时切，切换不产生变体。
    if (VistaApInShaderEnabled())
        VistaApplyAerialPerspective(color.rgb, inputData.normalizedScreenSpaceUV, inputData.positionWS);
#endif
    return color;
#endif
}

#endif // VISTA_LIGHTING_INCLUDED
