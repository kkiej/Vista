using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// 近层体积雾**深度积分**的数值判定（#21）。
    ///
    /// 被测对象是 <c>FroxelIntegration</c> 这一个核：一线程一柱，沿深度把注入表的
    /// (σ_s·J, σ_t) 累积成 (内散射 rgb, 1 − 累积透射率)。
    ///
    /// ---- 为什么判据自己造布景 ----
    /// 真实的雾 σ_t 沿视线随高度变，那时闭式解不存在，「参考解」只能是另一个积分器 ——
    /// 也就是同一个量的第二份实现。所以这里先用 <c>FroxelSynthMedium</c> 把一个**均匀**
    /// 介质写满注入表。被测的积分核不分支于数据来源（它只读注入表），合成数据合法地覆盖它。
    ///
    /// ---- 均匀介质下离散和 == 连续积分，**逐项抵消**，没有离散化误差 ----
    /// L_i = Σ_{j≤i} T_{j−1}·S·(1 − e^{−σΔ_j})/σ
    ///     = (S/σ)·Σ (e^{−σd_{j−1}} − e^{−σd_j})            ← T_{j−1} = e^{−σ d_{j−1}}
    ///     = (S/σ)·(1 − e^{−σ d_i})
    /// 中间项全部抵消（望远镜求和），因为分段恰好平铺 [0, d_i]：SegmentNear(0) = 0、
    /// SegmentFar(i) = StoredDistance(i) = SegmentNear(i+1)（这三条在 #19 判过）。
    /// 于是**参考解里一项离散化误差都没有**，残差预算只剩：
    ///   fp16 存储（相对整 ulp 2⁻¹⁰ = 9.77e-4 —— 这条路径实测**截断**，不是就近取整，
    ///     所以地板是整个 ulp 而不是一半；证据是 ⓪ 格打出来的 12 对原始读数）
    ///   + fp32 累加（≤ N·2⁻²⁴）
    ///   + 级数支的截断（x³/24，在 x ≤ 1e-4 处是 1e-13 量级，可忽略）。
    /// 这也是这套判据能把门开到 2e-3 而不是「看着差不多」的全部依据 ——
    /// 门 = 地板 × 2.05，两个数都在报表里，谁都能自己核。
    /// 望远镜恒等式本身不当假设用 —— 每一档都把「闭式解 vs 逐段求和」的残差打出来。
    ///
    /// ---- 参考解用的 σ_t / S / Δ 全部从 GPU 读回来 ----
    /// 注入表是 fp16 的，核实际用的 σ_t 是 <c>half(σ_t)</c>；分段长度是核自己算的 dtKm。
    /// 把这三个**输入**读回来当参考解的输入，判据就只测积分核的算术，不再混进
    /// 「布景被 fp16 量化了多少」和「切片几何对不对」—— 那两件事各有自己的格子（⓪ 与 ⑥）。
    ///
    /// ---- 与 #20 探针的分工 ----
    /// 这里是立即模式：没有相机、没有阴影、完全确定，所以能判**数值**。
    /// RenderGraph 那条积分写入路径由 <c>Window/Vista/Log Volumetric Fog State</c>
    /// 的探针槽 14~18 覆盖（真实帧、真实雾），那边判的是「跑过没有 + 量级/包络」。
    /// 两边缺一个都会留下一条没人看的路径。
    /// </summary>
    public static class VistaFroxelIntegrationSelfTest
    {
        // ==================================================================== 门

        /// <summary>
        /// fp16 存储的相对地板。用**截断**那一条（2⁻¹⁰）而不是就近取整那一条（2⁻¹¹）：
        /// 注入表是计算着色器写的类型化 UAV，那条路径实测是截断的，证据记在
        /// <see cref="VistaSelfTestNumerics.k_Fp16RelTrunc"/> 的注释里，原始读数由 ⓪ 格打出来。
        /// </summary>
        const float k_Fp16Floor = VistaSelfTestNumerics.k_Fp16RelTrunc;

        /// <summary>
        /// 积分读数的相对门：<see cref="k_Fp16Floor"/> 的 2.05 倍。
        ///
        /// 地板为什么是整个 ulp 而不是一半：注入表的 σ_t / S 由计算着色器写进 fp16，
        /// 而这条路径实测**截断**（12 对读数全部落在请求值下方的网格点，其中 3 对
        /// 「截断」与「就近」结论不同、GPU 三次都取截断）。于是单次量化的相对误差上界
        /// 是 2⁻¹⁰ = 9.766e-4，不是 2⁻¹¹。而且截断是**单边**的，误差不会在多片累加里
        /// 随机抵消，会同向传播 —— 这正是六个档的最坏读数全都贴在同一个数量级下方的原因。
        ///
        /// 第一版把门写成 1e-3，是按半 ulp 的两倍推的。按真实地板算那只有 **1.02 倍**，
        /// 而 ①② 的实测最坏（alpha 9.024e-4、L 9.267e-4）落在门的 0.90~0.93 处 ——
        /// 那一趟全绿是运气，换个档位或换个驱动的取整细节就会翻红，
        /// 「尺子的地板与被测量同量级时，尺子会自己伪造一个结论 —— 它伪造的可以是一个失败」。
        ///
        /// 放到 2.05 倍不丢拒绝力：这道门要拒绝的每一个错答案都远在它之上（每档实测，
        /// 打在报表上）——
        ///   写成 L_{i−1}（少加当前段）在片 0 上是 100%，5 百倍；
        ///   漏掉 T_acc 的乘法累积 +180%~+421%，9 百倍以上；
        ///   段内完全不衰减（S·Δ 矩形）累积 +1.58%~+7.37%，最弱的 D 档也有 7.9 倍。
        /// 唯一落在门下的是「中点透射率」写法（累积 −0.01%~−0.14%），它在 1e-3 的门下
        /// 也一样拒不掉 —— 那一条本来就只打印、不判，理由记在 ③ 那一格。
        /// </summary>
        const float k_RelGate = 2e-3f;

        /// <summary>
        /// fp32 在 1.0 附近的半 ulp = 2⁻²⁴。alpha 存的是 1 − T，T ≈ 1 时这个减法的
        /// 绝对地板就是它；巧的是 fp16 最小非规格数也正好是 2⁻²⁴，两条独立的地板重合。
        /// </summary>
        const double k_Fp32HalfUlpAtOne = 5.9604644775390625e-8;

        /// <summary>
        /// 切片几何的门（dtKm 与 C# 侧的 (segFar − segNear)·scale 比）。
        ///
        /// 这条残差是两个几乎相等的数相减，放大倍数是 ρ/(ρ−1)（因为 Δ/d = 1 − 1/ρ），
        /// 在 ρ 最接近 1 的 D 档是 28.3 倍。而两个操作数各自的误差**不是**表示误差：
        /// GPU 的 pow 与 CPU 的 Mathf.Pow 是同一个超越函数的两份实现（D3D11 只保证
        /// exp2/log2 各 1 ulp，pow = exp2(y·log2(x)) 要 2~3 ulp），所以每个操作数按
        /// <see cref="k_PowUlpBudget"/> 记账。
        ///
        /// 门开在 1e-3，是按「地板 ↔ 要拒绝的最小错答案」摆的，不是按误差上界摆的：
        ///   地板（D 档，最坏）= 8 ulp × 28.3 = 2.7e-5
        ///   最小错答案（D 档，最小）= 差一片 ⇒ dt 偏 ρ 倍 ⇒ |ρ−1| = 3.7e-2
        /// 1e-3 距两边各 37 倍，正好是两者的几何中点。近远端写反是 2.0，缩放丢了是 1.0。
        ///
        /// 第一版把门写成 1e-5、地板写成 ulp/(ρ−1)（漏了 ρ 这个因子、也把超越函数当成
        /// 只有半 ulp），D 档实测 1.474e-5 直接判红 —— 一个尺子自己算错地板的假失败。
        /// </summary>
        const float k_GeometryGate = 1e-3f;

        /// <summary>
        /// dtKm 两个操作数加起来的 ulp 预算：2 个操作数 ×（GPU pow ≈ 3 ulp + CPU 侧 ≈ 1 ulp）。
        /// 它是**上界**而不是估计 —— 实测要落在它下面，落上去说明这个记账漏了一项。
        /// </summary>
        const double k_PowUlpBudget = 8.0;

        /// <summary>fp32 在 1.0 附近的整 ulp = 2⁻²³。</summary>
        const double k_Fp32UlpAtOne = 1.1920928955078125e-7;

        /// <summary>
        /// m→km 缩放常量的门。这不是一个有精度问题的量：GPU 读到的必须**就是**
        /// CPU 那一份 <c>VistaAtmosphereParameters.worldToAtmosphere</c>。
        /// 它要拒绝的最小错答案是 0（忘了调 PrepareLuts ⇒ 相对差 1.0）。
        /// </summary>
        const float k_ConstGate = 1e-6f;

        /// <summary>
        /// 布景读回的门。它测的正是「一次 fp16 量化」本身，所以它的地板就是
        /// <see cref="k_Fp16Floor"/>，一分不多 —— 这一格是全套判据里地板最紧的一格。
        ///
        /// 直接写成 <see cref="k_RelGate"/> 而不是再抄一个 2e-3：上一版这里写的是
        /// 「与 k_RelGate 同量级」加一个手写的 1e-3，于是 k_RelGate 从 1e-3 抬到 2e-3 时
        /// 这一格被**静默解耦**，留在自己地板的 1.02 倍上（F 档实测 7.625e-4，
        /// 已经是门的 0.78）。注释断言的耦合必须由代码保证，否则它只是一句话。
        ///
        /// 它要拒绝的错答案是 O(1) 的：σ_t 写进了 S 的槽位、缩放系数用错、
        /// 合成核根本没跑（读回 0 ⇒ 相对差 1）。2e-3 对这些绰绰有余。
        /// </summary>
        const float k_SynthGate = k_RelGate;

        /// <summary><c>VistaSegmentIntegral</c> 里选级数支的那个阈值，逐字照抄。</summary>
        const double k_SeriesThreshold = 1e-4;

        // ==================================================================== 档位
        //
        // 六档。前五档的口径与 #19 的 A~E 一一对应（同一套几何被两套判据消费，
        // 一处口径改了另一处会跟着动），第六档是 #19 没有的：级数支的覆盖。

        struct Tier
        {
            public string name;
            public int screenW, screenH, divisor, slices;
            public float nearPlane, farMeters, shadowDistance;

            /// <summary>整柱的总光学深度 τ = σ_t · handoff。σ_t 由它反推，所以每档的 alpha 末值可比。</summary>
            public double tauTotal;

            /// <summary>S/σ_t，也就是 L 的渐近值 L_∞。用来把 L 抬离 fp16 的非规格区。</summary>
            public double sourceOverExtinction;

            public string covers;
        }

        static readonly Tier[] k_Tiers =
        {
            new Tier
            {
                name = "A 生产档", screenW = 1920, screenH = 1080, divisor = 8, slices = 64,
                nearPlane = 0.3f, farMeters = 64f, shadowDistance = 500f,
                tauTotal = 3.0, sourceOverExtinction = 1.3,
                covers = "HDRP Medium 同口径；τ = 3 ⇒ T_end ≈ 0.05，alpha 铺满 [0.015, 0.95]",
            },
            new Tier
            {
                name = "B 移动候选", screenW = 1920, screenH = 1080, divisor = 8, slices = 32,
                nearPlane = 0.3f, farMeters = 64f, shadowDistance = 500f,
                tauTotal = 3.0, sourceOverExtinction = 1.3,
                covers = "切片减半 ⇒ 逐段 x 翻倍，被放弃的两种近似的代价也随之翻 4 倍",
            },
            new Tier
            {
                name = "C 宽范围", screenW = 2560, screenH = 1440, divisor = 8, slices = 64,
                nearPlane = 0.1f, farMeters = 200f, shadowDistance = 500f,
                tauTotal = 3.0, sourceOverExtinction = 1.3,
                covers = "r = 2000（A 档 213）⇒ ρ 最大、逐段 x 的动态范围最宽",
            },
            new Tier
            {
                name = "D 密切片", screenW = 1280, screenH = 720, divisor = 4, slices = 128,
                nearPlane = 0.3f, farMeters = 30f, shadowDistance = 500f,
                tauTotal = 3.0, sourceOverExtinction = 1.3,
                covers = "ρ 最接近 1（1.0366）⇒ ⑥ 那条减法残差的地板最高，也是 128 片的容量测点",
            },
            new Tier
            {
                name = "E 夹紧档", screenW = 1920, screenH = 1080, divisor = 8, slices = 64,
                nearPlane = 0.3f, farMeters = 500f, shadowDistance = 200f,
                tauTotal = 6.0, sourceOverExtinction = 1.3,
                covers = "远边界被阴影距离夹住 + τ = 6 ⇒ alpha 顶到 0.9975，压 1 − T 的饱和端；"
                       + "逐段 x 也推到 0.48，超出推导包络上端 0.289",
            },
            new Tier
            {
                name = "F 极稀薄", screenW = 1920, screenH = 1080, divisor = 8, slices = 64,
                nearPlane = 0.3f, farMeters = 64f, shadowDistance = 500f,
                // τ = 1e-3 ⇒ 逐段 x ∈ [5.1e-6, 8.0e-5]，**整柱都走级数支**。
                // 这一档的存在理由：晴空最近一段的 x = 8.1e-6 是线上真实会走到的值
                // （见 CHANGELOG 的包络推导），而级数支此前一个判据都没有 ——
                // 「一个默认关闭、又没有判据覆盖的开关，等于一段永远不会被发现写错的代码」。
                // S/σ_t 抬到 1000：alpha 只能是小的（那正是稀薄的定义），
                // 但 L 必须离开 fp16 的非规格区，否则测到的是格式而不是算术。
                tauTotal = 1.0e-3, sourceOverExtinction = 1000.0,
                covers = "整柱走 VistaSegmentIntegral 的**级数支**（x ≤ 1e-4）—— 线上晴空最近段的量级",
            },
        };

        [MenuItem("Window/Vista/Validate Froxel Integration", priority = 142)]
        static void RunFromMenu()
        {
            var sb = new StringBuilder();
            bool ok = Run(sb);

            string oneLine = sb.ToString().TrimEnd().Replace("\r", "").Replace("\n", "  |  ");
            if (ok) Debug.Log("[Vista] froxel 深度积分自检通过  |  " + oneLine);
            else Debug.LogWarning("[Vista] froxel 深度积分自检失败  |  " + oneLine);
        }

        static bool Run(StringBuilder sb)
        {
            var res = VistaRuntimeResources.Get();
            if (res == null)
            {
                sb.AppendLine("✘ 取不到 VistaRuntimeResources（当前不是 URP？）。");
                return false;
            }
            if (res.volumetricFogCS == null)
            {
                sb.AppendLine("✘ VistaRuntimeResources.volumetricFogCS 为 null —— "
                            + "Shaders/Volumetrics/VolumetricFog.compute 没被 ResourcePath 填上。");
                return false;
            }
            if (res.atmosphereLutCS == null)
            {
                sb.AppendLine("✘ atmosphereLutCS 为 null（VistaAtmosphereLuts 构造需要它）。");
                return false;
            }

            var luts = new VistaAtmosphereLuts(res.atmosphereLutCS, null, res.volumetricFogCS);
            try
            {
                var vol = luts.froxelVolume;
                if (vol == null || !vol.isValid)
                {
                    sb.AppendLine("✘ froxelVolume 不可用（七个核没全找到 / compute 编译失败 / "
                                + "平台被 only_renderers 排除）。");
                    return false;
                }

                sb.Append("　 GPU ").Append(SystemInfo.graphicsDeviceName)
                  .Append("　fp16 地板（截断，整 ulp）").Append(Sci(k_Fp16Floor))
                  .Append("　积分门 ").Append(Sci(k_RelGate))
                  .Append("　= 地板 ×").Append((k_RelGate / k_Fp16Floor).ToString("0.00"))
                  .Append("　几何门 ").Append(Sci(k_GeometryGate))
                  .Append("　常量门 ").Append(Sci(k_ConstGate)).AppendLine();

                var atmo = VistaAtmosphereParameters.CreateEarth();
                var settings = new VistaVolumetricFogSettings();
                bool all = true;
                foreach (var tier in k_Tiers)
                    all &= RunTier(vol, luts, atmo, settings, tier, sb);
                return all;
            }
            finally
            {
                luts.Dispose();
            }
        }

        static bool RunTier(VistaFroxelVolume vol, VistaAtmosphereLuts luts,
                            VistaAtmosphereParameters atmo, VistaVolumetricFogSettings settings,
                            in Tier tier, StringBuilder sb)
        {
            settings.screenDivisor = tier.divisor;
            settings.sliceCount = tier.slices;
            settings.farDistanceMeters = tier.farMeters;

            var desc = settings.Resolve(tier.screenW, tier.screenH, tier.nearPlane,
                                        tier.shadowDistance, out string clampDiag);

            double kmScaleCpu = VistaAtmosphereParameters.worldToAtmosphere;
            double handoffKm = desc.handoffMeters * kmScaleCpu;
            double sigmaReq = tier.tauTotal / handoffKm;                 // 1/km
            double sourceReq = sigmaReq * tier.sourceOverExtinction;     // r 通道的源项基准

            sb.Append("── ").Append(tier.name).Append("　").AppendLine(tier.covers);
            sb.Append("　 ").Append(desc.ToString());
            if (clampDiag != null) sb.Append("　[已夹紧]");
            sb.AppendLine();
            sb.Append("　 布景 τ_total ").Append(tier.tauTotal.ToString("G4"))
              .Append("　σ_t ").Append(Sci(sigmaReq)).Append(" /km")
              .Append("　S_r ").Append(Sci(sourceReq))
              .Append("　S/σ_t ").Append(Sci(tier.sourceOverExtinction))
              .Append("　⇒ alpha_end ").Append((1.0 - System.Math.Exp(-tier.tauTotal)).ToString("G6"))
              .AppendLine();

            // ---- 派发 ----
            // EnsureStaticLuts 而不是裸的 PrepareLuts：后者返回 true 的契约是「调用方本帧
            // **必须**排静态表的两趟 dispatch」。积分核一张 LUT 都不采，但绕过那个契约就得
            // 在这里解释一次为什么可以绕 —— 直接照契约走更便宜。
            // 它顺带就是这一档能跑的前提：m→km 的缩放（_VistaGround.w）只在 PrepareLuts
            // 里被 VistaAtmosphereParameters.Bind 下发。忘了这一步的症状是 dtKm ≡ 0 ⇒
            // 整张积分表全 0，而那与「派发没跑」「布景没写」长得一样 —— 判据⑥就是为它准备的。
            var cmd = new CommandBuffer { name = "Vista Froxel Integration SelfTest" };
            luts.EnsureStaticLuts(cmd, atmo);
            if (!vol.Prepare(desc, cmd))
            {
                cmd.Release();
                sb.AppendLine("　 ✘ Prepare 返回 false（分配失败）。");
                return false;
            }
            vol.EnsureIntegrationReportBuffer(desc.depth);

            var d = new VistaImmediateLutDispatcher(cmd, luts);
            vol.DispatchSynthMedium(d, desc, (float)sigmaReq, (float)sourceReq);
            vol.DispatchIntegration(d, desc);
            vol.DispatchIntegralVerify(d, desc);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            int n = desc.depth;
            var rep = new Vector4[n * VistaFroxelVolume.k_IntegrationReportFloat4PerSlice];
            vol.integrationReportBuffer.GetData(rep);

            bool ok0 = CheckSynth(rep, n, sigmaReq, sourceReq, sb);
            bool ok6 = CheckGeometry(rep, n, desc, kmScaleCpu, sb);
            // ①②③④ 与「判据自身」共用一趟遍历：它们的参考解是同一条递推。
            bool ok1to4 = CheckIntegral(rep, n, desc, sb);
            bool ok5 = CheckCoverage(rep, n, sb);
            return ok0 && ok6 && ok1to4 && ok5;
        }

        static Vector4 Row(Vector4[] rep, int slice, int row)
            => rep[slice * VistaFroxelVolume.k_IntegrationReportFloat4PerSlice + row];

        // ---------------------------------------------------------------- 判据⓪
        //
        // 布景本身。判据的参考解要拿注入表里的 σ_t / S 当输入，所以必须先证明那三个数
        // 确实是本档请求的那三个 —— 否则「参考解与被测值一致」可以由「两边都用了错的布景」
        // 满足，而那是个假通过。
        //
        // 逐片一致性单独判：合成核的 Z 维度算少了的话，尾部若干片会读回 0，
        // 而积分核在那些片上照样自洽（σ_t = 0 ⇒ 什么都不加），判据①②不会红。
        static bool CheckSynth(Vector4[] rep, int n, double sigmaReq, double sourceReq, StringBuilder sb)
        {
            var r0 = Row(rep, 0, 1);
            double relSigma = Rel(r0.w, sigmaReq);
            double relR = Rel(r0.x, sourceReq);
            double relG = Rel(r0.y, sourceReq * 0.5);
            double relB = Rel(r0.z, sourceReq * 0.25);

            int firstMismatch = -1;
            for (int i = 1; i < n && firstMismatch < 0; ++i)
                if (Row(rep, i, 1) != r0) firstMismatch = i;

            bool ok = relSigma <= k_SynthGate && relR <= k_SynthGate
                   && relG <= k_SynthGate && relB <= k_SynthGate && firstMismatch < 0;

            sb.Append("　 ").Append(Mark(ok)).Append("⓪ 布景读回：σ_t 相对差 ").Append(Sci(relSigma))
              .Append("　S(r/g/b) ").Append(Sci(relR)).Append(" / ").Append(Sci(relG))
              .Append(" / ").Append(Sci(relB))
              .Append("（门 ").Append(Sci(k_SynthGate))
              .Append(" = 地板 ×").Append((k_SynthGate / k_Fp16Floor).ToString("0.00"))
              .Append("，余量 ×")
              .Append((k_SynthGate / System.Math.Max(System.Math.Max(relSigma, relR),
                                                     System.Math.Max(relG, relB))).ToString("0.00"))
              .Append("　注入表是 fp16，这一格测的就是那一次量化）");
            if (firstMismatch >= 0)
                sb.Append("　← 片 ").Append(firstMismatch)
                  .Append(" 的注入内容与片 0 不同：合成核的 Z 维度算少了（尾部若干片没被写）");
            else
                sb.Append("　逐片逐位一致 ✔");
            sb.AppendLine();
            // ⓘ 原始读数 + 取整模式归因。
            //
            // 为什么值得印：①② 的门是按「fp16 往返一次」的地板摆的，而那个地板的大小
            // 取决于**取整模式** —— 就近取整是半 ulp 2⁻¹¹ = 4.883e-4，截断是整 ulp
            // 2⁻¹⁰ = 9.766e-4，差一倍。第一版按前者摆了 1e-3 的门，实测地板却是后者，
            // 门只剩 1.02 倍余量，全绿是运气。
            //
            // 为什么不只印数字、而要把结论也算出来：「断言一个自己没有保留的中间读数
            // 等于编造证据」，而只印数字则是把归因推给读报表的人 —— 那条推导（落在
            // 哪两个网格点之间、GPU 取了哪个）以后没人会重做。这里直接判给它看。
            //
            // 为什么是 ⓘ 而不是门：截断是这台机器的**实测**行为，不是正确性要求。
            // 若哪天驱动改成就近取整，那是严格变好，判成红格就是假失败。
            // 它的作用是：一旦取整模式变了，报表会说出来，于是 k_RelGate 的
            // 理由行会**显式**变旧，而不是悄悄变旧。
            sb.Append("　 ⓘ 取整模式：");
            AppendRounding(sb, "σ_t", r0.w, sigmaReq);
            sb.Append("　");
            AppendRounding(sb, "S_r", r0.x, sourceReq);
            sb.Append("　⇒ 地板取 ").Append(Sci(k_Fp16Floor))
              .AppendLine("（整 ulp 2⁻¹⁰；就近取整则是它的一半）");
            return ok;
        }

        // ---------------------------------------------------------------- 判据⑥
        //
        // 归因行。两个读数，第一个是 #21 唯一的隐藏依赖：
        //
        //   dtKm = VistaFroxelSegmentLengthKm(slice, _VistaGround.w)
        //
        // 那个 .w 由 VistaAtmosphereParameters.Bind 上传，而 Bind 只被 PrepareLuts 调用。
        // 立即模式的判据要是没走到 PrepareLuts，它就是 0（或者上一次渲染留下的脏值）：
        // dtKm ≡ 0 ⇒ x ≡ 0 ⇒ 整张积分表全 0。那时①②会红，但成因有三个
        //（派发没跑 / 布景没写 / 缩放是 0）分不开。把缩放印出来就只剩一个。
        //
        // 参考值取 CPU 那一份 const，不写字面量 0.001 —— 这一格判的就是「两边是同一份」。
        static bool CheckGeometry(Vector4[] rep, int n, in VistaFroxelVolumeDesc desc,
                                  double kmScaleCpu, StringBuilder sb)
        {
            float kmScaleGpu = Row(rep, 0, 3).y;
            double relScale = Rel(kmScaleGpu, kmScaleCpu);
            bool okScale = relScale <= k_ConstGate;

            double worst = 0.0;
            int argWorst = -1;
            for (int i = 0; i < n; ++i)
            {
                double refKm = ((double)desc.SegmentFar(i) - desc.SegmentNear(i)) * kmScaleGpu;
                double rel = Rel(Row(rep, i, 3).x, refKm);
                if (rel > worst) { worst = rel; argWorst = i; }
            }
            bool okDt = worst <= k_GeometryGate;

            // 放大倍数 ρ/(ρ−1)：Δ_i = d_i(1 − 1/ρ)，所以操作数的相对误差被放大 ρ/(ρ−1) 倍。
            double amp = desc.sliceRatio / System.Math.Max(desc.sliceRatio - 1.0, 1e-9);
            double floorBound = k_PowUlpBudget * k_Fp32UlpAtOne * amp;
            double smallestWrong = System.Math.Abs(desc.sliceRatio - 1.0);   // 差一片

            sb.Append("　 ").Append(Mark(okScale && okDt))
              .Append("⑥ 归因：_VistaGround.w = ").Append(Sci(kmScaleGpu))
              .Append("（CPU 常量 ").Append(Sci(kmScaleCpu))
              .Append("，相对差 ").Append(Sci(relScale)).Append("，门 ").Append(Sci(k_ConstGate))
              .Append("；要拒绝的最小错答案是 0 ⇒ 相对差 1）")
              .Append("　dtKm 最坏相对差 ").Append(Sci(worst))
              .Append(" @ 片 ").Append(argWorst)
              .Append("（门 ").Append(Sci(k_GeometryGate)).Append("）")
              .AppendLine();
            sb.Append("　　 ⓘ 这一格的门摆在两个数之间：地板上界 ").Append(Sci(floorBound))
              .Append("（8 ulp × ρ/(ρ−1) = ").Append(amp.ToString("0.0")).Append("）")
              .Append("　实测/地板 = ").Append((worst / floorBound).ToString("0.00"))
              .Append("　要拒绝的最小错答案（差一片 ⇒ 偏 ρ 倍）").Append(Sci(smallestWrong))
              .Append("　门距两边 ").Append((k_GeometryGate / floorBound).ToString("0")).Append("× / ")
              .Append((smallestWrong / k_GeometryGate).ToString("0")).AppendLine("×");
            return okScale && okDt;
        }

        // ---------------------------------------------------------------- 判据①②③④
        //
        // 一趟遍历，五组读数：
        //   ① alpha_i 与 1 − Π e^{−x_j} 比（相对 + 绝对双门）
        //   ② L_i 与 (S_c/σ_t)·alpha_i 比（相对），外加三个通道的比值
        //   ③ x = σ_t·Δ 的包络与所走的分支
        //   ④ 有限性 / 值域 / 单调不减
        //   ⓘ 闭式解与逐段求和的残差 —— 望远镜恒等式不当假设用
        //
        // 参考解的输入全部是 GPU 报回来的**输入量**（σ_t、S、dtKm），不含任何一个被测输出。
        static bool CheckIntegral(Vector4[] rep, int n, in VistaFroxelVolumeDesc desc, StringBuilder sb)
        {
            double sigma = Row(rep, 0, 1).w;
            double sR = Row(rep, 0, 1).x;
            double sG = Row(rep, 0, 1).y;
            double sB = Row(rep, 0, 1).z;

            // σ_t = 0（布景没写 / 绑定错）会让参考解里的 (S/σ) 变成 Inf，而 Rel(finite, Inf)
            // 是 NaN，NaN > worst 恒为 false ⇒ 这一格会**印出「达标」**。
            // 「一个把未判达标印成达标的判据，比一个平门更危险」，所以先把它挡在门外。
            if (!(sigma > 0.0) || double.IsInfinity(sigma))
            {
                sb.Append("　 ✘ ①②③④ 全部未判：注入表报回的 σ_t = ").Append(Sci(sigma))
                  .AppendLine("（≤ 0 或非有限）⇒ 参考解 (S/σ_t) 无定义。成因见 ⓪ / ⑥ 两行。");
                return false;
            }

            // alpha 的绝对地板：alpha 存 1 − T，T ≈ 1 时那个减法的地板是 2⁻²⁴；
            // T 又是 N 次乘积，最坏情况把地板放大到 N 倍。取 2 倍余量。
            // fp16 最小非规格数也正好是 2⁻²⁴，两条独立的地板重合在同一个数上。
            double alphaAbsFloor = n * k_Fp32HalfUlpAtOne;
            double alphaAbsGate = 2.0 * alphaAbsFloor;

            double tRef = 1.0, dAcc = 0.0;
            double lSum = 0.0;                 // 逐段求和（只用来验望远镜恒等式）
            double lNoT = 0.0, lMid = 0.0, lRect = 0.0;   // 三种被放弃的写法
            double xMin = double.MaxValue, xMax = 0.0;
            double worstAlphaRel = 0.0, worstAlphaAbs = 0.0, worstL = 0.0, worstIdent = 0.0;
            int argAlpha = -1, argL = -1;
            char argLCh = '?';
            int nonFinite = 0, seriesCount = 0, alphaFail = 0;
            double alphaLo = double.MaxValue, alphaHi = -double.MaxValue, lLo = double.MaxValue;
            bool monoAlpha = true, monoL = true;
            float prevAlpha = -1f, prevL = -1f;
            double alphaRefEnd = 0.0, lRefEnd = 0.0;

            for (int i = 0; i < n; ++i)
            {
                double dt = Row(rep, i, 3).x;      // km，核自己算的那一个
                double x = sigma * dt;
                if (x < xMin) xMin = x;
                if (x > xMax) xMax = x;
                if (x <= k_SeriesThreshold) seriesCount++;

                double segPerS = (1.0 - System.Math.Exp(-x)) / sigma;   // ∫₀^dt e^{−σt} dt
                lSum += tRef * sR * segPerS;
                lNoT += sR * segPerS;                                   // 漏掉 T_acc
                lMid += tRef * sR * dt * System.Math.Exp(-0.5 * x);     // 中点透射率
                lRect += tRef * sR * dt;                                // 段内完全不衰减
                tRef *= System.Math.Exp(-x);
                dAcc += dt;

                double alphaRef = 1.0 - tRef;
                double lRefR = (sR / sigma) * alphaRef;
                alphaRefEnd = alphaRef;
                lRefEnd = lRefR;

                // 望远镜恒等式的残差。它是判据自身完备性的读数，不是被测对象的。
                double ident = Rel(lSum, lRefR);
                if (ident > worstIdent) worstIdent = ident;

                var got = Row(rep, i, 0);
                if (!IsFinite(got)) { nonFinite++; continue; }

                double aAbs = System.Math.Abs(got.w - alphaRef);
                double aRel = alphaRef > 0.0 ? aAbs / alphaRef : aAbs;
                // 双门：相对差超门**且**绝对差脱离地板才算失败。
                // 相对读数本身无条件记录 —— 只在「判失败」时才记的话，
                // 地板占优的档会打印一个 0，让「没判」长得像「完美」。
                if (aAbs > worstAlphaAbs) worstAlphaAbs = aAbs;
                if (aRel > worstAlphaRel) { worstAlphaRel = aRel; argAlpha = i; }
                if (aRel > k_RelGate && aAbs > alphaAbsGate) alphaFail++;

                Worst(ref worstL, ref argL, ref argLCh, got.x, lRefR, i, 'r');
                Worst(ref worstL, ref argL, ref argLCh, got.y, (sG / sigma) * alphaRef, i, 'g');
                Worst(ref worstL, ref argL, ref argLCh, got.z, (sB / sigma) * alphaRef, i, 'b');

                if (got.w < alphaLo) alphaLo = got.w;
                if (got.w > alphaHi) alphaHi = got.w;
                double lMin3 = System.Math.Min(got.x, System.Math.Min(got.y, got.z));
                if (lMin3 < lLo) lLo = lMin3;

                if (got.w < prevAlpha) monoAlpha = false;
                if (got.x < prevL) monoL = false;
                prevAlpha = got.w;
                prevL = got.x;
            }

            bool okAlpha = alphaFail == 0 && nonFinite == 0;
            bool okL = worstL <= k_RelGate && nonFinite == 0;
            bool okRange = nonFinite == 0 && alphaLo >= 0f && alphaHi <= 1f && lLo >= 0f
                        && monoAlpha && monoL;
            bool okIdent = worstIdent <= 1e-10;
            // 全档都必须落在同一支：混着走说明包络算错了，而那时「分支覆盖」这件事
            // 在报表上会被一个含混的读数掩盖。
            bool okBranch = seriesCount == 0 || seriesCount == n;

            // ---- ① ----
            sb.Append("　 ").Append(Mark(okAlpha)).Append("① alpha = 1 − T　最坏相对 ")
              .Append(Sci(worstAlphaRel)).Append(" @ 片 ").Append(argAlpha)
              .Append("　最坏绝对 ").Append(Sci(worstAlphaAbs))
              .Append("（相对门 ").Append(Sci(k_RelGate))
              .Append("，绝对地板 N·2⁻²⁴ = ").Append(Sci(alphaAbsFloor))
              .Append(" ⇒ 绝对门 ").Append(Sci(alphaAbsGate)).Append("）")
              .Append("　超双门片数 ").Append(alphaFail).Append("/").Append(n)
              .AppendLine();
            if (alphaAbsGate >= k_RelGate * alphaRefEnd)
                sb.AppendLine("　　 ⓘ 本档 alpha 的绝对地板已经吞掉了相对门（alpha 太小）——"
                            + "这一格**不承担**拒绝 off-by-one 的责任，那由 τ 较大的档承担；"
                            + "本档的拒绝力在 ② 那一行（L 被 S/σ_t 抬离了非规格区）。");

            // ---- ② ----
            var end = Row(rep, n - 1, 0);
            sb.Append("　 ").Append(Mark(okL)).Append("② L = (S/σ_t)·alpha　最坏相对 ")
              .Append(Sci(worstL)).Append(" @ 片 ").Append(argL).Append(" 通道 ").Append(argLCh)
              .Append("（门 ").Append(Sci(k_RelGate)).Append(" = 地板 ×")
              .Append((k_RelGate / k_Fp16Floor).ToString("0.00"))
              .Append("，余量 ×").Append((k_RelGate / System.Math.Max(worstL, 1e-30)).ToString("0.00"))
              .Append("）")
              .Append("　末片 L_r ").Append(Sci(end.x)).Append("（参考 ").Append(Sci(lRefEnd)).Append("）")
              .AppendLine();
            // 通道比：写成灰的话「rgb 被 swizzle」在报表上完全不可见，而那是一个
            // 只改颜色不改量级的错误 —— 最容易活到发布。
            sb.Append("　　 通道比 g/r = ").Append(Ratio(end.y, end.x))
              .Append("（期望 0.5）　b/r = ").Append(Ratio(end.z, end.x))
              .AppendLine("（期望 0.25）—— 布景故意不是灰的，swizzle 会在这里露出来");
            // 三个被放弃的写法在末片给出的偏差。它们是「门要拒绝的错答案」的实测大小，
            // 全部由 C# 侧的 double 参考积分器算出来，一个被测读数都不含。
            //
            // 注意这是**整柱累积**的偏差，不是 CHANGELOG 里那个逐段的数（+12.2% / −0.35% @ x_max）。
            // 累积值被前面 x 小的段稀释了，所以两个数不一样、也都对 —— 单段偏差才是
            // 「这条近似最坏能错多少」，累积偏差才是「画面上会差多少」。两个都印。
            double devNoT = lNoT / lRefEnd - 1.0;
            double devMid = lMid / lRefEnd - 1.0;
            double devRect = lRect / lRefEnd - 1.0;
            sb.Append("　　 被放弃的写法在**末片累积**上的偏差：漏乘 T_acc ").Append(Pct(devNoT))
              .Append("　中点透射率 ").Append(Pct(devMid))
              .Append("　段内不衰减 ").Append(Pct(devRect))
              .Append("　| 同三条在**最长那一段**上：")
              .Append("中点 ").Append(Pct(-xMax * xMax / 24.0))
              .Append("　不衰减 ").Append(Pct(xMax / 2.0))
              .Append("　| Weber 1% 之内的：")
              .Append(System.Math.Abs(devNoT) < 0.01 ? "漏乘T " : "")
              .Append(System.Math.Abs(devMid) < 0.01 ? "中点 " : "")
              .Append(System.Math.Abs(devRect) < 0.01 ? "不衰减 " : "")
              .AppendLine("（本档口径下）");

            // ---- ③ ----
            // 两支的固有误差：exact 支是 S − S·e^{−x} 的抵消 ⇒ ≈ ulp/x；
            // 级数支是截断 ⇒ ≈ x³/24（因为 (1−e^{−x})/x = 1 − x/2 + x²/6 − x³/24 而级数只留到 x²/6）。
            // 相等处 x⁴ = 24·ulp ⇒ x* = 4.11e-2，这才是**最优**阈值。
            // 线上那一支写的是 1e-4，比最优低了两个半数量级 —— 于是 x ∈ [1e-4, 4.11e-2]
            // 这一整段区间里，代码故意走了误差较大的那一支，代价上限 ulp/1e-4 = 1.19e-3。
            // 这是一条**实测到的**、可以只改一个常量的改进；但那个常量是 AP 也在用的
            // VistaSegmentIntegral 的，改它要重跑 AP 的两套判据，所以留给 #25。
            double xStar = System.Math.Pow(24.0 * k_Fp32UlpAtOne, 0.25);
            double branchErr = seriesCount == n
                ? xMax * xMax * xMax / 24.0
                : k_Fp32UlpAtOne / System.Math.Max(xMin, 1e-30);
            sb.Append("　 ").Append(Mark(okBranch)).Append("③ x = σ_t·Δ ∈ [")
              .Append(Sci(xMin)).Append(", ").Append(Sci(xMax)).Append("]　")
              .Append(seriesCount == n ? "整柱走**级数支**" :
                      seriesCount == 0 ? "整柱走 exact 支" : "两支混着走（包络算错了？）")
              .Append("（阈值 x ≤ ").Append(Sci(k_SeriesThreshold)).Append("，级数片数 ")
              .Append(seriesCount).Append("/").Append(n).Append("）")
              .Append("　推导包络 [8.1e-6, 2.89e-1]").AppendLine();
            sb.Append("　　 ⓘ 所走那一支的固有误差（推导，非实测）最坏 ").Append(Sci(branchErr))
              .Append("　最优阈值 x* = (24·ulp)^¼ = ").Append(Sci(xStar))
              .Append("，线上写的是 ").Append(Sci(k_SeriesThreshold));
            if (seriesCount == 0 && xMin < xStar)
                sb.Append("　← 本档最短的一段（x = ").Append(Sci(xMin))
                  .Append("）落在「阈值选低了」的带里：该走级数支（误差 ")
                  .Append(Sci(xMin * xMin * xMin / 24.0))
                  .Append("）却走了 exact 支（").Append(Sci(branchErr))
                  .Append("）—— 一个常量的事，但那个常量 AP 也在用，留给 #25");
            sb.AppendLine();

            // ---- ④ ----
            sb.Append("　 ").Append(Mark(okRange)).Append("④ 有限性/值域/单调：非有限 ")
              .Append(nonFinite).Append(" 片　alpha ∈ [").Append(Sci(alphaLo)).Append(", ")
              .Append(Sci(alphaHi)).Append("]　min L ").Append(Sci(lLo))
              .Append("　alpha 单调 ").Append(monoAlpha ? "✔" : "✘")
              .Append("　L 单调 ").Append(monoL ? "✔" : "✘")
              .AppendLine("　（累积量单调不减是定义性不变量；σ_t 若被写成负数，"
                        + "VistaSegmentIntegral 的 max(σ,1e-30) 会把它变成静默爆炸，"
                        + "这一格是它唯一的拦截点）");

            // ---- 判据自身 ----
            sb.Append("　 ").Append(Mark(okIdent)).Append("ⓘ 判据自身：闭式解 (S/σ)(1−T) vs 逐段求和 "
                    + "Σ T_{j−1}·S·(1−e^{−x_j})/σ 的最坏相对差 ")
              .Append(Sci(worstIdent))
              .AppendLine("　—— 望远镜恒等式成立 ⇒ 参考解里没有离散化误差项，"
                        + "这是门能开在 fp16 地板的 2 倍上的全部依据");

            return okAlpha && okL && okRange && okIdent && okBranch;
        }

        // ---------------------------------------------------------------- 判据⑤
        //
        // 覆盖性。均匀合成介质下同一片里每一列必须**逐位相同** —— 每列跑的是同一串
        // 算术、吃的是同一批输入。min 与 max 一旦分开就说明有一部分 froxel 没被写。
        //
        // 只读 (0,0) 一列的话，「XY 的 group 数少算一半」在报表上完全看不出来。
        // 哨兵态（aMin = 1e30、aMax = −1e30）单独点名：那是「这一片一列都没扫到」，
        // 与「扫到了但内容不一致」的修法完全不同。
        static bool CheckCoverage(Vector4[] rep, int n, StringBuilder sb)
        {
            int firstSplit = -1, firstSentinel = -1, firstLumSplit = -1;
            double worstSpread = 0.0;
            int nonFinite = 0;

            for (int i = 0; i < n; ++i)
            {
                var r2 = Row(rep, i, 2);
                nonFinite += (int)r2.x;

                if (r2.z <= -1e29f || r2.y >= 1e29f)
                {
                    if (firstSentinel < 0) firstSentinel = i;
                    continue;
                }
                if (r2.y != r2.z && firstSplit < 0) firstSplit = i;
                double spread = System.Math.Abs((double)r2.z - r2.y);
                if (spread > worstSpread) worstSpread = spread;

                // 扫描里的 lumMax 必须逐位等于 (0,0) 那一列的最大通道（r，因为 1:0.5:0.25）。
                if (r2.w != Row(rep, i, 0).x && firstLumSplit < 0) firstLumSplit = i;
            }

            bool ok = firstSplit < 0 && firstSentinel < 0 && firstLumSplit < 0 && nonFinite == 0;
            sb.Append("　 ").Append(Mark(ok)).Append("⑤ 覆盖性：逐片 16×16 列的 alpha min == max（逐位）")
              .Append("　最坏跨列离散 ").Append(Sci(worstSpread))
              .Append("　扫描内非有限 ").Append(nonFinite);
            if (firstSentinel >= 0)
                sb.Append("　← 片 ").Append(firstSentinel)
                  .Append(" 停在哨兵态（±1e30）：这一片一列都没被扫到，"
                        + "判据核的 Z 派发或 size.z 读错了");
            if (firstSplit >= 0)
                sb.Append("　← 片 ").Append(firstSplit)
                  .Append(" 的跨列 alpha 不一致：积分核的 XY 派发少算了一部分 froxel");
            if (firstLumSplit >= 0)
                sb.Append("　← 片 ").Append(firstLumSplit)
                  .Append(" 的扫描 lumMax 与 (0,0) 列不同（同一片内 L 也分叉了）");
            sb.AppendLine();
            return ok;
        }

        // ==================================================================== 小工具

        static void Worst(ref double worst, ref int argSlice, ref char argCh,
                          float got, double expect, int slice, char ch)
        {
            double rel = Rel(got, expect);
            if (rel <= worst) return;
            worst = rel;
            argSlice = slice;
            argCh = ch;
        }

        static double Rel(double got, double expect)
            => System.Math.Abs(got - expect) / System.Math.Max(System.Math.Abs(expect), 1e-30);

        static bool IsFinite(Vector4 v)
            => !(float.IsNaN(v.x) || float.IsInfinity(v.x) || float.IsNaN(v.y) || float.IsInfinity(v.y)
              || float.IsNaN(v.z) || float.IsInfinity(v.z) || float.IsNaN(v.w) || float.IsInfinity(v.w));

        /// <summary>
        /// 把一对（请求值，fp16 读回值）归到 half 网格上，判出 GPU 用的是截断还是就近取整。
        ///
        /// 关键在最后那个标注：只有当请求值落在两个网格点之间、且**两种取整给出不同答案**时
        /// （即格间小数 &gt; 0.5），这一对才携带证据。落在 0.5 以下的那些对，两种模式结论
        /// 相同，读数与截断一致完全说明不了问题 —— 那种「一致」是空的。
        /// 不把这一点标出来，报表就会用 12 条里 9 条没有区分力的样本去支撑一个结论。
        /// </summary>
        static void AppendRounding(StringBuilder sb, string label, double got, double req)
        {
            sb.Append(label).Append(" 读回 ").Append(got.ToString("G9"))
              .Append(" / 请求 ").Append(req.ToString("G9"));

            double a = System.Math.Abs(req);
            if (!(a > 0.0) || double.IsInfinity(a)) { sb.Append("（无法归网格）"); return; }

            float ulp = VistaSelfTestNumerics.HalfUlp((float)req);
            double baseExp = System.Math.Pow(2.0, System.Math.Floor(System.Math.Log(a, 2.0)));
            double k = (a - baseExp) / ulp;
            double frac = k - System.Math.Floor(k);
            double trunc = baseExp + System.Math.Floor(k) * ulp;
            double near = baseExp + System.Math.Round(k) * ulp;
            bool discriminating = System.Math.Abs(trunc - near) > ulp * 1e-3;

            string mode = System.Math.Abs(got - trunc) <= ulp * 1e-3 ? "截断"
                        : System.Math.Abs(got - near) <= ulp * 1e-3 ? "就近"
                        : "落在网格外(?)";
            sb.Append("　→ ").Append(mode);
            sb.Append(discriminating ? "【有区分力，格间 " : "（无区分力，格间 ")
              .Append(frac.ToString("0.000"))
              .Append(discriminating ? "，两模式结论不同】" : "，两模式同解）");
        }

        static string Sci(double v) => v.ToString("0.000e+0");
        // 带符号打印：一个把负数压成 0 的格式化会让「偏高」「偏低」「压线」长得一样。
        static string Pct(double v) => (100.0 * v).ToString("+0.00;-0.00") + "%";

        static string Ratio(float a, float b)
            => Mathf.Abs(b) < 1e-30f ? "n/a" : ((double)a / b).ToString("0.0000");

        static string Mark(bool ok) => ok ? "✔ " : "✘ ";
    }
}
