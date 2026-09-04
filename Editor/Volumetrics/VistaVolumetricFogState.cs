using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista.EditorTools
{
    /// <summary>
    /// #20 覆盖性判据：近层 froxel 注入这条路径**到底有没有跑、跑在什么编译期状态下**。
    ///
    /// ────────────────────────────────────────────────── 它为什么不是一条数值判据
    ///
    /// 注入的能量对不对由 #19 的分布判据与 AP 那批误差判据兜着（同一套
    /// VistaEvaluateScatterSample）。本判据要抓的是另一类失效 —— 跨模块接线：
    ///
    ///   MainLightRealtimeShadow(float4)（URP ShaderLibrary/Shadows.hlsl:385）在
    ///   MAIN_LIGHT_CALCULATE_SHADOWS 未定义时**直接 return 1.0**。
    ///   也就是关键字漏设的症状是「整个场景没有光柱、且不报错」。
    ///
    /// 本项目的 compute 绕开了那个函数、直接调三个无关键字门的底层函数，把症状从
    /// 「静默全亮」降级成「全按级联 0 查」（远处光柱错位，看得见、能归因）。
    /// 但「降级」不等于「被覆盖」——「一个默认关闭、又没有判据覆盖的开关，
    /// 等于一段永远不会被发现写错的代码」，所以要有这份读数。
    ///
    /// ────────────────────────────────────────────────── 为什么必须跑真实帧
    ///
    /// #19 的切片判据走的是立即模式（自己 new 一份 VistaAtmosphereLuts + CommandBuffer），
    /// 那对纯数值的东西是对的。这里不行：要测的两样东西 ——
    /// _MAIN_LIGHT_SHADOWS_CASCADE 这个 GlobalKeyword 与 _MainLightShadowmapTexture
    /// 这个全局纹理 —— **只在渲染循环内存在**。立即模式下读回来会是
    /// 「关键字全 0、阴影图未绑」，而那是一个**完全合法**的组合（关掉阴影就长这样），
    /// 判据会一路全绿 —— 正是「布景不走被测代码路径的自检，其数字不变是空判据」。
    ///
    /// 所以流程是：置 <c>probeRequested</c> → <c>Camera.Render()</c> 同步渲一帧
    /// → 从 GraphicsBuffer 读回 <see cref="k_SlotTotal"/> 个 uint。
    ///
    /// ────────────────────────────────────────────────── 槽位语义住在这里
    ///
    /// VolumetricFog.compute 里的 VISTA_PROBE_* 下标与本文件的常量必须一致；
    /// VistaFroxelVolume.k_ShadowProbeSlots 是容量。三处对不上的症状不是报错 ——
    /// D3D11 上越界 UAV 写是**静默丢弃**，判据会读到恒为初值的格子并把它当成
    /// 「这一路没执行」。所以容量那条断言在下面是一格能失败的判据。
    /// </summary>
    static class VistaVolumetricFogState
    {
        // ---- 与 VolumetricFog.compute 的 VISTA_PROBE_* 逐一对应 ----
        const int k_SlotShadowMin     = 0;
        const int k_SlotShadowMax     = 1;
        const int k_SlotFlags         = 2;
        const int k_SlotCount         = 3;
        const int k_SlotShadowedCount = 4;
        const int k_SlotCamDriftMm    = 5;
        const int k_SlotNonFinite     = 6;
        const int k_SlotInjectMax     = 7;
        const int k_SlotShadowmapMin  = 8;
        const int k_SlotShadowmapMax  = 9;
        const int k_SlotShadowStrength = 10;
        const int k_SlotSmWidth       = 11;
        const int k_SlotSmHeight      = 12;
        const int k_SlotUrpSizeZ      = 13;
        // ---- #21 追加：真实帧里的积分表读数 ----
        // 这五格覆盖的是立即模式自检**覆盖不到**的两件事：RenderGraph 那条积分写入
        // 路径、以及真实雾（而不是合成介质）下 x = σ_t·Δ 的包络。
        const int k_SlotIntegralAlphaMax = 14;
        const int k_SlotIntegralLumMax   = 15;
        const int k_SlotIntegralNonFinite = 16;
        const int k_SlotSegXMin       = 17;
        const int k_SlotSegXMax       = 18;
        // ---- #22a 追加：时间重投影的读数 ----
        // 前三格是「在线」读数（注入核这一帧实际吃进去的那份重投影），
        // 中间两格是静止恒等性，六格 HIT_* 是失效分支的分类计数，
        // COVER_COUNT 是守恒式的总数，JITTER_SPREAD 是亮度死区下端的标定输入。
        const int k_SlotReprojOnlineCount = 19;
        const int k_SlotReprojOnlineOk    = 20;
        const int k_SlotReprojOnlineMask  = 21;
        const int k_SlotReprojStaticCount = 22;
        const int k_SlotReprojStaticErr   = 23;
        const int k_SlotReprojStaticMask  = 24;
        const int k_SlotReprojHitNoHist   = 25;
        const int k_SlotReprojHitBehind   = 26;
        const int k_SlotReprojHitOffScr   = 27;
        const int k_SlotReprojHitRange    = 28;
        const int k_SlotReprojHitLum      = 29;
        const int k_SlotReprojHitNaN      = 30;
        const int k_SlotReprojCoverCount  = 31;
        const int k_SlotReprojJitterSpread = 32;
        // ---- #22b 追加：抖动源的统计 + 横向形态两档的聚合对照 ----
        // 这一批全部由第九个核 FroxelJitterProbe 写，它**一张表都不读** ——
        // 抖动偏移是下标的纯函数。所以这些读数不依赖体积的 xy 尺寸、不依赖相机，
        // 唯一的体积依赖是⑳要用的片数（核内那道 slices == 64 的门）。
        const int k_SlotBnHist0     = 33;   // 33..40：源图 8 桶直方图
        const int k_SlotBnSum       = 41;
        const int k_SlotBnSq        = 42;
        const int k_SlotBnCount     = 43;
        const int k_SlotJitProcBase = 44;   // 44..54
        const int k_SlotJitBlueBase = 55;   // 55..65
        const int k_JitOfsSum       = 0;    // +0/+1/+2
        const int k_JitOfsSq        = 3;    // +3/+4/+5
        const int k_JitOfsNbX       = 6;
        const int k_JitOfsNbY       = 7;
        const int k_JitOfsXY        = 8;
        const int k_JitOfsXZ        = 9;
        const int k_JitOfsYZ        = 10;
        const int k_SlotJitCount    = 66;
        const int k_SlotAggColSum   = 67;
        const int k_SlotAggColSq    = 68;
        const int k_SlotAggColDAxis = 69;
        const int k_SlotAggColDDiag = 70;
        const int k_SlotAggSlcSum   = 71;
        const int k_SlotAggSlcSq    = 72;
        const int k_SlotAggSlcDAxis = 73;
        const int k_SlotAggSlcDDiag = 74;
        const int k_SlotAggCount    = 75;
        const int k_SlotJitDCol     = 76;
        const int k_SlotJitDSlc     = 77;
        const int k_SlotJitDProc    = 78;
        const int k_SlotJitDBlue    = 79;
        const int k_SlotBnWidth     = 80;   // ⑰d：核里 GetDimensions 问出来的尺寸
        const int k_SlotBnHeight    = 81;
        const int k_SlotTotal         = 82;

        const uint k_FlagCascade   = 1u;
        const uint k_FlagShadowmap = 2u;
        const uint k_FlagScreen    = 4u;
        const uint k_FlagSoft      = 8u;
        const uint k_FlagRan       = 16u;

        // 探针网格（VISTA_PROBE_DIM_XY / _Z）。固定值，不跟体积分辨率走。
        const int k_ProbeDimXY = 32;
        const int k_ProbeDimZ  = 16;
        const int k_ProbeCountExpected = k_ProbeDimXY * k_ProbeDimXY * k_ProbeDimZ;

        // ---- 定点编码的反向缩放，与 shader 里的 VistaProbeFixed 调用一一对应 ----
        const float k_ShadowScale   = 1.0e6f;
        const float k_DriftScale    = 1.0e3f;   // 毫米
        const float k_InjectScale   = 1.0e3f;
        const float k_IntegralAlphaScale = 1.0e6f;
        const float k_IntegralLumScale   = 1.0e3f;
        /// <summary>
        /// x = σ_t·Δ 的定点缩放。与 shader 里那次 VistaProbeFixed(segX, 1.0e9) 对应。
        /// 选 1e9 的理由写在 VolumetricFog.compute 那一行旁边：推导出来的包络下端是
        /// 8.1e-6，1e6 的缩放会把它压到 8 个刻度上 —— 那时「包络下端」这个读数
        /// 就落进自己尺子的地板里了。
        /// </summary>
        const float k_SegXScale     = 1.0e9f;

        /// <summary>fp16 的上限。注入表存预曝光辐亮度正是为了不撞这个数。</summary>
        const float k_Fp16Max = 65504f;

        /// <summary>
        /// 相机漂移门（毫米）。0 表示 &lt; 1 mm；给 1 是留一个定点截断的格子，
        /// 不给 0 的理由是「一个正好压在门上的读数，判定由打印出来的最后一位小数决定」——
        /// 这里的读数是整数，1 就是「亚毫米」。真出问题时这个数是米级的。
        /// </summary>
        const uint k_DriftGateMm = 1u;

        /// <summary>
        /// atlas「不是一个常数」的门。定点分辨率是 1e-6，实测的 max − min 是 O(1)，
        /// 所以 1e-3 远在地板之上、也远在被拒答案（常数 ⇒ 差为 0）之上。
        /// </summary>
        const float k_AtlasSpreadGate = 1.0e-3f;

        /// <summary>
        /// 推导出来的 x = σ_t·Δ 包络。下端 = 晴空最近一段，上端 = 能见度 50 m 的雾
        /// 最远一段。这是**算**出来的配置区间，不是这一台布景的读数 ——
        /// 所以下面那一格是「实测落在推导区间内」的包容性判据，它抓的是
        /// 「x 的量级被某处改动整体挪走了」，抓不到一个数量级内的偏差。
        /// 区间宽到 3.6e4 倍这件事必须在报表上说出来，否则一个宽门会被读成一道紧门。
        /// </summary>
        const float k_SegXDerivedMin = 8.1e-6f;
        const float k_SegXDerivedMax = 0.289f;

        /// <summary>
        /// VistaSegmentIntegral 现在用的级数分支阈值（AtmosphereScattering.hlsl:492）。
        /// </summary>
        const float k_SeriesThresholdShipped = 1.0e-4f;

        /// <summary>
        /// 那个阈值的最优值 x* = (24·2⁻²³)¼ = 4.113e-2。#25 要把 shipped 换成它，
        /// 而这一格的读数决定了那次替换的影响面 —— 见下面判据⑫的说明。
        /// </summary>
        const float k_SeriesThresholdOptimum = 4.113e-2f;

        // ---- #22a：重投影两个定点缩放，与 shader 里的 VistaProbeFixed 调用一一对应 ----
        const float k_ReprojStaticErrScale = 1.0e9f;
        const float k_ReprojJitterScale    = 1.0e6f;

        /// <summary>
        /// 静止恒等性的地板 —— **实测基线，不是推导上界**。
        ///
        /// 原先这里写的是一个推导：「相机在原点 1 km 量级内时投影是几个 fp32 ulp（~1e-7），
        /// log/exp 各再放一次，取 1e-6 做保守上界」。这条推导被第一次实测**证伪**了：
        /// 本机（RTX 3060 / D3D11、布景 138×74×64）量到 6.962e-6，是那个「上界」的 7 倍。
        /// 原因也清楚 —— 格心世界坐标到 64 m 量级、decode/encode 各带一次 pow/log
        /// （超越函数不是半 ulp），放大之后落在 1e-6~1e-5 而不是 1e-7。
        ///
        /// 于是照「当推导给不出紧的上限时，诚实地把门标成实测基线」办：取与实测同量级的
        /// 1e-5 当地板，门仍摆在它与「要拒绝的最小错答案」的几何中点。
        /// 留着这段被证伪的推导是有用的 —— 它记着「这个量不该被当成 fp32 单次往返」。
        /// </summary>
        const float k_ReprojStaticFloorMeasured = 1.0e-5f;

        /// <summary>
        /// 亮度死区下端与抖动散布的关系。死区下端必须**高于**实测散布，否则抖动
        /// 自己制造的亮度变化会被判成「场景变了」而降权 —— 那会亲手毁掉它本该
        /// 保护的累积。这里不设额外余量系数：散布是 max 统计量，本身已经是上界。
        /// </summary>
        const string k_JitterDeadbandSymptom = "抖动打开后累积失效、画面比关抖动时更噪";

        // ---- 与 FroxelVolume.hlsl 的 VISTA_REPROJ_* 逐一对应 ----
        const uint k_ReprojNoHistory = 1u;
        const uint k_ReprojBehind    = 2u;
        const uint k_ReprojOffScreen = 4u;
        const uint k_ReprojOutRange  = 8u;
        const uint k_ReprojLuminance = 16u;
        const uint k_ReprojNaN       = 32u;

        // ================================================================ #22b 抖动源
        /// <summary>
        /// 抖动探针的窗口边长。蓝噪声瓦片是环形的、周期 64，取一个**整周期**是
        /// ⑰的「每桶正好 512」与⑳的对角恒等式两条精确期望的前提。
        /// </summary>
        const int k_JitProbeDim = 64;
        const int k_JitProbeCountExpected = k_JitProbeDim * k_JitProbeDim;

        const float k_JitProbeScale  = 1.0e5f;   // 矩：与 shader 的 VISTA_JITTER_PROBE_SCALE 一致
        const float k_JitProbeDScale = 1.0e9f;   // 逐点差的 max：VISTA_JITTER_PROBE_D_SCALE

        /// <summary>直方图每桶的**精确**期望，见 VolumetricFog.compute 里那段点算。</summary>
        const int k_BnBinExpected = k_JitProbeCountExpected / 8;   // 512

        /// <summary>
        /// 源图均值的门。Σv = 2048 精确 ⇒ 均值 0.5 精确。
        /// 定点累加是**向零截断**的（VistaProbeFixed），每次 add 少算 &lt; 1e-5，
        /// 4096 次之后均值只会**偏低** ≤ 1e-5 ⇒ 相对 2e-5。门取 1e-4，留 5 倍。
        /// 这一格判的不是统计，是精确算术：0.5 是算出来的，不是「打印出来看看」。
        /// </summary>
        const float k_BnMeanGate = 1.0e-4f;

        /// <summary>
        /// 8 bit 秩均匀（每阶正好 16 个像素、值取 k/255）下的解析方差：
        /// E[v²] = 511/(6·255) = 0.3339869 ⇒ Var = 0.0839869。
        /// **只当 ⓘ 交叉核对**：⑱⑲⑳的归一化用的是实测的逐通道矩，
        /// 判据的载荷路径上不留任何解析前提。
        /// </summary>
        const float k_BnVarAnalytic   = 0.0839869f;
        const float k_BnVarContinuous = 1f / 12f;   // 连续均匀分布，差的那一点是 8 bit 量化

        /// <summary>
        /// 4096 个样本上相关系数的抽样标准差 ≈ 1/√n。程序化档那道「≈ 0」的带
        /// 与⑲那道「三通道不相关」的带都按它的倍数摆，不是随手取的圆整数。
        /// </summary>
        const float k_JitCorrSigma = 1f / 64f;          // 1/√4096 = 1.5625e-2
        const float k_JitCorrNullGate = 3f * k_JitCorrSigma;  // 4.6875e-2，≈3σ
        const float k_JitCorrTapGate  = 6f * k_JitCorrSigma;  // 9.375e-2，≈6σ；要拒绝的是 ρ = 1

        /// <summary>
        /// 聚合场逐点差的 fp32 求和序地板（保守上界）。
        ///
        /// Ā = (Σ_{z&lt;64} j.x) / 64，|j.x| ≤ 0.5 ⇒ 部分和 |acc| ≤ 32。
        /// 每次 add 的舍入 ≤ ½eps·|acc| = 6.0e-8 × 32 = 1.9e-6，64 次 ⇒ 1.2e-4，
        /// 除 64 之后 1.9e-6；两列各一份 ⇒ **3.8e-6**。
        /// 这是一个很松的上界（部分和实际在 0 附近随机游走），但门要按上界摆。
        /// </summary>
        const float k_AggDiagFloor = 3.8e-6f;

        /// <summary>
        /// 对角恒等式的门。要拒绝的最小错答案是「DDIAG 与 DAXIS 同量级」，
        /// 也就是 O(1e-1)；门取地板上界 3.8e-6 与 1e-1 的**几何中点** = 6.2e-4。
        /// </summary>
        const float k_AggDiagGate = 6.2e-4f;

        /// <summary>
        /// 对照门：轴向差必须**远离** 0。
        /// 少了这一格，一个「Ā 恒为常数」的 bug（= 抖动整个失效）会把对角恒等式假通过 ——
        /// 而恒为常数正是抖动失效的样子。四格表里必须**恰好一格** ≈ 0。
        /// </summary>
        const float k_AggAxisGate = 1.0e-2f;

        /// <summary>
        /// ⑳b 的量级门。逐片独立时 Ā 是 N 个场的均值，方差按 1/N 收缩
        /// （1/64 = 1.5625e-2）。这里只摆一道 1/8 的**量级**门，不摆 ≈1/N 的紧门：
        /// 那 N 个场并不独立（固定步进 ⇒ 抽的是同一条反对角线），
        /// 1/N 是独立情形的值，拿它当紧门是「用一个前提不成立的公式去摆门」。
        /// </summary>
        const float k_AggVarRatioGate = 0.125f;

        /// <summary>
        /// ⑳a 那条跨槽位对账的相对门（两组独立累加的同一个量）。
        ///
        /// 地板不是 0：⑳走的是 64 次 fp32 累加再除 64，⑲走的是一次求值。
        /// 逐列一致档下 64 个被加数逐位相同，但 k·x 在 fp32 上并非对每个 k 都精确
        /// （3x 就要多两位），所以 Ā 相对 j.x 有 ≤ 64·½eps = 3.8e-6 的相对误差。
        /// 它传到**方差**上还要放大两次：E[v²] 的相对误差 ×2，再乘上
        /// E[v²]/Var = 0.334/0.084 = 3.98 的杠杆 ⇒ ≤ 3.0e-5。
        /// 定点截断的错配项（两组各截一次、只有跨格点时才差 1e-5）RMS 只有 ~1e-6，
        /// 不是主导项。门取 1e-3，对方差那一路留 ×33。
        /// </summary>
        const float k_AggCrossGate = 1.0e-3f;

        [MenuItem("Window/Vista/Log Volumetric Fog State", priority = 142)]
        static void RunFromMenu()
        {
            var sb = new StringBuilder();
            Run(sb);
            // MCP 那条通道会把多行日志截断，所以拍平成一行；即便拍平了也可能截，
            // 完整内容从 Editor.log 里读。
            Debug.Log(sb.ToString().Replace("\r", string.Empty).Replace("\n", "  |  "));
        }

        static void Run(StringBuilder sb)
        {
            sb.AppendLine("=== Vista 体积雾状态（#20 注入 + #21 积分 + #22a 时间重投影 + #22b 抖动源 覆盖性判据）===");

            var cam = FindGameCamera();
            if (cam == null)
            {
                sb.AppendLine("✘ 场景里没有启用的 Game 相机。本判据必须跑真实帧 —— "
                            + "见本文件头注「为什么必须跑真实帧」。");
                return;
            }
            sb.AppendLine($"相机：{cam.name}  near {cam.nearClipPlane:F3} m  {cam.pixelWidth}×{cam.pixelHeight}");

            // 预热一帧：VistaAtmosphereFeature.current 是在 RendererFeature.Create 里注册的，
            // 而 Create 要等 URP asset 第一次被用到才跑。不预热的话「刚打开工程就点菜单」
            // 会得到一个 current == null 的假失败。
            RenderOnce(cam);

            var feature = VistaAtmosphereFeature.current;
            if (feature == null)
            {
                sb.AppendLine("✘ VistaAtmosphereFeature.current 为 null：UniversalRendererData 上"
                            + "没挂 Vista Atmosphere，或者 compute 资源缺失（Create 里提前 return 了）。");
                return;
            }

            var settings = feature.volumetricFog;
            if (settings == null)
            {
                sb.AppendLine("✘ feature.volumetricFog 为 null。");
                return;
            }

            // 「一个默认关闭、又没有判据覆盖的开关」—— 这里就是点名它的地方。
            // 不自动打开：判据不该改被测对象的配置，否则「线上是关着的」这件事永远不会被发现。
            sb.AppendLine($"开关 VistaVolumetricFogSettings.enableInjection = {settings.enableInjection}"
                        + $"（screenDivisor {settings.screenDivisor}, sliceCount {settings.sliceCount}, "
                        + $"farDistanceMeters {settings.farDistanceMeters:F1} m）");
            if (!settings.enableInjection)
            {
                sb.AppendLine("ⓘ 注入是关着的，下面所有读数都不会产生 —— 本次判据**未覆盖任何东西**。");
                sb.AppendLine("  要跑：在 UniversalRendererData 的 Vista Atmosphere 上勾选"
                            + " Volumetric Fog ▸ 开发中（#21）▸ Enable Injection。");
                return;
            }

            var volume = feature.froxelVolume;
            if (volume == null || !volume.isValid)
            {
                sb.AppendLine("✘ froxelVolume 不可用：VolumetricFog.compute 缺失，或九个核里有编译不出来的"
                            + "（FroxelPlaceholder / FroxelSliceVerify / FroxelInjection / FroxelShadowProbe / "
                            + "FroxelIntegration / FroxelSynthMedium / FroxelIntegralVerify / FroxelReprojProbe / "
                            + "FroxelJitterProbe）。isValid 会 AND 掉全部九个下标 ≥ 0 —— "
                            + "少一个核的症状是整个近层雾不生效，而不是那一个核静默失效。");
                return;
            }

            // 调试视图的档位。打印**夹紧后**的切片下标，而且走
            // VistaVolumetricFogSettings.ResolveDebugSlice —— 与渲染路径同一份实现。
            // 各写一份的症状是日志说「看的是第 63 片」而画面上是第 127 片。
            {
                int depth = volume.allocatedDesc.HasValue ? volume.allocatedDesc.Value.depth : 0;
                int slice = VistaVolumetricFogSettings.ResolveDebugSlice(settings.debugSlice, depth);
                sb.Append($"调试视图 debugView = {settings.debugView}"
                        + $"（gain {settings.debugGain:F2}, slice 请求 {settings.debugSlice} ⇒ 实际 {slice}");
                if (slice != settings.debugSlice)
                    sb.Append($"，已夹到 [0, {Mathf.Max(0, depth - 1)}]");
                sb.AppendLine("）");
                if (settings.debugView == FroxelDebugView.Off)
                    sb.AppendLine("  ⓘ Off 档整趟 debug pass 不排入（失能态 = 零态），"
                                + "所以它不会占一次深度拷贝。");
                else
                    sb.AppendLine("  ⚠ 非 Off 档会**整屏替换**最终画面。这是诊断视图，"
                                + "不要留在出货配置里。");
            }

            // ---- 请求 + 渲一帧 ----
            volume.probeRequested = true;
            RenderOnce(cam);

            var buffer = volume.shadowProbeBuffer;
            if (buffer == null)
            {
                sb.AppendLine("✘ shadowProbeBuffer 仍为 null：探针 pass 一次都没记录过。"
                            + "probeRequested 被消费的地方只有 VistaAtmospherePass 里那段 #if UNITY_EDITOR，"
                            + "说明 froxelEnabled 在这一帧是 false（相机类型闸门 / Prepare 失败）。");
                return;
            }

            // 容量断言。对不上时下标 8 的 InterlockedMin 会被 D3D11 静默丢弃，
            // 而那一格恒为初值 —— 会被读成「阴影图这一路没跑」。
            bool capacityOk = buffer.count == k_SlotTotal
                           && VistaFroxelVolume.k_ShadowProbeSlots == k_SlotTotal;
            sb.AppendLine($"{Mark(capacityOk)}判据⓿ 槽位容量：buffer.count = {buffer.count}, "
                        + $"k_ShadowProbeSlots = {VistaFroxelVolume.k_ShadowProbeSlots}, 本文件期望 {k_SlotTotal}");
            if (!capacityOk)
            {
                sb.AppendLine("  ⚠ 容量不一致，下面的读数不可信（越界 UAV 写是静默丢弃）。");
                return;
            }

            var raw = new uint[k_SlotTotal];
            buffer.GetData(raw);

            sb.AppendLine($"原始槽位：[{string.Join(", ", raw)}]");

            // ---------------------------------------------------------------- 判据① 跑过没有
            uint flags = raw[k_SlotFlags];
            bool ran = (flags & k_FlagRan) != 0u;
            sb.AppendLine($"{Mark(ran)}判据① 「跑过」位：flags = 0x{flags:X}");
            if (!ran)
            {
                sb.AppendLine("  ⚠ flags == 0 时，「从未派发」与「级联关+阴影图未绑+屏幕空间关+软阴影关」"
                            + "（一个完全合法的组合）在报表上长得一模一样 —— 这就是 RAN 位存在的理由。"
                            + "本次读数是前者。");
                return;
            }

            // ---------------------------------------------------------------- 判据② 编译期关键字
            bool hasCascade   = (flags & k_FlagCascade)   != 0u;
            bool hasScreen    = (flags & k_FlagScreen)    != 0u;
            bool hasSoft      = (flags & k_FlagSoft)      != 0u;
            bool hasShadowmap = (flags & k_FlagShadowmap) != 0u;

            // 期望值从 URP asset 推，而不是「打印出来看看」：判据的输入不含被测对象的值。
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            int cascadeCount = urp != null ? urp.shadowCascadeCount : 0;
            bool urpShadowsOn = urp != null && urp.supportsMainLightShadows;
            bool cascadeExpected = urpShadowsOn && cascadeCount > 1;

            bool kwOk = hasCascade == cascadeExpected && !hasScreen && !hasSoft;
            sb.AppendLine($"{Mark(kwOk)}判据② 编译期关键字：CASCADE {hasCascade}（URP asset: "
                        + $"mainLightShadows {urpShadowsOn}, cascades {cascadeCount} ⇒ 期望 {cascadeExpected}）, "
                        + $"SCREEN {hasScreen}（必须 false）, SOFT {hasSoft}（必须 false）");
            if (hasScreen)
                sb.AppendLine("  ⚠ _MAIN_LIGHT_SHADOWS_SCREEN 被定义了：TransformWorldToShadowCoord 会走"
                            + "屏幕 NDC 那一支，而 compute 里没有屏幕坐标可言。见 compute 头部的 pragma 说明。");
            if (hasSoft)
                sb.AppendLine("  ⚠ _SHADOWS_SOFT* 被定义了：SampleShadowmap 会走 4~9 tap 的 tent 滤波，"
                            + "逐 froxel 的成本按 tap 数翻倍。本节明确选的是硬阴影 1 tap。");
            if (!hasCascade && cascadeExpected)
                sb.AppendLine("  ⚠ 级联关键字漏设：TransformWorldToShadowCoord 会**全按级联 0** 查"
                            + "（cascadeIndex = 0），远处的光柱会整体错位。");

            // ---------------------------------------------------------------- 判据③ 阴影图绑定
            // 期望同样从 URP asset 推。注意 supportsMainLightShadows 关掉时
            // cameraData.maxShadowDistance 是 0，远边界的夹紧会被跳过（见 ResolveFarDistance）。
            bool shadowmapOk = hasShadowmap == urpShadowsOn;
            sb.AppendLine($"{Mark(shadowmapOk)}判据③ 阴影图绑定：_VistaFroxelCameraWS.w ⇒ {hasShadowmap}"
                        + $"（URP asset supportsMainLightShadows = {urpShadowsOn}）");
            if (!hasShadowmap)
                sb.AppendLine("  ⓘ 阴影图未绑 ⇒ VistaFroxelSunShadow 恒返回 1.0，画面上是"
                            + "「有雾、没有光柱」。下面判据⑤（不恒为 1）在这条路径上**无法覆盖**。");

            // ---------------------------------------------------------------- 判据④ 网格全走过
            uint count = raw[k_SlotCount];
            bool countOk = count == (uint)k_ProbeCountExpected;
            sb.AppendLine($"{Mark(countOk)}判据④ 探针网格：计数 {count} / 期望 "
                        + $"{k_ProbeCountExpected}（{k_ProbeDimXY}×{k_ProbeDimXY}×{k_ProbeDimZ}）");
            if (!countOk)
                sb.AppendLine("  ⚠ 计数少一截通常是 dispatch 的 group 数与 numthreads 对不上，"
                            + "或者 _VistaFroxelSize 是 0（那时核在 any(size == 0u) 处提前 return）。");

            // ---------------------------------------------------------------- 判据⑤ 阴影不恒为 1
            float shadowMin = raw[k_SlotShadowMin] == uint.MaxValue
                ? float.NaN : raw[k_SlotShadowMin] / k_ShadowScale;
            float shadowMax = raw[k_SlotShadowMax] / k_ShadowScale;
            uint shadowed = raw[k_SlotShadowedCount];

            bool rangeOk = !float.IsNaN(shadowMin) && shadowMin >= 0f && shadowMax <= 1.0f + 1e-5f
                        && shadowMin <= shadowMax;
            sb.AppendLine($"{Mark(rangeOk)}判据⑤a 阴影值域：min {Fmt(shadowMin)} / max {Fmt(shadowMax)}"
                        + "（必须落在 [0, 1] 且 min ≤ max）");
            if (raw[k_SlotShadowMin] == uint.MaxValue)
                sb.AppendLine("  ⚠ min 槽仍是初值 uint.MaxValue：InterlockedMin 一次都没执行。"
                            + "（初值由 CPU 侧 ResetShadowProbeBuffer 写 —— 填成 0 会把"
                            + "「一个点都没被遮」伪装成「全被遮」，所以这一格的初值必须是 MaxValue。）");

            bool shadowedOk = shadowed > 0u;
            sb.AppendLine($"{(hasShadowmap ? Mark(shadowedOk) : "ⓘ ")}判据⑤b 不恒为 1："
                        + $"被遮点数 {shadowed} / {count}（shadow < 0.999）");

            // 阴影强度：min 读数的归因输入。SampleShadowmap 返回 lerp(1, s, strength)，
            // 所以「全被遮」的地板是 1 − strength。不打印它的话 min = 0.2 是个谜数。
            //
            // 这一格**刻意不设门**：shadowMin ≥ 1 − strength 是 lerp 的值域，
            // 由构造保证，写成门就是「一个本轮无法失败的守卫」。
            float strength = raw[k_SlotShadowStrength] / k_ShadowScale;
            float shadowFloor = 1f - strength;
            sb.AppendLine($"  ⓘ _MainLightShadowParams.x（阴影强度）= {Fmt(strength)}"
                        + $" ⇒ 全遮地板 1 − strength = {Fmt(shadowFloor)}；实测 min = {Fmt(shadowMin)}"
                        + $"（差 {(shadowMin - shadowFloor).ToString("+0.000000;-0.000000")}）。"
                        + "min 触到地板 ⇒ 确实有采样点被完全遮住，不只是半遮。");

            if (hasShadowmap && !shadowedOk)
            {
                sb.AppendLine("  ⚠ 一个点都没被遮。三种成因：");
                sb.AppendLine("    (a) 阴影图本身是空的（场景里没有投影物 / 光被关了）—— 看判据⑥b;");
                sb.AppendLine($"    (b) 阴影强度是 0（读数 {Fmt(strength)}）⇒ SampleShadowmap 恒返回 1，"
                            + "与代码无关;");
                sb.AppendLine("    (c) 阴影图有内容、强度非 0，但我们查出来恒 1（矩阵、级联下标、"
                            + "isPerspectiveProjection 传错）—— 这一种才是本模块的 bug。");
                sb.AppendLine("  这条还依赖场景布景：近层体只有 "
                            + $"{(volume.allocatedDesc.HasValue ? volume.allocatedDesc.Value.handoffMeters : 0f):F1} m 深，"
                            + "相机前方这段距离内必须有投影物，否则本格是布景问题、不是代码问题。");
            }
            else if (!hasShadowmap)
            {
                sb.AppendLine("  ⓘ 阴影图未绑，本格**未被覆盖**（不是通过）。"
                            + "「本轮无法失败的守卫要在报告里点名」。");
            }

            // ---------------------------------------------------------------- 判据⑥ 阴影图有没有内容
            if (hasShadowmap)
            {
                // ---- ⑥a atlas 尺寸 ----
                // 核内的尺寸走 _MainLightShadowmapTexture.GetDimensions()，**不读**
                // URP 的 _MainLightShadowmapSize：那个全局只在软阴影档下发
                //（MainLightShadowCasterPass.cs:334 把 _ShadowOffset0/1 与 _ShadowmapSize
                // 三行一起包在 if (shadowData.supportsSoftShadows) 里），硬阴影档下它
                // 保持上一次的值、甚至从没被写过。第一版按它算采样坐标，smCoord 恒为 (0,0)，
                // ⑥b 读到一个常数并**红了** —— 而红的理由不是场景没有投影物
                //（同一趟里有点被遮）。这一格把那个坑变成一个能失败的数字。
                uint smW = raw[k_SlotSmWidth];
                uint smH = raw[k_SlotSmHeight];
                bool sizeOk = smW > 0u && smH > 0u;
                sb.AppendLine($"{Mark(sizeOk)}判据⑥a atlas 尺寸：GetDimensions ⇒ {smW}×{smH}"
                            + "（必须都 > 0，否则 ⑥b 的采样坐标全塌到 (0,0)）");
                sb.AppendLine($"  ⓘ 对照：URP 的 _MainLightShadowmapSize.z = {raw[k_SlotUrpSizeZ]}"
                            + $"（真实 atlas 宽是 {smW}）。它只在软阴影档才被下发"
                            + "（MainLightShadowCasterPass.cs:334 把 _ShadowOffset0/1 与 _ShadowmapSize"
                            + "三行一起包在 if (shadowData.supportsSoftShadows) 里），"
                            + "所以硬阴影档下读到的是**别人留下的脏值**：1 = 某个走过 empty-shadowmap "
                            + "路径的相机（SceneView / 预览 / 反射探针）设的 s_EmptyShadowmapSize "
                            + "= (1,1,1,1)（同文件 :34, k_EmptyShadowMapDimensions = 1）。"
                            + "这比读到 0 更坏 —— 1×1 是一个看起来**完全合法**的尺寸，"
                            + "拿它算坐标全塌到 (0,0) 且不报任何错。"
                            + "URP 自己不受影响：那个尺寸只被 tent 软阴影的 offset 用，1 tap 不读它。");

                // ---- ⑥b atlas 内容 ----
                // 判的是「atlas 是不是一个常数」，不是「min 等于某个清空值」。
                //
                // 为什么不能拿 min 去跟清空值比：清空值取决于 UNITY_REVERSED_Z。
                // D3D11/Vulkan/Metal 上 URP 的阴影矩阵带 reversed-Z 翻转，清空态是
                // **0.0**；按「清空态是 1.0」写的门会在 atlas 全空时读到 min = 0
                // 并宣布「有内容」—— 一个**假通过**，而且它坏在本判据唯一的职责上
                //（把「atlas 空」与「采样错」分开）。
                // 「空 atlas 是一个常数」不依赖任何深度约定，两个方向都成立。
                bool minIsInit = raw[k_SlotShadowmapMin] == uint.MaxValue;
                float smMin = minIsInit ? float.NaN : raw[k_SlotShadowmapMin] / k_ShadowScale;
                float smMax = raw[k_SlotShadowmapMax] / k_ShadowScale;
                float spread = minIsInit ? 0f : smMax - smMin;

                bool smOk = !minIsInit && spread > k_AtlasSpreadGate;
                sb.AppendLine($"{(sizeOk ? Mark(smOk) : "ⓘ ")}判据⑥b 阴影图内容"
                            + $"（归因用，与我们的采样无关）：atlas 深度 min {Fmt(smMin)}"
                            + $" / max {Fmt(smMax)} ⇒ 跨度 {Sci(spread)}"
                            + $"（门 > {Sci(k_AtlasSpreadGate)}；这道门要拒绝的最小错答案是"
                            + "「atlas 是常数」⇒ 跨度 0）");
                if (!sizeOk)
                    sb.AppendLine("  ⓘ ⑥a 未通过 ⇒ 本格**未被覆盖**（不是失败）。"
                                + "尺寸塌成 0 时核内那一段被 smW > 0 跳过，读数留在初值上。");
                else
                {
                    sb.AppendLine($"  ⓘ 顺带读出了深度约定：min ≈ 0 且 max > 0 ⇒ reversed-Z"
                                + "（清空态 0 = 无穷远）；反之 max ≈ 1 ⇒ 正向 Z。"
                                + $"本次 {(smMin < 0.5f && smMax > 0.5f ? "reversed-Z" : "无法判定，见上面两个数")}。");
                    if (!smOk)
                        sb.AppendLine("  ⚠ atlas 是常数 ⇒ 判据⑤b 的成因是 (a)，不是我们的代码。"
                                    + "「第一个可疑对象被修掉、读数却没变时，必须回头重新归因」——"
                                    + "这一格就是那个「回头」的落点。"
                                    + "（残余风险：全部 1024 个采样点在光空间共面时 atlas 也是常数，"
                                    + "那要求整个 atlas 只有一个正对光的平面，可忽略。）");
                }
            }
            else
            {
                sb.AppendLine("ⓘ 判据⑥a/⑥b 未覆盖：阴影图未绑，核内那一段 LOAD 被 w < 0.5 跳过了。");
            }

            // ---------------------------------------------------------------- 判据⑦ 注入表健康
            uint nonFinite = raw[k_SlotNonFinite];
            bool finiteOk = nonFinite == 0u;
            sb.AppendLine($"{Mark(finiteOk)}判据⑦ 注入表有限性：非有限 froxel {nonFinite} / {count}");
            if (!finiteOk)
                sb.AppendLine("  ⚠ NaN/Inf 会顺着三线性插值蔓延到全屏。头号嫌疑是"
                            + " SampleShadowmap 的 isPerspectiveProjection 传了 true —— "
                            + "TransformWorldToShadowCoord 走 #else 那一支返回的 w 恒为 0，"
                            + "那会做一次 xyz /= 0。");

            // ---------------------------------------------------------------- 判据⑧ fp16 余量
            float injectMax = raw[k_SlotInjectMax] / k_InjectScale;
            bool fp16Ok = injectMax < k_Fp16Max;
            float headroom = injectMax > 0f ? k_Fp16Max / injectMax : float.PositiveInfinity;
            sb.AppendLine($"{Mark(fp16Ok)}判据⑧ fp16 余量：注入 rgb 最大值 {Sci(injectMax)}"
                        + $"（预曝光后），fp16 天花板 {k_Fp16Max:F0} ⇒ 余量 ×{Sci(headroom)}");
            sb.AppendLine("  ⓘ 这个读数是从 RGBA16F 的 UAV 里**读回来的**，不是 CPU 侧重算的 —— "
                        + "fp16 的饱和只有走一趟纹理往返才算量过。撞顶的症状是一整列雾被压平，"
                        + "而 #18 量到过：浓雾 + 低太阳的源项到 3.7e5 cd/m²，绝对单位下必然撞顶。"
                        + "预曝光这条约定就是为它存在的。");

            // ---------------------------------------------------------------- 判据⑨ 隐藏依赖
            uint driftMm = raw[k_SlotCamDriftMm];
            bool driftOk = driftMm <= k_DriftGateMm;
            sb.AppendLine($"{Mark(driftOk)}判据⑨ 隐藏依赖 |_WorldSpaceCameraPos − _VistaFroxelCameraWS|"
                        + $" = {driftMm} mm = {Sci(driftMm / k_DriftScale)} m（门 ≤ {k_DriftGateMm} mm）");
            sb.AppendLine("  ⓘ GetMainLightShadowFade（Shadows.hlsl:434）用 URP 的 _WorldSpaceCameraPos"
                        + "算淡出距离，而 _VistaFroxelCameraWS 存在的理由恰恰是不信任那个全局。"
                        + "仍然调它、不自己重写那一行 saturate —— 「同一个量的第二份实现连 8 行的"
                        + "辅助函数也算」。代价就是这条依赖，而这一格把它变成一个**能失败的数字**，"
                        + "而不是一句注释里的担忧。真出问题时这个数是米级的。");

            // ---------------------------------------------------------------- 判据⑩ 探针核跑过没有
            // 这一格是⑫「不是空判据」的唯一证明。
            //
            // 它证明的**只是探针核跑过**，不是积分核跑过 —— segX 是从注入表的 alpha
            // 现算的，探针核跑了它就有值，与积分表写没写无关。把这两件事混成一格
            // 的症状是：积分派发漏掉之后，⑩ 照样全绿，而它绿的理由是错的。
            // 「积分表被写过」是独立的一格（⑪c）。
            //
            // 为什么用 segX 的 min 槽位当哨兵，而不是别的槽位：
            // 它的初值是 uint.MaxValue（VistaFroxelVolume.k_ShadowProbeMinSlots 里那个 17），
            // 对应 x = 4.29 —— 一个物理上不可能的段光学厚度，所以哨兵与被测量不会撞车。
            // 换成 max 槽位就不行：那些初值是 0，而 0 是合法读数。
            uint segXMinRaw = raw[k_SlotSegXMin];
            bool probeRan = segXMinRaw != uint.MaxValue;
            sb.AppendLine($"{Mark(probeRan)}判据⑩ 探针核执行性：SEG_X_MIN 槽位"
                        + (probeRan ? $" = {segXMinRaw}（已被写过）" : " 仍是初值 uint.MaxValue"));
            if (!probeRan)
                sb.AppendLine("  ⚠ 下面判据⑪⑫的读数全部是初值，**不构成任何证据**。"
                            + "头号嫌疑：探针写那一段被 probeRequested 的门挡掉了。");

            // ---------------------------------------------------------------- 判据⑪ 积分表健康
            uint integralNonFinite = raw[k_SlotIntegralNonFinite];
            float alphaMax = raw[k_SlotIntegralAlphaMax] / k_IntegralAlphaScale;
            float lumMax   = raw[k_SlotIntegralLumMax] / k_IntegralLumScale;

            bool integralFiniteOk = probeRan && integralNonFinite == 0u;
            sb.AppendLine($"{Mark(integralFiniteOk)}判据⑪a 积分表有限性：非有限 froxel"
                        + $" {integralNonFinite} / {count}");
            if (integralNonFinite != 0u)
                sb.AppendLine("  ⚠ 头号嫌疑是 VistaSegmentIntegral 的 σ → 0 那一支："
                            + "S·(1 − exp(−σ·dt))/max(σ,1e-30) 在两支都算、再用 lerp 选的写法下，"
                            + "被丢弃的那一支产生的 NaN 会被 lerp 带回来。");

            // alpha = 1 − T 是**定义上**落在 [0,1] 的量。这一格判的不是精度，是定义性不变量：
            // > 1 只能来自「乘反了」「T 的递推漏乘」这类结构性错误，
            // 而那种错误在画面上的症状是雾偏浓 —— 一个「物理上讲得通」的漂移，
            // 也就是本项目记过的最容易被接受、因此最容易掩盖污染的那种形态。
            bool alphaRangeOk = probeRan && alphaMax <= 1.0f;
            sb.AppendLine($"{Mark(alphaRangeOk)}判据⑪b 积分 alpha 定义域：max(1 − T)"
                        + $" = {Sci(alphaMax)}（定义上 ≤ 1，定点分辨率 {Sci(1f / k_IntegralAlphaScale)}）");

            // 「积分表真的被写过」。存 1 − T 而不是 T 这条约定在这里第二次付钱：
            // 清空态 alpha = 0，所以 max > 0 就是「有人写过」，不需要额外的哨兵槽位。
            // 存 T 的话清空态是 0 = 全黑，而 0 同时也是「雾浓到不透明」的合法读数。
            float alphaFloor = 1f / k_IntegralAlphaScale;
            bool integralWritten = probeRan && alphaMax > alphaFloor;
            sb.AppendLine($"{Mark(integralWritten)}判据⑪c 积分表被写过：max(1 − T) = {Sci(alphaMax)}"
                        + $" > 定点地板 {Sci(alphaFloor)}");
            sb.AppendLine("  ⓘ 这一格**只在布景有介质时能失败**：雾关掉之后只剩空气，"
                        + "50 m 内的 1 − T 会落到定点地板附近，那时「没写」与「太淡」再次同形。"
                        + "所以它的作用域必须写出来 —— 下面那条上界是它的归因输入。");

            var fog = feature.fog;
            if (fog != null && fog.enabled && volume.allocatedDesc.HasValue)
            {
                // 均匀介质上界：σ_t 是**地面**处的值（高度雾只会更淡），所以
                // 1 − exp(−σ_t·d) 是 alpha 的一个真上界。σ_t 直接取 fog.extinctionPerKm，
                // 不在这里重算 —— 「同一个量的第二份实现连 8 行的辅助函数也算」。
                float sigmaT = fog.extinctionPerKm;
                float dKm = volume.allocatedDesc.Value.handoffMeters * 1.0e-3f;
                float alphaBound = 1f - Mathf.Exp(-sigmaT * dKm);
                bool boundOk = !probeRan || alphaMax <= alphaBound * 1.02f;
                sb.AppendLine($"{Mark(boundOk)}判据⑪d 均匀介质上界：σ_t(地面) = {Sci(sigmaT)} /km，"
                            + $"d = {Sci(dKm)} km ⇒ 1 − exp(−σ_t·d) = {Sci(alphaBound)}，"
                            + $"实测 {Sci(alphaMax)}（比值 {Sci(alphaMax / Mathf.Max(alphaBound, 1e-30f))}，"
                            + "留 2% 定点/插值余量）");
                sb.AppendLine($"  ⓘ 比值 {Sci(alphaMax / Mathf.Max(alphaBound, 1e-30f))} 的读法："
                            + "> 1 是结构性错误（那就是上面这道门）；≈ 1 说明取到 max 的那条射线"
                            + "几乎全程贴着地面密度走 —— 探针网格里有朝下看的方向，所以这是正常的；"
                            + "而**相机远高于雾层时**比值仍然 ≈ 1 才是可疑的，那意味着"
                            + "高度衰减没生效（scaleHeight 被下发成了 0 或 ∞）。"
                            + "这一格判的是前一种，后一种要靠 #27 的跨布景对照。");
            }
            else
            {
                // 空判据的格子要在报表上点名。
                sb.AppendLine("ⓘ 判据⑪d 未覆盖：雾是关着的（Fog ▸ Mode = Off 或 σ_t = 0），"
                            + "没有可比的上界。此时判据⑪c 也失去了失败能力 —— 上面已经说明。");
            }

            sb.AppendLine($"  ⓘ 积分 rgb 的最大亮度分量 = {Sci(lumMax)}"
                        + $"（预曝光后，定点读数 {raw[k_SlotIntegralLumMax]} 个刻度 ⇒ 相对分辨率 "
                        + $"{Sci(1f / Mathf.Max(raw[k_SlotIntegralLumMax], 1u))}）。");

            // ⑪e 把「积分 = Σ 源项 × 段长」这条恒等式的**量纲**变成一道能失败的门。
            // ∫S·T dt ≤ max(S)·d，因为 T ≤ 1 —— 两个操作数都是同一个 buffer 里
            // 已经读回来的数，段长用 handoff（表最后一片存的就是到 handoff 的累积）。
            //
            // 这道门要拒绝的最小错答案：段长搞错单位制。_VistaGround.w 那个坑
            // （注入的 alpha 是 1/km、SegmentNear/Far 返回米）会让这个比值差 1000 倍。
            // 前面那道宽门（⑫，×3.6e4）根本挡不住 1000 倍。
            if (volume.allocatedDesc.HasValue)
            {
                float dKmFull = volume.allocatedDesc.Value.handoffMeters * 1.0e-3f;
                float lumBound = injectMax * dKmFull;
                float lumRatio = lumMax / Mathf.Max(lumBound, 1e-30f);
                bool lumBoundOk = !probeRan || lumMax <= lumBound;
                sb.AppendLine($"{Mark(lumBoundOk)}判据⑪e 积分-注入量纲一致：max(S) = {Sci(injectMax)}，"
                            + $"d = {Sci(dKmFull)} km ⇒ 上界 max(S)·d = {Sci(lumBound)}，"
                            + $"实测 {Sci(lumMax)}（比值 {Sci(lumRatio)}，因 T ≤ 1 必须 ≤ 1）");
                sb.AppendLine($"  ⓘ 裕度 {Sci(1f - lumRatio)} 对上定点相对分辨率 "
                            + $"{Sci(1f / Mathf.Max(raw[k_SlotIntegralLumMax], 1u))} —— "
                            + "裕度必须大于分辨率，否则这一格是「一个正好压在门上的读数」。"
                            + "它要拒绝的最小错答案是段长差 1000 倍（米/千米混用），"
                            + "那个错答案会把比值推到 1e+3 或 1e-3，远在本门之外。");
            }


            // ---------------------------------------------------------------- 判据⑫ x = σ_t·Δ 包络
            float segXMin = probeRan ? segXMinRaw / k_SegXScale : float.NaN;
            float segXMax = raw[k_SlotSegXMax] / k_SegXScale;

            bool envelopeOk = probeRan
                           && segXMin >= k_SegXDerivedMin && segXMax <= k_SegXDerivedMax
                           && segXMin <= segXMax;
            sb.AppendLine($"{Mark(envelopeOk)}判据⑫ 段光学厚度包络：实测 x ∈ "
                        + $"[{Sci(segXMin)}, {Sci(segXMax)}]，推导区间 "
                        + $"[{Sci(k_SegXDerivedMin)}, {Sci(k_SegXDerivedMax)}]"
                        + $"（区间宽 ×{Sci(k_SegXDerivedMax / k_SegXDerivedMin)} —— 这是一道**宽门**，"
                        + "它抓的是量级整体挪位，抓不到一个数量级内的偏差）");
            sb.AppendLine($"  ⓘ 定点分辨率 {Sci(1f / k_SegXScale)}，下端还剩 "
                        + $"{(probeRan ? segXMinRaw.ToString() : "—")} 个刻度 ——"
                        + "「地板与被测量同量级时尺子会自己伪造结论」这条在这里是量出来的，不是估的。");

            // ⑫b 一道**紧**门。⑫ 那道推导区间宽 3.6e4 倍，挡不住一个 1000 倍的单位错；
            // 这一道的裕度是个位数百分比。
            //
            // x = σ_t·Δ，两个因子各有一个能算准的上界：σ_t ≤ 地面密度（高度雾只会更淡），
            // Δ ≤ 最长那一段。段长用 desc.SegmentFar/SegmentNear —— 这**不是**第二份实现：
            // 那两个函数与 HLSL 的 VistaFroxelSegmentLengthKm 逐位一致这件事，
            // 已经由 VistaFroxelVolumeSelfTest 的切片判据（#19）盯着了。这里复用它们，
            // 等于把那一格的结论接到这一格上。
            //
            // 最长段不假设是最后一段，直接扫一遍：切片 0 是 [0, d_0]，它不服从后面那个
            // 等比规律，「假设最后一段最长」是一条不需要的推导。
            if (probeRan && fog != null && fog.enabled && volume.allocatedDesc.HasValue)
            {
                var d = volume.allocatedDesc.Value;
                float longestMeters = 0f;
                int longestSlice = -1;
                for (int i = 0; i < d.depth; i++)
                {
                    float len = d.SegmentFar(i) - d.SegmentNear(i);
                    if (len > longestMeters) { longestMeters = len; longestSlice = i; }
                }

                float xBound = fog.extinctionPerKm * longestMeters * 1.0e-3f;
                float xRatio = segXMax / Mathf.Max(xBound, 1e-30f);
                bool xBoundOk = segXMax <= xBound;
                sb.AppendLine($"{Mark(xBoundOk)}判据⑫b 段光学厚度紧上界：σ_t(地面) {Sci(fog.extinctionPerKm)} /km"
                            + $" × 最长段 {longestMeters:F3} m（切片 {longestSlice}）= {Sci(xBound)}，"
                            + $"实测 x_max = {Sci(segXMax)}（比值 {Sci(xRatio)}，必须 ≤ 1）");
                sb.AppendLine($"  ⓘ 裕度 {Sci(1f - xRatio)}。比值还是一条归因，但只是**上界**："
                            + "它 ≥「取到 x_max 的那个 froxel 的 σ_t ÷ 地面 σ_t」，"
                            + "取等的条件是 x_max 恰好落在最长那一段上 —— 而探针没有记录它落在哪一段，"
                            + "所以这里不断言取等（断言一个自己没有保留的中间读数等于编造证据）。"
                            + "读法：比值 > 1 ⇒ 段长或 σ_t 的单位制错了（米/千米差 1000 倍）；"
                            + "比值 ≪ 1 而相机贴着地面 ⇒ 高度衰减被下发得太陡。");
            }
            else if (probeRan)
            {
                sb.AppendLine("ⓘ 判据⑫b 未覆盖：雾关着，σ_t(地面) = 0 会让紧上界退化成 0，"
                            + "而空气的 σ_t 不由 VistaFogSettings 给 —— 那条上界要另外推。");
            }

            // 分支覆盖：这是 #25 那次阈值替换的影响面，必须在**换之前**量出来。
            if (probeRan)
            {
                bool seriesCoveredNow = segXMin < k_SeriesThresholdShipped;
                bool allSeriesAfter    = segXMax < k_SeriesThresholdOptimum;
                sb.AppendLine($"  ⓘ 级数分支覆盖（VistaSegmentIntegral 的 x ≤ {Sci(k_SeriesThresholdShipped)} 那一支）："
                            + (seriesCoveredNow
                                ? "真实帧里**有**段落在级数支上。"
                                : "真实帧里**没有**任何段落在级数支上 —— 那一支今天只由合成介质自检覆盖。"));
                sb.AppendLine($"  ⓘ #25 要把阈值换成最优的 x* = {Sci(k_SeriesThresholdOptimum)}。"
                            + (allSeriesAfter
                                ? $"换完之后本布景**每一段**都会走级数支（x_max {Sci(segXMax)} < x*）—— "
                                + "所以那次替换不是一个边角优化，它会整体改写这条积分路径，"
                                + "AP 必须跟着重跑一遍判据。"
                                : $"换完之后仍有段走 exp 支（x_max {Sci(segXMax)} ≥ x*），两支都有真实帧覆盖。"));
            }

            // ================================================================ #22a 时间重投影
            {
                var reproj = feature.froxelReprojection;
                int depth = volume.allocatedDesc.HasValue ? volume.allocatedDesc.Value.depth : 0;

                settings.ResolveLuminanceReject(out float deadStart, out float deadFull);

                sb.AppendLine("---- #22a 时间重投影 ----");
                sb.AppendLine($"状态：jitterMode = {settings.jitterMode}"
                            + $"（横向 {settings.lateralJitterAmount:F2} 格, 深度 {settings.depthJitterAmount:F2} 片）"
                            + $"，τ = {settings.historyTimeConstant:F3} s"
                            + $"，亮度死区 [{deadStart:F3}, {deadFull:F3}]"
                            + $"（滑条 {settings.luminanceRejectStart:F3} / {settings.luminanceRejectFull:F3}）");
                // 这三个数是**帧后**的读数，与判据⑭那条「帧内」的结论方向相反，
                // 所以必须自己把这件事说出来：
                //   historyContentValid ≡ isAllocated && lastWritten == 1 − writeIndex。
                // 一帧的顺序是「交换 → 判历史 → 注入 → NoteInjectionDispatched」，
                // 注入那一步把 lastWritten 推成 writeIndex，于是**帧后**这个式子必然为 false。
                // 只印一个裸的 False，读者会以为它就是被测代码帧内吃进去的那一份，
                // 症状是判据⑭全绿旁边站着一个看起来矛盾的 False。
                //
                // 顺手把它做成一格能失败的判据（⑭c）：帧后 lastWritten == writeIndex
                // 恰好等价于「本帧的注入核真的派发到了当前写槽上」，
                // 而它为假就是「交换了两次却只派发了一次」那个错配 ——
                // 那个错配在画面上只表现为「累积没生效」，与「历史权重被填成 0」无法区分。
                bool injectDispatched = volume.lastWrittenIndex == volume.injectionWriteIndex;
                sb.AppendLine($"  双缓冲（**帧后**读数）：写下标 = {volume.injectionWriteIndex}, "
                            + $"最后被写过的 = {volume.lastWrittenIndex}, "
                            + $"historyContentValid = {volume.historyContentValid}");
                if (settings.enableInjection)
                {
                    sb.AppendLine($"{Mark(injectDispatched)}判据⑭c 帧后写槽一致（注入核确实派发到了本帧的写槽上）："
                                + $"lastWritten {volume.lastWrittenIndex} == writeIndex {volume.injectionWriteIndex}"
                                + $" ⇒ 帧后 historyContentValid 必然为 **False**，实测 {volume.historyContentValid}。");
                    sb.AppendLine("  ⓘ 这一格的绿是「帧后 False」，判据⑭的绿是「帧内 True」—— 两者不矛盾，"
                                + "是同一个量在一帧的两端。这里若读到 True，说明本帧交换过但没派发注入，"
                                + "症状是累积永远不生效。");
                }
                else
                {
                    sb.AppendLine("  空判据点名：enableInjection = false ⇒ 注入核这一帧没派发，"
                                + "判据⑭c（帧后写槽一致）无法判 —— 不是绿，是没跑。");
                }

                if (reproj == null)
                {
                    sb.AppendLine("✘ feature.froxelReprojection 为 null：VistaAtmospherePass 还没被 new 出来。"
                                + "下面判据⑬⑭⑮全部无法判 —— 不是「未覆盖」，是尺子缺了。");
                }
                else
                {
                    string reason = reproj.lastInvalidReason;
                    bool historyLive = reason == null;
                    sb.AppendLine($"  CPU 侧：frameIndex = {reproj.frameIndex}, "
                                + $"prevCapturedAtFrame = {reproj.prevCapturedAtFrame}, "
                                + $"framesSinceValid = {reproj.framesSinceValid}, "
                                + $"失效原因 = {reason ?? "（无，历史在用）"}");

                    // ---------------------------------------------------------- 判据⑬ 静止恒等性
                    // 三个数并排印：地板上界、实测、要拒绝的最小错答案。
                    // 门摆在「地板上界」与「最小错答案」的几何中点 ——
                    // 那个最小错答案是重投影漏掉半个纹素（0.5/N），它静止时逐位正确、
                    // 只有相机沿视线移动才露出拖影，所以判据必须自己把它挡住。
                    float staticErr   = raw[k_SlotReprojStaticErr] / k_ReprojStaticErrScale;
                    float halfTexel   = depth > 0 ? 0.5f / depth : 0f;
                    float staticGate  = Mathf.Sqrt(k_ReprojStaticFloorMeasured
                                                 * Mathf.Max(halfTexel, k_ReprojStaticFloorMeasured));
                    uint  staticCount = raw[k_SlotReprojStaticCount];
                    uint  staticMask  = raw[k_SlotReprojStaticMask];

                    bool staticOk = staticCount == (uint)k_ProbeCountExpected
                                 && staticMask == 0u
                                 && staticErr <= staticGate;
                    sb.AppendLine($"{Mark(staticOk)}判据⑬ 静止恒等性（prev 覆盖成 current ⇒ 格心必须投回自己那一格的纹素中心）："
                                + $"max|Δuvw| = {Sci(staticErr)}，门 {Sci(staticGate)}，"
                                + $"样本 {staticCount}/{k_ProbeCountExpected}，掩码 OR = 0x{staticMask:X}（必须 0）");
                    sb.AppendLine($"  ⓘ 地板 {Sci(k_ReprojStaticFloorMeasured)}（**实测基线**，不是推导上界："
                                + "原先按「fp32 投影 + log/exp 一次往返 ≈ 1e-6」推，被第一次实测证伪 —— "
                                + "格心到 64 m 量级、decode/encode 各带一次超越函数，实际落在 1e-6~1e-5），"
                                + $"要拒绝的最小错答案 {Sci(halfTexel)}（= 0.5/N，N = {depth}：重投影漏掉那半个纹素），"
                                + "门取两者的几何中点。裕度 "
                                + $"{Sci(staticGate - staticErr)}（带符号）；"
                                + $"实测离地板 ×{(staticErr > 0f ? k_ReprojStaticFloorMeasured / staticErr : 0f):F2}"
                                + $"，离最小错答案 ×{(staticErr > 0f ? halfTexel / staticErr : 0f):F0}。");
                    sb.AppendLine("  ⓘ 这一格是**必要不充分**的：prev = current 时恒等成立，"
                                + "不证明 prev ≠ current 时那份矩阵真是上一帧的 —— 补上另一半的是判据⑭。"
                                + "xy 之所以是**精确恒等**而不是近似：视锥四角取在 z = 1 平面上且未归一化"
                                + "（CalculateFrustumCorners(rect, 1f, ...)），而 viewProj 由 "
                                + "VistaFroxelReprojection.ViewProjOf 单点构造、同源于 camera.projectionMatrix。"
                                + "谁手工覆盖投影矩阵，这一格就会红 —— 那正是想要的红。");

                    // ---------------------------------------------------------- 判据⑭ 上一帧的新鲜度
                    // 「捕获写在算完 data 之后」这条因果没法直接读出来，但它有一个
                    // 可失败的等价形式：若捕获挪到前面，Update 里那条
                    // 「prevCapturedAtFrame != frameIndex − 1」的谓词就会命中，
                    // 于是 lastInvalidReason 非 null、historyWeight 归零。
                    // 所以在一个稳态帧上 reason == null 就是那条顺序的证据。
                    long expectCaptured = (long)reproj.frameIndex;
                    bool capturedOk = reproj.prevCapturedAtFrame == expectCaptured;
                    bool freshOk = capturedOk && historyLive && reproj.framesSinceValid >= 1;
                    sb.AppendLine($"{Mark(freshOk)}判据⑭ 上一帧视图的新鲜度："
                                + $"prevCapturedAtFrame {reproj.prevCapturedAtFrame} == frameIndex {expectCaptured}"
                                + $"（本帧末尾捕获过）{Mark(capturedOk)}，"
                                + $"framesSinceValid = {reproj.framesSinceValid} ≥ 1，"
                                + $"失效原因为空 = {historyLive}");
                    if (!freshOk)
                        sb.AppendLine($"  ⚠ 本帧历史不在用（原因：{reason ?? "捕获帧号对不上"}）。"
                                    + "自检连渲了两帧，第二帧本该是稳态 —— 若原因是"
                                    + "「历史表这一帧还没被写过」，说明双缓冲交换与注入派发的次数不匹配"
                                    + "（交换了两次却只派发了一次），看上面那两个下标。");

                    // ---------------------------------------------------------- 在线读数（CPU ↔ GPU 一致性）
                    uint onlineCount = raw[k_SlotReprojOnlineCount];
                    uint onlineOk    = raw[k_SlotReprojOnlineOk];
                    uint onlineMask  = raw[k_SlotReprojOnlineMask];
                    bool maskHasNoHist = (onlineMask & k_ReprojNoHistory) != 0u;

                    // 双向：CPU 说历史在用 ⇒ GPU 不该有一个 NO_HISTORY，且至少有一格 OK；
                    //       CPU 说历史不在用 ⇒ GPU 必须全是 NO_HISTORY。
                    // 写成双向而不是单向，是为了让「jitterMode = Off」这种合法配置
                    // 也走一条能失败的判据，而不是变成一格空判据。
                    bool onlineConsistent = onlineCount == (uint)k_ProbeCountExpected
                        && (historyLive ? (!maskHasNoHist && onlineOk > 0u)
                                        : (maskHasNoHist && onlineOk == 0u));
                    sb.AppendLine($"{Mark(onlineConsistent)}判据⑭b 在线读数与 CPU 状态一致："
                                + $"样本 {onlineCount}/{k_ProbeCountExpected}，"
                                + $"OK {onlineOk}，掩码 OR = 0x{onlineMask:X}"
                                + $"（CPU 说历史{(historyLive ? "在用 ⇒ 掩码里不许有 NO_HISTORY 且 OK > 0" : "不在用 ⇒ 必须全是 NO_HISTORY")}）");
                    sb.AppendLine("  ⓘ 这一格是那条「一个字节都没下发」的失效的唯一出口："
                                + "探针角色 1 跑在任何 reproj.Bind **之前**，读到的就是注入核吃进去的那一份。"
                                + "cbuffer 没下发时零态 = 历史权重 0 ⇒ 全是 NO_HISTORY，而 CPU 侧说历史在用 ⇒ 红。");

                    // ---------------------------------------------------------- 判据⑮ 失效路径的覆盖 + 守恒
                    uint hNoHist = raw[k_SlotReprojHitNoHist];
                    uint hBehind = raw[k_SlotReprojHitBehind];
                    uint hOffScr = raw[k_SlotReprojHitOffScr];
                    uint hRange  = raw[k_SlotReprojHitRange];
                    uint hLum    = raw[k_SlotReprojHitLum];
                    uint hNaN    = raw[k_SlotReprojHitNaN];
                    uint cover   = raw[k_SlotReprojCoverCount];

                    int role3 = feature.froxelReprojProbeRole3Dispatches;
                    int role4 = feature.froxelReprojProbeRole4Dispatches;
                    long expect3 = (long)role3 * k_ProbeCountExpected;
                    long expect4 = (long)role4 * k_ProbeCountExpected;
                    long sum3 = (long)hNoHist + hBehind + hOffScr + hRange;

                    bool eachHit = hNoHist > 0u && hBehind > 0u && hOffScr > 0u && hRange > 0u
                                && hLum > 0u && hNaN > 0u;
                    bool conserved = sum3 == expect3
                                  && hNaN == (uint)expect4 && hLum == (uint)expect4
                                  && cover == (uint)(expect3 + expect4);
                    sb.AppendLine($"{Mark(eachHit && conserved)}判据⑮ 六条失效路径都被驱动过 + 计数守恒："
                                + $"NO_HISTORY {hNoHist}, BEHIND {hBehind}, OFF_SCREEN {hOffScr}, "
                                + $"OUT_OF_RANGE {hRange}, LUMINANCE {hLum}, NaN {hNaN}");
                    sb.AppendLine($"  守恒式：四条位移分支之和 {sum3} == 角色 3 派发 {role3} × {k_ProbeCountExpected} = {expect3}"
                                + $"；NaN 与 LUMINANCE 各 == 角色 4 派发 {role4} × {k_ProbeCountExpected} = {expect4}"
                                + $"；COVER_COUNT {cover} == {expect3 + expect4}");
                    sb.AppendLine("  ⓘ 守恒式比逐条 > 0 严格：掩码是互斥的（谓词第一条命中就 return），"
                                + "所以「某个 froxel 意外返回 OK」或「掩码里带了两位」都会让和**小于**期望。"
                                + "派发趟数由 VistaAtmosphereLuts 自己累加后转发上来，不是在本文件里手抄一个 4 ——"
                                + "抄下来的常数在加第五个位移时不会跟着改，那一格会变成一个由「常数陈旧」造成的假失败。");
                    sb.AppendLine("  ⓘ NaN 那一格证明的是「闸接对了」（谓词命中 ⇒ 掩码置位 ⇒ 早退），"
                                + "**不是**「AnyIsNaN 在这台硬件上判得对」—— 后者是 URP 的位模式比较。"
                                + "线上那条路径读不到 NaN 由构造保证（historyContentValid 挡着未写过的表）。");

                    // ---------------------------------------------------------- 抖动散布 vs 死区下端
                    float spread = raw[k_SlotReprojJitterSpread] / k_ReprojJitterScale;
                    bool jitterOn = settings.jitterMode != JitterMode.Off;
                    if (jitterOn)
                    {
                        bool deadbandOk = deadStart > spread;
                        sb.AppendLine($"{Mark(deadbandOk)}判据⑮b 亮度死区下端高于抖动自己制造的散布："
                                    + $"死区下端 {deadStart:F4} > 实测散布 {Sci(spread)}"
                                    + $"（裕度 {Sci(deadStart - spread)}，带符号）");
                        sb.AppendLine("  ⓘ 散布量的是**同一个 froxel 两次独立抖动抽样**的相对亮度差的 max，"
                                    + "不是「历史与本帧之差」—— 后者混着相机运动与场景变化，"
                                    + "而这个数是用来摆死区下端的，被污染的上界会把死区顶得过高。");
                        // 可执行的输出：这一格真正告诉美术的是「下端不能拖到哪儿以下」。
                        // 只印裕度的话，那个数字要读者自己反算才知道能调到哪里。
                        sb.AppendLine($"  ⓘ 由此得出的可执行下限：本布景里 luminanceRejectStart 必须 > {Sci(spread)}"
                                    + $"（当前 {settings.luminanceRejectStart:F3}，只有 ×{(spread > 0f ? deadStart / spread : 0f):F2} 的余量）——"
                                    + "这个下限随布景走（雾越浓、明暗对比越大，散布越大），换布景要重量。");
                        if (!deadbandOk)
                            sb.AppendLine($"  ⚠ 症状：{k_JitterDeadbandSymptom}。"
                                        + "死区下端拖到散布之下时，这条失效规则会把抖动噪声判成"
                                        + "「场景变了」而降权 —— 亲手毁掉它本该保护的累积。");
                        sb.AppendLine($"  ⓘ 定点分辨率 {Sci(1f / k_ReprojJitterScale)}，"
                                    + $"实测占 {raw[k_SlotReprojJitterSpread]} 个刻度"
                                    + (raw[k_SlotReprojJitterSpread] < 8u
                                        ? " —— **落在尺子地板附近**，这个读数只能当上界用。"
                                        : "。"));
                    }
                    else
                    {
                        sb.AppendLine("ⓘ 判据⑮b 空判据：jitterMode = Off ⇒ 抖动幅度 0 ⇒ 两次抽样逐位相同 ⇒ "
                                    + $"散布恒为 0（实测 {Sci(spread)}），死区下端无论摆哪儿都 > 0。"
                                    + "这一格今天没有区分力，点名它。");
                    }
                }
            }

            // ================================================================ #22b 抖动源与横向形态
            {
                var reproj = feature.froxelReprojection;
                int  jitDispatches = feature.froxelJitterProbeDispatches;
                uint bnCount  = raw[k_SlotBnCount];
                uint jitCount = raw[k_SlotJitCount];
                uint aggCount = raw[k_SlotAggCount];
                int  depth    = volume.allocatedDesc.HasValue ? volume.allocatedDesc.Value.depth : 0;

                // 「线上实际用的是哪个源」。回落是一条真实路径：选了蓝噪声但取不到图时
                // JitterParamsOf 会把源改成程序化，并把原因记在 jitterFallbackReason 上。
                // 判据㉑的期望值必须用**回落之后**的源，否则回落一发生那一格就红，
                // 而它红的理由是错的（接线是对的，是资产缺了）。
                bool blueOnline = settings.jitterMode == JitterMode.BlueNoise
                               && reproj != null && reproj.jitterFallbackReason == null;

                sb.AppendLine("---- #22b 抖动源统计与横向形态两档对照 ----");
                sb.AppendLine($"状态：源旋钮 = {settings.jitterMode} ⇒ 线上生效 = "
                            + $"{(settings.jitterMode == JitterMode.Off ? "Off" : blueOnline ? "BlueNoise" : "Procedural")}"
                            + $"，横向形态 = {settings.lateralJitterShape}"
                            + $"，蓝噪声资产 available = {VistaBlueNoise.available}"
                            + $"（Resolve 失败原因：{VistaBlueNoise.lastFailure ?? "（无）"}）"
                            + $"，本帧回落原因 = {reproj?.jitterFallbackReason ?? "（无）"}");

                // ---------------------------------------------------------- 判据⑰a 探针执行性
                // 把两种「读数全 0」分开：**压根没派发**与**派发了但核内第一道守卫早退**。
                // 两者的 COUNT 槽都是 0，而前者是接线问题、后者是配置问题（幅度旋钮为 0）。
                // 没有这一格的话，归因会从「探针坏了」开始查一件其实是「旋钮关着」的事。
                bool dispatchedOk = jitDispatches == 1;
                bool jitRan = jitCount == (uint)k_JitProbeCountExpected;
                sb.AppendLine($"{Mark(dispatchedOk)}判据⑰a 抖动探针派发性：{jitDispatches} 趟（期望 1；"
                            + "−1 = 取不到 LUT，0 = 那趟 pass 没排进去或 froxelVolume 为 null）");
                if (dispatchedOk && !jitRan)
                {
                    sb.AppendLine($"ⓘ 判据⑰b~㉑ 全部未覆盖：探针派发过，但核内第一道守卫"
                                + "（any(_VistaFroxelJitter.xy ≤ 0)）命中并整核早退 ⇒ 三个计数槽留 0。"
                                + $"归因：横向幅度 {settings.lateralJitterAmount:F2}、深度幅度 "
                                + $"{settings.depthJitterAmount:F2}、jitterMode {settings.jitterMode} ——"
                                + "任意一个把两个幅度之一压成 0 都会走到这里。这是配置问题，不是代码问题。");
                }
                else if (!dispatchedOk)
                {
                    sb.AppendLine("ⓘ 判据⑰b~㉑ 全部未覆盖：探针一趟都没派发，下面的读数是初值。");
                }
                else
                {
                    // ------------------------------------------------------ 判据⑰d 绑定到达性
                    // 尺寸不问 CPU（VistaBlueNoise.texture.width），而是问**核里
                    // GetDimensions 的返回值** —— 判据⑥a 的同一条理由：CPU 侧那个数
                    // 只证明「资产存在」，证明不了「这一趟 dispatch 的 SRV 槽位上
                    // 真的坐着它」。
                    //
                    // 这一格是 #22b 第二轮排查的产物：⑰b/⑰c 全零时，
                    // 「未绑定 SRV 在 D3D11 上读 0」与「绑上了但内容是 0」在报表上
                    // 完全同形，而两者的修法在两个不同的文件里。
                    //
                    // 双向：资产取不到时派发处刻意不绑 ⇒ 期望 0×0；
                    // 取得到时期望 64×64。所以它在两条路径上都能失败。
                    uint bnW = raw[k_SlotBnWidth];
                    uint bnH = raw[k_SlotBnHeight];
                    uint expW = VistaBlueNoise.available ? (uint)VistaBlueNoise.k_TileSize : 0u;
                    bool dimOk = bnW == expW && bnH == expW;
                    sb.AppendLine($"{Mark(dimOk)}判据⑰d 绑定到达性（核里 GetDimensions）："
                                + $"{bnW}×{bnH}，期望 {expW}×{expW}"
                                + $"（资产 available = {VistaBlueNoise.available}）");
                    sb.AppendLine("  ⓘ 0×0 ⇒ 绑定**没到达核**（未绑定的 SRV 在 D3D11 上，"
                                + "GetDimensions 返回 0、Load 返回 0）；"
                                + $"{VistaBlueNoise.k_TileSize}×{VistaBlueNoise.k_TileSize} ⇒ 绑定到了，"
                                + "此时⑰b/⑰c 若仍全零，问题在资产内容而不在绑定链。"
                                + "槽位走 InterlockedMax、初值 0 ⇒ 「没绑上」与「核压根没跑」"
                                + $"读数相同，两者由 BN_COUNT（{bnCount} / 期望 {k_JitProbeCountExpected}）分开 ——"
                                + "这条依赖必须点名，否则这一格在核早退时会给出一个错的因果。");
                    sb.AppendLine("  ⓘ 资产格式 = "
                                + (VistaBlueNoise.texture != null
                                    ? $"{VistaBlueNoise.texture.format}"
                                      + $"（TextureFormat；graphicsFormat 的整数值 "
                                      + $"{(int)VistaBlueNoise.texture.graphicsFormat}）"
                                    : "（取不到）")
                                + "。#22b 的真凶就藏在这一行：URP 把这张图导成 "
                                + "textureType: 10（SingleChannel）+ singleChannelComponent: 0（Alpha）"
                                + " ⇒ 数据只在 **.a**；HLSL 读 .r 在 D3D11 上逐像素得 0，"
                                + "与「绑定没到达」在报表上完全同形。"
                                + "URP 自己的消费点 ShaderLibrary/LODCrossFade.hlsl:19 "
                                + "拿同一张资产读的也是 .a。");
                    sb.AppendLine("  ⓘ 为什么印 TextureFormat 而不是 GraphicsFormat："
                                + "GraphicsFormat **没有**任何 alpha-only 成员（反射过：A8 不在枚举里），"
                                + "所以 graphicsFormat.ToString() 会印出一个没有名字的裸数字 54 —— "
                                + "「一个报表里的魔数等于没有印」。TextureFormat.Alpha8 才是有名字的那个尺子。"
                                + "这里只印不判：换平台时 Unity 可能给 SingleChannel 选别的等价格式，"
                                + "把它摆成门会造出一个纯粹因为平台不同而红的假失败"
                                + "（「一条只在这台机器成立的实测行为要做成 ⓘ 而不是门」）。"
                                + "「通道选对了」这条真正由⑰b/⑰c 判：读错通道时那张图恒 0 或恒 1，"
                                + "直方图会全落进第 0 桶或第 7 桶 ⇒ 两格同时红。");

                    // ------------------------------------------------------ 判据⑰b/⑰c 源图
                    // 这三格量的是**源图 v 本身**（VistaFroxelBlueNoiseAt 的返回值），
                    // 不是动画之后的 n' —— 后者被 frac 绕回改写过分布（见下面那条 ⓘ）。
                    if (!VistaBlueNoise.available)
                    {
                        sb.AppendLine("ⓘ 判据⑰b/⑰c 未覆盖：取不到蓝噪声资产 "
                                    + $"（{VistaBlueNoise.lastFailure ?? "未知"}）。"
                                    + "此时派发处（VistaFroxelVolume.BindBlueNoise）刻意**不绑** —— "
                                    + "绑一个 null RTHandle 不会炸，隐式转换会给出 CameraTarget，"
                                    + "那是一次静默绑错；而 compute 里未绑定的 SRV 在 D3D11 上读到全 0"
                                    + "（**不是**引擎默认白图，那条经验只对 material shader 成立），"
                                    + "于是 frac(0 + φ) ≡ φ ⇒ 抖动退化成常数相位偏移。"
                                    + "所以这里报未覆盖，而不是让读数去伪造一个结论。");
                    }
                    else
                    {
                        bool bnCountOk = bnCount == (uint)k_JitProbeCountExpected;

                        // ⑰b 直方图。期望是**精确**的，不是统计带：
                        // 64×64 = 4096 像素、256 个 8 bit 灰阶、void-and-cluster 是秩均匀的
                        // ⇒ 每阶正好 16 个像素；bin = floor(v·8) 的桶界落在
                        // k = 31.875 / 63.75 / … 这些**非整数**上（fp32 余量 ~4e-3，比 eps 大五个量级），
                        // 逐桶点算是 32 阶 × 16 = 512。所以它可以是一道硬门。
                        var bins = new uint[8];
                        uint binSum = 0u;
                        bool binsOk = true;
                        for (int i = 0; i < 8; i++)
                        {
                            bins[i] = raw[k_SlotBnHist0 + i];
                            binSum += bins[i];
                            if (bins[i] != (uint)k_BnBinExpected) binsOk = false;
                        }
                        sb.AppendLine($"{Mark(binsOk && bnCountOk)}判据⑰b 源图秩均匀（8 桶 × 精确 {k_BnBinExpected}）："
                                    + $"[{string.Join(", ", bins)}]，和 {binSum}，线程数 {bnCount}/{k_JitProbeCountExpected}");
                        if (!binsOk)
                            sb.AppendLine("  ⚠ 桶不均匀 ⇒ 这张图不是秩均匀的 void-and-cluster 集合。"
                                        + "归因（⑰d 已经把「绑定到不到核」独立判掉，所以这里只剩内容/通道）："
                                        + "全落进第 0 桶 = **通道选错**，读到了一个恒 0 的通道"
                                        + "（该资产 textureType: 10 + singleChannelComponent: 0 ⇒ Alpha8，"
                                        + "数据只在 .a；D3D11 上读 .r 逐像素得 0 —— #22b 的真凶）；"
                                        + "全落进第 7 桶 = 反过来读到了一个恒 1 的通道"
                                        + "（若 URP 改回 Red 导入，则 .a 恒为 1）；"
                                        + "整体倾斜 = 采样器不是 Point、或者纹理被当成 sRGB 解码了"
                                        + "（资产上 sRGBTexture: 0、filterMode: 0，两者都不能改）。");

                        float bnMean = raw[k_SlotBnSum] / k_JitProbeScale / k_JitProbeCountExpected;
                        float bnVar  = raw[k_SlotBnSq]  / k_JitProbeScale / k_JitProbeCountExpected
                                     - bnMean * bnMean;
                        bool bnMeanOk = Mathf.Abs(bnMean - 0.5f) <= k_BnMeanGate;
                        sb.AppendLine($"{Mark(bnMeanOk)}判据⑰c 源图均值：实测 {bnMean:F6}，精确期望 0.5"
                                    + $"（Σv = 2048 精确），偏差 {Sci(bnMean - 0.5f)}，门 {Sci(k_BnMeanGate)}");
                        sb.AppendLine("  ⓘ 这一格的载荷不是「噪声图长得均匀」，是**抖动不引入密度偏差**："
                                    + "偏移是 (v − 0.5)，均值精确为 0.5 ⇒ 期望偏移精确为 0。"
                                    + "偏差只可能**偏低**：VistaProbeFixed 向零截断，每次 add 少算 < 1e-5，"
                                    + "4096 次之后均值偏低 ≤ 1e-5（相对 2e-5），门留了 5 倍。");
                        sb.AppendLine($"  ⓘ 方差交叉核对：实测 {bnVar:F7}，8 bit 秩均匀解析值 "
                                    + $"{k_BnVarAnalytic:F7}（E[v²] = 511/(6·255)），"
                                    + $"连续均匀分布 1/12 = {k_BnVarContinuous:F7}。"
                                    + "解析值**只是交叉核对**：⑱⑲⑳的归一化用的是实测矩，"
                                    + "判据的载荷路径上不留解析前提。");
                    }

                    // ------------------------------------------------------ 判据⑱⑲ 两档并排
                    var proc = ReadJitterTier(raw, k_SlotJitProcBase);
                    var blue = ReadJitterTier(raw, k_SlotJitBlueBase);

                    sb.AppendLine($"  动画后偏移场 n' = frac(n + φ) 的实测矩（slice 0、逐列一致档 —— "
                                + "**最难的一档**：z 步进为 0 让深度那一路的抽头退化成 x 抽头的一个固定像素偏移）：");
                    sb.AppendLine($"    程序化：均值 ({proc.mx:F4}, {proc.my:F4}, {proc.mz:F4})  "
                                + $"方差 ({proc.vx:F5}, {proc.vy:F5}, {proc.vz:F5})");
                    sb.AppendLine($"    蓝噪声：均值 ({blue.mx:F4}, {blue.my:F4}, {blue.mz:F4})  "
                                + $"方差 ({blue.vx:F5}, {blue.vy:F5}, {blue.vz:F5})");
                    sb.AppendLine("  ⓘ 这里的均值**不**判 0.5：frac 绕回不是平移。源图的 256 个灰阶是"
                                + " k/255（含 0 与 1 两端），frac(v + φ) 之后两端撞在一起 ⇒ 多重集合变了，"
                                + "均值偏离 0.5 到 O(1/256) = 4e-3 量级。判 0.5 的是⑰c（源图 v 本身），"
                                + "而密度无偏这条结论也挂在那一格上 —— 线上偏移用的是 n'，"
                                + $"所以这里的偏离量本身就是那条 frac 代价的读数（本次 {Sci(blue.mx - 0.5f)}）。");

                    float rFloor = (1f / k_JitProbeScale) / Mathf.Max(blue.vx, 1e-12f);
                    bool nbOk = blue.rNbX < 0f && blue.rNbY < 0f
                             && Mathf.Abs(proc.rNbX) < k_JitCorrNullGate
                             && Mathf.Abs(proc.rNbY) < k_JitCorrNullGate;
                    sb.AppendLine($"{Mark(nbOk)}判据⑱ 邻域相关（lag 1）的**符号**："
                                + $"蓝噪声 ρx {blue.rNbX:F4} / ρy {blue.rNbY:F4}（必须都 < 0），"
                                + $"程序化 ρx {proc.rNbX:F4} / ρy {proc.rNbY:F4}"
                                + $"（必须 |ρ| < {Sci(k_JitCorrNullGate)} ≈ 3σ）");
                    sb.AppendLine($"  ⓘ 尺子的地板：定点分辨率 {Sci(1f / k_JitProbeScale)} ÷ 实测方差 "
                                + $"{blue.vx:F5} ⇒ ρ 的地板 {Sci(rFloor)}，"
                                + $"抽样标准差 1/√{k_JitProbeCountExpected} = {Sci(k_JitCorrSigma)}"
                                + " —— 后者是主导项，所以程序化那道带按 σ 的倍数摆。"
                                + "程序化档是一个**真正的空对照**：hash 之间独立，而独立性被任何逐点映射"
                                + "（包括 frac）保留 ⇒ 它的 ρ ≡ 0 与相位无关。");
                    sb.AppendLine("  ⓘ 必须自己说出来的两条窄化："
                                + "①这一格量的是 **lag 1 的邻域相关，不是谱** —— 在一个 compute 探针里做 FFT "
                                + "超出范围，所以它看不见 lag ≥ 2 上的结构；画面级的残差谱与判据⑯一起放在 #27。"
                                + "②只判**符号**不判幅度：frac 绕回让 ρ 随相位 φ 摆动"
                                + "（k=l=1 那一项里有一个 Re(e^{4πiφ}·E[e^{2πi(v+v')}])），"
                                + "而那个摆幅没有推出紧的界 —— 「当推导给不出紧的上限时，"
                                + "诚实地把门标成能守住的那一半」。符号本身不受影响，"
                                + $"因为 φ 无关的那一项 −E[cos 2π(v−v')]/(2π²) 在蓝噪声下是负的。本帧 φ = "
                                + $"({(reproj != null ? reproj.lastJitterPhase : Vector4.zero).x:F4}, "
                                + $"{(reproj != null ? reproj.lastJitterPhase : Vector4.zero).y:F4}, "
                                + $"{(reproj != null ? reproj.lastJitterPhase : Vector4.zero).z:F4})。");

                    bool tapOk = Mathf.Abs(blue.rXY) < k_JitCorrTapGate
                              && Mathf.Abs(blue.rXZ) < k_JitCorrTapGate
                              && Mathf.Abs(blue.rYZ) < k_JitCorrTapGate
                              && Mathf.Abs(proc.rXY) < k_JitCorrTapGate
                              && Mathf.Abs(proc.rXZ) < k_JitCorrTapGate
                              && Mathf.Abs(proc.rYZ) < k_JitCorrTapGate;
                    sb.AppendLine($"{Mark(tapOk)}判据⑲ 三个抽头互不相关："
                                + $"蓝噪声 ρxy {blue.rXY:F4} / ρxz {blue.rXZ:F4} / ρyz {blue.rYZ:F4}，"
                                + $"程序化 ρxy {proc.rXY:F4} / ρxz {proc.rXZ:F4} / ρyz {proc.rYZ:F4}"
                                + $"（门 |ρ| < {Sci(k_JitCorrTapGate)} ≈ 6σ）");
                    sb.AppendLine("  ⓘ 这一格要拒绝的最小错答案是 **ρ = 1**：三个通道抄成同一个抽头。"
                                + "那种错在画面上的症状是抖动只沿一条对角线走 —— 幅度看起来是对的，"
                                + "方向永远只有一个，而这正是「一个物理上讲得通、因此最容易被接受」的形态。"
                                + "蓝噪声档的三个抽头是同一张图的 (0,0)/(37,17)/(11,43) 三个偏移，"
                                + "所以这一格顺带证明了那三个偏移量彼此够远。");

                    // ------------------------------------------------------ 判据⑳ 聚合两档
                    if (aggCount != (uint)k_JitProbeCountExpected)
                    {
                        sb.AppendLine($"ⓘ 判据⑳a~⑳c 未覆盖：AGG 计数 {aggCount}，片数 = {depth}"
                                    + $" ≠ {k_JitProbeDim}。对角恒等式的推导要求 z 跑满 (17z mod 64) 的"
                                    + "**整个周期** —— 17 在 mod 64 下可逆 ⇒ 一列的 64 个抽样点正好是"
                                    + "整条反对角线，列均值因此只是 (x−y) 的函数。片数不是 64 时那 64 个点"
                                    + "不再构成完整的反对角线，恒等式**本来就不该成立**，"
                                    + "所以核在这里直接 return、报表点名未覆盖，而不是去判一条前提不成立的恒等式。");
                    }
                    else
                    {
                        const float nAgg = k_JitProbeCountExpected;
                        float colMean = raw[k_SlotAggColSum] / k_JitProbeScale / nAgg;
                        float colVar  = raw[k_SlotAggColSq]  / k_JitProbeScale / nAgg - colMean * colMean;
                        float slcMean = raw[k_SlotAggSlcSum] / k_JitProbeScale / nAgg;
                        float slcVar  = raw[k_SlotAggSlcSq]  / k_JitProbeScale / nAgg - slcMean * slcMean;

                        // ⑳a 跨槽位对账。逐列一致档下 Ā ≡ n'.x(p, 0)（横向偏移沿 z 恒定），
                        // 而⑲的蓝噪声档量的正是 n'.x(p, 0)。两组槽位**独立累加**同一个量 ⇒ 必须相等。
                        // 这一格抓的是「⑳的列循环把源接错了」——那种错会让⑳c 照样全绿。
                        float dMean = Mathf.Abs(colMean - blue.mx) / Mathf.Max(Mathf.Abs(blue.mx), 1e-12f);
                        float dVar  = Mathf.Abs(colVar  - blue.vx) / Mathf.Max(Mathf.Abs(blue.vx), 1e-12f);
                        bool crossOk = VistaBlueNoise.available
                                    && dMean <= k_AggCrossGate && dVar <= k_AggCrossGate;
                        sb.AppendLine($"{(VistaBlueNoise.available ? Mark(crossOk) : "ⓘ ")}判据⑳a 跨槽位对账"
                                    + $"（逐列一致档下 Ā ≡ n'.x ⇒ 两组独立累加必须相等）："
                                    + $"均值 {colMean:F6} vs {blue.mx:F6}（相对差 {Sci(dMean)}），"
                                    + $"方差 {colVar:F6} vs {blue.vx:F6}（相对差 {Sci(dVar)}），"
                                    + $"门 {Sci(k_AggCrossGate)}");
                        if (!VistaBlueNoise.available)
                            sb.AppendLine("  ⓘ 未覆盖：蓝噪声资产取不到，两边读到的都是引擎白图，"
                                        + "相等是**平凡成立**的 —— 那不是通过。");
                        sb.AppendLine("  ⓘ 这一格的**区分力边界**（必须自己说出来）：它能抓"
                                    + "「COL 那一路的 latZStride 其实不是 0」（那时 Ā 的方差会按 ~1/N 掉，"
                                    + "差出一个量级）、「+0.5 编码写错」、「rcpAmp 用错」、「槽位下标串了」。"
                                    + "它**抓不到**「源接错了」—— 蓝噪声与程序化 hash 的边缘分布都是"
                                    + "近似均匀的，均值和方差几乎一样。源接错那一半由⑱的符号门覆盖，"
                                    + "两格合起来才是完整的。"
                                    + $"（地板：Ā 走 64 次 fp32 累加，相对误差 ≤ 3.8e-6，"
                                    + $"传到方差上带 ×2×3.98 的杠杆 ⇒ ≤ 3.0e-5；门 {Sci(k_AggCrossGate)} 留 ×33。）");

                        // ⑳b 量级门。
                        float varRatio = slcVar / Mathf.Max(colVar, 1e-12f);
                        bool ratioOk = varRatio < k_AggVarRatioGate;
                        sb.AppendLine($"{Mark(ratioOk)}判据⑳b 聚合幅度收缩：Var(Ā_逐片) / Var(Ā_逐列) = "
                                    + $"{Sci(slcVar)} / {Sci(colVar)} = {Sci(varRatio)}"
                                    + $"（门 < {k_AggVarRatioGate:F3}）");
                        sb.AppendLine($"  ⓘ 独立情形的值是 1/N = {Sci(1f / k_JitProbeDim)}。"
                                    + "**刻意不摆成 ≈1/N 的紧门**：固定步进下那 N 个场并不独立"
                                    + "（抽的是同一条反对角线），1/N 的前提不成立 ——"
                                    + "「用一个前提不成立的公式去摆门」会在收紧时伪造一个失败。"
                                    + $"实测与 1/N 的比 {Sci(varRatio * k_JitProbeDim)} 只当 ⓘ 读。");

                        // ⑳c 四格表。恰好一格 ≈ 0。
                        float colDAxis = raw[k_SlotAggColDAxis] / k_JitProbeDScale;
                        float colDDiag = raw[k_SlotAggColDDiag] / k_JitProbeDScale;
                        float slcDAxis = raw[k_SlotAggSlcDAxis] / k_JitProbeDScale;
                        float slcDDiag = raw[k_SlotAggSlcDDiag] / k_JitProbeDScale;

                        bool tableOk = slcDDiag < k_AggDiagGate
                                    && slcDAxis > k_AggAxisGate
                                    && colDAxis > k_AggAxisGate
                                    && colDDiag > k_AggAxisGate;
                        sb.AppendLine($"{Mark(tableOk)}判据⑳c 对角条纹恒等式（四格里**恰好一格** ≈ 0）：");
                        sb.AppendLine($"    逐列一致  max|Ā(p+(1,0)) − Ā(p)| = {Sci(colDAxis)}（须 > {Sci(k_AggAxisGate)}）"
                                    + $"， max|Ā(p+(1,1)) − Ā(p)| = {Sci(colDDiag)}（须 > {Sci(k_AggAxisGate)}）");
                        sb.AppendLine($"    逐片独立  max|Ā(p+(1,0)) − Ā(p)| = {Sci(slcDAxis)}（须 > {Sci(k_AggAxisGate)}）"
                                    + $"， max|Ā(p+(1,1)) − Ā(p)| = {Sci(slcDDiag)}（须 < {Sci(k_AggDiagGate)}）");
                        sb.AppendLine($"  ⓘ 门是怎么摆的：地板是 fp32 求和序误差的保守上界 {Sci(k_AggDiagFloor)}"
                                    + "（|部分和| ≤ 32、½eps = 6.0e-8、64 次 add、除 64、两列各一份），"
                                    + $"要拒绝的最小错答案是「DDIAG 与 DAXIS 同量级」= O(1e-1)，"
                                    + $"门取两者的几何中点 {Sci(k_AggDiagGate)}。实测离地板 "
                                    + $"×{(slcDDiag > 0f ? slcDDiag / k_AggDiagFloor : 0f):F2}，"
                                    + $"离门 ×{(slcDDiag > 0f ? k_AggDiagGate / slcDDiag : float.PositiveInfinity):F1}。");
                        sb.AppendLine("  ⓘ 这一格是本节的载荷，它替换掉一条我自己写错过的论证。"
                                    + "原先注释里写的是「CLT 把逐片独立的聚合场抹成白噪声」——"
                                    + "那条对程序化 hash 成立，对蓝噪声**是错的**：固定的逐片瓦片步进 "
                                    + "s = (17,17) 让聚合滤波器精确地等于 K(f) = δ[f_x + f_y ≡ 0 (mod 64)]，"
                                    + "剩下的基函数只有 e^{2πi f_x(x−y)/64} ⇒ Ā(x,y) = h((x−y) mod 64)，"
                                    + "也就是**对角条纹**。初等版本更好核对：17·49 = 833 = 13·64 + 1 ⇒ "
                                    + "17 在 mod 64 下可逆 ⇒ 一列的 64 个抽样点正好是整条反对角线 "
                                    + "{(x+k, y+k)}，列均值当然只是 (x−y) 的函数。这条对任意逐点函数都成立，"
                                    + "所以 frac 那次绕回救不了它。");
                        sb.AppendLine("  ⓘ 为什么量的是**逐点恒等式的 max 差**，而不是相关系数："
                                    + "①归一化的一阶相关 ρ₁ 判不出「变白了」—— N 个解相关场平均之后"
                                    + "分子分母同比缩小，ρ₁ 不变；能判的是**方向性**。"
                                    + $"②拿定点矩去算 ρ 时，尺子地板 {Sci(1f / k_JitProbeScale)} 对上 "
                                    + $"Var(Ā_逐片) = {Sci(slcVar)}，ρ 的误差与 1e-3 的门只差个位数倍 ——"
                                    + "「尺子的地板与被测量同量级时，尺子会自己伪造一个结论」。"
                                    + "换成逐点差之后地板降到 fp32 求和序误差："
                                    + "Ā(x+1,y+1) 与 Ā(x,y) 求和的是**同一个** 64 点多重集合、"
                                    + "只是起点旋转了 17⁻¹ ≡ 49 片。注意**不是**「逐位相同」——"
                                    + "fp32 加法不满足结合律，旋转求和次序会留下一点非零残差，"
                                    + $"本次 {Sci(slcDDiag)}（那 {Sci(k_AggDiagFloor)} 的保守界因此不紧，"
                                    + $"实测在它下面 ×{(slcDDiag > 0f ? k_AggDiagFloor / slcDDiag : float.PositiveInfinity):F0}）。");
                        sb.AppendLine("  ⓘ 后续（未实现）：把逐片的瓦片偏移改成**随机**而不是固定步进，"
                                    + "K(f) 就会退回 O(1/√N) 于所有 f ≠ 0，逐片独立与蓝噪声就不再互斥。"
                                    + "不现在做的理由：它要先证明蓝噪声档的收益值得多一次 hash，"
                                    + "而那正是判据⑱与 #27 的残差谱要回答的。");
                        // 组合禁忌：这条以前是推导，现在是**读数**。#22b 修好通道之后
                        // 蓝噪声这一路第一次真的活了，于是 ⑳c 量到的对角恒等式
                        // （DDIAG ≈ 0 而 DAXIS = O(0.1)）就不再是一个纸上的预言。
                        // 而 settings 目前允许美术把这两个枚举同时选上 ——
                        //「一个默认关闭、又没有判据覆盖的开关」的近亲：
                        // 这里是一个**允许被选中、且选中就出结构性瑕疵**的组合。
                        bool hazardNow = blueOnline
                                      && settings.lateralJitterShape == LateralJitterShape.PerSlice;
                        sb.AppendLine($"  {(hazardNow ? "⚠" : "ⓘ")} 组合禁忌（本帧"
                                    + (hazardNow ? "**正踩在上面**" : "未踩到")
                                    + "）：源 = BlueNoise **且** 形态 = PerSlice 时，"
                                    + $"聚合偏移场退化成对角条纹（本次量到 DDIAG {Sci(slcDDiag)} "
                                    + $"对 DAXIS {Sci(slcDAxis)}，差 ×{(slcDDiag > 0f ? slcDAxis / slcDDiag : float.PositiveInfinity):F0}）。"
                                    + "画面症状是切片台阶不再是台阶、而是一组 45° 的斜纹 —— "
                                    + "比台阶更难被当成「雾本来就这样」。在随机瓦片偏移落地之前，"
                                    + "这两档不要同时开；程序化 + PerSlice 不受影响"
                                    + "（hash 之间独立 ⇒ CLT 那条论证在**那一档**上是对的）。");
                    }

                    // ------------------------------------------------------ 判据㉑ 档位接线（双向）
                    // 在 **slice 1** 上做，不是 slice 0：z 步进 × 0 = 0 让两个形态档逐位相同
                    // ⇒ D_COL ≡ D_SLC ≡ 0 ⇒「恰好有一个为 0」会以**两个都为 0** 的方式假通过 ——
                    // 一个自己造不出失败的判据。
                    //
                    // 四个槽位走的是 InterlockedMax 而不是 Min：Min 的初值 0 会让四个都读 0，
                    // 同样是一个造不出失败的判据。
                    {
                        uint dCol  = raw[k_SlotJitDCol];
                        uint dSlc  = raw[k_SlotJitDSlc];
                        uint dProc = raw[k_SlotJitDProc];
                        uint dBlue = raw[k_SlotJitDBlue];

                        bool shapeIsCol = settings.lateralJitterShape == LateralJitterShape.PerColumn;
                        bool shapeOk = shapeIsCol ? (dCol == 0u && dSlc > 0u)
                                                  : (dSlc == 0u && dCol > 0u);
                        bool srcOk   = blueOnline ? (dBlue == 0u && dProc > 0u)
                                                  : (dProc == 0u && dBlue > 0u);

                        sb.AppendLine($"{Mark(shapeOk && srcOk)}判据㉑ 档位接线（双向：恰好一个为 0，且是 settings 指名的那一个）：");
                        sb.AppendLine($"    形态：settings = {settings.lateralJitterShape} ⇒ 期望 "
                                    + $"{(shapeIsCol ? "D_COL" : "D_SLC")} = 0；"
                                    + $"实测 D_COL {dCol}, D_SLC {dSlc}（×{Sci(k_JitProbeDScale)}）");
                        sb.AppendLine($"    源：线上生效 = {(blueOnline ? "BlueNoise" : "Procedural")} ⇒ 期望 "
                                    + $"{(blueOnline ? "D_BLUE" : "D_PROC")} = 0；"
                                    + $"实测 D_PROC {dProc}, D_BLUE {dBlue}");
                        sb.AppendLine("  ⓘ 它为什么不是循环论证：**期望**来自 settings 对象（美术填的那个枚举），"
                                    + "**读数**来自 shader 从 cbuffer 解出来的档位，中间整条 "
                                    + "settings → JitterParamsOf → _VistaFroxelJitter → "
                                    + "VistaFroxelJitterZStride/UseBlueNoise 都在载荷路径上。"
                                    + "两对各只放开一个自由度（形态那一对的源取自 cbuffer，源那一对的形态取自 cbuffer），"
                                    + "所以一格红能指到具体是哪一路接错了。");
                        sb.AppendLine("  ⓘ 「另一个 > 0」这一半是必需的：只判「选中的那个为 0」时，"
                                    + "一个把两档实现成同一件事的 bug（比如 zStride 根本没被读）会全绿 ——"
                                    + "而那正是本节要抓的东西。");
                    }
                }
            }

            // ---------------------------------------------------------------- 分配口径
            if (volume.allocatedDesc.HasValue)
            {
                var d = volume.allocatedDesc.Value;
                sb.AppendLine($"分配口径：{d}");
                sb.AppendLine($"  AP 的接手点应当是 handoff = {d.handoffMeters:F3} m，"
                            + $"不是 far = {d.farMeters:F1} m（差 {d.farMeters - d.handoffMeters:F3} m）。"
                            + "近层与 AP 现在都从 t = 0 开始积分，两层同开时近段的雾被算两遍。"
                            + "光把 AP 的 nearDistanceKm 推到 handoff 是不够的："
                            + "AerialPerspectiveLut 的积分起点是 tPrev = 0.0（AtmosphereLut.compute:375），"
                            + "切片 0 照样会积 [0, near] —— 推远 near 只让第 0 片变长，双计一点没少。"
                            + "起点也得一起移，而那会改变 AP LUT 的语义（相机→t 变成 handoff→t），"
                            + "读端合成要跟着改，所以归 #25。CHANGELOG 的 #19 待办已按此更正。");
            }

            sb.AppendLine("ⓘ 注入**历史**表没有「写入路径」这回事：双缓冲的交换"
                        + "（VistaFroxelVolume.SwapInjectionBuffers）只改写下标，本帧写的永远是"
                        + "FroxelInjection 指向的那张，下一帧交换后它就成了历史。"
                        + "所以要覆盖的是「读到的是不是上一帧那张」—— 判据⑬（静止恒等性）、"
                        + "⑭b（在线掩码与 CPU 状态双向一致）、⑮（六条失效路径 + 计数守恒）盯的就是这一条。\n"
                        + "  积分表的 RenderGraph 写入路径由判据⑩⑪⑫覆盖，画面侧由 Debug View 的四个档位覆盖。");
            sb.AppendLine("ⓘ 未覆盖（推迟到 #27）：判据⑯ 收敛性 —— 「累积真的降了噪、且没有引入偏差」。"
                        + "三条理由：①#27 本来就持有残影/收敛这一项，且带一个跨布景对照（本节没有对照，"
                        + "而「一个跨布景稳定复现的差值只有在两个布景做同样工作时才是尺子噪声」）；"
                        + "②今天唯一便宜的形式是 max-vs-max 的统计门，摆不紧，而"
                        + "「一个把『未判达标』印成『达标』的判据比一条平门更危险」；"
                        + "③无偏这一半已经有解析恒等式兜着 —— e 空间里振幅 1 的深度抖动，"
                        + "其期望**正好**等于不抖时的采样点（几何均值 = e 空间中点），"
                        + "而它要打败的噪声由上面判据⑮b 那个 JITTER_SPREAD 量出来了。\n"
                        + "  代价点名：现在实现它要么加第九个核、要么让重投影探针那一趟去读注入表，"
                        + "而后者会推翻那一趟「刻意不声明 froxelInjection」的理由。"
                        + "失效症状（#27 要盯的那个）：「打开抖动之后雾稍微浓了一点」——"
                        + "一个看起来像调好了的系统性偏差。\n"
                        + "  【#22b 括注】第九个核已经存在了（FroxelJitterProbe），但它**不读任何一张表** ——"
                        + "抖动偏移是下标的纯函数。⑯要的是「同一个 froxel 累积多帧之后的方差」，"
                        + "那需要读注入表、还需要一个收敛参考解，两条都不在这个核的能力里。"
                        + "所以上面那句「要么加第九个核」现在应读成「要么加第十个核」，"
                        + "推迟的三条理由一条都没被这次改动结清。");
        }

        /// <summary>
        /// 一档抖动的实测矩（#22b 判据⑱⑲）。定点槽 → 均值/方差/三个相关系数。
        ///
        /// 邻域那两格（rNbX/rNbY）以**基场自己的方差**归一，不为邻域场再累一份二阶矩：
        /// p 与 p + Δ 的边缘分布同分布（蓝噪声瓦片是环形的 ⇒ 逐位同一个多重集合；
        /// 程序化 hash 是同一族），所以 √(Var(p)·Var(p+Δ)) = Var(p)。
        /// 少累一份矩就少一份要对账的读数 —— 但这条前提必须写下来，
        /// 因为它一旦不成立（比如有人把邻域抽头改成 % 64 之外的越界坐标），
        /// 归一化就错了，而症状是 ρ 的**幅值**偏移，正好落在⑱不判幅值的那一半里。
        /// </summary>
        struct JitterTierMoments
        {
            public float mx, my, mz;
            public float vx, vy, vz;
            public float rNbX, rNbY;
            public float rXY, rXZ, rYZ;
        }

        static JitterTierMoments ReadJitterTier(uint[] raw, int b)
        {
            const float inv = 1f / k_JitProbeCountExpected;
            const float eps = 1e-12f;

            float sx = raw[b + k_JitOfsSum]     / k_JitProbeScale * inv;
            float sy = raw[b + k_JitOfsSum + 1] / k_JitProbeScale * inv;
            float sz = raw[b + k_JitOfsSum + 2] / k_JitProbeScale * inv;

            var m = new JitterTierMoments
            {
                mx = sx, my = sy, mz = sz,
                vx = raw[b + k_JitOfsSq]     / k_JitProbeScale * inv - sx * sx,
                vy = raw[b + k_JitOfsSq + 1] / k_JitProbeScale * inv - sy * sy,
                vz = raw[b + k_JitOfsSq + 2] / k_JitProbeScale * inv - sz * sz,
            };

            m.rNbX = (raw[b + k_JitOfsNbX] / k_JitProbeScale * inv - sx * sx) / Mathf.Max(m.vx, eps);
            m.rNbY = (raw[b + k_JitOfsNbY] / k_JitProbeScale * inv - sx * sx) / Mathf.Max(m.vx, eps);
            m.rXY  = (raw[b + k_JitOfsXY]  / k_JitProbeScale * inv - sx * sy)
                   / Mathf.Max(Mathf.Sqrt(Mathf.Max(m.vx * m.vy, 0f)), eps);
            m.rXZ  = (raw[b + k_JitOfsXZ]  / k_JitProbeScale * inv - sx * sz)
                   / Mathf.Max(Mathf.Sqrt(Mathf.Max(m.vx * m.vz, 0f)), eps);
            m.rYZ  = (raw[b + k_JitOfsYZ]  / k_JitProbeScale * inv - sy * sz)
                   / Mathf.Max(Mathf.Sqrt(Mathf.Max(m.vy * m.vz, 0f)), eps);
            return m;
        }

        static Camera FindGameCamera()
        {
            var cam = Camera.main;
            if (cam != null && cam.isActiveAndEnabled)
                return cam;

            foreach (var c in Camera.allCameras)
            {
                if (c.cameraType == CameraType.Game)
                    return c;
            }
            return null;
        }

        /// <summary>
        /// 同步渲一帧。走 <c>Camera.Render()</c> 而不是 <c>SceneView.RepaintAll()</c>：
        /// 后者是排队重绘，菜单回调返回时还没画完，读回来的会是**上一次**请求的结果 ——
        /// 而 min/max 是跨帧单调的，那种错位会长得非常像一个合理的读数。
        ///
        /// 借一张临时 RT 是为了不往 Game View 的后台缓冲上画（Editor 下那会有一堆
        /// 与本判据无关的警告）。副作用是 cameraTargetDescriptor 的尺寸变成 RT 的尺寸，
        /// 也就是 froxel 体的 XY 由它决定 —— 所以上面把分配口径打出来了。
        /// </summary>
        static void RenderOnce(Camera cam)
        {
            var prev = cam.targetTexture;
            var rt = RenderTexture.GetTemporary(
                Mathf.Max(1, cam.pixelWidth), Mathf.Max(1, cam.pixelHeight), 24,
                RenderTextureFormat.DefaultHDR);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
            }
            finally
            {
                cam.targetTexture = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        static string Mark(bool ok) => ok ? "✔ " : "✘ ";

        static string Sci(float v) => v.ToString("0.000e+0");

        static string Fmt(float v) => float.IsNaN(v) ? "n/a" : v.ToString("0.000000");
    }
}
