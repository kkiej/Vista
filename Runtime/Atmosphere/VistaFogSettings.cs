using System;
using UnityEngine;

namespace Vista
{
    /// <summary>
    /// 雾介质的配置。物理模型、平地近似的误差量级、为什么雾是与 Rayleigh/Mie/臭氧
    /// **并列的第四个介质组分**（而不是大气介质的一个参数），见
    /// <c>ShaderLibrary/FogMedium.hlsl</c> 的文件头。
    ///
    /// 本类只负责「把美术能理解的量换成 shader 收的 σ_t」这一层转换，
    /// 以及把三个 float4 打包出来。它不持有任何 GPU 资源，也不知道有几层雾体 ——
    /// 分层（近层 froxel / 远层 AP LUT）是 pass 的事。
    /// </summary>
    [Serializable]
    public class VistaFogSettings
    {
        /// <summary>雾用哪条路算。</summary>
        public enum Mode
        {
            /// <summary>不算雾。三个 float4 会被下发成零态，所以「关」是逐位等于没有雾。</summary>
            Off = 0,

            /// <summary>
            /// 并进现有 32³ AP LUT 的 march（档 D，移动端主线）。
            /// 无阴影查询、无新纹理、无历史 —— 成本是 AP LUT 每个采样点多几条指令。
            /// 代价：拿不到光柱（god rays），因为光柱完全来自逐 froxel 的级联阴影采样。
            /// </summary>
            AerialPerspective = 1,

            /// <summary>
            /// 近层 froxel 体 + AP LUT 分层（档 A，PC 主线）。
            ///
            /// 近层负责 [0, D]（D = froxel 体的接手距离，被 shadow distance 夹住），
            /// 逐 froxel 采级联阴影 ⇒ 光柱、局部灯（#23）、局部雾体（#24）都只在这一层里。
            /// AP LUT 负责其余的 (D, 32 km]，无阴影、无局部项。
            ///
            /// 两层怎么不重不漏：<c>FogMedium.hlsl</c> 里的 <c>VistaFogApWeight</c>
            /// 把 σ_fog 在两层之间**互补**地分掉（w 与 1−w），指数一相乘就拼回
            /// 完整的光学厚度。合成在 <c>VistaSampleAerialPerspective</c> 里，
            /// 消费者一行都不用改。
            ///
            /// 选了本档但 froxel 体这一帧不存在（feature 上的注入开关关着、
            /// 或体积分配失败）时会退化成 <see cref="AerialPerspective"/> ——
            /// 权重的零态让 AP 吃满雾，画面只是没有光柱，不会突然没有雾。
            /// </summary>
            Froxel = 2,
        }

        /// <summary>密度怎么给。两者都换成同一个 σ_t，只是口径不同。</summary>
        public enum DensityInput
        {
            /// <summary>
            /// 平均自由程 L（m）：σ_t[1/km] = 1000 / L。走 L 米后透射率降到 1/e。
            /// HDRP 的 Fog Attenuation Distance 就是这个量，所以口径有先例。
            /// </summary>
            MeanFreePath = 0,

            /// <summary>
            /// 气象能见度 V（m）：σ_t[1/km] = 3912 / V（Koschmieder，2% 对比阈）。
            /// 好处是可以直接对着「今天能见度 500 米」这种真实描述给数。
            /// </summary>
            Visibility = 1,
        }

        [Header("模式")]
        [Tooltip("雾用哪条路算。运行时可切 —— 走 uniform 而不是 shader keyword。\n"
               + "Off 会把雾的 cbuffer 下发成零态，逐位等于没有雾。")]
        public Mode mode = Mode.Off;

        [Header("密度")]
        [Tooltip("密度用哪个口径给。两者都换成同一个 σ_t（1/km），只是单位不同。\n"
               + "平均自由程：走这么多米后透射率降到 1/e（HDRP 的 Attenuation Distance）。\n"
               + "气象能见度：Koschmieder 2% 对比阈，能对着真实天气描述给数。\n"
               + "L = 100 m 等价于能见度 391 m —— 两个数都能直接在场景里量。")]
        public DensityInput densityInput = DensityInput.MeanFreePath;

        [Tooltip("雾层底部（h = 0 处）的平均自由程，米。仅 MeanFreePath 口径使用。")]
        [Min(1f)] public float meanFreePathMeters = 400f;

        [Tooltip("雾层底部（h = 0 处）的气象能见度，米。仅 Visibility 口径使用。")]
        [Min(1f)] public float visibilityMeters = 1500f;

        [Tooltip("逐通道的密度缩放。默认白色 —— 消光的波长相关性应当交给反照率与大气去表达，"
               + "而不是让 σ_t 自己偏色（后者会让『雾越厚颜色越偏』这种非线性行为很难反查）。\n"
               + "留着这个口是为了烟/沙尘这种确实有强吸收色的介质。")]
        [ColorUsage(false, false)] public Color densityTint = Color.white;

        [Header("高度剖面")]
        [Tooltip("雾层底部的世界 Y。低于它的高度按底部密度处理（不让 exp 继续涨）。")]
        public float bottomWorldY = 0f;

        [Tooltip("标高（米）：高度每升高这么多，密度降到 1/e。\n"
               + "地面雾典型 10~60 m；山谷云海 100~200 m；\n"
               + "调到很大等于均匀雾 —— 那时太阳自遮蔽项会把雾压黑，见下面那个开关的说明。")]
        [Min(0.1f)] public float scaleHeightMeters = 50f;

        [Header("散射")]
        [Tooltip("单次散射反照率 σ_s / σ_t（逐通道，0~1）。\n"
               + "真实的水滴雾吸收极弱，反照率接近 1（默认 0.98）。\n"
               + "调低 = 更多吸收，雾偏暗偏灰，用于烟、沙尘、脏雾。\n"
               + "shader 侧还会再 saturate 一次 —— σ_s > σ_t 这种不物理的组合"
               + "在数据上就不该可表示。")]
        [ColorUsage(false, false)] public Color albedo = new Color(0.98f, 0.98f, 0.98f, 1f);

        [Tooltip("Henyey-Greenstein 的各向异性 g。\n"
               + "0 = 各向同性；正值 = 前向散射（对着太阳看时雾发亮），水滴雾典型 0.6~0.9。\n"
               + "上下限是 ±0.99 而不是 ±1：g = ±1 时 HG 在正对/背对太阳处发散，"
               + "而雾关掉时 σ_s = 0，0 × inf = NaN，会让整帧的 AP 表变成黑洞。")]
        [Range(-0.99f, 0.99f)] public float anisotropy = 0.8f;

        [Tooltip("天光环境项的强度（0~1）。1 = 物理值（天光 SH 的平均入射亮度）。\n"
               + "这一项通常比太阳的侧散射还大：HG g=0.8 在 90° 处只有 0.0136 /sr，"
               + "太阳给约 1636 cd/m²/σ_s，而晴天平均天光约 5000 cd/m²。\n"
               + "所以调低它的后果是背光面与阴影里的雾发黑，别用它当『整体减淡雾』的旋钮。\n"
               + "已知近似：这一项不含遮挡 —— 洞穴里的雾拿到的天光和空地一样多。")]
        [Range(0f, 2f)] public float skyAmbientIntensity = 1f;

        [Header("太阳方向自遮蔽（可选）")]
        [Tooltip("雾自己挡住阳光的解析项（指数剖面沿直线的闭式光学深度）。\n"
               + "默认关：UE5 的 Volumetric Fog 与 HDRP 的 Volumetric Fog 都不做这一项，"
               + "所以『业内主流』这一栏的答案是不做。它是可选增强。\n"
               + "开了以后浓雾才有『雾顶亮、雾底暗』这个能读出厚度的层次。\n"
               + "模型是无限大平板，掠射太阳下会过暗，所以放大倍数必须有上限。")]
        public bool enableSunSelfShadow = false;

        [Tooltip("掠射太阳时 1/sin(仰角) 的放大上限。\n"
               + "无限平板模型在太阳贴地时给出无穷大的光学深度，会把日出日落的雾压黑 ——"
               + "而那恰好是雾最该好看的时刻。这是个观感参数，不是物理常数。")]
        [Range(1f, 50f)] public float grazingAmplifyMax = 8f;

        // --------------------------------------------------------------------
        //  打包
        //
        //  三个 float4 的语义与 FogMedium.hlsl 的 CBUFFER_START(VistaFogCB) 一一对应。
        //  Off 档在这里就被压成全零，而不是在 shader 里加分支：
        //  零态是 shader 侧刻意保住的性质（σ_t = 0 ⇒ 消光与散射都精确为 0），
        //  所以「忘了下发」与「明确关掉」殊途同归，都只能是没有雾。
        // --------------------------------------------------------------------

        /// <summary>true = 这一帧要算雾。</summary>
        public bool enabled => mode != Mode.Off && extinctionPerKm > 0f;

        /// <summary>雾层底部的消光系数 σ_t（1/km）。两种口径在这里合流。</summary>
        public float extinctionPerKm
        {
            get
            {
                switch (densityInput)
                {
                    case DensityInput.Visibility:
                        return 3912f / Mathf.Max(1f, visibilityMeters);
                    default:
                        return 1000f / Mathf.Max(1f, meanFreePathMeters);
                }
            }
        }

        /// <summary>
        /// 标高是否是一个有限正数。不是的话自遮蔽项必须关掉：
        /// H → ∞ 时闭式光学深度 τ = σ_t·(H/sy)·exp(-h/H) 无界，雾会变全黑。
        ///
        /// 注意这里**只**拦 NaN / Inf / 非正数，也就是「1/H 根本算不出来」的情况，
        /// 不拦「H 很大但有限」。后者（比如标高填了 50 km）确实会把雾压成全黑，
        /// 但那是个授权错误，让它以一眼可见的形态出现比静默关掉自遮蔽好查 ——
        /// 静默关掉的症状会退化成「浓雾比预期亮一点」，那是查不出来的。
        /// 见 <c>FogMedium.hlsl</c> 的「一个已知的坏配置」。
        /// </summary>
        public bool hasFiniteScaleHeight =>
            scaleHeightMeters > 0f
            && !float.IsNaN(scaleHeightMeters)
            && !float.IsInfinity(scaleHeightMeters);

        /// <summary>xyz: 单次散射反照率, w: HG 的 g。</summary>
        public Vector4 packedAlbedo => enabled
            ? new Vector4(
                Mathf.Clamp01(albedo.r), Mathf.Clamp01(albedo.g), Mathf.Clamp01(albedo.b),
                Mathf.Clamp(anisotropy, -0.99f, 0.99f))
            : Vector4.zero;

        /// <summary>xyz: 雾层底的 σ_t (1/km), w: 自遮蔽的掠射放大上限。</summary>
        public Vector4 packedExtinct
        {
            get
            {
                if (!enabled) return Vector4.zero;
                float sigma = extinctionPerKm;
                return new Vector4(
                    sigma * Mathf.Max(0f, densityTint.r),
                    sigma * Mathf.Max(0f, densityTint.g),
                    sigma * Mathf.Max(0f, densityTint.b),
                    Mathf.Max(1f, grazingAmplifyMax));
            }
        }

        /// <summary>
        /// x: 相机相对雾层底的高度 (m), y: 1/标高 (1/m), z: 天光强度, w: 自遮蔽开关。
        ///
        /// 高度必须由调用方用**精确的世界 Y** 算出来传进来，绝不能从大气空间的
        /// posKm 反算：fp32 在 6360 km 上的 ulp 是 0.49 m，对标高 20 m 的地面雾
        /// 会把整条密度剖面量化成 ~41 级台阶，症状是随相机高度跳动的水平条带。
        /// 见 <c>FogMedium.hlsl</c> 的「为什么高度不能从 posKm 算」。
        /// </summary>
        public Vector4 PackedHeight(float cameraWorldY)
        {
            if (!enabled) return Vector4.zero;

            bool finiteH = hasFiniteScaleHeight;
            return new Vector4(
                cameraWorldY - bottomWorldY,
                finiteH ? 1f / scaleHeightMeters : 0f,
                Mathf.Max(0f, skyAmbientIntensity),
                enableSunSelfShadow && finiteH ? 1f : 0f);
        }

        /// <summary>
        /// 交接带占接手距离 D 的比例。硬编码不暴露给美术 —— 见下面的理由。
        /// </summary>
        public const float k_HandoffBandFraction = 0.15f;

        /// <summary>
        /// 配置层面要不要近层 —— <see cref="UsesNearLayer"/> 里**与这一帧无关**的那一半。
        ///
        /// 分出来是给 pass 的分配闸门用的：闸门必须在解出 handoff **之前**就决定要不要
        /// 分配 froxel 体（handoff 是分配的产物，拿它当分配的前提是循环依赖），
        /// 而它又不能自己把 <c>enabled &amp;&amp; mode == Froxel</c> 再拼一遍 ——
        /// 那正是 <see cref="UsesNearLayer"/> 的头注在防的「两处各写一遍然后走歧」。
        ///
        /// 由此得到一条免费的保证：<c>UsesNearLayer ⇒ WantsNearLayer</c>。
        /// 闸门只会砍掉 <c>WantsNearLayer</c> 为假的帧，而那些帧
        /// <see cref="UsesNearLayer"/> 本来就是假的 ⇒ 分层与采样端不可能因此走歧。
        /// 反过来写（闸门比合成**松**）才会出事，那种形状在这里表达不出来。
        /// </summary>
        public bool WantsNearLayer => enabled && mode == Mode.Froxel;

        /// <summary>
        /// 这一帧到底有没有「近层」。<paramref name="handoffMeters"/> 是 pass 那边
        /// 解出来的接手距离（0 = froxel 体这一帧不存在）。
        ///
        /// 抽成一个谓词而不是让两处各写一遍同样的三个条件：它有**两个**消费者 ——
        /// <see cref="PackedLayering"/>（决定雾怎么在两层之间分）与 pass 里那个
        /// <c>_VistaFroxelComposite</c> 的开关位（决定采样端要不要去采近层的表）。
        /// 两者若走歧，得到的是最坏的一种失效：一边认为近层认领了 [0, D] 的雾，
        /// 另一边却不去采那张表 —— 那段雾**凭空消失**，而两边各自看都是自洽的。
        /// </summary>
        public bool UsesNearLayer(float handoffMeters)
            => WantsNearLayer && handoffMeters > 0f;

        /// <summary>
        /// x: 近层接手距离 D (km), y: 交接带宽度 (km), z: 1/交接带宽度 (1/km), w: 保留。
        ///
        /// <see cref="UsesNearLayer"/> 为 false 时返回全零 —— 而 <c>VistaFogApWeight</c>
        /// 在零态下恒返回 1，于是 AP 吃满雾、近层什么都不认领，
        /// **逐位等于本改动之前**。
        /// 这就是「失能态 = 零态」在这一层的具体含义，也是移动端那一档不受影响的保证。
        ///
        /// 为什么交接带宽度是硬编码的比例、不给美术一个旋钮：
        /// 它要控制的是「近层独有的那几项（阴影 / 局部灯 / 局部体）在 D 处别硬切」，
        /// 而 D 本身已经被 shadow distance 夹住了。再多一个旋钮，
        /// 美术能配出「带宽 > D」这种让权重在相机处就不是 1 的组合，
        /// 症状是近处的光柱莫名其妙变淡 —— 一个没人能反查的行为。
        /// 0.15 的来源：默认 D = 48 m ⇒ 带宽 7.2 m，
        /// 比 froxel 最后一片的厚度（48 × (1 − 1/1.083) = 3.7 m）宽约 2 片，
        /// 也就是「交接至少铺开两片」这个下界。切片数变了要重核这个数。
        /// </summary>
        public Vector4 PackedLayering(float handoffMeters)
        {
            if (!UsesNearLayer(handoffMeters))
                return Vector4.zero;

            float dKm    = handoffMeters * 0.001f;
            float bandKm = dKm * k_HandoffBandFraction;

            // bandKm 恒 > 0（handoffMeters > 0 且比例是正的常量），所以这里的
            // 除法不需要兜底。写成需要兜底的形态反而会把「D 可能是 0」这条
            // 已经在上面拦掉的路重新显得像是活的。
            return new Vector4(dKm, bandKm, 1f / bandKm, 0f);
        }

        public VistaFogSettings Clone() => (VistaFogSettings)MemberwiseClone();
    }
}
