using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista.EditorTools
{
    /// <summary>
    /// #20 覆盖性判据：近层 froxel 注入这条路径**到底有没有跑、跑在什么编译期状态下**。
    ///
    /// ────────────────────────────────────────────────── 它为什么不是一条数值判据
    ///
    /// 注入的能量对不对由 #19 的分布判据与 AP 那批误差判据兜着（同一套
    /// VistaEvaluateScatterSample）。本判据要抓的是另一类失效 —— 跨模块接线：
    ///
    ///   MainLightRealtimeShadow(float4)（URP ShaderLibrary/Shadows.hlsl:385）在
    ///   MAIN_LIGHT_CALCULATE_SHADOWS 未定义时**直接 return 1.0**。
    ///   也就是关键字漏设的症状是「整个场景没有光柱、且不报错」。
    ///
    /// 本项目的 compute 绕开了那个函数、直接调三个无关键字门的底层函数，把症状从
    /// 「静默全亮」降级成「全按级联 0 查」（远处光柱错位，看得见、能归因）。
    /// 但「降级」不等于「被覆盖」——「一个默认关闭、又没有判据覆盖的开关，
    /// 等于一段永远不会被发现写错的代码」，所以要有这份读数。
    ///
    /// ────────────────────────────────────────────────── 为什么必须跑真实帧
    ///
    /// #19 的切片判据走的是立即模式（自己 new 一份 VistaAtmosphereLuts + CommandBuffer），
    /// 那对纯数值的东西是对的。这里不行：要测的两样东西 ——
    /// _MAIN_LIGHT_SHADOWS_CASCADE 这个 GlobalKeyword 与 _MainLightShadowmapTexture
    /// 这个全局纹理 —— **只在渲染循环内存在**。立即模式下读回来会是
    /// 「关键字全 0、阴影图未绑」，而那是一个**完全合法**的组合（关掉阴影就长这样），
    /// 判据会一路全绿 —— 正是「布景不走被测代码路径的自检，其数字不变是空判据」。
    ///
    /// 所以流程是：置 <c>probeRequested</c> → <c>Camera.Render()</c> 同步渲一帧
    /// → 从 GraphicsBuffer 读回 <see cref="k_SlotTotal"/> 个 uint。
    ///
    /// ────────────────────────────────────────────────── 槽位语义住在这里
    ///
    /// VolumetricFog.compute 里的 VISTA_PROBE_* 下标与本文件的常量必须一致；
    /// VistaFroxelVolume.k_ShadowProbeSlots 是容量。三处对不上的症状不是报错 ——
    /// D3D11 上越界 UAV 写是**静默丢弃**，判据会读到恒为初值的格子并把它当成
    /// 「这一路没执行」。所以容量那条断言在下面是一格能失败的判据。
    /// </summary>
    static class VistaVolumetricFogState
    {
        // ---- 与 VolumetricFog.compute 的 VISTA_PROBE_* 逐一对应 ----
        const int k_SlotShadowMin     = 0;
        const int k_SlotShadowMax     = 1;
        const int k_SlotFlags         = 2;
        const int k_SlotCount         = 3;
        const int k_SlotShadowedCount = 4;
        const int k_SlotCamDriftMm    = 5;
        const int k_SlotNonFinite     = 6;
        const int k_SlotInjectMax     = 7;
        const int k_SlotShadowmapMin  = 8;
        const int k_SlotShadowmapMax  = 9;
        const int k_SlotShadowStrength = 10;
        const int k_SlotSmWidth       = 11;
        const int k_SlotSmHeight      = 12;
        const int k_SlotUrpSizeZ      = 13;
        const int k_SlotTotal         = 14;

        const uint k_FlagCascade   = 1u;
        const uint k_FlagShadowmap = 2u;
        const uint k_FlagScreen    = 4u;
        const uint k_FlagSoft      = 8u;
        const uint k_FlagRan       = 16u;

        // 探针网格（VISTA_PROBE_DIM_XY / _Z）。固定值，不跟体积分辨率走。
        const int k_ProbeDimXY = 32;
        const int k_ProbeDimZ  = 16;
        const int k_ProbeCountExpected = k_ProbeDimXY * k_ProbeDimXY * k_ProbeDimZ;

        // ---- 定点编码的反向缩放，与 shader 里的 VistaProbeFixed 调用一一对应 ----
        const float k_ShadowScale   = 1.0e6f;
        const float k_DriftScale    = 1.0e3f;   // 毫米
        const float k_InjectScale   = 1.0e3f;

        /// <summary>fp16 的上限。注入表存预曝光辐亮度正是为了不撞这个数。</summary>
        const float k_Fp16Max = 65504f;

        /// <summary>
        /// 相机漂移门（毫米）。0 表示 &lt; 1 mm；给 1 是留一个定点截断的格子，
        /// 不给 0 的理由是「一个正好压在门上的读数，判定由打印出来的最后一位小数决定」——
        /// 这里的读数是整数，1 就是「亚毫米」。真出问题时这个数是米级的。
        /// </summary>
        const uint k_DriftGateMm = 1u;

        /// <summary>
        /// atlas「不是一个常数」的门。定点分辨率是 1e-6，实测的 max − min 是 O(1)，
        /// 所以 1e-3 远在地板之上、也远在被拒答案（常数 ⇒ 差为 0）之上。
        /// </summary>
        const float k_AtlasSpreadGate = 1.0e-3f;

        [MenuItem("Window/Vista/Log Volumetric Fog State", priority = 142)]
        static void RunFromMenu()
        {
            var sb = new StringBuilder();
            Run(sb);
            // MCP 那条通道会把多行日志截断，所以拍平成一行；即便拍平了也可能截，
            // 完整内容从 Editor.log 里读。
            Debug.Log(sb.ToString().Replace("\r", string.Empty).Replace("\n", "  |  "));
        }

        static void Run(StringBuilder sb)
        {
            sb.AppendLine("=== Vista 体积雾状态（#20 注入覆盖性判据）===");

            var cam = FindGameCamera();
            if (cam == null)
            {
                sb.AppendLine("✘ 场景里没有启用的 Game 相机。本判据必须跑真实帧 —— "
                            + "见本文件头注「为什么必须跑真实帧」。");
                return;
            }
            sb.AppendLine($"相机：{cam.name}  near {cam.nearClipPlane:F3} m  {cam.pixelWidth}×{cam.pixelHeight}");

            // 预热一帧：VistaAtmosphereFeature.current 是在 RendererFeature.Create 里注册的，
            // 而 Create 要等 URP asset 第一次被用到才跑。不预热的话「刚打开工程就点菜单」
            // 会得到一个 current == null 的假失败。
            RenderOnce(cam);

            var feature = VistaAtmosphereFeature.current;
            if (feature == null)
            {
                sb.AppendLine("✘ VistaAtmosphereFeature.current 为 null：UniversalRendererData 上"
                            + "没挂 Vista Atmosphere，或者 compute 资源缺失（Create 里提前 return 了）。");
                return;
            }

            var settings = feature.volumetricFog;
            if (settings == null)
            {
                sb.AppendLine("✘ feature.volumetricFog 为 null。");
                return;
            }

            // 「一个默认关闭、又没有判据覆盖的开关」—— 这里就是点名它的地方。
            // 不自动打开：判据不该改被测对象的配置，否则「线上是关着的」这件事永远不会被发现。
            sb.AppendLine($"开关 VistaVolumetricFogSettings.enableInjection = {settings.enableInjection}"
                        + $"（screenDivisor {settings.screenDivisor}, sliceCount {settings.sliceCount}, "
                        + $"farDistanceMeters {settings.farDistanceMeters:F1} m）");
            if (!settings.enableInjection)
            {
                sb.AppendLine("ⓘ 注入是关着的，下面所有读数都不会产生 —— 本次判据**未覆盖任何东西**。");
                sb.AppendLine("  要跑：在 UniversalRendererData 的 Vista Atmosphere 上勾选"
                            + " Volumetric Fog ▸ 开发中（#20）▸ Enable Injection。");
                return;
            }

            var volume = feature.froxelVolume;
            if (volume == null || !volume.isValid)
            {
                sb.AppendLine("✘ froxelVolume 不可用：VolumetricFog.compute 缺失，或四个核里有编译不出来的"
                            + "（FroxelPlaceholder / FroxelSliceVerify / FroxelInjection / FroxelShadowProbe）。");
                return;
            }

            // ---- 请求 + 渲一帧 ----
            volume.probeRequested = true;
            RenderOnce(cam);

            var buffer = volume.shadowProbeBuffer;
            if (buffer == null)
            {
                sb.AppendLine("✘ shadowProbeBuffer 仍为 null：探针 pass 一次都没记录过。"
                            + "probeRequested 被消费的地方只有 VistaAtmospherePass 里那段 #if UNITY_EDITOR，"
                            + "说明 froxelEnabled 在这一帧是 false（相机类型闸门 / Prepare 失败）。");
                return;
            }

            // 容量断言。对不上时下标 8 的 InterlockedMin 会被 D3D11 静默丢弃，
            // 而那一格恒为初值 —— 会被读成「阴影图这一路没跑」。
            bool capacityOk = buffer.count == k_SlotTotal
                           && VistaFroxelVolume.k_ShadowProbeSlots == k_SlotTotal;
            sb.AppendLine($"{Mark(capacityOk)}判据⓿ 槽位容量：buffer.count = {buffer.count}, "
                        + $"k_ShadowProbeSlots = {VistaFroxelVolume.k_ShadowProbeSlots}, 本文件期望 {k_SlotTotal}");
            if (!capacityOk)
            {
                sb.AppendLine("  ⚠ 容量不一致，下面的读数不可信（越界 UAV 写是静默丢弃）。");
                return;
            }

            var raw = new uint[k_SlotTotal];
            buffer.GetData(raw);

            sb.AppendLine($"原始槽位：[{string.Join(", ", raw)}]");

            // ---------------------------------------------------------------- 判据① 跑过没有
            uint flags = raw[k_SlotFlags];
            bool ran = (flags & k_FlagRan) != 0u;
            sb.AppendLine($"{Mark(ran)}判据① 「跑过」位：flags = 0x{flags:X}");
            if (!ran)
            {
                sb.AppendLine("  ⚠ flags == 0 时，「从未派发」与「级联关+阴影图未绑+屏幕空间关+软阴影关」"
                            + "（一个完全合法的组合）在报表上长得一模一样 —— 这就是 RAN 位存在的理由。"
                            + "本次读数是前者。");
                return;
            }

            // ---------------------------------------------------------------- 判据② 编译期关键字
            bool hasCascade   = (flags & k_FlagCascade)   != 0u;
            bool hasScreen    = (flags & k_FlagScreen)    != 0u;
            bool hasSoft      = (flags & k_FlagSoft)      != 0u;
            bool hasShadowmap = (flags & k_FlagShadowmap) != 0u;

            // 期望值从 URP asset 推，而不是「打印出来看看」：判据的输入不含被测对象的值。
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            int cascadeCount = urp != null ? urp.shadowCascadeCount : 0;
            bool urpShadowsOn = urp != null && urp.supportsMainLightShadows;
            bool cascadeExpected = urpShadowsOn && cascadeCount > 1;

            bool kwOk = hasCascade == cascadeExpected && !hasScreen && !hasSoft;
            sb.AppendLine($"{Mark(kwOk)}判据② 编译期关键字：CASCADE {hasCascade}（URP asset: "
                        + $"mainLightShadows {urpShadowsOn}, cascades {cascadeCount} ⇒ 期望 {cascadeExpected}）, "
                        + $"SCREEN {hasScreen}（必须 false）, SOFT {hasSoft}（必须 false）");
            if (hasScreen)
                sb.AppendLine("  ⚠ _MAIN_LIGHT_SHADOWS_SCREEN 被定义了：TransformWorldToShadowCoord 会走"
                            + "屏幕 NDC 那一支，而 compute 里没有屏幕坐标可言。见 compute 头部的 pragma 说明。");
            if (hasSoft)
                sb.AppendLine("  ⚠ _SHADOWS_SOFT* 被定义了：SampleShadowmap 会走 4~9 tap 的 tent 滤波，"
                            + "逐 froxel 的成本按 tap 数翻倍。本节明确选的是硬阴影 1 tap。");
            if (!hasCascade && cascadeExpected)
                sb.AppendLine("  ⚠ 级联关键字漏设：TransformWorldToShadowCoord 会**全按级联 0** 查"
                            + "（cascadeIndex = 0），远处的光柱会整体错位。");

            // ---------------------------------------------------------------- 判据③ 阴影图绑定
            // 期望同样从 URP asset 推。注意 supportsMainLightShadows 关掉时
            // cameraData.maxShadowDistance 是 0，远边界的夹紧会被跳过（见 ResolveFarDistance）。
            bool shadowmapOk = hasShadowmap == urpShadowsOn;
            sb.AppendLine($"{Mark(shadowmapOk)}判据③ 阴影图绑定：_VistaFroxelCameraWS.w ⇒ {hasShadowmap}"
                        + $"（URP asset supportsMainLightShadows = {urpShadowsOn}）");
            if (!hasShadowmap)
                sb.AppendLine("  ⓘ 阴影图未绑 ⇒ VistaFroxelSunShadow 恒返回 1.0，画面上是"
                            + "「有雾、没有光柱」。下面判据⑤（不恒为 1）在这条路径上**无法覆盖**。");

            // ---------------------------------------------------------------- 判据④ 网格全走过
            uint count = raw[k_SlotCount];
            bool countOk = count == (uint)k_ProbeCountExpected;
            sb.AppendLine($"{Mark(countOk)}判据④ 探针网格：计数 {count} / 期望 "
                        + $"{k_ProbeCountExpected}（{k_ProbeDimXY}×{k_ProbeDimXY}×{k_ProbeDimZ}）");
            if (!countOk)
                sb.AppendLine("  ⚠ 计数少一截通常是 dispatch 的 group 数与 numthreads 对不上，"
                            + "或者 _VistaFroxelSize 是 0（那时核在 any(size == 0u) 处提前 return）。");

            // ---------------------------------------------------------------- 判据⑤ 阴影不恒为 1
            float shadowMin = raw[k_SlotShadowMin] == uint.MaxValue
                ? float.NaN : raw[k_SlotShadowMin] / k_ShadowScale;
            float shadowMax = raw[k_SlotShadowMax] / k_ShadowScale;
            uint shadowed = raw[k_SlotShadowedCount];

            bool rangeOk = !float.IsNaN(shadowMin) && shadowMin >= 0f && shadowMax <= 1.0f + 1e-5f
                        && shadowMin <= shadowMax;
            sb.AppendLine($"{Mark(rangeOk)}判据⑤a 阴影值域：min {Fmt(shadowMin)} / max {Fmt(shadowMax)}"
                        + "（必须落在 [0, 1] 且 min ≤ max）");
            if (raw[k_SlotShadowMin] == uint.MaxValue)
                sb.AppendLine("  ⚠ min 槽仍是初值 uint.MaxValue：InterlockedMin 一次都没执行。"
                            + "（初值由 CPU 侧 ResetShadowProbeBuffer 写 —— 填成 0 会把"
                            + "「一个点都没被遮」伪装成「全被遮」，所以这一格的初值必须是 MaxValue。）");

            bool shadowedOk = shadowed > 0u;
            sb.AppendLine($"{(hasShadowmap ? Mark(shadowedOk) : "ⓘ ")}判据⑤b 不恒为 1："
                        + $"被遮点数 {shadowed} / {count}（shadow < 0.999）");

            // 阴影强度：min 读数的归因输入。SampleShadowmap 返回 lerp(1, s, strength)，
            // 所以「全被遮」的地板是 1 − strength。不打印它的话 min = 0.2 是个谜数。
            //
            // 这一格**刻意不设门**：shadowMin ≥ 1 − strength 是 lerp 的值域，
            // 由构造保证，写成门就是「一个本轮无法失败的守卫」。
            float strength = raw[k_SlotShadowStrength] / k_ShadowScale;
            float shadowFloor = 1f - strength;
            sb.AppendLine($"  ⓘ _MainLightShadowParams.x（阴影强度）= {Fmt(strength)}"
                        + $" ⇒ 全遮地板 1 − strength = {Fmt(shadowFloor)}；实测 min = {Fmt(shadowMin)}"
                        + $"（差 {(shadowMin - shadowFloor).ToString("+0.000000;-0.000000")}）。"
                        + "min 触到地板 ⇒ 确实有采样点被完全遮住，不只是半遮。");

            if (hasShadowmap && !shadowedOk)
            {
                sb.AppendLine("  ⚠ 一个点都没被遮。三种成因：");
                sb.AppendLine("    (a) 阴影图本身是空的（场景里没有投影物 / 光被关了）—— 看判据⑥b;");
                sb.AppendLine($"    (b) 阴影强度是 0（读数 {Fmt(strength)}）⇒ SampleShadowmap 恒返回 1，"
                            + "与代码无关;");
                sb.AppendLine("    (c) 阴影图有内容、强度非 0，但我们查出来恒 1（矩阵、级联下标、"
                            + "isPerspectiveProjection 传错）—— 这一种才是本模块的 bug。");
                sb.AppendLine("  这条还依赖场景布景：近层体只有 "
                            + $"{(volume.allocatedDesc.HasValue ? volume.allocatedDesc.Value.handoffMeters : 0f):F1} m 深，"
                            + "相机前方这段距离内必须有投影物，否则本格是布景问题、不是代码问题。");
            }
            else if (!hasShadowmap)
            {
                sb.AppendLine("  ⓘ 阴影图未绑，本格**未被覆盖**（不是通过）。"
                            + "「本轮无法失败的守卫要在报告里点名」。");
            }

            // ---------------------------------------------------------------- 判据⑥ 阴影图有没有内容
            if (hasShadowmap)
            {
                // ---- ⑥a atlas 尺寸 ----
                // 核内的尺寸走 _MainLightShadowmapTexture.GetDimensions()，**不读**
                // URP 的 _MainLightShadowmapSize：那个全局只在软阴影档下发
                //（MainLightShadowCasterPass.cs:334 把 _ShadowOffset0/1 与 _ShadowmapSize
                // 三行一起包在 if (shadowData.supportsSoftShadows) 里），硬阴影档下它
                // 保持上一次的值、甚至从没被写过。第一版按它算采样坐标，smCoord 恒为 (0,0)，
                // ⑥b 读到一个常数并**红了** —— 而红的理由不是场景没有投影物
                //（同一趟里有点被遮）。这一格把那个坑变成一个能失败的数字。
                uint smW = raw[k_SlotSmWidth];
                uint smH = raw[k_SlotSmHeight];
                bool sizeOk = smW > 0u && smH > 0u;
                sb.AppendLine($"{Mark(sizeOk)}判据⑥a atlas 尺寸：GetDimensions ⇒ {smW}×{smH}"
                            + "（必须都 > 0，否则 ⑥b 的采样坐标全塌到 (0,0)）");
                sb.AppendLine($"  ⓘ 对照：URP 的 _MainLightShadowmapSize.z = {raw[k_SlotUrpSizeZ]}"
                            + $"（真实 atlas 宽是 {smW}）。它只在软阴影档才被下发"
                            + "（MainLightShadowCasterPass.cs:334 把 _ShadowOffset0/1 与 _ShadowmapSize"
                            + "三行一起包在 if (shadowData.supportsSoftShadows) 里），"
                            + "所以硬阴影档下读到的是**别人留下的脏值**：1 = 某个走过 empty-shadowmap "
                            + "路径的相机（SceneView / 预览 / 反射探针）设的 s_EmptyShadowmapSize "
                            + "= (1,1,1,1)（同文件 :34, k_EmptyShadowMapDimensions = 1）。"
                            + "这比读到 0 更坏 —— 1×1 是一个看起来**完全合法**的尺寸，"
                            + "拿它算坐标全塌到 (0,0) 且不报任何错。"
                            + "URP 自己不受影响：那个尺寸只被 tent 软阴影的 offset 用，1 tap 不读它。");

                // ---- ⑥b atlas 内容 ----
                // 判的是「atlas 是不是一个常数」，不是「min 等于某个清空值」。
                //
                // 为什么不能拿 min 去跟清空值比：清空值取决于 UNITY_REVERSED_Z。
                // D3D11/Vulkan/Metal 上 URP 的阴影矩阵带 reversed-Z 翻转，清空态是
                // **0.0**；按「清空态是 1.0」写的门会在 atlas 全空时读到 min = 0
                // 并宣布「有内容」—— 一个**假通过**，而且它坏在本判据唯一的职责上
                //（把「atlas 空」与「采样错」分开）。
                // 「空 atlas 是一个常数」不依赖任何深度约定，两个方向都成立。
                bool minIsInit = raw[k_SlotShadowmapMin] == uint.MaxValue;
                float smMin = minIsInit ? float.NaN : raw[k_SlotShadowmapMin] / k_ShadowScale;
                float smMax = raw[k_SlotShadowmapMax] / k_ShadowScale;
                float spread = minIsInit ? 0f : smMax - smMin;

                bool smOk = !minIsInit && spread > k_AtlasSpreadGate;
                sb.AppendLine($"{(sizeOk ? Mark(smOk) : "ⓘ ")}判据⑥b 阴影图内容"
                            + $"（归因用，与我们的采样无关）：atlas 深度 min {Fmt(smMin)}"
                            + $" / max {Fmt(smMax)} ⇒ 跨度 {Sci(spread)}"
                            + $"（门 > {Sci(k_AtlasSpreadGate)}；这道门要拒绝的最小错答案是"
                            + "「atlas 是常数」⇒ 跨度 0）");
                if (!sizeOk)
                    sb.AppendLine("  ⓘ ⑥a 未通过 ⇒ 本格**未被覆盖**（不是失败）。"
                                + "尺寸塌成 0 时核内那一段被 smW > 0 跳过，读数留在初值上。");
                else
                {
                    sb.AppendLine($"  ⓘ 顺带读出了深度约定：min ≈ 0 且 max > 0 ⇒ reversed-Z"
                                + "（清空态 0 = 无穷远）；反之 max ≈ 1 ⇒ 正向 Z。"
                                + $"本次 {(smMin < 0.5f && smMax > 0.5f ? "reversed-Z" : "无法判定，见上面两个数")}。");
                    if (!smOk)
                        sb.AppendLine("  ⚠ atlas 是常数 ⇒ 判据⑤b 的成因是 (a)，不是我们的代码。"
                                    + "「第一个可疑对象被修掉、读数却没变时，必须回头重新归因」——"
                                    + "这一格就是那个「回头」的落点。"
                                    + "（残余风险：全部 1024 个采样点在光空间共面时 atlas 也是常数，"
                                    + "那要求整个 atlas 只有一个正对光的平面，可忽略。）");
                }
            }
            else
            {
                sb.AppendLine("ⓘ 判据⑥a/⑥b 未覆盖：阴影图未绑，核内那一段 LOAD 被 w < 0.5 跳过了。");
            }

            // ---------------------------------------------------------------- 判据⑦ 注入表健康
            uint nonFinite = raw[k_SlotNonFinite];
            bool finiteOk = nonFinite == 0u;
            sb.AppendLine($"{Mark(finiteOk)}判据⑦ 注入表有限性：非有限 froxel {nonFinite} / {count}");
            if (!finiteOk)
                sb.AppendLine("  ⚠ NaN/Inf 会顺着三线性插值蔓延到全屏。头号嫌疑是"
                            + " SampleShadowmap 的 isPerspectiveProjection 传了 true —— "
                            + "TransformWorldToShadowCoord 走 #else 那一支返回的 w 恒为 0，"
                            + "那会做一次 xyz /= 0。");

            // ---------------------------------------------------------------- 判据⑧ fp16 余量
            float injectMax = raw[k_SlotInjectMax] / k_InjectScale;
            bool fp16Ok = injectMax < k_Fp16Max;
            float headroom = injectMax > 0f ? k_Fp16Max / injectMax : float.PositiveInfinity;
            sb.AppendLine($"{Mark(fp16Ok)}判据⑧ fp16 余量：注入 rgb 最大值 {Sci(injectMax)}"
                        + $"（预曝光后），fp16 天花板 {k_Fp16Max:F0} ⇒ 余量 ×{Sci(headroom)}");
            sb.AppendLine("  ⓘ 这个读数是从 RGBA16F 的 UAV 里**读回来的**，不是 CPU 侧重算的 —— "
                        + "fp16 的饱和只有走一趟纹理往返才算量过。撞顶的症状是一整列雾被压平，"
                        + "而 #18 量到过：浓雾 + 低太阳的源项到 3.7e5 cd/m²，绝对单位下必然撞顶。"
                        + "预曝光这条约定就是为它存在的。");

            // ---------------------------------------------------------------- 判据⑨ 隐藏依赖
            uint driftMm = raw[k_SlotCamDriftMm];
            bool driftOk = driftMm <= k_DriftGateMm;
            sb.AppendLine($"{Mark(driftOk)}判据⑨ 隐藏依赖 |_WorldSpaceCameraPos − _VistaFroxelCameraWS|"
                        + $" = {driftMm} mm = {Sci(driftMm / k_DriftScale)} m（门 ≤ {k_DriftGateMm} mm）");
            sb.AppendLine("  ⓘ GetMainLightShadowFade（Shadows.hlsl:434）用 URP 的 _WorldSpaceCameraPos"
                        + "算淡出距离，而 _VistaFroxelCameraWS 存在的理由恰恰是不信任那个全局。"
                        + "仍然调它、不自己重写那一行 saturate —— 「同一个量的第二份实现连 8 行的"
                        + "辅助函数也算」。代价就是这条依赖，而这一格把它变成一个**能失败的数字**，"
                        + "而不是一句注释里的担忧。真出问题时这个数是米级的。");

            // ---------------------------------------------------------------- 分配口径
            if (volume.allocatedDesc.HasValue)
            {
                var d = volume.allocatedDesc.Value;
                sb.AppendLine($"分配口径：{d}");
                sb.AppendLine($"  AP 的接手点应当是 handoff = {d.handoffMeters:F3} m，"
                            + $"不是 far = {d.farMeters:F1} m（差 {d.farMeters - d.handoffMeters:F3} m）。"
                            + "但 #20 **刻意不动** AP 的 nearDistanceKm：光把 near 推到 handoff 是不够的，"
                            + "AerialPerspectiveLut 的积分起点是 tPrev = 0.0，切片 0 照样会积 [0, near]，"
                            + "所以起点也得一起改 —— 那属于 #21/#25。#19 待办里那句话要按这个改。");
            }

            sb.AppendLine("ⓘ 未覆盖：注入历史表（#22 时间重投影）与积分表（#21 深度积分）"
                        + "这两条**写入**路径本节一次都没跑过。VistaAtmospherePass 里也刻意没有 ImportTexture "
                        + "它们 —— 顺手导入会让「没人写」这件事在代码里看不出来。");
        }

        static Camera FindGameCamera()
        {
            var cam = Camera.main;
            if (cam != null && cam.isActiveAndEnabled)
                return cam;

            foreach (var c in Camera.allCameras)
            {
                if (c.cameraType == CameraType.Game)
                    return c;
            }
            return null;
        }

        /// <summary>
        /// 同步渲一帧。走 <c>Camera.Render()</c> 而不是 <c>SceneView.RepaintAll()</c>：
        /// 后者是排队重绘，菜单回调返回时还没画完，读回来的会是**上一次**请求的结果 ——
        /// 而 min/max 是跨帧单调的，那种错位会长得非常像一个合理的读数。
        ///
        /// 借一张临时 RT 是为了不往 Game View 的后台缓冲上画（Editor 下那会有一堆
        /// 与本判据无关的警告）。副作用是 cameraTargetDescriptor 的尺寸变成 RT 的尺寸，
        /// 也就是 froxel 体的 XY 由它决定 —— 所以上面把分配口径打出来了。
        /// </summary>
        static void RenderOnce(Camera cam)
        {
            var prev = cam.targetTexture;
            var rt = RenderTexture.GetTemporary(
                Mathf.Max(1, cam.pixelWidth), Mathf.Max(1, cam.pixelHeight), 24,
                RenderTextureFormat.DefaultHDR);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
            }
            finally
            {
                cam.targetTexture = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        static string Mark(bool ok) => ok ? "✔ " : "✘ ";

        static string Sci(float v) => v.ToString("0.000e+0");

        static string Fmt(float v) => float.IsNaN(v) ? "n/a" : v.ToString("0.000000");
    }
}
