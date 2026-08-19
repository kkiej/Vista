using UnityEngine;
using UnityEngine.Rendering;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Vista.Editor
{
    /// <summary>
    /// Edit 模式的 GPU 计时核（本项目里叫「模型 B」）：把被测工作背靠背录 N 次、
    /// 硬同步一次、扣掉空 CommandBuffer 的基线、再除以 N。
    ///
    /// 这个文件是从 <see cref="VistaAtmosphereLutProfiler"/> 里**提出来**的，不是新写的。
    /// 提出来的理由很具体：AP 合成的性能项（Task #15）要用同一把尺子去量一个
    /// **光栅** pass，而计时纪律里的每一条都是事故换来的 ——
    /// fence 能力位不可信、等待不能带超时、必须取多轮最小值、
    /// 负值不能 clamp。复制一份的话这两份纪律会走歧，
    /// 而走歧的表现是「两份报告的数字对不上」，没人能一眼看出是哪份错。
    ///
    /// ── 口径的适用边界 ──
    ///
    /// 这套口径量的是**吞吐**：相邻的 dispatch / draw 在 GPU 上允许重叠，
    /// 所以背靠背 N 次的平均值是单次成本的**下界**，不是帧内延迟。
    /// 帧内延迟（含 barrier、pass 边界、资源状态转换）只有
    /// <see cref="VistaLutGpuRecorderCrossCheck"/>（模型 A，Play 模式 ProfilerRecorder）能给。
    /// 两者的关系是 A ≥ B，差额就是那三样的代价。
    ///
    /// 对光栅 pass 还多一条：同一张颜色附件上背靠背 N 次混合，ROP 对同一像素是串行的，
    /// 所以重叠空间比 compute 小 —— 这让 B 对光栅 pass 反而比对 compute 更接近真值。
    /// 但**它同时也意味着 N 次重复不是无关的**：第 k 次读到的是第 k−1 次写出的颜色，
    /// 值会一路演化（乘 T^k），可能滑进非规格化区间而改变 ROP 的实际耗时。
    /// 用它量光栅 pass 时必须让被测 pass 的输出**不影响下一次的输入代价**，
    /// 或者在每次重复之间重新 clear —— 而 clear 本身要计入基线。
    /// </summary>
    static class VistaGpuTimer
    {
        // ==================================================================
        //  参数
        // ==================================================================

        /// <summary>
        /// 默认摊销次数。选 200 的依据：稳态五 pass 单次亚毫秒，200 次几十到几百毫秒 GPU ——
        /// 足以把提交+同步的固定开销（实测亚毫秒）摊到小数点后三位以下，
        /// 又短得可以在一个菜单项里跑几十次测量而不让 Editor 卡住。
        /// 量更贵的工作（比如 1080p 以上的全屏 pass）时应显式传小一点的值：
        /// 200 × 1 ms = 200 ms 一轮，×5 轮 ×多档配置就会让 Editor 明显卡顿。
        /// </summary>
        public const int k_DefaultIterations = 200;

        /// <summary>
        /// 预热次数。首次 dispatch / draw 要付 shader 变体的实际上载、描述符堆分配、
        /// 以及 GPU 时钟从低功耗档爬升 —— 不预热的话第一项测量会明显偏高，
        /// 而"偏高的那一项"取决于菜单项里的调用顺序，是最容易被误读成真实开销的假象。
        /// </summary>
        public const int k_DefaultWarmup = 20;

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
        public const int k_DefaultTrials = 5;

        /// <summary>
        /// fence 探测预算。见 <see cref="ProbeFence"/> —— 这个常量存在的理由是
        /// 一次实测事故，不是防御性编程。
        /// </summary>
        const double k_FenceProbeSec = 0.25;

        // ==================================================================
        //  同步原语
        // ==================================================================

        public enum SyncMode
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

        /// <summary>
        /// 开一次计时会话。必须与 <see cref="End"/> 成对，且 End 要放在 finally 里 ——
        /// 释放正在被 GPU 读的资源在 D3D12/Vulkan 上是未定义行为，
        /// 所以 End 之前每条测量路径都已经硬同步过了。
        /// </summary>
        public static void Begin()
        {
            End();
            s_Sync = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float))
            {
                name = "VistaGpuTimerSync",
            };
        }

        public static void End()
        {
            s_Sync?.Dispose();
            s_Sync = null;
        }

        /// <summary>Begin 之外调用任何测量都是编程错误，直接说出来而不是让它 NRE。</summary>
        static void RequireSession()
        {
            if (s_Sync == null)
                throw new System.InvalidOperationException(
                    "VistaGpuTimer：先调 Begin() 再测量（End() 要放在 finally 里）。");
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
        public static bool ProbeFence()
        {
            RequireSession();

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

        // ==================================================================
        //  测量
        // ==================================================================

        /// <summary>
        /// 一项测量的结果。<c>min</c> 是要引用的那个数（理由见 <see cref="k_DefaultTrials"/>），
        /// 后者只用来判"这个数字值不值得引用"。
        /// </summary>
        public readonly struct Sample
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
            public static Sample Of(SyncMode mode, System.Action<CommandBuffer> record,
                                   int reps = k_DefaultIterations)
                => Collect(mode, record, 0.0, reps);

            /// <summary>扣掉基线并除以 N 的每次开销。</summary>
            public static Sample Amortized(SyncMode mode, System.Action<CommandBuffer> record,
                                           double baselineMs, int reps = k_DefaultIterations)
                => Collect(mode, record, baselineMs, reps);

            static Sample Collect(SyncMode mode, System.Action<CommandBuffer> record,
                                  double baselineMs, int reps)
            {
                // baselineMs 可以是 0（测基线本身），但**不能是负数**：那意味着调用方
                // 把一个已经落到噪声以下的 Sample 当基线传进来了，
                // 而 amortize 分支会因此把被测量抬高，看起来像"这个 pass 变贵了"。
                bool amortize = baselineMs > 0.0;
                RawMs(record, k_DefaultWarmup, mode);   // 预热一次就够，M 轮之间不必重复
                double lo = double.MaxValue, hi = double.MinValue;
                for (int t = 0; t < k_DefaultTrials; ++t)
                {
                    double raw = RawMs(record, reps, mode);
                    // 可能为负（固定开销本身有抖动，而最便宜的 pass 比抖动还小）。
                    // 不 clamp 到 0：负值是"这一项已经落在测量噪声以下"的信号，
                    // 抹平成 0 反而看不出来。
                    double v = amortize ? (raw - baselineMs) / reps : raw;
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                return new Sample(lo, hi);
            }
        }

        /// <summary>
        /// 录 N 次 + 一次硬同步的裸耗时。
        ///
        /// 一条 buffer 录 N 遍而不是 N 次提交+同步：后者量到的几乎全是提交与同步的
        /// 往返延迟（每次百微秒到毫秒量级），被测的 dispatch / draw 反而淹没在里面。
        ///
        /// buffer 的**录制**在计时窗口之外 —— 那是 CPU 成本，与被测的 GPU 工作无关。
        /// </summary>
        public static double RawMs(System.Action<CommandBuffer> record, int reps, SyncMode mode)
        {
            RequireSession();

            var cmd = new CommandBuffer { name = "Vista GPU Timer" };
            for (int i = 0; i < reps; ++i)
                record(cmd);

            GraphicsFence fence = default;
            if (mode == SyncMode.Fence)
            {
                // CPUSynchronisation 而不是 AsyncQueueSynchronisation：只有前者能用
                // fence.passed 在 CPU 上轮询。
                // 阶段给 AllGPUOperations 而不是 ComputeProcessing：这个计时核现在
                // 也要量光栅 pass，只等计算阶段会让 draw 还没跑完就判 passed。
                // 对纯 compute 的那批测量没有影响（它们的最后一个阶段就是计算）。
                //
                // 注意：**这一行在本机上未被执行过**。RTX 3060 / D3D11 上 ProbeFence
                // 一直不通过，整场退到 Readback，而 GetData 本身就等全部 GPU 工作，
                // 光栅也在内。所以改动的正确性目前只有推理支持，没有实测支持 ——
                // 哪天在 fence 可用的机器上跑，要重新核一遍光栅 pass 的数字。
                fence = cmd.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation,
                                                SynchronisationStageFlags.AllGPUOperations);
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

            // fence 路径补一次硬同步，在计时窗口之外：调用方在 finally 里要 Dispose
            // RTHandle 与 buffer，而释放正在被 GPU 读的资源在 D3D12/Vulkan 上是未定义行为。
            if (mode == SyncMode.Fence)
                s_Sync.GetData(s_SyncData);

            return ms;
        }
    }
}
