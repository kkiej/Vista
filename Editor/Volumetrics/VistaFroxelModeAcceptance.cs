using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using static Vista.EditorTools.VistaFroxelShadowRig;

namespace Vista.EditorTools
{
    /// <summary>
    /// #27 验收第 1 项：**Froxel 档 vs AP-only 对照**。
    ///
    /// ── 这一项要回答什么 ──
    /// Froxel 档（<c>VistaFogSettings.Mode.Froxel</c>）相对 AP 档多出来的唯一东西，
    /// 是「近层 froxel 体 + 逐 froxel 的主光阴影查询」。把它打开之后，画面上必须
    /// 真的多出**光柱**，而不只是多出「一点不一样」。
    ///
    /// ── 为什么不能只判「两档不一样」──
    /// 探路读数（Window/Vista/Scout Runtime Froxel Scene）已经量过：缝里与板后
    /// 两块区域之间有一个约 −0.29 的**共有项**，那是近层与 AP LUT 对「这段路上有多少雾」
    /// 本身的分歧，与阴影毫无关系，而它是阴影项（−0.107）的 2.7 倍。
    /// 一条判「整幅图两档不一样」的门，在一个把阴影查询整个删掉的构建上照样全绿 ——
    /// 「一个红不起来的红测，长得和一个通过了的红测一模一样」。
    ///
    /// 所以这里的三条门是**分解**出来的，每一条都能被一处具体的改动单独打红：
    ///
    ///   α 远场复现（2 操作数）
    ///       ① 把近层远边界 D 缩到 5 m 之后，整幅图必须收敛回 AP-only；
    ///       ② 但 D = 150 m 时近层必须是活的。
    ///     ① 单独存在会被「近层压根没生效」通过 —— 那时两档处处相同，收敛得完美。
    ///     ② 把这条后路堵上。这一对判的是**分层接手**：近层只该在 [near, D] 上说话。
    ///
    ///   γ 阴影幅度（1 操作数 + 噪声地板）
    ///       同一档、同一套几何，只把逐 froxel 阴影查询关掉
    ///       （<c>VistaFroxelVolume.s_DebugForceNoSunShadow</c>），
    ///       板后那块区域必须因为「开了阴影」而**变暗** ≥ 1%。
    ///     这是光柱的定义：被遮住的那段空气里没有 in-scatter。
    ///
    ///   β 位置相关（1 操作数 + 噪声地板）
    ///       同一个差（阴影开 − 阴影关）在缝里与板后必须**不一样**，板后更暗。
    ///     γ 单独存在会被「阴影查询恒返回某个小于 1 的常数」通过 —— 那是一个整体偏移，
    ///     不是光柱。β 判的正是「这个变暗随位置变化」。
    ///
    /// γ 与 β 一起，把「有阴影查询」与「阴影查询按位置给出不同答案」分成两个
    /// 能单独红的格子。三条门量的**都是差分**（阴影开 − 阴影关），
    /// 于是那个 −0.29 的共有项在两张图里完全一样、相减时精确抵消 ——
    /// 这正是判据敢把门摆在 1% 的前提。
    ///
    /// ── 布景 ──
    /// 用户选的是「运行时搭专用布景」：Vista 包里没有任何 .unity 资产，判据不能依赖
    /// 工程侧某个场景的摆法（那等于让「打开哪个场景」决定判据的结论）。
    /// 布景与探路工具共用同一份（<see cref="VistaFroxelShadowRig"/>）——
    /// 下面每一条门的阈值都是照探路在**那套**布景上的读数定的，两边各搭一套的话，
    /// 阈值担保的就不再是同一个场景。
    ///
    /// ── 五次渲染 ──
    ///   AP-only @D=150 · Froxel @D=150 · Froxel @D=150 再来一次（噪声地板）
    ///   · Froxel @D=150 关阴影 · Froxel @D=5
    /// 「再来一次」那一张不是冗余：只报「差了多少」而不报「什么都不改时差多少」，
    /// 那个差的对照组身份是假的。近层有时间重投影与抖动，它自己就会动。
    /// </summary>
    static class VistaFroxelModeAcceptance
    {
        // ================================================================ 门槛
        //
        // 三个数**先写下来再去量**。它们的出处全部是探路工具在同一套布景上的读数，
        // 每一条下面都写明了那个读数是多少、余量是几倍 —— 一个没有出处的阈值，
        // 在第一次红掉的时候没有人能判断该改代码还是该改阈值。

        /// <summary>
        /// α①：缩到这个远边界。5 m 是 150 m 的 1/30。
        ///
        /// 不取更小（比如 1 m）：<c>farDistanceMeters</c> 上有 <c>[Min(1f)]</c>，
        /// 贴着下界取值会让「被钳住了」与「就是这么小」无法区分。
        /// 也不取更大：D 越大，近层在前 D 米上的贡献越像一个真实信号，
        /// 而 α① 要的是「近层几乎不说话」这个极限。
        /// </summary>
        const float k_AlphaSmallFarM = 5f;

        /// <summary>
        /// α①：缩 D 之后，与 AP-only 的差必须至少缩小这个倍数。
        ///
        /// ── 出处（第二版，第一版的推导是错的，见末段）──
        /// 近层在 [near, D] 上的贡献正比于它**在光学意义上**覆盖了多少路径，
        /// 也就是 1 − e^(−D/mfp)。本布景 mfp = 120 m：
        ///   D = 150 m ⇒ 1 − e^(−1.25)   = 0.7135
        ///   D = 5 m   ⇒ 1 − e^(−0.0417) = 0.0408
        ///   ⇒ 预期缩小 0.7135 / 0.0408 ≈ 17.5×
        /// 实测 11.8×（收敛后连跑三次 11.4 / 11.9 / 11.8）。比预期小 1.5 倍，
        /// 因为 p99 是一个**极值统计**、不是线性泛函，而且 D = 5 时交接带
        /// 在这 5 m 里占的比例远大于 D = 150 时 —— 两者都让小 D 一侧的残差偏高。
        /// 门取 10×：低于实测 11.8 留 15% 余量，也明显低于理论 17.5 ——
        /// 这条门要抓的是「分层接手整个错位」（近层越过 D 继续说话，
        /// 或者根本没在 D 上交接）那一类失效，不是去卡一个比例系数。
        ///
        /// ── 第一版错在哪 ──
        /// 原文写的是「D = 5 覆盖 150 m 的 3.3%，薄雾区光学厚度近似线性，
        /// 于是差应落到百分之几，也就是 20~30 倍」。那是拿**长度比**当**贡献比**，
        /// 而且把线性外推用在了一个**相对量**（|Δ|/AP）上 ——
        /// 分母 AP 在本布景里横跨 60 倍，分子却是一段被照亮的雾的 in-scatter，
        /// 两者没有固定比例。归因表（ReportByBrightness）里那条错误的
        /// 「4% 物理上界」是同一个错的另一面，一并改掉了。
        /// </summary>
        const float k_AlphaShrinkFactor = 10f;

        /// <summary>
        /// α②：D = 150 时，两档的差必须超过噪声地板这个倍数，否则「近层是活的」不成立。
        /// 探路读数：信号 p50 = 3.73e-01，地板 p90 = 1.564e-02 ⇒ 实测 24 倍。取 10 倍。
        /// </summary>
        const float k_AlphaAliveFactor = 10f;

        /// <summary>
        /// γ / β 的幅度门，1% = Weber 阈，全项目统一的「肉眼可见」口径
        /// （<see cref="k_WeberThreshold"/> 是同一个数；这里单独起名，是因为
        /// 它在这里的身份是**门**而不是一个参考值 —— 改门与改参考值是两件事）。
        ///
        /// 探路量到的位置相关项是 −1.065e-01，也就是 10 倍余量。
        /// 不把门提到 5% 去贴着实测值：那会让门随布景的雾浓度漂移，
        /// 而 1% 有一个与布景无关的理由 —— 低于它的差人眼看不见，
        /// 也就谈不上「画面上多出了光柱」。
        /// </summary>
        const float k_AmplitudeGate = 0.01f;

        /// <summary>
        /// 噪声地板必须低于幅度门的这个分数，否则上面两条门量到的是尺子自己在抖。
        /// 3 倍是「尺子与被测量至少差半个数量级」的最低要求 ——
        /// 「尺子的地板与被测量同量级时，尺子会自己伪造一个结论」。
        /// 探路实测地板在区域中位数这个统计量上约 1e-4，即 100 倍余量。
        /// </summary>
        const float k_NoiseHeadroom = 3f;

        /// <summary>
        /// 本判据采图前空转的帧数。**不**复用 <see cref="k_SettleFrames"/>（= 12）。
        ///
        /// ── 为什么必须自己一个数 ──
        /// rig 里那 12 帧是给探路工具定的，它的注释写明了是「**故意**没取到完全收敛」：
        /// 探路只报数，一个偏高的噪声地板只会让结论更难成立，是安全的那一侧。
        /// 判据不是。α① 比的是两张图的 p99，而其中 D = 5 那张是在
        /// <c>farDistanceMeters</c> 刚改过、**重投影历史刚被作废**之后拍的 ——
        /// 12 帧不到一个时间常数，那张图带着多少上一状态的残留，
        /// 取决于上一状态是什么。
        ///
        /// 实测就是这个症状：同一份代码连跑五次，D = 150 一侧五次全稳
        /// （p99 0.9799~0.9803），D = 5 一侧**只有刚编译完那一次**是 1.163e-01（⇒ 8.4×），
        /// 之后四次全是 9.54e-02（⇒ 10.3×）。原因是 <c>feature.froxelVolume</c>
        /// 跨菜单运行存活（每次只重建布景、不重建体），于是第 N 次开场时
        /// 体里装的是第 N−1 次**结尾那张 D = 5 的历史**——热跑那个 10.3×
        /// 有一部分是上次跑剩下的状态喂进来的。
        /// 门摆在 10 ⇒ 这条门当时的真实行为是「刚编译完必红，之后必绿」。
        /// 一条由「你有没有刚重编译过」决定颜色的门，不是这个改动的回归护栏。
        ///
        /// ── 这个数怎么来的 ──
        /// historyTimeConstant = 0.33 s，按 60 fps 约 20 帧一个常数。
        /// 64 帧 ≈ 3.2 个常数 ⇒ 残留 e^(−3.2) ≈ 4%，再被后续帧继续压。
        /// 取 3 个常数以上是为了让「初始状态是什么」不再进入读数 ——
        /// 收敛之后的图不关心它从哪儿出发，那才是一条门能引用的读数。
        /// 代价：5 次采图 ⇒ 约 320 帧，整跑十几秒，对一条手动触发的判据可以接受。
        /// </summary>
        const int k_AcceptSettleFrames = 64;

        [MenuItem("Window/Vista/Acceptance: Froxel Mode vs AP-only", priority = 136)]
        static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== #27 验收第 1 项：Froxel 档 vs AP-only ===");
            sb.AppendLine();

            bool pass = Acceptance(sb);

            sb.AppendLine();
            sb.AppendLine(pass
                ? "结论：**通过**。近层只在 [near, D] 上说话；开阴影让被遮处变暗；"
                + "而且这个变暗随位置变化 —— 三条各自能被一处改动单独打红。"
                : "结论：**未通过**（或不可判定）。逐条看上面的 ✘ / ⓘ。");

            if (pass) Debug.Log(sb.ToString());
            else      Debug.LogError(sb.ToString());
        }

        static bool Acceptance(StringBuilder sb)
        {
            var feature = VistaAtmosphereFeature.current;
            if (feature == null)
            {
                sb.AppendLine("✘ 前置：VistaAtmosphereFeature.current 为 null —— "
                            + "feature 没装进当前 Renderer，或还没有相机渲过一帧。");
                return false;
            }

            var volume = feature.froxelVolume;
            if (volume == null || !volume.isValid)
            {
                sb.AppendLine("✘ 前置：近层体不可用（compute 缺失或有核编译不出来）。"
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
                sb.AppendLine("✘ 前置：32 个 layer 全都有物体在用，布景无法与当前场景隔离。");
                return false;
            }

            var fog = feature.fog;
            var vf  = feature.volumetricFog;
            if (fog == null || vf == null)
            {
                sb.AppendLine("✘ 前置：feature 上的 fog / volumetricFog 设置为 null。");
                return false;
            }

            var state = RigState.Capture(fog, vf);
            // 远边界与归因开关由本判据自己存还：rig 的 RigState 只托管两个消费者
            // **共用**的那几项，探路根本不碰这两样。理由写在 RigState 的头注里。
            float prevFar = vf.farDistanceMeters;
            bool  prevForceNoShadow = VistaFroxelVolume.s_DebugForceNoSunShadow;

            RenderTexture rt = null;
            GameObject root  = null;
            Material mat     = null;
            Texture2D tex    = null;
            bool ok = true;

            try
            {
                state.Apply();
                VistaFroxelVolume.s_DebugForceNoSunShadow = false;

                // ARGBFloat：要在 Weber 1% 的量级上量差，而 half 的相对精度约 1e-3，
                // 只差一个数量级 —— 量化噪声会被报成信号。与探路同一条理由、同一个选择。
                rt = new RenderTexture(k_RtSize, k_RtSize, 24,
                                       RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);

                Build(litShader, layer, feature.groundLevelWorldY, rt,
                      out root, out Camera cam, out mat, out Light sun);
                RenderSettings.sun = sun;

                for (int i = 0; i < k_WarmupFrames; i++)
                    cam.Render();

                // 布景自检不通过就停：半套布景量出来的数字比没数字更危险，
                // 而这三条正是探路工具上一版栽过的那两个跟头的守门人。
                if (!VerifyRig(sb, volume, cam))
                    return false;

                tex = new Texture2D(k_RtSize, k_RtSize, TextureFormat.RGBAFloat, false, true);

                // ================================================ 五次渲染
                vf.farDistanceMeters = prevFar;

                fog.mode = VistaFogSettings.Mode.AerialPerspective;
                float[] ap = CaptureLuminance(cam, rt, tex, k_AcceptSettleFrames);

                fog.mode = VistaFogSettings.Mode.Froxel;
                float[] fxOn = CaptureLuminance(cam, rt, tex, k_AcceptSettleFrames);

                // 什么都不改，只再渲一帧 —— 这一张与上一张的差就是地板。
                // 这里**故意**只渲 1 帧：地板的定义就是「相邻两帧之间它自己动多少」，
                // 给它也空转 64 帧的话量到的是「收敛之后还剩多少抖」，那是另一个量。
                float[] fxOn2 = CaptureLuminance(cam, rt, tex, 1);

                VistaFroxelVolume.s_DebugForceNoSunShadow = true;
                float[] fxOff = CaptureLuminance(cam, rt, tex, k_AcceptSettleFrames);
                VistaFroxelVolume.s_DebugForceNoSunShadow = false;

                vf.farDistanceMeters = k_AlphaSmallFarM;
                float[] fxSmall = CaptureLuminance(cam, rt, tex, k_AcceptSettleFrames);
                vf.farDistanceMeters = prevFar;

                // ================================================ 统计
                // 分母下限按**本布景 AP 档亮度的中位数**取比例，不写死常数：
                // 整条管线存的是预曝光辐亮度，而曝光由场景里的 VistaTimeOfDay 决定
                // （EV100 5 与 15 差 1024 倍）。一个按某次曝光调出来的绝对下限，
                // 换个场景就要么吃掉全部信号、要么形同虚设。
                float apMedian = Percentile(ap, 0.50f);
                float floor    = Mathf.Max(apMedian * k_DenomFloorFraction, 1e-12f);
                sb.AppendLine($"布景口径：AP 档亮度中位数 {Sci(apMedian)}，"
                            + $"相对差的分母下限 {Sci(floor)}"
                            + $"（= 中位数 × {k_DenomFloorFraction:0.###e+00}）");
                sb.AppendLine();

                float[] relOn    = Relative(fxOn,  ap,    ap, floor);   // 近层 vs AP（D = 150）
                float[] relSmall = Relative(fxSmall, ap,  ap, floor);   // 近层 vs AP（D = 5）
                float[] relNoise = Relative(fxOn2, fxOn,  ap, floor);   // 地板：什么都不改
                float[] relShad  = Relative(fxOn,  fxOff, ap, floor);   // 阴影开 − 阴影关

                ok &= Alpha(sb, relOn, relSmall, relNoise, prevFar);
                ReportByBrightness(sb, fxOn, fxSmall, ap, floor, prevFar);
                sb.AppendLine();
                ok &= GammaBeta(sb, relShad, relNoise);
            }
            finally
            {
                VistaFroxelVolume.s_DebugForceNoSunShadow = prevForceNoShadow;
                vf.farDistanceMeters = prevFar;
                state.Restore();

                if (tex  != null) Object.DestroyImmediate(tex);
                if (root != null) Object.DestroyImmediate(root);
                if (mat  != null) Object.DestroyImmediate(mat);
                if (rt   != null) { rt.Release(); Object.DestroyImmediate(rt); }
            }

            return ok;
        }

        // ==================================================================== 布景自检

        /// <summary>
        /// 三条布景前提。它们**不是**被测对象，但每一条不成立时，下面三条门都会
        /// 给出一个看起来正常的读数 —— 这正是探路工具上一版栽的两个跟头：
        ///   · 太阳方位角符号写反 ⇒ 被遮 5.9%，而那 5.9% 全在板背面、一个像素都没进画面；
        ///   · 分区里一半是天空 ⇒ 中位数恰好 +0，量的是天空不是雾柱。
        /// 两次都不是编译器或任何门发现的。所以这三条前提在这里是**会失败的格子**，
        /// 不是一段注释 ——「一条只写在注释里的恒等式不会自己失败」。
        /// </summary>
        static bool VerifyRig(StringBuilder sb, VistaFroxelVolume volume, Camera cam)
        {
            sb.AppendLine("⓪ 布景前提（不成立时下面三条门会给出看起来正常的假读数）");

            var desc = volume.allocatedDesc;
            if (!desc.HasValue)
            {
                sb.AppendLine("  ✘ 近层体一次都没分配下来（allocatedDesc 为 null）。"
                            + "嫌疑顺序：enableInjection / 相机类型闸门 / Prepare 失败。");
                return false;
            }

            var d = desc.Value;
            bool covered = k_OccluderZ < d.handoffMeters;
            sb.AppendLine($"  {Mark(covered)}遮挡物在 z = {k_OccluderZ:F0} m，"
                        + $"近层接手点 {d.handoffMeters:F1} m"
                        + $"（{d.width}×{d.height}×{d.depth}，far {d.farMeters:F1} m，"
                        + $"URP shadow distance {ResolveShadowDistance():F1} m）");

            // 太阳必须朝相机走，否则影子落在板背面、整个在视野外。
            // 判**向量**而不是判角度：上一版那个 5.9% 的读数就是角度写对了、向量朝反了，
            // 而「描述不会在符号写反时报错」。
            var sun = RenderSettings.sun;
            Vector3 fwd = sun != null ? sun.transform.forward : Vector3.zero;
            bool sunOk = sun != null && fwd.z < -0.5f && fwd.y < -0.1f;
            sb.AppendLine($"  {Mark(sunOk)}太阳 forward = ({fwd.x:F3}, {fwd.y:F3}, {fwd.z:F3})"
                        + "，要求 z < −0.5（朝相机走）且 y < −0.1（从上往下）");

            // 相机拿不拿得到近层体由上面的 allocatedDesc 覆盖；这里再报一次类型，
            // 因为闸门（VistaAtmospherePass.cs 的 froxelEnabled）是按类型开的。
            bool camOk = cam.cameraType == CameraType.Game;
            sb.AppendLine($"  {Mark(camOk)}cameraType = {cam.cameraType}（闸门放行 Game / SceneView）");

            // ---- 宿主场景：只打印，不判 ----
            //
            // 本布景是运行时搭的（自己的相机、自己的遮挡板、自己的太阳，
            // cullingMask 只放自己那一层），所以「宿主场景无关」曾经是个合理假设。
            // 它不成立：同一份代码在 Demo 里通过、在 Demo_Night 里三条门红两条，
            // 而遮挡像素数逐位相同（20910/20992、6552/13312）—— 几何一格没动，
            // 变的只有幅度。最硬的一格是缝内：那儿全程被太阳照到，开不开阴影查询
            // 本该逐位相同，Demo 给 −2.7e-03（对称性要求的值），Demo_Night 给 −3.0。
            //
            // 已逐个 A/B 排除的：anisotropy、luminanceRejectStart、localLightMode、
            // 场景的 EV100 覆盖、以及整份 Vista 代码与整份渲染器资产
            // （两边都检出到八连绿那一刻的版本，照红）。
            //
            // 所以现在**写不出**一条正确的前提 —— 写了就是拍前提。
            // 但「哪个场景开着」必须留在报表上：没有它，两份相反的读数
            // 在报表上长得一模一样，而「一个非零的数回答不了它是从哪儿来的」。
            // 查清之后这一行才该升格成门。
            sb.AppendLine($"  ⓘ 宿主场景 = {SceneManager.GetActiveScene().name}"
                        + "（本项对宿主场景敏感，成因未查清 —— 见 CHANGELOG 的这一条）");

            bool all = covered && sunOk && camOk;
            if (!all)
                sb.AppendLine("  ⇒ 布景不成立，**不往下量**。");
            return all;
        }

        // ==================================================================== α

        static bool Alpha(StringBuilder sb, float[] relOn, float[] relSmall, float[] relNoise,
                          float farFull)
        {
            sb.AppendLine("α 远场复现：近层只该在 [near, D] 上说话");

            float sOn    = Percentile(Abs(relOn),    0.99f);
            float sSmall = Percentile(Abs(relSmall), 0.99f);
            float nP90   = Percentile(Abs(relNoise), 0.90f);
            float onP50  = Percentile(Abs(relOn),    0.50f);

            float shrink = sSmall > 0f ? sOn / sSmall : float.PositiveInfinity;
            bool a1 = shrink >= k_AlphaShrinkFactor;
            sb.AppendLine($"  {Mark(a1)}① D 从 {farFull:F0} m 缩到 {k_AlphaSmallFarM:F0} m 后，"
                        + $"|Δ|/AP 的 p99 从 {Sci(sOn)} 落到 {Sci(sSmall)}"
                        + $" ⇒ 缩小 {Ratio(shrink)}×，门 ≥ {k_AlphaShrinkFactor:F0}×");

            float alive = nP90 > 0f ? onP50 / nP90 : float.PositiveInfinity;
            bool a2 = onP50 > k_AlphaAliveFactor * nP90;
            sb.AppendLine($"  {Mark(a2)}② D = {farFull:F0} m 时近层是活的："
                        + $"信号 p50 {Sci(onP50)} vs 地板 p90 {Sci(nP90)}"
                        + $" ⇒ {Ratio(alive)}×，门 > {k_AlphaAliveFactor:F0}×");
            sb.AppendLine("  ⓘ ② 是 ① 的后路：近层压根没生效时 ① 收敛得完美，"
                        + "而那恰恰是这一项要抓的失效之一。");
            return a1 && a2;
        }

        /// <summary>
        /// α① 的**归因诊断**，只报数、不判。
        ///
        /// ── 为什么需要它 ──
        /// α① 量的是 |Δ|/AP 的 p99。这是一个**相对量的极值统计**，它同时被两件事推高：
        ///   · 分子大 —— 近层与 AP 在这条光路上真的分歧很大（这是门想抓的）；
        ///   · 分母小 —— 像素本身就暗，而分母下限被压到中位数的 1/1000（这不是）。
        /// p99 取的是最大的那 1%，暗像素在这个统计量上有系统性优势，
        /// 于是「整幅图没收敛」与「最暗的 1% 像素相对差很大」在一个数字里分不开。
        /// 本项目已经在 ⑮b 上栽过同一个跟头，当时的结论也是**按亮度分桶量**。
        ///
        /// ── 有一个可以对照的量 ──
        /// 平均自由程 120 m，D = 5 m ⇒ 那一段的光学厚度 τ = 5/120 ≈ 0.042，
        /// 于是这 5 m 贡献的 in-scatter 是 (1 − e^(−τ))·L_s，L_s 是这段路上的散射辐亮度。
        /// 把量到的**绝对**残差除回去就得到 L_s 的实测值：
        ///   L_s = |Δ| / (1 − e^(−τ))
        /// 这个数是可以对照的 —— 它必须落在本布景里真实存在的辐亮度量级上
        /// （报表同时打 AP 档的最大亮度作尺）。若 L_s 冲到比画面上最亮的东西还高几倍，
        /// 那 5 m 里的雾就"发"出了场景里没有的光，是分层接手本身错了。
        ///
        /// 注意**不能**拿 1 − e^(−τ) ≈ 4% 直接当相对差的上界 —— 第一版就是这么写的，
        /// 那是把**透射率差**当成了**辐亮度差**：被太阳照亮的雾其 L_s 可以远高于
        /// 像素自身的亮度，于是一个暗像素的相对差轻易超过 4%，而这完全正常。
        /// 「一个非零（或恰好为零）的数回答不了它是从哪儿来的」——
        /// 除回去得到 L_s，才是把这个数问出出处的那一步。
        ///
        /// 这一版**故意不判**：先看清楚残差长在哪儿，再决定是改统计量还是改代码。
        /// 反过来（先把门从 10 挪到 8 让它变绿）会让这条门以后担保的是
        /// 「这次量到多少」而不是「收敛」——「一个恒真的绿比一个红更贵」。
        /// </summary>
        static void ReportByBrightness(StringBuilder sb, float[] fxOn, float[] fxSmall,
                                       float[] ap, float floor, float farFull)
        {
            const int k_Buckets = 5;

            int n = ap.Length;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            var key = (float[])ap.Clone();
            System.Array.Sort(key, order);          // 两个数组一起按 AP 亮度排序

            float tau     = k_AlphaSmallFarM / k_MeanFreePathM;
            float frac    = 1f - Mathf.Exp(-tau);   // 这 5 m 吃掉的那一份
            float apMax   = key[n - 1];

            sb.AppendLine($"  ⓘ 归因（只报数不判）：把像素按 AP 档亮度分 {k_Buckets} 桶，"
                        + $"每桶各报 D = {farFull:F0} m 与 D = {k_AlphaSmallFarM:F0} m 的残差 p99。");
            sb.AppendLine($"    口径：mfp {k_MeanFreePathM:F0} m、D = {k_AlphaSmallFarM:F0} m"
                        + $" ⇒ τ = {tau:F4}，1 − e^(−τ) = {frac:F4}。"
                        + $"把绝对残差除以它得到这段路上的散射辐亮度 L_s，"
                        + $"它应当落在本布景真实存在的量级上（AP 档最大亮度 {Sci(apMax)}）。");
            sb.AppendLine("    亮度桶     AP 区间                    D=150 rel p99   D=5 rel p99   D=5 |Δ| p99    ⇒ L_s      L_s/APmax");

            for (int b = 0; b < k_Buckets; b++)
            {
                int s = (int)((long)b * n / k_Buckets);
                int e = (int)((long)(b + 1) * n / k_Buckets);
                int m = e - s;
                if (m <= 0) continue;

                var relBig   = new float[m];
                var relSmall = new float[m];
                var absSmall = new float[m];
                for (int k = 0; k < m; k++)
                {
                    int i = order[s + k];
                    float den = Mathf.Max(ap[i], floor);
                    relBig[k]   = Mathf.Abs(fxOn[i]    - ap[i]) / den;
                    absSmall[k] = Mathf.Abs(fxSmall[i] - ap[i]);
                    relSmall[k] = absSmall[k] / den;
                }

                float absP99 = Percentile(absSmall, 0.99f);
                float ls     = absP99 / frac;
                sb.AppendLine($"    {b * 100 / k_Buckets,3}–{(b + 1) * 100 / k_Buckets,3}%"
                            + $"  [{Sci(key[s])}, {Sci(key[e - 1])}]"
                            + $"  {Sci(Percentile(relBig, 0.99f)),12}"
                            + $"  {Sci(Percentile(relSmall, 0.99f)),12}"
                            + $"  {Sci(absP99),12}"
                            + $"  {Sci(ls),10}"
                            + $"  {(apMax > 0f ? (ls / apMax).ToString("F2") : "n/a"),8}");
            }
        }

        // ==================================================================== γ + β

        static bool GammaBeta(StringBuilder sb, float[] relShad, float[] relNoise)
        {
            // relShad = (阴影开 − 阴影关) / AP。两次渲染之间**只有**这一个开关不同，
            // 所以这个差里不含「近层与 AP 对雾量本身的分歧」那个约 −0.29 的共有项 ——
            // 那一项在两张图里完全一样，相减时精确抵消。
            var gap   = RegionStats(relShad,  k_RtSize, k_RtSize, 0f,           k_GapHalfWidthU);
            var side  = RegionStats(relShad,  k_RtSize, k_RtSize, k_SideInnerU, k_SideOuterU);
            var nGap  = RegionStats(relNoise, k_RtSize, k_RtSize, 0f,           k_GapHalfWidthU);
            var nSide = RegionStats(relNoise, k_RtSize, k_RtSize, k_SideInnerU, k_SideOuterU);

            sb.AppendLine("γ 阴影幅度：开阴影查询让被遮处的雾变暗");
            sb.AppendLine($"  板后区（|u−0.5| ∈ [{k_SideInnerU:F2}, {k_SideOuterU:F2}]）"
                        + $"被阴影碰到 {side.touched}/{side.total} = {Pct(side)}，"
                        + $"中位数 {Signed(side.medianTouched)}（全像素 {Signed(side.medianAll)}）");
            sb.AppendLine($"  缝内区（|u−0.5| < {k_GapHalfWidthU:F2}）"
                        + $"被阴影碰到 {gap.touched}/{gap.total} = {Pct(gap)}，"
                        + $"中位数 {Signed(gap.medianTouched)}（全像素 {Signed(gap.medianAll)}）");
            sb.AppendLine("  ⓘ 判据用的是「被碰到的」那一栏。在这张差图里 0 有两个来源："
                        + "近层没覆盖到（天空、超出 D），以及**这条光路全程都被照到**（s ≡ 1，两档相减为 0）。"
                        + "全像素中位数会被前者压成 0 —— 相机水平看出去，画面上半几乎全是天空，"
                        + "那样量到的是天空而不是雾柱，探路第一次就是这么错的。"
                        + "两栏一起打，是因为它们的**比值**就是「这块区域里有多少像素真的被遮了」，"
                        + "而这个比值在 β 红掉时是第一个要看的数。");

            bool g = side.medianTouched <= -k_AmplitudeGate;
            sb.AppendLine($"  {Mark(g)}板后中位数 {Signed(side.medianTouched)} ≤ −{k_AmplitudeGate:0.###}"
                        + "（开阴影必须让它**变暗**，符号本身是判据的一部分）");

            float noiseAmp = Mathf.Max(Mathf.Abs(nSide.medianTouched), Mathf.Abs(nGap.medianTouched));
            bool gn = noiseAmp * k_NoiseHeadroom < k_AmplitudeGate;
            sb.AppendLine($"  {Mark(gn)}地板（同一统计量：什么都不改再渲一帧）"
                        + $" {Sci(noiseAmp)} × {k_NoiseHeadroom:F0} < {k_AmplitudeGate:0.###}");

            sb.AppendLine();
            sb.AppendLine("β 位置相关：这个变暗随位置变化，不是一个整体偏移");
            float beta = side.medianTouched - gap.medianTouched;
            bool b = beta <= -k_AmplitudeGate;
            sb.AppendLine($"  {Mark(b)}板后 − 缝内 = {Signed(beta)} ≤ −{k_AmplitudeGate:0.###}");

            float noiseBeta = Mathf.Abs(nSide.medianTouched - nGap.medianTouched);
            bool bn = noiseBeta * k_NoiseHeadroom < k_AmplitudeGate;
            sb.AppendLine($"  {Mark(bn)}地板（同一统计量）{Sci(noiseBeta)}"
                        + $" × {k_NoiseHeadroom:F0} < {k_AmplitudeGate:0.###}");
            sb.AppendLine("  ⓘ γ 单独存在会被「阴影查询恒返回某个小于 1 的常数」通过 —— "
                        + "那是一个整体偏移，不是光柱。β 正是为这条后路准备的。");

            return g && gn && b && bn;
        }

        // ==================================================================== 小工具

        /// <summary>(a − b) / max(ap, floor)。分母永远是 AP 档，好让三张差图可比。</summary>
        static float[] Relative(float[] a, float[] b, float[] ap, float floor)
        {
            var r = new float[a.Length];
            for (int i = 0; i < a.Length; i++)
                r[i] = (a[i] - b[i]) / Mathf.Max(ap[i], floor);
            return r;
        }

        static float[] Abs(float[] v)
        {
            var r = new float[v.Length];
            for (int i = 0; i < v.Length; i++) r[i] = Mathf.Abs(v[i]);
            return r;
        }

        static string Pct(RegionStat s)
            => s.total > 0 ? ((float)s.touched / s.total).ToString("P1") : "n/a";

        static string Ratio(float v)
            => float.IsInfinity(v) ? "∞" : float.IsNaN(v) ? "n/a" : v.ToString("F1");

        static string Mark(bool ok) => ok ? "✔ " : "✘ ";
    }
}
