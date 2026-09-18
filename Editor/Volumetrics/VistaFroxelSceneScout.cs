using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista.EditorTools
{
    /// <summary>
    /// 运行时布景探路工具（#27 验收第 1 项的**前置观测**）。
    ///
    /// ── 它不是判据，这一点必须写在最前面 ──
    ///
    /// 第 1 项要判的是「Froxel 档相对 AP-only 档到底多出了什么」，其中最硬的一条
    /// （判据 β：被主光遮住的那部分雾在 Froxel 档下更暗）只有在布景里**真的存在
    /// 被遮住的雾**时才有分辨力。而这件事在这个项目里从来没有被观测过：
    ///
    ///   · <c>VistaVolumetricFogState</c> 拿到过真实的阴影图，但它用的是**场景里
    ///     现成的相机**，于是读数随「当前打开的是哪个场景」变化；
    ///   · <c>VistaApVariantAgreementSelfTest</c> 会自己搭相机，但它在五个地方
    ///     **刻意把阴影全关掉**（相机 renderShadows、灯 shadows、材质
    ///     _RECEIVE_SHADOWS_OFF、renderer shadowCastingMode / receiveShadows），
    ///     因为阴影边界是 AP 合成的干扰项。
    ///
    /// 两个先例各覆盖了一半，没有一个回答「**自己搭的**布景里，URP 的主光
    /// shadow atlas 到底有没有内容」。这个答案决定判据 β 的形状：
    ///   有内容 ⇒ β 可以按「符号」判（遮住的雾变暗）；
    ///   没内容 ⇒ β 在这个布景上**没有分辨力**，它必须报「不可判定」而不是报「通过」。
    /// 后一条是本项目的常备反面模式：「不可判定必须是缺口，不能是通过」。
    ///
    /// 所以先跑这个工具读这几个数，再据此写判据 —— 而不是先写判据再去解释读数。
    ///
    /// ── 为什么观测要自己搭布景，而不是量当前场景 ──
    ///
    /// 判据将来住在 package 里，而 package 里**一个 .unity 都没有**
    /// （<c>Demo_Night.unity</c> 住在 Project-ARPG）。一个引用不到自己布景的判据，
    /// 在别人的工程里第一次运行就会红，而红的原因与被测代码无关。
    /// 所以布景必须由代码搭出来，探路也必须在同一种布景上探 ——
    /// 在 Demo 场景上探出来的结论，对代码搭的布景不成立。
    ///
    /// ── 为什么必须渲真实帧 ──
    ///
    /// 见 <c>VistaFroxelVolume.probeRequested</c> 的注释：探针要测的是 URP 的
    /// 编译期关键字状态与阴影图绑定，这两样只在渲染循环内存在。立即模式下读出来的
    /// 是「关键字全 0、阴影图未绑」—— 一个**完全合法**的组合，于是一切全绿。
    ///
    /// ── 它改了什么、还回去了什么 ──
    ///
    /// 要把 Froxel 档真正打开，就必须动 feature 上的三个旋钮（fog.mode、
    /// fog.meanFreePathMeters、volumetricFog.enableInjection）与场景的
    /// <c>RenderSettings.sun</c>。全部在 finally 里还原 —— 「量性能的工具不该改被量对象」
    /// 在这里的对应物是「探路工具不该给下一次运行留下不同的初始条件」。
    ///
    /// <c>RenderSettings.sun</c> 这一条不是可选的：平行光**不因为** layer 不在相机
    /// cullingMask 里就从 visibleLights 里消失，而 <c>GetMainLightIndex</c> 的第一条规则
    /// 是「等于 RenderSettings.sun 的那盏直接返回」。不接管的话，探到的阴影是
    /// **当前打开的场景**那盏灯投的，而报告里看不出这个前提。
    /// （这条是 #12 的自检实测出来的，原文在 VistaApVariantAgreementSelfTest.Build。）
    /// </summary>
    static class VistaFroxelSceneScout
    {
        // ---- 布景尺寸。全部必须落在 URP 的 shadow distance（本工程 150 m）以内：
        //      超出之后投影物不进 atlas，而那是布景问题、不是代码问题。
        const int   k_RtSize        = 256;
        const float k_Fov           = 60f;      // 正常视场，不用 AP 自检那个 2°：
                                                // 那一个是为了让远处面板填满一列，这里要的是「像个真实画面」
        const float k_NearClip      = 0.1f;
        const float k_FarClip       = 300f;     // > shadow distance，好让 maxShadowDistance 由资产决定
        const float k_CameraHeightM = 2f;       // 人眼高度：光柱是站在地面上看到的
        const float k_GroundSizeM   = 400f;

        // ---- 遮挡物：z = 40 m 处两块并排的板，中间留一道缝 ----
        //
        // 为什么是「两块带缝」而不是一整面墙：判据 β 判的是**同一张图里**
        // 被遮的雾比没被遮的雾暗。一整面墙把全屏的雾一起压暗，那种读数
        // 与「Froxel 档整体加了个常数偏移」长得一模一样 —— 它测不出
        // 阴影查询有没有真的按位置变化。缝让同一帧里同时存在两种像素，
        // 于是 β 可以在**图内**做对照，不依赖任何跨图的归一化。
        // 这也正是光柱（god ray）在真实场景里出现的几何：光从缝里漏过来。
        const float k_OccluderZ    = 40f;
        const float k_PanelW       = 20f;
        const float k_PanelH       = 25f;
        const float k_PanelCenterX = 16f;   // 两块板中心 ±16 ⇒ 缝在 |x| < 6

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
        const float k_SunAltitudeDeg = 25f;
        const float k_SunAzimuthDeg  = 15f;

        // 平均自由程 120 m：近层 64 m 上的光学厚度约 0.53，雾明显但远没饱和。
        // 取 400 m（资产默认）会让近层的 1 − T 只有 0.15，两档之间的差被压进读回噪声里。
        const float k_MeanFreePathM = 120f;

        // atlas「是不是一个常数」的门。与 VistaVolumetricFogState 判据⑥b 同一个数、
        // 同一条理由：判的是常数性，不是「min 等于某个清空值」——
        // 清空值取决于 UNITY_REVERSED_Z，按约定写死的门会在空 atlas 上**假通过**。
        const float k_AtlasSpreadGate = 1e-4f;

        // 空转帧数。第一帧要编译关键字变体、分配三张 3D 表、并让重投影历史进入稳态。
        const int k_WarmupFrames = 4;

        // 观测四：换档之后要空转多少帧才取图。
        //
        // 近层有时间重投影（#22a）与抖动（#22b），换档等于把历史整个作废，
        // 之后要若干帧才重新收敛。取 12 是因为 historyTimeConstant = 0.33 s，
        // 按 60 fps 约 20 帧一个时间常数 —— 12 帧不到一个常数，**故意**没取到完全收敛：
        // 观测四要报的是「这个布景上信噪比大概多少」，而不是「收敛之后多少」。
        // 真正的收敛性归 #27 第 2 项的判据⑯管，那一项有自己的尺子。
        // 这里取一个偏保守的帧数，于是报出来的噪声地板只会偏高、不会偏低 ——
        // 偏高的地板会让「有分辨力」这个结论更难成立，是安全的那一侧。
        const int k_SettleFrames = 12;

        // 相对差的分母下限，按**本布景 AP 档亮度的中位数**取比例，不写死常数。
        //
        // 写死常数在这里必然错：整条管线存的是**预曝光**辐亮度，而曝光由场景里的
        // VistaTimeOfDay 决定（EV100 5 与 15 差 1024 倍）。一个按某次曝光调出来的
        // 绝对下限，换个场景就要么吃掉全部信号、要么形同虚设。
        const float k_DenomFloorFraction = 1e-3f;

        // Weber 1%：全项目统一的可见阈。
        const float k_WeberThreshold = 0.01f;

        // 观测四的分区。u 是屏幕横向归一化坐标。
        // 缝在 |x| < 6 m、遮挡物在 z = 40 m、fov 60° ⇒ 该处可见半宽 40·tan30° = 23.1 m，
        // 故缝对应 |u − 0.5| < 6 / 46.2 ≈ 0.13。取 0.10 留一条余量，
        // 免得把缝边缘的半影算进「亮柱」那一侧。
        const float k_GapHalfWidthU  = 0.10f;
        const float k_SideInnerU     = 0.18f;   // 侧区取 [0.18, 0.34] 与镜像，整块落在板后
        const float k_SideOuterU     = 0.34f;

        [MenuItem("Window/Vista/Scout Runtime Froxel Scene", priority = 135)]
        static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Vista 运行时 froxel 布景探路 ===");
            sb.AppendLine("这是**观测**，不是判据：它只报读数，不报通过/失败。");
            sb.AppendLine("它要回答的四件事决定 #27 第 1 项里判据 β 的形状。");
            sb.AppendLine();

            bool complete = Scout(sb);

            sb.AppendLine();
            sb.AppendLine(complete
                ? "结论：四项观测全部取到读数，见上面每一项末尾的「⇒」。"
                : "结论：**有观测取不到读数**。在补齐之前，判据 β 只能写成「不可判定」。");
            Debug.Log(sb.ToString());
        }

        static bool Scout(StringBuilder sb)
        {
            // ── 前置条件。一条不满足就停：半套布景量出来的数字比没数字更危险。
            var feature = VistaAtmosphereFeature.current;
            if (feature == null)
            {
                sb.AppendLine("✘ 前置：VistaAtmosphereFeature.current 为 null —— "
                            + "feature 没装进当前 Renderer，或还没有相机渲过一帧。");
                return false;
            }

            var volume = feature.froxelVolume;
            if (volume == null)
            {
                sb.AppendLine("✘ 前置：feature.froxelVolume 为 null —— "
                            + "体积雾 compute 缺失（VistaRuntimeResources.volumetricFogCS），"
                            + "近层整条路径不存在，本工具无从探起。");
                return false;
            }

            if (!volume.isValid)
            {
                sb.AppendLine("✘ 前置：froxelVolume.isValid 为 false —— 11 个核里有编译不出来的。"
                            + "先看 Console 里 VolumetricFog.compute 的报错。");
                return false;
            }

            var litShader = Shader.Find("Vista/Lit");
            if (litShader == null)
            {
                sb.AppendLine("✘ 前置：找不到 Vista/Lit。布景需要一个带 ShadowCaster pass 的材质。");
                return false;
            }

            int layer = FindUnusedLayer();
            if (layer < 0)
            {
                sb.AppendLine("✘ 前置：32 个 layer 全都有物体在用，布景无法与当前场景隔离。"
                            + "腾出任意一个空 layer 再跑。\n"
                            + "  ⓘ 隔离不是洁癖：相机 cullingMask 只放行本布景的 layer，"
                            + "阴影投射物也是按同一套剔除收集的 —— 于是 atlas 里的内容"
                            + "可以确定地归因给**这个布景**，而不是当前打开的场景。");
                return false;
            }

            var fog = feature.fog;
            var vf  = feature.volumetricFog;

            // ---- 要改的全部状态，逐个存下来 ----
            var  prevMode        = fog.mode;
            var  prevDensityIn   = fog.densityInput;
            float prevMfp        = fog.meanFreePathMeters;
            bool prevInjection   = vf.enableInjection;
            var  prevSun         = RenderSettings.sun;
            bool prevUnityFog    = RenderSettings.fog;

            RenderTexture rt = null;
            GameObject root  = null;
            Material mat     = null;
            bool complete    = true;

            try
            {
                // Unity 内置雾关掉：它会在 Vista/Lit 之外的任何东西上叠一层与本模块无关的颜色。
                RenderSettings.fog = false;

                fog.mode               = VistaFogSettings.Mode.Froxel;
                fog.densityInput       = VistaFogSettings.DensityInput.MeanFreePath;
                fog.meanFreePathMeters = k_MeanFreePathM;
                vf.enableInjection     = true;

                // ARGBFloat 而不是 ARGBHalf：观测四要在 Weber 1% 的量级上量差，
                // 而 half 的相对精度约 1e-3 —— 只差一个数量级。
                // 量化噪声与被量的量同量级时，尺子会把自己的抖动报成信号
                // （这正是本项目记过的「尺子的地板与被测量同量级」那条）。
                // 三项探针观测不读像素，不受这个改动影响。
                rt = new RenderTexture(k_RtSize, k_RtSize, 24,
                                       RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);

                Build(litShader, layer, feature.groundLevelWorldY, rt,
                      out root, out Camera cam, out mat, out Light sun);

                // 接管主光。理由见类头注：不接管的话探到的是当前场景那盏灯的影子。
                RenderSettings.sun = sun;

                for (int i = 0; i < k_WarmupFrames; i++)
                    cam.Render();

                complete &= ReportVolume(sb, volume, cam);
                sb.AppendLine();
                complete &= ReportShadow(sb, volume, cam, sun);
                sb.AppendLine();
                complete &= ReportScreen(sb, fog, cam, rt);
            }
            finally
            {
                fog.mode               = prevMode;
                fog.densityInput       = prevDensityIn;
                fog.meanFreePathMeters = prevMfp;
                vf.enableInjection     = prevInjection;
                RenderSettings.sun     = prevSun;
                RenderSettings.fog     = prevUnityFog;

                if (root != null) Object.DestroyImmediate(root);
                if (mat  != null) Object.DestroyImmediate(mat);
                if (rt   != null) { rt.Release(); Object.DestroyImmediate(rt); }
            }

            return complete;
        }

        // ==================================================================== 观测一

        /// <summary>
        /// 观测一：自己搭的相机能不能拿到一个分配好的近层体。
        ///
        /// 会失败的具体机制是 <c>VistaAtmospherePass</c> 里那道相机类型闸门
        /// （<c>cameraType == Game || SceneView</c>）。代码 new 出来的 Camera 默认
        /// <c>cameraType == Game</c>，所以**应该**通过 —— 但「应该」不是读数，
        /// 而这一条不成立的话后面两项一个都测不了。
        /// </summary>
        static bool ReportVolume(StringBuilder sb, VistaFroxelVolume volume, Camera cam)
        {
            sb.AppendLine("① 自建相机能不能拿到近层体？");
            sb.AppendLine($"  cameraType = {cam.cameraType}（闸门放行 Game / SceneView）");
            sb.AppendLine($"  isAllocated = {volume.isAllocated}");

            var desc = volume.allocatedDesc;
            if (!desc.HasValue)
            {
                sb.AppendLine("  ⇒ **取不到**：allocatedDesc 为 null，近层体一次都没分配下来。");
                sb.AppendLine("     嫌疑顺序：enableInjection 没生效 / 相机类型闸门 / Prepare 失败。");
                return false;
            }

            var d = desc.Value;
            float shadowDistance = ResolveShadowDistance();
            sb.AppendLine($"  口径：{d.width}×{d.height}×{d.depth}，"
                        + $"near {d.nearMeters:F2} m，far {d.farMeters:F1} m，"
                        + $"接手点 {d.handoffMeters:F1} m");
            sb.AppendLine($"  URP shadow distance = {shadowDistance:F1} m"
                        + "（近层远边界被夹到它；布景几何必须在这个范围内）");
            sb.AppendLine($"  遮挡物在 z = {k_OccluderZ:F0} m，"
                        + (k_OccluderZ < d.handoffMeters
                            ? "**在**近层覆盖范围内 ✔"
                            : "**超出**近层覆盖范围 —— 探针采不到它的影子，下面的读数会失去意义"));
            sb.AppendLine("  ⇒ 取到了。");
            return k_OccluderZ < d.handoffMeters;
        }

        // ==================================================================== 观测二 + 三

        /// <summary>
        /// 观测二：布景的投影物有没有把内容写进 URP 的主光 shadow atlas。
        /// 观测三：<c>VistaFroxelSunShadow</c>（被测函数自己那一份）在这个布景上
        ///         读出来的值是不是真的低于 1。
        ///
        /// 两项写在一起，是因为它们读的是**同一趟探针**的同一批槽位，
        /// 而且二不成立时三的读数没有归因价值 —— 报表必须让这个依赖看得出来。
        /// </summary>
        static bool ReportShadow(StringBuilder sb, VistaFroxelVolume volume, Camera cam, Light sun)
        {
            sb.AppendLine("② 自建布景的投影物有没有进主光 shadow atlas？");

            volume.probeRequested = true;
            cam.Render();

            var buffer = volume.shadowProbeBuffer;
            if (buffer == null)
            {
                sb.AppendLine("  ⇒ **取不到**：shadowProbeBuffer 仍为 null，探针 pass 一次都没记录过。");
                return false;
            }

            if (buffer.count != VistaVolumetricFogState.k_SlotTotal)
            {
                sb.AppendLine($"  ⇒ **取不到**：槽位容量 {buffer.count} ≠ "
                            + $"{VistaVolumetricFogState.k_SlotTotal}。越界 UAV 写在 D3D11 上是"
                            + "静默丢弃，读数不可信。");
                return false;
            }

            var raw = new uint[VistaVolumetricFogState.k_SlotTotal];
            buffer.GetData(raw);

            uint flags = raw[VistaVolumetricFogState.k_SlotFlags];
            if ((flags & VistaVolumetricFogState.k_FlagRan) == 0u)
            {
                sb.AppendLine("  ⇒ **取不到**：RAN 位为 0，探针核这一帧压根没派发。");
                sb.AppendLine("     （flags == 0 时「从未派发」与「关键字全关 + 阴影图未绑」"
                            + "长得一模一样，RAN 位就是为了分开这两者。本次是前者。）");
                return false;
            }

            bool hasShadowmap = (flags & VistaVolumetricFogState.k_FlagShadowmap) != 0u;
            bool hasCascade   = (flags & VistaVolumetricFogState.k_FlagCascade)   != 0u;
            sb.AppendLine($"  flags = 0x{flags:X}："
                        + $"阴影图已绑 = {hasShadowmap}，级联 = {hasCascade}");
            sb.AppendLine($"  灯：type = {sun.type}, shadows = {sun.shadows}, "
                        + $"仰角 = {k_SunAltitudeDeg:F0}°；相机 renderShadows = "
                        + $"{cam.GetComponent<UniversalAdditionalCameraData>()?.renderShadows}");

            if (!hasShadowmap)
            {
                sb.AppendLine("  ⇒ **没有阴影图**：主光阴影这一帧根本没渲。");
                sb.AppendLine("     这直接判掉判据 β —— 在这种布景上它没有分辨力，"
                            + "必须写成「不可判定」而不是「通过」。");
                return false;
            }

            uint smW = raw[VistaVolumetricFogState.k_SlotSmWidth];
            uint smH = raw[VistaVolumetricFogState.k_SlotSmHeight];
            sb.AppendLine($"  atlas 尺寸（问纹理本身，不读 _MainLightShadowmapSize.z）：{smW}×{smH}");
            sb.AppendLine($"  ⓘ 顺带：_MainLightShadowmapSize.z 读数 = "
                        + $"{raw[VistaVolumetricFogState.k_SlotUrpSizeZ] / 1f:F0} —— "
                        + "硬阴影档它不被上传，读到的是别人留下的陈旧值，所以探针不信它。");

            if (smW == 0u || smH == 0u)
            {
                sb.AppendLine("  ⇒ **取不到**：GetDimensions 返回 0，atlas 读取那一段被跳过了。");
                return false;
            }

            bool minIsInit = raw[VistaVolumetricFogState.k_SlotShadowmapMin] == uint.MaxValue;
            float smMin = minIsInit ? float.NaN
                : raw[VistaVolumetricFogState.k_SlotShadowmapMin] / VistaVolumetricFogState.k_ShadowScale;
            float smMax = raw[VistaVolumetricFogState.k_SlotShadowmapMax] / VistaVolumetricFogState.k_ShadowScale;
            float spread = minIsInit ? 0f : smMax - smMin;
            bool atlasHasContent = !minIsInit && spread > k_AtlasSpreadGate;

            sb.AppendLine($"  atlas 深度：min {Fmt(smMin)} / max {Fmt(smMax)} ⇒ 跨度 {Sci(spread)}"
                        + $"（门 > {Sci(k_AtlasSpreadGate)}）");
            sb.AppendLine("  ⓘ 判的是「atlas 是不是一个常数」，不是「min 等于某个清空值」——"
                        + "清空值取决于 UNITY_REVERSED_Z，按约定写死会在空 atlas 上假通过。");
            sb.AppendLine(atlasHasContent
                ? "  ⇒ **有内容**：代码搭的投影物确实进了 atlas。"
                : "  ⇒ **是常数**：atlas 空的，布景的投影物没被收集进去。");

            // ---------------------------------------------------------------- 观测三
            sb.AppendLine();
            sb.AppendLine("③ VistaFroxelSunShadow 在这个布景上读到的是什么？");

            uint count    = raw[VistaVolumetricFogState.k_SlotCount];
            uint shadowed = raw[VistaVolumetricFogState.k_SlotShadowedCount];
            float strength = raw[VistaVolumetricFogState.k_SlotShadowStrength]
                           / VistaVolumetricFogState.k_ShadowScale;
            float floorVal = 1f - strength;
            bool shadowMinIsInit = raw[VistaVolumetricFogState.k_SlotShadowMin] == uint.MaxValue;
            float shadowMin = shadowMinIsInit ? float.NaN
                : raw[VistaVolumetricFogState.k_SlotShadowMin] / VistaVolumetricFogState.k_ShadowScale;
            float shadowMax = raw[VistaVolumetricFogState.k_SlotShadowMax]
                            / VistaVolumetricFogState.k_ShadowScale;

            sb.AppendLine($"  探针网格走过 {count} / {VistaVolumetricFogState.k_ProbeCountExpected} 格");
            sb.AppendLine($"  阴影值：min {Fmt(shadowMin)} / max {Fmt(shadowMax)}");
            sb.AppendLine($"  阴影强度 = {Fmt(strength)} ⇒ 全遮地板 1 − strength = {Fmt(floorVal)}"
                        + "（SampleShadowmap 返回 lerp(1, s, strength)，所以地板不是 0）");

            float ratio = count > 0u ? (float)shadowed / count : 0f;
            sb.AppendLine($"  被遮点（shadow < 0.999）：{shadowed} 格，占 {ratio * 100f:F1}%");

            if (shadowed == 0u)
            {
                sb.AppendLine("  ⇒ **一个点都没被遮**。归因（三条互斥）：");
                sb.AppendLine(atlasHasContent
                    ? "     (a) atlas 有内容 ⇒ 不是布景问题；"
                    : "     (a) atlas 是常数 ⇒ **布景问题**，投影物没进去；");
                sb.AppendLine(strength > 1e-6f
                    ? $"     (b) 强度 {Fmt(strength)} ≠ 0 ⇒ 不是强度问题；"
                    : $"     (b) 强度 {Fmt(strength)} == 0 ⇒ SampleShadowmap 恒返回 1，与代码无关；");
                sb.AppendLine("     (c) 若 (a)(b) 都排除了，剩下的才是本模块的 bug（矩阵 / 级联下标）。");
                sb.AppendLine("     无论哪一条，判据 β 在**当前这个布景**上都没有分辨力。");
                return false;
            }

            float camDriftMm = raw[VistaVolumetricFogState.k_SlotCamDriftMm] / 1000f;
            sb.AppendLine($"  ⓘ 相机漂移 = {camDriftMm:F3} mm"
                        + "（_WorldSpaceCameraPos 与 _VistaFroxelCameraWS 的距离；"
                        + "非零意味着 GetMainLightShadowFade 的淡出因子算在了另一个点上）");

            sb.AppendLine($"  ⇒ **读到了阴影**：{ratio * 100f:F1}% 的探针点被遮，"
                        + $"最暗到 {Fmt(shadowMin)}"
                        + (shadowMinIsInit ? "" : $"（距地板 {(shadowMin - floorVal):+0.000000;-0.000000}）")
                        + "。判据 β 在这个布景上**有**分辨力。");

            // 判据 β 要的是「够多的像素能分辨」，不是「有一个点被遮」。
            // 这个比例会直接决定 β 的分位数阈值摆在哪 —— 所以它是读数，不是脚注。
            sb.AppendLine($"  ⓘ 这个百分比是**体**的比例（32×32×16 探针格），不是屏幕像素的比例："
                        + "探针不做深度测试，被遮的格子可能整个藏在遮挡物背面、一个像素都没进画面。"
                        + "判据 β 要的那个数得从合成后的图上量 —— 这里只用它判「有没有被遮的格子」。");
            sb.AppendLine("     （上一版布景的太阳方位写反过一次，读数就是 5.9%："
                        + "非零、看起来正常，而那些格子全在板背面。"
                        + "一个非零的比例回答不了「被遮的雾在不在画面里」。）");
            return true;
        }

        // ==================================================================== 观测四

        /// <summary>
        /// 观测四：把观测三那个**体**的比例落到**屏幕**上 —— Froxel 档与 AP 档的
        /// 逐像素亮度差有多大，以及这个布景上尺子自己的噪声地板有多高。
        ///
        /// ── 为什么必须有这一项 ──
        ///
        /// 观测三给出 75.1% 的被遮格子，但探针网格**不做深度测试**：被遮的格子
        /// 完全可能整个藏在遮挡物背面，一个像素都进不了画面。上一版布景的 5.9%
        /// 就是这样一个「非零但没用」的读数。判据 β 判的是**画面上**被遮的雾更暗，
        /// 所以「有没有分辨力」这个问题只能在合成后的像素上回答。
        ///
        /// ── 为什么量 Froxel − AP 的差，而不是两档各自的绝对亮度 ──
        ///
        /// 缝里那一柱的光程比板前那一段长得多（透过缝能看到远处地面，板挡住的地方
        /// 只有 40 m 空气）。比较两档的**绝对**亮度会把「光程不同」读成「阴影」。
        /// 而两档看的是同一份深度、同一份几何，光程是共有的 —— 作差之后它基本抵消，
        /// 剩下的才是近层 froxel 相对 AP 多做的那些事（逐 froxel 阴影查询是其中最大的一项）。
        ///
        /// ── 为什么同时量噪声地板 ──
        ///
        /// 近层有重投影与抖动，两帧之间本来就不一样。只报「差了多少」而不报
        /// 「什么都不改时差多少」，那个差的对照组身份是假的。所以取三张图：
        /// AP、Froxel、以及**再一帧的** Froxel。后两张之间的差就是地板。
        ///
        /// 这一项仍然是**观测**：它不判通过失败，它给判据 β 定分位数与阈值。
        /// </summary>
        static bool ReportScreen(StringBuilder sb, VistaFogSettings fog, Camera cam, RenderTexture rt)
        {
            sb.AppendLine("④ 画面上：Froxel 档与 AP 档差多少，尺子自己的地板多高？");

            int w = rt.width, h = rt.height, n = w * h;
            var tex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            float[] ap, fx1, fx2;
            try
            {
                fog.mode = VistaFogSettings.Mode.AerialPerspective;
                ap = CaptureLuminance(cam, rt, tex, k_SettleFrames);

                fog.mode = VistaFogSettings.Mode.Froxel;
                fx1 = CaptureLuminance(cam, rt, tex, k_SettleFrames);

                // 什么都不改，只再渲一帧 —— 这一张与上一张的差就是地板。
                fx2 = CaptureLuminance(cam, rt, tex, 1);
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }

            // 分母：AP 档亮度，配一个按本布景中位数取比例的下限（理由见常数注释）。
            var sorted = (float[])ap.Clone();
            System.Array.Sort(sorted);
            float median = sorted[n / 2];
            float floor  = Mathf.Max(median * k_DenomFloorFraction, 1e-12f);

            sb.AppendLine($"  取图：{w}×{h}，换档后各空转 {k_SettleFrames} 帧；"
                        + $"AP 档亮度中位数 {Sci(median)}，分母下限 {Sci(floor)}"
                        + $"（= 中位数 × {Sci(k_DenomFloorFraction)}）");

            var signed = new float[n];
            var absSig = new float[n];
            var absNoi = new float[n];
            int overWeber = 0;
            int touchedAll = 0;
            for (int i = 0; i < n; i++)
            {
                float d = 1f / Mathf.Max(ap[i], floor);
                signed[i] = (fx1[i] - ap[i]) * d;
                absSig[i] = Mathf.Abs(signed[i]);
                absNoi[i] = Mathf.Abs(fx2[i] - fx1[i]) * d;
                if (absSig[i] > k_WeberThreshold) overWeber++;
                if (signed[i] != 0f) touchedAll++;
            }

            sb.AppendLine($"  近层参与的像素（两档不逐位相同）：{touchedAll} / {n}"
                        + $" = {(float)touchedAll / n:P1}"
                        + " —— 其余是天空与超出接手点的远景，两档走同一条 AP 路径");
            sb.AppendLine("  |Froxel − AP| / AP 的分位数（全屏）：");
            sb.AppendLine("    " + Percentiles(absSig));
            sb.AppendLine("  地板 |Froxel − Froxel'| / AP 的分位数（同一档、只差一帧）：");
            sb.AppendLine("    " + Percentiles(absNoi));
            sb.AppendLine($"  超过 Weber {k_WeberThreshold:P0} 的像素：{overWeber} / {n}"
                        + $" = {(float)overWeber / n:P1}");

            float p50Sig = Percentile(absSig, 0.50f);
            float p50Noi = Percentile(absNoi, 0.50f);
            float snr = p50Noi > 0f ? p50Sig / p50Noi : float.PositiveInfinity;
            sb.AppendLine($"  ⇒ 信噪比（两个 p50 之比）= {(float.IsInfinity(snr) ? "∞" : snr.ToString("F1"))}×");

            // 分区：缝里（亮柱）与板后（阴影）各取一块，看**同一张图内**的符号对不对。
            // 这是判据 β 的形状本身 —— 跨图归一化一概不需要。
            //
            // 每块都报两个中位数：全部像素、以及**近层真的参与了的**像素。
            // 后者靠「两档逐位相同」来识别：差恰好为 0 只可能出现在近层没参与的地方
            // （天空、以及深度超过接手点的远景，那些像素两档走的是同一条 AP 路径）。
            // 不把这个排除藏起来，是因为它第一次就改掉了结论 ——
            // 相机水平看出去，画面上半几乎全是天空，于是「缝里」那一栏的全像素中位数
            // 恰好是 0，量的是天空而不是被照亮的雾柱。
            // 「一个非零（或恰好为零）的数回答不了它是从哪儿来的」，所以两个都报，
            // 并且把排除掉多少也报出来。
            var gap  = RegionStats(signed, w, h, 0f, k_GapHalfWidthU);
            var side = RegionStats(signed, w, h, k_SideInnerU, k_SideOuterU);
            sb.AppendLine($"  图内分区（有符号，负 = Froxel 更暗）：");
            sb.AppendLine($"    缝里   |u−0.5| < {k_GapHalfWidthU:F2}"
                        + $"        全部 {Signed(gap.medianAll)}"
                        + $"   近层参与 {Signed(gap.medianTouched)}"
                        + $"（{gap.touched}/{gap.total} = {(float)gap.touched / gap.total:P1}）");
            sb.AppendLine($"    板后   {k_SideInnerU:F2} < |u−0.5| < {k_SideOuterU:F2}"
                        + $"   全部 {Signed(side.medianAll)}"
                        + $"   近层参与 {Signed(side.medianTouched)}"
                        + $"（{side.touched}/{side.total} = {(float)side.touched / side.total:P1}）");
            sb.AppendLine($"    差（板后 − 缝里，只看近层参与的像素）= "
                        + $"{Signed(side.medianTouched - gap.medianTouched)}"
                        + "（判据 β 要的就是这个量为负：被遮的那块比亮柱更暗）");

            sb.AppendLine("  ⓘ 这一项**不判**通过失败：它是给判据 β 定分位数与阈值用的标定，"
                        + "而不是判据本身。判据要跑在真实布景上、并且要把差分解成"
                        + "阴影 / 局部灯 / 逐 froxel 相位三项（γ）。");
            sb.AppendLine("  ⓘ 空转只有 12 帧、不到一个 historyTimeConstant，所以地板偏高。"
                        + "偏高的地板让「有分辨力」更难成立，是安全的那一侧；"
                        + "真正的收敛性归第 2 项的判据⑯。");
            return true;
        }

        /// <summary>渲若干帧后读回，返回逐像素的 Rec.709 亮度。</summary>
        static float[] CaptureLuminance(Camera cam, RenderTexture rt, Texture2D tex, int frames)
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
        static string Percentiles(float[] values)
        {
            return $"p50 {Sci(Percentile(values, 0.50f))}"
                 + $"  p90 {Sci(Percentile(values, 0.90f))}"
                 + $"  p99 {Sci(Percentile(values, 0.99f))}"
                 + $"  max {Sci(Percentile(values, 1.00f))}";
        }

        static float Percentile(float[] values, float q)
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
        static RegionStat RegionStats(float[] values, int w, int h, float innerU, float outerU)
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

        struct RegionStat
        {
            public int total;
            public int touched;
            public float medianAll;
            public float medianTouched;
        }

        static float Median(System.Collections.Generic.List<float> v)
        {
            if (v.Count == 0) return float.NaN;
            v.Sort();
            return v[v.Count / 2];
        }

        /// <summary>带正负号的科学计数，符号本身是读数的一部分（判据 β 判的就是符号）。</summary>
        static string Signed(float v)
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
        static void Build(Shader litShader, int layer, float groundLevelWorldY, RenderTexture rt,
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

        static Transform MakeQuad(Transform parent, int layer, Material mat, string name)
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

        /// <summary>
        /// 当前 URP 资产上的 shadow distance。取不到时返回 NaN，**不**回落到一个默认值：
        /// 回落会让报表打印一个看起来合理、但与实际生效值无关的数。
        /// </summary>
        static float ResolveShadowDistance()
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            return asset != null ? asset.shadowDistance : float.NaN;
        }

        static string Fmt(float v) => float.IsNaN(v) ? "n/a" : v.ToString("F6");

        static string Sci(float v) => float.IsNaN(v) ? "n/a" : v.ToString("0.###e+00");
    }
}
