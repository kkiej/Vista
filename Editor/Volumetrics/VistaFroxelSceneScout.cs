using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static Vista.EditorTools.VistaFroxelShadowRig;

namespace Vista.EditorTools
{
    /// <summary>
    /// 运行时布景探路工具（#27 验收第 1 项的**前置观测**）。
    ///
    /// ── 它不是判据，这一点必须写在最前面 ──
    ///
    /// 第 1 项要判的是「Froxel 档相对 AP-only 档到底多出了什么」，其中最硬的一条
    /// （判据 β：被主光遮住的那部分雾在 Froxel 档下更暗）只有在布景里**真的存在
    /// 被遮住的雾**时才有分辨力。而这件事在这个项目里从来没有被观测过：
    ///
    ///   · <c>VistaVolumetricFogState</c> 拿到过真实的阴影图，但它用的是**场景里
    ///     现成的相机**，于是读数随「当前打开的是哪个场景」变化；
    ///   · <c>VistaApVariantAgreementSelfTest</c> 会自己搭相机，但它在五个地方
    ///     **刻意把阴影全关掉**（相机 renderShadows、灯 shadows、材质
    ///     _RECEIVE_SHADOWS_OFF、renderer shadowCastingMode / receiveShadows），
    ///     因为阴影边界是 AP 合成的干扰项。
    ///
    /// 两个先例各覆盖了一半，没有一个回答「**自己搭的**布景里，URP 的主光
    /// shadow atlas 到底有没有内容」。这个答案决定判据 β 的形状：
    ///   有内容 ⇒ β 可以按「符号」判（遮住的雾变暗）；
    ///   没内容 ⇒ β 在这个布景上**没有分辨力**，它必须报「不可判定」而不是报「通过」。
    /// 后一条是本项目的常备反面模式：「不可判定必须是缺口，不能是通过」。
    ///
    /// 所以先跑这个工具读这几个数，再据此写判据 —— 而不是先写判据再去解释读数。
    ///
    /// ── 为什么观测要自己搭布景，而不是量当前场景 ──
    ///
    /// 判据将来住在 package 里，而 package 里**一个 .unity 都没有**
    /// （<c>Demo_Night.unity</c> 住在 Project-ARPG）。一个引用不到自己布景的判据，
    /// 在别人的工程里第一次运行就会红，而红的原因与被测代码无关。
    /// 所以布景必须由代码搭出来，探路也必须在同一种布景上探 ——
    /// 在 Demo 场景上探出来的结论，对代码搭的布景不成立。
    ///
    /// ── 为什么必须渲真实帧 ──
    ///
    /// 见 <c>VistaFroxelVolume.probeRequested</c> 的注释：探针要测的是 URP 的
    /// 编译期关键字状态与阴影图绑定，这两样只在渲染循环内存在。立即模式下读出来的
    /// 是「关键字全 0、阴影图未绑」—— 一个**完全合法**的组合，于是一切全绿。
    ///
    /// ── 它改了什么、还回去了什么 ──
    ///
    /// 要把 Froxel 档真正打开，就必须动 feature 上的三个旋钮（fog.mode、
    /// fog.meanFreePathMeters、volumetricFog.enableInjection）与场景的
    /// <c>RenderSettings.sun</c>。全部在 finally 里还原 —— 「量性能的工具不该改被量对象」
    /// 在这里的对应物是「探路工具不该给下一次运行留下不同的初始条件」。
    ///
    /// <c>RenderSettings.sun</c> 这一条不是可选的：平行光**不因为** layer 不在相机
    /// cullingMask 里就从 visibleLights 里消失，而 <c>GetMainLightIndex</c> 的第一条规则
    /// 是「等于 RenderSettings.sun 的那盏直接返回」。不接管的话，探到的阴影是
    /// **当前打开的场景**那盏灯投的，而报告里看不出这个前提。
    /// （这条是 #12 的自检实测出来的，原文在 VistaApVariantAgreementSelfTest.Build。）
    /// </summary>
    static class VistaFroxelSceneScout
    {
        // 布景尺寸、太阳方向、介质参数、分区口径、像素统计 —— 全部住在
        // VistaFroxelShadowRig 里，与判据共用同一份。为什么不各搭一套，见那个类的头注。


        [MenuItem("Window/Vista/Scout Runtime Froxel Scene", priority = 135)]
        static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Vista 运行时 froxel 布景探路 ===");
            sb.AppendLine("这是**观测**，不是判据：它只报读数，不报通过/失败。");
            sb.AppendLine("它要回答的四件事决定 #27 第 1 项里判据 β 的形状。");
            sb.AppendLine();

            bool complete = Scout(sb);

            sb.AppendLine();
            sb.AppendLine(complete
                ? "结论：四项观测全部取到读数，见上面每一项末尾的「⇒」。"
                : "结论：**有观测取不到读数**。在补齐之前，判据 β 只能写成「不可判定」。");
            Debug.Log(sb.ToString());
        }

        static bool Scout(StringBuilder sb)
        {
            // ── 前置条件。一条不满足就停：半套布景量出来的数字比没数字更危险。
            var feature = VistaAtmosphereFeature.current;
            if (feature == null)
            {
                sb.AppendLine("✘ 前置：VistaAtmosphereFeature.current 为 null —— "
                            + "feature 没装进当前 Renderer，或还没有相机渲过一帧。");
                return false;
            }

            var volume = feature.froxelVolume;
            if (volume == null)
            {
                sb.AppendLine("✘ 前置：feature.froxelVolume 为 null —— "
                            + "体积雾 compute 缺失（VistaRuntimeResources.volumetricFogCS），"
                            + "近层整条路径不存在，本工具无从探起。");
                return false;
            }

            if (!volume.isValid)
            {
                sb.AppendLine("✘ 前置：froxelVolume.isValid 为 false —— 11 个核里有编译不出来的。"
                            + "先看 Console 里 VolumetricFog.compute 的报错。");
                return false;
            }

            var litShader = Shader.Find("Vista/Lit");
            if (litShader == null)
            {
                sb.AppendLine("✘ 前置：找不到 Vista/Lit。布景需要一个带 ShadowCaster pass 的材质。");
                return false;
            }

            int layer = FindUnusedLayer();
            if (layer < 0)
            {
                sb.AppendLine("✘ 前置：32 个 layer 全都有物体在用，布景无法与当前场景隔离。"
                            + "腾出任意一个空 layer 再跑。\n"
                            + "  ⓘ 隔离不是洁癖：相机 cullingMask 只放行本布景的 layer，"
                            + "阴影投射物也是按同一套剔除收集的 —— 于是 atlas 里的内容"
                            + "可以确定地归因给**这个布景**，而不是当前打开的场景。");
                return false;
            }

            var fog = feature.fog;
            var vf  = feature.volumetricFog;

            // 要改的全部状态。列表住在 rig 里，与判据共用同一份 ——
            // 两边各抄一份的症状是某一份漏还一项，而那一项会污染**下一个**工具的读数。
            var state = RigState.Capture(fog, vf);

            RenderTexture rt = null;
            GameObject root  = null;
            Material mat     = null;
            bool complete    = true;

            try
            {
                state.Apply();

                // ARGBFloat 而不是 ARGBHalf：观测四要在 Weber 1% 的量级上量差，
                // 而 half 的相对精度约 1e-3 —— 只差一个数量级。
                // 量化噪声与被量的量同量级时，尺子会把自己的抖动报成信号
                // （这正是本项目记过的「尺子的地板与被测量同量级」那条）。
                // 三项探针观测不读像素，不受这个改动影响。
                rt = new RenderTexture(k_RtSize, k_RtSize, 24,
                                       RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);

                Build(litShader, layer, feature.groundLevelWorldY, rt,
                      out root, out Camera cam, out mat, out Light sun);

                // 接管主光。理由见类头注：不接管的话探到的是当前场景那盏灯的影子。
                RenderSettings.sun = sun;

                for (int i = 0; i < k_WarmupFrames; i++)
                    cam.Render();

                complete &= ReportVolume(sb, volume, cam);
                sb.AppendLine();
                complete &= ReportShadow(sb, volume, cam, sun);
                sb.AppendLine();
                complete &= ReportScreen(sb, fog, cam, rt);
            }
            finally
            {
                state.Restore();

                if (root != null) Object.DestroyImmediate(root);
                if (mat  != null) Object.DestroyImmediate(mat);
                if (rt   != null) { rt.Release(); Object.DestroyImmediate(rt); }
            }

            return complete;
        }

        // ==================================================================== 观测一

        /// <summary>
        /// 观测一：自己搭的相机能不能拿到一个分配好的近层体。
        ///
        /// 会失败的具体机制是 <c>VistaAtmospherePass</c> 里那道相机类型闸门
        /// （<c>cameraType == Game || SceneView</c>）。代码 new 出来的 Camera 默认
        /// <c>cameraType == Game</c>，所以**应该**通过 —— 但「应该」不是读数，
        /// 而这一条不成立的话后面两项一个都测不了。
        /// </summary>
        static bool ReportVolume(StringBuilder sb, VistaFroxelVolume volume, Camera cam)
        {
            sb.AppendLine("① 自建相机能不能拿到近层体？");
            sb.AppendLine($"  cameraType = {cam.cameraType}（闸门放行 Game / SceneView）");
            sb.AppendLine($"  isAllocated = {volume.isAllocated}");

            var desc = volume.allocatedDesc;
            if (!desc.HasValue)
            {
                sb.AppendLine("  ⇒ **取不到**：allocatedDesc 为 null，近层体一次都没分配下来。");
                sb.AppendLine("     嫌疑顺序：enableInjection 没生效 / 相机类型闸门 / Prepare 失败。");
                return false;
            }

            var d = desc.Value;
            float shadowDistance = ResolveShadowDistance();
            sb.AppendLine($"  口径：{d.width}×{d.height}×{d.depth}，"
                        + $"near {d.nearMeters:F2} m，far {d.farMeters:F1} m，"
                        + $"接手点 {d.handoffMeters:F1} m");
            sb.AppendLine($"  URP shadow distance = {shadowDistance:F1} m"
                        + "（近层远边界被夹到它；布景几何必须在这个范围内）");
            sb.AppendLine($"  遮挡物在 z = {k_OccluderZ:F0} m，"
                        + (k_OccluderZ < d.handoffMeters
                            ? "**在**近层覆盖范围内 ✔"
                            : "**超出**近层覆盖范围 —— 探针采不到它的影子，下面的读数会失去意义"));
            sb.AppendLine("  ⇒ 取到了。");
            return k_OccluderZ < d.handoffMeters;
        }

        // ==================================================================== 观测二 + 三

        /// <summary>
        /// 观测二：布景的投影物有没有把内容写进 URP 的主光 shadow atlas。
        /// 观测三：<c>VistaFroxelSunShadow</c>（被测函数自己那一份）在这个布景上
        ///         读出来的值是不是真的低于 1。
        ///
        /// 两项写在一起，是因为它们读的是**同一趟探针**的同一批槽位，
        /// 而且二不成立时三的读数没有归因价值 —— 报表必须让这个依赖看得出来。
        /// </summary>
        static bool ReportShadow(StringBuilder sb, VistaFroxelVolume volume, Camera cam, Light sun)
        {
            sb.AppendLine("② 自建布景的投影物有没有进主光 shadow atlas？");

            volume.probeRequested = true;
            cam.Render();

            var buffer = volume.shadowProbeBuffer;
            if (buffer == null)
            {
                sb.AppendLine("  ⇒ **取不到**：shadowProbeBuffer 仍为 null，探针 pass 一次都没记录过。");
                return false;
            }

            if (buffer.count != VistaVolumetricFogState.k_SlotTotal)
            {
                sb.AppendLine($"  ⇒ **取不到**：槽位容量 {buffer.count} ≠ "
                            + $"{VistaVolumetricFogState.k_SlotTotal}。越界 UAV 写在 D3D11 上是"
                            + "静默丢弃，读数不可信。");
                return false;
            }

            var raw = new uint[VistaVolumetricFogState.k_SlotTotal];
            buffer.GetData(raw);

            uint flags = raw[VistaVolumetricFogState.k_SlotFlags];
            if ((flags & VistaVolumetricFogState.k_FlagRan) == 0u)
            {
                sb.AppendLine("  ⇒ **取不到**：RAN 位为 0，探针核这一帧压根没派发。");
                sb.AppendLine("     （flags == 0 时「从未派发」与「关键字全关 + 阴影图未绑」"
                            + "长得一模一样，RAN 位就是为了分开这两者。本次是前者。）");
                return false;
            }

            bool hasShadowmap = (flags & VistaVolumetricFogState.k_FlagShadowmap) != 0u;
            bool hasCascade   = (flags & VistaVolumetricFogState.k_FlagCascade)   != 0u;
            sb.AppendLine($"  flags = 0x{flags:X}："
                        + $"阴影图已绑 = {hasShadowmap}，级联 = {hasCascade}");
            sb.AppendLine($"  灯：type = {sun.type}, shadows = {sun.shadows}, "
                        + $"仰角 = {k_SunAltitudeDeg:F0}°；相机 renderShadows = "
                        + $"{cam.GetComponent<UniversalAdditionalCameraData>()?.renderShadows}");

            if (!hasShadowmap)
            {
                sb.AppendLine("  ⇒ **没有阴影图**：主光阴影这一帧根本没渲。");
                sb.AppendLine("     这直接判掉判据 β —— 在这种布景上它没有分辨力，"
                            + "必须写成「不可判定」而不是「通过」。");
                return false;
            }

            uint smW = raw[VistaVolumetricFogState.k_SlotSmWidth];
            uint smH = raw[VistaVolumetricFogState.k_SlotSmHeight];
            sb.AppendLine($"  atlas 尺寸（问纹理本身，不读 _MainLightShadowmapSize.z）：{smW}×{smH}");
            sb.AppendLine($"  ⓘ 顺带：_MainLightShadowmapSize.z 读数 = "
                        + $"{raw[VistaVolumetricFogState.k_SlotUrpSizeZ] / 1f:F0} —— "
                        + "硬阴影档它不被上传，读到的是别人留下的陈旧值，所以探针不信它。");

            if (smW == 0u || smH == 0u)
            {
                sb.AppendLine("  ⇒ **取不到**：GetDimensions 返回 0，atlas 读取那一段被跳过了。");
                return false;
            }

            bool minIsInit = raw[VistaVolumetricFogState.k_SlotShadowmapMin] == uint.MaxValue;
            float smMin = minIsInit ? float.NaN
                : raw[VistaVolumetricFogState.k_SlotShadowmapMin] / VistaVolumetricFogState.k_ShadowScale;
            float smMax = raw[VistaVolumetricFogState.k_SlotShadowmapMax] / VistaVolumetricFogState.k_ShadowScale;
            float spread = minIsInit ? 0f : smMax - smMin;
            bool atlasHasContent = !minIsInit && spread > k_AtlasSpreadGate;

            sb.AppendLine($"  atlas 深度：min {Fmt(smMin)} / max {Fmt(smMax)} ⇒ 跨度 {Sci(spread)}"
                        + $"（门 > {Sci(k_AtlasSpreadGate)}）");
            sb.AppendLine("  ⓘ 判的是「atlas 是不是一个常数」，不是「min 等于某个清空值」——"
                        + "清空值取决于 UNITY_REVERSED_Z，按约定写死会在空 atlas 上假通过。");
            sb.AppendLine(atlasHasContent
                ? "  ⇒ **有内容**：代码搭的投影物确实进了 atlas。"
                : "  ⇒ **是常数**：atlas 空的，布景的投影物没被收集进去。");

            // ---------------------------------------------------------------- 观测三
            sb.AppendLine();
            sb.AppendLine("③ VistaFroxelSunShadow 在这个布景上读到的是什么？");

            uint count    = raw[VistaVolumetricFogState.k_SlotCount];
            uint shadowed = raw[VistaVolumetricFogState.k_SlotShadowedCount];
            float strength = raw[VistaVolumetricFogState.k_SlotShadowStrength]
                           / VistaVolumetricFogState.k_ShadowScale;
            float floorVal = 1f - strength;
            bool shadowMinIsInit = raw[VistaVolumetricFogState.k_SlotShadowMin] == uint.MaxValue;
            float shadowMin = shadowMinIsInit ? float.NaN
                : raw[VistaVolumetricFogState.k_SlotShadowMin] / VistaVolumetricFogState.k_ShadowScale;
            float shadowMax = raw[VistaVolumetricFogState.k_SlotShadowMax]
                            / VistaVolumetricFogState.k_ShadowScale;

            sb.AppendLine($"  探针网格走过 {count} / {VistaVolumetricFogState.k_ProbeCountExpected} 格");
            sb.AppendLine($"  阴影值：min {Fmt(shadowMin)} / max {Fmt(shadowMax)}");
            sb.AppendLine($"  阴影强度 = {Fmt(strength)} ⇒ 全遮地板 1 − strength = {Fmt(floorVal)}"
                        + "（SampleShadowmap 返回 lerp(1, s, strength)，所以地板不是 0）");

            float ratio = count > 0u ? (float)shadowed / count : 0f;
            sb.AppendLine($"  被遮点（shadow < 0.999）：{shadowed} 格，占 {ratio * 100f:F1}%");

            if (shadowed == 0u)
            {
                sb.AppendLine("  ⇒ **一个点都没被遮**。归因（三条互斥）：");
                sb.AppendLine(atlasHasContent
                    ? "     (a) atlas 有内容 ⇒ 不是布景问题；"
                    : "     (a) atlas 是常数 ⇒ **布景问题**，投影物没进去；");
                sb.AppendLine(strength > 1e-6f
                    ? $"     (b) 强度 {Fmt(strength)} ≠ 0 ⇒ 不是强度问题；"
                    : $"     (b) 强度 {Fmt(strength)} == 0 ⇒ SampleShadowmap 恒返回 1，与代码无关；");
                sb.AppendLine("     (c) 若 (a)(b) 都排除了，剩下的才是本模块的 bug（矩阵 / 级联下标）。");
                sb.AppendLine("     无论哪一条，判据 β 在**当前这个布景**上都没有分辨力。");
                return false;
            }

            float camDriftMm = raw[VistaVolumetricFogState.k_SlotCamDriftMm] / 1000f;
            sb.AppendLine($"  ⓘ 相机漂移 = {camDriftMm:F3} mm"
                        + "（_WorldSpaceCameraPos 与 _VistaFroxelCameraWS 的距离；"
                        + "非零意味着 GetMainLightShadowFade 的淡出因子算在了另一个点上）");

            sb.AppendLine($"  ⇒ **读到了阴影**：{ratio * 100f:F1}% 的探针点被遮，"
                        + $"最暗到 {Fmt(shadowMin)}"
                        + (shadowMinIsInit ? "" : $"（距地板 {(shadowMin - floorVal):+0.000000;-0.000000}）")
                        + "。判据 β 在这个布景上**有**分辨力。");

            // 判据 β 要的是「够多的像素能分辨」，不是「有一个点被遮」。
            // 这个比例会直接决定 β 的分位数阈值摆在哪 —— 所以它是读数，不是脚注。
            sb.AppendLine($"  ⓘ 这个百分比是**体**的比例（32×32×16 探针格），不是屏幕像素的比例："
                        + "探针不做深度测试，被遮的格子可能整个藏在遮挡物背面、一个像素都没进画面。"
                        + "判据 β 要的那个数得从合成后的图上量 —— 这里只用它判「有没有被遮的格子」。");
            sb.AppendLine("     （上一版布景的太阳方位写反过一次，读数就是 5.9%："
                        + "非零、看起来正常，而那些格子全在板背面。"
                        + "一个非零的比例回答不了「被遮的雾在不在画面里」。）");
            return true;
        }

        // ==================================================================== 观测四

        /// <summary>
        /// 观测四：把观测三那个**体**的比例落到**屏幕**上 —— Froxel 档与 AP 档的
        /// 逐像素亮度差有多大，以及这个布景上尺子自己的噪声地板有多高。
        ///
        /// ── 为什么必须有这一项 ──
        ///
        /// 观测三给出 75.1% 的被遮格子，但探针网格**不做深度测试**：被遮的格子
        /// 完全可能整个藏在遮挡物背面，一个像素都进不了画面。上一版布景的 5.9%
        /// 就是这样一个「非零但没用」的读数。判据 β 判的是**画面上**被遮的雾更暗，
        /// 所以「有没有分辨力」这个问题只能在合成后的像素上回答。
        ///
        /// ── 为什么量 Froxel − AP 的差，而不是两档各自的绝对亮度 ──
        ///
        /// 缝里那一柱的光程比板前那一段长得多（透过缝能看到远处地面，板挡住的地方
        /// 只有 40 m 空气）。比较两档的**绝对**亮度会把「光程不同」读成「阴影」。
        /// 而两档看的是同一份深度、同一份几何，光程是共有的 —— 作差之后它基本抵消，
        /// 剩下的才是近层 froxel 相对 AP 多做的那些事（逐 froxel 阴影查询是其中最大的一项）。
        ///
        /// ── 为什么同时量噪声地板 ──
        ///
        /// 近层有重投影与抖动，两帧之间本来就不一样。只报「差了多少」而不报
        /// 「什么都不改时差多少」，那个差的对照组身份是假的。所以取三张图：
        /// AP、Froxel、以及**再一帧的** Froxel。后两张之间的差就是地板。
        ///
        /// 这一项仍然是**观测**：它不判通过失败，它给判据 β 定分位数与阈值。
        /// </summary>
        static bool ReportScreen(StringBuilder sb, VistaFogSettings fog, Camera cam, RenderTexture rt)
        {
            sb.AppendLine("④ 画面上：Froxel 档与 AP 档差多少，尺子自己的地板多高？");

            int w = rt.width, h = rt.height, n = w * h;
            var tex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            float[] ap, fx1, fx2;
            try
            {
                fog.mode = VistaFogSettings.Mode.AerialPerspective;
                ap = CaptureLuminance(cam, rt, tex, k_SettleFrames);

                fog.mode = VistaFogSettings.Mode.Froxel;
                fx1 = CaptureLuminance(cam, rt, tex, k_SettleFrames);

                // 什么都不改，只再渲一帧 —— 这一张与上一张的差就是地板。
                fx2 = CaptureLuminance(cam, rt, tex, 1);
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }

            // 分母：AP 档亮度，配一个按本布景中位数取比例的下限（理由见常数注释）。
            var sorted = (float[])ap.Clone();
            System.Array.Sort(sorted);
            float median = sorted[n / 2];
            float floor  = Mathf.Max(median * k_DenomFloorFraction, 1e-12f);

            sb.AppendLine($"  取图：{w}×{h}，换档后各空转 {k_SettleFrames} 帧；"
                        + $"AP 档亮度中位数 {Sci(median)}，分母下限 {Sci(floor)}"
                        + $"（= 中位数 × {Sci(k_DenomFloorFraction)}）");

            var signed = new float[n];
            var absSig = new float[n];
            var absNoi = new float[n];
            int overWeber = 0;
            int touchedAll = 0;
            for (int i = 0; i < n; i++)
            {
                float d = 1f / Mathf.Max(ap[i], floor);
                signed[i] = (fx1[i] - ap[i]) * d;
                absSig[i] = Mathf.Abs(signed[i]);
                absNoi[i] = Mathf.Abs(fx2[i] - fx1[i]) * d;
                if (absSig[i] > k_WeberThreshold) overWeber++;
                if (signed[i] != 0f) touchedAll++;
            }

            sb.AppendLine($"  近层参与的像素（两档不逐位相同）：{touchedAll} / {n}"
                        + $" = {(float)touchedAll / n:P1}"
                        + " —— 其余是天空与超出接手点的远景，两档走同一条 AP 路径");
            sb.AppendLine("  |Froxel − AP| / AP 的分位数（全屏）：");
            sb.AppendLine("    " + Percentiles(absSig));
            sb.AppendLine("  地板 |Froxel − Froxel'| / AP 的分位数（同一档、只差一帧）：");
            sb.AppendLine("    " + Percentiles(absNoi));
            sb.AppendLine($"  超过 Weber {k_WeberThreshold:P0} 的像素：{overWeber} / {n}"
                        + $" = {(float)overWeber / n:P1}");

            float p50Sig = Percentile(absSig, 0.50f);
            float p50Noi = Percentile(absNoi, 0.50f);
            float snr = p50Noi > 0f ? p50Sig / p50Noi : float.PositiveInfinity;
            sb.AppendLine($"  ⇒ 信噪比（两个 p50 之比）= {(float.IsInfinity(snr) ? "∞" : snr.ToString("F1"))}×");

            // 分区：缝里（亮柱）与板后（阴影）各取一块，看**同一张图内**的符号对不对。
            // 这是判据 β 的形状本身 —— 跨图归一化一概不需要。
            //
            // 每块都报两个中位数：全部像素、以及**近层真的参与了的**像素。
            // 后者靠「两档逐位相同」来识别：差恰好为 0 只可能出现在近层没参与的地方
            // （天空、以及深度超过接手点的远景，那些像素两档走的是同一条 AP 路径）。
            // 不把这个排除藏起来，是因为它第一次就改掉了结论 ——
            // 相机水平看出去，画面上半几乎全是天空，于是「缝里」那一栏的全像素中位数
            // 恰好是 0，量的是天空而不是被照亮的雾柱。
            // 「一个非零（或恰好为零）的数回答不了它是从哪儿来的」，所以两个都报，
            // 并且把排除掉多少也报出来。
            var gap  = RegionStats(signed, w, h, 0f, k_GapHalfWidthU);
            var side = RegionStats(signed, w, h, k_SideInnerU, k_SideOuterU);
            sb.AppendLine($"  图内分区（有符号，负 = Froxel 更暗）：");
            sb.AppendLine($"    缝里   |u−0.5| < {k_GapHalfWidthU:F2}"
                        + $"        全部 {Signed(gap.medianAll)}"
                        + $"   近层参与 {Signed(gap.medianTouched)}"
                        + $"（{gap.touched}/{gap.total} = {(float)gap.touched / gap.total:P1}）");
            sb.AppendLine($"    板后   {k_SideInnerU:F2} < |u−0.5| < {k_SideOuterU:F2}"
                        + $"   全部 {Signed(side.medianAll)}"
                        + $"   近层参与 {Signed(side.medianTouched)}"
                        + $"（{side.touched}/{side.total} = {(float)side.touched / side.total:P1}）");
            sb.AppendLine($"    差（板后 − 缝里，只看近层参与的像素）= "
                        + $"{Signed(side.medianTouched - gap.medianTouched)}"
                        + "（判据 β 要的就是这个量为负：被遮的那块比亮柱更暗）");

            sb.AppendLine("  ⓘ 这一项**不判**通过失败：它是给判据 β 定分位数与阈值用的标定，"
                        + "而不是判据本身。判据要跑在真实布景上、并且要把差分解成"
                        + "阴影 / 局部灯 / 逐 froxel 相位三项（γ）。");
            sb.AppendLine("  ⓘ 空转只有 12 帧、不到一个 historyTimeConstant，所以地板偏高。"
                        + "偏高的地板让「有分辨力」更难成立，是安全的那一侧；"
                        + "真正的收敛性归第 2 项的判据⑯。");
            return true;
        }

    }
}
