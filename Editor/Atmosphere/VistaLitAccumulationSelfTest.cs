using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista.Editor
{
    /// <summary>
    /// Vista/Lit 前向累加与 URP <c>UniversalFragmentPBR</c> 的**等价性**自检。
    ///
    /// 这条自检是 <c>ShaderLibrary/VistaLighting.hlsl</c> 里那份拷贝的**存在前提**：
    /// 抄一份 URP 的函数体在这个项目里本来是不该做的事，唯一能让它可接受的
    /// 条件是「走歧会被自动抓到」。没有这条自检就不该抄。
    ///
    /// ── 为什么它与大气无关 ──
    ///
    /// VISTA_LIT_DIFF_DEBUG 变体在**上 AP 之前**取差：量的是「拆分累加是否等价」，
    /// 与 AP 表、太阳方向、compositeMode 全部无关。所以这条自检不需要
    /// VistaAtmosphereFeature 在场 —— 它是一条纯粹的回归判据，URP 升级时第一个该跑的就是它。
    /// 变体 A/B 的逐像素一致（#15 判据②）是另一件事，在另一个自检里。
    ///
    /// 但「与大气无关」不是自动成立的，要靠一条显式措施维持：#12 的逐像素太阳透射率
    /// 就长在 VistaComputeLighting 里面，**只乘我这一侧**。它一生效，两侧就天然差
    /// mainLightColor·(ratio−1)，与拷贝抄得对不对无关。所以本自检在
    /// <see cref="Run"/> 里把它强制置成不生效（<see cref="k_TRefHeldOff"/>），
    /// 并在判据 1 的报告里点名 —— 见那里的说明：一条被强制关掉的东西若不点名，
    /// 「累加 ≡ UniversalFragmentPBR」会被读成「#12 不改变光」，恰好是反的。
    ///
    /// ── 「恒 0」的三种解释，和怎么区分 ──
    ///
    /// 通过的样子是最大相对误差 0。但 0 有三种来源：
    ///   a) 累加确实等价（想要的）；
    ///   b) 编译器把两条表达式折叠成了一条 —— 这其实是**最强的通过**，
    ///      编译器只有在证明等价时才敢折叠；
    ///   c) **根本没渲到被测像素** —— 假通过。
    ///
    /// c) 用两道措施排除：
    ///   · 清屏色设成 shader 里那个哨兵值（30000）。没被 Vista/Lit 画到的像素
    ///     不是 0 而是一个巨大值，绝不可能被读成「一致」。
    ///   · 逐项故障注入 <c>_VistaDiffInject</c>：分四次给
    ///     mainLight / additionalLights / gi / vertexLighting 各注入 2% 偏差，
    ///     要求自检把它报出来。报不出来的那一项，说明它在这一帧根本没参与 ——
    ///     于是它被**明确列为未覆盖**，而不是混在「通过」里。
    ///
    /// ── 覆盖范围是量出来的，不是声称的 ──
    ///
    /// 拷贝的风险全在宏分支上：URP 的函数体里每个 <c>#if</c> 都是一条我可能抄漏
    /// 的路。所以这里跑一组**命名配置**，每个配置单独报数，并把动不了的关键字
    /// （由 URP asset / 烘焙数据决定的那些）明确写成未覆盖。
    /// 一个"全部通过"若不附覆盖清单，等于没说。
    /// </summary>
    public static class VistaLitAccumulationSelfTest
    {
        // ── 布景常量 ──

        const int k_Size = 128;
        const float k_Fov = 60f;
        const float k_NearClip = 0.3f;
        const float k_FarClip = 200f;
        const float k_BackdropZ = 40f;
        const float k_SphereZ = 9f;

        // ── 阈值 ──

        /// <summary>Weber 1%：全项目通用的可见性门槛。</summary>
        const float k_RelTol = 0.01f;

        /// <summary>fp16 相对精度 2^-11。共享量，见 <see cref="VistaSelfTestNumerics"/>。</summary>
        const float k_Fp16Rel = VistaSelfTestNumerics.k_Fp16Rel;

        /// <summary>
        /// 与 shader 里 VISTA_DIFF_NOT_COMPARED 对应的判定门。
        /// shader 写 30000，这里用 1e4 收：0 档的读数已经是 relError × 100，
        /// 即使 100% 的偏差也只有 100，离 1e4 还有两个数量级 ——
        /// 于是「哨兵」与「一个很大的真实偏差」不会撞车。
        /// 上一版门取 10、shader 写 100，那是 relError 未放大时定的；
        /// 放大后不改这两个数，一次 10% 的真实不一致就会被读成「该像素未参与比对」，
        /// 方向恰好是最危险的那一侧（真实失败被读成不适用）。
        /// </summary>
        const float k_SentinelGate = 1e4f;

        /// <summary>注入量。高于 Weber 1%（必须被判失败），又远高于 fp16 地板。</summary>
        const float k_Inject = 0.02f;

        /// <summary>
        /// 与 shader 里 VISTA_DIFF_DENOM_FLOOR 对应。两处必须一致，否则 1b 的归因
        /// 会拿一个错的下限去判断「相对误差是不是下限撑出来的」。
        /// </summary>
        const float VISTA_DIFF_DENOM_FLOOR = 1e-3f;

        /// <summary>
        /// 与 shader 里 VISTA_DIFF_REL_SCALE 对应。0 档把相对误差放大这么多倍再写出，
        /// CPU 侧除回来。
        ///
        /// 为什么必须放大：读回路径有一条**加性**扰动场，实测幅度 ±1/1024
        /// （见 <see cref="k_ReadbackFloor"/>）。
        /// relError 的期望值是 0，于是不放大的话读回来永远是那条地板 ~6.6e-4 ——
        /// 而它与 fp16 相对精度 4.88e-4 量级几乎相同，**尺子的地板伪装成了被测量的性质**，
        /// 判据 1 曾连续多轮报出这个数、八个配置一模一样。
        /// 放大 100 倍后地板折算回相对误差是 1e-5，比 Weber 1% 低三个数量级。
        /// </summary>
        const float k_RelScale = 100f;

        /// <summary>
        /// 与 shader 里 VISTA_DIFF_NUM_SCALE 对应。3 档把**分子**放大这么多倍再写出，
        /// CPU 侧除回来。取 1e6 而不是 2 的幂：与 0 档的量级刻意错开，
        /// 免得「档位其实没切换、读到的还是 0 档」这种失效碰巧算出一个自洽的数。
        /// 上一版取 1024 就吃过这个亏 —— 分母恰好落在 1/1024 附近时两档读数相等，
        /// 于是「没切档」与「归因成立」在数值上不可分辨。
        /// </summary>
        const float k_NumScale = 1e6f;

        /// <summary>
        /// 与 shader 里 VISTA_DIFF_NUM_BIAS 对应。3 档在放大后的分子上再加这个偏置。
        ///
        /// 为什么需要它：**读数接近 0 的档位无法自证自己执行过**。上一版 3 档写
        /// num×1e6（真实情形下 ≈ 0），5 档写死 0，两者读回一个小数时，
        /// 「else 分支没进」与「值本身就很小」在数值上完全不可分辨 ——
        /// 归因链在那里断掉，而我却先去怀疑读回路径。加了偏置，读数 &lt; 偏置一半
        /// 就直接判成「这一档没执行」，不再去解释那个数。
        ///
        /// 为什么取 0.25 而不是更大：偏置的 half ulp 直接决定分子的量化步长
        /// （见 <see cref="NumFloor"/>）。上一版取 8，ulp 7.8e-3 → 分子只能分辨到
        /// 7.8e-9，而读回路径那 2 个 ulp 的亏损换算成分子是 −1.6e-8，
        /// 于是解码出一个**负的绝对差** —— 不可能的数，只说明分辨力不够。
        /// 0.25 的 ulp 是 2.44e-4 → 量化步长降到 2.4e-10（此时真正的地板变成读回扰动
        /// 9.8e-10，见 <see cref="k_ReadbackFloor"/>），同时 0.25 仍远高于那条
        /// 1e-3 的加性地板，「有没有执行」这个判断不受影响。
        /// </summary>
        const float k_NumBias = 0.25f;

        /// <summary>
        /// 3 档能分辨的最小分子。取两条地板的大者，再除以放大倍数：
        /// ① 偏置自己的 half ulp（量化步长）；
        /// ② <see cref="k_ReadbackFloor"/>，渲染→读回路径那条实测的加性扰动。
        ///
        /// 只看 ① 会低估：0.25 的 ulp 是 2.44e-4，而读回扰动是 9.77e-4，
        /// 后者大四倍 —— 若按 ① 报数，一个纯粹由扰动造成的读数会被当成
        /// 「量到了一个真实的分子」，而它甚至可能是负的。
        ///
        /// 读回的分子低于这个数时只能说「不超过它」，不能说「等于它」——
        /// 报告里必须写成上界，否则等于把尺子的地板当成被测量的值。
        /// </summary>
        static float NumFloor => Mathf.Max(HalfUlp(k_NumBias), k_ReadbackFloor) / k_NumScale;

        /// <summary>
        /// 渲染→读回路径的加性扰动幅度，实测值 = 1/1024。
        ///
        /// **这条常量与它的完整来龙去脉搬到了 <see cref="VistaSelfTestNumerics"/>** ——
        /// #15 判据②也要用它，而「同一个量两份实现」在本项目里是禁止的：
        /// 两份之中只更新了一份的症状是「某一条判据的门限比另一条松」，
        /// 那是最难被发现的一类偏差。这里只留一个别名，
        /// 下面所有引用（包括那条「地板变大就失败」的交叉校验）都不用改。
        /// </summary>
        const float k_ReadbackFloor = VistaSelfTestNumerics.k_ReadbackFloor;

        /// <summary>
        /// 与 shader 里 5 档写出的常量对应。三个通道取三个**互不相同**的值：
        /// 单通道常量只能验证「写进去了」，三通道互异还能验证通道没有错位
        /// （rgb 被 blit 换序、被当成 bgra 读，都会在这里现形）。
        /// 0.25/0.5/0.75 在 half 里精确可表示，所以读回应当逐位相同、
        /// 容差可以取 0 而不需要编一个门限出来。
        /// </summary>
        static readonly Color k_ModeConst = new Color(0.25f, 0.5f, 0.75f, 1f);

        /// <summary>
        /// 归因用的 CPU 预清屏值。必须同时区别于 5 档常量的三个值与哨兵的 30000，
        /// 否则那次读数无法区分「像素没被碰过」和另外两种情形。取 3 没有别的讲究，
        /// 只要互不相同、且都在 half 的规格化区内即可。
        /// </summary>
        const float k_PreClear = 3f;

        static float Channel(Color c, int i) => i == 0 ? c.r : (i == 1 ? c.g : c.b);

        /// <summary>
        /// 本自检期间强制下发的 <c>_VistaSunTransmittanceRef</c>：w = 0 ⇒ #12 的比值恒为 1。
        ///
        /// 为什么必须强制、而不是「场景里没挂 ToD 所以自然是 1」：那是运气，不是保障。
        /// 布景是运行时搭的，但 <see cref="VistaTimeOfDay"/> 是**场景级**组件 ——
        /// 在一个挂了它的场景里跑本自检，w 就是 1，于是判据 1 会把 #12 报成
        /// 「拷贝走歧」。反过来更坏：现在它「通过」也可能只是因为当前场景恰好没挂 ToD，
        /// 而报告里看不出这个前提。用钩子钉住，两种情形都消失。
        ///
        /// 三个 xyz 给 1 只是为了让这个值本身自洽（比值 = T/1）；w = 0 时 shader
        /// 第一行就返回 1，xyz 根本不会被读到。
        /// </summary>
        static readonly Vector4 k_TRefHeldOff = new Vector4(1f, 1f, 1f, 0f);

        /// <summary>
        /// 5 档能与它混淆的**候选值**：另外两个通道的常量、CPU 预清屏值、以及 0
        /// （残留/未写）。哨兵不在其中 —— 它由 k_SentinelGate 单独挡掉。
        ///
        /// 这个集合的用处见 <see cref="ClassifyConst"/>：判据不需要发明容差，
        /// 只需要问「读回的值离它自己那个常量最近，还是离某个别的候选更近」。
        /// </summary>
        static readonly float[] k_ConstCandidates = { 0.25f, 0.5f, 0.75f, k_PreClear, 0f };

        /// <summary>
        /// 5 档（已知常量）的判定 + 地板测量。两件事必须分开报：
        ///
        /// ① <paramref name="misclassified"/> —— **硬判据**，不含任何发明出来的容差。
        ///    对每个通道问：读回的值离它应有的常量最近，还是离
        ///    <see cref="k_ConstCandidates"/> 里某个别的候选更近？离别人更近就算误判。
        ///    候选之间的最小间距是 0.25（0.5↔0.75），也就是说这条判据的判别余量是
        ///    0.125 —— 是实测读回地板（<see cref="k_ReadbackFloor"/> = 9.8e-4）的 128 倍。
        ///    它能抓的正是「读到的其实是别的东西」：通道错位（rgb 换序会让 r 通道读到
        ///    0.5 或 0.75）、残留（0）、预清屏值活到最后（3）、没写（0）。
        ///
        /// ② <paramref name="maxDev"/> —— **测量**，不是判据：读回值与精确常量之差的
        ///    全图最大值。这就是读回路径那条加性地板的幅度，判据 1 的数字要靠它来
        ///    折算成上界。调用方把它与 k_ReadbackFloor 比：超了说明地板比记录的更大，
        ///    判据 1 报的「上界」也就被低估了 —— 那才是这一档该让自检失败的情形。
        ///
        /// 为什么不能像上一版那样把容差取 0 当硬判据：容差 0 隐含「写入→读回无损」
        /// 这个**已被实测推翻**的前提（0.25/0.5/0.75 都是 half 精确值，却逐像素低
        /// 1~2 个 ulp）。前提错了，这条守卫就只会恒定报警，等于没有守卫。
        /// 但也不能就手把容差放宽到地板 —— 那是拿被测现象给自己的门限背书。
        /// 换成①的最近邻分类，门限由**候选之间的间距**决定，与地板幅度无关。
        ///
        /// <paramref name="interiorDeviating"/> 量的是偏离（不是误判）的分布形状：
        /// 局限在最外一圈说明来源是边界（边缘取样、blit 的 scaleBias、光栅化规则），
        /// 排除那一圈就是干净的测量；若内部也有，那是全图性的，排除边界只会把它藏起来。
        /// </summary>
        static void ClassifyConst(Color[] px, int size, Color expect,
                                  out int misclassified, out int deviating,
                                  out int interiorDeviating, out float maxDev)
        {
            misclassified = 0;
            deviating = 0;
            interiorDeviating = 0;
            maxDev = 0f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var c = px[y * size + x];
                if (Mathf.Max(c.r, Mathf.Max(c.g, c.b)) >= k_SentinelGate) continue;

                bool bad = false;
                float d = 0f;
                for (int ch = 0; ch < 3; ch++)
                {
                    float v = Channel(c, ch);
                    float want = Channel(expect, ch);
                    if (float.IsNaN(v)) { bad = true; break; }
                    float dch = Mathf.Abs(v - want);
                    if (dch > d) d = dch;
                    foreach (float alt in k_ConstCandidates)
                    {
                        if (alt == want) continue;
                        if (Mathf.Abs(v - alt) <= dch) { bad = true; break; }
                    }
                    if (bad) break;
                }
                if (bad) misclassified++;
                if (!(d > 0f)) continue;   // 这样写也能挡住 NaN
                deviating++;
                if (d > maxDev) maxDev = d;
                if (x == 0 || y == 0 || x == size - 1 || y == size - 1) continue;
                interiorDeviating++;
            }
        }

        /// <summary>
        /// 几个固定位置的原始 rgb。归因时「一个常数」与「一片有结构的场」处置完全
        /// 不同（前者查写入/读回路径，后者说明读到的其实是别的档位的输出），
        /// 而聚合量（最大值、计数）把这个区别抹掉了，所以直接把原始数打出来。
        /// </summary>
        static string SamplePixels(Color[] px, int size)
        {
            var sb = new StringBuilder();
            int[] xs = { 0, 10, size / 2, size - 28, size - 1 };
            int[] ys = { 0, 10, size / 2, 42, size - 1 };
            for (int i = 0; i < xs.Length; i++)
            {
                var c = px[ys[i] * size + xs[i]];
                sb.Append('(').Append(xs[i]).Append(',').Append(ys[i]).Append(")=")
                  .Append(c.r.ToString("E3")).Append('/').Append(c.g.ToString("E3"))
                  .Append('/').Append(c.b.ToString("E3")).Append("　");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 有多少像素的最大通道达到了 threshold。用来区分「一个孤立坏点」与
        /// 「一整片系统性偏差」—— 两者的归因方向完全不同。
        ///
        /// threshold 的单位是**读回来的原始通道值**，不是相对误差：0 档写出的是
        /// relError × <see cref="k_RelScale"/>，调用方要自己乘回去。
        /// </summary>
        static int CountAtLeast(Color[] px, float threshold)
        {
            int n = 0;
            foreach (var c in px)
            {
                float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                if (m < k_SentinelGate && m >= threshold) n++;
            }
            return n;
        }

        struct Config
        {
            public string name;
            public string[] keywords;
            public bool mainLightShadows;
            public bool additionalLights;
            public float baseAlpha;
        }

        static readonly Config[] k_Configs =
        {
            new Config { name = "① 不透明 / 无阴影 / 无附加光", keywords = new string[0], baseAlpha = 1f },
            new Config { name = "② + 主光软阴影", keywords = new string[0], mainLightShadows = true, baseAlpha = 1f },
            new Config { name = "③ + 两盏点光（带阴影）", keywords = new string[0], mainLightShadows = true, additionalLights = true, baseAlpha = 1f },
            // 贴图特性全开。没有对应贴图时采到的是 white/bump/black 默认值，
            // 分支照样成立 —— 判的是控制流有没有抄漏，不是贴图内容。
            new Config { name = "④ 贴图特性全开（法线/自发光/金属光滑/AO/细节/视差）",
                         keywords = new[] { "_NORMALMAP", "_EMISSION", "_METALLICSPECGLOSSMAP",
                                            "_OCCLUSIONMAP", "_DETAIL_MULX2", "_PARALLAXMAP" },
                         mainLightShadows = true, additionalLights = true, baseAlpha = 1f },
            // 这一条专门覆盖拷贝里那个 specularHighlightsOff 分支。
            new Config { name = "⑤ 高光关 + 环境反射关（覆盖 specularHighlightsOff 分支）",
                         keywords = new[] { "_SPECULARHIGHLIGHTS_OFF", "_ENVIRONMENTREFLECTIONS_OFF" },
                         mainLightShadows = true, additionalLights = true, baseAlpha = 1f },
            new Config { name = "⑥ Specular 工作流",
                         keywords = new[] { "_SPECULAR_SETUP" },
                         mainLightShadows = true, baseAlpha = 1f },
            new Config { name = "⑦ AlphaTest（cutoff = 0，不裁掉任何像素）",
                         keywords = new[] { "_ALPHATEST_ON" },
                         mainLightShadows = true, baseAlpha = 1f },
            // 覆盖 InitializeBRDFData 里 diffuse *= alpha 那一支 ——
            // 也就是「surfaceData 该按值收还是 inout」这个决定的唯一分歧点。
            // baseAlpha 取 0.6 而不是 1：alpha = 1 时预乘是恒等变换，
            // 分支"覆盖"了但什么也没发生，等于空判。
            new Config { name = "⑧ 透明 + 预乘 alpha（覆盖 diffuse *= alpha）",
                         keywords = new[] { "_SURFACE_TYPE_TRANSPARENT", "_ALPHAPREMULTIPLY_ON" },
                         mainLightShadows = true, baseAlpha = 0.6f },
        };

        [MenuItem("Window/Vista/Validate Vista Lit Accumulation", priority = 129)]
        public static void Run()
        {
            var sb = new StringBuilder();
            bool ok;

            // #12 在整条自检期间强制置成不生效，理由见 k_TRefHeldOff。
            // 范围是**整条**而不只是判据 1：判据 2 的注入也跑在同一套布景上，
            // 而 #12 只乘在我这一侧 —— 它一生效，四项注入都会带上一个共同的基线偏差。
            var prevTRef = VistaTimeOfDay.s_DebugTRefOverride;
            VistaTimeOfDay.s_DebugTRefOverride = k_TRefHeldOff;
            try
            {
                ok = Validate(sb);
            }
            finally
            {
                VistaTimeOfDay.s_DebugTRefOverride = prevTRef;
                Shader.SetGlobalVector(s_InjectId, Vector4.zero);
                Shader.SetGlobalVector(s_CtrlId, Vector4.zero);
                EditorUtility.ClearProgressBar();
            }

            Debug.Log(("[Vista] Vista/Lit 累加等价性自检" + (ok ? "通过" : "**失败**") + "\n" + sb)
                      .Replace("\r", "").Replace("\n", "  |  "));
        }

        static readonly int s_InjectId = Shader.PropertyToID("_VistaDiffInject");
        static readonly int s_CtrlId = Shader.PropertyToID("_VistaDiffCtrl");

        static bool Validate(StringBuilder sb)
        {
            // ── 前置：shader 本身
            var shader = Shader.Find("Vista/Lit");
            if (shader == null)
            {
                sb.AppendLine("**失败**：Shader.Find(「Vista/Lit」) 为 null。"
                            + "包没被识别到（检查 Packages/manifest.json 里的 com.kkiej.vista），"
                            + "或 .shader 的名字串写错了。");
                return false;
            }

            int msgCount = ShaderUtil.GetShaderMessageCount(shader);
            bool hasError = ShaderUtil.ShaderHasError(shader);
            sb.Append("── Shader　Vista/Lit 已解析　编译消息 ").Append(msgCount).Append(" 条")
              .Append(hasError ? "　**含错误**" : "　无错误").AppendLine();
            if (hasError)
            {
                sb.AppendLine("**失败**：Vista/Lit 有编译错误。消息正文在 Console 里（这里只报计数，"
                            + "不重复一遍）。此时后面所有判据都不成立。");
                return false;
            }

            int layer = FindUnusedLayer();
            if (layer < 0)
            {
                sb.AppendLine("**失败**：32 个 layer 全都有物体在用，布景无法与场景隔离。");
                return false;
            }

            RenderTexture rt = null;
            Texture2D readback = null;
            GameObject root = null;
            Material mat = null;
            // 本自检要临时接管 URP 的主光。理由不是整洁，是**可复现性**：
            // 不接管的话主光是「当前打开的那个场景里的太阳」，于是这条自检的读数
            // 随场景变化，而报告里看不出这个前提。实测（10/11 归因档）在接管前
            // 主光方向是 (-0.6538, 0.7539, -0.0444)、色是 (0.8926, 0.8018, 0.6748)
            // —— 布景那盏是纯白、方向 (-0.2531, 0.5736, -0.7791)，两者毫无关系。
            var prevSun = RenderSettings.sun;

            try
            {
                rt = new RenderTexture(k_Size, k_Size, 24,
                                       RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                readback = new Texture2D(k_Size, k_Size, TextureFormat.RGBAFloat, false, true);

                Build(shader, layer, rt, out root, out Camera cam, out var camData,
                      out mat, out Light[] pointLights, out Light sunLight);
                RenderSettings.sun = sunLight;

                sb.Append("── 布景　layer = ").Append(layer)
                  .Append("　RT = ").Append(k_Size).Append('×').Append(k_Size).Append(" ARGBHalf")
                  .Append("　清屏 = 哨兵 30000（未画到的像素不可能被读成一致）")
                  .Append("　0 档在 shader 里 ×").Append(k_RelScale.ToString("F0"))
                  .Append(" 后写出，读回再除掉：渲染→读回路径有一条 ±1/1024 的**加性**地板，"
                        + "不放大的话期望值为 0 的量会被顶到 ~6.6e-4，"
                        + "而那个量级恰好与 fp16 相对精度相同，尺子会替被测对象伪造一个结论")
                  .AppendLine();
                sb.Append("　 判据：最大相对误差 ≤ Weber 1%，且哨兵像素数 = 0。")
                  .Append("放大后地板折算回相对误差 ")
                  .Append((k_ReadbackFloor / k_RelScale).ToString("E3"))
                  .Append("，fp16 相对精度 ").Append(k_Fp16Rel.ToString("E3"))
                  .AppendLine("　（拆分只改控制流、不改算术，所以期望值是 0，"
                            + "而不是「落在精度内」—— 若量到 fp16 地板量级的非零，"
                            + "说明两侧的求值顺序被编译器排得不同，那本身值得查）");

                // ── 归因（不参与判据）：URP 这一帧实际选中的主光是哪一盏。
                //
                // 为什么这一行必须存在：Build 里那句「太阳从相机后上方打过来，
                // 好让球体的影子落在背板上」是一条**覆盖性断言** —— 它声称阴影分支
                // 被覆盖了。而这条断言成立的前提是「布景那盏灯就是主光」，
                // 那个前提靠 layer 隔离**保证不了**（理由见 Build 里的注释）。
                // 断言若不成立，症状不是失败而是「判据 1 照样全绿，只是绿在一条
                // 没有阴影的路径上」—— 一次覆盖范围的静默缩小，最难发现的那一类。
                //
                // 读的是 shader 里的 10/11 档而不是 C# 侧的 Shader.GetGlobalVector：
                // 那两处读的不是同一个东西（前者是「本次渲染着色器看到了什么」，
                // 后者是「现在 CPU 全局表里存着什么」）。
                Apply(k_Configs[0], mat, camData, pointLights);
                Shader.SetGlobalVector(s_CtrlId, new Vector4(10f, 0f, 0f, 0f));
                Warmup(cam);
                var mainDirRaw = RenderAndRead(cam, rt, readback)[k_Size / 2 * k_Size + k_Size / 2];
                Shader.SetGlobalVector(s_CtrlId, new Vector4(11f, 0f, 0f, 0f));
                Warmup(cam);
                var mainColRaw = RenderAndRead(cam, rt, readback)[k_Size / 2 * k_Size + k_Size / 2];
                Shader.SetGlobalVector(s_CtrlId, Vector4.zero);

                // 10 档在 shader 里做了 *0.5+0.5 偏置（负分量不能与「未覆盖」混淆）。
                var mainDir = new Vector3(mainDirRaw.r * 2f - 1f,
                                          mainDirRaw.g * 2f - 1f,
                                          mainDirRaw.b * 2f - 1f);
                Vector3 rigDir = -sunLight.transform.forward;
                float dirGap = Mathf.Max(Mathf.Abs(mainDir.x - rigDir.x),
                                Mathf.Max(Mathf.Abs(mainDir.y - rigDir.y),
                                          Mathf.Abs(mainDir.z - rigDir.z)));
                sb.Append("── 归因　URP 主光　_MainLightPosition = (")
                  .Append(mainDir.x.ToString("F4")).Append(", ")
                  .Append(mainDir.y.ToString("F4")).Append(", ")
                  .Append(mainDir.z.ToString("F4")).Append(")　布景那盏 = (")
                  .Append(rigDir.x.ToString("F4")).Append(", ")
                  .Append(rigDir.y.ToString("F4")).Append(", ")
                  .Append(rigDir.z.ToString("F4")).Append(")　最大分量差 = ")
                  .Append(dirGap.ToString("E3"))
                  .Append("　_MainLightColor = (")
                  .Append(mainColRaw.r.ToString("F4")).Append(", ")
                  .Append(mainColRaw.g.ToString("F4")).Append(", ")
                  .Append(mainColRaw.b.ToString("F4")).Append(')')
                  .AppendLine();
                sb.Append("　 怎么读：分量差远大于 ")
                  .Append((2f * k_ReadbackFloor).ToString("E3"))
                  .Append("（10 档解码把读回地板放大 2 倍）说明**接管没生效** —— "
                        + "URP 选中的不是布景那盏，于是 Build 里「影子落在背板上」这条"
                        + "覆盖性断言不成立，且读数会随当前打开的场景变化；"
                        + "主光色若接近 0，则任何「乘在主光上」的判据都乘在零上，是空判。")
                  .AppendLine();

                // ── 覆盖性**测量**（不参与判据）：主光阴影到底有没有让某些像素变暗。
                //
                // 为什么要量而不是断言：配置 ①/② 的关键字表都是空的，两者唯一的差别是
                // camData.renderShadows。所以「② 覆盖了阴影分支」这句话有两层含义，
                // 而它们的强度差很远 ——
                //   · 弱：_MAIN_LIGHT_SHADOWS 编进去了、那段代码跑了；
                //   · 强：它算出的 shadowAttenuation 真的 < 1，即真有像素落在阴影里。
                // 只有强的那层才让「阴影分支被比对过」有意义：若一个像素都没被遮住，
                // 那段代码每次都返回 1，与没有阴影的路径在数值上完全一样，
                // 判据 1 在 ② 上的通过就只是在 ① 上的通过又跑了一遍。
                // 这里量的是强的那层：①/② 两次渲染的**逐像素最大差**。
                Apply(k_Configs[0], mat, camData, pointLights);
                Shader.SetGlobalVector(s_CtrlId, new Vector4(1f, 0f, 0f, 0f));
                Warmup(cam);
                var shadowOff = RenderAndRead(cam, rt, readback);
                Apply(k_Configs[1], mat, camData, pointLights);
                Warmup(cam);
                var shadowOn = RenderAndRead(cam, rt, readback);
                Shader.SetGlobalVector(s_CtrlId, Vector4.zero);

                float shadowMaxDiff = 0f;
                int shadowDiffPixels = 0;
                for (int i = 0; i < shadowOff.Length; i++)
                {
                    Color a = shadowOff[i], b = shadowOn[i];
                    if (Mathf.Max(a.r, Mathf.Max(a.g, a.b)) >= k_SentinelGate) continue;
                    if (Mathf.Max(b.r, Mathf.Max(b.g, b.b)) >= k_SentinelGate) continue;
                    float d = Mathf.Max(Mathf.Abs(a.r - b.r),
                              Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
                    if (d > k_ReadbackFloor) shadowDiffPixels++;
                    shadowMaxDiff = Mathf.Max(shadowMaxDiff, d);
                }
                sb.Append("── 覆盖　主光阴影　配置①(关) vs ②(开) 的逐像素最大差 = ")
                  .Append(shadowMaxDiff.ToString("E3"))
                  .Append("　超地板(").Append(k_ReadbackFloor.ToString("E3"))
                  .Append(")的像素 ").Append(shadowDiffPixels).Append('/').Append(shadowOff.Length)
                  .AppendLine();
                if (shadowDiffPixels == 0)
                    sb.AppendLine("　 　 **没有任何像素因阴影而改变** —— shadowAttenuation 恒为 1，"
                                + "配置 ② 在数值上与 ① 等同，「阴影分支被比对过」这句话"
                                + "只在「代码编进去了」这个弱含义上成立，记为部分未覆盖。");

                bool ok = true;

                // ── 判据 1：逐配置等价
                sb.AppendLine("── 判据 1：各关键字配置下，我的累加 ≡ UniversalFragmentPBR");
                // 点名一件被强制关掉的东西。不点名的话，下面一排 OK 会被读成
                // 「#12 不改变直射光」—— 恰好是反的。这一行报的是
                // ResolveSunTransmittanceRef() 的**实际返回值**，不是「我设过了」这句声称：
                // 钩子若没生效（比如将来 pass 改成不走这个取值口），这里的 w 会是 1，
                // 读报告的人立刻看得见。
                var tRefNow = VistaTimeOfDay.ResolveSunTransmittanceRef();
                sb.Append("　 前提：#12 逐像素太阳透射率在本自检期间**强制不生效** —— "
                        + "实测下发值 w = ").Append(tRefNow.w.ToString("F1"))
                  .AppendLine("（0 = 比值恒 1）。它长在 VistaComputeLighting 里且只乘我这一侧，"
                            + "生效时两侧天然差 mainLightColor·(ratio−1)，与拷贝对不对无关。"
                            + "所以本判据的「等价」是**在 #12 关掉的前提下**的等价；"
                            + "#12 自己的正确性由另一条自检负责，不在这里。");

                // 硬闸：钩子没生效就不要往下解释数字。
                // 只报不判是不够的 —— 钩子失效 + 场景里有 ToD 时判据 1 会真的失败，
                // 而那份失败的报告长得和「拷贝走歧」一模一样。归因错了比失败更贵。
                if (tRefNow.w > 0.5f)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：钩子没有生效（w = 1），本自检的前提不成立。"
                                + "下面所有相对误差都可能只是 #12 的比值，而不是累加的差异 —— "
                                + "先查 VistaTimeOfDay.s_DebugTRefOverride 有没有被别处覆盖、"
                                + "以及 Sky-View pass 是否仍走 ResolveSunTransmittanceRef() 取值。");
                }
                // 0 档读数的可测下限：读回地板折算回相对误差。判据 1 与 1b 都用它，
                // 所以在这里声明一次，避免两处各写一遍而悄悄写出两个不同的地板。
                float relFloor = k_ReadbackFloor / k_RelScale;
                float worstAll = 0f;
                string worstAllCfg = null;
                int worstCfgIdx = -1, worstIndex = -1, worstChannel = -1;
                for (int ci = 0; ci < k_Configs.Length; ci++)
                {
                    var cfg = k_Configs[ci];
                    Apply(cfg, mat, camData, pointLights);
                    Warmup(cam);
                    var px = RenderAndRead(cam, rt, readback);
                    Measure(px, out float maxRel, out int sentinel, out int compared,
                            out int argIndex, out int argChannel);

                    bool cfgOk = sentinel == 0 && compared > 0 && maxRel <= k_RelTol;
                    if (!cfgOk) ok = false;
                    // maxRel 已经除过 k_RelScale，所以门限也得除 —— 直接拿
                    // k_SentinelGate 比会把门限放大 100 倍，等于这条守卫失效。
                    if (maxRel < k_SentinelGate / k_RelScale && maxRel > worstAll)
                    {
                        worstAll = maxRel;
                        worstAllCfg = cfg.name;
                        worstCfgIdx = ci;
                        worstIndex = argIndex;
                        worstChannel = argChannel;
                    }

                    // 读数落在尺子地板以下时只能作上界报出（与 3 档分子同一条规矩）：
                    // relError 的期望值是 0，而读回路径有一条 ±k_ReadbackFloor 的加性场，
                    // 把地板值印成「最大相对误差 = 6.6e-6」等于宣称量到了地板以下的东西。
                    string relText = sentinel > 0
                        ? "（有哨兵，见下）"
                        : (maxRel <= relFloor
                            ? "≤ " + relFloor.ToString("E3") + "（尺子地板，上界）"
                            : maxRel.ToString("E3"));

                    sb.Append("　 ").Append(cfgOk ? "OK  " : "**失败** ").Append(cfg.name)
                      .Append("　最大相对误差 = ")
                      .Append(relText)
                      .Append("　比对像素 ").Append(compared).Append('/').Append(k_Size * k_Size)
                      .AppendLine();

                    if (sentinel > 0)
                        sb.Append("　     ↳ 哨兵像素 ").Append(sentinel)
                          .AppendLine(" 个：这些像素没有参与比对。两种原因 —— 布景没盖满视口"
                                    + "（改 k_BackdropZ / 背板尺寸），或 Rendering Debugger 开着"
                                    + "调试视图接管了输出（关掉再跑）。");
                    else if (compared == 0)
                        sb.AppendLine("　     ↳ **一个像素都没比到**。这正是「恒 0 假通过」那种失效，"
                                    + "现在被哨兵挡住了。");
                    else if (maxRel > k_RelTol)
                        sb.AppendLine("　     ↳ 拷贝走歧了。对照 URP ShaderLibrary/Lighting.hlsl 的 "
                                    + "UniversalFragmentPBR：先看这个配置新开的关键字对应的 #if 分支"
                                    + "是不是在 VistaComputeLighting 里漏了。");
                }
                if (worstAllCfg != null)
                    sb.Append("　 全配置最坏 = ")
                      .Append(worstAll <= relFloor
                              ? "≤ " + relFloor.ToString("E3") + "（尺子地板，上界）"
                              : worstAll.ToString("E3"))
                      .Append("　@ ").Append(worstAllCfg).AppendLine();

                // ── 判据 1b：最坏像素的归因
                //
                // 判据 1 只给出一个相对误差。那个数在**两种相反的情形**下都会出现，
                // 而它们的处置完全不同：
                //   · |ref| ≲ 分母下限 → 相对误差是下限撑出来的，绝对量看不见；
                //   · |ref| 不小       → 真的算得不一样，得追。
                // 所以在下结论之前先把那次除法的两个操作数读出来。
                // 「先归因再理论」—— 不读这两个数就只能猜，而猜出的归因会被当结论用。
                if (worstCfgIdx >= 0 && worstIndex >= 0)
                {
                    Apply(k_Configs[worstCfgIdx], mat, camData, pointLights);

                    // 六档都读，且**都在同一组渲染里**。
                    // 只读 1/2 档是不够的：那样一旦与 0 档的结论矛盾，就分不清是
                    // 「两侧真的相等」还是「两次渲染不是同一幅画」。0 档重读一次，
                    // 这个歧义就被消掉了。
                    Shader.SetGlobalVector(s_CtrlId, Vector4.zero);
                    Warmup(cam);
                    var pass0 = RenderAndRead(cam, rt, readback);
                    // 0 档的读数是 relError × k_RelScale（原因见 shader 侧声明），
                    // 这里立刻还原成相对误差，好让下面所有归因分支都在同一套单位里。
                    float rel0 = Channel(pass0[worstIndex], worstChannel) / k_RelScale;
                    // CountAtLeast 扫的是**原始读数**，门限必须乘回去。
                    int plateau = CountAtLeast(pass0, worstAll * k_RelScale * 0.999f);

                    Shader.SetGlobalVector(s_CtrlId, new Vector4(1f, 0f, 0f, 0f));
                    Warmup(cam);
                    var mine = RenderAndRead(cam, rt, readback)[worstIndex];

                    Shader.SetGlobalVector(s_CtrlId, new Vector4(2f, 0f, 0f, 0f));
                    Warmup(cam);
                    var refc = RenderAndRead(cam, rt, readback)[worstIndex];

                    // 3/4 档才是能回答「这个相对误差是怎么来的」的两档：
                    // 它们是那次除法的**分子与分母本身**，减法发生在写出之前，
                    // 所以差值不再被两次独立量化抹掉；而分母让「下限撑出来的」
                    // 这个判断有了直接读数，不必再从参考值反推。
                    Shader.SetGlobalVector(s_CtrlId, new Vector4(3f, 0f, 0f, 0f));
                    Warmup(cam);
                    var numc = RenderAndRead(cam, rt, readback)[worstIndex];

                    Shader.SetGlobalVector(s_CtrlId, new Vector4(4f, 0f, 0f, 0f));
                    Warmup(cam);
                    var denc = RenderAndRead(cam, rt, readback)[worstIndex];

                    // 已知常量对照：payload 写死 (0.25, 0.5, 0.75)。这一档量的不是被测
                    // 对象，是**「读回的数确实出自 payload 表达式」这个前提**本身。
                    // 为什么不写 0：0 与「这一档没执行、读到的是很小的 relError」不可分辨 ——
                    // 上一版就是在这里断了归因链。一个古怪的已知常量同时回答两件事：
                    // 分支进了没有、以及写入/读回路径有没有改动这个值。
                    // 全图统计而不是只看最坏像素：污染若只出现在别的像素上，
                    // 对判据 1 的最大值同样有威胁，不该因为「这次最坏点没中」而漏掉。
                    Shader.SetGlobalVector(s_CtrlId, new Vector4(5f, 0f, 0f, 0f));
                    Warmup(cam);
                    var constPass = RenderAndRead(cam, rt, readback);
                    var constAtWorst = constPass[worstIndex];
                    Measure(constPass, out _, out int constSentinel, out _, out _, out _);
                    ClassifyConst(constPass, k_Size, k_ModeConst,
                                  out int constBad, out int constDev,
                                  out int constInteriorDev, out float constMaxDev);
                    string constSamples = SamplePixels(constPass, k_Size);

                    // 同一档再渲一次，但渲染前由 CPU 把 RT 涂成一个既非 5 档常量
                    // 又非哨兵(30000) 的值。这一次读数把「没被碰过 / 清了没画 /
                    // 正常着色 / 另有来源」四种可能分开（说明见 RenderAndRead 的重载）。
                    var probe = RenderAndRead(cam, rt, readback, new Color(k_PreClear, k_PreClear, k_PreClear, 1f));
                    float probeAtWorst = Channel(probe[worstIndex], worstChannel);

                    Shader.SetGlobalVector(s_CtrlId, Vector4.zero);

                    float m = Channel(mine, worstChannel);
                    float r = Channel(refc, worstChannel);
                    float numRaw = Channel(numc, worstChannel);
                    bool mode3Ran = numRaw >= k_NumBias * 0.5f;
                    float num = (numRaw - k_NumBias) / k_NumScale;
                    float den = Channel(denc, worstChannel);
                    float dQuant = Mathf.Abs(m - r);
                    float ulp = HalfUlp(r);
                    float relCpu = num / Mathf.Max(den, 1e-30f);
                    float constExpect = Channel(k_ModeConst, worstChannel);

                    // 分子低于 NumFloor 时只能作上界报出。写成一个具体值等于宣称
                    // 量到了尺子地板以下的东西 —— 上一版正是这样解出了「负的绝对差」。
                    //
                    // 两条 ulp 都报：half 那条对应「payload 存进 ARGBHalf RT」那一步的量化，
                    // fp32 那条对应「减法在写出之前完成」那一步的算术。3 档量的是后者，
                    // 只报 half 会把半个 fp32 ulp 的差异印成 0.000 —— 一个把最强的通过
                    // 说成「测不到」的读数。
                    string numText = Mathf.Abs(num) < NumFloor
                        ? "≤ " + NumFloor.ToString("E3") + "（低于尺子地板，只能作上界）"
                        : num.ToString("E3") + "　= " + (num / ulp).ToString("F3") + " 个 half ulp / "
                          + (num / VistaSelfTestNumerics.Fp32Ulp(r)).ToString("F3") + " 个 fp32 ulp";

                    sb.AppendLine("── 判据 1b：最坏像素归因（同配置换输出档位，六档同组渲染）");
                    sb.Append("　 像素 (").Append(worstIndex % k_Size).Append(", ")
                      .Append(worstIndex / k_Size).Append(")　通道 ")
                      .Append("rgb"[Mathf.Max(worstChannel, 0)])
                      .Append("　达到最坏值的像素数 ").Append(plateau).Append('/').Append(k_Size * k_Size)
                      .AppendLine();
                    sb.Append("　 0 档重读 = ").Append(rel0.ToString("E3"))
                      .Append("　我的 = ").Append(m.ToString("E6"))
                      .Append("　参考 = ").Append(r.ToString("E6"))
                      .AppendLine();
                    sb.Append("　 3 档原始读数 = ").Append(numRaw.ToString("E6"))
                      .Append("（偏置 ").Append(k_NumBias.ToString("G"))
                      .Append(" → ").Append(mode3Ran ? "该档执行过" : "**该档没执行**").Append("）")
                      .Append("　分子 |我的−参考| = ").Append(numText)
                      .Append("　4 档分母 = ").Append(den.ToString("E3"))
                      .Append("　商 = ").Append(relCpu.ToString("E3"))
                      .Append("　（1/2 档相减只能得到 ").Append(dQuant.ToString("E3"))
                      .AppendLine("，那是量化噪声，不是差异）");
                    sb.Append("　 5 档已知常量对照：期望 0.25/0.5/0.75　本像素读回 ")
                      .Append(constAtWorst.r.ToString("E3")).Append('/')
                      .Append(constAtWorst.g.ToString("E3")).Append('/')
                      .Append(constAtWorst.b.ToString("E3"))
                      .Append("　哨兵 ").Append(constSentinel)
                      .Append("　**误判**像素 ").Append(constBad).Append('/').Append(k_Size * k_Size)
                      .Append("（最近邻分类，判别余量 0.125 = 地板的 128 倍）")
                      .Append("　偏离像素 ").Append(constDev)
                      .Append("（内部 ").Append(constInteriorDev).Append("，最大偏离 ")
                      .Append(constMaxDev.ToString("E3"))
                      .Append(" = 读回地板的 ").Append((constMaxDev / k_ReadbackFloor).ToString("F2"))
                      .AppendLine(" 倍）　（三通道互异：还能查出通道错位）");
                    sb.Append("　 5 档采样：").AppendLine(constSamples);
                    sb.Append("　 5 档 + CPU 预清屏 ").Append(k_PreClear.ToString("F1"))
                      .Append("：本像素 = ").Append(probeAtWorst.ToString("E6"))
                      .AppendLine("　（= 预清屏值 → 该像素没被渲染碰过；= 哨兵 → 清了但没着色；"
                                + "= 5 档常量 → 正常着色；三者皆非 → 另有来源）");

                    // 归因分支必须先排除「尺子自己的问题」，再谈被测对象。
                    // 顺序反了就会把一次读数失效说成一条代码缺陷（或反过来）。
                    //
                    // 前几条都是**尺子坏了**，必须让整条自检失败：
                    // 归因链断了还印通过，等于把「我不知道这个数是怎么来的」
                    // 写成「这个数没问题」。判据 1 的数值本身也就不可信了。
                    bool ruler = false;

                    // 地板校核。这一条与下面的归因链是**两件事**：
                    // 归因链问「读到的数是不是出自 payload」，这一条问「判据 1 用来
                    // 折算上界的那个地板常量还成立吗」。地板变大了而不校核，判据 1
                    // 就会把一个被低估的上界当成结论 —— 而它恰恰是这条自检的产出。
                    // 反过来，地板**变小**不算失败：那只说明这台机器/驱动更干净，
                    // 上界仍然成立（把它也判失败等于要求两次测量逐位相同）。
                    if (constMaxDev > k_ReadbackFloor)
                    {
                        ruler = true;
                        sb.Append("　 归因：**读回地板比记录值大** —— 实测最大偏离 ")
                          .Append(constMaxDev.ToString("E3")).Append("，记录的 k_ReadbackFloor 是 ")
                          .Append(k_ReadbackFloor.ToString("E3"))
                          .AppendLine("。判据 1 报的相对误差上界是用这个常量折算的，"
                                    + "地板被低估时那个上界不成立。把 k_ReadbackFloor 更新成实测值"
                                    + "（并同步 shader 里 _VistaDiffCtrl 的说明），再重新解读判据 1。");
                    }

                    // 已知常量对照放在最前面：它检的是「读回的数一定出自 payload」这个
                    // **前提**。前提不成立时，后面每一条分支解释的都是别人的数。
                    //
                    // 判据是**最近邻误判数**而不是「偏离数 > 0」：后者隐含「写入→读回无损」，
                    // 而那个前提已被实测推翻（见 ClassifyConst 的说明），留着只会恒定报警。
                    if (constBad > 0)
                    {
                        ruler = true;
                        sb.Append("　 归因：**5 档写死已知常量，读回的值离别的候选更近** —— "
                                + "读到的数与 payload 无关。");
                        if (Mathf.Abs(probeAtWorst - k_PreClear) <= k_PreClear * 0.01f)
                            sb.AppendLine("预清屏探测显示该像素**根本没被渲染碰过**（读回 = 我自己涂的值），"
                                        + "所以它保留的是 RT 的残留内容 —— 这也解释了为什么八个配置报出一模一样的数。"
                                        + "修法：每次渲染前把 RT 预清成哨兵，未写到的像素就会被算成「未比对」而不是「误差最小」。");
                        else if (probeAtWorst >= k_SentinelGate)
                            sb.AppendLine("预清屏探测显示该像素被清成了哨兵但没有着色 —— 那里没有几何覆盖，"
                                        + "应当计入哨兵数而不是参与取最大值。");
                        else if (Mathf.Abs(probeAtWorst - constExpect) < 0.125f)
                            sb.AppendLine("但预清屏探测下**本像素读回的正是 5 档常量**（最近邻）—— "
                                        + "说明这个像素本身是好的，偏离出现在别处，看上面的分布计数定位。");
                        else
                            sb.Append("预清屏探测读回 ").Append(probeAtWorst.ToString("E6"))
                              .AppendLine("，既不是预清屏值、也不是哨兵、也不是 5 档常量 —— "
                                        + "该像素确实被写过，但写进去的东西与 payload 无关。"
                                        + "污染来自渲染之后的某一步（URP 的 FinalBlit、格式转换、"
                                        + "取样位置都在候选里）。");
                        if (constInteriorDev == 0)
                            sb.AppendLine("　 　 分布：全部落在最外一圈，内部干净 —— 这是边界效应，"
                                        + "把最外一圈排除掉就能得到干净的测量。");
                        else
                            sb.Append("　 　 分布：内部也有 ").Append(constInteriorDev)
                              .AppendLine(" 个偏离像素 —— 不是边界效应，排除边界只会把它藏起来。");
                    }
                    // 5 档说明「payload 决定读数」这个前提成立了，才轮到问 3 档进没进。
                    // 顺序不能反：3 档没进有两种原因（分支阈值不对 / 读回路径坏），
                    // 5 档先排掉后者，这条分支才只剩前者一种解释。
                    else if (!mode3Ran)
                    {
                        ruler = true;
                        sb.Append("　 归因：**3 档没有执行** —— 原始读数 ").Append(numRaw.ToString("E6"))
                          .Append(" 低于偏置 ").Append(k_NumBias.ToString("G"))
                          .AppendLine(" 的一半，说明 else-if 链没进到那一支（而 5 档已排除读回路径）。"
                                    + "先对齐 _VistaDiffCtrl 的档位取值与 shader 里的比较阈值，"
                                    + "分子/分母都不可信之前不要解释判据 1 的数字。");
                    }
                    // 4 档的生效检查与 3 档同类（问「这一档执行了吗」），所以排在
                    // 一切数值解释之前：分母不可信时，下面每个用到 relCpu 的判据
                    // 解释的都是一个错的商。
                    else if (den < VISTA_DIFF_DENOM_FLOOR * 0.99f)
                    {
                        ruler = true;
                        sb.Append("　 归因：**4 档读出的分母 ").Append(den.ToString("E3"))
                          .AppendLine(" 低于 shader 里的下限 —— 档位没有生效（读到的还是别的档），"
                                    + "或两侧的下限常量不一致。归因链在这里断了，先修尺子。");
                    }
                    // ── 两侧都落在尺子地板以下 ──
                    //
                    // 这一条必须排在所有「拿 rel0 去比」的判据**之前**。理由是上一轮
                    // 实测出来的：0 档读回 6.595e-6、3/4 档复算出 2.9e-10，两个数都是
                    // **上界**而不是测量值，它们的差必然接近地板本身 —— 于是那条 5%
                    // 相对门限**必然**触发，把「两侧一致」印成「尺子坏了」。
                    // 拿两个上界做相对比较，是这一整轮反复踩到的同一个坑。
                    //
                    // 用 && 而不是 ||：两侧同时落地板才是「一致」；只有一侧落地板
                    // 是矛盾，由下一条抓。
                    else if (rel0 <= relFloor && Mathf.Abs(num) < NumFloor)
                    {
                        sb.Append("　 归因：**0 档（≤ ").Append(relFloor.ToString("E3"))
                          .Append("）与 3/4 档（商 ≤ ").Append((NumFloor / den).ToString("E3"))
                          .AppendLine("）同时落在各自的尺子地板以下** —— 这是这台尺子能给出的"
                                    + "最强结论：两侧的差异小于可测下限。配合 1/2 档读回逐位相同，"
                                    + "以及 3 档在写出前就完成减法（分子不受两次独立量化影响），"
                                    + "拷贝与 UniversalFragmentPBR 的等价性成立。");
                        sb.Append("　 　 但要点明一条**惰性判据**：worstAll 也被钉在地板上，"
                                + "于是「两次渲染是不是同一幅画」那条守卫本轮无法失败"
                                + "（rel0 只能等于地板，不可能低于 worstAll 的一半）。"
                                + "它在本轮不提供保障 —— 真正排除「换了画面」的是 1/2 档"
                                + "读回 ").Append(m.ToString("E6"))
                          .AppendLine("：那是一个与地板无关的、有结构的量，换了画面它会跟着变。");
                    }
                    // ── 粗尺子钉在地板上，细尺子给出一个**低于粗尺子地板**的读数 ──
                    //
                    // 这不是矛盾，是分辨力差异 —— 必须排在下面那条「只有一侧落地板」之前。
                    // 两把尺子的可测下限实测差四个数量级：0 档是 relFloor（9.766E-006，
                    // 由 ±1/1024 的加性地板 ÷ REL_SCALE 得来），3/4 档折算回相对量只有
                    // NumFloor/den（≈ 1E-009，因为减法发生在写出**之前**）。
                    // 于是「细尺子量出 6E-008、粗尺子说测不到」是**必然结果**：
                    // 6E-008 落在粗尺子地板以下，它结构性看不见。
                    //
                    // ── 这条分支是为一次实测出来的失败加的，把那次读数记在这里 ──
                    //
                    // 像素 (126,42) 通道 b、配置①：rel0 = 6.642E-006（地板 9.766E-006 ⇒ 落地板）、
                    // 3 档原始读数 3.098145E-001（⇒ 该档执行过）、分子 5.981E-008
                    // （地板 9.766E-010 ⇒ **未**落地板）、分母 9.795E-001、商 6.107E-008、
                    // 我的 == 参考 == 9.794922E-001（读得出的每一位都相同）、1/2 档相减 = 0。
                    // 上一版把它并进了下面的矛盾分支，于是这一组读数 —— 这台尺子能给出的
                    // 最强结论 —— 被报成了失败。教训与上面那条「拿两个上界做相对比较」同源：
                    // **两把分辨力差几个数量级的尺子，粗尺子钉在自己的地板上、细尺子给出一个
                    // 低于粗尺子地板的读数，只说明分辨力不同，不构成矛盾。**
                    //
                    // 一条必须承认的事：那次读数**没有复现**。修完之后连跑两轮，最坏像素都落在
                    // (127,42)、分子回到 ≤ 9.766E-010，走的是上面那条「两侧同时落地板」。
                    // 也就是说本分支覆盖的是一个**间歇**条件（最坏像素在 16384 个里只领先
                    // 1 个，编译器/驱动的求值次序一变就换人），它在稳态下不执行。
                    // 留着它不是为了「以防万一」—— 上面那组数字就是它的反例来源；
                    // 但读报告的人要知道：报告里出现本分支的文字才说明它被执行过。
                    //
                    // 门取 2×relFloor 而不是 1×：0 档那条地板是**加性**的，真实值恰在
                    // 一倍地板附近时 rel0 读回 0 或读回 2 倍都可能。取 1× 会在边界上
                    // 造出一段必然误报的区间。超过 2× 还落地板，粗尺子就真的该看见
                    // 却没看见 —— 那才是矛盾，交给下一条。
                    // 条件里带 num > 0：分子在 shader 里是 abs() 出来的，解出负值只有一种
                    // 解释 —— 尺子坏了（档位/缩放/偏置对不上）。不加这一项的话，一个
                    // 大负数会满足 relCpu <= 2×relFloor 而被本分支当成**通过**吞掉，
                    // 恰好是最危险的方向。落回下面的矛盾分支才对：那里会点名 ruler。
                    // （读回地板造成的那点负值 |num| ≲ NumFloor，已被上一条分支接走。）
                    else if (rel0 <= relFloor && num > 0f && relCpu <= 2f * relFloor)
                    {
                        sb.Append("　 归因：**细尺子量到了粗尺子结构上看不见的量** —— 0 档钉在自己的"
                                + "地板上（≤ ").Append(relFloor.ToString("E3"))
                          .Append("），3/4 档复算出 ").Append(relCpu.ToString("E3"))
                          .Append("，而它的可测下限只有 ").Append((NumFloor / den).ToString("E3"))
                          .Append("（比 0 档细 ").Append((relFloor / (NumFloor / den)).ToString("F0"))
                          .AppendLine(" 倍）。细尺子的读数落在粗尺子地板以下，"
                                    + "粗尺子**不可能**看见它 —— 两个读数一致，不是矛盾。");
                        sb.Append("　 　 这个差异有多大：绝对差 ").Append(num.ToString("E3"))
                          .Append(" = ").Append((num / VistaSelfTestNumerics.Fp32Ulp(r)).ToString("F3"))
                          .Append(" 个 fp32 ulp（参考值 ").Append(r.ToString("E6"))
                          .AppendLine("）。半个 ulp 量级 = 一次加法的舍入 —— 两侧同一套算术、"
                                    + "编译器排的加法顺序不同，不是抄错。数 fp32 而不是 half："
                                    + "3 档的减法在写出前完成，是 fp32 算的（D3D11 上 half 即 float），"
                                    + "拿 half ulp 数它只会得到 0.000，把结论抹平成「测不到」。");
                        sb.Append("　 　 这比「两侧同时落地板」**更强**：那一档只能给出上界，"
                                + "这一档给出了具体的数。旁证：1/2 档读回相减 = ")
                          .Append(dQuant.ToString("E3"))
                          .Append("，3 档原始读数 ").Append(numRaw.ToString("E6"))
                          .AppendLine("（远高于偏置的一半 → 该档确实执行过）。");
                        sb.AppendLine("　 　 同样要点明那条**惰性判据**：worstAll 也钉在地板上，"
                                    + "于是「两次渲染是不是同一幅画」那条守卫本轮无法失败"
                                    + "（rel0 只能等于地板，不可能低于 worstAll 的一半）。"
                                    + "本轮排除「换了画面」靠的是 1/2 档与 3 档那两个有结构的读数。");
                    }
                    // 只有一侧落地板 → 三档量的不是同一件事，且这一次不能用相对门限
                    // 说明（其中一侧没有可比的量级），单独成一条。
                    //
                    // 注意上面那条新分支已经把**唯一一种无害的「只有一侧落地板」**接走了。
                    // 剩到这里的只有两种真矛盾，方向相反：
                    //   · 0 档落地板，而细尺子的读数**高过** 2 倍粗地板 —— 粗尺子该看见却没看见；
                    //   · 细尺子落地板（≤ 1e-9），而 0 档报出 > 9.8e-6 —— 细尺子比粗尺子细四个
                    //     数量级，它不可能测不到粗尺子能看见的东西。
                    // 两种都说明三档读的不是同一组量，归因链断裂。
                    else if (rel0 <= relFloor || Mathf.Abs(num) < NumFloor)
                    {
                        ruler = true;
                        sb.Append("　 归因：**只有一侧落在地板以下** —— 0 档 ").Append(rel0.ToString("E3"))
                          .Append("（地板 ").Append(relFloor.ToString("E3")).Append("）、分子 ")
                          .Append(num.ToString("E3")).Append("（地板 ").Append(NumFloor.ToString("E3"))
                          .AppendLine("）。一个说「有差异」另一个说「测不到」，两者矛盾，"
                                    + "归因链在这里断了。先查 VISTA_DIFF_NUM_SCALE / REL_SCALE "
                                    + "两侧是否一致，再看 6 档读数是否在同一组渲染里取的。");
                    }
                    // 与 worstAll 比，而不是与某个绝对小量比：这里要问的是
                    // 「重读到的还是判据 1 量到的那幅画吗」，而判据 1 量到的值就是 worstAll。
                    // 拿绝对小量当门限会把「真实相对误差本来就很小」误判成尺子坏 ——
                    // 而 fp32 求值次序差异落在 1e-7 量级，正好会踩这个坑。
                    // 同配置重渲应当逐位一致（比值 = 1），真的换了画面时那个像素通常
                    // 掉好几个数量级；0.5 离两者都很远，取哪个同量级的数都不改变结论。
                    else if (rel0 <= worstAll * 0.5f)
                    {
                        ruler = true;
                        sb.Append("　 归因：**0 档重读 ").Append(rel0.ToString("E3"))
                          .Append(" 与判据 1 记录的 ").Append(worstAll.ToString("E3"))
                          .AppendLine(" 不符** —— 两次渲染不是同一幅画。"
                                    + "这是尺子的问题，不是被测对象的问题："
                                    + "先查 Apply() 有没有把配置完整还原（残留关键字 / _BaseColor.a / "
                                    + "renderShadows / 点光开关），再看 Warmup 的帧数够不够。");
                    }
                    // 容差里必须带上两条地板的加性项，不能只有 5% 的相对项：
                    // rel0 与 relCpu 各自都被读回路径抬了最多一条地板，两者之差
                    // 因此天然含有 relFloor + NumFloor/den 这么大的一项。
                    // 只写相对项，就会在 rel0 刚刚超过地板的区间里必然误报。
                    else if (Mathf.Abs(relCpu - rel0) > 0.05f * rel0 + relFloor + NumFloor / den)
                    {
                        ruler = true;
                        sb.AppendLine("　 归因：**分子/分母复算出的相对误差与 0 档不一致** —— "
                                    + "三档量的不是同一件事，归因链在这里断了，先修尺子再谈结论。"
                                    + "（最可能：_VistaDiffCtrl 的档位判断与 CPU 侧传的值不对应，"
                                    + "或 VISTA_DIFF_NUM_SCALE 两边不一致。）");
                    }
                    else if (Mathf.Abs(r) <= 2f * VISTA_DIFF_DENOM_FLOOR)
                    {
                        sb.AppendLine("　 归因：参考值本身在分母下限量级，这个相对误差是**下限撑出来的**，"
                                    + "绝对差远在可见度之下。判据 1 的通过不依赖它。");
                    }
                    else if (num <= 0f)
                    {
                        sb.AppendLine("　 归因：**分子恰好为 0，0 档却不为 0** —— 两者矛盾，"
                                    + "不要把它读成「完全相等」。先看 0 档的读数是不是 half 非规格化区的量化残留。");
                    }
                    else if (num <= 2f * ulp)
                    {
                        sb.Append("　 归因：绝对差 ").Append((num / ulp).ToString("F3"))
                          .AppendLine(" 个 half ulp、参考值不小 —— 这是**求值次序**造成的舍入差异"
                                    + "（两侧同一套算术，编译器排的加法顺序不同），不是抄错。"
                                    + "它不随场景放大：ulp 是相对量。");
                    }
                    else
                    {
                        sb.AppendLine("　 归因：**参考值不小、且差异超过 2 个 ulp** —— 这不是舍入，"
                                    + "是累加真的不一样。即使还没超过 Weber 1% 也要查清："
                                    + "先看这个配置比前一个配置多开了哪个关键字。");
                    }

                    if (ruler)
                    {
                        ok = false;
                        sb.AppendLine("　 —— 归因链断裂按**失败**计：判据 1 的数值来源无法解释时，"
                                    + "它的「通过」也没有意义。");
                    }
                }

                // ── 判据 2：逐项故障注入 —— 自检自己有没有分辨力
                //
                // 在最丰富的配置下跑。四项分别注入 2%，要求被报出来。
                // 报不出来的项**不是通过**，是「这一项在这一帧没参与」，
                // 必须写成未覆盖。
                sb.AppendLine("── 判据 2：逐项故障注入（各 +2%），验证自检看得见");
                var richest = k_Configs[3];
                Apply(richest, mat, camData, pointLights);

                string[] termNames = { "mainLightColor", "additionalLightsColor", "giColor", "vertexLightingColor" };
                var detected = new float[4];
                var detectedX10 = new float[4];
                int liveTerms = 0;
                for (int t = 0; t < 4; t++)
                {
                    var v = Vector4.zero;
                    v[t] = k_Inject;
                    Shader.SetGlobalVector(s_InjectId, v);
                    Warmup(cam);
                    var px = RenderAndRead(cam, rt, readback);
                    Measure(px, out detected[t], out int sentinel, out _, out _, out _);
                    if (sentinel > 0) detected[t] = float.NaN;
                    if (detected[t] > k_RelTol) liveTerms++;

                    // ── 同一项再注一次 10 倍，只为了检验「占比」这个读数本身可不可信。
                    //
                    // 按代数，报出的相对误差 = δ·X / max(|参考|, 分母下限)，对 δ 是
                    // **严格线性**的，所以 报出/δ（即占比）应当与 δ 无关。它若随 δ 变，
                    // 只可能有两个原因，而两者都会让占比这个数失去意义：
                    //   · 分母下限被触发（参考值 ≲ 1e-3）—— 那时报出的不是相对误差，
                    //     占比也不是占比；
                    //   · 取最大值的那个像素换了地方 —— 那时两次报的是两个不同像素的占比。
                    //
                    // 为什么这一条非做不可：本轮 giColor 报出 1.000E-002，而判定门是
                    // 1.000E-002，占比恰好 50.00% = 门/注入。一个正好落在门上的读数，
                    // 判定由打印出来的最后一位决定 —— 本项目已经被这种形态咬过
                    //（尺子地板与期望值同量级时，尺子会替被测对象伪造一个结论）。
                    // 与其解释这个巧合，不如换一把刻度不同的尺子再量一次。
                    v[t] = k_Inject * 10f;
                    Shader.SetGlobalVector(s_InjectId, v);
                    Warmup(cam);
                    var pxX10 = RenderAndRead(cam, rt, readback);
                    Measure(pxX10, out detectedX10[t], out int sentinelX10, out _, out _, out _);
                    if (sentinelX10 > 0) detectedX10[t] = float.NaN;
                }
                Shader.SetGlobalVector(s_InjectId, Vector4.zero);

                for (int t = 0; t < 4; t++)
                {
                    bool broken = float.IsNaN(detected[t]);
                    bool live = detected[t] > k_RelTol;

                    // ── 把「未覆盖」这个判定拆成两件事，因为它们差三个数量级。
                    //
                    // 注入 δ 在第 t 项上，报出的相对误差就是 δ·X/total（X 是该项的值），
                    // 所以 detected/δ **就是该项在最佳像素上的占比** —— 一个可读的量，
                    // 不需要再猜。于是「报不出来」有两种完全不同的成因：
                    //   · 占比 ≈ 0：这一项这一帧真的没参与，判据 1 在它上面是空判；
                    //   · 占比不小、只是 δ·占比 落在判据 1 自己的门之下：这一项**参与了**，
                    //     判据 1 对它的灵敏度不够，一个 δ 量级的错误会被它放过。
                    //
                    // 上一版把两者都写成「为 0 或占比低于 1%」。那句话在第二种情形下是
                    // 错的，而且错得很具体：本轮 giColor 占比 50%，被那句话说成 < 1%，
                    // 差 50 倍。更糟的是它指向的修法也是错的（去查这一项为什么没参与，
                    // 而真正该做的是把该项在布景里的占比抬高、或承认灵敏度上限）。
                    float share = detected[t] / k_Inject;
                    bool shareAtFloor = detected[t] <= relFloor;

                    // 三态的判定不变（live 与否决定判据 1 的结论作用域），
                    // 变的是**说法**：把成因分开，并给出占比这个读数。
                    sb.Append("　 ")
                      .Append(broken ? "**布景坏** " : live ? "有分辨力 " : "灵敏度不足  ")
                      .Append(termNames[t].PadRight(22))
                      .Append("注入 2% → 报出 ")
                      .Append(broken ? "（出现哨兵像素，本项无法判定）"
                            : shareAtFloor
                              ? "≤ " + relFloor.ToString("E3") + "（尺子地板，上界）"
                              : detected[t].ToString("E3"));
                    if (!broken)
                        sb.Append("　该项占比 ")
                          .Append(shareAtFloor ? "≤ " : "= ")
                          .Append((share * 100f).ToString("F2")).Append('%');

                    // ── 占比这个读数的交叉验证：换 10 倍注入再量一次。
                    //
                    // 报的是**两次的占比之差**，而不是「两次读数之比是不是 10」：
                    // 后者在读数落到尺子地板上时会自动成立（地板/地板 = 1，
                    // 看起来只是"比值不对"，却不指向原因），前者在两种失效下都会张开。
                    bool bothBroken = broken || float.IsNaN(detectedX10[t]);
                    float shareX10 = detectedX10[t] / (k_Inject * 10f);
                    if (!bothBroken)
                    {
                        float shareGap = Mathf.Abs(shareX10 - share);
                        // 容差：×1 那一档的读数被地板抬高 relFloor，折算到占比是
                        // relFloor/δ；×10 那一档的地板折算量小 10 倍，取大的那个。
                        float shareTol = relFloor / k_Inject;
                        sb.Append("　（×10 注入复量 ")
                          .Append((shareX10 * 100f).ToString("F2")).Append("%，差 ")
                          .Append((shareGap * 100f).ToString("F2")).Append("pp）");
                        if (shareGap > shareTol)
                            sb.Append("　← **占比这个读数不可信**：占比按代数对注入量"
                                    + "严格线性，随 δ 变只能是分母下限被触发"
                                    + "（参考值 ≲ 1e-3）或最大值像素换了地方 —— "
                                    + "两种情形下「占比」都不再是占比");
                    }

                    if (!broken && !live)
                    {
                        // 占比自己就把成因分开了：低于注入量与判据门之比（1%/2% = 50%）
                        // 才可能落在门下，而落在尺子地板上则是「真的没参与」。
                        sb.Append(shareAtFloor
                            ? "　← 这一项在本帧**没有参与**（占比落在尺子地板以下），"
                            + "判据 1 在它上面是 0 == 0 的空判"
                            : "　← 这一项**参与了**，但 2% × 占比 落在判据 1 自己的 1% 门"
                            + "之下（门/注入 = 50%，占比不足 50% 的项必然如此）—— "
                            + "判据 1 会放过该项里一个 2% 量级的错误。想覆盖它得把该项"
                            + "在布景里的占比抬到 50% 以上，而不是去查它为什么没参与");
                    }
                    sb.AppendLine();
                    if (broken) ok = false;
                }

                if (liveTerms == 0)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：四项全都注不进去，说明这条自检对任何偏差都是瞎的 —— "
                                + "判据 1 的通过毫无意义。先查 _VistaDiffInject 有没有真的下发"
                                + "（材质上 VISTA_LIT_DIFF_DEBUG 关键字是否生效）。");
                }
                else
                {
                    sb.Append("　 ").Append(liveTerms).Append(" / 4 项有分辨力。")
                      .AppendLine("判据 1 的结论只对这几项成立。");
                }

                // ── 覆盖清单。不是判据，是「通过」这个词的作用域声明。
                sb.AppendLine("── 未覆盖（这些路径本自检管不到，不能算通过）");
                sb.AppendLine("　 · UniversalGBuffer / 延迟 —— Vista/Lit 刻意不提供该 pass，"
                            + "变体 B 在延迟下不存在挂载点；那条路由变体 A 负责。");
                sb.Append("　 · USE_CLUSTER_LIGHT_LOOP = ").Append(DescribeClusterLoop())
                  .AppendLine("。想覆盖它就把 Renderer 的 Rendering Path 切到 Forward+ 再跑一遍本自检"
                            + "（这一行是观测，不参与判定）。");
                sb.AppendLine("　 · LIGHTMAP_ON / DIRLIGHTMAP_COMBINED / DYNAMICLIGHTMAP_ON / SHADOWS_SHADOWMASK"
                            + " —— 需要烘焙数据，布景是运行时搭的，拿不到。"
                            + "MixRealtimeAndBakedGI 的 subtractive 分支因此未覆盖，"
                            + "而 #12 要动的正是它前后的位置：那一步必须补一个带烘焙数据的场景。");
                sb.AppendLine("　 · _ADDITIONAL_LIGHTS_VERTEX / EVALUATE_SH_VERTEX / 探针体 / _LIGHT_COOKIES /"
                            + " _LIGHT_LAYERS / _DBUFFER / _SCREEN_SPACE_OCCLUSION —— 由 URP asset 与"
                            + "其他 RendererFeature 决定，本自检不去改全局资产（改了就会落盘）。");
                sb.AppendLine("　 · DEBUG_DISPLAY 接管路径 —— 那条路上两边都输出替身颜色，"
                            + "比较无意义，shader 里直接写哨兵。");

                return ok;
            }
            finally
            {
                // 先还原全局、再销毁布景：RenderSettings.sun 指着即将被销毁的那盏灯，
                // 反过来的话中间有一小段「sun 指向已销毁对象」的窗口，
                // 那条窗口里任何一次编辑器重绘都会把它当成「没有太阳」。
                RenderSettings.sun = prevSun;
                if (root != null) Object.DestroyImmediate(root);
                if (mat != null) Object.DestroyImmediate(mat);
                if (readback != null) Object.DestroyImmediate(readback);
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 布景
        // ────────────────────────────────────────────────────────────────

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

        static void Build(Shader shader, int layer, RenderTexture rt,
                          out GameObject root, out Camera cam,
                          out UniversalAdditionalCameraData camData,
                          out Material mat, out Light[] pointLights,
                          out Light sunLight)
        {
            root = new GameObject("Vista Lit Accumulation Probe") { hideFlags = HideFlags.HideAndDontSave };

            // ── 相机
            var camGo = new GameObject("Camera") { hideFlags = HideFlags.HideAndDontSave };
            camGo.transform.SetParent(root.transform, false);
            camGo.layer = layer;
            cam = camGo.AddComponent<Camera>();

            cam.enabled = false;                  // 只手动 Render()
            cam.cullingMask = 1 << layer;
            cam.fieldOfView = k_Fov;
            cam.nearClipPlane = k_NearClip;
            cam.farClipPlane = k_FarClip;
            cam.clearFlags = CameraClearFlags.SolidColor;

            // 清屏色 = shader 里的哨兵（VISTA_DIFF_NOT_COMPARED）。这是「未覆盖不能被
            // 读成通过」的第一道措施：任何没被 Vista/Lit 画到的像素都会读出一个巨大值。
            // 必须与 shader 侧同步：0 档现在写的是 relError × 100，旧值 100 只相当于
            // 「100% 偏差」，会让一次真实的严重不一致被读成「这个像素没参与比对」。
            cam.backgroundColor = new Color(30000f, 30000f, 30000f, 1f);

            cam.allowHDR = true;                  // 否则中间 RT 退成 8-bit，1e-4 量级全丢
            cam.allowMSAA = false;                // MSAA 会把相邻像素的哨兵混进被测像素
            cam.targetTexture = rt;
            cam.aspect = 1f;

            camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = false;
            camData.volumeLayerMask = 0;          // 场景的 Tonemapping 不能进来
            camData.antialiasing = AntialiasingMode.None;

            // ── 太阳。从相机后上方打过来，好让球体的影子落在背板上（覆盖阴影分支）。
            //
            // 注意「这盏灯就是 URP 的主光」**不是**自动成立的，也不能靠 layer 隔离来保证：
            // 平行光不因为 layer 不在相机 cullingMask 里就从 visibleLights 里消失，
            // 而 URP 的 GetMainLightIndex 第一条规则是「等于 RenderSettings.sun 的那盏
            // 直接返回」。所以调用方要么把 RenderSettings.sun 指过来，要么承认
            // 上面那句「影子落在背板上」只是**期望**而非事实。
            // （这条是 #12 的自检实测出来的，那里三条判据一起被这个盲点带偏。）
            var lightGo = new GameObject("Sun") { hideFlags = HideFlags.HideAndDontSave };
            lightGo.transform.SetParent(root.transform, false);
            lightGo.layer = layer;
            var sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.4f;
            sun.shadows = LightShadows.Soft;
            lightGo.transform.localRotation = Quaternion.Euler(35f, 18f, 0f);
            sunLight = sun;

            // ── 两盏点光。位置一左一右贴着球，保证球面上有它们的贡献。
            pointLights = new Light[2];
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject("Point " + i) { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(root.transform, false);
                go.layer = layer;
                go.transform.localPosition = new Vector3(i == 0 ? -4f : 4f, 2.5f, k_SphereZ - 3f);
                var pl = go.AddComponent<Light>();
                pl.type = LightType.Point;
                pl.range = 30f;
                pl.intensity = 6f;
                pl.color = i == 0 ? new Color(1f, 0.7f, 0.5f) : new Color(0.5f, 0.7f, 1f);
                pl.shadows = LightShadows.Soft;   // 想覆盖 _ADDITIONAL_LIGHT_SHADOWS
                pointLights[i] = pl;
            }

            // ── 材质
            mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            mat.EnableKeyword("VISTA_LIT_DIFF_DEBUG");
            mat.SetFloat("_Cull", 0f);            // 双面，省掉朝向这个变量
            mat.SetFloat("_Cutoff", 0f);          // AlphaTest 配置下不裁掉任何像素
            mat.SetFloat("_Metallic", 0.35f);
            mat.SetFloat("_Smoothness", 0.55f);
            mat.SetColor("_EmissionColor", new Color(0.05f, 0.02f, 0.01f));

            // ── 背板：盖满视口。
            //
            // 尺寸给到视锥高度的 3 倍。哨兵机制会验证它是不是真盖住了 ——
            // 这里不靠"算得刚好"，靠"算得富余 + 被测量出来"。
            float frustumH = 2f * k_BackdropZ * Mathf.Tan(k_Fov * 0.5f * Mathf.Deg2Rad);
            MakeQuad(root.transform, layer, mat, "Backdrop",
                     new Vector3(0f, 0f, k_BackdropZ), Vector3.one * (frustumH * 3f));

            // ── 球体：给曲面法线（覆盖各种入射角）+ 投影到背板上（覆盖阴影分支）
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "Sphere";
            sphere.hideFlags = HideFlags.HideAndDontSave;
            sphere.layer = layer;
            sphere.transform.SetParent(root.transform, false);
            sphere.transform.localPosition = new Vector3(0f, 0f, k_SphereZ);
            sphere.transform.localScale = Vector3.one * 6f;
            StripAndAssign(sphere, mat);
        }

        static void MakeQuad(Transform parent, int layer, Material mat, string name,
                             Vector3 localPos, Vector3 scale)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            quad.hideFlags = HideFlags.HideAndDontSave;
            quad.layer = layer;
            quad.transform.SetParent(parent, false);
            quad.transform.localPosition = localPos;
            quad.transform.localScale = scale;
            StripAndAssign(quad, mat);
        }

        static void StripAndAssign(GameObject go, Material mat)
        {
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
            // 探针照常接：giColor 要有量，判据 2 才注得进去。
        }

        /// <summary>
        /// 应用一个配置。关键字之外还要动混合状态与相机的阴影开关。
        /// </summary>
        static void Apply(Config cfg, Material mat, UniversalAdditionalCameraData camData,
                          Light[] pointLights)
        {
            // 每次都从干净状态出发：残留关键字会让配置之间互相污染，
            // 而症状是「某个配置莫名通过/失败」，极难定位。
            foreach (var c in k_Configs)
                foreach (var k in c.keywords)
                    mat.DisableKeyword(k);

            foreach (var k in cfg.keywords)
                mat.EnableKeyword(k);

            // 透明配置：**关键字开着，但混合仍设成 One/Zero**。
            //
            // 判据要量的是着色路径（_SURFACE_TYPE_TRANSPARENT 与
            // _ALPHAPREMULTIPLY_ON 改的是 InitializeBRDFData 那一支），不是混合。
            // 若真按 SrcAlpha/OneMinusSrcAlpha 混，写进 RT 的就是
            // 「误差 × alpha + 哨兵清屏色 × (1−alpha)」—— 量到的是一锅粥。
            mat.SetFloat("_SrcBlend", (float)BlendMode.One);
            mat.SetFloat("_DstBlend", (float)BlendMode.Zero);
            mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            mat.SetFloat("_DstBlendAlpha", (float)BlendMode.Zero);
            mat.SetFloat("_ZWrite", 1f);
            mat.SetFloat("_Surface", 0f);
            mat.renderQueue = (int)RenderQueue.Geometry;

            var bc = mat.GetColor("_BaseColor");
            mat.SetColor("_BaseColor", new Color(bc.r, bc.g, bc.b, cfg.baseAlpha));

            camData.renderShadows = cfg.mainLightShadows;
            foreach (var pl in pointLights)
                pl.enabled = cfg.additionalLights;
        }

        /// <summary>
        /// 报告 cluster light loop 那段代码这一次到底编没编。
        ///
        /// 读的是**全局关键字状态**（URP 在 Forward+ 下打开 _CLUSTER_LIGHT_LOOP），
        /// 不是 UniversalRendererData 的私有字段 —— 反射私有布局的断言会在 URP
        /// 升级时静默失效，而这条信息不值得用那种代价换。
        ///
        /// 它是一条**观测**，不是判据：本自检不因它通过或失败。
        /// 所以即使这个关键字的时序与我预期不同，也不会造成错误归因。
        /// </summary>
        static string DescribeClusterLoop()
        {
            return Shader.IsKeywordEnabled("_CLUSTER_LIGHT_LOOP")
                ? "已开（Forward+）→ 那段方向光循环本次已编译并参与比对"
                : "未开（Forward 或该关键字此刻未下发）→ 视为未覆盖";
        }

        // ────────────────────────────────────────────────────────────────
        // 采集
        // ────────────────────────────────────────────────────────────────

        /// <summary>改了关键字/灯之后空转两帧：阴影贴图与灯列表下一帧才稳定。</summary>
        static void Warmup(Camera cam)
        {
            cam.Render();
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
        /// 渲染前先由 CPU 把 RT 涂成 <paramref name="preClear"/>，用来判定某个像素
        /// 到底被渲染管线碰过没有。
        ///
        /// preClear 必须与相机清屏色（哨兵）**以及 payload 可能写出的值**都不同，
        /// 否则「URP 清了它」与「我的清屏活到了最后」两种情形读出同一个数，等于没测。
        /// 于是一次读数能四路区分：
        ///   · = preClear      → 这个像素 URP 既没画也没清，读到的是残留内容；
        ///   · = 哨兵          → 清了但没着色（该像素没有几何覆盖）；
        ///   · = payload 的值  → 正常着色；
        ///   · 以上都不是      → 污染来自别处，继续查。
        /// </summary>
        static Color[] RenderAndRead(Camera cam, RenderTexture rt, Texture2D readback, Color preClear)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, preClear);
            RenderTexture.active = prev;
            return RenderAndRead(cam, rt, readback);
        }

        /// <summary>
        /// 扫全图，取 0 档的最坏相对误差。
        ///
        /// <paramref name="maxRel"/> 是**已经除掉 <see cref="k_RelScale"/> 的**相对误差，
        /// 而哨兵判定用的是**未除**的原始读数 —— 两者必须分开：哨兵是 shader 直接写死的
        /// 绝对量（30000），没有被放大过，拿除完的数去比门限会把门限放大 100 倍。
        /// </summary>
        static void Measure(Color[] px, out float maxRel, out int sentinel, out int compared,
                            out int argIndex, out int argChannel)
        {
            maxRel = 0f;
            sentinel = 0;
            compared = 0;
            argIndex = -1;
            argChannel = -1;
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                if (!(m < k_SentinelGate))   // 这样写也能挡住 NaN/Inf
                {
                    sentinel++;
                    continue;
                }
                compared++;
                if (m > maxRel * k_RelScale)
                {
                    maxRel = m / k_RelScale;
                    argIndex = i;
                    argChannel = m == c.r ? 0 : (m == c.g ? 1 : 2);
                }
            }
        }

        /// <summary>
        /// half 在 v 附近的一个 ulp。用来回答「差了几个最小可表示单位」。
        /// 实现在 <see cref="VistaSelfTestNumerics"/>（#15 判据②也用它）。
        /// </summary>
        static float HalfUlp(float v) => VistaSelfTestNumerics.HalfUlp(v);
    }
}
