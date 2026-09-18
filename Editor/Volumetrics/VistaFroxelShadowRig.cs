using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista.EditorTools
{
    /// <summary>
    /// #27 第 1 项的**共用布景与尺子**：几何、太阳方向、介质参数、分区口径、像素统计。
    ///
    /// ── 为什么单独一个类 ──
    /// 这套布景有两个消费者：探路工具（<see cref="VistaFroxelSceneScout"/>，只报读数）
    /// 与判据（<c>VistaFroxelModeAcceptance</c>，报通过/失败）。判据的三条门槛
    /// （α/β/γ）全部是照探路读数定下来的 —— 若两边各搭一套布景，那些门槛担保的
    /// 就不再是同一个场景，而「门槛是从哪次读数来的」这条链会在第一次改几何时
    /// 静默断掉：太阳挪 10°、缝宽改 2 m，判据照样全绿，因为它量的是**自己**那套布景。
    ///
    /// 本项目已经在这上面栽过两次，两次都是「数字看起来正常」：
    ///   · 方位角符号写反 ⇒ 被遮 5.9%，而那 5.9% 全在板背面、一个像素都没进画面；
    ///   · 分区里一半是天空 ⇒ 中位数恰好 +0，量的是天空不是雾柱。
    /// 两次都不是编译器或任何门发现的，是「这个数是从哪儿来的」问出来的。
    /// 把布景收成一份，至少让「从哪儿来的」只有一个答案。
    ///
    /// ── 与 <c>VistaApVariantAgreementSelfTest.Build</c> 的关系 ──
    /// 同一套骨架、相反的阴影取向：那一个在五个地方把阴影关死（阴影边界是 AP 合成的
    /// 干扰项），这一个必须把同样那五个地方全部打开 —— 被测的就是阴影本身。
    /// 两份布景因此**不**合并成一个带开关的函数：那会让「这里为什么关阴影」
    /// 这条理由从代码里消失，而它是 AP 那条自检成立的前提之一。
    /// </summary>
    internal static class VistaFroxelShadowRig
    {
        // ---- 布景尺寸。全部必须落在 URP 的 shadow distance（本工程 150 m）以内：
        //      超出之后投影物不进 atlas，而那是布景问题、不是代码问题。
        internal const int   k_RtSize        = 256;
        internal const float k_Fov           = 60f;      // 正常视场，不用 AP 自检那个 2°：
                                                // 那一个是为了让远处面板填满一列，这里要的是「像个真实画面」
        internal const float k_NearClip      = 0.1f;
        internal const float k_FarClip       = 300f;     // > shadow distance，好让 maxShadowDistance 由资产决定
        internal const float k_CameraHeightM = 2f;       // 人眼高度：光柱是站在地面上看到的
        internal const float k_GroundSizeM   = 400f;

        // ---- 遮挡物：z = 40 m 处两块并排的板，中间留一道缝 ----
        //
        // 为什么是「两块带缝」而不是一整面墙：判据 β 判的是**同一张图里**
        // 被遮的雾比没被遮的雾暗。一整面墙把全屏的雾一起压暗，那种读数
        // 与「Froxel 档整体加了个常数偏移」长得一模一样 —— 它测不出
        // 阴影查询有没有真的按位置变化。缝让同一帧里同时存在两种像素，
        // 于是 β 可以在**图内**做对照，不依赖任何跨图的归一化。
        // 这也正是光柱（god ray）在真实场景里出现的几何：光从缝里漏过来。
        internal const float k_OccluderZ    = 40f;
        internal const float k_PanelW       = 20f;
        internal const float k_PanelH       = 25f;
        internal const float k_PanelCenterX = 16f;   // 两块板中心 ±16 ⇒ 缝在 |x| < 6

        // ---- 太阳 ----
        //
        // 方位角**必须**让光朝 −Z（也就是朝相机）走，否则影子落在板的背面、
        // 整个在视野之外。这条不是风格选择，是这套布景成立的前提，
        // 所以把向量写在这里，而不是靠一句「从侧后方来」的描述 ——
        // 描述不会在符号写反时报错（上一版就写反了：读数只有 5.9% 被遮，
        // 而那 5.9% 全在板后面、一个像素都没进画面）。
        //
        // 约定与 VistaTimeOfDay.ApplyRotation 相同：Euler(alt, az + 180, 0)，
        //   forward = (cos alt·sin(az+180), −sin alt, cos alt·cos(az+180))
        // alt = 25°, az = 15° ⇒ Euler(25, 195, 0) ⇒ forward ≈ (−0.235, −0.423, −0.875)。
        // 光沿 forward 走，故朝 −Z：板顶 (y = 25) 的影子在 t = 25/0.423 ≈ 59 m 处落地，
        // 对应 Δz ≈ −52 m —— 影子从 z = 40 一路扫过相机脚下，
        // 相机与板之间那一整段空气都在阴影里，缝里那一柱不在。
        //
        // 不取 az = 0（正对 −Z）：那会让光柱与 froxel 网格的 XY 轴完全对齐，
        // 于是「按 froxel 中心采样」与「按任意点采样」在这个布景上给出相同答案，
        // 判据会对一类真实的采样错位免疫。15° 的斜角把这个巧合去掉。
        internal const float k_SunAltitudeDeg = 25f;
        internal const float k_SunAzimuthDeg  = 15f;

        // 平均自由程 120 m：近层 64 m 上的光学厚度约 0.53，雾明显但远没饱和。
        // 取 400 m（资产默认）会让近层的 1 − T 只有 0.15，两档之间的差被压进读回噪声里。
        internal const float k_MeanFreePathM = 120f;

        // atlas「是不是一个常数」的门。与 VistaVolumetricFogState 判据⑥b 同一个数、
        // 同一条理由：判的是常数性，不是「min 等于某个清空值」——
        // 清空值取决于 UNITY_REVERSED_Z，按约定写死的门会在空 atlas 上**假通过**。
        internal const float k_AtlasSpreadGate = 1e-4f;

        // 空转帧数。第一帧要编译关键字变体、分配三张 3D 表、并让重投影历史进入稳态。
        internal const int k_WarmupFrames = 4;

        // 观测四：换档之后要空转多少帧才取图。
        //
        // 近层有时间重投影（#22a）与抖动（#22b），换档等于把历史整个作废，
        // 之后要若干帧才重新收敛。取 12 是因为 historyTimeConstant = 0.33 s，
        // 按 60 fps 约 20 帧一个时间常数 —— 12 帧不到一个常数，**故意**没取到完全收敛：
        // 观测四要报的是「这个布景上信噪比大概多少」，而不是「收敛之后多少」。
        // 真正的收敛性归 #27 第 2 项的判据⑯管，那一项有自己的尺子。
        // 这里取一个偏保守的帧数，于是报出来的噪声地板只会偏高、不会偏低 ——
        // 偏高的地板会让「有分辨力」这个结论更难成立，是安全的那一侧。
        internal const int k_SettleFrames = 12;

        // 相对差的分母下限，按**本布景 AP 档亮度的中位数**取比例，不写死常数。
        //
        // 写死常数在这里必然错：整条管线存的是**预曝光**辐亮度，而曝光由场景里的
        // VistaTimeOfDay 决定（EV100 5 与 15 差 1024 倍）。一个按某次曝光调出来的
        // 绝对下限，换个场景就要么吃掉全部信号、要么形同虚设。
        internal const float k_DenomFloorFraction = 1e-3f;

        // Weber 1%：全项目统一的可见阈。
        internal const float k_WeberThreshold = 0.01f;

        // 观测四的分区。u 是屏幕横向归一化坐标。
        // 缝在 |x| < 6 m、遮挡物在 z = 40 m、fov 60° ⇒ 该处可见半宽 40·tan30° = 23.1 m，
        // 故缝对应 |u − 0.5| < 6 / 46.2 ≈ 0.13。取 0.10 留一条余量，
        // 免得把缝边缘的半影算进「亮柱」那一侧。
        internal const float k_GapHalfWidthU  = 0.10f;
        internal const float k_SideInnerU     = 0.18f;   // 侧区取 [0.18, 0.34] 与镜像，整块落在板后
        internal const float k_SideOuterU     = 0.34f;

        // ==================================================================== 状态托管

        /// <summary>
        /// 两个消费者都要改同一批全局状态再改回去。列表只有一份，是因为
        /// 「改了没还回去」的症状是**下一个**工具读到一个被污染的场景 ——
        /// 那时故障点与原因之间隔着一次菜单点击，几乎无法归因。
        ///
        /// 只托管**共用**的那几项。判据额外要动的（远边界 D、阴影归因开关）
        /// 由判据自己存还，不塞进来：塞进来会让这个结构体的字段随某一个消费者
        /// 的需要生长，而探路根本不碰那些。
        /// </summary>
        internal struct RigState
        {
            VistaFogSettings m_Fog;
            VistaVolumetricFogSettings m_Vf;
            VistaFogSettings.Mode m_Mode;
            VistaFogSettings.DensityInput m_DensityInput;
            float m_Mfp;
            bool m_Injection;
            Light m_Sun;
            bool m_UnityFog;

            internal static RigState Capture(VistaFogSettings fog, VistaVolumetricFogSettings vf)
            {
                return new RigState
                {
                    m_Fog = fog,
                    m_Vf = vf,
                    m_Mode = fog.mode,
                    m_DensityInput = fog.densityInput,
                    m_Mfp = fog.meanFreePathMeters,
                    m_Injection = vf.enableInjection,
                    m_Sun = RenderSettings.sun,
                    m_UnityFog = RenderSettings.fog,
                };
            }

            /// <summary>
            /// 进入本布景要求的那一档：Froxel + 注入开 + 平均自由程 120 m，
            /// 并关掉 Unity 内置雾（它会在画面上叠一层与本模块无关的颜色）。
            /// </summary>
            internal void Apply()
            {
                RenderSettings.fog = false;
                m_Fog.mode = VistaFogSettings.Mode.Froxel;
                m_Fog.densityInput = VistaFogSettings.DensityInput.MeanFreePath;
                m_Fog.meanFreePathMeters = k_MeanFreePathM;
                m_Vf.enableInjection = true;
            }

            internal void Restore()
            {
                if (m_Fog != null)
                {
                    m_Fog.mode = m_Mode;
                    m_Fog.densityInput = m_DensityInput;
                    m_Fog.meanFreePathMeters = m_Mfp;
                }
                if (m_Vf != null) m_Vf.enableInjection = m_Injection;
                RenderSettings.sun = m_Sun;
                RenderSettings.fog = m_UnityFog;
            }
        }

        /// <summary>渲若干帧后读回，返回逐像素的 Rec.709 亮度。</summary>
        internal static float[] CaptureLuminance(Camera cam, RenderTexture rt, Texture2D tex, int frames)
        {
            for (int i = 0; i < frames; i++)
                cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = prev;

            var px  = tex.GetPixels();
            var lum = new float[px.Length];
            for (int i = 0; i < px.Length; i++)
                lum[i] = 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
            return lum;
        }

        /// <summary>
        /// 分位数。报 p50/p90/p99/max 而不是只报 max ——
        /// 「max 回答不了阈值该摆哪儿，分位数才能」是本项目记过的一条。
        /// max 单独出现时永远只反映最坏的那一个像素，与整幅图的可分辨性无关。
        /// </summary>
        internal static string Percentiles(float[] values)
        {
            return $"p50 {Sci(Percentile(values, 0.50f))}"
                 + $"  p90 {Sci(Percentile(values, 0.90f))}"
                 + $"  p99 {Sci(Percentile(values, 0.99f))}"
                 + $"  max {Sci(Percentile(values, 1.00f))}";
        }

        internal static float Percentile(float[] values, float q)
        {
            var s = (float[])values.Clone();
            System.Array.Sort(s);
            int idx = Mathf.Clamp(Mathf.RoundToInt(q * (s.Length - 1)), 0, s.Length - 1);
            return s[idx];
        }

        /// <summary>
        /// 一个竖条区域（按 |u − 0.5| 的区间取，左右对称两块）里的统计量。
        /// 竖条而不是矩形：遮挡与缝的边界是竖直的，横向分区与几何对齐，
        /// 纵向则要把地面、板、天空都包进来 —— 只取某一行会让读数依赖那一行选在哪。
        ///
        /// 同时给全部像素与「近层参与了的」像素两个中位数，理由见调用处。
        /// </summary>
        internal static RegionStat RegionStats(float[] values, int w, int h, float innerU, float outerU)
        {
            var all     = new System.Collections.Generic.List<float>();
            var touched = new System.Collections.Generic.List<float>();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float du = Mathf.Abs((x + 0.5f) / w - 0.5f);
                    if (du < innerU || du >= outerU) continue;
                    float v = values[y * w + x];
                    all.Add(v);
                    if (v != 0f) touched.Add(v);
                }
            }
            return new RegionStat
            {
                total         = all.Count,
                touched       = touched.Count,
                medianAll     = Median(all),
                medianTouched = Median(touched),
            };
        }

        internal struct RegionStat
        {
            public int total;
            public int touched;
            public float medianAll;
            public float medianTouched;
        }

        internal static float Median(System.Collections.Generic.List<float> v)
        {
            if (v.Count == 0) return float.NaN;
            v.Sort();
            return v[v.Count / 2];
        }

        /// <summary>带正负号的科学计数，符号本身是读数的一部分（判据 β 判的就是符号）。</summary>
        internal static string Signed(float v)
            => float.IsNaN(v) ? "n/a" : (v >= 0f ? "+" : "−") + Sci(Mathf.Abs(v));

        // ==================================================================== 布景

        /// <summary>
        /// 搭一套只有三件东西的布景：相机、一盏投硬阴影的平行光、一块地 + 一面墙。
        ///
        /// 与 <c>VistaApVariantAgreementSelfTest.Build</c> 的关系是「同一套骨架、
        /// 相反的阴影取向」：那一个在五个地方把阴影关死（阴影边界是 AP 合成的干扰项），
        /// 这一个必须把同样那五个地方全部打开 —— 被测的就是阴影本身。
        /// 两份布景因此不能合并成一个带开关的函数：那会让「这里为什么关阴影」
        /// 这条理由从代码里消失，而它是 AP 那条自检成立的前提之一。
        /// </summary>
        internal static void Build(Shader litShader, int layer, float groundLevelWorldY, RenderTexture rt,
                          out GameObject root, out Camera cam, out Material mat, out Light sun)
        {
            root = new GameObject("Vista Froxel Scene Scout") { hideFlags = HideFlags.HideAndDontSave };
            root.transform.position = new Vector3(0f, groundLevelWorldY, 0f);

            // ---- 相机 ----
            var camGo = new GameObject("Camera") { hideFlags = HideFlags.HideAndDontSave };
            camGo.transform.SetParent(root.transform, false);
            camGo.layer = layer;
            camGo.transform.localPosition = new Vector3(0f, k_CameraHeightM, 0f);
            camGo.transform.localRotation = Quaternion.identity;      // 水平朝 +Z

            cam = camGo.AddComponent<Camera>();
            cam.enabled = false;              // 只在我们 Render() 时出图，不参与编辑器的常规重绘
            cam.cullingMask = 1 << layer;     // 与当前场景隔离（阴影投射物按同一套剔除收集）
            cam.orthographic = false;
            cam.fieldOfView = k_Fov;
            cam.nearClipPlane = k_NearClip;
            cam.farClipPlane = k_FarClip;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.allowHDR = true;
            cam.allowMSAA = false;
            cam.targetTexture = rt;
            cam.aspect = 1f;                  // 必须在 targetTexture 之后：赋目标纹理会重算 aspect

            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = false;
            camData.volumeLayerMask = 0;      // 不吃当前场景的 Volume：曝光/色调会改读数
            camData.antialiasing = AntialiasingMode.None;
            camData.renderShadows = true;     // ← 与 AP 自检相反的那一条

            // ---- 太阳 ----
            var lightGo = new GameObject("Sun") { hideFlags = HideFlags.HideAndDontSave };
            lightGo.transform.SetParent(root.transform, false);
            lightGo.layer = layer;
            sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            // 硬阴影：froxel 那边逐 froxel 只做 1 tap，软阴影关键字在探针里是被断言为 0 的。
            sun.shadows = LightShadows.Hard;
            sun.shadowStrength = 1f;
            sun.intensity = 1f;
            // Euler(仰角, 方位+180)：与 VistaTimeOfDay.ApplyRotation 同一个约定 ——
            // 灯沿自身 +Z 照射，所以要背对太阳，方位 +180。
            lightGo.transform.localRotation = Quaternion.Euler(k_SunAltitudeDeg, k_SunAzimuthDeg + 180f, 0f);

            // ---- 地面 + 遮挡墙 ----
            mat = new Material(litShader) { hideFlags = HideFlags.HideAndDontSave };
            var encoded = new Color(0.5f, 0.5f, 0.5f, 1f).gamma;   // _BaseColor 是 Color 属性，写入会 gamma→linear
            mat.SetVector("_BaseColor", new Vector4(encoded.r, encoded.g, encoded.b, 1f));
            mat.SetFloat("_Cull", 0f);            // 双面：省掉「墙朝哪边」这个变量
            mat.SetFloat("_Smoothness", 0f);
            mat.SetFloat("_Metallic", 0f);
            // 注意这里**不**开 _RECEIVE_SHADOWS_OFF / _SPECULARHIGHLIGHTS_OFF：
            // AP 自检关它们是为了把画面里与 AP 无关的梯度清掉，这里没有那个需求，
            // 而且地面收不收阴影是肉眼复核这套布景时唯一看得见的线索。

            var ground = MakeQuad(root.transform, layer, mat, "Ground");
            ground.localPosition = Vector3.zero;
            ground.localRotation = Quaternion.Euler(90f, 0f, 0f);
            ground.localScale = new Vector3(k_GroundSizeM, k_GroundSizeM, 1f);

            for (int s = -1; s <= 1; s += 2)
            {
                var panel = MakeQuad(root.transform, layer, mat, s < 0 ? "Occluder L" : "Occluder R");
                panel.localPosition = new Vector3(s * k_PanelCenterX, k_PanelH * 0.5f, k_OccluderZ);
                panel.localRotation = Quaternion.identity;
                panel.localScale = new Vector3(k_PanelW, k_PanelH, 1f);
            }
        }

        internal static Transform MakeQuad(Transform parent, int layer, Material mat, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.hideFlags = HideFlags.HideAndDontSave;
            go.layer = layer;
            go.transform.SetParent(parent, false);

            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            // 双面投影：Quad 只有一个面，单面投影时太阳从背面照过来就一点影子都没有，
            // 而那会被读成「atlas 是空的」——一个由布景造成的假结论。
            mr.shadowCastingMode = ShadowCastingMode.TwoSided;
            mr.receiveShadows = true;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return go.transform;
        }

        // ==================================================================== 小工具

        internal static int FindUnusedLayer()
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

        /// <summary>
        /// 当前 URP 资产上的 shadow distance。取不到时返回 NaN，**不**回落到一个默认值：
        /// 回落会让报表打印一个看起来合理、但与实际生效值无关的数。
        /// </summary>
        internal static float ResolveShadowDistance()
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            return asset != null ? asset.shadowDistance : float.NaN;
        }

        internal static string Fmt(float v) => float.IsNaN(v) ? "n/a" : v.ToString("F6");

        internal static string Sci(float v) => float.IsNaN(v) ? "n/a" : v.ToString("0.###e+00");
    }
}
