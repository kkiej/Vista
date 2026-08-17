using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// 大气 LUT 的数值自检。
    ///
    /// 意义：静态 LUT 是后面所有表的地基。参数化（Bruneton 的 uv &lt;-&gt; (r, mu) 映射）
    /// 一旦写反或纹素中心内缩弄错，症状会延后到"最终天空颜色不对"，那时已无从定位。
    /// 这里挑闭式解已知或有明确物理约束的纹素直接比对，把地基钉死。
    /// </summary>
    public static class VistaAtmosphereSelfTest
    {
        public struct Report
        {
            public bool passed;
            public string text;
        }

        [MenuItem("Window/Vista/Validate Atmosphere LUTs")]
        static void RunFromMenu()
        {
            VistaAtmosphereLuts luts = null;
            var report = Run(VistaAtmosphereParameters.CreateEarth(), ref luts);
            // 压成单行：部分日志转发工具（CI / MCP）只保留首行，多行报告会被截断
            string oneLine = report.text.Replace("\r", "").Replace("\n", "  |  ");
            if (report.passed) Debug.Log("[Vista] 大气 LUT 自检通过  |  " + oneLine);
            else Debug.LogError("[Vista] 大气 LUT 自检失败  |  " + oneLine);
            luts?.Dispose();
        }

        /// <summary>
        /// 烘一次静态 LUT 并比对。<paramref name="luts"/> 为 null 时会新建，非 null 时复用
        /// （窗口需要在 GUI 里持续显示这些贴图，所以所有权交给调用方）。
        /// </summary>
        public static Report Run(VistaAtmosphereParameters p, ref VistaAtmosphereLuts luts)
        {
            var res = VistaRuntimeResources.Get();
            if (res == null)
                return Fail("取不到 VistaRuntimeResources：当前管线不是 URP，或 Global Settings 尚未生成。");
            if (res.atmosphereLutCS == null)
                return Fail("atmosphereLutCS 为空：ResourcePath 未自动填充，检查 Shaders/Atmosphere/AtmosphereLut.compute 是否已导入。");

            if (luts == null)
                luts = new VistaAtmosphereLuts(res.atmosphereLutCS);
            if (!luts.isValid)
                return Fail("compute 无效：kernel 未全部找到（当前平台可能被 only_renderers 排除）。");

            luts.Invalidate();

            var cmd = new CommandBuffer { name = "Vista Atmosphere LUT (SelfTest)" };
            bool baked = luts.EnsureStaticLuts(cmd, p);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            // 第二次调用应判定为不脏——验证脏检查真的生效，而不是每帧都在白烘
            var cmd2 = new CommandBuffer();
            bool secondDirty = luts.EnsureStaticLuts(cmd2, p);
            cmd2.Release();

            var sb = new StringBuilder();
            bool ok = baked && !secondDirty;
            sb.AppendLine(Mark(ok) + " 脏检查　首次烘焙=" + baked + "，同参数二次调用=" + secondDirty + "（应为 True / False）");

            ok &= ValidateTransmittance(luts, p, sb);
            ok &= ValidateMultiScattering(luts, sb);
            ok &= ValidateSkyView(luts, p, sb);
            // AP 放最后：它要读前两张静态表，也要借 SkyView 之后已经稳定的全局绑定。
            ok &= ValidateAerialPerspective(luts, p, sb);

            return new Report { passed = ok, text = sb.ToString().TrimEnd() };
        }

        // ------------------------------------------------------------------ 天空视图
        //
        // 太阳高度取两档：正午（60°）与日落（3°）。
        // 这两档覆盖了这张表最容易出错的两种情形——正午看天顶（短路径、纯 Rayleigh）
        // 和日落看地平线（超长路径、Mie 前向峰、强红移）。
        //
        // 列约定（**与直觉相反，容易写错**）：
        //   uv.x = 0 -> lightViewCosAngle = +1 -> 视线方位**正对太阳**
        //   uv.x = 1 -> lightViewCosAngle = -1 -> 背对太阳
        // 来自 UvToParams 里的 -(coord*2-1)，沿用 Hillaire 的约定。
        static bool ValidateSkyView(VistaAtmosphereLuts luts, VistaAtmosphereParameters p, StringBuilder sb)
        {
            int w = luts.skyViewWidth;
            int h = luts.skyViewHeight;
            sb.AppendLine("── Sky-View " + w + "×" + h);

            // 相机放在离地 2 m（人眼高度），地面基准 Y=0
            var cameraPos = new Vector3(0f, 2f, 0f);

            bool ok = ValidateSkyViewParameterization(luts, p, cameraPos, sb);

            const int k_ColSun  = 0;
            int       k_ColAway = w - 1;
            int rowHorizon = RowFromV(0.49f, h);          // 地平线略上方
            int rowMid     = RowFromV(0.25f, h);          // 上半球中段

            var noon   = MakeView(p, cameraPos, 60f);
            var sunset = MakeView(p, cameraPos, 3f);

            // ---- 正午 ----
            var noonTex = RenderAndReadback(luts, noon);
            Color zenith       = noonTex.GetPixel(w / 2, 0);
            Color horizonSun   = noonTex.GetPixel(k_ColSun,  rowHorizon);
            Color horizonAway  = noonTex.GetPixel(k_ColAway, rowHorizon);
            Color nadir        = noonTex.GetPixel(w / 2, h - 1);
            bool finite = AllFinitePositive(noonTex, out float maxNoon);
            Object.DestroyImmediate(noonTex);

            // fp16 上限 65504。天空亮度（不含太阳圆盘）不应逼近它，否则这张表得升 fp32。
            // 注意上界给到 60000：低太阳的日晕本身就能到 5e4，这不是 bug。
            bool inFp16Range = maxNoon < 60000f;
            bool zenithBlue  = zenith.b > zenith.r * 1.5f;
            // 地平线路径长得多，散射累积更多 -> 更亮；同时被 Mie 洗白，蓝红比下降
            bool horizonBrighter = Lum(horizonSun) > Lum(zenith);
            bool horizonWhiter   = Ratio(horizonSun) < Ratio(zenith);
            // 地平线以下开了地面反弹，不能是全黑（环境光 SH 的下半球靠它）。
            // 天底的辐射有闭式解：L = albedo · E☉ · sin(仰角) · T(斜路径) / π。
            // 斜路径透射率用平面近似 T_zenith^(1/sin仰角)——60° 仰角下与真值差 <1%。
            // 这条不是精度测试，是**绝对单位**测试：漏乘/多乘一个 π，或把 lux 当 cd/m²，
            // 这里会立刻差一个数量级（3.14× 或 更多）。
            float sunElevSin = Mathf.Sin(60f * Mathf.Deg2Rad);
            Vector3 zenithT  = ZenithTransmittance(p);
            float slant      = 1f / sunElevSin;
            float groundIrradiance = p.sunIlluminanceLux * sunElevSin;
            var nadirExpect = new Vector3(
                p.groundAlbedo * groundIrradiance * Mathf.Pow(zenithT.x, slant) / Mathf.PI,
                p.groundAlbedo * groundIrradiance * Mathf.Pow(zenithT.y, slant) / Mathf.PI,
                p.groundAlbedo * groundIrradiance * Mathf.Pow(zenithT.z, slant) / Mathf.PI);
            float nadirErr = Mathf.Abs(Lum(nadir) - Lum(ToColor(nadirExpect)))
                           / Mathf.Max(1e-6f, Lum(ToColor(nadirExpect)));
            bool nadirOk = nadirErr < 0.1f;

            sb.AppendLine(Mark(finite)          + " 正午·全表有限非负，最大 " + maxNoon.ToString("F0") + " cd/m²");
            sb.AppendLine(Mark(inFp16Range)     + " 正午·未逼近 fp16 上限 65504（否则需升 fp32）");
            sb.AppendLine(Mark(zenithBlue)      + " 正午·天顶偏蓝　" + Fmt(zenith));
            sb.AppendLine(Mark(horizonBrighter) + " 正午·地平线比天顶亮（路径更长）　"
                                                + Lum(horizonSun).ToString("F0") + " vs 天顶 " + Lum(zenith).ToString("F0"));
            sb.AppendLine(Mark(horizonWhiter)   + " 正午·地平线被 Mie 洗白（B/R 下降）　"
                                                + Ratio(horizonSun).ToString("F2") + " vs 天顶 " + Ratio(zenith).ToString("F2"));
            sb.AppendLine(Mark(nadirOk)         + " 正午·天底辐射符合 albedo·E·T/π　" + Fmt(nadir)
                                                + "　闭式解 " + Fmt(nadirExpect) + "　亮度偏差 "
                                                + (nadirErr * 100f).ToString("F1") + "%（阈值 10%）");
            // 60° 仰角下前向峰只占几个百分点（Rayleigh 相位在 60°/120° 完全对称，
            // 差值全部来自 Mie），所以这条只做记录，判定放在日落那档。
            sb.AppendLine("　 正午·日侧地平线 " + Lum(horizonSun).ToString("F0")
                        + "　背侧 " + Lum(horizonAway).ToString("F0")
                        + "（此仰角下前向峰仅几个百分点，故不作判定）");

            // ---- 日落 ----
            var sunsetTex = RenderAndReadback(luts, sunset);
            Color sHorizonSun  = sunsetTex.GetPixel(k_ColSun,  rowHorizon);
            Color sHorizonAway = sunsetTex.GetPixel(k_ColAway, rowHorizon);
            Color sMidAway     = sunsetTex.GetPixel(k_ColAway, rowMid);
            Color sZenith      = sunsetTex.GetPixel(w / 2, 0);
            bool sunsetFinite  = AllFinitePositive(sunsetTex, out float maxSunset);
            Object.DestroyImmediate(sunsetTex);

            // 阳光切过大气最长的路径，蓝光被散射殆尽 -> 日侧地平线红移
            bool sunsetRed = sHorizonSun.r > sHorizonSun.b * 2f;
            // Mie 前向峰：低太阳下日侧与背侧的差异是数量级的，这才是有判别力的档位
            bool forwardPeak = Lum(sHorizonSun) > Lum(sHorizonAway) * 3f;
            // 红移是**方向性**的：日侧比背侧红得多。
            // （背侧地平线本身也偏红——那儿的空气同样被红化后的阳光照亮；
            //   肉眼看到的蓝紫「金星带」在更高的仰角上，见下一条。）
            bool directionalRedshift = Ratio(sHorizonSun) < Ratio(sHorizonAway) * 0.5f;
            // 背侧中高空恢复蓝紫：这是日落画面的关键对比，也是 Rayleigh 主导的证据
            bool oppositeBlueAloft = sMidAway.b > sMidAway.r;
            // 天顶变暗且仍偏蓝
            bool zenithDimmer = Lum(sZenith) < Lum(zenith) * 0.5f;

            sb.AppendLine(Mark(sunsetFinite)      + " 日落·全表有限非负，最大 " + maxSunset.ToString("F0") + " cd/m²");
            sb.AppendLine(Mark(sunsetRed)         + " 日落·日侧地平线红移 R>2B　" + Fmt(sHorizonSun));
            sb.AppendLine(Mark(forwardPeak)       + " 日落·Mie 前向峰 日侧>3×背侧　" + Lum(sHorizonSun).ToString("F0")
                                                  + " vs " + Lum(sHorizonAway).ToString("F0"));
            sb.AppendLine(Mark(directionalRedshift)+ " 日落·红移有方向性　日侧 B/R " + Ratio(sHorizonSun).ToString("F3")
                                                  + " < 背侧 " + Ratio(sHorizonAway).ToString("F3") + " 的一半");
            sb.AppendLine(Mark(oppositeBlueAloft) + " 日落·背侧中高空转蓝紫　" + Fmt(sMidAway));
            sb.AppendLine(Mark(zenithDimmer)      + " 日落·天顶比正午暗一半以上　" + Lum(sZenith).ToString("F0")
                                                  + " vs " + Lum(zenith).ToString("F0") + "　" + Fmt(sZenith));
            // 峰值日落 > 峰值正午 是**正确的**：低太阳日晕是晴空最亮的区域，
            // 而正午的日周区被 warp 稀疏的天顶行漏采了。所以不拿峰值做判定。
            sb.AppendLine("　 峰值 日落 " + maxSunset.ToString("F0") + " vs 正午 " + maxNoon.ToString("F0")
                        + "（低太阳日晕本就最亮，不作判定）");

            // 自检结束后把表恢复成一个可用状态（预览窗口要显示它）
            var cmd = new CommandBuffer();
            luts.RenderSkyViewLut(cmd, noon);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            return ok && finite && inFp16Range && zenithBlue && horizonBrighter && horizonWhiter && nadirOk
                      && sunsetFinite && sunsetRed && forwardPeak && directionalRedshift
                      && oppositeBlueAloft && zenithDimmer;
        }

        /// <summary>
        /// 参数化自检：正反映射 round-trip + 单调性 + 端点。
        ///
        /// 单调性为什么不用亮度来测：从天顶到地平线的亮度**并不单调**——背对太阳那一列在
        /// 散射角 90° 附近有 Rayleigh 相位的极小值，会出现几个百分点的回落，那是物理，不是 bug。
        /// 直接检查 warp 产出的 viewZenithCos / lightViewCos 是否单调，才是在测映射本身。
        /// </summary>
        static bool ValidateSkyViewParameterization(VistaAtmosphereLuts luts, VistaAtmosphereParameters p,
                                                    Vector3 cameraPos, StringBuilder sb)
        {
            var view = MakeView(p, cameraPos, 60f);

            var cmd = new CommandBuffer();
            luts.RenderSkyViewRoundTrip(cmd, view);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            var tex = Readback(luts.skyViewLut);
            int w = luts.skyViewWidth;
            int h = luts.skyViewHeight;

            // uv.y = 0.5 正好是地平线，那里 VistaRayIntersectsGround 的判别式恰为 0，
            // 上下分支在 fp 上会随机翻。两个分支都会落到同一纹素，所以无害，
            // 但要把跨界的两行排除，否则测的是这个必然的边界模糊而不是映射本身。
            int mid = h / 2;
            float maxErr = 0f, boundaryErr = 0f;
            for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                Color c = tex.GetPixel(x, y);
                float e = Mathf.Max(c.r, c.g) / VistaAtmosphereLuts.k_RoundTripScale;
                if (y >= mid - 1 && y <= mid) boundaryErr = Mathf.Max(boundaryErr, e);
                else maxErr = Mathf.Max(maxErr, e);
            }

            // 阈值取半个纹素：只要误差小于半个纹素，采样端就落回正确的纹素
            float threshold = 0.5f / w;
            bool okRoundTrip = maxErr < threshold;

            // ---- 单调性：B = viewZenithCos 沿 y 严格递减，A = lightViewCos 沿 x 严格递减 ----
            float worstZenithRise = 0f;
            {
                float prev = tex.GetPixel(0, 0).b;
                for (int y = 1; y < h; ++y)
                {
                    float cur = tex.GetPixel(0, y).b;
                    worstZenithRise = Mathf.Max(worstZenithRise, cur - prev);
                    prev = cur;
                }
            }
            float worstLightRise = 0f;
            {
                float prev = tex.GetPixel(0, 0).a;
                for (int x = 1; x < w; ++x)
                {
                    float cur = tex.GetPixel(x, 0).a;
                    worstLightRise = Mathf.Max(worstLightRise, cur - prev);
                    prev = cur;
                }
            }
            // fp16 量化：viewZenithCos 在 ±1 附近的间隔约 5e-4，容差取 2e-3
            bool monotonicY = worstZenithRise < 2e-3f;
            bool monotonicX = worstLightRise  < 2e-3f;

            // ---- 端点：首行=天顶(+1)、末行=天底(−1)、首列=正对太阳(+1)、末列=背对(−1) ----
            float zTop = tex.GetPixel(0, 0).b;
            float zBot = tex.GetPixel(0, h - 1).b;
            float lFirst = tex.GetPixel(0, 0).a;
            float lLast  = tex.GetPixel(w - 1, 0).a;
            Object.DestroyImmediate(tex);

            bool endpoints = zTop > 0.999f && zBot < -0.999f
                          && lFirst > 0.999f && lLast < -0.999f;

            sb.AppendLine(Mark(okRoundTrip) + " 参数化 round-trip　最大 uv 误差 " + maxErr.ToString("E2")
                        + "（阈值 " + threshold.ToString("E2") + " = 半个纹素）"
                        + "，地平线两行 " + boundaryErr.ToString("E2") + "（已排除，见注释）");
            sb.AppendLine(Mark(monotonicY)  + " warp 单调　viewZenithCos 沿 y 递减，最大回升 "
                        + worstZenithRise.ToString("E2") + "（阈值 2e-3）");
            sb.AppendLine(Mark(monotonicX)  + " warp 单调　lightViewCos 沿 x 递减，最大回升 "
                        + worstLightRise.ToString("E2") + "（阈值 2e-3）");
            sb.AppendLine(Mark(endpoints)   + " 端点　天顶 " + zTop.ToString("F4") + " / 天底 " + zBot.ToString("F4")
                        + " / 正对太阳 " + lFirst.ToString("F4") + " / 背对 " + lLast.ToString("F4")
                        + "（应为 +1 / −1 / +1 / −1）");

            return okRoundTrip && monotonicY && monotonicX && endpoints;
        }

        /// <summary>地面 → 大气顶、垂直向上的透射率闭式解。</summary>
        static Vector3 ZenithTransmittance(VistaAtmosphereParameters p)
        {
            Vector3 od = p.rayleighScattering * p.rayleighScaleHeight
                       + Vector3.Max(p.mieExtinction, p.mieScattering) * p.mieScaleHeight
                       + p.ozoneAbsorption * p.ozoneTentHalfWidth;
            return new Vector3(Mathf.Exp(-od.x), Mathf.Exp(-od.y), Mathf.Exp(-od.z));
        }

        static VistaAtmosphereViewData MakeView(VistaAtmosphereParameters p, Vector3 cameraPos, float sunElevationDeg)
        {
            float rad = sunElevationDeg * Mathf.Deg2Rad;
            // 太阳放在 +Z 方位，仰角 sunElevationDeg
            var sunDir = new Vector3(0f, Mathf.Sin(rad), Mathf.Cos(rad));
            return VistaAtmosphereViewData.Create(p, cameraPos, 0f, sunDir);
        }

        static Texture2D RenderAndReadback(VistaAtmosphereLuts luts, in VistaAtmosphereViewData view)
        {
            var cmd = new CommandBuffer();
            luts.RenderSkyViewLut(cmd, view);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            return Readback(luts.skyViewLut);
        }

        /// <summary>uv.v -> 行号。uv.y = (row + 0.5) / h。</summary>
        static int RowFromV(float v, int height) =>
            Mathf.Clamp(Mathf.FloorToInt(v * height), 0, height - 1);

        static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        /// <summary>蓝红比。越大越蓝（Rayleigh 主导），越小越红（长路径 / Mie 洗白）。</summary>
        static float Ratio(Color c) => c.b / Mathf.Max(1e-6f, c.r);

        static Color ToColor(Vector3 v) => new Color(v.x, v.y, v.z);

        static bool AllFinitePositive(Texture2D tex, out float maxValue)
        {
            bool ok = true;
            maxValue = 0f;
            var pixels = tex.GetPixels();
            foreach (var c in pixels)
            {
                if (float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b) ||
                    float.IsInfinity(c.r) || float.IsInfinity(c.g) || float.IsInfinity(c.b) ||
                    c.r < 0f || c.g < 0f || c.b < 0f)
                    ok = false;
                maxValue = Mathf.Max(maxValue, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
            }
            return ok;
        }

        // ------------------------------------------------------------------ 透射率
        static bool ValidateTransmittance(VistaAtmosphereLuts luts, VistaAtmosphereParameters p, StringBuilder sb)
        {
            int w = VistaAtmosphereLuts.k_TransmittanceWidth;
            int h = VistaAtmosphereLuts.k_TransmittanceHeight;
            var tex = Readback(luts.transmittanceLut);

            // 这三点之所以精确落在纹素中心：Bruneton 的映射把单位区间 0 / 1 对齐到
            // 首末纹素中心，所以 texel(0,0) / texel(w-1,0) 恰好是 xMu = 0 / 1，
            // 这同时也验证了那套内缩公式没写反。
            //   (0,    0  ): xR=0 -> r=bottomRadius；xMu=0 -> d=dMin -> 垂直向上
            //   (w-1,  0  ): xR=0, xMu=1 -> 地面处水平切线，路径最长
            //   (0,    h-1): xR=1 -> r=topRadius，垂直向上 -> 路径长度 0 -> T=1
            Color groundZenith  = tex.GetPixel(0, 0);
            Color groundTangent = tex.GetPixel(w - 1, 0);
            Color topZenith     = tex.GetPixel(0, h - 1);
            Object.DestroyImmediate(tex);

            // 地面垂直向上的闭式光学深度：
            //   Rayleigh  指数剖面  ∫exp(-x/H)dx = H          -> scattering * H
            //   Mie       同上                                -> extinction * H
            //   臭氧       三角帐篷  ∫tent dx = halfWidth      -> absorption * halfWidth
            Vector3 opticalDepth =
                  p.rayleighScattering * p.rayleighScaleHeight
                + Vector3.Max(p.mieExtinction, p.mieScattering) * p.mieScaleHeight
                + p.ozoneAbsorption * p.ozoneTentHalfWidth;
            var analytic = new Vector3(
                Mathf.Exp(-opticalDepth.x), Mathf.Exp(-opticalDepth.y), Mathf.Exp(-opticalDepth.z));

            float maxErr = Mathf.Max(
                Mathf.Abs(groundZenith.r - analytic.x),
                Mathf.Max(Mathf.Abs(groundZenith.g - analytic.y),
                          Mathf.Abs(groundZenith.b - analytic.z)));

            bool okZenith  = maxErr < 0.01f;
            bool okTop     = topZenith.r > 0.99f && topZenith.g > 0.99f && topZenith.b > 0.99f;
            bool okTangent = groundTangent.b < 0.05f;

            sb.AppendLine("── Transmittance " + w + "×" + h);
            sb.AppendLine(Mark(okZenith)  + " 地面·天顶　" + Fmt(groundZenith)
                        + "　闭式解 " + Fmt(analytic) + "　最大误差 " + maxErr.ToString("F5") + "（阈值 0.01）");
            sb.AppendLine(Mark(okTop)     + " 大气顶·天顶 " + Fmt(topZenith) + "　应为 (1,1,1)：路径长度为 0");
            sb.AppendLine(Mark(okTangent) + " 地面·切线　" + Fmt(groundTangent)
                        + "　蓝通道应趋近 0：最长路径，Rayleigh 把蓝光散尽");
            return okZenith && okTop && okTangent;
        }

        // ---------------------------------------------------------------- 多次散射
        static bool ValidateMultiScattering(VistaAtmosphereLuts luts, StringBuilder sb)
        {
            int n = VistaAtmosphereLuts.k_MultiScatteringSize;
            var tex = Readback(luts.multiScatteringLut);

            // 参数化：x -> cos(太阳天顶角) ∈ [-1,1]，y -> 海拔 ∈ [0, 厚度]
            Color sunDownGround = tex.GetPixel(0, 0);          // 太阳正下方
            Color sunUpGround   = tex.GetPixel(n - 1, 0);      // 太阳正头顶
            Color sunUpTop      = tex.GetPixel(n - 1, n - 1);  // 太阳正头顶，大气顶

            // 沿 muSun 单调性：太阳越高，多次散射越强。逐格比较允许极小的回退（fp16 噪声）。
            bool monotonic = true;
            float worstDrop = 0f;
            float prev = tex.GetPixel(0, 0).b;
            for (int x = 1; x < n; ++x)
            {
                float cur = tex.GetPixel(x, 0).b;
                float drop = prev - cur;
                if (drop > worstDrop) worstDrop = drop;
                prev = cur;
            }
            monotonic = worstDrop < 1e-3f;

            // 全表有限且非负、且远小于 1（入射归一化为 1，多次散射只是其中一小部分）
            bool finite = true, bounded = true;
            float maxVal = 0f;
            for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
            {
                Color c = tex.GetPixel(x, y);
                if (float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b) ||
                    float.IsInfinity(c.r) || float.IsInfinity(c.g) || float.IsInfinity(c.b) ||
                    c.r < 0f || c.g < 0f || c.b < 0f)
                    finite = false;
                maxVal = Mathf.Max(maxVal, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
            }
            bounded = maxVal < 1f;
            Object.DestroyImmediate(tex);

            // Rayleigh 蓝光散射系数是红光的 5.7 倍，多次散射必然偏蓝
            bool blueDominant = sunUpGround.b > sunUpGround.r;
            // 太阳在地平线下时地面几乎收不到多次散射
            bool darkWhenSunDown = sunDownGround.b < sunUpGround.b * 0.2f;

            sb.AppendLine("── Multi-Scattering " + n + "×" + n);
            sb.AppendLine(Mark(finite)         + " 全表有限非负，最大值 " + maxVal.ToString("F5"));
            sb.AppendLine(Mark(bounded)        + " 有界 < 1（入射归一化为 1）");
            sb.AppendLine(Mark(monotonic)      + " 沿 muSun 单调递增，最大回退 " + worstDrop.ToString("E2") + "（阈值 1e-3）");
            sb.AppendLine(Mark(blueDominant)   + " 偏蓝　太阳当顶·地面 " + Fmt(sunUpGround));
            sb.AppendLine(Mark(darkWhenSunDown)+ " 太阳在地平线下变暗　" + Fmt(sunDownGround));
            sb.AppendLine("　 太阳当顶·大气顶 " + Fmt(sunUpTop)
                        + "（高空以地面反弹为主，光谱接近中性，故不再偏蓝）");
            return finite && bounded && monotonic && blueDominant && darkWhenSunDown;
        }

        // ------------------------------------------------------ Aerial Perspective
        //
        // 这张表是 Step 1 合成的唯一输入，而它有三种错法、画面症状几乎无法区分
        // （都表现为"远景雾感不对"），所以必须分开用数值测：
        //   1) 深度分布的正反映射写反      -> 雾整体近了或远了一格
        //   2) 行进循环与共享积分器不等价  -> 与天空在地平线处接不上缝
        //   3) 切片布得太稀                -> 距离方向上出现台阶
        // 前两项有明确阈值，第三项是取舍量（Power vs Log 该选哪个），报数不判死。
        static bool ValidateAerialPerspective(
            VistaAtmosphereLuts luts, VistaAtmosphereParameters p, StringBuilder sb)
        {
            // 默认配置：32³ / far 32 km / Log / near 20 m
            var settings = new VistaAerialPerspectiveSettings();

            if (!luts.PrepareAerialPerspective(settings))
            {
                sb.AppendLine("✘ AP 不可用：AerialPerspective* kernel 未找到（检查 compute 的 only_renderers）。");
                return false;
            }

            // 相机贴地、视线水平指向 +Z（= 太阳方位），太阳仰角 60°。
            // 贴地水平是 AP 最吃力的情形：路径最长、密度最高，误差在这里最大。
            // 视锥用 Create 里的兜底（60°/16:9 正对 +Z），于是屏幕中心那根柱子的方向
            // 恰好是 (0,0,1) —— 与 SliceError 核里"固定测中心柱"的假设对齐。
            var view = MakeView(p, Vector3.zero, 60f);

            sb.AppendLine("── Aerial Perspective " + settings.width + "×" + settings.height
                        + "×" + settings.depth + "　far " + settings.maxDistanceKm.ToString("F0") + " km");

            bool ok = ValidateApTable(luts, view, settings, sb);
            ok &= ValidateApDistribution(luts, view, settings,
                      VistaAerialPerspectiveSettings.Distribution.Logarithmic, sb);
            ok &= ValidateApDistribution(luts, view, settings,
                      VistaAerialPerspectiveSettings.Distribution.Power, sb);

            // 自检把 (0, 0, z) 一列当过草稿纸（round-trip 与 slice error 都借它输出），
            // 最后重烘一次，预览窗口拿到的才是真表。
            settings.distribution = VistaAerialPerspectiveSettings.Distribution.Logarithmic;
            var cmd = new CommandBuffer();
            luts.RenderAerialPerspectiveLut(cmd, view, settings);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            return ok;
        }

        /// <summary>
        /// 整表结构性自检：有限非负 / fp16 余量 / 沿视线的累积单调性 / 灰度打包一致性 /
        /// 灰度 vs 彩色透射率的偏差量化。
        ///
        /// 单调性是这里最有价值的一条：散射沿切片只能不减、透射率只能不增，
        /// 因为两者都是"从相机到该切片"的累积量。写成"逐段独立"或把 throughput
        /// 漏乘一次，都会在这一条上立刻暴露，而画面上只是"雾淡了点"。
        /// </summary>
        static bool ValidateApTable(VistaAtmosphereLuts luts, in VistaAtmosphereViewData view,
                                    VistaAerialPerspectiveSettings settings, StringBuilder sb)
        {
            settings.distribution = VistaAerialPerspectiveSettings.Distribution.Logarithmic;

            var cmd = new CommandBuffer();
            luts.RenderAerialPerspectiveLut(cmd, view, settings);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            var scatter = Readback3D(luts.apScatterLut);
            var trans   = Readback3D(luts.apTransmittanceLut);
            int w = scatter.width, h = scatter.height, d = scatter.depth;

            // ---- 有限非负 + fp16 余量 ----
            bool finite = true;
            float maxScatter = 0f;
            for (int z = 0; z < d; ++z)
            for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                Color c = scatter[x, y, z];
                Color t = trans[x, y, z];
                if (!Sane(c) || !Sane(t)) finite = false;
                // 透射率必须落在 [0,1]：>1 意味着积分符号反了
                if (t.r > 1.001f || t.g > 1.001f || t.b > 1.001f) finite = false;
                maxScatter = Mathf.Max(maxScatter, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
            }
            bool inFp16Range = maxScatter < 60000f;

            // ---- 沿切片的累积单调性 ----
            // 散射用**相对**回退：不同柱子的量级差几个数量级（贴地朝下的柱子几乎全零），
            // 绝对阈值要么放过真 bug、要么被噪声打爆。低于整表峰值 1e-4 的柱子跳过，
            // 那里 fp16 只剩几位有效数字，测的是量化而不是逻辑。
            float floor = maxScatter * 1e-4f;
            float worstScatterDrop = 0f, worstTransRise = 0f;
            for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                float prevS = Lum(scatter[x, y, 0]);
                float prevT = trans[x, y, 0].g;
                for (int z = 1; z < d; ++z)
                {
                    float curS = Lum(scatter[x, y, z]);
                    if (prevS > floor)
                        worstScatterDrop = Mathf.Max(worstScatterDrop, (prevS - curS) / prevS);
                    prevS = curS;

                    // 透射率在 [0,1]，且短路径处贴近 1，绝对差就够；
                    // fp16 在 1.0 附近的间隔是 4.9e-4
                    float curT = trans[x, y, z].g;
                    worstTransRise = Mathf.Max(worstTransRise, curT - prevT);
                    prevT = curT;
                }
            }
            bool scatterMonotonic = worstScatterDrop < 2e-3f;
            bool transMonotonic   = worstTransRise   < 2e-3f;

            // ---- 灰度打包一致性：scatter.a 必须等于 mean(transmittance.rgb) ----
            // 移动端路径只采散射表一张，全靠这个 alpha。写错的症状是
            // "关掉 coloredTransmittance 之后雾的浓度整体不对"，而两条路径本该只差色偏。
            float worstGreyPack = 0f;
            for (int z = 0; z < d; ++z)
            for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                Color t = trans[x, y, z];
                float mean = (t.r + t.g + t.b) / 3f;
                worstGreyPack = Mathf.Max(worstGreyPack, Mathf.Abs(scatter[x, y, z].a - mean));
            }
            bool greyPackOk = worstGreyPack < 2e-3f;

            // ---- 灰度 vs 彩色透射率的偏差：量化移动端近似的代价 ----
            // 这是 AerialPerspective.hlsl 里"PC 存彩色、移动端回到灰度"那段取舍的数据来源。
            int cx = w / 2, cy = h / 2;
            var greyReport = new StringBuilder();
            float greyErrFar = 0f;
            foreach (float targetKm in new[] { 4f, 12f, 32f })
            {
                int z = NearestApSlice(settings, targetKm);
                Color t = trans[cx, cy, z];
                float grey = scatter[cx, cy, z].a;
                float err = 0f;
                for (int c = 0; c < 3; ++c)
                    err = Mathf.Max(err, Mathf.Abs(t[c] - grey) / Mathf.Max(t[c], 1e-3f));
                greyErrFar = Mathf.Max(greyErrFar, err);
                greyReport.Append(greyReport.Length > 0 ? "，" : "")
                          .Append(ApDistance(settings, z).ToString("F1")).Append(" km ")
                          .Append((err * 100f).ToString("F1")).Append('%');
            }

            sb.AppendLine(Mark(finite)          + " 全表有限非负、透射率 ≤ 1，散射峰值 "
                                                + maxScatter.ToString("F0") + " cd/m²");
            sb.AppendLine(Mark(inFp16Range)     + " 未逼近 fp16 上限 65504");
            sb.AppendLine(Mark(scatterMonotonic)+ " 散射沿切片非减（累积量），最大相对回退 "
                                                + worstScatterDrop.ToString("E2") + "（阈值 2e-3）");
            sb.AppendLine(Mark(transMonotonic)  + " 透射率沿切片非增，最大回升 "
                                                + worstTransRise.ToString("E2") + "（阈值 2e-3）");
            sb.AppendLine(Mark(greyPackOk)      + " 灰度打包 scatter.a = mean(T.rgb)，最大偏差 "
                                                + worstGreyPack.ToString("E2") + "（阈值 2e-3）");
            // 不判定：这是取舍的量，不是对错。移动端接受它，PC 不接受。
            sb.AppendLine("　 灰度 vs 彩色透射率·中心柱最大通道偏差　" + greyReport
                        + "（移动端灰度近似的代价，见 AerialPerspective.hlsl；不作判定）");

            return finite && inFp16Range && scatterMonotonic && transMonotonic && greyPackOk;
        }

        /// <summary>
        /// 单一分布模式的自检：正反映射 round-trip、切片距离单调性与两端点、
        /// texW 的半片对齐，以及对高步数参考解的重建误差。
        ///
        /// 两种分布都跑，因为"32 片够不够、Power 还是 Log"要靠 errMid 这一列数据决定，
        /// 而不是靠看图。
        /// </summary>
        static bool ValidateApDistribution(
            VistaAtmosphereLuts luts, in VistaAtmosphereViewData view,
            VistaAerialPerspectiveSettings settings,
            VistaAerialPerspectiveSettings.Distribution mode, StringBuilder sb)
        {
            settings.distribution = mode;
            int d = settings.depth;
            string tag = mode == VistaAerialPerspectiveSettings.Distribution.Logarithmic
                       ? "Log(near " + settings.effectiveNearKm.ToString("F3") + " km)"
                       : "Power(k=" + settings.powerExponent.ToString("F1") + ")";

            // 顺序是被约束的：SliceError 要把正式表当 SRV 读，所以必须先烘正式表；
            // RoundTrip 会覆盖散射表的 (0,0,z)，所以必须排在 SliceError 之后。
            var cmd = new CommandBuffer();
            luts.RenderAerialPerspectiveLut(cmd, view, settings);
            luts.RenderApSliceError(cmd, view, settings);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            var errCol = Readback3D(luts.apTransmittanceLut);

            var cmd2 = new CommandBuffer();
            luts.RenderApRoundTrip(cmd2, view, settings);
            Graphics.ExecuteCommandBuffer(cmd2);
            cmd2.Release();
            var rtCol = Readback3D(luts.apScatterLut);

            // ---- round-trip + w 一致性 ----
            float maxRt = 0f, maxWErr = 0f;
            for (int z = 0; z < d; ++z)
            {
                Color c = rtCol[0, 0, z];
                maxRt = Mathf.Max(maxRt, c.r / VistaAtmosphereLuts.k_RoundTripScale);
                maxWErr = Mathf.Max(maxWErr, Mathf.Abs(c.b - z / (float)(d - 1)));
            }
            // 阈值取 1e-3，比"半片"（0.5/31 ≈ 0.016）紧一个数量级：
            // 这是纯解析的正反映射，pow/log 在 fp32 上的往返误差应在 1e-6 量级。
            // 用半片当阈值会让"分支写反但两种分布碰巧接近"的情形溜过去。
            bool okRt = maxRt < 1e-3f;
            // _VistaApSize.w 必须是 1/(depth-1)。填成 1/depth 的症状是最远片差一格，
            // 而那一格在 Log 分布下是 27% 的距离差。
            bool okW = maxWErr < 1e-3f;

            // ---- 切片距离：严格递增 + 两端点 ----
            float worstDistDrop = 0f;
            for (int z = 1; z < d; ++z)
                worstDistDrop = Mathf.Max(worstDistDrop, rtCol[0, 0, z - 1].g - rtCol[0, 0, z].g);
            bool distIncreasing = worstDistDrop <= 0f;

            float dNear = rtCol[0, 0, 0].g;
            float dFar  = rtCol[0, 0, d - 1].g;
            float expectNear = mode == VistaAerialPerspectiveSettings.Distribution.Logarithmic
                             ? settings.effectiveNearKm : 0f;
            // fp16 在 32 附近的间隔是 0.03125 -> 相对 1e-3。阈值取 3e-3 留三倍余量。
            bool okNear = Mathf.Abs(dNear - expectNear) <= Mathf.Max(3e-3f * expectNear, 1e-4f);
            bool okFar  = Mathf.Abs(dFar - settings.maxDistanceKm) <= 3e-3f * settings.maxDistanceKm;

            // ---- texW 半片对齐 ----
            // 切片 i 代表 w = i/(depth-1)，它的纹素中心在 (i+0.5)/depth。
            // 少了这 0.5 就是整条深度轴偏半片，症状是"雾比几何体近半片"，
            // 在切片稀疏的远端表现为山体边缘一圈没有雾。
            float texWNear = rtCol[0, 0, 0].a;
            float texWFar  = rtCol[0, 0, d - 1].a;
            bool okTexW = Mathf.Abs(texWNear - 0.5f / d) < 1e-3f
                       && Mathf.Abs(texWFar - (d - 0.5f) / d) < 1e-3f;

            // ---- 对高步数参考解的误差 ----
            // errCenter 测**行进循环本身**（步数够不够、是否与共享积分器等价）；
            // errMid 测**切片分布**（三线性插值在两片之间还原得多准）。见核里的注释。
            // 距离由 C# 自己算（ApDistance 复刻了 packedParams），核里的两个通道
            // 让给原始亮度 —— 只有它能区分"LUT 偏高"和"参考解偏低"。
            float maxErrCenter = 0f, atCenterKm = 0f, cLut = 0f, cRef = 0f;
            float maxErrMid = 0f, atMidKm = 0f;
            for (int z = 0; z < d; ++z)
            {
                Color e = errCol[0, 0, z];
                if (e.r > maxErrCenter)
                {
                    maxErrCenter = e.r;
                    atCenterKm = ApDistance(settings, z);
                    cRef = e.b; cLut = e.a;
                }
                // 最后一片没有"下一片"，核里 errMid 恒为 0，排除掉免得拉低统计
                if (z < d - 1 && e.g > maxErrMid)
                {
                    maxErrMid = e.g;
                    atMidKm = ApDistance(settings, z, 0.5f);
                }
            }
            // 5%：段内解析积分 + 每段 ≤16 步对散射这种低频量应该远好于此。
            // 超了说明 VISTA_AP_STEPS_MAX 在该分布的远端段被打满（Log 尤其容易）。
            bool okErrCenter = maxErrCenter < 0.05f;
            // 2%：errMid 的分母是整根柱子的雾量总量（见核里的注释），所以这个数
            // 直接读作"画面上的雾亮错了百分之几"。平滑渐变上 1% 左右的对比度就能
            // 看出带状，2% 留一倍余量。这一项同时是 Task #6 选 Power 还是 Log 的依据。
            bool okErrMid = maxErrMid < 0.02f;

            sb.AppendLine("　── 分布 " + tag);
            sb.AppendLine(Mark(okRt)            + " 　round-trip 最大 |Δw| " + maxRt.ToString("E2") + "（阈值 1e-3）");
            sb.AppendLine(Mark(okW)             + " 　w = i/(depth−1) 最大误差 " + maxWErr.ToString("E2") + "（阈值 1e-3）");
            sb.AppendLine(Mark(distIncreasing)  + " 　切片距离严格递增，最大回退 " + worstDistDrop.ToString("E2"));
            sb.AppendLine(Mark(okNear && okFar) + " 　两端点　切片 0 = " + dNear.ToString("F4")
                                                + " km（应为 " + expectNear.ToString("F4") + "）／切片 " + (d - 1)
                                                + " = " + dFar.ToString("F3") + " km（应为 "
                                                + settings.maxDistanceKm.ToString("F3") + "）");
            sb.AppendLine(Mark(okTexW)          + " 　texW 半片对齐　" + texWNear.ToString("F5") + " / "
                                                + texWFar.ToString("F5") + "（应为 " + (0.5f / d).ToString("F5")
                                                + " / " + ((d - 0.5f) / d).ToString("F5") + "）");
            sb.AppendLine(Mark(okErrCenter)     + " 　切片中心 vs 256 步参考解　最大 "
                                                + (maxErrCenter * 100f).ToString("F2") + "% @ "
                                                + atCenterKm.ToString("F3") + " km（阈值 5%，测行进循环）"
                                                + "　LUT " + cLut.ToString("E3") + " vs 参考 " + cRef.ToString("E3"));
            sb.AppendLine(Mark(okErrMid)        + " 　切片中点（三线性插值）最大 "
                                                + (maxErrMid * 100f).ToString("F2") + "% @ "
                                                + atMidKm.ToString("F3") + " km（阈值 2%，相对柱子总量，测切片分布）");

            // 区间诊断（核里第 1、2 行）。不判定，只报数 —— 它回答的是"上面那些
            // 百分比是不是在比较同一段路"，而这个前提一旦破了，百分比本身就没意义。
            Color g1 = errCol[1, 0, 0];
            Color g2 = errCol[2, 0, 0];
            sb.AppendLine("　 区间　tBottom " + g1.r.ToString("E3") + " km／tTop " + g1.g.ToString("F1")
                        + " km／相机海拔 " + g1.b.ToString("F3") + " m／up·ray " + g1.a.ToString("F5"));
            sb.AppendLine("　 区间　earthShadow @3 m " + g2.r.ToString("F0") + " @1 km " + g2.g.ToString("F0")
                        + "／线性探针 ref(0.02)=" + g2.b.ToString("E3")
                        + " ref(0.04)=" + g2.a.ToString("E3")
                        + " 比值 " + (g2.b > 1e-6f ? (g2.a / g2.b).ToString("F3") : "n/a") + "（应≈2）");

            return okRt && okW && distIncreasing && okNear && okFar && okTexW && okErrCenter && okErrMid;
        }

        /// <summary>C# 侧复刻 <c>VistaApSliceCoordToDistance</c>，用来定位"最接近某个距离"的切片。
        /// <paramref name="offset"/> 用来取片与片之间的位置（0.5 = 中点），与核里的 wMid 对齐。</summary>
        static float ApDistance(VistaAerialPerspectiveSettings s, int slice, float offset = 0f)
        {
            // 与 packedParams 完全一致，包括那两个 Max 钳制 —— 否则这里算出来的距离
            // 和 GPU 上的不是同一个东西，报告的"@ x km"就成了假数据。
            Vector4 pp = s.packedParams;
            float w = (slice + offset) / (s.depth - 1);
            return pp.w > 0.5f ? pp.x * Mathf.Pow(pp.y / pp.x, w)
                               : pp.y * Mathf.Pow(Mathf.Max(w, 0f), pp.z);
        }

        static int NearestApSlice(VistaAerialPerspectiveSettings s, float targetKm)
        {
            int best = 0;
            float bestErr = float.MaxValue;
            for (int z = 0; z < s.depth; ++z)
            {
                float e = Mathf.Abs(ApDistance(s, z) - targetKm);
                if (e < bestErr) { bestErr = e; best = z; }
            }
            return best;
        }

        static bool Sane(Color c) =>
            !float.IsNaN(c.r) && !float.IsNaN(c.g) && !float.IsNaN(c.b) &&
            !float.IsInfinity(c.r) && !float.IsInfinity(c.g) && !float.IsInfinity(c.b) &&
            c.r >= 0f && c.g >= 0f && c.b >= 0f;

        // ------------------------------------------------------------------ 工具
        internal static Texture2D Readback(RTHandle handle)
        {
            var rt = handle.rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            tex.Apply(false, false);
            RenderTexture.active = prev;
            return tex;
        }

        /// <summary>读回一整个 3D 表。索引顺序 [x, y, z]。</summary>
        internal sealed class Volume
        {
            public readonly int width, height, depth;
            readonly Color[] m_Pixels;

            internal Volume(int w, int h, int d)
            {
                width = w; height = h; depth = d;
                m_Pixels = new Color[w * h * d];
            }

            public Color this[int x, int y, int z] => m_Pixels[(z * height + y) * width + x];

            // 不 using System：那会让 Object 在 UnityEngine.Object 与 object 之间歧义，
            // 而本文件到处在用 Object.DestroyImmediate。
            internal void SetSlice(int z, Color[] slice) =>
                System.Array.Copy(slice, 0, m_Pixels, z * width * height, width * height);
        }

        /// <summary>
        /// 3D 表的整体读回。
        ///
        /// 不能走 <see cref="Readback"/> 那条 <c>RenderTexture.active</c> + <c>ReadPixels</c> 的路：
        /// 那套只认 2D，对 3D RT 会静默只读到第 0 片，症状是"所有切片数值一模一样"——
        /// 而那恰好和"深度分布没生效"长得一样，会把人引向完全错误的方向。
        /// 这里用 <c>Graphics.CopyTexture</c> 把每一片搬到 2D 临时 RT 再读。
        /// 32 次同步在 Editor 自检里无所谓，而它的行为在各图形 API 上一致 ——
        /// AsyncGPUReadback 对 3D 的行距对齐并不统一，错了是整表错位，更难查。
        /// </summary>
        internal static Volume Readback3D(RTHandle handle)
        {
            var src = handle.rt;
            int w = src.width, h = src.height, d = src.volumeDepth;
            var vol = new Volume(w, h, d);

            var tmp = RenderTexture.GetTemporary(w, h, 0, src.graphicsFormat);
            var tex = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true);
            var prev = RenderTexture.active;
            for (int z = 0; z < d; ++z)
            {
                Graphics.CopyTexture(src, z, 0, tmp, 0, 0);
                RenderTexture.active = tmp;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                tex.Apply(false, false);
                vol.SetSlice(z, tex.GetPixels());
            }
            RenderTexture.active = prev;
            Object.DestroyImmediate(tex);
            RenderTexture.ReleaseTemporary(tmp);
            return vol;
        }

        static string Mark(bool ok) => ok ? "✔" : "✘";


        static string Fmt(Color c) =>
            "(" + c.r.ToString("F5") + ", " + c.g.ToString("F5") + ", " + c.b.ToString("F5") + ")";

        static string Fmt(Vector3 v) =>
            "(" + v.x.ToString("F5") + ", " + v.y.ToString("F5") + ", " + v.z.ToString("F5") + ")";

        static Report Fail(string message) => new Report { passed = false, text = "✘ " + message };
    }
}
