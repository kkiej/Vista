using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista.Editor
{
    /// <summary>
    /// Aerial Perspective 全屏合成（变体 A）的**数值**验收。接线自检见
    /// <see cref="VistaApCompositeSelfTest"/>，两者刻意分开：那个不碰 GPU、秒回，
    /// 这个要渲三百来帧。
    ///
    /// ── 为什么自带布景，而不是量美术场景 ──
    ///
    /// 判据要的是「距离 → 透射率/散射」这条曲线，需要一条从几米到几十 km 的
    /// **连续**距离梯度。手上的 Demo 场景里 Terrain 是关闭的，剩下一个 10 m 的
    /// Plane 和一个 Capsule，最远depth只有几十米 —— 在那里「远山洗白」不是
    /// 没实现，是没有可观测对象。而且量美术场景意味着结论跟着场景漂：
    /// 今天通过、明天挪个物体就不通过，回归测试的价值归零。
    ///
    /// 所以这里自己搭：临时相机 + 四块正对相机的 URP Unlit 色板，
    /// 全部挂在一个**没人用的 layer** 上，用 cullingMask 与场景完全隔离。
    /// 不用 <see cref="VistaLightSeamSelfTest"/> 那两招隔离（近远裁剪面夹一个
    /// 0.09 单位的薄片 / 把探针挪到 y = 1e5）：AP 要的视锥是 5 m → 45 km，
    /// 薄片装不下；y = 1e5 会把探针送到 100 km 海拔，出了大气层顶。
    ///
    /// ── 布景形状：同一距离、四色并排、一次渲染 ──
    ///
    /// 合成公式是 <c>dst·T + S</c>，两个未知量。想解出来就得在**同一个像素条件下**
    /// 拿到至少两个不同的 dst。做法是把四块色板放在同一个**径向**距离 d 上、
    /// 横向并排占据四列，一次渲染同时读四个像素：
    ///
    ///   列 0 = 黑 → 解出 S        列 1 = 白 → 与列 0 一起解出 T
    ///   列 2 = 中灰 → 验仿射      列 3 = 黑（与列 0 同一个材质）→ **尺子**
    ///
    /// 列 3 是这套布景的自我标定：它与列 0 材质相同、距离相同，理论上应当
    /// 逐位相等，实测差值就是「横向串扰 + 帧内不确定性」的地板。
    /// 后面所有阈值都必须站在这个地板之上 —— 否则就是在拿尺子自己的偏置
    /// 去指控被测对象，这个项目已经犯过三次了。
    ///
    /// 视场压到 2°：四列的射线方向几乎相同，径向距离也几乎相同，
    /// 「四列条件一致」这个前提才成立。
    ///
    /// ── 为什么用 Unlit 而不是 Lit ──
    ///
    /// Unlit 的未合成基线是**精确常数**（就是 _BaseColor），所以基线扫一遍就能
    /// 反过来标定整条链路的量化精度；Lit 会带进光照、间接光、阴影，基线本身
    /// 就带梯度，没法当尺子。附带好处是它同时验证了变体 A 「覆盖任意材质」
    /// 这个卖点 —— Unlit 里没有任何一行 Vista 的代码。
    ///
    /// 代价是 URP 的 Unlit 也会调 <c>MixFog</c>，所以必须临时关掉
    /// <c>RenderSettings.fog</c>（在 finally 里还原）。
    ///
    /// ── 报警是验证过的，不是写上去的 ──
    ///
    /// 一条从没响过的报警等于没有报警。所以每条判据都注入过对应的故障，
    /// 确认它**响**、并且确认别的判据**不乱响**。实测（RTX 3060 / D3D11）：
    ///
    /// 故障 1：把两趟混合的顺序调换（先加后乘），公式变成 (dst+S)·T = dst·T + S·T。
    ///   判据 3（T 单调）通过 —— 正确，dst 的系数仍然是 T，透射率没受影响。
    ///   判据 4（S 单调）**报警** 4/95 个样本回退，最大 3.418e-3 @ 30.1 km。
    ///   判据 5（仿射）通过 —— 这不是漏报，是**本来就抓不到**：
    ///     dst·T + S·T 依旧是 dst 的仿射函数。这条盲区原先只是注释里的断言，
    ///     现在是量出来的。也正因为如此，判据 4 不能删。
    ///
    /// 故障 2：把 VISTA_AP_IS_SKY_DEPTH 的 clip 关掉。
    ///   判据 7 **报警**，整屏最大差 7.385e-2（蓝通道最明显：背景 0.214 变成
    ///   0.214·T + S）。
    ///
    /// 顺带被这次注入抓出来的**尺子自身的两个缺陷**（已修）：
    ///   a) 判据 2 的非空门槛原先写成 20×Weber = 0.2，把「有信号但被算错」
    ///      （S 从 0.2375 掉到 0.0756）误判成「没有信号」；现在就用 Weber 1%。
    ///   b) 判据 2 原先失败即 return false，正好把唯一能抓故障 1 的判据 4
    ///      挡在了后面。现在只打横幅不截断。
    ///
    /// ── 不覆盖的判据 ──
    ///
    /// 变体 A/B 逐像素一致：已经做了，但在**另一条自检**里 ——
    /// <c>VistaApVariantAgreementSelfTest</c>（菜单
    /// 「Validate AP Variant A-B Agreement」）。为什么不并进这里：那条判据需要
    /// 六次渲染（关 / B / A / A 的对照 / B 的距离出口 / A 的距离出口）与一套
    /// Lit 布景，而这条自检的全部前提是「Unlit 的未合成基线是精确常数」——
    /// 两者的布景要求互斥。
    ///
    /// 那条自检担保的是：不透明、无镜面高光、无环境反射、非透明材质的路径上，
    /// A 与 B 逐像素相差不超过 2 个 fp16 ulp。它**不**担保开了镜面/反射之后
    /// 仍然一致（材质上是关掉的），也**不**担保透明材质（两条路都故意不合成），
    /// 更不覆盖延迟（变体 B 在延迟里概念上不存在）。
    /// 另外它把「A 从深度反投影 positionWS」这一项的贡献报成
    /// **低于读出地板的上界**，不是报成零 —— 32 位浮点深度的预测量级
    /// （6.6e-4 m @ 34 km）本来就在那把尺子的分辨力以下。
    ///
    /// 性能：要 Play 模式的 ProfilerRecorder 路径，见
    /// 「Cross-Check LUT Timing (Play Mode)」那条；不在这里混着做。
    /// </summary>
    public static class VistaApCompositeAcceptance
    {
        // ── 布景常量 ──

        const int k_Size = 64;                                  // 方形 RT 边长
        static readonly int[] k_ColumnX = { 8, 24, 40, 56 };     // 四列的采样像素 x
        const int k_RowY = 32;
        const float k_Fov = 2f;                                  // 垂直视场（度）
        const float k_NearClip = 0.1f;
        const float k_FarClip = 45000f;                          // > maxDistanceKm，为了量「钉在最后一片」
        const float k_CameraAltitudeM = 200f;                    // 相机相对星球表面的高度
        const float k_QuadWidthFrac = 0.7f;                      // 色板宽度占该列宽度的比例

        // ── 扫描常量 ──

        const float k_NearKm = 0.005f;                           // 5 m：在 nearDistanceKm = 20 m 的淡出区里面
        const float k_FarKm = 40f;                               // 超过默认 maxDistanceKm = 32
        const int k_Samples = 96;

        /// <summary>
        /// 中灰色板的**线性**目标值。只要求与 0、1 都拉开距离；
        /// 具体数值不进任何判定（见 Build 里关于 Color.gamma 的说明）。
        /// </summary>
        const float k_GreyLevel = 0.18f;
        const int k_FoldDepth = 256;                             // 对折测试用的切片数

        // ── 阈值 ──

        /// <summary>Weber 1%：全项目通用的可见性门槛。</summary>
        const float k_RelTol = 0.01f;

        /// <summary>同通道绝对可见性豁免：小于参考白的 0.1% 就算看不见。</summary>
        const float k_AbsTol = 1e-3f;

        /// <summary>
        /// fp16 相对精度 2^-11。两个 fp16 量相减要算两次。
        /// 值本身放在 <see cref="VistaSelfTestNumerics"/> 里：它是一条跨自检共享的
        /// 数值事实，各写一份的症状是「某条判据的门限比另一条松」，最难发现。
        /// </summary>
        const float k_Fp16Rel = VistaSelfTestNumerics.k_Fp16Rel;

        /// <summary>一次渲染读出的四列 RGB。</summary>
        struct Row
        {
            public Vector3 black0, white, grey, black3;
        }

        /// <summary>从 Off 基线 + Fullscreen 结果里解出来的一个距离点。</summary>
        struct Solved
        {
            public float distanceKm;
            public Vector3 t;          // 透射率
            public Vector3 s;          // 已曝光的散射项
            public Vector3 greyErr;    // |实测灰 − 仿射预测灰|
            public Vector3 rulerOn;    // |列3 − 列0|（合成后）
            public Vector3 rulerOff;   // |列3 − 列0|（基线）
        }

        [MenuItem("Window/Vista/Validate AP Composite (Numeric)", priority = 127)]
        public static void Run()
        {
            var sb = new StringBuilder();
            bool ok;
            try
            {
                ok = Validate(sb);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log(("[Vista] AP 合成数值验收" + (ok ? "通过" : "**失败**") + "\n" + sb)
                      .Replace("\r", "").Replace("\n", "  |  "));
        }

        static bool Validate(StringBuilder sb)
        {
            // ── 前置条件。任何一条不满足都直接停，不做「尽量测一点」的降级：
            //    半套布景量出来的数字比没数字更危险。
            var feature = VistaAtmosphereFeature.current;
            if (feature == null)
            {
                sb.AppendLine("**失败**：VistaAtmosphereFeature.current 为 null —— "
                            + "feature 没装进当前 Renderer，或还没有相机渲过一帧。");
                return false;
            }

            var unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlitShader == null)
            {
                sb.AppendLine("**失败**：找不到 Universal Render Pipeline/Unlit。当前管线不是 URP？");
                return false;
            }

            int layer = FindUnusedLayer();
            if (layer < 0)
            {
                sb.AppendLine("**失败**：32 个 layer 全都有物体在用，布景无法与场景隔离。"
                            + "腾出任意一个空 layer 再跑。");
                return false;
            }

            var ap = feature.aerialPerspective;

            // 保存要改的一切。这三样都是**全局/资产上的**状态，必须在 finally 里还原。
            // 注意 ap 是 feature 上那个活对象（VistaAtmosphereFeature.aerialPerspective
            // 是直接暴露字段的 getter），改它等于改 RendererData 资产上的序列化字段 ——
            // 这里不调 SetDirty，所以正常情况下不会落盘，但仍然必须还原：
            // 万一测试中途有别的东西把资产标脏，改动就跟着存进去了。
            var prevMode = ap.compositeMode;
            var prevRes = ap.resolution;
            bool prevFog = RenderSettings.fog;

            RenderTexture rt = null;
            Texture2D readback = null;
            GameObject root = null;
            Material[] mats = null;

            try
            {
                RenderSettings.fog = false;   // URP 的 Unlit 也会调 MixFog

                rt = new RenderTexture(k_Size, k_Size, 24,
                                       RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                readback = new Texture2D(k_Size, k_Size, TextureFormat.RGBAFloat, false, true);

                Build(unlitShader, layer, feature.groundLevelWorldY, rt,
                      out root, out Camera cam, out Transform[] quads, out mats);

                sb.Append("── 布景　layer = ").Append(layer)
                  .Append("　RT = ").Append(k_Size).Append('×').Append(k_Size).Append(" ARGBHalf")
                  .Append("　FOV = ").Append(k_Fov).Append("°")
                  .Append("　相机海拔 = ").Append(k_CameraAltitudeM).Append(" m")
                  .AppendLine();
                sb.Append("　 AP 配置　maxDistanceKm = ").Append(ap.maxDistanceKm.ToString("F1"))
                  .Append("　nearDistanceKm = ").Append(ap.nearDistanceKm.ToString("F3"))
                  .Append("　distribution = ").Append(ap.distribution)
                  .Append("　coloredTransmittance = ").Append(ap.coloredTransmittance)
                  .AppendLine();

                bool ok = true;

                // ── 判据 0：基线是干净的
                //
                // 先在 compositeMode = Off 下扫一遍。Unlit 的输出应当精确等于 _BaseColor，
                // 所以这一遍同时干三件事：
                //   a) 证明布景里没有别的东西在改颜色（雾、后处理、色调映射、曝光）；
                //   b) 量出整条链路的**实际**量化精度 —— 不是假设 fp16，而是测出来。
                //      URP 的中间 HDR 纹理可能是 R11G11B10（6 位尾数，相对精度 1.5e-2），
                //      那会让后面所有精细比较失效，必须先知道；
                //   c) 给后面的代数提供分母（白列基线、黑列基线）。
                ap.compositeMode = VistaAerialPerspectiveSettings.CompositeMode.Off;
                Warmup(cam);
                var baseline = Sweep(cam, rt, readback, quads, "基线 (Off)");

                // 量化精度只拿白列与黑列去标定。
                //
                // 中灰列**不能**参与：写进材质的 0.18 会被 Unity 做一次 gamma → linear
                // （见 Build 里的说明），渲出来是 0.0272，拿它去比 0.18 会得到
                // 0.85 的「误差」—— 那是尺子自己的偏置，不是链路精度。
                // 第一版就是这么写的，结果把地板抬到 1.698，比被判的量还大，
                // 判据 3~6 全部变成空判。这个项目已经在这类错误上摔过三次：
                // **绝不把尺子自己的偏置当成被测对象的缺陷**。
                //
                // 白 = 1、黑 = 0 是 sRGB 转换的两个不动点，所以它们与色彩空间设置无关，
                // 偏离多少就真的是链路精度。
                float measuredRel = 0f;      // 实测相对量化精度
                float baselineDrift = 0f;    // 基线随距离的漂移（应当为 0）
                var b0 = baseline[0];
                for (int i = 0; i < baseline.Length; i++)
                {
                    var r = baseline[i];
                    measuredRel = Mathf.Max(measuredRel, MaxComp(Abs(r.white - Vector3.one)));
                    measuredRel = Mathf.Max(measuredRel, MaxComp(Abs(r.black0)));
                    baselineDrift = Mathf.Max(baselineDrift, MaxComp(Abs(r.white - b0.white)));
                    baselineDrift = Mathf.Max(baselineDrift, MaxComp(Abs(r.grey - b0.grey)));
                    baselineDrift = Mathf.Max(baselineDrift, MaxComp(Abs(r.black0 - b0.black0)));
                }

                sb.AppendLine("── 判据 0：Off 基线是常数（布景自身干净）");
                sb.Append("　 白 = ").Append(Fmt(b0.white))
                  .Append("　中灰 = ").Append(Fmt(b0.grey))
                  .Append("　黑 = ").Append(Fmt(b0.black0)).AppendLine();
                sb.Append("　 随距离最大漂移 = ").Append(baselineDrift.ToString("E3"))
                  .Append("　实测相对量化精度 = ").Append(measuredRel.ToString("E3"))
                  .Append("（fp16 理论 ").Append(k_Fp16Rel.ToString("E3")).Append("）")
                  .AppendLine();

                // 中灰列只有一个职责：提供一个与 0 和 1 都拉开距离的第三个 dst，
                // 好让判据 5 的仿射检查有分辨力。它的**具体数值不参与任何判定**
                // （Solve 用的是实测基线），所以这里不比对目标值，只确认它够远。
                float greyBase = MaxComp(b0.grey);
                bool greyUsable = greyBase > 0.02f && greyBase < 0.98f;
                if (!greyUsable)
                {
                    ok = false;
                    sb.Append("　 **失败**：中灰基线 ").Append(greyBase.ToString("F4"))
                      .AppendLine(" 离 0 或 1 太近，判据 5 的仿射检查会失去分辨力。");
                }

                if (baselineDrift > k_AbsTol)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：Off 基线随距离变化。说明除 AP 之外还有东西在按深度改颜色 —— "
                                + "先查 RenderSettings.fog 有没有被别处重新打开、"
                                + "URP asset 上的 Volume 是否绕过了 volumeLayerMask、"
                                + "有没有第二个 RendererFeature 在做深度雾。此时后面所有判据都不成立。");
                    return false;
                }

                if (measuredRel > 8f * k_Fp16Rel)
                {
                    // 不当失败：这是链路精度事实，不是 AP 的缺陷。但要把话说清楚，
                    // 因为它会把后面所有阈值抬高，可能掩盖真实偏差。
                    sb.AppendLine("　 注意：实测精度远差于 fp16。最可能的原因是 URP asset 的 HDR "
                                + "精度设成了 32 Bit（R11G11B10，6 位尾数）。后面的阈值会按实测值放宽，"
                                + "「通过」的说服力相应下降 —— 想要严格结论就把 HDR 精度改成 64 Bit 再跑。");
                }
                else if (greyUsable)
                {
                    sb.AppendLine("　 基线常数 OK");
                }

                // 精度地板：解 T 要做一次减法（两个量化量），所以乘 2。
                float precisionFloor = 2f * Mathf.Max(measuredRel, k_Fp16Rel);

                // ── 采集：Fullscreen 下再扫一遍，解出 T 与 S
                ap.compositeMode = VistaAerialPerspectiveSettings.CompositeMode.Fullscreen;
                Warmup(cam);
                var composited = Sweep(cam, rt, readback, quads, "合成 (Fullscreen)");

                var solved = new Solved[k_Samples];
                for (int i = 0; i < k_Samples; i++)
                    solved[i] = Solve(DistanceKm(i), baseline[i], composited[i]);

                // ── 判据 1：尺子。所有阈值的地板。
                float rulerOn = 0f, rulerOff = 0f;
                for (int i = 0; i < k_Samples; i++)
                {
                    rulerOn = Mathf.Max(rulerOn, MaxComp(solved[i].rulerOn));
                    rulerOff = Mathf.Max(rulerOff, MaxComp(solved[i].rulerOff));
                }
                float floor = Mathf.Max(Mathf.Max(rulerOn, rulerOff), precisionFloor);

                sb.AppendLine("── 判据 1：尺子（列3 与列0 同材质同距离，差值即串扰地板）");
                sb.Append("　 基线态最大差 = ").Append(rulerOff.ToString("E3"))
                  .Append("　合成态最大差 = ").Append(rulerOn.ToString("E3"))
                  .AppendLine();
                sb.Append("　 后续判据地板 = max(串扰, 2×量化) = ").Append(floor.ToString("E3"))
                  .AppendLine();

                if (rulerOff > k_AbsTol)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：基线态两个黑列就已经不一致，说明色板横向溢出到了邻列 "
                                + "（k_QuadWidthFrac 太大）或视场内还有别的东西。布景本身不可信，停。");
                    return false;
                }
                sb.AppendLine("　 尺子可用 OK");

                // ── 判据 2：非空。曲线得真有变化，后面的单调性才不是空判。
                var near = solved[0];
                var far = solved[k_Samples - 1];
                float maxExtinction = 0f, maxScatter = 0f;
                for (int i = 0; i < k_Samples; i++)
                {
                    maxExtinction = Mathf.Max(maxExtinction, MaxComp(Vector3.one - solved[i].t));
                    maxScatter = Mathf.Max(maxScatter, MaxComp(solved[i].s));
                }

                sb.AppendLine("── 判据 2：信号非空");
                sb.Append("　 近端 ").Append(near.distanceKm.ToString("F4")).Append(" km：T = ")
                  .Append(Fmt(near.t)).Append("　S = ").Append(Fmt(near.s)).AppendLine();
                sb.Append("　 远端 ").Append(far.distanceKm.ToString("F2")).Append(" km：T = ")
                  .Append(Fmt(far.t)).Append("　S = ").Append(Fmt(far.s)).AppendLine();
                sb.Append("　 最大消光 1−T = ").Append(maxExtinction.ToString("F4"))
                  .Append("　最大散射 S = ").Append(maxScatter.ToString("F4")).AppendLine();

                bool nearClean = MaxComp(Vector3.one - near.t) < 0.02f && MaxComp(near.s) < 0.02f;
                if (!nearClean)
                {
                    ok = false;
                    sb.Append("　 **失败**：5 m 处就已经有可见的雾。nearDistanceKm = ")
                      .Append(ap.nearDistanceKm.ToString("F3"))
                      .AppendLine(" km 的近端淡出没生效 —— 查 packedFlags.y (1/nearKm) 与"
                                + " AerialPerspective.hlsl 里的淡出项。");
                }
                // 非空的门槛就用全项目那把尺子：Weber 1% of 参考白。不另外发明倍数。
                //
                // 第一版这里写的是 20×k_RelTol = 0.2，纯拍脑袋。一次注入故障验证
                // （把两趟混合的顺序调换，于是 S 被多乘一次 T，远端从 0.2375 掉到 0.0756）
                // 直接暴露了两个错：门槛把「有信号但被算错」误判成「没有信号」，
                // 而紧随其后的 return false 又把真正该报警的判据 4 挡在了外面 ——
                // 那条判据存在的**唯一理由**就是抓这个故障。
                bool vacuous = maxExtinction < k_RelTol || maxScatter < k_RelTol;
                if (vacuous)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：整条曲线上 AP 的信号都在 Weber 1% 以下，"
                                + "后面的单调性/仿射判据没有分辨力。"
                                + "最可能是太阳在地平线以下、或大气参数被改成了近乎真空。"
                                + "本布景自带一盏 25° 仰角的平行光，若这条仍不通过，"
                                + "问题在大气参数或 AP 表本身。");
                    // 刻意不 return：后面的判据照样跑完。
                    // 信号太弱会让它们的**通过**没有意义，但它们的**失败**仍然是真信息 ——
                    // 而那恰恰是这种时候最需要的东西。所以只打横幅，不截断。
                    sb.AppendLine("　 ⚠ 以下判据仍会跑完，但因本条未过，其中的「OK」不可采信；"
                                + "只看其中的失败项。");
                }
                else if (nearClean)
                {
                    sb.AppendLine("　 近端干净、远端有量 OK");
                }

                // ── 判据 3：T 单调不增且落在 [0,1]
                //
                // 越远只会衰减得越多，物理上没有反弹的余地。
                int tRegress = 0, tRange = 0;
                float tWorstRegress = 0f, tWorstRange = 0f;
                int tWorstAt = -1;
                for (int i = 0; i < k_Samples; i++)
                {
                    var t = solved[i].t;
                    float over = Mathf.Max(MaxComp(t - Vector3.one), MaxComp(-t));
                    if (over > floor) { tRange++; if (over > tWorstRange) tWorstRange = over; }

                    if (i == 0) continue;
                    float back = MaxComp(solved[i].t - solved[i - 1].t);   // 变大即回弹
                    if (back > floor)
                    {
                        tRegress++;
                        if (back > tWorstRegress) { tWorstRegress = back; tWorstAt = i; }
                    }
                }

                sb.AppendLine("── 判据 3：T 单调不增、且 ∈ [0,1]");
                sb.Append("　 越界样本 ").Append(tRange).Append(" / ").Append(k_Samples)
                  .Append("（最大越界 ").Append(tWorstRange.ToString("E3")).Append("）").AppendLine();
                sb.Append("　 回弹样本 ").Append(tRegress).Append(" / ").Append(k_Samples - 1)
                  .Append("（最大回弹 ").Append(tWorstRegress.ToString("E3"));
                if (tWorstAt >= 0)
                    sb.Append(" @ ").Append(solved[tWorstAt].distanceKm.ToString("F3")).Append(" km");
                sb.Append("）").AppendLine();
                if (tRange > 0 || tRegress > 0)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：T 不单调或越界。这是切片采样坐标错位的典型症状 —— "
                                + "查 VistaSampleAerialPerspective 的 w 映射与 packedParams 是否同源。");
                }
                else
                {
                    sb.AppendLine("　 T 单调 OK");
                }

                // ── 判据 4：S 单调不减
                //
                // 这一条是专门抓「两趟混合顺序被调换」的。调换后的公式是 dst·T + S·T：
                // 它**仍然是 dst 的仿射函数**，所以判据 5 的仿射检查抓不到；
                // 但 S·T 在远处会随 T 一起掉下去，于是这里会看到大段回退。
                int sRegress = 0;
                float sWorstRegress = 0f;
                int sWorstAt = -1;
                for (int i = 1; i < k_Samples; i++)
                {
                    float back = MaxComp(solved[i - 1].s - solved[i].s);
                    if (back > floor)
                    {
                        sRegress++;
                        if (back > sWorstRegress) { sWorstRegress = back; sWorstAt = i; }
                    }
                }

                sb.AppendLine("── 判据 4：S 单调不减（抓两趟混合顺序调换）");
                sb.Append("　 回退样本 ").Append(sRegress).Append(" / ").Append(k_Samples - 1)
                  .Append("（最大回退 ").Append(sWorstRegress.ToString("E3"));
                if (sWorstAt >= 0)
                    sb.Append(" @ ").Append(solved[sWorstAt].distanceKm.ToString("F3")).Append(" km");
                sb.Append("）").AppendLine();
                if (sRegress > 0)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：S 在远处回落。首先怀疑 shader 里两个 Pass 的声明顺序 —— "
                                + "先加后乘得到 (dst+S)·T，解出来的 S 会带一个 T 因子。"
                                + "接线自检的判据 2 会同时报错，两处一起看。");
                }
                else
                {
                    sb.AppendLine("　 S 单调 OK");
                }

                // ── 判据 5：合成对 dst 是仿射的
                //
                // 用黑、白两列解出 T 与 S，再去预测 0.18 灰那一列。
                // 这一条抓的是「公式里混进了非线性项」（比如误把 S 也乘了 dst、
                // 或者透射率被 saturate 到别的地方去了）。
                float greyWorst = 0f;
                int greyWorstAt = -1, greyFail = 0;
                for (int i = 0; i < k_Samples; i++)
                {
                    float e = MaxComp(solved[i].greyErr);
                    if (e > greyWorst) { greyWorst = e; greyWorstAt = i; }
                    // 灰列的量级约 0.18·T + S，用它算相对量
                    float mag = Mathf.Max(MaxComp(composited[i].grey), k_AbsTol);
                    if (e > Mathf.Max(k_AbsTol, floor) && e / mag > k_RelTol) greyFail++;
                }

                sb.AppendLine("── 判据 5：合成对底色是仿射的（黑白解 T/S → 预测中灰列）");
                sb.Append("　 最大预测误差 = ").Append(greyWorst.ToString("E3"));
                if (greyWorstAt >= 0)
                    sb.Append(" @ ").Append(solved[greyWorstAt].distanceKm.ToString("F3")).Append(" km");
                sb.Append("　超阈值样本 ").Append(greyFail).Append(" / ").Append(k_Samples).AppendLine();
                if (greyFail > 0)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：合成不是 dst·T + S 的形状。"
                                + "注意这一条抓不到两趟顺序调换（那个仍然仿射），要看判据 4。");
                }
                else
                {
                    sb.AppendLine("　 仿射 OK");
                }

                // ── 判据 6：采样坐标与切片数解耦（对折测试）
                //
                // 把切片数从 32 抬到 256 再解一遍。判的**不是**「32 片够不够」——
                // 那是 #7 在 LUT 上量过的事；判的是采样端的 w 映射与产出端的
                // 切片分布是否同一个公式。若两边错开，加密切片不会让曲线收敛，
                // 而是让它整体平移。
                sb.AppendLine("── 判据 6：切片数对折（32 → 256），判采样坐标是否与分布同源");
                var foldRes = prevRes;
                foldRes.z = k_FoldDepth;
                ap.resolution = foldRes;
                Warmup(cam);
                var foldedRaw = Sweep(cam, rt, readback, quads, "对折 (z=256)");
                ap.resolution = prevRes;

                float foldWorstRel = 0f, foldWorstAbs = 0f;
                int foldWorstAt = -1, foldFail = 0;
                for (int i = 0; i < k_Samples; i++)
                {
                    var f = Solve(DistanceKm(i), baseline[i], foldedRaw[i]);
                    float dT = MaxComp(Abs(f.t - solved[i].t));
                    float dS = MaxComp(Abs(f.s - solved[i].s));
                    float a = Mathf.Max(dT, dS);
                    float mag = Mathf.Max(Mathf.Max(MaxComp(f.t), MaxComp(f.s)), k_AbsTol);
                    float rel = a / mag;
                    if (a > foldWorstAbs) { foldWorstAbs = a; foldWorstRel = rel; foldWorstAt = i; }
                    // 相对**与**绝对同时越界才算失败：远端 S 本身很小，
                    // 只看相对量会把看不见的差异判成缺陷。
                    if (rel > k_RelTol && a > Mathf.Max(k_AbsTol, floor)) foldFail++;
                }

                sb.Append("　 最大偏差 = ").Append(foldWorstAbs.ToString("E3"))
                  .Append("（相对 ").Append((foldWorstRel * 100f).ToString("F2")).Append("%");
                if (foldWorstAt >= 0)
                    sb.Append(" @ ").Append(DistanceKm(foldWorstAt).ToString("F3")).Append(" km");
                sb.Append("）　超阈值样本 ").Append(foldFail).Append(" / ").Append(k_Samples).AppendLine();
                if (foldFail > 0)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：加密切片改变了曲线。采样端与产出端的距离映射不同源 —— "
                                + "两处都必须只从 _VistaApParams / _VistaApSize 推导，不能各写一份常量。");
                }
                else
                {
                    sb.AppendLine("　 采样坐标与切片数解耦 OK");
                }

                // ── 判据 7：天空像素一位不动
                //
                // 把色板全部关掉，整屏都是背景色（深度为清屏值）。合成 shader 里
                // 那个 clip 应当让这些像素完全不被触碰。
                //
                // 先用 Off 对 Off 验尺子：两帧必须逐位相同，否则「相同」这个判据
                // 本身没有分辨力（这就是所谓的假通过）。
                sb.AppendLine("── 判据 7：天空像素不被合成触碰（整屏逐位比较）");
                foreach (var q in quads) q.gameObject.SetActive(false);

                ap.compositeMode = VistaAerialPerspectiveSettings.CompositeMode.Off;
                Warmup(cam);
                var skyOffA = (Color[])RenderAndRead(cam, rt, readback).Clone();
                var skyOffB = (Color[])RenderAndRead(cam, rt, readback).Clone();
                float skyRuler = MaxDiff(skyOffA, skyOffB);

                ap.compositeMode = VistaAerialPerspectiveSettings.CompositeMode.Fullscreen;
                Warmup(cam);
                var skyOn = RenderAndRead(cam, rt, readback);
                float skyDiff = MaxDiff(skyOffA, skyOn);

                sb.Append("　 背景色实测 = ").Append(Fmt(new Vector3(skyOffA[0].r, skyOffA[0].g, skyOffA[0].b)))
                  .AppendLine();
                sb.Append("　 尺子 Off↔Off 最大差 = ").Append(skyRuler.ToString("E3"))
                  .Append("　被测 Off↔Fullscreen 最大差 = ").Append(skyDiff.ToString("E3")).AppendLine();

                if (skyRuler != 0f)
                {
                    ok = false;
                    sb.AppendLine("　 **失败**：同一配置渲两帧结果就不同，「逐位相同」这个判据没有分辨力。"
                                + "先查是否有随帧变化的抖动（TAA jitter / 蓝噪声偏移）漏进来了。");
                }
                else if (skyDiff != 0f)
                {
                    ok = false;
                    // 反例的量级不需要另做一个变体去测：它就是扫描远端那个 S，
                    // 因为 45 km 的清屏深度超过 maxDistanceKm，会被钉在最后一片。
                    sb.Append("　 **失败**：天空像素被改动了。合成 shader 里的 "
                            + "VISTA_AP_IS_SKY_DEPTH / clip 没生效。"
                            + "若 clip 完全去掉，天空会被叠上远端的 S ≈ ")
                      .Append(Fmt(far.s)).AppendLine("，那是肉眼可见的一层灰雾。");
                }
                else
                {
                    sb.Append("　 逐位相同 OK（反例量级：去掉 clip 会叠上 S ≈ ")
                      .Append(Fmt(far.s)).AppendLine("）");
                }

                // ── 曲线摘要。不是判据，是给人看的：数字要能自己讲清楚「远山洗白」。
                sb.AppendLine("── 曲线摘要（每 12 个样本取一个）");
                for (int i = 0; i < k_Samples; i += 12)
                {
                    var v = solved[i];
                    sb.Append("　 ").Append(v.distanceKm.ToString("F4").PadLeft(9)).Append(" km　T = ")
                      .Append(Fmt(v.t)).Append("　S = ").Append(Fmt(v.s)).AppendLine();
                }
                var last = solved[k_Samples - 1];
                sb.Append("　 ").Append(last.distanceKm.ToString("F4").PadLeft(9)).Append(" km　T = ")
                  .Append(Fmt(last.t)).Append("　S = ").Append(Fmt(last.s))
                  .AppendLine("　← 超过 maxDistanceKm，钉在最后一片");

                return ok;
            }
            finally
            {
                ap.compositeMode = prevMode;
                ap.resolution = prevRes;
                RenderSettings.fog = prevFog;

                if (root != null) Object.DestroyImmediate(root);
                if (mats != null)
                    foreach (var m in mats)
                        if (m != null) Object.DestroyImmediate(m);
                if (readback != null) Object.DestroyImmediate(readback);
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 布景
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 找一个没有任何 Renderer / Light 在用的 layer。从 31 往下找：
        /// 高位 layer 通常是用户自定义区，比内置的 0~7 更可能空着。
        /// 连非激活对象也算「在用」—— 保守一点，代价只是少一个候选。
        /// </summary>
        static int FindUnusedLayer()
        {
            int used = 0;
            foreach (var r in Object.FindObjectsByType<Renderer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                used |= 1 << r.gameObject.layer;
            foreach (var l in Object.FindObjectsByType<Light>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                used |= 1 << l.gameObject.layer;

            for (int i = 31; i >= 0; i--)
                if ((used & (1 << i)) == 0) return i;
            return -1;
        }

        static void Build(Shader unlitShader, int layer, float groundLevelWorldY, RenderTexture rt,
                          out GameObject root, out Camera cam, out Transform[] quads, out Material[] mats)
        {
            root = new GameObject("Vista AP Acceptance Probe") { hideFlags = HideFlags.HideAndDontSave };
            root.transform.position = new Vector3(0f, groundLevelWorldY + k_CameraAltitudeM, 0f);

            // ── 相机
            var camGo = new GameObject("Camera") { hideFlags = HideFlags.HideAndDontSave };
            camGo.transform.SetParent(root.transform, false);
            camGo.layer = layer;
            cam = camGo.AddComponent<Camera>();

            cam.enabled = false;                  // 不进正常渲染循环，只手动 Render()
            cam.cullingMask = 1 << layer;         // 与场景完全隔离
            cam.orthographic = false;
            cam.fieldOfView = k_Fov;
            cam.nearClipPlane = k_NearClip;
            cam.farClipPlane = k_FarClip;

            // 纯色清屏而不是天空盒：判据 7 要的是一个**确定**的背景值。
            // 用天空盒的话背景值取决于场景的 skybox 材质，结论就跟着场景漂。
            // 背景取 0.5 灰而不是黑：黑的话「天空被叠上 S」与「天空没被碰」
            // 在数值上仍然可分（0·T + S = S ≠ 0），但拿一个非零值去比更能
            // 顺带证明乘性那一趟也没碰到它。
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            cam.allowHDR = true;                  // 否则中间 RT 可能退成 8-bit sRGB
            cam.allowMSAA = false;
            cam.targetTexture = rt;
            cam.aspect = 1f;                      // 必须在 targetTexture 之后设

            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = false;
            camData.volumeLayerMask = 0;          // 场景里的 Tonemapping 不能进来
            camData.antialiasing = AntialiasingMode.None;
            camData.renderShadows = false;

            // 水平朝 +Z。不抬仰角是刻意的：贴着地平线的视线路径最长、AP 信号最强。
            // 也不会钻到地下 —— 星球是弯的，40 km 上的球面下沉 40²/(2·6371) km ≈ 126 m，
            // 小于相机的 200 m 海拔。
            camGo.transform.localRotation = Quaternion.identity;

            // ── 自带太阳
            //
            // AP 的散射量取决于太阳方向。若靠场景那盏灯，夜晚场景里 S ≈ 0，
            // 判据 2 会失败 —— 而那不是 AP 的错。所以布景自带一盏，
            // 挂在探针 layer 上：场景那盏灯不在 cullingMask 里，会被剔掉，
            // 于是 URP 的主光就是这一盏，VistaAtmospherePass.GetSunDirection 直接拿到它。
            // 这样也完全不必去动全局的 RenderSettings.sun。
            var lightGo = new GameObject("Sun") { hideFlags = HideFlags.HideAndDontSave };
            lightGo.transform.SetParent(root.transform, false);
            lightGo.layer = layer;
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;
            light.intensity = 1f;                 // 只用方向；强度不参与大气计算
            // 25° 仰角、偏离视线 150°：既不是正对太阳（Mie 峰值会让曲线过于陡）
            // 也不是背对（散射太弱），是一个「侧逆光远景」的典型条件。
            lightGo.transform.localRotation = Quaternion.Euler(25f, 150f, 0f);

            // ── 四块色板
            mats = new Material[3];
            //
            // ── 关于这里为什么要 .gamma ──
            //
            // 我原先以为 SetVector 会原样写入、只有 SetColor 才做色彩空间转换。
            // 实测不是：_BaseColor 在 shader 里声明为 Color 属性，往它写值
            // （无论 SetColor 还是 SetVector）在线性色彩空间下都会被做一次
            // gamma → linear。写 0.18 渲出来是 0.0272 = sRGBToLinear(0.18)。
            // 白和黑是这个变换的两个不动点，所以只有中灰暴露了它 ——
            // 这正是「用三个不同底色而不是两个」意外换来的好处。
            //
            // 所以这里预先做逆变换（Color.gamma），让渲出来的线性值落回 0.18。
            // 若工程是 Gamma 色彩空间，引擎不做转换，渲出来就是 0.46 ——
            // 也完全能用：中灰列的**具体数值不参与任何判定**，
            // Solve 用的一律是实测基线。这里追求 0.18 只是为了让报告好读。
            for (int i = 0; i < 3; i++)
            {
                float v = i == 0 ? 0f : i == 1 ? 1f : k_GreyLevel;
                var encoded = new Color(v, v, v, 1f).gamma;
                mats[i] = new Material(unlitShader) { hideFlags = HideFlags.HideAndDontSave };
                mats[i].SetVector("_BaseColor", new Vector4(encoded.r, encoded.g, encoded.b, 1f));
                mats[i].SetFloat("_Cull", 0f);    // 双面：省掉「朝向对不对」这个变量
            }

            // 列 3 复用列 0 的材质 —— 这是尺子成立的前提：
            // 两列除了屏幕位置之外没有任何差异。
            var order = new[] { 0, 1, 2, 0 };
            quads = new Transform[k_ColumnX.Length];
            for (int c = 0; c < quads.Length; c++)
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "Patch " + c;
                quad.hideFlags = HideFlags.HideAndDontSave;
                quad.layer = layer;
                quad.transform.SetParent(root.transform, false);

                var col = quad.GetComponent<Collider>();
                if (col != null) Object.DestroyImmediate(col);

                var mr = quad.GetComponent<MeshRenderer>();
                mr.sharedMaterial = mats[order[c]];
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                // 不接光照探针/反射探针：Unlit 用不到，但显式关掉能少一类干扰。
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

                quads[c] = quad.transform;
            }
        }

        /// <summary>
        /// 把四块色板摆到同一个**径向**距离 <paramref name="dMeters"/> 上，各占一列。
        /// </summary>
        static void PlaceQuads(Camera cam, Transform[] quads, float dMeters)
        {
            Vector3 eye = cam.transform.position;

            // 视锥在径向距离 d 处的半高。列宽 = 全宽 / 列数（aspect = 1）。
            float halfH = dMeters * Mathf.Tan(k_Fov * 0.5f * Mathf.Deg2Rad);
            float colW = 2f * halfH / k_ColumnX.Length;

            for (int c = 0; c < quads.Length; c++)
            {
                float u = (k_ColumnX[c] + 0.5f) / k_Size;
                float v = (k_RowY + 0.5f) / k_Size;

                // 不用 ViewportPointToRay：它的 origin 在近裁剪面上，
                // 那样 origin + dir·d 得到的是「距近裁剪面 d」，在 d = 5 m 时
                // 会有 2% 的系统误差。这里直接从相机位置量。
                Vector3 dir = (cam.ViewportToWorldPoint(new Vector3(u, v, 1f)) - eye).normalized;

                var t = quads[c];
                t.position = eye + dir * dMeters;
                t.rotation = cam.transform.rotation;      // 正对相机：面上每点的径向距离几乎相同
                // 宽度只占该列的 70%，绝不越界到邻列 —— 尺子（列3 vs 列0）会验证这一点。
                // 高度给足，横向扫描线一定落在板子上。
                t.localScale = new Vector3(colW * k_QuadWidthFrac, halfH * 2.4f, 1f);
            }
        }

        // ────────────────────────────────────────────────────────────────
        // 采集
        // ────────────────────────────────────────────────────────────────

        static float DistanceKm(int i) =>
            k_NearKm * Mathf.Pow(k_FarKm / k_NearKm, i / (float)(k_Samples - 1));

        /// <summary>
        /// 改了 compositeMode / resolution 之后先空转两帧。
        /// 3D 表的重分配发生在下一次 AddRenderPasses，第一帧可能还在用旧表。
        /// </summary>
        static void Warmup(Camera cam)
        {
            cam.Render();
            cam.Render();
        }

        static Color[] RenderAndRead(Camera cam, RenderTexture rt, Texture2D readback)
        {
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
            readback.Apply(false);
            RenderTexture.active = prev;
            return readback.GetPixels();
        }

        static Row[] Sweep(Camera cam, RenderTexture rt, Texture2D readback,
                           Transform[] quads, string label)
        {
            var rows = new Row[k_Samples];
            for (int i = 0; i < k_Samples; i++)
            {
                if ((i & 7) == 0)
                    EditorUtility.DisplayProgressBar("Vista AP 数值验收", label,
                                                     i / (float)k_Samples);

                PlaceQuads(cam, quads, DistanceKm(i) * 1000f);
                var px = RenderAndRead(cam, rt, readback);

                rows[i].black0 = Pick(px, 0);
                rows[i].white = Pick(px, 1);
                rows[i].grey = Pick(px, 2);
                rows[i].black3 = Pick(px, 3);
            }
            return rows;
        }

        static Vector3 Pick(Color[] px, int column)
        {
            var c = px[k_RowY * k_Size + k_ColumnX[column]];
            return new Vector3(c.r, c.g, c.b);
        }

        /// <summary>
        /// 由基线与合成结果解出 T 与 S。
        ///
        /// 合成是 <c>out = in·T + S</c>。黑白两列给出两个方程：
        ///   black_on = black_off·T + S
        ///   white_on = white_off·T + S
        /// 于是 T = (white_on − black_on) / (white_off − black_off)，S = black_on − black_off·T。
        ///
        /// 用**实测**的基线做分母，而不是直接假设白 = 1、黑 = 0：
        /// 一旦链路上有别的增益（曝光、色调映射漏进来），假设值会把那个增益
        /// 悄悄记到 T 的账上，而实测值不会。
        /// </summary>
        static Solved Solve(float distanceKm, Row off, Row on)
        {
            Vector3 den = off.white - off.black0;
            Vector3 t = new Vector3(
                (on.white.x - on.black0.x) / den.x,
                (on.white.y - on.black0.y) / den.y,
                (on.white.z - on.black0.z) / den.z);
            Vector3 s = on.black0 - Mul(off.black0, t);

            Vector3 greyPred = Mul(off.grey, t) + s;

            return new Solved
            {
                distanceKm = distanceKm,
                t = t,
                s = s,
                greyErr = Abs(on.grey - greyPred),
                rulerOn = Abs(on.black3 - on.black0),
                rulerOff = Abs(off.black3 - off.black0),
            };
        }

        // ────────────────────────────────────────────────────────────────
        // 小工具
        // ────────────────────────────────────────────────────────────────

        static Vector3 Abs(Vector3 v) =>
            new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        static Vector3 Mul(Vector3 a, Vector3 b) =>
            new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);

        static float MaxComp(Vector3 v) => Mathf.Max(v.x, Mathf.Max(v.y, v.z));

        static float MaxDiff(Color[] a, Color[] b)
        {
            float m = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                m = Mathf.Max(m, Mathf.Abs(a[i].r - b[i].r));
                m = Mathf.Max(m, Mathf.Abs(a[i].g - b[i].g));
                m = Mathf.Max(m, Mathf.Abs(a[i].b - b[i].b));
            }
            return m;
        }

        static string Fmt(Vector3 v) =>
            "(" + v.x.ToString("F4") + ", " + v.y.ToString("F4") + ", " + v.z.ToString("F4") + ")";
    }
}
