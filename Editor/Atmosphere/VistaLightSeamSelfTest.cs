using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista.Editor
{
    /// <summary>
    /// 单位接缝的端到端验收：CPU 写进 <see cref="Light"/> 的物理量，
    /// URP 着色器里读到的是不是 <c>T · lux · exposure / π</c>。
    ///
    /// ── 为什么这一项必须渲一帧，不能靠算术 ──
    ///
    /// CPU 与 GPU 之间夹着引擎的一段闭源计算：
    ///   finalColor = Light.color.linear × intensity × (useColorTemperature ? CCT(K) : 1)
    /// 没有任何 API 能把 finalColor 读回来（<c>VisibleLight</c> 只在 SRP 剔除上下文里存在）。
    /// 而 <see cref="VistaTimeOfDay.ApplyLightParams"/> 里那两行 ——
    /// 写 <c>.gamma</c>、强制关色温 —— 目前只有「读 URP 源码得出的推理」做支撑。
    /// 推理可能是对的，也可能对了一半（比如原生用 pow(2.2) 近似而 C# 的 .gamma 用精确
    /// sRGB 曲线，那样往返会在中间调差 1~2%）。所以要渲一次再读像素。
    ///
    /// ── 判据的可归因性 ──
    ///
    /// 探针 shader 只输出 <c>GetMainLight().color</c>，不含 BRDF / GI / 雾 / 阴影。
    /// 理由见 <c>Shaders/Debug/SeamProbe.shader</c> 的头注：唯一失败源才能唯一归因。
    /// <c>albedo × lightColor × NdotL</c> 那一步是纯算术，已由透射率自检的 C 项钉住。
    ///
    /// ── 三组测量 ──
    ///
    /// A. **锚点**：令 T=(1,1,1)，输出应当恰好是 0.97140 三通道相等 ——
    ///    也就是「Unity 惯用的正午平行光强度 1」的物理出处那个数，端到端量一遍。
    /// B. **工况扫描**：太阳 0.5°→90°，逐通道比 <c>T·lux·exposure/π</c>。
    ///    低太阳时三通道色度差两个数量级，是 gamma 往返误差最容易暴露的地方。
    /// C. **两条反例对照**：故意写回「不做 .gamma」和「开 3000K 色温」，
    ///    期望它们**明显偏离**。这一步不是在测大气，是在测那两行代码有没有实质后果 ——
    ///    若反例也能通过，那两行就是无依据的仪式，应当删掉。
    ///    #7 记过的坑：未覆盖路径的假通过。
    /// </summary>
    public static class VistaLightSeamSelfTest
    {
        [MenuItem("Window/Vista/Validate Light Seam")]
        static void RunFromMenu()
        {
            var report = Run(VistaAtmosphereParameters.CreateEarth(),
                             VistaAtmosphereViewData.k_DefaultEV100);
            string oneLine = report.text.Replace("\r", "").Replace("\n", "  |  ");
            if (report.passed) Debug.Log("[Vista] 单位接缝验收通过  |  " + oneLine);
            else Debug.LogError("[Vista] 单位接缝验收失败  |  " + oneLine);
        }

        /// <summary>
        /// 逐通道相对误差门。沿用全项目共用的 Weber 1%：直射光色的相对误差
        /// 1:1 传到受光面的像素值上。
        ///
        /// 这里**不需要**绝对可见性豁免，与透射率自检不同 —— 那边是因为 T 会趋 0
        /// 而分母消失。这边输出走的是 fp16 渲染目标，浮点的**相对**精度是恒定的
        /// 4.88e-4（尾数 10 位），无论量级都比 1% 的门低一个多数量级。
        /// 若哪天把 RT 换成 8-bit，就必须补绝对豁免了。
        /// </summary>
        const float k_RelThreshold = 0.01f;

        /// <summary>
        /// 反例对照要越过的偏离量。取 5% —— 5 倍于判据门，
        /// 确保「反例失败」不是踩线，而是量级上的失败。
        /// </summary>
        const float k_CounterExampleMin = 0.05f;

        /// <summary>
        /// 全屏一致性门。探针 Quad 铺满视口，所有像素应当读到同一个
        /// <c>_MainLightColor</c>，逐通道极差应为 0。给一点 fp16 余量。
        /// 这一项抓的是「Quad 没盖满、边缘读到清屏色」—— 那会让平均值悄悄偏低。
        /// </summary>
        const float k_SpreadThreshold = 1e-5f;

        const int k_Size = 4;

        public struct Report
        {
            public bool passed;
            public string text;
        }

        public static Report Run(VistaAtmosphereParameters p, float ev100)
        {
            var shader = Shader.Find("Hidden/Vista/SeamProbe");
            if (shader == null)
                return Fail("找不到 Hidden/Vista/SeamProbe：检查 Shaders/Debug/SeamProbe.shader 是否已导入。");

            var urp = UniversalRenderPipeline.asset;
            if (urp == null)
                return Fail("当前管线不是 URP。");

            var sb = new StringBuilder();
            float exposure = VistaAtmosphereViewData.ExposureFromEV100(ev100);

            // ---- 前置条件。不满足时明确报出是哪一条，而不是让判据笼统失败 ----
            if (urp.mainLightRenderingMode != LightRenderingMode.PerPixel)
                return Fail("URP asset 的 Main Light 设为 " + urp.mainLightRenderingMode
                          + "：此时 _MainLightColor 恒为黑，接缝无从测量。");

            sb.AppendLine("── 前置　URP HDR=" + urp.supportsHDR
                        + "　色彩空间=" + QualitySettings.activeColorSpace
                        + "　EV100=" + ev100 + "　exposure=" + exposure.ToString("E4"));
            if (!urp.supportsHDR)
                sb.AppendLine("    ⚠ URP 关了 HDR：中间渲染目标可能退化成 8-bit sRGB，"
                            + "往返量化在 0.9 附近约 0.4%，会吃掉 1% 门的近一半余量。"
                            + "本轮结果按「精度受限」读。");
            if (QualitySettings.activeColorSpace != ColorSpace.Linear)
                sb.AppendLine("    ⚠ Gamma 色彩空间：引擎不做 .linear，"
                            + "ApplyLightParams 也就不做 .gamma，两边一致，但这条路径未在产品中使用。");

            Material mat = null;
            RenderTexture rt = null;
            Texture2D readback = null;
            GameObject root = null;
            Light prevSun = RenderSettings.sun;

            try
            {
                mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                rt = new RenderTexture(k_Size, k_Size, 24,
                                       RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                readback = new Texture2D(k_Size, k_Size, TextureFormat.RGBAFloat, false, true);

                Build(mat, rt, out root, out Camera cam, out Light light);

                // 主平行光的选取：URP 的 GetMainLightIndex 优先返回 RenderSettings.sun，
                // 否则挑最亮的 directional。显式登记，免得场景里别的灯把探针顶掉 ——
                // 那种失败会表现为「接缝差一个任意倍数」，极难归因。
                RenderSettings.sun = light;

                bool ok = true;
                ok &= MeasureAnchor(p, exposure, sb, cam, rt, readback, light);
                ok &= MeasureSweep(p, exposure, sb, cam, rt, readback, light);
                ok &= MeasureCounterExamples(p, exposure, sb, cam, rt, readback, light);

                return new Report { passed = ok, text = sb.ToString().TrimEnd() };
            }
            finally
            {
                RenderSettings.sun = prevSun;
                if (root != null) Object.DestroyImmediate(root);
                if (readback != null) Object.DestroyImmediate(readback);
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (mat != null) Object.DestroyImmediate(mat);
            }
        }

        // ==================================================================== A 锚点

        /// <summary>
        /// T=(1,1,1)。期望输出恰好 lux·exposure/π = 0.97140，三通道相等。
        ///
        /// 选这一档做锚点是因为它把 gamma 往返摘出去了：色度是 (1,1,1)，
        /// sRGB 曲线在 1.0 处是不动点，所以这一档只测「intensity 有没有被额外系数动过」。
        /// 它通过而 B 项的低太阳档失败，就直接指向 gamma 往返；两者都失败则指向 intensity。
        /// 一次测量分开两个失败源。
        /// </summary>
        static bool MeasureAnchor(
            VistaAtmosphereParameters p, float exposure, StringBuilder sb,
            Camera cam, RenderTexture rt, Texture2D readback, Light light)
        {
            var lp = VistaSunTransmittance.ComputeLightParams(p, Vector3.one, exposure);
            VistaTimeOfDay.ApplyLightParams(light, lp);

            Vector3 got = RenderAndRead(cam, rt, readback, out float spread);
            float expected = p.sunIlluminanceLux * exposure / Mathf.PI;
            float worst = MaxRel(got, new Vector3(expected, expected, expected));

            bool okSpread = spread < k_SpreadThreshold;
            bool ok = worst < k_RelThreshold && okSpread;

            sb.AppendLine("── A 锚点　T=(1,1,1)　闭式 lux·exposure/π = " + expected.ToString("F5"));
            sb.AppendLine("    " + Mark(ok) + " GPU 读回 " + Fmt(got)
                        + "　最大相对误差 " + (worst * 100f).ToString("F4") + "%"
                        + "（门 " + (k_RelThreshold * 100f).ToString("F0") + "%）"
                        + "　全屏极差 " + spread.ToString("E2") + (okSpread ? "" : " ✘ 超出一致性门"));
            sb.AppendLine("      色度 (1,1,1) 是 sRGB 曲线的不动点，所以这一档只测 intensity；"
                        + "gamma 往返的误差要看 B 项的低太阳档。");
            return ok;
        }

        // ==================================================================== B 工况扫描

        static bool MeasureSweep(
            VistaAtmosphereParameters p, float exposure, StringBuilder sb,
            Camera cam, RenderTexture rt, Texture2D readback, Light light)
        {
            // 密集覆盖低仰角：色度在那里最偏离白，gamma 往返的误差最大
            float[] elevations = { 0.5f, 1f, 2f, 3f, 5f, 8f, 15f, 30f, 45f, 60f, 90f };

            sb.AppendLine("── B 工况扫描（参考海拔 0 m，逐通道相对判据 "
                        + (k_RelThreshold * 100f).ToString("F0") + "%）");
            sb.AppendLine("    仰角   写入 color（gamma 域）        闭式 T·lux·exp/π            GPU 读回                    最大相对");

            bool ok = true;
            float worstOverall = 0f;
            float worstAt = 0f;

            foreach (float deg in elevations)
            {
                var lp = LightParamsAt(p, exposure, deg);
                VistaTimeOfDay.ApplyLightParams(light, lp);

                Vector3 got = RenderAndRead(cam, rt, readback, out float spread);
                Vector3 expected = TAt(p, deg) * (p.sunIlluminanceLux * exposure / Mathf.PI);

                float worst = MaxRel(got, expected);
                bool rowOk = worst < k_RelThreshold && spread < k_SpreadThreshold;
                ok &= rowOk;
                if (worst > worstOverall) { worstOverall = worst; worstAt = deg; }

                Color written = light.color;
                sb.AppendLine("    " + Mark(rowOk) + " " + deg.ToString("F1").PadLeft(4) + "°  "
                            + Fmt(written) + "  " + Fmt(expected) + "  " + Fmt(got) + "  "
                            + (worst * 100f).ToString("F4").PadLeft(8) + "%");
            }

            sb.AppendLine("    全扫描最大相对误差 " + (worstOverall * 100f).ToString("F4")
                        + "% @ " + worstAt.ToString("F1") + "°"
                        + "　（对照：fp16 渲染目标的相对精度 4.88e-4 = 0.0488%）");
            return ok;
        }

        // ==================================================================== C 反例对照

        /// <summary>
        /// 把 <see cref="VistaTimeOfDay.ApplyLightParams"/> 里两行「保护性代码」各拆掉一次，
        /// 期望结果**明显偏离**。
        ///
        /// 不做这一步的话，那两行就无法与「顺手加的仪式」区分开。更实际的风险是：
        /// 若引擎哪天不再做 .linear，正确路径与反例路径会同时通过，而我们无从察觉 ——
        /// 此时这条对照会先失败，提醒去重读源码。
        ///
        /// 仰角选 5°：此时色度约 (1, 0.46, 0.12)，离白最远，两个反例的后果都最大。
        /// 选正午会让 gamma 反例几乎无偏离（(1,1,1) 是 sRGB 不动点），
        /// 那就成了一条无效对照。
        /// </summary>
        static bool MeasureCounterExamples(
            VistaAtmosphereParameters p, float exposure, StringBuilder sb,
            Camera cam, RenderTexture rt, Texture2D readback, Light light)
        {
            const float deg = 5f;
            var lp = LightParamsAt(p, exposure, deg);
            Vector3 expected = TAt(p, deg) * (p.sunIlluminanceLux * exposure / Mathf.PI);

            sb.AppendLine("── C 反例对照（太阳 " + deg.ToString("F1") + "°，色度 "
                        + Fmt(lp.color) + " 离白最远）　期望偏离 > "
                        + (k_CounterExampleMin * 100f).ToString("F0") + "%");

            // 反例 1：不做 .gamma —— 把线性色度直接当 Gamma 语义写进去，
            // 引擎的 .linear 会再转一次，暗部被压得更暗。
            VistaTimeOfDay.ApplyLightParams(light, lp);
            light.color = lp.color;                       // 绕过 .gamma
            Vector3 noGamma = RenderAndRead(cam, rt, readback, out _);
            float relNoGamma = MaxRel(noGamma, expected);
            bool ok1 = relNoGamma > k_CounterExampleMin;
            sb.AppendLine("    " + Mark(ok1) + " 不做 .gamma　GPU " + Fmt(noGamma)
                        + "　偏离 " + (relNoGamma * 100f).ToString("F2") + "%"
                        + "　→ .gamma 有实质后果" + (ok1 ? "" : "（✘ 无后果：该行应删或推理有误）"));

            // 反例 2：开色温。URP 无条件把 lightsUseColorTemperature 置真，
            // 于是 3000K 的暖色偏会乘在已经算好的物理色度上。
            VistaTimeOfDay.ApplyLightParams(light, lp);
            light.useColorTemperature = true;
            light.colorTemperature = 3000f;
            Vector3 warm = RenderAndRead(cam, rt, readback, out _);
            float relWarm = MaxRel(warm, expected);
            bool ok2 = relWarm > k_CounterExampleMin;
            sb.AppendLine("    " + Mark(ok2) + " 开 3000K 色温　GPU " + Fmt(warm)
                        + "　偏离 " + (relWarm * 100f).ToString("F2") + "%"
                        + "　→ 强制关色温有实质后果" + (ok2 ? "" : "（✘ 无后果：该行应删或推理有误）"));

            // 恢复正确写法并复测一次，确认 ApplyLightParams 能把被污染的状态拉回来。
            // 这条不是重复 B 项：它测的是**幂等性** —— 组件每帧都会调用它，
            // 若它只在「灯是干净的」时候正确，那用户手动碰过灯之后就再也回不去了。
            VistaTimeOfDay.ApplyLightParams(light, lp);
            Vector3 restored = RenderAndRead(cam, rt, readback, out _);
            float relRestored = MaxRel(restored, expected);
            bool ok3 = relRestored < k_RelThreshold;
            sb.AppendLine("    " + Mark(ok3) + " 复写恢复　GPU " + Fmt(restored)
                        + "　相对误差 " + (relRestored * 100f).ToString("F4") + "%"
                        + "　→ ApplyLightParams 幂等，能从被污染的灯状态拉回");

            return ok1 && ok2 && ok3;
        }

        // ==================================================================== 场景搭建

        /// <summary>
        /// 建一套完全自持的探针：相机 + 全屏 Quad + 平行光，全部 <c>HideAndDontSave</c>。
        ///
        /// 相机放在 (0, 1e5, 0) 且近远裁剪面只有 0.09 单位厚 —— 这是最省事的隔离手段：
        /// 场景里的几何体不可能落进这片薄片，于是不必去改 cullingMask 或临时禁用别的物体。
        /// （平行光不受此影响：它照亮整个场景，可见性只看 light 的 cullingMask。）
        /// </summary>
        static void Build(
            Material mat, RenderTexture rt, out GameObject root, out Camera cam, out Light light)
        {
            root = new GameObject("Vista Seam Probe") { hideFlags = HideFlags.HideAndDontSave };
            root.transform.position = new Vector3(0f, 100000f, 0f);

            var camGo = new GameObject("Probe Camera") { hideFlags = HideFlags.HideAndDontSave };
            camGo.transform.SetParent(root.transform, false);
            cam = camGo.AddComponent<Camera>();
            cam.enabled = false;                 // 不参与正常渲染循环，只手动 Render()
            cam.orthographic = true;
            cam.orthographicSize = 0.5f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 0.1f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.allowHDR = true;                 // 否则中间 RT 可能退成 8-bit sRGB
            cam.allowMSAA = false;
            cam.targetTexture = rt;

            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = false;
            camData.volumeLayerMask = 0;         // 场景里的 Tonemapping 不能进来
            camData.antialiasing = AntialiasingMode.None;
            camData.renderShadows = false;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.hideFlags = HideFlags.HideAndDontSave;
            quad.transform.SetParent(root.transform, false);
            quad.transform.localPosition = new Vector3(0f, 0f, 0.05f);
            quad.transform.localScale = new Vector3(4f, 4f, 1f);   // 铺满正交视口，留足余量
            var collider = quad.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);
            var mr = quad.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            var lightGo = new GameObject("Probe Sun") { hideFlags = HideFlags.HideAndDontSave };
            lightGo.transform.SetParent(root.transform, false);
            light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;
        }

        static Vector3 RenderAndRead(
            Camera cam, RenderTexture rt, Texture2D readback, out float spread)
        {
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
            readback.Apply(false);
            RenderTexture.active = prev;

            var px = readback.GetPixels();
            var sum = Vector3.zero;
            var mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var c in px)
            {
                sum += new Vector3(c.r, c.g, c.b);
                mn = Vector3.Min(mn, new Vector3(c.r, c.g, c.b));
                mx = Vector3.Max(mx, new Vector3(c.r, c.g, c.b));
            }
            var range = mx - mn;
            spread = Mathf.Max(range.x, Mathf.Max(range.y, range.z));
            return sum / px.Length;
        }

        // ==================================================================== 工具

        static Vector3 TAt(VistaAtmosphereParameters p, float elevationDeg)
        {
            float r = p.bottomRadius + VistaAtmosphereViewData.k_PlanetRadiusOffsetKm;
            return VistaSunTransmittance.Evaluate(p, r, Mathf.Sin(elevationDeg * Mathf.Deg2Rad));
        }

        static VistaSunTransmittance.LightParams LightParamsAt(
            VistaAtmosphereParameters p, float exposure, float elevationDeg) =>
            VistaSunTransmittance.ComputeLightParams(p, TAt(p, elevationDeg), exposure);

        /// <summary>逐通道相对误差的最大值。分母是闭式期望值。</summary>
        static float MaxRel(Vector3 got, Vector3 expected)
        {
            float worst = 0f;
            for (int c = 0; c < 3; ++c)
            {
                float a = c == 0 ? expected.x : (c == 1 ? expected.y : expected.z);
                float b = c == 0 ? got.x : (c == 1 ? got.y : got.z);
                worst = Mathf.Max(worst, Mathf.Abs(a - b) / Mathf.Max(1e-9f, a));
            }
            return worst;
        }

        static string Mark(bool ok) => ok ? "✔" : "✘";

        static string Fmt(Vector3 v) =>
            "(" + v.x.ToString("F5") + ", " + v.y.ToString("F5") + ", " + v.z.ToString("F5") + ")";

        static string Fmt(Color c) =>
            "(" + c.r.ToString("F5") + ", " + c.g.ToString("F5") + ", " + c.b.ToString("F5") + ")";

        static Report Fail(string message) => new Report { passed = false, text = "✘ " + message };
    }
}
