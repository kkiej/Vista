using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

namespace Vista.Editor
{
    /// <summary>
    /// 模型 A：Play 模式下用 <see cref="ProfilerRecorder"/> 抓 RenderGraph 的逐 pass marker，
    /// 交叉验证 <c>VistaAtmosphereLutProfiler</c> 与 <c>VistaApCompositePerf</c>（模型 B）
    /// 给出的数字。覆盖两张静态表 + 稳态五个 LUT pass + AP 合成（全屏光栅）。
    ///
    /// 两个模型测的**不是同一件事**，所以这不是"再量一遍看对不对"：
    ///   模型 B（Edit 模式立即提交、N 次背靠背摊销）测的是**吞吐** —— 相邻 dispatch
    ///     允许重叠，所以逐 pass 值是下界，只能读占比；而且立即模式的状态转换由图形层插，
    ///     **不是** RenderGraph 那批 barrier。
    ///   模型 A（这里）测的是**帧内延迟**，跑在真正的 RenderGraph 图上，
    ///     所以它是唯一能把 barrier / pass 边界 / 资源状态转换算进去的口径。
    /// 因此预期是**逐 pass A ≥ B**。若某一项 A 明显小于 B，结论不是"B 量高了"，
    /// 而是 marker 归属可疑（被合并、被剪掉、或者那一帧根本没跑这个 pass）——
    /// 报告里会把这个方向单独判一次，因为它指向的是工具错而不是性能事实。
    ///
    /// **已知的、无法绕过的限制**：RenderGraph 的逐 pass marker 被
    /// <c>#if DEVELOPMENT_BUILD || UNITY_EDITOR</c> 包着（core <c>RenderGraph.cs:2868-2884</c>），
    /// Release 构建里这些 marker 根本不存在。也就是说这条口径只在 Editor / Development
    /// 里成立，拿不到"发行版里这个 pass 多少毫秒"。这一点必须跟数字一起给出去，
    /// 否则等于在暗示一个测不到的东西。
    ///
    /// 实现上刻意**不放 MonoBehaviour 进场景**：
    /// ① Editor assembly 的组件塞进运行场景本身就是个坏味道，且退出 play 模式时
    ///    要处理清理与"用户手动停止"的中断路径；
    /// ② <c>EditorApplication.update</c> 在 play 模式里照样每个 Editor tick 触发，
    ///    读 recorder 不需要在渲染线程上，也不需要 per-frame 回调；
    /// ③ 这个工具一个字节的场景改动都不产生 —— 量性能的工具改被量的对象，
    ///    是这类 harness 最容易犯的错。
    /// </summary>
    static class VistaLutGpuRecorderCrossCheck
    {
        /// 进 play 模式会触发域重载（默认开），静态字段会被清掉，
        /// 所以"这次进 play 是为了跑交叉验证"必须存在能跨域重载的地方。
        /// SessionState 存活到 Editor 退出为止，正好是这个工具的生命周期。
        const string k_ArmedKey = "Vista.LutGpuXCheck.Armed";

        /// 前 N 帧丢掉：进 play 的头几帧有 shader 变体编译、资源上传、
        /// RenderGraph 首次编译（图结构缓存未建），那几帧的 marker 值比稳态大一个量级。
        /// 不丢的话最小值估计器也救不了 —— 因为我们要的恰恰是最小值，
        /// 而首帧的**大**值不会污染 min；真正会污染 min 的是"某个 pass 首帧还没被加进图里"
        /// 而产生的 0 或缺样本。所以丢首帧的目的是保证样本集完整，不是去噪。
        const int k_WarmupFrames = 45;
        const int k_SampleFrames = 300;

        /// 容量取到 > 采样帧数，这样整段采样都在环里，不必中途读。
        const int k_Capacity = 512;

        /// 与 <c>VistaAtmospherePass.cs</c> 里的 pass 名**逐字**一致。
        /// 这里刻意硬编一份字符串而不是从 Runtime 侧导出常量：pass 名是给人看的调试标签，
        /// 把它提成 API 会让"改名"变成破坏性改动。代价是这份表会走歧 ——
        /// 所以下面把"marker 一个都没抓到"判成**失败**而不是"这次没数据"。
        static readonly string[] k_PassNames =
        {
            "Vista Transmittance LUT",
            "Vista Multi-Scattering LUT",
            "Vista Sky-View LUT",
            "Vista Sky Ambient SH",
            "Vista Sky Reflection",
            "Vista Sky Reflection Copy",
            "Vista Aerial Perspective LUT",
            "Vista Aerial Perspective Composite",
        };

        /// 稳态五 pass（不含两张静态表、不含合成）在 k_PassNames 里的下标。
        static readonly int[] k_SteadyIdx = { 2, 3, 4, 5, 6 };

        /// AP 合成（全屏光栅）的下标。它**必须**自己一档，不能混进上面那五个，
        /// 理由是两条，都是硬的：
        ///
        /// ① **不能除以出现次数。** 那一步的前提写在下面的报告里 ——「LUT 尺寸与相机
        ///    分辨率无关，所以两次渲染做的是同样的工作量」。全屏光栅 pass 恰好违反它：
        ///    Scene View 与 Game View 分辨率不同，成本也就不同，把两者之和除以 2
        ///    得到的是一个**不对应任何一次真实渲染**的数。所以这一行只报原始和 + 出现次数。
        ///
        /// ② **不能进「稳态五 pass 之和」。** 那个和是拿去和模型 B 的 0.170~0.198 ms
        ///    对账的，而那个区间是五个 LUT compute 的口径。把一个随分辨率变化的项
        ///    加进去，和会随 Game View 大小漂移，而对账区间不会 —— 对账从此失效。
        static readonly int[] k_CompositeIdx = { 7 };

        /// 两张静态表的下标。这两个 pass 在稳态里**应该一个样本都没有** ——
        /// 它们只在大气参数变化时重算，太阳不动的 300 帧里根本不该进图。
        /// 第一版把"没样本"一律判成失败，于是这两行报了"pass 名走歧 / 被剪"，
        /// 而实际上那是缓存正常工作的证据。反过来才是 bug：
        /// 这两行**有**样本 = 静态表在每帧重算，画面完全正常、只是白烧 0.044 ms，
        /// 这种退化只有计时器能抓到。所以这里把方向掉过来判。
        static readonly int[] k_StaticIdx = { 0, 1 };

        static ProfilerRecorder[] s_Gpu;
        static ProfilerRecorder[] s_Cpu;
        static int s_StartFrame;
        static bool s_Running;
        static bool s_Started;

        /// <summary>
        /// 无帧进展的看门狗。这一段是一次实测事故换来的：<c>runInBackground</c> 关着的时候，
        /// Editor 一失焦 play 循环就停走，<c>Time.frameCount</c> 冻住，
        /// 于是 Tick 永远等不到采样帧数 —— 表现是**菜单执行完什么都不打印，一直挂着**，
        /// 而控制台还被 Clear on Play 清空了，连"武装成功"那条都看不见。
        /// 静默挂死是最坏的失败模式：它和"还在跑"、"pass 名走歧"长得一模一样。
        /// 所以这里宁可超时中止并把成因说出来。
        /// </summary>
        const double k_NoProgressAbortSec = 15.0;
        static int s_LastFrame;
        static System.Diagnostics.Stopwatch s_NoProgress;

        /// <summary>
        /// 进 play 时把 <c>Application.runInBackground</c> 打开、退出时还原。
        ///
        /// 动的是**运行期属性**，不是 <c>PlayerSettings.runInBackground</c> ——
        /// 后者会写进 ProjectSettings.asset，那就成了"量性能的工具改了工程配置"，
        /// 是这类 harness 最容易犯的错（本类的类注释里已经为场景立过这条规矩）。
        /// 运行期属性只作用于当前这次 play 会话，退出即还原，磁盘上不留痕。
        /// </summary>
        static bool s_RunInBackgroundSaved;

        [MenuItem("Window/Vista/Cross-Check Atmosphere Timing (Play Mode)")]
        static void Arm()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[Vista] 已经在 play 模式里了。先停止，再执行这个菜单。");
                return;
            }

            SessionState.SetBool(k_ArmedKey, true);

            // 把 Game 视图拉到前面。若 Scene 与 Game 停靠在同一个区域，这一步会让
            // Scene 视图停止渲染，于是每帧只有一个相机 —— 那样得到的是**单次渲染**耗时，
            // 不需要事后按出现次数去除。停靠布局不同的话这一步无害地不生效，
            // 报告里的除法分支会接住（并把假设写出来）。
            EditorApplication.ExecuteMenuItem("Window/General/Game");

            Debug.Log($"[Vista] 交叉验证已武装：进 play 模式，丢弃前 {k_WarmupFrames} 帧，"
                    + $"采样 {k_SampleFrames} 帧后自动退出并打报告。");
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        static void Hook()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                if (!SessionState.GetBool(k_ArmedKey, false)) return;
                SessionState.SetBool(k_ArmedKey, false);   // 一次性：手动再进 play 不会莫名开始采样

                s_StartFrame = Time.frameCount;
                s_Running = true;
                s_Started = false;
                s_LastFrame = int.MinValue;
                s_NoProgress = System.Diagnostics.Stopwatch.StartNew();
                s_RunInBackgroundSaved = Application.runInBackground;
                Application.runInBackground = true;
                EditorApplication.update -= Tick;
                EditorApplication.update += Tick;
            }
            else if (change == PlayModeStateChange.ExitingPlayMode && s_Running)
            {
                // 用户手动停了 play。此时报告已经不可信（采样帧数不足），
                // 所以只清理并说明，**不打半份数字** —— 半份数字会被当成结论用。
                Debug.LogWarning("[Vista] 交叉验证被手动中断，采样帧数不足，未出报告。");
                Cleanup();
            }
        }

        static void Tick()
        {
            if (!s_Running) return;

            // 看门狗：只看帧号有没有动，不看时间过了多久 —— 后者会把"机器慢"
            // 误判成"卡死"。帧号一动就重置，所以正常运行永远不会触发。
            int frame = Time.frameCount;
            if (frame != s_LastFrame)
            {
                s_LastFrame = frame;
                s_NoProgress.Restart();
            }
            else if (s_NoProgress.Elapsed.TotalSeconds > k_NoProgressAbortSec)
            {
                Debug.LogWarning($"[Vista] play 循环停走了：帧号 {frame} 已经 "
                    + $"{k_NoProgressAbortSec:F0} s 没动，交叉验证中止（未出报告）。"
                    + "成因通常是 Editor 失焦 + Run In Background 关着 —— "
                    + "本工具已在 play 期间打开运行期的 Application.runInBackground，"
                    + "所以还撞到这条的话要查是不是被暂停了（Pause 按钮 / 断点）。");
                Cleanup();
                EditorApplication.ExitPlaymode();
                return;
            }

            int elapsed = frame - s_StartFrame;

            if (!s_Started)
            {
                if (elapsed < k_WarmupFrames) return;

                // 预热之后才起 recorder：这样环里的每一个样本都属于稳态，
                // 不需要在读的时候按下标去掐掉前面几帧（那种掐法一改帧数就错）。
                s_Gpu = new ProfilerRecorder[k_PassNames.Length];
                s_Cpu = new ProfilerRecorder[k_PassNames.Length];
                try
                {
                    for (int i = 0; i < k_PassNames.Length; ++i)
                    {
                        // 三个 flag 都是必需的，各有各的理由：
                        //
                        // SumAllSamplesInFrame —— **不是可选项**。RenderGraph 的 pass 名是
                        //   ProfilerMarker，而 ProfilerRecorder 对 marker 只支持
                        //   SumAllSamplesInFrame 或 CollectOnlyOnCurrentThread 两种收集方式，
                        //   否则 StartNew 直接抛 NotSupportedException（我第一版两个都没给，
                        //   于是每个 Editor tick 抛一次）。CollectOnlyOnCurrentThread 在这里
                        //   没用：这些 marker 记在渲染线程上，从主线程按"当前线程"收集会是空的。
                        //   于是每个样本 = 一帧，Value = 该帧同名 marker 的**总和**。
                        //
                        // WrapAroundWhenCapacityReached —— 不写的话装满 capacity 就停止收集，
                        //   而我们要的是"最后 N 帧"。（名字不是 WrapAroundBuffer，那是我凭印象
                        //   写的，编译期 CS0117；枚举的真实成员表用 unity_reflect 查的。）
                        //
                        // GpuRecorder —— 只在 GPU 那一份上加。
                        //
                        // 「每帧出现几次」这个诊断没有丢：ProfilerRecorderSample 除了 Value
                        // 还有 Count，也就是这一帧被求和的样本个数。所以不需要再起第二个
                        // recorder 去做 sum/single 的比值 —— 那是我原来的写法，
                        // 一个字段就解决的事不该用两倍的采样开销换。
                        s_Gpu[i] = ProfilerRecorder.StartNew(
                            ProfilerCategory.Render, k_PassNames[i], k_Capacity,
                            ProfilerRecorderOptions.GpuRecorder
                            | ProfilerRecorderOptions.SumAllSamplesInFrame
                            | ProfilerRecorderOptions.WrapAroundWhenCapacityReached);

                        // CPU 侧同名 marker：GPU recorder 在某些后端 / Editor 配置下取不到值，
                        // 那时至少还能说出"marker 存在、图里跑了这个 pass"，
                        // 把"工具没抓到"与"pass 没跑"这两种失败分开。
                        s_Cpu[i] = ProfilerRecorder.StartNew(
                            ProfilerCategory.Render, k_PassNames[i], k_Capacity,
                            ProfilerRecorderOptions.SumAllSamplesInFrame
                            | ProfilerRecorderOptions.WrapAroundWhenCapacityReached);
                    }
                }
                catch (System.Exception e)
                {
                    // 立即收摊并退出 play：起 recorder 失败是**配置错**，重试不会好。
                    // 不 fail-fast 的代价我已经付过一次 —— 上一版在这里抛异常，
                    // 每个 Editor tick 重试一次，控制台被同一条填满，
                    // 而真正的信息（第一条的类型与行号）被埋在几十条重复里。
                    Debug.LogError("[Vista] 起 ProfilerRecorder 失败，交叉验证中止：" + e.Message);
                    Cleanup();
                    EditorApplication.ExitPlaymode();
                    return;
                }

                s_Started = true;
                return;
            }

            if (elapsed < k_WarmupFrames + k_SampleFrames) return;

            Report(elapsed);
            Cleanup();
            EditorApplication.ExitPlaymode();
        }

        struct Stat
        {
            public int count;                 // 有效帧数
            public double minMs, medMs, maxMs;
            public double occMin, occMax;     // 每帧同名 marker 的出现次数（Sample.Count）
            public bool valid;
        }

        static Stat Read(ProfilerRecorder r)
        {
            var s = new Stat { valid = r.Valid };
            if (!r.Valid) return s;

            int n = r.Count;
            if (n <= 0) return s;

            var samples = new List<ProfilerRecorderSample>(n);
            r.CopyTo(samples, false);

            var ms = new List<double>(samples.Count);
            s.occMin = double.MaxValue;
            for (int i = 0; i < samples.Count; ++i)
            {
                // 时间类 marker 的 Value 是纳秒；开了 SumAllSamplesInFrame 之后
                // 一个样本 = 一帧，Value 是该帧的总和，Count 是被求和的样本个数。
                double v = samples[i].Value * 1e-6;
                if (v <= 0.0) continue;        // 0 = 那一帧没有样本，不能当成"0 ms"
                ms.Add(v);
                double occ = samples[i].Count;
                s.occMin = System.Math.Min(s.occMin, occ);
                s.occMax = System.Math.Max(s.occMax, occ);
            }
            if (ms.Count == 0) { s.occMin = 0.0; return s; }

            ms.Sort();
            s.count = ms.Count;
            s.minMs = ms[0];
            s.medMs = ms[ms.Count / 2];
            s.maxMs = ms[ms.Count - 1];
            return s;
        }

        static void Report(int elapsedFrames)
        {
            var sb = new StringBuilder();
            sb.AppendLine("── 大气逐 pass 耗时（模型 A：Play 模式 ProfilerRecorder，帧内延迟）");
            sb.Append("　 GPU ").Append(SystemInfo.graphicsDeviceName)
              .Append("　后端 ").Append(SystemInfo.graphicsDeviceType)
              .Append("　场景 ").AppendLine(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            sb.Append("　 采样 ").Append(k_SampleFrames).Append(" 帧（丢弃预热 ")
              .Append(k_WarmupFrames).Append("，实际经过 ").Append(elapsedFrames).AppendLine(" 帧）");
            sb.Append("　 本次 play 期间强制 Application.runInBackground=true（原值 ")
              .Append(s_RunInBackgroundSaved ? "true" : "false")
              .AppendLine("，退出即还原，不写 ProjectSettings）—— 失焦时 play 循环不停走，否则采样会卡死");

            int gpuUsable = 0, missing = 0, occUnstable = 0, staticLeak = 0;
            double steadyRawMin = 0.0, steadyPerRenderMin = 0.0, steadyPerRenderMed = 0.0;
            bool steadyComplete = true;
            double occAll = 0.0;          // 稳态五 pass 共同的出现次数（不一致就置 0）
            bool occAgreed = true;

            // 合成 pass 单独留一份：它的对账口径与 LUT 那五个不同（见 k_CompositeIdx）。
            Stat composite = default;
            int compositeMissing = 0, compositeCpuOnly = 0, compositeOccUnstable = 0;

            for (int i = 0; i < k_PassNames.Length; ++i)
            {
                Stat g = Read(s_Gpu[i]);
                Stat c = Read(s_Cpu[i]);
                bool isStatic = System.Array.IndexOf(k_StaticIdx, i) >= 0;
                bool isComposite = System.Array.IndexOf(k_CompositeIdx, i) >= 0;

                sb.Append("　　 ").Append(k_PassNames[i].PadRight(34));

                if (isStatic)
                {
                    // 方向掉过来判：静态表在稳态里**没有样本才是对的**。
                    if (g.count == 0 && c.count == 0)
                    {
                        sb.Append("静态表：").Append(k_SampleFrames)
                          .Append(" 帧内 0 个样本　→ 只在参数变化时重算，缓存生效（**这是期望**）");
                    }
                    else
                    {
                        staticLeak++;
                        sb.Append("**静态表每帧在跑**：GPU min ").Append(g.minMs.ToString("F3"))
                          .Append(" ms　帧 ").Append(g.count).Append("/").Append(k_SampleFrames)
                          .Append("　→ 脏标记失效，白烧约 0.044 ms/帧（画面无异常，只有计时器能看见）");
                    }
                    sb.AppendLine();
                    continue;
                }

                if (isComposite)
                {
                    composite = g;
                    if (g.count > 0)
                    {
                        sb.Append("GPU min ").Append(g.minMs.ToString("F3"))
                          .Append("　中位 ").Append(g.medMs.ToString("F3"))
                          .Append("　max ").Append(g.maxMs.ToString("F3"))
                          .Append(" ms　帧 ").Append(g.count).Append("/").Append(k_SampleFrames)
                          .Append("　每帧出现 ").Append(g.occMin.ToString("F0"));
                        if (g.occMax != g.occMin)
                        {
                            sb.Append("~").Append(g.occMax.ToString("F0")).Append(" ⚠不稳定");
                            compositeOccUnstable++;
                        }
                        // 这里**故意不印「单次」**：除法不合法，理由见 k_CompositeIdx ①。
                        sb.Append("　（全屏光栅：不按出现次数相除）");
                    }
                    else if (c.count > 0)
                    {
                        compositeCpuOnly++;
                        sb.Append("GPU 无值（valid=").Append(g.valid).Append("）　但 CPU marker 在：min ")
                          .Append(c.minMs.ToString("F3")).Append(" ms　帧 ").Append(c.count)
                          .Append("　→ pass 跑了，是 GPU recorder 取不到");
                    }
                    else
                    {
                        compositeMissing++;
                        // 两种成因都要写出来，因为这个 harness **分不开**它们：
                        // 不把两种都摆上，读的人会默认是自己知道的那一种。
                        sb.Append("**一个样本都没有**（GPU valid=").Append(g.valid)
                          .Append("，CPU valid=").Append(c.valid).Append("）　→ 二义：")
                          .Append("pass 名走歧 ／ 合成 pass 没进这个 renderer（或已由变体 B 接管）。")
                          .Append("区分办法：Window/Analysis/Profiler 的 Rendering 里搜这个名字，"
                                + "有 marker 就是本表的字符串走歧了");
                    }
                    sb.AppendLine();
                    continue;
                }

                if (g.count > 0)
                {
                    gpuUsable++;
                    sb.Append("GPU min ").Append(g.minMs.ToString("F3"))
                      .Append("　中位 ").Append(g.medMs.ToString("F3"))
                      .Append("　max ").Append(g.maxMs.ToString("F3"))
                      .Append(" ms　帧 ").Append(g.count).Append("/").Append(k_SampleFrames);

                    // 每帧出现次数取自 Sample.Count。开了 SumAllSamplesInFrame 之后
                    // 这一行的值是该帧**所有**同名 marker 之和，最常见的原因是
                    // Scene View 与 Game View 各渲染一次。min/max 都报：两者不等说明
                    // 帧间相机数在变，那连"稳态"这个前提都不成立，除法也就不合法。
                    sb.Append("　每帧出现 ").Append(g.occMin.ToString("F0"));
                    if (g.occMax != g.occMin)
                    {
                        sb.Append("~").Append(g.occMax.ToString("F0")).Append(" ⚠不稳定");
                        occUnstable++;
                        occAgreed = false;
                    }
                    else
                    {
                        if (occAll == 0.0) occAll = g.occMin;
                        else if (occAll != g.occMin) occAgreed = false;

                        if (g.occMin > 1.0)
                            sb.Append("　单次 ").Append((g.minMs / g.occMin).ToString("F3")).Append(" ms");
                    }

                    steadyRawMin += g.minMs;
                    double div = (g.occMin == g.occMax && g.occMin >= 1.0) ? g.occMin : 1.0;
                    steadyPerRenderMin += g.minMs / div;
                    steadyPerRenderMed += g.medMs / div;
                }
                else if (c.count > 0)
                {
                    steadyComplete = false;
                    sb.Append("GPU 无值（valid=").Append(g.valid).Append("）　但 CPU marker 在：min ")
                      .Append(c.minMs.ToString("F3")).Append(" ms　帧 ").Append(c.count)
                      .Append("　每帧出现 ").Append(c.occMin.ToString("F0"))
                      .Append("　→ pass 跑了，是 GPU recorder 取不到");
                }
                else
                {
                    missing++;
                    steadyComplete = false;
                    sb.Append("**一个样本都没有**（GPU valid=").Append(g.valid)
                      .Append("，CPU valid=").Append(c.valid)
                      .Append("）　→ 稳态 pass 每帧都该在图里：pass 名走歧或 pass 被剪");
                }
                sb.AppendLine();
            }

            sb.Append("　 稳态五 pass 之和　原始 ").Append(steadyRawMin.ToString("F3")).Append(" ms");
            if (occAgreed && occAll > 1.0)
                sb.Append("（").Append(occAll.ToString("F0")).Append(" 次渲染之和）　单次 min ")
                  .Append(steadyPerRenderMin.ToString("F3")).Append("　单次中位 ")
                  .Append(steadyPerRenderMed.ToString("F3")).Append(" ms");
            else
                sb.Append("　单次 min ").Append(steadyPerRenderMin.ToString("F3")).Append(" ms");
            sb.AppendLine(steadyComplete ? "" : "　⚠ 有 pass 缺样本，这个和不完整");

            if (occAgreed && occAll > 1.0)
                sb.AppendLine("　 除以出现次数的**前提**：LUT 尺寸与相机分辨率无关（256×64 / 32×32 / "
                            + "192×108 / 64²×7 / 32³ 全是定值），每帧只有一份大气参数，"
                            + "所以两次渲染做的是同样的工作量。若两个相机的工作量不同，这一步不合法。");

            // ---- 与模型 B 对账 ----
            // 只判**方向**，不判差值：两个模型量的不是同一件事，差多少没有先验。
            // 能判的是"A 不应该比 B 小"—— 帧内延迟不可能低于允许重叠的吞吐下界。
            sb.AppendLine("　 与模型 B（Edit 模式摊销，稳态五 pass 0.170~0.198 ms）对账");
            if (!steadyComplete || steadyPerRenderMin <= 0.0)
            {
                sb.AppendLine("　　 样本不完整，不做判定。");
            }
            else
            {
                const double kModelBLow = 0.170;
                double ratio = steadyPerRenderMin / kModelBLow;
                sb.Append("　　 A(单次)/B = ").Append(ratio.ToString("F2")).Append("　→ ");
                sb.AppendLine(ratio >= 1.0
                    ? "A ≥ B，符合预期：差额就是 barrier / pass 边界 / 无重叠这三样的代价"
                    : "**A < B，方向反了** —— 这指向 marker 归属而不是性能事实（被合并 / 被剪 / 相机数不同）");
            }

            // ---- AP 合成与模型 B 的**包线**对账 ----
            // 为什么是包线而不是一个数：模型 B 给的是 ms = a + b·覆盖面积，
            // 而覆盖面积（非天空像素占比）取决于**构图**——模型 A 跑在真实场景上，
            // 那个占比不由测量方控制，也无法从 marker 里反推。
            // 所以能对的只有两端：全天空 = a，满覆盖 = a + b·全屏面积。
            //
            // 每帧出现多次时**不做除法**（理由见 k_CompositeIdx ①），改成
            // **把每次渲染的面积逐个点名加起来**再套包线 —— 模型对面积是线性的，
            // 所以「两次渲染之和」的包线就是两个包线之和，不需要任何等工作量假设。
            bool compositeDirOk = true, compositeJudged = false;
            sb.AppendLine("　 AP 合成与模型 B 对账（模型 B 给的是线：ms = a + b·覆盖面积，故只能对包线）");
            {
                var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                        as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                int msaa = urp != null ? urp.msaaSampleCount : 1;
                bool hdr = urp == null || urp.supportsHDR;

                // renderScale 只对 game 相机生效，且与 1 相差 < 0.05 时被吸成 1
                // （URP UniversalRenderPipeline.cs:1456-1460 的 kRenderScaleThreshold /
                //  isScenePreviewOrReflectionCamera 两条）。Scene View 一律 1.0 ——
                // 这不是我推测的，是照着那段代码写的，否则缩放会被错算两次。
                float raw = urp != null ? urp.renderScale : 1f;
                float scale = Mathf.Abs(1f - raw) < 0.05f ? 1f : raw;

                var bd = new StringBuilder();
                double mpxSum = 0.0;
                int nTargets = 0;

                int gw = Mathf.Max(1, Mathf.RoundToInt(Screen.width * scale));
                int gh = Mathf.Max(1, Mathf.RoundToInt(Screen.height * scale));
                mpxSum += gw * (double)gh / 1e6;
                nTargets++;
                bd.Append("Game ").Append(gw).Append("×").Append(gh);

                var views = SceneView.sceneViews;
                for (int v = 0; views != null && v < views.Count; ++v)
                {
                    var sv = views[v] as SceneView;
                    if (sv == null || sv.camera == null) continue;
                    int sw = sv.camera.pixelWidth, sh = sv.camera.pixelHeight;
                    if (sw <= 0 || sh <= 0) continue;
                    mpxSum += sw * (double)sh / 1e6;
                    nTargets++;
                    bd.Append(" + Scene ").Append(sw).Append("×").Append(sh);
                }

                double floorMs, ceilMs;
                VistaApCompositePerf.Bracket(mpxSum, out floorMs, out ceilMs);

                sb.Append("　　 渲染目标 ").Append(bd)
                  .Append("　合计 ").Append(mpxSum.ToString("F3")).Append(" Mpx")
                  .Append("　renderScale ").Append(scale.ToString("F2"))
                  .Append("（仅 Game）　MSAA ").Append(msaa).Append("×　HDR ")
                  .AppendLine(hdr ? "开" : "关");
                sb.Append("　　 包线 [").Append(floorMs.ToString("F3")).Append(", ")
                  .Append(ceilMs.ToString("F3"))
                  .Append("] ms＝[全天空构图, 满覆盖构图]，系数 a/全屏=")
                  .Append(VistaApCompositePerf.k_ClippedMsPerMpx.ToString("F4"))
                  .Append("　b=").Append(VistaApCompositePerf.k_CoveredMsPerMpx.ToString("F4"))
                  .AppendLine(" ms/Mpx（本机实测，换 GPU 要重测）");

                // 两端各自的前提**分别**失效，所以分别判、分别说，不合成一个「前提不成立」
                // 一票否决 —— 那样会把唯一还成立的那一端也挡掉。
                if (!hdr)
                    sb.AppendLine("　　 ⚠ HDR 关：颜色附件比模型 B 的 RGBA16F 窄，实际更便宜，"
                                + "**下界判定不成立**（上界仍成立）。");
                if (msaa > 1)
                    sb.AppendLine("　　 ⚠ MSAA " + msaa + "×：逐样本混合比模型 B 的 1× 更贵，"
                                + "**上界失效**（下界仍成立，只会更宽松）。");

                if (composite.count == 0)
                {
                    sb.AppendLine("　　 无 GPU 样本，不做判定（成因见上面那一行的二义）。");
                }
                else if (compositeOccUnstable > 0)
                {
                    sb.AppendLine("　　 **不可判定**：出现次数帧间在变，连「这一帧渲染了几次」都不固定。");
                }
                else if ((int)composite.occMin != nTargets)
                {
                    // 点名对不上就认输，**不猜**。少了 = 有视图没在渲染（被 tab 遮住），
                    // 多了 = 还有别的相机（第二个 game 相机 / 预览窗口）。
                    // 两种都会让面积和错，而错的面积和会给出一个看起来很像结论的数。
                    sb.Append("　　 **不可判定**：marker 每帧出现 ").Append(composite.occMin.ToString("F0"))
                      .Append(" 次，而点名点到 ").Append(nTargets)
                      .AppendLine(" 个渲染目标，对不上。少了＝某个视图被遮住没渲染；"
                                + "多了＝还有没点到的相机（第二个 game 相机 / 材质预览）。"
                                + "关掉多余视图或让 Scene 视图停止渲染后重跑。");
                }
                else if (!hdr)
                {
                    sb.AppendLine("　　 下界前提不成立（HDR 关），仅报数：min "
                                + composite.minMs.ToString("F3") + " ms。");
                }
                else
                {
                    compositeJudged = true;
                    bool aboveFloor = composite.minMs >= floorMs;
                    sb.Append("　　 A(").Append(nTargets).Append(" 次渲染之和) min ")
                      .Append(composite.minMs.ToString("F3")).Append(" ms　→ ");
                    if (aboveFloor)
                    {
                        sb.AppendLine("≥ 全天空下界 " + floorMs.ToString("F3")
                                    + " ms，方向符合预期（帧内延迟 ≥ 允许重叠的吞吐下界）");
                    }
                    else
                    {
                        compositeDirOk = false;
                        sb.AppendLine("**低于全天空下界 " + floorMs.ToString("F3")
                                    + " ms** —— 帧内延迟不可能低于吞吐下界，所以这指向工具而非性能："
                                    + "marker 被合并/被剪，或实际渲染分辨率不是上面点名的那些"
                                    + "（动态分辨率、渲染缩放被 Volume 覆盖）");
                    }

                    // 下界都没过就**不再谈落点**。故障注入（把 floor ×10）时这里原本照样打
                    // 「落在包线内（占上界 66%）」，和上一行的「低于全天空下界」直接自相矛盾 ——
                    // 真出故障时这段话会替故障背书。所以位置描述必须挂在下界判定之后。
                    if (!aboveFloor)
                    {
                        sb.AppendLine("　　 下界未过，落点位置不再解读：包线本身已经不可信"
                                    + "（要么面积点名错了，要么 marker 不是这个 pass 的）。");
                    }
                    else if (composite.minMs > ceilMs && msaa <= 1)
                    {
                        // 这一侧**可以**归因，而且只有这一侧可以：覆盖率只能把值往下拉，
                        // 所以超过满覆盖上界的部分不可能是构图造成的。
                        sb.Append("　　 超出满覆盖上界 ").Append((composite.minMs - ceilMs).ToString("F3"))
                          .AppendLine(" ms —— 覆盖率只能让值更低，所以这个超出量是"
                                    + " barrier / pass 边界 / 无重叠 三样代价之和的**下界**。");
                    }
                    else
                    {
                        sb.Append("　　 落在包线内（占上界 ")
                          .Append(ceilMs > 1e-9 ? (composite.minMs / ceilMs).ToString("P0") : "——")
                          .AppendLine("）。此时**不能**把它拆成「天空占比」与「barrier 加价」两项："
                                    + "两者都未知，且对同一个数的贡献符号相反（天空多→更低，"
                                    + "barrier→更高），会互相掩盖。要单独拿到 barrier 加价，"
                                    + "得让构图覆盖率可控 —— 那是模型 B 的活，不是场景 harness 的。");
                    }
                }
            }

            sb.AppendLine("── 引用这些数字时必须一起给");
            sb.AppendLine("　 1) RenderGraph 的逐 pass marker 被 #if DEVELOPMENT_BUILD || UNITY_EDITOR 包着"
                        + "（core RenderGraph.cs:2868-2884）。**Release 构建里没有这些 marker**，"
                        + "所以这套数字是 Editor 口径，不能当发行版性能。");
            sb.AppendLine("　 2) 逐帧取最小值：噪声单向（争用只会加时间）。同时报中位与 max，"
                        + "min 与中位差得多说明帧间抖动大，那时 min 描述的是「最好情况」而不是典型帧。");
            sb.AppendLine("　 3) 「每帧出现」> 1 时，行首的值是同一帧里多次渲染（通常 Scene View + "
                        + "Game View）的**总和**；「单次」是按出现次数除出来的，前提见上。"
                        + "要拿到无需假设的数字，让 Scene 视图停止渲染（与 Game 停靠同区并置前）再量。");
            sb.AppendLine("　 4) AP 合成那一行**没有「单次」**，也不进上面那个和：它是全屏光栅，"
                        + "成本随分辨率变，两个视图做的不是同样的工作量，除法与求和都不合法。");

            string flat = sb.ToString().Replace("\r", "").Replace("\n", "  |  ");
            // compositeJudged 也进这个门：上一版没进，于是「不可判定」照样打
            // 「交叉验证完成」—— 一条未覆盖的路径被读成通过，正是本项目记过的那个反模式。
            // 不可判定必须是缺口，不能是通过。
            bool ok = missing == 0 && occUnstable == 0 && staticLeak == 0
                   && gpuUsable == k_SteadyIdx.Length && steadyComplete
                   && compositeMissing == 0 && compositeCpuOnly == 0
                   && compositeOccUnstable == 0 && compositeJudged && compositeDirOk;
            if (ok) Debug.Log("[Vista] 模型 A 交叉验证完成  |  " + flat);
            else Debug.LogWarning($"[Vista] 模型 A 交叉验证有缺口（稳态缺样本 {missing}，GPU 可用 "
                                + $"{gpuUsable}/{k_SteadyIdx.Length}，出现次数不稳 {occUnstable}，"
                                + $"静态表泄漏 {staticLeak}；合成缺样本 {compositeMissing}，"
                                + $"合成仅 CPU {compositeCpuOnly}，合成出现次数不稳 {compositeOccUnstable}，"
                                + $"合成包线{(compositeJudged ? (compositeDirOk ? "通过" : "**方向反了**") : "**未判定**")}）"
                                + "  |  " + flat);
        }

        static void Cleanup()
        {
            s_Running = false;
            s_Started = false;
            Application.runInBackground = s_RunInBackgroundSaved;
            s_NoProgress = null;
            EditorApplication.update -= Tick;
            Dispose(ref s_Gpu);
            Dispose(ref s_Cpu);
        }

        static void Dispose(ref ProfilerRecorder[] arr)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Length; ++i)
                if (arr[i].Valid) arr[i].Dispose();
            arr = null;
        }
    }
}
