using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

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
    public sealed class VistaAtmospherePass : ScriptableRenderPass
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
            public TextureHandle transmittance;
            public TextureHandle multiScattering;
            public TextureHandle skyView;
            public TextureHandle apScatter;
            public TextureHandle apTransmittance;
            public BufferHandle skyAmbientSh;
        }

        VistaAtmosphereLuts m_Luts;
        VistaAtmosphereParameters m_Parameters;
        VistaAerialPerspectiveSettings m_ApSettings;
        float m_GroundLevelWorldY;
        float m_EV100;

        /// <summary>
        /// CPU 侧的环境光出口。pass 持有它而不是 feature：它的驱动时机就是记录期，
        /// 放在 feature 上还得再传一次相机类型。生命周期跟着 pass（feature 负责 Dispose）。
        /// </summary>
        readonly VistaSkyAmbientProbe m_AmbientProbe = new VistaSkyAmbientProbe();

        /// <summary>供 <see cref="VistaAtmosphereFeature"/> 在 Dispose 时清理在飞的读回请求。</summary>
        public VistaSkyAmbientProbe ambientProbe => m_AmbientProbe;

        public VistaAtmospherePass()
        {
            // 越早越好：LUT 不依赖任何屏幕空间资源，而下游（天空盒、雾、不透明物的
            // aerial perspective、SH 投影）分布在整条管线上。放在 prepass 之前，
            // 所有下游都能无条件拿到当帧的表。
            renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses;
            // 本 pass 不碰相机颜色/深度，声明出来避免 URP 为它准备 RT
            requiresIntermediateTexture = false;
        }

        public void Setup(VistaAtmosphereLuts luts, VistaAtmosphereParameters parameters,
                          VistaAerialPerspectiveSettings apSettings,
                          float groundLevelWorldY, float ev100)
        {
            m_Luts = luts;
            m_Parameters = parameters;
            m_ApSettings = apSettings;
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
            // ImportBuffer 而不是把持久 GraphicsBuffer 直接交给 dispatcher：
            // ComputeCommandBuffer.SetComputeBufferParam 有直收 GraphicsBuffer 的重载，
            // 所以那样写**能编译能跑**，但图不知道这个 pass 碰了它，不会插 barrier。
            // 走 import + UseBuffer 才让依赖对图可见。
            var skyAmbientSh    = shEnabled ? renderGraph.ImportBuffer(m_Luts.skyAmbientShBuffer) : default;

            // CPU 侧出口的驱动。放在记录期最前面而不是 SH pass 的 execute 里：
            // 读回请求是 CPU 侧 API，与图无关；而且这样它拿到的必然是"上一帧已完成"的内容，
            // 时序不依赖图的提交点。
            if (shEnabled)
                m_AmbientProbe.Update(m_Luts.skyAmbientShBuffer, cameraData.cameraType);

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
                        d.luts.RenderTransmittanceLut(new VistaGraphLutDispatcher(
                            ctx.cmd, d.transmittance, d.multiScattering, d.skyView,
                            d.apScatter, d.apTransmittance, d.skyAmbientSh)));
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
                        d.luts.RenderMultiScatteringLut(new VistaGraphLutDispatcher(
                            ctx.cmd, d.transmittance, d.multiScattering, d.skyView,
                            d.apScatter, d.apTransmittance, d.skyAmbientSh)));
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
                    d.luts.RenderSkyViewLut(new VistaGraphLutDispatcher(
                        ctx.cmd, d.transmittance, d.multiScattering, d.skyView,
                        d.apScatter, d.apTransmittance, d.skyAmbientSh), d.view));
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
                        d.luts.RenderSkyAmbientSh(new VistaGraphLutDispatcher(
                            ctx.cmd, d.transmittance, d.multiScattering, d.skyView,
                            d.apScatter, d.apTransmittance, d.skyAmbientSh), d.view);
                        // BufferHandle -> GraphicsBuffer 的隐式转换在 execute 阶段查
                        // RenderGraphResourceRegistry.current，这里正好在 execute 里。
                        ctx.cmd.SetGlobalBuffer(VistaShaderIDs._VistaSkyAmbientSh,
                                                (GraphicsBuffer)d.skyAmbientSh);
                    });
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
                    d.luts.RenderAerialPerspectiveLut(new VistaGraphLutDispatcher(
                        ctx.cmd, d.transmittance, d.multiScattering, d.skyView,
                        d.apScatter, d.apTransmittance, d.skyAmbientSh), d.view, d.apSettings));
            }
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
