using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista.Editor
{
    /// <summary>
    /// #15 判据②：Aerial Perspective 的两个合成变体是否给出同一个画面。
    /// 变体 A = <see cref="VistaAerialPerspectiveCompositePass"/> 的全屏 pass，
    /// 变体 B = <c>Vista/Lit</c> 在着色末尾就地合成。
    ///
    /// ── 为什么它不能做成「同一次调用比两遍」 ──
    ///
    /// 累加自检（<see cref="VistaLitAccumulationSelfTest"/>）能在**同一个片元调用里**
    /// 同时算两条路再相减，于是量到的差异是纯算术差异，不含任何渲染路径噪声。
    /// 这里做不到：变体 A 是一个独立的全屏 pass，两个变体本身就是**两套渲染配置**，
    /// 只能渲两次再比。而「两次渲染」正是那条自检论证过要避免的东西 ——
    /// 渲染→读回路径带一个 ±1/1024 的加性扰动场（见
    /// <see cref="VistaSelfTestNumerics.k_ReadbackFloor"/>）。
    ///
    /// 所以这里必须补一个**对照**：把同一个变体（A）在完全不改任何状态的情况下
    /// 渲第二遍，量出布景自己的地板。A-vs-B 的差异只能站在这个地板之上判。
    /// 那条扰动场实测随**像素位置**变化、在恒定值上也变化，所以它在
    /// 「同像素、两次渲染之差」里一阶抵消 —— 对照读到 0 就证明了这一点，
    /// 于是 A-vs-B 不必为这条地板买单。这个推理不能只靠说，必须由对照读出来。
    ///
    /// ── 为什么要把差异拆成两个操作数 ──
    ///
    /// A 与 B 的差异有两个来源，它们的成因和后果完全不同：
    ///   ① **距离不同**：A 从深度图反投影出 positionWS，B 直接用插值出来的
    ///      positionWS。前者要过深度缓冲的量化，后者不用。
    ///   ② **量化次数不同**：给定同一个距离，两者调的是同一个
    ///      <c>VistaApplyAerialPerspective</c>，但 A 是往一张**已经量化成 fp16**
    ///      的颜色上混合（写→读→乘→写→读→加→写，三次舍入），
    ///      B 是在寄存器里算完只量化一次。
    ///
    /// 只比最终颜色，这两项混成一个数，出问题定不了位 —— 而它们的修法方向相反
    /// （①要么接受、要么换深度格式；②只能换合成路径）。所以本自检先单独量①
    /// （两个变体各开一个「输出距离而不是颜色」的调试出口），再判最终颜色。
    ///
    /// ── 布景：一帧之内同时给出「正对面」与「掠射面」 ──
    ///
    /// 上半屏：8 块**正对相机**的 Vista/Lit 面板，各占一列，径向距离按几何级数
    /// 从 0.5 km 排到 34 km。正对面上「像素中心射线打到的点」有 CPU 已知的
    /// 精确距离，所以这 8 列既是被测对象，也是**标定距离读出路径本身**的已知量。
    ///
    /// 下半屏：一块水平地面（相机下方 30 m），恰好填满地平线以下那半屏。
    /// 它是深度反投影最难的情形 —— 掠射角下同一行像素之间的深度跨度极大。
    /// 距离范围约 1.7 km → 44 km，是一条连续梯度。
    ///
    /// 两者在几何上不可能互相遮挡：面板全在相机高度**之上**，地面在其**之下**，
    /// 向上的射线打不到向下的平面。中间那条缝（地平线上方一小条）不覆盖任何
    /// 几何，正好用来验证哨兵机制真的在工作。
    ///
    /// ── 六次渲染，每一对只差一件事 ──
    ///
    ///   (1) Off        → 未合成基线（判据②-0 用）
    ///   (2) InShader   → B 的颜色
    ///   (3) Fullscreen → A 的颜色
    ///   (4) Fullscreen → A 的颜色**再来一遍**（对照：布景地板）
    ///   (5) InShader + VISTA_AP_DEBUG_DISTANCE → B 折出的距离
    ///   (6) Fullscreen + s_DebugDistanceOutput → A 折出的距离
    ///
    /// (5) 刻意用 InShader 而不是 Off：这样它与 (2) 之间**只差一个关键字**。
    /// (6) 与 (3) 之间只差「画哪个 pass」。每一对只差一件事，读数才能归因。
    ///
    /// ── 明确不覆盖的路径 ──
    ///
    /// · **高光与环境反射**：材质上显式关掉了。它们是视线相关的，同一块面板内
    ///   不同像素的值会变，那会在颜色差里叠进一个与 AP 无关的项。
    ///   这不是「关掉就没问题了」—— 是**未覆盖**，两个变体在开高光时是否仍然一致
    ///   本自检不作担保。（合成发生在着色**之后**，所以机理上不受影响，
    ///   但机理不是测量。）
    /// · **透明材质**：两个变体都刻意不给透明物合成 AP（理由见
    ///   <c>VistaLighting.hlsl</c> 里 <c>VistaApplyApTail</c> 的注释），
    ///   所以这里没有可比的量。
    /// · **延迟渲染**：变体 B 在概念上就不存在（见 <c>VistaLit.shader</c> 文件头）。
    /// </summary>
    public static class VistaApVariantAgreementSelfTest
    {
        // ────────────────────────────────────────────────────────────────
        // 布景常量
        // ────────────────────────────────────────────────────────────────

        const int k_Size = 128;
        const int k_Columns = 8;
        const int k_ColumnW = k_Size / k_Columns;         // 16 px

        /// <summary>正对面板的采样行。选在上半屏中间，离缝和屏幕上沿都远。</summary>
        const int k_QuadRowY = 96;

        /// <summary>面板高度占视锥高度的比例。取 0.30 → 覆盖第 77~115 行。</summary>
        const float k_QuadBandFrac = 0.30f;

        /// <summary>面板宽度占该列宽度的比例。取 0.70 → 每列第 3~13 个像素。</summary>
        const float k_QuadWidthFrac = 0.70f;

        // 统计用的「一定在板子上」的保守窗口。
        // 面板实际覆盖第 77.3~115.7 行、列内第 2.9~14.1 个像素，这里各缩若干格。
        const int k_QuadRowLo = 80, k_QuadRowHi = 112;
        const int k_QuadColLo = 3, k_QuadColHi = 13;

        /// <summary>
        /// 「一定没有几何」的保守窗口：地平线以上、面板以下那条空带。
        ///
        /// 上界与下界都留了余量，而不是取「算出来刚好挨着」的行号：
        /// 第 62~63 行靠远裁剪面才为空（斜面在那里的命中距离是 73 km），
        /// 第 77 行是面板下沿所在的那一行。两端各让开两格之后，
        /// 这个窗口为空不依赖任何一条边界计算恰好正确 ——
        /// 它要证明的只是「哨兵机制确实能分辨没有几何」，
        /// 不该反过来变成一条对布景尺寸的精密要求。
        /// </summary>
        const int k_EmptyRowLo = 65, k_EmptyRowHi = 75;

        /// <summary>面板上沿之上（实际上沿在第 115.7 行）也一定为空。</summary>
        const int k_EmptyTopRow = 118;

        /// <summary>
        /// 斜面（地面）可用的最高一行。
        /// 地平线在 v = 0.5（第 63/64 行之间）；再往上射线朝上，打不到地面。
        /// 第 61 行的命中距离约 44 km，已经贴着 <see cref="k_FarClip"/>；
        /// 第 62 行约 73 km，超出远裁剪面 → 该行必然是哨兵。
        /// 这个数字只用来划**统计区间**，覆盖与否一律由哨兵判定，不由它判定。
        /// </summary>
        const int k_SlantRowHi = 61;

        /// <summary>地面在相机之下多少米。决定斜面的距离范围（30 m → 1.7~44 km）。</summary>
        const float k_SlantDropM = 30f;

        const float k_Fov = 2f;
        const float k_NearClip = 0.1f;
        const float k_FarClip = 45000f;
        const float k_CameraAltitudeM = 200f;

        const float k_NearColumnKm = 0.5f;
        const float k_FarColumnKm = 34f;

        /// <summary>面板的**线性**底色。写进材质前要预先逆 gamma，见 Build。</summary>
        const float k_BaseLevel = 0.5f;

        // ────────────────────────────────────────────────────────────────
        // 哨兵
        // ────────────────────────────────────────────────────────────────

        /// <summary>颜色档的清屏色。与累加自检用同一个数，理由同：远离任何真实颜色。</summary>
        const float k_ColorSentinel = 30000f;
        const float k_ColorGate = 1e4f;

        /// <summary>
        /// 距离档的清屏色，单位 km。真实距离最大 44，所以 1000 与它隔了 20 倍；
        /// 门限取 200，两侧各留 4.5 倍余量。
        /// 若距离读出路径其实是 fp16，清屏色经 gamma 变换后会溢出成 inf ——
        /// <c>!(v &lt; gate)</c> 的写法把 inf 与 NaN 一并算作未覆盖。
        /// </summary>
        const float k_DistSentinel = 1000f;
        const float k_DistGate = 200f;

        // ────────────────────────────────────────────────────────────────
        // 阈值
        // ────────────────────────────────────────────────────────────────

        /// <summary>Weber 1%：全项目通用的可见性门槛。</summary>
        const float k_RelTol = 0.01f;

        /// <summary>同通道绝对可见性豁免。</summary>
        const float k_AbsTol = 1e-3f;

        /// <summary>fp16 相对精度 2^-11。共享量，见 <see cref="VistaSelfTestNumerics"/>。</summary>
        const float k_Fp16Rel = VistaSelfTestNumerics.k_Fp16Rel;

        /// <summary>
        /// 距离读出路径的相对容差。
        ///
        /// 这个数不是拍的，是**放在一条很宽的空隙里**的：
        ///   · 变体 B 的距离来自 fp32 插值，理论相对误差 ~1e-7；
        ///     即使目标纹理其实是 fp16，也只有 4.88e-4。
        ///   · 任何**结构性**错误（反投影用错矩阵、视点没同步、单位搞错 1000 倍）
        ///     产生的相对差是几十个百分点。
        /// 两个量级之间隔着两三个数量级，所以门限取「至多 4 次 fp16 舍入」
        /// （= 1.95e-3）落在空隙正中，它的精确取值不影响结论。
        /// 它同时比 Weber 1% 低 5 倍 —— 也就是说距离侧比颜色侧判得更严。
        /// </summary>
        const float k_DistRelTol = 4f * k_Fp16Rel;

        /// <summary>
        /// 判据②-0 要求的动态范围倍数。
        ///
        /// 这不是一条物理阈值，是**尺子的分辨力要求**：若 AP 的贡献只比
        /// 判据②b 的容差大一点点，那么「A 与 B 一致」与「AP 根本没起作用」
        /// 会读出同一个数，判据就成了空判。要求至少一个数量级的余量。
        /// 同一类要求在 <see cref="VistaApCompositeAcceptance"/> 里以
        /// 「中灰基线必须离 0 和 1 都够远」的形式出现过。
        /// </summary>
        const int k_DynamicRange = 10;

        /// <summary>
        /// 变体 A 比变体 B 多经历的 fp16 舍入次数。
        ///
        /// B：<c>q(shaded·T + S)</c> —— 一次。
        /// A：<c>q(q(q(shaded)·T) + S)</c> —— 三次（着色写出、乘性趟写出、加性趟写出）。
        /// 差值最多 3 个 ulp。这里用整个 ulp 而不是半个（一次舍入的真实上界），
        /// 方向是保守的，代价是判据松了约 6 倍 —— 而 3×4.88e-4 = 0.15% 仍然
        /// 在 Weber 1% 之下 6.7 倍，所以这份保守没有吃掉分辨力。
        /// </summary>
        const int k_Roundings = 3;

        // 区域编号（报告里用）。
        //
        // 为什么有 k_RegionEdge 这个「什么都不是」的档：面板的边缘落在像素中间
        // （下沿在第 77.3 行、右沿在列内第 14.1 个像素），那些像素既不能算「一定在板上」
        // 也不能算「一定没几何」。把它们并进任何一边都会让某条判据依赖
        // 「边界算得刚好」这个前提 —— 而那正是覆盖判定要靠哨兵、不靠算的原因。
        // 它们照样参与判据②b（有几何就要判），只是不进「空缝为空」那条检查。
        const int k_RegionEdge = 0, k_RegionQuad = 1, k_RegionSlant = 2, k_RegionEmpty = 3;

        // ────────────────────────────────────────────────────────────────
        // 入口
        // ────────────────────────────────────────────────────────────────

        [MenuItem("Window/Vista/Validate AP Variant A-B Agreement", priority = 130)]
        public static void Run()
        {
            var sb = new StringBuilder();
            bool ok;
            try
            {
                ok = Validate(sb);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log(("[Vista] AP 变体 A/B 一致性（#15 判据②）" + (ok ? "通过" : "**失败**") + "\n" + sb)
                      .Replace("\r", "").Replace("\n", "  |  "));
        }

        static bool Validate(StringBuilder sb)
        {
            // ── 前置条件。一条不满足就停：半套布景量出来的数字比没数字更危险。
            var feature = VistaAtmosphereFeature.current;
            if (feature == null)
            {
                sb.AppendLine("**失败**：VistaAtmosphereFeature.current 为 null —— "
                            + "feature 没装进当前 Renderer，或还没有相机渲过一帧。");
                return false;
            }

            var litShader = Shader.Find("Vista/Lit");
            if (litShader == null)
            {
                sb.AppendLine("**失败**：找不到 Vista/Lit。变体 B 就在这个 shader 里，没有它无从比较。");
                return false;
            }

            int layer = FindUnusedLayer();
            if (layer < 0)
            {
                sb.AppendLine("**失败**：32 个 layer 全都有物体在用，布景无法与场景隔离。"
                            + "腾出任意一个空 layer 再跑。");
                return false;
            }

            var ap = feature.aerialPerspective;

            // 要改的全局/资产状态，全部在 finally 里还原。
            var prevMode = ap.compositeMode;
            bool prevFog = RenderSettings.fog;
            bool prevDebugPass = VistaAerialPerspectiveCompositePass.s_DebugDistanceOutput;

            RenderTexture rtColor = null, rtDist = null;
            Texture2D readback = null;
            GameObject root = null;
            Material mat = null;

            try
            {
                // Vista/Lit 永不调 MixFog，但斜面/面板之外若有别的东西掺进来
                // 会让基线不干净；与其推理，不如按 AP 验收那条自检的先例一起关掉。
                RenderSettings.fog = false;

                rtColor = new RenderTexture(k_Size, k_Size, 24,
                    RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);

                // 距离档单独一张 fp32 目标。
                // 不能沿用 ARGBHalf：half 在 40 附近的一个 ulp 是 3.125e-2 km = 31 m，
                // 而被测的 Δd 本身可能就是几十米 —— 那正是「尺子的地板顶出一个结论」
                // 这个反面模式。换成 fp32 之后地板降到 ~2.4e-6 km，
                // 但**是不是真的降下来了要量**（判据②a-0），因为 URP 完全可能
                // 先渲进一张 fp16 中间纹理再 blit 过来。
                rtDist = new RenderTexture(k_Size, k_Size, 24,
                    RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);

                readback = new Texture2D(k_Size, k_Size, TextureFormat.RGBAFloat, false, true);

                Build(litShader, layer, feature.groundLevelWorldY, rtColor,
                      out root, out Camera cam, out mat, out Transform[] quads);

                Vector3 eye = cam.transform.position;
                var known = new float[k_Columns];
                for (int c = 0; c < k_Columns; c++)
                    known[c] = Vector3.Distance(quads[c].position, eye);

                sb.Append("── 布景　layer = ").Append(layer)
                  .Append("　RT = ").Append(k_Size).Append('×').Append(k_Size)
                  .Append("（颜色 ARGBHalf / 距离 ARGBFloat）")
                  .Append("　FOV = ").Append(k_Fov).Append('°')
                  .Append("　相机海拔 = ").Append(k_CameraAltitudeM).Append(" m")
                  .AppendLine();
                sb.Append("　 正对面板 ").Append(k_Columns).Append(" 列，径向距离 ")
                  .Append(known[0].ToString("F1")).Append(" m → ")
                  .Append(known[k_Columns - 1].ToString("F0")).Append(" m")
                  .Append("　斜面：相机下方 ").Append(k_SlantDropM).Append(" m 的水平地面")
                  .AppendLine();
                sb.Append("　 AP 配置　compositeMode(原) = ").Append(prevMode)
                  .Append("　maxDistanceKm = ").Append(ap.maxDistanceKm.ToString("F1"))
                  .Append("　nearDistanceKm = ").Append(ap.nearDistanceKm.ToString("F3"))
                  .Append("　distribution = ").Append(ap.distribution)
                  .Append("　resolution = ").Append(ap.resolution)
                  .AppendLine();
                sb.AppendLine("　 注意：超过 maxDistanceKm 之后 T 与 S 被钉在最后一片，"
                            + "此时距离差**不产生**颜色差 —— 判据②b 在那一段是必然通过的，"
                            + "所以下面要按距离分段报，不能只报一个整屏最大值。");

                // ── 六次渲染 ──────────────────────────────────────────
                //
                // 顺序刻意排成「每一次与它的对照只差一件事」：
                //   (3) 与 (4) 之间什么都不改 → 对照
                //   (2) 与 (5) 之间只差一个材质关键字
                //   (3) 与 (6) 之间只差画哪个 pass
                var modeOff = VistaAerialPerspectiveSettings.CompositeMode.Off;
                var modeIn = VistaAerialPerspectiveSettings.CompositeMode.InShader;
                var modeFull = VistaAerialPerspectiveSettings.CompositeMode.Fullscreen;

                ap.compositeMode = modeOff;
                Warmup(cam, 2);
                var colBase = RenderAndRead(cam, rtColor, readback);

                ap.compositeMode = modeIn;
                Warmup(cam, 2);
                var colB = RenderAndRead(cam, rtColor, readback);

                ap.compositeMode = modeFull;
                Warmup(cam, 2);
                var colA = RenderAndRead(cam, rtColor, readback);

                // 对照：一行状态都不改，直接再渲一遍。
                var colA2 = RenderAndRead(cam, rtColor, readback);

                // 距离档。换目标纹理之后必须重设 aspect（Unity 会跟着目标纹理重算它）。
                SetTarget(cam, rtDist, k_DistSentinel);

                ap.compositeMode = modeIn;
                mat.EnableKeyword("VISTA_AP_DEBUG_DISTANCE");
                Warmup(cam, 3);      // 关键字变体是按需编译的，多空转一帧
                var distB = RenderAndRead(cam, rtDist, readback);
                mat.DisableKeyword("VISTA_AP_DEBUG_DISTANCE");

                ap.compositeMode = modeFull;
                VistaAerialPerspectiveCompositePass.s_DebugDistanceOutput = true;
                Warmup(cam, 2);
                var distA = RenderAndRead(cam, rtDist, readback);
                VistaAerialPerspectiveCompositePass.s_DebugDistanceOutput = false;

                // ── 覆盖掩码 ──────────────────────────────────────────
                //
                // 权威的几何掩码来自**距离档**：那两遍里非哨兵的像素就是有几何的像素。
                // 颜色档只做交叉核对 —— 若两者对「哪些像素有几何」的判断不同，
                // 那说明六次渲染之间布景变了，后面所有比较都不成立。
                var covered = new bool[k_Size * k_Size];
                int nBoth = 0, nOnlyA = 0, nOnlyB = 0, nEmptyCovered = 0, nColorMismatch = 0;
                int nEmptyExamined = 0;
                float emptyMin = float.PositiveInfinity, emptyMax = float.NegativeInfinity;

                for (int i = 0; i < covered.Length; i++)
                {
                    bool a = IsCovered(distA[i].r, k_DistGate);
                    bool b = IsCovered(distB[i].r, k_DistGate);
                    covered[i] = a && b;
                    if (a && b) nBoth++;
                    else if (a) nOnlyA++;
                    else if (b) nOnlyB++;

                    int region = Region(i % k_Size, i / k_Size);
                    if (region == k_RegionEmpty)
                    {
                        nEmptyExamined++;
                        if (covered[i]) nEmptyCovered++;
                        else
                        {
                            // 顺手量一下清屏色**实际**读回来是多少。
                            // 预测不了：Color 型的 backgroundColor 会被做一次 gamma→linear，
                            // 而 1000^2.2 在 fp16 里已经溢出。所以不去预测，只要求它
                            //   ① 在整条空带上是常数（min == max），
                            //   ② 落在门限之外。
                            // 这两条成立，就证明「未覆盖」这个判定确实由一个真实存在的、
                            // 一致的哨兵支撑，而不是碰巧读到了一个大数。
                            emptyMin = Mathf.Min(emptyMin, distA[i].r);
                            emptyMax = Mathf.Max(emptyMax, distA[i].r);
                        }
                    }

                    if (covered[i])
                    {
                        if (!IsCovered(colB[i].r, k_ColorGate) || !IsCovered(colA[i].r, k_ColorGate)
                            || !IsCovered(colBase[i].r, k_ColorGate))
                            nColorMismatch++;
                    }
                }

                bool ok = true;

                sb.AppendLine("── 覆盖统计（哨兵判定，不靠几何推算）");
                sb.Append("　 两档都覆盖 = ").Append(nBoth)
                  .Append(" / ").Append(covered.Length)
                  .Append("　仅 A 覆盖 = ").Append(nOnlyA)
                  .Append("　仅 B 覆盖 = ").Append(nOnlyB)
                  .Append("　颜色档不一致 = ").Append(nColorMismatch)
                  .AppendLine();
                sb.Append("　 保守空带：检查 ").Append(nEmptyExamined)
                  .Append(" 个像素（第 ").Append(k_EmptyRowLo).Append('~').Append(k_EmptyRowHi)
                  .Append(" 行与第 ").Append(k_EmptyTopRow).Append(" 行以上）")
                  .Append("，其中被判为有几何 = ").Append(nEmptyCovered)
                  .Append("　哨兵读回值区间 = [")
                  .Append(float.IsInfinity(emptyMin) ? "—" : emptyMin.ToString("E3")).Append(", ")
                  .Append(float.IsInfinity(emptyMax) ? "—" : emptyMax.ToString("E3")).Append(']')
                  .AppendLine();

                if (nBoth == 0)
                {
                    sb.AppendLine("　 **失败**：没有任何像素被两档同时覆盖。"
                                + "布景没画出来（材质编译失败？相机 cullingMask 不对？），后面全是空判。");
                    return false;
                }

                if (nOnlyA != 0 || nOnlyB != 0)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：两个变体对「哪些像素有几何」的判断不一致。"
                                + "变体 A 靠 VISTA_AP_IS_SKY_DEPTH 从深度图剔天空，变体 B 靠光栅化覆盖；"
                                + "两者本应逐像素相同（同一张深度、无 MSAA）。不同就说明"
                                + "天空判定的阈值与实际远裁剪深度对不上，或六次渲染之间布景动了。");
                }

                if (nColorMismatch != 0)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：有像素在距离档里有几何、在颜色档里却是清屏色。"
                                + "六次渲染之间布景不一致，A-vs-B 的比较不成立。");
                }

                if (nEmptyCovered != 0)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：保守空带里有像素被判为有几何。"
                                + "要么面板/斜面越界到了空带里（尺寸算错），要么哨兵没生效 —— "
                                + "后者更严重：那意味着「未覆盖」根本检测不出来，"
                                + "于是「所有覆盖像素都在容差内」这句话覆盖的是整屏而不是几何。");
                }
                else if (nEmptyExamined == 0)
                {
                    // 这条不会发生（空带的行数是编译期常量），但一条**无法失败的守卫**
                    // 要在报告里点名，否则读者会把「没报错」读成「查过了」。
                    ok = false;
                    sb.AppendLine("　 **失败**：保守空带一个像素都没检查到，这条守卫是空的。");
                }
                else if (!(emptyMin == emptyMax) || !(emptyMin >= k_DistGate) || float.IsNaN(emptyMin))
                {
                    ok = false;
                    sb.Append("　 **失败**：空带里的哨兵读回值不是一个落在门限之外的常数（区间 [")
                      .Append(emptyMin.ToString("E3")).Append(", ").Append(emptyMax.ToString("E3"))
                      .Append("]，门限 ").Append(k_DistGate.ToString("F0"))
                      .AppendLine("）。「未覆盖」这个判定就没有一个可靠的依据，"
                                + "后面所有以覆盖掩码为前提的判据都要打折。");
                }
                else
                {
                    sb.Append("　 空带全为哨兵、读回值恒为 ").Append(emptyMin.ToString("E3"))
                      .AppendLine(" → 哨兵机制确实能分辨「没有几何」，"
                                + "所以上面那个覆盖数不是把整屏都算进去了。");
                }

                // ── 判据②a-0：距离读出路径自己的分辨力 ─────────────────
                //
                // 先量尺子。8 列正对面板的距离是 CPU 已知的精确值，
                // 拿它去标定两条距离读出路径，而不是假设「fp32 目标就是 fp32 精度」。
                // 它同时给出下面判据②a 要用的地板 —— 一份实现，一处调用。
                ok &= JudgeDistanceCalibration(sb, cam, distA, distB, known, out float distFloor);

                // ── 判据②a：Δd 的量级与它落在哪儿 ─────────────────────
                ReportDistanceDelta(sb, distA, distB, covered, distFloor);

                // ── 判据②-0：AP 在这套布景上真的起作用了 ───────────────
                ok &= JudgeNonVacuous(sb, colBase, colB, covered, distB);

                // ── 对照 + 判据②b：颜色一致性 ──────────────────────────
                ok &= JudgeColorAgreement(sb, colA, colA2, colB, covered, distA, distB);

                sb.AppendLine("── 明确未覆盖：高光/环境反射（材质上关掉了，两变体在开它时是否一致本自检不担保）、"
                            + "透明材质（两变体都刻意不合成）、延迟渲染（变体 B 概念上不存在）。");

                return ok;
            }
            finally
            {
                ap.compositeMode = prevMode;
                RenderSettings.fog = prevFog;
                VistaAerialPerspectiveCompositePass.s_DebugDistanceOutput = prevDebugPass;

                if (mat != null) mat.DisableKeyword("VISTA_AP_DEBUG_DISTANCE");
                if (root != null) Object.DestroyImmediate(root);
                if (mat != null) Object.DestroyImmediate(mat);
                if (readback != null) Object.DestroyImmediate(readback);
                if (rtColor != null) { rtColor.Release(); Object.DestroyImmediate(rtColor); }
                if (rtDist != null) { rtDist.Release(); Object.DestroyImmediate(rtDist); }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 判据②a-0：标定距离读出路径
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 拿 8 个 CPU 已知距离去标定两条距离读出路径。
        ///
        /// 变体 B 的路径里没有深度缓冲，所以 <c>|B − 已知|</c> 就是**读出路径本身**
        /// 的地板（插值 + 目标纹理格式 + 读回）。这一点让 B 同时成为被测对象和尺子，
        /// 看似循环，其实不是：判 B 的那条门限来自「fp32 插值 vs 结构性错误」之间
        /// 那条两三个数量级宽的空隙，不依赖任何测量。
        ///
        /// 变体 A 的路径多了一次深度缓冲往返，所以
        /// <c>超出量 = max(0, |A − 已知| − |B − 已知|)</c> 才是深度量化贡献的部分。
        /// 把它与两种候选深度格式的**预测值**比，就能反过来判定 URP 这一帧
        /// 用的是哪种深度格式 —— 这是「测出来，不假设」的落地方式。
        /// 两个候选相差六个数量级，分类不会含糊。
        /// </summary>
        static bool JudgeDistanceCalibration(StringBuilder sb, Camera cam,
                                             Color[] distA, Color[] distB, float[] known,
                                             out float distFloor)
        {
            bool ok = true;
            var gpuVp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;

            // 第一趟：算出读出路径的地板。
            //
            // 必须先有它才能分类：excessA 自己的分辨力受这条地板限制，
            // 拿一个亚分辨力的读数去比预测值，等于让尺子替被测对象作证 ——
            // 「容差取 0 隐含了一条已被实测推翻的前提」的同一个坑。
            var dB = new float[k_Columns];
            var dA = new float[k_Columns];
            var errB = new float[k_Columns];
            distFloor = 0f;
            for (int c = 0; c < k_Columns; c++)
            {
                dB[c] = distB[ColumnX(c) + k_QuadRowY * k_Size].r * 1000f;   // shader 输出的是 km
                dA[c] = distA[ColumnX(c) + k_QuadRowY * k_Size].r * 1000f;
                errB[c] = Mathf.Abs(dB[c] - known[c]);
                distFloor = Mathf.Max(distFloor, errB[c]);
            }

            sb.AppendLine("── 判据②a-0：距离读出路径的分辨力（拿 8 个 CPU 已知距离标定）");
            sb.Append("　 读出地板（|B − 已知| 的最大值）= ").Append(distFloor.ToString("E3"))
              .AppendLine(" m —— 分类门限取 max(2×预测, 地板)，因为低于地板的超出量本身没有分辨力");
            sb.AppendLine("　 列 | 已知 d(m) | B 读出(m) | B 相对差 | A 读出(m) | A 超出 B(m) | 预测 D24(m) | 预测 D32(m) | 判定");

            float worstRelB = 0f;
            int nD24 = 0, nD32 = 0, nUnclear = 0, nD32SubFloor = 0;

            for (int c = 0; c < k_Columns; c++)
            {
                float dKnown = known[c];
                float relB = errB[c] / dKnown;
                worstRelB = Mathf.Max(worstRelB, relB);

                float excessA = Mathf.Max(0f, Mathf.Abs(dA[c] - dKnown) - errB[c]);
                float predD24 = DepthQuantStep(cam, gpuVp, dKnown, true);
                float predD32 = DepthQuantStep(cam, gpuVp, dKnown, false);
                if (predD32 < distFloor) nD32SubFloor++;

                // 一个 ulp 的量化在最坏情况下产生一整步，实测落在 [0, 步长]，
                // 所以「≤ 2×预测」算相容。地板兜底见上面那段注释。
                // 两个候选在远列隔着六个数量级，这条比较在那里不会两边都成立。
                string verdict = excessA <= Mathf.Max(2f * predD32, distFloor) ? "D32"
                               : excessA <= Mathf.Max(2f * predD24, distFloor) ? "D24"
                               : "?";
                if (verdict == "D32") nD32++;
                else if (verdict == "D24") nD24++;
                else nUnclear++;

                sb.Append("　 ").Append(c)
                  .Append(" | ").Append(dKnown.ToString("F1"))
                  .Append(" | ").Append(dB[c].ToString("F1"))
                  .Append(" | ").Append(relB.ToString("E2"))
                  .Append(" | ").Append(dA[c].ToString("F1"))
                  .Append(" | ").Append(excessA.ToString("E2"))
                  .Append(" | ").Append(predD24.ToString("E2"))
                  .Append(" | ").Append(predD32.ToString("E2"))
                  .Append(predD32 < distFloor ? "(地板下)" : "")
                  .Append(" | ").Append(verdict)
                  .AppendLine();

                if (relB > k_DistRelTol)
                {
                    ok = false;
                    sb.Append("　 **失败**：第 ").Append(c)
                      .Append(" 列变体 B 的距离相对差 ").Append(relB.ToString("E3"))
                      .Append(" 超过 ").Append(k_DistRelTol.ToString("E3"))
                      .AppendLine("。B 的路径里没有深度缓冲，这个量级只能来自结构性错误："
                                + "_VistaViewPosKm 与这一帧的相机不同步、"
                                + "VistaWorldToAtmosphere 的单位换算、或插值器丢了精度。");
                }

                if (verdict == "?")
                {
                    ok = false;
                    sb.Append("　 **失败**：第 ").Append(c)
                      .AppendLine(" 列 A 的超出量与两种深度格式的预测都不相容。"
                                + "说明 A 的距离误差不只来自深度量化 —— 反投影本身有问题，"
                                + "或者投影矩阵与 shader 里 UNITY_MATRIX_I_VP 用的那份不是同一个。");
                }
            }

            sb.Append("　 B 最大相对差 = ").Append(worstRelB.ToString("E3"))
              .Append("（门限 ").Append(k_DistRelTol.ToString("E3")).Append("）")
              .Append("　与 D32 相容 ").Append(nD32)
              .Append(" 列 / 与 D24 相容 ").Append(nD24)
              .Append(" 列 / 无法归类 ").Append(nUnclear).Append(" 列")
              .AppendLine();

            // ── 结论要按分辨力说话，不能按「哪个候选先命中」说话 ──
            if (nUnclear == 0 && nD24 == 0)
            {
                float predD24Far = DepthQuantStep(cam, gpuVp, known[k_Columns - 1], true);
                sb.Append("　 结论：**已排除 24 位定点深度** —— 它在最远列的预测是 ")
                  .Append(predD24Far.ToString("F0"))
                  .Append(" m，是读出地板的 ")
                  .Append((predD24Far / Mathf.Max(distFloor, 1e-9f)).ToString("F0"))
                  .AppendLine(" 倍，真有那么大的量化一定读得出来。读数与 32 位浮点深度相容。");
                if (nD32SubFloor > 0)
                {
                    sb.Append("　 但这是一条**上界**，不是等式：D32 的预测在 ")
                      .Append(nD32SubFloor).Append(" / ").Append(k_Columns)
                      .AppendLine(" 列上落在读出地板之下，本自检在那些列上无法区分"
                                + "「深度量化确实只有 D32 那么大」与「深度路径完全不引入量化」。"
                                + "它能担保的是：深度量化的贡献 < 读出地板，"
                                + "也就是**在这套布景上它不是一个可测的差异来源**。");
                }
                sb.AppendLine("　 顺带确认两档不是同一份数据被读了两遍："
                            + "若 (6) 的 debug pass 或 (5) 的关键字有一个没生效，"
                            + "那一档读到的会是合成后的颜色（0.2 量级）而不是 km 级距离，"
                            + "上面的相对差会是 ~99% 而不是 1e-6；"
                            + "并且判据②a 的逐像素 Δd 非零，说明两档确实是两份不同的数据。");
            }
            else if (nD24 > 0 && nD32 > 0)
            {
                sb.AppendLine("　 注意：两个候选在不同列上各自命中。近列的两种预测都远小于读出地板，"
                            + "分类在那里没有分辨力、会一律倒向 D32；只有远列的判定是有信息的。"
                            + "不要把这读成「深度格式在列之间变了」。");
            }
            if (nD24 > 0 && nUnclear == 0)
            {
                sb.AppendLine("　 结论：远距离上变体 A 的距离受**深度缓冲量化**限制，"
                            + "而变体 B 不受。这是两个变体之间一条真实且不可消除的差异 —— "
                            + "它是否可见由判据②b 回答（AP 在远端已被 maxDistanceKm 钉住，"
                            + "距离差在那里不产生颜色差）。");
            }

            return ok;
        }

        /// <summary>
        /// 变体 A 的距离在 <paramref name="distM"/> 处因深度缓冲量化产生的一步有多大。
        ///
        /// 不做解析推导：直接用**这一帧真实的** GPU 投影矩阵数值求 dndc/dd，
        /// 再乘上一个 ndc ulp。解析式要假设投影是哪种形式（有限远 / 无限远 / 反转 Z），
        /// 假设错了会得到一个看起来很像的错数字。
        /// </summary>
        /// <param name="fixed24">true = 24 位定点深度；false = 32 位浮点深度。</param>
        static float DepthQuantStep(Camera cam, Matrix4x4 gpuVp, float distM, bool fixed24)
        {
            float delta = distM * 1e-3f;
            float slope = Mathf.Abs((NdcZ(cam, gpuVp, distM + delta) - NdcZ(cam, gpuVp, distM - delta))
                                    / (2f * delta));
            if (slope <= 0f) return float.PositiveInfinity;

            float ndc = NdcZ(cam, gpuVp, distM);
            float ulp = fixed24 ? Mathf.Pow(2f, -24f) : Fp32Ulp(ndc);
            return ulp / slope;
        }

        static float NdcZ(Camera cam, Matrix4x4 gpuVp, float distM)
        {
            Vector3 p = cam.transform.position + cam.transform.forward * distM;
            Vector4 clip = gpuVp * new Vector4(p.x, p.y, p.z, 1f);
            return clip.z / clip.w;
        }

        static float Fp32Ulp(float v)
        {
            v = Mathf.Abs(v);
            const float minNormal = 1.1754944e-38f;
            if (v < minNormal) return 1.4e-45f;
            int e = Mathf.FloorToInt(Mathf.Log(v, 2f));
            return Mathf.Pow(2f, e - 23);
        }

        // ────────────────────────────────────────────────────────────────
        // 判据②a：Δd
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Δd = |A 的距离 − B 的距离|。这是一条**测量 + 结构归因**，不是通过/失败判据。
        ///
        /// 为什么不给它设阈值：Δd 的期望值就是深度量化步长，而那个步长随距离变化
        /// 六个数量级。给它一个整屏阈值等于给「远端」定一个近端根本用不上的门，
        /// 或者反过来。真正要判的是**它有没有变成可见的颜色差**，那是判据②b 的事。
        /// 这里要回答的是「若②b 失败，是①还是②造成的」，以及「Δd 的分布合不合机理」。
        /// </summary>
        static void ReportDistanceDelta(StringBuilder sb, Color[] distA, Color[] distB,
                                        bool[] covered, float distFloor)
        {
            sb.AppendLine("── 判据②a（测量）：两个变体折出来的距离差 Δd");
            sb.Append("　 沿用②a-0 标定出的读出地板 = ")
              .Append(distFloor.ToString("F3")).AppendLine(" m —— 低于它的 Δd 只能报成上界");

            // 按距离分档统计。档位边界取十进制，不参与任何判定，只为读报告。
            float[] edges = { 0f, 1000f, 5000f, 15000f, 32000f, float.PositiveInfinity };
            string[] names = { "<1 km", "1~5 km", "5~15 km", "15~32 km", ">32 km" };
            var maxDd = new float[names.Length];
            var maxRel = new float[names.Length];
            var count = new int[names.Length];

            Worst worstQuad = default, worstSlant = default;

            for (int y = 0; y < k_Size; y++)
            for (int x = 0; x < k_Size; x++)
            {
                int i = x + y * k_Size;
                if (!covered[i]) continue;

                float dB = distB[i].r * 1000f;
                float dd = Mathf.Abs(distA[i].r * 1000f - dB);

                for (int b = 0; b < names.Length; b++)
                {
                    if (dB < edges[b] || dB >= edges[b + 1]) continue;
                    count[b]++;
                    maxDd[b] = Mathf.Max(maxDd[b], dd);
                    maxRel[b] = Mathf.Max(maxRel[b], dd / Mathf.Max(dB, 1f));
                    break;
                }

                int rg = Region(x, y);
                if (rg == k_RegionQuad) worstQuad.Consider(dd, x, y, dB);
                else if (rg == k_RegionSlant) worstSlant.Consider(dd, x, y, dB);
            }

            sb.AppendLine("　 距离档 | 像素数 | 最大 Δd(m) | 最大 Δd/d");
            for (int b = 0; b < names.Length; b++)
            {
                if (count[b] == 0) continue;
                sb.Append("　 ").Append(names[b])
                  .Append(" | ").Append(count[b])
                  .Append(" | ").Append(maxDd[b] <= distFloor
                        ? "≤ " + distFloor.ToString("F3") + "（地板以下，只是上界）"
                        : maxDd[b].ToString("F3"))
                  .Append(" | ").Append(maxRel[b].ToString("E2"))
                  .AppendLine();
            }

            sb.Append("　 正对面板最差：Δd = ").Append(worstQuad.value.ToString("F3"))
              .Append(" m @ (").Append(worstQuad.x).Append(',').Append(worstQuad.y)
              .Append(")　d = ").Append(worstQuad.distB.ToString("F0")).Append(" m")
              .AppendLine();
            sb.Append("　 掠射斜面最差：Δd = ").Append(worstSlant.value.ToString("F3"))
              .Append(" m @ (").Append(worstSlant.x).Append(',').Append(worstSlant.y)
              .Append(")　d = ").Append(worstSlant.distB.ToString("F0")).Append(" m")
              .AppendLine();

            if (worstQuad.value <= distFloor && worstSlant.value <= distFloor)
            {
                // 两个读数都压在地板上时，「谁大谁小」是噪声而不是结论。
                // 不点名的话，读者会拿「斜面比面板小」去推断掠射角其实更容易 ——
                // 那是从一个没有分辨力的比较里读出结论。
                sb.AppendLine("　 两者都 ≤ 读出地板 → 这个比较**没有分辨力**，"
                            + "不要从「哪边更大」里读出任何关于掠射角的结论。"
                            + "它能担保的只是：两个变体折出来的距离在整屏上的差异都在地板量级，"
                            + "也就是深度反投影这条来源在这套布景上不构成可测差异。");
            }
            else
            {
                sb.AppendLine("　 机理预期：Δd 只来自深度缓冲量化，而量化步长随 d² 增长（ndc ∝ 1/d），"
                            + "所以它应当**随距离单调增大**，且在同一距离上正对面与斜面没有差别"
                            + "（两者看到的都是自己那个像素的深度）。上面两行若在同一距离档上"
                            + "差出一个数量级，说明还有别的机制在起作用，需要先查清再看②b。");
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 判据②-0：这条判据能不能失败
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 「A 与 B 一致」这句话只有在 AP 真的改变了画面时才有内容。
        /// 若 AP 在这套布景上什么都没做，两个变体当然一致 —— 判据就是空判。
        ///
        /// 所以这里量 <c>|colB − colBase|</c>：它必须比判据②b 的容差大
        /// 至少 <see cref="k_DynamicRange"/> 倍。
        /// </summary>
        static bool JudgeNonVacuous(StringBuilder sb, Color[] colBase, Color[] colB,
                                    bool[] covered, Color[] distB)
        {
            float maxAbs = 0f, maxRatio = 0f, atDist = 0f;
            int nVisible = 0;

            for (int i = 0; i < covered.Length; i++)
            {
                if (!covered[i]) continue;
                var b = colB[i];
                var z = colBase[i];
                for (int ch = 0; ch < 3; ch++)
                {
                    float diff = Mathf.Abs(b[ch] - z[ch]);
                    float tol = Tolerance(Mathf.Max(Mathf.Abs(b[ch]), Mathf.Abs(z[ch])), 0f);
                    if (diff > tol) { nVisible++; break; }
                }

                float dAbs = MaxChannelDiff(b, z);
                float dTol = Tolerance(MaxChannel(b), 0f);
                if (dAbs > maxAbs) maxAbs = dAbs;
                if (dAbs / dTol > maxRatio) { maxRatio = dAbs / dTol; atDist = distB[i].r * 1000f; }
            }

            sb.AppendLine("── 判据②-0：AP 在这套布景上真的改变了画面（否则②b 是空判）");
            sb.Append("　 |B − 未合成基线| 最大 = ").Append(maxAbs.ToString("E3"))
              .Append("　最大「差 / 容差」倍率 = ").Append(maxRatio.ToString("F1"))
              .Append(" @ d = ").Append(atDist.ToString("F0")).Append(" m")
              .Append("　可见像素 = ").Append(nVisible)
              .AppendLine();

            if (maxRatio < k_DynamicRange)
            {
                sb.Append("　 **失败**：AP 的贡献只有判据②b 容差的 ")
                  .Append(maxRatio.ToString("F1"))
                  .Append(" 倍，不足要求的 ").Append(k_DynamicRange)
                  .AppendLine(" 倍。此时「A 与 B 一致」与「AP 什么都没做」读出同一个数，"
                            + "判据②b 通过也不说明任何事。先查太阳方向（散射太弱？）、"
                            + "AP 的 LUT 有没有产出、布景距离是不是全落在 nearDistanceKm 淡出区里。");
                return false;
            }

            sb.Append("　 动态范围 ").Append(maxRatio.ToString("F1"))
              .Append("× ≥ ").Append(k_DynamicRange)
              .AppendLine("× → ②b 有分辨力，它的「通过」是有内容的。");
            return true;
        }

        // ────────────────────────────────────────────────────────────────
        // 对照 + 判据②b：颜色
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 先量对照（同一变体渲两遍），再判 A 与 B 的颜色差。
        ///
        /// 容差 = <c>max(1% · 值, 1e-3) + 对照</c>。
        /// 加对照而不是把它吸收进一个更大的常数：对照是**实测**的，
        /// 期望值是 0；把它显式写进容差里，一旦布景哪天真的抖起来，
        /// 报告里能同时看到「容差被谁抬高了」和「抬高了多少」。
        /// </summary>
        static bool JudgeColorAgreement(StringBuilder sb, Color[] colA, Color[] colA2, Color[] colB,
                                        bool[] covered, Color[] distA, Color[] distB)
        {
            // ── 对照
            float control = 0f;
            for (int i = 0; i < covered.Length; i++)
            {
                if (!covered[i]) continue;
                control = Mathf.Max(control, MaxChannelDiff(colA[i], colA2[i]));
            }

            sb.AppendLine("── 对照：变体 A 连渲两遍，一行状态都不改");
            sb.Append("　 最大逐像素差 = ").Append(control.ToString("E3"))
              .Append("（渲染→读回加性扰动场的幅度是 ")
              .Append(VistaSelfTestNumerics.k_ReadbackFloor.ToString("E3")).Append("）")
              .AppendLine();
            if (control == 0f)
            {
                sb.AppendLine("　 读到 0 → 那个扰动场确实是像素位置的函数，在同像素两次渲染之差里抵消。"
                            + "于是下面的 A-vs-B 不必为这条地板买单。");
            }
            else
            {
                sb.AppendLine("　 不为 0 → 布景本身有帧间不确定性（或那个扰动场带值相关分量）。"
                            + "它被原样加进下面的容差里，所以判据仍然成立，只是分辨力下降了这么多。");
            }

            // ── 判据②b
            sb.AppendLine("── 判据②b：A 与 B 的颜色差 ≤ max(1%·值, 1e-3) + 对照（项目通用尺）");

            int nFail = 0;
            float maxAbs = 0f, maxOver = 0f;
            Worst worst = default;
            float qHeadroomWorst = 0f;

            for (int y = 0; y < k_Size; y++)
            for (int x = 0; x < k_Size; x++)
            {
                int i = x + y * k_Size;
                if (!covered[i]) continue;

                var a = colA[i];
                var b = colB[i];
                bool pixelFail = false;

                for (int ch = 0; ch < 3; ch++)
                {
                    float v = Mathf.Max(Mathf.Abs(a[ch]), Mathf.Abs(b[ch]));
                    float diff = Mathf.Abs(a[ch] - b[ch]);
                    float tol = Tolerance(v, control);
                    maxAbs = Mathf.Max(maxAbs, diff);
                    if (diff > tol) pixelFail = true;
                    maxOver = Mathf.Max(maxOver, diff - tol);

                    // 量化余量：差值相当于多少个 fp16 ulp。
                    float ulp = VistaSelfTestNumerics.HalfUlp(v);
                    if (ulp > 0f) qHeadroomWorst = Mathf.Max(qHeadroomWorst, diff / ulp);
                }

                if (pixelFail)
                {
                    nFail++;
                    worst.Consider(MaxChannelDiff(a, b), x, y, distB[i].r * 1000f);
                }
            }

            sb.Append("　 覆盖像素最大 |A − B| = ").Append(maxAbs.ToString("E3"))
              .Append("　超出容差最多 = ").Append(maxOver.ToString("E3"))
              .Append("　越界像素 = ").Append(nFail)
              .AppendLine();

            // ── 判据②c（测量）：差值折成 fp16 ulp
            sb.Append("　 折成 fp16 ulp：最大 ").Append(qHeadroomWorst.ToString("F2"))
              .Append(" ulp（变体 A 比 B 多经历 ").Append(k_Roundings)
              .Append(" 次舍入，所以 ≤ ").Append(k_Roundings)
              .AppendLine(" ulp 意味着两个变体只差在**表示精度**上，算术是同一份）");
            if (qHeadroomWorst <= k_Roundings)
            {
                sb.AppendLine("　 ≤ 多出的舍入次数 → 两条路在数值上不可区分。"
                            + "这是本判据能给出的最强结论；它比 Weber 1% 严 "
                            + "约 " + (k_RelTol / (k_Roundings * k_Fp16Rel)).ToString("F1") + " 倍。");
            }
            else
            {
                // 不当失败：超过 3 ulp 未必可见，判据②b 才是通过/失败的那条。
                sb.AppendLine("　 超过多出的舍入次数 → 除了表示精度还有别的差异在起作用，"
                            + "最可能是判据②a 报的那个 Δd 变成了颜色差。是否可见看②b。");
            }

            if (nFail > 0)
            {
                sb.Append("　 **失败**：").Append(nFail).Append(" 个像素超出容差，最差 ")
                  .Append(worst.value.ToString("E3"))
                  .Append(" @ (").Append(worst.x).Append(',').Append(worst.y)
                  .Append(")　d = ").Append(worst.distB.ToString("F0")).Append(" m　区域 = ")
                  .Append(RegionName(Region(worst.x, worst.y)))
                  .AppendLine();
                sb.AppendLine("　 归因顺序：先看判据②a 在这个距离档上的 Δd —— "
                            + "若 Δd 在那里已经很大，差异来自「A 走深度反投影」这一项（来源①），"
                            + "换深度格式或接受它；若 Δd 在地板附近，差异来自"
                            + "「A 多两次 fp16 舍入」（来源②），那只能靠改合成路径解决"
                            + "（例如让 A 写进 fp32 中间纹理）。");
                return false;
            }

            sb.AppendLine("　 全部覆盖像素都在容差内。");
            return true;
        }

        /// <summary>判据②b 的逐通道容差。</summary>
        static float Tolerance(float value, float control) =>
            Mathf.Max(k_RelTol * value, k_AbsTol) + control;

        // ────────────────────────────────────────────────────────────────
        // 布景
        // ────────────────────────────────────────────────────────────────

        static int ColumnX(int c) => c * k_ColumnW + k_ColumnW / 2;

        /// <summary>第 c 列面板的径向距离（m）。几何级数：每一档的相对精度问题一样大。</summary>
        static float ColumnDistanceM(int c) =>
            k_NearColumnKm * Mathf.Pow(k_FarColumnKm / k_NearColumnKm,
                                       c / (float)(k_Columns - 1)) * 1000f;

        static int Region(int x, int y)
        {
            if (y <= k_SlantRowHi) return k_RegionSlant;
            if (y >= k_EmptyRowLo && y <= k_EmptyRowHi) return k_RegionEmpty;
            if (y >= k_EmptyTopRow) return k_RegionEmpty;
            if (y >= k_QuadRowLo && y <= k_QuadRowHi)
            {
                int inCol = x % k_ColumnW;
                if (inCol >= k_QuadColLo && inCol <= k_QuadColHi) return k_RegionQuad;
            }
            return k_RegionEdge;
        }

        static string RegionName(int r) =>
            r == k_RegionQuad ? "正对面板"
          : r == k_RegionSlant ? "掠射斜面"
          : r == k_RegionEmpty ? "空缝"
          : "面板边缘/列间";

        static int FindUnusedLayer()
        {
            int used = 0;
            foreach (var r in Object.FindObjectsByType<Renderer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                used |= 1 << r.gameObject.layer;
            foreach (var l in Object.FindObjectsByType<Light>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                used |= 1 << l.gameObject.layer;

            for (int i = 31; i >= 0; i--)
                if ((used & (1 << i)) == 0) return i;
            return -1;
        }

        static void Build(Shader litShader, int layer, float groundLevelWorldY, RenderTexture rt,
                          out GameObject root, out Camera cam, out Material mat, out Transform[] quads)
        {
            root = new GameObject("Vista AP Variant Agreement Probe") { hideFlags = HideFlags.HideAndDontSave };
            root.transform.position = new Vector3(0f, groundLevelWorldY + k_CameraAltitudeM, 0f);

            // ── 相机
            var camGo = new GameObject("Camera") { hideFlags = HideFlags.HideAndDontSave };
            camGo.transform.SetParent(root.transform, false);
            camGo.layer = layer;
            cam = camGo.AddComponent<Camera>();

            cam.enabled = false;
            cam.cullingMask = 1 << layer;
            cam.orthographic = false;
            cam.fieldOfView = k_Fov;
            cam.nearClipPlane = k_NearClip;
            cam.farClipPlane = k_FarClip;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.allowHDR = true;
            cam.allowMSAA = false;
            // 水平朝 +Z：贴着地平线的视线路径最长、AP 信号最强，同时让斜面刚好填满下半屏。
            camGo.transform.localRotation = Quaternion.identity;

            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = false;
            camData.volumeLayerMask = 0;
            camData.antialiasing = AntialiasingMode.None;
            // 阴影关掉：阴影边界会在面板内部产生与 AP 无关的颜色梯度。
            camData.renderShadows = false;

            SetTarget(cam, rt, k_ColorSentinel);

            // ── 自带太阳，挂在探针 layer 上（场景那盏不在 cullingMask 里，会被剔掉）。
            //    于是 URP 的主光就是这一盏，也完全不必动全局 RenderSettings.sun。
            var lightGo = new GameObject("Sun") { hideFlags = HideFlags.HideAndDontSave };
            lightGo.transform.SetParent(root.transform, false);
            lightGo.layer = layer;
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;
            light.intensity = 1f;
            // 与 AP 数值验收用同一组角度：侧逆光远景，Mie 峰值之外但散射够强。
            lightGo.transform.localRotation = Quaternion.Euler(25f, 150f, 0f);

            // ── 材质：一份，所有几何共用。
            //
            // 共用一份是判据成立的一部分：面板与斜面若用两个材质，
            // 「Δd 在正对面与斜面上是否一致」这条归因就多了一个变量。
            mat = new Material(litShader) { hideFlags = HideFlags.HideAndDontSave };
            // _BaseColor 是 Color 属性，往它写值会被做一次 gamma → linear
            // （无论 SetColor 还是 SetVector），所以预先逆变换一次。
            var encoded = new Color(k_BaseLevel, k_BaseLevel, k_BaseLevel, 1f).gamma;
            mat.SetVector("_BaseColor", new Vector4(encoded.r, encoded.g, encoded.b, 1f));
            mat.SetFloat("_Cull", 0f);                 // 双面：省掉朝向这个变量
            mat.SetFloat("_Smoothness", 0f);
            mat.SetFloat("_Metallic", 0f);
            // 高光与环境反射是**视线相关**的：同一块面板内不同像素的值会变，
            // 那会在 A-vs-B 的颜色差里叠进一个与 AP 无关的项。关掉它们。
            // SetFloat 只改 uniform，分支是关键字决定的 —— 两者都要动，否则等于没关。
            mat.SetFloat("_SpecularHighlights", 0f);
            mat.SetFloat("_EnvironmentReflections", 0f);
            mat.SetFloat("_ReceiveShadows", 0f);
            mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            mat.EnableKeyword("_RECEIVE_SHADOWS_OFF");

            // ── 上半屏：8 块正对相机的面板，各占一列。
            Vector3 eye = cam.transform.position;
            quads = new Transform[k_Columns];
            for (int c = 0; c < k_Columns; c++)
            {
                float d = ColumnDistanceM(c);
                float halfH = d * Mathf.Tan(k_Fov * 0.5f * Mathf.Deg2Rad);
                float colW = 2f * halfH / k_Columns;

                float u = (ColumnX(c) + 0.5f) / k_Size;
                float v = (k_QuadRowY + 0.5f) / k_Size;

                // 不用 ViewportPointToRay：它的 origin 在近裁剪面上，
                // 那样 origin + dir·d 得到的是「距近裁剪面 d」。这里直接从相机位置量，
                // 于是面板中心的径向距离**精确等于** d —— 判据②a-0 的已知量靠这一点成立。
                Vector3 dir = (cam.ViewportToWorldPoint(new Vector3(u, v, 1f)) - eye).normalized;

                var t = MakeQuad(root.transform, layer, mat, "Panel " + c);
                t.position = eye + dir * d;
                t.rotation = cam.transform.rotation;   // 正对相机
                t.localScale = new Vector3(colW * k_QuadWidthFrac, 2f * halfH * k_QuadBandFrac, 1f);
                quads[c] = t;
            }

            // ── 下半屏：水平地面，深度反投影最难的情形。
            //
            // 放在相机下方 k_SlantDropM 处，于是它恰好填满地平线（v = 0.5）以下那半屏：
            // 命中距离 = drop / |射线的向下分量|，从底边的 1.7 km 一直拉到贴着
            // 地平线的 44 km。尺寸给足富余（横向 6 km > 44 km 处的视锥半宽 0.77 km，
            // 纵向 120 km > 远裁剪面），到底盖住没有由哨兵回答，不靠算得刚好。
            var slant = MakeQuad(root.transform, layer, mat, "Slant");
            slant.rotation = Quaternion.Euler(-90f, 0f, 0f);   // 法线朝上（+Y）
            slant.position = eye + new Vector3(0f, -k_SlantDropM, k_FarClip * 0.5f);
            slant.localScale = new Vector3(6000f, 120000f, 1f);
        }

        static Transform MakeQuad(Transform parent, int layer, Material mat, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.hideFlags = HideFlags.HideAndDontSave;
            go.layer = layer;
            go.transform.SetParent(parent, false);

            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            // 探针一律不接：它们会在几何之间引入不同的间接光，
            // 而那与 AP 无关，只会让颜色差多一个来源。
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return go.transform;
        }

        // ────────────────────────────────────────────────────────────────
        // 采集
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 换目标纹理并设好这一档的哨兵清屏色。
        /// <c>cam.aspect</c> 必须在 <c>targetTexture</c> **之后**设 ——
        /// 赋目标纹理会让 Unity 按它的尺寸重算 aspect。
        /// </summary>
        static void SetTarget(Camera cam, RenderTexture rt, float sentinel)
        {
            cam.targetTexture = rt;
            cam.backgroundColor = new Color(sentinel, sentinel, sentinel, 1f);
            cam.aspect = 1f;
        }

        /// <summary>改了 compositeMode / 关键字之后空转几帧：变体编译与 LUT 下一帧才稳定。</summary>
        static void Warmup(Camera cam, int frames)
        {
            for (int i = 0; i < frames; i++)
                cam.Render();
        }

        static Color[] RenderAndRead(Camera cam, RenderTexture rt, Texture2D readback)
        {
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
            readback.Apply(false);
            RenderTexture.active = prev;
            return readback.GetPixels();
        }

        /// <summary>
        /// 这个读数是「有几何」还是哨兵。
        ///
        /// 覆盖的条件写成 <c>0 ≤ v &lt; gate</c> 这种**双侧闭合**的形式，而不是
        /// 「不等于哨兵」或「小于门限」：NaN 与 ±inf 都要算作未覆盖，
        /// 而 NaN 参与的任何比较都是 false —— 双侧写法天然把它排除，
        /// 单侧的 <c>v &lt; gate</c> 也能排 NaN，但排不掉 −inf。
        /// 后者不是假想：若某一档的清屏色经 gamma 变换后溢出，
        /// 读回来的可能是 ±inf 而不是一个大正数。
        /// </summary>
        static bool IsCovered(float v, float gate) => v >= 0f && v < gate;

        // ────────────────────────────────────────────────────────────────
        // 小工具
        // ────────────────────────────────────────────────────────────────

        struct Worst
        {
            public float value;
            public int x, y;
            public float distB;

            public void Consider(float v, int px, int py, float d)
            {
                if (v <= value) return;
                value = v; x = px; y = py; distB = d;
            }
        }

        static float MaxChannel(Color c) =>
            Mathf.Max(Mathf.Abs(c.r), Mathf.Max(Mathf.Abs(c.g), Mathf.Abs(c.b)));

        static float MaxChannelDiff(Color a, Color b) =>
            Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
    }
}
