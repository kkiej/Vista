using UnityEditor;
using UnityEngine;

namespace Vista.Editor
{
    /// <summary>
    /// <see cref="VistaTimeOfDay"/> 的面板：报警 + 诊断。
    ///
    /// ── 为什么这个组件必须有自定义面板 ──
    ///
    /// 组件自己的注释里写了两条承诺：缺 feature 时「面板上明确报警」、
    /// 「一半功能可用 + 面板报警」比「整个组件静默失效」好查得多。但
    /// <c>atmosphereMissing</c> / <c>sunMissing</c> 只是两个 public 属性，
    /// 默认面板不显示它们 —— 也就是说那两条承诺当时只落在注释里没落到 UI 上。
    /// 一个只有代码能读到的报警等于没有报警。
    ///
    /// ── 诊断为什么值得占面板空间 ──
    ///
    /// 这个组件是「物理天文 → URP 灯参数」的转换器，中间有三层不可见的换算
    /// （曝光、1/π 接缝、gamma 往返）。出问题时的表象都是「光色不对」，而原因
    /// 可能在任一层。把每一层的中间量摊在面板上，就能当场分辨是
    /// 天文算错（高度角/方位角不对）、大气算错（T 不对）、还是写灯错
    /// （T 对但 Light.color 不对）。
    ///
    /// 「求得的 lp」与「灯上实际的值」分两行显示是刻意的：两者不一致
    /// 说明有别的东西在改这盏灯（另一个组件、动画、或手动改过又被写入门挡住了）。
    ///
    /// ── 包线读数 ──
    ///
    /// 参考海拔是个单值近似，其有效范围随太阳角剧烈变化（实测天顶 271 m、
    /// 太阳 5° 只剩 51 m）。这个数以前只存在于自检报告里，美术看不到。
    /// 放到面板上，「我这个场景高差 800 m，现在太阳 5°，包线只有 51 m」
    /// 就成了当场能读到的结论，而不是文档里一句「注意高差」。
    /// 求解走 <see cref="VistaSunTransmittance.SolveReferenceAltitudeEnvelopeMeters"/>，
    /// 与自检 F2 项**同一份实现**。
    /// </summary>
    [CustomEditor(typeof(VistaTimeOfDay))]
    public sealed class VistaTimeOfDayEditor : UnityEditor.Editor
    {
        static bool s_ShowDiagnostics = true;

        /// <summary>
        /// 诊断是活的量：时间滑竿可能被 Timeline / 演示脚本 / 别的面板驱动，
        /// 不重绘的话面板会显示一个过期状态，而过期的诊断比没有诊断更坏。
        /// 代价是这个面板打开时每帧重绘一次；包线求解是 40 次二分 × 41 段积分，
        /// 量级几十微秒，只在面板可见时发生。
        /// </summary>
        public override bool RequiresConstantRepaint() => true;

        public override void OnInspectorGUI()
        {
            var tod = (VistaTimeOfDay)target;
            serializedObject.Update();

            DrawWarnings(tod);
            DrawDefaultInspector();

            EditorGUILayout.Space();
            DrawDiagnostics(tod);
        }

        // ==================================================================== 报警

        void DrawWarnings(VistaTimeOfDay tod)
        {
            if (tod.sunMissing)
            {
                EditorGUILayout.HelpBox(
                    "找不到可驱动的平行光 —— 这个组件当前什么也没做。\n"
                  + "回落链依次试过：Sun 字段 → 本对象上的 Light → RenderSettings.sun，"
                  + "三者都不是 Directional。\n"
                  + "把组件挂到场景那盏平行光上，或把灯拖进下面的 Sun 字段。",
                    MessageType.Error);
            }

            // 关掉光色驱动时不需要大气参数，此时报警是噪声。
            bool driveColor = serializedObject.FindProperty("m_DriveColor").boolValue;
            if (driveColor && tod.atmosphereMissing)
            {
                EditorGUILayout.HelpBox(
                    "取不到 Vista Atmosphere feature：朝向仍在驱动，光色与强度没有被驱动。\n"
                  + "把 Vista Atmosphere 加到当前 URP Renderer 的 Renderer Features 上。\n"
                  + "这里显式报警而不是静默回落到地球默认参数 —— 后者会让「忘挂 feature」"
                  + "表现为「光色差一点点」，那是最难查的一类问题。",
                    MessageType.Warning);
            }
        }

        // ==================================================================== 诊断

        void DrawDiagnostics(VistaTimeOfDay tod)
        {
            s_ShowDiagnostics = EditorGUILayout.Foldout(s_ShowDiagnostics, "诊断（只读）", true);
            if (!s_ShowDiagnostics)
                return;

            ++EditorGUI.indentLevel;
            DrawSolar(tod);
            EditorGUILayout.Space();
            DrawLight(tod);
            EditorGUILayout.Space();
            DrawEnvelope(tod);
            --EditorGUI.indentLevel;
        }

        static void DrawSolar(VistaTimeOfDay tod)
        {
            var s = tod.lastSolarPosition;

            EditorGUILayout.LabelField("太阳位置（天文）", EditorStyles.boldLabel);
            Row("高度角", s.altitudeDeg.ToString("F3") + " °　"
                        + (s.altitudeDeg > 0f ? "地平线上" : "地平线下 → 直射光为 0"));
            Row("方位角", s.azimuthDeg.ToString("F3") + " °　0=北　90=东　180=南");
            Row("赤纬", s.declinationDeg.ToString("F3") + " °　季节量，全年在 ±23.44° 之间");
            Row("时角", s.hourAngleDeg.ToString("F3") + " °　0=太阳时正午，15°/h");
            Row("光向（世界）", Fmt(s.direction));

            // 时角与时钟时间之差就是「时区/经度不匹配 + 时差」的总量，是最常见的配置错误。
            // 面板上直接给出分钟数，比让人自己去算 15°/h 有用。
            float solarMinutesFromNoon = s.hourAngleDeg / 15f * 60f;
            Row("距太阳时正午", solarMinutesFromNoon.ToString("F1") + " 分钟　"
                            + "（此刻时钟与太阳的偏差；经度/时区填错时这个数会有几十分钟）");
        }

        void DrawLight(VistaTimeOfDay tod)
        {
            EditorGUILayout.LabelField("直射光（三层换算）", EditorStyles.boldLabel);

            bool driveColor = serializedObject.FindProperty("m_DriveColor").boolValue;

            // 三个条件里任一个不满足，lastLightParams 就是上一次成功求值留下的旧值。
            // 把过期数据摆在「诊断」标题下面就是在骗人 —— 尤其 sunMissing 这一支：
            // Apply 在没灯时提前返回，连大气那段都没跑。
            string staleReason =
                  tod.sunMissing        ? "没有可驱动的灯，本帧未求值"
                : !driveColor           ? "光色驱动已关闭"
                : tod.atmosphereMissing ? "缺 Atmosphere feature，未求值"
                                        : null;
            if (staleReason != null)
            {
                Row("—", staleReason);
                return;
            }

            var lp = tod.lastLightParams;
            Row("① 透射率 T", Fmt(lp.transmittance) + "　太阳到参考点，逐通道");
            Row("② 曝光后照度", Fmt(lp.exposedIlluminance) + "　= lux · T · exposure");
            Row("③ 求得 color", Fmt(lp.color) + "　线性色度，最大通道归一");
            Row("③ 求得 intensity", lp.intensity.ToString("F5") + "　= 峰值 / π（URP 漫反射缺的那个 1/π）");

            var sun = tod.ResolveSun();
            if (sun == null)
                return;

            EditorGUILayout.Space(2f);
            Row("灯上 color", Fmt(sun.color) + "　Gamma 域（引擎会再做 .linear）");
            Row("灯上 intensity", sun.intensity.ToString("F5"));

            if (!LightMatchesComputed(sun, lp, out bool colorMatch, out _))
            {
                EditorGUILayout.HelpBox(
                    "灯上的" + (colorMatch ? "强度" : "颜色")
                  + "与本组件求得的值不一致：有别的东西在改这盏灯"
                  + "（另一个组件、Animator、或 Timeline 轨道）。\n"
                  + "本组件的写入门是 1e-4，正常情况下两者必须一致。",
                    MessageType.Warning);
            }

            if (sun.useColorTemperature)
            {
                // ApplyLightParams 每次都会强制关掉它；还是 true 说明写灯那一步没跑到。
                EditorGUILayout.HelpBox(
                    "灯还开着 Use Color Temperature。URP 无条件消费这个开关，"
                  + "会在已经算好的物理色度上再乘一遍色温偏移。\n"
                  + "本组件每次写灯都会强制关掉它 —— 它还是开着，说明写灯那一步没有执行。",
                    MessageType.Error);
            }
        }

        /// <summary>
        /// 灯上的实际值是否与组件求得的值一致。
        ///
        /// 公开静态：诊断自检要验这条判据「既不误报也不漏报」，
        /// 若自检自己抄一遍比较逻辑，那就是第二份实现。
        /// 阈值取组件写入门 <c>1e-4</c> 的两倍 —— 门本身允许 1e-4 的残差不写，
        /// 用同一个数会让恰好卡在门上的情况随机报警。
        /// </summary>
        public static bool LightMatchesComputed(
            Light sun, in VistaSunTransmittance.LightParams lp,
            out bool colorMatch, out bool intensityMatch)
        {
            const float eps = 2e-4f;
            Color expected = QualitySettings.activeColorSpace == ColorSpace.Linear
                ? lp.color.gamma
                : lp.color;
            colorMatch = Mathf.Abs(sun.color.r - expected.r) <= eps
                      && Mathf.Abs(sun.color.g - expected.g) <= eps
                      && Mathf.Abs(sun.color.b - expected.b) <= eps;
            intensityMatch = Mathf.Abs(sun.intensity - lp.intensity)
                          <= eps * Mathf.Max(1f, lp.intensity);
            return colorMatch && intensityMatch;
        }

        static void DrawEnvelope(VistaTimeOfDay tod)
        {
            EditorGUILayout.LabelField("参考海拔的有效包线", EditorStyles.boldLabel);

            var feature = VistaAtmosphereFeature.current;
            if (feature == null)
            {
                Row("—", "需要 Atmosphere feature");
                return;
            }

            float altitudeDeg = tod.lastSolarPosition.altitudeDeg;
            if (altitudeDeg <= 0f)
            {
                Row("—", "太阳在地平线下，包线无定义（直射光本就是 0）");
                return;
            }

            float refAltM = tod.referenceWorldY - feature.groundLevelWorldY;
            float dh = VistaSunTransmittance.SolveReferenceAltitudeEnvelopeMeters(
                feature.parameters, Mathf.Sin(altitudeDeg * Mathf.Deg2Rad), refAltM);

            bool capped = dh >= VistaSunTransmittance.k_EnvelopeSearchMaxMeters;
            Row("参考点海拔", refAltM.ToString("F1") + " m　（相对 Atmosphere 的地面高度）");
            Row("当前包线 Δh", capped
                ? "> " + VistaSunTransmittance.k_EnvelopeSearchMaxMeters.ToString("F0") + " m　（这个仰角下随便填）"
                : dh.ToString("F0") + " m");

            EditorGUILayout.HelpBox(
                "包线 = 参考海拔填错这么多米，画面上还看不出差别"
              + "（逐通道 Weber 1% 且同通道 |ΔT| ≥ 1e-3）。\n"
              + "场景高差明显超过这个数时，山顶与谷底的直射光本该不同亮度、不同颜色，"
              + "而现在它们共用一个 T。\n"
              + "太阳越低包线越窄（实测 90° 约 271 m、15° 约 77 m、5° 约 51 m）—— "
              + "也就是说这个近似恰好在日出日落这段最关键的时间失效。"
              + "要覆盖大高差就得开逐像素透射率，即 UE5 平行光上的 "
              + "Per Pixel Atmosphere Transmittance。",
                MessageType.Info);
        }

        // ==================================================================== 工具

        static void Row(string label, string value) =>
            EditorGUILayout.LabelField(label, value);

        static string Fmt(Vector3 v) =>
            "(" + v.x.ToString("F5") + ", " + v.y.ToString("F5") + ", " + v.z.ToString("F5") + ")";

        static string Fmt(Color c) =>
            "(" + c.r.ToString("F5") + ", " + c.g.ToString("F5") + ", " + c.b.ToString("F5") + ")";
    }
}
