using System.Text;
using UnityEditor;
using UnityEngine;

namespace Vista.Editor
{
    /// <summary>
    /// <see cref="VistaTimeOfDayEditor"/> 面板上那几条报警的可达性验收。
    ///
    /// ── 为什么面板也要自检 ──
    ///
    /// 报警是保护性代码，而保护性代码最常见的失效方式不是「报错」而是「永远不报」。
    /// 一条永不触发的 HelpBox 与一行注释等价，占着面板空间但不提供任何信息。
    /// 所以每一条报警都要给出**能让它亮起来的场景**，以及**能让它熄灭的场景** ——
    /// 只验其一都不够：只验亮会漏掉误报，只验灭会漏掉漏报。
    ///
    /// 这条纪律在接缝自检的 C 项里立过：每一条保护性代码配一条反例测量，
    /// 反例也通过的话说明那行代码没有实质后果，应当删掉。
    ///
    /// ── 四组 ──
    ///
    /// A. <c>sunMissing</c>：**正向格不可达**，原因被测成了观测值 —— 见
    ///    <see cref="CheckSunMissing"/>。这是四组里唯一一处「想验的东西验不了」，
    ///    所以标题上就写明，而不是让它读起来像通过了双向。
    /// B. <c>atmosphereMissing</c> 与 <c>VistaAtmosphereFeature.current</c> 一致。
    ///    这一组**只是一致性核对，不是反例对照** —— 见 <see cref="CheckAtmosphereFlag"/>。
    /// C. 灯值一致性判据双向：写灯后一致、手动污染后不一致、再 Apply 后恢复一致。
    /// D. <c>useColorTemperature</c> 报警双向：手动打开后条件成立、Apply 后熄灭。
    /// </summary>
    public static class VistaTimeOfDayDiagnosticsSelfTest
    {
        [MenuItem("Window/Vista/Validate TimeOfDay Diagnostics")]
        static void RunFromMenu()
        {
            var report = Run();
            string oneLine = report.text.Replace("\r", "").Replace("\n", "  |  ");
            if (report.passed) Debug.Log("[Vista] 时间轴面板诊断验收通过  |  " + oneLine);
            else Debug.LogError("[Vista] 时间轴面板诊断验收失败  |  " + oneLine);
        }

        public struct Report
        {
            public bool passed;
            public string text;
        }

        public static Report Run()
        {
            var feature = VistaAtmosphereFeature.current;
            if (feature == null)
                return Fail("取不到 VistaAtmosphereFeature.current：C/D 两组要用真实大气参数求光色，"
                          + "无从测量。把 Vista Atmosphere 挂到当前 URP Renderer 上再跑。");

            var sb = new StringBuilder();
            GameObject go = null;
            Light prevSun = RenderSettings.sun;

            try
            {
                go = new GameObject("Vista TOD Diagnostics Probe")
                { hideFlags = HideFlags.HideAndDontSave };
                var tod = go.AddComponent<VistaTimeOfDay>();

                bool ok = true;
                ok &= CheckSunMissing(sb, go, tod);
                ok &= CheckAtmosphereFlag(sb, tod);
                ok &= CheckLightConsistency(sb, tod);
                ok &= CheckColorTemperatureAlarm(sb, tod);

                return new Report { passed = ok, text = sb.ToString().TrimEnd() };
            }
            finally
            {
                RenderSettings.sun = prevSun;
                if (go != null) Object.DestroyImmediate(go);
            }
        }

        // ==================================================================== A

        /// <summary>
        /// <c>sunMissing</c>。
        ///
        /// ── 想测的那一格不可达，而原因值得记下来 ──
        ///
        /// 本来打算「清掉 RenderSettings.sun → 旗子应为 true」。实测稳定失败，
        /// 归因行显示 <c>ResolveSun</c> 捡到了场景里的 Directional Light。
        /// 第一反应是竞态（场景那盏灯上也挂着 VistaTimeOfDay，开着
        /// AssignRenderSettingsSun，编辑器重绘时会把自己写回这个全局），于是把清空
        /// 挪到 <c>Apply</c> 的前一行 —— 仍然失败。中间只剩一次 Apply，不可能有别人插进来。
        ///
        /// 真正的原因是 <c>RenderSettings.sun</c> 的 **getter 有回落语义**：字段为空时
        /// 它返回场景里最亮的平行光（这是 procedural skybox 取太阳的方式）。
        /// 下面第一格把这一点测成数字，而不是留一句断言。
        ///
        /// 由此得到的产品结论：<c>ResolveSun</c> 的第三级在任何**有平行光的场景**里
        /// 都不会返回 null，所以 <c>sunMissing</c> 只在「一盏平行光都没有」时为真 ——
        /// 而那正是它该报警的时候（用户面对的是没有直射光的画面）。旗子的语义是对的，
        /// 只是在这个场景里造不出触发条件。
        ///
        /// 不为了凑一格通过去硬造条件（禁用场景里的灯、改可见性、开临时场景）——
        /// 那些手段本身就是风险源，而覆盖不到的路径要说是覆盖不到。
        /// 这是 #7 记下的「未覆盖路径的假通过」的反面。
        /// </summary>
        static bool CheckSunMissing(StringBuilder sb, GameObject go, VistaTimeOfDay tod)
        {
            sb.AppendLine("── A sunMissing（正向格不可达，理由见源码注释）");

            // 第一格：把「getter 有回落语义」测成观测值。
            // 这是上面那段推理的证据，也是「正向格为什么不可达」的唯一依据。
            RenderSettings.sun = null;
            var back = RenderSettings.sun;
            bool fallsBack = back != null && back.type == LightType.Directional;
            sb.AppendLine("    " + Mark(fallsBack) + " 写 RenderSettings.sun = null 后立刻读回 → "
                        + (back == null ? "null" : back.name + "（场景 " + back.gameObject.scene.name
                                                 + "，type " + back.type + "）"));
            sb.AppendLine("      即 getter 在字段为空时回落到场景里最亮的平行光。"
                        + "于是 ResolveSun 的第三级在有平行光的场景里恒非空，"
                        + "sunMissing = true 在此不可达 —— 它只在无平行光场景下出现，"
                        + "而那时报警才有意义。");

            // 第二格：反向可达且必须为假 —— 挂上平行光后旗子要熄。
            // 只有这一格是真判据；它挡的是「旗子写死成 true 导致面板常亮红条」，
            // 那种失败会让用户学会忽略所有报警，比不报警更坏。
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;
            tod.Apply();
            bool offWithLight = !tod.sunMissing;
            sb.AppendLine("    " + Mark(offWithLight) + " 自身挂上 Directional 后 → sunMissing = "
                        + tod.sunMissing + "　期望 false"
                        + "（反例意义：若写死成 true，面板会常亮红条，报警的信噪比归零）");

            return fallsBack && offWithLight;
        }

        // ==================================================================== B

        /// <summary>
        /// <c>atmosphereMissing</c> 与 <see cref="VistaAtmosphereFeature.current"/> 一致。
        ///
        /// ── 为什么这一组给不出反例对照 ──
        ///
        /// <c>current</c> 是 feature 在 <c>Create()</c> / <c>Dispose()</c> 里设的静态量，
        /// Editor 侧没有合法途径把它翻成 null 再翻回来（硬翻要么改可见性、要么重建整个
        /// Renderer，两者都会让自检本身变成风险源）。所以这一组只能核对一致，不能造反例。
        ///
        /// 明写这一点而不是让它看起来像通过了一条强判据 —— 覆盖不到的路径要说是覆盖不到，
        /// 这正是 #7 记下的「未覆盖路径的假通过」。
        ///
        /// 真正被这一组挡住的是**时序**错误：<c>Apply</c> 里这个旗子必须在
        /// 「sun == null 提前返回」之前算，否则「既没灯又没 feature」时它会停在上一帧的值。
        /// 上面 A 组刚让组件走过一次无灯路径，此刻旗子仍与 current 一致，就说明它不是
        /// 那次提前返回留下的残值。
        /// </summary>
        static bool CheckAtmosphereFlag(StringBuilder sb, VistaTimeOfDay tod)
        {
            bool expected = VistaAtmosphereFeature.current == null;
            bool ok = tod.atmosphereMissing == expected;

            sb.AppendLine("── B atmosphereMissing 一致性（无反例对照，理由见源码注释）");
            sb.AppendLine("    " + Mark(ok) + " current == null 为 " + expected
                        + "　旗子为 " + tod.atmosphereMissing);
            sb.AppendLine("      刚走过 A 组的无灯路径后旗子仍然一致 → 它是在 sun 提前返回**之前**算的，"
                        + "不是上一帧的残值。这是本组唯一挡得住的失败模式。");
            return ok;
        }

        // ==================================================================== C

        /// <summary>
        /// 面板那条「有别的东西在改这盏灯」的判据，走
        /// <see cref="VistaTimeOfDayEditor.LightMatchesComputed"/> —— 与面板同一份实现。
        ///
        /// 三步：写灯后一致（不误报）→ 手动污染后不一致（不漏报）→ 再 Apply 后恢复（幂等）。
        /// 中间那步是反例：若污染后仍判「一致」，这条报警就永远不会亮，等于不存在。
        ///
        /// 颜色与强度分开污染：判据是两项的 AND，只污染一项才能验出另一项没有被
        /// 「一项通过就整体通过」这类写法短路掉。
        /// </summary>
        static bool CheckLightConsistency(StringBuilder sb, VistaTimeOfDay tod)
        {
            var sun = tod.ResolveSun();
            sb.AppendLine("── C 灯值一致性判据双向（面板「有别的东西在改这盏灯」那条）");

            if (sun == null)
            {
                sb.AppendLine("    ✘ A 组之后仍取不到灯，本组无从测量。");
                return false;
            }

            tod.Apply();
            var lp = tod.lastLightParams;
            bool clean = VistaTimeOfDayEditor.LightMatchesComputed(sun, lp, out _, out _);
            sb.AppendLine("    " + Mark(clean) + " 刚写完灯 → 判「一致」= " + clean
                        + "　期望 true　（求得 intensity " + lp.intensity.ToString("F5")
                        + "　灯上 " + sun.intensity.ToString("F5") + "）");

            // 反例 1：只动强度。乘 1.5 —— 远超 2e-4 的判据门，量级上的失败。
            sun.intensity = lp.intensity * 1.5f + 0.1f;
            bool caughtIntensity = !VistaTimeOfDayEditor.LightMatchesComputed(
                sun, lp, out bool colorStillOk, out _);
            bool intensityAttributed = caughtIntensity && colorStillOk;
            sb.AppendLine("    " + Mark(intensityAttributed) + " 只污染 intensity（×1.5+0.1）→ 判「不一致」= "
                        + caughtIntensity + "，且颜色项仍判一致 = " + colorStillOk
                        + "　期望 true/true　（后者确认两项没被短路成一项）");

            // 反例 2：只动颜色。
            tod.Apply();
            sun.color = new Color(0.2f, 0.9f, 0.4f, 1f);
            bool caughtColor = !VistaTimeOfDayEditor.LightMatchesComputed(
                sun, lp, out _, out bool intensityStillOk);
            bool colorAttributed = caughtColor && intensityStillOk;
            sb.AppendLine("    " + Mark(colorAttributed) + " 只污染 color → 判「不一致」= " + caughtColor
                        + "，且强度项仍判一致 = " + intensityStillOk + "　期望 true/true");

            // 恢复：组件每帧都会 Apply，必须能把被污染的状态拉回来。
            tod.Apply();
            bool restored = VistaTimeOfDayEditor.LightMatchesComputed(sun, tod.lastLightParams, out _, out _);
            sb.AppendLine("    " + Mark(restored) + " 再 Apply → 恢复一致 = " + restored
                        + "　期望 true（幂等；否则用户手动碰过灯就再也回不去）");

            return clean && intensityAttributed && colorAttributed && restored;
        }

        // ==================================================================== D

        /// <summary>
        /// 面板那条「灯还开着 Use Color Temperature」的报警。
        ///
        /// 它的后果已由接缝自检的 C 项测出来了（3000K 让光色偏 85.22%），这里只验
        /// **面板能看见这个状态**：手动打开后条件成立、Apply 一次后熄灭。
        /// 两者缺一：只验前者会漏掉「关不掉」，只验后者会漏掉「永远不报」。
        /// </summary>
        static bool CheckColorTemperatureAlarm(StringBuilder sb, VistaTimeOfDay tod)
        {
            var sun = tod.ResolveSun();
            sb.AppendLine("── D useColorTemperature 报警双向");

            if (sun == null)
            {
                sb.AppendLine("    ✘ 取不到灯，本组无从测量。");
                return false;
            }

            sun.useColorTemperature = true;
            sun.colorTemperature = 3000f;
            bool alarmOn = sun.useColorTemperature;
            sb.AppendLine("    " + Mark(alarmOn) + " 手动开 3000K → 报警条件成立 = " + alarmOn
                        + "　（后果量级见接缝自检 C 项：偏离 85%）");

            tod.Apply();
            bool alarmOff = !sun.useColorTemperature;
            sb.AppendLine("    " + Mark(alarmOff) + " Apply 一次 → 已强制关掉 = " + alarmOff
                        + "　期望 true（反例：关不掉的话报警会常亮，且物理色度被色温乘坏）");

            return alarmOn && alarmOff;
        }

        // ==================================================================== 工具

        static string Mark(bool ok) => ok ? "✔" : "✘";

        static Report Fail(string message) => new Report { passed = false, text = "✘ " + message };
    }
}
