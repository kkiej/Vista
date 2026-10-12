// ============================================================================
//  Vista/Terrain/Lit —— Step 6 地形材质系统
//
//  取代 URP 默认的 TerrainLit。四项新增技术：
//   ① Height-blend 混层（高度来自 albedo 亮度去均值代理，或 maskMap.b）
//   ② 陡坡 triplanar
//   ③ Detail normal 距离淡出
//   ④ Macro variation noise
//  实现与取舍见 Shaders/Terrain/VistaTerrainSplat.hlsl 的文件头。
//
//  ------------------------------------------------------------------ 层数上限 4
//  不提供 add-pass。方案文档自己把这条写成了 height-blend splatting 的既有代价
//  （「层数受 splat 通道限制」），而不是实现缺陷：多 pass 会破坏 height-blend
//  的归一化 —— 第二个 pass 看不见第一个 pass 的高度，两边各自归一化后
//  相加必然 > 1。URP 的做法是层数 > 4 时**直接关掉 height-blend**，
//  也就是用「放弃这项技术」换层数。本工程正好 4 层，选择保住技术。
//  `_NumLayersCount > 4` 时用 Vista 的材质面板会给出明确报错，而不是静默降级。
//
//  ------------------------------------------------------------------ Dependency
//  地形引擎通过 `Dependency` 找附属 shader。这里只声明 AddPassShader 与
//  BaseMapShader 两条**回落到 URP 自己那两个 shader**的路：
//   · 万一有人加到第 5 层，画面会是 URP 的线性混合而不是粉色 —— 降级可见、不崩。
//   · basemap 在本工程永不生效（basemapDistance 1000 m > 地形 512 m），
//     声明它只是为了不把「超出距离」这条路留成未定义。
//  没有 BaseMapGenShader：不声明它，引擎就不烘 basemap，省掉一份 RT。
// ============================================================================
Shader "Vista/Terrain/Lit"
{
    Properties
    {
        // ---- 引擎契约：下面这一组由 TerrainData 每帧写入，名字不能改
        [HideInInspector] _Control("Control (RGBA)", 2D) = "red" {}
        [HideInInspector] _Splat3("Layer 3 (A)", 2D) = "grey" {}
        [HideInInspector] _Splat2("Layer 2 (B)", 2D) = "grey" {}
        [HideInInspector] _Splat1("Layer 1 (G)", 2D) = "grey" {}
        [HideInInspector] _Splat0("Layer 0 (R)", 2D) = "grey" {}
        [HideInInspector] _Normal3("Normal 3 (A)", 2D) = "bump" {}
        [HideInInspector] _Normal2("Normal 2 (B)", 2D) = "bump" {}
        [HideInInspector] _Normal1("Normal 1 (G)", 2D) = "bump" {}
        [HideInInspector] _Normal0("Normal 0 (R)", 2D) = "bump" {}
        [HideInInspector] _Mask3("Mask 3 (A)", 2D) = "grey" {}
        [HideInInspector] _Mask2("Mask 2 (B)", 2D) = "grey" {}
        [HideInInspector] _Mask1("Mask 1 (G)", 2D) = "grey" {}
        [HideInInspector] _Mask0("Mask 0 (R)", 2D) = "grey" {}
        [HideInInspector][Gamma] _Metallic0("Metallic 0", Range(0.0, 1.0)) = 0.0
        [HideInInspector][Gamma] _Metallic1("Metallic 1", Range(0.0, 1.0)) = 0.0
        [HideInInspector][Gamma] _Metallic2("Metallic 2", Range(0.0, 1.0)) = 0.0
        [HideInInspector][Gamma] _Metallic3("Metallic 3", Range(0.0, 1.0)) = 0.0
        [HideInInspector] _Smoothness0("Smoothness 0", Range(0.0, 1.0)) = 0.5
        [HideInInspector] _Smoothness1("Smoothness 1", Range(0.0, 1.0)) = 0.5
        [HideInInspector] _Smoothness2("Smoothness 2", Range(0.0, 1.0)) = 0.5
        [HideInInspector] _Smoothness3("Smoothness 3", Range(0.0, 1.0)) = 0.5
        [HideInInspector] _NumLayersCount("Total Layer Count", Float) = 1.0
        [HideInInspector] _HeightTransition("Height Transition", Range(0, 1.0)) = 0.0
        [HideInInspector] _TerrainHolesTexture("Holes Map (RGB)", 2D) = "white" {}
        [HideInInspector] _MainTex("BaseMap (RGB)", 2D) = "grey" {}

        // ---- Vista
        _BaseColor("Tint", Color) = (1,1,1,1)
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        // 四项技术的开关。做成材质浮点属性而不是「只有关键字」：
        // 关键字是 shader 变体状态，材质升级/重导入时会丢；属性不会。
        // ShaderGUI 与 Editor 工具都从属性推导关键字，只有一处真值。
        [ToggleUI] _VistaEnableHeightBlend("Enable Height Blend", Float) = 1.0
        [ToggleUI] _VistaEnableTriplanar("Enable Slope Triplanar", Float) = 1.0
        [ToggleUI] _VistaEnableDetailNormal("Enable Detail Normal", Float) = 1.0
        [ToggleUI] _VistaEnableMacro("Enable Macro Variation", Float) = 1.0
        [ToggleUI] _VistaEnableRelief("Enable Mid-Freq Relief", Float) = 1.0
        [ToggleUI] _VistaEnableLayerTone("Enable Layer Tone", Float) = 1.0
        [ToggleUI] _VistaMobileTier("Mobile Tier", Float) = 0.0

        // 层数据里有没有法线图 / mask 图。由 Editor 工具从 TerrainLayer 统计后写入，
        // ShaderGUI.ApplyKeywords 把它们投影成 _NORMALMAP / _MASKMAP。
        //
        // 为什么这两个不能靠「引擎会替我置位」：`_Normal0..3` / `_Mask0..3` 是地形
        // 引擎在**运行期**往材质上塞的，不进 .mat 的序列化数据，所以材质自己
        // 回答不了「层里有没有法线图」。真值只在 TerrainLayer 资产上。
        // （第一版就是指望引擎置位的，结果 m_ValidKeywords 里根本没有 _NORMALMAP，
        //  四张法线图一张没进画面 —— 地形看着像塑料。）
        //
        // 两个默认值故意不对称，按「猜错的代价」定：
        //  · 法线默认 1：层没有法线图时 `_Normal#` 绑的是默认 "bump"，解出来是
        //    (0,0,1)，合成回世界空间正好等于几何法线 —— 画面**不变**，只多几次
        //    采样。猜错只花性能。反过来默认 0 猜错，是静默丢掉四张贴图。
        //  · mask 默认 0：mask 那条路不是恒等的 —— 开着它，没 mask 的层会走
        //    回落常量，AO 通道必须由回落给出 1（见 VistaTerrainLitPasses.hlsl）。
        //    默认 0 让「没量过层数据」的材质走一条确定为 1 的 AO。
        [HideInInspector] _VistaLayerHasNormalMap("Layers Have Normal Map", Float) = 1.0
        [HideInInspector] _VistaLayerHasMaskMap("Layers Have Mask Map", Float) = 0.0

        // 调试视图。0 = 关（不编译那条分支）。
        // 1 = 合成后的世界法线, 2 = 只有层法线（detail 之前）, 3 = _NormalScale0..3,
        // 4 = height-blend 权重, 5 = _Normal0 的原始采样, 6 = _LayerHasMask0..2,
        // 7 = 只有中频起伏的法线（层法线与 detail 都不参与）。
        //
        // 为什么需要它：`_Normal0..3` / `_NormalScale0..3` / `_LayerHasMask0..3`
        // 是地形引擎在运行期塞进**内部材质副本**的，materialTemplate 资产上读到的
        // 全是 <null> / 0 —— 从 C# 侧查不到它们的真值。能回答「shader 到底收到了
        // 什么」的只有 shader 自己。
        [HideInInspector] _VistaDebugView("Debug View", Float) = 0.0

        // xy = tile 尺寸（米），z = 高度代理中心（亮度 p05/p95 的中点，Editor 工具统计），w = 高度代理对比度
        [HideInInspector] _VistaLayerParams0("Layer 0 Params", Vector) = (10,10,0.5,2)
        [HideInInspector] _VistaLayerParams1("Layer 1 Params", Vector) = (10,10,0.5,2)
        [HideInInspector] _VistaLayerParams2("Layer 2 Params", Vector) = (10,10,0.5,2)
        [HideInInspector] _VistaLayerParams3("Layer 3 Params", Vector) = (10,10,0.5,2)

        // 逐层色调拉伸。rgba 对应层 0..3；全 1 = 恒等（见 VistaLayerTone）。
        // 由 Editor 工具按各层实测的亮度 p05/p95 标定后写入 —— 本工程这四张
        // albedo 的 p05→p95 只有 5% 的动态范围，不拉开的话混层技术做得再对，
        // 画出来仍然是一片均质塑料。
        _VistaLayerContrast("Layer Contrast (rgba = layer 0..3)", Vector) = (1,1,1,1)
        _VistaLayerSaturate("Layer Saturation (rgba = layer 0..3)", Vector) = (1,1,1,1)

        _VistaHeightBlendParams("Height Blend (x=transition)", Vector) = (0.3, 0, 0, 0)
        _VistaTriplanarParams("Triplanar (x=flatNone y=flatFull z=sharpness)", Vector) = (0.85, 0.5, 4, 0)
        _VistaDetailParams("Detail (x=tile m y=strength z=fadeStart w=fadeEnd)", Vector) = (1.5, 0.6, 15, 45)
        // 第二档 detail。第一档在 45 m 就淡完，而中频起伏的细档在 9 m 尺度上 ——
        // 两者之间 45 m~150 m 这一段原来没有任何法线细节，远景因此塌成一张光板。
        // tile 6 m / 淡出 40→150 m 正是去补那一段。
        _VistaDetailOctave2("Detail Octave 2 (x=tile m y=strength z=fadeStart w=fadeEnd)", Vector) = (6, 0.35, 40, 150)
        _VistaMacroParams("Macro (x=coarse m y=fine m z=lum w=sat)", Vector) = (60, 17, 0.18, 0.12)
        // 中频起伏。y/w 是**斜率**而不是高度（见 VistaReliefNormalWS）：
        // 典型 |梯度| ≈ 0.4，所以 0.5 对应约 11° 的面倾斜、在斜射光下约 ±20% 的明暗。
        _VistaReliefParams("Relief (x=coarse m y=coarse slope z=fine m w=fine slope)", Vector) = (40, 0.5, 9, 0.25)
        // ⑤ 的第三档，2 m 尺度。它存在的理由是一段实测的空白：
        // 把 03_no_detail 与基准做差分，画面上亮起来的只有近处那道坡，
        // 45 m 之外整片是 0 —— detail 的第二档（tile 6 m）名义上淡到 150 m，
        // 但 512² 的程序化法线图在那个距离已经取到高 mip，fBm 被平均成了平面。
        // 贴图路线在远场必然失效（这正是 ⑤ 当初存在的理由），而原来的 ⑤ 只有
        // 40 m / 9 m 两档：0.4 m ~ 9 m 这个频段在远景里是空的，远山因此是
        // 一张只有大起伏的光板。解析噪声没有 mip 塌缩，所以这一段只能由它补。
        //
        // 它与前两档不同，**要淡出**：2 m 的特征在 700 m 外接近一个像素，
        // 再往远贡献的是 shimmer。淡出区间 400→700 m 按 55° FOV / 1280 宽算：
        // 700 m 处一像素约 0.52 m，2 m 的特征还有约 4 像素，仍在安全侧。
        _VistaReliefOctave3("Relief Octave 3 (x=tile m y=slope z=fadeStart w=fadeEnd)", Vector) = (2, 0.22, 400, 700)
        // 层法线切向增益。1 = 原样。它是层法线唯一可判的 A/B 控制 ——
        // `_NORMALMAP` 关不掉（地形引擎在内部材质副本上重新置位），详见 Input.hlsl。
        _VistaLayerNormalWeight("Layer Normal Weight", Range(0, 4)) = 1.0
        [Normal] _VistaDetailNormal("Detail Normal", 2D) = "bump" {}

        [ToggleUI] _EnableInstancedPerPixelNormal("Enable Instanced per-pixel normal", Float) = 1.0
    }

    HLSLINCLUDE
    #pragma multi_compile_fragment __ _ALPHATEST_ON
    ENDHLSL

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry-100"
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "False"
            "TerrainCompatible" = "True"
        }

        // ------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex VistaTerrainVert
            #pragma fragment VistaTerrainFragment

            // URP keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_IRRADIANCE
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fragment _ LIGHTMAP_BICUBIC_SAMPLING
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap

            // Vista
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _MASKMAP
            #pragma shader_feature_local _TERRAIN_INSTANCED_PERPIXEL_NORMAL
            #pragma shader_feature_local_fragment _VISTA_HEIGHT_BLEND
            #pragma shader_feature_local_fragment _VISTA_TRIPLANAR
            #pragma shader_feature_local_fragment _VISTA_DETAIL_NORMAL
            #pragma shader_feature_local_fragment _VISTA_MACRO_VARIATION
            #pragma shader_feature_local_fragment _VISTA_RELIEF
            #pragma shader_feature_local_fragment _VISTA_LAYER_TONE
            #pragma shader_feature_local_fragment _VISTA_TERRAIN_MOBILE
            #pragma shader_feature_local_fragment _VISTA_TERRAIN_DEBUG

            #include "Packages/com.kkiej.vista/Shaders/Terrain/VistaTerrainLitInput.hlsl"
            #include "Packages/com.kkiej.vista/Shaders/Terrain/VistaTerrainLitPasses.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex VistaTerrainShadowVert
            #pragma fragment VistaTerrainShadowFragment
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma shader_feature_local _TERRAIN_INSTANCED_PERPIXEL_NORMAL

            #include "Packages/com.kkiej.vista/Shaders/Terrain/VistaTerrainLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.kkiej.vista/Shaders/Terrain/VistaTerrainLitPasses.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex VistaTerrainDepthVert
            #pragma fragment VistaTerrainDepthFragment
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #pragma shader_feature_local _TERRAIN_INSTANCED_PERPIXEL_NORMAL

            #include "Packages/com.kkiej.vista/Shaders/Terrain/VistaTerrainLitInput.hlsl"
            #include "Packages/com.kkiej.vista/Shaders/Terrain/VistaTerrainLitPasses.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex VistaTerrainVert
            #pragma fragment VistaTerrainDepthNormalsFragment

            // 不声明 _GBUFFER_NORMALS_OCT：这个 shader 没有 GBuffer pass（工程跑
            // Forward+），而 Forward+ 下的 DepthNormals prepass 消费方（SSAO、decal）
            // 读的是未编码的世界法线。给一条永不置位的关键字再写一个分支，
            // 等于多一倍变体去覆盖一件不会发生的事。URP 自己的地形 shader 同此。
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _MASKMAP
            #pragma shader_feature_local _TERRAIN_INSTANCED_PERPIXEL_NORMAL
            #pragma shader_feature_local_fragment _VISTA_HEIGHT_BLEND
            #pragma shader_feature_local_fragment _VISTA_TRIPLANAR
            #pragma shader_feature_local_fragment _VISTA_DETAIL_NORMAL
            #pragma shader_feature_local_fragment _VISTA_MACRO_VARIATION
            // _VISTA_RELIEF 要进这个 pass：它改的是法线，而 SSAO 与 decal 读的
            // 正是这个 pass 的法线。漏掉它，近处地表的 AO 会按一张光板算。
            // _VISTA_LAYER_TONE 故意**不**进：它只动 albedo，而这个 pass 不输出
            // albedo —— 声明它只会把变体数翻倍去覆盖一件不影响输出的事。
            #pragma shader_feature_local_fragment _VISTA_RELIEF
            #pragma shader_feature_local_fragment _VISTA_TERRAIN_MOBILE

            #include "Packages/com.kkiej.vista/Shaders/Terrain/VistaTerrainLitInput.hlsl"
            #include "Packages/com.kkiej.vista/Shaders/Terrain/VistaTerrainLitPasses.hlsl"
            ENDHLSL
        }
    }

    Dependency "AddPassShader" = "Hidden/Universal Render Pipeline/Terrain/Lit (Add Pass)"
    Dependency "BaseMapShader" = "Hidden/Universal Render Pipeline/Terrain/Lit (Base Pass)"

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "Vista.Editor.VistaTerrainLitShaderGUI"
}
