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
    /// 给出的数字。覆盖两张静态表 + 稳态五个 LUT pass + AP 合成（全屏光栅）
    /// + 近层 froxel 的两趟出货 pass + 五趟只该在被请求时出现的自检/诊断 pass。
    ///
    /// **注意 froxel 那两趟没有第二个模型可对**：模型 B 只覆盖了大气的七张表与 AP 合成。
    /// 所以下面 froxel 一节报的是**单模型数字**，它回答"多少毫秒"，回答不了"这个数对不对"。
    /// 把它和上面两节混在一起看，会让对过账的数给没对过账的数背书。
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
    ///    **一处例外**，见 <see cref="k_NameProbeView"/>：预热期会把
    ///    <c>debugView</c> 翻开一瞬再翻回去，那个字段序列化在 RendererFeature 资产上。
    ///    这是整个工具唯一碰项目资产的动作，代价与换来的东西都写在那里。
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

        /// 与 <c>VistaAtmospherePass.cs</c> / <c>VistaFroxelDebugPass.cs</c> 里的 pass 名
        /// **逐字**一致。这里刻意硬编一份字符串而不是从 Runtime 侧导出常量：
        /// pass 名是给人看的调试标签，把它提成 API 会让"改名"变成破坏性改动。
        ///
        /// 代价是这份表会走歧。对**每帧都跑**的 pass，兜底是下面那条
        /// 「marker 一个都没抓到 = 失败」。但对期望为 0 的那几个（两张静态表、
        /// 四个探针、调试视图）这条兜底不成立 —— 零样本是它们的合法稳态，
        /// 于是"名字打错了"和"它确实没跑"在报表上长得一模一样，
        /// 那就是一个红不起来的红测。
        ///
        /// 所以补了一个**预热期的名字验证**（见 <see cref="k_NameProbeFrame"/>）：
        /// 主动制造一帧让它们出现，看名字抓不抓得到，再让它们消失。
        /// 名字的正确性从此是一个**测量结果**，不是一句声明。
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
            "Vista Froxel Injection",
            "Vista Froxel Integration",
            "Vista Froxel Shadow Probe",
            "Vista Froxel Reproj Probe",
            "Vista Froxel Jitter Probe",
            "Vista Froxel Local Light Probe",
            "Vista Froxel Debug View",
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

        /// 近层 froxel 的两趟**出货** pass。它们与稳态五 pass 分开的理由有三条：
        ///
        /// ① **没有第二个模型可对。** 模型 B（VistaAtmosphereLutProfiler / ApCompositePerf）
        ///    只覆盖了大气七表与 AP 合成。把 froxel 加进「稳态五 pass 之和」，
        ///    那个和就不再对应 0.170~0.198 ms 这个区间，对账当场失效 ——
        ///    与 k_CompositeIdx ② 是同一条理由。
        ///
        /// ② **期望值依赖配置。** enableInjection 默认关着；关着的时候这两趟零样本是
        ///    **完全正确**的状态。写死"每帧都该在"会在一个合法布景上红 ——
        ///    「一道只看配置、不看运行期剔除的门，会在一个完全合法的布景上红」。
        ///    所以下面先读 feature.volumetricFog.enableInjection 再定期望。
        ///
        /// ③ **成本随屏幕分辨率变**（体的 XY = 屏幕 / screenDivisor），
        ///    与 LUT 那五个的定值口径不同，所以同样不能按出现次数相除。
        static readonly int[] k_FroxelSteadyIdx = { 8, 9 };

        /// 稳态里**应该一个样本都没有**的自检/诊断 pass：四个覆盖性探针 + 调试视图。
        ///
        /// 四个探针全在 <c>VistaAtmospherePass.cs:811</c> 的
        /// <c>if (froxelVolume.probeRequested)</c> 里，而那个标记只有 Editor 自检会置、
        /// 且 pass 自己在 816 行清掉（"一次请求只跑一帧"）。调试视图则由
        /// <c>debugView != Off</c> 把整趟 pass 挡在 EnqueuePass 之前。
        ///
        /// 判定方向与 k_StaticIdx 相同：**有**样本才是 bug。有样本意味着
        /// 诊断开销漏进了每一帧，而画面上完全看不出来。
        static readonly int[] k_AbsentIdx = { 10, 11, 12, 13, 14 };

        /// 名字验证：在这一帧（相对 play 起点）主动把四个探针请求出来、
        /// 并把调试视图翻开，好让那五个"稳态里不该出现"的 marker 各出现一次。
        ///
        /// 取 5 而不是 0：froxelVolume 要等第一帧的 RecordRenderGraph 里 Prepare 过才存在，
        /// 太早置标记会写到一个 null 上（然后静默什么都没发生 —— 那正是这个验证
        /// 要消灭的失效形态）。
        const int k_NameProbeFrame = 5;

        /// 验证窗口关掉的帧。窗口要比 1 宽：探针那一帧与 Editor tick 不是同步的，
        /// 卡一帧就会把"名字对"误判成"名字错"。
        const int k_NameProbeEndFrame = 18;

        /// 主动重烘两张静态表的帧。见 <see cref="TriggerStaticRebake"/>。
        ///
        /// 必须**严格晚于** <see cref="k_NameProbeFrame"/>：重烘会把 m_Luts 整个换掉，
        /// 而四个探针是挂在 m_Luts 的近层体上的。反过来的症状是拿一格换四格。
        /// 也要留足到 k_NameProbeEndFrame 的余量：烘在下一帧发生，收割还要再一个 tick。
        const int k_NameProbeRebakeFrame = 10;

        // ================================================================ 旋钮响应测试
        //
        //  近层 froxel 那两趟（注入/积分）是本报告里**唯一没有第二个模型可对**的数字：
        //  LUT 那五趟能跟模型 B 对账，AP 合成能对一条包线，而 0.354 ms 这个数
        //  只能自己担保自己。一个无法被证伪的读数不是测量，是一个印出来的字符串。
        //
        //  能拿到的唯一独立证据是**因果**：把一个已知会改变工作量的旋钮翻一档，
        //  看这个读数动不动。screenDivisor 减半 ⇒ 体的 XY 各 ×2 ⇒ 线程数 ×4。
        //  这是 UE5 用 r.VolumetricFog.GridPixelSize、HDRP 用 Volumetric Fog 的
        //  screen fraction 做同一件事的那个旋钮。
        //
        //  所以这一节采两次：A 档（出货配置，报告正文用它）→ 翻旋钮 → B 档。
        //  代价是 play 时长翻倍（约 690 帧 ≈ 12 s），换来的是把
        //  「唯一的验证手段是改一个旋钮看它动不动」这句**注释**变成一道会红的门。

        /// 翻的倍数：除数减半。选 2 而不是 4 是因为 divisor 的合法区间是 [2,16]，
        /// 出货档 8 除以 4 得 2 正好压在下界上 —— 压在边界上的测试会在别人把
        /// divisor 调到 4 的那天悄悄失效（4/4=1 被 clamp 回 2，实际只翻了一档）。
        const int k_DivisorTestFactor = 2;

        /// 注入耗时的最小响应比。线程数 ×4，时间比取 1.5 作门。
        ///
        /// 为什么不是 4：×4 是**线程数**比，不是时间比。占用率、缓存命中、
        /// 每趟派发的固定开销都会把它压下来，拿 4 当门是拿一个不存在的模型定阈值。
        /// 为什么是 1.5：这道门要抓的是「读数对被测对象根本没反应」——
        /// 线程数翻两番而时间涨不到一半，说明这个数被某个与 froxel 无关的常数项主导，
        /// 那时它不能被当作「近层雾的每帧成本」引用。
        /// **不设上界**：偏高不代表仪器坏，而我手上没有能定出上界的模型。
        const float k_MinInjectionResponse = 1.5f;

        /// 对照项允许的偏离。对照涨了说明整机状态变了（热降频、编辑器自己在导资源），
        /// 那时测量项的上涨不能归因给旋钮 —— 少了这一项，一次后台编译就能让门通过。
        ///
        /// 0.35 的来源：同一份构建连续两次运行，Sky-View 中位都是 0.155 ms、
        /// Sky Reflection 中位都是 0.352 ms（#27 实测），也就是中位数在同配置下
        /// 复现到 5% 以内。0.35 比它宽一个量级，只挡住「整机状态明显变了」。
        const float k_ControlRatioTolerance = 0.35f;

        /// 对照用哪一趟：Sky-View LUT。它每帧都跑、尺寸固定 192×108，
        /// 与 froxel 的 divisor 没有任何关系。
        /// 不拿静态表当对照：那两趟稳态里是 0 个样本，比值算不出来 ——
        /// 一个恒为「不可判定」的对照等于没有对照。
        const int k_ResponseControlIdx = 2;

        /// D 档（注入关）相对 A 档必须省下的**比例**：近层两趟的耗时至少掉到三成以下。
        ///
        /// 为什么门摆在比例而不是毫秒：毫秒阈值绑死本机（3060）与本场景的构图，
        /// 换台机器就得重标一次，而没人会记得去重标 —— 一条过时的毫秒门要么恒绿要么恒红。
        /// 比例则只要求「关掉它确实不跑了」，与绝对速度无关。
        ///
        /// 为什么是 0.3 而不是 0：关掉 enableInjection 之后那两个 marker 本应
        /// 一个样本都没有，理想值就是 0。但**不能**把门摆在「必须恰好为零」——
        /// 若 recorder 在某一帧抓到一点残留（换档那一帧的 pass 还在图里），
        /// 一个严格的零会把一次正确的运行判红。0.3 的意思是「至少掉了七成」，
        /// 而实现若真的没关掉，比值会在 1.0 附近 —— 两种情形隔着三倍，不会误判。
        const double k_MaxNoInjectionResidual = 0.30;

        /// 「切档省下的占近层总成本的比例」低于这个数时，报告要明说切档几乎没省钱。
        /// 这是一条**只报不判**的线，理由写在档位对照那一节里。
        const double k_ModeSavingNotable = 0.10;

        /// 翻给 debugView 的档位。选 IntegralRgb 而不是 SingleSlice：
        /// 后者要一个合法的切片下标，而那是另一个可能配错的量 ——
        /// 验证名字的这一步不该再引入第二个失败来源。
        ///
        /// ── 为什么允许这个工具碰资产 ──
        /// debugView 序列化在 RendererFeature 资产上，翻它会把资产标脏。本类别处
        /// 立过"不改被量对象"的规矩（runInBackground 那里为此专门用了运行期属性）。
        /// 这里破一次例，因为没有非持久化的等价物，而换来的是两件都没有门的事：
        ///   ① "Vista Froxel Debug View" 这个名字被验；
        ///   ② feature 注释里那句「Off 档整趟 pass 不被记录」第一次有判据 ——
        ///      在此之前它只是一句注释，而「一条只写在注释里的恒等式不会自己失败」。
        /// 复位放在 Cleanup()，手动停止 play 那条路径也会走到。
        const FroxelDebugView k_NameProbeView = FroxelDebugView.IntegralRgb;

        /// 名字验证那组 recorder 的环容量。窗口只有十几帧，但每个 Editor tick 都收割一次，
        /// 所以这里只需要盖住「两个 tick 之间可能渲了几帧」，64 远远够。
        const int k_NameProbeCapacity = 64;

        static ProfilerRecorder[] s_Gpu;
        static ProfilerRecorder[] s_Cpu;
        static int s_StartFrame;
        static bool s_Running;
        static bool s_Started;

        /// 名字验证用的一组 recorder（只要 CPU 侧：这一步问的是"marker 在不在"，
        /// 不是"多少毫秒"，起 GPU recorder 是白付采样开销）。
        ///
        /// 从进 play 的第一个 tick 就起，而不是等到 k_NameProbeFrame ——
        /// 静态表烘得非常早，越早起越有机会抢到。
        /// 但**实测（#27）证明抢不住**：域重载后的第一帧就烘完，而 recorder 是在
        /// EnteredPlayMode 回调里起的，两者的先后没有任何保证 ——
        /// 于是那两格恒为「没抓到」，一道恒红的门不是门。
        /// 所以真正的解法不是抢时机，是 <see cref="TriggerStaticRebake"/> 自己造一次。
        /// 这里仍然早起，因为它免费，而且早起时若恰好抢到就多一条独立证据。
        static ProfilerRecorder[] s_NameProbe;
        /// 每个 pass 名在验证窗口里是否至少出现过一次。
        static bool[] s_NameSeen;
        static bool s_NameProbeTriggered;
        static bool s_NameProbeClosed;
        /// 探针请求是否真的递进去了（froxelVolume 为 null 时递不进去）。
        static bool s_ProbeRequestSent;

        /// 触发那一刻 <c>enableInjection</c> 是不是开着。关着的时候四个探针
        /// 根本不在图里，那一格必须报「不可判定」而不是「名字错」。
        static bool s_NameProbeInjectionOn;

        /// debugView 的原值。null = 本次没碰过它，Cleanup 不要复位 ——
        /// 复位一个没动过的字段会把资产标脏，那正是这里要避免的。
        static FroxelDebugView? s_DebugViewSaved;

        /// 验证窗口里 debugView 是不是确实处在非 Off 档。
        /// 与 <see cref="s_DebugViewSaved"/> 是两件事：那个在复位之后会被置回 null
        /// （它的语义是「还欠一次复位」），拿它去判「调试 pass 该不该出现」，
        /// 会在收摊之后永远读到 false —— 而报告正是在收摊之后才打印的。
        static bool s_NameProbeDebugOn;

        /// 重烘那一步是否已经执行过（与 s_NameProbeTriggered 同构，只是另一件事）。
        static bool s_RebakeTriggered;

        /// 重烘是否**真的发起了**。取不到 feature 时它留 false，
        /// 于是静态表那两格报「不可判定」而不是「名字错」——
        /// 不可判定必须是缺口，但它也不能是诬告：指着两个拼写正确的字符串说对不上，
        /// 会让人去修一个不存在的 bug，那比不报还坏。
        static bool s_RebakeRequested;

        // ---------------------------------------------------------------- 旋钮响应测试的状态
        //
        //  A 档读数必须**冻结**下来再进 B 档：recorder 环在 B 档会被整批重建，
        //  而报告正文是在两档都跑完之后才打印的。若报告仍然现场 Read(s_Gpu[i])，
        //  正文里那一整页数字会在不知不觉间变成 B 档（divisor 减半）的数 ——
        //  一份标着出货配置、内容却是测试配置的报告，比没有报告更危险。

        /// A 档（出货配置）的冻结读数。报告正文的每一个数字都从这里取。
        static Stat[] s_StatGpu;
        static Stat[] s_StatCpu;

        /// B 档（divisor 减半）的冻结读数。只要 GPU 侧：这一节问的是「工作量变了没」，
        /// 而 CPU 侧记的是录制命令的时间，它对派发规模基本不敏感 —— 拿它当证据会削弱判据。
        static Stat[] s_StatGpuB;

        /// C 档（<c>fog.mode = AerialPerspective</c>，divisor 已还原）的冻结读数。
        /// D 档（<c>enableInjection = false</c>，mode 已还原）的冻结读数。
        /// 两者一起构成 #27 第 1 项的性能对照，见 <see cref="BeginModePhase"/>。
        static Stat[] s_StatGpuC;
        static Stat[] s_StatGpuD;

        /// 当前档位。0 = A（出货）、1 = B（divisor 减半）、2 = C（AP 档）、3 = D（注入关）。
        ///
        /// 原来是一个 bool（<c>s_PhaseB</c>）。改成序号而不是再加两个 bool：
        /// 三个互斥的 bool 能表示 8 种状态，其中 5 种是非法的，而没有任何一行代码
        /// 会在它们出现时报错 —— 那类状态机是靠「谁也没写出那种组合」维持正确的。
        static int s_Phase;
        const int k_PhaseA = 0, k_PhaseDiv = 1, k_PhaseApMode = 2, k_PhaseNoInj = 3;

        /// 翻旗前的原值。null = 没翻过，<see cref="RestoreDivisor"/> 不要复位 ——
        /// 与 <see cref="s_DebugViewSaved"/> 同一条理由：复位一个没动过的字段会把资产标脏。
        static int? s_DivisorSaved;

        /// 同上，另外两个旋钮：雾的档位与近层注入开关。
        /// 这两个与 divisor 一样**会被存盘**，所以它们的复位同样要进 <see cref="Cleanup"/>
        /// 的兜底路径（用户手停 play / 看门狗中止都会落到那里）。
        /// 漏掉的症状比 divisor 更难归因：场景的雾档位被静默留在 AerialPerspective 上，
        /// 画面只是「近处的雾淡了一点」，没有任何报错。
        static VistaFogSettings.Mode? s_ModeSaved;
        static bool? s_InjectionSaved;

        /// 两档各自的 divisor。报告正文打 <c>s_DivA</c> 而不是现场读 fogCfg ——
        /// 打印发生在复位之后，现场读到的是被还原的值，正文会宣称一个与 B 档读数不符的配置。
        static int s_DivA, s_DivB;

        /// C/D 两档起采样那一刻读到的 divisor 与 A 档不符时记在这里。
        ///
        /// 为什么要专门留一个字段：C/D 是拿来**和 A 比**的，前提是除了那一个旋钮之外
        /// 两边配置相同。若 <see cref="RestoreDivisor"/> 没生效（资产被别处改过、
        /// feature.current 换了实例），C/D 会跑在减半的 divisor 上，
        /// 于是「切档省了多少」那一栏里混进了一笔与切档毫无关系的四倍工作量 ——
        /// 而那个数看起来完全正常。不可判定必须是缺口，所以这里记下来并让它进门。
        static string s_DivMismatch;

        /// 旋钮翻不动的原因。非 null ⇒ 这道门报**不可判定**，不报通过。
        static string s_ResponseSkipReason;

        /// C/D 两档翻不动的原因，与 <see cref="s_ResponseSkipReason"/> 同一条约定。
        /// 分开存而不是复用一个：divisor 翻不动（已经是下界 2）与 mode 翻不动
        /// （feature 取不到）是两件独立的事，共用一个字段会让先发生的那个把后一个吞掉，
        /// 报告里则表现为「另一节莫名其妙地引用了一条不相干的原因」。
        static string s_ModeSkipReason, s_InjSkipReason;

        /// 整个 run（含两档）的起始帧。<c>s_StartFrame</c> 在进 B 档时会被重新基准化，
        /// 拿它算「实际经过 N 帧」会只报后半程，把报告头上那个数字变成一句假话。
        static int s_RunStartFrame;

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

            Debug.Log($"[Vista] 交叉验证已武装：进 play 模式，四档依次跑，每档丢弃前 {k_WarmupFrames} 帧、"
                    + $"采样 {k_SampleFrames} 帧 —— A 出货配置、B screenDivisor 减半（旋钮响应）、"
                    + "C 雾档位切 AerialPerspective、D 关近层注入（#27 第 1 项的性能对照）。"
                    + $"三个旋钮退出时自动还原，四档跑完自动退出并打报告（约 {4 * (k_WarmupFrames + k_SampleFrames)} 帧）。");
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
                s_RunStartFrame = s_StartFrame;
                s_Running = true;
                s_Started = false;
                s_Phase = k_PhaseA;
                s_DivisorSaved = null;
                s_ModeSaved = null;
                s_InjectionSaved = null;
                s_DivA = 0;
                s_DivB = 0;
                s_DivMismatch = null;
                s_StatGpu = null;
                s_StatCpu = null;
                s_StatGpuB = null;
                s_StatGpuC = null;
                s_StatGpuD = null;
                s_ResponseSkipReason = null;
                s_ModeSkipReason = null;
                s_InjSkipReason = null;
                s_NameProbeTriggered = false;
                s_NameProbeClosed = false;
                s_RebakeTriggered = false;
                s_RebakeRequested = false;
                s_ProbeRequestSent = false;
                s_DebugViewSaved = null;
                s_NameProbeDebugOn = false;
                s_NameProbeInjectionOn = false;
                s_NameSeen = new bool[k_PassNames.Length];
                s_LastFrame = int.MinValue;
                s_NoProgress = System.Diagnostics.Stopwatch.StartNew();
                s_RunInBackgroundSaved = Application.runInBackground;
                Application.runInBackground = true;

                // 名字验证的 recorder 必须在**第一帧渲染之前**起。
                // EnteredPlayMode 正好在那之前，而 EditorApplication.update 的第一个 tick
                // 不保证 —— 两张静态表在第一帧就烘完，晚一个 tick 就永远抓不到它们了。
                StartNameProbe();

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

            // 名字验证整个跑在预热期里，与采样窗口不重叠：它要主动制造几帧
            // 「稳态里不该有」的活儿（四个探针 + 一趟调试全屏），那些活儿若落进采样窗口，
            // 量到的就不是稳态了。预热 45 帧，窗口 5~18 帧，中间还剩 27 帧给
            // 调试视图那趟 pass 的深度拷贝退场。
            TickNameProbe(elapsed);

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

                // 记下这一档实际在跑的 divisor。**在这里**取，而不是在报告里现场读：
                // 报告打印发生在旋钮复位之后，现场读到的是还原值 ——
                // 于是正文会宣称一个与 B 档读数不符的配置，一份自相矛盾的报告。
                //
                // C/D 两档不记新值，而是**核对**它有没有回到 A 档那个数：
                // 它们是拿来和 A 比的，divisor 不同则整个对照失去意义（见 s_DivMismatch）。
                {
                    var fogCfg = VistaAtmosphereFeature.current?.volumetricFog;
                    int div = fogCfg != null ? Mathf.Clamp(fogCfg.screenDivisor, 2, 16) : 0;
                    if (s_Phase == k_PhaseA) s_DivA = div;
                    else if (s_Phase == k_PhaseDiv) s_DivB = div;
                    else if (div != s_DivA && s_DivMismatch == null)
                        s_DivMismatch = $"{(s_Phase == k_PhaseApMode ? "C" : "D")} 档起采样时 "
                                      + $"screenDivisor = {div}，而 A 档是 {s_DivA} —— 复位没生效";
                }

                s_Started = true;
                return;
            }

            if (elapsed < k_WarmupFrames + k_SampleFrames) return;

            // 四档依次：A（出货）→ B（divisor 减半）→ C（AP 档）→ D（注入关）。
            //
            // 翻旗失败时**继续往下翻**而不是直接出报告：B 档翻不动（divisor 已在下界）
            // 与 C/D 能不能跑是两件互不相干的事。让前者否决后者，等于把一道门的
            // 「不可判定」升格成另外两道门的「没跑」—— 而报告上那两节会显示成缺口，
            // 看起来像它们自己失败了，成因却在三十行之外的另一道门里。
            switch (s_Phase)
            {
                case k_PhaseA:
                    s_StatGpu = new Stat[k_PassNames.Length];
                    s_StatCpu = new Stat[k_PassNames.Length];
                    Capture(s_StatGpu, s_StatCpu);
                    if (BeginResponsePhase(frame)) return;
                    if (BeginModePhase(frame)) return;
                    if (BeginNoInjectionPhase(frame)) return;
                    break;

                case k_PhaseDiv:
                    s_StatGpuB = new Stat[k_PassNames.Length];
                    Capture(s_StatGpuB, null);
                    if (BeginModePhase(frame)) return;
                    if (BeginNoInjectionPhase(frame)) return;
                    break;

                case k_PhaseApMode:
                    s_StatGpuC = new Stat[k_PassNames.Length];
                    Capture(s_StatGpuC, null);
                    if (BeginNoInjectionPhase(frame)) return;
                    break;

                default:
                    s_StatGpuD = new Stat[k_PassNames.Length];
                    Capture(s_StatGpuD, null);
                    break;
            }

            // 复位排在报告**之前**：报告里没有一个数字是现场读配置得来的
            // （见上面 s_DivA 的取值时机），所以复位不会污染正文；
            // 反过来把复位放在报告之后，一旦报告里抛出任何异常，
            // 旋钮就永远停在测试值上 —— 一个量性能的工具把被量对象改了还不还回去。
            RestoreDivisor();
            RestoreMode();
            RestoreInjection();
            Report(frame - s_RunStartFrame);
            Cleanup();
            EditorApplication.ExitPlaymode();
        }

        // ================================================================ 旋钮响应测试

        /// <summary>把当前环里的读数冻结到数组里。<paramref name="cpu"/> 可为 null。</summary>
        static void Capture(Stat[] gpu, Stat[] cpu)
        {
            for (int i = 0; i < k_PassNames.Length; ++i)
            {
                gpu[i] = Read(s_Gpu[i]);
                if (cpu != null) cpu[i] = Read(s_Cpu[i]);
            }
        }

        /// <summary>
        /// 把 screenDivisor 减半并重新开一轮预热 + 采样。返回 false 表示这道门**不可判定**
        /// （原因写进 <see cref="s_ResponseSkipReason"/>），调用方应当直接翻下一个旋钮。
        ///
        /// 重建采样环与重新预热的理由见 <see cref="RestartSampling"/>——
        /// 三处翻旗共用那一份，不各写一遍：复制出来的第二份预热逻辑
        /// 会在改预热帧数时漏掉一边。
        /// </summary>
        static bool BeginResponsePhase(int frame)
        {
            var fog = VistaAtmosphereFeature.current?.volumetricFog;
            if (fog == null)
            {
                s_ResponseSkipReason = "VistaAtmosphereFeature.current 取不到，旋钮翻不动";
                return false;
            }

            if (s_DivA <= 0)
            {
                s_ResponseSkipReason = "A 档的 screenDivisor 没取到，没有可翻的基准";
                return false;
            }

            int target = s_DivA / k_DivisorTestFactor;
            if (target < 2)
            {
                // 下界是 VistaVolumetricFogSettings.Resolve 里的 Clamp(2,16)。
                // 不"往上翻"来凑一个能跑的方向：那是另一道门（线程数 ÷4，
                // 阈值方向相反），而临时改判据方向去让一次运行出结果，
                // 等于让门自己迎合被测对象。宁可报不可判定。
                s_ResponseSkipReason = $"screenDivisor 已是 {s_DivA}，减半会越过下界 2，本次没有可翻的方向";
                return false;
            }

            s_DivisorSaved = s_DivA;
            fog.screenDivisor = target;
            RestartSampling(frame, k_PhaseDiv);
            return true;
        }

        /// <summary>
        /// 把采样环整批重建、时基归零、进下一档。三处翻旗共用这一份。
        ///
        /// 环必须**整批重建**：下一档的样本不能掺一个上一档的。
        /// 只清不重建也行不通 —— 下面那段预热代码会 new 一批新的覆盖上去，
        /// 旧的不 Dispose 就是 512 帧容量 ×15×2 个 native 句柄泄漏。
        ///
        /// 为什么每一档都要重新预热满 45 帧、而不是接着采：这三个旋钮
        /// （divisor / 雾档位 / 注入开关）每一个都会让近层的时间重投影历史失效 ——
        /// divisor 改 desc 尺寸触发重新分配，另外两个直接让体停转或停用。
        /// 紧接着那几帧跑在「历史无效」的分支上，把它们算进中位数，
        /// 量到的是一次性的重建成本，不是那一档的稳态 —— 而这些门比的正是稳态。
        /// </summary>
        static void RestartSampling(int frame, int nextPhase)
        {
            Dispose(ref s_Gpu);
            Dispose(ref s_Cpu);
            s_Started = false;
            s_StartFrame = frame;
            s_Phase = nextPhase;
        }

        /// <summary>
        /// 进 C 档：<c>fog.mode = AerialPerspective</c>，divisor 先还原回出货值。
        ///
        /// ── 这一档问的是什么 ──
        /// 「把雾档位从 Froxel 切到 AP，GPU 上少花多少」。这是 #27 第 1 项里
        /// 用户点名要的那份性能对照，也是任何一个接手这套雾的人第一个会问的问题。
        ///
        /// ── 为什么它单独成一档、而不是拿 D 档（注入关）去代表 AP 档 ──
        /// 因为这两件事在本实现里**不是同一件事**，而这个差别正是本节要量的东西：
        /// <c>VistaAtmospherePass.cs:250</c> 的 froxelEnabled 只看 enableInjection
        /// 与相机类型，**根本不看 fog.mode**；mode 只在 :490 决定合成时采不采近层的表
        /// （那一行的注释自己就写着「档 D 下 froxel 体可能仍然分配着，但它不参与合成」）。
        /// 也就是说切到 AP 档之后，注入与积分照跑，产物直接扔掉。
        /// 若拿 D 档冒充 AP 档，报出来的「切档省了 X 毫秒」是一个**在产品里拿不到**的数。
        ///
        /// 所以三档一起才能把话说全：
        ///   A − C ＝ 切档**实际**省下的
        ///   A − D ＝ 近层这一整套一共值多少
        ///   C − D ＝ 切了档还在白交的钱
        ///
        /// ── 为什么先 RestoreDivisor ──
        /// C/D 是拿来和 A 比的。若还停在 B 档减半的 divisor 上，近层的线程数是 A 的四倍，
        /// 于是「切档省了多少」那一栏会变成「切档 + divisor 复原」两件事之和 ——
        /// 一个混了两个自变量的差值，符号都未必对。
        /// </summary>
        static bool BeginModePhase(int frame)
        {
            RestoreDivisor();

            var fog = VistaAtmosphereFeature.current?.fog;
            if (fog == null)
            {
                s_ModeSkipReason = "VistaAtmosphereFeature.current?.fog 取不到，档位翻不动";
                return false;
            }

            if (fog.mode != VistaFogSettings.Mode.Froxel)
            {
                // A 档本来就不是 Froxel ⇒ 没有「切档」可言。不硬把它先设成 Froxel 再切回来：
                // 那量到的是一个本次运行里根本不存在的配置差，而报告会把它写成
                // 「本机切档省下 X」—— 一个针对别人的场景、却署着本场景名字的结论。
                s_ModeSkipReason = $"A 档的 fog.mode 是 {fog.mode}，不是 Froxel，"
                                 + "本次没有「Froxel → AP」这个方向可翻";
                return false;
            }

            s_ModeSaved = fog.mode;
            fog.mode = VistaFogSettings.Mode.AerialPerspective;
            RestartSampling(frame, k_PhaseApMode);
            return true;
        }

        /// <summary>
        /// 进 D 档：<c>enableInjection = false</c>，档位还原回 Froxel。
        ///
        /// 档位要还原：D 档与 A 档之间**只能差一个**自变量，否则 A − D 不是
        /// 「近层的成本」而是「近层的成本 + 合成分支的差」。注入关掉之后
        /// froxelEnabled 为 false，合成那边的 nearLayer 也随之为 false
        /// （<c>nearLayer = froxelEnabled &amp;&amp; ...</c>），所以档位留在 Froxel
        /// 不会让它去采一张不存在的表 —— 这一点是照着 :490 那一行写的，不是推测。
        ///
        /// 这一档同时是本节唯一一道**会红的门**：A − D 必须显著大于 0。
        /// 它与 divisor 那道门同构 —— 判的不是「多少毫秒」，而是
        /// 「上面那些毫秒数与近层之间有没有因果关系」。若关掉注入一分钱不省，
        /// 那么注入/积分那两行量到的就不是近层。
        /// </summary>
        static bool BeginNoInjectionPhase(int frame)
        {
            RestoreDivisor();
            RestoreMode();

            var vf = VistaAtmosphereFeature.current?.volumetricFog;
            if (vf == null)
            {
                s_InjSkipReason = "VistaAtmosphereFeature.current?.volumetricFog 取不到，注入开关翻不动";
                return false;
            }

            if (!vf.enableInjection)
            {
                s_InjSkipReason = "A 档的 enableInjection 本来就是关的，没有可关的东西";
                return false;
            }

            s_InjectionSaved = vf.enableInjection;
            vf.enableInjection = false;
            RestartSampling(frame, k_PhaseNoInj);
            return true;
        }

        /// <summary>
        /// 把 screenDivisor 还回去。幂等：没翻过就什么都不做。
        /// Cleanup 里也调一次，接住「用户手动停 play」那条路径 ——
        /// 与 <see cref="RestoreDebugView"/> 完全同构。
        /// </summary>
        static void RestoreDivisor()
        {
            if (!s_DivisorSaved.HasValue) return;
            var fog = VistaAtmosphereFeature.current?.volumetricFog;
            if (fog != null) fog.screenDivisor = s_DivisorSaved.Value;
            s_DivisorSaved = null;
        }

        /// <summary>把雾档位还回去。幂等，理由与 <see cref="RestoreDivisor"/> 逐字相同。</summary>
        static void RestoreMode()
        {
            if (!s_ModeSaved.HasValue) return;
            var fog = VistaAtmosphereFeature.current?.fog;
            if (fog != null) fog.mode = s_ModeSaved.Value;
            s_ModeSaved = null;
        }

        /// <summary>把近层注入开关还回去。幂等，同上。</summary>
        static void RestoreInjection()
        {
            if (!s_InjectionSaved.HasValue) return;
            var vf = VistaAtmosphereFeature.current?.volumetricFog;
            if (vf != null) vf.enableInjection = s_InjectionSaved.Value;
            s_InjectionSaved = null;
        }

        // ================================================================ 名字验证（预热期）

        /// <summary>
        /// 起一组**只测 CPU** 的 recorder，专门回答「这个 marker 名字存不存在」。
        ///
        /// 不起 GPU recorder：这一步问的是有无，不是多少毫秒，GPU 侧多一份采样开销
        /// 换不回任何信息。也正因为它不产出时间数字，它落在预热期里不影响稳态测量。
        /// </summary>
        static void StartNameProbe()
        {
            s_NameProbe = new ProfilerRecorder[k_PassNames.Length];
            try
            {
                for (int i = 0; i < k_PassNames.Length; ++i)
                    s_NameProbe[i] = ProfilerRecorder.StartNew(
                        ProfilerCategory.Render, k_PassNames[i], k_NameProbeCapacity,
                        ProfilerRecorderOptions.SumAllSamplesInFrame
                        | ProfilerRecorderOptions.WrapAroundWhenCapacityReached);
            }
            catch (System.Exception e)
            {
                // 这里**不**中止整个交叉验证：名字验证是附加的一层，
                // 起不起得来与主采样能不能跑是两件事。主采样那边有自己的
                // fail-fast（同一个 StartNew 会在那里再抛一次，带完整堆栈）。
                // 报告里这一节会说「没跑成」——不可判定要露成缺口，不能静默当通过。
                Debug.LogWarning("[Vista] 名字验证的 recorder 起不来，这一节跳过：" + e.Message);
                Dispose(ref s_NameProbe);
                s_NameProbeClosed = true;
            }
        }

        /// <summary>
        /// 预热期每个 tick 调一次。四段：收割 → 探针触发 → 重烘触发 → 到点收摊。
        ///
        /// 收割放在最前面且从 elapsed=0 就开始，是为了尽量抢在环被覆盖之前把样本捞出来。
        /// 但那对两张静态表**不够** —— 实测（#27）：域重载后 Create() 重建 m_Luts，
        /// 两张表在 play 的第一帧就烘完，而 recorder 是在 EnteredPlayMode 里起的，
        /// 起点与那一帧的距离小到无法保证，结果是两格恒为「没抓到」。
        /// 一格恒红的判据不是判据，它只会教人去改一个拼写正确的字符串。
        /// 所以不靠抢时机，改为**自己造一次重烘**，见 TriggerStaticRebake。
        /// </summary>
        static void TickNameProbe(int elapsed)
        {
            if (s_NameProbeClosed) return;
            if (s_NameProbe == null) { s_NameProbeClosed = true; return; }

            HarvestNames();

            if (!s_NameProbeTriggered && elapsed >= k_NameProbeFrame)
            {
                s_NameProbeTriggered = true;
                TriggerNameProbe();
            }

            // 重烘**排在探针之后**，不合并成一步。Create() 会把 m_Luts 整个换掉，
            // 而近层体是挂在 m_Luts 上的：同一 tick 里先重建再去置 probeRequested，
            // 拿到的是一个还没分配好的体（froxelVolume 为 null），四个探针名会从
            // 「已验证」退回「不可判定」—— 用一格的修复换掉四格，是净亏。
            if (!s_RebakeTriggered && elapsed >= k_NameProbeRebakeFrame)
            {
                s_RebakeTriggered = true;
                TriggerStaticRebake();
            }

            if (elapsed >= k_NameProbeEndFrame)
            {
                HarvestNames();                 // 触发之后的最后一次
                RestoreDebugView();
                Dispose(ref s_NameProbe);
                s_NameProbeClosed = true;
            }
        }

        /// <summary>
        /// 强迫两张静态表重烘一次，好让它们的 marker 在验证窗口里出现。
        ///
        /// 手段是调 <c>feature.Create()</c>：它 dispose 掉旧的 <c>m_Luts</c> 再 new 一个，
        /// 新实例的静态表是脏的，于是下一帧必然烘。
        ///
        /// 为什么是这一个手段：
        ///  · 它**不写任何序列化字段**。改大气参数也能触发重烘，但那会把 RendererFeature
        ///    资产标脏并留下一个需要撤销的改动 —— 量性能的工具不该改被量对象。
        ///  · 它是引擎自己每次 shader 重编译都会走的那条路，不是为自检新开的后门。
        ///  · 它在预热期（第 10 帧）跑完，离采样窗口起点还有 35 帧，重烘那一帧的
        ///    尖峰不会进统计。
        /// </summary>
        static void TriggerStaticRebake()
        {
            var feature = VistaAtmosphereFeature.current;
            if (feature == null) return;
            feature.Create();
            s_RebakeRequested = true;
        }

        /// <summary>
        /// 主动制造一帧，让那五个「稳态里不该出现」的 pass 各出现一次。
        /// </summary>
        static void TriggerNameProbe()
        {
            var feature = VistaAtmosphereFeature.current;
            if (feature == null) return;

            // 记下这一刻的注入开关。四个探针都住在 froxel 那一段里，
            // 关着的时候它们**不可能**出现 —— 那时「名字错」与「配置关着」
            // 在读数上一模一样，报告必须把这一格报成不可判定，而不是报成失败。
            var fog = feature.volumetricFog;
            s_NameProbeInjectionOn = fog != null && fog.enableInjection;

            var volume = feature.froxelVolume;
            if (volume != null)
            {
                volume.probeRequested = true;
                s_ProbeRequestSent = true;
            }

            // 只在 Off 档翻。已经开着的话不动它：那时调试 pass 本来就每帧在跑，
            // 名字会被收割到，而稳态那一节会如实报「诊断开销漏进了每一帧」——
            // 那是一个**真实**的读数，不该被这个工具伪造成 Off。
            if (fog != null && fog.debugView == FroxelDebugView.Off)
            {
                s_DebugViewSaved = fog.debugView;
                fog.debugView = k_NameProbeView;
            }

            // 「这段窗口里调试档是开着的」——**翻过**与**本来就开着**都算。
            // 分成两个旗子是因为它们回答的是两个问题：s_DebugViewSaved 回答
            // 「还欠不欠一次复位」，这个回答「第 14 格能不能判」。
            s_NameProbeDebugOn = fog != null && fog.debugView != FroxelDebugView.Off;
        }

        /// <summary>
        /// 把 <c>debugView</c> 放回去。<c>s_DebugViewSaved</c> 为 null = 本次没碰过，
        /// 那就一个字节都不写 —— 写一个没动过的字段会把资产标脏。
        /// </summary>
        static void RestoreDebugView()
        {
            if (!s_DebugViewSaved.HasValue) return;
            var feature = VistaAtmosphereFeature.current;
            var fog = feature != null ? feature.volumetricFog : null;
            if (fog != null)
                fog.debugView = s_DebugViewSaved.Value;
            s_DebugViewSaved = null;
        }

        /// <summary>
        /// 把「至少出现过一次」这件事从环里收出来。只看 <c>Count</c>，不看 <c>Value</c>：
        /// 一个耗时被舍入成 0 ns 的 pass 仍然是**出现过**的，用 Value &gt; 0 去判
        /// 会让最便宜的那趟 pass 报成名字错。
        /// </summary>
        static void HarvestNames()
        {
            var list = new List<ProfilerRecorderSample>(k_NameProbeCapacity);
            for (int i = 0; i < s_NameProbe.Length; ++i)
            {
                if (s_NameSeen[i]) continue;                 // 一次为真即定，不必再读
                var r = s_NameProbe[i];
                if (!r.Valid || r.Count <= 0) continue;

                list.Clear();
                r.CopyTo(list, false);
                for (int k = 0; k < list.Count; ++k)
                {
                    if (list[k].Count <= 0) continue;        // 那一帧这个 marker 没出现
                    s_NameSeen[i] = true;
                    break;
                }
            }
        }

        struct Stat
        {
            public int count;                 // 有效帧数
            public double minMs, medMs, maxMs;
            /// <summary>
            /// 95 分位。加它不是为了多一个数好看，是因为 max 回答不了「阈值该摆哪儿」——
            /// 300 帧里的 max 常常是某一次编辑器自己的抢占（资源导入、GC、窗口重绘），
            /// 拿它定门会把门定在一个与这段代码无关的事件上。
            /// min 是另一端的极值，回答「这条路径最快能有多快」，同样不能当预算。
            /// 预算要拿一个**分位数**，这是 UE5 的 stat unit / Frostbite 的帧预算报表
            /// 都在用的口径。
            /// </summary>
            public double p95Ms;
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
            // 最近秩法（nearest-rank），不插值：样本量只有几百，插值出来的那个数
            // 落在两个真实观测之间，是一个**没有被观测到的**耗时。定预算时
            // 「有一帧真的花了这么久」比「统计上大约这么久」更站得住。
            int rank = (int)System.Math.Ceiling(0.95 * ms.Count) - 1;
            s.p95Ms = ms[Mathf.Clamp(rank, 0, ms.Count - 1)];
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
              .Append(k_WarmupFrames).Append("）×4 档（A 出货配置｜B screenDivisor 减半｜"
                                           + "C 雾档位切 AP｜D 关近层注入），实际经过 ")
              .Append(elapsedFrames).AppendLine(" 帧；正文数字**全部**来自 A 档");
            sb.Append("　 本次 play 期间强制 Application.runInBackground=true（原值 ")
              .Append(s_RunInBackgroundSaved ? "true" : "false")
              .AppendLine("，退出即还原，不写 ProjectSettings）—— 失焦时 play 循环不停走，否则采样会卡死");

            // ── 量纲与配置：任何一个亮度/耗时数字都要跟着这两行读 ──
            //
            // 曝光印出来是因为它**线性缩放画面里的一切亮度**。它不改耗时，但这份报告
            // 会和亮度类报表（⑪ 系列、max(S)）放在一起看，而跨曝光比较那些数字是无意义的。
            // 印「来源」而不只是数值：资产默认与场景覆盖差 10 档 EV 是常态，
            // 只印一个 9 的话，「这是谁定的」得翻两个 Inspector 才知道。
            var feature = VistaAtmosphereFeature.current;
            if (feature != null)
            {
                sb.Append("　 EV100 ").Append(feature.ev100.ToString("F2"))
                  .Append("（来源：").Append(feature.ev100IsOverridden
                      ? "场景覆盖，资产默认 " + feature.ev100Asset.ToString("F2")
                      : "资产默认，无场景覆盖")
                  .Append("）　exposure ")
                  .AppendLine(VistaAtmosphereViewData.ExposureFromEV100(feature.ev100).ToString("E4"));
            }
            else
            {
                sb.AppendLine("　 ⚠ VistaAtmosphereFeature.current 为 null —— 下面每一条「期望值」都取不到配置，"
                            + "froxel 一节会整节报不可判定");
            }

            // froxel 的两个尺寸旋钮决定了注入/积分的线程数，也就是它们的成本。
            // 不印的话，两次运行之间的差会被归因到代码，而实际上可能只是有人改了 divisor。
            var fogCfg = feature != null ? feature.volumetricFog : null;
            bool froxelExpected = fogCfg != null && fogCfg.enableInjection;
            if (fogCfg != null)
            {
                // divisor 印 s_DivA（A 档采样启动那一刻记下的值），不印 fogCfg.screenDivisor：
                // 报告是在旋钮复位之后打的，现场读虽然也是 A 档的值，但那只是"碰巧相等"——
                // 一旦将来复位顺序变了，正文会跟着变成一句假话而没有任何提示。
                // 让正文的配置与正文的数字来自同一次快照，是它们不会互相说谎的唯一保证。
                int divA = s_DivA > 0 ? s_DivA : Mathf.Clamp(fogCfg.screenDivisor, 2, 16);
                sb.Append("　 近层 froxel：enableInjection ").Append(froxelExpected ? "开" : "**关**")
                  .Append("　screenDivisor ").Append(divA)
                  .Append("　slices ").Append(fogCfg.sliceCount)
                  .Append("　远边界 ").Append(fogCfg.depth.ToString("F1")).Append(" m");
                var gameRes = UnityEditor.Handles.GetMainGameViewSize();
                int vx = Mathf.Max(1, Mathf.CeilToInt(gameRes.x / divA));
                int vy = Mathf.Max(1, Mathf.CeilToInt(gameRes.y / divA));
                sb.Append("　→ 体约 ").Append(vx).Append("×").Append(vy).Append("×")
                  .Append(fogCfg.sliceCount)
                  .Append("（按 Game 视图 ").Append((int)gameRes.x).Append("×").Append((int)gameRes.y)
                  .AppendLine(" 推算，仅供量级参考——实际尺寸由 pass 侧的渲染目标定）");
            }

            int gpuUsable = 0, missing = 0, occUnstable = 0, staticLeak = 0;
            double steadyRawMin = 0.0, steadyPerRenderMin = 0.0, steadyPerRenderMed = 0.0;
            bool steadyComplete = true;
            double occAll = 0.0;          // 稳态五 pass 共同的出现次数（不一致就置 0）
            bool occAgreed = true;

            // 合成 pass 单独留一份：它的对账口径与 LUT 那五个不同（见 k_CompositeIdx）。
            Stat composite = default;
            int compositeMissing = 0, compositeCpuOnly = 0, compositeOccUnstable = 0;

            // froxel 出货两趟：单模型，不并进上面那三个累加器（见 k_FroxelSteadyIdx ①）。
            int froxelMissing = 0, froxelCpuOnly = 0, froxelOccUnstable = 0, froxelUnexpected = 0;
            double froxelPerRenderMin = 0.0, froxelPerRenderMed = 0.0, froxelPerRenderP95 = 0.0;
            bool froxelComplete = true;

            // 自检/诊断五趟：期望零样本，方向与静态表相同。
            int absentLeak = 0;

            // 旋钮响应门的缺口计数。见「旋钮响应测试」一节的常量注释。
            int responseGap = 0;

            // 档位对照（C/D 两档）的缺口计数。见「档位对照」一节。
            int modeGap = 0;

            for (int i = 0; i < k_PassNames.Length; ++i)
            {
                // 读冻结下来的 A 档快照，不现场读环。B 档跑完之后环里装的是 B 档的样本，
                // 现场读会让这份标着「screenDivisor 8」的正文悄悄变成 divisor 4 的数字。
                Stat g = s_StatGpu != null ? s_StatGpu[i] : Read(s_Gpu[i]);
                Stat c = s_StatCpu != null ? s_StatCpu[i] : Read(s_Cpu[i]);
                bool isStatic = System.Array.IndexOf(k_StaticIdx, i) >= 0;
                bool isComposite = System.Array.IndexOf(k_CompositeIdx, i) >= 0;
                bool isFroxel = System.Array.IndexOf(k_FroxelSteadyIdx, i) >= 0;
                bool isAbsent = System.Array.IndexOf(k_AbsentIdx, i) >= 0;

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

                if (isFroxel)
                {
                    // 期望值**读配置**，不写死。enableInjection 关着的时候零样本是
                    // 完全正确的状态，一道只看「每帧都该在」的门会在一个合法布景上红。
                    if (!froxelExpected)
                    {
                        if (g.count == 0 && c.count == 0)
                        {
                            sb.Append("enableInjection 关着：0 个样本（**这是期望**）　"
                                    + "→ 这一趟的耗时本次未测，不是「很便宜」");
                        }
                        else
                        {
                            froxelUnexpected++;
                            sb.Append("**enableInjection 关着却在跑**：GPU min ")
                              .Append(g.minMs.ToString("F3"))
                              .Append(" ms　帧 ").Append(g.count).Append("/").Append(k_SampleFrames)
                              .Append("　→ 关态没有真正关掉，白烧（画面上看不出来）");
                        }
                        froxelComplete = false;
                        sb.AppendLine();
                        continue;
                    }

                    if (g.count > 0)
                    {
                        sb.Append("GPU min ").Append(g.minMs.ToString("F3"))
                          .Append("　中位 ").Append(g.medMs.ToString("F3"))
                          .Append("　p95 ").Append(g.p95Ms.ToString("F3"))
                          .Append("　max ").Append(g.maxMs.ToString("F3"))
                          .Append(" ms　帧 ").Append(g.count).Append("/").Append(k_SampleFrames)
                          .Append("　每帧出现 ").Append(g.occMin.ToString("F0"));

                        if (g.occMax != g.occMin)
                        {
                            sb.Append("~").Append(g.occMax.ToString("F0")).Append(" ⚠不稳定");
                            froxelOccUnstable++;
                            froxelComplete = false;
                        }
                        else if (g.occMin > 1.0)
                        {
                            sb.Append("　单次 ").Append((g.minMs / g.occMin).ToString("F3")).Append(" ms");
                        }

                        // 这里按出现次数相除是合法的，理由与 LUT 那五个相同而与合成不同：
                        // 近层体的尺寸由 screenDivisor 定，Scene 视图与 Game 视图各渲一次
                        // 时两次的线程数一样。合成不能除是因为它的成本跟着各自的
                        // 渲染目标分辨率走，两次根本不等价。
                        double fdiv = (g.occMin == g.occMax && g.occMin >= 1.0) ? g.occMin : 1.0;
                        froxelPerRenderMin += g.minMs / fdiv;
                        froxelPerRenderMed += g.medMs / fdiv;
                        froxelPerRenderP95 += g.p95Ms / fdiv;
                    }
                    else if (c.count > 0)
                    {
                        froxelCpuOnly++;
                        froxelComplete = false;
                        sb.Append("GPU 无值（valid=").Append(g.valid).Append("）　但 CPU marker 在：min ")
                          .Append(c.minMs.ToString("F3")).Append(" ms　帧 ").Append(c.count)
                          .Append("　→ pass 跑了，是 GPU recorder 取不到");
                    }
                    else
                    {
                        froxelMissing++;
                        froxelComplete = false;
                        sb.Append("**一个样本都没有**，而 enableInjection 是开着的（GPU valid=")
                          .Append(g.valid).Append("，CPU valid=").Append(c.valid)
                          .Append("）　→ pass 名走歧、或这一趟被运行期条件剪掉了"
                                + "（体积无效 / 相机类型不符）");
                    }
                    sb.AppendLine();
                    continue;
                }

                if (isAbsent)
                {
                    // 与静态表同一个方向：**有**样本才是 bug。
                    // 这五趟是自检与诊断用的，稳态里出现意味着诊断开销漏进了每一帧，
                    // 而画面上完全看不出来 —— 只有计时器能看见的那一类问题。
                    if (g.count == 0 && c.count == 0)
                    {
                        sb.Append("稳态 0 个样本（**这是期望**）　→ ");
                        sb.Append(i == 14
                            ? "debugView=Off，整趟 pass 没被记录"
                            : "probeRequested 只被 Editor 自检置、且 pass 自己当帧清掉");
                    }
                    else
                    {
                        absentLeak++;
                        sb.Append("**稳态里在跑**：GPU min ").Append(g.minMs.ToString("F3"))
                          .Append(" ms　帧 ").Append(g.count).Append("/").Append(k_SampleFrames)
                          .Append(i == 14
                              ? "　→ debugView 没回 Off，每帧多一次全屏 + 一次深度拷贝"
                              : "　→ probeRequested 没被清，自检开销漏进了每一帧");
                    }
                    sb.AppendLine();
                    continue;
                }

                if (g.count > 0)
                {
                    gpuUsable++;
                    sb.Append("GPU min ").Append(g.minMs.ToString("F3"))
                      .Append("　中位 ").Append(g.medMs.ToString("F3"))
                      .Append("　p95 ").Append(g.p95Ms.ToString("F3"))
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

            // ---- 近层 froxel 的两趟出货 pass（单模型，没有第二个模型可对） ----
            //
            // 这一节**刻意排在与模型 B 对账之前**，且自己不进那个和。
            // 模型 B 只覆盖大气七表与 AP 合成；把 froxel 并进去，
            // 0.170~0.198 ms 那个区间当场失效，而失效的方式是"看起来还能对"——
            // 一个被污染的和会给两边都发一张假的合格证。
            sb.AppendLine("　 近层 froxel 出货两趟（#21~#24 的全部每帧成本；**无第二个模型对账**）");
            if (!froxelExpected)
            {
                sb.AppendLine("　　 enableInjection 关着 —— 本次没有测到这两趟的耗时。"
                            + "报表上的 0 是「没测」，不是「不要钱」。");
            }
            else if (!froxelComplete)
            {
                sb.AppendLine("　　 有缺口（见上），小计不成立 —— 不印一个不完整的和。");
            }
            else
            {
                sb.Append("　　 单次小计　min ").Append(froxelPerRenderMin.ToString("F3"))
                  .Append("　中位 ").Append(froxelPerRenderMed.ToString("F3"))
                  .Append("　p95 ").Append(froxelPerRenderP95.ToString("F3")).AppendLine(" ms");
                // 定预算要拿 p95，不是 min 也不是 max，理由见 Stat.p95Ms。
                sb.Append("　　 这个数只回答「多少毫秒」，回答不了「对不对」——");
                sb.AppendLine("它没有独立模型可对，唯一的验证手段是改一个已知的旋钮"
                            + "（screenDivisor 减半，线程数 ×4）看它动不动 —— 下一节就是那道门。");
            }

            // ---- 旋钮响应：把上面那句话变成一道会红的门 ----
            //
            // 这一节回答的不是「多少毫秒」，而是「上面那个毫秒数到底测的是不是 froxel」。
            // 它是本报告里唯一一条**因果**证据：把一个已知会改工作量的旋钮翻一档，
            // 看读数动不动。没有它，注入/积分那两行就是两个无法被证伪的字符串。
            sb.AppendLine("　 旋钮响应（screenDivisor 减半 ⇒ 体的 XY 各 ×2 ⇒ 线程数 ×4）");
            if (s_ResponseSkipReason != null || s_StatGpuB == null || s_StatGpu == null)
            {
                // 不可判定必须是缺口，不能是通过 —— 一道翻不动旋钮的门若报绿，
                // 它给上面那两行背的书和真跑过一遍一模一样，而实际上一个字都没验。
                responseGap = 1;
                sb.Append("　　 **不可判定**：")
                  .AppendLine(s_ResponseSkipReason ?? "B 档读数缺失（本次没有跑到第二档）");
                sb.AppendLine("　　 → 注入/积分那两行本次没有任何独立证据，不要拿它们去定预算。");
            }
            else
            {
                // 下标走 k_FroxelSteadyIdx，不写字面量 8/9：手抄的下标在有人往
                // k_PassNames 中间插一趟 pass 的那天不会跟着改，那时这道门会安静地
                // 去比另外两趟毫不相干的 pass，并且照样给出一个漂亮的比值。
                int iInj = k_FroxelSteadyIdx[0], iInt = k_FroxelSteadyIdx[1];
                Stat injA = s_StatGpu[iInj], injB = s_StatGpuB[iInj];
                Stat intA = s_StatGpu[iInt], intB = s_StatGpuB[iInt];
                Stat ctlA = s_StatGpu[k_ResponseControlIdx], ctlB = s_StatGpuB[k_ResponseControlIdx];

                sb.Append("　　 A 档 screenDivisor ").Append(s_DivA)
                  .Append("　→　B 档 ").Append(s_DivB)
                  .Append("　（各自独立预热 ").Append(k_WarmupFrames)
                  .Append(" 帧、采样 ").Append(k_SampleFrames).AppendLine(" 帧）");

                // 一律用**中位数**比，不用 min 也不用 max：
                // min 会被两档各自最幸运的那一帧主导（它对负载不敏感，正是这道门要看的东西），
                // max 会被任意一次编辑器抖动主导。中位数在同配置下复现到 5% 以内（#27 实测），
                // 这正是它能当比值分母的理由。
                bool have = injA.count > 0 && injB.count > 0
                         && intA.count > 0 && intB.count > 0
                         && ctlA.count > 0 && ctlB.count > 0
                         && injA.medMs > 0.0 && intA.medMs > 0.0 && ctlA.medMs > 0.0;
                if (!have)
                {
                    responseGap = 1;
                    sb.AppendLine("　　 **不可判定**：两档里至少有一档没采到注入/积分/对照的样本，比值算不出来。");
                }
                else
                {
                    double rInj = injB.medMs / injA.medMs;
                    double rInt = intB.medMs / intA.medMs;
                    double rCtl = ctlB.medMs / ctlA.medMs;

                    sb.Append("　　 ").Append(k_PassNames[iInj].PadRight(30))
                      .Append("中位 ").Append(injA.medMs.ToString("F3")).Append(" → ")
                      .Append(injB.medMs.ToString("F3")).Append(" ms　比值 ")
                      .AppendLine(rInj.ToString("F2"));
                    sb.Append("　　 ").Append(k_PassNames[iInt].PadRight(30))
                      .Append("中位 ").Append(intA.medMs.ToString("F3")).Append(" → ")
                      .Append(intB.medMs.ToString("F3")).Append(" ms　比值 ")
                      .AppendLine(rInt.ToString("F2"));
                    sb.Append("　　 对照 ").Append(k_PassNames[k_ResponseControlIdx].PadRight(27))
                      .Append("中位 ").Append(ctlA.medMs.ToString("F3")).Append(" → ")
                      .Append(ctlB.medMs.ToString("F3")).Append(" ms　比值 ")
                      .AppendLine(rCtl.ToString("F2"));

                    bool injOk = rInj >= k_MinInjectionResponse;
                    bool ctlOk = System.Math.Abs(rCtl - 1.0) <= k_ControlRatioTolerance;
                    if (!injOk || !ctlOk) responseGap = 1;

                    sb.Append("　　 判据：注入比值 ≥ ").Append(k_MinInjectionResponse.ToString("F2"))
                      .Append(" —— ").Append(injOk ? "通过" : "**不通过**")
                      .Append("；对照比值 |r−1| ≤ ").Append(k_ControlRatioTolerance.ToString("F2"))
                      .Append(" —— ").AppendLine(ctlOk ? "通过" : "**不通过**");

                    if (!ctlOk)
                        sb.AppendLine("　　 → 对照项自己也变了，说明整机状态在两档之间变过"
                                    + "（热降频、后台导资源、另一个 Editor 窗口在渲染）。"
                                    + "这时注入项涨没涨都**不能**归因给旋钮，整节作废，重跑一次。");
                    else if (!injOk)
                        sb.AppendLine("　　 → 线程数翻了两番而耗时涨不到一半，"
                                    + "说明这个读数被某个与 froxel 规模无关的常数项主导"
                                    + "（派发固定开销、barrier、或者根本没接到这趟 pass 上）。"
                                    + "在查清之前，上面那个「每帧成本」不能当作近层雾的成本引用。");

                    // 为什么只有下界、没有上界：×4 是**线程数**比，不是时间比。
                    // 占用率、缓存命中率、每趟派发的固定开销都会把它拉离 4，
                    // 而我手上没有能定出上界的模型 —— 拿一个猜的上界当门，
                    // 红起来的时候没人知道该修代码还是该改那个猜测。
                    sb.AppendLine("　　 不设上界：×4 是线程数比不是时间比，占用率/缓存/固定开销都会让它偏离，"
                                + "而定上界需要一个我现在没有的模型。这里只报数。");
                    sb.Append("　　 积分项（比值 ").Append(rInt.ToString("F2"))
                      .AppendLine("）只报不判：它读的是同一张表但写的是同一批像素，"
                                + "线程数与 divisor 的关系不是简单的 ×4，没有可辩护的阈值。");
                }
            }

            // ---- 档位对照：Froxel 档 vs AP 档 vs 近层全关（#27 第 1 项） ----
            //
            // 这一节回答的是「开这套近层雾，GPU 上一共花多少，切档能省回多少」。
            // 它与上面那道旋钮响应门的关系是：那一道证明注入/积分那两行**测的是 froxel**，
            // 这一节在此基础上把这笔钱拆成「切档能省的」与「切了档也省不掉的」两半。
            //
            // 三个差各有各的问题，缺一个都会让另外两个变得可以任意解释：
            //   A − C ＝ 把 fog.mode 切到 AerialPerspective 实际省下的
            //   A − D ＝ 近层这一整套（注入 + 积分）一共值多少
            //   C − D ＝ 切了档、产物却被扔掉，仍然在付的那部分
            // 只给 A − D 的话，读者会默认「切到 AP 档就省下这些」——
            // 而在本实现里那句话是错的，错的方向还是乐观的那一侧。
            sb.AppendLine("　 档位对照（A Froxel 档｜C AP 档｜D 近层全关，三档同一个 screenDivisor）");
            {
                int iInj = k_FroxelSteadyIdx[0], iInt = k_FroxelSteadyIdx[1];

                if (s_DivMismatch != null)
                {
                    modeGap = 1;
                    sb.Append("　　 **整节作废**：").AppendLine(s_DivMismatch);
                    sb.AppendLine("　　 → 三档不在同一个配置上，任何差值都同时含着两个自变量。");
                }
                else if (s_StatGpu == null || s_StatGpuC == null || s_StatGpuD == null)
                {
                    modeGap = 1;
                    sb.Append("　　 **不可判定**：")
                      .AppendLine(s_ModeSkipReason ?? s_InjSkipReason ?? "C/D 档读数缺失（本次没有跑到）");
                    if (s_ModeSkipReason != null && s_InjSkipReason != null)
                        sb.Append("　　 （另一条：").Append(s_InjSkipReason).AppendLine("）");
                    sb.AppendLine("　　 → 本次拿不到「近层一共花多少」，上面那个「每帧成本」"
                                + "只能当作一个孤立数字，不能拿去谈「关掉能省多少」。");
                }
                else
                {
                    Stat injA = s_StatGpu[iInj],  intA = s_StatGpu[iInt];
                    Stat injC = s_StatGpuC[iInj], intC = s_StatGpuC[iInt];
                    Stat injD = s_StatGpuD[iInj], intD = s_StatGpuD[iInt];

                    // D 档期望零样本，所以**不能**要求 D 有样本 —— 那正好把健康状态判成缺口。
                    // A/C 两档则必须有：它们是分母。
                    bool have = injA.count > 0 && intA.count > 0
                             && injC.count > 0 && intC.count > 0
                             && injA.medMs > 0.0 && intA.medMs > 0.0;
                    // ---- 整机状态漂移的对照项 ----
                    //
                    // 与旋钮响应那一节同一个对照 pass（Sky-View LUT）、同一条理由，
                    // 但这里更需要它：那一节只跨两档，这一节跨四档、前后差着一千多帧，
                    // 热降频、后台导资源、另一个窗口开始渲染，都会让**所有**行一起变。
                    //
                    // 没有这一项的话，这一节报出来的 A − C 会把机器状态的漂移
                    // 原封不动地写成「切档省下/多花了多少毫秒」——
                    // 而那个数看起来完全正常，符号甚至可能是反的。
                    // 本次首跑就撞上了：对照比值 1.35，而注入 A→C 涨了 77%，
                    // 两个数一起说明「那 77% 里有多少是切档造成的」当时根本答不出来。
                    //
                    // 对照 pass 与三个旋钮全都无关（divisor/mode/injection 一个都不进 Sky-View LUT），
                    // 所以它在四档之间**应当**是同一个数；它不是，就说明这四档不可比。
                    Stat ctlA = s_StatGpu[k_ResponseControlIdx];
                    Stat ctlC = s_StatGpuC[k_ResponseControlIdx];
                    Stat ctlD = s_StatGpuD[k_ResponseControlIdx];
                    bool ctlHave = ctlA.count > 0 && ctlC.count > 0 && ctlD.count > 0 && ctlA.medMs > 0.0;
                    double rCtlC = ctlHave ? ctlC.medMs / ctlA.medMs : double.NaN;
                    double rCtlD = ctlHave ? ctlD.medMs / ctlA.medMs : double.NaN;
                    bool ctlSteady = ctlHave
                        && System.Math.Abs(rCtlC - 1.0) <= k_ControlRatioTolerance
                        && System.Math.Abs(rCtlD - 1.0) <= k_ControlRatioTolerance;

                    if (!have)
                    {
                        modeGap = 1;
                        sb.AppendLine("　　 **不可判定**：A 或 C 档没采到注入/积分的样本，差值算不出来。");
                    }
                    else if (!ctlSteady)
                    {
                        // 整节作废，与 divisor 不符同一个处置：三档不可比时，
                        // 连那道看起来很稳的 D/A 门也一起不判 —— 它同样是一个跨档比值。
                        // 「大概没问题」不是判据。
                        modeGap = 1;
                        sb.Append("　　 **整节作废**：对照 ").Append(k_PassNames[k_ResponseControlIdx])
                          .Append(" 中位 A ").Append(ctlA.count > 0 ? ctlA.medMs.ToString("F3") : "—")
                          .Append("　C ").Append(ctlC.count > 0 ? ctlC.medMs.ToString("F3") : "—")
                          .Append("　D ").Append(ctlD.count > 0 ? ctlD.medMs.ToString("F3") : "—")
                          .Append(" ms　比值 C/A ").Append(ctlHave ? rCtlC.ToString("F2") : "n/a")
                          .Append("　D/A ").Append(ctlHave ? rCtlD.ToString("F2") : "n/a")
                          .Append("（容差 |r−1| ≤ ").Append(k_ControlRatioTolerance.ToString("F2"))
                          .AppendLine("）");
                        sb.AppendLine("　　 → 对照 pass 不吃这三个旋钮里的任何一个，它变了就是整机状态变了。"
                                    + "这时三档之间的任何差值都同时含着「旋钮」与「机器」两个自变量，"
                                    + "归因不成立。让机器闲下来重跑一次。");
                        // 数字照打，但明确标成不可引用：删掉它们会让「作废」与「没跑」
                        // 在报告上长得一模一样，而这两件事该做的下一步不同。
                        sb.Append("　　 （仅供参考，**不可引用**）近层合计 A ")
                          .Append((injA.medMs + intA.medMs).ToString("F3"))
                          .Append("　C ").Append((injC.medMs + intC.medMs).ToString("F3"))
                          .Append("　D ").Append(((injD.count > 0 ? injD.medMs : 0.0)
                                                + (intD.count > 0 ? intD.medMs : 0.0)).ToString("F3"))
                          .AppendLine(" ms");
                    }
                    else
                    {
                        double nearA = injA.medMs + intA.medMs;
                        double nearC = injC.medMs + intC.medMs;
                        // D 档没有样本 ⇒ 这两趟没跑 ⇒ 成本就是 0。这里把「零样本」读成 0 ms
                        // 是**有前提**的：前提是上面那一节已经证明这两个名字拼对了
                        // （pass 名验证）。没有那一节，零样本与名字打错逐字相同。
                        double nearD = (injD.count > 0 ? injD.medMs : 0.0)
                                     + (intD.count > 0 ? intD.medMs : 0.0);

                        sb.Append("　　 ").Append(k_PassNames[iInj].PadRight(30))
                          .Append("中位 A ").Append(injA.medMs.ToString("F3"))
                          .Append("　C ").Append(injC.medMs.ToString("F3"))
                          .Append("　D ").Append(injD.count > 0 ? injD.medMs.ToString("F3") : "—（零样本）")
                          .AppendLine(" ms");
                        sb.Append("　　 ").Append(k_PassNames[iInt].PadRight(30))
                          .Append("中位 A ").Append(intA.medMs.ToString("F3"))
                          .Append("　C ").Append(intC.medMs.ToString("F3"))
                          .Append("　D ").Append(intD.count > 0 ? intD.medMs.ToString("F3") : "—（零样本）")
                          .AppendLine(" ms");
                        sb.Append("　　 近层合计　　　　　　　　　　　 A ").Append(nearA.ToString("F3"))
                          .Append("　C ").Append(nearC.ToString("F3"))
                          .Append("　D ").Append(nearD.ToString("F3")).AppendLine(" ms");

                        // ---- 门：关掉注入必须真的不跑了 ----
                        double residual = nearD / nearA;
                        bool injOff = residual <= k_MaxNoInjectionResidual;
                        if (!injOff) modeGap = 1;
                        sb.Append("　　 判据：D/A = ").Append(residual.ToString("F3"))
                          .Append(" ≤ ").Append(k_MaxNoInjectionResidual.ToString("F2"))
                          .Append(" —— ").AppendLine(injOff ? "通过" : "**不通过**");
                        if (!injOff)
                            sb.AppendLine("　　 → enableInjection 关掉之后这两趟还在跑（或者还在计时）。"
                                        + "在查清之前，上面所有近层的毫秒数都不能归因给近层："
                                        + "一个关不掉的开关说明这个 marker 底下不止近层这一件事。");

                        // ---- 只报不判：切档省了多少 ----
                        double savedByMode = nearA - nearC;
                        double totalNear   = nearA - nearD;
                        sb.Append("　　 A − C（切 AP 档省下）").Append(savedByMode.ToString("F3"))
                          .Append(" ms　｜　A − D（近层总价）").Append(totalNear.ToString("F3"))
                          .Append(" ms　｜　C − D（切了档仍在付）")
                          .Append((nearC - nearD).ToString("F3")).AppendLine(" ms");

                        if (totalNear > 1e-6)
                        {
                            double share = savedByMode / totalNear;
                            sb.Append("　　 切档省下的占近层总价 ").Append(share.ToString("P0"));
                            if (share < k_ModeSavingNotable)
                            {
                                // 这不是一次意外，是照着代码写死的行为，所以这里指名道姓给出行号：
                                // 一条说得出「在哪一行」的结论，读者可以三十秒内自己证伪。
                                sb.AppendLine("　　←**切档几乎不省钱**");
                                sb.AppendLine("　　 → 成因不是测量误差，是实现如此："
                                            + "VistaAtmospherePass.cs:250 的 froxelEnabled 只看 "
                                            + "enableInjection 与相机类型，**不看 fog.mode**；"
                                            + "mode 只在 :490 决定合成时采不采近层的表。"
                                            + "于是 AP 档下注入与积分照跑，产物直接扔掉。");
                                sb.AppendLine("　　 → 这一栏**只报不判**：把它做成门的话它今天必红，"
                                            + "而一格恒红的判据不是判据。要让它能绿，"
                                            + "得先改运行期行为（把 froxelEnabled 也挂上 UsesNearLayer），"
                                            + "那是一次会改画面与生命周期的改动，得单独立项讨论；"
                                            + "改完之后这一栏连同一道「切档必须省下九成」的门一起补上。");
                            }
                            else
                            {
                                sb.AppendLine("　　（切档确实省下了大部分近层成本）");
                            }
                        }
                        else
                        {
                            sb.AppendLine("　　 近层总价 ≤ 0，占比不计算 —— 这与上面那道门互为印证，"
                                        + "以那一道的结论为准。");
                        }

                        sb.AppendLine("　　 三档之间只差一个自变量：C 相对 A 只改 fog.mode，"
                                    + "D 相对 A 只改 enableInjection（档位已还原），"
                                    + "screenDivisor 三档一致（起采样时逐档核对过）。");
                        // 对照项通过时也要把数打出来：一道只在失败时才留下痕迹的检查，
                        // 与一道根本没跑的检查在报告上无法区分。
                        sb.Append("　　 整机状态对照 ").Append(k_PassNames[k_ResponseControlIdx])
                          .Append("：C/A ").Append(rCtlC.ToString("F2"))
                          .Append("　D/A ").Append(rCtlD.ToString("F2"))
                          .Append("（容差 |r−1| ≤ ").Append(k_ControlRatioTolerance.ToString("F2"))
                          .AppendLine("）—— 四档之间机器状态没变过。");
                    }
                }
            }

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

            // ---- pass 名验证（预热期，与上面的采样窗口不重叠） ----
            //
            // 这一节回答的问题上面任何一行都回答不了：**这 15 个字符串拼对了吗**。
            //
            // 对「每帧都在跑」的那几趟，采样窗口本身就是验证：名字打错 → 零样本 → 那一行红。
            // 但对静态表（烘完就不再跑）、四个自检探针、以及 Off 档的调试视图，
            // 「零样本」恰恰是**正确**的稳态 —— 于是一个打错的名字与一份健康的管线
            // 在报表上逐字相同。一个红不起来的红测，长得和一个通过了的红测一模一样。
            //
            // 所以预热期里主动制造一帧让它们各出现一次，只看「有没有出现过」，不看耗时。
            sb.AppendLine("　 pass 名验证（预热期主动触发，只问有无、不问耗时）");
            int nameGap = 0;
            if (s_NameSeen == null)
            {
                nameGap++;
                sb.AppendLine("　　 **整节没跑成** —— recorder 起不来（见上面的 warning）。"
                            + "这 15 个名字本次一个都没有被验证过。");
            }
            else
            {
                if (!s_NameProbeTriggered)
                {
                    nameGap++;
                    sb.AppendLine("　　 ⚠ 触发那一步没执行到（预热被提前打断？）—— "
                                + "下面凡是靠触发才出现的格子都只能算不可判定。");
                }

                var namesOk = new System.Text.StringBuilder();
                for (int i = 0; i < k_PassNames.Length; ++i)
                {
                    if (s_NameSeen[i]) { namesOk.Append(namesOk.Length > 0 ? "、" : "").Append(i); continue; }

                    // 没抓到的才逐个点名，并且**先判它这次有没有机会出现**。
                    // 顺序不能反：一个本来就不可能出现的 pass 报成「名字错」，
                    // 会让人去改一个拼写正确的字符串。
                    bool probeDependent = System.Array.IndexOf(k_AbsentIdx, i) >= 0 && i != 14;
                    bool triggerDependent = probeDependent || i == 14;
                    // 两张静态表只在「表脏了」的那一帧跑。play 的第一帧那次抢不到
                    // （见 s_NameProbe 的注释），所以它们唯一的出场机会是主动重烘那一次。
                    bool rebakeDependent = System.Array.IndexOf(k_StaticIdx, i) >= 0;
                    string why = null;
                    // 「触发压根没执行」排在最前面。放后面的话，那几个记录触发时状态的
                    // 旗子全是初值 false，于是会打出「enableInjection 关着」——
                    // 一个从来没被读过的状态被当成观测值报出去。
                    if (triggerDependent && !s_NameProbeTriggered)
                        why = "触发没执行到，这一格本次没有被观测";
                    else if (probeDependent && !s_NameProbeInjectionOn)
                        why = "触发时 enableInjection 关着，四个探针不在图里";
                    else if (probeDependent && !s_ProbeRequestSent)
                        why = "froxelVolume 取不到，probeRequested 没递进去";
                    else if (i == 14 && !s_NameProbeDebugOn)
                        why = "触发时 debugView 仍是 Off，这趟 pass 整个没被排入";
                    else if ((i == 8 || i == 9) && !s_NameProbeInjectionOn)
                        why = "enableInjection 关着，注入/积分本就不跑";
                    else if (rebakeDependent && !s_RebakeTriggered)
                        why = "重烘那一步没执行到（预热被提前打断？），静态表本次没有重跑过";
                    else if (rebakeDependent && !s_RebakeRequested)
                        why = "VistaAtmosphereFeature.current 取不到，重烘没发起，静态表本次不可能出现";

                    if (why != null)
                    {
                        nameGap++;
                        sb.Append("　　 [").Append(i).Append("] ").Append(k_PassNames[i])
                          .Append("　**不可判定**：").AppendLine(why);
                    }
                    else
                    {
                        nameGap++;
                        sb.Append("　　 [").Append(i).Append("] ").Append(k_PassNames[i])
                          .AppendLine("　**一次都没出现**，而这次它本该出现　→ "
                                    + "这个字符串与 AddComputePass/AddRasterRenderPass "
                                    + "那边的字面量对不上（或那趟 pass 被运行期条件剪掉了）");
                    }
                }
                sb.Append("　　 抓到：").AppendLine(namesOk.Length > 0 ? namesOk.ToString() : "（无）");
                sb.AppendLine("　　 顺带验了三条只写在注释里的契约：「一次 probeRequested 只跑一帧」"
                            + "（探针在稳态一节报 0）、「Off 档整趟 pass 不被记录」"
                            + "（第 14 格在稳态一节报 0，在这一节报有）、"
                            + "「静态表只在脏了那一帧跑」（0/1 在稳态一节报 0，"
                            + "而这一节主动 Create() 之后报有）。"
                            + "三条都是靠**两节读数方向相反**才成立的，缺一节就判不了。");
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
                   && compositeOccUnstable == 0 && compositeJudged && compositeDirOk
                   // froxel 两趟出货：与合成同一套四项（缺样本 / 仅 CPU / 出现次数不稳），
                   // 外加一项只有它有的 —— 关着却在跑。那一项在画面上完全看不见，
                   // 只有计时器能看见，所以它必须进门，不能只在正文里说一句。
                   && froxelMissing == 0 && froxelCpuOnly == 0
                   && froxelOccUnstable == 0 && froxelUnexpected == 0
                   // 自检/诊断五趟泄漏到稳态。同上：看不见的成本。
                   && absentLeak == 0
                   // 名字没验成也算缺口。这一条是本次新增的门里最要紧的一道：
                   // 没有它，一个打错的 pass 名会让上面那些「期望 0 个样本」的格子
                   // 全部打钩，整份报告绿着交付一套根本没接上的读数。
                   && nameGap == 0
                   // 旋钮响应门。它与上面每一道门的性质都不同：那些判的是
                   // 「读数自洽吗」，这一条判的是「读数与被测对象有因果关系吗」。
                   // 前者全绿而这一条红，意味着一份内部完全一致、却测错了东西的报告。
                   && responseGap == 0
                   // 档位对照门。它判的是「关掉近层，近层就真的不花钱了」——
                   // 与旋钮响应那一条同一类（因果），但方向相反：那一条证明加量会变贵，
                   // 这一条证明关掉会变免费。两条一起才封住「这个 marker 底下是不是
                   // 只有近层这一件事」，单独任何一条都留着一半的空间。
                   && modeGap == 0;
            if (ok) Debug.Log("[Vista] 模型 A 交叉验证完成  |  " + flat);
            else Debug.LogWarning($"[Vista] 模型 A 交叉验证有缺口（稳态缺样本 {missing}，GPU 可用 "
                                + $"{gpuUsable}/{k_SteadyIdx.Length}，出现次数不稳 {occUnstable}，"
                                + $"静态表泄漏 {staticLeak}；合成缺样本 {compositeMissing}，"
                                + $"合成仅 CPU {compositeCpuOnly}，合成出现次数不稳 {compositeOccUnstable}，"
                                + $"合成包线{(compositeJudged ? (compositeDirOk ? "通过" : "**方向反了**") : "**未判定**")}；"
                                + $"froxel 缺样本 {froxelMissing}，froxel 仅 CPU {froxelCpuOnly}，"
                                + $"froxel 出现次数不稳 {froxelOccUnstable}，froxel 关着却在跑 {froxelUnexpected}；"
                                + $"诊断 pass 泄漏进稳态 {absentLeak}；pass 名未验 {nameGap}；"
                                + $"旋钮响应{(responseGap == 0 ? "通过" : "**未通过/不可判定**")}；"
                                + $"档位对照{(modeGap == 0 ? "通过" : "**未通过/不可判定**")}）"
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

            // 名字验证正常收摊是在预热期末尾（TickNameProbe）。这里是**被打断**那条路的兜底：
            // 用户手停 play、看门狗超时中止，都会落到这里，而那时 debugView 还翻着。
            // 漏掉这一句的症状是：一次中断过的验证会把 RendererFeature 资产留在
            // debugView = IntegralRgb 上 —— 整个画面被调试视图覆盖，而且它**会被存盘**。
            // 幂等：s_DebugViewSaved 为 null 时一个字节都不写。
            RestoreDebugView();
            // 同一条兜底，另一个旋钮：被打断时 screenDivisor 还停在测试值（减半）上，
            // 而它同样**会被存盘** —— 症状是近层雾的分辨率悄悄翻了四倍，
            // 下次有人量性能时得到一组比出货配置贵得多的数字，且没有任何线索指向这里。
            RestoreDivisor();
            // 另外两个旋钮，同一条兜底。它们比 divisor 更需要这一句：divisor 停在测试值上
            // 至少还会让下一次测量出现一组反常的数字，而雾档位被静默留在 AerialPerspective、
            // 或者 enableInjection 被留在关上，画面只是「近处的雾淡了/没了」——
            // 一个没有任何报错、也不会被任何判据抓到的、被存进资产的配置改动。
            RestoreMode();
            RestoreInjection();
            Dispose(ref s_NameProbe);
            s_NameProbeClosed = true;
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
