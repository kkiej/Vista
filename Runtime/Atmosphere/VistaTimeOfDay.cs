using UnityEngine;

namespace Vista
{
    /// <summary>
    /// 时间轴组件：给定经纬度与时刻，驱动平行光的**朝向**与**颜色/强度**。
    ///
    /// ── 它解决的是哪个问题 ──
    ///
    /// 大气模块已经能画出物理正确的天空，但物体的受光还是美术手调的一盏白灯。
    /// 于是日落时天空是橙红的、地面上的石头还是正午的白 —— 这是「有大气散射」和
    /// 「看起来有大气散射」之间最大的一条缝。缝的两端各有一半：
    ///
    ///   1. **朝向**：太阳的日行轨迹是一个倾角由纬度决定的斜圆（见
    ///      <see cref="VistaSolarPosition"/>），冬夏的日出方位能差几十度。
    ///   2. **光色**：直射光 = 日面照度 × 到着色点的透射率 T。T 就是日落变红的原因，
    ///      而且它必须和天空用**同一份**大气参数算，否则两边会各红各的。
    ///
    /// ── 为什么由场景组件写灯，而不是由 RenderPass 写 ──
    ///
    /// 从渲染代码里改场景状态是反模式（编辑器里会把场景标脏、和 Undo 打架、
    /// 多相机时后渲染的那个覆盖前一个）。更要紧的是**光色会变成相机相关的** ——
    /// 同一盏灯在主相机和反射探针相机下拿到不同的 T，画面与反射对不上。
    /// 太阳照的是**几何**，不是相机，所以求值点应当是场景里的一个固定参考点
    /// （<see cref="referenceWorldY"/>），与相机无关。
    ///
    /// ── 大气参数从哪来 ──
    ///
    /// 从 <see cref="VistaAtmosphereFeature.current"/> 读，组件**不存**自己的副本 ——
    /// 理由见那个属性的注释。取不到时只驱动朝向、不动光色，并在面板上明确报警：
    /// 静默回落到地球默认值会让「忘挂 feature」表现为「光色差一点点」，那是最难查的。
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Vista/Vista Time Of Day")]
    [DisallowMultipleComponent]
    public sealed class VistaTimeOfDay : MonoBehaviour
    {
        // ==================================================================== 时间与地点

        [SerializeField]
        [Tooltip("年。低精度太阳位置算法的适用区间是 1950–2050。")]
        int m_Year = 2026;

        [SerializeField]
        [Tooltip("月。")]
        [Range(1, 12)]
        int m_Month = 6;

        [SerializeField]
        [Tooltip("日。给到当月天数以外的值不会报错，会按儒略日往后顺延（如 2 月 31 日 = 3 月 3 日）。")]
        [Range(1, 31)]
        int m_Day = 21;

        [SerializeField]
        [Tooltip("当地时钟时间（小时，含小数）。这是拖动最频繁的一个值。")]
        [Range(0f, 24f)]
        float m_LocalHours = 12f;

        [SerializeField]
        [Tooltip("纬度，北纬为正。它决定日行轨迹的倾角，也就是正午太阳能升多高、"
               + "以及影子在一天里怎么扫。")]
        [Range(-90f, 90f)]
        float m_Latitude = 35f;

        [SerializeField]
        [Tooltip("经度，东经为正。配合下面的时区决定「当地时钟几点」对应太阳在哪 —— "
               + "两者不匹配时表现为正午太阳不在正南（北半球）。")]
        [Range(-180f, 180f)]
        float m_Longitude = 139f;

        [SerializeField]
        [Tooltip("时区相对 UTC 的偏移。东八区填 8。")]
        [Range(-12f, 14f)]
        float m_UtcOffsetHours = 9f;

        // ==================================================================== 求值参考点

        [SerializeField]
        [Tooltip("求透射率的参考高度，世界 Y (m)。\n"
               + "太阳照的是几何而不是相机，所以这里填「场景里主要受光面在哪个高度」，"
               + "通常就是地面高度。\n"
               + "注意这是一个单值近似：高差越大、太阳越低，偏差越大。"
               + "实测包线（Validate Solar Position 的 F 项）——正午天顶约 ±270 m，"
               + "太阳 5° 时只有几十米。大高差场景要靠逐像素透射率补，见 CHANGELOG。")]
        float m_ReferenceWorldY = 0f;

        // ==================================================================== 输出

        [SerializeField]
        [Tooltip("要驱动的平行光。留空则依次尝试：本对象上的 Light → RenderSettings.sun。")]
        Light m_Sun;

        [SerializeField]
        [Tooltip("驱动灯的朝向。关掉可以手摆光向做构图，同时仍拿到物理光色 —— "
               + "UE 的 Sun Position Calculator 也是可分离的。")]
        bool m_DriveRotation = true;

        [SerializeField]
        [Tooltip("驱动灯的颜色与强度。需要 Vista Atmosphere feature 已挂在当前 URP Renderer 上。")]
        bool m_DriveColor = true;

        [SerializeField]
        [Tooltip("把这盏灯登记到 RenderSettings.sun。\n"
               + "大气 pass 取太阳方向时优先用主平行光，主光被剔除/关掉时退回 RenderSettings.sun —— "
               + "登记上可以让那条兜底指向同一盏灯，而不是 45° 的硬编码默认值。")]
        bool m_AssignRenderSettingsSun = true;

        // ==================================================================== 判据阈值

        /// <summary>
        /// 朝向的写入门（度）。太阳自身视角直径 0.545°，取它的 1/50 ——
        /// 远小于「阴影方向看得出变化」的量级。
        ///
        /// 有这个门是因为编辑器下每帧写 <c>transform.rotation</c> 会把场景标脏，
        /// 于是时间不动也会一直冒 <c>*</c>，保存提示变成噪声。
        /// </summary>
        const float k_RotationEpsilonDeg = 0.01f;

        /// <summary>
        /// 光色/强度的写入门（相对）。全项目的可见阈是 Weber 1%，这里取它的 1/100 ——
        /// 门只用来挡住「什么都没变」，不该吃掉任何可能看得见的变化。
        /// </summary>
        const float k_ColorRelEpsilon = 1e-4f;

        // ==================================================================== 诊断（自检/面板读）

        VistaSolarPosition.Result m_LastSolar;
        VistaSunTransmittance.LightParams m_LastLight;
        bool m_AtmosphereMissing;
        bool m_SunMissing;

        /// <summary>上一次求得的太阳位置。</summary>
        public VistaSolarPosition.Result lastSolarPosition => m_LastSolar;

        /// <summary>上一次求得的平行光参数。<c>m_DriveColor</c> 关掉时不更新。</summary>
        public VistaSunTransmittance.LightParams lastLightParams => m_LastLight;

        /// <summary>取不到 <see cref="VistaAtmosphereFeature.current"/>：光色未被驱动。</summary>
        public bool atmosphereMissing => m_AtmosphereMissing;

        /// <summary>找不到可驱动的平行光。</summary>
        public bool sunMissing => m_SunMissing;

        // ==================================================================== 可编程接口

        /// <summary>当地时钟时间（小时）。Timeline / 演示脚本驱动这一个值即可。</summary>
        public float localHours
        {
            get => m_LocalHours;
            set { m_LocalHours = value; Apply(); }
        }

        public float latitude  { get => m_Latitude;  set { m_Latitude = value;  Apply(); } }
        public float longitude { get => m_Longitude; set { m_Longitude = value; Apply(); } }

        /// <summary>求透射率的参考高度，世界 Y (m)。</summary>
        public float referenceWorldY { get => m_ReferenceWorldY; set { m_ReferenceWorldY = value; Apply(); } }

        /// <summary>设置日期。</summary>
        public void SetDate(int year, int month, int day)
        {
            m_Year = year; m_Month = month; m_Day = day;
            Apply();
        }

        /// <summary>设置时区与经纬度。</summary>
        public void SetLocation(float latitudeDeg, float longitudeDeg, float utcOffsetHours)
        {
            m_Latitude = latitudeDeg; m_Longitude = longitudeDeg; m_UtcOffsetHours = utcOffsetHours;
            Apply();
        }

        // ==================================================================== 生命周期

        void OnEnable() => Apply();

        void Update() => Apply();

        void OnValidate()
        {
#if UNITY_EDITOR
            // OnValidate 也会在反序列化过程中被调用，那个时机碰别的对象会触发
            // 「SendMessage cannot be called during OnValidate」。延到编辑器主循环里做。
            //
            // 有了它并不是因为 Update 不够 —— ExecuteAlways 的 Update 在编辑器里
            // 每次重绘都会跑。但面板上拖滑竿时若 Scene 视图没在重绘，Update 可能不跟手，
            // 而 Step 2 的验收标准明写「无一项滞后」。
            UnityEditor.EditorApplication.delayCall += ApplyIfAlive;
#endif
        }

#if UNITY_EDITOR
        void ApplyIfAlive()
        {
            // MonoBehaviour 的 == 会在原生对象已销毁时返回真 null，所以这一句是安全的
            if (this == null) return;
            Apply();
        }
#endif

        // ==================================================================== 主体

        /// <summary>
        /// 求一次太阳位置与光色并写入灯。幂等，可随时调用。
        /// </summary>
        public void Apply()
        {
            m_LastSolar = VistaSolarPosition.Evaluate(
                m_Year, m_Month, m_Day, m_LocalHours,
                m_Latitude, m_Longitude, m_UtcOffsetHours);

            // 大气参数的可用性与灯无关，所以先判 —— 若放在下面 sun == null 的提前返回之后，
            // 「既没灯又没 feature」时这个旗子会停在上一帧的值，面板就会照着过期状态报警。
            // 诊断旗子的时序错误比功能 bug 更坏：它让人去查一个不存在的问题。
            var feature = VistaAtmosphereFeature.current;
            m_AtmosphereMissing = feature == null;

            var sun = ResolveSun();
            m_SunMissing = sun == null;
            if (sun == null)
                return;

            if (m_AssignRenderSettingsSun && !ReferenceEquals(RenderSettings.sun, sun))
                RenderSettings.sun = sun;

            if (m_DriveRotation)
                ApplyRotation(sun.transform, m_LastSolar);

            // 朝向与光色分开处理：朝向只需要天文，不需要大气参数。
            // 缺 feature 时仍然把太阳摆对位置，只是不动光色 ——
            // 「一半功能可用 + 面板报警」比「整个组件静默失效」好查得多。
            if (!m_DriveColor || feature == null)
                return;

            var p = feature.parameters;

            // 参考点的 muSun 就是仰角的正弦：该点的 up 是局部天顶，
            // dot(up, sunDir) = sin(altitude)。不必绕 VistaAtmosphereViewData。
            float muSun = Mathf.Sin(m_LastSolar.altitudeDeg * Mathf.Deg2Rad);

            float r = VistaSunTransmittance.RadiusFromAltitudeMeters(
                p, m_ReferenceWorldY - feature.groundLevelWorldY);

            Vector3 transmittance = VistaSunTransmittance.Evaluate(p, r, muSun);
            m_LastLight = VistaSunTransmittance.ComputeLightParams(
                p, transmittance, VistaAtmosphereViewData.ExposureFromEV100(feature.ev100));

            ApplyLightParams(sun, m_LastLight);
        }

        /// <summary>
        /// 高度角/方位角 -> 灯的旋转。
        ///
        /// 用 Euler 而不是 <c>Quaternion.LookRotation(-dir, Vector3.up)</c>：
        /// 太阳升到天顶时 forward 与 up 共线，LookRotation 退化（结果未定义），
        /// 症状是赤道地区正午光向突然乱跳。Euler 形式没有这个退化。
        ///
        /// 推导：灯沿自身 +Z 照射，故 forward 应当 = -sunDir。
        /// <c>Euler(alt, az, 0)</c> 先绕 X 俯仰、再绕 Y 偏航，得
        /// forward = (cosAlt·sin az, -sin alt, cosAlt·cos az)，
        /// 水平分量与 -sunDir 反号 —— 因为灯要**背对**太阳照过来，
        /// 所以方位角 +180°。这也让 Inspector 上的 Rotation.X 直接读作「太阳仰角」。
        /// </summary>
        static void ApplyRotation(Transform t, in VistaSolarPosition.Result solar)
        {
            var target = Quaternion.Euler(solar.altitudeDeg, solar.azimuthDeg + 180f, 0f);
            if (Quaternion.Angle(t.rotation, target) > k_RotationEpsilonDeg)
                t.rotation = target;
        }

        /// <summary>
        /// 把物理光参数写进 <see cref="Light"/>，处理两套单位制之间的所有转换。
        ///
        /// 公开而不是私有：接缝验收自检要驱动这段代码去渲一帧再读回像素。
        /// 若自检自己抄一遍写灯逻辑，那就是第二份实现 —— 改一边忘另一边时
        /// 自检会替错误的实现背书，等于自检失效。这类「两份真相」在 #7 里吃过教训。
        /// </summary>
        public static void ApplyLightParams(Light sun, in VistaSunTransmittance.LightParams lp)
        {
            // 色温必须关掉。URP 在 UniversalRenderPipeline.cs 里无条件写
            //   GraphicsSettings.lightsUseColorTemperature = true
            // 于是引擎算 VisibleLight.finalColor 时会在 Light.color 上再乘一遍
            // CorrelatedColorTemperatureToRGB(colorTemperature)。我们的色度已经是
            // 大气透射率算出来的物理结果，再乘一层 6500K 白点偏移就等于把日落色调偏一次，
            // 而面板上完全看不出是这个开关干的。
            if (sun.useColorTemperature)
                sun.useColorTemperature = false;

            // Light.color 按 **Gamma 语义** 存：URP 用的是引擎的 VisibleLight.finalColor，
            // 而引擎在 lightsUseLinearIntensity（URP 在线性色彩空间下会打开它）时算的是
            //   finalColor = Light.color.linear × intensity
            // 我们手里的 lp.color 已经是线性色度，所以写进去之前要先 .gamma，
            // 否则会被 .linear 多转一次 —— 症状是日落的红被压暗、整体偏冷。
            Color target = QualitySettings.activeColorSpace == ColorSpace.Linear
                ? lp.color.gamma
                : lp.color;

            Color c = sun.color;
            if (Mathf.Abs(c.r - target.r) > k_ColorRelEpsilon
             || Mathf.Abs(c.g - target.g) > k_ColorRelEpsilon
             || Mathf.Abs(c.b - target.b) > k_ColorRelEpsilon)
                sun.color = target;

            float i = sun.intensity;
            // 相对判据，但分母加个下限：日落尾段 intensity 会趋 0，
            // 纯相对判据在那里会因为分母消失而永远判「变了」，于每帧重写。
            if (Mathf.Abs(i - lp.intensity) > k_ColorRelEpsilon * Mathf.Max(1f, Mathf.Abs(lp.intensity)))
                sun.intensity = lp.intensity;
        }

        /// <summary>
        /// 找要驱动的灯。显式指定 → 本对象上的 Light → <c>RenderSettings.sun</c>。
        ///
        /// 三级回落而不是强制手挂：这个组件最常见的用法就是直接挂在场景那盏
        /// Directional Light 上，那时候再让用户手动把自己拖进自己的字段是多余的仪式。
        ///
        /// 注意第三级比它看起来更宽：<c>RenderSettings.sun</c> 的 getter 在字段为空时
        /// 会**回落到场景里最亮的平行光**（procedural skybox 就是这样取太阳的）。
        /// 实测：写入 null 后立刻读回，拿到的仍是场景那盏灯。所以只要场景里有一盏平行光，
        /// 这个函数就不会返回 null，<see cref="sunMissing"/> 只在「一盏平行光都没有」时为真 ——
        /// 那也正是它该报警的时候。面板诊断自检的 A 组把这一点测成了观测值。
        /// </summary>
        public Light ResolveSun()
        {
            if (m_Sun != null && m_Sun.type == LightType.Directional)
                return m_Sun;

            var own = GetComponent<Light>();
            if (own != null && own.type == LightType.Directional)
                return own;

            var sun = RenderSettings.sun;
            return sun != null && sun.type == LightType.Directional ? sun : null;
        }
    }
}
