using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static Vista.EditorTools.VistaFroxelShadowRig;

namespace Vista.EditorTools
{
    /// <summary>
    /// #27 验收第 3 项：**档 A（Froxel）vs 档 D（AP-only）的逐像素差 + 光轴动态范围**。
    ///
    /// ── 这一项要兑现的是一笔旧账 ──
    /// #18 有三处把结论写成了「未判达标」而不是「达标」，理由都一样：
    /// 那里拿不出紧的解析上界（掠射区 20%、均匀雾 8%、视角③ 的 25%/15% 容量上限），
    /// 而「一个不紧到 15 倍的解析上界写成门，等于没有门」。当时的处置是把它们的
    /// **画质**结论显式推到这里，用一张真实画面上的差来兑现。
    /// 所以本判据问的不是「档 A 对不对」（那是第 1 项），而是
    /// **「档 D 在画面上差了多少、差在哪儿」**。
    ///
    /// ── 与第 1 项（<c>VistaFroxelModeAcceptance</c>）不重复 ──
    /// 第 1 项的三条门量的**全是差分**（阴影开 − 阴影关），目的是让两档共有的那个
    /// −0.29 偏移精确抵消，它问的是「近层的阴影查询работ不工作」。
    /// 本判据恰恰**不**抵消那一项 —— 那个共有偏移正是档 D 的误差本身，是被测对象。
    /// 同一套几何回答不了这两个问题，所以布景也不共用（见下）。
    ///
    /// ── 为什么不用第 1 项那套布景 ──
    /// 那套布景里，缝区看到的是远处地面、侧区看到的是 40 m 处的板，
    /// 两块区域**终止在不同的表面、不同的距离上**。做差分时表面项抵消，所以第 1 项
    /// 不在乎；做绝对比较时它是头号混淆项。本判据要的是「同一块幕布、同一段距离、
    /// 只差空气亮不亮」，所以自己搭一套。
    /// 也**没有**改第 1 项那套：rig 的头注写得很清楚，动它等于让那三条门担保的
    /// 不再是同一个场景。
    ///
    /// ── 布景（幕布 + 侧墙）──
    ///   · 幕布：z = 100 m 的一块大平面，法线恒定 ⇒ 表面辐亮度 S 在全屏**恒等**。
    ///     用平面而不是球：球的内表面 N·L 随位置变，那道梯度会以一个不完全抵消的
    ///     残项漏进下面的对比度差里。
    ///   · 侧墙：x ∈ [70, 72]、y ∈ [0, 118]、z ∈ [0, 95] 的一根高墙，
    ///     **整个落在视锥之外**（视锥在 z = 95 处半宽只有 54.9 m）。
    ///     它一帧都不出现在画面里，只负责在空气里切出一道影子 ——
    ///     墙一旦进画面，它自己的硬剪影边就会以「对比度」的身份混进读数。
    ///     z 只到 95 是**承重**的：太阳的 z 分量朝 −z（从 +z 方向来），
    ///     幕布在 z = 100，指向太阳的射线 z 只增不减 ⇒ 永远够不到 z ≤ 95 的墙
    ///     ⇒ **幕布恒被照亮**。墙一旦铺到 z ≥ 100，幕布上就会出现一道暗带，
    ///     S 恒等这条前提当场作废，而症状是「对比度差莫名其妙地变大」——
    ///     一个看起来更好的结果。⓪ 里为这两条各留了一格会失败的判子。
    ///   · 太阳仰角 60°：低太阳（第 1 项用 25°）的影子几乎与视线平行，
    ///     空气里的明暗界面在屏幕上退化成一条与视线重合的线，分不出区域。
    ///   · 方位角 75°（**不是** 15°）：照抄第 1 项的 15° 时 ROI 一个配对都扫不出来，
    ///     原因见 k_TierSunAzimuthDeg 的注释 —— 那一次失败的形状值得一读。
    ///
    /// ── 相位函数必须设成各向同性（g = 0）──
    /// 被判的是「亮度差**随位置**变化」。而 HG 相位本身就产生一道随视线方向变化的
    /// 梯度，它与近层毫无关系，却和光轴长在同一个维度上。g = 0 把这个混淆项整个删掉，
    /// 代价是布景不像真实场景（真实雾 g ≈ 0.2~0.8）——
    /// 这个代价是值得的：本判据要判的是**有没有光轴**，不是光轴好不好看。
    ///
    /// ── ROI 由几何算出来，不是照着结果画的 ──
    /// 「事先指定一块区域」这句话有一个很容易踩的坑：看着渲染结果去圈一块差得最大的
    /// 区域，等于把结论写进判据。所以这里的 ROI 是**逐像素解析算**的：
    /// 对每个像素沿它自己的视线做一次遮挡积分，得到
    ///   φ = ∫ e^(−t/mfp)·[这一点被墙遮住] dt / ∫ e^(−t/mfp) dt
    /// 也就是**按入散射权重**计的遮蔽路径占比。φ 只用到相机矩阵、墙的包围盒、
    /// 太阳方向三样东西，**一个渲染出来的像素都没读**。
    /// 用 e^(−t/mfp) 加权而不是按长度均分：入散射本来就被透射率压向近端，
    /// 按长度算会把远端那段权重算高，φ 就不再预测对比度。
    ///
    /// ── 两块 ROI 必须配对，不能各圈各的 ──
    /// 幕布是平面 ⇒ 像素到幕布的距离随屏幕半径变（100/cos θ，边角比中心长 15%）。
    /// 光学厚度跟着变，于是「中心一块 vs 侧边一块」这种圈法自带一个与近层无关的差。
    /// 处置：**亮侧 ROI 取暗侧 ROI 关于 u = 0.5 的镜像**。镜像像素与原像素
    /// 屏幕半径逐点相等 ⇒ 距离、光学厚度、表面辐亮度三样按对称性精确相同。
    /// 配不上对的（镜像像素也被遮、或者没打在幕布上）**两个一起丢**，
    /// 宁可样本少，也不要一对里只留一半。
    ///
    /// 于是档 D 的对比度 C_D 有了一个**由对称性给出的预测值：0**。
    /// 这不是一个「量出来大概很小」的数，是一条可以当门用的恒等式 ——
    /// 判据⑱c 判的就是它，而它正好是那个已知缺陷的探针：
    /// <c>VistaAtmospherePass</c> 的 froxelEnabled 闸门不看 <c>fog.mode</c>，
    /// 档 D 下近层照跑。今天它只是白付开销（结果没被合成），
    /// 哪天合成漏进去了，C_D 就不再是 0 —— ⑱c 会红。
    ///
    /// ── 四条门 ──
    ///   ⑱a 幅度：暗侧 ROI 上 |A − D|/D 的中位数 ≥ 1%（Weber），且 ≥ 地板 × 3。
    ///       红的含义有两种，都是要抓的：近层坏了；或者近层在这个布景上**白付开销**。
    ///   ⑱b 动态范围：C_A − C_D ≥ 1%，且 ≥ 地板 × 3。
    ///       ⑱a 单独存在会被「档 A 整体亮了一个常数」通过 —— 那是一次整体偏移，
    ///       不是光轴。⑱b 判的正是「这个差随位置变化」。
    ///   ⑱c 档 D 不该有光轴：|C_D| ≤ 1%。由上面那条对称性预测的 0 摆出来的门。
    ///   ⑱d 地板：A 档连拍两张之间的散布必须 < 1%/3，否则上面三条量的是尺子自己在抖。
    ///
    /// ── 一条明说的缺口 ──
    /// 本判据只判**太阳**造成的光轴。局部灯（手电筒式光锥）在档 D 里**根本不存在**，
    /// 拿它做 A vs D 会得到一个近乎恒真的绿 ——「一个恒真的绿比一个红更贵」。
    /// 局部灯那一路的画质归 #30 与夜景布景，不在这里。
    /// ── 为什么这个类有几个 internal 成员 ──
    /// #27 第 4 项（切片数够不够，<c>VistaFroxelSliceCountAcceptance</c>）复用本类的
    /// **布景几何**与 φ 计算，不是图省事：那一项要在「同一套几何」上比不同切片数，
    /// 而这套几何的四条前提（幕布恒被照亮、侧墙整个在视锥外、φ 两侧分得开、
    /// 首个命中是幕布）已经由 ⓪ 那一节判过了。另起一套等于把这四条重写一遍，
    /// 而「同一个量不留两份实现」——两份几何走歧的那天，两项判据会各自自洽地说谎。
    /// 反过来，**门与阈值一个都不共享**：那些是各自判据的事。
    /// </summary>
    static class VistaFogTierQualityAcceptance
    {
        // ================================================================ 布景常量
        //
        // 每一个数下面都写了它为什么是这个值。没有出处的阈值在第一次红掉的时候，
        // 没有人能判断该改代码还是该改数字。

        /// <summary>幕布所在的 z。100 m 远小于近层远边界（150 m）⇒ 整条光路都归近层管，
        /// 两档的差才是「近层 vs AP」而不是「近层 + AP 的某种混合 vs AP」。</summary>
        internal const float k_BackdropZ = 100f;

        /// <summary>幕布边长。z = 100 处半视场 100·tan30° = 57.7 m，400 有 3.5 倍余量。</summary>
        const float k_BackdropSize = 400f;

        // 侧墙包围盒（局部坐标，相机在 (0, 2, 0)）。这三组数是**解析扫出来的**，不是试出来的：
        // 把 φ 的算法原样搬到 Unity 外面跑了一遍参数扫描（同一套 slab 求交、同一个加权），
        // 因为「先写死，再去量」在这里没法用 —— ROI 空不空由纯几何决定，
        // 拿渲染结果去调几何等于把结论写进判据。
        //
        // · x 从 70 起：墙必须**整个落在视锥外**。视锥在 z = 95 处半宽 0.577·95 = 54.9 m，
        //   70 有 15 m 余量。墙一旦进画面，它自己的剪影会吃掉暗侧 ROI，
        //   而且剪影边缘是一条硬边，会以「对比度」的身份混进读数。⓪ 里有一格判这个。
        // · y 到 118：这个数定的是**影子左边界与光轴的相对位置**。
        //   K = 70 − cot(60°)·sin(75°)·(118 − 2) = +5.3 m > 0 ⇒ 影子整个落在光轴右侧
        //   ⇒ 镜像那一侧恒亮（实扫 φ_lit 的最大值就是 0.0000，不是「很小」）。
        //   扫描结果（96×96，配对占比 / 镜像侧 φ 的 p90）：
        //     105 → 0.3% / 0     112 → 3.2% / 0     118 → 8.2% / 0
        //     124 → 14.6% / 0    130 → 18.4% / 0.062    136 → 4.2% / 0.097    145 → 0% / —
        //   两侧各有一个失效边：K 太大影子缩回墙根（配对数归零），K 转负影子越过光轴
        //   （镜像侧被污染）。118 选在**两个失效边之间**，不是选在配对数最高的 130 ——
        //   130 已经在污染那一侧起步了。
        // · z 到 95：< 幕布的 100。这一条是「幕布恒被照亮」的唯一保证（见 ⓪）。
        const float k_WallXMin = 70f, k_WallXMax = 72f;
        const float k_WallYMin = 0f,  k_WallYMax = 118f;
        const float k_WallZMin = 0f,  k_WallZMax = 95f;

        // 太阳的两个角都带 Tier 前缀，是为了**不**遮住 rig 里同名的 k_SunAltitudeDeg（25°）。
        // C# 会让本类的成员静默盖过 using static 进来的那个，于是「这里的 25 是哪个 25」
        // 这个问题在编译期没有任何人会问 —— 换个名字比留一条注释可靠。

        /// <summary>太阳仰角。见类注释：25° 那种低太阳的影子界面与视线近乎平行。</summary>
        internal const float k_TierSunAltitudeDeg = 60f;

        /// <summary>
        /// 太阳方位角 75°。这个数被改过一次，改的原因值得记下来：
        ///
        /// 第一版照抄第 1 项用了 15°，ROI 扫出来**一个配对都没有**。
        /// 原因是指向太阳的向量 z 分量高达 0.483：一条从空气点射向太阳的线，
        /// 横向挪 12 m 的同时要往 +z 走 95 m，于是它从墙的深度范围里穿出去了 ——
        /// 空气根本没被遮住。而屏幕上唯一 φ > 0.6 的那一块，恰好是墙**自己**挡住幕布的
        /// 那几列，被「首个命中必须是幕布」这条规矩排掉了。
        /// 读数当时看起来完全正常（⓪ 四格全绿），只有配对数是 0 —— 这正是
        /// k_MinPairs 那道门存在的理由。
        ///
        /// 75° 把 z 分量压到 0.129（影子基本不跑深度），同时留下 0.483 的横向投射量。
        /// 不取 90°（z 分量恰好为 0，最干净）的理由与不取 0° 的是同一条：
        /// 那会让光轴与 froxel 网格的另一根轴完全对齐，判据于是对一类真实的采样错位免疫。
        /// </summary>
        internal const float k_TierSunAzimuthDeg = 75f;

        // ================================================================ ROI 判定

        /// <summary>暗侧：遮蔽路径占比至少这么多。</summary>
        const float k_PhiShadow = 0.60f;

        /// <summary>亮侧（镜像）：遮蔽路径占比至多这么多。</summary>
        const float k_PhiLit = 0.10f;

        /// <summary>
        /// 两侧 φ 的中位数至少要差这么多，否则**不可判定**。
        ///
        /// 这一条是给布景兜底的，不是给被测对象的：上面那套墙/太阳的几何是手算的，
        /// 而手算的几何**会错**（本项目在方位角符号上栽过一次，读数当时完全正常）。
        /// φ 是从同一份几何解析算出来的，所以它不能证明几何「对」，
        /// 但它能证明「这两块 ROI 在几何上确实一亮一暗」——
        /// 而那正是下面三条门唯一需要几何提供的东西。
        /// 分不开时报缺口，不报通过：「不可判定必须是缺口，不能是通过」。
        /// </summary>
        const float k_PhiSeparationMin = 0.40f;

        /// <summary>配对样本数下限。少于这个数时中位数本身就没有意义，报不可判定。</summary>
        const int k_MinPairs = 400;

        // ================================================================ 门槛

        /// <summary>Weber 1%，全项目统一的可见阈。三条门共用同一个数是刻意的：
        /// 它们判的是同一件事的三个侧面 ——「这个差在画面上看得见吗」。</summary>
        const float k_Gate = k_WeberThreshold;

        /// <summary>地板必须比门低这个倍数。与第 1 项同一个数、同一条理由。</summary>
        const float k_NoiseHeadroom = 3f;

        /// <summary>
        /// 采图前空转的帧数。与第 1 项的 64 帧同一条推导：
        /// historyTimeConstant 0.33 s ≈ 20 帧一个常数，64 帧 ≈ 3.2 个常数 ⇒ 残留 ~4%，
        /// 于是「上一张图是什么」不再进入读数。
        /// </summary>
        internal const int k_SettleFramesTier = 64;

        /// <summary>φ 的积分步数。192 步铺在 100 m 上 ⇒ 0.52 m/步，
        /// 远细于墙在画面上最窄处的投影（z = 60 处 2 m 宽）。</summary>
        const int k_PhiSteps = 192;

        // ================================================================ 事先写死的预测：**被证伪了**
        //
        // 下面三个数是在跑之前算出来并提交的（「先写死，再去量」）。它们错了。
        // 留在这里不是留个念想 —— 是因为「一个被推翻的预测」与「一个从没写下的预测」
        // 在报告上必须能区分开，而后者永远不会红。
        //
        // 原模型：单次散射 + 各向同性相位 + 均匀介质 + 恒等幕布。
        //   T   = e^(−D/mfp)，D = 105 m，mfp = 120 ⇒ T = 0.417
        //   J   = (E/4π)·(1 − T) = 0.0464 E        ← 视线上积出来的入散射
        //   S   = (albedo/π)·E·(N·L) = 0.0206 E    ← 幕布：albedo 0.5，N·L = 0.129（很掠）
        //   ⇒ C_A = J·φ/(S·T + J) = 0.844·φ = 0.587；L_off/L_lit = S/(S·T+J) = 0.375
        //
        // 实测：C_A = 0.1805（预测 0.587，差 3.25×），L_off/L_lit = 0.003（预测 0.375，差 125×）。
        //
        // 查出来的原因，两条，都在代码里能指着看，不是"大概是因为"：
        //
        //  (1) 入散射**不是**全部来自太阳直射。AtmosphereScattering.hlsl 的
        //      VistaFogSourceRadiance 返回 direct·sunShadow + ambientRadiance·k，
        //      天光环境项不乘阴影；同文件 VistaEvaluateScatterSample 里的 multiScattered
        //      也不乘。阴影只削掉直射那一份，于是
        //          C_A = φ · f_sun，  f_sun = 太阳直射占入散射的比例
        //      实测 f_sun = 0.1805/0.694 = 0.26 —— 晴空正午 g=0 下天光 + 多次散射占七成多，
        //      这个量级是对的（直射各向同性项 E/4π ≈ 0.080E，天光均值 L̄ 与它同量级）。
        //      原模型把 f_sun 默认成了 1。
        //
        //  (2) 幕布表面的辐亮度比雾的入散射小两个多数量级（0.003 而不是 0.375）。
        //      这不是本判据的问题，是已登记的那笔债：这套布景里表面通路与雾通路的
        //      能量标定差三个数量级（见 #29 与 CHANGELOG 的「表面 vs 雾 能量标定」）。
        //      本判据被它咬到，是这笔债的**第二次独立目击**。
        //
        // 结论：这两个常数不再当门用，降级成 ⓘ 存根。⑱e 换成下面那条没有可调常数的恒等式。
        // 不把带子从 2× 拉到 4× —— 那只是把结论写进判据。

        /// <summary>C_A 的原解析预测（中位）。已被证伪，只在报告里对账用。</summary>
        const float k_PredictedContrastA = 0.587f;

        /// <summary>关掉雾之后亮侧该剩下的比例 = S/(S·T+J)。已被证伪，只在报告里对账用。</summary>
        const float k_PredictedOffRatio = 0.375f;

        // ================================================================ 入口

        [MenuItem("Window/Vista/Acceptance: Fog Tier A vs D (Quality)", priority = 137)]
        static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== #27 验收第 3 项：档 A（Froxel）vs 档 D（AP-only） ===");
            sb.AppendLine();

            bool pass = Acceptance(sb);

            sb.AppendLine();
            sb.AppendLine(pass
                ? "结论：**通过**。两档在画面上差得看得见；这个差随位置变化（是光轴，不是偏移）；"
                + "档 D 自己没有光轴；而这个对比度落在几何算出的天花板底下 —— "
                + "四条各自能被一处改动单独打红。"
                : "结论：**未通过**（或不可判定）。逐条看上面的 ✘ / ⓘ。");

            if (pass) Debug.Log(sb.ToString());
            else      Debug.LogError(sb.ToString());
        }

        static bool Acceptance(StringBuilder sb)
        {
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
            // 相位函数由本判据自己存还：rig 的 RigState 只托管两个消费者共用的那几项，
            // 而 g 是**这一条**判据的前提（见类注释），塞进 rig 会让第 1 项莫名其妙地
            // 跟着改布景。
            float prevG = fog.anisotropy;
            bool  prevForceNoShadow = VistaFroxelVolume.s_DebugForceNoSunShadow;

            RenderTexture rt = null;
            GameObject root  = null;
            Material mat     = null;
            Texture2D tex    = null;
            bool ok = true;

            try
            {
                state.Apply();
                fog.anisotropy = 0f;
                VistaFroxelVolume.s_DebugForceNoSunShadow = false;

                // ARGBFloat：要在 Weber 1% 上量差，half 的相对精度约 1e-3，
                // 只差一个数量级 —— 量化噪声会被报成信号。与第 1 项同一条理由。
                rt = new RenderTexture(k_RtSize, k_RtSize, 24,
                                       RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);

                BuildTierRig(litShader, layer, feature.groundLevelWorldY, rt,
                             out root, out Camera cam, out mat, out Light sun);
                RenderSettings.sun = sun;

                for (int i = 0; i < k_WarmupFrames; i++)
                    cam.Render();

                Vector3 toSun = -sun.transform.forward;

                if (!VerifyRig(sb, volume, cam, toSun))
                    return false;

                // ================================================ ROI：解析算，不读像素
                var pairs = BuildPairs(sb, toSun, out float phiShadow, out float phiLit);
                if (pairs == null) return false;

                tex = new Texture2D(k_RtSize, k_RtSize, TextureFormat.RGBAFloat, false, true);

                // ================================================ 三次渲染
                fog.mode = VistaFogSettings.Mode.AerialPerspective;
                float[] tierD = CaptureLuminance(cam, rt, tex, k_SettleFramesTier);

                fog.mode = VistaFogSettings.Mode.Froxel;
                float[] tierA = CaptureLuminance(cam, rt, tex, k_SettleFramesTier);

                // 什么都不改，只再渲一帧 —— 这一张与上一张的差就是地板。
                // 与第 1 项同一条理由：地板的定义是「相邻两帧之间它自己动多少」，
                // 给它也空转 64 帧量到的是另一个量。
                float[] tierA2 = CaptureLuminance(cam, rt, tex, 1);

                // 第四张：整档关掉雾。只用来说明「画面里有多少是雾」——
                // 没有这一张，上面那个 C_A 是「对比度」还是「幕布本来就这么花」分不清。
                fog.mode = VistaFogSettings.Mode.Off;
                float[] tierOff = CaptureLuminance(cam, rt, tex, 8);
                fog.mode = VistaFogSettings.Mode.Froxel;

                // ================================================ 统计
                float dMedian = Percentile(tierD, 0.50f);
                float floorDenom = Mathf.Max(dMedian * k_DenomFloorFraction, 1e-12f);
                sb.AppendLine($"布景口径：档 D 亮度中位数 {Sci(dMedian)}，"
                            + $"相对差的分母下限 {Sci(floorDenom)}"
                            + $"（= 中位数 × {k_DenomFloorFraction:0.###e+00}）"
                            + $"，相位 g = {fog.anisotropy:F2}（判据自己设的，见头注）");
                sb.AppendLine();

                ok &= GateA(sb, pairs, tierA, tierD, tierA2, floorDenom);
                sb.AppendLine();
                ok &= GateBC(sb, pairs, tierA, tierD, tierA2);
                sb.AppendLine();
                ok &= ReportShaft(sb, pairs, tierA, tierD, tierOff, phiShadow, phiLit);
            }
            finally
            {
                VistaFroxelVolume.s_DebugForceNoSunShadow = prevForceNoShadow;
                fog.anisotropy = prevG;
                state.Restore();

                if (tex  != null) Object.DestroyImmediate(tex);
                if (root != null) Object.DestroyImmediate(root);
                if (mat  != null) Object.DestroyImmediate(mat);
                if (rt   != null) { rt.Release(); Object.DestroyImmediate(rt); }
            }

            return ok;
        }

        // ==================================================================== 布景前提

        /// <summary>
        /// 四条布景前提。它们**不是**被测对象，但每一条不成立时，下面三条门都会给出
        /// 一个看起来正常的读数 —— 这正是第 1 项的探路工具栽过的那两个跟头的形状。
        /// 所以它们在这里是**会失败的格子**，不是一段注释。
        /// </summary>
        internal static bool VerifyRig(StringBuilder sb, VistaFroxelVolume volume, Camera cam, Vector3 toSun)
        {
            sb.AppendLine("⓪ 布景前提（不成立时下面三条门会给出看起来正常的假读数）");

            var desc = volume.allocatedDesc;
            if (!desc.HasValue)
            {
                sb.AppendLine("  ✘ 近层体一次都没分配下来（allocatedDesc 为 null）。"
                            + "嫌疑顺序：enableInjection / 相机类型闸门 / Prepare 失败。");
                return false;
            }

            var d = desc.Value;
            // 幕布必须整个落在近层里，否则这条光路是「近层 + AP」的混合，
            // 而本判据的全部前提是两档在**同一段路**上作答。
            // 判的是 handoffMeters 不是 farMeters：后者是**参数**，体积实际的远端是前者
            // （最后一片存的距离），两者之间差半片。拿 farMeters 判会在幕布落进那半片时
            // 放行，而那半片正是两层都算过的那段。
            bool covered = k_BackdropZ < d.handoffMeters;
            sb.AppendLine($"  {Mark(covered)}幕布在 z = {k_BackdropZ:F0} m，"
                        + $"近层接手点 {d.handoffMeters:F1} m"
                        + $"（{d.width}×{d.height}×{d.depth}，far {d.farMeters:F1} m，"
                        + $"URP shadow distance {ResolveShadowDistance():F1} m）");

            // 太阳要足够高：低太阳的影子界面与视线近乎平行，两块 ROI 分不开。
            // 判**向量**而不是判角度 ——「描述不会在符号写反时报错」。
            bool steep = toSun.y > 0.8f;
            sb.AppendLine($"  {Mark(steep)}指向太阳 = ({toSun.x:F3}, {toSun.y:F3}, {toSun.z:F3})"
                        + "，要求 y > 0.8（足够高）");

            // 幕布恒被照亮：指向太阳的射线 z 只增不减，而墙止于 z = 95 < 100。
            // 这一条写成会跑的断言而不是注释，因为它的失效症状（幕布上一道暗带）
            // 长得就像「对比度差变大了」——一个看起来更好的结果。
            bool backdropLit = toSun.z > 0f && k_WallZMax < k_BackdropZ;
            sb.AppendLine($"  {Mark(backdropLit)}幕布恒被照亮：指向太阳的 z 分量 {toSun.z:F3} > 0"
                        + $" 且墙止于 z = {k_WallZMax:F0} < 幕布 z = {k_BackdropZ:F0}");

            // 墙必须整个在视锥外。进了画面，它的硬剪影边会以「对比度」的身份混进读数，
            // 而且会吃掉暗侧 ROI —— 第一版 ROI 全空就是这个形状（只是当时的成因是别的）。
            float frustumHalfX = Mathf.Tan(k_Fov * 0.5f * Mathf.Deg2Rad) * cam.aspect * k_WallZMax;
            bool wallHidden = k_WallXMin > frustumHalfX;
            sb.AppendLine($"  {Mark(wallHidden)}墙在视锥外：墙近侧 x = {k_WallXMin:F0} m，"
                        + $"视锥在 z = {k_WallZMax:F0} 处半宽 {frustumHalfX:F1} m");

            bool camOk = cam.cameraType == CameraType.Game;
            sb.AppendLine($"  {Mark(camOk)}cameraType = {cam.cameraType}（闸门放行 Game / SceneView）");

            bool all = covered && steep && backdropLit && wallHidden && camOk;
            if (!all)
                sb.AppendLine("  ⇒ 布景不成立，**不往下量**。");
            return all;
        }

        // ==================================================================== ROI

        struct Pair
        {
            public int dark;    // 暗侧像素下标
            public int lit;     // 它关于 u = 0.5 的镜像
        }

        /// <summary>
        /// 逐像素解析算遮蔽占比 φ，再按 φ 配对。**一个渲染出来的像素都没读。**
        /// </summary>
        static List<Pair> BuildPairs(StringBuilder sb, Vector3 toSun,
                                     out float phiShadowMed, out float phiLitMed)
        {
            phiShadowMed = phiLitMed = float.NaN;

            int w = k_RtSize, h = k_RtSize;
            float tanHalf = Mathf.Tan(k_Fov * 0.5f * Mathf.Deg2Rad);

            var phi = new float[w * h];
            var onBackdrop = new bool[w * h];

            for (int y = 0; y < h; y++)
            {
                float b = (2f * ((y + 0.5f) / h) - 1f) * tanHalf;   // dy / dz
                for (int x = 0; x < w; x++)
                {
                    float a = (2f * ((x + 0.5f) / w) - 1f) * tanHalf;  // dx / dz
                    int i = y * w + x;
                    onBackdrop[i] = PrimaryIsBackdrop(a, b);
                    phi[i] = onBackdrop[i] ? ShadowedFraction(a, b, toSun) : float.NaN;
                }
            }

            var pairs = new List<Pair>();
            var phiDark = new List<float>();
            var phiBright = new List<float>();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    int m = y * w + (w - 1 - x);        // 关于 u = 0.5 的精确镜像
                    if (!onBackdrop[i] || !onBackdrop[m]) continue;
                    if (phi[i] < k_PhiShadow) continue;
                    if (phi[m] > k_PhiLit) continue;    // 配不上对的两个一起丢
                    pairs.Add(new Pair { dark = i, lit = m });
                    phiDark.Add(phi[i]);
                    phiBright.Add(phi[m]);
                }
            }

            sb.AppendLine();
            sb.AppendLine("① ROI（解析算出来的，没读任何一个渲染像素）");
            sb.AppendLine($"  配对数 {pairs.Count}（门 ≥ {k_MinPairs}），"
                        + $"占全屏 {(float)pairs.Count / (w * h):P1}");

            if (pairs.Count < k_MinPairs)
            {
                sb.AppendLine($"  ✘ 配对太少 ⇒ **不可判定**。"
                            + "几何变了（墙/太阳/视场），或者 φ 的两道阈值夹得太紧。");
                return null;
            }

            phiShadowMed = Median(phiDark);
            phiLitMed    = Median(phiBright);
            float sep = phiShadowMed - phiLitMed;
            bool sepOk = sep >= k_PhiSeparationMin;
            sb.AppendLine($"  {Mark(sepOk)}遮蔽占比 φ：暗侧中位数 {phiShadowMed:F3}，"
                        + $"亮侧 {phiLitMed:F3} ⇒ 分离 {sep:F3}，门 ≥ {k_PhiSeparationMin:F2}");
            sb.AppendLine("  ⓘ φ 按 e^(−t/mfp) 加权，也就是按入散射的权重 —— "
                        + "按长度均分会把远端那段算重，φ 就不再预测对比度。");

            if (!sepOk)
            {
                sb.AppendLine("  ⇒ 两块 ROI 在几何上分不开，**不可判定**，不往下量。");
                return null;
            }
            return pairs;
        }

        /// <summary>
        /// 这条视线的首个命中是不是幕布。地面、侧墙挡在前面的像素一律排除 ——
        /// 本判据的全部前提是「同一块幕布、同一段距离」。
        /// 用 z 当参数（相机沿 +z 看，dz ≡ 1），省掉一次归一化。
        /// </summary>
        internal static bool PrimaryIsBackdrop(float a, float b)
        {
            // 地面 y = 0（相机在 y = 2）
            if (b < 0f && (-k_CameraHeightM / b) < k_BackdropZ) return false;

            // 侧墙包围盒：x ∈ [70,72]、y ∈ [0,118]、z ∈ [0,95]。
            // 它本应整个在视锥外（⓪ 有一格判这个），所以这里正常情况下恒返回 true；
            // 留着是因为「恒真」是 ⓪ 保证出来的，不是这个函数自己能保证的。
            // 三个 slab 都从 [0, +∞) 开始夹，任何一个夹空就说明这条视线打不到墙。
            float zEnter = 0f, zExit = float.PositiveInfinity;
            if (!SlabZ(a, k_WallXMin, k_WallXMax, 0f, ref zEnter, ref zExit)) return true;
            if (!SlabZ(b, k_WallYMin - k_CameraHeightM, k_WallYMax - k_CameraHeightM,
                       0f, ref zEnter, ref zExit)) return true;
            if (!SlabZ(1f, k_WallZMin, k_WallZMax, 0f, ref zEnter, ref zExit)) return true;

            return zEnter >= k_BackdropZ;
        }

        /// <summary>
        /// 沿 z 参数化的一维 slab 求交：把 <c>origin + slope·z ∈ [lo, hi]</c> 解成 z 区间，
        /// 与已有区间取交。slope 为 0 时退化成「要么恒在区间内、要么恒在外」。
        /// </summary>
        static bool SlabZ(float slope, float lo, float hi, float origin, ref float zEnter, ref float zExit)
        {
            if (Mathf.Abs(slope) < 1e-8f)
                return origin >= lo && origin <= hi;

            float z0 = (lo - origin) / slope;
            float z1 = (hi - origin) / slope;
            if (z0 > z1) { var t = z0; z0 = z1; z1 = t; }
            zEnter = Mathf.Max(zEnter, z0);
            zExit  = Mathf.Min(zExit,  z1);
            return zEnter <= zExit;
        }

        /// <summary>
        /// 这条视线上，按入散射权重计的遮蔽路径占比。
        /// 权重 e^(−t/mfp) 是雾自己的透射率：入散射被它压向近端，
        /// 按长度均分会把远端那段权重算高，算出来的 φ 就不再预测对比度。
        /// </summary>
        internal static float ShadowedFraction(float a, float b, Vector3 toSun)
        {
            float r = Mathf.Sqrt(1f + a * a + b * b);     // 每单位 z 对应的真实距离
            float dz = k_BackdropZ / k_PhiSteps;
            float num = 0f, den = 0f;

            for (int s = 0; s < k_PhiSteps; s++)
            {
                float z = (s + 0.5f) * dz;
                float t = z * r;
                float wgt = Mathf.Exp(-t / k_MeanFreePathM) * dz * r;
                den += wgt;
                if (Occluded(new Vector3(a * z, k_CameraHeightM + b * z, z), toSun))
                    num += wgt;
            }
            return den > 0f ? num / den : 0f;
        }

        /// <summary>从 P 朝太阳走，会不会撞上那根墙。标准 slab 求交，参数是真实距离 s。</summary>
        static bool Occluded(Vector3 p, Vector3 toSun)
        {
            float sEnter = 0f, sExit = float.PositiveInfinity;
            if (!SlabS(p.x, toSun.x, k_WallXMin, k_WallXMax, ref sEnter, ref sExit)) return false;
            if (!SlabS(p.y, toSun.y, k_WallYMin, k_WallYMax, ref sEnter, ref sExit)) return false;
            if (!SlabS(p.z, toSun.z, k_WallZMin, k_WallZMax, ref sEnter, ref sExit)) return false;
            return sEnter <= sExit && sExit > 0f;
        }

        static bool SlabS(float o, float d, float lo, float hi, ref float sEnter, ref float sExit)
        {
            if (Mathf.Abs(d) < 1e-8f)
                return o >= lo && o <= hi;

            float s0 = (lo - o) / d;
            float s1 = (hi - o) / d;
            if (s0 > s1) { var t = s0; s0 = s1; s1 = t; }
            sEnter = Mathf.Max(sEnter, s0);
            sExit  = Mathf.Min(sExit,  s1);
            return sEnter <= sExit;
        }

        // ==================================================================== ⑱a / ⑱d

        static bool GateA(StringBuilder sb, List<Pair> pairs,
                          float[] tierA, float[] tierD, float[] tierA2, float floorDenom)
        {
            sb.AppendLine("② ⑱a 幅度：档 A 与档 D 在暗侧差得看得见吗；⑱d 地板");

            var rel   = new List<float>(pairs.Count);
            var noise = new List<float>(pairs.Count);
            foreach (var p in pairs)
            {
                float den = Mathf.Max(tierD[p.dark], floorDenom);
                rel.Add(Mathf.Abs(tierA[p.dark] - tierD[p.dark]) / den);
                noise.Add(Mathf.Abs(tierA2[p.dark] - tierA[p.dark]) / den);
            }
            rel.Sort();
            noise.Sort();

            float relMed   = rel[rel.Count / 2];
            float floorP90 = noise[Mathf.Min(noise.Count - 1, Mathf.RoundToInt(0.90f * (noise.Count - 1)))];

            bool d1 = floorP90 < k_Gate / k_NoiseHeadroom;
            sb.AppendLine($"  {Mark(d1)}⑱d 地板：档 A 连拍两张，|Δ|/D 的 p90 = {Sci(floorP90)}"
                        + $"，门 < {Sci(k_Gate / k_NoiseHeadroom)}（= 1% / {k_NoiseHeadroom:F0}）");

            bool a1 = relMed >= k_Gate && relMed >= floorP90 * k_NoiseHeadroom;
            sb.AppendLine($"  {Mark(a1)}⑱a 幅度：|A − D|/D 中位数 = {Sci(relMed)}"
                        + $"，门 ≥ {Sci(k_Gate)} 且 ≥ 地板 × {k_NoiseHeadroom:F0} = {Sci(floorP90 * k_NoiseHeadroom)}");
            sb.AppendLine($"  ⓘ 分布：p10 {Sci(rel[rel.Count / 10])}  "
                        + $"p50 {Sci(relMed)}  "
                        + $"p90 {Sci(rel[Mathf.Min(rel.Count - 1, rel.Count * 9 / 10)])}");
            sb.AppendLine("  ⓘ ⑱a 红有两种含义，都是要抓的：近层坏了；"
                        + "或者近层在这个布景上**白付了开销**（差到看不见）。");

            return a1 && d1;
        }

        // ==================================================================== ⑱b / ⑱c

        static bool GateBC(StringBuilder sb, List<Pair> pairs,
                           float[] tierA, float[] tierD, float[] tierA2)
        {
            sb.AppendLine("③ ⑱b 动态范围：这个差是光轴（随位置变）还是一次整体偏移；⑱c 档 D 的对照");

            float cA  = PairContrast(pairs, tierA);
            float cD  = PairContrast(pairs, tierD);
            float cA2 = PairContrast(pairs, tierA2);

            float floorC = Mathf.Abs(cA2 - cA);
            float delta  = cA - cD;

            bool b1 = delta >= k_Gate && delta >= floorC * k_NoiseHeadroom;
            sb.AppendLine($"  {Mark(b1)}⑱b C_A − C_D = {Sci(cA)} − {Sci(cD)} = {Signed(delta)}"
                        + $"，门 ≥ {Sci(k_Gate)} 且 ≥ 地板 × {k_NoiseHeadroom:F0} = {Sci(floorC * k_NoiseHeadroom)}"
                        + $"（地板 = 档 A 连拍两张的对比度差 {Sci(floorC)}）");

            bool c1 = Mathf.Abs(cD) <= k_Gate;
            sb.AppendLine($"  {Mark(c1)}⑱c 档 D 不该有光轴：|C_D| = {Sci(Mathf.Abs(cD))}"
                        + $"，门 ≤ {Sci(k_Gate)}");
            sb.AppendLine("  ⓘ C_D 的预测值是 **0**，由对称性给出而不是量出来的："
                        + "亮侧像素是暗侧像素关于 u = 0.5 的镜像 ⇒ 屏幕半径逐点相等 ⇒ "
                        + "到幕布的距离、光学厚度、表面辐亮度三样精确相同；"
                        + "而 g = 0 让相位函数不随视线方向变。档 D 没有逐 froxel 阴影，"
                        + "于是一对像素之间**没有任何东西可以不同**。");
            sb.AppendLine("  ⓘ 这条门同时是那个已知缺陷的探针：VistaAtmospherePass 的 froxelEnabled "
                        + "闸门不看 fog.mode，档 D 下近层照跑。今天它只是白付开销（结果没被合成），"
                        + "哪天合成漏进去了，C_D 就不再是 0。");

            return b1 && c1;
        }

        /// <summary>
        /// 逐对算对比度再取中位数，**不是**先各自取中位数再相减。
        /// 逐对算才用得上「镜像 ⇒ 屏幕半径相等」这条对称性；
        /// 先聚合再相减会把两块区域的半径分布差重新放进来。
        /// </summary>
        static float PairContrast(List<Pair> pairs, float[] img)
        {
            var c = new List<float>(pairs.Count);
            foreach (var p in pairs)
            {
                float lit = img[p.lit];
                if (lit <= 0f) continue;
                c.Add((lit - img[p.dark]) / lit);
            }
            return Median(c);
        }

        // ==================================================================== 读数

        /// <summary>光轴的动态范围，外加 ⑱e：实测对比度要落在事先写死的解析预测附近。</summary>
        static bool ReportShaft(StringBuilder sb, List<Pair> pairs,
                                float[] tierA, float[] tierD, float[] tierOff,
                                float phiShadow, float phiLit)
        {
            sb.AppendLine("④ 光轴动态范围（亮侧 / 暗侧，逐对算）+ ⑱e 像素侧对比度与几何侧天花板对账");

            var rA = new List<float>(pairs.Count);
            var rD = new List<float>(pairs.Count);
            var offRatio = new List<float>(pairs.Count);
            foreach (var p in pairs)
            {
                if (tierA[p.dark] > 0f) rA.Add(tierA[p.lit] / tierA[p.dark]);
                if (tierD[p.dark] > 0f) rD.Add(tierD[p.lit] / tierD[p.dark]);
                if (tierA[p.lit]  > 0f) offRatio.Add(tierOff[p.lit] / tierA[p.lit]);
            }
            rA.Sort();
            rD.Sort();
            offRatio.Sort();

            sb.AppendLine("   | 档 | p10 | p50 | p90 |");
            sb.AppendLine("   |---|---|---|---|");
            sb.AppendLine($"   | A（Froxel） | {Q(rA, 0.10f)} | {Q(rA, 0.50f)} | {Q(rA, 0.90f)} |");
            sb.AppendLine($"   | D（AP-only） | {Q(rD, 0.10f)} | {Q(rD, 0.50f)} | {Q(rD, 0.90f)} |");

            float cA = PairContrast(pairs, tierA);

            // ---- 亮侧的档间偏置：两档在**没有阴影**的地方该逐点相同 ----
            // 亮侧像素两档都全亮、同一个屏幕半径、同一段光学厚度。它们的差里
            // 不含阴影，只含「A 与 D 对同一个积分的两种离散化」。
            // 先量出这个数，下面两格都要靠它才能说话。
            var dAD = new List<float>(pairs.Count);
            var e2s = new List<float>(pairs.Count);
            foreach (var p in pairs)
            {
                if (tierD[p.lit]  > 0f) dAD.Add((tierA[p.lit] - tierD[p.lit]) / tierD[p.lit]);
                if (tierD[p.dark] > 0f) e2s.Add((tierD[p.dark] - tierA[p.dark]) / tierD[p.dark]);
            }
            float delta = Median(dAD);
            float e2 = Median(e2s);

            // ---- ⑱e：像素侧量到的对比度，必须落在几何侧算出的天花板底下 ----
            //
            // 这是整份判据里**唯一**把「读出来的像素」和「算出来的几何」接起来的一格。
            // 上界 φ 是模型无关的：阴影最多只能削掉全部入散射，削不掉比它更多。
            // 越界只有三种解释，都是真缺陷 —— 阴影乘到了天光环境项上、乘到了
            // 多次散射上、或者乘了两次。布景怎么改这条都成立。
            //
            // 下界是 0：C_A ≤ 0 说明暗侧不比亮侧暗，光轴根本没出来。
            //
            // 它是**单边**的，而且当前余量 3.8×（0.182 vs 0.694），只抓得住粗错。
            // 想把它收成双边，需要一个 f_sun 的独立测量 —— 下面那格解释了为什么
            // 「拿档 D 当无阴影参考」这条看着最顺的路走不通。
            bool okBound = cA > 0f && cA <= phiShadow;
            sb.AppendLine($"   {Mark(okBound)}⑱e 几何天花板：C_A {Sci(cA)} 必须落在 (0, φ] = (0, {phiShadow:F3}]");
            sb.AppendLine($"   ⓘ 由此反解出的太阳直射占比 f_sun = C_A/φ = {(cA / phiShadow):F3}。"
                        + "剩下的 1 − f_sun 是天光环境项 + 多次散射，它们**不乘阴影**"
                        + "（AtmosphereScattering.hlsl 的 VistaFogSourceRadiance 与 "
                        + "VistaEvaluateScatterSample），所以在阴影里照亮不误。");

            // ---- 一条走不通的路，记在这里，免得下次再走一遍 ----
            sb.AppendLine($"   ⓘ 档间偏置：亮侧（两档都无阴影，该逐点相同）A 比 D 亮 {delta:P1}；"
                        + $"暗侧 (D−A)/D 却只有 {Sci(e2)} ≈ 0。");
            sb.AppendLine("     曾想拿「暗侧 A/D 对照」当 C_A 的第二份实现（档 D 没有逐 froxel 阴影 ⇒"
                        + "差出来的就是阴影）。实测否掉了这条路：它的前提是无阴影处 A ≡ D，"
                        + $"而实测差 {delta:P1}，与要测的 φ·f_sun = {Sci(cA)} 同量级。"
                        + "尺子的地板与被测量同量级时，尺子会自己伪造一个结论 ——"
                        + "按它建出来的门，容差会比被测量的整个取值范围 (0, φ] 还宽，恒绿。");

            float lo = k_PredictedContrastA / 2f;
            float hi = k_PredictedContrastA * 2f;
            sb.AppendLine($"   ⓘ 对账那条**被证伪**的事前预测：C_A 实测 {Sci(cA)}，"
                        + $"原预测 {k_PredictedContrastA:F3}（带 [{lo:F3}, {hi:F3}]）—— 没中。"
                        + "原模型把 f_sun 默认成了 1，见本文件常数区的结案。留这一行是因为"
                        + "「一个被推翻的预测」与「一个从没写下的预测」在报告上必须能区分开。");

            float offMed = offRatio.Count > 0 ? offRatio[offRatio.Count / 2] : float.NaN;
            sb.AppendLine($"   ⓘ 雾占比：关掉雾之后亮侧只剩 {offMed:F3}（原预测 {k_PredictedOffRatio:F3}）。"
                        + "这也没中，而且差 100 倍 —— 它指的是已登记的那笔债"
                        + "（表面通路与雾通路的能量标定差三个数量级），本判据是它的第二次独立目击。"
                        + $"顺带它确认了上面那个对比度**确实是雾的**：画面里 {(1f - offMed):P1} 是雾。");

            // φ 只用几何算，对比度只用像素算。两者同号同量级才说明量到的确实是那根光轴 ——
            // 「一个非零的数回答不了它是从哪儿来的」，⑱e 这两格就是问出处的那一步。
            sb.AppendLine($"   ⓘ 几何侧：暗侧遮蔽 {phiShadow:F3}、亮侧 {phiLit:F3}"
                        + "（亮侧恒为 0 是几何保证的，不是「很小」）。");

            sb.AppendLine();
            sb.AppendLine("   ⚠ 缺口（登记在案，不是遗漏）：");
            sb.AppendLine("   · 本判据只判**太阳**造成的光轴。局部灯的光锥在档 D 里根本不存在，"
                        + "拿它做 A vs D 得到的是一个近乎恒真的绿。那一路归 #30 与夜景布景。");
            sb.AppendLine("   · g 被设成 0。真实雾 g ≈ 0.2~0.8，相位函数会改变光轴的**形状**，"
                        + "本判据对那件事完全免疫 —— 它只保证「有没有」，不保证「好不好看」。");
            sb.AppendLine("   · #18 推过来的三笔账（掠射 20%、均匀雾 8%、视角③ 的 25%/15%）"
                        + "是**远场**的，而这套布景只有 100 m。它们要的是另一套相机俯仰角的布景，"
                        + "本判据没有覆盖到。");
            sb.AppendLine("   · ⑱e 只有上界没有下界，余量 3.8×，只抓得住粗错。"
                        + "收成双边需要 f_sun 的独立测量，而「拿档 D 当无阴影参考」这条路"
                        + "已被上面那 24% 的档间偏置否掉。");
            sb.AppendLine($"   · **新登记的债**：亮侧两档差 {delta:P1}。那里没有阴影、没有局部灯、"
                        + "没有局部雾体，同一个像素同一段 100 m —— 两档对同一个积分的离散化"
                        + "不该差这么多。本判据没有针对它的门（没有事先算好的期望值，"
                        + "现在补一个阈值等于把结论写进判据）。它要单独一项去跑。");
            return okBound;
        }

        static string Q(List<float> v, float q)
            => v.Count == 0 ? "n/a"
             : v[Mathf.Clamp(Mathf.RoundToInt(q * (v.Count - 1)), 0, v.Count - 1)].ToString("F3");

        // ==================================================================== 布景

        /// <summary>
        /// 幕布 + 地面 + 一根高侧墙。与第 1 项那套布景**不共用**，理由见类注释。
        /// 名字带 Tier 前缀是为了不静默盖住 rig 里同签名的 Build。
        /// </summary>
        internal static void BuildTierRig(Shader litShader, int layer, float groundLevelWorldY, RenderTexture rt,
                                 out GameObject root, out Camera cam, out Material mat, out Light sun)
        {
            root = new GameObject("Vista Fog Tier Quality Rig") { hideFlags = HideFlags.HideAndDontSave };
            root.transform.position = new Vector3(0f, groundLevelWorldY, 0f);

            var camGo = new GameObject("Camera") { hideFlags = HideFlags.HideAndDontSave };
            camGo.transform.SetParent(root.transform, false);
            camGo.layer = layer;
            camGo.transform.localPosition = new Vector3(0f, k_CameraHeightM, 0f);
            camGo.transform.localRotation = Quaternion.identity;      // 水平朝 +Z

            cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.cullingMask = 1 << layer;
            cam.orthographic = false;
            cam.fieldOfView = k_Fov;
            cam.nearClipPlane = k_NearClip;
            cam.farClipPlane = k_FarClip;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.allowHDR = true;
            cam.allowMSAA = false;
            cam.targetTexture = rt;
            cam.aspect = 1f;                  // 必须在 targetTexture 之后：赋目标纹理会重算 aspect

            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = false;
            camData.volumeLayerMask = 0;      // 不吃当前场景的 Volume：曝光/色调会改读数
            camData.antialiasing = AntialiasingMode.None;
            camData.renderShadows = true;

            var lightGo = new GameObject("Sun") { hideFlags = HideFlags.HideAndDontSave };
            lightGo.transform.SetParent(root.transform, false);
            lightGo.layer = layer;
            sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Hard;   // 逐 froxel 那边只做 1 tap，软阴影关键字在探针里被断言为 0
            sun.shadowStrength = 1f;
            sun.intensity = 1f;
            // Euler(仰角, 方位+180)：与 VistaTimeOfDay.ApplyRotation 同一个约定。
            lightGo.transform.localRotation =
                Quaternion.Euler(k_TierSunAltitudeDeg, k_TierSunAzimuthDeg + 180f, 0f);

            mat = new Material(litShader) { hideFlags = HideFlags.HideAndDontSave };
            var encoded = new Color(0.5f, 0.5f, 0.5f, 1f).gamma;
            mat.SetVector("_BaseColor", new Vector4(encoded.r, encoded.g, encoded.b, 1f));
            mat.SetFloat("_Cull", 0f);
            mat.SetFloat("_Smoothness", 0f);
            mat.SetFloat("_Metallic", 0f);

            var ground = MakeQuad(root.transform, layer, mat, "Ground");
            ground.localPosition = Vector3.zero;
            ground.localRotation = Quaternion.Euler(90f, 0f, 0f);
            ground.localScale = new Vector3(k_GroundSizeM, k_GroundSizeM, 1f);

            // 幕布：法线恒定（+Z，朝太阳那一侧）⇒ 表面辐亮度在全屏恒等。
            // 这是 ⑱c 那条「C_D 预测为 0」成立的三个前提之一。
            var backdrop = MakeQuad(root.transform, layer, mat, "Backdrop");
            backdrop.localPosition = new Vector3(0f, 0f, k_BackdropZ);
            backdrop.localRotation = Quaternion.identity;
            backdrop.localScale = new Vector3(k_BackdropSize, k_BackdropSize, 1f);

            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Side Wall";
            wall.hideFlags = HideFlags.HideAndDontSave;
            wall.layer = layer;
            wall.transform.SetParent(root.transform, false);
            var col = wall.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
            var wr = wall.GetComponent<MeshRenderer>();
            wr.sharedMaterial = mat;
            wr.shadowCastingMode = ShadowCastingMode.On;   // 实体盒，不需要 TwoSided
            wr.receiveShadows = true;
            wr.lightProbeUsage = LightProbeUsage.Off;
            wr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            wall.transform.localPosition = new Vector3(
                (k_WallXMin + k_WallXMax) * 0.5f,
                (k_WallYMin + k_WallYMax) * 0.5f,
                (k_WallZMin + k_WallZMax) * 0.5f);
            wall.transform.localScale = new Vector3(
                k_WallXMax - k_WallXMin,
                k_WallYMax - k_WallYMin,
                k_WallZMax - k_WallZMin);
        }

        static string Mark(bool ok) => ok ? "✔ " : "✘ ";
    }
}
