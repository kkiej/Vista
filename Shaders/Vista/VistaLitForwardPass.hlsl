#ifndef VISTA_LIT_FORWARD_PASS_INCLUDED
#define VISTA_LIT_FORWARD_PASS_INCLUDED

// ============================================================================
//  Vista/Lit 的前向片元
//
//  ------------------------------------------------------------------ 为什么 include URP 的 pass 文件
//  下面这个 include 把 URP 的 Attributes / Varyings / LitPassVertex /
//  InitializeInputData / InitializeBakedGIData 全部拿过来直接用 ——
//  它们都是文件作用域的，include 之后就在手里。
//
//  这样做的收益很具体：插值器布局（哪个 TEXCOORD 放什么、哪些宏下才有
//  tangentWS / shadowCoord / probeOcclusion）、顶点阶段的 SH 与 lightmap UV 输出、
//  以及 InputData 的填法，**一行都不用抄**。这三样是最容易抄漏又最难发现的：
//  抄漏一个插值器的症状是「某个关键字组合下法线错了」，而那个组合可能
//  在作品集的场景里根本不出现。
//
//  于是本文件真正拥有的只有片元体那三十行 —— 对照
//  Shaders/LitForwardPass.hlsl:236-283（@13e5115b98bf），
//  差异只有末尾：URP 调 MixFog，这里调 Vista 的累加 + AP。
//
//  被 include 进来的 LitPassFragment 不会被编译：pragma 指向的是
//  VistaLitPassFragment，Unity 只编译入口点及其调用图。
//
//  ------------------------------------------------------------------ 为什么不能同时上 MixFog 和 AP
//  URP 的雾与 AP 描述的是同一件事（视线上的大气），两者都乘一遍透射率、
//  都加一遍散射，结果是远处过暗且颜色偏向 fog color。
//  所以 Vista/Lit **永远不调 MixFog** —— 不是「AP 关掉时退回 URP 雾」，
//  因为那会让「关掉 AP」变成「换一种雾」，A/B 与反例测量都失去意义。
//  URP 雾在 Vista 材质上不受支持，Step 3 的体积雾会接管这个位置。
//
//  ------------------------------------------------------------------ VISTA_LIT_DIFF_DEBUG
//  这个变体是 ShaderLibrary/VistaLighting.hlsl 里那份拷贝的**存在前提**：
//  同一次片元调用里既算我的累加、又算 UniversalFragmentPBR，输出两者的
//  逐通道相对误差，由 Editor 自检读回取最大值。
//
//  为什么放在同一次调用里而不是画两遍比对：两遍画的话，两次的 shader 关键字集合、
//  渲染状态、插值结果都得靠"应该一样"来保证，而这三样恰恰是最容易不一样的。
//  同一次调用里两者共享**同一份** inputData / surfaceData / 寄存器，
//  差异只可能来自累加本身。
//
//  一个反直觉但正确的性质：如果编译器把两条表达式折叠成一条、输出恒 0，
//  那不是"自检失效"，而是**最强的通过** —— 编译器只有在证明两者等价时才敢折叠。
//  反过来 URP 哪天改了函数体，折叠不成立，数字立刻变大。
//
//  但「恒 0」还有第三种解释：根本没渲到被测像素。所以配一套逐项故障注入
//  （_VistaDiffInject，见下），要求自检把注入的偏差报出来 ——
//  报不出来就说明这条自检自己是瞎的。
// ============================================================================

#include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"
#include "Packages/com.kkiej.vista/ShaderLibrary/VistaLighting.hlsl"

#if defined(VISTA_LIT_DIFF_DEBUG)
// 相对误差的分母下限。
//
// 不是随手取的数：本项目的一致性判据是「同通道相对误差 1%（Weber 可见阈）
// + 绝对可见度豁免 1e-3」。把分母压到 max(|ref|, 1e-3)，这两条就合成了一个数 ——
// ref 大的地方它是真实相对误差，ref 趋零的地方它退化成 |d|/1e-3，
// 即"绝对量还没到能看见的程度"。所以输出一个通道就够，不需要再回读参考值。
#define VISTA_DIFF_DENOM_FLOOR 1e-3

// 「这个像素没有参与比对」的哨兵值。
//
// 为什么写进 rgb 而不是用 alpha 当标记位：相机带 targetTexture 时 URP 末尾还有
// 一次 FinalBlit，alpha 能不能原样活到 RT 里取决于那个 blit 的实现 ——
// 把判据建在一条我没验证过的性质上，等于给自己留一个「未覆盖被读成通过」的口子。
// 而 rgb 是被测量本身，一定活着。
//
// 取 30000：必须同时高过**放大后**的相对误差（relError × 100，即使 100% 的
// 偏差也只有 100）与读回路径的加性地板，又远低于 half 上限 65504。
// 上一版取 100 是在 relError 未放大时定的；放大之后 100 只相当于 100% 偏差，
// 「哨兵」与「一个很大的真实偏差」会在判定门上撞车 —— 那会把一次真实的
// 严重不一致读成「这个像素没参与比对」，方向恰好是最危险的那一侧。
//
// 自检端把清屏色也设成这个值，于是**没被画到的像素同样是哨兵** ——
// 任何一个未覆盖的像素都不可能被读成「误差 0，一致」。
// （清屏色若被 gamma→linear 处理过会溢出成 inf，判定用 !(m < gate) 写法，
//  inf 同样落进哨兵一侧，两种情形都不会漏。）
#define VISTA_DIFF_NOT_COMPARED 30000.0

// 逐项故障注入系数：(mainLight, additionalLights, gi, vertexLighting)。
// 全 0 = 不注入，这是默认值，所以自检不设它时行为与不存在时相同。
//
// ── 为什么是 uniform 而不是 shader keyword ──
//
// 这个自检正常输出恒 0，而「恒 0」有三种解释：累加确实等价 / 两边被编译器
// 折叠成同一条表达式 / **根本没渲到被测像素**。只有注入一个已知量级的偏差、
// 并要求自检把它报出来，才能把第三种排除掉。
//
// 关键在于它必须**逐项**注入：LightingData 的四项各自由不同的关键字组合
// （_ADDITIONAL_LIGHTS、LIGHTMAP_ON、_ADDITIONAL_LIGHTS_VERTEX…）决定是否活着。
// 若只注一项，其余三项到底有没有参与这一帧就只能靠"应该有吧"来假设 ——
// 而它们恰恰是拷贝里分支最多的地方。用 uniform 四元组，一个变体就能
// 分四次探到底，且不多编一个变体。
//
// 声明在 UnityPerMaterial 之外、且包进**具名** CBUFFER：裸写在全局作用域会落进
// 隐式的 $Globals，那会让整个 pass 被判成 SRP Batcher 不兼容。具名 cbuffer 里的
// 全局量不影响兼容性，而 Shader.SetGlobalVector 照样能写进去。
// 它只存在于 DIFF_DEBUG 这个变体里，出货变体连声明都看不到。
CBUFFER_START(VistaDiffDebug)
float4 _VistaDiffInject;

// x：输出什么。
//   0 = 相对误差 × VISTA_DIFF_REL_SCALE（判定用）
//   1 = 我的值
//   2 = 参考值
//   3 = |mine − ref| × VISTA_DIFF_NUM_SCALE + VISTA_DIFF_NUM_BIAS
//   4 = 相对误差的**分母** max(|ref|, 分母下限)
//   5 = 常量 (0.25, 0.5, 0.75)（已知常量对照）
//
// ── 为什么 0 档要放大：读回路径有一条**加性**地板 ──
//
// 实测（5 档，见下）：写死 (0.25, 0.5, 0.75) 读回来逐像素低 1~2 个 half ulp，
// 最大偏离 9.766e-4 = 1/1024，且随像素位置变化。也就是说着色器与 CPU 之间
// 存在一个幅度 ±1/1024 的加性扰动场。
//
// 后果很具体：**任何接近 0 的读数都被这条地板顶到 ~6.6e-4**。
// 而 relError 的期望值恰恰是 0 —— 于是判据 1 曾经连续报出 6.595E-004，
// 八个配置一模一样，我却把它当成 fp16 相对精度（4.88e-4，量级巧合到几乎相等）
// 找了很久。教训：**尺子的地板与被测量的期望值同量级时，尺子会自己伪造一个结论。**
//
// 修法不是给判据放宽容差（那会把 1e-3 量级的真实偏差一起放过），
// 而是在**写出之前**把被测量抬到地板之上：×100 之后地板折算回相对误差是 1e-5，
// 比判定门（Weber 1%）低三个数量级，也比故障注入的 2% 低三个数量级。
// 代价只有一次乘法，而且哨兵已随之上调，不会与放大后的量撞车。
//
// 为什么需要 1/2 档：判据只给出「最坏相对误差是多少」，但那个数有两种完全
// 相反的来源 ——
//   · 参考值本身很小（≲ 分母下限）：相对误差是下限撑出来的，绝对量看不见；
//   · 参考值不小：那就是**真的**算得不一样，得追到求值次序甚至抄错。
//
// 为什么 1/2 档不够、必须有 3/4 档：1/2 档把两个值**各自**量化成 half 再写出，
// 于是小于一个 half ulp 的差异读回来必然是 0 —— 用它们相减，得到的是量化噪声，
// 不是差异。3 档在写出**之前**做减法，差值本身成为被存的量，分辨力从
// 「ref 的 ulp」提高到「差值自己的 ulp」。
//
// 3/4 档取的是那次除法的**两个操作数本身**，不是它们的替代品。
// 这样 CPU 侧能验证恒等式 0 档 == 3 档 / 4 档 ——
// 归因不成立时会当场暴露，而不是靠"应该是这样"推下去。
//
// ── 为什么 3 档要加一个偏置、5 档要输出一个古怪的常量 ──
//
// 这是上一版的教训，值得写下来：**读数接近 0 的档位，无法区分「档位没切换」
// 与「这一档的值本来就接近 0」**。上一版 3 档写 num×1e6、5 档写死 0，
// 两者在真实情形下都接近 0，于是它们读回一个与 0 档相同的小数时，
// 「else 分支没进」和「值确实很小」在数值上完全不可分辨 ——
// 归因链在这里断掉，而我却先去怀疑读回路径。
//
// 加了偏置之后，每一档的读数都自带「我是几档」的印记：
//   · 3 档读回 < 偏置的一半 → 这一档根本没执行，别去解释那个数；
//   · 5 档读回不是 (0.25,0.5,0.75) → 要么没执行，要么写入/读回路径真有问题，
//     而这两种情形能被 3 档的印记进一步分开。
//
// 偏置取 0.25 而不是更大的数：偏置的 half ulp 直接决定分子的分辨力
// （resolution = ulp(bias) / NUM_SCALE）。上一版取 8，ulp 是 7.8e-3，
// 分子只能分辨到 7.8e-9，而且读回路径那 2 个 ulp 的亏损换算成分子是 −1.6e-8，
// 于是解码出一个**负的绝对差** —— 一个不可能的数，只能说明分辨力不够。
// 取 0.25 后 ulp 是 2.44e-4，分子分辨到 2.4e-10，同时 0.25 仍远高于
// 那条 1e-3 的加性地板，「有没有执行」这个判断不受影响。
// 1/2/4 档不加印记：它们的量级本身就与 0 档差三个数量级，已经是印记了，
// 加偏置反而会毁掉它们「读数就是那个量本身」的性质。
float4 _VistaDiffCtrl;
CBUFFER_END

#define VISTA_DIFF_REL_SCALE 100.0
#define VISTA_DIFF_NUM_SCALE 1e6
#define VISTA_DIFF_NUM_BIAS  0.25
#endif

// 逐行对照 LitPassFragment，差异只在末尾。
void VistaLitPassFragment(
    Varyings input
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

#if defined(_PARALLAXMAP)
#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirTS = input.viewDirTS;
#else
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, viewDirWS);
#endif
    ApplyPerPixelDisplacement(viewDirTS, input.uv);
#endif

    SurfaceData surfaceData;
    InitializeStandardLitSurfaceData(input.uv, surfaceData);

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

    InputData inputData;
    InitializeInputData(input, surfaceData.normalTS, inputData);
    SETUP_DEBUG_TEXTURE_DATA(inputData, UNDO_TRANSFORM_TEX(input.uv, _BaseMap));

#if defined(_DBUFFER)
    ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
#endif

    InitializeBakedGIData(input, inputData);

#if defined(VISTA_LIT_DIFF_DEBUG)
    // 参考实现要吃**没被我改过**的 surfaceData（VistaComputeLighting 收 inout）。
    SurfaceData surfaceDataRef = surfaceData;
#endif

    // ---- 这里开始与 URP 分道 ----
    LightingData lightingData;
    half4 debugColor;
    bool debugOverridden = VistaComputeLighting(inputData, surfaceData, lightingData, debugColor);

#if defined(VISTA_LIT_DIFF_DEBUG)
    // 逐项故障注入（默认全 0 = 无注入），说明见 _VistaDiffInject 的声明处。
    // 乘在**我这一侧**而不是参考侧：真实的抄错就是发生在我这一侧。
    lightingData.mainLightColor        *= 1.0h + (half)_VistaDiffInject.x;
    lightingData.additionalLightsColor *= 1.0h + (half)_VistaDiffInject.y;
    lightingData.giColor               *= 1.0h + (half)_VistaDiffInject.z;
    lightingData.vertexLightingColor   *= 1.0h + (half)_VistaDiffInject.w;
#endif

    half4 color = debugOverridden ? debugColor
                                  : VistaResolveLighting(lightingData, surfaceData.alpha);

#if defined(VISTA_LIT_DIFF_DEBUG)
    // inputData 是按值传进 VistaComputeLighting 的（URP 的签名也是按值），
    // 所以里面 MixRealtimeAndBakedGI 对 bakedGI 的修改不会漏到这里 ——
    // 参考实现拿到的 inputData 与我拿到的是同一份。
    half4 reference = UniversalFragmentPBR(inputData, surfaceDataRef);

    // 归因用的档位（说明见 _VistaDiffCtrl 的声明处）。
    // 3/4 档故意写成与 relError **同一对表达式**，而不是重算一遍等价的东西：
    // 若两边各写一遍，量到的偏差里就混进了「两处写法不同」这一项。
    half3 diffNumerator = abs(color.rgb - reference.rgb);
    half3 diffDenominator = max(abs(reference.rgb), VISTA_DIFF_DENOM_FLOOR);
    half3 relError = diffNumerator / diffDenominator;

    // 0 档放大 100 倍再写出：读回路径有一条 ±1/1024 的加性地板，
    // 而 relError 的期望值是 0，不放大的话读回来永远是地板值（理由见声明处）。
    half3 payload = relError * VISTA_DIFF_REL_SCALE;
    if (_VistaDiffCtrl.x > 4.5)      payload = half3(0.25, 0.5, 0.75);
    else if (_VistaDiffCtrl.x > 3.5) payload = diffDenominator;
    else if (_VistaDiffCtrl.x > 2.5) payload = diffNumerator * VISTA_DIFF_NUM_SCALE + VISTA_DIFF_NUM_BIAS;
    else if (_VistaDiffCtrl.x > 1.5) payload = reference.rgb;
    else if (_VistaDiffCtrl.x > 0.5) payload = color.rgb;

    // 调试视图接管输出时两边都是替身颜色，比它们的差没有意义 —— 输出哨兵，
    // 而不是 0。未覆盖不能算通过。
    outColor = debugOverridden ? half4(VISTA_DIFF_NOT_COMPARED.xxx, 1)
                               : half4(payload, 1);
#else
    color = VistaApplyApTail(color, inputData);
    // 注意：这里**没有** MixFog，理由见文件头。
    color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent(_Surface));

    outColor = color;
#endif

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif // VISTA_LIT_FORWARD_PASS_INCLUDED
