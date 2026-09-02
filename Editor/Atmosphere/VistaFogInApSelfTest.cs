using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// #18 的验收：把雾并进 32³ AP LUT 的 march 之后，那条路径到底跑没跑过、跑对没跑对。
    ///
    /// ── 为什么不能挂在 <c>Validate Atmosphere LUTs</c> 里 ──
    ///
    /// 那条自检的布景是**晴空**（fog = null）。它的四段误差数字在 #18 前后逐位相同 ——
    /// 这正是 #18 想要的零态证明，但也意味着它对雾的代码路径**一个字节都没有覆盖**。
    /// 一个默认关闭、又没有判据覆盖的开关，等于一段永远不会被发现写错的代码。
    ///
    /// ── 视角必须倾斜（这条差点漏掉，记下来）──
    ///
    /// SliceError 核量的是**固定的中心柱** <c>uint2(size.x/2, size.y/2)</c>，
    /// 于是 uv = 16.5/32 = 0.515625。而 <c>VistaAtmosphereViewData.Create</c> 的兜底视锥
    /// 是正对 +Z 的 60°/16:9，那根柱子的 rayDir.y ≈ 0.0176 —— 近乎水平。
    /// 相机再放在 Y=0、雾底也在 0，则 <c>VistaFogHeightMeters</c> 在整个近场都 ≈ 0，
    /// 密度恒为 1；而 <c>VistaSegmentIntegral</c> 对**常密度是精确的**。
    /// 那样一来「标高剖面积得准不准」这条轴会静默变成空判据，errMid 贵在好看。
    /// 所以这里一律用公开的 <c>SetFrustumRays</c> 把视锥转起来，并且用
    /// 「视角① 与视角② 的注入量必须显著不同」这条**测量**来证明转真的生效了，
    /// 而不是断言「我设过了」。
    /// </summary>
    public static partial class VistaAtmosphereSelfTest
    {
        // ───────────────────────── 门槛 ─────────────────────────

        /// <summary>
        /// 判据0（分辨力）的门：雾开/关之间的差必须至少这么大。
        ///
        /// 取 0.10 = 判据1 那个 1~2% 韦伯门的 10 倍，理由和 #12 里那套 share 代数一样：
        /// 被注入的量必须**远大于**判据本身的门，读数才有分辨力。若注入量只有 2%
        /// 而判据门是 2%，「通过」就既可能是积分对了、也可能是雾根本没进 march。
        /// </summary>
        const float k_FogInjectMin = 0.10f;

        /// <summary>
        /// 天光环境项的覆盖门 (cd/m²)。
        ///
        /// <c>PrepareAerialPerspective</c> 只**分配并清零** SH buffer；不烘 SkyView + SH
        /// 的话 <c>s.fogAmbientRadiance</c> 恒为 0，雾的环境项那一行虽然执行了，
        /// 贡献却恒等于零 —— 「读数接近 0 的档位无法自证自己执行过」。
        ///
        /// 1E+003 → 1E+002（#18）。原来那个门是按**正午**晴空的 L̄ ≈ 5E+003 推的，
        /// 而布景里的视角④ 太阳只有 5°：实测 L̄ = 9.8E+002，比门低 2% —— 一条
        /// 「太阳低所以天光暗」的正确物理被判成了失败。这是自找的假失败：
        /// 门是拿布景里**最亮**的一个视角推的，却要去判布景里最暗的那个。
        ///
        /// 新门取 1E+002：比布景里最暗的读数（视角④ 的 9.8E+002）低一个数量级，
        /// 同时比「buffer 没烘」那个恒零态高两个数量级。这条判据要抓的是零/非零，
        /// 不是量级准不准 —— 后者由 <c>Validate Atmosphere LUTs</c> 里的 SH 判据管。
        /// </summary>
        const float k_FogAmbientMin = 1e2f;

        /// <summary>视角①② 注入量之差的门。见类注释里「视角必须倾斜」那段。</summary>
        const float k_FogViewSpreadMin = 0.05f;

        // ───────────────────────── 布景 ─────────────────────────

        struct FogView
        {
            public string name;
            public Vector3 cameraPos;
            /// <summary>正数 = 俯视。</summary>
            public float pitchDeg;
            public float sunElevDeg;
            /// <summary>true = 跑全部配置；false = 只跑标记为 signature 的几档。</summary>
            public bool full;
            /// <summary>true = 在这个视角上顺带跑判据2（零态 + 确定性对照）。</summary>
            public bool zeroState;
            /// <summary>这个视角已知超出档 D 切片包络时的**声明上限**（0 = 用正常门）。
            /// 单位与 errMid / errMidT 同（相对柱子总量 / 相对 T 自身）。
            ///
            /// ⚠ 这两个数是**回归基线**，不是画质门。它们放宽的只是「分布够不够用」
            /// 那一条断言（<c>固有 &lt; 上限</c>），「实现有没有错」那条恒等式断言
            /// 一分不放。放宽的代价会印在同一行里（「固有超门 n 片(已声明上限 X%，非画质达标)」），
            /// 所以「未判达标」不可能被读成「达标」。
            /// 档 D 的雾画质门在 #27（档 A vs 档 D 的逐像素差），不在这里；
            /// #19 近层 froxel 体落地后这两个字段必须删掉。</summary>
            public float capacityCeilingS, capacityCeilingT;
        }

        static readonly FogView[] k_FogViews =
        {
            // ① 最长光程：几乎水平的柱子在雾里走满 32 km，是最饱和的一档。
            new FogView { name = "① 贴地平视",       cameraPos = new Vector3(0f,   2f, 0f), pitchDeg =   0f, sunElevDeg = 60f },
            // ② 密度沿射线掉得最快：对步数预算与切片分布最苛刻，全套配置在这里跑。
            new FogView { name = "② 抬头20°",        cameraPos = new Vector3(0f,   2f, 0f), pitchDeg = -20f, sunElevDeg = 60f, full = true, zeroState = true },
            // ③ 密度沿射线**上升**（0.0025 → 1），覆盖 max(heightMeters,0) 的钳位分支
            //   与 rayDir.y 的负号。地面在 t = 300/(0.342·1000) = 0.877 km 处被打到，
            //   tMax 会钳 —— 已经确认正式核与参考解的 tMax 逻辑一致，所以这不会造假误差。
            //
            //   这个视角的切片分布**已知装不下**雾的拐点，所以带声明上限：
            //     拐点在 t = 300/(sin20°·1000) = 300/342 = 0.877 km（相机降到雾层顶）；
            //     Log 32 片在那里的片长 ≈ 0.269·877 = 236 m；
            //     雾沿射线的 e 折 = H/|dir.y| = 50/0.342 = 146 m（标高 50 m 那几档）
            //                                 或 20/0.342 = 58 m（标高 20 m 那几档）。
            //   片长是 e 折的 1.6~4.1 倍 —— 中点线性插值在这一片上必然差得离谱，
            //   而这与积分器无关：把参考解自己按同一个分布做中点插值，误差一样大。
            //   这正是 exact 这个量要回答的问题，也是 #19 存在的理由。
            //
            //   上限的数值是**实测基线 + 余量**（S 实测最差 19.46% @配置D，取 25%；
            //   T 实测最差 11.05% @配置C，取 15%），不是推导值 —— 这里拿不出更紧的
            //   解析上限：纯指数的中点插值误差是 cosh(Δ/2L)−1，Δ/L = 4.07 时算出 290%，
            //   而实测只有 19% —— 因为累积入散射在打到地面处**饱和**了，
            //   指数律在这一段是个远远不紧的上界。把 290% 写成门等于没有门。
            //   所以这两个数只承担「回归基线」这一个职责：它们变了就说明分布或积分器动了。
            new FogView { name = "③ 雾层上方俯视20°", cameraPos = new Vector3(0f, 300f, 0f), pitchDeg =  20f, sunElevDeg = 60f,
                          capacityCeilingS = 0.25f, capacityCeilingT = 0.15f },
            // ④ 太阳 5°：1/sin = 11.5 > grazingAmplifyMax 8，覆盖自遮蔽那条钳位。
            new FogView { name = "④ 低太阳5°平视",    cameraPos = new Vector3(0f,   2f, 0f), pitchDeg =   0f, sunElevDeg =  5f },
        };

        struct FogCase
        {
            public string label;
            public VistaFogSettings fog;
            /// <summary>true = 在视角①③④ 也跑一遍。</summary>
            public bool signature;
        }

        static VistaFogSettings FogMfp(float mfpMeters, float scaleHeightMeters, float g, bool selfShadow)
        {
            var f = new VistaFogSettings();
            f.mode = VistaFogSettings.Mode.AerialPerspective;
            f.densityInput = VistaFogSettings.DensityInput.MeanFreePath;
            f.meanFreePathMeters = mfpMeters;
            f.scaleHeightMeters = scaleHeightMeters;
            f.anisotropy = g;
            f.enableSunSelfShadow = selfShadow;
            return f;
        }

        static FogCase FogCaseOf(string label, VistaFogSettings f, bool signature)
        {
            return new FogCase { label = label, fog = f, signature = signature };
        }

        /// <summary>
        /// 手工挑的 9 档，不是叉乘。每一档都对应一条**说得出名字的**代码路径或物理边界：
        /// 叉乘会把跑测时间乘上去，却不会多覆盖任何一条分支。
        /// </summary>
        static FogCase[] MakeFogCases()
        {
            var visibility = new VistaFogSettings();
            visibility.mode = VistaFogSettings.Mode.AerialPerspective;
            visibility.densityInput = VistaFogSettings.DensityInput.Visibility;
            visibility.visibilityMeters = 200f;
            visibility.scaleHeightMeters = 50f;

            var infinite = FogMfp(1000f, float.PositiveInfinity, 0.8f, true);

            return new[]
            {
                // 三档密度：跨一个数量级的 σ_t（2.5 → 10 /km），看误差随光学厚度怎么走。
                FogCaseOf("A 薄雾 MFP1000/H50/g.8",   FogMfp(1000f, 50f, 0.8f, false), false),
                FogCaseOf("B 默认 MFP400/H50/g.8",    FogMfp( 400f, 50f, 0.8f, false), true),
                FogCaseOf("C 浓雾 MFP100/H50/g.8",    FogMfp( 100f, 50f, 0.8f, false), true),
                // H=20 m：fp32 在 6360 km 处 ulp ≈ 0.49 m，20 m 标高只剩 ~41 个台阶
                // —— 这个数字就是当初定「相机 Y 不做 km 缩放」那条的理由，必须有一档压上去。
                FogCaseOf("D 低雾 MFP400/H20/g.8",    FogMfp( 400f, 20f, 0.8f, false), true),
                // g=0：相位取常数，误差只能归因到密度剖面本身。
                FogCaseOf("E 等向 MFP400/H50/g0",     FogMfp( 400f, 50f, 0f,   false), false),
                // g=0.99：钳位边界。g=1 会让 HG 在 cosθ=1 处发散，0·inf = NaN。
                FogCaseOf("F 极前向 MFP400/H50/g.99", FogMfp( 400f, 50f, 0.99f, false), false),
                // 自遮蔽：#27 里那条「跑不出就删掉 VistaFogTransmittanceToSun」的预跑。
                FogCaseOf("G 自遮蔽 MFP400/H50/g.8",  FogMfp( 400f, 50f, 0.8f, true),  true),
                // 无限标高：密度恒 1，断言 CPU 把 _VistaFogHeight.w 强制成 0（FogMedium.hlsl:195）。
                FogCaseOf("H 无限标高 MFP1000/H∞",    infinite,   false),
                // 第二把标定尺：Koschmieder σ_t = 3912/V。V=200 m ⇒ 19.56 /km。
                FogCaseOf("I 能见度 V200m/H50",       visibility, false),
            };
        }

        // ───────────────────────── 入口 ─────────────────────────

        [MenuItem("Window/Vista/Validate Fog (AP + Sky)", priority = 140)]
        static void RunFogInApFromMenu()
        {
            VistaAtmosphereLuts luts = null;
            var report = RunFogInAp(VistaAtmosphereParameters.CreateEarth(), ref luts);
            string oneLine = report.text.Replace("\r", "").Replace("\n", "  |  ");
            if (report.passed) Debug.Log("[Vista] 雾自检通过（AP + 天空）  |  " + oneLine);
            else Debug.LogError("[Vista] 雾自检失败（AP + 天空）  |  " + oneLine);
            luts?.Dispose();
        }

        public static Report RunFogInAp(VistaAtmosphereParameters p, ref VistaAtmosphereLuts luts)
        {
            var res = VistaRuntimeResources.Get();
            if (res == null)
                return Fail("取不到 VistaRuntimeResources：当前管线不是 URP，或 Global Settings 尚未生成。");
            if (res.atmosphereLutCS == null)
                return Fail("atmosphereLutCS 为空：检查 Shaders/Atmosphere/AtmosphereLut.compute 是否已导入。");

            if (luts == null)
                luts = new VistaAtmosphereLuts(res.atmosphereLutCS);
            if (!luts.isValid)
                return Fail("compute 无效：kernel 未全部找到。");

            var settings = new VistaAerialPerspectiveSettings();
            if (!luts.PrepareAerialPerspective(settings))
                return Fail("PrepareAerialPerspective 失败：AP 的 3D 表没分配出来。");
            if (!luts.PrepareSkyAmbientSh())
                return Fail("PrepareSkyAmbientSh 失败：雾的环境项拿不到天光 SH。");

            luts.Invalidate();
            var warm = new CommandBuffer { name = "Vista Fog-in-AP static (SelfTest)" };
            luts.EnsureStaticLuts(warm, p);
            Graphics.ExecuteCommandBuffer(warm);
            warm.Release();

            var sb = new StringBuilder();
            bool ok = true;

            ok &= ValidateFogCpuPacking(sb);

            var cases = MakeFogCases();
            float[] injectByView = new float[k_FogViews.Length];

            for (int v = 0; v < k_FogViews.Length; ++v)
            {
                ok &= ValidateFogView(luts, p, settings, k_FogViews[v], cases,
                                      out injectByView[v], sb);
            }

            // 视锥真的转了吗 —— 用测量证明，不用断言。①②④ 相机/太阳/雾都可以一样，
            // ①与② 之间唯一的变量就是俯仰角；若 SetFrustumRays 没生效，两者会读出同一个数。
            float spread = Mathf.Abs(injectByView[0] - injectByView[1]);
            bool okSpread = spread >= k_FogViewSpreadMin;
            ok &= okSpread;
            sb.AppendLine(Mark(okSpread) + " 视锥生效　|注入① − 注入②| = " + Pct(spread)
                        + "（门 " + Pct(k_FogViewSpreadMin) + "；相等即 SetFrustumRays 没生效，"
                        + "那时全部标高判据都退化成常密度的空判据）");

            // ── 天空像素的雾（#18b，判据4~7）──
            // 放在同一次跑里而不是另开一个菜单项：两条路径共用同一组雾配置与同一批
            // 预热好的静态表，而「地平线上下必须接得上」正是它们唯一的耦合点。
            // 拆成两个入口就没法保证两边用的是同一组参数 —— 那时"接缝"这件事无人验证。
            ok &= ValidateSkyFog(luts, p, cases, sb);

            return new Report { passed = ok, text = sb.ToString() };
        }

        // ───────────────────────── 判据3：CPU 侧钳位与零态打包 ─────────────────────────

        /// <summary>
        /// shader 里有三处注释把责任推给 CPU，这里逐条兑现。纯 CPU、不碰 GPU，
        /// 但它们决定 GPU 上会不会出 NaN，所以必须在跑 GPU 之前先判。
        /// </summary>
        static bool ValidateFogCpuPacking(StringBuilder sb)
        {
            // ① g 的钳位。anisotropy 上的 [Range] 只管 Inspector，代码赋值不受约束；
            //    g = ±1 让 HG 在 cosθ = ±1 处发散，再乘上 0 密度就是 NaN。
            var wild = FogMfp(400f, 50f, 0.8f, false);
            wild.anisotropy = 1.5f;
            float gPacked = wild.packedAlbedo.w;
            bool okG = Mathf.Abs(gPacked - 0.99f) < 1e-6f;

            // ② 标高非有限时必须把自遮蔽开关按下去（FogMedium.hlsl:195）：
            //    那个闭式解拿 1/scaleHeight 当分母，无限标高下它没有意义。
            var inf = FogMfp(1000f, float.PositiveInfinity, 0.8f, true);
            Vector4 infH = inf.PackedHeight(2f);
            bool okInf = infH.y == 0f && infH.w == 0f;

            // ③ 关态 = 零态。三个 cbuffer 全零时 shader 侧 extinction/scattering 逐位为 0，
            //    于是「漏传一次 uniform」只可能表现为「没有雾」，不可能是「错的雾」。
            var off = new VistaFogSettings();
            bool okOff = off.packedAlbedo == Vector4.zero
                      && off.packedExtinct == Vector4.zero
                      && off.PackedHeight(2f) == Vector4.zero
                      && !off.enabled;

            bool ok = okG && okInf && okOff;
            sb.AppendLine(Mark(ok) + " 判据3 CPU 钳位　g:1.5→" + gPacked.ToString("F4") + Mark(okG)
                        + "　H=∞→(1/H=" + infH.y.ToString("F1") + ", 自遮蔽=" + infH.w.ToString("F1") + ")" + Mark(okInf)
                        + "　Off→三表全零" + Mark(okOff));
            return ok;
        }

        // ───────────────────────── 单个视角 ─────────────────────────

        static VistaAtmosphereViewData MakeFogView(VistaAtmosphereParameters p, in FogView v)
        {
            var view = MakeView(p, v.cameraPos, v.sunElevDeg);
            var rot = Quaternion.Euler(v.pitchDeg, 0f, 0f);
            view.SetFrustumRays(rot * Vector3.forward, Vector3.right, rot * Vector3.up,
                                Mathf.Tan(30f * Mathf.Deg2Rad) * (16f / 9f),
                                Mathf.Tan(30f * Mathf.Deg2Rad));
            return view;
        }

        static bool ValidateFogView(
            VistaAtmosphereLuts luts, VistaAtmosphereParameters p,
            VistaAerialPerspectiveSettings settings, in FogView v, FogCase[] cases,
            out float signatureInject, StringBuilder sb)
        {
            signatureInject = 0f;
            var view = MakeFogView(p, v);

            // SkyView → SH：不烘的话雾的环境项恒为 0，那条路就只是「跑过但读数恒 0」。
            var cmd = new CommandBuffer { name = "Vista Fog-in-AP sky (SelfTest)" };
            luts.RenderSkyViewLut(cmd, view);
            luts.RenderSkyAmbientSh(cmd, view);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            var moments = new Vector4[VistaAtmosphereLuts.k_ShCoeffCount];
            luts.skyAmbientShBuffer.GetData(moments);
            // 常数取运行时那份，不在这里再写一遍 0.2820948 —— shader 的 VISTA_SH_Y0、
            // 运行时的 k_Y0、自检里的字面量，三份里只要漂一份，症状就是「环境项差个常数
            // 但每一处看起来都对」。
            float ambient = (moments[0].x + moments[0].y + moments[0].z) / 3f
                          * VistaSphericalHarmonics.k_Y0;
            bool okAmbient = ambient >= k_FogAmbientMin;

            sb.AppendLine("　── " + v.name + "　cam" + v.cameraPos.ToString("F0")
                        + " 俯仰" + v.pitchDeg.ToString("F0") + "° 太阳" + v.sunElevDeg.ToString("F0") + "°　"
                        + Mark(okAmbient) + " 天光 L̄ = " + ambient.ToString("E1") + " cd/m²"
                        + "（门 " + k_FogAmbientMin.ToString("E1") + "）");

            bool ok = okAmbient;

            // 关态基线：判据0 的减数，同时也是判据2 的一个操作数。
            var baseScat = BakeFogAp(luts, view, settings, null, p, out _);
            FogColumn(baseScat, 1f / Mathf.Max(view.exposure, 1e-30f),
                      out float[] baseS, out float[] baseT);

            if (v.zeroState)
                ok &= ValidateFogZeroState(luts, view, settings, p, baseScat, sb);

            for (int i = 0; i < cases.Length; ++i)
            {
                if (!v.full && !cases[i].signature) continue;
                ok &= MeasureFogCase(luts, view, settings, p, cases[i], baseS, baseT,
                                     v.capacityCeilingS, v.capacityCeilingT,
                                     out float inject, sb);
                if (cases[i].label[0] == 'B')
                    signatureInject = inject;
            }
            return ok;
        }

        // ───────────────────────── 判据2：零态 + 确定性对照 ─────────────────────────

        /// <summary>
        /// <c>Mode.Off</c> 必须与 <c>fog = null</c> 在整张 32³ 表上逐位相同。
        ///
        /// 光有这一条不够：读回本身若是常量，两次读回自然也相同 —— 那时这个 0 什么都不证明。
        /// 所以配一条「null 连烘两遍」的确定性对照。三条读数放在一起才是完整论证：
        /// 判据0 非零（读回跟得上变化）+ 对照为零（同输入可复现）+ 本条为零（Off ≡ null）。
        /// </summary>
        static bool ValidateFogZeroState(
            VistaAtmosphereLuts luts, in VistaAtmosphereViewData view,
            VistaAerialPerspectiveSettings settings, VistaAtmosphereParameters p,
            Volume nullVol, StringBuilder sb)
        {
            var ctrlVol = BakeFogAp(luts, view, settings, null, p, out _);
            var offVol  = BakeFogAp(luts, view, settings, new VistaFogSettings(), p, out _);

            float ctrl = VolumeMaxAbsDiff(nullVol, ctrlVol);
            float diff = VolumeMaxAbsDiff(nullVol, offVol);

            bool ok = ctrl == 0f && diff == 0f;
            sb.AppendLine(Mark(ok) + " 判据2 零态　Off vs null 全表 max|Δ| = " + diff.ToString("E3")
                        + "　对照 null×2 = " + ctrl.ToString("E3") + "（两者都必须是 0）");
            return ok;
        }

        // ───────────────────────── 判据0 + 判据1 ─────────────────────────

        static bool MeasureFogCase(
            VistaAtmosphereLuts luts, in VistaAtmosphereViewData view,
            VistaAerialPerspectiveSettings settings, VistaAtmosphereParameters p,
            in FogCase c, float[] baseS, float[] baseT,
            float capacityCeilingS, float capacityCeilingT, out float inject, StringBuilder sb)
        {
            var scat = BakeFogAp(luts, view, settings, c.fog, p, out ApCurve curve);

            // ── 判据0：分辨力 ──
            // dS 拿 ReduceApCurve 已经在用的那个日照参考白当分母（albedo 0.3 的漫反射面，
            // ≈1.1E+004 cd/m²），于是读数直接是「画面亮了百分之几」；dT 是 [0,1] 上的量，
            // 绝对差就是相对可见度。两个通道都要看，因为近处是 T 主导、远处是入散射主导。
            // 报的是两个**导出量之差**，不是两个原始读数之比 —— 后者在分母趋零时会自己造结论。
            //
            // ---- 为什么判据量是逐片的**和**，不是 max(dS, dT)（#18 改）----
            // 合成式是 final = geo·T + S。同一个像素上雾干了两件事：把背景乘掉一点、
            // 再加进来一点。以参考白当背景归一化后，这个像素的相对变化就是 dT + dS ——
            // 两项本来就同分母、同时作用，取 max 等于**只承认其中一半**。
            // 这不是把门放宽：漏掉的那一半是真实存在的可见变化，
            // 少算它的症状是「视角③ 配置D 读 0.106、门 0.100」这种压线，
            // 判定由打印出来的最后一位小数决定 —— 本项目点过名的坑。
            // 和法之后同一档读 ~0.18，余量 80%。
            // 注意是**逐片求和再取 max**，不是 max(dS)+max(dT)：后者是上界，
            // 而这里的门是下界判据（inject ≥ 门），拿上界去过下界的门方向是错的
            // —— 两个峰值若落在不同切片上，就会通过一条实际上没有哪一片满足的判据。
            FogColumn(scat, 1f / Mathf.Max(view.exposure, 1e-30f),
                      out float[] s, out float[] t);
            float dS = 0f, dT = 0f;
            inject = 0f;
            for (int z = 0; z < s.Length; ++z)
            {
                float zS = Mathf.Abs(s[z] - baseS[z]) / Mathf.Max(curve.visibleWhite, 1e-6f);
                float zT = Mathf.Abs(t[z] - baseT[z]);
                dS = Mathf.Max(dS, zS);
                dT = Mathf.Max(dT, zT);
                inject = Mathf.Max(inject, zS + zT);
            }
            bool okInject = inject >= k_FogInjectMin;

            // ── 数值健全性 ──
            // g=0.99 那一档专门压着 HG 的发散边界；NaN 会让下面所有比较静默变成 false，
            // 「通过」和「读数是 NaN」在报表上必须长得不一样。
            bool okSane = VolumeFinite(scat, out float maxAbs);

            // ── 判据1：自适应步长 march vs 4096 步同物理参考解 ──
            // 阈值直接复用 AP 那三个（5% / 2% / 1%），不为雾另开一套：
            // 换尺子就没法和 #7 的晴空读数横向比，而「雾开了之后误差涨了多少」正是要看的东西。
            //
            // ---- errMid / errMidT 为什么拆成两条断言（#18 改）----
            // 雾把 AP 表推到了它的表达能力边界上：视角③（相机 300 m 俯视 20°）的雾拐点
            // 在 877 m，而 Log 32 片在那里的片长是 236 m、雾沿射线的 e 折只有 58~146 m ——
            // 中景段读到 19.25% 不是 march 积错了，是**这个切片分布装不下那条曲线**。
            // 平门把这两种情况判成同一个失败，而它们导向的行动完全不同
            // （前者 → 去查积分器；后者 → #19 近层雾体，或者接受档 D 的已知上限）。
            //
            // ApBandText 现在逐片把实测误差按三角不等式**分解**成两项：
            //     实测 ≤ 固有（4096 步参考解自己在这个分布上的中点插值误差）
            //          ＋ 端点行进误差的贡献上界（已由 errCenter 单独设门）
            // 于是两条各自可失败的断言：
            //     实现 —— 实测 ≤ 固有 + 端点 + 存储圆整允差。这是恒等式，只会被实现错误破。
            //     分布 —— 固有 < 上限。固有**一个 LUT 值都不含**，所以改被测代码骗不过它。
            // 视角③ 带显式声明的 capacityCeiling（见 FogView 的注释），代价印在同一行。
            // 推导、以及为什么不用曲率做泰勒预测（#18 走过的弯路），见 FillApMidDecomposition。
            bool okC = curve.maxErrCenter < k_ApErrCenterMax;
            string midText  = ApBandText(curve, false, k_ApErrMidMax,
                                         Mathf.Max(capacityCeilingS, k_ApErrMidMax),  out bool okMid);
            string midTText = ApBandText(curve, true,  k_ApErrMidTMax,
                                         Mathf.Max(capacityCeilingT, k_ApErrMidTMax), out bool okMidT);

            // refT 的最小值：errMidT 的分母在核里有 1e-4 的地板。浓雾把 refT 压到地板附近时，
            // errMidT 就变成一个**由地板决定**的数字，与真实误差无关。#18 起 ReduceApCurve
            // 会按 |ΔT| < k_ApVisibleDeltaT 把这些片豁免掉（推导见那个常量的注释），
            // 但 refT↓ 与豁免片数仍然都要报：不报的话「T 全绿」既可能是真的准，
            // 也可能是整段被豁免了，而这两件事对下一步的决策完全不同。
            float refTMin = float.MaxValue;
            for (int z = 0; z < curve.sampleCount; ++z)
                refTMin = Mathf.Min(refTMin, curve.refT[z]);

            // (3,0,z) 三通道互验。雾这条路径上 refT 会被压到很小，正是打包错位最容易
            // 被误读成"物理误差"的场合 —— 先把"读的是不是同一个纹素的三个量"钉住。
            bool okPack = curve.packMaxResidual < k_ApPackResidualMax;

            bool ok = okInject && okSane && okC && okMid && okMidT && okPack;
            sb.AppendLine(Mark(ok) + " " + c.label
                        + "　注入 " + Pct(inject) + Mark(okInject)
                        + "(dS " + Pct(dS) + " dT " + dT.ToString("F3") + ")"
                        + "　errC " + Pct(curve.maxErrCenter) + "@" + curve.atErrCenterKm.ToString("F2") + Mark(okC)
                        // 带上 ref/lut 的绝对值：errC 只给了差的大小，给不出**方向**。
                        // 而方向恰好能分开两种完全不同的病因：lut > ref 意味着尺子读少了
                        // （参考解自己没解析出雾），lut < ref 才是被测对象漏了能量。
                        + "(ref " + curve.centerRef.ToString("E2") + " lut " + curve.centerLut.ToString("E2") + ")"
                        + "　S:" + midText
                        + "　T:" + midTText
                        + "　refT↓" + refTMin.ToString("E2")
                        + "　ΔT免检 " + curve.exemptT + "/" + curve.sampleCount
                        + "(max " + curve.exemptMaxDeltaT.ToString("E1") + ")"
                        + "　打包残差 " + curve.packMaxResidual.ToString("E1") + Mark(okPack)
                        // maxAbs 读的是**表里存的**预曝光值（#18 起），不解码 ——
                        // 这一项的用途就是盯 fp16 的 65504 上限，而 fp16 装的正是存储值。
                        // 解码后再报会让「6.55E+004 = 恰好撞上限」这条一眼可辨的证据消失。
                        + "　max|表存| " + maxAbs.ToString("E2") + Mark(okSane));
            return ok;
        }

        // ───────────────────────── 读回工具 ─────────────────────────

        /// <summary>
        /// 烘一次正式 AP 表 + 一次 SliceError，读回散射柱与误差柱。
        ///
        /// 顺序是被约束的：SliceError 要把正式表当 SRV 读，所以必须先烘正式表；
        /// 两趟必须传**同一份** fog，否则量的是「有雾的 LUT 对无雾的参考解」，
        /// 那个数字会大得像切片崩了，但原因完全不在切片上。
        /// </summary>
        static Volume BakeFogAp(
            VistaAtmosphereLuts luts, in VistaAtmosphereViewData view,
            VistaAerialPerspectiveSettings settings, VistaFogSettings fog,
            VistaAtmosphereParameters p, out ApCurve curve)
        {
            var cmd = new CommandBuffer { name = "Vista Fog-in-AP (SelfTest)" };
            luts.RenderAerialPerspectiveLut(cmd, view, settings, fog);
            luts.RenderApSliceError(cmd, view, settings, fog);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            // SliceError 只写透射率表的 (0,0,z) / (3,0,z)，不动散射表，所以两张都能在这之后读。
            var scat = Readback3D(luts.apScatterLut);
            curve = ReduceApCurve(Readback3D(luts.apTransmittanceLut), settings, p, view.exposure);
            return scat;
        }

        /// <summary>取 SliceError 量的那根中心柱：灰度入散射 + 灰度透射率。</summary>
        /// <param name="decode">散射表存的是预曝光值（#18，见 AerialPerspectiveLut），
        /// 乘 1/exposure 解回绝对 cd/m²。必须解：dS 的分母 visibleWhite 是绝对量，
        /// 两边单位不一致的话 dS 会整体小 4 万倍，而症状是「判据0 全线不通过」——
        /// 长得完全像「雾没接上」，是最容易误导的形态。透射率无量纲，不动。</param>
        static void FogColumn(Volume v, float decode,
                              out float[] scatter, out float[] transmittance)
        {
            int x = v.width / 2, y = v.height / 2;
            scatter = new float[v.depth];
            transmittance = new float[v.depth];
            for (int z = 0; z < v.depth; ++z)
            {
                Color c = v[x, y, z];
                scatter[z] = (c.r + c.g + c.b) / 3f * decode;
                transmittance[z] = c.a;
            }
        }

        static float VolumeMaxAbsDiff(Volume a, Volume b)
        {
            float worst = 0f;
            for (int z = 0; z < a.depth; ++z)
                for (int y = 0; y < a.height; ++y)
                    for (int x = 0; x < a.width; ++x)
                    {
                        Color ca = a[x, y, z], cb = b[x, y, z];
                        worst = Mathf.Max(worst, Mathf.Abs(ca.r - cb.r));
                        worst = Mathf.Max(worst, Mathf.Abs(ca.g - cb.g));
                        worst = Mathf.Max(worst, Mathf.Abs(ca.b - cb.b));
                        worst = Mathf.Max(worst, Mathf.Abs(ca.a - cb.a));
                    }
            return worst;
        }

        /// <summary>整张表有限且非负。NaN 会让所有比较静默变 false，必须单独点名。</summary>
        static bool VolumeFinite(Volume v, out float maxAbs)
        {
            maxAbs = 0f;
            bool ok = true;
            for (int z = 0; z < v.depth; ++z)
                for (int y = 0; y < v.height; ++y)
                    for (int x = 0; x < v.width; ++x)
                    {
                        Color c = v[x, y, z];
                        for (int ch = 0; ch < 4; ++ch)
                        {
                            float f = c[ch];
                            if (float.IsNaN(f) || float.IsInfinity(f) || f < 0f) ok = false;
                            else maxAbs = Mathf.Max(maxAbs, f);
                        }
                    }
            return ok;
        }
    }
}
