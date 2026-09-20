using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using static Vista.EditorTools.VistaFroxelShadowRig;

namespace Vista.EditorTools
{
    /// <summary>
    /// #27 验收第 2 项：**近层 froxel 的时间收敛性与抖动无偏性**（判据⑯ + #22b 残余谱）。
    ///
    /// ── 这一项要回答的四个问题 ──
    ///   ⑯a 时间累积**确实在降噪**吗？降了多少？降幅与配置的 τ 对得上吗？
    ///   ⑯b 抖动把答案推向真值了，还是推歪了？（「打开抖动之后雾是不是浓了一点」）
    ///   ⑯c 丢掉历史的那一帧，画面上要付多大代价？（<c>luminanceRejectStart</c> 的定标依据）
    ///   ⑯d 程序化与蓝噪声两档，收敛到同一个极限吗？残余场的空间性质真的不同吗？
    ///
    /// ── 为什么不加第九个探针核、不加 slot ──
    /// CHANGELOG 里给这一项留的成本估计是「加一个核 / 让重投影探针去读注入表」。
    /// 那条路是多余的：**布景是静态的**（<see cref="VistaFroxelShadowRig"/> 的相机与
    /// 太阳都不动），所以每个像素的「真值」是一个常数，逐帧读回得到的
    /// **时间序列的标准差就精确地等于噪声本身** —— 没有任何空间混淆。
    ///
    /// 这一点值得说死：如果改用「逐 froxel 的**空间**标准差」当噪声度量，量到的
    /// 是「真实雾的空间变化 + 噪声」两项之和，而前者在光柱边缘比后者大两个数量级。
    /// 那种尺子会把一个噪声减半的改动报成「几乎没变」——
    /// 「尺子的地板与被测量同量级时，尺子会自己伪造一个结论」。
    ///
    /// ── σ_x 是怎么量到的：不需要「把滤波关掉」这个状态 ──
    /// 降噪幅度 = 单样本噪声 σ_x ÷ 累积后噪声 σ_y。难点在 σ_x：
    ///   · 把 <c>jitterMode</c> 设成 Off 会**同时**关掉抖动，量到的不是同一个信号；
    ///   · 把 τ 拉到最小也压不到 0（Δt 被 <c>k_MinDeltaTime</c> 夹住，w 最低只到 e⁻¹）。
    /// 真正干净的办法是 <see cref="VistaFroxelReprojection.Invalidate"/>：**每帧**调它一次，
    /// 历史就每帧都不可用（w ≡ 0），而抖动分毫未动。于是 σ_x 是**实测**的，
    /// 不依赖任何模型。顺带地，这个档位本身就是⑯c 要的「丢历史的那一帧」。
    ///
    /// ── 理论锚点，以及它为什么是单边的 ──
    /// 历史滤波是 AR(1)：<c>y_t = w·y_{t−1} + (1−w)·x_t</c>。
    /// 驱动 x 若是白噪声，则 <c>var(y)/var(x) = (1−w)/(1+w)</c>，
    /// 降幅 <c>R_white = √((1+w)/(1−w))</c>，且 <c>ρ₁(y) = w</c>。
    ///
    /// 但本项目的驱动**刻意不是白噪声**：R3 Kronecker 序列让相邻帧的抖动相位
    /// 负相关（那正是低差异序列的全部意义）。负相关的驱动会让滤波**多**压一点，
    /// 于是 <c>R ≥ R_white</c>，同时 <c>ρ₁ ≤ w</c>。两个偏差来自同一个原因、
    /// 方向相反 —— 所以这里的两条门都是**单边**的，且门槛取自解析预测而不是手拍：
    ///   a-1  R ≥ R_white·(1 − 容差)      ← 滤波比理论差 ⇒ 没在跑 / τ 没接上
    ///   a-2  ρ₁ ≤ w + 容差               ← 比理论强 ⇒ w 比下发的大（滤波过度）
    /// 一条双边等式门会被「低差异序列正常工作」本身打红，那是把正确行为判成错误。
    ///
    /// ── 有限序列的方差偏低，必须显式改正 ──
    /// 对一个自相关 ρ 的序列，n 个样本估出来的方差期望是
    /// <c>E[s²] = σ²·(n − τ_int)/(n − 1)</c>，其中 <c>τ_int = 1 + 2Σ(1−k/n)ρᵏ</c>。
    /// 默认 τ=0.33 s、Δt≈1/60 时 w≈0.95 ⇒ τ_int≈39 ⇒ n=96 时 s² 只有真值的 **0.6 倍**。
    /// 不改正的话 R 会被高估约 29%，而报表会把它读成「R3 序列比理论好 29%」——
    /// 一个完全由估计量伪影造出来的结论。所以这里用**实测 ρ₁** 算 τ_int 并除掉它，
    /// 原始值与改正值都印出来。
    ///
    /// ── ⑯b 为什么是一道**弱**门 ──
    /// 片心采样已经是切片平均的中点法则（二阶精度）。深度抖动的期望是
    /// **按片号均匀**的平均，而分段常数积分要的是**按距离**的平均 —— 两者不相等。
    /// 所以「抖动一定更接近参考解」**没有保证**，把它写成门就是把一条未经证明的
    /// 断言当判据。这里的门只断言诚实的那一半：
    /// **抖动不得把答案推得更远，超出尺子地板**。真正的读数是按亮度分桶的
    /// **带号**相对偏差 —— 它直接回答「打开抖动之后雾是浓了还是淡了」。
    ///
    /// ── ⑯d 为什么拆成两格 ──
    /// <see cref="JitterMode.BlueNoise"/> 自己的文档就写着：两档的**时间极限是同一个**
    /// （都是抖动分布上的期望），空间上的好处又大半被 screen/8 的双线性上采样吃掉。
    /// 于是：
    ///   d1 = μ_proc ≡ μ_blue 的恒等式门（真的能抓错：任一档的抖动分布有偏就红）；
    ///   d2 = 残余场的空间 lag 曲线，**读数**；只有当尺子分辨得出来时才升成门。
    /// 「不可判定必须是缺口，不能是通过」。
    ///
    /// ── 空间相关必须取 lag 8 而不是 lag 1 ──
    /// froxel 在屏幕上的足迹是 screen/8 ⇒ **8×8 像素**。同一个 froxel 内部的 8 个
    /// 相邻像素是同一个值的双线性插值，lag 1 的相关恒为正且与抖动源无关。
    /// 所以这里把 lag 1..16 整条曲线都印出来，让尺子的口径本身可以被证伪。
    /// </summary>
    static class VistaFroxelConvergenceSelfTest
    {
        // ---------------------------------------------------------------- 采样口径

        /// <summary>每档采多少帧。96 帧在 w≈0.95 下给出 τ_int≈39、偏低因子≈0.6 ——
        /// 还在可改正的范围内；再短（如 32 帧）偏低因子会掉到负数，方差根本估不出来。</summary>
        const int k_Frames = 96;

        /// <summary>
        /// 有历史的档位换档后空转多少帧，**不是常数** —— 见 <see cref="SettleFramesFor"/>。
        /// 这里只保留一个预跑段：先渲这么多帧把 w 读出来，再按 w 算还要空转几帧。
        ///
        /// 为什么不能写死：残余历史权重是 w^k，而 w = exp(−Δt/τ) 里的 Δt 是**编辑器的
        /// 帧间隔**，判据控制不了它。第一版写死 96 帧，实测 Δt = 12.97 ms ⇒ w = 0.9615
        /// ⇒ w^96 = 2.3e−2，差一点点不够 —— 而「差一点点」在另一台机器上就是「差很多」。
        /// 「一个不稳的 dt 会让解析预测无处可锚」，所以锚要挂在实测的 w 上。
        /// </summary>
        const int k_SettlePreroll = 12;

        /// <summary>空转帧数的上限。超过它说明 Δt 小到 w→1（比如编辑器在以极高帧率空转），
        /// 那时与其跑一万帧不如判不可判定 —— 下面的残余权重门会替它红。</summary>
        const int k_MaxSettleFrames = 4000;

        /// <summary>无历史的档位（w = 0）自然只跑预跑段 —— 它们只需要等资源重分配
        /// 与第一帧的注入写完。</summary>
        const int k_RefSliceCount = 256;

        /// <summary>
        /// 按实测的历史权重算「空转到残余 ≤ <see cref="k_MaxSettleResidual"/> 要几帧」：
        /// w^k ≤ r ⇔ k ≥ ln r / ln w。再乘 1.2 留余量（w 在运行中会随 Δt 小幅摆动）。
        /// </summary>
        static int SettleFramesFor(float w)
        {
            if (!(w > 0f)) return 0;                       // 没有历史 ⇒ 不需要空转
            if (w >= 1f) return k_MaxSettleFrames;         // 永不衰减 ⇒ 让残余权重门去红
            double k = System.Math.Log(k_MaxSettleResidual) / System.Math.Log(w);
            return Mathf.Clamp(Mathf.CeilToInt((float)(k * 1.2)), 0, k_MaxSettleFrames);
        }

        /// <summary>⑯a-1 的容差。σ 比值在 n 个样本下的相对抽样误差约 1/√(2n_eff)，
        /// 报表里会把解析抽样误差印出来供核对；0.25 留了约两倍余量。</summary>
        const float k_RatioTolerance = 0.25f;

        /// <summary>⑯a-2 的容差。ρ̂₁ 的逐像素抽样标准差 ≈ √((1−ρ²)/n)，且小样本偏差是
        /// **向下**的（对一道上边门而言是安全方向）。同样把解析值印出来。</summary>
        const float k_RhoTolerance = 0.05f;

        /// <summary>
        /// 残余历史权重 w^settle 的上限 = Weber 阈值的十分之一。
        ///
        /// 门槛取自项目已有的可见性口径而不是手拍：没空转够时 μ 里残留的是
        /// 「上一档的值 × w^settle」，而⑯b 量的正是 1% 量级的 μ 差。
        /// 残余若与被测量同量级，「尺子的地板与被测量同量级时，尺子会自己伪造一个结论」。
        /// 压到十分之一，残余就退到尺子之下。
        /// </summary>
        const float k_MaxSettleResidual = k_WeberThreshold * 0.1f;

        /// <summary>有效像素的绝对地板。fp32 读回在 1e−2 量级上的 ulp 约 1e−9，
        /// 1e−7 比它高两个数量级，同时远低于 Weber 1% 关心的尺度。</summary>
        const float k_AbsFloor = 1e-7f;

        /// <summary>空间相关谱取到多少 lag。froxel 足迹 8 px，取 16 是为了把
        /// 「跨一个 froxel」与「跨两个 froxel」都看见。</summary>
        const int k_MaxLag = 16;

        const string k_InvalidateReason = "判据⑯：刻意每帧丢历史以实测单样本噪声";

        /// <summary>
        /// 把亮度死区整条关掉的下端取值。
        ///
        /// 不取一个「很大的数」：<c>ResolveLuminanceReject</c> 会 Clamp01，所以 1.0 就是能取到的上界，
        /// 而 <c>rel</c> 本身被 saturate 到 [0,1] ⇒ <c>rel − 1 ≤ 0</c> ⇒ 过渡量 t 恒为 0，
        /// 与过渡区宽度（会被兜底成 start + 1e-3）无关。**关掉的路径与线上是同一份代码**，
        /// 不是另写一条「跳过死区」的分支 —— 否则对照组测的就不再是线上那份实现。
        /// </summary>
        const float k_RejectDisabled = 1f;

        /// <summary>
        /// ⑯c 的标定扫描点（线上值 0.25 与关闭态 1.0 由 A / A₀ 两档提供，不在这里重复渲）。
        ///
        /// 为什么必须扫而不是算：死区吃掉多少权重，取决于**froxel 单片**口径的抖动散布，
        /// 而⑯能读回的只有屏幕口径（沿 64 片积分 + screen/8 双线性上采样之后，
        /// 相对散布小一到两个量级）。用屏幕口径的分位数去推一个作用在 froxel 口径上的门槛，
        /// 是拿一把量错了对象的尺子拍板 —— 「拍阈值等于把结论写进判据」。
        /// 扫描测的是同一条线上路径的实际后果，不需要中间那层换算。
        /// </summary>
        static readonly float[] k_RejectSweep = { 0.10f, 0.40f, 0.55f, 0.70f };

        /// <summary>
        /// 推荐值采用的判准：死区吃掉的历史权重不超过这个比例。
        /// 它是**推荐**不是门 —— 曲线本身才是这一格的产物，见 ReportRejectCalibration 的收尾。
        /// </summary>
        const float k_RejectKneeKeep = 0.95f;

        // ---------------------------------------------------------------- 入口

        [MenuItem("Window/Vista/Acceptance: Froxel Convergence (Temporal)", priority = 137)]
        static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 判据⑯：近层 froxel 时间收敛性 + 抖动无偏性（#27 第 2 项）===");
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

        sealed class Cfg
        {
            public string name;

            /// <summary>逐像素的时间均值。</summary>
            public double[] mean;

            /// <summary>逐像素的时间标准差（样本，未做有限序列改正）。</summary>
            public double[] sd;

            /// <summary>逐像素的一阶自相关；方差为 0 的像素记 NaN。</summary>
            public double[] rho1;

            /// <summary>最后一帧的原始读回。⑯d2 的残余场用它。</summary>
            public float[] lastFrame;

            /// <summary>采样结束时 CPU 侧实际下发的历史权重与 Δt。</summary>
            public float wCpu, dtCpu;

            public int framesValid;
            public string invalidReason;

            /// <summary>这一档实际空转了多少帧（预跑段 + 按 w 算出来的补足段）。</summary>
            public int settleFrames;
            /// <summary>这一档实际生效的亮度死区下端。见 <see cref="Measure"/> 的同名参数。</summary>
            public float rejectStart;

            /// <summary>抖动源有没有回落（选了蓝噪声但取不到图）。null = 没回落。
            /// ⑯d1 必须印它：回落之后 D 档就**是** A 档，那一格会恒真。</summary>
            public string jitterFallback;
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
            var state = RigState.Capture(fog, vf);

            // 这三项 RigState 不管（它只管两个消费者**共有**的那一档），由本判据自己存 ——
            // 与 VistaFroxelModeAcceptance 自己存 farDistanceMeters 是同一条约定：
            // 谁改的谁负责还原，否则「某个判据跑完之后线上配置变了」会变成无主的事。
            int prevSlices = vf.sliceCount;
            var prevJitter = vf.jitterMode;
            float prevRejStart = vf.luminanceRejectStart;

            RenderTexture rt = null;
            Texture2D tex = null;
            GameObject root = null;
            Material mat = null;
            bool prevForceNoShadow = VistaFroxelVolume.s_DebugForceNoSunShadow;

            try
            {
                state.Apply();
                VistaFroxelVolume.s_DebugForceNoSunShadow = false;

                rt = new RenderTexture(k_RtSize, k_RtSize, 24,
                                       RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                Build(litShader, layer, feature.groundLevelWorldY, rt,
                      out root, out Camera cam, out mat, out Light sun);
                RenderSettings.sun = sun;

                vf.sliceCount = prevSlices;
                vf.jitterMode = JitterMode.Procedural;
                for (int i = 0; i < k_WarmupFrames; i++) cam.Render();

                var reproj = feature.froxelReprojection;
                if (!VerifyPreconditions(sb, volume, cam, reproj, vf)) return false;

                tex = new Texture2D(k_RtSize, k_RtSize, TextureFormat.RGBAFloat, false, true);

                // ---- 七个档位 ----
                // A  线上档：64 片 + 程序化抖动 + 历史开   → σ_y, ρ₁, μ_A
                // A0 同 A，但把亮度死区整条关掉            → a-4 的对照组，见下
                // A' 同 A 但每帧丢历史                      → σ_x, μ_A'  （⑯c 也是它）
                // B  64 片 + 抖动 Off                      → 片心样本 μ_B，同时是读回地板
                // C  256 片 + 程序化抖动 + 历史开          → 参考解 μ_C
                // D  64 片 + 蓝噪声 + 历史开               → μ_D
                // D' 同上但每帧丢历史                      → 蓝噪声的单帧残余场
                //
                // ---- A0 为什么必须存在 ----
                //  GPU 上真正乘进 lerp 的权重是 VISTA_FROXEL_HISTORY_WEIGHT × lumWeight
                //（FroxelVolume.hlsl:660），而 CPU 侧的 lastHistoryWeight 只是前一个因子。
                //  也就是说 a-1 一旦红，报表无法区分两件事：滤波本身接错了（τ 没下发、
                //  历史采样点错位），还是滤波是对的、只是亮度死区把权重逐 froxel 乘小了。
                //  两者的修法完全不同，而「一个数回答不了它是从哪儿来的」。
                //  A0 把第二个因子钉成 1，于是 a-1 与 a-4 的红/绿组合直接给出归因。
                var A = Measure("A 线上档（64 片・程序化・历史开）", cam, rt, tex, vf, reproj,
                                prevSlices, JitterMode.Procedural, false, prevRejStart);
                var A0 = Measure("A₀ 对照（64 片・程序化・历史开・亮度死区关）", cam, rt, tex, vf, reproj,
                                prevSlices, JitterMode.Procedural, false, k_RejectDisabled);
                var Af = Measure("A′ 单样本（64 片・程序化・每帧丢历史）", cam, rt, tex, vf, reproj,
                                prevSlices, JitterMode.Procedural, true, prevRejStart);
                var B = Measure("B 片心（64 片・抖动 Off）", cam, rt, tex, vf, reproj,
                                prevSlices, JitterMode.Off, false, prevRejStart);
                var C = Measure($"C 参考解（{k_RefSliceCount} 片・程序化・历史开）", cam, rt, tex, vf, reproj,
                                k_RefSliceCount, JitterMode.Procedural, false, prevRejStart);
                var D = Measure("D 蓝噪声（64 片・蓝噪声・历史开）", cam, rt, tex, vf, reproj,
                                prevSlices, JitterMode.BlueNoise, false, prevRejStart);
                var Df = Measure("D′ 单样本（64 片・蓝噪声・每帧丢历史）", cam, rt, tex, vf, reproj,
                                prevSlices, JitterMode.BlueNoise, true, prevRejStart);

                // ⑯c 的标定扫描。放在这里而不是 ReportRejectCalibration 里：渲染都归这一层，
                // 报表层只拿数组 —— 否则「报表函数会不会改被测对象」变成一件要读代码才知道的事。
                var sweep = new List<Cfg>();
                foreach (float rs in k_RejectSweep)
                {
                    sweep.Add(Measure($"⑯c 扫描点（死区下端 {rs:F2}・历史开）", cam, rt, tex, vf, reproj,
                                      prevSlices, JitterMode.Procedural, false, rs));
                }
                sweep.Add(A);
                sweep.Add(A0);
                sweep.Sort((x, y) => x.rejectStart.CompareTo(y.rejectStart));

                bool ok = true;
                ok &= ReportWiring(sb, A, A0, Af, B, C, D, Df);
                if (!ok)
                {
                    sb.AppendLine("  ⇒ 各档的历史状态与预期不符，**不往下量** ——"
                                + "在一个 w 不对的序列上算出来的降幅会是一个看起来正常的假读数。");
                    return false;
                }

                // ---- 有效像素 ----
                float floorSd = Percentile(ToF(B.sd), 0.99f);
                var mask = BuildMask(A, Af, B, C, floorSd, out int masked);
                sb.AppendLine();
                sb.AppendLine("① 有效像素（尺子地板取自 B 档 —— 它既不抖也不混历史，"
                            + "逐帧应当逐位相同，于是它的时间标准差就是读回链路自己的地板）");
                sb.AppendLine($"  B 档时间标准差 p50/p90/p99/max = {Percentiles(ToF(B.sd))}");
                sb.AppendLine($"  地板 = p99(B.sd) = {Sci(floorSd)}；绝对地板 = {Sci(k_AbsFloor)}");
                sb.AppendLine($"  有效像素 {masked} / {A.mean.Length}"
                            + $"（{100.0 * masked / A.mean.Length:F1}%），要求"
                            + " σ_x > 两个地板 且 μ_C > 绝对地板");
                if (masked < 1024)
                {
                    sb.AppendLine("  ✘ 有效像素少于 1024，后面所有分位数都没有统计意义 ⇒ 不可判定。");
                    return false;
                }

                ok &= ReportConvergence(sb, A, A0, Af, mask, masked);
                ok &= ReportBias(sb, A, B, C, mask, masked);
                ReportRejectCalibration(sb, A, Af, mask, sweep);
                ok &= ReportJitterSourceIdentity(sb, A, D, mask, masked);
                ReportSpatialSpectrum(sb, Af, Df, B, mask, vf.screenDivisor);

                return ok;
            }
            finally
            {
                VistaFroxelVolume.s_DebugForceNoSunShadow = prevForceNoShadow;
                vf.sliceCount = prevSlices;
                vf.jitterMode = prevJitter;
                vf.luminanceRejectStart = prevRejStart;
                state.Restore();
                if (tex != null) Object.DestroyImmediate(tex);
                if (root != null) Object.DestroyImmediate(root);
                if (mat != null) Object.DestroyImmediate(mat);
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
            }
        }

        // ---------------------------------------------------------------- 前提

        static bool VerifyPreconditions(StringBuilder sb, VistaFroxelVolume volume, Camera cam,
                                        VistaFroxelReprojection reproj, VistaVolumetricFogSettings vf)
        {
            sb.AppendLine("⓪ 前提（不成立时下面每一格都会给出看起来正常的假读数）");

            var desc = volume.allocatedDesc;
            if (!desc.HasValue)
            {
                sb.AppendLine("  ✘ 近层体一次都没分配下来（allocatedDesc 为 null）。");
                return false;
            }

            var d = desc.Value;
            bool covered = k_OccluderZ < d.handoffMeters;
            sb.AppendLine($"  {Mark(covered)}遮挡物 z = {k_OccluderZ:F0} m < 近层接手点 {d.handoffMeters:F1} m"
                        + $"（体 {d.width}×{d.height}×{d.depth}，far {d.farMeters:F1} m）");

            var sun = RenderSettings.sun;
            Vector3 fwd = sun != null ? sun.transform.forward : Vector3.zero;
            bool sunOk = sun != null && fwd.z < -0.5f && fwd.y < -0.1f;
            sb.AppendLine($"  {Mark(sunOk)}太阳 forward = ({fwd.x:F3}, {fwd.y:F3}, {fwd.z:F3})，"
                        + "要求 z < −0.5 且 y < −0.1");

            bool camOk = cam.cameraType == CameraType.Game;
            sb.AppendLine($"  {Mark(camOk)}cameraType = {cam.cameraType}");

            // 重投影对象必须拿得到 —— 整条⑯a 的 CPU 侧操作数（w、Δt）都从它来。
            // 拿不到时若只报「降幅 = 6.1」，那个数就回答不了它是从哪儿来的。
            bool reprojOk = reproj != null;
            sb.AppendLine($"  {Mark(reprojOk)}能拿到 VistaFroxelReprojection"
                        + (reprojOk ? $"（本帧 w = {Fmt(reproj.lastHistoryWeight)}，"
                                    + $"Δt = {Fmt(reproj.lastDeltaTime)} s，"
                                    + $"连续有效 {reproj.framesSinceValid} 帧）"
                                    : "（VistaAtmosphereFeature.froxelReprojection 为 null）"));

            // 收敛判据的全部前提是「布景是静态的」：静态时逐像素真值是常数，
            // 时间标准差就精确等于噪声。这里不判相机/太阳有没有动（Build 之后没人动它们），
            // 但把 τ 与预期 w 印出来，好让「w 落在一个让 96 帧空转不够的区间」被看见。
            float tau = vf.historyTimeConstant;
            sb.AppendLine($"  ⓘ historyTimeConstant τ = {Fmt(tau)} s，"
                        + $"sliceCount（线上）= {vf.sliceCount}，"
                        + $"jitterMode（线上）= {vf.jitterMode}");

            bool all = covered && sunOk && camOk && reprojOk;
            if (!all) sb.AppendLine("  ⇒ 前提不成立，**不往下量**。");
            return all;
        }

        // ---------------------------------------------------------------- 采一档

        /// <param name="rejectStart">
        /// 亮度死区的下端。传 <see cref="k_RejectDisabled"/> 时死区整条失效
        /// （rel 由 saturate 有界到 [0,1]，下端摆到 1.0 之后 rel − 1 ≤ 0 ⇒ t ≡ 0 ⇒ lumWeight ≡ 1），
        /// 于是 GPU 上实际用的权重就恰好是 CPU 下发的那个 w —— 这是 a-4 对照组存在的全部理由。
        /// </param>
        static Cfg Measure(string name, Camera cam, RenderTexture rt, Texture2D tex,
                           VistaVolumetricFogSettings vf, VistaFroxelReprojection reproj,
                           int slices, JitterMode jitter, bool forceInvalid, float rejectStart)
        {
            vf.sliceCount = slices;
            vf.jitterMode = jitter;
            vf.luminanceRejectStart = rejectStart;

            // 先跑一个预跑段，把这一档实际的 w 读出来；再按它算还要空转几帧。
            for (int i = 0; i < k_SettlePreroll; i++)
            {
                if (forceInvalid) reproj.Invalidate(k_InvalidateReason);
                cam.Render();
            }

            int settle = k_SettlePreroll;
            int extra = forceInvalid ? 0 : SettleFramesFor(reproj.lastHistoryWeight);
            for (int i = 0; i < extra; i++)
            {
                cam.Render();
            }
            settle += extra;

            var series = new float[k_Frames][];
            for (int i = 0; i < k_Frames; i++)
            {
                if (forceInvalid) reproj.Invalidate(k_InvalidateReason);
                cam.Render();
                series[i] = ReadLuminance(rt, tex);
            }

            var cfg = new Cfg
            {
                rejectStart = vf.luminanceRejectStart,
                name = name,
                lastFrame = series[k_Frames - 1],
                wCpu = reproj.lastHistoryWeight,
                dtCpu = reproj.lastDeltaTime,
                framesValid = reproj.framesSinceValid,
                invalidReason = reproj.lastInvalidReason,
                jitterFallback = reproj.jitterFallbackReason,
                settleFrames = settle,
            };
            Reduce(series, out cfg.mean, out cfg.sd, out cfg.rho1);
            return cfg;
        }

        /// <summary>
        /// 只读回，不渲染 —— 与 <see cref="VistaFroxelShadowRig.CaptureLuminance"/> 的区别
        /// 就在这里：那一个是「渲 n 帧然后读最后一帧」，⑯ 要的是**每帧都读**。
        /// 没有把它塞进 rig：rig 的类文档写着它只放两个消费者共有的东西，而逐帧序列
        /// 只有⑯要。
        /// </summary>
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

        /// <summary>
        /// 逐像素归约。**全程 double**：序列值在 1e−2 量级、方差在 1e−10 量级，
        /// 用 float 走 Σx² − mean² 会把有效位全部消掉，得到的方差可能是负数。
        /// 这里走两遍（先均值再中心矩），连 Σx² 的形式都不用。
        /// </summary>
        static void Reduce(float[][] series, out double[] mean, out double[] sd, out double[] rho1)
        {
            int n = series.Length;
            int np = series[0].Length;
            mean = new double[np];
            sd = new double[np];
            rho1 = new double[np];

            for (int p = 0; p < np; p++)
            {
                double s = 0.0;
                for (int t = 0; t < n; t++) s += series[t][p];
                double m = s / n;

                double c0 = 0.0, c1 = 0.0;
                double prevDev = series[0][p] - m;
                c0 += prevDev * prevDev;
                for (int t = 1; t < n; t++)
                {
                    double dev = series[t][p] - m;
                    c0 += dev * dev;
                    c1 += dev * prevDev;
                    prevDev = dev;
                }

                mean[p] = m;
                sd[p] = System.Math.Sqrt(c0 / (n - 1));
                rho1[p] = c0 > 0.0 ? c1 / c0 : double.NaN;
            }
        }

        // ---------------------------------------------------------------- 接线核对

        static bool ReportWiring(StringBuilder sb, params Cfg[] cfgs)
        {
            sb.AppendLine();
            sb.AppendLine("② 各档的历史状态（**双边**核对：该有历史的必须有，该没有的必须没有）");
            sb.AppendLine("   一道只检查「有历史的那几档 w > 0」的门，会被「Invalidate 压根没生效」"
                        + "整个通过 —— 那时 A′ 和 A 是同一个东西，而降幅会报成 1.0 并被读成"
                        + "「滤波没在跑」，归因指向完全错误的地方。");

            bool ok = true;
            foreach (var c in cfgs)
            {
                bool wantHistory = c.name.Contains("历史开");
                bool has = c.wCpu > 0f;
                bool good = has == wantHistory;
                ok &= good;
                sb.AppendLine($"  {Mark(good)}{c.name}：w = {Fmt(c.wCpu)}，Δt = {Fmt(c.dtCpu)} s，"
                            + $"连续有效 {c.framesValid} 帧，"
                            + $"亮度死区下端 = {Fmt(c.rejectStart)}{(c.rejectStart >= k_RejectDisabled ? "（= 关闭）" : "")}，"
                            + $"失效原因 = {c.invalidReason ?? "（无，历史可用）"}"
                            + $" ⇒ 期望{(wantHistory ? "有" : "无")}历史");

                if (wantHistory && has)
                {
                    // 空转够不够。注意这里是一次**真正的**核对而不是同义反复：
                    // 空转帧数是用**预跑段末尾**那个 w 算出来的，而这里用的是
                    // **采样结束时**的 w —— 两次独立的读。Δt 若在这一档中途漂了
                    // （编辑器忙起来、别的窗口在重绘），两个 w 就对不上，这一格会红。
                    double residual = System.Math.Pow(c.wCpu, c.settleFrames);
                    bool settled = residual <= k_MaxSettleResidual;
                    ok &= settled;
                    sb.AppendLine($"     {Mark(settled)}空转 {c.settleFrames} 帧（按实测 w 自适应）后"
                                + $"残余历史权重 w^{c.settleFrames} = {Sci((float)residual)}"
                                + $"，要求 ≤ {Sci(k_MaxSettleResidual)}");

                    bool enough = c.framesValid >= k_Frames;
                    ok &= enough;
                    sb.AppendLine($"     {Mark(enough)}采样期间历史一次都没断"
                                + $"（framesSinceValid {c.framesValid} ≥ {k_Frames}）");
                }
            }
            return ok;
        }

        // ---------------------------------------------------------------- 有效像素

        static bool[] BuildMask(Cfg A, Cfg Af, Cfg B, Cfg C, float floorSd, out int count)
        {
            int np = A.mean.Length;
            var mask = new bool[np];
            double floor = System.Math.Max(floorSd, k_AbsFloor);
            count = 0;
            for (int p = 0; p < np; p++)
            {
                bool ok = Af.sd[p] > floor
                       && A.sd[p] > 0.0
                       && !double.IsNaN(A.rho1[p])
                       && C.mean[p] > k_AbsFloor;
                mask[p] = ok;
                if (ok) count++;
            }
            return mask;
        }

        // ---------------------------------------------------------------- ⑯a

        static bool ReportConvergence(StringBuilder sb, Cfg A, Cfg A0, Cfg Af, bool[] mask, int masked)
        {
            sb.AppendLine();
            sb.AppendLine("③ 判据⑯a：时间累积确实在降噪（4 操作数）");

            var rho = Gather(A.rho1, mask, masked);
            var sdY = Gather(A.sd, mask, masked);
            var sdX = Gather(Af.sd, mask, masked);

            float rhoMed = Percentile(rho, 0.5f);
            float w = A.wCpu;

            // 有限序列的方差偏低改正。用**实测** ρ₁ 而不是 w 去算 τ_int：
            // 要改正的是这条序列自己的相关，而不是配置里那个数。
            double tauInt = IntegratedAcTime(rhoMed, k_Frames);
            double biasY = (k_Frames - tauInt) / (k_Frames - 1.0);
            double tauIntX = IntegratedAcTime(Percentile(Gather(Af.rho1, mask, masked), 0.5f), k_Frames);
            double biasX = (k_Frames - tauIntX) / (k_Frames - 1.0);

            sb.AppendLine($"  ⓘ 有限序列改正：τ_int(A) = {tauInt:F2} ⇒ s² 只有真值的 {biasY:F3} 倍；"
                        + $"τ_int(A′) = {tauIntX:F2} ⇒ {biasX:F3} 倍");
            sb.AppendLine("     （不改正的话 96 帧、w≈0.95 下 R 会被高估约 29%，"
                        + "而报表会把这 29% 读成「低差异序列的功劳」）");

            if (biasY <= 0.0 || biasX <= 0.0)
            {
                sb.AppendLine($"  ✘ 序列太短，方差估不出来（偏低因子 ≤ 0）⇒ 不可判定。");
                return false;
            }

            float medY = Percentile(sdY, 0.5f);
            float medX = Percentile(sdX, 0.5f);
            double rRaw = medX / System.Math.Max(medY, 1e-30);
            double rCorr = rRaw / (System.Math.Sqrt(biasX) / System.Math.Sqrt(biasY));

            double rWhite = System.Math.Sqrt((1.0 + w) / System.Math.Max(1e-9, 1.0 - w));

            sb.AppendLine($"  ⓘ σ_x（单样本，A′）p50/p90/p99/max = {Percentiles(sdX)}");
            sb.AppendLine($"  ⓘ σ_y（累积后，A ）p50/p90/p99/max = {Percentiles(sdY)}");
            sb.AppendLine($"  ⓘ 实测降幅 R = σ_x/σ_y = {rRaw:F3}（原始）→ {rCorr:F3}（有限序列改正后）");
            sb.AppendLine($"  ⓘ 白噪声驱动的解析预测 R_white = √((1+w)/(1−w)) = {rWhite:F3}"
                        + $"（w = {Fmt(w)}，Δt = {Fmt(A.dtCpu)} s ⇒ 反算 τ = "
                        + $"{-A.dtCpu / Mathf.Log(Mathf.Max(w, 1e-9f)):F4} s）");

            // a-1：单边下门。比理论差 ⇒ 滤波没在跑或 τ 没接上。
            bool a1 = rCorr >= rWhite * (1.0 - k_RatioTolerance);
            sb.AppendLine($"  {Mark(a1)}a-1 R_corr {rCorr:F3} ≥ R_white·(1−{k_RatioTolerance:F2})"
                        + $" = {rWhite * (1.0 - k_RatioTolerance):F3}");
            sb.AppendLine("     单边的理由：R3 低差异序列让驱动逐帧**负**相关，滤波会比白噪声预测"
                        + "多压一点 ⇒ R ≥ R_white 是正常的。一道双边等式门会被「低差异序列正常工作」打红。");

            // a-2：单边上门。ρ₁ 比 w 大 ⇒ 实际混进去的历史比下发的多。
            double rhoSe = System.Math.Sqrt(System.Math.Max(0.0, 1.0 - rhoMed * rhoMed) / k_Frames);
            bool a2 = rhoMed <= w + k_RhoTolerance;
            sb.AppendLine($"  {Mark(a2)}a-2 实测 ρ₁（p50）= {rhoMed:F4} ≤ w + {k_RhoTolerance:F2} = {w + k_RhoTolerance:F4}");
            sb.AppendLine($"     ⓘ ρ̂₁ 的逐像素抽样标准差 ≈ √((1−ρ²)/n) = {rhoSe:F4}；"
                        + $"分位数 p10/p50/p90 = {Percentile(rho, 0.1f):F4} / {rhoMed:F4} / {Percentile(rho, 0.9f):F4}");
            sb.AppendLine($"     ⓘ ρ₁ 与 w 的差 = {Signed((float)(rhoMed - w))}。负值 = 驱动逐帧负相关"
                        + "（低差异序列在兑现），正值超出容差 = 混进去的历史比下发的多。");

            // a-3：滤波不得改变期望。**判在 A₀ 上**，不是 A 上。
            //  理由是这条恒等式的前提：AR(1) 的稳态期望恒等于驱动的期望，只在权重与**样本值无关**
            //  时成立。亮度死区恰恰是按「本帧离历史多远」去调权重的 —— 离得远就少混历史 ——
            //  那是一次有偏筛选，它改变期望是**定义使然**，不是缺陷的证据。
            //  把恒等式判在 A 上，等于用一条必然为假的断言去当门：它会永远红，而红的原因
            //  与 a-1 完全重复，报表上却看起来像两个独立的问题。
            //  A 那一档的偏差改成读数保留 —— 它量的是死区注入了多大的偏，随下端标定而收敛。
            var dAAf = new float[masked];
            var d0Af = new float[masked];
            int k = 0;
            for (int p = 0; p < mask.Length; p++)
            {
                if (!mask[p]) continue;
                dAAf[k] = (float)System.Math.Abs(A.mean[p] - Af.mean[p]);
                d0Af[k] = (float)System.Math.Abs(A0.mean[p] - Af.mean[p]);
                k++;
            }
            // 地板：两个均值各自的标准误。A₀ 的有效样本数是 n/τ_int，不是 n。
            // A₀ 的三个约简量在 a-3 与 a-4 上都要用，算一次 —— 同一个量的第二份实现
            // 连三行的约简也算，两处各算一遍就会在某次改动里各自漂移。
            var sdY0 = Gather(A0.sd, mask, masked);
            float rhoMed0 = Percentile(Gather(A0.rho1, mask, masked), 0.5f);
            double tauInt0 = IntegratedAcTime(rhoMed0, k_Frames);
            double bias0 = (k_Frames - tauInt0) / (k_Frames - 1.0);

            double seA0 = Percentile(sdY0, 0.5f)
                        / System.Math.Sqrt(k_Frames / System.Math.Max(1.0, tauInt0));
            double seAf = Percentile(sdX, 0.5f) / System.Math.Sqrt(k_Frames / System.Math.Max(1.0, tauIntX));
            double floorMean = 3.0 * System.Math.Sqrt(seA0 * seA0 + seAf * seAf);
            float d0Med = Percentile(d0Af, 0.5f);
            bool a3 = d0Med <= floorMean;
            sb.AppendLine($"  {Mark(a3)}a-3 滤波不改期望（判在 A₀ 上）：|μ_A₀ − μ_A′| p50 = {Sci(d0Med)}"
                        + $" ≤ 3σ 地板 {Sci((float)floorMean)}"
                        + $"（p90 = {Sci(Percentile(d0Af, 0.9f))}）");
            sb.AppendLine("     AR(1) 的稳态期望恒等于驱动的期望，所以这是一条**恒等式**而不是经验门。"
                        + "它能抓到「历史混进来的是别的东西」：反解的 uvw 错、片号错、上一档的残留。");
            sb.AppendLine($"     ⓘ 同一个量在**线上档**上 = {Sci(Percentile(dAAf, 0.5f))}"
                        + $"（p90 = {Sci(Percentile(dAAf, 0.9f))}）。它不判门：亮度死区按「本帧离历史多远」"
                        + "调权重，是一次有偏筛选，改变期望是定义使然。这个数量的是死区注入的偏，"
                        + "随 ⑯c 的下端标定收敛 —— 缺陷本身已由 a-1 判红，不在这里重复计一次。");

            // ---- a-4：把「滤波错了」与「死区把权重乘小了」分开 ----
            //  A0 与 A 只差 luminanceRejectStart 一个字段，于是 A0 上 GPU 实际用的权重
            //  就是 CPU 下发的 w 本身。a-1 与 a-4 的四种组合各自只对应一个结论：
            //    a-1 ✔ a-4 ✔ → 都对。
            //    a-1 ✘ a-4 ✔ → 滤波本身是对的，是死区下端摆得太低，把抖动自己的散布当成
            //                  「场景变了」在降权 —— 正是 FroxelVolume.hlsl:588 那段注释
            //                  点名的失效模式，修法是标定 luminanceRejectStart，不是动滤波。
            //    a-1 ✘ a-4 ✘ → 滤波本身就没接上（τ 没下发、历史采样点错位、表没 swap）。
            //    a-1 ✔ a-4 ✘ → 自相矛盾，说明这两档之间还有第三个变量没控住，判据本身有问题。
            double rRaw0 = Percentile(sdX, 0.5f) / System.Math.Max(Percentile(sdY0, 0.5f), 1e-30);
            double rCorr0 = bias0 > 0.0
                ? rRaw0 / (System.Math.Sqrt(biasX) / System.Math.Sqrt(bias0))
                : double.NaN;

            bool a4 = rCorr0 >= rWhite * (1.0 - k_RatioTolerance);
            sb.AppendLine();
            sb.AppendLine($"  {Mark(a4)}a-4 关掉亮度死区后 R_corr {rCorr0:F3} ≥ R_white·(1−{k_RatioTolerance:F2})"
                        + $" = {rWhite * (1.0 - k_RatioTolerance):F3}"
                        + $"（A₀ 的 ρ₁ p50 = {rhoMed0:F4}，同一个 w = {Fmt(A0.wCpu)}）");
            sb.AppendLine("     GPU 上真正乘进 lerp 的是 w × lumWeight（FroxelVolume.hlsl:660），"
                        + "CPU 的 lastHistoryWeight 只是前一个因子。a-1 与 a-4 的组合才构成归因；"
                        + "单独一个 R 回答不了它是从哪儿来的。");

            // ---- 两条独立的 w_eff 反解 ----
            //  同一个 AR(1) 有两条互不相干的路能反解出权重：
            //    由方差比：  var(y)/var(x) = (1−w)/(1+w)  ⇒ w = (R²−1)/(R²+1)
            //    由自相关：  ρ₁(y) = w
            //  **固定**权重下这两个数必须一致。逐帧逐 froxel 随机变动的权重会把它们劈开
            //（随机权重对自相关的破坏远大于对方差的压制），所以这两个数的比值本身
            //  就是「权重是不是常数」的一个读数 —— 它不需要再加任何探针。
            sb.AppendLine($"  ⓘ w_eff 的两条独立反解（固定权重时必须一致）：");
            sb.AppendLine($"     A ：由 R 得 {(rCorr * rCorr - 1.0) / (rCorr * rCorr + 1.0):F4}"
                        + $"，由 ρ₁ 得 {rhoMed:F4}");
            sb.AppendLine($"     A₀：由 R 得 {(rCorr0 * rCorr0 - 1.0) / (rCorr0 * rCorr0 + 1.0):F4}"
                        + $"，由 ρ₁ 得 {rhoMed0:F4}");
            sb.AppendLine($"     下发值 w = {Fmt(w)}。两条反解在某一档上劈开 ⇒ 那一档的权重不是常数。");

            return a1 && a2 && a3 && a4;
        }

        static int CountMask(bool[] mask)
        {
            int n = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i]) n++;
            return n;
        }

        /// <summary>
        /// 积分自相关时间 τ_int = 1 + 2·Σ_{k=1}^{n−1}(1 − k/n)·ρᵏ（AR(1) 近似）。
        /// 有限序列的样本方差期望是 σ²·(n − τ_int)/(n − 1)。
        /// </summary>
        static double IntegratedAcTime(double rho, int n)
        {
            if (!(rho > 0.0)) return 1.0;
            double s = 0.0, p = 1.0;
            for (int k = 1; k < n; k++)
            {
                p *= rho;
                s += (1.0 - (double)k / n) * p;
                if (p < 1e-12) break;
            }
            return 1.0 + 2.0 * s;
        }

        // ---------------------------------------------------------------- ⑯b

        static bool ReportBias(StringBuilder sb, Cfg A, Cfg B, Cfg C,
                               bool[] mask, int masked)
        {
            sb.AppendLine();
            sb.AppendLine($"④ 判据⑯b：抖动没有把答案推离参考解（参考 = {k_RefSliceCount} 片，同一条渲染路径）");
            sb.AppendLine("   这里的门刻意是**弱**的。片心采样已经是切片平均的中点法则（二阶精度），");
            sb.AppendLine("   而深度抖动的期望是按**片号**均匀的平均、分段常数积分要的是按**距离**的平均 ——");
            sb.AppendLine("   两者不相等，所以「抖动一定更接近真值」没有保证，写成门就是把断言当判据。");
            sb.AppendLine("   门只断言诚实的那一半：抖动不得把答案推得更远，超出尺子地板。");

            var dJit = new float[masked];
            var dCtr = new float[masked];
            var relJit = new float[masked];
            var relCtr = new float[masked];
            var refMean = new float[masked];
            int k = 0;
            for (int p = 0; p < mask.Length; p++)
            {
                if (!mask[p]) continue;
                double r = C.mean[p];
                dJit[k] = (float)System.Math.Abs(A.mean[p] - r);
                dCtr[k] = (float)System.Math.Abs(B.mean[p] - r);
                relJit[k] = (float)((A.mean[p] - r) / r);
                relCtr[k] = (float)((B.mean[p] - r) / r);
                refMean[k] = (float)r;
                k++;
            }

            // 地板：两个均值估计各自的标准误（3σ）。C 档也有历史滤波，用它自己的 ρ₁ 算 n_eff。
            double tauA = IntegratedAcTime(Percentile(Gather(A.rho1, mask, masked), 0.5f), k_Frames);
            double tauC = IntegratedAcTime(Percentile(Gather(C.rho1, mask, masked), 0.5f), k_Frames);
            double seA = Percentile(Gather(A.sd, mask, masked), 0.5f) / System.Math.Sqrt(k_Frames / System.Math.Max(1.0, tauA));
            double seC = Percentile(Gather(C.sd, mask, masked), 0.5f) / System.Math.Sqrt(k_Frames / System.Math.Max(1.0, tauC));
            double floorD = 3.0 * System.Math.Sqrt(seA * seA + seC * seC);

            float medJit = Percentile(dJit, 0.5f);
            float medCtr = Percentile(dCtr, 0.5f);
            sb.AppendLine($"  ⓘ |μ − μ_ref| 抖动档 p50/p90/p99/max = {Percentiles(dJit)}");
            sb.AppendLine($"  ⓘ |μ − μ_ref| 片心档 p50/p90/p99/max = {Percentiles(dCtr)}");
            sb.AppendLine($"  ⓘ 尺子地板（两个均值标准误的 3σ 合成）= {Sci((float)floorD)}");

            bool b1 = medJit <= medCtr + floorD;
            sb.AppendLine($"  {Mark(b1)}b-1 抖动档 p50 {Sci(medJit)} ≤ 片心档 p50 {Sci(medCtr)}"
                        + $" + 地板 {Sci((float)floorD)}");
            sb.AppendLine($"     ⓘ 抖动带来的改善（正=更近真值）= {Signed(medCtr - medJit)}，"
                        + $"地板 = {Sci((float)floorD)} ⇒ "
                        + (System.Math.Abs(medCtr - medJit) > floorD
                            ? (medCtr > medJit ? "**可分辨且抖动更准**" : "**可分辨且抖动更差**")
                            : "不可分辨（两者在尺子精度内相同）"));

            // 带号读数：这才是回答「打开抖动之后雾是浓了还是淡了」的那一格。
            sb.AppendLine("  ⓘ 带号相对偏差 (μ − μ_ref)/μ_ref，按参考亮度四分位分桶"
                        + "（正 = 比参考解**亮**；雾越浓这里越亮，因为是散射进来的能量）：");
            ReportBuckets(sb, refMean, relJit, relCtr);

            return b1;
        }

        static void ReportBuckets(StringBuilder sb, float[] key, float[] a, float[] b)
        {
            int n = key.Length;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            var keyCopy = (float[])key.Clone();
            System.Array.Sort(keyCopy, order);

            sb.AppendLine("     桶（按 μ_ref）          n      抖动档带号 p50    片心档带号 p50");
            for (int q = 0; q < 4; q++)
            {
                int lo = n * q / 4, hi = n * (q + 1) / 4;
                int cnt = hi - lo;
                if (cnt <= 0) continue;
                var av = new List<float>(cnt);
                var bv = new List<float>(cnt);
                for (int i = lo; i < hi; i++) { av.Add(a[order[i]]); bv.Add(b[order[i]]); }
                sb.AppendLine($"     Q{q + 1} [{Sci(keyCopy[lo])}, {Sci(keyCopy[hi - 1])}]"
                            + $"  {cnt,6}   {Signed(Median(av)),16}   {Signed(Median(bv)),16}");
            }
        }

        // ---------------------------------------------------------------- ⑯c

        static void ReportRejectCalibration(StringBuilder sb, Cfg A, Cfg Af, bool[] mask,
                                            List<Cfg> sweep)
        {
            sb.AppendLine();
            sb.AppendLine("⑤ 判据⑯c：丢掉历史那一帧的画面代价 —— luminanceRejectStart 的定标依据（**读数**）");
            sb.AppendLine("   历史拒绝（亮度死区）本质上是在赌「丢历史的代价 < 拖影的代价」。");
            sb.AppendLine("   这一格给出前者的实测值：单样本相对噪声 σ_x/μ。它不是门 ——");
            sb.AppendLine("   门槛该摆哪儿是一个美术决定，判据只负责把那个决定要的数字摆出来。");

            int masked = 0;
            for (int p = 0; p < mask.Length; p++) if (mask[p]) masked++;
            var relX = new float[masked];
            var relY = new float[masked];
            int k = 0;
            for (int p = 0; p < mask.Length; p++)
            {
                if (!mask[p]) continue;
                relX[k] = (float)(Af.sd[p] / System.Math.Max(Af.mean[p], k_AbsFloor));
                relY[k] = (float)(A.sd[p] / System.Math.Max(A.mean[p], k_AbsFloor));
                k++;
            }
            sb.AppendLine($"  ⓘ 单样本相对噪声 σ_x/μ  p50/p90/p99/max = {Percentiles(relX)}");
            sb.AppendLine($"  ⓘ 累积后相对噪声 σ_y/μ  p50/p90/p99/max = {Percentiles(relY)}");
            sb.AppendLine($"  ⓘ 对照 Weber 阈值 {k_WeberThreshold:P0}：单样本 p90 是它的 "
                        + $"{Percentile(relX, 0.9f) / k_WeberThreshold:F1} 倍，"
                        + $"累积后 p90 是 {Percentile(relY, 0.9f) / k_WeberThreshold:F1} 倍。");
            sb.AppendLine("     读法：若累积后 p90 已经低于 1 倍，则拒绝历史造成的那一帧噪声**可见**，"
                        + "死区下端应当往上抬（更不容易拒绝）；反之则拒绝几乎不要钱。");

            // ---- 标定曲线 ----
            //  上面两行量的是「拒绝一次要付多少」，这一段量的是「当前这个下端到底在拒绝多少」。
            //  两者缺一不可：只有代价没有频率，定不出门槛；只有频率没有代价，不知道该不该在乎。
            sb.AppendLine();
            sb.AppendLine("  标定曲线：死区下端 → 实际保住的历史权重");
            sb.AppendLine("   w_eff 由方差比反解（w = (R²−1)/(R²+1)），对照下发值 w。");
            sb.AppendLine("   低差异序列让 R 比白噪声预测更大 ⇒ 权重没被吃时 w_eff 会**高于** w；");
            sb.AppendLine("   所以这一列要看的是「有没有掉下来」，不是「等不等于 w」。");
            sb.AppendLine("     死区下端    R_corr      w_eff     w_eff/w      ρ₁      |μ−μ_A′|");

            float wNom = A.wCpu;
            var sdXs = Gather(Af.sd, mask, CountMask(mask));
            double tauX = IntegratedAcTime(Percentile(Gather(Af.rho1, mask, CountMask(mask)), 0.5f), k_Frames);
            double biasXs = (k_Frames - tauX) / (k_Frames - 1.0);
            float medXs = Percentile(sdXs, 0.5f);

            float best = float.NaN;
            foreach (var c in sweep)
            {
                int mc = CountMask(mask);
                float medY = Percentile(Gather(c.sd, mask, mc), 0.5f);
                float rho = Percentile(Gather(c.rho1, mask, mc), 0.5f);
                double bias = (k_Frames - IntegratedAcTime(rho, k_Frames)) / (k_Frames - 1.0);
                double r = bias > 0.0 && biasXs > 0.0
                    ? (medXs / System.Math.Max(medY, 1e-30)) / (System.Math.Sqrt(biasXs) / System.Math.Sqrt(bias))
                    : double.NaN;
                double wEff = (r * r - 1.0) / (r * r + 1.0);
                double keep = wEff / System.Math.Max(wNom, 1e-9);

                var dm = new float[mc];
                int k2 = 0;
                for (int p = 0; p < mask.Length; p++)
                {
                    if (!mask[p]) continue;
                    dm[k2++] = (float)System.Math.Abs(c.mean[p] - Af.mean[p]);
                }

                bool isOnline = Mathf.Approximately(c.rejectStart, A.rejectStart);
                if (keep >= k_RejectKneeKeep && float.IsNaN(best)) best = c.rejectStart;
                sb.AppendLine($"     {c.rejectStart,8:F2}  {r,9:F3}  {wEff,9:F4}  {keep,9:F3}"
                            + $"  {rho,8:F4}  {Sci(Percentile(dm, 0.5f))}"
                            + (isOnline ? "   ←线上" : "")
                            + (c.rejectStart >= k_RejectDisabled ? "   ←关闭" : ""));
            }

            sb.AppendLine($"  ⓘ 按「死区吃掉的权重 ≤ {1f - k_RejectKneeKeep:P0}」取，最小可用下端 = "
                        + (float.IsNaN(best) ? "扫描范围内没有" : best.ToString("F2"))
                        + $"；线上当前 = {Fmt(A.rejectStart)}。");
            sb.AppendLine("  ⚠ 这条曲线只有**一半**。另一半是抬高下端的代价：对真实的亮度变化"
                        + "（灯灭、人走过光轴）拒绝得更迟钝 ⇒ 拖影。本判据的布景是**静止**的，"
                        + "量不到那一半 —— 所以这里给的是「不付噪声代价的最低下端」，"
                        + "不是「该用的下端」。要定后者，需要一个带运动的布景去量拖影长度，"
                        + "那是另一条判据，目前**缺**。");
        }

        // ---------------------------------------------------------------- ⑯d1

        static bool ReportJitterSourceIdentity(StringBuilder sb, Cfg A, Cfg D,
                                               bool[] mask, int masked)
        {
            sb.AppendLine();
            sb.AppendLine("⑥ 判据⑯d1：程序化与蓝噪声收敛到**同一个**极限（恒等式门）");
            sb.AppendLine("   两档只换了抖动偏移的来源，而两个来源在各自的分布上是同一个期望，");
            sb.AppendLine("   所以累积之后的均值必须相同。这条能抓到的是真错：任一档的抖动分布有偏"
                        + "（幅度写成 [0,1] 而不是 [−0.5,0.5]、蓝噪声图没绑上读到全 0、"
                        + "z 步进把三个轴锁成一条线）。");

            var d = new float[masked];
            int k = 0;
            for (int p = 0; p < mask.Length; p++)
            {
                if (!mask[p]) continue;
                d[k++] = (float)System.Math.Abs(A.mean[p] - D.mean[p]);
            }

            double tauA = IntegratedAcTime(Percentile(Gather(A.rho1, mask, masked), 0.5f), k_Frames);
            double tauD = IntegratedAcTime(Percentile(Gather(D.rho1, mask, masked), 0.5f), k_Frames);
            double seA = Percentile(Gather(A.sd, mask, masked), 0.5f) / System.Math.Sqrt(k_Frames / System.Math.Max(1.0, tauA));
            double seD = Percentile(Gather(D.sd, mask, masked), 0.5f) / System.Math.Sqrt(k_Frames / System.Math.Max(1.0, tauD));
            double floor = 3.0 * System.Math.Sqrt(seA * seA + seD * seD);

            float med = Percentile(d, 0.5f);
            bool ok = med <= floor;
            sb.AppendLine($"  {Mark(ok)}|μ_proc − μ_blue| p50 = {Sci(med)} ≤ 3σ 地板 {Sci((float)floor)}"
                        + $"（p90 = {Sci(Percentile(d, 0.9f))}，p99 = {Sci(Percentile(d, 0.99f))}）");
            sb.AppendLine($"  ⓘ D 档的抖动源回落原因 = "
                        + (D.jitterFallback ?? "（没回落，真的在用蓝噪声图）")
                        + "。若蓝噪声资产取不到，CPU 侧会回落到程序化，那时这一格会**恒真**，"
                        + "下面 d2 的两条曲线也会重合 —— 两格一起看才判得出回落。");
            return ok;
        }

        // ---------------------------------------------------------------- ⑯d2

        static void ReportSpatialSpectrum(StringBuilder sb, Cfg Af, Cfg Df, Cfg B, bool[] mask, int divisor)
        {
            sb.AppendLine();
            sb.AppendLine("⑦ 判据⑯d2：单帧残余场的空间相关谱（#22b 残余谱，**读数**）");
            sb.AppendLine("   残余场 = 某一帧 − 该档的时间均值，即那一帧的纯噪声。");
            sb.AppendLine("   蓝噪声的承诺是「噪声能量往高频挪」⇒ 小 lag 上更**负**。");
            sb.AppendLine($"   但 froxel 在屏幕上的足迹是 screenDivisor = {divisor} ⇒ "
                        + $"**{divisor}×{divisor} 像素**：lag 1..{divisor - 1} 全在同一个");
            sb.AppendLine($"   froxel 的双线性插值内部，恒为正且与抖动源无关。真正该看的是 **lag {divisor}**。");
            sb.AppendLine("   B 档（不抖不混历史）的曲线是尺子自己的地板 —— 它该处处接近 0。");

            var cProc = SpatialLagProfile(Af.lastFrame, Af.mean, mask, k_RtSize, k_RtSize, k_MaxLag);
            var cBlue = SpatialLagProfile(Df.lastFrame, Df.mean, mask, k_RtSize, k_RtSize, k_MaxLag);
            var cFloor = SpatialLagProfile(B.lastFrame, B.mean, mask, k_RtSize, k_RtSize, k_MaxLag);

            sb.AppendLine("     lag   程序化 A′      蓝噪声 D′      地板 B        差 (blue−proc)");
            for (int L = 1; L <= k_MaxLag; L++)
            {
                string star = divisor > 0 && L % divisor == 0 ? " ←froxel 边界" : "";
                sb.AppendLine($"     {L,3}   {cProc[L],10:F4}   {cBlue[L],10:F4}   {cFloor[L],10:F4}"
                            + $"   {cBlue[L] - cProc[L],10:F4}{star}");
            }

            int lagKey = Mathf.Clamp(divisor, 1, k_MaxLag);
            double diff8 = cBlue[lagKey] - cProc[lagKey];
            double floor8 = System.Math.Abs(cFloor[lagKey]);
            sb.AppendLine($"  ⓘ lag {lagKey} 处 blue−proc = {Signed((float)diff8)}，"
                        + $"地板 |B({lagKey})| = {Sci((float)floor8)}");
            if (System.Math.Abs(diff8) <= System.Math.Max(floor8, 1e-3))
            {
                sb.AppendLine($"  ⓘ **不可分辨** —— 两档在 lag {lagKey} 上的差落在尺子地板内。");
                sb.AppendLine("     这与 JitterMode.BlueNoise 自己的文档一致：空间上的好处大半被");
                sb.AppendLine("     screen/8 的双线性上采样吃掉了。所以这一格**留作读数、不升为门** ——");
                sb.AppendLine("     「一格恒红的判据不是判据」，一格恒绿的同样不是。");
                sb.AppendLine("     升门的前提是先把上采样换成 bilateral / 或把 screenDivisor 降到 4，");
                sb.AppendLine("     那时这条曲线才有分辨力，到时再回来把它改成门。");
            }
            else
            {
                sb.AppendLine($"  ⓘ **可分辨**，差 {System.Math.Abs(diff8) / System.Math.Max(floor8, 1e-12):F1} 倍于地板。"
                            + (diff8 < 0 ? "方向与蓝噪声的承诺一致（更负 = 能量更高频）。"
                                         : "方向**与承诺相反** —— 蓝噪声档反而更低频，值得查。"));
                sb.AppendLine("     ⇒ 尺子已经有分辨力，这一格可以升成门（记进 CHANGELOG 待办）。");
            }
        }

        /// <summary>
        /// 残余场沿 x 的归一化空间自相关 c(L) = Σ r(x)r(x+L) / √(Σr_a²·Σr_b²)。
        /// 用两端各自的能量做分母（而不是统一用 Σr²）：mask 让两端的像素集不同，
        /// 共用分母时 c(L) 会随 lag 系统性衰减，那个衰减会被读成「相关在下降」。
        /// </summary>
        static double[] SpatialLagProfile(float[] frame, double[] mean, bool[] mask,
                                          int w, int h, int maxLag)
        {
            var c = new double[maxLag + 1];
            for (int L = 1; L <= maxLag; L++)
            {
                double sab = 0.0, saa = 0.0, sbb = 0.0;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x + L < w; x++)
                    {
                        int ia = row + x, ib = row + x + L;
                        if (!mask[ia] || !mask[ib]) continue;
                        double ra = frame[ia] - mean[ia];
                        double rb = frame[ib] - mean[ib];
                        sab += ra * rb; saa += ra * ra; sbb += rb * rb;
                    }
                }
                double den = System.Math.Sqrt(saa * sbb);
                c[L] = den > 0.0 ? sab / den : 0.0;
            }
            return c;
        }

        // ---------------------------------------------------------------- 小工具

        static float[] Gather(double[] src, bool[] mask, int count)
        {
            var dst = new float[count];
            int k = 0;
            for (int p = 0; p < mask.Length && k < count; p++)
                if (mask[p]) dst[k++] = (float)src[p];
            return dst;
        }

        static float[] ToF(double[] src)
        {
            var dst = new float[src.Length];
            for (int i = 0; i < src.Length; i++) dst[i] = (float)src[i];
            return dst;
        }

        static string Mark(bool ok) => ok ? "✔ " : "✘ ";
    }
}
