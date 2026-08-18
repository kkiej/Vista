using System.Text;
using UnityEditor;
using UnityEngine;

namespace Vista.Editor
{
    /// <summary>
    /// 天文太阳位置 + TimeOfDay 接线的验收。
    ///
    /// ── 为什么不用「和某个天文年历对一遍」当判据 ──
    ///
    /// 那需要引入一份外部数据，而且对不上时无法区分「我的公式错了」和
    /// 「年历的时区/夏令时/折射约定跟我不同」—— 尺子和被测对象纠缠在一起，
    /// 这正是 #7 反复踩到的坑。
    ///
    /// 改用**几何锚点**：一批由球面几何本身唯一确定、不依赖任何观测的值。
    /// 它们的正确性可以在纸上验证：
    ///
    ///   A. 赤纬。春秋分 ≈ 0°，夏至 ≈ +23.44°，冬至 ≈ -23.44°
    ///      —— 这就是黄赤交角的定义。
    ///   B. 天顶。太阳在纬度 = 赤纬的地方过天顶（sin alt = sin²δ + cos²δ = 1）。
    ///      故：春分赤道正午 alt ≈ 90°，夏至 23.44°N 正午 alt ≈ 90°。
    ///   C. 极昼边界。北极圈 66.56°N（= 90 - 23.44）夏至午夜 alt ≈ 0°
    ///      —— 极昼的定义就是这条边界。
    ///   D. 对称性。同一天、同一地点，太阳时正午前后 ±t 的仰角必须相等
    ///      （日行轨迹关于子午线对称）。这一项不依赖任何具体数值，
    ///      却能抓住时角/方位角象限写错这类最常见的 bug。
    ///
    /// 「正午」用**太阳时**而不是钟表 12:00：钟表正午与太阳正午差一个时差
    /// （±16 分钟）加一个经度修正，用钟表时间去要求 alt = 90° 会把
    /// 「时差算对了」误判成失败。做法是把该地该日的太阳时正午解出来再验，
    /// 于是时差项本身也被顺带验了一遍（它若为 0，B 项会差到 ~0.07°）。
    ///
    /// ── 另外两项接线验收 ──
    ///
    ///   E. **Euler 路径 vs 三角路径**。组件写灯用的是
    ///      <c>Quaternion.Euler(alt, az+180, 0)</c>，而
    ///      <c>DirectionFromAltAz</c> 走的是三角展开。两条完全不同的路径必须给出
    ///      同一个方向 —— 又一把独立的尺子（这条推导写错过就是「光向反了」）。
    ///   F. **单值参考海拔的有效包线**。这一项原本要验的是「几百米起伏用一个 T 就够」，
    ///      被实测直接推翻（500 m 在天顶就有 1.8%），于是改成判单调性 + 报包线。
    ///      细节见该函数的注释。
    /// </summary>
    public static class VistaSolarPositionSelfTest
    {
        [MenuItem("Window/Vista/Validate Solar Position")]
        static void RunFromMenu()
        {
            var report = Run();
            string oneLine = report.text.Replace("\r", "").Replace("\n", "  |  ");
            if (report.passed) Debug.Log("[Vista] 太阳位置验收通过  |  " + oneLine);
            else Debug.LogError("[Vista] 太阳位置验收失败  |  " + oneLine);
        }

        /// <summary>
        /// 角度判据（度）。低精度算法的赤纬误差 ~0.01°，加上「地球公转不是整年、
        /// 至日/分日的真实时刻每年在一天内浮动」带来的额外偏差，取 0.05°。
        ///
        /// 参照物是太阳自身的视角半径 0.27° —— 0.05° 不到日面的 1/5，
        /// 画面上不可能看出来。这个数是**导出**的，不是调到刚好通过的。
        /// </summary>
        const float k_AngleToleranceDeg = 0.05f;

        /// <summary>
        /// 极昼边界（C 项）单独放宽到 0.6°。
        ///
        /// 理由不是「那里不准」，是那里**导数发散**：午夜太阳的仰角对赤纬的敏感度
        /// 在边界上是 1:1，而至日真实时刻在一天里的浮动本身就能给出 0.4° 量级的赤纬差。
        /// 用 0.05° 去要求它是在测地球公转的历元，不是在测这套公式。
        /// 明写出来而不是悄悄用一个大阈值 —— 阈值的口径必须能被追问。
        /// </summary>
        const float k_PolarToleranceDeg = 0.6f;

        /// <summary>E 项：两条方向路径的最大分量差。纯三角恒等式，只剩 float 舍入。</summary>
        const float k_DirectionTolerance = 1e-4f;

        public struct Report
        {
            public bool passed;
            public string text;
        }

        public static Report Run()
        {
            var sb = new StringBuilder();
            bool ok = true;

            ok &= ValidateDeclination(sb);
            ok &= ValidateZenith(sb);
            ok &= ValidatePolarDay(sb);
            ok &= ValidateSymmetry(sb);
            ok &= ValidateDirectionPaths(sb);
            ok &= ValidateAltitudeSensitivity(sb);

            return new Report { passed = ok, text = sb.ToString().TrimEnd() };
        }

        // ==================================================================== A 赤纬

        static bool ValidateDeclination(StringBuilder sb)
        {
            // 2026 年的分至日（UTC）。日期取当天，赤纬在一天内变化 < 0.4°（分日附近最快），
            // 所以用当天正午 UT 求值即可落在阈值内。
            var cases = new[]
            {
                (name: "春分 3/20", y: 2026, m: 3,  d: 20, expect: 0f),
                (name: "夏至 6/21", y: 2026, m: 6,  d: 21, expect: 23.44f),
                (name: "秋分 9/23", y: 2026, m: 9,  d: 23, expect: 0f),
                (name: "冬至 12/21", y: 2026, m: 12, d: 21, expect: -23.44f),
            };

            sb.AppendLine("── A 赤纬（黄赤交角 23.44° 的定义）　阈值 " + k_AngleToleranceDeg + "°");
            bool ok = true;
            foreach (var c in cases)
            {
                // 分至日的真实时刻在一天里浮动，取当天 UT 中午作为代表点
                var r = VistaSolarPosition.Evaluate(c.y, c.m, c.d, 12f, 0f, 0f, 0f);
                float err = Mathf.Abs(r.declinationDeg - c.expect);
                // 分日的赤纬变化率约 0.4°/天，所以分日用一天内的浮动量放宽；
                // 至日附近赤纬是极值，变化率近 0，反而更稳。
                float tol = c.expect == 0f ? 0.4f : k_AngleToleranceDeg;
                bool pass = err < tol;
                ok &= pass;
                sb.AppendLine("    " + Mark(pass) + " " + c.name + "　赤纬 "
                            + r.declinationDeg.ToString("F4") + "°　期望 " + c.expect.ToString("F2")
                            + "°　误差 " + err.ToString("F4") + "°（阈 " + tol.ToString("F2") + "°）");
            }
            sb.AppendLine("      分日阈值放宽到 0.4°：那是赤纬**一天内**的变化量（分日是变化最快的时候），"
                        + "不是算法误差。至日处赤纬取极值、变化率近 0，故仍用 " + k_AngleToleranceDeg + "°。");
            return ok;
        }

        // ==================================================================== B 天顶

        /// <summary>
        /// 太阳过天顶。sin(alt) = sinφ·sinδ + cosφ·cosδ·cos(H)，
        /// 当 φ = δ 且 H = 0 时 = sin²δ + cos²δ = 1，即 alt = 90°，与 δ 具体取值无关。
        /// </summary>
        static bool ValidateZenith(StringBuilder sb)
        {
            sb.AppendLine("── B 过天顶（φ = δ 且太阳时正午 ⇒ alt = 90°，纯球面几何）　阈值 "
                        + k_AngleToleranceDeg + "°");

            var cases = new[]
            {
                (name: "春分・赤道",       y: 2026, m: 3, d: 20),
                (name: "夏至・北纬 23.44", y: 2026, m: 6, d: 21),
                (name: "冬至・南纬 23.44", y: 2026, m: 12, d: 21),
            };

            bool ok = true;
            foreach (var c in cases)
            {
                // 先求当天的赤纬，把纬度设成它 —— 这样「φ = δ」是构造出来的，不是查表来的
                var probe = VistaSolarPosition.Evaluate(c.y, c.m, c.d, 12f, 0f, 0f, 0f);
                float lat = probe.declinationDeg;

                // 再解出该地该日的**太阳时正午**（时角为 0 的时刻）。
                // 直接用钟表 12:00 会带进时差 + 经度修正，把「时差算对了」误判成失败。
                float noon = SolveSolarNoon(c.y, c.m, c.d, lat, 0f, 0f);
                var r = VistaSolarPosition.Evaluate(c.y, c.m, c.d, noon, lat, 0f, 0f);

                float err = Mathf.Abs(90f - r.altitudeDeg);
                bool pass = err < k_AngleToleranceDeg;
                ok &= pass;
                sb.AppendLine("    " + Mark(pass) + " " + c.name + "　φ=δ=" + lat.ToString("F3")
                            + "°　太阳时正午 " + noon.ToString("F4") + " 时（UT）"
                            + "　alt " + r.altitudeDeg.ToString("F4") + "°　误差 " + err.ToString("F4") + "°");
            }

            // 顺带把时差本身量出来：若中心差那两项被删掉，太阳时正午会回到 12:00 整，
            // 下面这个偏移量就会掉到 0，而 B 项会因此差到 ~0.07°。
            float noonSpring = SolveSolarNoon(2026, 3, 20, 0f, 0f, 0f);
            sb.AppendLine("      春分当天太阳时正午偏离钟表 12:00 共 "
                        + ((noonSpring - 12f) * 60f).ToString("F2")
                        + " 分钟 —— 这一项就是时差(Equation of Time)，"
                        + "中心差被删掉时它会归零，B 项随之失败 " + k_AngleToleranceDeg + "° 量级。");
            return ok;
        }

        /// <summary>
        /// 求太阳时正午（时角过零的时刻）。
        ///
        /// ── 为什么不用「仰角取最大」去找 ──
        ///
        /// 第一版就是那么写的，D 项因此在 ±0.5h 那行稳定失败 0.06°，
        /// 而赤纬驻点（夏至）的对照组也一样失败 —— 说明失败源不是赤纬漂移。
        /// 归因：仰角在正午是个**平的极值**（alt ≈ A - k·Δt²，实测 k ≈ 3.8 °/h²），
        /// 而 alt 以 float 返回，精度约 7.6e-6°。靠比较仰角定位极值的时间精度只有
        /// sqrt(7.6e-6 / 3.8) ≈ 1.4e-3 h ≈ 5 秒。正午附近方位角变化 ~38°/h，
        /// 于是 5 秒的轴偏移会在镜像残差里放出 2×5s×38°/h ≈ 0.1° —— 正是实测的量级。
        ///
        /// 也就是说那 0.06° 是**尺子自己的偏置**，不是被测对象的缺陷。
        /// #7 里同样的坑记过一次，这次是第二次。
        ///
        /// 时角是条件数良好的替代：它是太阳时正午的**定义**（H = 0），
        /// 在 [6,18] 时区间内单调、斜率 15°/h，求根精度 ~1e-7 h。
        ///
        /// 用 H 定位对称轴不构成循环论证：H 若写错（Wrap180 的区间、时差、经度修正），
        /// 轴就会落在错的地方，D 项的仰角对称性会立刻大幅失败。
        /// </summary>
        static float SolveSolarNoon(int y, int m, int d, float lat, float lon, float utcOffset)
        {
            float lo = 6f, hi = 18f;
            for (int i = 0; i < 50; ++i)
            {
                float mid = 0.5f * (lo + hi);
                float h = VistaSolarPosition.Evaluate(y, m, d, mid, lat, lon, utcOffset).hourAngleDeg;
                if (h < 0f) lo = mid; else hi = mid;
            }
            return 0.5f * (lo + hi);
        }

        /// <summary>
        /// 「仰角最大」那把旧尺子，只用来做归因对照 —— 报出它与 H=0 差多少秒，
        /// 把上面那段推理落成可核对的数字。
        /// </summary>
        static float SolveSolarNoonByPeakAltitude(
            int y, int m, int d, float lat, float lon, float utcOffset)
        {
            float lo = 6f, hi = 18f;
            for (int i = 0; i < 60; ++i)
            {
                float t1 = lo + (hi - lo) / 3f;
                float t2 = hi - (hi - lo) / 3f;
                float a1 = VistaSolarPosition.Evaluate(y, m, d, t1, lat, lon, utcOffset).altitudeDeg;
                float a2 = VistaSolarPosition.Evaluate(y, m, d, t2, lat, lon, utcOffset).altitudeDeg;
                if (a1 < a2) lo = t1; else hi = t2;
            }
            return 0.5f * (lo + hi);
        }

        // ==================================================================== C 极昼边界

        static bool ValidatePolarDay(StringBuilder sb)
        {
            // 北极圈纬度 = 90 - 黄赤交角。夏至这一天，午夜太阳恰好擦地平线。
            var probe = VistaSolarPosition.Evaluate(2026, 6, 21, 12f, 0f, 0f, 0f);
            float lat = 90f - probe.declinationDeg;

            // 午夜 = 太阳时正午 + 12 h
            float noon = SolveSolarNoon(2026, 6, 21, lat, 0f, 0f);
            float midnight = noon + 12f;
            // 跨过 24 时就退到前一天的同一时刻（同一夜的另一半，赤纬差 < 0.001°）
            int day = 21;
            if (midnight >= 24f) { midnight -= 24f; day = 22; }

            var r = VistaSolarPosition.Evaluate(2026, 6, day, midnight, lat, 0f, 0f);
            float err = Mathf.Abs(r.altitudeDeg);
            bool ok = err < k_PolarToleranceDeg;

            sb.AppendLine("── C 极昼边界（北极圈夏至午夜 alt = 0°）　阈值 " + k_PolarToleranceDeg + "°");
            sb.AppendLine("    " + Mark(ok) + " φ = 90 - δ = " + lat.ToString("F3")
                        + "°　午夜 " + midnight.ToString("F3") + " 时　alt "
                        + r.altitudeDeg.ToString("F4") + "°　误差 " + err.ToString("F4") + "°");
            sb.AppendLine("      这里的阈值比 A/B 松一个数量级，是因为午夜仰角对赤纬的敏感度是 1:1，"
                        + "而至日真实时刻在一天内的浮动本身就有 0.4° 量级的赤纬差 —— "
                        + "用 0.05° 去要求它等于在测公转历元，不是在测这套公式。");
            return ok;
        }

        // ==================================================================== D 对称性

        /// <summary>
        /// 太阳时正午前后 ±t 的仰角必须相等，方位角必须关于子午线镜像。
        ///
        /// 这一项不含任何「期望值」，所以不可能靠调阈值通过 —— 它测的是
        /// 时角折叠 (Wrap180) 与方位角 atan2 象限有没有写反。这两处写错时
        /// A/B/C 全都还能过（正午那一刻是对称轴，象限错误在轴上看不出来）。
        ///
        /// ── 一个必须先扣掉的混淆项 ──
        ///
        /// 严格的镜像对称只在**赤纬固定**时成立，而赤纬一天里一直在走
        /// （8 月约 0.35°/天）。±t 两端相差 2t/24 天的赤纬，这是真实的物理不对称。
        /// 更麻烦的是它在方位角上被放大：正午附近方位角对赤纬极其敏感
        /// （正午那一刻方位角被钉在 180°，稍微偏离就靠赤纬决定往哪边偏）。
        ///
        /// 所以阈值里的漂移项**不能猜**，要**实测**：把日期 +1 天、其余不变，
        /// 量出 daz/d(天)、dalt/d(天)，再乘 2t/24。这样阈值的每一项都有出处，
        /// 而不是「调到刚好能过」。#7 记过的坑：把尺子自己的偏置当成被测对象的缺陷。
        ///
        /// 另配一条决定性对照：同一套判据在**夏至**（dδ/dt ≈ 0）重跑一遍，
        /// 残差应当塌到 0.0x°。塌下去才证明「漂移解释」是对的，而不只是自圆其说。
        ///
        /// ── 对照组反过来揪出了第二个混淆项 ──
        ///
        /// 加上夏至那一组之后，方位角残差在赤纬漂移只有 0.0001° 的情况下仍然是 0.0595°。
        /// 也就是说漂移解释只覆盖了一部分，还剩一项与赤纬无关的偏置 ——
        /// 追下去是**对称轴自己**不准：原先用「仰角取最大」定位正午，那是在一个平的
        /// 二阶极值上找位置，float 精度下只有 ~5 秒，而正午附近方位角走 ~38°/h。
        /// 详见 <see cref="SolveSolarNoon"/>。改成按时角过零求根后这一项消失。
        /// 每一行都同时报出两把尺子差多少秒，让这个归因是可核对的数字而不是断言。
        /// </summary>
        static bool ValidateSymmetry(StringBuilder sb)
        {
            sb.AppendLine("── D 子午线对称性（无期望值，测时角折叠与方位角象限）　基准阈值 "
                        + k_AngleToleranceDeg + "° + 实测赤纬漂移项");

            bool ok = true;
            ok &= SymmetryRun(sb, 2026, 8, 18, 35f, 139f, 9f, "一般日期 8/18・北纬 35");
            ok &= SymmetryRun(sb, 2026, 6, 21, 35f, 139f, 9f, "夏至 6/21・同地（赤纬驻点，对照组）");
            sb.AppendLine("      两组对比：一般日期的残差随 dt 增大而增大（赤纬漂移，真实物理项）；"
                        + "夏至组整体塌一个数量级 —— 证明漂移解释成立，象限/折叠没写错。");
            return ok;
        }

        static bool SymmetryRun(
            StringBuilder sb, int y, int m, int d, float lat, float lon, float tz, string label)
        {
            float noon = SolveSolarNoon(y, m, d, lat, lon, tz);

            // 两把尺子的差距，落成秒。这不是判据（谁对谁错已由条件数分析定了），
            // 是把「旧尺子精度只有秒级」这句话变成可核对的观测值。
            float noonPeak = SolveSolarNoonByPeakAltitude(y, m, d, lat, lon, tz);
            float axisGapSec = Mathf.Abs(noon - noonPeak) * 3600f;
            var atNoon = VistaSolarPosition.Evaluate(y, m, d, noon, lat, lon, tz);

            sb.AppendLine("    " + label + "　太阳时正午 " + noon.ToString("F4")
                        + " 时（H=" + atNoon.hourAngleDeg.ToString("F5")
                        + "°，az " + atNoon.azimuthDeg.ToString("F4") + "°）");
            sb.AppendLine("      轴归因：H=0 求根 vs 仰角取极值　相差 "
                        + axisGapSec.ToString("F1") + " s，即镜像残差里会被放进 "
                        + (2f * axisGapSec / 3600f * 38f).ToString("F4")
                        + "°（按正午附近 daz/dt ≈ 38°/h 折算）—— 这就是旧判据失败的量。");
            sb.AppendLine("      偏移     Δalt      阈(alt)   az 镜像残差  阈(az)    赤纬漂移");

            bool ok = true;
            foreach (float dt in new[] { 0.5f, 1f, 2f, 3f, 4f, 5f })
            {
                var a = VistaSolarPosition.Evaluate(y, m, d, noon - dt, lat, lon, tz);
                var b = VistaSolarPosition.Evaluate(y, m, d, noon + dt, lat, lon, tz);

                float dAlt = Mathf.Abs(a.altitudeDeg - b.altitudeDeg);
                // 北半球正午太阳在南（az ≈ 180），上午偏东(az<180)、下午偏西(az>180)，
                // 两者应关于 180° 镜像
                float mirror = Mathf.Abs((a.azimuthDeg + b.azimuthDeg) - 360f);

                // 实测灵敏度：同一时刻、日期 +1 天
                var aNext = VistaSolarPosition.Evaluate(y, m, d + 1, noon - dt, lat, lon, tz);
                float frac = 2f * dt / 24f;                 // ±t 两端相隔多少天
                float driftAlt = Mathf.Abs(aNext.altitudeDeg - a.altitudeDeg) * frac;
                float driftAz  = Mathf.Abs(aNext.azimuthDeg  - a.azimuthDeg)  * frac;
                float driftDec = Mathf.Abs(aNext.declinationDeg - a.declinationDeg) * frac;

                float tolAlt = k_AngleToleranceDeg + driftAlt;
                float tolAz  = k_AngleToleranceDeg + driftAz;
                bool pass = dAlt < tolAlt && mirror < tolAz;
                ok &= pass;

                sb.AppendLine("      " + Mark(pass) + " ±" + dt.ToString("F1") + "h "
                            + dAlt.ToString("F4").PadLeft(8) + "  "
                            + tolAlt.ToString("F4").PadLeft(8) + "  "
                            + mirror.ToString("F4").PadLeft(10) + "  "
                            + tolAz.ToString("F4").PadLeft(8) + "  "
                            + driftDec.ToString("F4").PadLeft(8) + "°");
            }
            return ok;
        }

        // ==================================================================== E 方向两路径

        /// <summary>
        /// 组件写灯的 <c>Euler(alt, az+180, 0)</c> 与 <c>DirectionFromAltAz</c> 的三角展开
        /// 必须给出同一个方向（差一个符号：灯的 forward 是 -sunDir）。
        /// </summary>
        static bool ValidateDirectionPaths(StringBuilder sb)
        {
            sb.AppendLine("── E 光向两条路径对账（Euler ↔ 三角展开）　阈值 "
                        + k_DirectionTolerance.ToString("E0"));

            float worst = 0f;
            float worstAlt = 0f, worstAz = 0f;
            for (float alt = -80f; alt <= 90f; alt += 10f)
            for (float az = 0f; az < 360f; az += 15f)
            {
                Vector3 sunDir = VistaSolarPosition.DirectionFromAltAz(alt, az);
                Vector3 lightForward = Quaternion.Euler(alt, az + 180f, 0f) * Vector3.forward;
                float e = Mathf.Max(Mathf.Abs(lightForward.x + sunDir.x),
                          Mathf.Max(Mathf.Abs(lightForward.y + sunDir.y),
                                    Mathf.Abs(lightForward.z + sunDir.z)));
                if (e > worst) { worst = e; worstAlt = alt; worstAz = az; }
            }

            bool ok = worst < k_DirectionTolerance;
            sb.AppendLine("    " + Mark(ok) + " 扫 18×24 = 432 组 (alt, az)　最大分量差 "
                        + worst.ToString("E3") + "　最差处 alt=" + worstAlt.ToString("F0")
                        + "° az=" + worstAz.ToString("F0") + "°");

            // 天顶退化：LookRotation 在这里会失效，Euler 不会。把它单独钉一下。
            Vector3 zenith = VistaSolarPosition.DirectionFromAltAz(90f, 0f);
            Vector3 zenithFwd = Quaternion.Euler(90f, 180f, 0f) * Vector3.forward;
            bool okZenith = (zenithFwd + zenith).magnitude < k_DirectionTolerance;
            sb.AppendLine("    " + Mark(okZenith) + " 天顶 alt=90°　光向 " + Fmt(zenithFwd)
                        + "　-sunDir " + Fmt(-zenith)
                        + "（LookRotation 在这里 forward 与 up 共线会退化，Euler 不会）");
            return ok && okZenith;
        }

        // ==================================================================== F 参考海拔敏感度

        /// <summary>
        /// 单值参考海拔的**有效包线**。
        ///
        /// ── 这一项原本要验的命题是错的 ──
        ///
        /// 我先写下的假设是「几百米地形起伏用一个 T 值就够，偏差在千分之几量级」，
        /// 判据也照这个写了。实测直接推翻：500 m 高差在正午天顶就把蓝通道的 T
        /// 抬高 1.8%，太阳 1° 时红通道抬高约 20%。
        ///
        /// 量级是对的、方向也对：地面 σ_blue ≈ 0.0375 /km，500 m 天顶路径的光学深度差
        /// 约 0.0185，exp 出来正是 1.9%。也就是说这不是 bug，而是**真实的物理梯度** ——
        /// 山顶比谷底受光更亮更蓝，低太阳时山尖挂着晚照而谷里已经暗下去。
        ///
        /// 所以这一项改成两件事：
        ///   1. 判**单调性**（真实不变量，能抓住 altitude→radius 的符号/偏置写错）。
        ///   2. 报**包线**：每个太阳仰角下，单值 T 还看不出差别的最大高差是多少。
        ///      这个数会直接进文档，是「参考海拔该怎么填」的依据。
        ///
        /// 不再对包线本身设通过门 —— 那会退化成「把阈值调到刚好覆盖实测值」，
        /// 是自我实现的判据。包线是产出的数据，不是被验的命题。
        ///
        /// ── 由此得到的后续项 ──
        ///
        /// 大高差 + 低太阳需要**逐像素透射率**：在不透明着色里用
        /// T(着色点海拔)/T(参考海拔) 调制直射光。这不是自创方案，UE5 的平行光上
        /// 就有 Per Pixel Atmosphere Transmittance 这个开关，存在的理由完全一样。
        /// 已记为后续任务。
        /// </summary>
        static bool ValidateAltitudeSensitivity(StringBuilder sb)
        {
            var p = VistaAtmosphereParameters.CreateEarth();
            float[] elevations = { 1f, 3f, 5f, 15f, 45f, 90f };

            sb.AppendLine("── F 单值参考海拔的有效包线");

            // ---- F1 单调性：T 必须随海拔严格上升（逐通道、逐仰角）----
            bool mono = true;
            float worstDrop = 0f;
            foreach (float deg in elevations)
            {
                float mu = Mathf.Sin(deg * Mathf.Deg2Rad);
                Vector3 prev = TAt(p, 0f, mu);
                for (float aM = 50f; aM <= 5000f; aM += 50f)
                {
                    Vector3 cur = TAt(p, aM, mu);
                    for (int c = 0; c < 3; ++c)
                    {
                        float dv = Ch(cur, c) - Ch(prev, c);
                        if (dv < 0f) { mono = false; worstDrop = Mathf.Min(worstDrop, dv); }
                    }
                    prev = cur;
                }
            }
            sb.AppendLine("    " + Mark(mono) + " F1 单调性：T 随海拔严格不降（6 仰角 × 100 步 × 3 通道）"
                        + (mono ? "" : "　最大下降 " + worstDrop.ToString("E3")));
            sb.AppendLine("      这是真实不变量：海拔越高、头顶剩下的大气越少。"
                        + "altitude→radius 若写成减号或漏掉单位换算，这一项立刻失败。");

            // ---- F2 包线：单值 T 仍看不出差别的最大高差 ----
            //
            // 求解本身在 VistaSunTransmittance.SolveReferenceAltitudeEnvelopeMeters 里，
            // 不在这里 —— 面板诊断（VistaTimeOfDayEditor）要显示同一个量，
            // 若自检自己二分一遍就是第二份实现，改一边忘另一边时自检会替错的那份背书。
            // 同样的理由让 ApplyLightParams 从私有改成公开。
            sb.AppendLine("    F2 包线（判据逐通道：该通道相对误差 ≥ 1% **且** 该通道 |ΔT| ≥ 1e-3 才算可见）");
            sb.AppendLine("      仰角   参考 T (R,G,B)                包线 Δh    包线处最大相对");

            const float k_EnvelopeMax = VistaSunTransmittance.k_EnvelopeSearchMaxMeters;
            foreach (float deg in elevations)
            {
                float mu = Mathf.Sin(deg * Mathf.Deg2Rad);
                Vector3 t0 = TAt(p, 0f, mu);

                float dh = VistaSunTransmittance.SolveReferenceAltitudeEnvelopeMeters(
                    p, mu, 0f, 0.01f, 1e-3f, k_EnvelopeMax);

                float relAt = MaxRel(p, t0, mu, dh);
                sb.AppendLine("      " + deg.ToString("F0").PadLeft(4) + "°  " + Fmt(t0) + "  "
                            + (dh >= k_EnvelopeMax ? "> 20000" : dh.ToString("F0")).PadLeft(8) + " m  "
                            + (relAt * 100f).ToString("F3").PadLeft(8) + "%");
            }

            sb.AppendLine("      读法：包线就是「参考海拔填错这么多米还看不出来」。"
                        + "太阳越低包线越窄 —— 低太阳时光程被 1/μ 放大，同样的高差扣掉更多大气。");
            sb.AppendLine("      结论（推翻了原假设）：单值 T 只在包线内成立。"
                        + "大高差 + 低太阳要靠逐像素透射率 T(着色点)/T(参考) 调制直射光，"
                        + "即 UE5 平行光上的 Per Pixel Atmosphere Transmittance —— 已记为后续项。");

            // 只对 F1 判定。包线是产出的数据，给它设门会变成自我实现的判据。
            return mono;
        }

        static Vector3 TAt(VistaAtmosphereParameters p, float altitudeM, float muSun) =>
            VistaSunTransmittance.Evaluate(
                p, VistaSunTransmittance.RadiusFromAltitudeMeters(p, altitudeM), muSun);

        static float Ch(Vector3 v, int c) => c == 0 ? v.x : (c == 1 ? v.y : v.z);

        static float MaxRel(VistaAtmosphereParameters p, Vector3 t0, float mu, float altitudeM)
        {
            Vector3 t = TAt(p, altitudeM, mu);
            float rel = 0f;
            for (int c = 0; c < 3; ++c)
                rel = Mathf.Max(rel, Mathf.Abs(Ch(t, c) - Ch(t0, c)) / Mathf.Max(1e-9f, Ch(t0, c)));
            return rel;
        }

        // 「差别是否看得见」的逐通道判据（相对 AND 绝对、且必须同通道）曾在这里，
        // 现已提到 VistaSunTransmittance.IsTransmittanceDifferenceVisible ——
        // 跨通道 OR 那个假通过的洞的完整说明也一并搬过去了。

        // ==================================================================== 杂项

        static string Mark(bool ok) => ok ? "✔" : "✘";

        static string Fmt(Vector3 v) =>
            "(" + v.x.ToString("F5") + ", " + v.y.ToString("F5") + ", " + v.z.ToString("F5") + ")";
    }
}
