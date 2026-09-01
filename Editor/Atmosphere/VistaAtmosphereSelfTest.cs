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
    /// <remarks>
    /// 声明成 partial 是为了让雾的判据（<c>VistaFogInApSelfTest.cs</c>）直接复用
    /// 这里的 <see cref="Readback3D"/> / <see cref="ReduceApCurve"/> / <see cref="ApBandText"/>
    /// 与那三个阈值常量，而不是照抄一份。本项目已经写死的一条纪律是
    /// 「同一个量的第二份实现连 8 行的辅助函数也算」—— 误差归约这套东西尤其不能有两份，
    /// 因为两份漂移之后的症状是「雾的判据和 AP 的判据对同一张表给出不同的百分比」，
    /// 而那时没有任何办法判断哪一份是对的。
    /// </remarks>
    public static partial class VistaAtmosphereSelfTest
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
        // 前两项有明确阈值；第三项过去只报数不判死，现在由 ValidateApSliceBudget
        // 的分段判据定档（见那里的注释：为什么聚合最大值可以被刷，而分段不能）。
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
            ok &= ValidateApDistribution(luts, view, p, settings,
                      VistaAerialPerspectiveSettings.Distribution.Logarithmic, sb);
            ok &= ValidateApDistribution(luts, view, p, settings,
                      VistaAerialPerspectiveSettings.Distribution.Power, sb);
            ok &= ValidateApSliceBudget(luts, p, settings, sb);

            // 自检把 (0, 0, z) 一列当过草稿纸（round-trip 与 slice error 都借它输出），
            // 最后重烘一次，预览窗口拿到的才是真表。
            // Prepare 必须重来：上面的扫描按 depth 重新分配过，若不还原，
            // 下面这次烘焙会拿 depth=32 的 _VistaApSize 去写一张 64 片的表，
            // 后 32 片留着上一档的残留 —— 预览里表现为"远处雾突然跳一下"。
            settings.distribution = VistaAerialPerspectiveSettings.Distribution.Logarithmic;
            luts.PrepareAerialPerspective(settings);
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
            // 注意 scatter 从 #18 起存的是**预曝光**辐亮度（见 AerialPerspectiveLut）。
            // fp16 余量必须判在**存储值**上（那才是 fp16 装的东西），
            // 而报告里给的峰值解回绝对 cd/m²，好跟"晒到太阳的雪地 ~3e4"这类量级对照。
            bool finite = true;
            float maxStored = 0f;
            for (int z = 0; z < d; ++z)
            for (int y = 0; y < h; ++y)
            for (int x = 0; x < w; ++x)
            {
                Color c = scatter[x, y, z];
                Color t = trans[x, y, z];
                if (!Sane(c) || !Sane(t)) finite = false;
                // 透射率必须落在 [0,1]：>1 意味着积分符号反了
                if (t.r > 1.001f || t.g > 1.001f || t.b > 1.001f) finite = false;
                maxStored = Mathf.Max(maxStored, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
            }
            bool inFp16Range = maxStored < 60000f;
            float maxScatter = maxStored / Mathf.Max(view.exposure, 1e-30f);

            // ---- 沿切片的累积单调性 ----
            // 散射用**相对**回退：不同柱子的量级差几个数量级（贴地朝下的柱子几乎全零），
            // 绝对阈值要么放过真 bug、要么被噪声打爆。低于整表峰值 1e-4 的柱子跳过，
            // 那里 fp16 只剩几位有效数字，测的是量化而不是逻辑。
            // 用 maxStored 而不是 maxScatter：下面比较的 Lum() 读的是**存储值**，
            // 地板和被比较的量必须同一个单位制 —— 混用会让地板整体差 4e4 倍，
            // 症状是"单调性判据突然一条柱子都不跳过"或"全部跳过"，两种都不报错。
            float floor = maxStored * 1e-4f;
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
                                                + maxScatter.ToString("E2") + " cd/m²（存储值 "
                                                + maxStored.ToString("E2") + "，预曝光）");
            // 这条判据自 #18 改成预曝光存储后**极难失败**：要失败得让绝对辐亮度超过
            // 2.6E+009 cd/m²（60000 / 2.54E-005）。点名说明它已经从"可能触发的判据"
            // 退成"回归探针"—— 不写明的话，一条永远绿的断言看起来和一条有效断言一样。
            sb.AppendLine(Mark(inFp16Range)     + " 存储值未逼近 fp16 上限 65504（余量 "
                                                + (65504f / Mathf.Max(maxStored, 1e-30f)).ToString("F0")
                                                + "×；预曝光后此条已退为回归探针）");
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
            in VistaAtmosphereParameters p,
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
            // errMid / errMidT 测**切片分布**（三线性插值在两片之间还原得多准）。见核里的注释。
            // 距离由 C# 自己算（ApDistance 复刻了 packedParams），核里的通道
            // 让给原始亮度 —— 只有它能区分"LUT 偏高"和"参考解偏低"。
            var curve = ReduceApCurve(errCol, settings, p, view.exposure);

            // 5%：段内解析积分 + 每段 ≤16 步对散射这种低频量应该远好于此。
            // 超了说明 VISTA_AP_STEPS_MAX 在该分布的远端段被打满（Log 尤其容易）。
            bool okErrCenter = curve.maxErrCenter < k_ApErrCenterMax;
            string midText  = ApBandText(curve, false, k_ApErrMidMax,  out bool okErrMid);
            string midTText = ApBandText(curve, true,  k_ApErrMidTMax, out bool okErrMidT);
            bool okPack = curve.packMaxResidual < k_ApPackResidualMax;

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
            sb.AppendLine(Mark(okErrCenter)     + " 　切片中心 vs 4096 步参考解　最大 "
                                                + Pct(curve.maxErrCenter) + " @ "
                                                + curve.atErrCenterKm.ToString("F3") + " km（阈值 "
                                                + Pct(k_ApErrCenterMax) + "，测行进循环）"
                                                + "　LUT " + curve.centerLut.ToString("E3")
                                                + " vs 参考 " + curve.centerRef.ToString("E3"));
            sb.AppendLine(Mark(okErrMid)        + " 　errMid 逐段 max（" + k_ApBandLegend + "，相对柱子总量）"
                                                + "　判两条：实现＝实测≤固有+端点+" + Pct(k_ApMidDecompAllow)
                                                + "（恒等式）；分布＝固有<" + Pct(k_ApErrMidMax) + "　" + midText);
            sb.AppendLine(Mark(okErrMidT)       + " 　errMidT 逐段 max（相对 T 自身）"
                                                + "　同上两条，分布门 " + Pct(k_ApErrMidTMax) + "　" + midTText);
            sb.AppendLine(Mark(okPack)          + " 　(3,0,z) 通道打包一致　max|errMidT·max(refT,1e-4) − |lutT−refT|| = "
                                                + curve.packMaxResidual.ToString("E2") + "（阈值 "
                                                + k_ApPackResidualMax.ToString("E0") + " = fp16 在 T≈1 处的 ~3 ulp）");
            sb.AppendLine("　 ΔT 绝对可见度豁免　" + curve.exemptT + "/" + curve.sampleCount
                        + " 片，其中最大 |ΔT| " + curve.exemptMaxDeltaT.ToString("E2")
                        + "（门 " + k_ApVisibleDeltaT.ToString("E0") + "，不判定）");
            sb.AppendLine("　 台阶强度 max|ΔerrMid| " + Pct(curve.maxMidStep) + " @ "
                        + curve.atMidStepKm.ToString("F3") + " km　"
                        + "每柱行进步数 " + curve.marchSteps + "（不判定，见 ReduceApCurve）");
            sb.AppendLine("　 errMid 曲线（中点 km→%）　" + ApCurveText(errCol, settings));

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

            return okRt && okW && distIncreasing && okNear && okFar && okTexW
                && okErrCenter && okErrMid && okErrMidT && okPack;
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

        // ------------------------------------------------- AP 切片分布：分段判据
        //
        // 为什么不能用"整根柱子的 max errMid"当判据 —— 这是这一节唯一重要的事：
        // 那个聚合值可以被"把切片堆在积分已经饱和的远端、饿死近端"刷出漂亮的数字。
        //   · 远端：透射率衰减完了，累积量走上平台，线性插值误差自然趋近 0；
        //   · 近端：绝对雾量只有柱子总量的千分之几，除以柱子总量之后照样很小。
        // 两端都读作"小"，于是一个近处糊成一坨的分布能拿到全场最低的聚合值。
        // 分段之后每一段各自过阈值，这条路就堵死了：近端那段的分母虽然还是柱子总量，
        // 但同段内只有两三片可比，插不准就是插不准，藏不到别的段里去。
        //
        // 分段的边界按**画面里有什么**切，不按整数：
        //   脚下 <50 m       角色、脚下地面、近处石头（第三人称相机的常驻内容）
        //   近景 0.05–0.5 km 能跑到的那块地、树
        //   中景 0.5–4 km    建筑轮廓、树线（ER 里"看到远处那座教堂"的距离）
        //   远景 4–32 km     远山层叠与天际线
        static readonly float[] k_ApBandEdgesKm = { 0f, 0.05f, 0.5f, 4f, 32f };
        static readonly string[] k_ApBandMarks  = { "①", "②", "③", "④" };
        const string k_ApBandLegend = "①脚下<50m ②近景.05-.5km ③中景.5-4km ④远景4-32km";

        /// <summary>5%：段内解析积分 + 每段 ≤16 步对散射这种低频量应该远好于此。
        /// 这一项是**前置门**而不是并列判据：它一旦超标，说明行进本身就不准，
        /// 后面的 errMid 混着行进误差，不同分布之间不再可比。</summary>
        const float k_ApErrCenterMax = 0.05f;

        /// <summary>2%：分母是整根柱子的雾量总量（见核里的注释），而晒到太阳的漫反射面
        /// 约 0.3·120000/π ≈ 1.1e4 cd/m²，与饱和后的雾量同量级 —— 所以这个数可以直接
        /// 读作"画面亮错了百分之几"。平滑渐变上 1% 对比度就能看出带状，2% 留一倍余量。</summary>
        const float k_ApErrMidMax = 0.02f;

        /// <summary>1%：透射率的相对误差**就是**它给几何体项带来的相对误差
        /// （final = geometry·T + inScatter），所以直接用 Task #6 那把 Weber 尺子。</summary>
        const float k_ApErrMidTMax = 0.01f;

        /// <summary>可见性下限，用在"某段没有中点样本"时（见 <see cref="ApBandVisible"/>）。
        /// 与上面两个阈值同源：都是 1% 的 Weber 对比度阈。</summary>
        const float k_ApVisibleFloor = 0.01f;

        /// <summary>透射率误差的**绝对**可见度豁免线。
        ///
        /// 为什么必须有这一条：核里 errMidT = |ΔT| / max(refT, 1e-4)。浓雾把 refT 压到
        /// 1e-4 这个地板附近时，分母不再是 refT 而是地板本身，errMidT 报出来的是
        /// 「|ΔT| 相对地板有多大」——一个与真实可见度无关的数字。#18 实测：H 档
        /// （无限标高浓雾）refT↓0.00E+000 而 T④ 报 49.83%，那不是误差，是地板。
        /// 这正是本项目点过名的坑：「拿一条落在尺子地板之下的预测去分类，
        /// 会在读数落进地板内时产生假失败」。
        ///
        /// 门槛怎么定 —— **两条下界都要满足，取更大的那个**：
        ///   (1) 可见度：T 只作用在几何体项上（final = geometry·T + inScatter），
        ///       所以 |ΔT| 造成的画面变化上界是 |ΔT| · L_几何，而最亮的合理背景就是
        ///       visibleWhite（日照下 albedo 0.3 的漫反射面）。1% 的 Weber 阈
        ///       ⇒ 可见度这一侧允许到 1e-2。
        ///   (2) **这个量自己的测量地板**：deltaT 是从 (3,0,z) 里三个 fp16 通道
        ///       代数还原出来的（errMidT × max(refT,1e-4)），fp16 在 T≈1 处
        ///       1 ulp = 2⁻¹¹ = 4.9e-4，三次圆整 ⇒ ~1.5e-3 的纯圆整噪声。
        ///       门设在 1.5e-3 以下，分类结果就由圆整噪声决定。
        /// 取 3e-3：比地板 (2) 高一倍，仍是可见度 (1) 的 1/3（≤0.3% 参考白）。
        /// #18 之前这里是 1e-3 —— 它满足 (1) 但**落在 (2) 的地板之下**，
        /// 正是本项目点过名的「尺子的地板与被测量同量级时，尺子会自己伪造一个结论」。
        /// 症状是压线读数：②H 档 T④ 报 9.99%，紧贴在豁免线上方。
        ///
        /// ⚠ 这个值与 <see cref="k_ApPackResidualMax"/> 数值相同（都是 3 fp16 ulp），
        /// 但**不能**写成引用它：若哪天误差表换成 fp32，地板 (2) 会消失，
        /// packResidual 该降到 1e-6 量级，而这条豁免线该按可见度 (1) 重推到 1e-2 ——
        /// 两者往相反方向走。
        ///
        /// 为什么判 |ΔT| 而不判「refT 与 lutT 双双低于门」：后者在 refT 恰好落在
        /// 地板上方（比如 2e-3）时不触发，而那时 |ΔT| = 1e-3 会被报成 50% ——
        /// 洞正好开在最需要豁免的那一档。判 ΔT 本身则无条件地界定了可见效果的上界。</summary>
        const float k_ApVisibleDeltaT = 3e-3f;

        /// <summary>(3,0,z) 三个 T 相关通道的互相一致阈。
        ///
        /// 核里 errMidT 是用全精度的 lutTMid / refTMid 算出来的，之后三个量各自被
        /// 圆到 fp16 才落表。于是 errMidT·max(refT,1e-4) 与 |lutT − refT| 的差只剩
        /// refT / lutT 两次独立圆整：fp16 在 T≈1 处 1 ulp = 2⁻¹¹ = 4.9e-4，两个量
        /// 各一次、deltaT 里再借 refT 一次，取 3 ulp ≈ 1.5e-3，留余量到 3e-3。
        /// 而这条判据真要抓的失败（通道对错、写错纹素）残差是 0.1~1 的量级 ——
        /// 分辨力有 100 倍，不是压线判据。</summary>
        const float k_ApPackResidualMax = 3e-3f;

        /// <summary>误差分解的允差：要求 实测 ≤ 固有 + 端点上界 + 这个数。
        ///
        /// 这不是"画质裕度"，而是**存储圆整**的允差。分解本身是代数恒等式加一次
        /// 三角不等式，理论上严格成立；会破的唯一原因是三个量各自被圆到 fp16：
        /// 实测 errMid 在核里用全精度算完才落表，而固有/端点两项是 C# 侧拿
        /// 落过 fp16 的端点值重算的。fp16 在 1 ulp = 2⁻¹¹ = 4.9e-4（相对），
        /// S 通道的分母 farC 本身就是柱子最大值、被除的都不超过它，
        /// T 通道非豁免片的量级是 O(1)，两边都归一到"相对 1"这同一个尺度上，
        /// 四次圆整取 3e-3 ≈ 6 ulp。
        ///
        /// ⚠ 它与 <see cref="k_ApPackResidualMax"/> 数值相同但**不能写成引用**：
        /// 那个是三通道互验的残差门，这个是误差分解的允差。若哪天误差表换成 fp32，
        /// 两者一个降到 1e-7 量级、另一个仍受 AP 表本身 fp16 的限制而不动。
        ///
        /// 分辨力：这条判据要抓的失败（读错纹素、通道对错、interp 不是两端点的中点）
        /// 残差在 0.1~1 的量级，比允差高两个数量级以上，不是压线判据。</summary>
        const float k_ApMidDecompAllow = 3e-3f;

        /// <summary>定档时的余量系数：能看见的段要压到阈值的一半以内才算候选。
        ///
        /// 理由不是"越严越好"，而是**这套阈值本身就是可见阈**，压线通过意味着
        /// "刚好看不见"，任何一点没测到的变化都会把它推到看得见那边。而这次扫描
        /// 只测了两个视角、两种大气状态、一根柱子（屏幕中心），覆盖面远不够支撑压线。
        /// 已经有直接证据：d=16 Log 20m 在主视角 T④0.49%，换到海拔 1 km／太阳 10°
        /// 就变 0.46%、errMid④ 从 0.77% 涨到 0.91% —— 同一档配置在两个视角上的
        /// 摆动量就有 0.14 个百分点，和阈值本身同量级。</summary>
        const float k_ApSelectMargin = 0.5f;

        struct ApCurve
        {
            public float maxErrCenter, atErrCenterKm, centerRef, centerLut;
            public float[] bandMid, bandMidT, bandMidAtKm;
            public int[] bandCount;
            /// <summary>T 通道的有效样本数：<see cref="bandCount"/> 减去被
            /// <see cref="k_ApVisibleDeltaT"/> 豁免的那些片。两个通道的计数必须分开，
            /// 否则「这一段的 T 误差全在可见度之下」和「这一段一片都没有」会挤在
            /// 同一个 0 上，而它们的正确结论只是碰巧相同（都免检）—— 挤在一起以后
            /// 报表读不出来到底豁免了什么。</summary>
            public int[] bandCountT;
            public float maxMidStep, atMidStepKm;
            public int marchSteps;
            // 逐片的绝对量，给可见性下限用（见 ApBandVisible）
            public float[] midKm, refS, refT, lutT;
            /// <summary>切片**中心**处的灰度量（不是中点）：参考解与 LUT 各一份，
            /// 长度为 depth。前两个已解码成绝对 cd/m²，后两个是无量纲透射率。
            /// 这四个数组是误差分解的全部输入，见 <see cref="FillApMidDecomposition"/>。</summary>
            public float[] refCc, lutCc, refTc, lutTc;
            /// <summary>逐片实测的中点重建误差（S 通道相对柱子总量、T 通道相对 refT），
            /// 与 <see cref="exact"/> / <see cref="bound"/> 一一配对、同分母。
            /// bandMid / bandMidT 是它们的逐段 max —— 聚合值留着当报表的头条数字，
            /// 但判定要逐片做（见 <see cref="ApBandText"/>）。</summary>
            public float[] mid, midT;
            /// <summary>**固有**重建误差：4096 步参考解自己在这个切片分布上的中点插值误差。
            /// 一个 LUT 值都不含 —— 它回答的是「这个分布最好能做到多少」。同分母。</summary>
            public float[] exact, exactT;
            /// <summary>两个端点各自的行进误差对中点的贡献上界（三角不等式）。同分母。
            /// 见 <see cref="FillApMidDecomposition"/>。</summary>
            public float[] bound, boundT;
            /// <summary>这一片的中点落在哪一段。存下来是为了 ApBandText 不用第二次算
            /// ApBandIndex —— 同一个分段一旦有两处实现，边界差半片的症状是
            /// 「某一段的判定和它打印的片数对不上」，极难看出来。</summary>
            public int[] bandIdx;
            /// <summary>这一片的 |ΔT| 落在 <see cref="k_ApVisibleDeltaT"/> 之下（T 通道免检）。</summary>
            public bool[] exemptSlice;
            /// <summary>errMid 的分母，等于核里的 <c>farC</c>（这根柱子最远片的累积入散射，
            /// 已解码成绝对 cd/m²，带 1e-4 地板）。误差分解必须用**同一个**分母，
            /// 否则分解出来的两项和实测不同基，相加比较没有意义。</summary>
            public float farC;
            public int sampleCount;
            public float visibleWhite;
            /// <summary>被绝对可见度豁免的中点数与其中最大的 |ΔT|。报出来，
            /// 免得「T 全绿」既可能是真的准、也可能是整段被豁免掉了。</summary>
            public int exemptT;
            public float exemptMaxDeltaT;
            /// <summary>打包一致性残差：errMidT·max(refT,1e-4) 与 |lutT − refT| 的最大差。
            /// 前者是核里那次除法的分子按代数还原，后者是同一个纹素里两个原始通道相减，
            /// 两者本该相等。不等只有一个原因：(3,0,z) 的通道对应错了。
            /// 这条把一个**从来没人读过**的通道（lutT）变成了活判据 ——
            /// 「一个默认关闭、又没有判据覆盖的开关，等于一段永远不会被发现写错的代码」。</summary>
            public float packMaxResidual;
        }

        /// <summary>把 SliceError 的两行原始输出压成分段统计 + 台阶强度 + 成本代理。</summary>
        /// <param name="exposure">烘这张表时用的曝光倍率。核里三个辐亮度诊断通道
        /// （refC / lutC / refSMid）存的是**预曝光**值 —— 因为它们的目标纹理是 fp16，
        /// 而 #18 实测浓雾对着低太阳时连参考解都会被存储钳到 65504。这里乘回去，
        /// 让下游（报告、visibleWhite 归一化、注入量分母）全程用绝对 cd/m²，
        /// 阈值一个都不用动。曝光取自 <see cref="VistaAtmosphereViewData.exposure"/>，
        /// 全项目只有那一处算 1/(1.2·2^EV100)。</param>
        static ApCurve ReduceApCurve(Volume errCol, VistaAerialPerspectiveSettings s,
                                     in VistaAtmosphereParameters p, float exposure)
        {
            float decode = 1f / Mathf.Max(exposure, 1e-30f);
            int d = s.depth, bands = k_ApBandMarks.Length;
            var c = new ApCurve
            {
                bandMid     = new float[bands],
                bandMidT    = new float[bands],
                bandMidAtKm = new float[bands],
                bandCount   = new int[bands],
                bandCountT  = new int[bands],
                marchSteps  = ApMarchStepsPerColumn(s),
                midKm       = new float[Mathf.Max(d - 1, 1)],
                refS        = new float[Mathf.Max(d - 1, 1)],
                refT        = new float[Mathf.Max(d - 1, 1)],
                lutT        = new float[Mathf.Max(d - 1, 1)],
                mid         = new float[Mathf.Max(d - 1, 1)],
                midT        = new float[Mathf.Max(d - 1, 1)],
                exact       = new float[Mathf.Max(d - 1, 1)],
                exactT      = new float[Mathf.Max(d - 1, 1)],
                bound       = new float[Mathf.Max(d - 1, 1)],
                boundT      = new float[Mathf.Max(d - 1, 1)],
                bandIdx     = new int[Mathf.Max(d - 1, 1)],
                exemptSlice = new bool[Mathf.Max(d - 1, 1)],
                // 这四个是**逐切片中心**的，所以长度是 d 而不是 d−1。
                refCc       = new float[d],
                lutCc       = new float[d],
                refTc       = new float[d],
                lutTc       = new float[d],
                // 判"看不看得见"的分母：日照下 albedo 0.3 的漫反射面。
                // 取它而不是取柱子总量，是因为柱子总量本身会随切片布局变
                // —— 那样又变成一个能被布局刷的判据了。
                visibleWhite = p.groundAlbedo * p.sunIlluminanceLux / Mathf.PI,
            };
            for (int b = 0; b < bands; ++b) { c.bandMid[b] = float.NaN; c.bandMidT[b] = float.NaN; }

            float prevMid = float.NaN;
            for (int z = 0; z < d; ++z)
            {
                Color e = errCol[0, 0, z];
                if (e.r > c.maxErrCenter)
                {
                    c.maxErrCenter = e.r;
                    c.atErrCenterKm = ApDistance(s, z);
                    c.centerRef = e.b * decode; c.centerLut = e.a * decode;
                }

                // 切片中心的四个量。S 侧本来就在 (0,0,z) 的 zw 里（预曝光，解回绝对值）；
                // T 侧是 #18 为误差分解新加的 (4,0,z)。这些是**端点**值，
                // 下面 refS/refT/lutT 那三个是**中点**值，两组不可混用。
                c.refCc[z] = e.b * decode;
                c.lutCc[z] = e.a * decode;
                Color e4 = errCol[4, 0, z];
                c.refTc[z] = e4.r;
                c.lutTc[z] = e4.g;

                // errMid 的分母：核里取的是这根柱子最远片的 LUT 累积量（带 1e-4 地板）。
                // 这里必须逐位取同一个量 —— 误差分解要除它。
                if (z == d - 1)
                    c.farC = Mathf.Max(e.a * decode, 1e-4f);

                // 最后一片没有"下一片"，核里两个中点通道恒为 0；算进去会把统计洗低。
                if (z >= d - 1) continue;

                float dMid = ApDistance(s, z, 0.5f);
                int b = ApBandIndex(dMid);
                float mid  = e.g;
                Color e3   = errCol[3, 0, z];
                float midT = e3.r / VistaAtmosphereLuts.k_ApErrorScale;

                c.midKm[z] = dMid;
                c.refS[z]  = e3.a * decode;   // 参考解在该中点的累积入散射（解回绝对 cd/m²，单调增）
                c.refT[z]  = e3.g;   // 参考解在该中点的灰度透射率（单调减）
                c.lutT[z]  = e3.b;   // LUT 在该中点插值出的灰度透射率
                c.mid[z]   = mid;
                c.midT[z]  = midT;
                c.bandIdx[z] = b;
                c.sampleCount = z + 1;

                c.bandCount[b]++;
                if (float.IsNaN(c.bandMid[b]) || mid > c.bandMid[b])
                {
                    c.bandMid[b] = mid;
                    c.bandMidAtKm[b] = dMid;
                }

                // ---- T 通道：先还原 |ΔT|，再决定这一片算不算 ----
                // deltaT 是核里 errMidT = |ΔT| / max(refT, 1e-4) 那次除法的分子，
                // 按代数乘回去 —— 不是 |lutT − refT| 的第二份实现，那个量下一行才用，
                // 而且只用来验通道没对错（packMaxResidual）。
                float deltaT = midT * Mathf.Max(c.refT[z], 1e-4f);
                c.packMaxResidual = Mathf.Max(c.packMaxResidual,
                                              Mathf.Abs(deltaT - Mathf.Abs(c.lutT[z] - c.refT[z])));

                if (deltaT < k_ApVisibleDeltaT)
                {
                    c.exemptSlice[z] = true;
                    c.exemptT++;
                    c.exemptMaxDeltaT = Mathf.Max(c.exemptMaxDeltaT, deltaT);
                }
                else
                {
                    c.bandCountT[b]++;
                    if (float.IsNaN(c.bandMidT[b]) || midT > c.bandMidT[b]) c.bandMidT[b] = midT;
                }

                // 相邻区间的下垂量之差。errMid 本身是"插值比真值低多少"，
                // 而**均匀**的下垂只是整幅画面雾偏淡一点点，人眼没有参照物、看不出来；
                // 真正能看出来的是下垂量在片界处突然变化，也就是重建曲线的斜率不连续
                // （Mach band 的成因）。所以台阶的实际强度是这个差值。
                // 报它、但判据仍用 max errMid：后者是前者的上界（最多差 2 倍），
                // 拿上界当门槛只会误杀、不会漏过，方向是安全的。
                if (!float.IsNaN(prevMid) && Mathf.Abs(mid - prevMid) > c.maxMidStep)
                {
                    c.maxMidStep = Mathf.Abs(mid - prevMid);
                    c.atMidStepKm = dMid;
                }
                prevMid = mid;
            }
            FillApMidDecomposition(ref c);
            return c;
        }

        /// <summary>把 errMid / errMidT 这两个读数**分解**成两个可分别归因的部分。
        ///
        /// ---- 为什么需要它（这一节是这条判据存在的全部理由）----
        /// errMid 是中点线性插值的误差，它有两个完全不同的来源：
        ///   a) 切片分布在这一段太疏 —— 曲线本身弯，两片之间就是插不准。
        ///      这是**表达能力上限**，不是 bug；只有换分布（或换成近层 froxel 体）才能改。
        ///   b) 行进循环本身积错了 —— 存进去的两个端点值就偏，插值只是把偏差搬到中间。
        /// 一个固定的 2% / 1% 平门无法区分这两者。#18 实测：视角③ 的中景段读到 19.25%，
        /// 而同一份代码在视角① 上读 0.4% —— 平门只会把前者判成失败，
        /// 却说不出到底是"这个 Log 分布在 877 m 的雾拐点上装不下"还是"march 写错了"。
        /// 而这两个结论导向的行动截然不同（前者 → #19 近层雾体；后者 → 去查积分器）。
        ///
        /// ---- 分解式（是恒等式，不是模型）----
        /// 核里 errMid = |½(L_i + L_{i+1}) − R_mid| / farC，其中 L 是 LUT 的切片**中心**值、
        /// R_mid 是 4096 步参考解在 w 中点的值。插一项参考解自己的中心值 R_i：
        ///     |½(L_i+L_{i+1}) − R_mid| ≤ |½(R_i+R_{i+1}) − R_mid| + ½(|L_i−R_i| + |L_{i+1}−R_{i+1}|)
        ///       └── 实测 ─────────┘   └── 固有 exact ──────┘   └── 端点行进误差 bound ──┘
        /// 前一项**一个 LUT 值都不含**，是这个切片分布在这一段的能力上限；
        /// 后一项是两个端点各自的行进误差，而它已经由 errCenter 单独设门（本项目 5%）。
        /// 于是 errMid 里的每一分误差都被归到了一个有主的地方，不多不少。
        ///
        /// ---- 为什么不用曲率做泰勒预测（#18 走过的弯路，记下来）----
        /// 第一版把 exact 用二阶差分估成 |g_{i−1} − 2g_i + g_{i+1}|/8。那是同一个量的
        /// **近似**，代价是要配一个 2× 的经验裕度（因为三样本估到的是 2Δw 跨度上的
        /// 平均曲率，而雾拐点附近 g'' 一片之内变几倍），而配了裕度之后：
        ///   · 视角③ 配置 D 读到 实测/预测 = 2.6× —— 判成"模型不成立"，
        ///     可它真实的原因只是泰勒展开在近似阶跃处失效；
        ///   · 更糟的是"预测 < 门"与"预测 ≥ 门"两侧判法不同，在 预测 ≈ 门 处不连续，
        ///     一片预测 1.9% 的切片零裕度、预测 2.1% 的切片有 2 倍裕度 ——
        ///     正是本项目点过名的「一个正好压在门上的读数，判定由最后一位小数决定」。
        /// 让核多写一行（切片中心的参考 T，见 (4,0,z)）就能把近似换成恒等式，
        /// 经验常数从 1 个降到 0 个。**能算准的量不要去估。**
        ///
        /// ---- 为什么它不是把判据变成空判据 ----
        /// 分解本身确实是恒等式（所以"实测 ≤ 固有 + 端点"这一条只抓实现错误：
        /// 读错纹素、通道对错、interp 不是两端点的中点 —— 那些残差是 0.1~1 量级）。
        /// 画质断言在**另一条**上：exact 必须低于画质门。exact 与被测代码无关，
        /// 刷不动 —— 想让它变小只能真的把切片布密。所以两条判据合起来是：
        ///     「实现没错」（恒等式）＋「分布够用」（exact 过门），各自独立可失败。
        ///
        /// ---- 首/末片 ----
        /// 分解只需要 z 与 z+1 两个端点，两者对每一个中点都存在，
        /// 所以**没有边缘片**（曲率版需要左右各一个邻居，首末两片拿不到，
        /// 那两片当时只能退回平门）。这也是换掉曲率版的附带收益。
        /// </summary>
        static void FillApMidDecomposition(ref ApCurve c)
        {
            float invFar = 1f / Mathf.Max(c.farC, 1e-6f);
            for (int z = 0; z < c.sampleCount; ++z)
            {
                // S 通道：分母与核里逐位相同（farC）。
                c.exact[z] = Mathf.Abs(0.5f * (c.refCc[z] + c.refCc[z + 1]) - c.refS[z]) * invFar;
                c.bound[z] = 0.5f * (Mathf.Abs(c.lutCc[z]     - c.refCc[z])
                                   + Mathf.Abs(c.lutCc[z + 1] - c.refCc[z + 1])) * invFar;

                // T 通道：分母是 max(refT_mid, 1e-4)，同样与核里逐位相同。
                float invT = 1f / Mathf.Max(c.refT[z], 1e-4f);
                c.exactT[z] = Mathf.Abs(0.5f * (c.refTc[z] + c.refTc[z + 1]) - c.refT[z]) * invT;
                c.boundT[z] = 0.5f * (Mathf.Abs(c.lutTc[z]     - c.refTc[z])
                                    + Mathf.Abs(c.lutTc[z + 1] - c.refTc[z + 1])) * invT;
            }
        }

        static int ApBandIndex(float km)
        {
            for (int b = k_ApBandMarks.Length - 1; b >= 0; --b)
                if (km >= k_ApBandEdgesKm[b]) return b;
            return 0;
        }

        /// <summary>这一段里的雾**看不看得见**。
        ///
        /// 只有"某段一个中点样本都没有"时才需要它：那种情况下没有误差可测，
        /// 一律判死会误杀（Log near=100m 在四个深度上全被段①否掉，而段①是脚下
        /// 50 m 以内 —— 那里的累积入散射只有 5.8 cd/m²，对着 1.1E+004 的日照
        /// 参考白是 0.05%，人眼根本不可能看见"这 50 m 的雾插值错了"）。
        /// 一律跳过又等于给"近端被饿死"免检，那才是真正要防的失败模式。
        ///
        /// 所以判据换成绝对量：这段能藏起来的雾量上限，够不够到 1% 的可见阈值。
        /// 两个通道都要看 —— 入散射（加到画面上的亮度）和遮挡 1−T（吃掉的对比度），
        /// 因为近处恰恰是 T 主导、入散射可以忽略的区间。
        ///
        /// 上限怎么取：累积入散射沿距离单调增、T 单调减（这两条由 ValidateApTable
        /// 独立验过），所以**该段远端**的值就是段内任意位置的上界。取第一个中点落在
        /// 段远边界之外的样本，它比真正的远边界更远，是个偏保守的上界。
        ///
        /// 这条规则刷不动：refS / refT 来自 4096 步参考解，是大气本身的性质，
        /// 和切片怎么布无关。想让一段"被豁免"，只能是它真的没东西可看。
        /// </summary>
        static bool ApBandVisible(in ApCurve c, int b, out float lumRatio, out float occl)
        {
            float far = (b + 1 < k_ApBandEdgesKm.Length) ? k_ApBandEdgesKm[b + 1] : float.MaxValue;

            int i = -1;
            for (int z = 0; z < c.sampleCount; ++z)
                if (c.midKm[z] >= far) { i = z; break; }
            if (i < 0) i = c.sampleCount - 1;   // 整张表都没到这段远端：拿最远的样本兜底

            lumRatio = c.refS[i] / Mathf.Max(c.visibleWhite, 1e-6f);
            occl     = 1f - c.refT[i];
            return lumRatio >= k_ApVisibleFloor || occl >= k_ApVisibleFloor;
        }

        /// <summary>四段判据的布尔版（不建字符串），给扫描里的余量档与第二视角用。
        ///
        /// ⚠ 它**故意不**走 <see cref="ApBandText"/> 的那两条断言（#18 起三者语义分岔）：
        /// ApBandText 判的是「实现有没有错」（恒等式：实测 ≤ 固有+端点+允差）
        /// 加「分布够不够用」（固有 &lt; 上限，可被显式声明放宽）。
        /// 这里问的是第三个问题：「这个分布够不够好，能不能**进候选**」——
        /// 它必须用**实测**的平门，而且还要再乘 <see cref="k_ApSelectMargin"/> 收紧。
        /// 理由：一段误差"已被固有重建能力解释"恰恰意味着这个分布在那一段装不下曲线，
        /// 那正是不该选它的理由，不是放过它的理由。
        /// 三条判据答不同的问题，所以不是同一个量的多份实现。</summary>
        static bool ApBandsOk(in ApCurve c, bool transmittance, float threshold)
        {
            float[] vals   = transmittance ? c.bandMidT   : c.bandMid;
            int[]   counts = transmittance ? c.bandCountT : c.bandCount;
            for (int b = 0; b < k_ApBandMarks.Length; ++b)
            {
                if (counts[b] <= 0)
                {
                    // T 通道有两种 0：这一段一片都没有（要过 ApBandVisible），
                    // 或者有片但每片的 |ΔT| 都在绝对可见度之下（已经论证过看不见，直接放过）。
                    if (transmittance && c.bandCount[b] > 0) continue;
                    if (ApBandVisible(c, b, out _, out _)) return false;
                    continue;
                }
                if (!(vals[b] < threshold)) return false;
            }
            return true;
        }

        /// <summary>能看见的段里最差的那个百分比。只用于报表排序，不参与判定。</summary>
        static float ApWorstBand(in ApCurve c, bool transmittance)
        {
            float[] vals   = transmittance ? c.bandMidT   : c.bandMid;
            int[]   counts = transmittance ? c.bandCountT : c.bandCount;
            float worst = 0f;
            for (int b = 0; b < k_ApBandMarks.Length; ++b)
                if (counts[b] > 0) worst = Mathf.Max(worst, vals[b]);
            return worst;
        }

        /// <summary>分段结论。某段没有中点样本时，交给 <see cref="ApBandVisible"/> 定生死：
        /// 段内的雾在 1% 可见阈值之下才豁免，否则仍然判死（近端被饿死的签名）。</summary>
        static string ApBandText(ApCurve c, bool transmittance, float threshold, out bool ok)
        {
            return ApBandText(c, transmittance, threshold, threshold, out ok);
        }

        /// <summary>分段结论 + 误差分解判定。
        ///
        /// 两条独立的断言，各自可失败，报表上分开印（见 <see cref="FillApMidDecomposition"/>）：
        ///   实现：实测 ≤ 固有 + 端点上界 + <see cref="k_ApMidDecompAllow"/>。恒等式，
        ///         抓的是"读错纹素 / 通道对错 / interp 不是两端点的中点"这类实现错误。
        ///   分布：固有误差 exact 必须低于 <paramref name="threshold"/>（画质门）。
        ///         exact 只由 4096 步参考解与切片分布决定，与被测代码无关，刷不动。
        ///
        /// <paramref name="capacityCeiling"/> 是分布那一条的**已声明上限**：
        /// 传 threshold（默认重载）= 不放宽。传更大的值 = 这个布景已知超出档 D 的
        /// 切片包络（例如相机在 300 m 高空俯视一层 20 m 厚的雾，拐点那一片的片长
        /// 236 m 远大于雾沿射线的 e 折 58 m），此时 exact 超门是**推导得出的预期**，
        /// 而不是缺陷。放宽的代价必须印在同一行里（"固有超门 n 片"），
        /// 否则就是让"未判达标"冒充"达标"。</summary>
        static string ApBandText(ApCurve c, bool transmittance, float threshold,
                                 float capacityCeiling, out bool ok)
        {
            ok = true;
            var line = new StringBuilder();
            float[] vals   = transmittance ? c.bandMidT   : c.bandMid;
            int[]   counts = transmittance ? c.bandCountT : c.bandCount;
            float[] meas   = transmittance ? c.midT       : c.mid;
            float[] exact  = transmittance ? c.exactT     : c.exact;
            float[] bound  = transmittance ? c.boundT     : c.bound;
            int overGate = 0, judged = 0;
            float worstExactAll = 0f, worstSlackAll = float.NegativeInfinity;
            for (int b = 0; b < k_ApBandMarks.Length; ++b)
            {
                if (b > 0) line.Append(' ');
                line.Append(k_ApBandMarks[b]);

                // T 通道：有片、但每片的 |ΔT| 都低于绝对可见度门槛。
                // 打印成「ΔT免检(n片)」而不是复用下面那个 "0片:免检" —— 两者论证不同，
                // 报表上必须能分辨是"没测到"还是"测到了但看不见"。
                if (transmittance && counts[b] <= 0 && c.bandCount[b] > 0)
                {
                    line.Append("ΔT免检(").Append(c.bandCount[b]).Append("片)");
                    continue;
                }

                if (counts[b] <= 0)
                {
                    bool visible = ApBandVisible(c, b, out float lumRatio, out float occl);
                    ok &= !visible;
                    line.Append("0片:")
                        .Append(visible ? "有雾" : "免检")
                        .Append('(').Append(Pct(lumRatio)).Append('/').Append(Pct(occl)).Append(')');
                    if (visible) line.Append('✘');
                    continue;
                }

                float worstExact = 0f;
                // ⚠ 从 −∞ 起累加，而**不是**从 0：这是余量，负数才是正常态。
                // 从 0 起的话读数永远非负，「恒等余量 0%」就同时意味着
                // 「余量充足」和「实测那一路根本没写（meas ≡ 0）」——
                // 正是本项目点过名的「读数接近 0 的档位无法自证自己执行过」。
                float worstSlack = float.NegativeInfinity;
                int nOver = 0;
                for (int z = 0; z < c.sampleCount; ++z)
                {
                    if (c.bandIdx[z] != b) continue;
                    if (transmittance && c.exemptSlice[z]) continue;
                    judged++;
                    worstExact = Mathf.Max(worstExact, exact[z]);
                    // 恒等式的违反量（正数 = 违反）。报最差的那个，而不只报"过了没"——
                    // 一个恒等式判据的余量本身就是它有没有真跑过的证据。
                    worstSlack = Mathf.Max(worstSlack, meas[z] - exact[z] - bound[z]);
                    if (exact[z] >= threshold) { nOver++; overGate++; }
                }
                worstExactAll = Mathf.Max(worstExactAll, worstExact);
                worstSlackAll = Mathf.Max(worstSlackAll, worstSlack);

                bool okImpl = worstSlack <= k_ApMidDecompAllow;
                bool okDist = worstExact < capacityCeiling;
                bool bandOk = okImpl && okDist;
                ok &= bandOk;

                line.Append(Pct(vals[b]));
                if (!transmittance) line.Append('@').Append(c.bandMidAtKm[b].ToString("F2"));
                line.Append('(').Append(counts[b]).Append("片,固").Append(Pct(worstExact));
                if (!okImpl) line.Append(",恒等违").Append(SlackPct(worstSlack));
                if (nOver > 0) line.Append(',').Append(nOver).Append("片超门");
                line.Append(')');
                if (!bandOk) line.Append('✘');
                else if (nOver > 0) line.Append('◇');
            }

            // 两条断言的代价与覆盖面都必须印出来：
            //   判n片        —— 这一行不是空判据的证明（豁免掉全部切片时它会是 0）。
            //   恒等余量      —— 实测 −（固有+端点）的最大值，负数越大越安全。
            //                   一个恒等式判据的余量本身就是它有没有真跑过的证据：
            //                   若它恒等于 −(固有+端点)（即实测恒为 0），说明 meas 那一路没写。
            //   固有超门 n 片 —— 分布那一条被放宽到 capacityCeiling 的全部代价。
            line.Append(" 判").Append(judged).Append("片 固有最差").Append(Pct(worstExactAll));
            if (judged > 0) line.Append(" 恒等余量").Append(SlackPct(worstSlackAll));
            if (overGate > 0)
            {
                line.Append(" 固有超门").Append(overGate).Append("片");
                if (capacityCeiling > threshold)
                    line.Append("(已声明上限 ").Append(Pct(capacityCeiling)).Append("，非画质达标)");
            }
            return line.ToString();
        }

        /// <summary>逐片打印 errMid，不做任何聚合 —— 定档的原始依据。
        /// 只有看到整条曲线才能区分"整体偏一点"和"某一段塌下去"。</summary>
        static string ApCurveText(Volume errCol, VistaAerialPerspectiveSettings s)
        {
            var line = new StringBuilder();
            for (int z = 0; z < s.depth - 1; ++z)
            {
                if (z > 0) line.Append(' ');
                line.Append(ApDistance(s, z, 0.5f).ToString("F3")).Append('→')
                    .Append((errCol[0, 0, z].g * 100f).ToString("F3"));
            }
            return line.ToString();
        }

        /// <summary>成本代理：复刻核里 <c>clamp(ceil(segLen / VISTA_AP_STEP_MAX_KM), MIN, MAX)</c>
        /// 累计出"一根柱子跑多少步"。这是 AP 那一趟 dispatch 的算力主项
        /// （宽高固定 32×32，唯一变的就是深度方向的段划分）。
        ///
        /// 复刻忽略了核里对 tLimit（大气顶/地面交点）的钳制。对本自检用的贴地水平视线，
        /// tTop 有几百 km，钳制不生效；若以后拿它去评朝天的柱子，这个数会偏高。</summary>
        static int ApMarchStepsPerColumn(VistaAerialPerspectiveSettings s)
        {
            const float stepMaxKm = 0.25f;
            // 与核里的 VISTA_AP_STEPS_MIN / VISTA_AP_STEPS_MAX 对齐。#18 把上限 16 → 64，
            // 这里跟着改：不改的症状是报表里的「步/柱」还印 149，而实际跑 168 ——
            // 一个**偏低**的成本代理会让"放开上限不花钱"看起来是量出来的。
            const int stepsMin = 2, stepsMax = 64;
            int total = 0;
            float prev = 0f;
            for (int z = 0; z < s.depth; ++z)
            {
                float dz = ApDistance(s, z);
                total += Mathf.Clamp(Mathf.CeilToInt((dz - prev) / stepMaxKm), stepsMin, stepsMax);
                prev = dz;
            }
            return total;
        }

        static string Pct(float v)
        {
            if (float.IsNaN(v)) return "n/a";
            float p = v * 100f;
            return p >= 0.01f ? p.ToString("F2") + "%"
                 : p > 0f     ? p.ToString("E1") + "%"
                              : "0%";
        }

        /// <summary>带符号的百分比，专给「余量」这类**负数是正常态**的量用。
        ///
        /// ⚠ 不能复用 <see cref="Pct"/>：它把一切 ≤ 0 的值都打成 "0%"，于是
        ///   「余量 −5%（充足）」「余量恰好 0（压线）」「−∞（这一段一片都没判）」
        /// 三种完全不同的状态在报表上长得一样 —— 尺子自己造了个结论。
        /// 小数位取 F3 是因为这个量的实际量级在 0.0x%（fp16 圆整级别）。</summary>
        static string SlackPct(float v)
        {
            if (float.IsNaN(v)) return "n/a";
            if (float.IsNegativeInfinity(v)) return "无判定片";
            return (v * 100f).ToString("F3") + "%";
        }

        /// <summary>
        /// 深度 × 分布的扫描，给 AP 定档。
        ///
        /// 网格：depth ∈ {16, 24, 32, 48, 64} × {Log(near 20 m), Log(near 100 m), Power k=2, Power k=3}。
        /// Log(near 100 m) 必须在里面：Step 3 的体积雾会接管近处那一层，届时 nearKm 会抬上去，
        /// 定档不能只在"近端极密"这一个前提下成立。
        ///
        /// **单个配置不通过不算自检失败** —— 扫描的产出就是"哪些不行"，
        /// 把预期中的失败当红灯会逼着人去放宽阈值。只有两件事算失败：
        /// 一个都不通过（说明判据或行进精度有问题，不该靠调阈值糊过去），
        /// 以及胜出配置在第二个视角上翻车（说明是对着一帧调的）。
        /// </summary>
        static bool ValidateApSliceBudget(VistaAtmosphereLuts luts, VistaAtmosphereParameters p,
                                         VistaAerialPerspectiveSettings baseSettings, StringBuilder sb)
        {
            var view = MakeView(p, Vector3.zero, 60f);
            // 第二视角从"事后复核"提到"参与筛选"。理由：余量规则（k_ApSelectMargin）
            // 存在的唯一依据就是视角敏感性，只在一个视角上核它是自相矛盾的 ——
            // 上一版就出现过 d=16 Log 20m 在主视角 T④0.49% 刚好压线过、
            // 换视角 errMid④ 从 0.77% 抬到 0.91% 的情况。既然手上有两个视角，
            // 就两个都要满足；把第二个只当事后确认，等于让筛选看不见自己的依据。
            var view2 = MakeView(p, new Vector3(0f, 1000f, 0f), 10f);
            var modes = new[]
            {
                ("Log 20m ", VistaAerialPerspectiveSettings.Distribution.Logarithmic, 2f, 0.02f),
                ("Log 100m", VistaAerialPerspectiveSettings.Distribution.Logarithmic, 2f, 0.10f),
                ("Pow k=2 ", VistaAerialPerspectiveSettings.Distribution.Power,       2f, 0.02f),
                ("Pow k=3 ", VistaAerialPerspectiveSettings.Distribution.Power,       3f, 0.02f),
            };
            int[] depths = { 16, 24, 32, 48, 64 };

            sb.AppendLine("　── 切片预算扫描　" + k_ApBandLegend);
            sb.AppendLine("　 通过 = 四段的 errMid 全 < " + Pct(k_ApErrMidMax)
                        + " 且 errMidT 全 < " + Pct(k_ApErrMidTMax)
                        + "；空段按可见性下限 " + Pct(k_ApVisibleFloor)
                        + " 豁免（入散射/遮挡，见 ApBandVisible）；errCenter < "
                        + Pct(k_ApErrCenterMax) + " 为前置门");
            sb.AppendLine("　 定档另加余量：两个视角（贴地/太阳 60° 与海拔 1 km/太阳 10°）"
                        + "都要 ≤ 阈值×" + k_ApSelectMargin.ToString("F2")
                        + "（见 k_ApSelectMargin），标 ◎ 的才进候选");

            VistaAerialPerspectiveSettings bestS = null;
            string bestTag = null;
            int bestCost = int.MaxValue, bestDepth = int.MaxValue;

            // 出厂档（VistaAerialPerspectiveSettings 的字段初始值）也要参与这次扫描，
            // 并且它才是本项判定的对象 —— 见循环后面那段说明。
            var shipped = new VistaAerialPerspectiveSettings();
            bool shippedSeen = false, shippedPass = false, shippedCand = false;
            int shippedCost = 0;

            foreach (int depth in depths)
            foreach (var m in modes)
            {
                var s = baseSettings.Clone();
                s.resolution = new Vector3Int(baseSettings.width, baseSettings.height, depth);
                s.distribution = m.Item2;
                s.powerExponent = m.Item3;
                s.nearDistanceKm = m.Item4;

                var c = MeasureApConfig(luts, view, s, p);
                bool okCenter = c.maxErrCenter < k_ApErrCenterMax;
                string midText  = ApBandText(c, false, k_ApErrMidMax,  out bool okMid);
                string midTText = ApBandText(c, true,  k_ApErrMidTMax, out bool okMidT);

                var c2 = MeasureApConfig(luts, view2, s, p);
                bool okCenter2 = c2.maxErrCenter < k_ApErrCenterMax;
                bool okMid2  = ApBandsOk(c2, false, k_ApErrMidMax);
                bool okMidT2 = ApBandsOk(c2, true,  k_ApErrMidTMax);

                bool pass = okCenter && okMid && okMidT && okCenter2 && okMid2 && okMidT2;

                // 余量档：同一套判据、阈值减半、两个视角都要过。
                // 只影响"选谁"，不影响"谁算合格"。
                bool cand = pass
                    && ApBandsOk(c,  false, k_ApErrMidMax  * k_ApSelectMargin)
                    && ApBandsOk(c,  true,  k_ApErrMidTMax * k_ApSelectMargin)
                    && ApBandsOk(c2, false, k_ApErrMidMax  * k_ApSelectMargin)
                    && ApBandsOk(c2, true,  k_ApErrMidTMax * k_ApSelectMargin);

                // 这一格是不是出厂档。Log 分布下 powerExponent 不参与映射，Power 分布下
                // nearDistanceKm 不参与（effectiveNearKm 返回 0），所以只比生效的那个。
                bool isShipped = depth == shipped.depth && m.Item2 == shipped.distribution
                    && (m.Item2 == VistaAerialPerspectiveSettings.Distribution.Logarithmic
                        ? Mathf.Approximately(m.Item4, shipped.nearDistanceKm)
                        : Mathf.Approximately(m.Item3, shipped.powerExponent));

                sb.AppendLine(Mark(pass) + (cand ? "◎" : "　") + (isShipped ? "★" : "")
                            + "d=" + depth.ToString().PadLeft(2)
                            + " " + m.Item1
                            + "　步/柱 " + c.marchSteps.ToString().PadLeft(3)
                            + "　errC " + Pct(c.maxErrCenter) + "@" + c.atErrCenterKm.ToString("F4")
                            + (okCenter ? "" : "✘门")
                            + "　" + midText
                            + "　Δ " + Pct(c.maxMidStep)
                            + "　T:" + midTText
                            + "　｜视角2 最差 " + Pct(ApWorstBand(c2, false))
                                      + "/T" + Pct(ApWorstBand(c2, true))
                                      + (okCenter2 && okMid2 && okMidT2 ? "" : "✘"));

                if (isShipped)
                {
                    shippedSeen = true; shippedPass = pass; shippedCand = cand;
                    shippedCost = c.marchSteps;
                }

                if (cand && (c.marchSteps < bestCost
                          || (c.marchSteps == bestCost && depth < bestDepth)))
                {
                    bestCost = c.marchSteps; bestDepth = depth;
                    bestS = s; bestTag = "d=" + depth + " " + m.Item1.Trim();
                }
            }

            if (bestS == null)
            {
                sb.AppendLine("✘ 没有配置通过分段判据。先查行进精度与判据本身，不要直接放宽阈值。");
                return false;
            }

            // ---- 最省的候选：它回答的是"合格线能压到多低"，不是"出厂发什么" ----
            //
            // 这一行曾经就叫"定档"，那是个错误的口径。这套判据（1% 的 Weber 可见阈）
            // 只能判**合格**，不能判**该选谁** —— 20 组配置全部合格时，"取最省的"
            // 就成了一条藏在代码里的偏好，而它并没有画面或成本上的依据支撑：
            // 成本侧实测（Profile Atmosphere LUTs 的 AP 定档一节）给出 d=16→32 在
            // 稳态整链上只差 0.01~0.02 ms 量级、显存差 256 KB，而误差差约 4 倍。
            // 也就是说这里"省"下来的东西小到测不太准，换掉的却是四倍余量。
            // 具体毫秒数不在这儿抄一份：抄了就会在核变化后静默过期。
            float bestKb = baseSettings.width * baseSettings.height * bestS.depth * 8f * 2f / 1024f;
            sb.AppendLine("　 最省候选 → " + bestTag + "　步/柱 " + bestCost
                        + "　显存 " + bestKb.ToString("F0") + " KB"
                        + "　（只说明合格线能压到多低；出厂档见下一行）");

            // ---- 出厂档：把默认值本身当被测对象 ----
            //
            // 判定挂在出厂档而不是挂在"扫描里有没有配置能过"上。理由：后者永远会过
            // （总有一档够精细），于是这项自检就永远绿，改坏默认值也照样绿 ——
            // 那是一条伪通过。真正需要回归保护的是"用户什么都不改时拿到的那一档"。
            if (!shippedSeen)
            {
                sb.AppendLine("✘ 出厂档 d=" + shipped.depth + "/" + shipped.distribution
                            + " 不在扫描网格里，本项无法判定。改了默认值就要同步 depths/modes。");
                return false;
            }

            float shippedKb = shipped.width * shipped.height * shipped.depth * 8f * 2f / 1024f;
            sb.AppendLine(Mark(shippedPass && shippedCand) + " 　出厂档 ★ d=" + shipped.depth
                        + " " + shipped.distribution
                        + (shipped.distribution == VistaAerialPerspectiveSettings.Distribution.Logarithmic
                            ? " near " + (shipped.nearDistanceKm * 1000f).ToString("F0") + "m"
                            : " k=" + shipped.powerExponent.ToString("F1"))
                        + "　步/柱 " + shippedCost
                        + "　显存 " + shippedKb.ToString("F0") + " KB"
                        + "　合格 " + (shippedPass ? "✔" : "✘")
                        + "　含余量 " + (shippedCand ? "✔" : "✘")
                        + "　（余量档不过就说明默认值已经压到可见阈附近，要么升档要么改判据，"
                        + "不能靠「两个视角都刚好没超」过关）");

            // 出厂档在第二视角上的完整逐段数据。筛选阶段已经用过这个视角，
            // 这里把它摊开印出来 —— 定档要能被人复核，不能只留一个"最差值"。
            var cWin2 = MeasureApConfig(luts, view2, shipped, p);
            bool okCenterW = cWin2.maxErrCenter < k_ApErrCenterMax;
            string midW  = ApBandText(cWin2, false, k_ApErrMidMax,  out bool okMidW);
            string midTW = ApBandText(cWin2, true,  k_ApErrMidTMax, out bool okMidTW);
            bool pass2 = okCenterW && okMidW && okMidTW;

            sb.AppendLine(Mark(pass2) + " 　复核 出厂档 @ 海拔 1 km／太阳 10°（落日）"
                        + "　errC " + Pct(cWin2.maxErrCenter) + "@" + cWin2.atErrCenterKm.ToString("F4")
                                  + (okCenterW ? "" : "✘门")
                        + "　" + midW + "　Δ " + Pct(cWin2.maxMidStep) + "　T:" + midTW);

            return pass2 && shippedPass && shippedCand;
        }

        /// <summary>烘一个配置并测一次。顺序是硬的：SliceError 要把散射表当 SRV 读回来对照，
        /// 所以必须在正式核之后。</summary>
        static ApCurve MeasureApConfig(VistaAtmosphereLuts luts, in VistaAtmosphereViewData view,
                                       VistaAerialPerspectiveSettings s,
                                       in VistaAtmosphereParameters p)
        {
            luts.PrepareAerialPerspective(s);

            var cmd = new CommandBuffer();
            luts.RenderAerialPerspectiveLut(cmd, view, s);
            luts.RenderApSliceError(cmd, view, s);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            return ReduceApCurve(Readback3D(luts.apTransmittanceLut), s, p, view.exposure);
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
