#ifndef VISTA_TERRAIN_LIT_PASSES_INCLUDED
#define VISTA_TERRAIN_LIT_PASSES_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"
#include "Packages/com.kkiej.vista/Shaders/Terrain/VistaTerrainSplat.hlsl"

// ============================================================================
//  与 URP TerrainLitPasses.hlsl 的结构差异，以及为什么
//
//  ① **没有切线插值器。** URP 在顶点里输出 tangent/bitangent，片元把混好的
//     tangent-space 法线转回世界。本实现的法线从一开始就是世界空间的
//     （见 VistaTerrainSplat.hlsl 的说明：三平面投影有三套切空间，
//     折进一套再折出去是绕路）。省掉两个 half4 插值器。
//
//  ② **没有顶点 splat UV。** 层 UV 在片元里由 positionWS.xz / tileSize 现算，
//     四块地形因此共享同一个平铺相位，拼缝处不错格。省掉两个 float4 插值器。
//     代价：片元里多两次除法，换四个插值器，在地形这种高覆盖率几何上是赚的。
//
//  ③ **没有 GBuffer / Meta / SceneSelection / basemap pass。**
//     工程跑 Forward+（不走延迟）、用 PRTGI 而不烘 lightmap（不需要 Meta）、
//     basemapDistance 1000 m > 地形尺寸（basemap 永不生效）。
//     少的是 SceneSelectionPass —— 代价是编辑器里选中地形没有描边高亮。
//     这是已知缺口，写在这里而不是默默省掉。
// ============================================================================

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 texcoord   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 uvMainAndLM              : TEXCOORD0; // xy: control uv, zw: lightmap uv
    half3  normalWS                 : TEXCOORD1;
    half3  vertexSH                 : TEXCOORD2;

    #ifdef _ADDITIONAL_LIGHTS_VERTEX
        half4 fogFactorAndVertexLight : TEXCOORD3;
    #else
        half  fogFactor               : TEXCOORD3;
    #endif

    float3 positionWS               : TEXCOORD4;

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
        float4 shadowCoord          : TEXCOORD5;
    #endif
    #if defined(DYNAMICLIGHTMAP_ON)
        float2 dynamicLightmapUV    : TEXCOORD6;
    #endif
    #ifdef USE_APV_PROBE_OCCLUSION
        float4 probeOcclusion       : TEXCOORD7;
    #endif

    float4 clipPos                  : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

// ---------------------------------------------------------------- 几何法线
// 实例化 + per-pixel normal 时几何法线来自地形法线图；否则来自插值顶点法线。
half3 VistaTerrainGeometricNormal(Varyings IN)
{
#if defined(ENABLE_TERRAIN_PERPIXEL_NORMAL)
    float2 sampleCoords = (IN.uvMainAndLM.xy / _TerrainHeightmapRecipSize.zw + 0.5f) * _TerrainHeightmapRecipSize.xy;
    half3 n = SAMPLE_TEXTURE2D(_TerrainNormalmapTexture, sampler_TerrainNormalmapTexture, sampleCoords).rgb * 2 - 1;
    return NormalizeNormalPerPixel(TransformObjectToWorldNormal(normalize(n)));
#else
    return NormalizeNormalPerPixel(IN.normalWS);
#endif
}

void InitializeInputData(Varyings IN, half3 normalWS, out InputData inputData)
{
    inputData = (InputData)0;

    inputData.positionWS = IN.positionWS;
    inputData.positionCS = IN.clipPos;
    inputData.normalWS = normalWS;
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
        inputData.shadowCoord = IN.shadowCoord;
    #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
        inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
    #else
        inputData.shadowCoord = float4(0, 0, 0, 0);
    #endif

    #ifdef _ADDITIONAL_LIGHTS_VERTEX
        inputData.fogCoord = InitializeInputDataFog(float4(IN.positionWS, 1.0), IN.fogFactorAndVertexLight.x);
        inputData.vertexLighting = IN.fogFactorAndVertexLight.yzw;
    #else
        inputData.fogCoord = InitializeInputDataFog(float4(IN.positionWS, 1.0), IN.fogFactor);
    #endif

    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.clipPos);

    #if defined(DEBUG_DISPLAY)
        #if defined(DYNAMICLIGHTMAP_ON)
            inputData.dynamicLightmapUV = IN.dynamicLightmapUV;
        #endif
        #if defined(LIGHTMAP_ON)
            inputData.staticLightmapUV = IN.uvMainAndLM.zw;
        #else
            inputData.vertexSH = IN.vertexSH;
        #endif
        #if defined(USE_APV_PROBE_OCCLUSION)
            inputData.probeOcclusion = IN.probeOcclusion;
        #endif
    #endif
}

void InitializeBakedGIData(Varyings IN, inout InputData inputData)
{
    half3 SH = IN.vertexSH;

#if defined(_SCREEN_SPACE_IRRADIANCE)
    inputData.bakedGI = SAMPLE_GI(_ScreenSpaceIrradiance, inputData.positionCS.xy);
#elif defined(DYNAMICLIGHTMAP_ON)
    inputData.bakedGI = SAMPLE_GI(IN.uvMainAndLM.zw, IN.dynamicLightmapUV, SH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(IN.uvMainAndLM.zw);
#elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
    inputData.bakedGI = SAMPLE_GI(SH,
        GetAbsolutePositionWS(inputData.positionWS),
        inputData.normalWS,
        inputData.viewDirectionWS,
        inputData.positionCS.xy,
        IN.probeOcclusion,
        inputData.shadowMask);
#else
    inputData.bakedGI = SAMPLE_GI(IN.uvMainAndLM.zw, SH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(IN.uvMainAndLM.zw);
#endif
}

// ============================================================================
//  顶点
// ============================================================================
Varyings VistaTerrainVert(Attributes v)
{
    Varyings o = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    TerrainInstancing(v.positionOS, v.normalOS, v.texcoord);

    VertexPositionInputs vpi = GetVertexPositionInputs(v.positionOS.xyz);

    o.uvMainAndLM.xy = v.texcoord;
    o.uvMainAndLM.zw = v.texcoord * unity_LightmapST.xy + unity_LightmapST.zw;

#if defined(DYNAMICLIGHTMAP_ON)
    o.dynamicLightmapUV = v.texcoord * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
#endif

    o.normalWS = TransformObjectToWorldNormal(v.normalOS);
    OUTPUT_SH4(vpi.positionWS, o.normalWS.xyz, GetWorldSpaceNormalizeViewDir(vpi.positionWS), o.vertexSH, o.probeOcclusion);

    half fogFactor = 0;
#if !defined(_FOG_FRAGMENT)
    fogFactor = ComputeFogFactor(vpi.positionCS.z);
#endif

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    o.fogFactorAndVertexLight.x = fogFactor;
    o.fogFactorAndVertexLight.yzw = VertexLighting(vpi.positionWS, o.normalWS.xyz);
#else
    o.fogFactor = fogFactor;
#endif

    o.positionWS = vpi.positionWS;
    o.clipPos = vpi.positionCS;

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    o.shadowCoord = GetShadowCoord(vpi);
#endif

    return o;
}

// ============================================================================
//  混层：四项技术在这里汇合
// ============================================================================
struct VistaTerrainSurface
{
    half3 albedo;
    half3 normalWS;
    half  metallic;
    half  smoothness;
    half  occlusion;
    half  alpha;
#if defined(_VISTA_TERRAIN_DEBUG)
    half3 debug;
#endif
};

VistaTerrainSurface VistaBuildTerrainSurface(Varyings IN, half3 geomNormalWS)
{
    VistaTerrainSurface s = (VistaTerrainSurface)0;

    // ---- splat control。半像素修正是必须的：控制图是逐 texel 的权重，
    //      不修正会在每个 texel 边界上多出半格的线性渗色。
    float2 splatUV = (IN.uvMainAndLM.xy * (_Control_TexelSize.zw - 1.0f) + 0.5f) * _Control_TexelSize.xy;
    half4 control = SAMPLE_TEXTURE2D(_Control, sampler_Control, splatUV);

    // ---- ② 陡坡 triplanar：先算权重，平地上 triW = 0，后面所有采样退化成一次。
    half3 planeW = half3(0, 1, 0);
    half  triW = 0;
#if defined(_VISTA_TRIPLANAR)
    triW = VistaSlopeWeight(geomNormalWS, _VistaTriplanarParams.x, _VistaTriplanarParams.y);
    planeW = VistaTriplanarWeights(geomNormalWS, _VistaTriplanarParams.z);
#endif

    VistaTriUV uv0 = VistaBuildTriUV(IN.positionWS, geomNormalWS, _VistaLayerParams0.xy);
    VistaTriUV uv1 = VistaBuildTriUV(IN.positionWS, geomNormalWS, _VistaLayerParams1.xy);
    VistaTriUV uv2 = VistaBuildTriUV(IN.positionWS, geomNormalWS, _VistaLayerParams2.xy);
    VistaTriUV uv3 = VistaBuildTriUV(IN.positionWS, geomNormalWS, _VistaLayerParams3.xy);

    half4 a0 = VistaSampleTriplanar(TEXTURE2D_ARGS(_Splat0, sampler_Splat0), uv0, planeW, triW);
    half4 a1 = VistaSampleTriplanar(TEXTURE2D_ARGS(_Splat1, sampler_Splat0), uv1, planeW, triW);
    half4 a2 = VistaSampleTriplanar(TEXTURE2D_ARGS(_Splat2, sampler_Splat0), uv2, planeW, triW);
    half4 a3 = VistaSampleTriplanar(TEXTURE2D_ARGS(_Splat3, sampler_Splat0), uv3, planeW, triW);

    a0.rgb *= _DiffuseRemapScale0.rgb;
    a1.rgb *= _DiffuseRemapScale1.rgb;
    a2.rgb *= _DiffuseRemapScale2.rgb;
    a3.rgb *= _DiffuseRemapScale3.rgb;

    // ---- mask map。回落常量不是 0.5 而是逐通道给的：
    //      R 金属（未用，走 _Metallic#）/ G 环境光遮蔽 / B 高度 / A 光滑（未用，走 _Smoothness#）。
    //      AO 那一路必须回落到 **1**：四层里只要有一层带 mask，_MASKMAP 就是开的，
    //      另外三层此时走回落。回落给 0.5 的话它们会把 AO 拉到一半 —— 整片地表
    //      凭空暗下来，而那种暗长得和「GI 没配好」一模一样。
    //      （URP 的 TerrainLit 同此：lerp(half4(0, 1, 0, 0.5), mask, _LayerHasMask#)。）
    //      B 给 0.5 是为了不让 height 回落到一个极端值；实际上 _LayerHasMask# 为 0 时
    //      VistaLayerHeight 的 lerp 会把 mask.b 整个丢掉，所以这一路取值不进画面。
    half4 mFallback = half4(0.0h, 1.0h, 0.5h, 0.5h);
    half4 m0 = mFallback, m1 = mFallback, m2 = mFallback, m3 = mFallback;
#if defined(_MASKMAP)
    m0 = lerp(m0, VistaSampleTriplanar(TEXTURE2D_ARGS(_Mask0, sampler_Mask0), uv0, planeW, triW), _LayerHasMask0);
    m1 = lerp(m1, VistaSampleTriplanar(TEXTURE2D_ARGS(_Mask1, sampler_Mask0), uv1, planeW, triW), _LayerHasMask1);
    m2 = lerp(m2, VistaSampleTriplanar(TEXTURE2D_ARGS(_Mask2, sampler_Mask0), uv2, planeW, triW), _LayerHasMask2);
    m3 = lerp(m3, VistaSampleTriplanar(TEXTURE2D_ARGS(_Mask3, sampler_Mask0), uv3, planeW, triW), _LayerHasMask3);
    m0 = m0 * _MaskMapRemapScale0 + _MaskMapRemapOffset0;
    m1 = m1 * _MaskMapRemapScale1 + _MaskMapRemapOffset1;
    m2 = m2 * _MaskMapRemapScale2 + _MaskMapRemapOffset2;
    m3 = m3 * _MaskMapRemapScale3 + _MaskMapRemapOffset3;
#endif

    // ---- ① Height-blend。高度来源见 VistaTerrainSplat.hlsl 的长注释。
#if defined(_VISTA_HEIGHT_BLEND)
    half4 heights = half4(
        VistaLayerHeight(a0.rgb, m0.b, _LayerHasMask0, _VistaLayerParams0.z, _VistaLayerParams0.w),
        VistaLayerHeight(a1.rgb, m1.b, _LayerHasMask1, _VistaLayerParams1.z, _VistaLayerParams1.w),
        VistaLayerHeight(a2.rgb, m2.b, _LayerHasMask2, _VistaLayerParams2.z, _VistaLayerParams2.w),
        VistaLayerHeight(a3.rgb, m3.b, _LayerHasMask3, _VistaLayerParams3.z, _VistaLayerParams3.w));
    half4 w = VistaHeightBlend(control, heights, _VistaHeightBlendParams.x);
#else
    half4 w = control / max(dot(control, half4(1, 1, 1, 1)), 1e-5h);
#endif

    // ---- ⑥ 逐层色调拉伸。
    //      位置必须在 heights 之后：高度代理的中心/对比度
    //      （_VistaLayerParams#.z/w）是在**原始**贴图的亮度分位数上标定的，
    //      先拉伸再算代理等于把同一个对比度乘两遍 —— 混层边界会随色调参数漂移，
    //      而那两件事（混层位置、单层明暗）必须解耦，否则调色会悄悄改地貌。
#if defined(_VISTA_LAYER_TONE)
    a0.rgb = VistaLayerTone(a0.rgb, _VistaLayerParams0.z, _VistaLayerContrast.r, _VistaLayerSaturate.r);
    a1.rgb = VistaLayerTone(a1.rgb, _VistaLayerParams1.z, _VistaLayerContrast.g, _VistaLayerSaturate.g);
    a2.rgb = VistaLayerTone(a2.rgb, _VistaLayerParams2.z, _VistaLayerContrast.b, _VistaLayerSaturate.b);
    a3.rgb = VistaLayerTone(a3.rgb, _VistaLayerParams3.z, _VistaLayerContrast.a, _VistaLayerSaturate.a);
#endif

    // ---- 合成 albedo / smoothness / metallic / occlusion
    s.albedo = a0.rgb * w.r + a1.rgb * w.g + a2.rgb * w.b + a3.rgb * w.a;

    half4 layerSmooth = half4(a0.a, a1.a, a2.a, a3.a)
                      * half4(_Smoothness0, _Smoothness1, _Smoothness2, _Smoothness3);
    s.smoothness = dot(layerSmooth, w);
    s.metallic   = dot(half4(_Metallic0, _Metallic1, _Metallic2, _Metallic3), w);
    s.occlusion  = dot(half4(m0.g, m1.g, m2.g, m3.g), w);
    // 这里不再额外写一句 `#if !defined(_MASKMAP) s.occlusion = 1`：回落常量的
    // G 已经是 1，关掉 _MASKMAP 时四层都走回落，dot 的结果恒等于 sum(w) = 1。
    // 同一个量不留两份实现。
    s.alpha = 1.0h;

    // ---- 层法线（世界空间合成）
    half3 n = geomNormalWS;
#if defined(_NORMALMAP)
    half3 n0 = VistaSampleTriplanarNormalWS(TEXTURE2D_ARGS(_Normal0, sampler_Normal0), uv0, planeW, triW, _NormalScale0, geomNormalWS);
    half3 n1 = VistaSampleTriplanarNormalWS(TEXTURE2D_ARGS(_Normal1, sampler_Normal0), uv1, planeW, triW, _NormalScale1, geomNormalWS);
    half3 n2 = VistaSampleTriplanarNormalWS(TEXTURE2D_ARGS(_Normal2, sampler_Normal0), uv2, planeW, triW, _NormalScale2, geomNormalWS);
    half3 n3 = VistaSampleTriplanarNormalWS(TEXTURE2D_ARGS(_Normal3, sampler_Normal0), uv3, planeW, triW, _NormalScale3, geomNormalWS);
    n = n0 * w.r + n1 * w.g + n2 * w.b + n3 * w.a;
    // 增益作用在**切向增量**上，而不是在 n 上做 lerp —— lerp 到 geomNormalWS
    // 会在权重 < 1 时把后面 ⑤/③ 的增量也一起缩掉（它们都是加在 n 上的）。
    // 这里只缩层法线自己贡献的那一份。
    n = geomNormalWS + (n - geomNormalWS) * _VistaLayerNormalWeight;
#endif

#if defined(_VISTA_TERRAIN_DEBUG)
    half3 layerNormalOnly = n;
    half3 reliefOnly = geomNormalWS;
#endif

    // ---- ⑤ 中频起伏。无距离淡出 —— 它的特征尺寸（40 m / 9 m）在整个可视
    //      距离内都远大于一个像素，而它正是远景唯一还活着的法线来源：
    //      层法线图的切向振幅实测 0.008~0.073，且 mip 到 16×16 还要腰斩，
    //      其中两张已经落到 8-bit 量化步长（0.0078）上 —— 放大它们只放大条带。
#if defined(_VISTA_RELIEF) || defined(_VISTA_DETAIL_NORMAL)
    // 到相机的距离。⑤ 的第三档与 ③ 的两档都要用它，所以算一次。
    half dist = (half)length(GetCameraPositionWS() - IN.positionWS);
#endif

#if defined(_VISTA_RELIEF)
    // 第三档的淡出在这里乘进斜率，而不是在函数内部算距离：函数拿到的是
    // 「这一档还有多少强度」，于是它不需要知道相机在哪儿，也就不会在
    // 别的调用点（比如以后的烘焙路径）悄悄引入一个相机依赖。
    half reliefW3 = 1.0h - smoothstep(_VistaReliefOctave3.z, _VistaReliefOctave3.w, dist);
    half3 nr = VistaReliefNormalWS(IN.positionWS, geomNormalWS, _VistaReliefParams,
                                   half2(_VistaReliefOctave3.x, _VistaReliefOctave3.y * reliefW3));
    // 同 detail 一样走「加切向增量」而不是 lerp：lerp 会连带削弱层法线。
    n = n + (nr - geomNormalWS);
    #if defined(_VISTA_TERRAIN_DEBUG)
    reliefOnly = nr;
    #endif
#endif

    // ---- ③ Detail normal 距离淡出（两档）
#if defined(_VISTA_DETAIL_NORMAL)
    half detailW = 1.0h - smoothstep(_VistaDetailParams.z, _VistaDetailParams.w, dist);
    if (detailW > 0.002h)
    {
        VistaTriUV uvd = VistaBuildTriUV(IN.positionWS, geomNormalWS, _VistaDetailParams.xx);
        half3 nd = VistaSampleTriplanarNormalWS(TEXTURE2D_ARGS(_VistaDetailNormal, sampler_VistaDetailNormal),
                                                uvd, planeW, triW,
                                                _VistaDetailParams.y * detailW, geomNormalWS);
        // 用「把 detail 的切向分量加到已有法线上」而不是 lerp：lerp 会在
        // detailW < 1 时**同时削弱层法线**，远处地表会越来越平 —— 那不是
        // 「细节淡出」，是「法线整体淡出」。这里只淡掉 detail 自己的贡献。
        n = n + (nd - geomNormalWS);
    }

    // 第二档：同一张贴图、tile 放大 4 倍、强度约一半、淡出推到更远。
    // 为什么不是「把第一档的淡出拉长」：那会让 1.5 m 的特征在 150 m 处只占
    // 0.3 像素 —— 拉长淡出等于把 shimmer 一路带到远景。两档各自待在自己的
    // Nyquist 安全区内，交接处（40~45 m）两档同时有效，接缝因此不可见。
    // 移动档不叠第二档：多 3 次采样换一段中距离细节，在带宽受限的平台上不划算。
    #if !defined(_VISTA_TERRAIN_MOBILE)
    half detailW2 = 1.0h - smoothstep(_VistaDetailOctave2.z, _VistaDetailOctave2.w, dist);
    if (detailW2 > 0.002h)
    {
        VistaTriUV uvd2 = VistaBuildTriUV(IN.positionWS, geomNormalWS, _VistaDetailOctave2.xx);
        half3 nd2 = VistaSampleTriplanarNormalWS(TEXTURE2D_ARGS(_VistaDetailNormal, sampler_VistaDetailNormal),
                                                 uvd2, planeW, triW,
                                                 _VistaDetailOctave2.y * detailW2, geomNormalWS);
        n = n + (nd2 - geomNormalWS);
    }
    #endif
#endif

    n = normalize(n);
    s.normalWS = n;

    // ---- ④ Macro variation
#if defined(_VISTA_MACRO_VARIATION)
    half3 macro = VistaMacroVariation(IN.positionWS, _VistaMacroParams, _VistaMacroParams.w);
    s.albedo = VistaApplyMacro(s.albedo, macro);
#endif

    s.albedo *= _BaseColor.rgb;

#if defined(_VISTA_TERRAIN_DEBUG)
    // 这几路专门回答「shader 收到了什么」。第 3 / 5 / 6 档读的是引擎逐帧写入的量，
    // 它们在 materialTemplate 资产上全是 0 / <null>，只有在这里才拿得到真值。
    int dbg = (int)round(_VistaDebugView);
    if (dbg == 1)      s.debug = s.normalWS * 0.5h + 0.5h;
    else if (dbg == 2) s.debug = normalize(layerNormalOnly) * 0.5h + 0.5h;
    else if (dbg == 3) s.debug = half3(_NormalScale0, _NormalScale1, _NormalScale2);
    else if (dbg == 4) s.debug = w.rgb;
    else if (dbg == 5) s.debug = SAMPLE_TEXTURE2D(_Normal0, sampler_Normal0, uv0.xz).rgb;
    else if (dbg == 6) s.debug = half3(_LayerHasMask0, _LayerHasMask1, _LayerHasMask2);
    else if (dbg == 7) s.debug = reliefOnly * 0.5h + 0.5h;
    // 第 8 档回答的是前面几档回答不了的那个量：层法线**相对几何法线的切向偏移**。
    // 第 1/2 档看的是合成后的法线，而一个 0.02 的偏移在 ±1 的编码里只占 1%，
    // 与 0 在 8-bit 上无法区分 —— 「层法线没进画面」和「进了但很弱」会给出
    // 同一张图。×20 之后 0.025 的偏移就是半格灰阶，两种解释才分得开。
    else if (dbg == 8) s.debug = (layerNormalOnly - geomNormalWS) * 20.0h + 0.5h;
    else               s.debug = half3(0, 0, 0);
#endif

    return s;
}

// ============================================================================
//  ForwardLit
// ============================================================================
void VistaTerrainFragment(
    Varyings IN
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
#ifdef _ALPHATEST_ON
    ClipHoles(IN.uvMainAndLM.xy);
#endif

    half3 geomNormalWS = VistaTerrainGeometricNormal(IN);
    VistaTerrainSurface s = VistaBuildTerrainSurface(IN, geomNormalWS);

#if defined(_VISTA_TERRAIN_DEBUG)
    // 直接输出、不过光照：这几张图是要被**逐像素比数**的，过一遍 PBR 会
    // 把「法线差了多少」和「这个法线在当前光照下差多少」混成一个数。
    // 注意后处理的 tonemap 仍会作用在它上面 —— 所以只拿它做等值/非零判断，
    // 不拿它当标定过的数值读数。
    outColor = half4(s.debug, 1.0h);
    #ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = float4(EncodeMeshRenderingLayer(GetMeshRenderingLayer()), 0, 0, 0);
    #endif
    return;
#endif

    InputData inputData;
    InitializeInputData(IN, s.normalWS, inputData);

    SurfaceData surfaceData;
    surfaceData = (SurfaceData)0;
    surfaceData.albedo = s.albedo;
    surfaceData.metallic = s.metallic;
    surfaceData.specular = half3(0, 0, 0);
    surfaceData.smoothness = s.smoothness;
    surfaceData.occlusion = s.occlusion;
    surfaceData.emission = half3(0, 0, 0);
    surfaceData.alpha = s.alpha;
    surfaceData.normalTS = half3(0, 0, 1);   // 未使用：法线已是世界空间

#if defined(_DBUFFER)
    ApplyDecalToSurfaceData(IN.clipPos, surfaceData, inputData);
    inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
#endif

    InitializeBakedGIData(IN, inputData);

    half4 color = UniversalFragmentPBR(inputData, surfaceData);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    color.a = 1.0h;

    outColor = color;

#ifdef _WRITE_RENDERING_LAYERS
    uint renderingLayers = GetMeshRenderingLayer();
    outRenderingLayers = float4(EncodeMeshRenderingLayer(renderingLayers), 0, 0, 0);
#endif
}

// ============================================================================
//  ShadowCaster
// ============================================================================
float3 _LightDirection;
float3 _LightPosition;

float4 VistaTerrainShadowPosition(float3 positionWS, float3 normalWS)
{
#if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif
    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
#if UNITY_REVERSED_Z
    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif
    return positionCS;
}

struct VistaTerrainShadowVaryings
{
    float2 uv        : TEXCOORD0;
    float4 clipPos   : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

VistaTerrainShadowVaryings VistaTerrainShadowVert(Attributes v)
{
    VistaTerrainShadowVaryings o = (VistaTerrainShadowVaryings)0;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    TerrainInstancing(v.positionOS, v.normalOS, v.texcoord);

    o.uv = v.texcoord;
    float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
    float3 normalWS = TransformObjectToWorldNormal(v.normalOS);
    o.clipPos = VistaTerrainShadowPosition(positionWS, normalWS);
    return o;
}

half4 VistaTerrainShadowFragment(VistaTerrainShadowVaryings IN) : SV_TARGET
{
#ifdef _ALPHATEST_ON
    ClipHoles(IN.uv);
#endif
    return 0;
}

// ============================================================================
//  DepthOnly
// ============================================================================
VistaTerrainShadowVaryings VistaTerrainDepthVert(Attributes v)
{
    VistaTerrainShadowVaryings o = (VistaTerrainShadowVaryings)0;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    TerrainInstancing(v.positionOS, v.normalOS, v.texcoord);

    o.uv = v.texcoord;
    o.clipPos = TransformObjectToHClip(v.positionOS.xyz);
    return o;
}

half4 VistaTerrainDepthFragment(VistaTerrainShadowVaryings IN) : SV_TARGET
{
#ifdef _ALPHATEST_ON
    ClipHoles(IN.uv);
#endif
    return 0;
}

// ============================================================================
//  DepthNormals
//
//  这个 pass 必须把**混完的**法线写出去，不能只写几何法线：SSAO 与 decal
//  都读它。写几何法线的话，地形上的 decal 会贴得像贴在一块光板上，
//  而崖面的 triplanar 法线在 SSAO 里完全不参与 —— 那正是最需要它的地方。
//
//  写的是**未编码**的世界法线。Forward+ 下没有 GBuffer，`_GBUFFER_NORMALS_OCT`
//  不会被置位，消费方按原始法线读；写一条 oct 编码分支只会多一倍变体。
// ============================================================================
void VistaTerrainDepthNormalsFragment(
    Varyings IN
    , out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
#ifdef _ALPHATEST_ON
    ClipHoles(IN.uvMainAndLM.xy);
#endif

    half3 geomNormalWS = VistaTerrainGeometricNormal(IN);
    VistaTerrainSurface s = VistaBuildTerrainSurface(IN, geomNormalWS);

    outNormalWS = half4(s.normalWS, 0.0);

#ifdef _WRITE_RENDERING_LAYERS
    uint renderingLayers = GetMeshRenderingLayer();
    outRenderingLayers = float4(EncodeMeshRenderingLayer(renderingLayers), 0, 0, 0);
#endif
}

#endif // VISTA_TERRAIN_LIT_PASSES_INCLUDED
