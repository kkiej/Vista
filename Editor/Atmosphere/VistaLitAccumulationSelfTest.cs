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
    /// VistaAtmosphereFeature 在场，也不会被时间/天气配置影响 ——
    /// 它是一条纯粹的回归判据，URP 升级时第一个该跑的就是它。
    /// 变体 A/B 的逐像素一致（#15 判据②）是另一件事，在另一个自检里。
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
            try
            {
                ok = Validate(sb);
            }
            finally
            {
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

            try
            {
                rt = new RenderTexture(k_Size, k_Size, 24,
                                       RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                readback = new Texture2D(k_Size, k_Size, TextureFormat.RGBAFloat, false, true);

                Build(shader, layer, rt, out root, out Camera cam, out var camData,
                      out mat, out Light[] pointLights);

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

                bool ok = true;

                // ── 判据 1：逐配置等价
                sb.AppendLine("── 判据 1：各关键字配置下，我的累加 ≡ UniversalFragmentPBR");
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
                    string numText = Mathf.Abs(num) < NumFloor
                        ? "≤ " + NumFloor.ToString("E3") + "（低于尺子地板，只能作上界）"
                        : num.ToString("E3") + "　= " + (num / ulp).ToString("F3") + " 个 half ulp";

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
                    // 只有一侧落地板 → 三档量的不是同一件事，且这一次不能用相对门限
                    // 说明（其中一侧没有可比的量级），单独成一条。
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
                }
                Shader.SetGlobalVector(s_InjectId, Vector4.zero);

                for (int t = 0; t < 4; t++)
                {
                    bool broken = float.IsNaN(detected[t]);
                    bool live = detected[t] > k_RelTol;

                    // 三态，不是两态：把「布景坏了」和「这一项没参与」分开报。
                    // 合成一个「未覆盖」会把一次真实故障说成一句无害的说明。
                    sb.Append("　 ")
                      .Append(broken ? "**布景坏** " : live ? "有分辨力 " : "未覆盖　  ")
                      .Append(termNames[t].PadRight(22))
                      .Append("注入 2% → 报出 ")
                      .Append(broken ? "（出现哨兵像素，本项无法判定）"
                            : detected[t] <= relFloor
                              ? "≤ " + relFloor.ToString("E3") + "（尺子地板，上界）"
                              : detected[t].ToString("E3"));
                    if (!broken && !live)
                        sb.Append("　← 这一项在本帧为 0 或占比低于 1%，"
                                + "所以它在判据 1 里的「通过」不可采信");
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
                          out Material mat, out Light[] pointLights)
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
            var lightGo = new GameObject("Sun") { hideFlags = HideFlags.HideAndDontSave };
            lightGo.transform.SetParent(root.transform, false);
            lightGo.layer = layer;
            var sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.4f;
            sun.shadows = LightShadows.Soft;
            lightGo.transform.localRotation = Quaternion.Euler(35f, 18f, 0f);

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
