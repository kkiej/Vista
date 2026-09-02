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

        VistaAtmosphereLuts m_Luts;
        VistaAtmospherePass m_Pass;
        VistaAerialPerspectiveCompositePass m_ApCompositePass;

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
                         m_GroundLevelWorldY, m_EV100);
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
            m_Luts?.Dispose();
            m_Luts = null;
        }
    }
}
