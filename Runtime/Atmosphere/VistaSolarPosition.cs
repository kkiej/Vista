using UnityEngine;

namespace Vista
{
    /// <summary>
    /// 真实天文太阳位置。给定经纬度与时刻，算出太阳的高度角/方位角。
    ///
    /// ── 为什么用真实天文公式，而不是"把灯绕 X 轴转一圈" ──
    ///
    /// 绕轴旋转的做法在赤道以外是错的：太阳的日行轨迹是一个**倾斜的圆**，
    /// 倾角由纬度决定，圆心随季节上下平移。绕单轴转出来的结果最直观的破绽是
    /// 日出日落方位固定不变 —— 而真实场景里冬夏的日出方位能差几十度，
    /// 这直接决定了大场景中山体、建筑的受光面在一天里怎么扫过去，
    /// 以及影子的长度曲线是什么形状。魂系/开放世界外景的"这一眼像不像真的"
    /// 很大一部分落在这条轨迹上。
    ///
    /// 成本上也没有理由省：一帧一次的三角函数。
    ///
    /// ── 精度与取舍 ──
    ///
    /// 用 USNO/NOAA 的低精度太阳位置算法（Meeus 简化式）：赤纬误差 ~0.01°，
    /// 适用 1950–2050。作为参照，太阳自身的视角半径是 0.27° —— 也就是说
    /// 误差不到日面的 4%，画面上不可能看出来。
    ///
    /// **不做大气折射修正**（地平线附近折射把太阳视位置抬高约 0.57°）。
    /// 理由不是"省事"，是**一致性**：这套管线的透射率是按几何路径积分的，
    /// 若只把视位置弯折、却不弯折积分路径，日落时刻的光色与太阳位置就会来自
    /// 两套互相矛盾的物理。要么两边都做，要么两边都不做；折射对光色的影响
    /// 远小于它对"日落时刻"的影响，所以选都不做，并把它记下来。
    /// </summary>
    public static class VistaSolarPosition
    {
        /// <summary>太阳位置的结果。</summary>
        public struct Result
        {
            /// <summary>高度角 (度)。0 = 地平线，90 = 天顶，负值 = 地平线以下。</summary>
            public float altitudeDeg;
            /// <summary>方位角 (度)。从正北起算、向东为正，[0, 360)。</summary>
            public float azimuthDeg;
            /// <summary>赤纬 (度)。诊断用：春秋分 ≈ 0，夏至 ≈ +23.44，冬至 ≈ -23.44。</summary>
            public float declinationDeg;
            /// <summary>
            /// 时角 (度)，折到 (-180, 180]。0 = 太阳正过当地子午线，即**太阳时正午**；
            /// 负 = 上午，正 = 下午；每小时约 15°。
            ///
            /// 暴露出来是因为它是唯一条件数良好的「正午」判据：仰角在正午处是个
            /// **平的极值**（二阶），拿它找正午的时间精度只有秒级；时角是过零点、
            /// 斜率 15°/h，求根精度高好几个数量级。自检的对称性判据需要一根精确的对称轴。
            /// </summary>
            public float hourAngleDeg;
            /// <summary>由着色点指向太阳的单位矢量（Unity 世界空间约定见 <see cref="DirectionFromAltAz"/>）。</summary>
            public Vector3 direction;
        }

        /// <summary>
        /// 儒略日。Meeus, Astronomical Algorithms, 第 7 章。
        /// </summary>
        /// <param name="hoursUT">世界时的小时数（含小数）。</param>
        public static double JulianDay(int year, int month, int day, double hoursUT)
        {
            if (month <= 2) { year -= 1; month += 12; }
            double a = System.Math.Floor(year / 100.0);
            // 格里历改历修正项。用 double 而不是 int 除：year 为负（公元前）时
            // C# 的整数除法朝零截断，与 floor 不同，这里会给出错一天的结果。
            double b = 2.0 - a + System.Math.Floor(a / 4.0);
            return System.Math.Floor(365.25 * (year + 4716))
                 + System.Math.Floor(30.6001 * (month + 1))
                 + day + b - 1524.5
                 + hoursUT / 24.0;
        }

        /// <summary>
        /// 求太阳位置。
        /// </summary>
        /// <param name="latitudeDeg">纬度，北纬为正。</param>
        /// <param name="longitudeDeg">经度，东经为正。</param>
        /// <param name="localHours">当地时钟时间，小时（含小数），0~24。</param>
        /// <param name="utcOffsetHours">当地时区相对 UTC 的偏移，如东八区 +8。</param>
        public static Result Evaluate(
            int year, int month, int day, float localHours,
            float latitudeDeg, float longitudeDeg, float utcOffsetHours)
        {
            // 全程 double。float 在这里不够：n 是 J2000 起算的天数（当代约 1e4），
            // 而 GMST 的系数是 24.0657098244…，两者相乘要保住小数点后好几位 ——
            // float 的 7 位有效数字会在 1e4 量级上把秒级的时角抹掉，
            // 症状是太阳位置随日期出现十几分钟的跳动。
            double hoursUT = localHours - utcOffsetHours;
            double jd = JulianDay(year, month, day, hoursUT);
            double n = jd - 2451545.0;                       // J2000.0 起的天数

            const double deg2Rad = System.Math.PI / 180.0;

            // ---- 太阳的黄道位置（USNO 低精度式）----
            double meanLongitude = 280.460 + 0.9856474 * n;  // 平黄经 L
            double meanAnomaly   = 357.528 + 0.9856003 * n;  // 平近点角 g
            meanLongitude = Wrap360(meanLongitude);
            meanAnomaly   = Wrap360(meanAnomaly);

            double g = meanAnomaly * deg2Rad;
            // 中心差：把匀速的平黄经修正成真黄经。这一项就是"时差"(Equation of Time)
            // 的主要来源，也是日晷与钟表能差 ±16 分钟的原因。
            double eclipticLongitude = meanLongitude
                                     + 1.915 * System.Math.Sin(g)
                                     + 0.020 * System.Math.Sin(2.0 * g);
            double lambda = eclipticLongitude * deg2Rad;

            double obliquity = (23.439 - 0.0000004 * n) * deg2Rad;

            // ---- 赤道坐标 ----
            double sinDec = System.Math.Sin(obliquity) * System.Math.Sin(lambda);
            double dec = System.Math.Asin(System.Math.Clamp(sinDec, -1.0, 1.0));
            // atan2 而不是 atan：λ 跨过 90°/270° 时 atan 会把赤经折回错误的象限，
            // 症状是每年有两段时间太阳跑到天空的另一半。
            double rightAscension = System.Math.Atan2(
                System.Math.Cos(obliquity) * System.Math.Sin(lambda),
                System.Math.Cos(lambda));

            // ---- 时角 ----
            // 格林尼治平恒星时 (小时)
            double gmst = Wrap24(18.697374558 + 24.06570982441908 * n);
            double lmst = Wrap24(gmst + longitudeDeg / 15.0);          // 当地平恒星时
            double hourAngleDeg = Wrap180Deg(lmst * 15.0 - rightAscension / deg2Rad);
            double hourAngle = hourAngleDeg * deg2Rad;

            // ---- 地平坐标 ----
            double phi = latitudeDeg * deg2Rad;
            double sinAlt = System.Math.Sin(phi) * System.Math.Sin(dec)
                          + System.Math.Cos(phi) * System.Math.Cos(dec) * System.Math.Cos(hourAngle);
            double alt = System.Math.Asin(System.Math.Clamp(sinAlt, -1.0, 1.0));

            // 方位角从正北起、向东为正。用 atan2 形式而不是 acos 形式：
            // acos 形式在正午前后要靠时角符号手工补象限，容易写漏。
            double az = System.Math.Atan2(
                -System.Math.Sin(hourAngle) * System.Math.Cos(dec),
                System.Math.Cos(phi) * System.Math.Sin(dec)
                    - System.Math.Sin(phi) * System.Math.Cos(dec) * System.Math.Cos(hourAngle));

            var r = new Result
            {
                altitudeDeg = (float)(alt / deg2Rad),
                azimuthDeg = (float)Wrap360(az / deg2Rad),
                declinationDeg = (float)(dec / deg2Rad),
                hourAngleDeg = (float)hourAngleDeg,
            };
            r.direction = DirectionFromAltAz(r.altitudeDeg, r.azimuthDeg);
            return r;
        }

        /// <summary>
        /// 高度角/方位角 -> Unity 世界方向（指向太阳）。
        ///
        /// 世界朝向约定：**+Z = 正北，+X = 正东，+Y = 天顶**。
        /// 这是 Unity 里最通行的一套（地形导入、GPS 定位、地图类工具都按它来），
        /// 换约定只需改这一个函数。
        /// </summary>
        public static Vector3 DirectionFromAltAz(float altitudeDeg, float azimuthDeg)
        {
            float alt = altitudeDeg * Mathf.Deg2Rad;
            float az = azimuthDeg * Mathf.Deg2Rad;
            float cosAlt = Mathf.Cos(alt);
            return new Vector3(
                Mathf.Sin(az) * cosAlt,     // 东
                Mathf.Sin(alt),             // 上
                Mathf.Cos(az) * cosAlt);    // 北
        }

        static double Wrap360(double d) { d %= 360.0; return d < 0.0 ? d + 360.0 : d; }
        static double Wrap24(double h) { h %= 24.0; return h < 0.0 ? h + 24.0 : h; }

        /// <summary>折到 (-180, 180]。时角必须落在这个区间，否则地平坐标的象限会错。</summary>
        static double Wrap180Deg(double d)
        {
            d = Wrap360(d);
            return d > 180.0 ? d - 360.0 : d;
        }
    }
}
