using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
// 计时核（fence 探测 / 多轮取最小 / 背靠背摊销）住在 VistaGpuTimer 里，AP 合成的
// 性能项也用它 —— 别名只是让下面几十处 Sample/SyncMode 不必写全名。
using SyncMode = Vista.Editor.VistaGpuTimer.SyncMode;
using Sample = Vista.Editor.VistaGpuTimer.Sample;

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

        // 摊销次数 / 预热次数 / 取最小的轮数，以及"为什么取最小而不是平均"那条
        // 实测理由，都在 VistaGpuTimer 里 —— 它们现在被 AP 合成的性能项共用。

        /// <summary>
        /// 稳态五 pass 的预算。0.3 ms 不是拍的：目标是 60 fps（16.67 ms）下
        /// 整条大气链路占不到 2%。这一档留给"每帧都跑"的五个 pass，
        /// 静态两表只在参数变化帧出现，不计入。
        /// </summary>
        const float k_SteadyBudgetMs = 0.300f;

        /// <summary>太阳仰角。与另外三份自检的"正午档"一致，三份报告的数字才能横向对照。</summary>
        const float k_SunElevationDeg = 60f;

        /// <summary>
        /// harness 自校验的一致性阈值。两条同步路径量同一份工作，15% 以内算一致
        /// （它们的固定开销不同，扣除后仍会留一点残差）。
        /// </summary>
        const float k_HarnessTolerance = 0.15f;

        [MenuItem("Window/Vista/Profile Atmosphere LUTs", priority = 125)]
        public static void Run()
        {
            var sb = new StringBuilder();
            bool ok = Profile(sb);

            Debug.Log(("[Vista] LUT 耗时" + (ok ? "达标" : "**未达标/不可判定**") + "\n" + sb)
                      .Replace("\r", "").Replace("\n", "  |  "));
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
            VistaGpuTimer.Begin();

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

                bool fenceUsable = VistaGpuTimer.ProbeFence();
                var syncMode = fenceUsable ? SyncMode.Fence : SyncMode.Readback;

                sb.Append("── LUT 链路耗时（模型 B：Edit 模式立即提交，N=")
                  .Append(VistaGpuTimer.k_DefaultIterations).Append(" 摊销 × ")
                  .Append(VistaGpuTimer.k_DefaultTrials).Append(" 轮取最小）")
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
                    ? Per(syncMode, baseline, cmd => luts.RenderAerialPerspectiveLut(D(cmd, luts), view, ap, null))
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
                // fog 也当参数：含雾的整链才是出货那一帧真正付的钱，而 0.300 ms 的
                // 预算此前只在晴空下量过。把它做成参数而不是在别处另写一条链，
                // 是为了保住"链的其余四 pass 逐字相同"这条归因前提。
                System.Func<VistaAerialPerspectiveSettings, VistaFogSettings,
                            System.Action<CommandBuffer>> chainWith =
                    (a, fog) => cmd =>
                {
                    luts.RenderSkyViewLut(D(cmd, luts), view);
                    if (shOk)   luts.RenderSkyAmbientSh(D(cmd, luts), view);
                    if (reflOk) { luts.RenderSkyReflection(D(cmd, luts), view, mode);
                                  luts.CopySkyReflectionToCube(cmd); }
                    if (apOk)   luts.RenderAerialPerspectiveLut(D(cmd, luts), view, a, fog);
                };
                var steadyChain = chainWith(ap, null);
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

                if (apOk)
                    ReportFogCost(sb, syncMode, baseline, luts, p, sunDir, ap, chainWith, tAp, tSteady);

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
                VistaGpuTimer.End();
                luts.Dispose();
            }
        }

        // ==================================================================
        //  测量
        // ==================================================================

        // Sample / RawMs / SyncMode 都在 VistaGpuTimer 里（文件头有 using 别名）。
        // 只留这一个本地便利包装：它把"扣基线"这件事的调用点收成一处。

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
            System.Func<VistaAerialPerspectiveSettings, VistaFogSettings,
                        System.Action<CommandBuffer>> chainWith,
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
                                cmd => luts.RenderAerialPerspectiveLut(D(cmd, luts), view, s, null));
                var chain = Per(syncMode, baseline, chainWith(s, null));
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

        // ==================================================================
        //  雾的成本归因
        // ==================================================================

        /// <summary>雾数学（逐样本）占 AP pass 的上限。超了就该给关雾留一条 uniform 分支。</summary>
        const float k_FogMathShareMax = 0.05f;

        /// <summary>雾驱动的步长上限占 AP pass 的上限。超了它就该降级成画质档（PC 开 / 移动端关）。</summary>
        const float k_FogCapShareMax = 0.50f;

        /// <summary>
        /// 放大档的横向分辨率。见 <see cref="ReportFogCost"/> 里「为什么必须放大」。
        /// 深度保持 32：切片数一变，每柱的步数分布也变，就不是同一个被测对象了。
        /// </summary>
        static readonly Vector3Int k_FogMidRes  = new Vector3Int(256, 144, 32);
        static readonly Vector3Int k_FogBigRes  = new Vector3Int(512, 288, 32);

        /// <summary>
        /// 「每柱代价」跨档比较时固定用哪个视角。理由见 <see cref="ReportFogCost"/>：
        /// 两个视角的工作量本来就不同，换视角就把工作量差混进地板的读数里。
        /// 取 0（更贵的那个）是保守方向。
        /// </summary>
        const int k_FogFloorViewIndex = 0;

        /// <summary>
        /// 雾并进 AP march 之后多花的钱，以及它花在哪一半。
        ///
        /// 要回答的是两个**独立**的决定，所以必须分开量：
        ///   1) 逐样本的雾数学（采一次密度剖面 + 一次 HG + 一次环境项）值多少？
        ///      → 超过 AP pass 的 5% 就该给「关雾」留一条 uniform 分支；
        ///        否则无条件算雾更好 —— 少一个变体，少一条能写错的路。
        ///   2) 雾驱动的步长上限（<c>FogMedium.hlsl</c> 的 <c>0.4·efold</c>）值多少？
        ///      → 超过 AP pass 的 50% 它就该变成画质档（PC 开 / 移动端关），
        ///        而不是无条件生效。
        ///
        /// ---- 怎么把两项分开，又不动 shader ----
        /// 用**标高 → ∞ 的均匀雾**当中间档。均匀雾的 σ_t 与指数雾逐位相同，
        /// 逐样本的雾数学一模一样，但 <c>efold → ∞ ⇒ 上限恒不生效</c>
        /// （见 <c>VistaFogStepMaxKm</c> 的三条退化注释）。于是
        ///     均匀 − 关雾 = 雾数学的钱
        ///     指数 − 均匀 = 步长上限的钱
        /// 三个档是同一个 kernel、同一份视锥、同一个切片分布，唯一变的是 cbuffer 里的 1/H。
        /// 这比「注释掉那行 min 再重编」可信：后者要改被测代码，这里连 shader 都没重编。
        ///
        /// ---- 尺子的分辨力从哪儿来（不是 spread，也不是两个视角之差）----
        /// 用**同一个视角、同一个关雾配置量两遍**，一遍放在 uni/exp 之前、一遍放在之后。
        /// 两遍本该逐位相同，它们实际差多少，就是这一档尺子在这段时间窗里的分辨力下界。
        /// 比它小的差分一律不可判。
        /// 为什么不用 <c>Sample.spread</c>：spread 是同一次调用内 5 轮之间的抖动，
        /// 抓不到"跨调用的系统性漂移"（纹理首次写入、GPU 时钟档位、Editor 偶发重绘），
        /// 而 uni/exp 与 off 恰恰是不同调用。把 off 量两遍是唯一能覆盖这段漂移的 A-A 空测。
        /// 差分的分母因此取两遍 off 的平均：uni/exp 在时间上被这一对括起来，
        /// 用平均值当基线能把线性漂移一阶消掉。
        ///
        /// ---- 一个曾经被我当成"尺子噪声"的真实差异 ----
        /// 两个视角的关雾读数差 37%（0.569 vs 0.415 ms），三个规模上都稳定复现。
        /// 第一版把它当成尺子的地板，**错了** —— 它是真实的工作量差：
        /// <c>AtmosphereLut.compute</c> 的 AP 核里 <c>tLimit = min(出大气顶, 撞地)</c>，
        /// 而 <c>if (segLen &gt; 0.0)</c> 让 tLimit 之外的切片**整段被跳过**。
        /// 于是每柱的步数取决于这根射线在哪儿撞地：
        ///   · 视角①（2 m 平视）：dir.y &lt; 0 的行在 4~20 m 处撞地，
        ///     **早于第一片（20 m）** → 那些柱只做 2 步。约一半行几乎免费，
        ///     另一半跑满 168 步 → 平均 ≈ 85 步/柱。
        ///   · 视角③（300 m 俯 20°）：dir.y ∈ [−0.77, +0.17]，向下的行在 0.4~6 km 撞地，
        ///     仍覆盖 13~19 片（每片 2 步）→ 82% 的行约 35 步，18% 跑满
        ///     → 平均 ≈ 59 步/柱。
        /// 预测比值 85/59 = 1.44×，实测 1.37× —— 对得上。
        /// 这是解析交叉校验，不是第二份实现：CPU 侧不重写步数逻辑
        /// （<c>ApMarchStepsPerColumn</c> 只算中心柱的晴空代理，不含撞地截断）。
        /// 顺带一条对 #19 有用的结论：AP 的成本主要由**视锥的撞地分布**决定，
        /// 而不是由切片数决定。
        ///
        /// ---- 为什么必须放大分辨率，以及第一次放大时我又猜错了什么 ----
        /// 出货档 32×32×32 的横向只有 32×32 = 1024 根柱 = **16 个线程组**（numthreads 8×8），
        /// 而 3060 有 28 个 SM —— 连一半 SM 都占不满，每个 SM 上只有 2 个 warp
        /// 可以互相隐藏延迟。它量到的是**派发与延迟的地板**，不是逐样本的算力。
        /// 第一次跑就是在这个规模上量的：关雾 0.048 / 均匀 0.049 / 指数 0.053 ms，
        /// 三个数间距 0.002 ms，而这一项自己的离散度 ±23%（0.011 ms）—— 差比噪声小 5 倍。
        ///
        /// 然后我按「256×144 是 36 倍的柱数 ⇒ 单次约 1.7 ms ⇒ 摊销次数可以从 200 减到 30」
        /// 配了放大档。**实测 0.19 ms**：36 倍的工作只换来 4.4 倍的时间。
        /// 这个 8 倍的落空恰恰是地板的直接度量（也是这条推理第二次栽在同一个坑里 ——
        /// 拿公式去预测一个已经被证明落在地板上的量），而减到 30 次的摊销
        /// 让固定开销的份额从 0.3% 涨到 11%，反把噪声放大了。
        /// 现在的做法：摊销次数一律用默认值，靠**分辨率**离开地板，
        /// 并把三档的「每柱代价」印出来 —— 那条比值就是地板占了多少的实测值。
        ///
        /// ---- 为什么必须量两个视角，以及关于步长上限我猜错的另一件事 ----
        /// 原来的注释写的是「平视时 dir.y → 0 ⇒ efold → ∞ ⇒ 上限不生效，
        /// 所以只测平视会量到 0」。**这条是错的**：AP LUT 覆盖的是整个视锥，
        /// 30° 半张角下只有正中那一行 dir.y ≈ 0，其余各行的 |dir.y| 一直到 0.5。
        /// 而贴地平视时相机就在雾里（h = 2 m ⇒ ρ ≈ 0.96），那些斜向上的行
        /// 从第一片起就要被收紧步长。「按 dir.y 推」这种论证只对单根射线成立，
        /// 对一个视锥不成立 —— 所以两个视角都得量，判定取最差的那个。
        ///
        /// ---- 口径 ----
        /// 这一节的 AP 单测仍然是**下界**（见类注释边界 1），但它要的是**比值**，
        /// 而背靠背的重叠红利对三个档是同性质的。绝对值的口径由末尾那条「整链含雾」给。
        ///
        /// 另：<c>VistaAtmosphereSelfTest.ApMarchStepsPerColumn</c> 那个 CPU 步数代理
        /// **只算晴空**（它不含 <c>VistaFogStepMaxKm</c>），含雾时偏低。这里不在 CPU 上
        /// 再实现一遍那个函数 —— 同一个量的第二份实现连 8 行的辅助函数也算，
        /// 两份哪天分叉，症状是「代理说没涨、实测涨了」，而那时先被怀疑的会是测量。
        /// 含雾的成本由本节实测给，代理只报晴空并在报告里标明。
        /// </summary>
        static void ReportFogCost(
            StringBuilder sb, SyncMode syncMode, Sample baseline, VistaAtmosphereLuts luts,
            VistaAtmosphereParameters p, Vector3 sunDir, VistaAerialPerspectiveSettings ap,
            System.Func<VistaAerialPerspectiveSettings, VistaFogSettings,
                        System.Action<CommandBuffer>> chainWith,
            Sample tApNoFog, Sample tSteadyNoFog)
        {
            // σ_t = 1000/400 = 2.5 /km，与 Validate Fog (AP + Sky) 的 B 档逐字相同，
            // 两份报告才能对着看。均匀档只改标高，σ_t 一个字都不动 —— 这是本节归因的前提。
            var expFog = FogOf(400f, 50f);
            var uniFog = FogOf(400f, float.PositiveInfinity);

            var views = new (string label, Vector3 pos, float pitch)[]
            {
                ("贴地平视 (0,2,0) 0°", new Vector3(0f,   2f, 0f),  0f),
                ("俯视 (0,300,0) 20°",  new Vector3(0f, 300f, 0f), 20f),
            };

            sb.AppendLine("　 雾成本归因（AP 单测；关雾 → 均匀雾σ_t同/标高∞ → 指数雾 H=50；"
                        + "两个差分别是「雾数学」与「步长上限」）");

            double worstMath = 0.0, worstCap = 0.0;
            double judgedFloor = 0.0;   // 判定档的分辨力下界（同一视角关雾量两遍之差）
            bool judgedOk = false;
            double bigPerColumnNs = 0.0, shipPerColumnNs = 0.0;

            var regimes = new (string label, Vector3Int res, bool judged)[]
            {
                ("出货档", ap.resolution, false),
                ("中间档", k_FogMidRes,   false),
                ("放大档", k_FogBigRes,   true),
            };

            foreach (var reg in regimes)
            {
                var s = new VistaAerialPerspectiveSettings { resolution = reg.res };
                // 分配在计时窗口之外。Equals 只比尺寸，所以尺寸不变时不会重建纹理。
                if (!luts.PrepareAerialPerspective(s))
                {
                    sb.Append("　　 ✘ ").Append(reg.label)
                      .AppendLine(" PrepareAerialPerspective 失败，本档跳过。");
                    continue;
                }

                int columns = s.width * s.height;
                int groups  = (s.width + 7) / 8 * ((s.height + 7) / 8);
                sb.Append("　　 ").Append(reg.label)
                  .Append(' ').Append(s.width).Append('×').Append(s.height).Append('×').Append(s.depth)
                  .Append("　柱 ").Append(columns)
                  .Append("　线程组 ").Append(groups)
                  .Append("（占不满 SM 就是地板；RTX 3060 有 28 个 SM，换机器要重核这句）")
                  .Append("　显存 ").Append(columns * s.depth * 16 / 1024).Append(" KB")
                  .Append(reg.judged ? "　← 判定取这一档" : "　← 地板参照，不判定")
                  .AppendLine();

                var offA = new Sample[views.Length];
                double regimeFloor = 0.0;
                for (int i = 0; i < views.Length; ++i)
                {
                    var vd = views[i];
                    var v = MakeProfilerView(p, vd.pos, vd.pitch, sunDir);
                    // off 量两遍，把 uni/exp 括在中间。第二遍不是冗余 —— 它是这一档的
                    // A-A 空测，见方法注释「尺子的分辨力从哪儿来」。
                    offA[i]  = Per(syncMode, baseline,
                                   cmd => luts.RenderAerialPerspectiveLut(D(cmd, luts), v, s, null));
                    var uni  = Per(syncMode, baseline,
                                   cmd => luts.RenderAerialPerspectiveLut(D(cmd, luts), v, s, uniFog));
                    var exp  = Per(syncMode, baseline,
                                   cmd => luts.RenderAerialPerspectiveLut(D(cmd, luts), v, s, expFog));
                    var offB = Per(syncMode, baseline,
                                   cmd => luts.RenderAerialPerspectiveLut(D(cmd, luts), v, s, null));

                    // 分母取两遍关雾的平均：uni/exp 在时间上被这一对括起来，
                    // 用平均值当基线能把线性漂移一阶消掉。
                    // 两个百分比同基，才能直接相加成「雾一共多花了多少」——
                    // 若第二项拿均匀档当分母，两个数不同基，加起来没有意义。
                    double basis = 0.5 * (offA[i].Or0() + offB.Or0());
                    double math = basis > 1e-4 ? (uni.Or0() - basis) / basis : 0.0;
                    double cap  = basis > 1e-4 ? (exp.Or0() - uni.Or0()) / basis : 0.0;
                    double nul  = basis > 1e-4
                        ? System.Math.Abs(offB.Or0() - offA[i].Or0()) / basis : 1.0;
                    if (reg.judged)
                    {
                        worstMath = System.Math.Max(worstMath, math);
                        worstCap  = System.Math.Max(worstCap,  cap);
                    }
                    // 地板取**最差视角**：它是全局的判定闸，哪个视角的尺子更钝就按更钝的算。
                    regimeFloor = System.Math.Max(regimeFloor, nul);

                    sb.Append("　　　 ").Append(vd.label.PadRight(20))
                      .Append("关雾 ").Append(basis.ToString("F3"))
                      .Append("　均匀 ").Append(uni.Or0().ToString("F3"))
                      .Append('(').Append(math.ToString("+0.0%;-0.0%")).Append(')')
                      .Append("　指数 ").Append(exp.Or0().ToString("F3"))
                      .Append('(').Append(cap.ToString("+0.0%;-0.0%")).Append(')')
                      .Append(" ms　空测 ").Append(nul.ToString("P1"))
                      .Append("　±")
                      .Append(System.Math.Max(offA[i].spread,
                                              System.Math.Max(uni.spread,
                                              System.Math.Max(exp.spread, offB.spread))).ToString("P0"))
                      .AppendLine();
                }

                // 每柱代价必须固定用**同一个视角**跨档比 —— 两个视角做的工作量本来就不同
                // （见方法注释），min/max 会随机换视角，把工作量差混进地板的读数里。
                // 取更贵的那个视角（index 0）是保守方向：W 越大，ratio ≈ 1 + floor/W 越小，
                // 报出来的地板占比越低。
                double refOff = offA[k_FogFloorViewIndex].Or0();
                double perColumnNs = refOff > 1e-4 ? refOff * 1e6 / columns : 0.0;
                if (reg.judged) { judgedFloor = regimeFloor; judgedOk = true; bigPerColumnNs = perColumnNs; }
                else if (reg.res == ap.resolution) shipPerColumnNs = perColumnNs;

                // 跨视角比值印出来当**观察**，不当地板：它已被证明是真实的工作量差。
                double lo = System.Math.Min(offA[0].Or0(), offA[1].Or0());
                sb.Append("　　　 每柱 ").Append(perColumnNs.ToString("F2")).Append(" ns（视角 ")
                  .Append(views[k_FogFloorViewIndex].label).Append("）")
                  .Append("　跨视角比 ")
                  .Append((lo > 1e-4
                      ? System.Math.Max(offA[0].Or0(), offA[1].Or0()) / lo : 0.0).ToString("F2"))
                  .AppendLine("×（撞地截断造成的真实工作量差，解析预测 1.44×；不是地板）");
            }

            // ---- 恢复出货档 ----
            // 上面的循环把两张 3D 表留在了放大档尺寸。不恢复的话此后任何用默认 settings
            // 的调用都会把 32×32 的 dispatch 打进一张大表里。
            luts.PrepareAerialPerspective(new VistaAerialPerspectiveSettings());

            // 地板的实测值：每柱代价在两个规模之间的比值。放大档自己也还含一部分地板，
            // 所以这是地板占比的**下界**。
            if (shipPerColumnNs > 0.0 && bigPerColumnNs > 0.0)
                sb.Append("　　 地板实测：出货档每柱 ").Append(shipPerColumnNs.ToString("F2"))
                  .Append(" ns 是放大档的 ").Append((shipPerColumnNs / bigPerColumnNs).ToString("F1"))
                  .Append(" 倍　→ 出货档至少 ")
                  .Append((1.0 - bigPerColumnNs / shipPerColumnNs).ToString("P0"))
                  .AppendLine(" 的时间不在做逐样本的工作（放大档自身仍含地板，故为下界）");

            // 判定印的是**最差视角**而不是平均：这两个开关是全局的，一个视角超门就得付。
            // 但先要过分辨力这道闸 —— 差分小于尺子的地板时，「小于门」这个结论
            // 是尺子伪造的，不是量到的。
            FogVerdict(sb, "雾数学　", worstMath, k_FogMathShareMax, judgedFloor, judgedOk,
                       "无条件算雾（不给关雾加 uniform 分支）",
                       "**超门，值得给关雾留一条 uniform 分支**");
            FogVerdict(sb, "步长上限", worstCap, k_FogCapShareMax, judgedFloor, judgedOk,
                       "无条件生效（不降级成画质档）",
                       "**超门，应降级成画质档（PC 开 / 移动端关）**");

            // ---- 整链含雾：唯一能与 0.300 ms 预算比的绝对值 ----
            // 上面「稳态五 pass 整链」那一节测的是**晴空**，而出货那一帧是含雾的。
            // 不补这一条，报告里那个达标结论就只对关雾的场景成立。
            var chainFog = Per(syncMode, baseline, chainWith(ap, expFog));
            double delta = chainFog.Or0() - tSteadyNoFog.Or0();
            bool quotable = chainFog.valid && chainFog.spread < 0.25;
            sb.Append("　　 整链含雾（出货档）").Append(chainFog.Fmt())
              .Append("　晴空 ").Append(tSteadyNoFog.Or0().ToString("F3"))
              .Append(" ms　差 ").Append(delta.ToString("+0.000;-0.000"))
              .Append(" ms ／阈 ").Append(k_SteadyBudgetMs.ToString("F3")).Append(" ms ")
              .Append(!quotable ? "（离散度过大，不判定）"
                    : chainFog.min < k_SteadyBudgetMs ? "OK" : "**超预算**")
              .AppendLine();
            sb.Append("　　 参照：默认档关雾的 AP 单测 ").Append(tApNoFog.Or0().ToString("F3"))
              .AppendLine(" ms（上方逐 pass 那一节测的就是它）");
            sb.AppendLine("　　 注：VistaAtmosphereSelfTest 报的「步/柱」是**晴空**代理，"
                        + "不含 VistaFogStepMaxKm；含雾的步数成本只看本节实测。");
        }

        /// <summary>
        /// 一条门的判定。三种结局要分清，因为它们导向的行动不同：
        ///   · 差分 &gt; 门　　　　　　　　→ 该加那个开关；
        ///   · 2×空测 &lt; 差分 &lt; 门　　　 → 量到了，且没超门 → 不加；
        ///   · 差分 &lt; 2×空测　　　　　　→ **没量到**。这时缺省行动仍是"不加"，
        ///     但理由不是"它便宜"，而是"加一个变体需要正面证据，而这里没有"。
        ///     把这两种"不加"印成同一句话，等于让一个量不出来的读数冒充一个达标的读数。
        ///
        /// 为什么是 2× 而不是 1×：门设在空测本身时，差分与空测同量级的读数
        /// （实测出现过 +0.4% vs 0.4%）落在哪一边由**打印出来的最后一位小数**决定，
        /// 而它们在下一轮的排序完全可能反过来。留 2 倍裕度是要求"信号明显高于噪声"
        /// 才算量到，与 #7 判据里 okModel 用 predicted·2.0 是同一种做法。
        /// 代价是把一部分真实的小信号也判成未量到 —— 方向是保守的：
        /// 它只会让我少加开关，不会让我基于噪声去加开关。
        /// </summary>
        static void FogVerdict(StringBuilder sb, string name, double value, float gate,
                               double floorRel, bool floorKnown, string pass, string fail)
        {
            double confident = 2.0 * floorRel;
            sb.Append("　　 判定（放大档）").Append(name).Append(" 最差 ")
              .Append(value.ToString("+0.0%;-0.0%"))
              .Append(" ／门 ").Append(gate.ToString("P0"))
              .Append("　／A-A 空测 ").Append(floorKnown ? floorRel.ToString("P1") : "未知")
              .Append("（可判门槛 ").Append(floorKnown ? confident.ToString("P1") : "未知").Append("）")
              .Append("　→ ");

            if (!floorKnown)
                sb.AppendLine("**分辨力未知，不判定**");
            else if (value >= gate)
                sb.AppendLine(fail);
            else if (System.Math.Abs(value) < confident)
                sb.Append("落在 2× 空测之下：**未量到**（上界 ")
                  .Append(confident.ToString("P1"))
                  .AppendLine("）。缺省行动仍是不加开关 —— 加变体要正面证据。");
            else
                sb.AppendLine(pass);
        }

        /// <summary>
        /// 一个只改标高的雾档。σ_t 由平均自由程给（1000/L），其余全取默认 ——
        /// 本节要的是「同一份雾，上限生效与不生效」，所以除标高外一个参数都不许动。
        /// </summary>
        static VistaFogSettings FogOf(float mfpMeters, float scaleHeightMeters)
        {
            var f = new VistaFogSettings();
            f.mode = VistaFogSettings.Mode.AerialPerspective;
            f.densityInput = VistaFogSettings.DensityInput.MeanFreePath;
            f.meanFreePathMeters = mfpMeters;
            f.scaleHeightMeters = scaleHeightMeters;
            return f;
        }

        /// <summary>
        /// 带俯仰角的视图。必须显式给视锥四角：<c>Create</c> 的兜底视锥是水平的，
        /// 而雾的步长上限完全由 <c>dir.y</c> 驱动 —— 靠兜底值就只能量到平视那一档。
        /// 30° 半张角 / 16:9 与 <c>Validate Fog (AP + Sky)</c> 一致。
        /// </summary>
        static VistaAtmosphereViewData MakeProfilerView(
            VistaAtmosphereParameters p, Vector3 cameraPos, float pitchDeg, Vector3 sunDir)
        {
            var view = VistaAtmosphereViewData.Create(p, cameraPos, 0f, sunDir);
            var rot = Quaternion.Euler(pitchDeg, 0f, 0f);
            view.SetFrustumRays(rot * Vector3.forward, Vector3.right, rot * Vector3.up,
                                Mathf.Tan(30f * Mathf.Deg2Rad) * (16f / 9f),
                                Mathf.Tan(30f * Mathf.Deg2Rad));
            return view;
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
    }
}
