using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using static Vista.EditorTools.VistaFroxelShadowRig;

namespace Vista.EditorTools
{
    /// <summary>
    /// 判据⑰：近层 froxel 的**时间滞后（拖影）**，以及亮度死区的判别力。
    ///
    /// ── 它补的是哪个缺口 ──
    /// ⑯c 扫出了 <c>luminanceRejectStart</c> 的**下半条**曲线：下端越高，抖动噪声被
    /// 当成「场景变了」的机会越小，历史权重留得越多，画面越干净（拐点 0.55）。
    /// 当时明写了这条曲线只有一半，另一半量不到：布景是静止的，没有任何真实的
    /// 亮度变化可以拿来看「历史拖了多久」。⑰ 就是那另一半。
    ///
    /// ── 先纠正一个错误的框架 ──
    /// 原先在 CHANGELOG 里给这一项留的说法是「量拖影长度，再和噪声曲线做权衡」。
    /// 那个框架站不住。抬高死区下端 ⇒ 更多变化被吸收 ⇒ 权重留得高 ⇒
    /// **既降噪也加拖影**，两者由同一个 w_eff 决定。如果拖影长度只是 w_eff 的函数，
    /// 两条曲线就是同一件事的两面，取舍退化成「选一个 w_eff」——
    /// 而那件事直接调 <c>historyTimeConstant</c> 就能做，**死区整个机制就白加了**。
    ///
    /// 死区要有存在价值，只能是因为它能**区分**：对小幅抖动保持高权重、
    /// 对大幅真实变化立刻掉权重。所以这一项该量的是**判别力**，不是拖影长度：
    /// <code>
    ///     判别力 = w_eff(纯噪声，无阶跃)  ÷  w_eff(真实阶跃)
    /// </code>
    /// ⑯a 已经量了分子（⑯c 的标定扫描：0.25→0.643、0.55→0.984、关闭→1.021）。
    /// ⑰ 补分母，横轴用**同一组** <c>luminanceRejectStart</c> 取值，两条曲线才能并排读。
    ///
    /// ★ 2026-09-20 这两条曲线夹出了定值：<c>luminanceRejectStart</c> 0.25 → **0.55**。
    ///   完整论证印在报表的 ⑥ 节里（含「0.55 不是调好了而是承认它不工作」那一条），
    ///   不在这里重复 —— 论证要长在**会被运行**的那份文本上，注释不会自己失败。
    ///
    /// ── 阶跃怎么造：改大气的太阳照度 ──
    /// 死区读的是 <c>rel = |lumHist − lumNow| / max(lumHist, lumNow)</c>，一个**相对**量。
    /// 把 <see cref="VistaAtmosphereParameters.sunIlluminanceLux"/> 乘上 (1+a)，
    /// 整条链路（天空、AP、近层注入，以及由它反推出来的平行光强度）同倍放大
    /// ⇒ 阶跃后第一帧 <c>rel₀ = a/(1+a)</c>，**幅度精确可控且与布景无关**。
    ///
    /// **第一版写的是改 <c>Light.intensity</c>，那是错的，而且错得很安静。**
    /// <see cref="VistaSunTransmittance"/> 每帧按
    /// <c>color · intensity = sunIlluminanceLux · T · exposure / π</c>
    /// **覆写**平行光，所以手写进去的 intensity 活不过一帧：六档全部量到
    /// 「阶跃 = 0」，报表只说得出「入选像素 0」。
    /// 这一课直接变成了 g-0b：**实测阶跃幅度必须对得上下发的幅度**。
    /// 「一个非零的数回答不了它是从哪儿来的」有个对偶 —— 一个零也回答不了，
    /// 除非判据自己去量那个本该非零的量。
    ///
    /// 换密度（<c>meanFreePathMeters</c>）也能造阶跃，但它同时改透射率，
    /// 于是「新稳态」不再是旧稳态的一个倍数，r_t 的分母要重新定义；
    /// 换灯的方向会改阴影 ⇒ 改的是几何不是亮度。改照度是唯一一个
    /// 只动被测量那一个自由度的杠杆。
    ///
    /// 已知副作用：改照度会命中 <see cref="VistaAtmosphereParameters.Equals"/>
    /// 的脏检查 ⇒ 下一帧重烘 Transmittance / MultiScattering。
    /// 物理上透射率与照度无关，这一次重烘是白跑的；它的后果是阶跃可能被摊到两帧，
    /// 相对 T50 ≈ 25 帧是 4% 的量级偏差。记在这里，不假装它不存在。
    ///
    /// 关键的安全性：<see cref="VistaFroxelReprojection.Update"/> 的失效清单里
    /// （抖动 Off / 没相机 / 历史没写过 / 上一帧没捕获 / 换相机）**没有任何一条**
    /// 与光照有关，所以阶跃不会顺手把历史打掉。这一条不靠读代码背书——
    /// 每一档都把阶跃后的 <c>framesSinceValid</c> 读出来当门（见 g-0a）：
    /// 衰减若是瞬间完成的，报表必须能分清「死区拒绝了」和「重投影自己失效了」，
    /// 否则两者的修法完全不同却长得一样。
    ///
    /// ── 六个档位，每一档都有预言 ──
    /// 下端 ∈ {0.25 旧值, 0.55 线上（定值后）, 1.00 关闭} × 幅度 a ∈ {0.15, 0.50}：
    ///   · a = 0.15 ⇒ rel₀ = 0.130，**低于三个下端**  ⇒ 死区一次都不该拒
    ///     ⇒ 三档的衰减必须**一模一样**。这是 g-1，一道能红的恒等式门，
    ///       同时是这把尺子的自检：它证明档间的差异确实来自死区，
    ///       而不是来自档间某个没控住的漂移。
    ///       ★ 第七档是其中一档的**原样重跑**：没有它，「极差超了容差」这句话
    ///       同时覆盖「真有变量在漂」与「尺子只有这么细」两种故障，而它们
    ///       一个要查代码、一个要改口径。g-1 因此是三值的，见 ReportIdentity。
    ///   · a = 0.50 ⇒ rel₀ = 0.333，**高于 0.25、低于 0.55**
    ///     ⇒ 0.25 档拒绝（快），0.55 与 1.00 档不拒（慢）。
    ///       g-2 断言这个方向；T50(0.55)/T50(0.25) 就是把下端从 0.25 抬到 0.55
    ///       要付的拖影倍数 —— **用户要的那半条曲线，就是这个比值**。
    ///
    /// ── 衰减不是单指数，所以只报特征帧数不报 τ ──
    /// 阶跃后 rel 随历史追上来而变小；一旦 rel 掉回下端以下，死区就不再拒绝，
    /// 剩下的误差改以**完整的** w_eff（比线上噪声态那个更高）慢慢衰减。
    /// 于是衰减是两段的：先快后慢，而拖影之所以看得见，靠的正是后面那条慢尾。
    /// 拟合一个 τ 会把两段平均成一个既不描述快段也不描述慢段的数。
    /// 报 T50 / T90 / T99（首次跨过阈值的帧号，线性插值）—— 它们对两段结构是诚实的。
    ///
    /// ── 尺子：谁有资格进统计 ──
    /// 逐像素归一化 <c>r_t = (y_t − y_∞) / (y_0 − y_∞)</c>。分母小的像素会把噪声放大
    /// 成任意大的 r，所以要两道筛：
    ///   1. **阶跃看得见**：|y_∞ − y_0| 要高过读回地板的若干倍；
    ///   2. **雾占主导**：地表对灯强度的响应是**瞬时**的（不过时间滤波），
    ///      一个亮度主要来自地面的像素会报 T = 0，把中位数往下拽。
    ///      判别办法是单独渲一张 <c>fog.mode = Off</c> 的图当「无雾参考」，
    ///      逐像素算雾占比。天空/空洞区（清屏黑）在这张参考里天然是 0，
    ///      于是「雾占比 = 1」自动成立 —— 不需要为它们另写一条分支。
    ///
    ///      参考图**不能**用 <c>enableInjection = false</c> 去取。这一条是量出来的：
    ///      关掉注入只撤走近层，AP 随即独自覆盖 0–150 m，而 AP **没有逐 froxel
    ///      太阳阴影** ⇒ 整片空气全亮、画面反而**更亮**（p50 3.82e-02 → 5.11e-02）。
    ///      那张图不是「无雾」，是「另一团雾」，拿它当分母会让占比处处为负、全图出局。
    ///      换成整档 Off 之后同一口径读到 8.664e-05 —— 比开雾低三个数量级，那才是地表。
    ///
    /// ── y₀ 与 y_∞ 都是实测的稳态，不是模型值 ──
    /// y_∞ **不能**用「每帧丢历史」的均值去代：⑯ 的 a-3 已经证明，死区在场时
    /// 滤波的稳态期望**不等于**驱动的期望（死区是一次按样本值做的有偏筛选）。
    /// 所以两端都用「按实测 w 算够空转帧数，再平均最后若干帧」得到。
    /// </summary>
    static class VistaFroxelLagSelfTest
    {
        // ---------------------------------------------------------------- 常量

        /// <summary>阶跃后逐帧读回的长度。覆盖最慢那一档：w_eff ≈ 0.982 ⇒ τ ≈ 55 帧 ⇒ T99 ≈ 253。</summary>
        const int k_CaptureFrames = 280;

        /// <summary>稳态平均用的帧数。要够多才压得住抖动噪声，又不必多到影响总时长。</summary>
        const int k_SteadyAvgFrames = 24;

        /// <summary>预跑段：读出这一档实际的 w，好据它算还要空转多少帧。</summary>
        const int k_SettlePreroll = 12;

        const int k_MaxSettleFrames = 4000;

        /// <summary>
        /// 空转残余的上限，与⑯同一个锚：<c>k_WeberThreshold × 0.1</c>。
        /// 稳态没坐稳的话 y₀ / y_∞ 自己就带偏，而 r_t 的分子分母都用它们。
        /// </summary>
        const float k_MaxSettleResidual = k_WeberThreshold * 0.1f;

        /// <summary>亮度死区的关闭态。理由见⑯的 <c>k_RejectDisabled</c>：Clamp01 + saturate ⇒ 1.0 就是恒等。</summary>
        const float k_RejectDisabled = 1f;

        /// <summary>横轴：与 ⑯c 的扫描点取同一组，两条曲线才对得上。</summary>
        static readonly float[] k_RejectSweep = { 0.25f, 0.55f, k_RejectDisabled };

        /// <summary>
        /// 阶跃幅度。两个值不是随手取的，各自对应一条预言（见类文档）：
        ///   0.15 ⇒ rel₀ = 0.130 低于全部下端 ⇒ 三档必须一致（g-1）；
        ///   0.50 ⇒ rel₀ = 0.333 夹在 0.25 与 0.55 之间 ⇒ 三档必须分开（g-2）。
        /// 改这两个数就等于改掉这两道门的含义，不要当成可调参数。
        /// </summary>
        static readonly float[] k_StepAmplitudes = { 0.15f, 0.50f };

        /// <summary>
        /// 雾占比的下限。0.5 = 这个像素一半以上的亮度来自雾，而不是地表。
        ///
        /// ★ 这道筛是**必要不充分**的，原因要写清楚：参考图是 <c>fog.mode = Off</c>，
        /// 所以 (y − S)/y 里的分子是**近层 + AP**，而 AP 没有时间滤波 ——
        /// 一个由 AP 主导的像素同样会报 T = 0，却能通过这道筛。
        /// 想单独量近层占比是做不到的：关掉注入不等于「只少了近层」，
        /// AP 会顶上来独自覆盖全程（见类文档里那条实测）。
        ///
        /// 后果的方向是**安全**的那一侧：混进来的是一批 r_t ≡ 0 的像素，
        /// 它把每一档的 |r_t| 中位数同向压低 ⇒ 每一档的 T50 都偏小、
        /// 档间比值被压向 1 ⇒ g-2 的分离度只会**更难**达标。
        /// 于是 g-2 通过仍然成立，只有 g-2 失败时这一条才需要先排除。
        /// </summary>
        const float k_FogFractionMin = 0.5f;

        /// <summary>阶跃幅度相对读回地板的最小倍数。低于它的像素，r_t 的分母是噪声。</summary>
        const float k_StepOverFloor = 20f;

        /// <summary>g-1 的容差：三档 T50 的极差相对中位数。</summary>
        const float k_IdenticalTolerance = 0.25f;

        /// <summary>
        /// g-1 红/黄的分界：极差要比「同一档跑两次之差」大这么多倍，才算得上
        /// 「真有一个没控住的变量」。低于它只能说明尺子不够细。
        ///
        /// 取 1.5 而不是 1.0：两个样本估出来的重复性本身就有一倍量级的涨落，
        /// 贴着 1.0 判会让「红」与「黄」每次运行随机互换 —— 一格随机翻面的判据
        /// 与一格恒绿的判据一样没有信息。
        /// </summary>
        const float k_DriftOverRepeat = 1.5f;

        /// <summary>g-2 要求的最小分离度：慢档的 T50 至少是快档的这个倍数。</summary>
        const float k_SeparationMin = 1.5f;

        const float k_AbsFloor = 1e-7f;

        /// <summary>g-0b 的容差：实测阶跃幅度与下发幅度的相对偏差上限。见 ReportWiring 里的理由。</summary>
        const float k_StepRealizedTolerance = 0.25f;

        /// <summary>
        /// g-0c：地表（<c>fog.mode = Off</c> 参考）占整幅亮度的中位比例上限。
        /// 掩膜要求逐像素的雾占比 ≥ <see cref="k_FogFractionMin"/> = 0.5，
        /// 所以布景的中位数至少要明显在那一侧，否则入选集会退化成画面里
        /// 最暗的那一小撮 —— 而那一撮同时也是最过不了步长筛的。
        /// 取 0.30 而不是 0.50：等于要求布景有余量，而不是刚好卡在掩膜的门上。
        /// </summary>
        const float k_SurfaceShareMax = 0.30f;

        static int SettleFramesFor(float w)
        {
            if (!(w > 0f)) return 0;
            if (w >= 1f) return k_MaxSettleFrames;
            double k = System.Math.Log(k_MaxSettleResidual) / System.Math.Log(w);
            return Mathf.Clamp(Mathf.CeilToInt((float)(k * 1.2)), 0, k_MaxSettleFrames);
        }

        // ---------------------------------------------------------------- 入口

        [MenuItem("Window/Vista/Acceptance: Froxel Temporal Lag (Ghosting)", priority = 138)]
        static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 判据⑰：近层 froxel 时间滞后 + 亮度死区判别力（补⑯c 的另一半曲线）===");
            bool pass;
            try
            {
                pass = Acceptance(sb);
            }
            catch (System.Exception e)
            {
                sb.AppendLine($"✘ 运行中抛异常：{e}");
                pass = false;
            }

            sb.AppendLine(pass ? "=== 结论：通过 ===" : "=== 结论：**未通过 / 不可判定** ===");
            if (pass) Debug.Log(sb.ToString());
            else Debug.LogError(sb.ToString());
        }

        // ---------------------------------------------------------------- 一档的产物

        sealed class Lag
        {
            public string name;
            public float rejectStart;
            public float amplitude;

            /// <summary>阶跃前的稳态（逐像素，最后 <see cref="k_SteadyAvgFrames"/> 帧的平均）。</summary>
            public double[] y0;

            /// <summary>阶跃后充分空转得到的新稳态（同上）。**不是**用丢历史的均值代的，见类文档。</summary>
            public double[] yInf;

            /// <summary>阶跃后逐帧的读回，长度 <see cref="k_CaptureFrames"/>。</summary>
            public float[][] series;

            /// <summary>阶跃后第一帧结束时的 framesSinceValid。0 表示重投影在阶跃那一帧失效了 ⇒ 这一档不可判定。</summary>
            public int framesValidAfterStep;
            public string invalidReasonAfterStep;

            /// <summary>CPU 侧下发的历史权重（阶跃前稳态末尾读的）。</summary>
            public float wCpu, dtCpu;
            public int settleFrames;

            // ---- 归约产物（BuildMask 之后才填） ----
            public float t50, t90, t99;
            public float t50p90;      // 逐像素 T50 的 90 分位：拖影最久的那一撮
            public int   maskedCount;
            public float[] medianCurve;   // 逐帧的 |r_t| 中位数，报表画趋势用

            /// <summary>
            /// **实测**的相对阶跃幅度 = median over p of (y_∞[p] − y₀[p]) / y₀[p]，
            /// 取在全图（只滤掉过暗的像素），与入选掩膜无关。
            ///
            /// 为什么要单独存一个数：第一版的杠杆是 <c>Light.intensity</c>，
            /// 被 VistaSunTransmittance 每帧覆写，实际阶跃恒为 0，
            /// 于是掩膜一个像素都不入选，而报表只说得出「入选像素 0 ✘太少」——
            /// 那句话对「布景太暗」「雾太薄」「阶跃没生效」三种完全不同的故障
            /// 给出同一段文字。把这个量读出来，g-0b 就能直接点名是第三种。
            /// </summary>
            public float realizedStepRel;

            /// <summary>
            /// 掩膜的**落选原因分解**，以及 y₀ 的分位数。
            ///
            /// 为什么要有：「入选像素 0」这句话对「步长筛太严」「雾占比筛太严」
            /// 「布景整体太暗」三种故障给出同一段文字，而它们的修法分别是
            /// 「调 k_StepOverFloor」「调 k_FogFractionMin」「改布景亮度」。
            /// 上一轮我把它猜成了第二种（压黑地表），入选数只动了 ±10% ——
            /// 一次本来可以用一行读数替代的往返。
            /// </summary>
            public int rejStep, rejFog;
            public float y0p50, y0p90, y0max;
            public float stepFloorUsed;

            /// <summary>
            /// 被**步长筛**毙掉的那一批像素自己的 y₀ p90。
            ///
            /// 单独报它，是因为「步长筛毙 8770」这个数对两件事给出同一句话：
            /// 那 8770 个是画面里最暗的一批（布景问题），还是明明很亮却没跟着阶跃动
            /// （杠杆问题，或者那一片压根没吃到近层）。前者这一列会远低于 y₀ p50，
            /// 后者会与之持平 —— 一列数分开两种修法。
            /// </summary>
            public float y0RejStepP90;
        }

        // ---------------------------------------------------------------- 主体

        static bool Acceptance(StringBuilder sb)
        {
            var feature = VistaAtmosphereFeature.current;
            if (feature == null)
            {
                sb.AppendLine("✘ 前置：场景里没有启用中的 VistaAtmosphereFeature。");
                return false;
            }

            var volume = feature.froxelVolume;
            if (volume == null || !volume.isValid)
            {
                sb.AppendLine("✘ 前置：近层 froxel 体不可用（froxelVolume 为 null 或无效）。");
                return false;
            }

            var litShader = Shader.Find("Vista/Lit");
            if (litShader == null)
            {
                sb.AppendLine("✘ 前置：找不到 Vista/Lit。");
                return false;
            }

            int layer = FindUnusedLayer();
            if (layer < 0)
            {
                sb.AppendLine("✘ 前置：没有空闲 layer 可以隔离布景。");
                return false;
            }

            var fog = feature.fog;
            var vf = feature.volumetricFog;
            var atmo = feature.parameters;
            var state = RigState.Capture(fog, vf);

            // RigState 不管这四项，谁改谁还原 —— 与⑯同一条约定。
            var prevJitter = vf.jitterMode;
            float prevRejStart = vf.luminanceRejectStart;
            bool prevForceNoShadow = VistaFroxelVolume.s_DebugForceNoSunShadow;
            float baseLux = atmo.sunIlluminanceLux;

            RenderTexture rt = null;
            Texture2D tex = null;
            GameObject root = null;
            Material mat = null;

            try
            {
                state.Apply();
                VistaFroxelVolume.s_DebugForceNoSunShadow = false;

                rt = new RenderTexture(k_RtSize, k_RtSize, 24,
                                       RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                Build(litShader, layer, feature.groundLevelWorldY, rt,
                      out root, out Camera cam, out mat, out Light sun);
                RenderSettings.sun = sun;

                // ---- 这里**不**改布景的亮度。留一段说明，因为它花了三轮才问清 ----
                //
                //  掩膜要求「像素亮度主要来自近层」：地表对照度的响应是零延迟的，
                //  一个亮度主要来自地面的像素会报 T = 0，把中位数拽向「拖影比想象的短」
                //  —— 一个让人放心的错误答案。所以我一度在这里把 albedo 压到 0.04、
                //  再压到 0、再关掉镜面与环境反射。
                //
                //  **这一整条都是在给一个不存在的问题下药。** 探针
                //  （Window/Vista/Probe: Froxel Lag Rig Breakdown）把四档并排量了一次：
                //      albedo 0.5 → p50 3.945e-02      雾档 Off → p50 8.664e-05
                //      albedo 0   → p50 3.941e-02      再关太阳 → p50 8.664e-05
                //  地表只占整幅亮度的 0.23%，而且**与 albedo 无关**。
                //  原因是曝光：布景的灯是一盏 intensity = 1 的裸 Unity 灯，
                //  EV100 = 9 ⇒ exposure ≈ 1.6e-3 ⇒ 地表预曝光辐亮度 ~1e-4；
                //  而雾那一路吃的是 sunIlluminanceLux（十万量级），亮三个数量级。
                //
                //  留下的教训是方法上的：albedo 0.5 → 0.04 预言了 12 倍的变化，
                //  读数只动了 0.1% —— 那一刻就该去量「那 12 倍去哪儿了」，
                //  而不是接着把 0.04 改成 0。「一个非零的数回答不了它是从哪儿来的」
                //  的对偶是：**一个不动的数同样回答不了**，两种情况都只能靠拆通路来问。

                vf.jitterMode = JitterMode.Procedural;
                for (int i = 0; i < k_WarmupFrames; i++) cam.Render();

                var reproj = feature.froxelReprojection;
                if (reproj == null)
                {
                    sb.AppendLine("✘ 前置：拿不到 froxelReprojection —— "
                                + "阶跃后的 framesSinceValid 读不到，g-0 就没法把"
                                + "「死区拒绝了」与「重投影自己失效了」分开。");
                    return false;
                }

                tex = new Texture2D(k_RtSize, k_RtSize, TextureFormat.RGBAFloat, false, true);

                sb.AppendLine();
                sb.AppendLine("① 前置与布景");
                var desc = volume.allocatedDesc;
                if (!desc.HasValue)
                {
                    sb.AppendLine("  ✘ 近层体一次都没分配下来（allocatedDesc 为 null）。");
                    return false;
                }
                var d = desc.Value;
                sb.AppendLine($"  体 {d.width}×{d.height}×{d.depth}，接手 {d.handoffMeters:F1} m，"
                            + $"far {d.farMeters:F1} m；遮挡物 z = {k_OccluderZ:F0} m");
                sb.AppendLine($"  抖动档 = {vf.jitterMode}，τ = {vf.historyTimeConstant:F3} s，"
                            + $"片数 = {vf.sliceCount}");

                // ---- 无雾参考：用来验证「地表确实不参与」----
                //  地表对灯强度的响应是瞬时的，一个亮度主要来自地面的像素会报 T = 0。
                //  没有这张参考的话，中位数会被这些像素拽低，而报表上看起来只是
                //  「拖影比想象的短」—— 一个让人放心的错误答案。
                //
                //  ★ 关掉的必须是 **fog.mode**，不能是 vf.enableInjection。
                //  这一条是量出来的，不是推出来的：关掉注入之后画面 p50 从
                //  3.82e-02 涨到 **5.11e-02** —— 更亮，不是更暗。因为近层一撤，
                //  AP 就独自覆盖 0–150 m，而 AP **没有逐 froxel 太阳阴影**，
                //  于是整片空气全亮、光柱消失。那张图不是「无雾」，它是「另一团雾」。
                //  拿它当参考，占比 (y − ref)/y 处处为负 ⇒ 全图 56766 px 被毙，
                //  而报表只说得出「入选 0」。
                //  换成 fog.mode = Off 之后同一口径读到 8.664e-05，比开雾低三个数量级 ——
                //  这才是地表。
                //
                //  这张图**不需要**按 w 空转：雾整档关掉之后画面里没有任何时间滤波。
                //  但切回来那一趟必须空转够（近层历史要重新收敛），
                //  帧数与六档正文共用 SettleFramesFor —— 「一个不稳的 dt 会让解析预测
                //  无处可锚」在这里的推论是：帧数也不能写死。
                var prevFogMode = fog.mode;
                fog.mode = VistaFogSettings.Mode.Off;
                for (int i = 0; i < k_SettlePreroll; i++) cam.Render();
                var noFog = AverageFrames(cam, rt, tex, k_SteadyAvgFrames);
                fog.mode = prevFogMode;
                for (int i = 0; i < k_SettlePreroll; i++) cam.Render();
                for (int i = 0; i < SettleFramesFor(reproj.lastHistoryWeight); i++) cam.Render();

                // ---- 布景前提：这幅画面确实是「雾占主导」的 ----
                //  掩膜整条链都建立在这上面，而它是**布景**的性质，不是被测代码的。
                //  把它单独判出来，「入选 0」就不会再与「近层没开」「地表太亮」
                //  「参考图里还留着雾」三件事共用同一段文字。
                var fogOn = AverageFrames(cam, rt, tex, k_SteadyAvgFrames);
                float surfaceShare;
                {
                    var sh = new List<float>(fogOn.Length);
                    for (int p = 0; p < fogOn.Length; p++)
                    {
                        if (fogOn[p] <= k_AbsFloor) continue;
                        sh.Add((float)(noFog[p] / fogOn[p]));
                    }
                    surfaceShare = sh.Count > 0 ? Median(sh) : float.NaN;
                }

                // ---- 尺子（必须先于六档算出来：每一档跑完就地归约，好把 series 立刻放掉）----
                //  六档的 series 各 280 × 256² × 4B ≈ 73 MB，全留到最后一起归约是 440 MB。
                //  「量性能的工具不该改被量对象」在这里的变体：一个会把编辑器逼进
                //  GC / 换页的判据，量到的 Δt 就不是线上那个 Δt，而 w = exp(−Δt/τ) 直接吃 Δt。
                //
                //  ★ 地板改成**同一稳态的两次重复测量之差**，不再用「无近层参考图的
                //  逐像素二阶差分」。两条理由：
                //  · 压黑地表之后那张参考图近乎全黑，它的二阶差分是 0 ⇒ 被
                //    k_AbsFloor 兜成 1e-7 ⇒ 步长门掉到 2e-6，等于**尺子自己没了**，
                //    而报表会照常打印一个数。这正是「一格恒绿的判据不是判据」的地板版。
                //  · 步长筛要挡的是「|y_∞ − y₀| 小到分母被噪声吃掉」，那里的噪声是
                //    *这一口径下一次 AverageFrames 的可重复性*，不是空间梯度。
                //    直接量它，比用一个空间量去代理它少一层假设。
                //  取 p50 与 20 倍倍率，两者都沿用旧口径，好让新旧读数还能比。
                var fogOn2 = AverageFrames(cam, rt, tex, k_SteadyAvgFrames);
                float floorSd;
                {
                    var rep = new float[fogOn.Length];
                    for (int p = 0; p < fogOn.Length; p++)
                        rep[p] = (float)System.Math.Abs(fogOn2[p] - fogOn[p]);
                    floorSd = Mathf.Max(Percentile(rep, 0.50f), k_AbsFloor);
                    sb.AppendLine($"  读回地板（同一稳态两次 {k_SteadyAvgFrames} 帧平均之差 p50）"
                                + $"= {Sci(floorSd)}；p90 = {Sci(Percentile(rep, 0.90f))}");
                }

                // g-0c：布景确实雾占主导。判在**中位数**上，不判 max ——
                // 「max 回答不了阈值该摆哪儿」。
                bool fogDominant = surfaceShare < k_SurfaceShareMax;
                sb.AppendLine($"  {Mark(fogDominant)}g-0c 地表占比中位数 = {Fmt(surfaceShare)}"
                            + $"（要求 < {k_SurfaceShareMax:F2}）—— 掩膜的雾占比筛"
                            + $"（≥ {k_FogFractionMin:F2}）站在这条前提上");
                sb.AppendLine($"     无雾参考 S（fog.mode = Off）：p50 {Sci(Pct(noFog, 0.50f))}"
                            + $"  p90 {Sci(Pct(noFog, 0.90f))}  max {Sci(Pct(noFog, 1.00f))}");
                sb.AppendLine($"     开雾画面 y（Froxel 档）：p50 {Sci(Pct(fogOn, 0.50f))}"
                            + $"  p90 {Sci(Pct(fogOn, 0.90f))}  max {Sci(Pct(fogOn, 1.00f))}");
                if (!fogDominant)
                    sb.AppendLine("     ⓘ 这一格红 ⇒ 下面的「入选 0」是布景问题，不是被测代码问题。"
                                + "比值 > 1 说明参考图比开雾那张还亮 —— 那就不是「没有雾」而是"
                                + "「另一团雾」（关注入而不是关雾档时会这样：AP 独自覆盖全程且没有阴影）。"
                                + "比值在 0.3~1 之间才是真的地表太亮，那时查 albedo 与曝光。");

                // ---- 六档 ----
                var runs = new List<Lag>();
                foreach (float rs in k_RejectSweep)
                    foreach (float a in k_StepAmplitudes)
                    {
                        var r = Measure(cam, rt, tex, vf, reproj, atmo, baseLux, rs, a);
                        ReduceLag(r, noFog, floorSd);
                        runs.Add(r);
                    }

                // ---- 第七档：把其中一档**原封不动再跑一遍** ----
                //  为什么必须有它：g-1 判的是「三档 T50 的极差 ≤ 25%」，而 25% 是
                //  手挑的。极差一旦落在这条线附近（#6 跑出 25.1%），报表就回答不了
                //  「真有一个没控住的变量」还是「这把尺子本来就只有这么细」——
                //  两种情况的修法一个是查代码、一个是改口径，而它们在报表上长得一样。
                //
                //  重复档取「下端 1.00（死区关闭）+ 小幅阶跃」：死区整条关掉之后
                //  两次之间**按设计没有任何差异来源**，于是两次 T50 之差就是
                //  这把尺子的重复性本身，不掺任何被测效应。
                //  取 0.25 或 0.55 去重复的话，死区的偏置会混进这个数里，
                //  而那正是要被它当分母的东西 —— 尺子不能拿被测量去标定自己。
                var repeat = Measure(cam, rt, tex, vf, reproj, atmo, baseLux,
                                     k_RejectDisabled, k_StepAmplitudes[0]);
                repeat.name = $"重复档（= 下端 {k_RejectDisabled:F2}・阶跃 +{k_StepAmplitudes[0] * 100f:F0}%，第二次）";
                ReduceLag(repeat, noFog, floorSd);

                bool pass = fogDominant;
                pass &= ReportWiring(sb, runs);
                pass &= ReportCurves(sb, runs);
                pass &= ReportIdentity(sb, runs, repeat, out float tFloor);   // g-1，顺带定出 T50 的地板
                pass &= ReportSeparation(sb, runs, tFloor);           // g-2
                ReportTradeoff(sb, runs, prevRejStart);                // 读数：那半条曲线
                return pass;
            }
            finally
            {
                vf.jitterMode = prevJitter;
                vf.luminanceRejectStart = prevRejStart;
                VistaFroxelVolume.s_DebugForceNoSunShadow = prevForceNoShadow;
                // 照度要在布景拆掉**之前**还回去：还原会让静态 LUT 脏一次，
                // 而重烘发生在下一次有人渲染的时候 —— 那已经是场景里的相机了。
                atmo.sunIlluminanceLux = baseLux;
                state.Restore();
                if (tex != null) Object.DestroyImmediate(tex);
                if (mat != null) Object.DestroyImmediate(mat);
                if (root != null) Object.DestroyImmediate(root);
                if (rt != null) rt.Release();
            }
        }

        // ---------------------------------------------------------------- 一档的测量

        static Lag Measure(Camera cam, RenderTexture rt, Texture2D tex,
                           VistaVolumetricFogSettings vf, VistaFroxelReprojection reproj,
                           VistaAtmosphereParameters atmo, float baseLux,
                           float rejectStart, float amplitude)
        {
            vf.luminanceRejectStart = rejectStart;

            // 每一档都先显式写回基准值。上一档结束时已经写回过一次，所以这一行
            // 正常情况下是个无操作（值相等 ⇒ 不脏 ⇒ 不重烘）。留着它是因为
            // 「上一档一定还原过」是一条要读另一段代码才能确认的前提，
            // 而这一行让它变成本地可见的。
            atmo.sunIlluminanceLux = baseLux;

            // 预跑段读出这一档实际的 w，再据它算够空转帧数 —— 与⑯同一条：
            // Δt 不在判据的控制之内，写死帧数不可能对。
            for (int i = 0; i < k_SettlePreroll; i++) cam.Render();
            int extra = SettleFramesFor(reproj.lastHistoryWeight);
            for (int i = 0; i < extra; i++) cam.Render();

            var lag = new Lag
            {
                name = $"下端 {rejectStart:F2}・阶跃 +{amplitude * 100f:F0}%",
                rejectStart = rejectStart,
                amplitude = amplitude,
                settleFrames = k_SettlePreroll + extra,
                wCpu = reproj.lastHistoryWeight,
                dtCpu = reproj.lastDeltaTime,
            };
            lag.y0 = AverageFrames(cam, rt, tex, k_SteadyAvgFrames);

            // ---- 阶跃 ----
            //  改的是大气照度而**不是** Light.intensity：后者每帧被
            //  VistaSunTransmittance 从 sunIlluminanceLux 反推着覆写掉
            //  （见类头「阶跃怎么造」）。乘法而不是加法：rel₀ = a/(1+a)
            //  与基准照度无关，于是这一档踩不踩死区不取决于场景调了多亮。
            atmo.sunIlluminanceLux = baseLux * (1f + amplitude);

            lag.series = new float[k_CaptureFrames][];
            for (int i = 0; i < k_CaptureFrames; i++)
            {
                cam.Render();
                lag.series[i] = ReadLuminance(rt, tex);
                if (i == 0)
                {
                    // 必须读在**第一帧之后**：重投影若因阶跃失效，就是在这一帧失效的。
                    lag.framesValidAfterStep = reproj.framesSinceValid;
                    lag.invalidReasonAfterStep = reproj.lastInvalidReason;
                }
            }

            // ---- 新稳态 ----
            //  不拿 series 的尾巴当 y_∞：k_CaptureFrames 在最慢那一档只压到 1% 残余，
            //  而 r_t 的分母正比于 (y₀ − y_∞)，1% 的 y_∞ 偏差会直接变成 T 的系统偏差。
            for (int i = 0; i < SettleFramesFor(reproj.lastHistoryWeight); i++) cam.Render();
            lag.yInf = AverageFrames(cam, rt, tex, k_SteadyAvgFrames);

            atmo.sunIlluminanceLux = baseLux;
            return lag;
        }

        static double[] AverageFrames(Camera cam, RenderTexture rt, Texture2D tex, int n)
        {
            double[] acc = null;
            for (int i = 0; i < n; i++)
            {
                cam.Render();
                var f = ReadLuminance(rt, tex);
                if (acc == null) acc = new double[f.Length];
                for (int p = 0; p < f.Length; p++) acc[p] += f[p];
            }
            for (int p = 0; p < acc.Length; p++) acc[p] /= n;
            return acc;
        }

        static float[] ReadLuminance(RenderTexture rt, Texture2D tex)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = prev;

            var px = tex.GetPixels();
            var lum = new float[px.Length];
            for (int i = 0; i < px.Length; i++)
                lum[i] = 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
            return lum;
        }

        // ---- 这里原本有一个 ReadbackFloor(noFog)：无近层参考图上相邻像素二阶差分的 p50。
        //      它被删掉而不是留着备用，因为压黑地表之后那张图近乎全黑，它会返回
        //      k_AbsFloor 兜底值并把步长门压到 2e-6 —— 一把量不到东西却照常出数的尺子，
        //      留在文件里迟早会被谁接回去。地板改由「同一稳态两次平均之差」直接测，见 ①。

        // ---------------------------------------------------------------- 归约

        /// <summary>double[] 上的分位数。共用 <c>Percentile</c> 的口径（clone + sort），
        /// 不另写一套排序 —— 「同一个量的第二份实现连三行的约简也算」。</summary>
        static float Pct(double[] v, float q)
        {
            var a = new float[v.Length];
            for (int i = 0; i < v.Length; i++) a[i] = (float)v[i];
            return Percentile(a, q);
        }

        static void ReduceLag(Lag lag, double[] noFog, float floorSd)
        {
            int np = lag.y0.Length;
            float stepFloor = floorSd * k_StepOverFloor;
            lag.stepFloorUsed = stepFloor;

            // ---- g-0b 的读数：实测阶跃幅度 ----
            //  先算、且独立于入选掩膜。下面那个掩膜要求「阶跃看得见」，
            //  所以它一旦为空就再也回答不了「阶跃到底有多大」——
            //  一个用被测前提去筛样本、再用样本去验证那个前提的环。
            //  这里只滤掉过暗的像素（比值的分母），门槛用同一把尺子。
            {
                var rel = new List<float>(np);
                double lumFloor = stepFloor;
                for (int p = 0; p < np; p++)
                {
                    if (lag.y0[p] < lumFloor) continue;
                    rel.Add((float)((lag.yInf[p] - lag.y0[p]) / lag.y0[p]));
                }
                lag.realizedStepRel = rel.Count > 0 ? Median(rel) : float.NaN;
            }

            // ---- y₀ 的量级：报表里那些「地板」「20 倍」才有对照 ----
            {
                var a = new float[np];
                for (int p = 0; p < np; p++) a[p] = (float)lag.y0[p];
                lag.y0p50 = Percentile(a, 0.50f);
                lag.y0p90 = Percentile(a, 0.90f);
                lag.y0max = Percentile(a, 1.00f);
            }

            var t50 = new List<float>();
            var t90 = new List<float>();
            var t99 = new List<float>();
            var rejStepY0 = new List<float>();
            var curveAcc = new List<float>[k_CaptureFrames];
            for (int i = 0; i < k_CaptureFrames; i++) curveAcc[i] = new List<float>();

            for (int p = 0; p < np; p++)
            {
                double denom = lag.yInf[p] - lag.y0[p];
                if (System.Math.Abs(denom) < stepFloor)
                {
                    lag.rejStep++;
                    rejStepY0.Add((float)lag.y0[p]);
                    continue;
                }

                // 雾占比：整幅亮度里有多少来自雾（近层 + AP），而不是地表。
                // 天空/空洞（清屏黑）在 noFog 里是 0 ⇒ 占比 1，自动入选，不需要另写分支。
                // 这道筛为什么是必要不充分的，以及为什么它的偏差方向是安全的，
                // 见 <see cref="k_FogFractionMin"/> 的文档。
                //
                // ★ 分母取 y₀ 而不是 y_∞：noFog 是在**基准**照度下拍的，
                // 而 y_∞ 在阶跃后的照度上，两者差一个 (1+a) 因子。
                // 拿 y_∞ 当分母等于把占比按幅度人为抬高 ——
                // 上一轮的读数把这个后果摆得很清楚：同一套像素，
                // +50% 档过了 2192 个、+15% 档过 0 个。掩膜若依赖被测的幅度，
                // g-1「三档 T50 必须一致」比较的就不再是同一批像素，
                // 那一格会变成一个量着别的东西的绿。
                double total = System.Math.Max(lag.y0[p], k_AbsFloor);
                double fogFrac = (total - noFog[p]) / total;
                if (fogFrac < k_FogFractionMin) { lag.rejFog++; continue; }

                lag.maskedCount++;

                float a50 = float.NaN, a90 = float.NaN, a99 = float.NaN;
                double prevR = 1.0;
                for (int i = 0; i < k_CaptureFrames; i++)
                {
                    double r = (lag.series[i][p] - lag.yInf[p]) / denom;
                    double ar = System.Math.Abs(r);
                    curveAcc[i].Add((float)ar);

                    if (float.IsNaN(a50)) a50 = CrossAt(prevR, ar, 0.50, i);
                    if (float.IsNaN(a90)) a90 = CrossAt(prevR, ar, 0.10, i);
                    if (float.IsNaN(a99)) a99 = CrossAt(prevR, ar, 0.01, i);
                    prevR = ar;
                }
                // 没跨过阈值的像素记成「至少 k_CaptureFrames」，不丢弃：
                // 丢掉它们等于把最慢的那些像素从统计里删掉，而拖影恰恰是它们造成的 ——
                // 「把跑得最慢的那些样本剔掉，剩下的平均值一定很好看」。
                t50.Add(float.IsNaN(a50) ? k_CaptureFrames : a50);
                t90.Add(float.IsNaN(a90) ? k_CaptureFrames : a90);
                t99.Add(float.IsNaN(a99) ? k_CaptureFrames : a99);
            }

            lag.t50 = t50.Count > 0 ? Median(t50) : float.NaN;
            lag.t90 = t90.Count > 0 ? Median(t90) : float.NaN;
            lag.t99 = t99.Count > 0 ? Median(t99) : float.NaN;
            lag.t50p90 = t50.Count > 0 ? Percentile(t50.ToArray(), 0.9f) : float.NaN;
            lag.y0RejStepP90 = rejStepY0.Count > 0
                ? Percentile(rejStepY0.ToArray(), 0.90f) : float.NaN;

            lag.medianCurve = new float[k_CaptureFrames];
            for (int i = 0; i < k_CaptureFrames; i++)
                lag.medianCurve[i] = curveAcc[i].Count > 0 ? Median(curveAcc[i]) : float.NaN;

            // 归约完就把逐帧读回放掉。留着它唯一的用处是「事后再想量点别的」，
            // 而代价是六档同时在内存里 ≈ 440 MB —— 那会通过 GC 抖动反过来改 Δt。
            lag.series = null;
        }

        /// <summary>|r| 首次跨到阈值以下的帧号，在两帧之间线性插值。没跨过返回 NaN。</summary>
        static float CrossAt(double prev, double now, double thr, int i)
        {
            if (now > thr) return float.NaN;
            if (i == 0 || prev <= thr) return i;
            double f = (prev - thr) / System.Math.Max(prev - now, 1e-12);
            return (float)(i - 1 + f);
        }

        // ---------------------------------------------------------------- 报表

        static bool ReportWiring(StringBuilder sb, List<Lag> runs)
        {
            sb.AppendLine();
            sb.AppendLine("② g-0a：阶跃没有顺手把重投影打掉");
            sb.AppendLine("   （衰减若是瞬间完成的，报表必须能分清「死区拒绝了」与"
                        + "「重投影自己失效了」—— 两者的修法完全不同）");
            bool ok = true;
            foreach (var r in runs)
            {
                bool valid = r.framesValidAfterStep >= 1;
                ok &= valid;
                sb.AppendLine($"  {Mark(valid)}{r.name}：阶跃后 framesSinceValid = {r.framesValidAfterStep}"
                            + $"，失效原因 = {(r.invalidReasonAfterStep ?? "无")}"
                            + $"；w = {r.wCpu:F4}，Δt = {r.dtCpu * 1000f:F2} ms，空转 {r.settleFrames} 帧");
            }

            // ---- g-0b ----
            //  这一格是第一版的墓志铭：那时杠杆写的是 Light.intensity，
            //  被 VistaSunTransmittance 每帧覆写，实测阶跃恒为 0，
            //  六档全部只报出「入选像素 0 ✘太少」——「一个零回答不了它是从哪儿来的」。
            //  容差 25% 挑得比较松，为的是不与已知的死区稳态偏置（⑯a-3，约 1.2%）
            //  和读回噪声抢戏：这一格要抓的是「差一个数量级」和「压根没动」，
            //  而不是去当第二把亮度尺子。
            sb.AppendLine();
            sb.AppendLine("②b g-0b：实测阶跃幅度对得上下发的幅度");
            sb.AppendLine("   （量的是 median (y_∞ − y₀)/y₀，取在全图、与入选掩膜无关："
                        + "掩膜本身要求「阶跃看得见」，用它筛出来的样本没资格验证这一条）");
            foreach (var r in runs)
            {
                float ratio = r.realizedStepRel / r.amplitude;
                bool good = !float.IsNaN(ratio)
                         && Mathf.Abs(ratio - 1f) <= k_StepRealizedTolerance;
                ok &= good;
                sb.AppendLine($"  {Mark(good)}{r.name}：下发 {r.amplitude:F3}，"
                            + $"实测 {Fmt(r.realizedStepRel)}，比值 {Fmt(ratio)}"
                            + $"（容差 ±{k_StepRealizedTolerance:P0}）");
            }
            if (!ok)
                sb.AppendLine("  ⓘ 这一格红 ⇒ 阶跃杠杆没接上，下面所有 T 都不可判定；"
                            + "先查 sunIlluminanceLux 是不是真的被写进去了，再看掩膜。");
            return ok;
        }

        static bool ReportCurves(StringBuilder sb, List<Lag> runs)
        {
            sb.AppendLine();
            sb.AppendLine("③ 衰减特征帧数（|r| 首次跨到阈值以下；r = (y_t − y_∞)/(y₀ − y_∞)）");
            sb.AppendLine("   | 档位 | 入选像素 | T50 | T90 | T99 | T50 的 p90 |");
            sb.AppendLine("   |---|---|---|---|---|---|");
            bool ok = true;
            foreach (var r in runs)
            {
                bool enough = r.maskedCount >= 1000;
                ok &= enough;
                sb.AppendLine($"   | {r.name} | {r.maskedCount}{(enough ? "" : " ✘太少")} | "
                            + $"{r.t50:F1} | {r.t90:F1} | {r.t99:F1} | {r.t50p90:F1} |");
            }
            if (!ok)
                sb.AppendLine("  ✘ 有档位入选像素不足 1000 ⇒ 分位数不可信，这一轮不可判定。");

            // 落选原因分解。三个筛子各毙掉多少，以及 y₀ 的量级 ——
            // 有了这一行，「入选 0」才能指向具体那一个筛子。
            sb.AppendLine();
            sb.AppendLine("   ⓘ 落选原因分解（共 " + (k_RtSize * k_RtSize) + " px）与 y₀ 量级");
            sb.AppendLine("   | 档位 | 步长筛毙 | 其 y₀ p90 | 雾占比筛毙 | 入选 | y₀ p50 | y₀ p90 | y₀ max | 步长门 |");
            sb.AppendLine("   |---|---|---|---|---|---|---|---|---|");
            foreach (var r in runs)
                sb.AppendLine($"   | {r.name} | {r.rejStep} | {Sci(r.y0RejStepP90)} | {r.rejFog} | {r.maskedCount} | "
                            + $"{Sci(r.y0p50)} | {Sci(r.y0p90)} | {Sci(r.y0max)} | {Sci(r.stepFloorUsed)} |");

            // 慢尾的形状：只印几个采样点，够看出「先快后慢」就行。
            sb.AppendLine();
            sb.AppendLine("   ⓘ |r| 中位数随帧（看两段结构：rel 掉回下端以下之后换成慢衰减）");
            int[] probe = { 0, 2, 5, 10, 20, 40, 80, 160, k_CaptureFrames - 1 };
            var head = new StringBuilder("   | 档位 |");
            var sep = new StringBuilder("   |---|");
            foreach (int i in probe) { head.Append($" f{i} |"); sep.Append("---|"); }
            sb.AppendLine(head.ToString());
            sb.AppendLine(sep.ToString());
            foreach (var r in runs)
            {
                var row = new StringBuilder($"   | {r.name} |");
                foreach (int i in probe) row.Append($" {r.medianCurve[i]:F3} |");
                sb.AppendLine(row.ToString());
            }
            return ok;
        }

        /// <summary>
        /// g-1：小幅阶跃下三档必须一致。**三值**，不是两值。
        ///
        /// 「极差超了容差」这一句话覆盖两种故障：真有一个没控住的变量在漂，
        /// 或者这把尺子本来就分辨不到这么细。<paramref name="repeat"/>（同一档跑两次）
        /// 把后者量出来，于是报表能分开说：
        ///   · 极差 ≤ 容差                        ⇒ ✔ 通过
        ///   · 极差 &gt; 容差 且 极差 &gt; 重复性×1.5  ⇒ ✘ 真的有东西在漂，去查
        ///   · 极差 &gt; 容差 但 极差 ≲ 重复性       ⇒ ⚠ **不可判定**（仍然不通过）
        /// 第三种必须是缺口不能是通过 ——「不可判定必须是缺口，不能是通过」。
        /// </summary>
        static bool ReportIdentity(StringBuilder sb, List<Lag> runs, Lag repeat, out float tFloor)
        {
            sb.AppendLine();
            float a = k_StepAmplitudes[0];
            float rel0 = a / (1f + a);
            sb.AppendLine($"④ g-1：阶跃 +{a * 100f:F0}%（rel₀ = {rel0:F3}）低于全部下端 "
                        + $"⇒ 死区一次都不该拒 ⇒ 三档的 T50 必须一致");
            sb.AppendLine("   这一格同时是尺子的自检：它一旦红，③ 里任何档间差异都不能"
                        + "归给死区 —— 可能是某个没控住的变量在漂。");

            var group = runs.FindAll(r => Mathf.Approximately(r.amplitude, a));
            var t = new List<float>();
            foreach (var r in group) t.Add(r.t50);
            float med = Median(t), lo = Mathf.Min(t.ToArray()), hi = Mathf.Max(t.ToArray());
            float range = hi - lo;
            float spread = med > 0f ? range / med : float.PositiveInfinity;

            foreach (var r in group)
                sb.AppendLine($"     {r.name} → T50 {r.t50:F1}");

            // ---- 重复性：同一档跑两次的 T50 之差 ----
            var twin = group.Find(r => Mathf.Approximately(r.rejectStart, repeat.rejectStart));
            float repeatability = twin != null ? Mathf.Abs(repeat.t50 - twin.t50) : float.NaN;
            sb.AppendLine($"     {repeat.name} → T50 {repeat.t50:F1}");
            sb.AppendLine($"  ⓘ 重复性：同一档两次之差 = **{repeatability:F1} 帧**"
                        + $"（{(twin != null ? $"{twin.t50:F1} vs {repeat.t50:F1}" : "配不上对")}）"
                        + " —— 这是这把尺子在这个幅度上的分辨极限，不含任何被测效应。");

            bool identical   = spread <= k_IdenticalTolerance;
            bool exceedsNoise = !float.IsNaN(repeatability) && range > repeatability * k_DriftOverRepeat;

            if (identical)
                sb.AppendLine($"  ✔ 极差 {range:F1} 帧 / 中位 {med:F1} 帧 = {spread:P1}"
                            + $"，要求 ≤ {k_IdenticalTolerance:P0}");
            else if (exceedsNoise)
                sb.AppendLine($"  ✘ 极差 {range:F1} 帧 / 中位 {med:F1} 帧 = {spread:P1}"
                            + $"，超出容差 {k_IdenticalTolerance:P0}，"
                            + $"且 {range:F1} > 重复性 {repeatability:F1} × {k_DriftOverRepeat:F1}"
                            + " ⇒ **真有一个没控住的变量在漂**，③ 的档间差异这一轮不能归给死区。");
            else
                sb.AppendLine($"  ⚠ 极差 {range:F1} 帧 / 中位 {med:F1} 帧 = {spread:P1}"
                            + $"，超出容差 {k_IdenticalTolerance:P0}，"
                            + $"但没超过重复性 {repeatability:F1} 帧 × {k_DriftOverRepeat:F1}"
                            + " ⇒ **不可判定**：这把尺子在 +"
                            + $"{a * 100f:F0}% 幅度上分辨不到 {k_IdenticalTolerance:P0} 这么细。"
                            + "这是缺口不是通过，所以仍然不放行。"
                            + "要让它可判，得把 k_SteadyAvgFrames 加大（偏置按 1/√n 掉），"
                            + "而**不是**把容差放宽 ——「拍阈值等于把结论写进判据」。");

            // T 的地板：两条来源取大。极差反映「预言相同的三档实际上差了多少」，
            // 重复性反映「同一档自己差了多少」—— 后者可能更大（极差只有三个样本），
            // 取小的那个会让 g-2 用一把比实际更细的尺子去宣布可分辨。
            // 不去猜一个数，也不拿别处的地板来套 ——「拍阈值等于把结论写进判据」。
            // 用返回值交给 g-2 而不是一个 static 字段：static 的话「谁先跑」变成一件
            // 要读代码才知道的事，而 ⑯d2 刚刚才因为「地板取错了对象」栽过一次。
            tFloor = float.IsNaN(repeatability) ? range : Mathf.Max(range, repeatability);
            sb.AppendLine($"  ⓘ 由此定出 T50 的可分辨地板 = max(极差 {range:F1}, 重复性 {repeatability:F1})"
                        + $" = **{tFloor:F1} 帧**，g-2 用它。");
            return identical;
        }

        static bool ReportSeparation(StringBuilder sb, List<Lag> runs, float tFloor)
        {
            sb.AppendLine();
            float a = k_StepAmplitudes[1];
            float rel0 = a / (1f + a);
            sb.AppendLine($"⑤ g-2：阶跃 +{a * 100f:F0}%（rel₀ = {rel0:F3}）夹在 0.25 与 0.55 之间");
            sb.AppendLine("   ⇒ 下端 0.25 那一档**应当拒绝**这次阶跃（快），"
                        + "0.55 与关闭档**不拒**（慢）。方向写死在判据里，不是事后解读。");

            var group = runs.FindAll(r => Mathf.Approximately(r.amplitude, a));
            group.Sort((x, y) => x.rejectStart.CompareTo(y.rejectStart));
            foreach (var r in group)
            {
                bool shouldReject = rel0 > r.rejectStart;
                sb.AppendLine($"     下端 {r.rejectStart:F2} → 预言{(shouldReject ? "拒绝（快）" : "不拒（慢）")}"
                            + $"，实测 T50 {r.t50:F1}，T90 {r.t90:F1}");
            }

            var fast = group.Find(r => rel0 > r.rejectStart);
            var slow = group.Find(r => rel0 <= r.rejectStart);
            if (fast == null || slow == null)
            {
                sb.AppendLine("  ✘ 扫描点里凑不出「一个该拒、一个不该拒」的配对 ⇒ 这一格不可判定。"
                            + "（k_StepAmplitudes 或 k_RejectSweep 被改过？两者是配套的。）");
                return false;
            }

            float ratio = slow.t50 / Mathf.Max(fast.t50, 1e-3f);
            bool resolvable = (slow.t50 - fast.t50) > tFloor;
            bool ok = resolvable && ratio >= k_SeparationMin;
            sb.AppendLine($"  {Mark(ok)}T50 慢/快 = {slow.t50:F1} / {fast.t50:F1} = **{ratio:F2}×**，"
                        + $"要求 ≥ {k_SeparationMin:F2}× 且差值 {slow.t50 - fast.t50:F1} 帧 "
                        + $"> 地板 {tFloor:F1} 帧");
            if (!resolvable)
                sb.AppendLine("     差值没过 g-1 定出来的地板 ⇒ **不可判定**，不是「没有差异」。"
                            + "这两件事在报表上必须分开。");
            return ok;
        }

        /// <summary>
        /// ⑯c 噪声侧曲线的**手抄**副本：下端 → w_eff/w。
        ///
        /// 手抄是一笔明账，不是偷懒：⑯c 与 ⑰ 是两个菜单项、两套布景、两次运行，
        /// 一次运行里拿不到另一次的读数。抄下来的代价是它会过期，所以：
        ///   · 出处写死在这里（判据⑯ 的 ⑤ 节标定表，2026-09-20 那一次运行）；
        ///   · 下面 ReportTradeoff 会把「线上那一档抄的是哪个数」连同**实际**的
        ///     luminanceRejectStart 一起印出来 —— 抄的值对不上线上档时，
        ///     报表里会缺那一行而不是印一个看似合理的数。
        /// 「一个非零的数回答不了它是从哪儿来的」在这里的形式就是：宁可缺行，不可编数。
        /// </summary>
        static readonly (float start, float wRatio)[] k_NoiseCurve =
        {
            (0.10f, 0.527f), (0.25f, 0.643f), (0.40f, 0.834f),
            (0.55f, 0.984f), (0.70f, 1.026f), (1.00f, 1.021f),
        };

        static string NoiseAt(float start)
        {
            foreach (var p in k_NoiseCurve)
                if (Mathf.Approximately(p.start, start))
                    return p.wRatio.ToString("F3");
            return "—（⑯c 没扫这个点）";
        }

        static void ReportTradeoff(StringBuilder sb, List<Lag> runs, float onlineStart)
        {
            sb.AppendLine();
            sb.AppendLine("⑥ ⓘ 两半曲线并排：抬高下端，两边各付什么");

            sb.AppendLine("   | 下端 | w_eff/w（⑯c 手抄，噪声态） | 一次 +50% 阶跃的 T50 | T99 |");
            sb.AppendLine("   |---|---|---|---|");
            foreach (float rs in k_RejectSweep)
            {
                var r = runs.Find(x => Mathf.Approximately(x.rejectStart, rs)
                                    && Mathf.Approximately(x.amplitude, k_StepAmplitudes[1]));
                if (r == null) continue;
                string tag = Mathf.Approximately(rs, onlineStart) ? "（线上）"
                           : Mathf.Approximately(rs, k_RejectDisabled) ? "（关闭）" : "";
                sb.AppendLine($"   | {rs:F2}{tag} | {NoiseAt(rs)} | {r.t50:F1} 帧 | {r.t99:F1} 帧 |");
            }
            sb.AppendLine($"   ⓘ 线上实际值 luminanceRejectStart = {onlineStart:F2}"
                        + (System.Array.Exists(k_RejectSweep, x => Mathf.Approximately(x, onlineStart))
                           ? "（在本次扫描点里）"
                           : " ← **不在本次扫描点里**，上表没有一行描述线上档"));

            sb.AppendLine();
            sb.AppendLine("   ---- 2026-09-20 的定值：0.25 → 0.55。三条理由，第一条是决定性的 ----");
            sb.AppendLine("   ① ⑯a-1 是一道**已经存在**的门，它红着，而红的原因就是 0.25。");
            sb.AppendLine("      它要求 R ≥ R_white×0.75 = 5.35；实测 0.25→2.06 ✘、0.40→3.02 ✘、0.55→6.00 ✔。");
            sb.AppendLine("      所以 0.40 不是折中而是纯亏损：付了判别力，仍然过不了去噪门。");
            sb.AppendLine("      可选集合只有 {0.55, 0.70, 1.00}，0.55 是其中最小的 ——");
            sb.AppendLine("      再往上买不到去噪（0.70 的 1.026 与关闭的 1.021 已同量级），只会白扔判别力。");
            sb.AppendLine("   ② 0.55 的拖影代价实测为零：T50 与「关闭」差 0.5 帧 < g-1 定出的可分辨地板。");
            sb.AppendLine("   ③ 噪声是**常驻**缺陷（⑯c 实测屏幕 p90 相对噪声在 0.25 下是 1.5× Weber，");
            sb.AppendLine("      静止画面也在闪），拖影是**偶发**的。常驻优先于偶发。");
            sb.AppendLine();
            sb.AppendLine("   ★ 但 0.55 不是「把死区调好了」，是承认它在当前形态下不工作：");
            sb.AppendLine("     死区在下端 0.10 就吃掉一半权重（0.527），而屏幕上单样本相对噪声的**最大值**");
            sb.AppendLine("     才 6.6e-02。两个数放一起只有一个解释 —— rel 是**逐 froxel** 算的");
            sb.AppendLine("     （FroxelVolume.hlsl:598），而沿视线 64 片求和本身就是一次 8 倍降噪。");
            sb.AppendLine("     成因（**推断，未实测**）：逐 froxel 只做 1 tap 硬阴影，骑在阴影边界上的格子");
            sb.AppendLine("     每帧在全亮/全暗之间整跳 ⇒ 那里 rel 冲到 1。于是死区优先关掉的正是");
            sb.AppendLine("     **阴影边界上**的累积 —— 而那里恰好是累积唯一必要的地方，它与需求反相关。");
            sb.AppendLine("     根子是信息不够：这里只有「本帧」「历史」两个标量，没有屏幕空间 TAA 那样的");
            sb.AppendLine("     邻域统计去区分「这次变化是噪声还是信号」，任何标量阈值都分不开两个重叠的分布。");
            sb.AppendLine("     修法：1 tap → PCF（砍掉 rel 尖峰），或换成带 froxel 邻域方差的 clamp。");
            sb.AppendLine("     验证这条推断需要一张**逐 froxel rel 的直方图**，目前**缺**。");
            sb.AppendLine();
            sb.AppendLine("   ⚠ 这张表**不替美术拍板**，它只是把「多少拖影可以忍」从一句形容词"
                        + "换成了一个毫秒数。真正的判准是：游戏里最小的、必须被立刻跟上的"
                        + "亮度变化有多大？它的 rel 若低于选定的下端，那次变化会吃满慢尾。");
            sb.AppendLine("   ⚠ 还有一条这套布景量不到：阶跃是**全屏**的，而真实的遮挡"
                        + "（人走过光轴）是**局部**的，局部阶跃会在运动边缘留下一条拖尾，"
                        + "它的可见性取决于边缘对比度而不是 T50。要量那个，需要一个带"
                        + "运动物体的布景 —— 记为**缺口**，不要把 T50 当成它的代理。");
        }

        // ---------------------------------------------------------------- 小工具
        //  Median / Percentile / Sci 一律用 VistaFroxelShadowRig 的那一份。
        //  这里**不**另写一个「非破坏性的 Median」，哪怕它只有三行：
        //  「同一个量的第二份实现连三行的约简也算」——两处各算一遍，
        //  就会在某次改动里各自漂移（比如偶数长度取中点还是取上位）。
        //  代价是 rig 的 Median 会就地排序，所以调用处必须容忍传进去的 List 被重排；
        //  本文件的三处调用都是「算完就不再看顺序」，且排序不改 Min/Max/Percentile。

        static string Mark(bool ok) => ok ? "✔ " : "✘ ";
    }
}
