using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using static Vista.EditorTools.VistaFroxelShadowRig;
using static Vista.EditorTools.VistaFogTierQualityAcceptance;

namespace Vista.EditorTools
{
    /// <summary>
    /// #27 验收第 4 项：**近层 froxel 体的切片数够不够**。
    ///
    /// ── 这一项要兑现的是一笔旧账，而且那笔账的前提已经过期了 ──
    /// #21 把这件事记成债时写的是：「现在的 64 片在 far 64 m / ρ 1.087 下最长段 4.933 m，
    /// x_max 1.144e-2 ——『一阶误差足够小』这件事有推导、没有变步数的实测对照。」
    /// 那之后远边界从 64 m 调到了 150 m（对齐 shadow distance）。同样 64 片，
    /// 指数比 ρ = (150/0.3)^(1/64) = 1.1020，**最长段 13.223 m**，是当时那个数的 2.68 倍。
    /// 所以那条推导不是「还没实测」，是**连推导本身都得重来**。这是一个具体的
    /// 反面教材：一条只写在注释里的误差估计，不会在它引用的参数被改掉那天报错。
    ///
    /// ── 判什么：收敛阶，不是阈值 ──
    /// 「64 片够不够」这个问题没有一个能拍出来的数。能拍的是**收敛行为**：
    /// 阴影边界是一个沿深度的阶跃，任何分段常数求积在阶跃上都是一阶的，
    /// 于是把 N 加倍，误差该减半。拿参考解 N = 256 当基准，在 e(N) = C/N^p 的模型下
    ///     err(N) ≡ |L_N − L_256| = C·(1/N^p − 1/256^p)
    /// 于是相邻两档的 err 之比是**可以在跑之前算出来的**，而且一阶与二阶分得很开：
    ///     16/32  一阶 2.143   二阶 4.048   不收敛 1.000
    ///     32/64  一阶 2.333   二阶 4.200   不收敛 1.000
    ///     64/128 一阶 3.000   二阶 5.000   不收敛 1.000
    /// 注意这三个数**不是 2**。「加倍步数误差减半 ⇒ 比值 ≈ 2」是拿真解当基准时的结论，
    /// 而这里的基准是 256 片、自己带 C/256 的残差。写 2 就会在 64/128 那一格
    /// （真值 3.0）以 50% 的偏差红，然后被当成「实现有问题」去查。
    ///
    /// 门因此摆在三个假设的**几何中点**上，一个可调常数都没有：
    /// 观测比值必须比「不收敛」更靠近「一阶」，也必须比「二阶」更靠近「一阶」。
    /// 这一条是刻意的：本判据要排除的两种失效是「切片数根本没生效」（比值 → 1）
    /// 与「我把误差阶算错了」（比值 → 4~5），而不是「误差大不大」。
    ///
    /// ── 参考解自己的残差是可以读出来的，不用另外假设 ──
    /// 在同一个模型下 err(128) = C·(1/128 − 1/256) = C/256 ——
    /// 这**恰好就是参考解自身的误差**。于是尺子不用拿被测量去标定自己：
    /// 128 与 256 之间那点残差，同时是「256 收敛到什么程度」的估计。
    /// 由此还得到一条修正：真误差 true_err(N) = err(N) · (1/N)/(1/N − 1/256)，
    /// 对 N = 64 是 ×4/3。不做这个修正，出厂档的误差会被**低估 25%** ——
    /// 而低估的方向正好让 Weber 那道门更容易过。
    ///
    /// ── 必须先拆掉的混淆：接手距离随 N 变 ──
    /// handoff = near·exp(L·(N−0.5)/N) 是 N 的函数。本 rig 上实测
    /// 119.4 m (16) → 133.8 → 141.7 → 145.8 → 147.9 m (256)。
    /// 也就是说改 N 不只改了切片粗细，还改了**哪一段雾由带阴影的近层去算**。
    /// 而 (1 − k_HandoffBandFraction)·D 之外的那一段会按权重淡给 AP（无阴影），
    /// 于是同一个像素在不同 N 下比的根本不是同一个量。
    /// 处置是几何上的：只采纳**射线长度 ≤ 0.85 × min(handoff)** 的像素。
    /// 这一条必须是门而不是注释 —— 这套布景的幕布在 z = 100 m，中心射线 100 m
    /// 看起来很安全，但 fov 60°/aspect 1 的**角落**射线长 128.9 m（实测），
    /// 远超 0.85×119.4 = 101.5 m。「看起来很安全」正是这类混淆活下来的方式。
    ///
    /// ── 本 rig 比出厂配置**更严**，这不是巧合，要说清楚 ──
    /// 体的近端取的是**相机近裁剪面**，rig 是 0.1 m、出厂相机是 0.3 m，
    /// 于是同样 150 m 远边界下 rig 的 ln(far/near) 大 18%，同样片数下段更长：
    /// 64 片时 rig 最长段 15.298 m，出厂配置 13.223 m。
    /// 方向是对的（判据比被担保的对象严），但**它不是出厂配置的读数** ——
    /// 这一条必须写在这里，因为「在 rig 上过了」与「在出厂配置上过了」
    /// 差着一个 1.16 倍，而报表上这两句话长得一模一样。
    /// 第一版这份注释里写的是出厂相机的 123.5 m，跑出来才发现读的是 119.4 m：
    /// **一个非零的数回答不了它是从哪儿来的**，所以上面那一串改成了实测值。
    ///
    /// ── 两趟：抖动关 / 出厂档 ──
    /// 抖动关（<c>JitterMode.Off</c>，同时也关掉时间重投影）量的是**纯空间离散化**，
    /// 上面那条收敛阶预测只在这一趟成立。出厂档（程序化抖动 + 重投影）里误差含
    /// 随机残差，比值不该被要求落在解析带里 —— 那一趟只报比值、判 Weber。
    /// 两趟一起跑是因为真正要回答的问题是「抖动帮我盖住了多少」，
    /// 而那个差决定 64 能不能降到 32（移动端那一档要用）。
    ///
    /// ── 不在这里量毫秒 ──
    /// 近层的两趟 dispatch 是 O(N) 的线程数，256 片对 64 片解析上是 4×。
    /// 实测归 <c>VistaLutGpuRecorderCrossCheck</c>：「同一个量不留两份实现」，
    /// 而且那边有整机状态对照（Sky-View LUT）与暖机/采样帧数纪律，这里都没有。
    ///
    /// ── 为什么复用 ⑱ 的布景 ──
    /// 见 <see cref="VistaFogTierQualityAcceptance"/> 的类注释。一句话：
    /// 那套几何的四条前提已经由 ⑱ 的 ⓪ 判过了，另起一套等于把四条前提重写一遍。
    /// 门与阈值一个都不共享。
    /// </summary>
    static class VistaFroxelSliceCountAcceptance
    {
        // ================================================================ 被测的档位

        /// <summary>参考解。取 256 是 <c>depth</c> 的上限（<c>Clamp(sliceCount, 2, 256)</c>），
        /// 再往上 clamp 会静默生效 —— 那会让「参考解」和某个被测档变成同一张图。</summary>
        const int k_RefSlices = 256;

        /// <summary>被测档位，必须**严格递增**且都小于参考解（下面有一格判这个）。</summary>
        static readonly int[] k_Tiers = { 16, 32, 64, 128 };

        /// <summary>出厂档。必须在 <see cref="k_Tiers"/> 里，否则 Weber 那道门无从取值。</summary>
        const int k_ShippingSlices = 64;

        // ================================================================ 门

        /// <summary>
        /// 参考解相邻两帧之间的自变动（地板）。<c>err(128)</c> 至少要高过它这么多倍，
        /// 否则最小那一档的读数是噪声，而三个比值里有两个都用到它。
        /// 3 倍沿用 ⑱ 的 <c>k_NoiseHeadroom</c> 口径（同一个尺子、同一套布景）。
        /// </summary>
        const float k_FloorHeadroom = 3f;

        /// <summary>
        /// 最粗那一档相对地板的富余。它判的不是「误差大」，是**旋钮到底有没有生效**：
        /// 若 <c>sliceCount</c> 的写入被静默忽略，五张图会逐位相同，
        /// 所有「≤」形状的门会**全部通过** ——「一个恒真的绿比一个红更贵」。
        /// 10 倍而不是 3 倍：这一格要能与「旋钮生效了但效果很小」区分开，
        /// 而 16 片对 256 片按一阶模型差 15 倍，取 10 留下 1.5 倍余量。
        /// </summary>
        const float k_KnobHeadroom = 10f;

        /// <summary>
        /// p99 至少要有这么多个像素落在它之上，否则这个分位数是几个像素说了算。
        /// 由统计量本身定，不由答案定：门是 <c>admitted × 0.01 ≥ 50</c>。
        /// </summary>
        const int k_MinP99Support = 50;

        /// <summary>报读数用的分位数。判的是 p99；p100（max）只入 ⓘ ——
        /// 「max 回答不了阈值该摆哪儿，分位数才能」。</summary>
        const float k_JudgedQuantile = 0.99f;

        /// <summary>
        /// 本判据自己的渲染分辨率，**不用** rig 的 256。
        ///
        /// 理由是上面那道采纳判据的代价：它剔掉了幕布像素的 92%（2570 / 33792），
        /// 于是 256² 下 p99 只有 25 个像素撑着 —— 低于
        /// <see cref="k_MinP99Support"/>，第一次跑就是这么红的。
        ///
        /// 修的是**样本量**，不是门：把分辨率翻倍，采纳集 ×4 ⇒ 支撑 ≈ 100。
        /// 另一条路（把判的分位数从 p99 降到 p95）会让支撑够用，
        /// 但那是看见 p99 没过之后去动分位数 ——「拍阈值等于把结论写进判据」，
        /// 而且降分位数正好是让「最坏的那一撮像素」退出统计的方向。
        ///
        /// 副作用：屏幕大一倍 ⇒ froxel 横向格子从 32×32 变 64×64（screenDivisor 不变）。
        /// 这不是混淆：五个档位共用同一个横向网格，变的只有深度方向。
        /// </summary>
        const int k_RtSizeHi = 512;

        // ================================================================ 入口

        [MenuItem("Window/Vista/Acceptance: Froxel Slice Count (Quality)", priority = 138)]
        static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== #27 验收第 4 项：近层 froxel 切片数（收敛阶 + Weber） ===");
            sb.AppendLine();

            bool pass = Acceptance(sb);

            sb.AppendLine();
            sb.AppendLine(pass
                ? "结论：**通过**。切片数旋钮真的改了体的形状；误差随 N 按**一阶**收敛"
                + "（而不是不收敛、也不是二阶）；参考解自身的残差有独立估计；"
                + "出厂档 64 片修正后的真误差落在 Weber 1% 以内。"
                : "结论：**未通过**（或不可判定）。逐条看上面的 ✘ / ⓘ。");

            if (pass) Debug.Log(sb.ToString());
            else      Debug.LogError(sb.ToString());
        }

        // ================================================================ 一档的读数

        struct TierRead
        {
            public int slices;          // 请求值
            public int depth;           // 体实际分到的片数
            public float handoff;       // 体实际的接手距离 (m)
            public float maxSegment;    // 最长一段的厚度 (m)
        }

        /// <summary>一趟（抖动关 / 出厂档）里每一档的误差分位数。</summary>
        struct TierErr
        {
            public int slices;
            public float p50, p90, p99, max;
            public float p99Lit, p99Edge, p99Dark;   // 按 φ 分箱，只入 ⓘ
        }

        static bool Acceptance(StringBuilder sb)
        {
            // ---- 被测档位表自身的前提。写错了它会让整条比值链静默错位，
            //      而错位后的比值仍然落在某个带里 —— 那种失效没有症状。
            if (k_Tiers.Length < 3)
            {
                sb.AppendLine("✘ 前置：至少要三档才有两个比值可判。");
                return false;
            }
            for (int i = 1; i < k_Tiers.Length; i++)
                if (k_Tiers[i] <= k_Tiers[i - 1])
                {
                    sb.AppendLine("✘ 前置：档位表必须严格递增。");
                    return false;
                }
            if (k_Tiers[k_Tiers.Length - 1] >= k_RefSlices)
            {
                sb.AppendLine("✘ 前置：最细那一档必须严格小于参考解。");
                return false;
            }
            int shipIdx = System.Array.IndexOf(k_Tiers, k_ShippingSlices);
            if (shipIdx < 0)
            {
                sb.AppendLine($"✘ 前置：出厂档 {k_ShippingSlices} 不在被测档位表里。");
                return false;
            }

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
                sb.AppendLine("✘ 前置：近层体不可用（compute 缺失或有核编译不出来）。");
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

            // RigState 只托管两个消费者共用的那几项。下面这四项是**本判据**的前提
            // （相位函数、切片数、抖动形态与幅度），塞进 rig 会让别的判据莫名其妙
            // 跟着改布景 —— 与 ⑱ 存还 anisotropy 是同一条理由。
            float prevG           = fog.anisotropy;
            int   prevSlices      = vf.sliceCount;
            var   prevJitter      = vf.jitterMode;
            float prevJitterAmt   = vf.depthJitterAmount;
            bool  prevForceNoShad = VistaFroxelVolume.s_DebugForceNoSunShadow;

            RenderTexture rt = null;
            GameObject root  = null;
            Material mat     = null;
            Texture2D tex    = null;
            bool ok = true;

            try
            {
                state.Apply();
                // g = 0：相位函数的形状不是本判据的被测量，而它会随视角改变入散射的
                // 空间分布，从而改变「误差最大的像素在哪儿」。与 ⑱ 同一个取值，
                // 这样两项判据的布景口径可比。代价是本判据对相位函数免疫 —— 已登记。
                fog.anisotropy = 0f;
                VistaFroxelVolume.s_DebugForceNoSunShadow = false;

                // ARGBFloat：要在 Weber 1% 上量差，half 的相对精度约 1e-3，
                // 只差一个数量级 —— 量化噪声会被报成信号。与 ⑱ 同一条理由。
                rt = new RenderTexture(k_RtSizeHi, k_RtSizeHi, 24,
                                       RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);

                BuildTierRig(litShader, layer, feature.groundLevelWorldY, rt,
                             out root, out Camera cam, out mat, out Light sun);
                RenderSettings.sun = sun;
                Vector3 toSun = -sun.transform.forward;

                sb.AppendLine($"布景：⑱ 的幕布 rig（幕布 z = {k_BackdropZ:F0} m，"
                            + $"太阳 仰角 {k_TierSunAltitudeDeg:F0}° 方位 {k_TierSunAzimuthDeg:F0}°"
                            + $" ⇒ 指向太阳 ({toSun.x:F3}, {toSun.y:F3}, {toSun.z:F3})），"
                            + $"相位 g = {fog.anisotropy:F2}（判据自己设的）；"
                            + $"每档静置 {k_SettleFramesTier} 帧。");

                // ================================================ ⓪ 旋钮响应 + 体的形状
                //
                // 这一节**先跑**，因为下面那个采纳判据的阈值来自这里读到的 handoff。
                // 顺序反过来（先定阈值再量 handoff）就得把 StoredDistance 的公式
                // 在这里再写一遍 —— 那是第二份实现。
                var reads = new List<TierRead>();
                if (!ProbeTiers(sb, vf, volume, cam, reads))
                    return false;

                float minHandoff = float.PositiveInfinity;
                foreach (var r in reads) minHandoff = Mathf.Min(minHandoff, r.handoff);
                float admitLen = (1f - VistaFogSettings.k_HandoffBandFraction) * minHandoff;

                // ================================================ ① 采纳集：解析算，不读像素
                bool[] admitted = BuildAdmissionMask(sb, toSun, admitLen, minHandoff,
                                                     out int nAdmit,
                                                     out float[] phi);
                if (admitted == null) return false;

                if (!VerifyRigAtCoarsest(sb, vf, volume, cam, toSun))
                    return false;

                tex = new Texture2D(k_RtSizeHi, k_RtSizeHi, TextureFormat.RGBAFloat, false, true);

                // ================================================ ② 两趟
                sb.AppendLine();
                sb.AppendLine("② 抖动关（JitterMode.Off ⇒ 无抖动、无时间重投影）＝ 纯空间离散化");
                vf.jitterMode = JitterMode.Off;
                bool okOff = Sweep(sb, vf, cam, rt, tex, admitted, phi, nAdmit,
                                   gateOrder: true, out var errOff);
                ok &= okOff;

                sb.AppendLine();
                sb.AppendLine("③ 出厂档（程序化抖动 + 时间重投影）＝ 真正上线的那一档");
                vf.jitterMode = JitterMode.Procedural;
                vf.depthJitterAmount = 1f;
                bool okOn = Sweep(sb, vf, cam, rt, tex, admitted, phi, nAdmit,
                                  gateOrder: false, out var errOn);
                ok &= okOn;

                // ================================================ ④ 抖动盖住了多少
                sb.AppendLine();
                ok &= CompareJitter(sb, errOff, errOn, shipIdx);

                // ================================================ ⑤ 成本：只写解析，不量
                sb.AppendLine();
                Cost(sb, reads);
            }
            finally
            {
                VistaFroxelVolume.s_DebugForceNoSunShadow = prevForceNoShad;
                vf.depthJitterAmount = prevJitterAmt;
                vf.jitterMode        = prevJitter;
                vf.sliceCount        = prevSlices;
                fog.anisotropy       = prevG;
                state.Restore();

                if (tex  != null) Object.DestroyImmediate(tex);
                if (root != null) Object.DestroyImmediate(root);
                if (mat  != null) Object.DestroyImmediate(mat);
                if (rt   != null) { rt.Release(); Object.DestroyImmediate(rt); }
            }

            return ok;
        }

        // ==================================================================== ⓪ 旋钮响应

        /// <summary>
        /// 逐档把 <c>sliceCount</c> 写下去、渲几帧、读回体**实际**分到的形状。
        ///
        /// 为什么这一节是门而不是一句「设了就生效」：分配走的是
        /// <c>VistaFroxelVolume.Prepare</c> 里的 <c>!m_Allocated.Value.Equals(desc)</c>，
        /// 中间隔着 <c>Resolve()</c>、一道 shadow-distance 夹取和一道相机类型闸门。
        /// 任何一环没生效，五张图就逐位相同，而本判据所有「≤」形状的门会**全部通过**。
        /// </summary>
        static bool ProbeTiers(StringBuilder sb, VistaVolumetricFogSettings vf,
                               VistaFroxelVolume volume, Camera cam, List<TierRead> reads)
        {
            sb.AppendLine("⓪ 旋钮响应：写下去的切片数与体实际分到的形状");
            bool all = true;

            var all_n = new List<int>(k_Tiers) { k_RefSlices };
            foreach (int n in all_n)
            {
                vf.sliceCount = n;
                for (int i = 0; i < k_WarmupFrames; i++)
                    cam.Render();

                var desc = volume.allocatedDesc;
                if (!desc.HasValue)
                {
                    sb.AppendLine($"  ✘ N = {n}：近层体一次都没分配下来（allocatedDesc 为 null）。"
                                + "嫌疑顺序：enableInjection / 相机类型闸门 / Prepare 失败。");
                    return false;
                }

                var d = desc.Value;
                bool hit = d.depth == n;
                all &= hit;

                // 最长一段：对数分布下是最后一段。直接问体本身要，不在这里重写
                // StoredDistance 的公式 ——「同一个量不留两份实现」。
                float maxSeg = d.SegmentFar(d.depth - 1) - d.SegmentNear(d.depth - 1);

                reads.Add(new TierRead
                {
                    slices = n, depth = d.depth, handoff = d.handoffMeters, maxSegment = maxSeg,
                });

                sb.AppendLine($"  {Mk(hit)}体近端 {d.nearMeters:F2} m / 远边界 {d.farMeters:F1} m；"
                            + $"请求 {n,3} 片 → 体 {d.width}×{d.height}×{d.depth}"
                            + $"，ρ = {d.sliceRatio:F4}"
                            + $"，最长段 {maxSeg:F3} m"
                            + $"，接手 {d.handoffMeters:F1} m");
            }

            if (!all)
                sb.AppendLine("  ⇒ 切片数写下去没有生效（或被 clamp 了），**不往下量**："
                            + "若五张图逐位相同，下面每一道「≤」形状的门都会通过。");
            return all;
        }

        // ==================================================================== ① 采纳集

        /// <summary>
        /// 采纳哪些像素。两条：首个命中必须是幕布（复用 ⑱ 的解析求交），
        /// 且射线长度必须落在**所有**档位的「纯近层」区段内。
        /// 一个渲染像素都没读。
        /// </summary>
        static bool[] BuildAdmissionMask(StringBuilder sb, Vector3 toSun,
                                         float admitLen, float minHandoff,
                                         out int nAdmit, out float[] phi)
        {
            int w = k_RtSizeHi, h = k_RtSizeHi;
            float tanHalf = Mathf.Tan(k_Fov * 0.5f * Mathf.Deg2Rad);

            var mask = new bool[w * h];
            phi = new float[w * h];
            nAdmit = 0;
            int nBackdrop = 0;
            float maxLen = 0f;

            for (int y = 0; y < h; y++)
            {
                float b = (2f * ((y + 0.5f) / h) - 1f) * tanHalf;
                for (int x = 0; x < w; x++)
                {
                    float a = (2f * ((x + 0.5f) / w) - 1f) * tanHalf;
                    int i = y * w + x;
                    phi[i] = float.NaN;

                    if (!PrimaryIsBackdrop(a, b)) continue;
                    nBackdrop++;

                    // 射线长度：幕布在 z = k_BackdropZ，每单位 z 走 sqrt(1+a²+b²)。
                    float len = k_BackdropZ * Mathf.Sqrt(1f + a * a + b * b);
                    maxLen = Mathf.Max(maxLen, len);
                    if (len > admitLen) continue;

                    mask[i] = true;
                    nAdmit++;
                    phi[i] = ShadowedFraction(a, b, toSun);
                }
            }

            sb.AppendLine();
            sb.AppendLine("① 采纳集（解析算出来的，没读任何一个渲染像素）");
            sb.AppendLine($"  接手距离随 N 变：最小 {minHandoff:F1} m（N = {k_Tiers[0]}），"
                        + $"纯近层区段止于 (1 − {VistaFogSettings.k_HandoffBandFraction:F2})"
                        + $"×{minHandoff:F1} = {admitLen:F1} m");
            sb.AppendLine($"  幕布像素 {nBackdrop}，其中射线长 ≤ {admitLen:F1} m 的 {nAdmit}"
                        + $"（占全屏 {(float)nAdmit / (w * h):P1}）；"
                        + $"幕布上最长射线 {maxLen:F1} m");

            // 这一格的存在理由：中心射线 100 m 看起来很安全，角落 129.1 m 不安全。
            // 若哪天布景或 fov 变了、整幅都落进纯近层区，这一格会变成恒真 ——
            // 那时它印出的 maxLen 会直接说明原因，而不是无声通过。
            bool bites = maxLen > admitLen;
            sb.AppendLine($"  {Mk(bites, info: true)}这道采纳判据**确实咬到了东西**："
                        + $"最长射线 {maxLen:F1} m {(bites ? ">" : "≤")} {admitLen:F1} m"
                        + (bites ? "（有像素被剔掉）" : "（一个都没剔 —— 这一格此刻是恒真的）"));

            int support = Mathf.FloorToInt(nAdmit * (1f - k_JudgedQuantile));
            bool enough = support >= k_MinP99Support;
            sb.AppendLine($"  {Mk(enough)}p{k_JudgedQuantile * 100:F0} 的支撑："
                        + $"{support} 个像素落在它之上，门 ≥ {k_MinP99Support}");
            if (!enough)
            {
                sb.AppendLine("  ⇒ 采纳集太小，分位数由个别像素说了算，**不可判定**，不往下量。");
                return null;
            }

            // φ 只用于 ⓘ 分箱：说明误差最大的像素是不是阴影边界那一带。
            int nLit = 0, nEdge = 0, nDark = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i]) continue;
                if (phi[i] <= 0.1f) nLit++;
                else if (phi[i] >= 0.6f) nDark++;
                else nEdge++;
            }
            sb.AppendLine($"  ⓘ 按遮蔽占比 φ 分箱（只报，不判）：亮 φ≤0.1 共 {nLit}，"
                        + $"边界 0.1<φ<0.6 共 {nEdge}，暗 φ≥0.6 共 {nDark}。"
                        + "一阶误差该集中在「边界」那一箱 —— 下面每一档都会分箱报一次。");
            return mask;
        }

        /// <summary>
        /// 在**最粗**那一档上跑 ⑱ 的四条布景前提。
        /// 挑最粗档不是随手：那四条里有一条判的是「幕布整个落在近层里」
        /// （<c>k_BackdropZ &lt; handoffMeters</c>），而 handoff 随 N 单增 ——
        /// 在 N = 256 上跑那一格几乎恒真，在 N = 16 上跑才是最紧的那一次。
        /// </summary>
        static bool VerifyRigAtCoarsest(StringBuilder sb, VistaVolumetricFogSettings vf,
                                        VistaFroxelVolume volume, Camera cam, Vector3 toSun)
        {
            sb.AppendLine();
            sb.AppendLine($"（下面这一节是 ⑱ 的布景前提，在**最粗**档 N = {k_Tiers[0]} 上跑 ——"
                        + "「幕布在近层里」那一格随 N 单调放松，在参考解上跑几乎恒真。）");
            vf.sliceCount = k_Tiers[0];
            for (int i = 0; i < k_WarmupFrames; i++)
                cam.Render();
            return VerifyRig(sb, volume, cam, toSun);
        }

        // ==================================================================== ②③ 扫描

        /// <summary>
        /// 一趟扫描：参考解 → 地板 → 四档 → 参考解重跑（漂移地板）。
        /// </summary>
        static bool Sweep(StringBuilder sb, VistaVolumetricFogSettings vf, Camera cam,
                          RenderTexture rt, Texture2D tex,
                          bool[] admitted, float[] phi, int nAdmit,
                          bool gateOrder, out TierErr[] errs)
        {
            errs = new TierErr[k_Tiers.Length];
            bool ok = true;

            vf.sliceCount = k_RefSlices;
            float[] refImg = CaptureLuminance(cam, rt, tex, k_SettleFramesTier);

            // 地板：什么都不改，只再渲一帧。「地板的定义是相邻两帧之间它自己动多少」，
            // 给它也空转 64 帧量到的是另一个量（⑱ 同一条理由）。
            float[] refNext = CaptureLuminance(cam, rt, tex, 1);

            float denom = Mathf.Max(MedianOver(refImg, admitted, nAdmit) * k_DenomFloorFraction,
                                    1e-12f);
            float floorAdj = RelQuantile(refImg, refNext, admitted, denom, k_JudgedQuantile);

            for (int k = 0; k < k_Tiers.Length; k++)
            {
                vf.sliceCount = k_Tiers[k];
                float[] img = CaptureLuminance(cam, rt, tex, k_SettleFramesTier);
                errs[k] = Measure(k_Tiers[k], refImg, img, admitted, phi, denom);
            }

            // 漂移地板：整趟跑完之后把参考解原样再跑一次。
            // 邻帧地板量不到「16 → 128 这四档之间机器自己漂了多少」，而每一档的
            // err(N) 都是拿**这趟最开始**那张参考图去比的 —— 那段漂移全记在 err 上。
            vf.sliceCount = k_RefSlices;
            float[] refEnd = CaptureLuminance(cam, rt, tex, k_SettleFramesTier);
            float floorDrift = RelQuantile(refImg, refEnd, admitted, denom, k_JudgedQuantile);

            float floor = Mathf.Max(floorAdj, floorDrift);

            sb.AppendLine($"  参考解 N = {k_RefSlices}；采纳集中位亮度 "
                        + $"{Sci(MedianOver(refImg, admitted, nAdmit))}，"
                        + $"相对差分母下限 {Sci(denom)}"
                        + $"（= 中位数 × {k_DenomFloorFraction:0.###e+00}）");
            sb.AppendLine($"  地板 p99：邻帧 {Sci(floorAdj)}，整趟漂移 {Sci(floorDrift)}"
                        + $" ⇒ 取大者 {Sci(floor)}"
                        + (floorDrift > floorAdj
                            ? "（漂移更大 —— 说明这趟里机器自己动得比邻帧抖动多）"
                            : "（邻帧更大 —— 这趟没有可观测的单调漂移）"));

            sb.AppendLine("  逐档 |L_N − L_ref| / max(L_ref, 下限) 的分位数：");
            sb.AppendLine("  ┌ N   ┬ p50      ┬ p90      ┬ p99      ┬ max      ┬ p99(亮)  ┬ p99(边界)┬ p99(暗)  ┐");
            foreach (var e in errs)
                sb.AppendLine($"  │ {e.slices,3} │ {S(e.p50)} │ {S(e.p90)} │ {S(e.p99)} │ "
                            + $"{S(e.max)} │ {S(e.p99Lit)} │ {S(e.p99Edge)} │ {S(e.p99Dark)} │");
            sb.AppendLine("  └─────┴──────────┴──────────┴──────────┴──────────┴──────────┴──────────┴──────────┘");

            // ---- 地板与旋钮响应 ----
            float errFinest = errs[errs.Length - 1].p99;
            float errCoarse = errs[0].p99;

            bool aboveFloor = errFinest >= floor * k_FloorHeadroom;
            sb.AppendLine($"  {Mk(aboveFloor)}最细档 N = {k_Tiers[k_Tiers.Length - 1]} 的 p99 "
                        + $"{Sci(errFinest)} ≥ 地板 {Sci(floor)} × {k_FloorHeadroom:F0} = "
                        + $"{Sci(floor * k_FloorHeadroom)}"
                        + "（不成立则最小那一档是噪声，而三个比值里有两个用到它）");
            ok &= aboveFloor;

            bool knobBites = errCoarse >= floor * k_KnobHeadroom;
            sb.AppendLine($"  {Mk(knobBites)}最粗档 N = {k_Tiers[0]} 的 p99 {Sci(errCoarse)} ≥ "
                        + $"地板 × {k_KnobHeadroom:F0} = {Sci(floor * k_KnobHeadroom)}"
                        + "（不成立则「切片数根本没改变画面」与「误差本来就很小」分不开）");
            ok &= knobBites;

            // ---- 收敛阶 ----
            if (!aboveFloor || !knobBites)
            {
                sb.AppendLine("  ⇒ 地板或旋钮响应不成立，**收敛阶不判**（比值的分母不可信）。");
                return false;
            }
            ok &= Order(sb, errs, gateOrder);

            // ---- Weber：出厂档，带参考解残差修正 ----
            ok &= Weber(sb, errs);
            return ok;
        }

        // ==================================================================== 收敛阶

        /// <summary>
        /// 观测比值 vs 三个假设。带子是三个假设的**几何中点**，没有可调常数。
        /// </summary>
        static bool Order(StringBuilder sb, TierErr[] errs, bool gate)
        {
            sb.AppendLine($"  收敛阶（{(gate ? "判" : "只报 —— 抖动档的误差含随机残差，解析预测不适用")}）：");
            bool ok = true;

            for (int k = 0; k + 1 < errs.Length; k++)
            {
                int nA = errs[k].slices, nB = errs[k + 1].slices;
                float obs = errs[k + 1].p99 > 0f ? errs[k].p99 / errs[k + 1].p99 : float.NaN;

                double p1 = Pred(nA, 1.0) / Pred(nB, 1.0);
                double p2 = Pred(nA, 2.0) / Pred(nB, 2.0);
                const double p0 = 1.0;          // 不收敛：err 与 N 无关
                double lo = System.Math.Sqrt(p0 * p1);
                double hi = System.Math.Sqrt(p1 * p2);

                bool inBand = !float.IsNaN(obs) && obs >= lo && obs <= hi;
                string mark = gate ? Mk(inBand) : "  ⓘ ";
                if (gate) ok &= inBand;

                sb.AppendLine($"  {mark}err({nA})/err({nB}) = {obs:F3}；"
                            + $"预测 一阶 {p1:F3} / 二阶 {p2:F3} / 不收敛 {p0:F3}"
                            + $" ⇒ 带 [{lo:F3}, {hi:F3}]");
            }

            if (gate)
                sb.AppendLine("  （带子是「一阶」与另两个假设的几何中点 —— "
                            + "「拍阈值等于把结论写进判据」，这里两侧的值都是算出来的。）");
            return ok;
        }

        /// <summary>e_p(N) = 1/N^p − 1/ref^p：拿有限步参考解比出来的那一份残差。</summary>
        static double Pred(int n, double p)
            => System.Math.Pow(n, -p) - System.Math.Pow(k_RefSlices, -p);

        // ==================================================================== Weber

        static bool Weber(StringBuilder sb, TierErr[] errs)
        {
            int idx = System.Array.IndexOf(k_Tiers, k_ShippingSlices);
            var e = errs[idx];

            // err(N) 是拿 256 片比出来的，在一阶模型下它**低估**真误差：
            //   err(N) = C(1/N − 1/256)，true = C/N ⇒ true = err × (1/N)/(1/N − 1/256)
            // 对 64 片是 ×4/3。不修正的话低估 25%，而低估的方向正好让这道门更容易过。
            double corr = (1.0 / k_ShippingSlices) / Pred(k_ShippingSlices, 1.0);
            float trueP99 = (float)(e.p99 * corr);

            bool pass = trueP99 <= k_WeberThreshold;
            sb.AppendLine($"  {Mk(pass)}出厂档 N = {k_ShippingSlices}：p99 {Sci(e.p99)} × "
                        + $"参考解残差修正 {corr:F4} = **{Sci(trueP99)}**，"
                        + $"门 ≤ Weber {k_WeberThreshold:P1}");
            sb.AppendLine($"    ⓘ 同样修正下 max = {Sci((float)(e.max * corr))}"
                        + "（不判 ——「max 回答不了阈值该摆哪儿」）");

            // 顺带回答「能不能降档」。这不是门：「32 片够用」不是本判据的要求，
            // 把它写成门等于替移动端那一档先把结论定了。
            for (int k = 0; k < errs.Length; k++)
            {
                if (k_Tiers[k] >= k_ShippingSlices) continue;
                double c = (1.0 / k_Tiers[k]) / Pred(k_Tiers[k], 1.0);
                float t = (float)(errs[k].p99 * c);
                sb.AppendLine($"    ⓘ N = {k_Tiers[k]}：修正后 p99 {Sci(t)}"
                            + $"（Weber 的 {t / k_WeberThreshold:F2} 倍）"
                            + $" —— {(t <= k_WeberThreshold ? "也在 1% 以内" : "超出 1%")}"
                            + "，移动端降档要用的就是这一行，但本判据不判它。");
            }
            return pass;
        }

        // ==================================================================== ④ 抖动盖住了多少

        static bool CompareJitter(StringBuilder sb, TierErr[] off, TierErr[] on, int shipIdx)
        {
            sb.AppendLine("④ 抖动盖住了多少（同一套布景、同一个采纳集、同一个参考解片数）");
            sb.AppendLine("  ┌ N   ┬ 抖动关 p99 ┬ 出厂档 p99 ┬ 比值 关/开 ┐");
            for (int k = 0; k < off.Length; k++)
            {
                float r = on[k].p99 > 0f ? off[k].p99 / on[k].p99 : float.NaN;
                sb.AppendLine($"  │ {off[k].slices,3} │ {S(off[k].p99)}   │ {S(on[k].p99)}   │ {r,10:F2} │");
            }
            sb.AppendLine("  └─────┴────────────┴────────────┴────────────┘");

            // 这里**不设门**，而且这是刻意的。比值 > 1 说明抖动确实在削误差，
            // 但它同时引入了随机残差与重投影的滞后 —— 两者在这个单一标量上
            // 是相反方向，所以「比值该多大」没有一个能算出来的预测。
            // 「拍阈值等于把结论写进判据」：这一栏因此只报。
            //
            // 能判的只有一条**方向**：出厂档在出厂片数上不该比抖动关更差。
            // 若更差，说明抖动/重投影在这个布景上是净亏，那是个功能问题，
            // 而不是「切片数够不够」的答案。
            float shipOff = off[shipIdx].p99, shipOn = on[shipIdx].p99;
            bool notWorse = shipOn <= shipOff;
            sb.AppendLine($"  {Mk(notWorse)}出厂片数 N = {k_ShippingSlices} 上，"
                        + $"出厂档 p99 {Sci(shipOn)} ≤ 抖动关 p99 {Sci(shipOff)}"
                        + "（抖动 + 重投影至少不该是净亏）");
            sb.AppendLine("  ⓘ 比值本身不设门：抖动削空间离散化误差、同时加随机残差与"
                        + "重投影滞后，两者在这个标量上方向相反 ——「该多大」算不出来。");
            return notWorse;
        }

        // ==================================================================== ⑤ 成本

        static void Cost(StringBuilder sb, List<TierRead> reads)
        {
            sb.AppendLine("⑤ 成本（解析，**本判据不量毫秒**）");
            var baseRead = reads.Find(r => r.slices == k_ShippingSlices);
            foreach (var r in reads)
            {
                float ratio = baseRead.depth > 0 ? (float)r.depth / baseRead.depth : float.NaN;
                sb.AppendLine($"  N = {r.slices,3}：{r.depth} 片 × 同一张屏幕 tile 网格"
                            + $" ⇒ 相对出厂档线程数 ×{ratio:F2}"
                            + $"；最长段 {r.maxSegment:F3} m，接手 {r.handoff:F1} m");
            }
            sb.AppendLine("  注入与积分两趟的线程数都正比于片数，所以上面那一列就是解析上的相对成本。");
            sb.AppendLine("  实测归 `VistaLutGpuRecorderCrossCheck`（Window/Vista/Cross-Check "
                        + "Atmosphere Timing）——「同一个量不留两份实现」，"
                        + "而且那边有整机状态对照与暖机/采样帧数纪律，这里都没有。");
        }

        // ==================================================================== 统计

        static TierErr Measure(int slices, float[] refImg, float[] img,
                               bool[] admitted, float[] phi, float denom)
        {
            var all  = new List<float>();
            var lit  = new List<float>();
            var edge = new List<float>();
            var dark = new List<float>();

            for (int i = 0; i < admitted.Length; i++)
            {
                if (!admitted[i]) continue;
                float rel = Mathf.Abs(img[i] - refImg[i]) / Mathf.Max(refImg[i], denom);
                all.Add(rel);
                if (phi[i] <= 0.1f) lit.Add(rel);
                else if (phi[i] >= 0.6f) dark.Add(rel);
                else edge.Add(rel);
            }

            return new TierErr
            {
                slices  = slices,
                p50     = Pctl(all,  0.50f),
                p90     = Pctl(all,  0.90f),
                p99     = Pctl(all,  k_JudgedQuantile),
                max     = Pctl(all,  1.00f),
                p99Lit  = Pctl(lit,  k_JudgedQuantile),
                p99Edge = Pctl(edge, k_JudgedQuantile),
                p99Dark = Pctl(dark, k_JudgedQuantile),
            };
        }

        /// <summary>
        /// 空数组上的分位数是**不可判定**，不是 0。<c>VistaFroxelShadowRig.Percentile</c>
        /// 在空数组上会下标越界，而「返回 0」会让那一栏长得像「误差为零」——
        /// 一个恰好为零的数回答不了它是从哪儿来的。
        /// </summary>
        static float Pctl(List<float> v, float q)
            => v.Count == 0 ? float.NaN : Percentile(v.ToArray(), q);

        static float RelQuantile(float[] a, float[] b, bool[] admitted, float denom, float q)
        {
            var v = new List<float>();
            for (int i = 0; i < admitted.Length; i++)
                if (admitted[i])
                    v.Add(Mathf.Abs(a[i] - b[i]) / Mathf.Max(a[i], denom));
            return Pctl(v, q);
        }

        static float MedianOver(float[] img, bool[] admitted, int nAdmit)
        {
            var v = new float[nAdmit];
            int k = 0;
            for (int i = 0; i < admitted.Length && k < nAdmit; i++)
                if (admitted[i]) v[k++] = img[i];
            return Percentile(v, 0.50f);
        }

        /// <summary>定宽的 Sci，只为表格能对齐。NaN 原样印成 n/a。</summary>
        static string S(float v)
            => (float.IsNaN(v) ? "n/a" : Sci(v)).PadLeft(8);

        static string Mk(bool ok, bool info = false)
            => info ? (ok ? "✔ " : "ⓘ ") : (ok ? "✔ " : "✘ ");
    }
}
