using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista.Editor
{
    /// <summary>
    /// #12 逐像素太阳透射率的验收。
    ///
    /// ── 被测的是哪一句 ──
    ///
    /// <c>VistaLighting.hlsl</c> 里那一句 <c>mainLight.color *= VistaSunTransmittanceRatio(posWS)</c>。
    /// 它要成立的三件事，各自需要不同的判据：
    ///
    ///   1. **乘对了位置**：只乘主光、恰好乘一次、不漏进 GI/附加光。
    ///      判据①（乘法恒等式）+ 判据④（GI 隔离）。
    ///   2. **比值本身对**：GPU LUT 采样与 CPU 闭式一致。判据②。
    ///   3. **判据不是空的**：比值真的在画面上变化、且变化量远高于尺子的地板。判据③。
    ///
    /// 另外三条守卫的是「平时永不执行的分支」：判据⑤（语义闸门 w = 0）、
    /// 判据⑥（两条数值闸门 <c>VISTA_T_REF_FLOOR</c> / <c>VISTA_T_RATIO_MAX</c>）、
    /// 判据⑦（单调性 —— 它是 CPU 像素映射的独立交叉核对，见那里的说明）。
    ///
    /// ── 布景为什么是「正交相机 + 一面 8 km 高的墙」 ──
    ///
    /// 这条特性的全部内容就是「海拔不同 ⇒ 太阳色不同」，所以布景**必须**有大高差，
    /// 否则整条验收在一个比值恒等于常数的画面上跑，判据②会通过、判据③会揭穿它是空的。
    /// 用正交相机是为了让「屏幕行号 ↔ 世界海拔」成为一条精确的线性关系：
    /// 透视相机下同一行像素的着色点落在一个斜面上，CPU 要复现就得先解一次射线-平面求交，
    /// 那等于给期望值引入第二处可能出错的几何代码。正交下只有一条乘加。
    ///
    /// ── 这台夹具**不**驱动灯色，这是有意的 ──
    ///
    /// 比值 = T(px) / T_ref，与 <c>Light.color</c> 里装的是什么无关；T_ref 由
    /// <see cref="VistaTimeOfDay.s_DebugTRefOverride"/> 直接给定，CPU 期望用的是同一个值。
    /// 于是灯保持白光、强度 1 —— 少一个变量，判据①才是一条纯代数恒等式。
    /// 代价：本自检**不覆盖** <c>VistaTimeOfDay</c> 把 T 写进灯的那一段
    /// （那是 #8 的接缝自检 <c>Validate Light Seam</c> 的职责），也不覆盖
    /// 「T_ref 与灯里的因子是否为同一个 float」这件事 —— 那一条在结构上无法用
    /// 渲染读回来证明，只能靠 <c>VistaAtmospherePass</c> 里那一行「不重算、直接读
    /// <c>ResolveSunTransmittanceRef</c>」的代码本身担保。
    ///
    /// ── 未覆盖，明写在这里 ──
    ///
    ///   · <c>muSun &lt; 0</c> 时的 <c>VistaEarthShadow</c> 分支：布景刻意让全屏
    ///     <c>muSun &gt; 0.1</c>（判据②会把实测最小值报出来）。
    ///     为什么不覆盖：CPU 侧要判它就得再写一份射线-球求交，那是「同一个量的第二份实现」，
    ///     本项目不允许 —— 与其加一份会漂移的期望值，不如把这条分支明确宣布为未覆盖。
    ///   · 附加光（点光/聚光）：按设计它们不参与逐像素透射率，这里连布景都没有。
    ///   · 延迟/GBuffer 路径：Vista/Lit 只有前向。
    ///   · 判据⑥里「下限接管」与「上限接管」无法互相分离，见那里的说明。
    /// </summary>
    static class VistaSunTransmittanceRatioSelfTest
    {
        // ──────────────────────────────────────────────────────────── 布景

        const int k_Size = 128;

        /// <summary>正交半高 (m)。墙面因此跨 8 km 海拔，一行 = 62.5 m。</summary>
        const float k_OrthoSizeM = 4000f;

        /// <summary>墙面所在的 Z (m)。非零是为了让 <c>up</c> 不是纯 +Y —— 比值的
        /// 参数化是三维的，把布景摆在 z = 0 会让「只按海拔算」这种错误实现照样通过。</summary>
        const float k_WallZ = 1000f;

        const float k_NearClip = 0.1f;
        const float k_FarClip = 5000f;

        /// <summary>墙的边长 (m)。视口只有 8 km，给足富余；到底盖住没有由哨兵回答。</summary>
        const float k_WallSizeM = 9000f;

        /// <summary>底色。取得亮一些是为了把主光贡献 M 抬高 —— 判据①的容差里有
        /// 若干条与读回地板同量级的加性项，M 越大信噪比越好。</summary>
        const float k_BaseLevel = 0.8f;

        /// <summary>太阳方位角。取 (−90°, 90°) 内的值，使 <c>dot(墙面法线, sunDir) &gt; 0</c>
        /// （墙面法线在 <see cref="MakeWall"/> 里被旋到朝向相机，即 −Z）。
        /// 背光的话 M = 0，判据①会变成 0 == 0 的空判 —— 判据③会当场揭穿。</summary>
        const float k_SunAzimuthDeg = 20f;

        // ──────────────────────────────────────────────────────────── 判定阈值

        /// <summary>判据②的相对门：全项目统一的 Weber 1%。</summary>
        const float k_RelTol = 0.01f;

        /// <summary>判据②的相对判据分母下限，即绝对可见度豁免。</summary>
        const float k_AbsExempt = 1e-3f;

        /// <summary>
        /// 哨兵门。**故意与姊妹自检 <c>VistaLitAccumulationSelfTest</c> 的 1e4 不同。**
        ///
        /// 那边取 1e4 是因为它的被测量是 relError × 100，1e4 相当于 100 倍偏差，
        /// 已经远超任何真实值。这里不行：6 档输出的是比值本身，而比值被
        /// <c>VISTA_T_RATIO_MAX</c> 钳在 **恰好 1e4** —— 而哨兵判定写成
        /// <c>!(v &lt; gate)</c>（为了让 NaN 落在哨兵一侧），于是一个被钳住的合法读数
        /// 会被判成「这个像素没参与比对」。那正是本项目记过的反面模式
        /// 「哨兵值与放大后的被测量撞车」，而且方向最坏：判据⑥要验的就是钳位，
        /// 结果它自己被当成未覆盖跳过。
        ///
        /// 取 2e4：严格夹在钳位上界 1e4 与 shader 哨兵 30000 之间。
        /// </summary>
        const float k_SentinelGate = 2e4f;

        /// <summary>shader 侧 <c>VISTA_DIFF_NOT_COMPARED</c>，也用作清屏色。</summary>
        const float k_Sentinel = 30000f;

        /// <summary>shader 侧 <c>VISTA_T_RATIO_MAX</c> 的镜像。</summary>
        const float k_RatioMax = 1e4f;

        /// <summary>shader 侧 <c>VISTA_T_REF_FLOOR</c> 的镜像。</summary>
        const float k_TRefFloor = 1e-6f;

        /// <summary>判据③要求「信号 / 容差」至少到这个倍数（至少一档太阳达到）。</summary>
        const float k_MinSignalToTol = 5f;

        /// <summary>判据③要求比值在画面上至少张开这么多（绝对量）。1% 判据的 5 倍。</summary>
        const float k_MinRatioSpread = 0.05f;

        /// <summary>判据②要求全屏 <c>muSun</c> 不低于此值，即地球阴影分支确实没被踩到。</summary>
        const float k_MinMuSunMargin = 0.01f;

        static float ReadbackFloor => VistaSelfTestNumerics.k_ReadbackFloor;
        static float Fp16Rel => VistaSelfTestNumerics.k_Fp16Rel;

        // ──────────────────────────────────────────────────────────── 档位/注入

        const float k_CodeColor = 1f;    // _VistaDiffCtrl.x = 1 -> 输出我的着色结果
        const float k_CodeRatio = 6f;    // _VistaDiffCtrl.x = 6 -> 输出比值本身

        /// <summary>把 <c>mainLightColor</c> 精确清零：shader 里乘的是 <c>1 + inject.x</c>。</summary>
        const float k_KillMainLight = -1f;

        static readonly Vector4 k_TRefOff = new Vector4(1f, 1f, 1f, 0f);

        static readonly int s_InjectId = Shader.PropertyToID("_VistaDiffInject");
        static readonly int s_CtrlId = Shader.PropertyToID("_VistaDiffCtrl");
        static readonly int s_SunDirId = Shader.PropertyToID("_VistaSunDirection");
        static readonly int s_TRefId = Shader.PropertyToID("_VistaSunTransmittanceRef");

        struct SunConfig
        {
            public string label;
            public float elevationDeg;
        }

        /// <summary>
        /// 三档太阳高度。为什么要三档而不是一档：比值的量级、以及它在画面上张开多少，
        /// 完全由太阳高度决定 —— 天顶时各海拔的 T 差别很小（判据③的信噪比最差），
        /// 贴地时 LUT 在近地平线那一段的双线性误差最大（判据②最容易破）。
        /// 一档跑通证明不了另一档，所以三档各自报，不合并成一个整屏最大值。
        /// </summary>
        static readonly SunConfig[] k_Suns =
        {
            new SunConfig { label = "高 60°", elevationDeg = 60f },
            new SunConfig { label = "中 20°", elevationDeg = 20f },
            new SunConfig { label = "低 6°",  elevationDeg = 6f  },
        };

        // ════════════════════════════════════════════════════════════ 入口

        [MenuItem("Window/Vista/Validate Per-Pixel Sun Transmittance", priority = 131)]
        public static void Run()
        {
            var sb = new StringBuilder();
            bool ok;

            var prevTRef = VistaTimeOfDay.s_DebugTRefOverride;
            try
            {
                ok = Validate(sb);
            }
            finally
            {
                // 三样都要还原：覆写钩子会一直骗住整个渲染管线，
                // 两个调试 uniform 留着会让**场景里**的 Vista/Lit 材质
                // （若有人手开了 DIFF_DEBUG 关键字）输出调试载荷。
                VistaTimeOfDay.s_DebugTRefOverride = prevTRef;
                Shader.SetGlobalVector(s_InjectId, Vector4.zero);
                Shader.SetGlobalVector(s_CtrlId, Vector4.zero);
            }

            Debug.Log(("[Vista] 逐像素太阳透射率自检" + (ok ? "通过" : "**失败**") + "\n" + sb)
                      .Replace("\r", "").Replace("\n", "  |  "));
        }

        static bool Validate(StringBuilder sb)
        {
            // ── 前提。每一条都是硬失败：这些条件不满足时后面所有数字都没有意义，
            //    而「先跑出一堆看起来正常的数、最后才发现前提不成立」是最费时间的一种失败。
            var feature = VistaAtmosphereFeature.current;
            if (feature == null)
            {
                sb.AppendLine("**失败**：VistaAtmosphereFeature.current 为 null。"
                            + "比值要采 _VistaTransmittanceLut、还要读逐视图 CB 里的 _VistaSunDirection，"
                            + "没有 feature 时两者都不存在 —— 读到的会是全 0，"
                            + "而 0/T_ref 是一个「看起来像失败」的合法数，归因会绕远路。");
                return false;
            }

            var urp = UniversalRenderPipeline.asset;
            if (urp == null)
            {
                sb.AppendLine("**失败**：当前不是 URP（UniversalRenderPipeline.asset 为 null）。");
                return false;
            }
            if (urp.mainLightRenderingMode != LightRenderingMode.PerPixel)
            {
                sb.Append("**失败**：URP 资产的 Main Light = ")
                  .Append(urp.mainLightRenderingMode)
                  .AppendLine("。此时 _MainLightColor 恒为黑，mainLightColor 整项是 0，"
                            + "比值乘在零上，判据①退化成 0 == 0 的空判。");
                return false;
            }

            var litShader = Shader.Find("Vista/Lit");
            if (litShader == null)
            {
                sb.AppendLine("**失败**：找不到 Vista/Lit。");
                return false;
            }
            if (ShaderUtil.ShaderHasError(litShader))
            {
                sb.Append("**失败**：Vista/Lit 有编译错误（")
                  .Append(ShaderUtil.GetShaderMessageCount(litShader))
                  .AppendLine(" 条消息）。此时它渲不出东西，全屏都是哨兵。");
                return false;
            }

            int layer = FindUnusedLayer();
            if (layer < 0)
            {
                sb.AppendLine("**失败**：32 个 layer 全都有物体在用，布景无法与场景隔离。");
                return false;
            }

            var p = feature.parameters;
            float groundY = feature.groundLevelWorldY;
            var ap = feature.aerialPerspective;

            // ── 两处要临时改、finally 还原的全局/资产状态。
            //
            // ① AP 合成模式必须关掉。
            //    DIFF_DEBUG 分支输出的是**调试载荷**而不是颜色，它只绕开了变体 B
            //    （那条路径根本不调 VistaApplyApTail），可变体 A 是一个独立的全屏 pass，
            //    它不认识调试载荷，照样把 payload 当颜色乘透射率加散射。
            //    第一次跑 #12 就栽在这里：归因档解出一条逐通道的仿射
            //    payload·T + L（绿 T = 0.99147 / L = 0.00223，蓝 T = 0.9798 / L = 0.0065，
            //    蓝更重 —— 正是大气透视的指纹），于是「w = 0 时比值恒 1」这条判据
            //    读到 0.0913 的偏差。更糟的是本夹具用正交相机，而 AP 的距离重建是按
            //    透视视锥四角插值的，contamination 因此**逐像素不同**，
            //    看起来就像「比值随位置乱跳」。
            //    取 Off 而不是 InShader：两者在 DIFF_DEBUG 下都给出干净的载荷，
            //    但 Off 连 AP 的两次 dispatch 都省了，少一个活动部件。
            //
            // ② RenderSettings.sun 必须指向本夹具那盏灯。
            //    原先这里写着「场景灯不在 cullingMask 里会被剔掉，所以不必动全局」——
            //    那句话是错的，代价是三条判据一起失真。URP 的 GetMainLightIndex 第一条
            //    规则是「visibleLights 里等于 RenderSettings.sun 的那盏直接返回」，
            //    而平行光并不因为 layer 不在 cullingMask 里就从 visibleLights 消失。
            //    于是主光是场景那盏：Vista 发布的 _VistaSunDirection 是它的方向
            //    （判据②因此拿一个错的 muSun 去对账，误差 13%~584%，与 LUT 精度无关），
            //    而它相对本夹具那面墙几乎是掠射（NdotL ≈ 0.04），主光贡献 M 只有 ~0.025，
            //    低于除法测量的分母下限 —— 判据①的除法形式因此一个像素都没量到。
            //    指定 RenderSettings.sun 之后两条路径（主光解析与 Vista 的兜底）
            //    同时指向探针灯，不必再赌哪一条生效。
            var prevMode = ap.compositeMode;
            var prevSun = RenderSettings.sun;

            RenderTexture rt = null;
            Texture2D readback = null;
            GameObject root = null;
            Material mat = null;

            try
            {
                ap.compositeMode = VistaAerialPerspectiveSettings.CompositeMode.Off;

                rt = new RenderTexture(k_Size, k_Size, 24,
                    RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                readback = new Texture2D(k_Size, k_Size, TextureFormat.RGBAFloat, false, true);

                Build(litShader, layer, groundY, rt,
                      out root, out Camera cam, out Transform sunT, out mat);

                RenderSettings.sun = sunT.GetComponent<Light>();

                float camY = groundY + k_OrthoSizeM;
                float altLo = PixelToWorld(0, 0, camY).y - groundY;
                float altHi = PixelToWorld(k_Size - 1, k_Size - 1, camY).y - groundY;

                sb.Append("── 布景　layer = ").Append(layer)
                  .Append("　RT = ").Append(k_Size).Append('×').Append(k_Size)
                  .Append(" ARGBHalf　正交半高 = ").Append(k_OrthoSizeM).Append(" m")
                  .Append("　墙面 z = ").Append(k_WallZ).Append(" m")
                  .AppendLine();
                sb.Append("　 海拔覆盖 ").Append(altLo.ToString("F1")).Append(" m → ")
                  .Append(altHi.ToString("F1")).Append(" m（每行 ")
                  .Append((2f * k_OrthoSizeM / k_Size).ToString("F2")).Append(" m）")
                  .Append("　groundLevelWorldY = ").Append(groundY.ToString("F1")).Append(" m")
                  .Append("　bottomRadius = ").Append(p.bottomRadius.ToString("F1")).Append(" km")
                  .AppendLine();
                sb.AppendLine("　 灯：白光、强度 1，**不**驱动物理光色 —— 比值与灯色无关，"
                            + "少一个变量（理由见类注释）。阴影/附加光/探针全关。");
                sb.Append("　 临时状态：AP compositeMode ").Append(prevMode)
                  .Append(" → Off（调试载荷不能被合成，理由见代码注释）")
                  .Append("　RenderSettings.sun ")
                  .Append(prevSun == null ? "(null)" : prevSun.name)
                  .Append(" → 探针灯（否则 URP 主光是场景那盏）")
                  .AppendLine();

                bool ok = true;

                // ── 归因先行。判据⑤/② 的读数一旦不对，第一个要回答的问题是
                //    「着色器本次渲染看到的 uniform 是什么」，而不是「我设的是什么」。
                //    把这三档打在最前面，后面每一条失败都能立刻对照。
                ReportShaderObserved(sb, cam, rt, readback, sunT);

                // ── 判据⑤：它是「语义闸门」，也顺便证明覆写钩子确实能改到 GPU。
                //    放在最前面是因为它一旦不成立（比如钩子没生效），
                //    后面每一档的 T_ref 都不是我以为的那个值，全部数字作废。
                ok &= JudgeDisabled(sb, cam, rt, readback, sunT);

                // ── 三档太阳
                bool anyStrongSignal = false;
                for (int i = 0; i < k_Suns.Length; i++)
                {
                    ok &= JudgeSun(sb, k_Suns[i], cam, rt, readback, sunT, p, groundY,
                                   i == 0, ref anyStrongSignal);
                }

                if (!anyStrongSignal)
                {
                    ok = false;
                    sb.Append("── 判据③ **失败**：没有任何一档太阳的信号达到容差的 ")
                      .Append(k_MinSignalToTol).AppendLine(" 倍。"
                            + "判据①在这种情形下是「两个都在噪声里的量相等」，不构成证据。");
                }

                // ── 判据⑥ 钳位覆盖（只在中间那档太阳上跑一次）
                ok &= JudgeClamps(sb, cam, rt, readback, sunT, p, groundY, k_Suns[1]);

                return ok;
            }
            finally
            {
                // 先还原全局状态、再销毁布景：RenderSettings.sun 指着即将被销毁的那盏灯，
                // 反过来的话中间会有一小段「sun 指向已销毁对象」的窗口，
                // 而这条窗口里任何一次编辑器重绘都会把它当成"没有太阳"。
                ap.compositeMode = prevMode;
                RenderSettings.sun = prevSun;

                if (root != null) Object.DestroyImmediate(root);
                if (mat != null) Object.DestroyImmediate(mat);
                if (readback != null) Object.DestroyImmediate(readback);
                if (rt != null)
                {
                    if (RenderTexture.active == rt) RenderTexture.active = null;
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
        }

        // ════════════════════════════════════════════════════════════ 归因

        const float k_CodeTRefXyz = 7f;
        const float k_CodeTRefW = 8f;
        const float k_CodeSunDir = 9f;
        const float k_CodeMainDir = 10f;
        const float k_CodeMainColor = 11f;

        /// <summary>
        /// 把「着色器本次渲染看到的 uniform」打出来。不参与任何判据。
        ///
        /// 为什么值得单独有这么一段：C# 侧 <c>Shader.GetGlobalVector</c> 读的是渲染**之后**
        /// CPU 全局表里的残值，而判据需要归因的是渲染**当时**着色器手里的值。
        /// 大气 pass 每帧重发这两个全局，编辑器又会在我两次 Render 之间穿插自己的重绘，
        /// 于是两者可以合法地不一致 —— 第一次跑 #12 就撞上了：C# 读到 w = 0，
        /// 画面的行为却像 w = 1，只看 C# 那一侧永远查不出是谁不对。
        ///
        /// 取中心像素一个点就够：这里问的是「uniform 是多少」，不是「哪个像素不对」。
        /// </summary>
        static void ReportShaderObserved(StringBuilder sb, Camera cam, RenderTexture rt,
                                         Texture2D readback, Transform sunT)
        {
            sunT.localRotation = Quaternion.Euler(k_Suns[1].elevationDeg, k_SunAzimuthDeg, 0f);
            Vector3 cpuSunDir = -sunT.forward;
            int mid = (k_Size / 2) * k_Size + k_Size / 2;

            var probe = new Vector4(0.5f, 0.25f, 0.125f, 1f);   // 三个通道互不相同，便于看错位

            sb.AppendLine("── 归因（着色器实测 uniform，不参与判据）");

            var w0 = Shot(cam, rt, readback, k_TRefOff, k_CodeTRefW, 0f)[mid];
            var x0 = Shot(cam, rt, readback, k_TRefOff, k_CodeTRefXyz, 0f)[mid];
            sb.Append("　 override = (1,1,1,0) →　着色器读到 ref.xyz = (")
              .Append(x0.r.ToString("F4")).Append(", ").Append(x0.g.ToString("F4")).Append(", ")
              .Append(x0.b.ToString("F4")).Append(")　w = ").Append(w0.r.ToString("F3"))
              .Append("　ctrl.x = ").Append(w0.g.ToString("F3"))
              .Append("（应为 ").Append(k_CodeTRefW.ToString("F1")).Append("）")
              .Append("　曝光×1e4 = ").Append(w0.b.ToString("F4"))
              .AppendLine();

            var w1 = Shot(cam, rt, readback, probe, k_CodeTRefW, 0f)[mid];
            var x1 = Shot(cam, rt, readback, probe, k_CodeTRefXyz, 0f)[mid];
            sb.Append("　 override = (0.5,0.25,0.125,1) →　着色器读到 ref.xyz = (")
              .Append(x1.r.ToString("F4")).Append(", ").Append(x1.g.ToString("F4")).Append(", ")
              .Append(x1.b.ToString("F4")).Append(")　w = ").Append(w1.r.ToString("F3"))
              .Append("　ctrl.x = ").Append(w1.g.ToString("F3"))
              .AppendLine();

            var d = Shot(cam, rt, readback, k_TRefOff, k_CodeSunDir, 0f)[mid];
            var decoded = new Vector3(d.r * 2f - 1f, d.g * 2f - 1f, d.b * 2f - 1f);
            var csSide = Shader.GetGlobalVector(s_SunDirId);
            sb.Append("　 sunDir：着色器实测 = (")
              .Append(decoded.x.ToString("F4")).Append(", ").Append(decoded.y.ToString("F4"))
              .Append(", ").Append(decoded.z.ToString("F4")).Append(")　CPU 期望 = (")
              .Append(cpuSunDir.x.ToString("F4")).Append(", ").Append(cpuSunDir.y.ToString("F4"))
              .Append(", ").Append(cpuSunDir.z.ToString("F4")).Append(")　渲染后 C# 读回 = (")
              .Append(csSide.x.ToString("F4")).Append(", ").Append(csSide.y.ToString("F4"))
              .Append(", ").Append(csSide.z.ToString("F4")).Append(')')
              .AppendLine();

            // URP 自己解析出来的主光。与上一行并排看才能分辨
            // 「URP 没选中探针灯」与「URP 选对了、Vista 解析错了」——
            // 这两种成因的修法在不同文件里，只读上一行分不开。
            var md = Shot(cam, rt, readback, k_TRefOff, k_CodeMainDir, 0f)[mid];
            var mdDecoded = new Vector3(md.r * 2f - 1f, md.g * 2f - 1f, md.b * 2f - 1f);
            var mc = Shot(cam, rt, readback, k_TRefOff, k_CodeMainColor, 0f)[mid];
            sb.Append("　 URP 主光：_MainLightPosition = (")
              .Append(mdDecoded.x.ToString("F4")).Append(", ").Append(mdDecoded.y.ToString("F4"))
              .Append(", ").Append(mdDecoded.z.ToString("F4")).Append(")　_MainLightColor = (")
              .Append(mc.r.ToString("F4")).Append(", ").Append(mc.g.ToString("F4"))
              .Append(", ").Append(mc.b.ToString("F4")).Append(')')
              .AppendLine();
            sb.AppendLine("　 　 这两行怎么读：方向若与上一行的 CPU 期望一致，说明"
                        + "RenderSettings.sun 的指定生效了；主光色若接近 0，"
                        + "则任何「乘在主光上」的判据都乘在零上，是空判。");
        }

        /// <summary>
        /// w = 0 ⇒ 比值恒为 (1,1,1)。
        ///
        /// 这条守的是 <c>VistaSunTransmittanceRatio</c> 开头那个提前返回。它平时永不执行
        /// （只要场上有 <see cref="VistaTimeOfDay"/> 在驱动光色），而它一旦坏掉的症状是
        /// **拿掉时间轴组件之后画面才变** —— 本项目反复吃过的那类失效。
        ///
        /// 顺带它是唯一一条能证明「覆写钩子真的改到了 GPU」的判据：读数从
        /// 「随海拔变化的比值」变成恒 1，只可能是 w 生效了。所以它必须排在最前面。
        /// </summary>
        static bool JudgeDisabled(StringBuilder sb, Camera cam, RenderTexture rt,
                                  Texture2D readback, Transform sunT)
        {
            sunT.localRotation = Quaternion.Euler(k_Suns[1].elevationDeg, k_SunAzimuthDeg, 0f);

            var px = Shot(cam, rt, readback, k_TRefOff, k_CodeRatio, 0f);

            // 观测下发值，而不是断言「我设过了」。
            var observed = Shader.GetGlobalVector(s_TRefId);

            float worst = 0f;
            int sentinels = 0, examined = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (!IsCovered(px[i].r) || !IsCovered(px[i].g) || !IsCovered(px[i].b))
                {
                    sentinels++;
                    continue;
                }
                examined++;
                for (int c = 0; c < 3; c++)
                    worst = Mathf.Max(worst, Mathf.Abs(Ch(px[i], c) - 1f));
            }

            sb.Append("── 判据⑤ 语义闸门（w = 0 ⇒ 比值恒 1）　实测下发 _VistaSunTransmittanceRef = (")
              .Append(observed.x.ToString("F3")).Append(", ")
              .Append(observed.y.ToString("F3")).Append(", ")
              .Append(observed.z.ToString("F3")).Append(", w = ")
              .Append(observed.w.ToString("F1")).Append(')')
              .AppendLine();
            sb.Append("　 覆盖 ").Append(examined).Append(" / ").Append(px.Length)
              .Append(" 像素（哨兵 ").Append(sentinels).Append("）　最大 |比值 − 1| = ")
              .Append(worst.ToString("E3")).Append("　地板 = ")
              .Append(ReadbackFloor.ToString("E3"))
              .AppendLine();

            bool ok = true;
            if (examined == 0)
            {
                sb.AppendLine("　 **失败**：一个像素都没画到。墙没盖住视口、材质没编出 DIFF_DEBUG 变体，"
                            + "或相机 cullingMask 不对 —— 后面每一条判据都会因此变成空判。");
                return false;
            }
            if (sentinels != 0)
            {
                ok = false;
                sb.Append("　 **失败**：有 ").Append(sentinels)
                  .AppendLine(" 个像素是哨兵。墙的富余量算错了（视口 8 km，墙 "
                            + "9 km），或 DIFF_DEBUG 里走了 debugOverridden 分支。");
            }
            if (observed.w > 0.5f)
            {
                ok = false;
                sb.AppendLine("　 **失败**：下发的 w = 1，覆写钩子没生效。"
                            + "此后每一档的 T_ref 都不是本自检设定的值，所有数字作废。");
            }
            if (worst > ReadbackFloor)
            {
                ok = false;
                sb.AppendLine("　 **失败**：w = 0 时比值不是 1。提前返回没生效，"
                            + "或者读回路径的地板比记录值大 —— 后者会让判据①的容差也偏小。");
            }
            return ok;
        }

        // ════════════════════════════════════════════════════════════ 判据①②③④⑦

        static bool JudgeSun(StringBuilder sb, in SunConfig cfg, Camera cam, RenderTexture rt,
                             Texture2D readback, Transform sunT,
                             VistaAtmosphereParameters p, float groundY,
                             bool primary, ref bool anyStrongSignal)
        {
            sunT.localRotation = Quaternion.Euler(cfg.elevationDeg, k_SunAzimuthDeg, 0f);
            Vector3 sunDir = -sunT.forward;

            // T_ref 取「墙脚正下方的地面高度」那一点。于是底行的比值略大于 1、
            // 顶行明显大于 1 —— 比值 > 1 是**增益**，不是衰减，这也是判据⑦单调性的方向依据。
            Vector3 refPosKm = ToAtmosphere(new Vector3(0f, groundY, k_WallZ), p, groundY);
            float rRef = Mathf.Max(refPosKm.magnitude, 1e-4f);
            float muRef = Vector3.Dot(refPosKm / rRef, sunDir);
            Vector3 tRef = VistaSunTransmittance.Evaluate(p, rRef, muRef);

            var tRefV = new Vector4(tRef.x, tRef.y, tRef.z, 1f);

            sb.Append("── 太阳 ").Append(cfg.label)
              .Append("　sunDir(CPU) = (").Append(sunDir.x.ToString("F4")).Append(", ")
              .Append(sunDir.y.ToString("F4")).Append(", ").Append(sunDir.z.ToString("F4")).Append(')')
              .Append("　T_ref = (").Append(tRef.x.ToString("E3")).Append(", ")
              .Append(tRef.y.ToString("E3")).Append(", ").Append(tRef.z.ToString("E3")).Append(')')
              .AppendLine();

            bool ok = true;

            if (tRef.x <= 0f || tRef.y <= 0f || tRef.z <= 0f)
            {
                sb.AppendLine("　 **失败**：T_ref 有通道为 0。那一档的分母会掉到 VISTA_T_REF_FLOOR，"
                            + "判据②量的就不再是「LUT 对不对」而是「下限夹得对不对」——"
                            + "后者是判据⑥的职责。换一个不贴地平线的太阳高度。");
                return false;
            }

            // ── 五次渲染。每一次与它的对照只差一件事（见类注释里的表）。
            var r4 = Shot(cam, rt, readback, tRefV, k_CodeRatio, 0f);            // 比值本身
            var r1 = Shot(cam, rt, readback, k_TRefOff, k_CodeColor, 0f);        // G + M
            var r2a = Shot(cam, rt, readback, k_TRefOff, k_CodeColor, k_KillMainLight); // G （w=0）
            var r2b = Shot(cam, rt, readback, tRefV, k_CodeColor, k_KillMainLight);     // G （w=1）
            var r3 = Shot(cam, rt, readback, tRefV, k_CodeColor, 0f);           // G + M·ratio

            // GPU 侧实际用的太阳方向。判据②的期望值建在 CPU 的 sunDir 上，
            // 两者若不同，②会报出一个与 LUT 精度毫无关系的偏差。
            var gpuSun = Shader.GetGlobalVector(s_SunDirId);
            float sunMismatch = Mathf.Max(Mathf.Abs(gpuSun.x - sunDir.x),
                                Mathf.Max(Mathf.Abs(gpuSun.y - sunDir.y),
                                          Mathf.Abs(gpuSun.z - sunDir.z)));
            sb.Append("　 sunDir(GPU 实测) = (").Append(gpuSun.x.ToString("F4")).Append(", ")
              .Append(gpuSun.y.ToString("F4")).Append(", ").Append(gpuSun.z.ToString("F4"))
              .Append(")　最大分量差 = ").Append(sunMismatch.ToString("E3"))
              .AppendLine();
            if (sunMismatch > 1e-3f)
            {
                ok = false;
                sb.AppendLine("　 **失败**：GPU 用的太阳方向与 CPU 期望值不同。"
                            + "主光可能不是本夹具那盏（场景灯漏进 cullingMask？），"
                            + "此时判据②的偏差与 LUT 精度无关，别去查 LUT。");
            }

            float camY = cam.transform.position.y;

            // ────────────────────────────────────── 判据④：比值只碰主光
            //
            // R2a 与 R2b 之间只差 w。主光被清零之后画面里剩下的全是 GI/自发光，
            // 比值若漏进 giColor（例如误加在 MixRealtimeAndBakedGI 之前），这两张会不同。
            // 这是「乘法插在 MixRealtimeAndBakedGI **之后**」那个选择的数值依据。
            float giDelta = 0f, giLevel = 0f;
            for (int i = 0; i < r2a.Length; i++)
            {
                if (!Covered(r2a[i]) || !Covered(r2b[i])) continue;
                for (int c = 0; c < 3; c++)
                {
                    giDelta = Mathf.Max(giDelta, Mathf.Abs(Ch(r2a[i], c) - Ch(r2b[i], c)));
                    giLevel = Mathf.Max(giLevel, Ch(r2a[i], c));
                }
            }
            sb.Append("　 判据④ GI 隔离：max|R2a − R2b| = ").Append(giDelta.ToString("E3"))
              .Append("　地板 = ").Append(ReadbackFloor.ToString("E3"))
              .Append("　此时画面量级 max R2 = ").Append(giLevel.ToString("E3"))
              .AppendLine();
            if (primary)
            {
                // 这条覆盖缺口是**结构性**的，不是「换个场景就好了」：
                // 布景的墙是运行时 CreatePrimitive 出来的，没有 lightmap UV、没有 lightmapIndex，
                // 于是 SubtractDirectMainLightFromLightmap 那条分支在本夹具里永远进不去。
                // 而那条分支恰好是「乘法放在 MixRealtimeAndBakedGI 之后」这个选择的**唯一**理由。
                // 换句话说：判据④证明的是「比值没漏进 GI 项」，
                // **没有**证明「subtractive 减法用的是烘焙同源的光色」。
                // 后者要一个烘好 lightmap、Mixed 光照模式为 Subtractive 的场景来验，
                // 那是 #12 记在 CHANGELOG 里的一条未清项。
                sb.AppendLine("　 　 未覆盖：Subtractive lightmap 分支。夹具的墙是运行时生成的，"
                            + "没有 lightmap UV/index，那条分支结构上进不去 —— "
                            + "「减法口径自洽」这半个理由本自检不作担保。");
            }
            if (giLevel <= ReadbackFloor)
            {
                ok = false;
                sb.AppendLine("　 **失败**：主光清零后画面是黑的（GI ≈ 0），"
                            + "「比值没漏进 GI」这句话此时无法失败 —— 是空判据。"
                            + "需要一个有非零环境光的场景（天空 SH / 探针）。");
            }
            else if (giDelta > ReadbackFloor)
            {
                ok = false;
                sb.AppendLine("　 **失败**：主光被清零之后 w 还能改变画面。"
                            + "比值漏进了主光以外的项（giColor / additionalLights / 自发光）。");
            }

            // ────────────────────────────────────── 判据① 乘法恒等式
            //
            // 判的是 (R3 − R2) == (R1 − R2)·R4，**乘法形式**而不是除法。
            // 除法形式在 M ≈ 0 的像素（斜射到掠角的那一段）上分母趋零，会喷出一个
            // 与真实失败长得一样的大数；乘法形式在那里两边都趋零，自然通过。
            // 除法形式作为**可读的测量**在下面单独报（限定在 M 够大的像素上）。
            float worstRatioToTol = 0f, worstResidual = 0f, worstTol = 0f;
            float maxSignal = 0f, signalAtWorst = 0f;
            int examined = 0, sentinels = 0;
            float divWorst = 0f, divLevel = 0f, mMaxAll = 0f;
            float mFloor = 32f * ReadbackFloor;   // 除法测量的分母下限，纯为可读性

            for (int i = 0; i < r1.Length; i++)
            {
                if (!Covered(r1[i]) || !Covered(r2a[i]) || !Covered(r3[i]) || !Covered(r4[i]))
                {
                    sentinels++;
                    continue;
                }
                examined++;
                for (int c = 0; c < 3; c++)
                {
                    float v1 = Ch(r1[i], c), v2 = Ch(r2a[i], c);
                    float v3 = Ch(r3[i], c), v4 = Ch(r4[i], c);

                    float m = v1 - v2;
                    float lhs = v3 - v2;
                    float rhs = m * v4;

                    // 容差按三个读数各自的误差上界推，不是编出来的门限：
                    //   · 每个读数带一条 ±ReadbackFloor 的加性扰动（实测，见 VistaSelfTestNumerics）
                    //   · 每个读数在存进 ARGBHalf 时被量化，相对误差 ≤ Fp16Rel
                    //     —— 注意 R4 也吃这一条：shader 内部用 fp32 的比值去乘 mainLightColor，
                    //        而我读到的 R4 是量化后的版本，两者本来就不是同一个数。
                    // 保守方向：同像素两次渲染之差里那条加性场一阶抵消（实测性质），
                    // 这里没有把抵消算进去。
                    float tolLhs = 2f * ReadbackFloor + Fp16Rel * (Mathf.Abs(v3) + Mathf.Abs(v2));
                    float tolRhs = Mathf.Abs(v4) * (2f * ReadbackFloor
                                                    + Fp16Rel * (Mathf.Abs(v1) + Mathf.Abs(v2)))
                                 + Mathf.Abs(m) * (ReadbackFloor + Fp16Rel * Mathf.Abs(v4));
                    float tol = tolLhs + tolRhs;

                    float residual = Mathf.Abs(lhs - rhs);
                    float signal = Mathf.Abs(m * (v4 - 1f));   // w 打开之后画面变了多少

                    // 按「残差 / 容差」排序而不是按残差本身：容差在画面上差好几个量级
                    // （M 大的像素容差也大），拿绝对残差挑最坏点会一直挑到最亮那一行，
                    // 而真正危险的是残差刚好顶到自己那一格容差的像素。
                    if (residual / tol > worstRatioToTol)
                    {
                        worstRatioToTol = residual / tol;
                        worstResidual = residual;
                        worstTol = tol;
                        signalAtWorst = signal;
                    }
                    maxSignal = Mathf.Max(maxSignal, signal);

                    if (m > mFloor)
                    {
                        divWorst = Mathf.Max(divWorst, Mathf.Abs(lhs / m - v4));
                        divLevel = Mathf.Max(divLevel, m);
                    }
                    mMaxAll = Mathf.Max(mMaxAll, m);
                }
            }

            if (examined == 0)
            {
                sb.AppendLine("　 **失败**：五次渲染没有共同覆盖任何像素，判据①②③全是空判。");
                return false;
            }
            if (sentinels != 0)
            {
                ok = false;
                sb.Append("　 **失败**：").Append(sentinels)
                  .AppendLine(" 个像素在某一次渲染里是哨兵。五次渲染之间布景变了。");
            }

            sb.Append("　 判据① (R3−R2) == (R1−R2)·R4　最坏 残差/容差 = ")
              .Append(worstRatioToTol.ToString("F3"))
              .Append("（残差 ").Append(worstResidual.ToString("E3"))
              .Append("　容差 ").Append(worstTol.ToString("E3"))
              .Append("　该点信号 ").Append(signalAtWorst.ToString("E3")).Append('）')
              .AppendLine();
            sb.Append("　 　 除法形式（可读测量，仅 M > ").Append(mFloor.ToString("E3"))
              .Append(" 的像素）：max|(R3−R2)/(R1−R2) − R4| = ").Append(divWorst.ToString("E3"))
              .Append("　该子集内 max M = ").Append(divLevel.ToString("E3"))
              .Append("　全图 max M = ").Append(mMaxAll.ToString("E3"))
              .AppendLine();
            // 「全图 max M」是为了让这条测量不能悄悄地什么都没量：
            // 上一版只报子集内的 max M，读数是 0.000E+000 —— 那个 0 既可以是
            // 「没有像素过门」也可以是「过了门但 M 恰好为 0」，而它实际的含义是
            // 主光根本没照到这面墙（URP 主光是场景那盏，掠射，M ≈ 0.025 < 门限）。
            // 一条本轮无法失败的测量，必须在报告里点名说自己没执行。
            if (divLevel <= 0f)
            {
                sb.Append("　 　 注意：**没有任何像素过门**，除法形式这一轮没有执行。")
                  .Append("全图最大主光贡献 M = ").Append(mMaxAll.ToString("E3"))
                  .AppendLine("。乘法形式（上一行）仍然有效，但它在 M 很小时是弱判据。");
            }
            if (worstRatioToTol > 1f)
            {
                ok = false;
                sb.AppendLine("　 **失败**：乘法恒等式不成立。比值被乘了不止一次、"
                            + "或乘在了与 6 档输出不同的那个量上。");
            }

            // ────────────────────────────────────── 判据③ 非空性
            float signalToTol = worstTol > 0f ? maxSignal / worstTol : 0f;
            sb.Append("　 判据③ 非空：max|M·(ratio−1)| = ").Append(maxSignal.ToString("E3"))
              .Append("　≈ 容差的 ").Append(signalToTol.ToString("F1")).Append(" 倍")
              .AppendLine();
            if (signalToTol >= k_MinSignalToTol)
                anyStrongSignal = true;

            // ────────────────────────────────────── 判据② 比值 vs CPU 闭式
            //
            // 老实说清楚这一条量的是什么：T_ref 在 GPU 与 CPU 两侧是**同一个** float
            // （都来自上面那次 Evaluate），所以它在比值里整项约掉，剩下的是
            //   T_LUT(px)  vs  T_CPU(px)
            // 也就是 Validate Sun Transmittance 已经验收过的那个量，加上**新增的那一步**：
            // positionWS → (r, muSun) 的参数化。残差的主项来自 LUT 的双线性插值，
            // 不是一条独立的精度结论。
            float worstRel = 0f, worstGot = 0f, worstExp = 0f;
            int worstRow = -1;
            float minMuSun = float.PositiveInfinity, maxMuSun = float.NegativeInfinity;
            float ratioMin = float.PositiveInfinity, ratioMax = float.NegativeInfinity;

            for (int y = 0; y < k_Size; y++)
            {
                for (int x = 0; x < k_Size; x++)
                {
                    var got = r4[y * k_Size + x];
                    if (!Covered(got)) continue;

                    Vector3 world = PixelToWorld(x, y, camY);
                    Vector3 posKm = ToAtmosphere(world, p, groundY);
                    float r = Mathf.Max(posKm.magnitude, 1e-4f);
                    float mu = Vector3.Dot(posKm / r, sunDir);
                    minMuSun = Mathf.Min(minMuSun, mu);
                    maxMuSun = Mathf.Max(maxMuSun, mu);

                    Vector3 t = VistaSunTransmittance.Evaluate(p, r, mu);

                    for (int c = 0; c < 3; c++)
                    {
                        float expect = Mathf.Min(
                            Ch(t, c) / Mathf.Max(Ch(tRef, c), k_TRefFloor), k_RatioMax);
                        float g = Ch(got, c);
                        ratioMin = Mathf.Min(ratioMin, g);
                        ratioMax = Mathf.Max(ratioMax, g);

                        float rel = Mathf.Abs(g - expect) / Mathf.Max(expect, k_AbsExempt);
                        if (rel > worstRel)
                        {
                            worstRel = rel; worstGot = g; worstExp = expect; worstRow = y;
                        }
                    }
                }
            }

            sb.Append("　 判据② 比值 vs CPU 闭式：最坏相对误差 = ")
              .Append((worstRel * 100f).ToString("F3")).Append(" %（门 ")
              .Append((k_RelTol * 100f).ToString("F1")).Append(" %）　GPU = ")
              .Append(worstGot.ToString("E4")).Append("　CPU = ").Append(worstExp.ToString("E4"))
              .Append("　行 = ").Append(worstRow)
              .AppendLine();
            sb.Append("　 　 比值包线 = [").Append(ratioMin.ToString("F4")).Append(", ")
              .Append(ratioMax.ToString("F4")).Append("]　张开 ")
              .Append((ratioMax - ratioMin).ToString("F4"))
              .Append("　muSun ∈ [").Append(minMuSun.ToString("F4")).Append(", ")
              .Append(maxMuSun.ToString("F4")).Append(']')
              .AppendLine();

            if (worstRel > k_RelTol)
            {
                ok = false;
                sb.AppendLine("　 **失败**：比值与 CPU 闭式不一致。先看是不是参数化错了"
                            + "（换一个 k_WallZ，若误差随之变化则 up/r 的算法有问题）；"
                            + "若 Validate Sun Transmittance 同样超门，那是 LUT 本身的事，"
                            + "不要在这里放宽容差把它吸收掉。");
            }

            if (ratioMax - ratioMin < k_MinRatioSpread)
            {
                ok = false;
                sb.Append("　 **失败**：比值在整屏几乎不变（张开 ")
                  .Append((ratioMax - ratioMin).ToString("E3")).Append(" < ")
                  .Append(k_MinRatioSpread)
                  .AppendLine("）。判据②此时只验证了一个常数，"
                            + "「随海拔变化」这件事没被测到 —— 布景高差不够或墙没画上。");
            }

            if (minMuSun <= k_MinMuSunMargin)
            {
                ok = false;
                sb.Append("　 **失败**：实测最小 muSun = ").Append(minMuSun.ToString("F4"))
                  .AppendLine("，已经贴到地球阴影分支。CPU 期望值里没有那个分支"
                            + "（刻意不写第二份射线-球求交），所以这一档的数字不可信。");
            }

            // ────────────────────────────────────── 判据⑦ 单调性
            //
            // 为什么值得单独有一条：它**不经过 CPU 闭式**。
            // 若我把像素→世界的映射写成上下翻转（GetPixels 是自底向上的，这是最常犯的一处），
            // 判据②会报出一个巨大的相对误差、却指不出方向；而单调性直接说
            // 「读数随行号递减」—— 一条与闭式无关的独立交叉核对。
            // 方向依据：海拔越高，头顶的大气越少，T 单调增，比值单调增。
            int xMid = k_Size / 2;
            int violations = 0;
            float worstDrop = 0f;
            for (int y = 1; y < k_Size; y++)
            {
                var lo = r4[(y - 1) * k_Size + xMid];
                var hi = r4[y * k_Size + xMid];
                if (!Covered(lo) || !Covered(hi)) continue;
                for (int c = 0; c < 3; c++)
                {
                    float drop = Ch(lo, c) - Ch(hi, c);
                    if (drop > ReadbackFloor)
                    {
                        violations++;
                        worstDrop = Mathf.Max(worstDrop, drop);
                    }
                }
            }
            sb.Append("　 判据⑦ 沿列单调增（x = ").Append(xMid).Append("）：违反 ")
              .Append(violations).Append(" 次　最大回落 = ").Append(worstDrop.ToString("E3"))
              .AppendLine();
            if (violations != 0)
            {
                ok = false;
                sb.AppendLine("　 **失败**：比值随海拔升高反而变小。"
                            + "最可能的原因是像素→世界的 v 方向翻了（GetPixels 自底向上）。");
            }

            return ok;
        }

        // ════════════════════════════════════════════════════════════ 判据⑥

        /// <summary>
        /// 两条数值闸门的覆盖。
        ///
        /// <c>VISTA_T_REF_FLOOR</c> 与 <c>VISTA_T_RATIO_MAX</c> 在正常运行里**永不执行**
        /// （实测 T_ref 最小的一档也在 5e-5 量级，比下限高 50 倍）。
        /// 一条永不执行的保护线等于没有 —— 所以这里强行把 T_ref 压到触发区，
        /// 让它们真的跑一遍，并把观测值报出来。
        ///
        /// ── 一处无法分离的覆盖，明写在这里 ──
        ///
        /// 「下限接管」与「上限接管」在这台夹具上给出**同一个读数**（都是 1e4）：
        /// T(px) 在本布景里是 1e-1 量级，除以下限 1e-6 得到 1e5，必然又撞上上限。
        /// 要把下限单独测出来需要 T(px) &lt; 1e-2，那只在掠射角上出现，而那时 CPU 期望值
        /// 又要依赖没有第二份实现的地球阴影分支。结论：c3 只证明
        /// 「0 分母没有产出 NaN/inf、结果被夹住了」，不证明是哪条闸门夹的。
        /// </summary>
        static bool JudgeClamps(StringBuilder sb, Camera cam, RenderTexture rt,
                                Texture2D readback, Transform sunT,
                                VistaAtmosphereParameters p, float groundY, in SunConfig cfg)
        {
            sunT.localRotation = Quaternion.Euler(cfg.elevationDeg, k_SunAzimuthDeg, 0f);
            Vector3 sunDir = -sunT.forward;

            sb.Append("── 判据⑥ 数值闸门覆盖（太阳 ").Append(cfg.label).Append('）').AppendLine();

            bool ok = true;

            // c1：不触发任何闸门，但比值被抬到 ~500。
            // 存在的理由：证明「大比值」本身走得通 —— 否则 c2/c3 读到 1e4 时，
            // 「被上限夹住了」与「除法在大比值下就是坏的」无法区分。
            ok &= ClampCase(sb, cam, rt, readback, p, groundY, sunDir,
                            new Vector4(1e-3f, 1e-3f, 1e-3f, 1f),
                            "c1 T_ref = 1e-3（不触闸门）", false);

            // c2：只触发上限（1e-5 > 下限 1e-6，所以下限不接管）。
            ok &= ClampCase(sb, cam, rt, readback, p, groundY, sunDir,
                            new Vector4(1e-5f, 1e-5f, 1e-5f, 1f),
                            "c2 T_ref = 1e-5（只触上限）", true);

            // c3：T_ref = 0 且 w = 1。这一组在正常代码路径里到不了
            // （VistaTimeOfDay 在 T 全零时发布 w = 0），只能靠覆写钩子造出来。
            ok &= ClampCase(sb, cam, rt, readback, p, groundY, sunDir,
                            new Vector4(0f, 0f, 0f, 1f),
                            "c3 T_ref = 0（下限 + 上限，无法互相分离）", true);

            return ok;
        }

        static bool ClampCase(StringBuilder sb, Camera cam, RenderTexture rt, Texture2D readback,
                              VistaAtmosphereParameters p, float groundY, Vector3 sunDir,
                              Vector4 tRefV, string label, bool expectSaturated)
        {
            var px = Shot(cam, rt, readback, tRefV, k_CodeRatio, 0f);
            float camY = cam.transform.position.y;

            float gotMin = float.PositiveInfinity, gotMax = float.NegativeInfinity;
            int bad = 0, saturated = 0, examined = 0, notSaturatedByCpu = 0;
            float worstRel = 0f;

            for (int y = 0; y < k_Size; y++)
            {
                for (int x = 0; x < k_Size; x++)
                {
                    var got = px[y * k_Size + x];
                    Vector3 posKm = ToAtmosphere(PixelToWorld(x, y, camY), p, groundY);
                    float r = Mathf.Max(posKm.magnitude, 1e-4f);
                    float mu = Vector3.Dot(posKm / r, sunDir);
                    Vector3 t = VistaSunTransmittance.Evaluate(p, r, mu);

                    for (int c = 0; c < 3; c++)
                    {
                        float g = Ch(got, c);
                        // NaN/inf 的检查必须在覆盖判定之前：它们正是这条判据要抓的东西，
                        // 而 IsCovered 会把它们归成「哨兵」悄悄跳过。
                        if (float.IsNaN(g) || float.IsInfinity(g)) { bad++; continue; }
                        if (g >= k_SentinelGate) continue;      // 真哨兵（没画到）

                        examined++;
                        gotMin = Mathf.Min(gotMin, g);
                        gotMax = Mathf.Max(gotMax, g);

                        float raw = Ch(t, c) / Mathf.Max(Ch(tRefV, c), k_TRefFloor);
                        float expect = Mathf.Min(raw, k_RatioMax);
                        if (raw < k_RatioMax) notSaturatedByCpu++;
                        else saturated++;

                        worstRel = Mathf.Max(worstRel,
                            Mathf.Abs(g - expect) / Mathf.Max(expect, k_AbsExempt));
                    }
                }
            }

            // 上限档的容差不能用 1%：1e4 在 fp16 里的一个 ulp 是 8，
            // 相对量 8e-4，1% 门是它的 12 倍 —— 够用，所以仍按 1% 报，
            // 只是要知道这一档的分辨力是 8 而不是 1e-2。
            sb.Append("　 ").Append(label)
              .Append("　读数 ∈ [").Append(float.IsInfinity(gotMin) ? "—" : gotMin.ToString("E4"))
              .Append(", ").Append(float.IsInfinity(gotMax) ? "—" : gotMax.ToString("E4")).Append(']')
              .Append("　最坏相对误差 = ").Append((worstRel * 100f).ToString("F3")).Append(" %")
              .Append("　CPU 判定被夹住 = ").Append(saturated)
              .Append(" / 未夹住 = ").Append(notSaturatedByCpu)
              .Append("　NaN/inf = ").Append(bad)
              .AppendLine();

            bool ok = true;
            if (examined == 0)
            {
                sb.AppendLine("　 　 **失败**：这一档一个像素都没读到，闸门仍然是未覆盖的。");
                return false;
            }
            if (bad != 0)
            {
                ok = false;
                sb.AppendLine("　 　 **失败**：出现 NaN/inf。闸门没挡住 —— 这正是它存在的理由，"
                            + "而 NaN 会顺着 BRDF 污染整个像素（移动端表现为日落零星黑点）。");
            }
            if (worstRel > k_RelTol)
            {
                ok = false;
                sb.AppendLine("　 　 **失败**：读数与 CPU 预测（含钳位）不一致。"
                            + "两侧的 VISTA_T_REF_FLOOR / VISTA_T_RATIO_MAX 镜像值可能已经漂开。");
            }
            if (expectSaturated && saturated == 0)
            {
                ok = false;
                sb.AppendLine("　 　 **失败**：这一档本应触发上限，但 CPU 预测没有任何通道被夹住 —— "
                            + "闸门没被执行，这条判据是空的。挑一个更小的 T_ref。");
            }
            if (!expectSaturated && saturated != 0)
            {
                ok = false;
                sb.AppendLine("　 　 **失败**：这一档本应**不**触发任何闸门，"
                            + "却有通道被夹住。它就失去了「大比值本身走得通」这个作用。");
            }
            return ok;
        }

        // ════════════════════════════════════════════════════════════ 采集

        /// <summary>
        /// 设好状态渲一帧并读回。
        ///
        /// 里面渲了**两遍**取第二遍：<c>_VistaSunTransmittanceRef</c> 由大气 pass 在本帧
        /// 下发，理论上一遍就够；但阴影贴图、可见灯列表这类每帧状态在第一帧之后才稳定，
        /// 而「读数比设定值晚一帧」这种失效的症状是「偶尔通过」—— 最费时间的一类。
        /// 一帧 128² 的代价可以忽略，不值得为它省。
        /// </summary>
        static Color[] Shot(Camera cam, RenderTexture rt, Texture2D readback,
                            Vector4 tRef, float code, float injectMain)
        {
            VistaTimeOfDay.s_DebugTRefOverride = tRef;
            Shader.SetGlobalVector(s_InjectId, new Vector4(injectMain, 0f, 0f, 0f));
            Shader.SetGlobalVector(s_CtrlId, new Vector4(code, 0f, 0f, 0f));

            cam.Render();
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
            readback.Apply(false);
            RenderTexture.active = prev;
            return readback.GetPixels();
        }

        /// <summary>
        /// 这个读数是被测量还是哨兵。双侧闭合写法：NaN 与 ±inf 都算哨兵
        /// （NaN 参与的比较全为 false，单侧的 <c>v &lt; gate</c> 排不掉 −inf）。
        /// </summary>
        static bool IsCovered(float v) => v >= 0f && v < k_SentinelGate;

        static bool Covered(Color c) => IsCovered(c.r) && IsCovered(c.g) && IsCovered(c.b);

        static float Ch(Color c, int i) => i == 0 ? c.r : (i == 1 ? c.g : c.b);
        static float Ch(Vector3 v, int i) => i == 0 ? v.x : (i == 1 ? v.y : v.z);
        static float Ch(Vector4 v, int i) => i == 0 ? v.x : (i == 1 ? v.y : v.z);

        // ════════════════════════════════════════════════════════════ 几何

        /// <summary>
        /// 像素中心 → 世界坐标。轴对齐的正交相机下这是一条乘加，没有射线求交。
        /// <c>GetPixels</c> 自底向上，所以 y = 0 对应 v = 0，与这里一致 ——
        /// 这条约定由判据⑦独立交叉核对。
        /// </summary>
        static Vector3 PixelToWorld(int x, int y, float camY)
        {
            float u = (x + 0.5f) / k_Size;
            float v = (y + 0.5f) / k_Size;
            // aspect 固定为 1（SetTarget 里设的），所以水平半宽也是 k_OrthoSizeM
            return new Vector3(
                (2f * u - 1f) * k_OrthoSizeM,
                camY + (2f * v - 1f) * k_OrthoSizeM,
                k_WallZ);
        }

        /// <summary>
        /// 世界 (m) → 大气空间 (km)。这是 <c>VistaWorldToAtmosphere</c> 的 CPU 对照，
        /// 精确可复现的前提是星球中心的 XZ 钉在原点、不跟相机走
        /// （见 <c>VistaAtmosphereViewData.Create</c>）。
        /// </summary>
        static Vector3 ToAtmosphere(Vector3 worldMeters, VistaAtmosphereParameters p, float groundY)
        {
            float toKm = VistaAtmosphereParameters.worldToAtmosphere;
            var centerKm = new Vector3(0f, groundY * toKm - p.bottomRadius, 0f);
            return worldMeters * toKm - centerKm;
        }

        // ════════════════════════════════════════════════════════════ 布景

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

        static void Build(Shader litShader, int layer, float groundY, RenderTexture rt,
                          out GameObject root, out Camera cam, out Transform sunT, out Material mat)
        {
            root = new GameObject("Vista Sun Transmittance Ratio Probe")
                { hideFlags = HideFlags.HideAndDontSave };
            root.transform.position = new Vector3(0f, groundY + k_OrthoSizeM, 0f);

            // ── 相机：正交、轴对齐、朝 +Z。
            var camGo = new GameObject("Camera") { hideFlags = HideFlags.HideAndDontSave };
            camGo.transform.SetParent(root.transform, false);
            camGo.layer = layer;
            camGo.transform.localRotation = Quaternion.identity;
            cam = camGo.AddComponent<Camera>();

            cam.enabled = false;                 // 只手动 Render()
            cam.cullingMask = 1 << layer;
            cam.orthographic = true;
            cam.orthographicSize = k_OrthoSizeM;
            cam.nearClipPlane = k_NearClip;
            cam.farClipPlane = k_FarClip;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.allowHDR = true;                 // 否则中间 RT 退成 8-bit
            cam.allowMSAA = false;               // MSAA 会把哨兵混进被测像素
            cam.targetTexture = rt;
            cam.backgroundColor = new Color(k_Sentinel, k_Sentinel, k_Sentinel, 1f);
            cam.aspect = 1f;                     // 必须在 targetTexture 之后设

            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = false;
            camData.volumeLayerMask = 0;         // 场景的 Tonemapping 不能进来
            camData.antialiasing = AntialiasingMode.None;
            camData.renderShadows = false;       // 阴影边界会在墙上造出与比值无关的梯度

            // ── 自带太阳，挂在探针 layer 上。
            //
            // 注意：**光挂在探针 layer 上并不足以让它成为主光**。
            // 平行光不因为 layer 不在 cullingMask 里就从 visibleLights 里消失，
            // 而 URP 的 GetMainLightIndex 第一条规则就是「等于 RenderSettings.sun 的
            // 那盏直接返回」—— 于是场景那盏会赢。所以调用方还要把 RenderSettings.sun
            // 指过来（见 Validate 里的说明；这条是 #12 第一次运行实测出来的）。
            var lightGo = new GameObject("Sun") { hideFlags = HideFlags.HideAndDontSave };
            lightGo.transform.SetParent(root.transform, false);
            lightGo.layer = layer;
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;
            light.color = Color.white;
            light.intensity = 1f;                // 物理光色不参与，理由见类注释
            light.useColorTemperature = false;
            sunT = lightGo.transform;

            // ── 材质
            mat = new Material(litShader) { hideFlags = HideFlags.HideAndDontSave };
            mat.EnableKeyword("VISTA_LIT_DIFF_DEBUG");
            var encoded = new Color(k_BaseLevel, k_BaseLevel, k_BaseLevel, 1f).gamma;
            mat.SetVector("_BaseColor", new Vector4(encoded.r, encoded.g, encoded.b, 1f));
            mat.SetFloat("_Cull", 0f);
            mat.SetFloat("_Smoothness", 0f);
            mat.SetFloat("_Metallic", 0f);
            // 高光与环境反射是视线相关的，会在墙内造出与比值无关的梯度。
            // uniform 与关键字都要动：分支由关键字决定，只写 uniform 等于没关。
            mat.SetFloat("_SpecularHighlights", 0f);
            mat.SetFloat("_EnvironmentReflections", 0f);
            mat.SetFloat("_ReceiveShadows", 0f);
            mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            mat.EnableKeyword("_RECEIVE_SHADOWS_OFF");

            MakeWall(root.transform, layer, mat, cam.transform.position.y);
        }

        /// <summary>
        /// 一面覆盖整个视口的墙。
        ///
        /// 朝向不靠猜：读 mesh 自己的法线，再把它旋到指向相机（−Z）。
        /// 「Unity 的 Quad 法线朝 +Z 还是 −Z」是一条我不该在判据里赌的事实 ——
        /// 猜错的后果是 NdotL &lt; 0、主光贡献为 0、判据①变成 0 == 0 的空判。
        /// （判据③会揭穿它，但让判据自己去发现一件可以直接读出来的事实是浪费。）
        /// </summary>
        static void MakeWall(Transform parent, int layer, Material mat, float camY)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "Wall";
            go.hideFlags = HideFlags.HideAndDontSave;
            go.layer = layer;
            go.transform.SetParent(parent, false);

            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);

            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            var normals = mesh.normals;
            Vector3 nLocal = normals != null && normals.Length > 0 ? normals[0] : Vector3.forward;

            go.transform.position = new Vector3(0f, camY, k_WallZ);
            go.transform.rotation = Quaternion.FromToRotation(nLocal, Vector3.back);
            go.transform.localScale = new Vector3(k_WallSizeM, k_WallSizeM, 1f);

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            // 探针**开着**：判据④要求「主光清零之后画面不是黑的」，
            // 否则「比值没漏进 GI」这句话在结构上无法失败。
            mr.lightProbeUsage = LightProbeUsage.BlendProbes;
            mr.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
        }
    }
}
