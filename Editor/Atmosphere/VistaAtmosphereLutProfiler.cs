using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
// System.Diagnostics 里也有一个 Debug，直接 using 会与 UnityEngine.Debug 撞成 CS0104。
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Vista.Editor
{
    /// <summary>
    /// LUT 链路的耗时测量。产出的数字要写进 CHANGELOG 与作品集，所以**测量模型本身**
    /// 必须能当面讲清 —— 这个文件里注释的重点不是代码，而是"这个数字到底是什么"。
    ///
    /// ---- 为什么是这套模型（B），以及它不是什么 ----
    /// 可选的三套：
    ///   A) Play 模式 ProfilerRecorder + GpuRecorder 按 pass 名取值。
    ///      量的是**帧内真实开销**，含 RenderGraph 真正插的那些 barrier。
    ///      但要进 Play 模式，与另外三份自检（都是 Edit 模式菜单项）不同居，
    ///      且 Editor 自身的渲染会混进来。作为交叉验证单独实现。
    ///   B) 本文件：Edit 模式，把同一份工作录 N 遍进一条 CommandBuffer，提交一次、
    ///      同步一次，用墙钟除以 N；再重复 M 轮取最小值。
    ///   C) RenderDoc / Nsight。金标准，但纯手工、无法回归。
    ///
    /// 选 B 当主口径的理由只有一条：它能变成**回归测试**。A 与 C 都是一次性快照，
    /// 而这个菜单项每次改核都能重跑，数字一涨就知道是哪一 pass。
    ///
    /// B 有两条必须主动说出来的边界，别等人问：
    ///   1) 它量的是**吞吐**，不是帧内延迟。N 次背靠背重复让 GPU 有机会把相邻 dispatch
    ///      重叠起来（尤其逐 pass 单测：N 次同一个 dispatch 之间只有 UAV 写-写关系，
    ///      驱动通常不为此串行化）。所以**逐 pass 的数字是下界**。
    ///      整链测量要可信得多：链内 Transmittance→MS→SkyView 有真实的 SRV/UAV 依赖，
    ///      图形层必须插转换，重叠被这些依赖挡住。所以对外引用的数字取"整链实测"，
    ///      逐 pass 只用来看**相对占比**。
    ///   2) 立即模式的资源状态转换是**图形层**自动插的，不是 RenderGraph 插的那一批。
    ///      所以"整链 − 各部分之和"这个差不能直接叫"RenderGraph 的 barrier 开销"，
    ///      它是"串行化 + 调度"与"逐 pass 重复时多吃到的并行度"两项的净值 ——
    ///      符号本身就有信息：为正说明串行化占主导，为负说明单测的重叠红利更大。
    ///      真正的 RenderGraph barrier 成本只能由 A 给出。
    ///
    /// ---- 一个跑不掉的限制 ----
    /// RenderGraph 的逐 pass profiler marker 挂在
    /// <c>RenderGraph.GetDefaultProfilingSampler</c> 上，而那个方法整体被
    /// <c>#if DEVELOPMENT_BUILD || UNITY_EDITOR</c> 包着（core 包
    /// Runtime/RenderGraph/RenderGraph.cs:2868-2884），Release 构建里它返回 null。
    /// 也就是说**任何逐 pass 的数字都只能来自 Editor 或 Development 构建**，
    /// 没有第二条路。引用时必须标注环境，而不是等面试官问出来。
    /// </summary>
    public static class VistaAtmosphereLutProfiler
    {
        // ==================================================================
        //  参数
        // ==================================================================

        /// <summary>
        /// 摊销次数。选 200 的依据：稳态五 pass 单次亚毫秒，200 次几十到几百毫秒 GPU ——
        /// 足以把提交+同步的固定开销（实测亚毫秒）摊到小数点后三位以下，
        /// 又短得可以在一个菜单项里跑几十次测量而不让 Editor 卡住。
        /// </summary>
        const int k_Iterations = 200;

        /// <summary>
        /// 预热次数。首次 dispatch 要付 shader 变体的实际上载、描述符堆分配、
        /// 以及 GPU 时钟从低功耗档爬升 —— 不预热的话第一项测量会明显偏高，
        /// 而"偏高的那一项"取决于菜单项里的调用顺序，是最容易被误读成真实开销的假象。
        /// </summary>
        const int k_WarmupIterations = 20;

        /// <summary>
        /// 每项测量重复的轮数，取**最小值**上报。
        ///
        /// 取最小而不是平均：这类测量的噪声是**单向**的 —— Editor 自己的重绘、驱动的
        /// 命令批处理、其它进程抢 GPU，全都只会让某一轮变慢，没有任何机制能让一轮
        /// 比真实开销更快。所以最小值是对"无争用下的真实开销"的最好估计，
        /// 而平均值会把一次偶发的 Editor 重绘平摊进最终数字里。
        /// 第一版只取一轮，六次重跑给出 0.647~1.138 ms（±40%）—— 那个离散度本身
        /// 就是"必须取最小值"的实测理由。同时上报 min/max，离散度太大时数字不该引用。
        /// </summary>
        const int k_Trials = 5;

        /// <summary>
        /// 稳态五 pass 的预算。0.3 ms 不是拍的：目标是 60 fps（16.67 ms）下
        /// 整条大气链路占不到 2%。这一档留给"每帧都跑"的五个 pass，
        /// 静态两表只在参数变化帧出现，不计入。
        /// </summary>
        const float k_SteadyBudgetMs = 0.300f;

        /// <summary>
        /// fence 探测预算。见 <see cref="ProbeFence"/> —— 这个常量存在的理由是
        /// 一次实测事故，不是防御性编程。
        /// </summary>
        const double k_FenceProbeSec = 0.25;

        /// <summary>太阳仰角。与另外三份自检的"正午档"一致，三份报告的数字才能横向对照。</summary>
        const float k_SunElevationDeg = 60f;

        /// <summary>
        /// harness 自校验的一致性阈值。两条同步路径量同一份工作，15% 以内算一致
        /// （它们的固定开销不同，扣除后仍会留一点残差）。
        /// </summary>
        const float k_HarnessTolerance = 0.15f;

        // ==================================================================
        //  同步原语
        // ==================================================================

        enum SyncMode
        {
            /// <summary>CommandBuffer 末尾插 fence，CPU 自旋等 passed。测量窗口里不含回读传输。</summary>
            Fence,
            /// <summary>拿一个一元素 buffer 做 GetData —— 它会 flush 整条命令流并等 GPU 跑完。</summary>
            Readback,
        }

        /// <summary>
        /// 硬同步用的哑元 buffer。<c>GetData</c> 会阻塞到**先前排入的全部**GPU 工作完成
        /// （这正是另外三份自检能在 <c>ExecuteCommandBuffer</c> 之后直接读到正确数据的原因），
        /// 所以它不需要与被测工作有任何数据关系。
        /// </summary>
        static GraphicsBuffer s_Sync;
        static readonly float[] s_SyncData = new float[1];

        [MenuItem("Window/Vista/Profile Atmosphere LUTs", priority = 125)]
        public static void Run()
        {
            var sb = new StringBuilder();
            bool ok = Profile(sb);

            Debug.Log(("[Vista] LUT 耗时" + (ok ? "达标" : "**未达标/不可判定**") + "\n" + sb)
                      .Replace("\r", "").Replace("\n", "  |  "));
        }

        /// <summary>
        /// 试一下 fence 到底能不能用，**不信 <c>SystemInfo.supportsGraphicsFence</c>**。
        ///
        /// 这个函数是一次实测事故换来的。第一版直接按那个能力位选 fence 路径，结果：
        /// 后端是 D3D11，<c>supportsGraphicsFence</c> 返回 **true**，而 CPU 轮询
        /// <c>fence.passed</c> 永远等不到 —— 因为提交要靠 Editor 推动渲染线程，
        /// 而我的自旋把主线程占满了，于是"等提交"和"推动提交"互相锁死。
        /// 二十次测量各自撞满 5 s 超时，Editor 卡了四分多钟，所有数字变成 0 或负数。
        ///
        /// 结论不是"fence 不能用"，而是**能力查询 ≠ 该原语在当前线程模型下可用**。
        /// 所以这里先用一条空 buffer 花 250 ms 探一次：过了就用 fence，
        /// 没过就整场退到 readback 并在报告里说明。代价是最坏 250 ms，
        /// 而不是二十次 5 s。
        /// </summary>
        static bool ProbeFence()
        {
            if (!SystemInfo.supportsGraphicsFence)
                return false;

            var cmd = new CommandBuffer { name = "Vista Fence Probe" };
            var fence = cmd.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation,
                                                SynchronisationStageFlags.ComputeProcessing);
            var sw = Stopwatch.StartNew();
            Graphics.ExecuteCommandBuffer(cmd);
            GL.Flush();

            bool passed = false;
            while (sw.Elapsed.TotalSeconds < k_FenceProbeSec)
            {
                if (fence.passed) { passed = true; break; }
            }
            cmd.Release();
            // 无论探测结果如何都硬同步一次，别把探测的残留留给第一项正式测量。
            s_Sync.GetData(s_SyncData);
            return passed;
        }

        static bool Profile(StringBuilder sb)
        {
            var res = VistaRuntimeResources.Get();
            if (res == null || res.atmosphereLutCS == null)
            {
                sb.AppendLine("　 ✘ 取不到 atmosphereLutCS：当前管线不是 URP，或资源未导入。");
                return false;
            }
            if (res.skyReflectionCS == null)
            {
                sb.AppendLine("　 ✘ VistaRuntimeResources 里没有配 skyReflectionCS。");
                return false;
            }

            var p = VistaAtmosphereParameters.CreateEarth();
            var apSettings = new VistaAerialPerspectiveSettings();
            var luts = new VistaAtmosphereLuts(res.atmosphereLutCS, res.skyReflectionCS);
            s_Sync = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float))
            {
                name = "VistaProfilerSync",
            };

            try
            {
                if (!luts.isValid)
                {
                    sb.AppendLine("　 ✘ 大气核缺失。");
                    return false;
                }

                // 分配 + 推大气 cbuffer。之后逐 pass 用的是**泛型**重载，它们无条件 dispatch ——
                // 立即模式那两个便利包装不能用来逐 pass 计时：EnsureStaticLuts 把
                // Transmittance 与 MS 绑在一起且只在脏时跑，RenderSkyReflection(cmd,…) 则
                // 把积分与那 6 次 CopyTexture 绑在一起。要分开量就得自己建 dispatcher。
                luts.PrepareLuts(p);
                bool apOk = luts.PrepareAerialPerspective(apSettings);
                bool shOk = luts.PrepareSkyAmbientSh();
                var mode = luts.PrepareSkyReflection(VistaSkyReflectionMode.SkyViewLut);
                bool reflOk = mode != VistaSkyReflectionMode.Off;
                bool allOk = apOk && shOk && reflOk;

                if (!allOk)
                {
                    // 不直接 return：能测几项就报几项，但要说清缺了什么，
                    // 否则"稳态五 pass 合计"这个数会在悄悄少两项的情况下漂亮得离谱。
                    sb.Append("　 ⚠ 部分模块不可用 —— AP ").Append(apOk ? "OK" : "缺")
                      .Append("　SH ").Append(shOk ? "OK" : "缺")
                      .Append("　反射 ").Append(reflOk ? "OK" : "缺")
                      .AppendLine("。缺的项不计入合计，合计也就不能与阈值比。");
                }

                float rad = k_SunElevationDeg * Mathf.Deg2Rad;
                var sunDir = new Vector3(0f, Mathf.Sin(rad), Mathf.Cos(rad));
                var view = VistaAtmosphereViewData.Create(p, new Vector3(0f, 2f, 0f), 0f, sunDir);
                // AP 需要视锥四角。这里没有真相机，用 Create 里那个 60°/16:9 的兜底 ——
                // AP 核的开销与视锥朝向无关（每个线程走的步数由深度分布决定，不由方向决定），
                // 所以兜底值对耗时没有影响。
                var ap = apSettings;

                bool fenceUsable = ProbeFence();
                var syncMode = fenceUsable ? SyncMode.Fence : SyncMode.Readback;

                sb.Append("── LUT 链路耗时（模型 B：Edit 模式立即提交，N=")
                  .Append(k_Iterations).Append(" 摊销 × ").Append(k_Trials).Append(" 轮取最小）")
                  .AppendLine();
                sb.Append("　 GPU ").Append(SystemInfo.graphicsDeviceName)
                  .Append("　后端 ").Append(SystemInfo.graphicsDeviceType).AppendLine();
                sb.Append("　 同步原语 ").Append(syncMode)
                  .Append("　supportsGraphicsFence = ").Append(SystemInfo.supportsGraphicsFence)
                  .Append("　fence 探测 ").Append(fenceUsable ? "通过" : "**未通过 → 退到 readback**")
                  .AppendLine();
                if (!fenceUsable && SystemInfo.supportsGraphicsFence)
                    sb.AppendLine("　　 能力位说支持但探测不过：CPU 轮询 fence 要靠 Editor 推动提交，"
                                + "自旋把主线程占满就互相锁死。能力查询 ≠ 该原语在当前线程模型下可用。");
                sb.Append("　 规格　SkyView ").Append(VistaAtmosphereLuts.k_SkyViewWidthDefault)
                  .Append('×').Append(VistaAtmosphereLuts.k_SkyViewHeightDefault)
                  .Append("　AP ").Append(ap.width).Append('×').Append(ap.height)
                  .Append('×').Append(ap.depth).Append('/').Append(ap.distribution)
                  .Append("　反射 ").Append(VistaAtmosphereLuts.k_SkyReflectionSize).Append("²×")
                  .Append(VistaAtmosphereLuts.k_SkyReflectionMipCount).Append(" mip")
                  .AppendLine();

                // ---- 固定开销基线 ----
                // 空 CommandBuffer 走一遍完全相同的提交+同步协议。总耗时 = 固定开销 F + N·W，
                // 所以 W = (总 − F)/N。不减这一项的话，最便宜的那几个 pass（Transmittance
                // 只有 256×64 个纹素）测出来会几乎全是 F。
                var baseline = Sample.Of(syncMode, _ => { });
                sb.Append("　 固定开销（空 buffer 提交+同步）").Append(baseline.min.ToString("F3"))
                  .Append(" ms（min，max ").Append(baseline.max.ToString("F3"))
                  .AppendLine("），已从每项中扣除");

                // ---- 逐 pass ----
                // 这些数字是**下界**（见类注释边界 1），只用来看相对占比。
                var tTrans = Per(syncMode, baseline, cmd => luts.RenderTransmittanceLut(D(cmd, luts)));
                var tMs    = Per(syncMode, baseline, cmd => luts.RenderMultiScatteringLut(D(cmd, luts)));
                var tSky   = Per(syncMode, baseline, cmd => luts.RenderSkyViewLut(D(cmd, luts), view));
                var tSh    = shOk
                    ? Per(syncMode, baseline, cmd => luts.RenderSkyAmbientSh(D(cmd, luts), view))
                    : Sample.Invalid;
                var tRefl  = reflOk
                    ? Per(syncMode, baseline, cmd => luts.RenderSkyReflection(D(cmd, luts), view, mode))
                    : Sample.Invalid;
                var tCopy  = reflOk
                    ? Per(syncMode, baseline, cmd => luts.CopySkyReflectionToCube(cmd))
                    : Sample.Invalid;
                var tAp    = apOk
                    ? Per(syncMode, baseline, cmd => luts.RenderAerialPerspectiveLut(D(cmd, luts), view, ap))
                    : Sample.Invalid;

                sb.AppendLine("　 逐 pass（各自 N 次背靠背；是下界，只看占比）");
                Row(sb, "Transmittance",   tTrans, "仅参数变化帧");
                Row(sb, "MultiScattering", tMs,    "仅参数变化帧");
                Row(sb, "SkyView",         tSky,   null);
                Row(sb, "AmbientSH",       tSh,    null);
                Row(sb, "Reflection",      tRefl,  "7 趟 dispatch");
                Row(sb, "ReflCopy",        tCopy,  "6 次 CopyTexture");
                Row(sb, "AP",              tAp,    null);

                // ---- 反射 pass 的 CPU/GPU 拆分 ----
                // 为什么单独给这一项做拆分：它占稳态链路 79%，而它的 GPU 工作量是
                // 已知的 —— 401k 次 LUT 双线性取样（mip0 24.6k + mip1~6 376k，
                // 见 SkyReflection.compute 头注）。3060 的取样率是每秒几百亿次量级，
                // 这点工作只该值几十微秒，与实测差一个数量级。
                //
                // 差在哪只有两种可能：要么核比估算慢得多，要么开销根本不在 GPU 上。
                // 这个 pass 每次要发 7×(SetGlobalVector + SetTextureMip + Dispatch) = 21 条命令，
                // 而其余每个 pass 只有 3 条上下 —— 其中 SetTextureMip 指定 mip 级别，
                // 在 D3D11 上意味着要拿到该 mip 的 UAV view。
                //
                // 所以量一遍"一个 mip 都不派"（mipMask: 0，绑定照发）：它与完整版的差
                // 就是 GPU 积分的部分。注意这个模型对这一项是**公平**的：一次 rep 就是
                // 真实一帧的命令数（都是 7 次绑定），N 次重复只用来摊掉提交+同步，
                // 不会放大逐帧命令成本。
                var tReflBinds = reflOk
                    ? Per(syncMode, baseline,
                          cmd => luts.RenderSkyReflection(D(cmd, luts), view, mode, mipMask: 0))
                    : Sample.Invalid;
                if (reflOk && tReflBinds.valid && tRefl.valid)
                {
                    double gpuPart = tRefl.min - tReflBinds.min;
                    double cpuShare = tRefl.min > 1e-6 ? tReflBinds.min / tRefl.min : 0.0;
                    sb.Append("　 反射拆分：只绑不派 ").Append(tReflBinds.Fmt())
                      .Append("　→ CPU 侧命令占 ").Append(cpuShare.ToString("P0"))
                      .Append("，GPU 积分 ").Append(gpuPart.ToString("F3")).Append(" ms")
                      .AppendLine();
                    sb.Append("　　 每次 21 条命令（7 mip × 绑参+绑 UAV+派发），"
                            + "单条约 ").Append((tReflBinds.min / 14.0 * 1000.0).ToString("F1"))
                      .AppendLine(" µs（按 14 条绑定算）");
                }

                if (reflOk && tRefl.valid)
                    ReportMipAttribution(sb, syncMode, baseline, luts, view, mode, tRefl);

                // ---- 稳态五 pass 整链 ----
                // 直接测五个串起来，而不是把五个单测加起来：加法会把单测里吃到的
                // dispatch 重叠红利也加进去，得出一个比实际更低的"合计"。
                // 工厂而不是直接写死 ap：下面 AP 定档那一节要把同一条链在不同切片数下
                // 各测一遍，链的其余四 pass 必须逐字相同，否则差值就不能归因给 AP。
                System.Func<VistaAerialPerspectiveSettings, System.Action<CommandBuffer>> chainWith =
                    a => cmd =>
                {
                    luts.RenderSkyViewLut(D(cmd, luts), view);
                    if (shOk)   luts.RenderSkyAmbientSh(D(cmd, luts), view);
                    if (reflOk) { luts.RenderSkyReflection(D(cmd, luts), view, mode);
                                  luts.CopySkyReflectionToCube(cmd); }
                    if (apOk)   luts.RenderAerialPerspectiveLut(D(cmd, luts), view, a);
                };
                var steadyChain = chainWith(ap);
                var tSteady = Per(syncMode, baseline, steadyChain);

                double sumSteady = tSky.min + tSh.Or0() + tRefl.Or0() + tCopy.Or0() + tAp.Or0();
                // 离散度太大就不判定 —— 一个 ±40% 的数字既不能说达标也不能说超标。
                bool quotable = tSteady.spread < 0.25;
                bool budgetOk = tSteady.min < k_SteadyBudgetMs && allOk && quotable;

                sb.Append("　 稳态五 pass 整链实测 ").Append(tSteady.Fmt())
                  .Append(" ／阈 ").Append(k_SteadyBudgetMs.ToString("F3")).Append(" ms ")
                  .Append(!allOk ? "（项不全，不判定）"
                        : !quotable ? "（离散度 " + tSteady.spread.ToString("P0") + " 过大，不判定）"
                        : budgetOk ? "OK" : "**超预算**")
                  .AppendLine();
                sb.Append("　　 五项单测之和 ").Append(sumSteady.ToString("F3"))
                  .Append(" ms　差 ").Append((tSteady.min - sumSteady).ToString("+0.000;-0.000"))
                  .AppendLine(" ms = 串行化+调度 −（单测多吃到的重叠）");

                // ---- 全七 pass 整链（参数变化帧）----
                var tFull = Per(syncMode, baseline, cmd =>
                {
                    luts.RenderTransmittanceLut(D(cmd, luts));
                    luts.RenderMultiScatteringLut(D(cmd, luts));
                    steadyChain(cmd);
                });

                double sumFull = sumSteady + tTrans.min + tMs.min;
                sb.Append("　 全七 pass 整链实测 ").Append(tFull.Fmt())
                  .Append("（仅参数变化帧付这个）　七项单测之和 ").Append(sumFull.ToString("F3"))
                  .Append(" ms　差 ").Append((tFull.min - sumFull).ToString("+0.000;-0.000")).Append(" ms")
                  .AppendLine();
                sb.Append("　　 静态两表的净代价 ").Append((tFull.min - tSteady.min).ToString("F3"))
                  .AppendLine(" ms —— 这就是「静态表不逐帧重烘」省下的量");

                // ---- harness 自校验：两种同步原语应当给出同一个数 ----
                // 这一项不测被测对象，测的是**测量工具**。fence 与 GetData 是两条完全不同的
                // 同步路径，它们对同一份工作给出的数字若差得多，说明其中一条没有真正等到
                // GPU 跑完 —— 那种错会让所有数字系统性偏低，而单看数字本身完全看不出来。
                if (fenceUsable)
                {
                    var altBase = Sample.Of(SyncMode.Readback, _ => { });
                    var alt = Per(SyncMode.Readback, altBase, steadyChain);

                    // 判定前先要求两边都在噪声以上。第一版写的是
                    // `tSteady > 1e-6 ? |Δ|/tSteady : 0`，于是 fence 侧测出 0.000 时
                    // 分支落到 0，报告印出"偏差 0.0% OK" —— 一条**伪通过**，
                    // 恰好在测量彻底失效时最像正常。判据必须先否掉退化输入。
                    bool usable = tSteady.min > 1e-3 && alt.min > 1e-3;
                    if (!usable)
                    {
                        sb.Append("　 harness 自校验：**不可判定** —— fence ")
                          .Append(tSteady.min.ToString("F3")).Append(" ms / readback ")
                          .Append(alt.min.ToString("F3"))
                          .AppendLine(" ms，至少一侧落在噪声以下，说明该侧没真正等到 GPU。");
                        budgetOk = false;
                    }
                    else
                    {
                        float dev = Mathf.Abs((float)((alt.min - tSteady.min) / tSteady.min));
                        bool agree = dev < k_HarnessTolerance;
                        sb.Append("　 harness 自校验：整链 fence ").Append(tSteady.min.ToString("F3"))
                          .Append(" ms vs readback ").Append(alt.min.ToString("F3"))
                          .Append(" ms　偏差 ").Append(dev.ToString("P1")).Append(' ')
                          .Append(agree ? "OK" : "**两条同步路径不一致，数字可疑**")
                          .AppendLine();
                        if (!agree) budgetOk = false;
                    }
                }
                else
                {
                    sb.AppendLine("　 harness 自校验：跳过（fence 不可用，只有一条同步路径）。");
                }

                if (apOk)
                    ReportApDepthCost(sb, syncMode, baseline, luts, view, chainWith, tSteady, tAp);

                sb.AppendLine("── 模型说明（引用数字时必须一起给）");
                sb.AppendLine("　 1) 这是**吞吐**不是帧内延迟：N 次背靠背允许相邻 dispatch 重叠。"
                            + "对外只引用「整链实测」，逐 pass 仅用于占比。");
                sb.AppendLine("　 2) 立即模式的状态转换由图形层插，**不是** RenderGraph 那批 barrier。"
                            + "RenderGraph 的真实 barrier 成本要由 Play 模式的 ProfilerRecorder 给。");
                sb.AppendLine("　 3) 环境是 Editor。RenderGraph 的逐 pass marker 被 "
                            + "#if DEVELOPMENT_BUILD || UNITY_EDITOR 包着"
                            + "（core RenderGraph.cs:2868-2884），Release 构建里拿不到逐 pass 数字。");
                sb.AppendLine("　 4) 每项取 M 轮最小值：噪声单向（争用只会加时间），"
                            + "平均会把偶发的 Editor 重绘平摊进结果。min/max 同时上报，"
                            + "离散度大于 25% 的数字不判定、不引用。");

                return budgetOk;
            }
            finally
            {
                s_Sync?.Dispose();
                s_Sync = null;
                luts.Dispose();
            }
        }

        // ==================================================================
        //  测量
        // ==================================================================

        /// <summary>
        /// 一项测量的 M 轮结果。上报 min（见 <see cref="k_Trials"/> 的理由）与 max，
        /// 后者只用来判"这个数字值不值得引用"。
        /// </summary>
        readonly struct Sample
        {
            public readonly double min, max;
            Sample(double min, double max) { this.min = min; this.max = max; }

            public static Sample Invalid => new Sample(double.NaN, double.NaN);
            public bool valid => !double.IsNaN(min);
            /// <summary>相对离散度。min 落到噪声以下时给 1（视为完全不可信），不给 0。</summary>
            public double spread => min > 1e-3 ? (max - min) / min : 1.0;
            public double Or0() => valid ? min : 0.0;

            public string Fmt() => valid
                ? min.ToString("F3") + " ms（max " + max.ToString("F3") + "，±"
                  + spread.ToString("P0") + "）"
                : "  ——  ";

            /// <summary>裸测量：不扣基线，用于测基线本身。</summary>
            public static Sample Of(SyncMode mode, System.Action<CommandBuffer> record)
                => Collect(mode, record, 0.0);

            /// <summary>扣掉基线并除以 N 的每次开销。</summary>
            public static Sample Amortized(SyncMode mode, System.Action<CommandBuffer> record,
                                           double baselineMs)
                => Collect(mode, record, baselineMs);

            static Sample Collect(SyncMode mode, System.Action<CommandBuffer> record,
                                  double baselineMs)
            {
                bool amortize = baselineMs > 0.0;
                RawMs(record, k_WarmupIterations, mode);   // 预热一次就够，M 轮之间不必重复
                double lo = double.MaxValue, hi = double.MinValue;
                for (int t = 0; t < k_Trials; ++t)
                {
                    double raw = RawMs(record, k_Iterations, mode);
                    // 可能为负（固定开销本身有抖动，而最便宜的 pass 比抖动还小）。
                    // 不 clamp 到 0：负值是"这一项已经落在测量噪声以下"的信号，
                    // 抹平成 0 反而看不出来。
                    double v = amortize ? (raw - baselineMs) / k_Iterations : raw;
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                return new Sample(lo, hi);
            }
        }

        static Sample Per(SyncMode mode, Sample baseline, System.Action<CommandBuffer> record)
            => Sample.Amortized(mode, record, baseline.min);

        /// <summary>
        /// 每次都新建 dispatcher 而不是复用一个：它是 readonly struct，构造只是存两个引用，
        /// 但它捕获的是**那一条** CommandBuffer —— 复用会把工作录到已经提交掉的 buffer 里。
        /// </summary>
        static VistaImmediateLutDispatcher D(CommandBuffer cmd, VistaAtmosphereLuts luts)
            => new VistaImmediateLutDispatcher(cmd, luts);

        static void Row(StringBuilder sb, string name, Sample s, string note)
        {
            sb.Append("　　 ").Append(name.PadRight(16)).Append(s.Fmt());
            // 把"这一行能不能引用"直接印在行上，而不是只写在末尾的模型说明里。
            // 实测同一台机器上 Transmittance 在两次调用间给出 0.011 与 0.045 ms
            // （各自已是 5 轮最小值）—— 亚 0.05 ms 的项落在这套 harness 的分辨率以下，
            // 靠读者自己去对照脚注是不现实的，会被当成实测值抄走。
            if (s.valid && s.spread > 0.25)
                sb.Append("　⚠ 离散度过大，不引用");
            if (!string.IsNullOrEmpty(note)) sb.Append("　← ").Append(note);
            sb.AppendLine();
        }

        /// <summary>
        /// 反射 pass 的逐 mip 归因。回答的问题是「0.391 ms 花在哪几级上」，
        /// 而这个问题不能靠推 dispatch 形状回答：粗 mip 上有两个方向相反的效应叠着 ——
        /// 线程组数掉到 1（3060 有 28 个 SM，只占得住 6 个）、且 4²/2²/1² 三级分别
        /// 浪费 75%/94%/98% 的 lane，但同时 K 涨到 256 次**串行相关**的 LUT 取样，
        /// 每次取样的地址依赖上一次的结果，延迟藏不住。占比只能量。
        ///
        /// 两种口径都测，因为两者的差本身携带信息：
        ///   isolated（<c>1&lt;&lt;m</c>）  只派该级，干净，但 N 次重复都打同一级；
        ///   prefix  （<c>(1&lt;&lt;m+1)−1</c>）派 0..m 级，每次重复是真实的混合序列。
        /// prefix 的差分 <c>pre[m] − pre[m−1]</c> 与 isolated[m] 若一致，说明相邻级之间
        /// 没有可观的重叠（各自都能把 GPU 填满或各自都填不满）；若 isolated 明显更大，
        /// 说明混排时相邻 dispatch 被重叠掉了 —— 而这正好决定「重排 dispatch」值不值得：
        /// 若粗 mip 已经在与前一级重叠，把它们合并成一趟就赚不到多少。
        /// 另外 prefix 的满掩码那一档必须回到 tRefl，这是一条自洽校验。
        /// </summary>
        static void ReportMipAttribution(StringBuilder sb, SyncMode syncMode, Sample baseline,
                                         VistaAtmosphereLuts luts, in VistaAtmosphereViewData view,
                                         VistaSkyReflectionMode mode, Sample tRefl)
        {
            const int mips = VistaAtmosphereLuts.k_SkyReflectionMipCount;
            var iso = new Sample[mips];
            var pre = new Sample[mips];

            // in 参数不能被 lambda 捕获，先拷一份到局部。
            var v = view;
            for (int m = 0; m < mips; ++m)
            {
                int isoMask = 1 << m;
                int preMask = (1 << (m + 1)) - 1;
                iso[m] = Per(syncMode, baseline,
                             cmd => luts.RenderSkyReflection(D(cmd, luts), v, mode, isoMask));
                pre[m] = Per(syncMode, baseline,
                             cmd => luts.RenderSkyReflection(D(cmd, luts), v, mode, preMask));
            }

            sb.AppendLine("　 逐 mip 归因　iso=只派该级　Δpre=前缀差分　占比按 iso");
            double sumIso = 0.0;
            for (int m = 0; m < mips; ++m) sumIso += iso[m].Or0();

            long totalSamples = 0;
            double coarseMs = 0.0;   // mip3..6：线程组数已经掉到 1 的那几级
            long coarseTexels = 0;   // 同上几级产出的纹素数
            long allTexels = 0;
            for (int m = 0; m < mips; ++m)
            {
                int size = VistaAtmosphereLuts.k_SkyReflectionSize >> m;
                int gridXY = (size + 7) / 8;
                int groups = gridXY * gridXY * 6;
                long texels = 6L * size * size;
                int k = NominalSampleCount(m, mode);
                long samples = texels * k;
                totalSamples += samples;
                allTexels += texels;
                // 不写死 126/8190：mip 数或 64² 一改，字面量就静默变错，
                // 而"占 1.5% 纹素却吃 86% 时间"正是整个归因的论点所在。
                if (m >= 3) { coarseMs += iso[m].Or0(); coarseTexels += texels; }

                double dPre = m == 0 ? pre[0].Or0() : pre[m].Or0() - pre[m - 1].Or0();
                double share = sumIso > 1e-6 ? iso[m].Or0() / sumIso : 0.0;
                // 达成的取样吞吐。这一列是判据的核心：若某级的 G样本/s 比 mip0 低一两个
                // 数量级，那一级就是被占用率/延迟卡住的，不是被取样总量卡住的。
                // 注意别据此推出"于是该重排而不该限 K"：延迟受限时耗时 ≈ K × 单次暴露延迟，
                // 限 K 同样能压下去（少要隐藏的延迟），重排是另一条（多点并行来隐藏）。
                // 这一列只判**病因**，不判**药方**。
                double gps = iso[m].min > 1e-4 ? samples / iso[m].min / 1e6 : 0.0;

                sb.Append("　　 mip").Append(m)
                  .Append("  ").Append((size + "²").PadRight(5))
                  .Append("组 ").Append(groups.ToString().PadLeft(3))
                  .Append("  纹素 ").Append(texels.ToString().PadLeft(5))
                  .Append("  K=").Append(k.ToString().PadLeft(3))
                  .Append("  取样 ").Append((samples / 1000.0).ToString("F1").PadLeft(6)).Append('k')
                  .Append("　iso ").Append(iso[m].Or0().ToString("F3"))
                  .Append("  Δpre ").Append(dPre.ToString("+0.000;-0.000"))
                  .Append("　占 ").Append(share.ToString("P0").PadLeft(4))
                  .Append("　").Append(gps.ToString("F2")).Append(" G样本/s");
                if (iso[m].valid && iso[m].spread > 0.25)
                    sb.Append("　⚠±").Append(iso[m].spread.ToString("P0"));
                sb.AppendLine();
            }

            // ---- 自洽校验：满掩码的 prefix 必须回到独立测的 tRefl ----
            // 这一条不测被测对象，测的是掩码这条路本身有没有改变被测的工作量。
            // 它一红就说明掩码实现有问题（比如把绑定也跳掉了），此时上面整张表都不能用。
            double full = pre[mips - 1].Or0();
            bool closes = tRefl.min > 1e-3 && full > 1e-3
                          && Mathf.Abs((float)((full - tRefl.min) / tRefl.min)) < 0.15f;
            sb.Append("　　 自洽：满掩码 prefix ").Append(full.ToString("F3"))
              .Append(" ms vs 独立测 ").Append(tRefl.min.ToString("F3")).Append(" ms　")
              .Append(closes ? "一致 OK" : "**不一致，上表不可用**").AppendLine();
            sb.Append("　　 单级之和 ").Append(sumIso.ToString("F3"))
              .Append(" ms　差 ").Append((full - sumIso).ToString("+0.000;-0.000"))
              .AppendLine(" ms（正=混排时并未重叠；负=单测各自吃到了重叠）");

            // ---- 结论：让数据自己回答，不复述预设的二分 ----
            // 第一版这里印的是"mip3~6 与 mip1 谁大决定修法：前者→重排、后者→限 K"。
            // 那是个**假二分**：实测粗 mip 的耗时正比于 K（见下面 K 比例那一行），
            // 所以限 K 同样作用在粗 mip 上 —— 两条修法治的是同一批 mip，
            // 差别在机理（一个减少要隐藏的延迟量，一个增加隐藏延迟的并行度），
            // 不在作用对象。把预设的分类印成结论，就是让工具替我确认偏见。
            double coarseShare = sumIso > 1e-6 ? coarseMs / sumIso : 0.0;
            sb.Append("　　 mip3~6（K 已饱和到 256、纹素最少的那几级）合计 ").Append(coarseMs.ToString("F3"))
              .Append(" ms，占 ").Append(coarseShare.ToString("P0"))
              .Append("，而它们只产出 ").Append(coarseTexels).Append('/').Append(allTexels)
              .Append(" 个纹素（").Append((coarseTexels / (double)allTexels).ToString("P1")).Append("）")
              .AppendLine();

            // 证据一：组数相同（都是 6）时耗时随 K 走。mip3 与 mip4 的组数、
            // dispatch 形状完全一致，唯一差别是 K 翻倍。判定写成双向的 ——
            // ≈2 指向"被取样循环的延迟卡住"，≈1 指向"K 已经不是自变量了"。
            // 只印其中一边就是把上一次的病因当成永久结论。
            double kRatio = 0.0;
            if (iso[3].Or0() > 1e-4 && mips > 4)
            {
                kRatio = iso[4].Or0() / iso[3].Or0();
                sb.Append("　　 证据 1｜组数同为 6，K 从 128→256：耗时 ")
                  .Append(iso[3].Or0().ToString("F3")).Append("→").Append(iso[4].Or0().ToString("F3"))
                  .Append(" ms，比 ").Append(kRatio.ToString("F2")).Append("　→ ")
                  .AppendLine(kRatio > 1.6 ? "正比于 K：取样循环的延迟没被隐藏"
                            : kRatio < 1.25 ? "与 K 基本无关：取样循环已不是自变量"
                            : "介于两者之间，这一条不做判定");
            }

            // 证据二：反过来，工作量降 16 倍而耗时几乎不动 —— 说明纹素数不是自变量。
            // 每迭代暴露的时间与"一次纹理取样往返"（数百 ns 量级）比：
            // 同量级 = 延迟全额暴露；低一个数量级 = 已经被别的 warp 藏住了。
            double nsPerIter = 0.0;
            if (iso[6].Or0() > 1e-4 && mips > 6)
            {
                nsPerIter = iso[6].Or0() / NominalSampleCount(6, mode) * 1e6;
                sb.Append("　　 证据 2｜K 同为 256，纹素 96→6（16×）：耗时 ")
                  .Append(iso[4].Or0().ToString("F3")).Append("→").Append(iso[6].Or0().ToString("F3"))
                  .Append(" ms，仅快 ").Append((iso[4].Or0() / iso[6].Or0()).ToString("F2"))
                  .AppendLine(" 倍（纹素数几乎不是自变量）");
                sb.Append("　　 　 每次循环迭代暴露 ").Append(nsPerIter.ToString("F0"))
                  .Append(" ns　→ ")
                  .AppendLine(nsPerIter > 150.0 ? "与一次纹理取样往返同量级：延迟完全没被隐藏"
                            : nsPerIter < 60.0 ? "远小于一次取样往返：延迟已被藏住，这一级剩下的是别的开销"
                            : "介于两者之间，这一条不做判定");
            }

            // 证据三：per-dispatch 的地板。最便宜的那一级的耗时里既含它自己的工作
            // 也含一趟 dispatch 的固定开销，所以它是**固定开销的上界**；
            // 乘上趟数再与单级之和比，就得到"整个 pass 里有多少是花在'派发'本身上"。
            // 这一条的用途在改完形状之后才显出来：工作量被压下去以后，
            // 地板占比会自动升上来，而它指向的修法（合并 dispatch）与占用率无关。
            double floorMs = double.MaxValue;
            for (int m = 0; m < mips; ++m)
                if (iso[m].Or0() > 1e-4) floorMs = System.Math.Min(floorMs, iso[m].Or0());
            if (floorMs < double.MaxValue)
            {
                double floorTotal = floorMs * mips;
                double floorShare = sumIso > 1e-6 ? floorTotal / sumIso : 0.0;
                sb.Append("　　 证据 3｜最便宜的一级 ").Append(floorMs.ToString("F3"))
                  .Append(" ms（含它自己的工作，所以是单趟固定开销的**上界**）×")
                  .Append(mips).Append(" 趟 = ").Append(floorTotal.ToString("F3"))
                  .Append(" ms，占单级之和 ").Append(floorShare.ToString("P0")).Append("　→ ")
                  .AppendLine(floorShare > 0.5 ? "派发地板已是主项：下一个杠杆是合并 dispatch，不是占用率"
                            : floorShare < 0.25 ? "派发地板是零头：修法应指向占用率 / 取样量"
                            : "地板与工作量各占一半，两条修法都只能拿到一半收益");
            }

            sb.Append("　　 全 pass 标称取样 ").Append((totalSamples / 1000.0).ToString("F0"))
              .Append("k，按 iso 之和折算 ")
              .Append((sumIso > 1e-6 ? totalSamples / sumIso / 1e6 : 0.0).ToString("F2"))
              .AppendLine(" G样本/s（3060 的纹理取样率在数百 G样本/s 量级）");
        }

        /// <summary>
        /// AP 切片数 / 分布的**成本**侧，为 #7 的定档服务。
        ///
        /// 为什么不能拿自检里的"每柱行进步数"交差：那是个**标称**量。AP 核是一个线程
        /// 走一整柱（见 AerialPerspective.hlsl），同一个 warp 里的 32 根柱子步数不同时
        /// 会被最长的那根拖住，而 VISTA_AP_STEPS_MAX 又会把长柱截断 —— 标称步数与
        /// 实测耗时不必成比例。精度那一侧有 20 行实测，成本这一侧不该只有一个代理量。
        ///
        /// 两个口径都测，理由同逐 mip 归因那处：
        ///   iso   单独测 AP：N 次背靠背之间只有 UAV 写-写关系，驱动通常不为此串行化，
        ///         所以它是**下界**。但同一个偏低作用在每一个配置上，配置之间的**比值**
        ///         仍然可信 —— 这一列只用来读"切片数翻倍要多花多少"。
        ///   chain 把该配置换进稳态五 pass 整链再测：链内有真实的 SRV/UAV 依赖，重叠被
        ///         挡住。这才是能对外引用、能与 0.300 ms 预算比的绝对值。
        ///
        /// 报告里同时印显存，因为 AP 的两张 RGBAHalf 3D 表是这套方案里**唯一**随质量档
        /// 线性涨的常驻显存，而它小到常被忽略（32³ 才 512 KB）—— 印出来是为了让
        /// "为什么不干脆开 64 片"这个问题有数字可答，而不是靠印象。
        /// </summary>
        static void ReportApDepthCost(
            StringBuilder sb, SyncMode syncMode, Sample baseline, VistaAtmosphereLuts luts,
            VistaAtmosphereViewData view,
            System.Func<VistaAerialPerspectiveSettings, System.Action<CommandBuffer>> chainWith,
            Sample tSteadyDefault, Sample tApDefault)
        {
            // 候选取自 #7 精度扫描里的四个焦点：最省的 d=16 Log、同深度但把切片重分布的
            // d=16 Pow k=3（精度追上 d=32 Log 的那一档）、中档 d=24、以及 PC 备选 d=32 的
            // 两种分布。再加 d=64 Log 作为"切片数—耗时"曲线的远端锚点：没有它就无法判断
            // 这条曲线到底是线性的还是有拐点，而"翻倍很便宜"恰恰是要被证伪的直觉。
            var configs = new (string label, int depth,
                               VistaAerialPerspectiveSettings.Distribution dist, float k, float nearKm)[]
            {
                ("d=16 Log 20m ", 16, VistaAerialPerspectiveSettings.Distribution.Logarithmic, 2f, 0.02f),
                ("d=16 Pow k=3 ", 16, VistaAerialPerspectiveSettings.Distribution.Power,       3f, 0.02f),
                ("d=24 Log 20m ", 24, VistaAerialPerspectiveSettings.Distribution.Logarithmic, 2f, 0.02f),
                ("d=32 Log 20m ", 32, VistaAerialPerspectiveSettings.Distribution.Logarithmic, 2f, 0.02f),
                ("d=32 Pow k=3 ", 32, VistaAerialPerspectiveSettings.Distribution.Power,       3f, 0.02f),
                ("d=64 Log 20m ", 64, VistaAerialPerspectiveSettings.Distribution.Logarithmic, 2f, 0.02f),
            };

            sb.AppendLine("── AP 切片数定档·成本侧（iso 是下界，只读配置间比值；chain 是可引用的绝对值）");

            var isoMs = new double[configs.Length];
            var chainMs = new double[configs.Length];

            for (int i = 0; i < configs.Length; ++i)
            {
                var c = configs[i];
                var s = new VistaAerialPerspectiveSettings
                {
                    resolution   = new Vector3Int(32, 32, c.depth),
                    distribution = c.dist,
                    powerExponent = c.k,
                    nearDistanceKm = c.nearKm,
                };
                // 分配在计时窗口之外。深度变了才会真的重建纹理（Equals 只比尺寸）。
                if (!luts.PrepareAerialPerspective(s))
                {
                    sb.AppendLine("　　 ✘ PrepareAerialPerspective 失败，成本侧中止。");
                    return;
                }

                var iso   = Per(syncMode, baseline,
                                cmd => luts.RenderAerialPerspectiveLut(D(cmd, luts), view, s));
                var chain = Per(syncMode, baseline, chainWith(s));
                isoMs[i]   = iso.Or0();
                chainMs[i] = chain.Or0();

                // 两张 RGBAHalf（散射 + 彩色透射率），每纹素 8 B。
                double kb = 2.0 * s.width * s.height * s.depth * 8.0 / 1024.0;
                double apShare = chain.Or0() > 1e-6 ? iso.Or0() / chain.Or0() : 0.0;

                sb.Append("　　 ").Append(c.label)
                  .Append("iso ").Append(iso.Or0().ToString("F3")).Append(" ms")
                  .Append("　chain ").Append(chain.Fmt())
                  .Append("　显存 ").Append(kb.ToString("F0")).Append(" KB")
                  .Append("　AP 占链 ").Append(apShare.ToString("P0"));
                if (chain.valid && chain.spread > 0.25) sb.Append("　⚠±").Append(chain.spread.ToString("P0"));
                sb.AppendLine();
            }

            // ---- 曲线形状：翻倍到底贵不贵 ----
            // d=16→32→64 三点。只报比值不下结论的原因：这条曲线的斜率会被
            // VISTA_AP_STEPS_MAX 的截断改变，而截断点随 far/near 变 —— 现在量到的
            // 斜率只对当前 far=32 km 成立，写成"AP 耗时随切片数亚线性"就越界了。
            double r16to32 = isoMs[0] > 1e-4 ? isoMs[3] / isoMs[0] : 0.0;
            double r32to64 = isoMs[3] > 1e-4 ? isoMs[5] / isoMs[3] : 0.0;
            sb.Append("　　 iso 比值　16→32 ×").Append(r16to32.ToString("F2"))
              .Append("　32→64 ×").Append(r32to64.ToString("F2"))
              .Append("（切片数各翻一倍；斜率随 VISTA_AP_STEPS_MAX 的截断点变，"
                    + "只对 far=32 km 这一档成立）").AppendLine();

            // ---- 同深度换分布的代价 ----
            // 这是定档里最关键的一条：#7 量到 d=16 Pow k=3 的最差段误差是 d=16 Log 的 1/4，
            // 若它的耗时也差不多，那"提精度"就不必买切片数 —— 重分布是免费的。
            double dLog16 = isoMs[0], dPow16 = isoMs[1];
            if (dLog16 > 1e-4)
                sb.Append("　　 同深度换分布　d=16 Log ").Append(dLog16.ToString("F3"))
                  .Append(" → Pow k=3 ").Append(dPow16.ToString("F3"))
                  .Append(" ms（×").Append((dPow16 / dLog16).ToString("F2"))
                  .AppendLine("）；显存完全不变，切片数不变，只是把切片挪了位置");

            // ---- 恢复默认配置 ----
            // 上面的循环把纹理留在了最后一个候选的深度上。不恢复的话，此后任何用
            // 默认 apSettings 的调用都会拿 depth=32 的 dispatch 打进 64 深的表里。
            // 这个函数现在排在所有测量之后，但"当前没有后续调用"不是可以不恢复的理由。
            luts.PrepareAerialPerspective(new VistaAerialPerspectiveSettings());
            sb.Append("　　 参照：默认档 32³/Log 的 iso ").Append(tApDefault.Or0().ToString("F3"))
              .Append(" ms、整链 ").Append(tSteadyDefault.Or0().ToString("F3"))
              .AppendLine(" ms（上方逐 pass 与整链两节测的就是这一档）");
        }

        /// <summary>
        /// 镜像 <c>VistaSkyReflectionSampleCount</c>（SkyReflection.hlsl:94-100）的取样数。

        ///
        /// 这是一份**报告专用**的副本，故意不去做"唯一真源"：它只用来给上面那张表算
        /// 取样量与吞吐，不参与任何判定。所以两边哪天走歧，后果是吞吐那一列的分子错了，
        /// 而不是某条断言给出错误的通过 —— 前者一眼能看出（G样本/s 会突变），
        /// 后者才是不可接受的。要做真源同步就得让核把它写进一个 buffer 再读回，
        /// 而为一列诊断数字加一趟 dispatch 不划算。
        /// </summary>
        static int NominalSampleCount(int mip, VistaSkyReflectionMode mode)
        {
            if (mip == 0) return 1;
            if (mode == VistaSkyReflectionMode.AmbientSh) return 16;
            return Mathf.Min(256, 16 << mip);
        }

        ///
        /// 一条 buffer 录 N 遍而不是 N 次提交+同步：后者量到的几乎全是提交与同步的
        /// 往返延迟（每次百微秒到毫秒量级），被测的 dispatch 反而淹没在里面。
        ///
        /// buffer 的**录制**在计时窗口之外 —— 那是 CPU 成本，与被测的 GPU 工作无关。
        /// </summary>
        static double RawMs(System.Action<CommandBuffer> record, int reps, SyncMode mode)
        {
            var cmd = new CommandBuffer { name = "Vista LUT Profile" };
            for (int i = 0; i < reps; ++i)
                record(cmd);

            GraphicsFence fence = default;
            if (mode == SyncMode.Fence)
            {
                // CPUSynchronisation 而不是 AsyncQueueSynchronisation：只有前者能用
                // fence.passed 在 CPU 上轮询。ComputeProcessing 是这条链路唯一用到的阶段。
                fence = cmd.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation,
                                                SynchronisationStageFlags.ComputeProcessing);
            }

            var sw = Stopwatch.StartNew();
            Graphics.ExecuteCommandBuffer(cmd);
            // ExecuteCommandBuffer 只是排进渲染线程的队列；GL.Flush 把它推下去。
            GL.Flush();

            // 这里**没有超时**，因为能不能用已经由 ProbeFence 在 250 ms 内定过了 ——
            // 走到这条路径就说明 fence 在这台机器上确实会 passed。
            // 第一版把"能不能用"和"每次等多久"混成同一个 5 s 超时，代价是
            // 二十次各撞满超时、Editor 卡四分钟、全部数字失效。探测与等待要分开。
            if (mode == SyncMode.Fence)
                while (!fence.passed) { }
            else
                s_Sync.GetData(s_SyncData);

            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            cmd.Release();

            // fence 路径补一次硬同步，在计时窗口之外：finally 里要 Dispose 这些
            // RTHandle 与 buffer，而释放正在被 GPU 读的资源在 D3D12/Vulkan 上是未定义行为。
            if (mode == SyncMode.Fence)
                s_Sync.GetData(s_SyncData);

            return ms;
        }
    }
}
