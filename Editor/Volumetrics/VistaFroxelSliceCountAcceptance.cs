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
    /// ── 参考解夹逼每一档，地板用同一个估计量 ──
    /// 第一版一趟只渲头尾两张参考图，四档全拿头一张去比 ⇒ 最后一档承担了整趟漂移，
    /// 而那恰好是地板门盯着的那一档。出厂档那一趟就红在这里（详见 <c>Sweep</c> 的头注）。
    /// 现在是 <c>ref₀ → 16 → ref₁ → 32 → ref₂ → 64 → ref₃ → 128 → ref₄</c>，
    /// 每档比两侧参考图的逐像素平均，地板则拿**中间那张参考图**比它两侧的平均
    /// —— 那个量的真值按构造恒为 0，时间间距与每一档的测量完全相同。
    /// 阈值、分位数、收敛阶的接受带一个都没动：变的是尺子，不是门。
    ///
    /// ── 两趟：抖动关 / 出厂档 ──
    /// 抖动关（<c>JitterMode.Off</c>，同时也关掉时间重投影）量的是**纯空间离散化**；
    /// 出厂档（程序化抖动 + 重投影）是真正上线的那一档。
    /// 两趟一起跑是因为真正要回答的问题是「抖动帮我盖住了多少」，
    /// 而那个差决定 64 能不能降到 32（移动端那一档要用）。
    ///
    /// ── 哪一趟判哪道门（这一节在第三次跑之后重排过，得说清楚重排的理由）──
    /// 收敛阶的解析预测**只在抖动关那一趟成立**（有随机残差时 err 不再是 C/N^p），
    /// 所以 <see cref="Order"/> 只在 ② 判；Weber 判的是**上线那张画面看不看得出来**，
    /// 而 ② 那个配置从不出货，所以 <see cref="Weber"/> 只在 ③ 判。
    /// 第一版两道门都在两趟跑：于是 ② 的 Weber 对一个永远不会出货的配置报红 ——
    /// 一个健康的出货版本也会让它**恒红**，而「一格恒红的判据不是判据」。
    ///
    /// 同一次重排还修了一处作用域错误：地板余量那道门原本只看**最细档**，
    /// 却被当成 Weber 的硬前提 —— 而 Weber 只读**出厂档**，根本不碰最细档。
    /// 注释给的理由（「三个比值里有两个用到它」）说的是收敛阶，代码却拿它卡了两道门，
    /// 注释与代码不一致、且注释是对的。现在地板余量**逐门各看各读的那一档**：
    /// Order 的前提是 err(最细) ≥ 地板×3，Weber 的前提是 err(出厂) ≥ 地板×3。
    ///
    /// **这次重排不是在放松。** 对 ③ 的净效果是更严格的那个方向：
    /// 旧接线下 ③ 在最细档那一格红完就整趟返回，Weber 连算都没算；
    /// 新接线下 Weber 有了自己的前提，而 err(出厂) 4.164e-03 同样落在
    /// 地板 4.144e-03 的 3 倍以内 ⇒ **Weber 仍然不判**，这一项仍然停在「不可判定」。
    /// 这一条必须写在这里：重排门的归属之后判定若立刻变绿，形状正是本项目反复
    /// 警告的那一种，所以重排的正确性**不能**靠「跑出来是绿的」验证 ——
    /// 它靠的是「每道门的前提只看它自己读的那一档」这条能独立论证的规则。
    ///
    /// 由此少了一道曾经想加的门：「地板 ≤ Weber 阈值」（尺子比阈值还吵就什么都证不了）。
    /// 它是上面两条的**推论**而不是新信息 —— err(出厂) ≥ 3·地板 与
    /// err(出厂)·4/3 ≤ 1% 同时成立时，地板必 ≤ 0.25%。
    /// 「一个恒真的绿比一个红更贵」，所以它只入 ⓘ。
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
        /// 一档的读数要高过本趟地板这么多倍，才算「这一档量到的是信号不是噪声」。
        ///
        /// **它是逐门的前提，不是一道全局门**：<see cref="Order"/> 拿它卡**最细档**
        /// （三个比值里有两个用到最细档），<see cref="Weber"/> 拿它卡**出厂档**
        /// （Weber 只读出厂档，不碰最细档）。第一版只卡最细档却同时当了两道门的前提，
        /// 那是个作用域错误 —— 详见类头注「哪一趟判哪道门」。
        ///
        /// 不成立时对应的门**不判**（计入未通过），不是判过 ——
        /// 「不可判定必须是缺口，不能是通过」。
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

        /// <summary>
        /// 出厂相机的近裁剪面（m）。froxel 体的近端**就是它**：
        /// <c>VistaVolumetricFogSettings.Resolve</c> 拿 <c>Mathf.Max(0.01f, cameraNearPlane)</c>
        /// 当体的 near，所以切片分布完全由 <c>ln(far / near)</c> 决定。
        ///
        /// 为什么它在这里出现：rig 的相机近裁剪面是 0.1（<c>VistaFroxelShadowRig.k_NearClip</c>），
        /// 而出厂是 0.3 ⇒ 同样 far = 150 m 下 <c>ln(far/near)</c> 差 17.7%，
        /// rig 比出厂**严 1.18 倍**。方向是对的（尺子比被担保的对象严），
        /// 但这一项**并不通过** —— 而一把偏严的尺子只能撑起「通过」，撑不起「未通过」：
        /// ② 报的那个 2.77% 完全可能是这 1.18 倍的产物。
        /// 所以出厂几何必须有**自己的读数**，不能靠一句「方向是对的」推过去。
        ///
        /// 0.3 这个数来自 `Project-ARPG/Assets/Scenes/Demo.unity` 的唯一一台相机。
        /// 它写死在这里、没有去读宿主场景 —— 那是刻意的：本 rig 自带相机、
        /// 自带 layer、自带太阳，「与宿主场景无关」是它成立的前提，
        /// 而 #27 第 1 项正是栽在「对宿主场景敏感却不检查也不打印」上。
        /// 代价是这个数会过期，所以 ⑤ 那一节把两个近裁剪面**都打印出来**，
        /// 并且判的是两者之间的**比值规律**而不是某个绝对值 ——
        /// 数过期了比值仍然成立，过期的只有「这就是出厂那一档」这句话。
        /// </summary>
        const float k_ShippingNearClip = 0.3f;

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
                ? "结论：**通过**。切片数旋钮真的改了体的形状；②（抖动关）里误差随 N 按"
                + "**一阶**收敛（而不是不收敛、也不是二阶）；参考解自身的残差有独立估计；"
                + "③（出厂档）里出厂 64 片的读数高出本趟地板 3 倍以上**且**修正后落在 Weber 1% 以内。"
                : "结论：**未通过**（或不可判定）。逐条看上面的 ✘ / ⓘ。"
                + "注意「不判」也计入未通过 ——「不可判定必须是缺口，不能是通过」。");

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
            public float nearM, farM;   // 体的两端 (m) —— ⑤ 的 ln(far/near) 从这里来，不另算一遍
        }

        /// <summary>一趟（抖动关 / 出厂档）里每一档的误差分位数。</summary>
        struct TierErr
        {
            public int slices;
            public float p50, p90, p99, max;
            public float p99Lit, p99Edge, p99Dark;   // 按 φ 分箱，只入 ⓘ
        }

        /// <summary>
        /// 哪一趟。**两道门的归属从这一个值推出来**，不由调用方分别给 ——
        /// 「Order 判 + Weber 判」和「两道都不判」这两种组合在这里表达不出来，
        /// 而第一版的毛病正是前者（Weber 对一个从不出货的配置恒红）。
        /// </summary>
        enum Pass
        {
            /// <summary>抖动关：解析收敛阶在这里成立，但这个配置不出货。</summary>
            JitterOff,
            /// <summary>出厂档：Weber 要判的就是这一张画面，但收敛阶预测在这里不成立。</summary>
            Shipping,
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
                if (!ProbeTiers(sb, vf, volume, cam, reads,
                                "⓪ 旋钮响应：写下去的切片数与体实际分到的形状"))
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
                //
                // 每一趟判哪道门由 Pass 一个值决定，而不是由调用方分别传两个 bool ——
                // 后者表达得出「两道门都判」与「两道门都不判」这两种没有意义的组合，
                // 而第一版的毛病恰恰就是前者。见类头注「哪一趟判哪道门」。
                sb.AppendLine();
                sb.AppendLine("② 抖动关（JitterMode.Off ⇒ 无抖动、无时间重投影）＝ 纯空间离散化");
                vf.jitterMode = JitterMode.Off;
                bool okOff = Sweep(sb, vf, cam, rt, tex, admitted, phi, nAdmit,
                                   Pass.JitterOff, out var errOff, out float floorOff, out _);
                ok &= okOff;

                sb.AppendLine();
                sb.AppendLine("③ 出厂档（程序化抖动 + 时间重投影）＝ 真正上线的那一档");
                vf.jitterMode = JitterMode.Procedural;
                vf.depthJitterAmount = 1f;
                bool okOn = Sweep(sb, vf, cam, rt, tex, admitted, phi, nAdmit,
                                  Pass.Shipping, out var errOn, out _, out _);
                ok &= okOn;

                // ================================================ ④ 抖动盖住了多少
                sb.AppendLine();
                ok &= CompareJitter(sb, errOff, errOn, shipIdx);

                // ================================================ ⑤ 出厂近裁剪面：同一条规律的正交方向
                sb.AppendLine();
                ok &= NearClipSweep(sb, vf, volume, cam, rt, tex,
                                    admitted, phi, nAdmit, admitLen, minHandoff,
                                    reads, errOff, floorOff);

                // ================================================ ⑥ 成本：只写解析，不量
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
                               VistaFroxelVolume volume, Camera cam, List<TierRead> reads,
                               string head)
        {
            sb.AppendLine(head);
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
                    nearM = d.nearMeters, farM = d.farMeters,
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
        /// 一趟扫描。参考解**夹逼**每一档：
        /// <c>ref₀ → 16 → ref₁ → 32 → ref₂ → 64 → ref₃ → 128 → ref₄</c>，
        /// 第 k 档比的是它两侧参考图的**逐像素平均** (ref_k + ref_{k+1})/2。
        ///
        /// ── 为什么改成夹逼（这一段是第一次跑之后补的，得说清楚它不是在松门）──
        /// 第一版一趟只渲两张参考图（头一张、尾一张），四档全部拿**头一张**去比。
        /// 于是 128 片（最后一档）承担了几乎整趟的机器漂移，而它恰好是
        /// <see cref="k_FloorHeadroom"/> 那道门盯着的那一档 —— 出厂档那一趟就红在这里：
        /// 最细档 p99 5.739e-03 &lt; 地板 4.877e-03 × 3，而地板里占大头的不是邻帧抖动
        /// （2.057e-03）而是整趟漂移。三档读数全和地板同量级、且不单调（128 比 64 还差），
        /// 于是收敛阶与 Weber 都判不了。
        ///
        /// **改的是估计量，不是门。** 阈值（×3）、判的分位数（p99）、收敛阶的接受带
        /// 一个都没动。动的是两件事，而且两件必须一起动：
        ///   ① 每一档与**时间上最近**的参考解比，线性漂移在两侧平均里一阶对消；
        ///   ② 地板改用**同一个估计量**去量 —— 拿中间那张参考图 ref_j 当「被测档」，
        ///      比它两侧的 ref_{j−1}、ref_{j+1} 的平均。那个量的真值**按构造恒为 0**，
        ///      所以它报出来的全是尺子自己的抖动 + 残余漂移，时间间距还与
        ///      每一档的测量完全相同。
        /// 只做 ① 不做 ② 才是松门：那会拿一个旧口径的地板去卡一个新口径的误差。
        ///
        /// 旧口径的整趟漂移仍然算出来、以 ⓘ 印在报表上，因为
        /// **「夹逼买到了多少」本身是个需要被看见的读数** —— 若这两个数差不多，
        /// 说明漂移不是线性的，那这次改动没解决问题，报表必须能说出这件事。
        /// （第三次跑的实际结果：② 买到了 5.35 倍，③ 只有 1.04 倍 —— 也就是
        /// ③ 的地板根本不是漂移，是出厂配置自己的跑间随机性。这条自检响过一次。）
        /// </summary>
        static bool Sweep(StringBuilder sb, VistaVolumetricFogSettings vf, Camera cam,
                          RenderTexture rt, Texture2D tex,
                          bool[] admitted, float[] phi, int nAdmit,
                          Pass pass, out TierErr[] errs, out float floorOut, out bool usable)
        {
            int K = k_Tiers.Length;
            errs = new TierErr[K];
            floorOut = 0f;
            usable = false;
            bool ok = true;

            var refs   = new float[K + 1][];
            var tierPx = new float[K][];

            vf.sliceCount = k_RefSlices;
            refs[0] = CaptureLuminance(cam, rt, tex, k_SettleFramesTier);

            // 邻帧地板：什么都不改，只再渲一帧。「地板的定义是相邻两帧之间它自己动多少」，
            // 给它也空转 64 帧量到的是另一个量（⑱ 同一条理由）。
            // 它不是下面那道门的主地板，但保留着：它把「帧间抖动」与「跑间漂移」分开，
            // 而这两者的比例正是 ⑱ 那笔漂移债要查的东西。
            float[] refNext = CaptureLuminance(cam, rt, tex, 1);

            float denom = Mathf.Max(MedianOver(refs[0], admitted, nAdmit) * k_DenomFloorFraction,
                                    1e-12f);
            float floorAdj = RelQuantile(refs[0], refNext, admitted, denom, k_JudgedQuantile);

            for (int k = 0; k < K; k++)
            {
                vf.sliceCount = k_Tiers[k];
                tierPx[k] = CaptureLuminance(cam, rt, tex, k_SettleFramesTier);

                vf.sliceCount = k_RefSlices;
                refs[k + 1] = CaptureLuminance(cam, rt, tex, k_SettleFramesTier);
            }

            for (int k = 0; k < K; k++)
                errs[k] = Measure(k_Tiers[k], Mid(refs[k], refs[k + 1]),
                                  tierPx[k], admitted, phi, denom);

            // ---- 地板：同一个估计量，真值按构造恒为 0 ----
            // ref_j 与它两侧参考图的平均之差。j 只能取内点 ⇒ K−1 个样本。
            // 取**最大**而不是中位数：三个样本的中位数太脆，而取最大只会让门更难过
            // —— 方向是「宁可不可判定，不可假通过」。这个偏保守的取法要说出来，
            // 因为每一档的误差是单次抽样、地板却是三抽取大，两者口径本来就不完全对称。
            float floorBracket = 0f;
            var bracketSamples = new List<float>();
            for (int j = 1; j < K; j++)
            {
                float f = RelQuantile(Mid(refs[j - 1], refs[j + 1]), refs[j],
                                      admitted, denom, k_JudgedQuantile);
                bracketSamples.Add(f);
                floorBracket = Mathf.Max(floorBracket, f);
            }

            // 旧口径：头尾两张参考图之差。只入 ⓘ —— 它现在不卡任何东西，
            // 但它是「夹逼买到了多少」的唯一读数。
            float driftWhole = RelQuantile(refs[0], refs[K], admitted, denom, k_JudgedQuantile);

            float floor = Mathf.Max(floorAdj, floorBracket);
            // ⑤ 那一节要拿它算比值的传播不确定度。传出去而不是在那边重算一遍：
            // 地板的口径（邻帧 vs 夹逼取大）是这把尺子的定义之一，「同一个量不留两份实现」。
            floorOut = floor;

            sb.AppendLine($"  参考解 N = {k_RefSlices}，**夹逼**（ref₀→16→ref₁→32→ref₂→64→ref₃→128→ref₄，"
                        + $"每档比两侧参考图的逐像素平均）；采纳集中位亮度 "
                        + $"{Sci(MedianOver(refs[0], admitted, nAdmit))}，"
                        + $"相对差分母下限 {Sci(denom)}"
                        + $"（= 中位数 × {k_DenomFloorFraction:0.###e+00}）");

            var bs = new StringBuilder();
            for (int j = 0; j < bracketSamples.Count; j++)
                bs.Append(j == 0 ? "" : ", ").Append(Sci(bracketSamples[j]));
            sb.AppendLine($"  地板 p99：邻帧 {Sci(floorAdj)}；夹逼地板 {{{bs}}} ⇒ 取最大 {Sci(floorBracket)}"
                        + $" ⇒ 本趟地板 {Sci(floor)}"
                        + (floorBracket > floorAdj
                            ? "（夹逼地板更大 —— 相邻区间里仍有机器自变动）"
                            : "（邻帧更大 —— 相邻区间内没有可观测的残余漂移）"));
            sb.AppendLine($"  ⓘ 旧口径「整趟漂移」（ref₀ vs ref₄）= {Sci(driftWhole)}，"
                        + $"是夹逼地板的 {(floorBracket > 0f ? driftWhole / floorBracket : float.NaN):F2} 倍"
                        + " —— 这个倍数就是夹逼买到的东西；若它 ≈ 1，说明漂移不是线性的，"
                        + "这次改动没解决问题。");

            sb.AppendLine("  逐档 |L_N − L_ref| / max(L_ref, 下限) 的分位数：");
            sb.AppendLine("  ┌ N   ┬ p50      ┬ p90      ┬ p99      ┬ max      ┬ p99(亮)  ┬ p99(边界)┬ p99(暗)  ┐");
            foreach (var e in errs)
                sb.AppendLine($"  │ {e.slices,3} │ {S(e.p50)} │ {S(e.p90)} │ {S(e.p99)} │ "
                            + $"{S(e.max)} │ {S(e.p99Lit)} │ {S(e.p99Edge)} │ {S(e.p99Dark)} │");
            sb.AppendLine("  └─────┴──────────┴──────────┴──────────┴──────────┴──────────┴──────────┴──────────┘");

            // ---- 门的归属：从 pass 推出来，不在这里各写一遍 ----
            bool gateOrder = pass == Pass.JitterOff;   // 解析收敛阶只在纯空间离散化下成立
            bool gateWeber = pass == Pass.Shipping;    // Weber 判的是上线那张画面
            sb.AppendLine($"  本趟判：{(gateOrder ? "收敛阶（Weber 只报）" : "Weber（收敛阶只报）")}"
                        + " —— 见类头注「哪一趟判哪道门」。");

            int shipIdx = System.Array.IndexOf(k_Tiers, k_ShippingSlices);
            float errFinest = errs[errs.Length - 1].p99;
            float errCoarse = errs[0].p99;
            float errShip   = errs[shipIdx].p99;

            // ---- 旋钮响应：两道门共同的前提，所以两趟都判 ----
            // 它防的不是「误差大」，是「五张图逐位相同」—— 那种失效下所有
            //「≤」形状的门都会通过，包括 Weber。因此它必须先于一切。
            bool knobBites = errCoarse >= floor * k_KnobHeadroom;
            sb.AppendLine($"  {Mk(knobBites)}最粗档 N = {k_Tiers[0]} 的 p99 {Sci(errCoarse)} ≥ "
                        + $"地板 × {k_KnobHeadroom:F0} = {Sci(floor * k_KnobHeadroom)}"
                        + "（不成立则「切片数根本没改变画面」与「误差本来就很小」分不开）");
            ok &= knobBites;
            if (!knobBites)
            {
                sb.AppendLine("  ⇒ 旋钮响应不成立，**本趟下面全部不判**"
                            + "（若五张图逐位相同，每一道「≤」形状的门都会无声通过）。");
                return false;
            }

            // 到这里为止，这一趟的读数本身是**成立的**：五档图彼此不同、地板算得出来。
            // 下面那几道门判成什么，与「这批读数能不能拿去给别的判据用」是两件事 ——
            // ⑤b 要的是后者。把两者混成同一个 bool，就等于让 Order 的裁决决定
            // Scaling 跑不跑，而那正是这个文件在 ②/③ 上刚修掉的那种接线。
            usable = true;

            // ---- 地板余量：**逐门各看各读的那一档** ----
            // 第一版只算最细档那一个，却同时当了 Order 与 Weber 的前提 ——
            // 而 Weber 根本不读最细档。见类头注。
            bool finestOverFloor = errFinest >= floor * k_FloorHeadroom;
            bool shipOverFloor   = errShip   >= floor * k_FloorHeadroom;

            sb.AppendLine($"  {(gateOrder ? Mk(finestOverFloor) : "  ⓘ ")}"
                        + $"最细档 N = {k_Tiers[k_Tiers.Length - 1]} 的 p99 {Sci(errFinest)}"
                        + $" ≥ 地板 × {k_FloorHeadroom:F0} = {Sci(floor * k_FloorHeadroom)}"
                        + $"（收敛阶的前提，{(gateOrder ? "本趟判" : "本趟不判收敛阶，只报")}）");
            sb.AppendLine($"  {(gateWeber ? Mk(shipOverFloor) : "  ⓘ ")}"
                        + $"出厂档 N = {k_ShippingSlices} 的 p99 {Sci(errShip)}"
                        + $" ≥ 地板 × {k_FloorHeadroom:F0} = {Sci(floor * k_FloorHeadroom)}"
                        + $"（Weber 的前提，{(gateWeber ? "本趟判" : "本趟不判 Weber，只报")}）");
            sb.AppendLine($"    ⓘ 地板占出厂档读数的 {(errShip > 0f ? floor / errShip : float.NaN):P1}"
                        + " —— 这个比例越接近 100%，那一档量到的越是渲染器自己的噪声。");
            sb.AppendLine($"    ⓘ 地板 {Sci(floor)} 是 Weber 阈值 {k_WeberThreshold:P1} 的 "
                        + $"{floor / k_WeberThreshold:F2} 倍。**不单设一道门**："
                        + "上面那条前提与 Weber 同时成立时它必然 ≤ 25%，"
                        + "是推论不是新信息 ——「一个恒真的绿比一个红更贵」。");

            // ---- 收敛阶 ----
            if (gateOrder && !finestOverFloor)
            {
                ok = false;
                Order(sb, errs, gate: false,
                      whyNotGated: "最细档落在地板里，比值的分母不可信 ⇒ **不判**（计入未通过）");
            }
            else
            {
                ok &= Order(sb, errs, gateOrder,
                            whyNotGated: "抖动档的误差含随机残差，解析收敛阶预测在这一趟不适用");
            }

            // ---- Weber：出厂档，带参考解残差修正 ----
            ok &= Weber(sb, errs, gateWeber, shipOverFloor, floor);
            return ok;
        }

        // ==================================================================== 收敛阶

        /// <summary>
        /// 观测比值 vs 三个假设。带子是三个假设的**几何中点**，没有可调常数。
        ///
        /// <paramref name="whyNotGated"/> 在不判时必须给出**这一次**不判的理由。
        /// 不判有两种成因（这一趟是出厂档 / 最细档落在地板里），
        /// 它们在报表上长得一模一样，而后一种要计入未通过、前一种不计 ——
        /// 「一道只在失败时才留下痕迹的检查，与一道根本没跑的检查在报告上无法区分」。
        /// </summary>
        static bool Order(StringBuilder sb, TierErr[] errs, bool gate, string whyNotGated)
        {
            sb.AppendLine($"  收敛阶（{(gate ? "判" : "只报 —— " + whyNotGated)}）：");
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

        /// <summary>
        /// 出厂档的真误差 vs Weber 1%。
        ///
        /// <paramref name="gate"/> 只在出厂那一趟为真：抖动关那个配置从不出货，
        /// 对它判 Weber 会让一个健康的出货版本**恒红**。
        /// <paramref name="overFloor"/> 是这道门自己的前提 ——
        /// 出厂档的读数必须高出本趟地板 <see cref="k_FloorHeadroom"/> 倍，
        /// 否则那个「≤ 1%」只说明**出厂档与参考解在尺子的噪声里分不开**，
        /// 不说明偏置小。那种情况下判成通过，就是拿噪声地板撑起一个结论。
        /// </summary>
        static bool Weber(StringBuilder sb, TierErr[] errs, bool gate, bool overFloor, float floor)
        {
            int idx = System.Array.IndexOf(k_Tiers, k_ShippingSlices);
            var e = errs[idx];

            // err(N) 是拿 256 片比出来的，在一阶模型下它**低估**真误差：
            //   err(N) = C(1/N − 1/256)，true = C/N ⇒ true = err × (1/N)/(1/N − 1/256)
            // 对 64 片是 ×4/3。不修正的话低估 25%，而低估的方向正好让这道门更容易过。
            double corr = (1.0 / k_ShippingSlices) / Pred(k_ShippingSlices, 1.0);
            float trueP99 = (float)(e.p99 * corr);
            bool under = trueP99 <= k_WeberThreshold;

            string mark = !gate ? "  ⓘ " : (overFloor ? Mk(under) : "✘ ");
            sb.AppendLine($"  {mark}出厂档 N = {k_ShippingSlices}：p99 {Sci(e.p99)} × "
                        + $"参考解残差修正 {corr:F4} = **{Sci(trueP99)}**，"
                        + $"门 ≤ Weber {k_WeberThreshold:P1}"
                        + $" ⇒ {(under ? "在门内" : "超门")}");

            if (!gate)
            {
                sb.AppendLine("    ⓘ **本趟不判 Weber**：这个配置（抖动关）从不出货，"
                            + "对它判「上线画面看不看得出来」会让一个健康的出货版本恒红。"
                            + "这一行仍然印出来，因为「不靠抖动时 64 片够不够」是一条"
                            + "要写进移动端降档账里的独立读数。");
            }
            else if (!overFloor)
            {
                sb.AppendLine("    ✘ **不判**：出厂档的读数没有高出本趟地板 "
                            + $"{k_FloorHeadroom:F0} 倍（见上面那一格）⇒ 上面那个"
                            + $"「{(under ? "在门内" : "超门")}」量到的主要是渲染器自己的噪声，"
                            + "不是离散化偏置。**不可判定必须是缺口，不能是通过**，计入未通过。");
            }

            sb.AppendLine($"    ⓘ 同样修正下 max = {Sci((float)(e.max * corr))}"
                        + "（不判 ——「max 回答不了阈值该摆哪儿」）");

            // 顺带回答「能不能降档」。这不是门：「32 片够用」不是本判据的要求，
            // 把它写成门等于替移动端那一档先把结论定了。
            //
            // 但每一行都必须带上它自己与地板的关系。第四次跑的 ③ 里
            // N = 32 修正后是 Weber 的 0.77 倍、读起来像「32 片就够了」，
            // 而它 6.77e-03 同样落在地板 3.516e-03 的 3 倍以内 —— 那个 0.77
            // 是噪声。一个由地板撑起来的数印成「也在 1% 以内」，
            // 与出厂档那一格被判成通过是同一种错，只是它藏在 ⓘ 里。
            for (int k = 0; k < errs.Length; k++)
            {
                if (k_Tiers[k] >= k_ShippingSlices) continue;
                double c = (1.0 / k_Tiers[k]) / Pred(k_Tiers[k], 1.0);
                float t = (float)(errs[k].p99 * c);
                bool tierOverFloor = errs[k].p99 >= floor * k_FloorHeadroom;
                sb.AppendLine($"    ⓘ N = {k_Tiers[k]}：修正后 p99 {Sci(t)}"
                            + $"（Weber 的 {t / k_WeberThreshold:F2} 倍）"
                            + $" —— {(t <= k_WeberThreshold ? "在 1% 以内" : "超出 1%")}"
                            + (tierOverFloor
                                ? "，且高出本趟地板 3 倍以上（这个数量到的是信号）"
                                : $"，**但它没有高出本趟地板 3 倍（{Sci(errs[k].p99)} < "
                                  + $"{Sci(floor * k_FloorHeadroom)}）⇒ 这一行是噪声，不能当降档依据**")
                            + "。移动端降档要用的就是这一行，但本判据不判它。");
            }

            return !gate || (overFloor && under);
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

        // ==================================================================== ⑤ 出厂近裁剪面

        /// <summary>
        /// 把相机近裁剪面从 rig 的 <c>k_NearClip</c> 换成出厂的
        /// <see cref="k_ShippingNearClip"/>，再跑一遍抖动关那一趟。
        ///
        /// ── 为什么这一节值得多花一趟 ──
        /// 它一次回答两个问题，而第二个比第一个值钱得多。
        ///
        /// **一、出厂几何下 ② 那条真失败还成不成立。**
        /// froxel 体的近端就是相机近裁剪面（<c>VistaVolumetricFogSettings.Resolve</c>），
        /// rig 用 0.1、出厂用 0.3 ⇒ rig 比出厂严。偏严的尺子只能撑起「通过」，
        /// 撑不起「未通过」—— ② 报的 2.77% 完全可能是这个口径差的产物。
        /// 所以出厂几何必须有自己的读数。
        ///
        /// **二、从一个正交方向再验一次「误差是一阶的」。**
        /// 对数分布下第 k 片的相对厚度是 <c>Δ/t ≈ L/N</c>（<c>L = ln(far/near)</c>），
        /// 也就是说**切片密度只通过 L/N 这一个组合进入误差**。
        /// ② 判的是固定 L 下改 N，这一节判的是固定 N 下改 L ——
        /// 同一个 <c>C·(L/N)^p</c> 的两个正交切面。
        ///
        /// 而且这个方向上**参考解残差自动对消**：两趟的参考解都是 256 片、
        /// 都在各自的 near 上，于是
        /// <code>
        ///   err(N, L) = C·L^p·(1/N^p − 1/256^p)
        ///   err(N, L_b) / err(N, L_a) = (L_b / L_a)^p        ← 与 N 无关，也与 C 无关
        /// </code>
        /// 三个假设因此各给一个**纯几何**的预测值，带子照旧取几何中点，
        /// 一个可调常数都没有 —— 与 <see cref="Order"/> 完全同一套方法论。
        ///
        /// ── 为什么这道门需要一个比 <see cref="k_FloorHeadroom"/> 更紧的前提 ──
        /// 一阶预测到最近那条带边的相对余量只有约 7.8%，而 <c>地板 × 3</c>
        /// 对应的比值不确定度约 47% —— **用得上的前提在这里根本不够紧**。
        /// 所以这一节自己算传播不确定度
        /// <c>u = √((σ_a/e_a)² + (σ_b/e_b)²)</c>（σ 取各趟自己的地板），
        /// 并要求 <c>u ≤ 到最近带边的相对余量</c>。
        /// 这个余量是从带子本身算出来的，不是拍的：门与门的前提共用同一套几何中点。
        /// 不满足的档位**不判**（计入未通过口径的「缺口」），只报。
        ///
        /// ── 采纳集为什么可以原样复用 ──
        /// 近裁剪面变大 ⇒ L 变小 ⇒ 同样片数下接手距离**变大**，
        /// 于是 <c>admitLen = 0.85 × minHandoff</c> 这条在 0.1 上算出来的界
        /// 在 0.3 上更宽松（采纳集仍然整个落在纯近层区里）。
        /// 这不是「应该没问题」——下面那一格会拿新探到的 handoff 把它判一次。
        /// 复用同一张掩码是必须的：比值要成立，两趟必须量**同一撮像素**。
        /// </summary>
        static bool NearClipSweep(StringBuilder sb, VistaVolumetricFogSettings vf,
                                  VistaFroxelVolume volume, Camera cam,
                                  RenderTexture rt, Texture2D tex,
                                  bool[] admitted, float[] phi, int nAdmit,
                                  float admitLen, float minHandoffRig,
                                  List<TierRead> readsRig,
                                  TierErr[] errRig, float floorRig)
        {
            float nearRig = cam.nearClipPlane;
            sb.AppendLine($"⑤ 出厂近裁剪面：相机 near {nearRig:F2} m → {k_ShippingNearClip:F2} m"
                        + "（froxel 体的近端就是它）");

            bool ok = true;
            var readsShip = new List<TierRead>();

            cam.nearClipPlane = k_ShippingNearClip;
            try
            {
                // 重新探一遍形状：near 变了，ρ / 最长段 / 接手距离全都跟着变，
                // 而下面那条采纳集的前提正是拿新的 handoff 判的。
                if (!ProbeTiers(sb, vf, volume, cam, readsShip,
                                "  ⓪' 出厂近裁剪面下重新探一遍形状（与 ⓪ 同一段代码，只有 near 不同）"))
                {
                    sb.AppendLine("  ⇒ 出厂近裁剪面下切片数没有生效，**本节不判**。");
                    return false;
                }

                float minHandoffShip = float.PositiveInfinity;
                foreach (var r in readsShip) minHandoffShip = Mathf.Min(minHandoffShip, r.handoff);
                float admitLenShip = (1f - VistaFogSettings.k_HandoffBandFraction) * minHandoffShip;

                bool maskStillValid = admitLen <= admitLenShip;
                sb.AppendLine($"  {Mk(maskStillValid)}采纳集沿用 0.1 口径那一张："
                            + $"admitLen {admitLen:F1} m ≤ 出厂口径的 {admitLenShip:F1} m"
                            + $"（最小接手 {minHandoffRig:F1} → {minHandoffShip:F1} m）"
                            + " —— 两趟必须量同一撮像素，比值才成立。");
                ok &= maskStillValid;
                if (!maskStillValid)
                {
                    sb.AppendLine("  ⇒ 复用的掩码在出厂几何下伸出了纯近层区，**本节不判**。");
                    return false;
                }

                // ---- L 从体自己报的两端算，不在这里重写一遍几何 ----
                //
                // 比值能对消掉 C 与参考解残差的前提是**两趟的 far 完全相同**
                // （err = C·L^p·(1/N^p − 1/256^p) 里只准 L 变）。far 由
                // ResolveFarDistance(farDistanceMeters, maxShadowDistance) 给，
                // 与 near 无关 —— 但「无关」是个可以被将来的改动推翻的性质，
                // 所以这里判它，不是描述它。
                var rRig  = readsRig .Find(r => r.slices == k_ShippingSlices);
                var rShip = readsShip.Find(r => r.slices == k_ShippingSlices);

                bool sameFar = Mathf.Abs(rShip.farM - rRig.farM) <= 1e-3f * Mathf.Max(1f, rRig.farM);
                sb.AppendLine($"  {Mk(sameFar)}两趟的远边界相同：{rRig.farM:F3} m vs {rShip.farM:F3} m"
                            + " —— 只准 near 变，far 一变 C 就不再对消，下面的比值失去意义。");
                ok &= sameFar;
                if (!sameFar)
                {
                    sb.AppendLine("  ⇒ 远边界跟着 near 动了，**比值不判**。");
                    return false;
                }

                double La = System.Math.Log(rRig .farM / rRig .nearM);
                double Lb = System.Math.Log(rShip.farM / rShip.nearM);

                sb.AppendLine($"  ⓘ L = ln(far/near)：rig {La:F4}（near {rRig.nearM:F2} m）"
                            + $" → 出厂 {Lb:F4}（near {rShip.nearM:F2} m），"
                            + $"比值 {Lb / La:F5}（rig 比出厂严 {La / Lb:F3} 倍）。"
                            + "切片密度只通过 L/N 这一个组合进入误差 ⇒ 这就是一阶预测值。");

                sb.AppendLine();
                sb.AppendLine("  ⑤a 出厂近裁剪面下的抖动关那一趟（与 ② 同配置，只有 near 不同）");
                vf.jitterMode = JitterMode.Off;
                bool okShip = Sweep(sb, vf, cam, rt, tex, admitted, phi, nAdmit,
                                    Pass.JitterOff, out var errShip, out float floorShip,
                                    out bool usableShip);
                ok &= okShip;

                // 注意这里**不是** `if (!okShip) return`。⑤a 的收敛阶判成什么，
                // 与 ⑤b 能不能判是两件事：前者是同一批读数在 **N 方向**的比值，
                // 后者是同一批读数在 **L 方向**的比值。⑤a 的阶一旦反常，⑤b 恰恰是
                // 唯一能回答「这个反常是不是从 L/N 这个组合进来的」的读数 ——
                // 此时把它关掉，等于把最该问的那个问题连同红一起丢了。
                // 让一道门的裁决决定另一道门跑不跑，正是本文件在 ②/③ 上刚修掉的接线。
                if (!usableShip)
                {
                    sb.AppendLine("  ⇒ 出厂几何这一趟的**读数本身**不成立（旋钮没响应），**比值不判**。");
                    return false;
                }

                ok &= Scaling(sb, errRig, floorRig, errShip, floorShip, La, Lb);
            }
            finally
            {
                cam.nearClipPlane = nearRig;
            }

            return ok;
        }

        /// <summary>
        /// 逐档比 <c>err(出厂 near) / err(rig near)</c> 与 <c>(L_b/L_a)^p</c>。
        /// 带子是三个假设的几何中点（与 <see cref="Order"/> 同一套），
        /// 前提是这一档的传播不确定度小于到最近带边的余量 —— 详见 <see cref="NearClipSweep"/>。
        /// </summary>
        static bool Scaling(StringBuilder sb, TierErr[] a, float floorA,
                            TierErr[] b, float floorB, double La, double Lb)
        {
            double q  = Lb / La;
            double p1 = q, p2 = q * q, p0 = 1.0;
            double lo = System.Math.Sqrt(p1 * p2);   // 一阶 ↔ 二阶
            double hi = System.Math.Sqrt(p0 * p1);   // 不收敛 ↔ 一阶

            // 前提要多紧，由带子自己定：一阶预测到最近那条边的相对余量。
            double margin = System.Math.Min((p1 - lo) / p1, (hi - p1) / p1);

            sb.AppendLine();
            sb.AppendLine($"  ⑤b 比值 err(出厂 near)/err(rig near) vs (L_b/L_a)^p"
                        + $" —— 预测 一阶 {p1:F4} / 二阶 {p2:F4} / 不收敛 {p0:F4}"
                        + $" ⇒ 带 [{lo:F4}, {hi:F4}]");
            sb.AppendLine($"     判这一档的前提：传播不确定度 u ≤ 到最近带边的余量 {margin:P1}"
                        + $"（u = √((σ_a/e_a)² + (σ_b/e_b)²)，σ 取各趟自己的地板 "
                        + $"{Sci(floorA)} / {Sci(floorB)}）。"
                        + $"**注意这里用不上 k_FloorHeadroom**：地板 × {k_FloorHeadroom:F0} "
                        + "对应的 u 约 47%，比这条带子还宽 —— 那个前提在这一节根本不够紧。");

            bool anyGated = false;
            bool ok = true;
            for (int k = 0; k < a.Length; k++)
            {
                double ea = a[k].p99, eb = b[k].p99;
                double obs = ea > 0.0 ? eb / ea : double.NaN;
                double u = (ea > 0.0 && eb > 0.0)
                    ? System.Math.Sqrt((floorA / ea) * (floorA / ea) + (floorB / eb) * (floorB / eb))
                    : double.PositiveInfinity;

                bool tight  = u <= margin;
                bool inBand = obs >= lo && obs <= hi;
                anyGated |= tight;
                if (tight) ok &= inBand;

                sb.AppendLine($"     {(tight ? Mk(inBand) : "  ⓘ ")}N = {a[k].slices,3}："
                            + $"{Sci(a[k].p99)} → {Sci(b[k].p99)}，比值 {obs:F4}"
                            + $"，u = {u:P1}"
                            + (tight
                                ? (inBand ? "（在带内）" : "（**出带**）")
                                : $"（u > 余量 {margin:P1} ⇒ **这一档不判**，"
                                  + "读数被本趟地板糊住了，分不清三个假设）"));
            }

            if (!anyGated)
            {
                sb.AppendLine("     ✘ **没有任何一档的不确定度够小 ⇒ 本节整体不判**（计入未通过）。"
                            + "「不可判定必须是缺口，不能是通过」。");
                ok = false;
            }

            sb.AppendLine("     ⓘ 这个比值与 N 无关、也与常数 C 无关（参考解残差在两趟里同形对消），"
                        + "所以它是对「误差是一阶的」这条结论的**正交**检验 —— "
                        + "② 固定 L 改 N，这里固定 N 改 L。两者若给出不同的阶，"
                        + "说明误差里有一块不是来自切片密度。");
            return ok;
        }

        // ==================================================================== ⑥ 成本

        static void Cost(StringBuilder sb, List<TierRead> reads)
        {
            sb.AppendLine("⑥ 成本（解析，**本判据不量毫秒**）");
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

        /// <summary>
        /// 一档的误差分位数。<paramref name="refMid"/> 是**夹逼中点**
        /// （这一档两侧参考图的逐像素平均），不是某一张参考图 —— 分子分母都用它，
        /// 两处若走歧，相对误差的分母会来自另一个时刻，那是个查不出来的偏置。
        /// </summary>
        static TierErr Measure(int slices, float[] refMid, float[] img,
                               bool[] admitted, float[] phi, float denom)
        {
            var all  = new List<float>();
            var lit  = new List<float>();
            var edge = new List<float>();
            var dark = new List<float>();

            for (int i = 0; i < admitted.Length; i++)
            {
                if (!admitted[i]) continue;
                float rel = Mathf.Abs(img[i] - refMid[i]) / Mathf.Max(refMid[i], denom);
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

        /// <summary>
        /// 两张参考图的逐像素平均 —— 夹逼估计量里「这一档该和谁比」的那个谁。
        ///
        /// 取平均而不是取更近的那一张：若漂移在这一段里近似线性，
        /// 中点恰好落在被测档的采集时刻上，一阶项对消；取单张只能消掉零阶。
        /// 副作用是参考侧的随机噪声被平均掉 √2 —— 这个副作用也进了地板的口径，
        /// 因为地板用的是**同一个** <c>Mid</c>（见 <see cref="Sweep"/> 的头注 ②）。
        /// </summary>
        static float[] Mid(float[] a, float[] b)
        {
            var m = new float[a.Length];
            for (int i = 0; i < a.Length; i++) m[i] = 0.5f * (a[i] + b[i]);
            return m;
        }

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
