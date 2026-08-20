using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Vista
{
    /// <summary>
    /// 大气 LUT 的逐帧调度。RenderGraph-only（URP 17 已移除 compatibility mode 的新增支持）。
    ///
    /// 为什么拆成多个 pass 而不是一个：
    ///   Transmittance 以 UAV 写出，MultiScattering 要把它当 SRV 读；SkyView / AP 又要读前两张。
    ///   RenderGraph **只在 pass 边界插入资源状态转换**，一个 pass 内同一资源只有一个状态。
    ///   写在同一个 pass 里，在 D3D12 / Vulkan 上就是缺 barrier 的未定义行为（在 NV 驱动上
    ///   常常"看起来是对的"，这类 bug 换台 AMD 机器才炸，非常难查）。
    ///   立即模式（Editor 预览）没有这个问题：原生 CommandBuffer 的状态转换由图形层自动插。
    ///
    /// 前两个 pass 只在大气参数变化时排入 —— 静态表逐帧重烘是纯浪费（MS LUT 是
    /// 32×32 个线程组 × 64 方向 × 20 步的 raymarch，比 SkyView 还贵）。
    ///
    /// SkyView 与 AP 之间**没有依赖**（都只读静态表），合成一个 pass 在状态转换上是合法的。
    /// 仍然分开，是因为质量分级要能单独关掉 AP（移动端低档只留天空盒），
    /// 而"能不能少排一个 pass"在这个量级上不值一提。
    /// </summary>
    public sealed class VistaAtmospherePass : ScriptableRenderPass, IVistaRenderSettingsClient
    {
        /// <summary>
        /// 各 pass 共用一份 pass data。字段各 pass 只填自己声明过的那几个 —— 没填的
        /// 是默认的空 TextureHandle，而没声明就不会被 dispatch 用到，所以是安全的。
        /// 分成多个类只会让"哪个 pass 读哪张表"更难一眼看清。
        /// </summary>
        class LutPassData
        {
            public VistaAtmosphereLuts luts;
            public VistaAtmosphereViewData view;
            public VistaAerialPerspectiveSettings apSettings;
            public VistaSkyReflectionMode reflectionMode;
            /// <summary>见 SkyView pass 里填这个字段处的注释。</summary>
            public Vector4 apConsumer;
            /// <summary>同上，同一条「每帧无条件下发」的理由。</summary>
            public Vector4 sunTransmittanceRef;
            public TextureHandle transmittance;
            public TextureHandle multiScattering;
            public TextureHandle skyView;
            public TextureHandle apScatter;
            public TextureHandle apTransmittance;
            public TextureHandle skyReflection;
            public TextureHandle skyReflectionArray;
            public BufferHandle skyAmbientSh;
        }

        /// <summary>
        /// 把 pass data 里的 handle 打包给 dispatcher。抽成一个方法而不是在六个 render func
        /// 里各写一遍字段赋值：这些 handle 的**集合**是全局的（每个 pass 只声明自己那几个，
        /// 其余留 default），所以每处都必须是完全一样的一行。抄六遍的东西迟早有一处漏掉新字段，
        /// 而漏掉的症状是那张表在某个 pass 里绑成 null。
        /// </summary>
        static VistaLutHandles Handles(LutPassData d) => new VistaLutHandles
        {
            transmittance   = d.transmittance,
            multiScattering = d.multiScattering,
            skyView         = d.skyView,
            apScatter       = d.apScatter,
            apTransmittance = d.apTransmittance,
            skyReflection   = d.skyReflection,
            skyReflectionArray = d.skyReflectionArray,
            skyAmbientSh    = d.skyAmbientSh,
        };

        VistaAtmosphereLuts m_Luts;
        VistaAtmosphereParameters m_Parameters;
        VistaAerialPerspectiveSettings m_ApSettings;
        VistaSkyReflectionMode m_ReflectionMode = VistaSkyReflectionMode.SkyViewLut;
        float m_GroundLevelWorldY;
        float m_EV100;

        /// <summary>
        /// CPU 侧的环境光出口。pass 持有它而不是 feature：它的驱动时机就是记录期，
        /// 放在 feature 上还得再传一次相机类型。生命周期跟着 pass（feature 负责 Dispose）。
        /// </summary>
        readonly VistaSkyAmbientProbe m_AmbientProbe = new VistaSkyAmbientProbe();

        /// <summary>供 <see cref="VistaAtmosphereFeature"/> 在 Dispose 时清理在飞的读回请求。</summary>
        public VistaSkyAmbientProbe ambientProbe => m_AmbientProbe;

        // 反射那条出口改过的场景全局状态。理由与环境光那半完全一致，见
        // VistaSkyAmbientProbe 里字段处的说明（含"为什么要记场景、以及为什么存
        // Scene 而不是 scene.handle"）。
        bool m_HasSavedReflection;
        Scene m_SavedReflectionScene;
        Texture m_SavedReflectionTexture;
        DefaultReflectionMode m_SavedReflectionMode;
        float m_SavedReflectionIntensity;

        public VistaAtmospherePass()
        {
            // 越早越好：LUT 不依赖任何屏幕空间资源，而下游（天空盒、雾、不透明物的
            // aerial perspective、SH 投影）分布在整条管线上。放在 prepass 之前，
            // 所有下游都能无条件拿到当帧的表。
            renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses;
            // 本 pass 不碰相机颜色/深度，声明出来避免 URP 为它准备 RT
            requiresIntermediateTexture = false;

#if UNITY_EDITOR
            // 在构造期注册、而不是等到第一次写 RenderSettings：注册本身极便宜，
            // 而"先写了一帧、还没注册、用户正好在这一帧保存"这条缝隙没有必要留着。
            VistaRenderSettingsGuard.Register(this);
#endif
        }

        public void Setup(VistaAtmosphereLuts luts, VistaAtmosphereParameters parameters,
                          VistaAerialPerspectiveSettings apSettings,
                          VistaSkyReflectionMode reflectionMode,
                          float groundLevelWorldY, float ev100)
        {
            m_Luts = luts;
            m_Parameters = parameters;
            m_ApSettings = apSettings;
            m_ReflectionMode = reflectionMode;
            m_GroundLevelWorldY = groundLevelWorldY;
            m_EV100 = ev100;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_Luts == null || !m_Luts.isValid || m_Parameters == null)
                return;

            var cameraData = frameData.Get<UniversalCameraData>();
            var lightData  = frameData.Get<UniversalLightData>();

            // 记录期：分配 + 推大气 cbuffer + 静态表脏检查
            bool staticDirty = m_Luts.PrepareLuts(m_Parameters);
            bool apEnabled = m_ApSettings != null && m_Luts.PrepareAerialPerspective(m_ApSettings);
            bool shEnabled = m_Luts.PrepareSkyAmbientSh();
            // 解析后的模式，不是请求的模式：SH 不可用时它会退到 LUT（见 PrepareSkyReflection）。
            var reflectionMode = m_Luts.PrepareSkyReflection(m_ReflectionMode);

            var view = VistaAtmosphereViewData.Create(
                m_Parameters,
                cameraData.camera.transform.position,
                m_GroundLevelWorldY,
                GetSunDirection(lightData),
                m_EV100);

            // 视锥四角只有 AP 需要。Create 里已经填了一个 60°/16:9 的兜底，
            // 这里用真实相机覆盖 —— 反射探针那样的立方体面相机不走 AP，兜底值不会被用到。
            if (apEnabled)
                view.SetFrustumRays(cameraData.camera);

            var transmittance   = renderGraph.ImportTexture(m_Luts.transmittanceLut);
            var multiScattering = renderGraph.ImportTexture(m_Luts.multiScatteringLut);
            var skyView         = renderGraph.ImportTexture(m_Luts.skyViewLut);
            var apScatter       = apEnabled ? renderGraph.ImportTexture(m_Luts.apScatterLut) : default;
            var apTransmittance = apEnabled ? renderGraph.ImportTexture(m_Luts.apTransmittanceLut) : default;
            var skyReflection   = reflectionMode != VistaSkyReflectionMode.Off
                ? renderGraph.ImportTexture(m_Luts.skyReflectionCube) : default;
            var skyReflectionArray = reflectionMode != VistaSkyReflectionMode.Off
                ? renderGraph.ImportTexture(m_Luts.skyReflectionArray) : default;
            // ImportBuffer 而不是把持久 GraphicsBuffer 直接交给 dispatcher：
            // ComputeCommandBuffer.SetComputeBufferParam 有直收 GraphicsBuffer 的重载，
            // 所以那样写**能编译能跑**，但图不知道这个 pass 碰了它，不会插 barrier。
            // 走 import + UseBuffer 才让依赖对图可见。
            var skyAmbientSh    = shEnabled ? renderGraph.ImportBuffer(m_Luts.skyAmbientShBuffer) : default;

            // CPU 侧出口的驱动。放在记录期最前面而不是 SH pass 的 execute 里：
            // 读回请求是 CPU 侧 API，与图无关；而且这样它拿到的必然是"上一帧已完成"的内容，
            // 时序不依赖图的提交点。
            if (shEnabled)
                m_AmbientProbe.Update(m_Luts.skyAmbientShBuffer, cameraData.cameraType, view.exposure);

            // 反射 cubemap 与 RenderSettings 的挂接。**只挂一次引用**，之后每帧只改内容 ——
            // 与环境光 SH 那条链路不同，这里没有任何逐帧的 CPU 开销，也不需要读回。
            // 放在记录期而不是分配处：分配在 PrepareSkyReflection 里，那儿不知道相机类型，
            // 而反射探针烘焙相机不该顺手改全局 RenderSettings。
            if (reflectionMode != VistaSkyReflectionMode.Off
                && cameraData.cameraType is CameraType.Game or CameraType.SceneView)
                BindReflectionToRenderSettings(view.exposure);

            if (staticDirty)
            {
                using (var builder = renderGraph.AddComputePass<LutPassData>(
                           "Vista Transmittance LUT", out var data))
                {
                    data.luts = m_Luts;
                    data.transmittance = transmittance;
                    builder.UseTexture(transmittance, AccessFlags.Write);
                    // 这张表的消费者大多不在图里（天空盒 shader 通过全局纹理读），
                    // 图看不到依赖就会把 pass 剪掉。
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((LutPassData d, ComputeGraphContext ctx) =>
                        d.luts.RenderTransmittanceLut(
                            new VistaGraphLutDispatcher(ctx.cmd, Handles(d))));
                }

                using (var builder = renderGraph.AddComputePass<LutPassData>(
                           "Vista Multi-Scattering LUT", out var data))
                {
                    data.luts = m_Luts;
                    data.transmittance = transmittance;
                    data.multiScattering = multiScattering;
                    builder.UseTexture(transmittance, AccessFlags.Read);
                    builder.UseTexture(multiScattering, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((LutPassData d, ComputeGraphContext ctx) =>
                        d.luts.RenderMultiScatteringLut(
                            new VistaGraphLutDispatcher(ctx.cmd, Handles(d))));
                }
            }

            using (var builder = renderGraph.AddComputePass<LutPassData>(
                       "Vista Sky-View LUT", out var data))
            {
                data.luts = m_Luts;
                data.view = view;
                data.transmittance = transmittance;
                data.multiScattering = multiScattering;
                data.skyView = skyView;

                // 变体 B（Vista 材质自己合成 AP）的开关，**每帧无条件下发**。
                //
                // 为什么挂在这个 pass 上：它是唯一一个「一定存在」的逐帧 pass。
                // AP 关掉的帧里 AP pass 根本不排入（记录期就 return 了），
                // 而材质里那个 uniform 会留着上一帧的 1，去采一张已经释放的 3D 表。
                // 「关掉某功能后画面才坏」是最难反查的一类失效，所以这一行必须
                // 走每帧必跑的路径，不能跟着 AP 一起消失。
                //
                // apEnabled 由这里传进设置对象：设置对象只知道用户「想要」哪种模式，
                // 不知道核缺失 / 分级降档这类运行时结果。
                data.apConsumer = m_ApSettings != null
                    ? m_ApSettings.PackedConsumer(apEnabled)
                    : Vector4.zero;

                // 逐像素太阳透射率的分母，同样**每帧无条件下发**（理由同上一段）。
                //
                // 值来自场景侧的 VistaTimeOfDay —— 它算出 T_ref 并写进 Light.color，
                // 这里读的就是**同一次** Evaluate 的结果，不重算。理由是着色端要靠
                // 「分母与 Light.color 里那个因子是同一个 float」把 CPU 那份 T 整项约掉，
                // 最终只留下 GPU LUT 那一份（详见 AtmosphereDef.hlsl 里
                // _VistaSunTransmittanceRef 的注释）。重算一份就约不掉了。
                //
                // 组件不在 / 没在驱动光色时给 (1,1,1,0)，比值恒为 1，整条退化成 no-op。
                data.sunTransmittanceRef = VistaTimeOfDay.ResolveSunTransmittanceRef();

                builder.UseTexture(transmittance, AccessFlags.Read);
                builder.UseTexture(multiScattering, AccessFlags.Read);
                builder.UseTexture(skyView, AccessFlags.Write);

                // 三张表统一在这里对外发布，而不是各自在产出 pass 里发布：
                // 静态表的 pass 在参数不变的帧里根本不存在，那些帧也必须有全局绑定。
                // 一个 pass 可以为它**读**的资源设全局，正好用上。
                builder.SetGlobalTextureAfterPass(transmittance,   VistaShaderIDs._VistaTransmittanceLut);
                builder.SetGlobalTextureAfterPass(multiScattering, VistaShaderIDs._VistaMultiScatteringLut);
                builder.SetGlobalTextureAfterPass(skyView,         VistaShaderIDs._VistaSkyViewLut);

                // view.Bind 在 execute 里写全局 cbuffer，必须显式申报
                builder.AllowGlobalStateModification(true);
                // URP 自带的 DrawSkyboxPass 无法声明对 SkyView 的读取（它不认识我们的资源），
                // 所以图里没有任何消费者 -> 必须关掉剪枝，否则整个大气模块被静默剪掉，
                // 症状是"天空一片黑但 Frame Debugger 里连 pass 都找不到"。
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((LutPassData d, ComputeGraphContext ctx) =>
                {
                    d.luts.RenderSkyViewLut(
                        new VistaGraphLutDispatcher(ctx.cmd, Handles(d)), d.view);
                    ctx.cmd.SetGlobalVector(VistaShaderIDs._VistaApConsumer, d.apConsumer);
                    ctx.cmd.SetGlobalVector(
                        VistaShaderIDs._VistaSunTransmittanceRef, d.sunTransmittanceRef);
                });
            }

            // 排在 SkyView 之后：这个核**采样** SkyView（SRV），而 SkyView pass 里它是 UAV。
            // 同一 pass 内一个资源只有一个状态，合进去就是缺 barrier 的未定义行为。
            // 与 AP 之间没有依赖，图可以自行并行/重排。
            if (shEnabled)
            {
                using (var builder = renderGraph.AddComputePass<LutPassData>(
                           "Vista Sky Ambient SH", out var data))
                {
                    data.luts = m_Luts;
                    data.view = view;
                    data.skyView = skyView;
                    data.skyAmbientSh = skyAmbientSh;

                    builder.UseTexture(skyView, AccessFlags.Read);
                    // UseBuffer 而不是 UseBufferRandomAccess：后者额外调
                    // SetRandomWriteResourceRaw，那是给"光栅 pass 里用 u# 寄存器写 UAV"
                    // 准备的（对应 Graphics.SetRandomWriteTarget）。我们是 compute，
                    // buffer 按名字绑到 kernel 上，多绑一次随机写目标只会白占一个 UAV 槽。
                    builder.UseBuffer(skyAmbientSh, AccessFlags.Write);

                    // 全局发布只能在 execute 里做：RenderGraph 有 SetGlobalTextureAfterPass，
                    // 但**没有** buffer 的对应物（core 包里 SetGlobalBuffer 只存在于
                    // CommandBuffer 包装层）。所以这里手动绑，靠 AllowGlobalStateModification
                    // 通过 ComputeCommandBuffer 的 ThrowIfGlobalStateNotAllowed 检查。
                    builder.AllowGlobalStateModification(true);
                    // 消费者（雾、PRT relight、以及 CPU 读回）都不在图里。
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((LutPassData d, ComputeGraphContext ctx) =>
                    {
                        d.luts.RenderSkyAmbientSh(
                            new VistaGraphLutDispatcher(ctx.cmd, Handles(d)), d.view);
                        // BufferHandle -> GraphicsBuffer 的隐式转换在 execute 阶段查
                        // RenderGraphResourceRegistry.current，这里正好在 execute 里。
                        ctx.cmd.SetGlobalBuffer(VistaShaderIDs._VistaSkyAmbientSh,
                                                (GraphicsBuffer)d.skyAmbientSh);
                    });
                }
            }

            // 第六个 pass：反射 cubemap。同样排在 SkyView 之后（LUT 模式采它），
            // 且在 SH pass 之后（SH 模式读那份 buffer）。两个模式各只依赖其中一条 ——
            // 所以下面的 UseTexture / UseBuffer 是按模式声明的，不是无条件都声明：
            // 无条件声明会让 LUT 模式在 SH 核缺失时拿到一个 default BufferHandle，
            // 而 UseBuffer 对 default handle 会抛。
            //
            // 分两个 pass（compute 积分 + unsafe 拷贝）而不是一个：CopyTexture 只存在于
            // IUnsafeCommandBuffer，ComputeCommandBuffer 上没有。这不是绕路 ——
            // 中转纹理与 cube 之间本来就是"写完再读"，跨 pass 正好让图去插那道 barrier。
            if (reflectionMode != VistaSkyReflectionMode.Off)
            {
                using (var builder = renderGraph.AddComputePass<LutPassData>(
                           "Vista Sky Reflection", out var data))
                {
                    data.luts = m_Luts;
                    data.view = view;
                    data.reflectionMode = reflectionMode;
                    data.skyView = skyView;
                    data.skyReflectionArray = skyReflectionArray;

                    if (reflectionMode == VistaSkyReflectionMode.AmbientSh)
                    {
                        data.skyAmbientSh = skyAmbientSh;
                        builder.UseBuffer(skyAmbientSh, AccessFlags.Read);
                    }
                    else
                    {
                        builder.UseTexture(skyView, AccessFlags.Read);
                    }

                    // 七级 mip 一次声明。RenderGraph 跟踪的是整张资源的状态，
                    // 而这张图全程只有 UAV 一个状态（每级都独立地从源积分，级间无依赖）——
                    // 这正是"不做渐进预滤波"换来的东西：七趟 dispatch 挤在一个 pass 里，
                    // 零 barrier。见 SkyReflection.compute 的头注。
                    builder.UseTexture(skyReflectionArray, AccessFlags.Write);

                    // view.Bind + _VistaSkyReflectionParams 都是全局
                    builder.AllowGlobalStateModification(true);
                    // 消费者是 unity_SpecCube0，图里看不见。
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((LutPassData d, ComputeGraphContext ctx) =>
                        d.luts.RenderSkyReflection(
                            new VistaGraphLutDispatcher(ctx.cmd, Handles(d)), d.view, d.reflectionMode));
                }

                using (var builder = renderGraph.AddUnsafePass<LutPassData>(
                           "Vista Sky Reflection Copy", out var data))
                {
                    data.luts = m_Luts;

                    builder.UseTexture(skyReflectionArray, AccessFlags.Read);
                    builder.UseTexture(skyReflection, AccessFlags.Write);

                    // 全局纹理主要给自定义 shader 用；URP 的标准材质走的是
                    // unity_SpecCube0，那条路由 RenderSettings.customReflectionTexture 接。
                    // 挂在**拷贝**之后而不是积分之后：积分完 cube 里还是上一帧的内容。
                    builder.SetGlobalTextureAfterPass(skyReflection, VistaShaderIDs._VistaSkyReflection);
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);

                    // 拷贝走的是 LUT 自己持有的持久 RTHandle，而不是从 handle 解析回资源。
                    // 这是安全的**因为**两张图都在上面 UseTexture 声明过 —— 图知道这个 pass
                    // 读写了它们，barrier 与执行顺序都是对的；被绕过的只有 handle→资源
                    // 那一步查表，而对 ImportTexture 进来的资源那一步本来就是恒等映射。
                    builder.SetRenderFunc((LutPassData d, UnsafeGraphContext ctx) =>
                        d.luts.CopySkyReflectionToCube(
                            CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd)));
                }
            }

            if (!apEnabled)
                return;

            using (var builder = renderGraph.AddComputePass<LutPassData>(
                       "Vista Aerial Perspective LUT", out var data))
            {
                data.luts = m_Luts;
                data.view = view;
                data.apSettings = m_ApSettings;
                data.transmittance = transmittance;
                data.multiScattering = multiScattering;
                data.apScatter = apScatter;
                data.apTransmittance = apTransmittance;

                builder.UseTexture(transmittance, AccessFlags.Read);
                builder.UseTexture(multiScattering, AccessFlags.Read);
                builder.UseTexture(apScatter, AccessFlags.Write);
                builder.UseTexture(apTransmittance, AccessFlags.Write);

                // AP 的两张表在这里发布。与静态表不同，它们**每帧都由本 pass 产出**，
                // 所以不存在"pass 不在但要有绑定"的问题，就地发布即可。
                // AP 不可用时（核缺失 / 低档分级）这两个全局不会被写 —— 下游必须靠
                // 关键字或开关判断，不能默认"全局一定有效"。见 Step 1 的合成。
                builder.SetGlobalTextureAfterPass(apScatter,       VistaShaderIDs._VistaApScatterLut);
                builder.SetGlobalTextureAfterPass(apTransmittance, VistaShaderIDs._VistaApTransmittanceLut);

                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((LutPassData d, ComputeGraphContext ctx) =>
                    d.luts.RenderAerialPerspectiveLut(
                        new VistaGraphLutDispatcher(ctx.cmd, Handles(d)), d.view, d.apSettings));
            }
        }

        /// <summary>
        /// 把反射 cubemap 接到 <c>RenderSettings.customReflectionTexture</c>，让 URP 的
        /// <c>GlossyEnvironmentReflection</c> 把它当 <c>unity_SpecCube0</c> 采 ——
        /// 不需要改任何材质 shader，这是选"产出真 cubemap"而不是"给材质塞一个自定义
        /// 全局 + 改 shader"的全部理由。
        ///
        /// 只在引用变化时写。RenderSettings 的赋值是**场景状态**，逐帧无条件写在 Editor 里
        /// 会反复触发场景比较。（原先这里写的是"实测环境光那几个属性不会置脏"——
        /// 那句是错的，Log Ambient Probe State 实测写 ambientProbe 会让场景 isDirty=true。
        /// 现在不依赖任何"不会置脏"的假设，一律走保存/还原。）
        /// 内容更新走的是 RT 本身，与这里无关。
        /// </summary>
        void BindReflectionToRenderSettings(float exposure)
        {
            var cube = m_Luts.skyReflectionCube;
            if (cube == null || cube.rt == null) return;

            var scene = SceneManager.GetActiveScene();
            if (!m_HasSavedReflection || m_SavedReflectionScene != scene)
            {
                m_SavedReflectionScene   = scene;
                m_SavedReflectionTexture = RenderSettings.customReflectionTexture;
                m_SavedReflectionMode    = RenderSettings.defaultReflectionMode;
                m_SavedReflectionIntensity = RenderSettings.reflectionIntensity;
                m_HasSavedReflection = true;
            }

            if (!ReferenceEquals(RenderSettings.customReflectionTexture, cube.rt))
                RenderSettings.customReflectionTexture = cube.rt;
            // 必须同时切 Custom：留在 Skybox 模式下，引擎会去烘 RenderSettings.skybox
            // 那张材质的反射探针，我们这张图根本不会被采 —— 症状是"反射看着对，
            // 但完全不跟太阳走"，而且没有任何报错。
            if (RenderSettings.defaultReflectionMode != DefaultReflectionMode.Custom)
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;

            // cubemap 里存的是**绝对光度量**（与自检的对账口径一致，不能改），
            // 而采它的是 URP 的 GlossyEnvironmentReflection —— 不是 Vista 的 shader，
            // 没有曝光级。reflectionIntensity 是引擎给 custom reflection 留的唯一乘子，
            // 曝光就挂在这里。
            //
            // 不把曝光烘进 cubemap 的理由和 SH 那条一样：烘进去之后，反射自检的
            // round-trip / 均值恒等式全部会跟着 EV100 漂，那些判据就废了。
            if (RenderSettings.reflectionIntensity != exposure)
                RenderSettings.reflectionIntensity = exposure;
        }

        /// <summary>
        /// 还原被本 pass 与环境光出口改过的场景全局状态。
        /// 两个调用方：feature 的 <c>Dispose</c>（经 <see cref="Teardown"/>），
        /// 以及场景保存前的守卫（见 <c>VistaRenderSettingsGuard</c>）。
        /// 调用后 <c>m_HasSavedReflection</c> 归零，下一帧会重新扣一份原值再写 ——
        /// 所以守卫可以反复调它。
        /// </summary>
        public void RestoreRenderSettings()
        {
            if (m_HasSavedReflection && SceneManager.GetActiveScene() == m_SavedReflectionScene)
            {
                RenderSettings.customReflectionTexture = m_SavedReflectionTexture;
                RenderSettings.defaultReflectionMode   = m_SavedReflectionMode;
                RenderSettings.reflectionIntensity     = m_SavedReflectionIntensity;
            }
            m_HasSavedReflection = false;

            m_AmbientProbe.RestoreRenderSettings();
        }

        /// <summary>
        /// 只丢基线、不写回。见 <see cref="IVistaRenderSettingsClient"/>。
        ///
        /// 与 <see cref="RestoreRenderSettings"/> 的差别只在"要不要写回"，但那一点决定了
        /// 复位工具能否生效：复位把实时值改干净之后，若走还原路径就等于把脏基线又写回去，
        /// 于是"复位 + 保存"落盘的仍然是 Custom。
        /// </summary>
        public void ForgetRenderSettingsBaseline()
        {
            m_HasSavedReflection = false;
            m_AmbientProbe.ForgetRenderSettingsBaseline();
        }

        /// <summary>
        /// 永久退场：还原状态并从保存守卫上摘下来。
        /// 与 <see cref="RestoreRenderSettings"/> 分开是因为守卫**不能**顺手摘掉自己 ——
        /// 摘了之后这个 pass 后续的写入就再也不会在保存前被还原，
        /// 表现为"第一次 Ctrl+S 是干净的，之后每次都把 Custom 存进去"。
        /// </summary>
        public void Teardown()
        {
            RestoreRenderSettings();
#if UNITY_EDITOR
            VistaRenderSettingsGuard.Unregister(this);
#endif
        }

        /// <summary>
        /// 取指向太阳的世界方向。
        /// 优先主平行光；没有主光时退回 <c>RenderSettings.sun</c>；都没有就给一个 45° 兜底，
        /// 免得 LUT 参数化落在退化情形上（太阳正贴地平线时 Mie 前向峰会顶到 fp16 上限）。
        /// </summary>
        static Vector3 GetSunDirection(UniversalLightData lightData)
        {
            int idx = lightData.mainLightIndex;
            if (idx >= 0 && idx < lightData.visibleLights.Length)
            {
                // 用矩阵列而不是 light.transform：VisibleLight.light 在某些剔除路径下为 null。
                // +Z 是光的传播方向，取负得到"指向太阳"。
                Vector3 forward = lightData.visibleLights[idx].localToWorldMatrix.GetColumn(2);
                if (forward.sqrMagnitude > 1e-8f)
                    return -forward.normalized;
            }

            var sun = RenderSettings.sun;
            if (sun != null)
                return -sun.transform.forward;

            return new Vector3(0f, 0.7071f, 0.7071f);
        }
    }
}
