#ifndef VISTA_TERRAIN_LIT_INPUT_INCLUDED
#define VISTA_TERRAIN_LIT_INPUT_INCLUDED

// ============================================================================
//  Vista/Terrain/Lit 的输入声明
//
//  归属说明（对应方案文档 1.2 的划界要求）：
//  · `_Control` / `_Splat0..3` / `_Normal0..3` / `_Mask0..3` / `_Terrain` cbuffer
//    里的那一组名字，以及 `TerrainInstancing()` 的 heightmap 解包，**都是 Unity
//    地形引擎的外部契约**，与 URP 的 TerrainLitInput.hlsl 同名同义。
//    TerrainData 每帧按名字往材质上写这些值；换名字等于地形不渲染。
//    这一段是照抄的，不是设计。
//  · `_VistaTerrain` cbuffer 与 VistaTerrainSplat.hlsl 是本模块新增的。
//
//  ------------------------------------------------------------------ 少了什么
//  · `_MainTex` / basemap 一路不声明。地形的 basemap 只在超出
//    `Terrain.basemapDistance` 时启用，本场景该值为 1000 m，而地形自身
//    512 m 见方 —— 在这个工程里 basemap 永不生效。声明一条永不执行的路径，
//    等于声明一件没测过的事。远景降级如果以后要做，要做成 impostor 或
//    真正测过的 basemap，不是现在留一个空壳。
//  · `_MetallicTex` 不声明（地形层的 metallic 是 per-layer 标量）。
// ============================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

#define _Surface 0.0 // 地形永远不透明

// ---------------------------------------------------------------- Vista 自己的参数
// 放进 UnityPerMaterial：这些是**材质级**参数（由 Vista 的材质面板与 Editor 工具写），
// 与下面 `_Terrain` cbuffer 里由引擎逐地形写的量不是一类，混在一起会让
// SRP Batcher 的 per-material 常量布局和引擎的 per-terrain 写入撞在同一块。
CBUFFER_START(UnityPerMaterial)
    half4 _BaseColor;
    half  _Cutoff;

    // 四项技术的开关（真值源）。HLSL 里不读它们——分支走关键字；
    // 但必须声明，否则 SRP Batcher 会因为「材质有属性、cbuffer 里没有」而拒绝这个 shader。
    half _VistaEnableHeightBlend;
    half _VistaEnableTriplanar;
    half _VistaEnableDetailNormal;
    half _VistaEnableMacro;
    half _VistaEnableRelief;
    half _VistaEnableLayerTone;
    half _VistaMobileTier;
    half _VistaLayerHasNormalMap;
    half _VistaLayerHasMaskMap;
    half _VistaDebugView;

    // xy = 该层的 tile 尺寸（米/次重复），z = 该层 albedo 的亮度均值，w = 高度代理对比度
    half4 _VistaLayerParams0, _VistaLayerParams1, _VistaLayerParams2, _VistaLayerParams3;

    // rgba 各对应层 0..3。1 = 恒等。
    half4 _VistaLayerContrast;
    half4 _VistaLayerSaturate;

    // x = height transition, y =（保留）, z =（保留）, w =（保留）
    half4 _VistaHeightBlendParams;
    // x = 开始混入 triplanar 的 |n.y|, y = 完全 triplanar 的 |n.y|, z = 平面权重指数
    half4 _VistaTriplanarParams;
    // x = detail tile（米）, y = 强度, z = 淡出起点（米）, w = 淡出终点（米）
    half4 _VistaDetailParams;
    // 第二档 detail，同上的四个分量
    half4 _VistaDetailOctave2;
    // x = 粗尺度（米）, y = 细尺度（米）, z = 亮度幅度, w = 饱和度幅度
    half4 _VistaMacroParams;
    // x = 粗尺度（米/周期）, y = 粗斜率, z = 细尺度（米/周期）, w = 细斜率
    half4 _VistaReliefParams;
    // ⑤ 的第三档：x = 尺度（米/周期）, y = 斜率, z = 淡出起点（米）, w = 淡出终点（米）
    // 这一档与前两档不同，**带距离淡出** —— 它的特征只有 2 m，到 700 m 外就
    // 接近一个像素，再往远是 shimmer 而不是细节。前两档（40 m / 9 m）在整个
    // 可视距离内都远离 Nyquist，所以它们不淡出，这一档必须淡。
    half4 _VistaReliefOctave3;
    // 层法线的切向增益。存在的理由不是美术调参，而是**它是层法线唯一可判的
    // A/B 控制**：`_NORMALMAP` 关不掉 —— 地形引擎会在它自己的内部材质副本上
    // 按「层有没有法线图」重新置位，模板上关掉的那一下根本到不了渲染。
    // 于是「关掉层法线」与「层法线没生效」在画面上无法区分，而那正是
    // 06_no_layernormal 曾经与基准逐像素相等、被我读成「层法线毫无贡献」的原因。
    // 这个量在 Vista 自己的 cbuffer 里，引擎碰不到。
    half _VistaLayerNormalWeight;
CBUFFER_END

// ---------------------------------------------------------------- 引擎契约
CBUFFER_START(_Terrain)
    half _NormalScale0, _NormalScale1, _NormalScale2, _NormalScale3;
    half _Metallic0, _Metallic1, _Metallic2, _Metallic3;
    half _Smoothness0, _Smoothness1, _Smoothness2, _Smoothness3;
    half4 _DiffuseRemapScale0, _DiffuseRemapScale1, _DiffuseRemapScale2, _DiffuseRemapScale3;
    half4 _MaskMapRemapOffset0, _MaskMapRemapOffset1, _MaskMapRemapOffset2, _MaskMapRemapOffset3;
    half4 _MaskMapRemapScale0, _MaskMapRemapScale1, _MaskMapRemapScale2, _MaskMapRemapScale3;

    float4 _Control_ST;
    float4 _Control_TexelSize;
    half _DiffuseHasAlpha0, _DiffuseHasAlpha1, _DiffuseHasAlpha2, _DiffuseHasAlpha3;
    half _LayerHasMask0, _LayerHasMask1, _LayerHasMask2, _LayerHasMask3;
    half4 _Splat0_ST, _Splat1_ST, _Splat2_ST, _Splat3_ST;
    half _HeightTransition;
    half _NumLayersCount;
    float _TerrainBasemapDistance;

#ifdef UNITY_INSTANCING_ENABLED
    float4 _TerrainHeightmapRecipSize;   // (1/w, 1/h, 1/(w-1), 1/(h-1))
#endif
    float4 _TerrainHeightmapScale;       // (hmScale.x, hmScale.y / kMaxHeight, hmScale.z, 0)
CBUFFER_END

TEXTURE2D(_Control);    SAMPLER(sampler_Control);
TEXTURE2D(_Splat0);     SAMPLER(sampler_Splat0);
TEXTURE2D(_Splat1);
TEXTURE2D(_Splat2);
TEXTURE2D(_Splat3);

TEXTURE2D(_Normal0);    SAMPLER(sampler_Normal0);
TEXTURE2D(_Normal1);
TEXTURE2D(_Normal2);
TEXTURE2D(_Normal3);

TEXTURE2D(_Mask0);      SAMPLER(sampler_Mask0);
TEXTURE2D(_Mask1);
TEXTURE2D(_Mask2);
TEXTURE2D(_Mask3);

// Vista 新增：全地形共用的一张 detail normal
TEXTURE2D(_VistaDetailNormal);  SAMPLER(sampler_VistaDetailNormal);

#if defined(UNITY_INSTANCING_ENABLED) && defined(_TERRAIN_INSTANCED_PERPIXEL_NORMAL)
#define ENABLE_TERRAIN_PERPIXEL_NORMAL
#endif

#ifdef UNITY_INSTANCING_ENABLED
TEXTURE2D(_TerrainHeightmapTexture);
TEXTURE2D(_TerrainNormalmapTexture);
SAMPLER(sampler_TerrainNormalmapTexture);
#endif

UNITY_INSTANCING_BUFFER_START(Terrain)
UNITY_DEFINE_INSTANCED_PROP(float4, _TerrainPatchInstanceData)  // (xBase, yBase, skipScale, ~)
UNITY_INSTANCING_BUFFER_END(Terrain)

#ifdef _ALPHATEST_ON
TEXTURE2D(_TerrainHolesTexture);
SAMPLER(sampler_TerrainHolesTexture);

void ClipHoles(float2 uv)
{
    float hole = SAMPLE_TEXTURE2D(_TerrainHolesTexture, sampler_TerrainHolesTexture, uv).r;
    // 压缩开启时 0 并不真是 0（可能是 1/2047），所以比一个 epsilon 而不是比 0。
    clip(hole < 0.0005f ? -1 : 1);
}
#endif

// ---------------------------------------------------------------- 引擎契约：实例化地形
void TerrainInstancing(inout float4 positionOS, inout float3 normal, inout float2 uv)
{
#ifdef UNITY_INSTANCING_ENABLED
    float2 patchVertex = positionOS.xy;
    float4 instanceData = UNITY_ACCESS_INSTANCED_PROP(Terrain, _TerrainPatchInstanceData);

    float2 sampleCoords = (patchVertex.xy + instanceData.xy) * instanceData.z;
    float height = UnpackHeightmap(_TerrainHeightmapTexture.Load(int3(sampleCoords, 0)));

    positionOS.xz = sampleCoords * _TerrainHeightmapScale.xz;
    positionOS.y = height * _TerrainHeightmapScale.y;

#ifdef ENABLE_TERRAIN_PERPIXEL_NORMAL
    normal = float3(0, 1, 0);
#else
    normal = _TerrainNormalmapTexture.Load(int3(sampleCoords, 0)).rgb * 2 - 1;
#endif
    uv = sampleCoords * _TerrainHeightmapRecipSize.zw;
#endif
}

void TerrainInstancing(inout float4 positionOS, inout float3 normal)
{
    float2 uv = float2(0, 0);
    TerrainInstancing(positionOS, normal, uv);
}

void TerrainInstancing(inout float4 positionOS)
{
    float3 normal = float3(0, 0, 0);
    TerrainInstancing(positionOS, normal);
}

#endif // VISTA_TERRAIN_LIT_INPUT_INCLUDED
