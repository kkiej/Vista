using System;
using UnityEngine;

namespace Vista
{
    /// <summary>
    /// froxel 表的调试视图档位。
    ///
    /// ------------------------------------------------------------------ 数值与 shader 的耦合
    /// 这里的整数值被原样下发给 <c>Shaders/Volumetrics/VistaFroxelDebug.shader</c>，
    /// 与那里的 <c>VISTA_DBG_*</c> 宏必须逐个对应。C# 与 HLSL 之间没有共享枚举的办法，
    /// 所以这是一处**真实的两份定义**，不能用「同一个量只写一份」抹掉。
    ///
    /// 为什么不给它配一条判据：改错了的症状是「选积分 RGB 却显示了注入 RGB」，
    /// 一个整屏、立刻可见、且不可能被误读成正确的画面。本项目要判据的是
    /// **静默**失效（关键字漏设、半个纹素偏移、单位差 1000 倍）；
    /// 一个大声喊出来的错，判据买不到任何东西。
    ///
    /// <see cref="Off"/> 必须是 0：它是失能态，而失能态在本项目里恒等于零态
    /// （那一趟 pass 根本不被记录，不是「记录了但画占位内容」）。
    /// </summary>
    public enum FroxelDebugView
    {
        /// <summary>不记录调试 pass。默认值，也是出货值。</summary>
        Off = 0,
        /// <summary>积分表的 rgb（累积内散射，预曝光辐亮度 × gain），按场景深度采样。</summary>
        IntegralRgb = 1,
        /// <summary>积分表的 alpha（1 − 累积透射率，已在 [0,1]），按场景深度采样。</summary>
        IntegralAlpha = 2,
        /// <summary>注入表的 rgb（σ_s·J × gain），按场景深度采样。</summary>
        InjectionRgb = 3,
        /// <summary>积分表某一片的 rgb 铺满屏幕，点采样。切片下标见 debugSlice。</summary>
        SingleSlice = 4,
    }

    /// <summary>
    /// 近层体积雾 froxel 体的配置。
    ///
    /// 本类只做两件事：把「屏幕比例 + 远边界」换成一份**分配口径**
    /// （<see cref="VistaFroxelVolumeDesc"/>），以及把远边界按阴影距离夹紧并给出诊断串。
    /// 它不持有 GPU 资源，也不知道体积里装的是什么。
    ///
    /// 介质本身的参数（σ_t、标高、反照率、HG g）不在这里 —— 那些是
    /// <see cref="VistaFogSettings"/> 的，近层与 AP LUT 共用**同一份介质定义**。
    /// 分开的理由：换分辨率不该动介质，换介质不该重分配纹理。
    ///
    /// ------------------------------------------------------------------ 分层归属
    /// 近层负责 [0, handoff]，AP LUT 负责 (handoff, 32 km]，两者在 handoff 处对接 ——
    /// 也就是说 AP 的 <c>nearDistanceKm</c> 由本类推出来，而不是美术填的。
    /// 为什么不是「AP 关掉雾、远场另写一份解析式」：那会产生第二份远场雾实现，
    /// 而两份实现漂移的症状是「远景雾感不对」，会被误判成切片不够密。
    /// 为什么不是「从 AP 里把近段减掉」：那要除以一个在浓雾里趋近 0 的 T_near，
    /// 数值上是灾难。
    /// 推 near 的代价：#7 的 AP 档位扫描（Log vs Power、d=16/32/48/64）必须在
    /// <c>near = handoff</c> 下重跑，且 Log 优于 Power 的结论**可能翻转**
    /// （近场不再需要密切片了）。那次重跑本来就是 #7 留下的待办。
    /// </summary>
    [Serializable]
    public class VistaVolumetricFogSettings
    {
        [Header("分辨率")]
        [Tooltip("XY 相对屏幕的降采样倍数。8 = 1920×1080 下 240×135。\n"
               + "UE5 的 VolumetricFogGridPixelSize 默认 8，HDRP 的 V-Buffer 也是屏幕 /8。\n"
               + "为什么是「屏幕比例」而不是「固定 240×135」：后者在 2560×1440 下变成 1/10.7，"
               + "效果会随分辨率漂移 —— 同一套参数在两台机器上不是同一个画面。")]
        [Range(2, 16)] public int screenDivisor = 8;

        [Tooltip("深度切片数。64 = HDRP 的 Medium 档。\n"
               + "UE5 用 128，但它没有时间重投影；本项目在 #22 会加抖动 + 重投影，"
               + "所以 64 片配抖动的等效采样密度不低于 128 片不抖动。")]
        [Range(8, 256)] public int sliceCount = 64;

        [Header("深度范围")]
        [Tooltip("近层体的远边界（米）。HDRP 默认 64 m，UE5 默认 60 m —— 业内量级是几十米。\n"
               + "运行时会被相机的阴影距离夹住（超了会报错，不静默夹）。\n"
               + "别把它直接填成阴影距离：最后一级级联本身就低分辨率（光柱在那儿已经糊了），"
               + "而 64 片摊到 500 m 会让相机前方第一片从 0.3 m 长到 ~4 m，"
               + "近处会看到切片台阶。")]
        [Min(1f)] public float farDistanceMeters = 64f;

        [Header("开发中（#21）")]
        [Tooltip("逐 froxel 的光照注入 + 深度积分。\n\n"
               + "开了**最终画面仍然不变**，但两张表都已经在逐帧计算了：\n"
               + "注入表存 (σ_s·J, σ_t)（#20），积分表存 (累积内散射, 1 − T)（#21）。\n"
               + "把积分结果贴到画面上是 #25 的统一采样函数 —— 那一步还要处理"
               + "半透明物体吃雾、以及近层与 AP LUT 的接手，不属于这里。\n\n"
               + "所以下面的 Debug View 是现在唯一能看到这两张表的地方，"
               + "它也是关掉这个开关时唯一的症状来源：表全 0 ⇒ 积分 Alpha 档全黑。\n\n"
               + "为什么注入与积分要分两步做、而不是一趟算完：\n"
               + "注入表里的量是**局部**的（换个相机位置还是那个值），可以做时间重投影；"
               + "积分表是「从相机走到这里」的累积量，重投影它在物理上没有意义。"
               + "#22 的重投影因此必须插在两者之间 —— 这条两趟的划分是它的结构前提，"
               + "不是实现顺序上的偶然。\n\n"
               + "美术不需要动它：VistaFogSettings.Mode 里**没有** Froxel 档，"
               + "在 #25 的合成落地之前也不会有。")]
        public bool enableInjection = false;

        [Tooltip("把 froxel 表直接画到屏幕上（整屏替换，不叠加）。\n\n"
               + "Off 之外的档位都会**盖掉整个画面** —— 这是故意的：叠加会让"
               + "「表是空的」与「表很淡」在同一个像素上混起来，而这个视图存在的"
               + "全部目的就是消除那种混淆。\n\n"
               + "三个深度耦合档（积分 RGB / 积分 Alpha / 注入 RGB）在**像素自己的场景深度**"
               + "上采样，也就是 #25 的合成将来会吃的那一个操作数。\n"
               + "「单片」档改为直接把某一片铺满屏幕，回答的是另一个问题：表自身有没有"
               + "空洞或条带 —— 场景深度只覆盖 z-buffer 里存在的距离，天空方向上的切片"
               + "永远不会被任何像素采到。\n\n"
               + "越界不钳死：比远边界更远的像素画品红（归 AP LUT 管），"
               + "比近端更近的画青色。品红区占画面多大一块，就是近层实际覆盖了多少。")]
        public FroxelDebugView debugView = FroxelDebugView.Off;

        [Tooltip("「单片」档要看的切片下标。超过 N−1 会被夹到最后一片，"
               + "夹紧后的实际值由 Window/Vista/Log Volumetric Fog State 打印。")]
        [Range(0, 255)] public int debugSlice = 32;

        [Tooltip("调试视图的 RGB 增益。\n\n"
               + "表里存的是**预曝光**辐亮度，而这个视图刻意不套色调映射 ——"
               + "套了之后「表饱和了」与「tonemap 滚到顶了」在画面上无法区分。"
               + "代价就是暗部要靠这个旋钮手动抬：一个能读的数，比一条看不见的曲线好。\n\n"
               + "积分 Alpha 档不受它影响：那一路已经是归一化的 1 − T ∈ [0,1]，"
               + "乘上去会让「雾很厚」与「增益开大了」看起来一样。")]
        [Min(0f)] public float debugGain = 1f;

        /// <summary>
        /// 「单片」档的实际切片下标。夹到 [0, depth−1]。
        ///
        /// 抽成静态函数是为了让**渲染路径与状态日志共用同一份夹紧规则** ——
        /// 各写一份的症状是日志说「看的是第 63 片」而画面上是第 127 片，
        /// 而这个视图的全部价值就是「屏幕上这一片到底是哪一片」。
        /// </summary>
        public static int ResolveDebugSlice(int requested, int depth)
            => Mathf.Clamp(requested, 0, Mathf.Max(0, depth - 1));

        // --------------------------------------------------------------------
        //  切片分布：纯指数
        //
        //  约定（写在这里，因为写反的症状是「雾整体近了/远了半片」，只能靠判据抓）：
        //
        //    编码坐标 e = ln(d / near) / ln(far / near)，**e 直接就是 3D 纹理的 w 坐标**。
        //    切片 i 的纹素中心在 w = (i + 0.5) / N，于是它存的是
        //      「从相机到 d_i 的累积」，d_i = near · (far/near)^((i+0.5)/N)。
        //
        //  为什么这么定，而不是像 AP LUT 那样让两端精确（w_i = i/(N-1)）：
        //  这里的读端是**逐像素按深度采样**，采样坐标必须是 e(d) 本身。
        //  若切片存的是分段远平面 t(i+1)（HDRP 的做法），那么 e(t(i+1)) = (i+1)/N
        //  而纹素中心在 (i+0.5)/N，读的时候必须显式回退半个纹素 —— HDRP 那个
        //  已知的 half-slice bias 就是这么来的。把「存的距离」直接放在纹素中心上，
        //  读端就是 w = e(d)，一个偏移都不用记。
        //
        //  代价：体积的实际远端是 d_{N-1} = far · (far/near)^(-0.5/N)，**不是 far**。
        //  默认档（near 0.3 m / far 64 m / N 64）下 d_63 = 61.374 m，差 2.6 m。
        //  所以 AP 的接手点是 d_{N-1} 而不是 far —— 见 VistaFroxelVolumeDesc.handoffMeters。
        //  这个差值是判据②抓的东西：把 AP 的 near 填成 far 会在 61.4~64 m 之间留一段
        //  两层都算过的雾，症状是那个距离上一圈很淡的亮环。
        //
        //  分段 i 的介质求值点取两个存储距离的几何均值，闭式解正好是 e = i/N：
        //    sample(i) = near · (far/near)^(i/N)
        //  它落在分段的**度量**中点附近，偏差 = (√ρ − 1)/(ρ − 1) − 0.5，ρ 是相邻切片比。
        //  默认档 ρ = 1.0874 ⇒ 0.4895，即比中点早 0.0105 个分段 ——
        //  这条与项目既有的「采样点重心律」是同一件事，由判据④按恒等式校验。
        // --------------------------------------------------------------------

        /// <summary>切片数下界为 2：分布映射里有 1/N，且判据要能取到相邻两片。</summary>
        public int depth => Mathf.Clamp(sliceCount, 2, 256);

        /// <summary>
        /// 解析这一帧的分配口径。
        ///
        /// <paramref name="maxShadowDistance"/> 传 <c>UniversalCameraData.maxShadowDistance</c>
        /// （URP 里它是 <c>min(asset.shadowDistance, camera.farClipPlane)</c>），
        /// 不是 asset 上那个原始值 —— 远裁剪面也会把阴影距离砍掉，而近层体越过阴影范围
        /// 之后光柱会在一条硬边上消失。
        /// </summary>
        public VistaFroxelVolumeDesc Resolve(int screenWidth, int screenHeight,
                                             float cameraNearPlane, float maxShadowDistance,
                                             out string clampDiagnostic)
        {
            int div = Mathf.Clamp(screenDivisor, 2, 16);
            float far = ResolveFarDistance(farDistanceMeters, maxShadowDistance, out clampDiagnostic);

            return new VistaFroxelVolumeDesc(
                Mathf.Max(1, VistaComputeUtils.DivRoundUp(Mathf.Max(1, screenWidth), div)),
                Mathf.Max(1, VistaComputeUtils.DivRoundUp(Mathf.Max(1, screenHeight), div)),
                depth,
                // 近端取相机近裁剪面：比它更近的东西不会被画出来，为不可见的距离
                // 留切片等于白扔分辨率。下界 1 cm 是防「近裁剪面填了 0」——
                // 那时 ln(d/near) 会变成 -inf，整张体积变 NaN。
                Mathf.Max(0.01f, cameraNearPlane),
                far);
        }

        /// <summary>
        /// 远边界的夹紧规则。抽成 static 纯函数**只为一件事**：判据能直接调它，
        /// 不需要跑一帧真渲染 —— 而「D 被夹了却没人报错」正是要抓的失效。
        ///
        /// 硬约束：<c>D ≤ maxShadowDistance</c>。越过阴影距离就没有阴影贴图了，
        /// 那里的介质会被当成全亮，于是光柱在一个平面上**整齐地消失**。
        /// 那条硬边恰好坐在最浓的近雾外侧，是所有失效形态里最显眼的一种。
        ///
        /// 例外：<paramref name="maxShadowDistance"/> ≤ 0 表示这台相机**根本没有阴影**
        /// （URP 在阴影全关时把它置 0）。那时整个画面都没有光柱，也就没有「硬边」可言，
        /// 夹紧只会把体积压成 0，把「没有光柱」升级成「连雾都没有」。所以不夹。
        ///
        /// 静默夹紧是不可接受的：美术把范围调到 500 m、画面却没变，
        /// 这个问题在日志里查不到任何线索。
        /// </summary>
        /// <param name="clampDiagnostic">被夹紧时是人类可读的原因串；未夹紧时为 null。</param>
        public static float ResolveFarDistance(float requested, float maxShadowDistance,
                                              out string clampDiagnostic)
        {
            clampDiagnostic = null;
            float far = Mathf.Max(k_MinFarDistanceMeters, requested);

            if (maxShadowDistance <= 0f)
                return far;

            if (far <= maxShadowDistance)
                return far;

            clampDiagnostic =
                $"[Vista] 体积雾的远边界 {far:F1} m 超过了相机的阴影距离 {maxShadowDistance:F1} m，"
                + $"已夹到 {maxShadowDistance:F1} m。阴影距离之外没有阴影贴图，"
                + "那里的雾会被当成全亮，光柱会在一个平面上整齐消失。"
                + "要么把 URP Asset 的 Shadow Distance 调大，要么把远边界调小。";
            return maxShadowDistance;
        }

        /// <summary>
        /// 远边界的下界。1 m 不是「够用」，是「1/N 的指数比还有意义」的下界：
        /// far 掉到近裁剪面以下时 ln(far/near) 变负，切片顺序会整体翻转。
        /// </summary>
        public const float k_MinFarDistanceMeters = 1f;

        public VistaVolumetricFogSettings Clone()
            => (VistaVolumetricFogSettings)MemberwiseClone();
    }

    /// <summary>
    /// froxel 体的**分配口径 + 分布常量**。settings 是美术填的，本结构是解析后的结果 ——
    /// 分开的理由：分辨率依赖屏幕尺寸与相机，不是设置对象自己能算出来的，
    /// 而分配脏检查必须比较「解析后的东西」。
    ///
    /// 是 readonly struct 而不是 class：它每帧都要构造一次，且要能用 == 做脏检查。
    /// </summary>
    public readonly struct VistaFroxelVolumeDesc : IEquatable<VistaFroxelVolumeDesc>
    {
        public readonly int width;
        public readonly int height;
        public readonly int depth;
        /// <summary>体积近端（米）= 相机近裁剪面。</summary>
        public readonly float nearMeters;
        /// <summary>远边界参数（米），已夹紧。**不是**体积实际的远端，见 <see cref="handoffMeters"/>。</summary>
        public readonly float farMeters;

        public VistaFroxelVolumeDesc(int width, int height, int depth,
                                     float nearMeters, float farMeters)
        {
            this.width = width;
            this.height = height;
            this.depth = depth;
            this.nearMeters = nearMeters;
            // 保证 far > near：相等时 ln(far/near) = 0，编码坐标除零。
            this.farMeters = Mathf.Max(nearMeters * 1.001f, farMeters);
        }

        /// <summary>far / near。指数分布的总比值。</summary>
        public float ratio => farMeters / nearMeters;

        /// <summary>ln(far / near)。编码/解码两个方向都要它，所以打包下发而不是在 shader 里算。</summary>
        public float logRatio => Mathf.Log(ratio);

        /// <summary>相邻两个存储距离的比 ρ = (far/near)^(1/N)。判据④的输入。</summary>
        public float sliceRatio => Mathf.Exp(logRatio / depth);

        /// <summary>切片 i 存的累积距离（米）= near · (far/near)^((i+0.5)/N)。</summary>
        public float StoredDistance(int slice)
            => nearMeters * Mathf.Exp(logRatio * (slice + 0.5f) / depth);

        /// <summary>分段 i 的介质求值点（米）= near · (far/near)^(i/N)。i = 0 时退化成分段中点。</summary>
        public float SampleDistance(int slice)
            => slice == 0
                ? 0.5f * StoredDistance(0)
                : nearMeters * Mathf.Exp(logRatio * slice / depth);

        /// <summary>分段 i 的近端（米）。分段 0 从相机（0）开始。</summary>
        public float SegmentNear(int slice) => slice == 0 ? 0f : StoredDistance(slice - 1);

        /// <summary>分段 i 的远端（米）。</summary>
        public float SegmentFar(int slice) => StoredDistance(slice);

        /// <summary>
        /// 体积实际的远端（米）= 最后一片存的距离 = far · (far/near)^(-0.5/N)。
        /// **AP LUT 的 near 必须填这个数**，不是 <see cref="farMeters"/> ——
        /// 填后者会在这两个数之间留一段两层都算过的雾。
        /// </summary>
        public float handoffMeters => StoredDistance(depth - 1);

        /// <summary>x: near (m), y: far (m), z: ln(far/near), w: 1/ln(far/near)。</summary>
        public Vector4 packedRange => new Vector4(
            nearMeters, farMeters, logRatio, 1f / Mathf.Max(1e-6f, logRatio));

        /// <summary>xyz: 尺寸, w: 1/N。</summary>
        public Vector4 packedSize => new Vector4(width, height, depth, 1f / depth);

        /// <summary>
        /// 只比较影响 3D 纹理分配的三个尺寸。距离范围每帧推 cbuffer 即可生效 ——
        /// 相机走动时近裁剪面不变但阴影距离可能变，把距离并进来会让体积在
        /// 那一帧被整体重分配（三张 RGBA16F 3D 表，实打实的卡顿）。
        /// </summary>
        public bool Equals(VistaFroxelVolumeDesc other)
            => width == other.width && height == other.height && depth == other.depth;

        public override bool Equals(object obj)
            => obj is VistaFroxelVolumeDesc other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(width, height, depth);

        public static bool operator ==(VistaFroxelVolumeDesc a, VistaFroxelVolumeDesc b) => a.Equals(b);
        public static bool operator !=(VistaFroxelVolumeDesc a, VistaFroxelVolumeDesc b) => !a.Equals(b);

        public override string ToString()
            => $"{width}×{height}×{depth}, near {nearMeters:F2} m, far {farMeters:F1} m, "
             + $"handoff {handoffMeters:F3} m, ρ {sliceRatio:F6}";
    }
}
