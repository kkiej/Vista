using UnityEngine;

namespace Vista
{
    /// <summary>
    /// CPU 侧的"太阳到着色点"透射率求值器，以及由它导出的 URP 平行光参数。
    ///
    /// ── 为什么要在 CPU 上重算一份，而不是读回 GPU 的 Transmittance LUT ──
    ///
    /// <c>Light.color</c> / <c>Light.intensity</c> 是 CPU 属性，所以 T 必须在 CPU 拿到。
    /// 读回那张表是"零物理重复"的写法，但异步读回有 1~2 帧延迟，而延迟的那一项是
    /// **直射光色** —— 拖时间滑竿时天空已经红了、物体还是白的，是全场最显眼的滞后。
    /// 文档 Step 2 的验收标准明写"无一项滞后"。同步读回则会 stall。
    ///
    /// 另外还有三个消费者根本不在渲染上下文里：Step 4 的 PRTGI 烘焙（要对每个时刻
    /// relight）、自检、Editor 的时间轴预览。它们没有"上一帧的读回结果"可用。
    ///
    /// Chapman 函数那类解析近似不够用：它只覆盖**指数**密度剖面，而臭氧是帐篷剖面，
    /// 正是黄昏蓝紫对侧色的来源，这个项目里不能丢。
    ///
    /// ── 第二份实现的风险怎么控 ──
    ///
    /// 本文件是 <c>AtmosphereDef.hlsl</c> 里 VistaSampleMedium +
    /// VistaComputeOpticalDepthToTopAtmosphereBoundary 的逐行镜像，改一边必须改另一边。
    /// 靠纪律不够，所以配了一条 CPU/GPU 逐通道对账自检（Validate Sun Transmittance）。
    /// 那条自检是双向的：CPU 走解析积分，GPU 走 LUT + Bruneton 参数化 + 双线性插值，
    /// 两条完全不同的路径对上，才说明**两边都对**。多一把独立的尺子是资产不是负债 ——
    /// #7 里两次"尺子自己坏了"都是因为只有一把尺子。
    /// </summary>
    public static class VistaSunTransmittance
    {
        /// <summary>
        /// 光学深度积分的段数。必须与 <c>AtmosphereLut.compute</c> 的
        /// VISTA_TRANSMITTANCE_SAMPLE_COUNT 一致 —— 对账自检的阈值是按"同段数下
        /// 只差 LUT 插值误差"定的，段数不同会让那条自检失去意义。
        /// </summary>
        public const int k_OpticalDepthSamples = 40;

        /// <summary>
        /// URP 直接光漫反射缺失的 1/π。
        ///
        /// URP 的直接光是 <c>albedo · (lightColor · intensity) · NdotL</c>
        /// （Lighting.hlsl 的 LightingLambert 只有 lightColor·NdotL，BRDF.hlsl 的
        /// brdfData.diffuse 是裸 albedo，UniversalRenderPipelineCore 里
        /// lightColor = finalColor = color.linear · intensity，全程没有 π 因子）。
        /// 物理上 Lambert BRDF 是 albedo/π。两边相等就要求
        ///   color · intensity = E_exposed / π
        ///
        /// 注意 URP **不消费** <c>Light.lightUnit</c>（物理灯单位是 HDRP 独有，
        /// URP 包里搜不到任何 LightUnit 引用），所以这个系数只能我们自己乘。
        /// </summary>
        public const float k_UrpDiffuseNormalization = 1f / Mathf.PI;

        /// <summary>
        /// 太阳低到一定程度后停止求 T 并把灯归零的高度角余弦阈值。
        ///
        /// 不是为了省性能（这函数一帧一次），是为了避免一个真实的物理断崖：
        /// muSun 略小于 0 时太阳已在地平线下，直射光物理上就是 0，但
        /// Transmittance LUT 的参数化在 mu &lt; 0 且视线穿地时给出的是"穿过地心"的
        /// 无意义值（见 AtmosphereScattering.hlsl 里 VistaSampleTransmittanceToSun 的
        /// 警告）。GPU 侧靠 VistaEarthShadow 归零，CPU 侧就是这个阈值。
        ///
        /// 取 0 而不是某个负的软化值：地平线附近的软化应该由**大气自己**给出
        /// （T 在那里已经掉到 1e-2 量级，是连续的），人为再加一层淡出会让
        /// "日落时刻的光色"变成一个凑出来的东西，就不能拿物理去辩护了。
        /// </summary>
        const float k_MinMuSun = 0f;

        /// <summary>
        /// 大气介质在某海拔处的消光系数 (1/km)。
        /// <c>AtmosphereDef.hlsl</c> 的 VistaSampleMedium 的 extinction 分支镜像。
        /// </summary>
        public static Vector3 SampleExtinction(VistaAtmosphereParameters p, float altitudeKm)
        {
            float densityRayleigh = Mathf.Exp(-altitudeKm / Mathf.Max(1e-4f, p.rayleighScaleHeight));
            float densityMie      = Mathf.Exp(-altitudeKm / Mathf.Max(1e-4f, p.mieScaleHeight));
            // 臭氧帐篷剖面（Bruneton）：峰值在 ~25 km，线性上下降到 0
            float densityOzone = Mathf.Clamp01(
                1f - Mathf.Abs(altitudeKm - p.ozoneTentCenter) / Mathf.Max(1e-4f, p.ozoneTentHalfWidth));

            // Mie 消光不得小于 Mie 散射，否则 exp(-opticalDepth) > 1 会造成能量增益。
            // 这个钳制在 GPU 侧是在 VistaAtmosphereParameters.Bind 里做的（写 cbuffer 时），
            // CPU 侧读的是原始字段，所以必须在这里重做一遍 —— 漏了它，
            // 用户把 mieExtinction 填得比 mieScattering 小时 CPU 与 GPU 会分叉。
            Vector3 mieExt = new Vector3(
                Mathf.Max(p.mieExtinction.x, p.mieScattering.x),
                Mathf.Max(p.mieExtinction.y, p.mieScattering.y),
                Mathf.Max(p.mieExtinction.z, p.mieScattering.z));

            // Rayleigh 吸收为 0，故其消光 == 散射
            return p.rayleighScattering * densityRayleigh
                 + mieExt * densityMie
                 + p.ozoneAbsorption * densityOzone;
        }

        /// <summary>
        /// 从 (r, mu) 沿视线到大气顶的距离 (km)。
        /// mu = dot(up, dir)，向上为正。
        /// </summary>
        public static float DistanceToTopBoundary(VistaAtmosphereParameters p, float r, float mu)
        {
            float top = p.topRadius;
            float discriminant = r * r * (mu * mu - 1f) + top * top;
            return Mathf.Max(0f, -r * mu + Mathf.Sqrt(Mathf.Max(0f, discriminant)));
        }

        /// <summary>
        /// 到大气顶的光学深度。梯形法，两端点权重 0.5 —— 与
        /// <c>VistaComputeOpticalDepthToTopAtmosphereBoundary</c> 逐行一致
        /// （含 <c>i &lt;= sampleCount</c> 这个上界：循环跑 41 个点、40 段）。
        /// </summary>
        public static Vector3 OpticalDepthToTopBoundary(
            VistaAtmosphereParameters p, float r, float mu, int sampleCount = k_OpticalDepthSamples)
        {
            float dx = DistanceToTopBoundary(p, r, mu) / sampleCount;

            Vector3 opticalDepth = Vector3.zero;
            for (int i = 0; i <= sampleCount; ++i)
            {
                float d = i * dx;
                // 余弦定理求该采样点的半径
                float ri = Mathf.Sqrt(Mathf.Max(0f, d * d + 2f * r * mu * d + r * r));
                Vector3 extinction = SampleExtinction(p, ri - p.bottomRadius);
                float weight = (i == 0 || i == sampleCount) ? 0.5f : 1f;
                opticalDepth += extinction * (weight * dx);
            }
            return opticalDepth;
        }

        /// <summary>
        /// 从半径 r 处沿 muSun 方向到大气顶的透射率，逐通道。
        /// 太阳在地平线以下时返回 0（理由见 <see cref="k_MinMuSun"/>）。
        /// </summary>
        /// <param name="r">到星球中心的半径 (km)。</param>
        /// <param name="muSun">dot(up, sunDir)，向上为正。</param>
        public static Vector3 Evaluate(VistaAtmosphereParameters p, float r, float muSun)
        {
            if (muSun <= k_MinMuSun)
                return Vector3.zero;

            Vector3 od = OpticalDepthToTopBoundary(p, r, muSun);
            return new Vector3(Mathf.Exp(-od.x), Mathf.Exp(-od.y), Mathf.Exp(-od.z));
        }

        /// <summary>
        /// 从大气视图数据求透射率。muSun 由 <c>viewPosKm</c> 的 up 与太阳方向点乘得到，
        /// 与 GPU 侧 <c>VistaEvaluateScatterSample</c> 的算法完全一致。
        /// </summary>
        public static Vector3 Evaluate(VistaAtmosphereParameters p, in VistaAtmosphereViewData view)
        {
            Vector3 up = view.viewHeightKm > 1e-4f
                ? view.viewPosKm / view.viewHeightKm
                : Vector3.up;
            return Evaluate(p, view.viewHeightKm, Vector3.Dot(up, view.sunDirection));
        }

        /// <summary>
        /// 平行光参数：把绝对光度量换算成 URP 的 <c>color × intensity</c>。
        /// </summary>
        public struct LightParams
        {
            /// <summary>色度，最大通道归一到 1。线性空间。</summary>
            public Color color;
            /// <summary>幅度，即 <c>Light.intensity</c>。</summary>
            public float intensity;
            /// <summary>换算前的透射率，逐通道。诊断/自检用。</summary>
            public Vector3 transmittance;
            /// <summary>曝光后的太阳照度 E_exposed = lux · T · exposure，逐通道。诊断用。</summary>
            public Vector3 exposedIlluminance;
        }

        /// <summary>
        /// 求 URP 平行光该填什么。
        ///
        ///   color · intensity = sunIlluminanceLux · T · exposure / π
        ///
        /// 幅度放 intensity、色度放 color，而不是把整个矢量塞进 color：
        /// <c>Light.color</c> 在 Inspector 上是非 HDR 的颜色选择器，塞 &gt;1 的值虽然
        /// 代码里能生效，但一被面板碰到就会被夹掉，表现为"手点一下灯颜色，画面突然变暗"。
        /// UE 与 HDRP 也都是色度/幅度分离的。
        ///
        /// 归一化取**最大通道**而不是亮度：取亮度会让日落时 T=(0.3,0.1,0.03) 归一出
        /// 一个 &gt;1 的红通道，同样被面板夹掉。最大通道保证 color 的三个分量都在 [0,1]。
        /// </summary>
        public static LightParams ComputeLightParams(
            VistaAtmosphereParameters p, Vector3 transmittance, float exposure)
        {
            Vector3 e = transmittance * (p.sunIlluminanceLux * exposure);
            Vector3 rgb = e * k_UrpDiffuseNormalization;

            float peak = Mathf.Max(rgb.x, Mathf.Max(rgb.y, rgb.z));

            var lp = new LightParams
            {
                transmittance = transmittance,
                exposedIlluminance = e,
                intensity = Mathf.Max(0f, peak),
            };

            // peak 为 0（太阳在地平线下）时色度无定义。给白而不是黑：
            // intensity 已经是 0，画面上没有区别，但留一个合法色度可以让美术在
            // 面板上看到"灯还是白的、只是强度为 0"，而不是"灯变黑了"这种像 bug 的状态。
            lp.color = peak > 1e-8f
                ? new Color(rgb.x / peak, rgb.y / peak, rgb.z / peak, 1f)
                : Color.white;

            return lp;
        }

        /// <inheritdoc cref="ComputeLightParams(VistaAtmosphereParameters, Vector3, float)"/>
        public static LightParams ComputeLightParams(
            VistaAtmosphereParameters p, in VistaAtmosphereViewData view)
        {
            return ComputeLightParams(p, Evaluate(p, view), view.exposure);
        }

        // ============================================================ 单值参考海拔的有效包线

        /// <summary>
        /// 相对地面的海拔 (m) -> 星球半径 (km)。
        ///
        /// 下限用 GPU 侧同一个偏置常量：着色器里相机/着色点都被抬过这 10 m
        /// （避免射线-球求交退化），CPU 侧跟着抬是为了让两边采到同一个 r。
        /// 量级上无所谓（10 m 对 8 km 标高的密度影响是 0.1%），但省下一个"为什么差一点"。
        /// </summary>
        public static float RadiusFromAltitudeMeters(
            VistaAtmosphereParameters p, float altitudeMeters)
        {
            float altitudeKm = altitudeMeters * VistaAtmosphereParameters.worldToAtmosphere;
            return p.bottomRadius
                 + Mathf.Max(VistaAtmosphereViewData.k_PlanetRadiusOffsetKm, altitudeKm);
        }

        /// <summary>
        /// 两个透射率之差是否**看得见**。逐通道判：该通道既要绝对可见、又要相对可见。
        ///
        /// ── 为什么必须逐通道，且必须是 AND ──
        ///
        /// 低太阳时三通道的 T 差两三个数量级（实测 0.5° 是 0.168/0.022/0.0003）。
        /// 若写成"最大相对 或 最大绝对"的跨通道 OR，就会出现**红通道的绝对量小
        /// 去替蓝通道的相对量大担保**——两个数根本不来自同一个通道，那是个假通过的洞。
        /// 这个坑在透射率对账那条自检里踩过一次。
        ///
        /// AND 的含义：相对误差大但绝对量看不见（蓝通道趋 0 时分母自己都快没了）
        /// 就不算可见；绝对量可见但相对占比极小同样不算。两条都过才是真看得见。
        /// </summary>
        /// <param name="relThreshold">相对门。默认 Weber 1%，全项目共用的可见阈。</param>
        /// <param name="absThreshold">
        /// 绝对门。默认 1e-3 —— 受光面亮度 = albedo·lux·T/π，正午满照(T=1)是参考白
        /// 0.3·120000/π ≈ 1.146e4 cd/m²，所以 |ΔT| 就是"错了参考白的百分之几"，
        /// 1e-3 = 0.1%，比 Weber 阈还严一个数量级。
        /// </param>
        public static bool IsTransmittanceDifferenceVisible(
            Vector3 reference, Vector3 candidate,
            float relThreshold = 0.01f, float absThreshold = 1e-3f)
        {
            for (int c = 0; c < 3; ++c)
            {
                float a = c == 0 ? reference.x : (c == 1 ? reference.y : reference.z);
                float b = c == 0 ? candidate.x : (c == 1 ? candidate.y : candidate.z);
                float abs = Mathf.Abs(b - a);
                if (abs >= absThreshold && abs >= relThreshold * Mathf.Max(1e-9f, a))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 包线搜索的上界 (m)。到顶只说明「这个仰角下参考海拔随便填」，不是物理量 ——
        /// 取 20 km 是因为它已经超过任何可玩场景的高差，再往上算没有意义。
        /// 面板与自检共用这一个数，否则两边报「> 20000」的口径会不一致。
        /// </summary>
        public const float k_EnvelopeSearchMaxMeters = 20000f;

        /// <summary>
        /// 单值参考海拔的**有效包线**：参考海拔填错多少米，画面上还看不出来。

        ///
        /// ── 这个量为什么值得作为产品级 API 暴露 ──
        ///
        /// 用一个参考高度的 T 去照亮整个场景是个近似，而它的有效范围随太阳角剧烈变化：
        /// 实测正午天顶约 271 m，太阳 5° 时只剩 51 m，1° 时 41 m。魂系/开放世界外景的
        /// 地形起伏是数百到数千米量级，也就是说**近似在日出日落这段最关键的时间失效**。
        ///
        /// 把它算出来放到面板上，美术就能当场看到"我这个场景的高差远超包线"，
        /// 从而知道该开逐像素透射率（UE5 平行光上的 Per Pixel Atmosphere Transmittance）。
        /// 这比在文档里写一句"注意高差"有用得多。
        ///
        /// 返回值被 <paramref name="maxDeltaMeters"/> 截断；到顶说明"这个仰角下随便填"。
        /// </summary>
        /// <param name="muSun">dot(up, sunDir)。&lt;= 0（地平线下）时返回 0。</param>
        public static float SolveReferenceAltitudeEnvelopeMeters(
            VistaAtmosphereParameters p, float muSun,
            float referenceAltitudeMeters = 0f,
            float relThreshold = 0.01f, float absThreshold = 1e-3f,
            float maxDeltaMeters = k_EnvelopeSearchMaxMeters)
        {
            if (muSun <= k_MinMuSun)
                return 0f;

            Vector3 t0 = Evaluate(p, RadiusFromAltitudeMeters(p, referenceAltitudeMeters), muSun);

            // 连上界都看不出差别：包线比 maxDelta 还宽，报上界。
            if (!VisibleAt(p, t0, muSun, referenceAltitudeMeters + maxDeltaMeters,
                           relThreshold, absThreshold))
                return maxDeltaMeters;

            float lo = 0f, hi = maxDeltaMeters;
            for (int i = 0; i < 40; ++i)
            {
                float mid = 0.5f * (lo + hi);
                if (VisibleAt(p, t0, muSun, referenceAltitudeMeters + mid, relThreshold, absThreshold))
                    hi = mid;
                else
                    lo = mid;
            }
            return lo;
        }

        static bool VisibleAt(
            VistaAtmosphereParameters p, Vector3 t0, float muSun, float altitudeMeters,
            float relThreshold, float absThreshold)
        {
            Vector3 t = Evaluate(p, RadiusFromAltitudeMeters(p, altitudeMeters), muSun);
            return IsTransmittanceDifferenceVisible(t0, t, relThreshold, absThreshold);
        }
    }
}
