using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// CPU 解析透射率 ↔ GPU Transmittance LUT 的逐通道对账。
    ///
    /// ── 为什么必须有这条自检 ──
    ///
    /// <see cref="VistaSunTransmittance"/> 是 <c>AtmosphereDef.hlsl</c> 里那套物理的
    /// **第二份实现**。重复实现的代价是"改一边忘另一边"，而症状极其隐蔽：
    /// 天空还是对的，只有物体的直射光色悄悄偏了，看起来像是美术调过的。
    /// 这条自检把两份实现钉在一起。
    ///
    /// ── 它同时是一把独立的尺子 ──
    ///
    /// 两条路径**没有任何共用代码**：CPU 走 40 段梯形解析积分，GPU 走
    /// UvToRMu 参数化 + compute 烘表 + fp16 存储 + 双线性采样。它们对上，
    /// 说明的不只是"CPU 抄对了"，还包括"Bruneton 映射没写反、内缩公式没搞错、
    /// fp16 够用"。#7 里两次误判（126% 的 errCenter、20 组配置上的 0.27% 恒定偏置）
    /// 都是因为只有一把尺子，尺子自己坏了就无从发现。
    ///
    /// ── 三项判据 ──
    ///
    /// A. **纹素中心对账**：把 LUT 的正向映射反解回 (r, mu)，逐纹素比。
    ///    绕开双线性插值，测的是**积分器本身**。阈值由 fp16 存储精度导出。
    /// B. **实用工况对账**：相机 2 m、太阳从 0.5° 扫到 90°，按 GPU 的
    ///    正向映射 + 双线性采样取值，比 CPU 解析值。测的是**灯色会不会和天空对不上**，
    ///    误差里包含 LUT 插值误差 —— 这才是画面上真正会看到的那一份。
    /// C. **接缝系数**：color·intensity 是否等于 E_exposed/π，正午值是否是 0.971。
    ///    纯算术，但这个数会被写进文档和面试回答里，钉住它防止有人"顺手把 π 修掉"。
    ///
    /// 另外带一项**灵敏度对照**：把 CPU 侧的臭氧关掉，确认 A 项会因此判失败。
    /// 不做这一步的话，"两边都漏了臭氧"会以满分通过 —— 而臭氧正是黄昏蓝紫色的来源。
    /// </summary>
    public static class VistaSunTransmittanceSelfTest
    {
        [MenuItem("Window/Vista/Validate Sun Transmittance")]
        static void RunFromMenu()
        {
            var report = Run(VistaAtmosphereParameters.CreateEarth());
            string oneLine = report.text.Replace("\r", "").Replace("\n", "  |  ");
            if (report.passed) Debug.Log("[Vista] 太阳透射率对账通过  |  " + oneLine);
            else Debug.LogError("[Vista] 太阳透射率对账失败  |  " + oneLine);
        }

        /// <summary>
        /// A 项阈值。LUT 存 fp16，尾数 10 位，相对精度 2^-11 = 4.88e-4；
        /// T ∈ [0,1]，故存储量化的绝对误差上界就是 4.88e-4。
        /// 取 4 倍余量到 2e-3，把 GPU/CPU 各自 exp 实现的差异（相对 1e-6 量级，
        /// 41 次累加后仍远小于量化）一并盖住。
        ///
        /// 这个数是**导出**的，不是调出来的 —— 若实测超过它，说明的一定不是精度不够，
        /// 而是两份实现真的分叉了。
        /// </summary>
        const float k_TexelAbsThreshold = 2e-3f;

        /// <summary>
        /// B 项的相对误差门。沿用全项目共用的 Weber 1% 可见阈：
        /// 直射光的相对误差会 1:1 传到受光面的像素值上。
        /// </summary>
        const float k_UseRelThreshold = 0.01f;

        /// <summary>
        /// B 项的绝对可见性豁免。
        ///
        /// 太阳压到地平线时 T 掉到 1e-3 量级，此时相对误差会被放大到无意义
        /// （分母自己都快没了），但画面上那点直射光根本看不见。判据必须落回绝对量：
        /// 受光面亮度 = albedo·lux·T/π，正午满照（T=1）是参考白
        /// 0.3·120000/π ≈ 1.146e4 cd/m²，所以 |ΔT| 就是"错了参考白的百分之几"。
        /// 取 0.1% —— 比 Weber 阈还严一个数量级，豁免掉的东西保证看不见。
        ///
        /// 这条豁免的写法直接沿用 #7 AP 定档时的空段豁免：**不能**用全局 max 判，
        /// 也不能在相对误差爆掉的地方硬撑相对误差。
        /// </summary>
        const float k_UseAbsExemption = 1e-3f;

        public struct Report
        {
            public bool passed;
            public string text;
        }

        public static Report Run(VistaAtmosphereParameters p)
        {
            var res = VistaRuntimeResources.Get();
            if (res == null)
                return Fail("取不到 VistaRuntimeResources：当前管线不是 URP，或 Global Settings 尚未生成。");
            if (res.atmosphereLutCS == null)
                return Fail("atmosphereLutCS 为空：检查 Shaders/Atmosphere/AtmosphereLut.compute 是否已导入。");

            var luts = new VistaAtmosphereLuts(res.atmosphereLutCS);
            try
            {
                if (!luts.isValid)
                    return Fail("compute 无效：kernel 未全部找到。");

                luts.Invalidate();
                var cmd = new CommandBuffer { name = "Vista Transmittance (SelfTest)" };
                luts.EnsureStaticLuts(cmd, p);
                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Release();

                var tex = VistaAtmosphereSelfTest.Readback(luts.transmittanceLut);
                int w = tex.width, h = tex.height;
                var pixels = tex.GetPixels();
                Object.DestroyImmediate(tex);

                var sb = new StringBuilder();
                sb.AppendLine("── Transmittance LUT " + w + "×" + h
                            + "　CPU 积分段数 " + VistaSunTransmittance.k_OpticalDepthSamples);

                bool ok = true;
                ok &= ValidateTexelCenters(p, pixels, w, h, sb);
                ok &= ValidateSensitivity(p, pixels, w, h, sb);
                ok &= ValidateUseCases(p, pixels, w, h, sb);
                ok &= ValidateSeamFactor(p, sb);

                return new Report { passed = ok, text = sb.ToString().TrimEnd() };
            }
            finally
            {
                luts.Dispose();
            }
        }

        // ==================================================================== A 纹素中心

        /// <summary>
        /// 逐纹素反解 (r, mu) 后与 CPU 解析值比。
        /// 全表扫（256×64 = 16384 纹素 × 41 段），Editor 里 ~0.1 s，不必抽样 ——
        /// 抽样会给"最大误差"留下漏掉极值的空间（#7 踩过：粗抽样静默丢失极值）。
        /// </summary>
        static bool ValidateTexelCenters(
            VistaAtmosphereParameters p, Color[] pixels, int w, int h, StringBuilder sb)
        {
            float worst = 0f;
            int worstX = 0, worstY = 0;
            Vector3 worstCpu = Vector3.zero;
            Color worstGpu = default;

            // 分通道也记一份：三个通道的 σ 差 6 倍，只报总 max 会掩盖"只有一个通道错"。
            var perChannel = new float[3];

            for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                TexelToRMu(p, x, y, w, h, out float r, out float mu);

                Vector3 od = VistaSunTransmittance.OpticalDepthToTopBoundary(p, r, mu);
                var cpu = new Vector3(Mathf.Exp(-od.x), Mathf.Exp(-od.y), Mathf.Exp(-od.z));
                Color gpu = pixels[y * w + x];

                var e = new Vector3(
                    Mathf.Abs(cpu.x - gpu.r), Mathf.Abs(cpu.y - gpu.g), Mathf.Abs(cpu.z - gpu.b));
                perChannel[0] = Mathf.Max(perChannel[0], e.x);
                perChannel[1] = Mathf.Max(perChannel[1], e.y);
                perChannel[2] = Mathf.Max(perChannel[2], e.z);

                float m = Mathf.Max(e.x, Mathf.Max(e.y, e.z));
                if (m > worst)
                {
                    worst = m; worstX = x; worstY = y; worstCpu = cpu; worstGpu = gpu;
                }
            }

            bool ok = worst < k_TexelAbsThreshold;
            sb.AppendLine("── A 纹素中心对账（绕开双线性，测积分器本身）");
            sb.AppendLine(Mark(ok) + " 全表 " + (w * h) + " 纹素　最大绝对误差 "
                        + worst.ToString("E3") + "（阈值 " + k_TexelAbsThreshold.ToString("E1")
                        + " = fp16 量化 4.88e-4 的 4 倍余量）");
            sb.AppendLine("    逐通道 R/G/B " + perChannel[0].ToString("E3") + " / "
                        + perChannel[1].ToString("E3") + " / " + perChannel[2].ToString("E3"));
            sb.AppendLine("    最差纹素 (" + worstX + "," + worstY + ")　CPU " + Fmt(worstCpu)
                        + "　GPU " + Fmt(worstGpu));
            return ok;
        }

        // ============================================================== 灵敏度对照

        /// <summary>
        /// 把 CPU 侧的臭氧吸收清零后重跑 A 项，**期望它失败**。
        ///
        /// 这不是在测大气，是在测 A 项那把尺子有没有牙。若 A 项对"少一整个吸收项"
        /// 都无感，那它通过与否就没有信息量了 —— 这正是 #7 记过的
        /// "未覆盖路径的假通过"。选臭氧而不是随便扰动某个数：臭氧是黄昏蓝紫
        /// 对侧色的唯一来源，也是最容易在移植时被当成"可以先不管"漏掉的一项。
        /// </summary>
        static bool ValidateSensitivity(
            VistaAtmosphereParameters p, Color[] pixels, int w, int h, StringBuilder sb)
        {
            var noOzone = p.Clone();
            noOzone.ozoneAbsorption = Vector3.zero;

            float worst = 0f;
            for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                // 反解仍用真参数：只改被测物理，不改参数化，否则误差来源就说不清了
                TexelToRMu(p, x, y, w, h, out float r, out float mu);
                Vector3 od = VistaSunTransmittance.OpticalDepthToTopBoundary(noOzone, r, mu);
                Color gpu = pixels[y * w + x];
                worst = Mathf.Max(worst, Mathf.Max(
                    Mathf.Abs(Mathf.Exp(-od.x) - gpu.r),
                    Mathf.Max(Mathf.Abs(Mathf.Exp(-od.y) - gpu.g),
                              Mathf.Abs(Mathf.Exp(-od.z) - gpu.b))));
            }

            // 期望**超过**阈值
            bool ok = worst > k_TexelAbsThreshold;
            sb.AppendLine("── 灵敏度对照（CPU 侧关掉臭氧，A 项应当报错）");
            sb.AppendLine(Mark(ok) + " 关臭氧后最大误差 " + worst.ToString("E3")
                        + "　应 > " + k_TexelAbsThreshold.ToString("E1")
                        + "（说明 A 项确实在测臭氧，不是无感通过）");
            return ok;
        }

        // ==================================================================== B 实用工况

        /// <summary>
        /// 相机 2 m（人眼高度），太阳仰角扫一遍。GPU 侧走**正向映射 + 双线性**，
        /// 即着色器里 <c>VistaSampleTransmittanceToSun</c> 的完整路径。
        /// </summary>
        static bool ValidateUseCases(
            VistaAtmosphereParameters p, Color[] pixels, int w, int h, StringBuilder sb)
        {
            // 0.5° 起：地平线以下由 k_MinMuSun 归零，不在对账范围内。
            // 密集覆盖 0~10°（日出日落的全部色彩变化都挤在这一段）。
            float[] elevations = { 0.5f, 1f, 2f, 3f, 5f, 8f, 12f, 20f, 30f, 45f, 60f, 90f };
            var cameraPos = new Vector3(0f, 2f, 0f);

            sb.AppendLine("── B 实用工况对账（相机 2 m，含 LUT 双线性插值误差）");
            sb.AppendLine("    仰角      CPU T (R,G,B)                GPU T (R,G,B)                行内最大相对  最大 ΔT  判定（逐通道）");

            bool ok = true;
            float worstRelJudged = 0f;
            int exemptChannels = 0;
            var chName = new[] { "R", "G", "B" };

            foreach (float deg in elevations)
            {
                float rad = deg * Mathf.Deg2Rad;
                var sunDir = new Vector3(0f, Mathf.Sin(rad), Mathf.Cos(rad));
                var view = VistaAtmosphereViewData.Create(p, cameraPos, 0f, sunDir);

                float r = view.viewHeightKm;
                float mu = Vector3.Dot(view.viewPosKm / r, view.sunDirection);

                Vector3 cpu = VistaSunTransmittance.Evaluate(p, r, mu);
                Vector3 gpu = SampleLutBilinear(p, pixels, w, h, r, mu);

                // 判定必须**逐通道**做。
                //
                // 曾经写成"全通道最大相对 < 门 或 全通道最大 ΔT < 豁免"，那是个假通过的洞：
                // 三通道的 T 在低太阳时差三个数量级（实测 0.5° 是 0.168 / 0.022 / 0.0003），
                // 最大相对来自蓝通道、最大 ΔT 来自红通道，于是「红的绝对量小」会去替
                // 「蓝的相对量大」担保 —— 两个数根本不来自同一个通道。真要出问题时
                // （某个通道同时相对超标且绝对可见），这个 OR 照样放它过去。
                float rowWorstRel = 0f, rowWorstAbs = 0f;
                bool rowOk = true;
                string rowWhy = "相对达标";

                for (int c = 0; c < 3; ++c)
                {
                    float a = c == 0 ? cpu.x : (c == 1 ? cpu.y : cpu.z);
                    float b = c == 0 ? gpu.x : (c == 1 ? gpu.y : gpu.z);
                    float abs = Mathf.Abs(a - b);
                    float rel = abs / Mathf.Max(1e-9f, a);

                    rowWorstRel = Mathf.Max(rowWorstRel, rel);
                    rowWorstAbs = Mathf.Max(rowWorstAbs, abs);

                    bool relPass = rel < k_UseRelThreshold;
                    // 同一通道的绝对量看不见时才豁免它自己
                    bool absExempt = abs < k_UseAbsExemption;

                    if (relPass)
                    {
                        worstRelJudged = Mathf.Max(worstRelJudged, rel);
                    }
                    else if (absExempt)
                    {
                        exemptChannels++;
                        rowWhy = chName[c] + " 绝对豁免(rel " + (rel * 100f).ToString("F2") + "%)";
                    }
                    else
                    {
                        rowOk = false;
                        rowWhy = chName[c] + " 超标";
                    }
                }
                ok &= rowOk;

                sb.AppendLine("    " + Mark(rowOk) + " " + deg.ToString("F1").PadLeft(5) + "°  "
                            + Fmt(cpu) + "  " + Fmt(gpu) + "  "
                            + (rowWorstRel * 100f).ToString("F3").PadLeft(7) + "%  "
                            + rowWorstAbs.ToString("E2") + "  " + rowWhy);
            }

            sb.AppendLine("    走相对判据的通道里最大相对误差 " + (worstRelJudged * 100f).ToString("F3")
                        + "%（门 " + (k_UseRelThreshold * 100f).ToString("F0") + "%）；"
                        + "走绝对豁免的通道 " + exemptChannels + " 个（该通道自身 |ΔT| < "
                        + k_UseAbsExemption.ToString("E0") + "，即参考白 1.146e4 cd/m² 的 0.1% 以内）");
            return ok;
        }

        // ==================================================================== C 接缝系数

        static bool ValidateSeamFactor(VistaAtmosphereParameters p, StringBuilder sb)
        {
            float exposure = VistaAtmosphereViewData.ExposureFromEV100(VistaAtmosphereViewData.k_DefaultEV100);

            // 正午满照：T=1，此时 color·intensity 应当 = lux·exposure/π
            var noon = VistaSunTransmittance.ComputeLightParams(p, Vector3.one, exposure);
            float expected = p.sunIlluminanceLux * exposure / Mathf.PI;
            float err = Mathf.Abs(noon.intensity - expected) / expected;
            bool okNoon = err < 1e-5f && noon.color == Color.white;

            sb.AppendLine("── C 单位接缝　EV100=" + VistaAtmosphereViewData.k_DefaultEV100
                        + "　exposure=" + exposure.ToString("E4"));
            sb.AppendLine(Mark(okNoon) + " T=(1,1,1) 时 intensity=" + noon.intensity.ToString("F5")
                        + "　闭式 lux·exposure/π=" + expected.ToString("F5")
                        + "　color=" + Fmt(noon.color));
            sb.AppendLine("    这个 0.971 就是「Unity 惯用的正午平行光强度 1」的物理出处"
                        + "（Sunny-16 的近似），也是宿主工程的灯不需要改单位制的原因。");

            // 逐通道恒等式：color·intensity == T·lux·exposure/π，含色度归一化的往返
            var t = new Vector3(0.31f, 0.12f, 0.035f);   // 一组典型日落 T
            var sunset = VistaSunTransmittance.ComputeLightParams(p, t, exposure);
            Vector3 roundTrip = new Vector3(
                sunset.color.r, sunset.color.g, sunset.color.b) * sunset.intensity;
            Vector3 target = t * (p.sunIlluminanceLux * exposure / Mathf.PI);
            float rtErr = 0f;
            for (int c = 0; c < 3; ++c)
            {
                float a = c == 0 ? roundTrip.x : (c == 1 ? roundTrip.y : roundTrip.z);
                float b = c == 0 ? target.x : (c == 1 ? target.y : target.z);
                rtErr = Mathf.Max(rtErr, Mathf.Abs(a - b) / Mathf.Max(1e-8f, b));
            }
            bool okRt = rtErr < 1e-5f;
            // color 三通道必须都落在 [0,1]：Light.color 是非 HDR 选择器，
            // 超出的分量一被面板碰到就会被夹掉，表现为"点一下灯色画面突然变暗"。
            bool okRange = sunset.color.r <= 1f && sunset.color.g <= 1f && sunset.color.b <= 1f
                        && sunset.color.r >= 0f && sunset.color.g >= 0f && sunset.color.b >= 0f;

            sb.AppendLine(Mark(okRt && okRange) + " 日落 T=" + Fmt(t)
                        + " → color=" + Fmt(sunset.color) + " × intensity=" + sunset.intensity.ToString("F5")
                        + "　色度/幅度往返最大相对误差 " + rtErr.ToString("E2")
                        + "　color ∈ [0,1]=" + okRange);

            // 地平线以下：intensity 必须严格为 0，且色度保持合法（白）而不是黑
            var below = VistaSunTransmittance.ComputeLightParams(p, Vector3.zero, exposure);
            bool okBelow = below.intensity == 0f && below.color == Color.white;
            sb.AppendLine(Mark(okBelow) + " 太阳在地平线下　intensity=" + below.intensity.ToString("F5")
                        + "　color=" + Fmt(below.color) + "（色度给白而非黑：面板上看得出「灯没坏，只是强度 0」）");

            return okNoon && okRt && okRange && okBelow;
        }

        // ==================================================================== 参数化镜像

        /// <summary>
        /// 纹素坐标 -> (r, mu)。<c>AtmosphereLut.compute</c> 的 TransmittanceLut kernel
        /// 前四行 + <c>VistaTransmittanceLutUvToRMu</c> 的逐行镜像。
        /// </summary>
        static void TexelToRMu(
            VistaAtmosphereParameters p, int x, int y, int w, int h, out float r, out float mu)
        {
            // 纹素中心 -> 内缩前的单位区间
            float xMu = UnitRangeFromTexCoord((x + 0.5f) / w, w);
            float xR  = UnitRangeFromTexCoord((y + 0.5f) / h, h);

            float bottom = p.bottomRadius, top = p.topRadius;
            float H   = Mathf.Sqrt(Mathf.Max(0f, top * top - bottom * bottom));
            float rho = H * xR;
            r = Mathf.Sqrt(Mathf.Max(0f, rho * rho + bottom * bottom));

            float dMin = top - r;
            float dMax = rho + H;
            float d = dMin + xMu * (dMax - dMin);

            mu = d == 0f ? 1f : (H * H - rho * rho - d * d) / (2f * r * d);
            mu = Mathf.Clamp(mu, -1f, 1f);
        }

        /// <summary>(r, mu) -> LUT uv。<c>VistaRMuToTransmittanceLutUv</c> 的镜像。</summary>
        static Vector2 RMuToUv(VistaAtmosphereParameters p, float r, float mu, int w, int h)
        {
            float bottom = p.bottomRadius, top = p.topRadius;
            float H   = Mathf.Sqrt(Mathf.Max(0f, top * top - bottom * bottom));
            float rho = Mathf.Sqrt(Mathf.Max(0f, r * r - bottom * bottom));
            float d   = VistaSunTransmittance.DistanceToTopBoundary(p, r, mu);
            float dMin = top - r;
            float dMax = rho + H;

            float xMu = dMax == dMin ? 0f : (d - dMin) / (dMax - dMin);
            float xR  = H == 0f ? 0f : rho / H;

            return new Vector2(
                TexCoordFromUnitRange(Mathf.Clamp01(xMu), w),
                TexCoordFromUnitRange(Mathf.Clamp01(xR), h));
        }

        static float TexCoordFromUnitRange(float x, int size) =>
            0.5f / size + x * (1f - 1f / size);

        static float UnitRangeFromTexCoord(float u, int size) =>
            (u - 0.5f / size) / (1f - 1f / size);

        /// <summary>
        /// 双线性采样 + clamp 寻址，复刻 <c>sampler_LinearClamp</c>。
        ///
        /// 不用 <c>Texture2D.GetPixelBilinear</c>：它的边缘行为取决于贴图的 wrapMode，
        /// 而读回来的这张 Texture2D 的 wrapMode 是默认值 Repeat —— 在 xMu 接近 1
        /// （地平线方向，恰好是最关心的低太阳）时会把对侧边缘混进来，
        /// 于是"实测误差"里就掺进了一个采样器差异，那就不再是在测两份物理实现了。
        /// 自己写四取样，寻址行为看得见。
        /// </summary>
        static Vector3 SampleLutBilinear(
            VistaAtmosphereParameters p, Color[] pixels, int w, int h, float r, float mu)
        {
            Vector2 uv = RMuToUv(p, r, mu, w, h);

            float fx = uv.x * w - 0.5f;
            float fy = uv.y * h - 0.5f;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;

            Color c00 = At(pixels, w, h, x0,     y0);
            Color c10 = At(pixels, w, h, x0 + 1, y0);
            Color c01 = At(pixels, w, h, x0,     y0 + 1);
            Color c11 = At(pixels, w, h, x0 + 1, y0 + 1);

            Color a = Color.LerpUnclamped(c00, c10, tx);
            Color b = Color.LerpUnclamped(c01, c11, tx);
            Color o = Color.LerpUnclamped(a, b, ty);
            return new Vector3(o.r, o.g, o.b);
        }

        static Color At(Color[] pixels, int w, int h, int x, int y) =>
            pixels[Mathf.Clamp(y, 0, h - 1) * w + Mathf.Clamp(x, 0, w - 1)];

        // ==================================================================== 杂项

        static string Mark(bool ok) => ok ? "✔" : "✘";

        static string Fmt(Vector3 v) =>
            "(" + v.x.ToString("F5") + ", " + v.y.ToString("F5") + ", " + v.z.ToString("F5") + ")";

        static string Fmt(Color c) =>
            "(" + c.r.ToString("F5") + ", " + c.g.ToString("F5") + ", " + c.b.ToString("F5") + ")";

        static Report Fail(string message) => new Report { passed = false, text = "✘ " + message };
    }
}
