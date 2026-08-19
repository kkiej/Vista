using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using SyncMode = Vista.Editor.VistaGpuTimer.SyncMode;
using Sample = Vista.Editor.VistaGpuTimer.Sample;

namespace Vista.Editor
{
    /// <summary>
    /// AP 全屏合成（变体 A）的性能测量 —— Task #15 的验收项 ④。
    ///
    /// ── 为什么不是「跑一次报一个 ms」 ──
    ///
    /// 一个全屏 pass 的耗时不是一个数，是一条直线。它有两个成分：
    ///   a  每个像素都要付的：全屏三角形、采样深度、判天空、（被 clip 的像素到此为止）
    ///   b  只有**非天空**像素才付的：两趟混合对颜色附件的读-改-写
    /// 报一个 ms 等于把某一次构图的天空占比偷偷编进结论里，换个镜头就不成立。
    /// 所以这里量的是 <c>ms = a + b · 覆盖面积</c> 这条线的两个系数 ——
    /// 有了它，任意构图的开销可以直接算出来，而不是再测一遍。
    ///
    /// 这条线同时把 shader 文件头里那句「天空占屏比例高的构图省掉的是真带宽」
    /// 变成可判定的：省下的正是 b·(天空面积)，而 b 是否显著大于噪声是判据 ③。
    /// 若 b 落在噪声里，那句话就是错的，注释得改。
    ///
    /// ── 判据全部由测量本身给出，没有一个是拍的 ──
    ///
    /// 这个 harness 刻意**不设**「1080p 必须低于 X ms」这类门。理由在
    /// CHANGELOG 里记过一次教训：给一条产出数据设通过门，会让门自己变成结论。
    /// 这里的四条判据都是**内部一致性**：线性性对比观测离散度、b 的分辨率折叠、
    /// b 是否超出噪声、以及换 N 之后每次开销是否不变。
    /// 带宽地板只**报告**、不判定，理由见 <see cref="ReportFloors"/>。
    ///
    /// ── 与真实帧的差别（必须一起引用）──
    ///
    ///   1) 口径是 <see cref="VistaGpuTimer"/> 的模型 B：吞吐，不是帧内延迟。
    ///      RenderGraph 在这个 pass 前后插的 barrier（深度拷贝 → 合成）不在里面，
    ///      那要由 <c>VistaLutGpuRecorderCrossCheck</c>（模型 A）给。
    ///   2) 深度来源是一张**手填的** R32 贴图，不是 URP 拷出来的深度。分辨率、
    ///      格式、采样器都对齐了，所以带宽是一样的；差别是它的内容由本文件决定 ——
    ///      这正是要的：覆盖率必须是自变量，不能受场景摆放摆布。
    ///   3) 没有 MSAA、没有 XR。两者都会改混合的样本数，属于本 harness 未覆盖的路径。
    /// </summary>
    public static class VistaApCompositePerf
    {
        // ==================================================================
        //  布景
        // ==================================================================

        struct Res
        {
            public int w, h, reps;
            public string tag;
        }

        /// <summary>
        /// 四档分辨率，全部 16:9 —— 宽高比固定，AP 的视锥四角才不用跟着换，
        /// 于是「换分辨率」这一个自变量是干净的。
        ///
        /// reps 的取法不是「越贵越少」这么随意，而是**让每项测量的 GPU 窗口大致等长**
        /// （这里约 25 ms）。等长有两个理由：
        ///   · 窗口太长（4K × 200 × 5 轮）会让 Editor 明显卡住；
        ///   · 窗口太短，Editor 自己的重绘抖动占比就变大，而
        ///     <see cref="k_MaxSpread"/> 判的是**相对**离散度 —— 窗口不等长的话
        ///     各行的离散度不可横向比较。
        /// 后半句是实测出来的：第一版给 720p 也用 200 次，总窗口只有 ~6 ms
        /// （另外三档是 21~26 ms），结果它的「覆盖 25%」离散度 36% 被判不可用，
        /// 而那一档的五个点本身是漂亮的直线（斜率 0.0249，与另外三档折叠得很好）。
        /// 换句话说被判掉的不是被测对象，是测量窗口太短。修法是加长窗口，
        /// **不是**放宽 k_MaxSpread —— 放宽容差去让一档通过就是自我实现的判据。
        ///
        /// 除以 reps 之后每次开销本该与 reps 无关，而「本该」需要证明 ——
        /// 判据 ④ 就是拿同一档配置在两个 N 下重测一遍来证明它。
        /// </summary>
        static readonly Res[] k_Res =
        {
            new Res { w = 1280, h =  720, reps = 800, tag = " 720p" },
            new Res { w = 1920, h = 1080, reps = 200, tag = "1080p" },
            new Res { w = 2560, h = 1440, reps = 120, tag = "1440p" },
            new Res { w = 3840, h = 2160, reps =  60, tag = "2160p" },
        };

        /// <summary>
        /// 覆盖率档位（非天空像素占比）。取五点而不是两点：两点必然拟合出一条直线，
        /// 「线性」就成了不可能失败的判据。五点才能让残差有意义。
        /// </summary>
        static readonly float[] k_Coverage = { 0f, 0.25f, 0.5f, 0.75f, 1f };

        const float k_FovY = 60f;
        const float k_Near = 0.1f;
        const float k_Far  = 45000f;
        const float k_CameraAltitudeM = 200f;
        const float k_SunElevationDeg = 60f;   // 与另外几份报告的正午档一致，可横向对照

        /// <summary>
        /// 覆盖区域内几何深度扫过的距离范围 (m)。
        ///
        /// 不给一个常量深度：那会让全屏所有像素落在 AP froxel 的同一片上，
        /// 3D 表的读取变成最理想的缓存命中，测出来的 b 会系统性偏低。
        /// 让它扫过整个距离区间更接近真实构图。常量深度那一档也测，
        /// 但只作为**缓存局部性的归因行**，不进拟合。
        /// </summary>
        const float k_GeoNearM = 20f;
        const float k_GeoFarM  = 30000f;

        /// <summary>归因行用的常量深度。取区间的几何中点。</summary>
        const float k_GeoConstM = 800f;

        /// <summary>
        /// 单点可用的离散度上限。与 <c>VistaAtmosphereLutProfiler</c> 里那条
        /// 「±25% 以上不判定、不引用」同一条规矩 —— 两份报告用同一把尺子，
        /// 数字才能横向对照。
        ///
        /// 这里它还多一个作用：判据 ② 的噪声上界只从**可用点**里取。
        /// 第一版没有这一层，结果 720p 有个 ±520% 的坏点把上界撑到 519.8%，
        /// 于是「斜率折叠」这条判据对任何极差都成立 —— 一条永远通过的判据
        /// 不是判据。坏点必须先被排除在外，而不是拿去当宽容度。
        /// </summary>
        const double k_MaxSpread = 0.25;

        /// <summary>
        /// 全局预热的重复次数。1080p 满覆盖约 0.1 ms/次，1000 次 ≈ 100 ms GPU。
        ///
        /// 为什么需要它，而 <see cref="VistaGpuTimer"/> 里每项各自的 20 次预热不够：
        /// 那 20 次只够把**这一项**的命令流跑热，付不掉整场第一次才有的那些
        /// 一次性成本 —— 两个 pass 的 PSO 创建、RT 首次分配、以及 GPU 从低功耗档
        /// 爬到高频。第一版没有这一层，代价是 720p（扫描表里的第一档）
        /// 的「覆盖 0%」测出 0.181 ms（另外三档同位置折算是 0.026 ms/Mpx 量级），
        /// 拟合出 −0.11 ms/Mpx 的**负斜率**。
        /// 预热之后还要把「第一次 vs 第二次」两个数都印出来 ——
        /// 那两个数就是这一层存在的实测理由，不印出来的话它看起来像多余的防御。
        /// </summary>
        const int k_WarmupReps = 1000;

        // ==================================================================
        //  带宽地板
        // ==================================================================

        /// <summary>RTX 3060：192-bit GDDR6 @ 15 Gbps = 360 GB/s（厂商标称）。</summary>
        const double k_DramGBs = 360.0;
        const int k_ColorBytesPerPx = 8;   // RGBA16F
        const int k_DepthBytesPerPx = 4;   // URP 拷出来的深度是单通道 32 位

        // ==================================================================
        //  给模型 A 用的线模型系数
        // ==================================================================

        /// <summary>
        /// 本机实测的两个折叠系数（四档均值，单次测量的极差见 CHANGELOG 的表：
        /// b 9.6%、a/全屏 4.5%）。
        ///
        /// 放在这里而不是抄进 <c>VistaLutGpuRecorderCrossCheck</c>：模型 A 要拿这条线
        /// 做包线对账，而同一个量有两份字面值的话，改了这边忘了那边，
        /// 表现是"两份报告的结论互相矛盾"而没人知道该信哪份。
        ///
        /// 换 GPU 必须重跑本菜单项重填这两个数 —— 它们是这台机器的系数，
        /// 只有 <c>ms = a + b·覆盖面积</c> 这个**形式**与机器无关。
        /// </summary>
        public const double k_ClippedMsPerMpx = 0.0266;   // a/全屏：每个像素都付的
        public const double k_CoveredMsPerMpx = 0.0245;   // b：只有非天空像素付的

        /// <summary>
        /// 给定**总像素面积**下本 pass 的包线：全天空构图的下界、满覆盖构图的上界。
        ///
        /// 只能给包线不能给一个数，因为耗时取决于构图的天空占比 ——
        /// 而模型 A 跑在真实场景上，那个占比不由测量方控制。
        /// 这正是包线的用法：模型 A 的值必须落在下界之上；超出上界的部分不可能由
        /// 构图解释（覆盖率只能把值往下拉），所以那一段可以归因给 barrier / pass 边界。
        ///
        /// 收**面积**而不是宽高，是因为模型对面积是线性的：一帧里多次渲染
        /// （Game View + Scene View）的包线就是各自面积之和的包线，
        /// 调用方把面积加起来传进来就行，不需要在自己那边再写一遍这两个乘法。
        /// </summary>
        public static void Bracket(double megapixels, out double floorMs, out double ceilMs)
        {
            floorMs = k_ClippedMsPerMpx * megapixels;
            ceilMs = (k_ClippedMsPerMpx + k_CoveredMsPerMpx) * megapixels;
        }

        [MenuItem("Window/Vista/Profile AP Composite (Perf)", priority = 128)]
        public static void Run()
        {
            var sb = new StringBuilder();
            bool ok;
            try
            {
                ok = Measure(sb);
            }
            catch (System.Exception e)
            {
                sb.Append("　 ✘ 抛异常：").Append(e.GetType().Name).Append(" ")
                  .AppendLine(e.Message);
                ok = false;
            }

            Debug.Log(("[Vista] AP 合成性能" + (ok ? "自洽" : "**不自洽/不可判定**") + "\n" + sb)
                      .Replace("\r", "").Replace("\n", "  |  "));
        }

        // ==================================================================
        //  测量主体
        // ==================================================================

        static bool Measure(StringBuilder sb)
        {
            var res = VistaRuntimeResources.Get();
            if (res == null || res.atmosphereLutCS == null || res.skyReflectionCS == null)
            {
                sb.AppendLine("　 ✘ 取不到大气计算核：当前管线不是 URP，或资源未导入。");
                return false;
            }
            if (res.aerialPerspectiveCompositeShader == null)
            {
                sb.AppendLine("　 ✘ VistaRuntimeResources 里没有配 aerialPerspectiveCompositeShader。");
                return false;
            }

            // GL 的裁剪空间 z 是 [-1,1]，填原始深度时要多一次重映射。
            // 合成 shader 的 only_renderers 里没有 GL（d3d11/vulkan/metal/主机/switch
            // 全是 [0,1]），所以这里不写那条分支，而是直接把它挡在门外 ——
            // 悄悄跑一个深度值全错的测量，比不跑更糟。
            var api = SystemInfo.graphicsDeviceType;
            if (api == GraphicsDeviceType.OpenGLCore || api == GraphicsDeviceType.OpenGLES3)
            {
                sb.Append("　 ✘ 后端是 ").Append(api)
                  .AppendLine("：本 harness 的原始深度按 [0,1] 裁剪空间填，GL 需要额外重映射，未实现。");
                return false;
            }

            var p = VistaAtmosphereParameters.CreateEarth();
            var apSettings = new VistaAerialPerspectiveSettings();
            var luts = new VistaAtmosphereLuts(res.atmosphereLutCS, res.skyReflectionCS);
            var material = CoreUtils.CreateEngineMaterial(res.aerialPerspectiveCompositeShader);

            VistaGpuTimer.Begin();
            try
            {
                if (!luts.isValid)
                {
                    sb.AppendLine("　 ✘ 大气核缺失。");
                    return false;
                }
                if (material == null)
                {
                    sb.AppendLine("　 ✘ 合成材质创建失败。");
                    return false;
                }

                // ---- 视图 / 投影 ----
                float aspect = 16f / 9f;
                var camPos = new Vector3(0f, k_CameraAltitudeM, 0f);
                // Unity 的相机空间是 -Z 朝前，所以 world→camera 要翻 Z。
                var viewM = Matrix4x4.TRS(camPos, Quaternion.identity,
                                          new Vector3(1f, 1f, -1f)).inverse;
                var projM = Matrix4x4.Perspective(k_FovY, aspect, k_Near, k_Far);
                var gpuProj = GL.GetGPUProjectionMatrix(projM, true);

                float tanHalfY = Mathf.Tan(k_FovY * 0.5f * Mathf.Deg2Rad);
                float sunRad = k_SunElevationDeg * Mathf.Deg2Rad;
                var sunDir = new Vector3(0f, Mathf.Sin(sunRad), Mathf.Cos(sunRad));

                var view = VistaAtmosphereViewData.Create(p, camPos, 0f, sunDir);
                view.SetFrustumRays(Vector3.forward, Vector3.right, Vector3.up,
                                    tanHalfY * aspect, tanHalfY);

                // ---- 一次性把 LUT 与全局 cbuffer 备好 ----
                // 这些不在被测窗口里：合成 pass 在真实帧里读到的也是别人产出的表。
                if (!luts.PrepareAerialPerspective(apSettings))
                {
                    sb.AppendLine("　 ✘ AP 表分配失败。");
                    return false;
                }
                var setup = new CommandBuffer { name = "Vista AP Perf Setup" };
                luts.EnsureStaticLuts(setup, p);
                view.Bind(new VistaImmediateLutDispatcher(setup, luts),
                          VistaAtmosphereLuts.k_SkyViewWidthDefault,
                          VistaAtmosphereLuts.k_SkyViewHeightDefault);
                view.BindAerialPerspective(new VistaImmediateLutDispatcher(setup, luts), apSettings);
                luts.RenderAerialPerspectiveLut(setup, view, apSettings);
                Graphics.ExecuteCommandBuffer(setup);
                setup.Release();

                bool fenceUsable = VistaGpuTimer.ProbeFence();
                var mode = fenceUsable ? SyncMode.Fence : SyncMode.Readback;

                Header(sb, mode, fenceUsable, apSettings);
                Warmup(sb, mode, material, viewM, projM, gpuProj);

                // ---- 逐分辨率 ----
                var fits = new Fit[k_Res.Length];
                for (int i = 0; i < k_Res.Length; ++i)
                    fits[i] = Sweep(sb, mode, material, viewM, projM, gpuProj, k_Res[i]);

                // ---- 判据 ----
                bool ok = true;
                ok &= JudgeLinearity(sb, fits);
                ok &= JudgeFolding(sb, fits);
                ok &= JudgeClipSaving(sb, fits);
                ok &= JudgeRepsIndependence(sb, mode, material, viewM, projM, gpuProj);

                ReportFloors(sb, fits);
                ReportModel(sb);
                return ok;
            }
            finally
            {
                VistaGpuTimer.End();
                CoreUtils.Destroy(material);
                luts.Dispose();
            }
        }

        static void Header(StringBuilder sb, SyncMode mode, bool fenceUsable,
                           VistaAerialPerspectiveSettings ap)
        {
            sb.Append("── 口径　模型 B（Edit 模式背靠背摊销 × ")
              .Append(VistaGpuTimer.k_DefaultTrials).AppendLine(" 轮取最小）");
            sb.Append("　 GPU ").Append(SystemInfo.graphicsDeviceName)
              .Append("　后端 ").Append(SystemInfo.graphicsDeviceType)
              .Append("　同步 ").Append(mode)
              .Append(fenceUsable ? "" : "（fence 探测未通过 → readback）")
              .AppendLine();
            sb.Append("　 AP 表 ").Append(ap.width).Append('×').Append(ap.height)
              .Append('×').Append(ap.depth).Append('/').Append(ap.distribution)
              .Append("　maxDistanceKm ").Append(ap.maxDistanceKm.ToString("F1"))
              .Append("　coloredTransmittance ").Append(ap.coloredTransmittance)
              .AppendLine();
            sb.Append("　 颜色 RGBA16F　深度 R32（手填）　FOV ").Append(k_FovY.ToString("F0"))
              .Append("°　相机海拔 ").Append(k_CameraAltitudeM.ToString("F0"))
              .Append(" m　覆盖区深度 ").Append(k_GeoNearM.ToString("F0"))
              .Append('~').Append(k_GeoFarM.ToString("F0")).AppendLine(" m 扫描");
        }

        // ==================================================================
        //  被测布景（颜色附件 + 手填深度 + 每次重复要重设的状态）
        // ==================================================================

        /// <summary>
        /// 一档分辨率的全套资源。抽成一个类而不是在三处各写一遍 ——
        /// 覆盖率扫描、全局预热、判据 ④ 都要同一套布景，
        /// 而三份拷贝一旦有一处的 <see cref="State"/> 少设一样东西，
        /// 表现就是「某个数字和另一个对不上」，没人看得出是哪份错。
        /// </summary>
        sealed class Rig : System.IDisposable
        {
            static readonly int s_DepthID = Shader.PropertyToID("_CameraDepthTexture");

            public readonly Res res;
            readonly RenderTexture m_Color;
            readonly Texture2D m_Depth;
            readonly float[] m_Pixels;
            readonly Matrix4x4 m_View, m_Proj, m_GpuProj;

            public Rig(Res r, Matrix4x4 viewM, Matrix4x4 projM, Matrix4x4 gpuProj)
            {
                res = r;
                m_View = viewM; m_Proj = projM; m_GpuProj = gpuProj;

                // 无深度附件：本 pass 是 ZTest Always / ZWrite Off，真实帧里也不附深度。
                m_Color = new RenderTexture(r.w, r.h, 0, RenderTextureFormat.ARGBHalf,
                                            RenderTextureReadWrite.Linear)
                {
                    name = "VistaApPerfColor",
                };
                m_Color.Create();

                m_Depth = new Texture2D(r.w, r.h, TextureFormat.RFloat, false, true)
                {
                    name = "VistaApPerfDepth",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                m_Pixels = new float[r.w * r.h];
            }

            public void SetCoverage(int geoRows, bool constDepth = false)
            {
                FillDepth(m_Pixels, res, geoRows, m_GpuProj, constDepth);
                m_Depth.SetPixelData(m_Pixels, 0);
                m_Depth.Apply(false, false);
            }

            /// <summary>
            /// 每次重复都要重设的状态。基线走的是**完全相同**的这一套、只是不画 ——
            /// 于是「实测 − 基线」里剩下的正好是两次 DrawProcedural，
            /// SetRenderTarget / 矩阵上传 / 绑深度这三件事被减干净了。
            /// </summary>
            public void State(CommandBuffer cmd)
            {
                cmd.SetRenderTarget(m_Color);
                cmd.SetViewProjectionMatrices(m_View, m_Proj);
                cmd.SetGlobalTexture(s_DepthID, m_Depth);
            }

            public void Dispose()
            {
                Object.DestroyImmediate(m_Depth);
                m_Color.Release();
                Object.DestroyImmediate(m_Color);
            }
        }

        /// <summary>
        /// 全局预热。理由与两个印出来的数字都在 <see cref="k_WarmupReps"/> 上。
        /// </summary>
        static void Warmup(StringBuilder sb, SyncMode mode, Material material,
                           Matrix4x4 viewM, Matrix4x4 projM, Matrix4x4 gpuProj)
        {
            using (var rig = new Rig(k_Res[1], viewM, projM, gpuProj))
            {
                rig.SetCoverage(k_Res[1].h);
                System.Action<CommandBuffer> rec = cmd => { rig.State(cmd); Draw(cmd, material); };
                double first  = VistaGpuTimer.RawMs(rec, k_WarmupReps, mode);
                double second = VistaGpuTimer.RawMs(rec, k_WarmupReps, mode);
                sb.Append("　 预热 1080p 满覆盖 ×").Append(k_WarmupReps)
                  .Append("：第一趟 ").Append(first.ToString("F1"))
                  .Append(" ms　第二趟 ").Append(second.ToString("F1"))
                  .Append(" ms　比 ")
                  .Append(second > 1e-6 ? (first / second).ToString("F2") : "——")
                  .AppendLine("×　→ >1 就是这一层存在的理由（PSO 创建 + 升频）");
            }
        }

        // ==================================================================
        //  一档分辨率的覆盖率扫描
        // ==================================================================

        struct Fit
        {
            public Res res;
            public double a;              // ms，覆盖面积为 0 时的截距
            public double b;              // ms / Mpx，覆盖面积的斜率
            public double residualMs;     // 五点对拟合线的最大偏离
            public double noiseMs;        // 五点里最大的 (max−min)
            public double totalMpx;
            public bool   valid;
            /// <summary>本档可用点里最差的相对离散度。判据 ② 的上界只由**入选档位**的它决定。</summary>
            public double maxSpread;
            /// <summary>拟合被跳过的原因。非空即"不可判定"，要印出来而不是静默丢弃。</summary>
            public string invalidReason;
            // 归因用
            public Sample full, mulOnly, addOnly, constDepth;
        }

        static Fit Sweep(StringBuilder sb, SyncMode mode, Material material,
                         Matrix4x4 viewM, Matrix4x4 projM, Matrix4x4 gpuProj, Res r)
        {
            var fit = new Fit { res = r, totalMpx = r.w * (double)r.h / 1e6 };

            using (var rig = new Rig(r, viewM, projM, gpuProj))
            {
                System.Action<CommandBuffer> state = rig.State;
                var baseline = Sample.Of(mode, state, r.reps);

                sb.Append("── ").Append(r.tag).Append("　N=").Append(r.reps)
                  .Append("　基线（只设状态不画）").Append(baseline.min.ToString("F3"))
                  .Append(" ms／").Append(r.reps).Append(" 次")
                  .AppendLine();

                var xs = new double[k_Coverage.Length];
                var ys = new double[k_Coverage.Length];

                for (int c = 0; c < k_Coverage.Length; ++c)
                {
                    int rows = Mathf.RoundToInt(k_Coverage[c] * r.h);
                    rig.SetCoverage(rows);

                    var s = Sample.Amortized(mode, cmd =>
                    {
                        state(cmd);
                        Draw(cmd, material);
                    }, baseline.min, r.reps);

                    xs[c] = rows * (double)r.w / 1e6;
                    ys[c] = s.min;
                    if (c == k_Coverage.Length - 1) fit.full = s;

                    // 可用性先判，再决定这个点能不能进拟合、能不能撑判据 ② 的上界。
                    // 顺序反过来（先记录再判）就是本项目记过的那个反模式：
                    // 拿坏点自己的离散度当宽容度，判据从此不可能失败。
                    bool usable = s.valid && s.spread <= k_MaxSpread;
                    if (usable)
                    {
                        fit.noiseMs = System.Math.Max(fit.noiseMs, s.max - s.min);
                        fit.maxSpread = System.Math.Max(fit.maxSpread, s.spread);
                    }
                    else if (fit.invalidReason == null)
                    {
                        fit.invalidReason = "覆盖 " + (k_Coverage[c] * 100f).ToString("F0")
                                          + "% 这一点" + (s.valid
                                              ? "离散度 " + s.spread.ToString("P0") + " 超过 "
                                                + k_MaxSpread.ToString("P0")
                                              : "测量无效");
                    }

                    sb.Append("　　 覆盖 ").Append((k_Coverage[c] * 100f).ToString("F0").PadLeft(3))
                      .Append("%（").Append(xs[c].ToString("F3")).Append(" Mpx）　")
                      .Append(s.Fmt())
                      .AppendLine(usable ? "" : "　⚠ 离散度过大，不可用");
                }

                // 满覆盖下的两趟拆分 + 常量深度归因。都不进拟合，只做归因。
                rig.SetCoverage(r.h);
                fit.mulOnly = Sample.Amortized(mode, cmd =>
                {
                    state(cmd);
                    cmd.DrawProcedural(Matrix4x4.identity, material,
                        VistaAerialPerspectiveCompositePass.k_PassMultiply, MeshTopology.Triangles, 3);
                }, baseline.min, r.reps);
                fit.addOnly = Sample.Amortized(mode, cmd =>
                {
                    state(cmd);
                    cmd.DrawProcedural(Matrix4x4.identity, material,
                        VistaAerialPerspectiveCompositePass.k_PassAdd, MeshTopology.Triangles, 3);
                }, baseline.min, r.reps);

                rig.SetCoverage(r.h, true);
                fit.constDepth = Sample.Amortized(mode, cmd =>
                {
                    state(cmd);
                    Draw(cmd, material);
                }, baseline.min, r.reps);

                sb.Append("　　 满覆盖拆分　仅乘 ").Append(fit.mulOnly.min.ToString("F3"))
                  .Append("　仅加 ").Append(fit.addOnly.min.ToString("F3"))
                  .Append("　两趟 ").Append(fit.full.min.ToString("F3"))
                  .Append(" ms　差 ")
                  .Append((fit.full.min - fit.mulOnly.min - fit.addOnly.min).ToString("+0.000;-0.000"))
                  .AppendLine("（正=两趟被串行化，负=单测各自吃到了重叠）");
                // 只报比值，不预设结论。这一行的初版写着「>1 说明 froxel 3D 表的缓存
                // 局部性是真实成本项」，而实测比值是 1.11/1.00/0.98/1.01 —— ≥1080p 上
                // 它根本量不到。把结论写进判定文本里，数字就再也否证不了它。
                sb.Append("　　 深度归因　扫描深度 ").Append(fit.full.min.ToString("F3"))
                  .Append("　常量深度 ").Append(fit.constDepth.min.ToString("F3"))
                  .Append(" ms　比 ")
                  .Append((fit.constDepth.min > 1e-6
                           ? (fit.full.min / fit.constDepth.min).ToString("F2") : "——"))
                  .AppendLine("　（>1 = froxel 3D 表的缓存局部性可测；≈1 = 量不到）");

                if (fit.invalidReason != null)
                {
                    sb.Append("　　 ⚠ 本档不拟合、不参与折叠：").AppendLine(fit.invalidReason);
                    return fit;
                }

                LeastSquares(xs, ys, out fit.a, out fit.b);
                double worst = 0.0;
                for (int c = 0; c < xs.Length; ++c)
                    worst = System.Math.Max(worst, System.Math.Abs(ys[c] - (fit.a + fit.b * xs[c])));
                fit.residualMs = worst;
                fit.valid = true;

                sb.Append("　　 拟合　ms = ").Append(fit.a.ToString("F4"))
                  .Append(" + ").Append(fit.b.ToString("F4")).Append(" × 覆盖Mpx")
                  .Append("　→ 截距/全屏 ").Append((fit.a / fit.totalMpx).ToString("F4"))
                  .Append(" ms/Mpx　斜率 ").Append(fit.b.ToString("F4")).Append(" ms/Mpx")
                  .AppendLine();
                sb.Append("　　 天空省下的比例　b·满覆盖/(a+b·满覆盖) = ")
                  .Append((fit.b * fit.totalMpx / (fit.a + fit.b * fit.totalMpx)).ToString("P1"))
                  .Append("　→ 全天空构图只付 ").Append(fit.a.ToString("F4")).Append(" ms")
                  .AppendLine();
                return fit;
            }
        }

        static void Draw(CommandBuffer cmd, Material material)
        {
            // 顺序与 VistaAerialPerspectiveCompositePass 里一致。这里量的是成本，
            // 顺序换了成本几乎不变 —— 但仍然照抄，因为一旦这里和运行时不同步，
            // 这份报告就不再是那个 pass 的报告了。
            cmd.DrawProcedural(Matrix4x4.identity, material,
                VistaAerialPerspectiveCompositePass.k_PassMultiply, MeshTopology.Triangles, 3);
            cmd.DrawProcedural(Matrix4x4.identity, material,
                VistaAerialPerspectiveCompositePass.k_PassAdd, MeshTopology.Triangles, 3);
        }

        // ==================================================================
        //  合成原始深度
        // ==================================================================

        /// <summary>
        /// 填一张原始设备深度。底部 <paramref name="geoRows"/> 行是几何，其余是天空。
        ///
        /// 天空值取远平面（反向 Z 下是 0）——这正是合成 shader 里 clip 的判据，
        /// 所以「覆盖率」这个自变量是通过**被测 shader 自己的分支**起作用的，
        /// 不是靠外部改画的区域。若哪天那个宏改了方向，这里会立刻表现为
        /// 覆盖率与耗时反相关，而不是悄悄测了个别的东西。
        /// </summary>
        static void FillDepth(float[] px, Res r, int geoRows, Matrix4x4 gpuProj, bool constDepth)
        {
            float sky = SystemInfo.usesReversedZBuffer ? 0f : 1f;
            geoRows = Mathf.Clamp(geoRows, 0, r.h);

            for (int y = 0; y < r.h; ++y)
            {
                float v;
                if (y < geoRows)
                {
                    float d;
                    if (constDepth)
                    {
                        d = k_GeoConstM;
                    }
                    else
                    {
                        // 对数扫描：froxel 的切片分布也是对数的，线性扫描会让
                        // 绝大多数行挤在最后几片上，达不到"铺开切片"的目的。
                        float t = geoRows > 1 ? y / (float)(geoRows - 1) : 0f;
                        d = k_GeoNearM * Mathf.Pow(k_GeoFarM / k_GeoNearM, t);
                    }
                    v = RawDepth(gpuProj, d);
                }
                else
                {
                    v = sky;
                }

                int row = y * r.w;
                for (int x = 0; x < r.w; ++x)
                    px[row + x] = v;
            }
        }

        /// <summary>
        /// 眼空间距离 → 原始设备深度。用投影矩阵算而不是自己套 near/far 公式：
        /// 反向 Z、平台差异、无穷远平面这三件事都已经折在 GetGPUProjectionMatrix 里，
        /// 手写一遍就是第二份实现，而两份实现只会在某个平台上悄悄分叉。
        /// </summary>
        static float RawDepth(Matrix4x4 gpuProj, float eyeDistanceM)
        {
            var clip = gpuProj * new Vector4(0f, 0f, -eyeDistanceM, 1f);
            return Mathf.Abs(clip.w) < 1e-9f ? 0f : clip.z / clip.w;
        }

        static void LeastSquares(double[] xs, double[] ys, out double a, out double b)
        {
            int n = xs.Length;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < n; ++i)
            {
                sx += xs[i]; sy += ys[i];
                sxx += xs[i] * xs[i]; sxy += xs[i] * ys[i];
            }
            double den = n * sxx - sx * sx;
            b = System.Math.Abs(den) < 1e-12 ? 0.0 : (n * sxy - sx * sy) / den;
            a = (sy - b * sx) / n;
        }

        // ==================================================================
        //  判据
        // ==================================================================

        /// <summary>
        /// ① 线性性。阈值不是拍的：拿**同一批测量自己的离散度**当尺子 ——
        /// 若五点对直线的最大偏离还不到单点 max−min，那"这是一条直线"与数据一致；
        /// 反过来若残差明显超出噪声，说明成本里有非线性项（比如某个分辨率
        /// 越过了 L2 容量），那时候 a/b 这个模型本身就不该用。
        /// </summary>
        static bool JudgeLinearity(StringBuilder sb, Fit[] fits)
        {
            bool ok = true;
            sb.AppendLine("── 判据 ①：ms 对覆盖面积线性（残差 ≤ 观测离散度）");
            foreach (var f in fits)
            {
                if (!f.valid)
                {
                    sb.Append("　 ").Append(f.res.tag).Append("　✘ 不可判定：")
                      .AppendLine(f.invalidReason ?? "无有效拟合");
                    ok = false; continue;
                }
                bool pass = f.residualMs <= f.noiseMs;
                ok &= pass;
                sb.Append("　 ").Append(f.res.tag)
                  .Append("　残差 ").Append(f.residualMs.ToString("F4"))
                  .Append(" ms　噪声 ").Append(f.noiseMs.ToString("F4"))
                  .Append(" ms　").AppendLine(pass ? "OK" : "✘ 残差超噪声：线性模型不成立");
            }
            return ok;
        }

        /// <summary>
        /// ② 斜率的分辨率折叠。b 的单位是 ms/Mpx，若它真是"每像素成本"，
        /// 四档分辨率必须给出同一个值。这是对整套测量最强的一条自检：
        /// 布景、深度填法、基线扣除里任何一处出错，都很难同时在四个分辨率上
        /// 错成同一个斜率。
        ///
        /// <paramref name="fits"/> 里被排除的档位既不进折叠、**也不给上界**：
        /// 上界只由入选档位自己的最差可用点离散度决定，见
        /// <see cref="k_MaxSpread"/> 上记的那条教训的推论 ——
        /// 一个被排除的档位若还能撑高上界，判据就又松了一层。
        /// </summary>
        static bool JudgeFolding(StringBuilder sb, Fit[] fits)
        {
            double lo = double.MaxValue, hi = double.MinValue, sum = 0, bar = 0;
            int n = 0;
            sb.AppendLine("── 判据 ②：斜率 b 的分辨率折叠（极差/均值 ≤ 入选档位最差可用点离散度）");
            foreach (var f in fits)
            {
                if (!f.valid)
                {
                    sb.Append("　 ").Append(f.res.tag).Append("　已排除：")
                      .AppendLine(f.invalidReason ?? "无有效拟合");
                    continue;
                }
                lo = System.Math.Min(lo, f.b);
                hi = System.Math.Max(hi, f.b);
                bar = System.Math.Max(bar, f.maxSpread);
                sum += f.b; ++n;
            }
            if (n < 2)
            {
                sb.AppendLine("　 ✘ 有效档位不足 2 个，无法折叠。");
                return false;
            }
            double mean = sum / n;
            double range = mean > 1e-9 ? (hi - lo) / mean : 1.0;
            bool pass = range <= bar;
            sb.Append("　 b ∈ [").Append(lo.ToString("F4")).Append(", ").Append(hi.ToString("F4"))
              .Append("] ms/Mpx　均值 ").Append(mean.ToString("F4"))
              .Append("　极差 ").Append(range.ToString("P1"))
              .Append("　入选最差离散度 ").Append(bar.ToString("P1"))
              .Append("（上限 ").Append(k_MaxSpread.ToString("P0")).Append("）　")
              .AppendLine(pass ? "OK" : "✘ 斜率不折叠：b 不是纯每像素成本");
            return pass;
        }

        /// <summary>
        /// ③ 天空 clip 省的是真带宽。判的是 b·满覆盖是否超出噪声 ——
        /// 这条判据存在的意义是它**能失败**：若两趟混合的 ROP 成本被别的开销淹没，
        /// 那 shader 文件头里"省掉的是真带宽"这句话就该删掉，而不是留在那里当理由。
        /// </summary>
        static bool JudgeClipSaving(StringBuilder sb, Fit[] fits)
        {
            bool ok = true;
            sb.AppendLine("── 判据 ③：覆盖项 b·满覆盖 显著大于噪声（否则天空 clip 不值一提）");
            foreach (var f in fits)
            {
                if (!f.valid) { ok = false; continue; }
                double covered = f.b * f.totalMpx;
                bool pass = covered > f.noiseMs;
                ok &= pass;
                sb.Append("　 ").Append(f.res.tag)
                  .Append("　覆盖项 ").Append(covered.ToString("F4"))
                  .Append(" ms　噪声 ").Append(f.noiseMs.ToString("F4"))
                  .Append(" ms　占总额 ")
                  .Append((covered / (f.a + covered)).ToString("P1"))
                  .Append("　").AppendLine(pass ? "OK" : "✘ 覆盖项落在噪声里");
            }
            return ok;
        }

        /// <summary>
        /// ④ 除以 N 之后与 N 无关。
        ///
        /// 这条不是形式主义。<see cref="VistaGpuTimer"/> 的类注释里点了一个真实风险：
        /// 同一张附件上背靠背混合，第 k 次读到的是第 k−1 次写出的值。
        /// 本 pass 的两趟合起来是 dst ← dst·T + S，这是个**压缩映射**（T &lt; 1），
        /// 于是 dst 会收敛到 S/(1−T) 这个有限不动点 —— 不会下溢到非规格化数、
        /// 也不会溢出到 Inf，所以逐次成本本该不变。
        /// 「本该」是推理；换一个 N 重测才是证明。若两个 N 给出不同的每次开销，
        /// 说明重复之间存在耦合，摊销口径对这个 pass 就不成立。
        ///
        /// 两个 N 取 400 与 100（4× 跨度）而不是 200 与 50：本判据必须让 N 变，
        /// 所以没法像 <see cref="k_Res"/> 那样把窗口拉齐，但可以让**两个窗口都够长**。
        /// 200/50 那一版的小 N 只有 ~5 ms 窗口，于是它自己的离散度被 Editor 抖动撑到
        /// 15.7%，而相对差 15.2% —— 擦着上界过，重跑一次很可能翻。
        /// 判据擦线通过等于没判，加长窗口才是修法。
        /// </summary>
        static bool JudgeRepsIndependence(StringBuilder sb, SyncMode mode, Material material,
                                          Matrix4x4 viewM, Matrix4x4 projM, Matrix4x4 gpuProj)
        {
            const int k_HiReps = 400;
            const int k_LoReps = 100;

            var r = k_Res[1];   // 1080p，满覆盖
            using (var rig = new Rig(r, viewM, projM, gpuProj))
            {
                rig.SetCoverage(r.h);
                System.Action<CommandBuffer> state = rig.State;

                sb.AppendLine("── 判据 ④：每次开销与摊销次数 N 无关（重复之间无耦合）");
                Sample a = At(k_HiReps), b = At(k_LoReps);
                double rel = a.min > 1e-6 ? System.Math.Abs(a.min - b.min) / a.min : 1.0;
                double bar = System.Math.Max(a.spread, b.spread);
                bool pass = a.valid && b.valid && rel <= bar;
                sb.Append("　 1080p 满覆盖　N=").Append(k_HiReps).Append(' ')
                  .Append(a.min.ToString("F4"))
                  .Append(" ms　N=").Append(k_LoReps).Append(' ').Append(b.min.ToString("F4"))
                  .Append(" ms　相对差 ").Append(rel.ToString("P1"))
                  .Append("　离散度上界 ").Append(bar.ToString("P1"))
                  .Append("　").AppendLine(pass ? "OK" : "✘ 与 N 相关：摊销口径对本 pass 不成立");
                return pass;

                Sample At(int reps)
                {
                    var bl = Sample.Of(mode, state, reps);
                    return Sample.Amortized(mode, cmd => { state(cmd); Draw(cmd, material); },
                                            bl.min, reps);
                }
            }
        }

        // ==================================================================
        //  报告
        // ==================================================================

        /// <summary>
        /// 带宽地板：只报告，**不判定**。
        ///
        /// 直觉上"实测必须 ≥ 理论地板"是个免费的一致性检查，但它不成立 ——
        /// 地板算的是 DRAM 流量，而 3060 有 3 MB L2。两趟之间深度贴图可能还留在
        /// L2 里，覆盖率低时颜色附件的活跃区域也可能装得下。也就是说实测**可以**
        /// 合法地低于地板，把它当判据会把缓存命中报成"尺子坏了"。
        /// 这正是本项目记过的那个反模式：把尺子自己的偏置当成被测对象的缺陷。
        /// </summary>
        static void ReportFloors(StringBuilder sb, Fit[] fits)
        {
            // 覆盖像素：两趟各一次颜色读+写，加两次深度读。
            double coveredBytes = 2.0 * (2.0 * k_ColorBytesPerPx) + 2.0 * k_DepthBytesPerPx;
            // 被 clip 的像素：只有两次深度读，ROP 完全跳过。
            double clippedBytes = 2.0 * k_DepthBytesPerPx;
            double bytesPerSec = k_DramGBs * 1e9;
            double bFloor = coveredBytes * 1e6 / bytesPerSec * 1e3;
            double aFloor = clippedBytes * 1e6 / bytesPerSec * 1e3;

            sb.Append("── 带宽参照（只报告不判定）　").Append(k_DramGBs.ToString("F0"))
              .Append(" GB/s　覆盖像素 ").Append(coveredBytes.ToString("F0"))
              .Append(" B/px → 地板 ").Append(bFloor.ToString("F4"))
              .Append(" ms/Mpx　clip 像素 ").Append(clippedBytes.ToString("F0"))
              .Append(" B/px → 地板 ").Append(aFloor.ToString("F4")).AppendLine(" ms/Mpx");
            foreach (var f in fits)
            {
                if (!f.valid) continue;
                sb.Append("　 ").Append(f.res.tag)
                  .Append("　b ").Append(f.b.ToString("F4"))
                  .Append(" ／地板 ").Append((f.b / bFloor).ToString("F2")).Append("×")
                  .Append("　a/全屏 ").Append((f.a / f.totalMpx).ToString("F4"))
                  .Append(" ／地板 ").Append((f.a / f.totalMpx / aFloor).ToString("F2"))
                  .AppendLine("×");
            }
            sb.AppendLine("　 <1× 不是错误：L2 有 3 MB，深度在两趟之间可能还在缓存里。"
                        + "这也是它不能当判据的原因。");
        }

        static void ReportModel(StringBuilder sb)
        {
            sb.AppendLine("── 模型说明（引用数字时必须一起给）");
            sb.AppendLine("　 1) 吞吐，不是帧内延迟。深度拷贝→合成这条边上的 barrier 不在里面，"
                        + "那要由 Play 模式的 ProfilerRecorder（模型 A）给。");
            sb.AppendLine("　 2) 深度是手填的 R32 贴图，分辨率/格式/采样器与 URP 拷出来的一致，"
                        + "内容由本文件决定 —— 覆盖率必须是自变量。");
            sb.AppendLine("　 3) 未覆盖：MSAA（改混合样本数）、XR 单趟立体（反投影不逐眼）、"
                        + "移动端 tile 架构（两趟在同一 RenderPass 内的收益量不到）。");
            sb.AppendLine("　 4) a/b 是这台机器这套配置的系数，换 GPU 要重测；"
                        + "但『ms = a + b·覆盖面积』这个形式与机器无关，可以直接换算构图。");
        }
    }
}
