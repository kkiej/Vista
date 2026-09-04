using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista
{
    /// <summary>
    /// 大气模块的接入点。挂到 UniversalRendererData 上即生效。
    ///
    /// 只有一个 RendererFeature 而不是每个效果一个：LUT 是共享资源（天空盒、雾、
    /// aerial perspective、环境光 SH 都读同一批表），分成多个 feature 会让用户能配出
    /// "开了雾但没开大气"这种必然黑屏的组合。质量分级与算法切换（Task #7）也统一在这里。
    /// </summary>
    [DisallowMultipleRendererFeature("Vista Atmosphere")]
    public sealed class VistaAtmosphereFeature : ScriptableRendererFeature
    {
        [SerializeField]
        [Tooltip("星球与大气的物理参数。只影响静态 LUT，改动会触发一次重烘。")]
        VistaAtmosphereParameters m_Parameters = VistaAtmosphereParameters.CreateEarth();

        [SerializeField]
        [Tooltip("世界空间中哪个 Y 值对应星球表面 (m)。通常填场景的海平面高度。")]
        float m_GroundLevelWorldY = 0f;

        [SerializeField]
        [Tooltip("摄影曝光值。15 = 晴天正午（Sunny 16）。整条管线共用这一个曝光，"
               + "改这里等于改整个画面的亮度基准。")]
        [Range(5f, 20f)]
        float m_EV100 = VistaAtmosphereViewData.k_DefaultEV100;

        [SerializeField]
        [Tooltip("Sky-View LUT 分辨率。192×108 是论文推荐值；移动端分级降到 128×72。"
               + "太低会在日落时的地平线上看到横向台阶。")]
        Vector2Int m_SkyViewResolution = new Vector2Int(
            VistaAtmosphereLuts.k_SkyViewWidthDefault,
            VistaAtmosphereLuts.k_SkyViewHeightDefault);

        [SerializeField]
        [Tooltip("Aerial Perspective froxel LUT 设置。控制远景雾感的精度与深度范围。")]
        VistaAerialPerspectiveSettings m_AerialPerspective = new VistaAerialPerspectiveSettings();

        [SerializeField]
        [Tooltip("雾介质设置。默认 Off —— 关态会把雾的 cbuffer 下发成零，逐位等于没有雾。\n"
               + "AerialPerspective 档把雾并进 32³ AP LUT 的 march：无阴影查询、无新纹理、"
               + "无历史，代价是拿不到光柱（那个要等近层 froxel 体）。")]
        VistaFogSettings m_Fog = new VistaFogSettings();

        [SerializeField]
        [Tooltip("天空镜面反射 cubemap 的辐射来源。\n"
               + "SkyViewLut：从 Sky-View LUT 逐 mip GGX 预积分，含地平线的橙红带（PC 默认）。\n"
               + "AmbientSh：从环境光 SH 重建，零 LUT 依赖、采样数 16（移动端分级）。\n"
               + "Off：不产出，镜面反射回落到场景自带的反射探针。")]
        VistaSkyReflectionMode m_SkyReflection = VistaSkyReflectionMode.SkyViewLut;

        [SerializeField]
        [Tooltip("近层体积雾（froxel 体）的分辨率与深度范围。介质参数在上面的 Fog 里 —— "
               + "近层与 AP LUT 共用同一份介质定义。\n"
               + "注意：本节的开关产出注入表与积分表，但**最终画面还没有消费它们**"
               + "（合成在 #25）。要看到这两张表，用本节的 Debug View 档位。")]
        VistaVolumetricFogSettings m_VolumetricFog = new VistaVolumetricFogSettings();

        VistaAtmosphereLuts m_Luts;
        VistaAtmospherePass m_Pass;
        VistaAerialPerspectiveCompositePass m_ApCompositePass;
        VistaFroxelDebugPass m_FroxelDebugPass;

        /// <summary>
        /// 当前生效的大气模块。场景侧的组件（<see cref="VistaTimeOfDay"/>）靠它拿到
        /// 大气参数与曝光值。
        ///
        /// ── 为什么用静态注册，而不是让组件自己存一份参数 ──
        ///
        /// 太阳的直射光色 = lux · T(大气参数) · exposure / π。这三样里有两样住在
        /// RendererFeature 上。若让组件自己也存一份，两份必然漂移，症状是
        /// **天空是一套大气、物体的受光是另一套大气** —— 日落时天空红了但物体偏黄，
        /// 而且没有任何报错。这类"两份真相"的 bug 在 #7 里已经吃过教训。
        ///
        /// 也不走"遍历 URP asset 找 rendererData"：URP 没有公开取 renderer data 列表的 API，
        /// 只能反射私有字段，换版本就断。
        ///
        /// 这套形状不是自创：<c>RenderSettings.sun</c>、<c>UniversalRenderPipeline.asset</c>、
        /// HDRP 的 <c>VolumeManager.instance</c> 都是同一个模式 ——
        /// 「渲染侧的唯一配置源，场景侧只读」。
        ///
        /// 取不到时组件**显式报警**，不静默回落到地球默认值：回落会让"忘挂 feature"
        /// 表现为"光色差一点点"，那是最难查的一类问题。
        /// </summary>
        public static VistaAtmosphereFeature current { get; private set; }

        /// <summary>大气参数。运行时改会在下一帧触发静态 LUT 重烘。</summary>
        public VistaAtmosphereParameters parameters => m_Parameters;

        /// <summary>
        /// 整条管线共用的曝光值 (EV100)。场景侧算平行光强度要用同一个数 ——
        /// 天空走 GPU 的 <c>VISTA_EXPOSURE</c>，平行光走 CPU 的这个，两者必须同源。
        /// </summary>
        public float ev100 => m_EV100;

        /// <summary>世界空间中对应星球表面的 Y 值 (m)。求透射率要把海拔换成半径。</summary>
        public float groundLevelWorldY => m_GroundLevelWorldY;

        /// <summary>AP froxel 设置。改尺寸会在下一帧重新分配 3D 表，其余立即生效。</summary>
        public VistaAerialPerspectiveSettings aerialPerspective => m_AerialPerspective;

        /// <summary>
        /// 雾设置。所有字段都是运行时可改的 uniform（没有 shader keyword，也没有
        /// 依赖它的 GPU 资源），所以改完下一帧就生效，不需要通知任何人。
        /// </summary>
        public VistaFogSettings fog => m_Fog;

        /// <summary>
        /// 近层体积雾设置。与 <see cref="fog"/> 分开的理由见 VistaVolumetricFogSettings 的头注：
        /// 这里是「体积怎么切」，那里是「介质是什么」，换分辨率不该动介质。
        ///
        /// Editor 自检（Window/Vista/Log Volumetric Fog State）靠它读出
        /// <c>enableInjection</c> 的状态并在关着的时候点名 —— 所以它必须是 public。
        /// </summary>
        public VistaVolumetricFogSettings volumetricFog => m_VolumetricFog;

#if UNITY_EDITOR
        /// <summary>
        /// 仅 Editor 自检：近层 froxel 体。用来置 <c>probeRequested</c> 并读回覆盖性探针。
        ///
        /// 不把整个 <c>m_Luts</c> 开出去：那会让场景侧能改到七张大气表的分配口径，
        /// 而那些的脏检查是由本类的参数驱动的 —— 从外面动一下的症状是
        /// 「表被重烘了但参数没变」，在 profiler 上表现为一个无法归因的尖峰。
        /// </summary>
        public VistaFroxelVolume froxelVolume => m_Luts?.froxelVolume;

        /// <summary>
        /// 仅 Editor 自检：近层体的时间重投影状态（#22a）。判据⑭要的三个整数
        /// （帧号、上一帧的捕获帧号、连续有效帧数）与失效原因字符串都在它上面。
        ///
        /// 它挂在 pass 上而不是 <c>m_Luts</c> 上：「上一帧」这个概念只在有渲染循环时
        /// 才存在，而 m_Luts 会被立即模式的自检直接 new 出来用。
        /// </summary>
        public VistaFroxelReprojection froxelReprojection => m_Pass?.reprojection;

        /// <summary>
        /// 仅 Editor 自检：重投影探针里「角色 3 / 角色 4 各派发了几趟」。
        /// 判据⑮的守恒式要拿它乘 32×32×16 去对分支计数的总和。
        ///
        /// 转发而不是把 <c>m_Luts</c> 开出去（同 <see cref="froxelVolume"/> 的理由），
        /// 也不在判据文件里手抄一个 4：抄下来的常数在加第五个位移时不会跟着改，
        /// 那一格会变成一个由「常数陈旧」造成的假失败。
        ///
        /// 取不到 LUT 时返回 **−1** 而不是 0：0 会让守恒式在「探针一趟都没跑」时
        /// 以 0 == 0 全绿。
        /// </summary>
        public int froxelReprojProbeRole3Dispatches => m_Luts != null ? m_Luts.reprojProbeRole3Dispatches : -1;

        /// <summary>见 <see cref="froxelReprojProbeRole3Dispatches"/>。</summary>
        public int froxelReprojProbeRole4Dispatches => m_Luts != null ? m_Luts.reprojProbeRole4Dispatches : -1;

        /// <summary>
        /// 仅 Editor 自检：抖动探针（#22b）派发了几趟。期望 1。
        ///
        /// 它存在的理由是把两种「读数全 0」分开：**压根没派发**（m_FroxelVolume 为 null、
        /// 或那趟 pass 没排进去）与**派发了但核内第一道守卫早退**（抖动幅度为 0）。
        /// 两者的 COUNT 槽都是 0，而前者是布景/接线问题、后者是配置问题 ——
        /// 报表要能一眼分开，否则归因会从「探针坏了」开始查一件其实是「旋钮关着」的事。
        ///
        /// 同上：取不到 LUT 时返回 **−1**，不是 0。
        /// </summary>
        public int froxelJitterProbeDispatches => m_Luts != null ? m_Luts.jitterProbeDispatches : -1;
#endif

        /// <summary>
        /// 反射来源模式。可运行时改 —— Demo 视频要在同一帧里对比 PC 与移动端两条路径。
        /// 切到 Off 之后 cubemap 停止更新但仍留在 RenderSettings 上（内容冻结在最后一帧），
        /// 这是有意的：拔掉引用会让画面在切换瞬间闪一下场景默认反射，A/B 对比时很干扰。
        /// </summary>
        public VistaSkyReflectionMode skyReflection
        {
            get => m_SkyReflection;
            set => m_SkyReflection = value;
        }

        public override void Create()
        {
            // 注册放在最前面，早于下面那个 return。
            // 组件只需要 parameters / ev100 / groundLevelWorldY，这三样都不依赖 compute。
            // 若 compute 缺失就连注册也跳过，故障会从"天空没了"升级成
            // "天空没了 + 平行光还悄悄回落到地球默认值"，多一层混淆。
            current = this;

            var resources = VistaRuntimeResources.Get();
            if (resources == null || resources.atmosphereLutCS == null)
            {
                // 不抛异常：换管线 / 资源还没导入完的中间态会走到这里，
                // 报错弹窗会盖住真正的问题。
                m_Luts?.Dispose();
                m_Luts = null;
                return;
            }

            // Create 会在 shader 重编译后重新调用，旧的 RTHandle 必须先还回去
            m_Luts?.Dispose();
            // 反射核可以为 null（资源没配 / 平台不支持编译）—— 那时 isSkyReflectionValid
            // 为 false，PrepareSkyReflection 返回 Off，天空/AP/环境光全都照常工作。
            // 不在这里检查它，是因为把它并进上面那个 return 会让"反射核缺失"
            // 表现为"整个大气模块不生效"。
            // 体积雾核同理可以为 null。它缺失时 froxelVolume.isValid 为 false，
            // 近层雾整个不生效，雾回落到 AP LUT 那一层 —— 画面上是「有雾、没有光柱」。
            // #19 阶段这个 compute 只被 Editor 自检消费（RenderGraph 接线在 #20），
            // 但资源引用现在就接上：ResourcePath 的自动填充失败是静默的，
            // 而自检里那条「资源容器里的 compute 非 null」的判据正好覆盖它。
            m_Luts = new VistaAtmosphereLuts(resources.atmosphereLutCS, resources.skyReflectionCS,
                                             resources.volumetricFogCS);
            m_Luts.SetSkyViewResolution(m_SkyViewResolution.x, m_SkyViewResolution.y);

            m_Pass ??= new VistaAtmospherePass();

            // 合成 pass 每次 Create 都重建：它持有一个由 shader 生成的材质，
            // 而 Create 正是在 shader 重编译后被重新调用的时机。
            // 材质缺失（资源没配）时 isValid 为 false，AP 的 Fullscreen 模式失效，
            // 大气其余部分照常 —— 与反射核缺失走的是同一套降级逻辑。
            m_ApCompositePass?.Dispose();
            m_ApCompositePass = new VistaAerialPerspectiveCompositePass(
                resources.aerialPerspectiveCompositeShader);

            // froxel 表的调试视图（#21）。与合成 pass 同一条重建理由（持有生成材质）。
            // shader 缺失时 isValid 为 false，非 Off 档静默失效 —— 这条降级在
            // VistaRuntimeResources.froxelDebugShader 的注释里写明了为什么可以接受：
            // 它是纯诊断工具，缺席不影响任何出货路径。
            m_FroxelDebugPass?.Dispose();
            m_FroxelDebugPass = new VistaFroxelDebugPass(resources.froxelDebugShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_Luts == null || !m_Luts.isValid || m_Pass == null)
                return;

            // Preview 相机（材质球缩略图、Inspector 预览）不需要物理天空，
            // 而且它们每帧可能有十几个，逐个刷 SkyView 是纯浪费。
            // Reflection 相机要留着：反射探针烘天空靠它。
            var cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview)
                return;

            m_Luts.SetSkyViewResolution(m_SkyViewResolution.x, m_SkyViewResolution.y);
            m_Pass.Setup(m_Luts, m_Parameters, m_AerialPerspective, m_Fog, m_SkyReflection,
                         m_GroundLevelWorldY, m_EV100, m_VolumetricFog);
            renderer.EnqueuePass(m_Pass);

            // 全屏合成（变体 A）。三个条件都必须在**排入之前**判掉，而不是排进去再在
            // RecordRenderGraph 里 return：这个 pass 声明了 ConfigureInput(Depth)，
            // 光是排入就会让 URP 安排一次深度拷贝。AP 关掉的帧里为一个什么都不做的
            // pass 拷一张全屏深度，是实打实的浪费。
            //
            // isAerialPerspectiveValid 与大气 pass 里那个 apEnabled 必须同真同假 ——
            // 契约写在 VistaAtmosphereLuts.PrepareAerialPerspective 的注释里。
            if (m_AerialPerspective.compositeMode == VistaAerialPerspectiveSettings.CompositeMode.Fullscreen
                && m_Luts.isAerialPerspectiveValid
                && m_ApCompositePass != null && m_ApCompositePass.isValid)
            {
                renderer.EnqueuePass(m_ApCompositePass);
            }

            // froxel 调试视图（#21）。只在选了非 Off 档时排入 —— 它同样声明了
            // ConfigureInput(Depth)，排入就要一次深度拷贝，Off 档不该付这个钱。
            //
            // 相机类型必须与 VistaAtmospherePass 里那个 froxelEnabled 的门**逐字一致**：
            // 那边不为反射探针相机分配 froxel 体（探针不需要近层雾，而且它的分辨率
            // 与主相机不同会导致每帧重分配三张 3D 表）。若这里不同步，反射探针相机
            // 每帧都会走进「表不存在」那条警告分支，而主相机帧又把去重串清掉 ——
            // 症状是每秒 60 条警告，且内容指向一个根本没配错的开关。
            if (m_FroxelDebugPass != null && m_FroxelDebugPass.isValid
                && m_VolumetricFog.debugView != FroxelDebugView.Off
                && (cameraType == CameraType.Game || cameraType == CameraType.SceneView))
            {
                m_FroxelDebugPass.Setup(m_VolumetricFog.debugView,
                                        m_VolumetricFog.debugSlice,
                                        m_VolumetricFog.debugGain);
                renderer.EnqueuePass(m_FroxelDebugPass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            // 只在"当前就是自己"时摘牌。判等不能省：Create 会在 shader 重编译后重新调用，
            // 而 Unity 的调用顺序是「新实例 Create → 旧实例 Dispose」，
            // 无条件清空会把刚注册好的新实例抹掉，症状是重编译后平行光突然失去大气参数。
            if (current == this)
                current = null;

            // 先清读回、再放 buffer：VistaSkyAmbientProbe.Dispose 里会 WaitForCompletion，
            // 反过来就是让在飞的读回从已释放的显存里搬数据。
            m_Pass?.ambientProbe?.Dispose();
            // 反射那条出口的场景全局状态 + 从保存守卫上摘下来。必须在 m_Luts.Dispose 之前：
            // customReflectionTexture 指着 m_Luts 里那张 cube RT，先释放 RT 就等于
            // 把一个已销毁的 Texture 留在 RenderSettings 里，Editor 下表现为
            // 反射变黑 + 偶发的 "Texture has been destroyed" 报错。
            m_Pass?.Teardown();
            m_ApCompositePass?.Dispose();
            m_ApCompositePass = null;
            m_FroxelDebugPass?.Dispose();
            m_FroxelDebugPass = null;
            m_Luts?.Dispose();
            m_Luts = null;

            // 蓝噪声的解析缓存也一起丢掉（#22b）。放在这里而不是另开一个
            // RenderPipelineManager.activeRenderPipelineDisposed 钩子：这一趟 Dispose
            // 正好覆盖那些让缓存失效的事件 —— 换 URP 资产、改全局设置、shader 重编译。
            // 缓存的内容是 GetRenderPipelineSettings 的结果（绑在当前
            // RenderPipelineGlobalSettings 上）与一个包着它的 RTHandle；不丢的症状是
            // 换过全局设置资产之后句柄指向一张可能已被卸载的 Texture2D。
            //
            // 与「新实例 Create → 旧实例 Dispose」的顺序无冲突：Invalidate 只丢缓存，
            // 下一次访问会重新解析；而 import 发生在每帧的 RecordRenderGraph 里，
            // 不在 Create 里，所以这一刻不会有人正持着那个 handle。
            VistaBlueNoise.Invalidate();
        }
    }
}
