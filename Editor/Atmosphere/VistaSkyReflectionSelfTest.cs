using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// 天空镜面反射 cubemap 的自检。
    ///
    /// 与 <see cref="VistaAmbientShSelfTest"/> 分开一个文件的理由与那边分出来时相同：
    /// 失败原因不重叠。SH 那边验的是「投影 + Unity 的 SH 约定」，这边验三件事 ——
    /// 逐面方向约定（含 CopyTexture 的 element→CubemapFace 映射）、跨模块的尺度一致性、
    /// 以及 mip↔粗糙度这条与 URP 采样端的接口。
    ///
    /// 三条判据全部在 GPU 上算（见 SkyReflection.compute 的 SkyReflectionVerify），
    /// 这里只负责摆参数、读回、判阈值、把数字排成一份能贴进 CHANGELOG 的报告。
    /// C# 侧刻意**不重算**任何一条判据：重算就得再抄一遍 mip 映射与面方向约定，
    /// 而抄错的那一份会与 shader 里那份走歧 —— 那时自检报的偏差既不是 0
    /// 也不是明显错误，是最难查的那种失败。
    /// </summary>
    public static class VistaSkyReflectionSelfTest
    {
        [MenuItem("Window/Vista/Validate Sky Reflection", priority = 123)]
        public static void Run()
        {
            var sb = new StringBuilder();
            bool ok = Validate(sb);

            Debug.Log(("[Vista] 天空反射自检" + (ok ? "通过" : "**失败**") + "\n" + sb)
                      .Replace("\r", "").Replace("\n", "  |  "));
        }

        /// <summary>
        /// 打印**运行期**反射链路的实际状态。
        ///
        /// 与 <see cref="Run"/> 测的是完全不同的东西：那边走立即模式（自己建表、自己
        /// dispatch、阻塞读回），验的是数学；这里什么都不算，只看"真的有相机在渲时，
        /// RenderGraph 那两个 pass 有没有跑、cubemap 有没有真的挂到
        /// <c>RenderSettings.customReflectionTexture</c> 上"。
        /// 立即模式全绿而运行期没接通完全可能发生（pass 被剪、feature 没装、
        /// defaultReflectionMode 还留在 Skybox），而只看自检报告看不出来 ——
        /// 这与 <see cref="VistaAmbientShSelfTest.LogProbeState"/> 是同一类工具。
        /// </summary>
        [MenuItem("Window/Vista/Log Sky Reflection State", priority = 124)]
        public static void LogState()
        {
            var sb = new StringBuilder();
            sb.AppendLine("── 运行期反射链路状态");

            sb.Append("　 defaultReflectionMode = ").Append(RenderSettings.defaultReflectionMode)
              .Append("（预期 Custom）").AppendLine();

            var tex = RenderSettings.customReflectionTexture;
            sb.Append("　 customReflectionTexture = ")
              .Append(tex == null ? "(null)" : tex.name);

            var rt = tex as RenderTexture;
            if (rt != null)
            {
                sb.Append("　").Append(rt.width).Append('²')
                  .Append("　dim = ").Append(rt.dimension)
                  .Append("　mip = ").Append(rt.mipmapCount)
                  .Append("　fmt = ").Append(rt.graphicsFormat)
                  .Append("　filter = ").Append(rt.filterMode);
            }
            sb.AppendLine();

            // 只判"有没有接通"，不判像素值：数值正确性是 Run() 的职责，
            // 这里再比一遍就得复现一遍参考解，两份判据迟早走歧。
            bool linked = RenderSettings.defaultReflectionMode == DefaultReflectionMode.Custom
                          && rt != null
                          && rt.dimension == TextureDimension.Cube
                          && rt.mipmapCount == VistaAtmosphereLuts.k_SkyReflectionMipCount;
            sb.Append("　 判定：链路").Append(linked ? "已接通 OK" : "**未接通**").AppendLine();
            if (!linked)
                sb.AppendLine("　 排查顺序：feature 是否装进当前 Renderer → Sky Reflection 是否为 Off "
                            + "→ Frame Debugger 里有无 \"Vista Sky Reflection\" 与 "
                            + "\"Vista Sky Reflection Copy\" → 是否有 Game/SceneView 相机在渲。");

            // unity_SpecCube0_HDR 的残余风险（见 CHANGELOG #5b）：这张图是 float RT，
            // 引擎应当给出 (1,1,0,0) 让 DecodeHDREnvironment 变成恒等。这里报出
            // 实际值，省得靠"看反射亮度差一个常数倍"去猜。
            var hdr = Shader.GetGlobalVector("unity_SpecCube0_HDR");
            sb.Append("　 unity_SpecCube0_HDR = ").Append(hdr.ToString("F4"))
              .AppendLine("（预期 (1,1,0,0)：DecodeHDREnvironment 恒等）");

            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            sb.Append("　 活动场景 ").Append(string.IsNullOrEmpty(scene.name) ? "(未命名)" : scene.name)
              .Append("　isDirty = ").Append(scene.isDirty)
              .AppendLine("（RenderSettings 只在引用变化时写，这里不该长期为 true）");

            Debug.Log(("[Vista] " + sb).Replace("\r", "").Replace("\n", "  |  "));
        }

        // ==================================================================
        //  阈值
        // ==================================================================

        /// <summary>
        /// 逐面 round-trip 的阈值。mip0 在 α=0 时是**精确**镜面（SampleGGXDir 的
        /// cosθ ≡ 1，一个样本就是精确值），取样点又落在 stride 8 / offset 4 的纹素中心上
        /// 不涉及跨面滤波 —— 所以理论偏差只有 fp16 量化（相对 ~1e-3）。
        /// 1% 留一个数量级余量。放宽到 5% 会放过"某一面的 v 轴翻了"这类错
        /// （那种错在天空这种大范围渐变上未必产生大偏差）。
        /// </summary>
        const float k_FaceTolerance = 0.01f;

        /// <summary>
        /// cube 整球均值 vs LUT 同方向均值的阈值，**正午档**。实测 0.078%，留 6 倍余量。
        ///
        /// 残余来源是 cube 的 64² 角度离散化：Fibonacci 方向普遍落在纹素之间，cube 那边
        /// 吃的是双线性插值，而 LUT 那边是精确取值 —— 也就是说这一项量的是
        /// 「64² 这个分辨率把天空压掉了多少」。判据 1 已经证明纹素中心上两边一致到 0.04%，
        /// 所以这里的偏差只可能来自纹素**之间**。
        ///
        /// 正午卡得比日落紧一个数量级，不是双标：正午天空的角频率低，
        /// 双线性插值几乎无损，所以这一档留着当**回归探测器** —— 真出现 1% 级别的
        /// 整体缩放错（比如曝光被乘了两次），它会在这里红，而在日落档会被离散化误差淹掉。
        /// </summary>
        const float k_MeanToleranceNoon = 0.005f;

        /// <summary>
        /// 同上，**日落档**。实测 4.3%，留 1.4 倍余量。
        ///
        /// 为什么日落必须单独一档：实测 cube 在三个通道上分别偏暗 4.3% / 1.4% / 0.2%，
        /// 而且**一律偏暗**。这是线性插值在凸的亮带上系统性下冲的签名 ——
        /// 日落地平线那圈橙红是全天角频率最高的结构（64² 下每纹素约 1.4°），
        /// 它几乎全在红通道，蓝通道在日落时整片天空近乎平坦所以几乎不错。
        /// 缩放类的错会是三通道等比的，而且会连判据 1 一起红。
        /// 所以这 4.3% 是「64² 表示日落地平线的固有代价」，不是缺陷。
        ///
        /// 这个误差**只影响 mip0**（α=0 的镜面）。任何有粗糙度的表面读 mip≥1，
        /// 那里 GGX lobe 的张角远大于 1.4°，离散化被滤波宽度盖掉了。
        /// 而 mip0 只在完全光滑的物体上可见，且反射走的是间接高光 ——
        /// 对比之下驱动**漫反射**的那条链路（#5a 的 SH）在同一个太阳角上有 31% 的
        /// L2 截断误差，那个已经作为物理事实接受了。
        ///
        /// 没有取 PositiveInfinity（SH 自检日落档的做法）：那边的截断误差在特定法线上
        /// 无界，给不出有意义的上限；这边的离散化误差是有界的，留一个真阈值它才还是断言。
        /// </summary>
        const float k_MeanToleranceSunset = 0.06f;

        /// <summary>
        /// LUT 均值 vs SH 的 L_00·Y00 的阈值。这一项是**精确**的：两边用的是同一个
        /// 1024 点 Fibonacci 序列（VISTA_SKY_SH_SAMPLES == VISTA_SKY_REFL_VERIFY_MEAN_SAMPLES），
        /// 所以均值恒等式在有限样本下也精确成立。差异只来自浮点重排（两边的归约顺序不同）。
        /// 这条与 #5a 的"均值恒等式"是同一条，在这里重跑一遍是**跨模块**的交叉验证：
        /// 它一红就说明两个模块之一的整体缩放变了，而单看任何一边都看不出来。
        /// </summary>
        const float k_CrossModuleTolerance = 1e-3f;

        /// <summary>
        /// mip↔粗糙度 round-trip 的阈值。<c>VistaMipToPerceptualRoughness</c> 是
        /// URP <c>PerceptualRoughnessToMipmapLevel</c> 的解析反函数，round-trip 是
        /// 精确的（只差一次 sqrt 的舍入）。给到 1e-3 而不是 1e-6，是因为 fp32 上
        /// sqrt(2.89 − 2.8m/6) 在 m=6 附近分母很小。
        /// </summary>
        const float k_MipTolerance = 1e-3f;

        /// <summary>亮度下限。夜间整张天空都趋 0，相对误差没有意义。</summary>
        const float k_RadianceFloor = 1e-4f;

        static bool Validate(StringBuilder sb)
        {
            sb.AppendLine("── 反射 cubemap round-trip");

            var res = VistaRuntimeResources.Get();
            if (res == null || res.atmosphereLutCS == null)
            {
                sb.AppendLine("　 ✘ 取不到 atmosphereLutCS：当前管线不是 URP，或资源未导入。");
                return false;
            }
            if (res.skyReflectionCS == null)
            {
                sb.AppendLine("　 ✘ VistaRuntimeResources 里没有配 skyReflectionCS。");
                return false;
            }

            var p = VistaAtmosphereParameters.CreateEarth();
            var luts = new VistaAtmosphereLuts(res.atmosphereLutCS, res.skyReflectionCS);
            try
            {
                if (!luts.isSkyReflectionValid)
                {
                    sb.AppendLine("　 ✘ SkyReflectionFilter kernel 缺失（编译失败或被 only_renderers 排除）。");
                    return false;
                }
                // SH 也要备好：判据 2 的第三个数是 SH 的 L_00·Y00。
                if (!luts.isSkyAmbientShValid || !luts.PrepareSkyAmbientSh())
                {
                    sb.AppendLine("　 ✘ SH buffer 不可用，判据 2 的跨模块比对无法进行。");
                    return false;
                }
                if (luts.PrepareSkyReflection(VistaSkyReflectionMode.SkyViewLut)
                    != VistaSkyReflectionMode.SkyViewLut)
                {
                    sb.AppendLine("　 ✘ PrepareSkyReflection 没有返回 SkyViewLut（分配失败）。");
                    return false;
                }
                if (!luts.EnsureSkyReflectionVerify())
                {
                    sb.AppendLine("　 ✘ 自检缓冲分配失败（SkyReflectionVerify kernel 缺失？）。");
                    return false;
                }

                // 常量一致性只跟太阳无关，先单独跑一次判定，报告里排在最前 ——
                // 它一红，下面两档的数字全都不必看（size 传错的话逐面取样点都是错的）。
                bool ok = true;
                ok &= CompareAtSun(luts, p, 60f, "正午 60°", k_MeanToleranceNoon,
                                   checkConstants: true, sb);
                // 日落那一档才是逐面判据的真考验：地平线一圈橙红是全天角频率最高的地方，
                // 面与面的接缝正好横穿它。正午天空太平滑，方向约定错了也可能"看着不太错"。
                ok &= CompareAtSun(luts, p,  3f, "日落 3°",  k_MeanToleranceSunset,
                                   checkConstants: false, sb);
                return ok;
            }
            finally
            {
                luts.Dispose();
            }
        }

        static bool CompareAtSun(VistaAtmosphereLuts luts, VistaAtmosphereParameters p,
                                 float sunElevationDeg, string label, float meanTolerance,
                                 bool checkConstants, StringBuilder sb)
        {
            float rad = sunElevationDeg * Mathf.Deg2Rad;
            var sunDir = new Vector3(0f, Mathf.Sin(rad), Mathf.Cos(rad));
            // 2 m 人眼高度，与 LUT 自检和 SH 自检一致 —— 三份报告的数字才能横向对照。
            var view = VistaAtmosphereViewData.Create(p, new Vector3(0f, 2f, 0f), 0f, sunDir);

            // 一条 CommandBuffer 串起全部 dispatch + 那 6 次 CopyTexture。
            // 立即模式下资源状态转换由图形层自动插，所以 UAV 写完紧接着当 SRV 读是安全的；
            // RenderGraph 那边**必须**拆成两个 pass（compute + unsafe copy），
            // 而且 CopyTexture 只存在于 IUnsafeCommandBuffer —— 见 VistaAtmospherePass。
            var cmd = new CommandBuffer { name = "Vista Sky Reflection (SelfTest)" };
            luts.EnsureStaticLuts(cmd, p);
            luts.RenderSkyViewLut(cmd, view);
            luts.RenderSkyAmbientSh(cmd, view);
            luts.RenderSkyReflection(cmd, view, VistaSkyReflectionMode.SkyViewLut);
            luts.RenderSkyReflectionVerify(cmd, view);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            var rows = new Vector4[VistaAtmosphereLuts.k_ReflVerifyElementCount];
            luts.skyReflectionVerifyBuffer.GetData(rows);

            bool ok = true;

            // ---- 判据 1：逐面 round-trip（方位判据）----
            // 逐面报，不报一个全局最大值：约定错通常只错其中几个面，
            // 而现在它还兼管 CopyTexture 的 element→CubemapFace 映射 ——
            // 搬错面的症状就是"某两面互换"，只有逐面数字能指出来。
            const string k_FaceNames = "+X-X+Y-Y+Z-Z";
            float faceWorst = 0f;
            int faceWorstIdx = -1;
            int faceEmpty = 0;
            sb.Append("　 ").Append(label).Append("　逐面 |cube(mip0) − LUT|");
            for (int f = 0; f < 6; ++f)
            {
                Vector4 row = rows[VistaAtmosphereLuts.k_ReflVerifyRowFace + f];
                float maxErr = row.x, avgErr = row.y, count = row.z;
                if (count < 1f) faceEmpty++;
                if (maxErr > faceWorst) { faceWorst = maxErr; faceWorstIdx = f; }

                sb.Append("　").Append(k_FaceNames.Substring(f * 2, 2)).Append(' ')
                  .Append(maxErr.ToString("P2")).Append('/').Append(avgErr.ToString("P2"))
                  .Append('(').Append((int)count).Append(')');
            }
            sb.AppendLine();

            // 某一面一个取样点都没比上，说明那一面全黑 —— 而"最大偏差 0"看起来是满分，
            // 必须单独拦住。这正是 Cube UAV 那条路失败时会出现的形态（只有 face 0 有内容），
            // 虽然那条路现在已经换成 CopyTexture，判据留着不花钱。
            bool okFace = faceWorst < k_FaceTolerance && faceEmpty == 0;
            ok &= okFace;
            sb.Append("　　 判定：最大 ").Append(faceWorst.ToString("P2"))
              .Append("（面 ").Append(faceWorstIdx < 0 ? "-" : k_FaceNames.Substring(faceWorstIdx * 2, 2))
              .Append("）　空面 ").Append(faceEmpty).Append("/6 ")
              .Append(Mark(okFace)).AppendLine();

            // ---- 判据 2：均值恒等式（尺度判据，跨模块）----
            Vector3 cubeMean = rows[VistaAtmosphereLuts.k_ReflVerifyRowMean + 0];
            Vector3 lutMean  = rows[VistaAtmosphereLuts.k_ReflVerifyRowMean + 1];
            Vector3 shMean   = rows[VistaAtmosphereLuts.k_ReflVerifyRowMean + 2];

            float meanMag = Mathf.Max(lutMean.x, Mathf.Max(lutMean.y, lutMean.z));
            bool meanUsable = meanMag > k_RadianceFloor;
            // 两个独立的偏差，而不是一个"cube vs SH"：那一个数没法区分是 cube 错了
            // 还是 SH 错了。cube-vs-LUT 只含 cubemap 的离散化，LUT-vs-SH 只含 #5a
            // 的投影 —— 拆开之后哪一层坏了一眼可见。
            float cubeErr = meanUsable ? RelErr(cubeMean, lutMean, meanMag) : 1f;
            float crossErr = meanUsable ? RelErr(lutMean, shMean, meanMag) : 1f;

            bool okMean = meanUsable && cubeErr < meanTolerance;
            bool okCross = meanUsable && crossErr < k_CrossModuleTolerance;
            ok &= okMean && okCross;

            sb.Append("　　 整球均值　cube ").Append(Fmt(cubeMean))
              .Append("　LUT ").Append(Fmt(lutMean))
              .Append("　SH ").Append(Fmt(shMean)).AppendLine();
            sb.Append("　　 判定：cube 离散化 ").Append(cubeErr.ToString("P3"))
              .Append("／阈 ").Append(meanTolerance.ToString("P1")).Append(' ').Append(Mark(okMean))
              .Append("　跨模块（#5a 恒等式）").Append(crossErr.ToString("E2")).Append(' ').Append(Mark(okCross))
              .AppendLine();

            if (!checkConstants)
                return ok;

            // ---- 判据 3：mip↔粗糙度 round-trip + 常量导出（约定判据）----
            // 只在第一档跑：它与太阳方向无关，跑两遍只是把同样的数字打两次。
            float mipWorst = 0f;
            bool prMonotone = true;
            float prev = -1f;
            sb.Append("　 mip→pr→mip");
            for (int m = 0; m < VistaAtmosphereLuts.k_SkyReflectionMipCount; ++m)
            {
                Vector4 row = rows[VistaAtmosphereLuts.k_ReflVerifyRowMip + m];
                float pr = row.y, back = row.z, err = row.w;
                mipWorst = Mathf.Max(mipWorst, err);
                // 单调性是独立的一条：round-trip 对一个**常量**映射也会全绿
                // （pr 恒为 0.5 时反函数照样把它送回原 mip 吗？不会 —— 但一个
                // 写错方向的映射（pr = 1 − 真值）却能既单调又 round-trip 失败，
                // 反之亦然。两条一起才把这个映射钉住。）
                if (pr <= prev) prMonotone = false;
                prev = pr;
                sb.Append("　").Append(m).Append(':').Append(pr.ToString("F4"));
            }
            sb.AppendLine();

            Vector4 consts = rows[VistaAtmosphereLuts.k_ReflVerifyRowConst];
            // HLSL 侧的 SIZE / MIPS / LOD_STEPS / 本次传入的 size，逐个对 C# 侧那份。
            // 两边任何一边改了另一边没跟上，这里红，而不是等到看图发现"粗糙度高的
            // 反射不再变模糊"。
            bool okSize   = Mathf.Approximately(consts.x, VistaAtmosphereLuts.k_SkyReflectionSize);
            bool okMips   = Mathf.Approximately(consts.y, VistaAtmosphereLuts.k_SkyReflectionMipCount);
            bool okSteps  = Mathf.Approximately(consts.z, VistaAtmosphereLuts.k_SkyReflectionMipCount - 1);
            bool okParam  = Mathf.Approximately(consts.w, VistaAtmosphereLuts.k_SkyReflectionSize);
            bool okMip    = mipWorst < k_MipTolerance && prMonotone;
            ok &= okSize && okMips && okSteps && okParam && okMip;

            sb.Append("　　 HLSL 常量　SIZE ").Append((int)consts.x).Append(' ').Append(Mark(okSize))
              .Append("　MIPS ").Append((int)consts.y).Append(' ').Append(Mark(okMips))
              .Append("　LOD_STEPS ").Append((int)consts.z).Append(' ').Append(Mark(okSteps))
              .Append("　传入 size ").Append((int)consts.w).Append(' ').Append(Mark(okParam))
              .AppendLine();
            sb.Append("　　 判定：round-trip 最大 ").Append(mipWorst.ToString("E2"))
              .Append("　pr 单调 ").Append(prMonotone ? "OK" : "**FAIL**")
              .Append(' ').Append(Mark(okMip)).AppendLine();

            return ok;
        }

        /// <summary>
        /// 逐通道最大偏差 / <paramref name="scale"/>。分母统一用三通道最大值 ——
        /// 理由与 <see cref="VistaAmbientShSelfTest"/> 里那个同名函数相同：
        /// 日落时蓝通道趋 0，用它当分母会让自检长期红着，形同废掉。
        /// </summary>
        static float RelErr(Vector3 a, Vector3 b, float scale)
        {
            float d = Mathf.Max(Mathf.Abs(a.x - b.x),
                      Mathf.Max(Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z)));
            return d / scale;
        }

        static string Mark(bool ok) => ok ? "OK" : "**FAIL**";

        static string Fmt(Vector3 v) =>
            "(" + v.x.ToString("F1") + ", " + v.y.ToString("F1") + ", " + v.z.ToString("F1") + ")";
    }
}
