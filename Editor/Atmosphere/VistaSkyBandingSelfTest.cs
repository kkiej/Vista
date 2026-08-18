using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// Sky-View 表的**带状（banding）数值判定**。
    ///
    /// 要回答的是三个不同的问题，所以是三条判据，而不是一条判据的三种参数：
    ///   A（时间轴）：太阳从 0° 走到 90°，同一个方向的天空亮度会不会出现台阶？
    ///                这是"时间轴推进时画面会不会跳"，也是唯一有截图观感、缺数字的那一项。
    ///   B（空间轴）：192×108 这个分辨率够不够？用**分辨率减半/加倍**测插值误差本身。
    ///   C（地板）：  在**一个纹素内部**采样。双线性在纹素内严格线性，理论二阶差分恒为 0，
    ///                所以实测到的就是 fp16 存储 + 采样算术的噪声地板。
    ///
    /// C 不是可选项，它是 A 和 B 能下结论的前提：二阶差分的收敛阶 p≈0 既可能是
    /// "真台阶"，也可能是"已经掉到量化地板里了"，这两种结论一个要修一个不用修，
    /// 而只看阶数分不开。先把地板量出来，A/B 的幅度才有比较对象。
    ///
    /// ---- 为什么用收敛阶，而不是拍一个阈值 ----
    /// 拍阈值等于把结论写进判据。改成测**二阶差分随步长的收敛阶** p 之后，
    /// 三种机制自己会分开（Δ 是采样步长）：
    ///   p ≈ 2  被采的函数在这个轴上是光滑的（S₂ ∝ Δ²）→ 分辨率不是瓶颈
    ///   p ≈ 1  双线性重建在纹素边界上的**斜率跳变**：跨界的三点二阶差分 ≈ |Δf'|·Δ。
    ///          这是纹理过滤的固有行为，不是 bug，但幅度随 Δ 线性缩小 ——
    ///          所以能外推到生产步长（时间轴上 ≤0.01°/帧）再判可见性。
    ///   p ≈ 0  幅度与步长无关 = 真的 C⁰ 台阶（参数化分支、或量化）→ 要看是否高于地板
    /// 一次 0.5° 的扫描按 stride 1/2/4 抽稀就得到 0.5°/1°/2° 三条曲线：
    /// 少烘三分之二的表，而且三条曲线严格同源，排除了"两次烘焙参数不一致"这种伪信号。
    /// </summary>
    public static class VistaSkyBandingSelfTest
    {
        // ---- 判据 A：太阳仰角扫描 ----
        const float k_SunStepDeg = 0.5f;
        const int   k_SunSteps   = 181;              // 0°, 0.5°, ..., 90°

        /// <summary>
        /// 生产环境里的时间轴步长：一个 20 分钟的完整昼夜循环、60 fps，
        /// 太阳每帧走 360°/72000 ≈ 0.005°。取 0.01° 是留一倍余量。
        /// 判据 A 用 0.5° 量（信号才出得来），再按实测收敛阶外推到这个步长 ——
        /// 直接在 0.01° 上量的话，信号会整个埋进 fp16 地板里，测不出东西。
        /// </summary>
        const float k_ProductionSunStepDeg = 0.01f;

        /// <summary>
        /// 可见性阈值：大面积平滑渐变上的 Weber 对比阈约 1%。
        /// 这是判据的**唯一**一个外部常数，取自感知阈而不是取自"跑出来的数字附近"。
        /// </summary>
        const float k_VisibleWeber = 0.01f;

        /// <summary>
        /// fp16 二阶差分的理论地板。尾数 10 位 → 相对 ULP 2⁻¹⁰，半 ULP 舍入 2⁻¹¹ ≈ 4.9e-4；
        /// 二阶差分的系数是 (1, −2, 1)，绝对值之和 4，最坏情形 4 × 2⁻¹¹ ≈ 1.95e-3。
        /// 判据 C 实测出来的值应该落在这个数附近；差一个量级说明我对存储格式的理解错了。
        /// </summary>
        const float k_Fp16SecondDiffFloor = 1.95e-3f;

        // ---- 判据 B：分辨率三档（h、h/2 用于 Richardson 外推）----
        static readonly Vector2Int[] k_Resolutions =
        {
            new Vector2Int(96,  54),
            new Vector2Int(192, 108),          // 生产档
            new Vector2Int(384, 216),
        };
        const int k_ProdResIdx = 1;

        // ---- 判据 C：纹素内部 ----
        const float k_IntraTexelStartDeg = 8f;
        const int   k_IntraTexelCount    = 32;

        /// <summary>
        /// 四档步长，每档减半。跨度 0.155° / 0.078° / 0.039° / 0.019°。
        ///
        /// 为什么是"逐档减半直到平台"而不是"取一个足够小的步长"：地板是个**平台**，
        /// 而"够小"事先不知道有多小。纹素在 v 轴上是被 sqrt warp 扭过的，
        /// 平均纹素（180°/108）根本不代表 8° 处的局部纹素，照平均值挑窗口就是在赌。
        ///
        /// 逐档减半之后不用赌：二阶差分里能出现的东西只有三类 ——
        ///   纹素边界斜率跳变 ∝ Δ¹、被采函数自身的曲率 ∝ Δ²、算术噪声 ∝ Δ⁰。
        /// 前两类都随 Δ 单调缩小，所以只要一直缩，剩下的**必然**只有第三类。
        /// 幅度停止下降的那个值就是地板，不需要任何关于"窗口是否在纹素内"的假设。
        /// </summary>
        static readonly float[] k_IntraTexelSteps = { 0.005f, 0.0025f, 0.00125f, 0.000625f };

        /// <summary>平台判定：相邻两档的阶低于此值即认为已经停止下降。</summary>
        const float k_PlateauOrder = 0.3f;

        /// <summary>日落档。日晕、暮光带、长路径 Mie 全在这一档最强，是最坏情形。</summary>
        const float k_SunsetElevDeg = 3f;

        static readonly Vector3 k_CameraPos = new Vector3(0f, 2f, 0f);

        /// <summary>与 HLSL 侧 <c>ringElev</c> 逐字一致，报告要按环分组给结果。</summary>
        static readonly float[] k_RingElev = { 2f, 10f, 30f, 70f };

        [MenuItem("Window/Vista/Validate Sky-View Banding")]
        static void RunFromMenu()
        {
            var sb = new StringBuilder();
            bool ok = Run(VistaAtmosphereParameters.CreateEarth(), sb);

            string oneLine = sb.ToString().TrimEnd().Replace("\r", "").Replace("\n", "  |  ");
            if (ok) Debug.Log("[Vista] Sky-View banding 判定通过  |  " + oneLine);
            else Debug.LogWarning("[Vista] Sky-View banding 判定有未通过项  |  " + oneLine);
        }

        static bool Run(VistaAtmosphereParameters p, StringBuilder sb)
        {
            var res = VistaRuntimeResources.Get();
            if (res == null || res.atmosphereLutCS == null)
            {
                sb.AppendLine("✘ 取不到 VistaRuntimeResources / atmosphereLutCS。");
                return false;
            }

            var luts = new VistaAtmosphereLuts(res.atmosphereLutCS);
            try
            {
                if (!luts.isValid)
                {
                    sb.AppendLine("✘ compute 无效：kernel 未全部找到。");
                    return false;
                }
                if (!luts.EnsureSkyViewBanding())
                {
                    sb.AppendLine("✘ 找不到 SkyViewBandingSignature kernel（compute 未重编译？）。");
                    return false;
                }

                luts.Invalidate();
                var cmd = new CommandBuffer { name = "Vista Banding (SelfTest)" };
                luts.EnsureStaticLuts(cmd, p);
                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Release();

                sb.Append("　 相机 (0,2,0)　Sky-View ").Append(luts.skyViewWidth).Append("×")
                  .Append(luts.skyViewHeight).Append("　方向 ").Append(VistaAtmosphereLuts.k_SkyBandingDirCount)
                  .Append("（4 仰角环 × 16 方位，方位 0 正对太阳）　GPU ")
                  .AppendLine(SystemInfo.graphicsDeviceName);

                // C 先跑：它给出的地板是 A / B 判定的比较基准。
                bool okC = RunIntraTexelFloor(luts, p, sb, out float floor, out bool floorConfirmed);
                bool okA = RunSunSweep(luts, p, sb, floor, floorConfirmed);
                bool okB = RunResolutionConvergence(luts, p, sb, floor);
                return okA && okB && okC;
            }
            finally
            {
                luts.SetSkyViewResolution(
                    VistaAtmosphereLuts.k_SkyViewWidthDefault, VistaAtmosphereLuts.k_SkyViewHeightDefault);
                luts.Dispose();
            }
        }

        // ==================================================================== 判据 C
        //
        // 在一个纹素内部走 32 步，步长逐档减半。双线性插值在纹素内部是**严格线性**的，
        // 而 uv 是仰角的光滑函数，所以理论上二阶差分只剩两项：函数自身的曲率 ∝ Δ²，
        // 以及 fp16 存储 + 采样算术的噪声 ∝ Δ⁰。前者随 Δ 缩、后者不缩，
        // 于是**幅度停止下降的那个平台就是地板**。
        //
        // 这一项不是可选的：判据 A / B 里 p≈0 既可能是"真台阶"也可能是"已经掉进地板"，
        // 这两种结论一个要修一个不用修，只看阶数分不开。而且方向危险 ——
        // 地板若被高估（窗口跨了纹素边界、量到斜率跳变），A/B 会**更容易通过**。
        // 所以这里不去赌窗口够不够小，而是一直缩到幅度自己停下来。
        static bool RunIntraTexelFloor(
            VistaAtmosphereLuts luts, VistaAtmosphereParameters p, StringBuilder sb,
            out float floor, out bool confirmed)
        {
            sb.AppendLine("── 判据 C｜纹素内部的算术地板（步长逐档减半，缩到幅度停止下降）");

            var view = MakeView(p, k_SunsetElevDeg);
            int n = k_IntraTexelSteps.Length;
            var amp = new float[n];
            var buf = new Vector4[VistaAtmosphereLuts.k_SkyBandingMaxCount];

            for (int i = 0; i < n; ++i)
            {
                SampleArc(luts, view, k_IntraTexelStartDeg, k_IntraTexelSteps[i], k_IntraTexelCount, buf);
                amp[i] = MaxRelSecondDiff(buf, k_IntraTexelCount, 0f, out _);
            }

            sb.Append("　 弧段 ").Append(k_IntraTexelStartDeg.ToString("F2")).Append("° 起，")
              .Append(k_IntraTexelCount).AppendLine(" 点");
            for (int i = 0; i < n; ++i)
            {
                sb.Append("　　 Δ=").Append(k_IntraTexelSteps[i].ToString("F6"))
                  .Append("°（跨度 ").Append((k_IntraTexelSteps[i] * (k_IntraTexelCount - 1)).ToString("F4"))
                  .Append("°）　S₂ = ").Append(Sci(amp[i]));
                if (i > 0)
                    sb.Append("　阶 ").Append(Order(amp[i - 1], amp[i]).ToString("F2"));
                sb.AppendLine();
            }

            // 最后一档的阶决定"是否已经到平台"。中间几档的阶只是过程信息 ——
            // 混着 ∝Δ¹ / ∝Δ² / ∝Δ⁰ 三项时中间的阶必然落在 0 与 2 之间，
            // 拿中间那个阶去判"是不是地板"就是在读混合物的表观指数，读不出东西。
            float lastOrder = Order(amp[n - 2], amp[n - 1]);
            floor = amp[n - 1];
            float ratioToTheory = floor / k_Fp16SecondDiffFloor;

            // 幅度停止下降 = 到平台。同时要求它不超过 fp16 的理论最坏地板：
            // 超了说明这个平台不是量化造成的，那就还有别的机制，不能叫地板。
            confirmed = lastOrder >= 0f && lastOrder < k_PlateauOrder && ratioToTheory < 1.5f;

            sb.Append("　 理论最坏 fp16 地板 4·2⁻¹¹ = ").Append(Sci(k_Fp16SecondDiffFloor))
              .Append("　实测平台/理论 = ").Append(ratioToTheory.ToString("F2"))
              .Append("（理论是最坏情形，实测低于它才合理）").AppendLine();
            sb.Append("　 ").Append(Mark(confirmed));
            if (confirmed)
                sb.Append("地板已确认 = ").Append(Sci(floor))
                  .AppendLine("（最末两档阶 <0.3 = 幅度已停止下降，且不超过 fp16 理论最坏值）"
                            + "→ 判据 A/B 的幅度以此为基准");
            else if (lastOrder >= k_PlateauOrder)
                sb.Append("**未到平台**：最末两档阶 ").Append(lastOrder.ToString("F2"))
                  .AppendLine(" ≥0.3，幅度仍随步长下降，说明还有 ∝Δ 或 ∝Δ² 的项没被压掉（窗口仍跨着纹素边界，"
                            + "或函数曲率还没降到噪声以下）。这个数只能当**上界**用，"
                            + "而上界会让 A/B 更容易通过 —— 所以 A/B 中一切「低于地板」的结论都降级为无判定。"
                            + "修法：继续往 k_IntraTexelSteps 末尾加更小的步长。");
            else
                sb.Append("**平台高于 fp16 理论最坏值**（比值 ").Append(ratioToTheory.ToString("F2"))
                  .AppendLine("）：幅度确实不随步长缩，但量级说明它不是 fp16 量化 —— "
                            + "是采样路径里另有一处 C⁰ 不连续（参数化分支？）。这不是地板，是缺陷。");

            return confirmed;
        }

        // ==================================================================== 判据 A
        //
        // 181 次重烘（0°→90°，步长 0.5°），每次采 64 个**固定世界方向**。
        // 方向固定是必须的：如果观察方向随太阳转，逐仰角的差分里就同时混进了
        // "表变了"与"我在看别处"，二者无法分离。
        static bool RunSunSweep(
            VistaAtmosphereLuts luts, VistaAtmosphereParameters p, StringBuilder sb,
            float floor, bool floorConfirmed)
        {
            const int dirs = VistaAtmosphereLuts.k_SkyBandingDirCount;
            sb.Append("── 判据 A｜太阳 0°→90° 扫描，步长 ").Append(k_SunStepDeg)
              .Append("°，").Append(k_SunSteps).AppendLine(" 次重烘");

            // [sun, dir] 展平。存 rgb 是为了能说出"哪个通道在跳"——
            // 只跳蓝通道指向 Rayleigh 项，跳红指向长路径 Mie / 透射率，修法不同。
            var data = new Vector4[k_SunSteps * dirs];
            var tmp  = new Vector4[VistaAtmosphereLuts.k_SkyBandingMaxCount];

            float globalMax = 0f;
            for (int i = 0; i < k_SunSteps; ++i)
            {
                var view = MakeView(p, i * k_SunStepDeg);
                SampleFixedDirs(luts, view, tmp);
                for (int d = 0; d < dirs; ++d)
                {
                    data[i * dirs + d] = tmp[d];
                    globalMax = Mathf.Max(globalMax, tmp[d].w);
                }
            }

            // 归一化用局部值（Weber），但对极暗处设下限：太阳在 0° 附近时天空亮度
            // 趋近 0，相对差分会发散，而那里画面上本就什么都看不见。
            // 下限取全场最大值的 1%，并把它打出来 —— 这是个假设，不该隐含在结论里。
            float lumFloor = 0.01f * globalMax;
            sb.Append("　 亮度峰值 ").Append(Sci(globalMax))
              .Append(" cd/m²　相对差分的分母下限取峰值的 1% = ").Append(Sci(lumFloor))
              .AppendLine("（更暗处不参与可见性判定）");

            int[] strides = { 4, 2, 1 };                 // Δ = 2°, 1°, 0.5°
            var s2 = new float[strides.Length, dirs];
            var d1 = new float[strides.Length, dirs];
            var worstChannel = new int[dirs];

            for (int s = 0; s < strides.Length; ++s)
            {
                int stride = strides[s];
                int n = (k_SunSteps - 1) / stride + 1;
                var col = new Vector4[n];
                for (int d = 0; d < dirs; ++d)
                {
                    for (int j = 0; j < n; ++j)
                        col[j] = data[(j * stride) * dirs + d];
                    s2[s, d] = MaxRelSecondDiff(col, n, lumFloor, out int ch);
                    d1[s, d] = MaxRelFirstDiff(col, n, lumFloor);
                    if (stride == 1) worstChannel[d] = ch;
                }
            }

            // 逐方向定阶，然后报"最坏方向"与"中位阶"。
            // 阶用逐方向算而不是用"逐 Δ 的最大值"算：后者在不同 Δ 上可能取到不同方向，
            // 那个比值就不是同一个函数的收敛比，阶数没有意义。
            var order = new float[dirs];
            for (int d = 0; d < dirs; ++d)
                order[d] = Order(s2[1, d], s2[2, d]);     // Δ=1° → 0.5°

            int worst = 0;
            for (int d = 1; d < dirs; ++d)
                if (s2[2, d] > s2[2, worst]) worst = d;

            float medOrder = Median(order);
            float ampWorst = s2[2, worst];
            float orderWorst = order[worst];

            // 外推到生产步长：p≈1 的拐折幅度 ∝ Δ，p≈2 ∝ Δ²，p≈0 不缩。
            // 这一步是判据 A 真正的落地点 —— 0.5° 是为了让信号出来才用的测量步长，
            // 时间轴上实际每帧只走 0.01°。
            float shrink = Mathf.Pow(k_ProductionSunStepDeg / k_SunStepDeg, Mathf.Max(0f, orderWorst));
            float ampProd = ampWorst * shrink;

            sb.Append("　 最坏方向 #").Append(worst).Append("（仰角环 ")
              .Append(k_RingElev[worst / VistaAtmosphereLuts.k_SkyBandingAzimuths].ToString("F0"))
              .Append("°，方位 ")
              .Append((worst % VistaAtmosphereLuts.k_SkyBandingAzimuths * 22.5f).ToString("F0"))
              .Append("°）　跳变最大的通道 ").AppendLine(ChannelName(worstChannel[worst]));
            sb.Append("　 S₂  Δ=2° ").Append(Sci(s2[0, worst]))
              .Append("　Δ=1° ").Append(Sci(s2[1, worst]))
              .Append("　Δ=0.5° ").Append(Sci(ampWorst))
              .Append("　→ 阶 p = ").Append(orderWorst.ToString("F2"))
              .Append("（64 方向中位阶 ").Append(medOrder.ToString("F2")).AppendLine("）");
            // Δ=2° 这一档只有 46 个采样点，最坏点可能整个被跨过去 ——
            // 所以阶只取 1°→0.5°，而 2° 那一档单纯是"三点是否成幂律"的旁证。
            // 若它比 1° 还小，说明它被抽稀漏掉了极值，必须说出来而不是让读者自己发现。
            if (s2[0, worst] < s2[1, worst])
                sb.AppendLine("　 ⚠ Δ=2° 的幅度反而小于 Δ=1°：粗档抽稀漏掉了极值点，"
                            + "该档不参与定阶（阶只取 1°→0.5°），三点幂律不成立不代表信号异常。");
            sb.Append("　 一阶差分 D₁(Δ=0.5°) = ").Append(Sci(d1[2, worst]))
              .AppendLine("　（天空本身该有的变化量，作对照：D₁ 大而 S₂ 小 = 变化快但平滑）");
            sb.Append("　 外推到生产步长 ").Append(k_ProductionSunStepDeg.ToString("F3"))
              .Append("°/帧：S₂ ≈ ").Append(Sci(ampProd))
              .Append("　可见阈（Weber 1%）= ").Append(Sci(k_VisibleWeber)).AppendLine();

            sb.AppendLine("　 逐环最坏 S₂(Δ=0.5°)：");
            for (int r = 0; r < k_RingElev.Length; ++r)
            {
                float m = 0f; int arg = 0;
                for (int a = 0; a < VistaAtmosphereLuts.k_SkyBandingAzimuths; ++a)
                {
                    int d = r * VistaAtmosphereLuts.k_SkyBandingAzimuths + a;
                    if (s2[2, d] > m) { m = s2[2, d]; arg = a; }
                }
                sb.Append("　　 仰角 ").Append(k_RingElev[r].ToString("F0").PadLeft(2))
                  .Append("°　").Append(Sci(m)).Append("　@ 方位 ")
                  .Append((arg * 22.5f).ToString("F0")).AppendLine("°");
            }

            // ---- 判定 ----
            bool pass;
            string why;
            if (ampWorst < floor)
            {
                // 落在地板里 = 无法与量化噪声区分。地板本身没被确认时这条不能算通过。
                pass = floorConfirmed;
                why = floorConfirmed
                    ? "S₂ 已在实测地板以下，与 fp16 量化噪声不可区分 → 通过（也说明这条轴上没有可再优化的余量）"
                    : "S₂ 在地板以下，但**地板只是上界**（判据 C 未确认）→ 无判定";
            }
            else if (ampProd < k_VisibleWeber)
            {
                pass = true;
                why = orderWorst >= 1.5f
                    ? "阶≈2：这个轴上被采的函数是光滑的，S₂ 就是真实物理曲率 → 通过"
                    : orderWorst >= 0.6f
                        ? "阶≈1：双线性在纹素边界的斜率跳变（纹理过滤的固有行为），幅度随步长线性缩小，"
                        + "外推到生产步长后低于可见阈 → 通过"
                        : "阶≈0 但外推后仍低于可见阈 → 通过（幅度不随步长缩小，属真台阶，只是看不见）";
            }
            else if (orderWorst < 0.6f)
            {
                pass = false;
                why = "**阶≈0 且外推后仍高于可见阈** = 真台阶且可见（参数化分支或量化），需要修";
            }
            else
            {
                pass = false;
                why = "外推后仍高于可见阈，但阶≥0.6 说明它随分辨率/步长可收敛 —— 修法是提分辨率，不是改参数化";
            }
            sb.Append("　 ").Append(Mark(pass)).AppendLine(why);
            return pass;
        }

        // ==================================================================== 判据 B
        //
        // 分辨率 96×54 / 192×108 / 384×216 各烘一次，同一组固定方向、同一个太阳。
        // 双线性重建的插值误差是 O(h²)，所以：
        //   e_hi = |L(h) − L(h/2)| = |E(h) − E(h/2)| = C h² (1 − 1/4) = 0.75 · E(h)
        // 于是生产档自身的插值误差 E(192) ≈ e_hi / 0.75 ≈ 1.33 · e_hi（Richardson 外推）。
        // e_lo / e_hi 应该 ≈ 4；若明显不到，O(h²) 这个前提就不成立，
        // 那么上面那个外推系数也不成立 —— 所以阶数不是附赠信息，是这个估计能不能用的门槛。
        static bool RunResolutionConvergence(
            VistaAtmosphereLuts luts, VistaAtmosphereParameters p, StringBuilder sb, float floor)
        {
            const int dirs = VistaAtmosphereLuts.k_SkyBandingDirCount;
            sb.Append("── 判据 B｜分辨率收敛（太阳 ").Append(k_SunsetElevDeg)
              .AppendLine("° 日落档，最坏情形）");

            var view = MakeView(p, k_SunsetElevDeg);
            var lum = new float[k_Resolutions.Length][];
            var tmp = new Vector4[VistaAtmosphereLuts.k_SkyBandingMaxCount];

            for (int r = 0; r < k_Resolutions.Length; ++r)
            {
                luts.SetSkyViewResolution(k_Resolutions[r].x, k_Resolutions[r].y);
                luts.PrepareLuts(p);                     // 尺寸变了要重新分配
                SampleFixedDirs(luts, view, tmp);
                lum[r] = new float[dirs];
                for (int d = 0; d < dirs; ++d) lum[r][d] = tmp[d].w;
            }

            float maxE = 0f, maxLo = 0f;
            int worst = 0;
            var eHi = new float[dirs];
            var ord = new float[dirs];
            for (int d = 0; d < dirs; ++d)
            {
                float refv = Mathf.Max(lum[2][d], 1e-6f);
                float lo = Mathf.Abs(lum[0][d] - lum[1][d]) / refv;
                float hi = Mathf.Abs(lum[1][d] - lum[2][d]) / refv;
                eHi[d] = hi;
                ord[d] = Order(lo, hi);
                float est = hi / 0.75f;
                if (est > maxE) { maxE = est; worst = d; maxLo = lo; }
            }

            float ordWorst = Order(maxLo, eHi[worst]);
            float medOrd = Median(ord);
            bool orderOk = ordWorst > 1.2f;              // O(h²) 前提成立才敢用 1.33 这个系数

            sb.Append("　 最坏方向 #").Append(worst).Append("（仰角环 ")
              .Append(k_RingElev[worst / VistaAtmosphereLuts.k_SkyBandingAzimuths].ToString("F0"))
              .Append("°，方位 ")
              .Append((worst % VistaAtmosphereLuts.k_SkyBandingAzimuths * 22.5f).ToString("F0"))
              .AppendLine("°）");
            sb.Append("　 |L(96)−L(192)| = ").Append(Sci(maxLo))
              .Append("　|L(192)−L(384)| = ").Append(Sci(eHi[worst]))
              .Append("　比值 ").Append((eHi[worst] > 0f ? maxLo / eHi[worst] : 0f).ToString("F2"))
              .Append("（O(h²) 应 ≈4）　阶 ").Append(ordWorst.ToString("F2"))
              .Append("（64 方向中位 ").Append(medOrd.ToString("F2")).AppendLine("）");
            sb.Append("　 → 生产档 192×108 自身的插值误差 ≈ ").Append(Sci(maxE))
              .Append("　可见阈 ").Append(Sci(k_VisibleWeber))
              .Append("　实测地板 ").Append(Sci(floor)).AppendLine();

            bool pass;
            string why;
            if (!orderOk)
            {
                pass = false;
                why  = "**阶 < 1.2，O(h²) 前提不成立** → 1.33 这个 Richardson 系数无效，"
                     + "上面那个误差估计不可引用（可能是采样点落在了参数化分支两侧）";
            }
            else if (maxE < floor)
            {
                pass = true;
                why  = "插值误差已在实测地板以下 → 192×108 有余量，可以考虑降档（移动端分级的依据）";
            }
            else if (maxE < k_VisibleWeber)
            {
                pass = true;
                why  = "插值误差低于可见阈 → 192×108 够用；"
                     + "误差/阈 = " + (maxE / k_VisibleWeber).ToString("F2") + "，这就是降分辨率的余量";
            }
            else
            {
                pass = false;
                why  = "**插值误差高于可见阈** → 192×108 不够，按 O(h²) 每翻一倍降 4 倍";
            }
            sb.Append("　 ").Append(Mark(pass)).AppendLine(why);
            return pass;
        }

        // ==================================================================== 采样
        //
        // 每次都是"烘一次 Sky-View + 派一次签名核 + 一次同步读回"。
        // 同一个 CommandBuffer 里 UAV→SRV 的转换由图形层自动插，所以不用拆两次提交。
        // GetData 是硬同步：自检要的正是这个 —— 异步读回会让"读到上一次的结果"
        // 表现为"曲线整体平移一格"，而那种错几乎不影响二阶差分，报告会照样全绿。

        static void SampleFixedDirs(VistaAtmosphereLuts luts, in VistaAtmosphereViewData view, Vector4[] dst)
            => Sample(luts, view, 0, 0f, 0f, VistaAtmosphereLuts.k_SkyBandingDirCount, dst);

        static void SampleArc(VistaAtmosphereLuts luts, in VistaAtmosphereViewData view,
                              float startDeg, float stepDeg, int count, Vector4[] dst)
            => Sample(luts, view, 1, startDeg, stepDeg, count, dst);

        static void Sample(VistaAtmosphereLuts luts, in VistaAtmosphereViewData view,
                           int mode, float startDeg, float stepDeg, int count, Vector4[] dst)
        {
            var cmd = new CommandBuffer { name = "Vista Banding Sample" };
            luts.RenderSkyViewLut(cmd, view);
            luts.RenderSkyViewBanding(cmd, view, mode, startDeg, stepDeg, count);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            luts.skyViewBandingBuffer.GetData(dst);
        }

        // ==================================================================== 统计
        //
        // 二阶差分归一化用**局部值**（Weber 对比），不是全场峰值：
        // 台阶可见性是局部对比度的事，用全场峰值归一会把暗处的可见台阶算成"很小"。

        static float MaxRelSecondDiff(Vector4[] v, int n, float lumFloor, out int worstChannel)
        {
            float best = 0f;
            worstChannel = 3;
            for (int j = 1; j < n - 1; ++j)
            {
                float den = Mathf.Max(Mathf.Abs(v[j].w), lumFloor);
                if (den <= 0f) continue;
                float s = Mathf.Abs(v[j + 1].w - 2f * v[j].w + v[j - 1].w) / den;
                if (s <= best) continue;
                best = s;

                // 哪个通道跳得最厉害（相对各自的量级）。修法不同：蓝 → Rayleigh 项，
                // 红 → 长路径 Mie / 透射率，全通道同步 → 参数化或量化。
                float bestCh = -1f;
                for (int c = 0; c < 3; ++c)
                {
                    float dc = Mathf.Max(Mathf.Abs(v[j][c]), 1e-6f);
                    float sc = Mathf.Abs(v[j + 1][c] - 2f * v[j][c] + v[j - 1][c]) / dc;
                    if (sc > bestCh) { bestCh = sc; worstChannel = c; }
                }
            }
            return best;
        }

        static float MaxRelFirstDiff(Vector4[] v, int n, float lumFloor)
        {
            float best = 0f;
            for (int j = 0; j < n - 1; ++j)
            {
                float den = Mathf.Max(Mathf.Abs(v[j].w), lumFloor);
                if (den <= 0f) continue;
                best = Mathf.Max(best, Mathf.Abs(v[j + 1].w - v[j].w) / den);
            }
            return best;
        }

        /// <summary>
        /// 收敛阶 p，由 S(2Δ)/S(Δ) = 2^p 定义。
        ///
        /// 两个分母都可能是 0（信号完全落进地板），这时**不能返回 0** ——
        /// 0 在这套判据里的含义是"真台阶"，是最严重的那一档。返回 NaN 会让
        /// 后面的比较全部为 false，同样会误判。所以返回 -1 当哨兵，
        /// 调用方的每个分支都拿 ≥ 阈值 来判，-1 自然落进"无判定/失败"侧，
        /// 而报告里 -1.00 一眼能看出是"没信号"而不是"阶等于 0"。
        /// </summary>
        static float Order(float coarse, float fine)
        {
            if (coarse <= 0f || fine <= 0f) return -1f;
            return Mathf.Log(coarse / fine, 2f);
        }

        static float Median(float[] a)
        {
            var c = (float[])a.Clone();
            System.Array.Sort(c);
            return c[c.Length / 2];
        }

        static VistaAtmosphereViewData MakeView(VistaAtmosphereParameters p, float sunElevationDeg)
        {
            float rad = sunElevationDeg * Mathf.Deg2Rad;
            // 与 HLSL 侧签名核逐字一致：Y 为 up，太阳在 +Z 方位。
            var sunDir = new Vector3(0f, Mathf.Sin(rad), Mathf.Cos(rad));
            return VistaAtmosphereViewData.Create(p, k_CameraPos, 0f, sunDir);
        }

        static string ChannelName(int c) => c switch { 0 => "R", 1 => "G", 2 => "B", _ => "亮度" };
        static string Sci(float v) => v.ToString("0.000e+0");
        static string Mark(bool ok) => ok ? "✔ " : "✘ ";
    }
}
