using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// 近层 froxel 与航空透视的**雾归属分层**判定（#25）。
    ///
    /// ---- 被测的是什么 ----
    /// #25 把同一份雾沿视线切成两层：
    ///     σ_AP(t)     = σ_fog(t) · w(t)
    ///     σ_froxel(t) = σ_fog(t) · (1 − w(t))
    /// 两份权重逐点求和为 1，所以 T_near · T_ap **恰好**等于分层前的单层 T ——
    /// 这不是"近似相等"，是代数恒等式。整个 #25 的正确性就压在这条恒等式上：
    /// 一旦 w 与 1−w 不是同一个 w，雾会在交接带附近凭空多出来或少掉一截，
    /// 而症状（"远处雾比近处浓一点点"）与"参数没调好"完全无法区分。
    ///
    /// 于是这套判据盯的不是"雾好不好看"，而是四件可以逐位判定的事：
    ///   ① 两层的权重互补、两层的 σ 相加复原原值；
    ///   ② 不该被权重缩放的字段（density / phaseG）三份逐位相同；
    ///   ③ w 在六个指定点上取到该取的值，且沿 t 单调不减；
    ///   ④ 零态（分层 cbuffer 全零）下 w ≡ 1、k ≡ 0 —— 也就是"退回改动前"；
    ///   ⑤ CPU 侧 PackedLayering 为零 ⟺ UsesNearLayer 为 false，遍历档位矩阵。
    ///
    /// ---- 为什么这条判据不需要 froxel 体、不需要相机、不需要一张纹理 ----
    /// <c>FroxelLayeringProbe</c> 核一张纹理都不绑、一张表都不读：权重是 t 与
    /// <c>VistaFogCB</c> 的纯函数，雾采样是 t、rayDir.y 与同一个 cbuffer 的纯函数
    /// （<c>VistaSampleFogAlongRay</c> 里没有任何 SAMPLE_）。所以这里既不 Prepare
    /// froxel 体也不 EnsureStaticLuts —— 挂上那两道门的代价是判据在"体还没分配"
    /// 的那一帧变成空判据，而分层这件事跟体分不分配得出来毫无关系。
    ///
    /// ---- 分层 cbuffer 由**线上那份绑定代码**推下去 ----
    /// 派发前走的是 <c>VistaAtmosphereViewData.BindFog</c>，不是自己 SetGlobalVector。
    /// 自己补一发等于让判据自带一份布景，于是"CPU 打包的 D 和 shader 用的 D 不是
    /// 同一个"这条失效会被探针自己抹平 —— 而那正是判据⑥要抓的东西。
    ///
    /// ---- 空判据的处理 ----
    /// 每个格子先报**分辨力**再报结论。零态档里 w ≡ 1，于是"density 有没有被
    /// 权重缩放"在那一档退化成"x·1 == x"，恒真且什么都没测到 —— 那种格子打 ⓘ
    /// 而不是 ✔，且不计入通过与否。一个恒真的绿比一个红更贵。
    /// </summary>
    public static class VistaFogLayeringSelfTest
    {
        // 采样点数。前 6 个是指定点（见 VistaFroxelVolume.k_LayeringProbeAnchors），
        // 其余 58 个沿 [0, sweep] 均匀铺开 —— 单调性与零态两条判据靠的是这 58 个。
        const int k_SampleCount = 64;

        // 零态档的扫掠上界（km）。零态下 D = 0，六个指定点会**全部塌到 t = 0**，
        // 传 2·D 的话"零态下 w ≡ 1"就只测了一个点。所以零态档必须传一个固定正数。
        const float k_ZeroStateSweepKm = 0.2f;

        // 判据①与③的容差预算，单位是 1.0 附近的 fp32 ulp。
        //
        // 为什么不是 0（逐位）：w = saturate(1 − (D − t)·rcpBand)，而指定点的 t 本身
        // 是由 D 与 band 算出来的（例如 t = D − band/2）。fp32 下 D − (D − band/2)
        // 不保证恰好等于 band/2，再乘 rcpBand = 1/band 又是一次舍入 —— 两次舍入之后
        // 0.5 可以差出一两个 ulp。把门开到 0 会让判据在**算术完全正确**时变红。
        // 4 个 ulp 是"两次舍入 + 一次减法"的宽松上界；实测值会逐格打出来，
        // 谁都能自己核这个预算有没有被用满。
        const int k_UlpBudget = 4;

        // 米→千米。与 VistaFogSettings.PackedLayering 里那个字面量必须是同一个数 ——
        // 它在 ⑤ 里只用来把参考值抬到 double 精度上算（见那里的注释），
        // 不用来复刻被测的 float 算式。
        const float k_MetersToKm = 0.001f;

        [MenuItem("Window/Vista/Validate Fog Layering", priority = 143)]
        static void RunFromMenu()
        {
            var sb = new StringBuilder();
            bool ok = Run(sb);
            string oneLine = sb.ToString().TrimEnd().Replace("\r", "").Replace("\n", "  |  ");
            if (ok) Debug.Log("[Vista] 雾分层自检通过  |  " + oneLine);
            else    Debug.LogWarning("[Vista] 雾分层自检失败  |  " + oneLine);
        }

        readonly struct Tier
        {
            public readonly string name;
            public readonly VistaFogSettings.Mode mode;
            public readonly float mfpMeters;
            public readonly float scaleHeightMeters;
            public readonly float handoffMeters;
            public readonly float rayDirY;
            public readonly float cameraY;

            public Tier(string name, VistaFogSettings.Mode mode, float mfpMeters,
                        float scaleHeightMeters, float handoffMeters,
                        float rayDirY, float cameraY)
            {
                this.name = name;
                this.mode = mode;
                this.mfpMeters = mfpMeters;
                this.scaleHeightMeters = scaleHeightMeters;
                this.handoffMeters = handoffMeters;
                this.rayDirY = rayDirY;
                this.cameraY = cameraY;
            }
        }

        // rayDirY **一个都不能是 0**：雾的高度剖面沿 y 变，dir.y = 0 会让 density
        // 全程恒等于同一个数，于是判据②"density 没被权重缩放"退化成"常量 == 常量"。
        // 三个近层档的 rayDirY 正负都有，顺带覆盖"往下看"与"往上看"两种剖面走向。
        static readonly Tier[] k_Tiers =
        {
            // ---- 近层档：分层真正生效，判据①②③⑥ 有分辨力 ----
            new Tier("生产档 D=48m",   VistaFogSettings.Mode.Froxel,             400f,  50f,  48f, -0.35f, 120f),
            new Tier("浓雾短接手 D=12m", VistaFogSettings.Mode.Froxel,              60f,  15f,  12f, -0.72f,  40f),
            new Tier("长接手 D=200m",   VistaFogSettings.Mode.Froxel,            1200f, 300f, 200f,  0.45f,  20f),

            // ---- 零态档：三条互不相同的"退回改动前"的路径，判据④ 有分辨力 ----
            // 它们必须**分别**跑：三者走的是 UsesNearLayer 里三个不同的合取项，
            // 只测一条的话另外两条改坏了照样全绿。
            new Tier("零态·AP 模式",    VistaFogSettings.Mode.AerialPerspective, 400f,  50f,  48f, -0.50f, 120f),
            new Tier("零态·雾关闭",     VistaFogSettings.Mode.Off,               400f,  50f,  48f, -0.50f, 120f),
            new Tier("零态·D=0",       VistaFogSettings.Mode.Froxel,            400f,  50f,   0f, -0.50f, 120f),
        };

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

            var luts = new VistaAtmosphereLuts(res.atmosphereLutCS, null, res.volumetricFogCS);
            try
            {
                var vol = luts.froxelVolume;
                if (vol == null || !vol.isValid)
                {
                    sb.AppendLine("✘ froxel 体无效（核没编出来？）—— 分层探针核也在同一份 compute 里。");
                    return false;
                }

                float ulp1 = VistaSelfTestNumerics.Fp32Ulp(1f);
                double gate = k_UlpBudget * (double)ulp1;

                sb.AppendLine($"GPU = {SystemInfo.graphicsDeviceName}；采样点 N = {k_SampleCount}"
                            + $"（指定点 {VistaFroxelVolume.k_LayeringProbeAnchors} + 扫掠 "
                            + $"{k_SampleCount - VistaFroxelVolume.k_LayeringProbeAnchors}）；"
                            + $"ulp(1) = {Sci(ulp1)}，权重/σ 的门 = {k_UlpBudget} ulp = {Sci(gate)}；"
                            + $"交接带常量 = {VistaFogSettings.k_HandoffBandFraction:0.####}。");

                var atmo = VistaAtmosphereParameters.CreateEarth();
                bool all = true;
                foreach (var tier in k_Tiers)
                    all &= RunTier(tier, luts, vol, atmo, gate, sb);

                all &= RunCpuMatrix(gate, sb);
                return all;
            }
            finally
            {
                luts.Dispose();
            }
        }

        static bool RunTier(in Tier tier, VistaAtmosphereLuts luts, VistaFroxelVolume vol,
                            VistaAtmosphereParameters atmo, double gate, StringBuilder sb)
        {
            var fog = new VistaFogSettings
            {
                mode = tier.mode,
                densityInput = VistaFogSettings.DensityInput.MeanFreePath,
                meanFreePathMeters = tier.mfpMeters,
                scaleHeightMeters = tier.scaleHeightMeters,
                bottomWorldY = 0f,
                anisotropy = 0.7f,
            };

            Vector4 cpuLayering = fog.PackedLayering(tier.handoffMeters);
            bool nearLayer = fog.UsesNearLayer(tier.handoffMeters);
            float sweepKm = nearLayer ? 2f * cpuLayering.x : k_ZeroStateSweepKm;

            // ---- 派发 ----
            vol.EnsureLayeringProbeBuffer(k_SampleCount);
            var cmd = new CommandBuffer { name = "Vista Fog Layering SelfTest" };
            var d = new VistaImmediateLutDispatcher(cmd, luts);
            var view = VistaAtmosphereViewData.Create(
                atmo, new Vector3(0f, tier.cameraY, 0f), 0f, new Vector3(0.3f, 0.6f, 0.74f).normalized);
            view.BindFog(d, fog, tier.handoffMeters);          // ← 线上那份绑定代码
            vol.DispatchLayeringProbe(d, k_SampleCount, sweepKm, tier.rayDirY);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            var rep = new Vector4[k_SampleCount * VistaFroxelVolume.k_LayeringProbeFloat4PerSample];
            vol.layeringProbeBuffer.GetData(rep);

            Vector4 Row(int s, int r) => rep[s * VistaFroxelVolume.k_LayeringProbeFloat4PerSample + r];

            sb.AppendLine($"—— 档「{tier.name}」 mode={tier.mode} σ={fog.extinctionPerKm:0.###}/km "
                        + $"H={tier.scaleHeightMeters:0.#}m D={tier.handoffMeters:0.##}m "
                        + $"band={cpuLayering.y * 1000f:0.###}m rayY={tier.rayDirY:0.00} "
                        + $"camY={tier.cameraY:0.#} sweep={sweepKm:0.####}km 近层={(nearLayer ? "是" : "否")}");

            bool ok = true;

            // ---- ⓪ 有限性。NaN 会让下面所有的 <= 门比较静默变成 false，
            //      那时报表上会是一片红而看不出成因，所以先单独点名。
            //      逐 float 分量数，不是逐 float4 条目数：写「分量」就必须真的是分量，
            //      否则读数会被按 4× 的量级误读（红测 R0 就撞上过这一点）。
            //      条目数同时报出来，是为了让"污染集中在少数几行"还是"散落各行"能一眼看出。 ----
            int nonFinite = 0, nonFiniteRows = 0;
            for (int i = 0; i < rep.Length; i++)
            {
                int bad = 0;
                Vector4 v = rep[i];
                if (!IsFinite(v.x)) bad++;
                if (!IsFinite(v.y)) bad++;
                if (!IsFinite(v.z)) bad++;
                if (!IsFinite(v.w)) bad++;
                nonFinite += bad;
                if (bad != 0) nonFiniteRows++;
            }
            bool okFinite = nonFinite == 0;
            ok &= okFinite;
            sb.AppendLine($"  {Mark(okFinite)}⓪有限性: 非有限分量 {nonFinite}/{rep.Length * 4}"
                        + $"（分布在 {nonFiniteRows}/{rep.Length} 个 float4 条目里）");

            // ---- ⓘ 布景分辨力 ----
            // ② 只有在"存在 w ≠ 1 的采样点"且"density > 0"时才测得到东西。
            // 两个条件都在这里报出来，绿不绿由下面的格子说，能不能测由这一格说。
            int wNotOne = 0, wZero = 0, wStrictInner = 0;
            double dMin = double.PositiveInfinity, dMax = 0.0;
            for (int s = 0; s < k_SampleCount; s++)
            {
                float w = Row(s, 0).y;
                if (w != 1f) wNotOne++;
                if (w == 0f) wZero++;
                if (w > 0f && w < 1f) wStrictInner++;
                double den = Row(s, 3).x;
                dMin = System.Math.Min(dMin, den);
                dMax = System.Math.Max(dMax, den);
            }
            bool scaleTestable = wNotOne > 0 && dMax > 0.0;
            sb.AppendLine($"  ⓘ 分辨力: w≠1 的采样点 {wNotOne}/{k_SampleCount}"
                        + $"（其中 w=0 的 {wZero}，0<w<1 的 {wStrictInner}）；"
                        + $"density ∈ [{Sci(dMin)}, {Sci(dMax)}]；"
                        + $"σt_full ∈ [{Sci(MinOf(rep, 1, 0))}, {Sci(MaxOf(rep, 1, 0))}]");

            // ---- ① 互补 ----
            // 分两个操作数各自判，不判"预先做好的差"：核里已经把 w+k 与 σ_ap+σ_near
            // 写进了 .w 分量，但那是**核自己算的和**。这里拿回 w、k、σ_ap、σ_near
            // 四个操作数在 CPU 侧重算，两者都判 —— 只判核算的那个和，等于让
            // "核把 k 也写成了 w"这种失效自己给自己作证。
            double maxWSum = 0.0, maxWSumGpu = 0.0;
            double maxExtRel = 0.0, maxScaRel = 0.0;
            int extCells = 0, scaCells = 0;
            for (int s = 0; s < k_SampleCount; s++)
            {
                Vector4 r0 = Row(s, 0), r1 = Row(s, 1), r2 = Row(s, 2);
                maxWSum    = System.Math.Max(maxWSum,    System.Math.Abs((double)r0.y + r0.z - 1.0));
                maxWSumGpu = System.Math.Max(maxWSumGpu, System.Math.Abs((double)r0.w - 1.0));

                if (r1.x > 0f) { maxExtRel = System.Math.Max(maxExtRel, Rel((double)r1.y + r1.z, r1.x)); extCells++; }
                if (r2.x > 0f) { maxScaRel = System.Math.Max(maxScaRel, Rel((double)r2.y + r2.z, r2.x)); scaCells++; }
            }
            bool okComp = maxWSum <= gate && maxWSumGpu <= gate
                       && maxExtRel <= gate && maxScaRel <= gate;
            ok &= okComp;
            sb.AppendLine($"  {Mark(okComp)}①互补: max|w+k−1| = {Sci(maxWSum)}（核自算的和 {Sci(maxWSumGpu)}）；"
                        + $"σt 相加复原 max rel = {Sci(maxExtRel)}（{extCells}/{k_SampleCount} 格有分辨力）；"
                        + $"σs 相加复原 max rel = {Sci(maxScaRel)}（{scaCells}/{k_SampleCount} 格）；门 {Sci(gate)}");
            if (extCells == 0 || scaCells == 0)
                sb.AppendLine("    ⓘ σ 恒为 0（本档雾未启用），相加复原一项**无分辨力** —— 上面那两个 0 不是证据。");

            // ---- ② 不该被缩放的字段 ----
            // density 喂的是太阳自遮蔽的闭式解（那一柱雾无论记在谁账上都是完整的），
            // phaseG 是介质属性 —— 两者都必须逐位穿过 VistaScaleFogSample。
            int denSame = 0, gSame = 0;
            for (int s = 0; s < k_SampleCount; s++)
            {
                Vector4 r3 = Row(s, 3), r4 = Row(s, 4);
                if (r3.y == r3.x && r3.z == r3.x) denSame++;
                if (r4.x == r3.w && r4.y == r3.w) gSame++;
            }
            bool okUnscaled = denSame == k_SampleCount && gSame == k_SampleCount;
            if (scaleTestable)
            {
                ok &= okUnscaled;
                sb.AppendLine($"  {Mark(okUnscaled)}②未缩放: density 三份逐位相同 {denSame}/{k_SampleCount}；"
                            + $"phaseG 三份逐位相同 {gSame}/{k_SampleCount}");
            }
            else
            {
                sb.AppendLine($"  ⓘ ②未缩放**无分辨力**（本档 w ≡ 1 或 density ≡ 0，"
                            + $"乘上权重也看不出来）：density {denSame}/{k_SampleCount}、"
                            + $"phaseG {gSame}/{k_SampleCount}，不计入通过与否。");
            }

            if (nearLayer)
            {
                // ---- ③ 端点与形状 ----
                // 指定点的期望值全部由 D 与 band 现算，不写死：
                //   w(0) = 0（saturate 掉一个大负数）      w(D−band)   = 0（边界）
                //   w(D−band/2) = 0.5（带中点）            w(D) = 1（交接完成）
                //   w(D+band) = 1、w(2D) = 1（saturate 上界）
                //
                // **这一格判的是形状，不是位置。** 指定点的 t 是从**被测自己**打包出的
                // (D, band) 派生的，所以「D 打包成了 1.5D」这类错误，指定点会跟着一起漂，
                // ③ 照样能全绿。红测 R5d 实测到这一点：把 D、band 分别乘 1.5 与 1.1，
                // ③ 只因 rcpBand 没跟着缩放而报 0.45（形状不自洽），偏差 5.000e-2；
                // 若把 z 也改成 1/(1.1·band)，形状自洽，③ 会全绿。
                // 「打包出来的 D/band 数值本身对不对」由 ⑤（对 5 档接手距离验 x、y/x、z·y）
                // 与 ⑥（GPU 回读 vs CPU 逐位对账）负责，不要指望这一格。
                float[] expect = { 0f, 0f, 0.5f, 1f, 1f, 1f };
                double maxAnchorErr = 0.0;
                var anchorTxt = new StringBuilder();
                for (int a = 0; a < VistaFroxelVolume.k_LayeringProbeAnchors; a++)
                {
                    double err = System.Math.Abs((double)Row(a, 0).y - expect[a]);
                    maxAnchorErr = System.Math.Max(maxAnchorErr, err);
                    anchorTxt.Append(a == 0 ? "" : " ").Append($"w[{a}]={Row(a, 0).y:0.000000}");
                }

                int mono = 0;
                for (int s = VistaFroxelVolume.k_LayeringProbeAnchors + 1; s < k_SampleCount; s++)
                    if (Row(s, 0).y < Row(s - 1, 0).y) mono++;

                bool okShape = maxAnchorErr <= gate && mono == 0;
                ok &= okShape;
                sb.AppendLine($"  {Mark(okShape)}③端点与形状（指定点由被测的 D/band 派生，"
                            + $"只判形状不判位置，位置见 ⑤⑥）: {anchorTxt}"
                            + $"（期望 0 0 0.5 1 1 1，max 偏差 {Sci(maxAnchorErr)}，门 {Sci(gate)}）；"
                            + $"扫掠段单调性违例 {mono}/{k_SampleCount - VistaFroxelVolume.k_LayeringProbeAnchors - 1}");

                // ---- ⑥ 与 CPU 打包对账 ----
                // 这一格是判据"不自带布景"的兑现处：探针读回来的 D/band 是
                // **shader 从 cbuffer 里拿到的那一份**，与 CPU 的 PackedLayering 逐位比。
                // 不等 = 下发路径把值改了（或者压根没下发，读到上一档的残留）。
                Vector4 r4last = Row(0, 4);
                bool okD = r4last.z == cpuLayering.x;
                bool okB = r4last.w == cpuLayering.y;
                double ratio = cpuLayering.x > 0f ? (double)cpuLayering.y / cpuLayering.x : double.NaN;
                double ratioErr = System.Math.Abs(ratio - VistaFogSettings.k_HandoffBandFraction);
                bool okRatio = ratioErr <= gate;
                // 指定点 3 的 t 就是 D 本身；它由 shader 用自己那份 D 算出来，
                // 与上面的 .z 是同一个值的两条读出路径 —— 都对上才说明整条链没错位。
                bool okAnchorT = Row(3, 0).x == cpuLayering.x;
                bool okLedger = okD && okB && okRatio && okAnchorT;
                ok &= okLedger;
                sb.AppendLine($"  {Mark(okLedger)}⑥对账: GPU D={Sci(r4last.z)} vs CPU {Sci(cpuLayering.x)}"
                            + $"（逐位{(okD ? "同" : "**不同**")}）；GPU band={Sci(r4last.w)} vs CPU {Sci(cpuLayering.y)}"
                            + $"（逐位{(okB ? "同" : "**不同**")}）；band/D = {ratio:0.000000}"
                            + $"（偏差 {Sci(ratioErr)}）；指定点3 的 t {(okAnchorT ? "= D" : "**≠ D**")}");
            }
            else
            {
                // ---- ④ 零态 ----
                // 分层 cbuffer 全零 ⇒ rcpBand = 0 ⇒ w = saturate(1 − 0) = 1、k = 0，
                // 全程如此。这一格就是"#25 关掉之后与改动前逐位一致"的凭据，
                // 所以它判**逐位**，不给容差：这里没有任何算术，只有一个常量。
                int wOne = 0, kZero = 0;
                for (int s = 0; s < k_SampleCount; s++)
                {
                    if (Row(s, 0).y == 1f) wOne++;
                    if (Row(s, 0).z == 0f) kZero++;
                }
                bool cpuZero = cpuLayering.x == 0f && cpuLayering.y == 0f
                            && cpuLayering.z == 0f && cpuLayering.w == 0f;
                // 扫掠点必须真的散开，否则"全程 w ≡ 1"只测了 t = 0 一处。
                int distinctT = 0;
                for (int s = VistaFroxelVolume.k_LayeringProbeAnchors + 1; s < k_SampleCount; s++)
                    if (Row(s, 0).x > Row(s - 1, 0).x) distinctT++;
                int sweepSpan = k_SampleCount - VistaFroxelVolume.k_LayeringProbeAnchors - 1;

                bool okZero = wOne == k_SampleCount && kZero == k_SampleCount
                           && cpuZero && distinctT == sweepSpan;
                ok &= okZero;
                sb.AppendLine($"  {Mark(okZero)}④零态: w≡1 {wOne}/{k_SampleCount}、k≡0 {kZero}/{k_SampleCount}（逐位）；"
                            + $"CPU PackedLayering 四分量{(cpuZero ? "全零" : "**非全零**")}；"
                            + $"扫掠点严格递增 {distinctT}/{sweepSpan}（t ∈ [0, {sweepKm:0.###}km]）");
            }

            return ok;
        }

        /// <summary>
        /// ⑤ CPU 侧的档位矩阵。
        ///
        /// <c>UsesNearLayer</c> 是**唯一**的谓词：<c>PackedLayering</c> 与
        /// <c>_VistaFroxelComposite</c> 的开关位都问它。两边分叉的症状是雾整个消失
        /// 而两边各自看着都自洽（AP 把近段让出去了、froxel 那一侧却认为自己没开），
        /// 所以这条恒等式必须被单独钉死，而不是靠上面某个档"顺带覆盖到了"。
        ///
        /// 零向量的判定走**逐分量 == 0**，不走 <c>Vector4 ==</c>：后者是带
        /// kEpsilon 的近似比较，D = 1e-6 m 那种极端档会被它判成"约等于零向量"，
        /// 而 PackedLayering 在那一档返回的 1/band 是 6.7e9。
        /// </summary>
        static bool RunCpuMatrix(double relGate, StringBuilder sb)
        {
            var modes = new[]
            {
                VistaFogSettings.Mode.Off,
                VistaFogSettings.Mode.AerialPerspective,
                VistaFogSettings.Mode.Froxel,
            };
            // 负值 / 0 / 次正规量级 / 生产值 / 远超 shadow distance —— 五档覆盖
            // "h > 0" 这个合取项的两侧与它的边界。
            var handoffs = new[] { -5f, 0f, 1e-6f, 48f, 4000f };

            var fog = new VistaFogSettings
            {
                densityInput = VistaFogSettings.DensityInput.MeanFreePath,
                meanFreePathMeters = 400f,
            };

            int cells = 0, mismatch = 0, nonZeroCells = 0, badPack = 0;
            var bad = new StringBuilder();
            foreach (var m in modes)
            {
                fog.mode = m;
                foreach (float h in handoffs)
                {
                    cells++;
                    bool uses = fog.UsesNearLayer(h);
                    Vector4 p = fog.PackedLayering(h);
                    bool isZero = p.x == 0f && p.y == 0f && p.z == 0f && p.w == 0f;

                    if (isZero == uses)      // 该零的不零 / 该有的没有
                    {
                        mismatch++;
                        bad.Append($" [{m}/h={h}: uses={uses} zero={isZero}]");
                    }

                    if (uses)
                    {
                        nonZeroCells++;

                        // 这里**不重算** PackedLayering 的算式再逐位比 —— 那是同一个量的
                        // 第二份实现，而 C# 规范允许 float 运算以**高于结果类型**的精度进行
                        // （Editor 跑的 Mono 就这么干）。实测：同样的两句
                        //   dKm = h * 0.001f;  band = dKm * 0.15f;
                        // 判据侧得 0x3BEBEDFC、被测侧得 0x3BEBEDFB，差 1 ulp；
                        // 而 0x3BEBEDFB 才是正确的就近舍入积（用 double 复算可验）。
                        // 逐位比会让这条判据在被测代码**完全正确**时变红。
                        // 更早的一版还把 `1f / bandKm` 直接写在比较式里，那一版有三格红；
                        // 只是把它提成一个 float 局部变量，红就掉到一格 —— 中间精度这件事
                        // 连"改不改变语义"都判断不了，所以这条路整条都不能走。
                        //
                        // 改成验**关系**：语义契约是 y/x = 0.15、z·y = 1、w = 0，
                        // 三条都不需要第二份实现，且照样抓得住"比例常量写错"
                        // "z 存成了 band 本身而不是它的倒数""x 存的是米不是千米"。
                        double expX = (double)h * k_MetersToKm;   // 0.001f 提升成 double，只剩一次 float 舍入
                        double errX = System.Math.Abs((double)p.x - expX);
                        double gateX = 2.0 * VistaSelfTestNumerics.Fp32Ulp(p.x);
                        double errRatio = System.Math.Abs((double)p.y / p.x - VistaFogSettings.k_HandoffBandFraction);
                        double errRcp = System.Math.Abs((double)p.z * p.y - 1.0);

                        bool cellOk = errX <= gateX && errRatio <= relGate
                                   && errRcp <= relGate && p.w == 0f;
                        if (!cellOk)
                        {
                            badPack++;
                            bad.Append($" [{m}/h={h}: x={Bits(p.x)} 期望≈{Sci(expX)} 偏差 {Sci(errX)}（门 {Sci(gateX)}）"
                                     + $"；band/D 偏差 {Sci(errRatio)}；z·y−1 = {Sci(errRcp)}"
                                     + $"；w={Bits(p.w)}]");
                        }
                    }
                }
            }

            // 分辨力：矩阵里必须**两种结果都出现过**，否则这条 ⟺ 只测到了一侧。
            bool testable = nonZeroCells > 0 && nonZeroCells < cells;
            bool ok = mismatch == 0 && badPack == 0 && testable;
            sb.AppendLine($"  {Mark(ok)}⑤档位矩阵: {cells} 格（{modes.Length} 模式 × {handoffs.Length} 接手距离）；"
                        + $"PackedLayering 为零 ⟺ !UsesNearLayer 违例 {mismatch}；"
                        + $"非零格的打包内容违例 {badPack}；"
                        + $"分辨力 = 非零 {nonZeroCells} / 零 {cells - nonZeroCells}"
                        + $"{(testable ? "" : "（**单侧，本判据无分辨力**）")}{bad}");
            return ok;
        }

        static double MinOf(Vector4[] rep, int row, int comp)
        {
            double v = double.PositiveInfinity;
            for (int s = 0; s < k_SampleCount; s++)
            {
                Vector4 r = rep[s * VistaFroxelVolume.k_LayeringProbeFloat4PerSample + row];
                v = System.Math.Min(v, comp == 0 ? r.x : r.y);
            }
            return v;
        }

        static double MaxOf(Vector4[] rep, int row, int comp)
        {
            double v = double.NegativeInfinity;
            for (int s = 0; s < k_SampleCount; s++)
            {
                Vector4 r = rep[s * VistaFroxelVolume.k_LayeringProbeFloat4PerSample + row];
                v = System.Math.Max(v, comp == 0 ? r.x : r.y);
            }
            return v;
        }

        static double Rel(double got, double expect)
            => System.Math.Abs(got - expect) / System.Math.Max(System.Math.Abs(expect), 1e-30);

        static bool IsFinite(float v) => !(float.IsNaN(v) || float.IsInfinity(v));

        static string Sci(double v) => v.ToString("0.000e+0");

        // float 的位模式。判据在"两个看起来一样的数不相等"这件事上必须能出示证据。
        static string Bits(float v)
            => "0x" + System.BitConverter.SingleToInt32Bits(v).ToString("X8");

        static string Mark(bool ok) => ok ? "✔ " : "✘ ";
    }
}
