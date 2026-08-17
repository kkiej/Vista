using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// 天空 SH 环境光的自检。
    ///
    /// 与 <see cref="VistaAtmosphereSelfTest"/> 分开一个文件：那边验的是 LUT 本身的
    /// 数值正确性（纯 GPU 读回），这边验的是**投影与对接**——SH 系数、Unity 的
    /// <c>SphericalHarmonicsL2</c> 归一化约定、以及"辐照度重建对不对"。
    /// 两者的失败原因完全不重叠，混在一起会让报告读不出是哪一层坏了。
    /// </summary>
    public static class VistaAmbientShSelfTest
    {
        [MenuItem("Window/Vista/Validate Ambient SH", priority = 121)]
        public static void Run()
        {
            var sb = new StringBuilder();
            bool ok = ProbeUnityConvention(sb);
            // 约定标定放前面、辐照度比对放后面，是因为后者的判定**依赖**前者：
            // 缩放表错了的话，辐照度那一项也会红，但根因在标定这一层。
            // 顺序固定，报告就能从上往下读出"最先坏的是哪一层"。
            ok &= ValidateIrradiance(sb);

            Debug.Log(("[Vista] SH 环境光自检" + (ok ? "通过" : "**失败**") + "\n" + sb)
                      .Replace("\r", "").Replace("\n", "  |  "));
        }

        /// <summary>
        /// 标准实数球谐基（Unity 的槽位顺序）在方向 d 上的取值。
        /// 转发到运行时那一份 —— 自检**必须**验线上用的常量，自己再抄一份就等于验自己。
        /// </summary>
        internal static void ShBasis(Vector3 d, float[] y) => VistaSphericalHarmonics.Basis(d, y);

        /// <summary>
        /// 打印**运行期**环境光链路的实际状态。
        ///
        /// 与上面那个自检测的是完全不同的东西：<see cref="Run"/> 走立即模式
        /// （自己建 LUT、自己 dispatch、阻塞读回），验的是数学；这里什么都不算，
        /// 只看"真的有相机在渲时，RenderGraph 那条 pass 有没有跑、
        /// <c>AsyncGPUReadback</c> 有没有把系数灌进 <c>RenderSettings</c>"。
        /// 立即模式全绿而运行期是黑的，这种情况完全可能发生（pass 被剪、
        /// feature 没装、读回请求从没完成），而只看自检报告是看不出来的。
        ///
        /// 顺手报一下场景脏标记：逐帧写 <c>RenderSettings.ambientProbe</c> 若会把场景
        /// 标脏，Editor 里就会一直挂着 * 号、且每次保存都产生无意义的 diff ——
        /// 那就得给导出加门控。
        /// </summary>
        [MenuItem("Window/Vista/Log Ambient Probe State", priority = 122)]
        public static void LogProbeState()
        {
            var sb = new StringBuilder();
            sb.AppendLine("── 运行期环境光链路状态");

            sb.Append("　 ambientMode = ").Append(RenderSettings.ambientMode)
              .Append("（预期 Custom）").AppendLine();

            var probe = RenderSettings.ambientProbe;
            var c0 = new Vector3(probe[0, 0], probe[1, 0], probe[2, 0]);
            sb.Append("　 ambientProbe c_0 = ").Append(Fmt(c0))
              .Append("　L1(y) = ").Append(Fmt(new Vector3(probe[0, 1], probe[1, 1], probe[2, 1])))
              .AppendLine();

            // 上/下/水平三个法线的重建值。加这三个数是因为 c_1（y 的系数）在白天是
            // **负**的，一眼看去像符号错了，而它其实是正确的签名：
            // 天顶是全天最暗的地方（正午 R 通道仅 ~943 cd/m²），而天底吃的是被太阳
            // 直射的地面反弹（≈ albedo·E☉·sin(elev)·T/π，正午在数千量级、且偏暖）。
            // 于是 y 矩为负、且 |R| > |G| >> |B|（蓝天与暖地面在 y 矩上几乎抵消）。
            // 把这三个数打出来，下次看到负号不用再重新推一遍。
            var dirs = new[] { Vector3.up, Vector3.down, Vector3.forward };
            var outs = new Color[dirs.Length];
            probe.Evaluate(dirs, outs);
            sb.Append("　 重建　上 ").Append(Fmt(new Vector3(outs[0].r, outs[0].g, outs[0].b)))
              .Append("　下 ").Append(Fmt(new Vector3(outs[1].r, outs[1].g, outs[1].b)))
              .Append("　水平 ").Append(Fmt(new Vector3(outs[2].r, outs[2].g, outs[2].b)))
              .AppendLine();

            // 只判"有没有被驱动过"，不判数值：数值正确性是 Run() 的职责，
            // 这里若也去比对就得再复现一遍参考解，两份判据迟早走歧。
            bool driven = RenderSettings.ambientMode == AmbientMode.Custom
                          && (c0.x + c0.y + c0.z) > 1e-3f;
            sb.Append("　 判定：链路").Append(driven ? "已接通 OK" : "**未接通**").AppendLine();
            if (!driven)
                sb.AppendLine("　 排查顺序：feature 是否装进当前 Renderer → Frame Debugger 里"
                            + "有无 \"Vista Sky Ambient SH\" → 是否有 Game/SceneView 相机在渲。");

            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            sb.Append("　 活动场景 ").Append(string.IsNullOrEmpty(scene.name) ? "(未命名)" : scene.name)
              .Append("　isDirty = ").Append(scene.isDirty)
              .AppendLine("（若逐帧写 RenderSettings 会标脏，这里会长期为 true）");

            Debug.Log(("[Vista] " + sb).Replace("\r", "").Replace("\n", "  |  "));
        }

        const float k_Y0 = VistaSphericalHarmonics.k_Y0;

        internal static float[] k_ShNorm => VistaSphericalHarmonics.k_ShNorm;
        internal static float[] k_RadianceToUnitySh => VistaSphericalHarmonics.k_RadianceToUnitySh;

        /// <summary>
        /// 测出 <c>SphericalHarmonicsL2.Evaluate</c> 对每个槽位的**实际权重** k_i，
        /// 即 <c>Evaluate(n) = Σ c_i · k_i · Y_i(n)</c> 里的 k_i。
        ///
        /// 为什么要测这个、而不是照文档写归一化：这个类型存的到底是"辐射亮度 SH"
        /// 还是"已与余弦瓣卷积过的辐照度 SH"，文档没写全，两者相差逐阶的
        /// Â_l（π、2π/3、π/4）；而基函数常数（Y00=0.282…）是否也折进去了同样没写。
        /// 猜错的症状是环境光整体亮/暗 3 倍左右，**在任何单一场景里都像是"美术没调好"**，
        /// 不会被当成 bug —— 这类错误必须在数值层拦住。
        ///
        /// 但真正需要的其实不是"Unity 内部存什么"，而是一条更弱、更可测的东西：
        /// 只要 <c>Evaluate</c> 就是渲染器对这组系数的解释（对 Flat 环境光实测成立，
        /// 见下面 Flat 标定那一项），那么写入端只需满足
        ///     Evaluate(n) == (1/π)·∫L(ω)·max(0, n·ω)dω
        /// 于是写入公式是 c_i = (Â_l/π)·Ŷ_i·L_i，只差 k_i 未知。
        /// 一次一个槽位置 1、在通用方向上求值，就把 k_i 直接量出来了 ——
        /// 完全不需要知道 Unity 折了哪些常数进去。
        ///
        /// 实测结论：k_i 恰好等于 1/Ŷ_i（3.54491 = 1/0.2820948，等等），也就是
        /// <c>Evaluate</c> 用的是**未归一化的多项式基** {1, y, z, x, xy, yz, 3z²−1, xz, x²−y²}。
        /// 这正是 <c>ShadeSH9</c> / <c>unity_SHAr</c> 那套形态。
        /// 下面把它写成断言而不是打印：<see cref="k_RadianceToUnitySh"/> 的正确性完全
        /// 依赖这一条，Unity 哪天改了约定必须在这里炸，而不是等到看图。
        /// </summary>
        static bool ProbeUnityConvention(StringBuilder sb)
        {
            sb.AppendLine("── Unity SphericalHarmonicsL2 约定标定");

            // 通用方向：9 个基函数在这里全部非零且互不相同，所以逐槽位的权重可以
            // 一次一个地读出来，不会被"某个基恰好为 0"掩盖。
            Vector3 d = new Vector3(1f, 2f, 3f).normalized;
            var basis = new float[9];
            ShBasis(d, basis);

            var probeDirs = new[] { d };
            var probeOut = new Color[1];

            float maxKErr = 0f;
            var k = new float[9];
            for (int i = 0; i < 9; ++i)
            {
                var sh = new SphericalHarmonicsL2();
                sh[0, i] = 1f;
                sh.Evaluate(probeDirs, probeOut);
                k[i] = probeOut[0].r / basis[i];
                // 预期 k_i·Ŷ_i == 1，即 Evaluate 用未归一化多项式基
                maxKErr = Mathf.Max(maxKErr, Mathf.Abs(k[i] * k_ShNorm[i] - 1f));
            }

            sb.Append("　 k_i = Evaluate 权重 / Y_i　");
            for (int i = 0; i < 9; ++i)
                sb.Append(k[i].ToString("F5")).Append(i == 8 ? "" : ", ");
            sb.AppendLine();
            sb.Append("　 |k_i·Ŷ_i − 1| 最大值 ").Append(maxKErr.ToString("E2"))
              .AppendLine("（预期 ~0：Evaluate 用未归一化多项式基）");

            // Flat 环境光是唯一语义完全确定的标定源：URP 下 albedo=1 的朗伯面在
            // ambientLight=c 时就渲成 c。所以 Evaluate 必须对任意法线返回 c ——
            // 这条成立才说明 Evaluate 代表的是"albedo 1 的出射亮度"（辐照度/π），
            // 也就是上面那条写入公式的前提。
            var ambient = new SphericalHarmonicsL2();
            ambient.AddAmbientLight(Color.white);
            var flatDirs = new[] { Vector3.up, Vector3.down, Vector3.right, Vector3.forward };
            var flatOut = new Color[flatDirs.Length];
            ambient.Evaluate(flatDirs, flatOut);

            float flatMax = 0f;
            for (int i = 0; i < flatOut.Length; ++i)
                flatMax = Mathf.Max(flatMax, Mathf.Abs(flatOut[i].r - 1f));

            sb.Append("　 AddAmbientLight(white)：c_0 = ").Append(ambient[0, 0].ToString("F6"))
              .Append("，Evaluate 各向偏离 1 的最大值 ").Append(flatMax.ToString("E2"))
              .AppendLine();

            // 交叉验证写入公式本身：均匀天空 L(ω)=1 的解析矩是 L_0 = 4π·Y00，高阶全 0。
            // 过一遍 k_RadianceToUnitySh 必须精确回到 AddAmbientLight(1) 的 c_0 = 1。
            // 这一项独立于上面两条 —— 它测的是那张缩放表自己有没有抄错。
            float uniformC0 = k_RadianceToUnitySh[0] * (4f * Mathf.PI * k_Y0);
            sb.Append("　 均匀天空 L=1 过缩放表得 c_0 = ").Append(uniformC0.ToString("F6"))
              .AppendLine("（预期 1）");

            bool okFlat = flatMax < 1e-5f && Mathf.Abs(ambient[0, 0] - 1f) < 1e-5f;
            bool okBasis = maxKErr < 1e-4f;
            bool okScale = Mathf.Abs(uniformC0 - 1f) < 1e-4f;

            sb.Append("　 判定：Flat 标定 ").Append(okFlat ? "OK" : "**FAIL**")
              .Append("　未归一化基 ").Append(okBasis ? "OK" : "**FAIL**")
              .Append("　缩放表自洽 ").Append(okScale ? "OK" : "**FAIL**").AppendLine();

            return okFlat && okBasis && okScale;
        }

        // ==================================================================
        //  辐照度比对：SH 重建 vs 从 LUT 数值积分的参考解
        //
        //  这一节要回答的是"投影写对了吗"。上面那些只能说明"我们对 Unity 的约定
        //  理解自洽"，说明不了投影本身 —— 少乘一个 4π、Fibonacci 分布写歪、
        //  groupshared 归约漏一半，都会得到一组**完全有限、非负、看着合理**的系数，
        //  而画面上只是"环境光偏亮/偏暗/偏色"，与美术没调好无法区分。
        //
        //  ---- 三条判据，以及为什么"逐法线偏差"不能当判据 ----
        //  逐法线的 SH 重建与参考解之间存在**必然**的差距：SH9 是二阶带限的，
        //  而日落时的天空（地平线一圈橙、天顶暗、地面反弹更暗）含大量高阶成分。
        //  实测这个差距在正午 ≤ 2.2%，在日落最暗的那个法线（朝下，只吃地面反弹）
        //  达到 31%。这是 L2 截断误差，不是 bug —— 把它设成阈值只会得到一个
        //  "日落必红"的自检，而那种自检会被习惯性忽略，等于没有。
        //  （顺带说明了为什么镜面反射必须另走 cubemap，见 #5b：截断误差在漫反射上
        //  可接受，在镜面上就是把高光糊成一团。）
        //
        //  所以真正判定的是三条与截断无关的：
        //   1) 均值恒等式（唯一的**精确**判据）：
        //        mean_n[(1/π)∫L·max(0,n·ω)dω] = (1/4π)∫L dω = L_00·Y00
        //      一阶以上在整球上均值为 0，所以这条等式与"L2 表达力够不够"无关，
        //      只取决于 4π/N 权重、Ŷ_0、Â_0 有没有写对 —— 也就是唯一会真出 bug 的部分。
        //      要点：参考解必须用**与投影完全相同的方向集**（1024），否则比的是
        //      1024 与 4096 的求积差异（日落红通道实测 2.75%），恒等式在有限样本下
        //      本来就不精确成立。那个差异单独作为收敛性度量打印。
        //   2) GPU 重建 == CPU 重建：两条"矩 -> 辐照度"的实现互不相干
        //      （GPU 是 VistaShIrradiance，走 Â_l；CPU 是 k_RadianceToUnitySh
        //      -> SphericalHarmonicsL2.Evaluate，走 Ŷ_i 与 Unity 的多项式基）。
        //      它们的失败模式不重叠，只测一条等于放过另一条。
        //   3) 正午的逐法线偏差 < 5%：正午天空足够平滑，L2 截断本来就很小，
        //      这一档的紧阈值能抓住"某一阶的缩放错了"（那会同时污染两个太阳高度，
        //      而截断误差只在日落放大）。日落那一档只打印、不判定。
        // ==================================================================

        /// <summary>
        /// 均值恒等式与 GPU/CPU 一致性的阈值。两边都是同一批 fp32 数据上的
        /// 少量乘加，差异只可能来自浮点重排，实测在 1e-6 量级。
        /// 给到 1e-3 是留给驱动差异，仍然比任何真实缩放错误小两个数量级以上。
        /// </summary>
        const float k_ExactTolerance = 1e-3f;

        /// <summary>
        /// 正午档逐法线偏差的阈值。实测最大 2.17%（天顶法线），
        /// 5% 留出一倍余量而不至于放过"少乘一个 Â_l"（那至少是 30% 量级）。
        /// </summary>
        const float k_NoonTruncationTolerance = 0.05f;

        /// <summary>
        /// 判定用的下限。朝下的法线在地面反弹很弱时辐照度接近 0，
        /// 此时相对误差没有意义（分母趋 0），改用绝对量级门限跳过。
        /// </summary>
        const float k_IrradianceFloor = 1e-4f;

        static bool ValidateIrradiance(StringBuilder sb)
        {
            sb.AppendLine("── SH 投影 vs 参考解（辐照度/π）");

            var res = VistaRuntimeResources.Get();
            if (res == null || res.atmosphereLutCS == null)
            {
                sb.AppendLine("　 ✘ 取不到 atmosphereLutCS：当前管线不是 URP，或资源未导入。");
                return false;
            }

            var p = VistaAtmosphereParameters.CreateEarth();
            var luts = new VistaAtmosphereLuts(res.atmosphereLutCS);
            try
            {
                if (!luts.isSkyAmbientShValid)
                {
                    sb.AppendLine("　 ✘ SkyAmbientSh kernel 缺失（编译失败或被 only_renderers 排除）。");
                    return false;
                }
                if (!luts.PrepareSkyAmbientSh() || !luts.EnsureSkyAmbientShReference())
                {
                    sb.AppendLine("　 ✘ SH buffer 分配失败。");
                    return false;
                }

                // 正午与日落两档。日落那档才是真正的考验：那时天空的角频率最高
                // （地平线一圈橙、背侧蓝紫），L2 的表达能力被顶到上限 ——
                // 所以它的逐法线偏差只作为**截断误差的量化记录**打印出来，
                // 供"L2 够不够"这个设计判断留证，不参与判定。
                bool ok = true;
                ok &= CompareAtSun(luts, p, 60f, "正午 60°", k_NoonTruncationTolerance, sb);
                ok &= CompareAtSun(luts, p,  3f, "日落 3°",  float.PositiveInfinity, sb);
                return ok;
            }
            finally
            {
                luts.Dispose();
            }
        }

        /// <param name="truncationTolerance">
        /// 逐法线偏差的阈值。传 <see cref="float.PositiveInfinity"/> 表示只打印不判定。
        /// </param>
        static bool CompareAtSun(VistaAtmosphereLuts luts, VistaAtmosphereParameters p,
                                 float sunElevationDeg, string label,
                                 float truncationTolerance, StringBuilder sb)
        {
            float rad = sunElevationDeg * Mathf.Deg2Rad;
            var sunDir = new Vector3(0f, Mathf.Sin(rad), Mathf.Cos(rad));
            // 人眼高度。海拔会影响地面反弹的占比，取 2 m 与 LUT 自检一致，
            // 两份报告的数字才能横向对照。
            var view = VistaAtmosphereViewData.Create(p, new Vector3(0f, 2f, 0f), 0f, sunDir);

            // 一条 CommandBuffer 串起四趟 dispatch。立即模式下资源状态转换由图形层
            // 自动插，所以可以合并 —— RenderGraph 那边必须拆成独立 pass（见 VistaAtmospherePass）。
            var cmd = new CommandBuffer { name = "Vista Ambient SH (SelfTest)" };
            luts.EnsureStaticLuts(cmd, p);
            luts.RenderSkyViewLut(cmd, view);
            luts.RenderSkyAmbientSh(cmd, view);
            luts.RenderSkyAmbientShReference(cmd, view);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            // GetData 是同步阻塞读回。自检里正是想要的 —— 用 AsyncGPUReadback
            // 就得在 Editor 里等回调，而菜单项是一次性同步调用。
            var moments = new Vector4[VistaAtmosphereLuts.k_ShCoeffCount];
            luts.skyAmbientShBuffer.GetData(moments);
            var refData = new Vector4[VistaAtmosphereLuts.k_ShRefElementCount];
            luts.skyAmbientShRefBuffer.GetData(refData);

            var probe = new SphericalHarmonicsL2();
            bool converted;
            // NativeArray 而不是给 TryConvertMomentsToProbe 再加一个 Vector4[] 重载：
            // 运行时那条路（AsyncGPUReadback.GetData）拿到的就是 NativeArray，
            // 加重载等于让自检走一条线上不存在的分支。
            using (var tmp = new NativeArray<Vector4>(moments, Allocator.Temp))
                converted = VistaSphericalHarmonics.TryConvertMomentsToProbe(tmp, ref probe);

            sb.Append("　 ").Append(label)
              .Append("　L_00 = ").Append(Fmt(moments[0]))
              .Append("　c_0 = ").Append(Fmt(new Vector3(probe[0, 0], probe[1, 0], probe[2, 0])))
              .AppendLine();

            if (!converted)
            {
                sb.AppendLine("　 ✘ TryConvertMomentsToProbe 拒绝了这批矩（全 0 / 非有限值）。");
                return false;
            }

            float maxTrunc = 0f, maxGpuCpu = 0f;
            int worstTrunc = -1;
            int compared = 0;

            var evalDir = new Vector3[1];
            var evalOut = new Color[1];

            for (int i = 0; i < VistaAtmosphereLuts.k_ShRefNormalCount; ++i)
            {
                Vector3 n      = refData[i * 3 + 0];
                Vector3 refIrr = refData[i * 3 + 1];
                Vector3 gpuIrr = refData[i * 3 + 2];

                evalDir[0] = n;
                probe.Evaluate(evalDir, evalOut);
                var cpuIrr = new Vector3(evalOut[0].r, evalOut[0].g, evalOut[0].b);

                // 逐通道比。只比亮度会漏掉"红绿蓝各自缩放不同"这类错误。
                float refMag = Mathf.Max(refIrr.x, Mathf.Max(refIrr.y, refIrr.z));
                if (refMag < k_IrradianceFloor)
                    continue;

                compared++;
                float trunc = RelErr(gpuIrr, refIrr, refMag);
                float gpuCpu = RelErr(gpuIrr, cpuIrr, refMag);
                if (trunc > maxTrunc) { maxTrunc = trunc; worstTrunc = i; }
                maxGpuCpu = Mathf.Max(maxGpuCpu, gpuCpu);

                sb.Append("　　 n").Append(i).Append(' ').Append(Fmt(n))
                  .Append("　ref ").Append(Fmt(refIrr))
                  .Append("　截断 ").Append(trunc.ToString("P2"))
                  .AppendLine();
            }

            // ---- 判据 1：均值恒等式（精确）----
            // 均值组的布局：[N] 用 4096 样本（精度参考），[N+1] 用与 SH 投影
            // 完全相同的 1024 方向集。恒等式只对后者在有限样本下精确成立 ——
            // 拿 4096 那份卡紧阈值实测差 2.75%（日落红通道），那是求积差异不是 bug。
            int miAcc = VistaAtmosphereLuts.k_ShRefNormalCount * 3;
            int miSame = miAcc + 3;
            Vector3 refMeanAcc  = refData[miAcc + 1];
            Vector3 refMeanSame = refData[miSame + 1];
            Vector3 shMean      = refData[miSame + 2];
            float meanMag = Mathf.Max(refMeanAcc.x, Mathf.Max(refMeanAcc.y, refMeanAcc.z));
            float meanErr = meanMag > k_IrradianceFloor ? RelErr(shMean, refMeanSame, meanMag) : 1f;
            // CPU 侧同一个量就是 c_0（Evaluate 在整球上的均值 = 常数项）。
            // 单独比一次：它验的是 k_RadianceToUnitySh[0] 而不是 GPU 的 Y0。
            var cpuMean = new Vector3(probe[0, 0], probe[1, 0], probe[2, 0]);
            float cpuMeanErr = meanMag > k_IrradianceFloor ? RelErr(cpuMean, refMeanSame, meanMag) : 1f;

            bool okMean = meanErr < k_ExactTolerance && cpuMeanErr < k_ExactTolerance;
            sb.Append("　 均值恒等式（同方向集）　ref ").Append(Fmt(refMeanSame))
              .Append("　GPU 偏差 ").Append(meanErr.ToString("E2"))
              .Append("　CPU 偏差 ").Append(cpuMeanErr.ToString("E2"))
              .Append(' ').Append(Mark(okMean)).AppendLine();

            // ---- 度量（不判定）：采样数收敛性 ----
            // 1024 与 4096 在同一个天空上的求积差异。这就是 VISTA_SKY_SH_SAMPLES
            // 该取多少的直接依据（Task #7 分级要看这个数），而不是"看起来够不够"。
            float quadErr = meanMag > k_IrradianceFloor
                ? RelErr(refMeanSame, refMeanAcc, meanMag) : 0f;
            sb.Append("　 求积收敛　1024 vs 4096 均值偏差 ").Append(quadErr.ToString("P2"))
              .AppendLine("（仅记录）");

            // ---- 判据 2 / 3 ----
            // 一个法线都没比上，说明整张 SkyView 是黑的（或参考核没写）——
            // 这时"最大偏差 0"看起来是满分，必须单独拦住。
            bool okCount = compared >= 4;
            bool okGpuCpu = maxGpuCpu < k_ExactTolerance;
            bool okTrunc = maxTrunc < truncationTolerance;

            sb.Append("　 判定：有效法线 ").Append(compared).Append('/')
              .Append(VistaAtmosphereLuts.k_ShRefNormalCount).Append(' ').Append(Mark(okCount))
              .Append("　GPU/CPU 一致 ").Append(maxGpuCpu.ToString("E2")).Append(' ').Append(Mark(okGpuCpu))
              .Append("　最大截断 ").Append(maxTrunc.ToString("P2"))
              .Append("（n").Append(worstTrunc).Append("）")
              .Append(float.IsPositiveInfinity(truncationTolerance) ? "（仅记录）" : " " + Mark(okTrunc))
              .AppendLine();

            return okCount && okMean && okGpuCpu && okTrunc;
        }

        /// <summary>
        /// 逐通道最大偏差 / <paramref name="scale"/>。分母统一用**该法线三通道的最大值**
        /// 而不是各通道自己：用各通道自己的值做分母，日落时蓝通道那个接近 0 的数会让
        /// 相对误差爆到几百，而那点绝对偏差在画面上完全看不见 ——
        /// 自检会因此长期红着，形同废掉。
        /// </summary>
        static float RelErr(Vector3 a, Vector3 b, float scale)
        {
            float d = Mathf.Max(Mathf.Abs(a.x - b.x),
                      Mathf.Max(Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z)));
            return d / scale;
        }

        static string Mark(bool ok) => ok ? "OK" : "**FAIL**";

        static string Fmt(Vector3 v) =>
            "(" + v.x.ToString("F4") + ", " + v.y.ToString("F4") + ", " + v.z.ToString("F4") + ")";
    }
}
