using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Vista
{
    /// <summary>
    /// 大气 LUT 的持有者与调度器。
    ///
    /// 分工：静态表（Transmittance / MultiScattering）只依赖大气参数，脏检查后重算；
    /// 逐帧表（SkyView / AerialPerspective）依赖太阳方向与相机，走 RenderGraph 每帧刷。
    ///
    /// 生命周期由 RendererFeature 持有（Create/Dispose），不做静态单例——
    /// 多个 RendererData / 多相机预览时静态单例会互相踩。
    ///
    /// 这个类只负责「分配 + 脏检查 + dispatch」，**不负责 pass 划分与依赖声明**。
    /// 后者在 <see cref="VistaAtmospherePass"/> 里做，因为哪几个 dispatch 必须落在
    /// 不同的 RenderGraph pass 里，是由资源状态转换（UAV -> SRV）决定的，
    /// 而 RenderGraph 只在 pass 边界插入 barrier —— 见那边的注释。
    /// </summary>
    public sealed class VistaAtmosphereLuts : IDisposable
    {
        // Hillaire 2020 的推荐尺寸。Transmittance 256x64 已经远超需要，
        // 但它是静态表，显存代价 256*64*8B = 128KB，不值得为省这点做取舍。
        public const int k_TransmittanceWidth  = 256;
        public const int k_TransmittanceHeight = 64;

        /// <summary>
        /// MS LUT 边长。**算法常量**，必须与 <c>AtmosphereScattering.hlsl</c> 里的
        /// <c>VISTA_MULTISCATTERING_LUT_RES</c> 一致（采样端的纹素中心内缩用它）。
        /// </summary>
        public const int k_MultiScatteringSize = 32;

        /// <summary>
        /// Sky-View LUT 默认尺寸（Hillaire 2020 的推荐值，16:9）。
        /// 与 MS LUT 不同，这个是**质量分级项**：它逐帧重算，且地平线附近的纹素密度
        /// 直接决定日落时会不会看到横向台阶。移动端分级降到 128×72。
        /// </summary>
        public const int k_SkyViewWidthDefault  = 192;
        public const int k_SkyViewHeightDefault = 108;

        /// <summary>与 compute 里的 VISTA_ROUNDTRIP_SCALE 一致。</summary>
        public const float k_RoundTripScale = 4096f;

        /// <summary>SH9。与 <c>VISTA_SH_COEFF_COUNT</c> 一致。</summary>
        public const int k_ShCoeffCount = 9;

        /// <summary>
        /// 参考解自检核的输出布局：每组 3 个 float4（法线 / 参考值 / SH 重建值）。
        /// 前 8 组是测试法线，最后一组是全天球平均亮度（法线写成零向量做标记）。
        /// 与 compute 里的 <c>VISTA_SKY_SH_REF_NORMALS</c> / <c>VISTA_SKY_SH_REF_GROUPS</c> 一致。
        /// 布局做成**自描述**的（法线也一并写出）是有意的：C# 侧不需要再镜像一份法线定义，
        /// 而镜像的那一份迟早会与 shader 里的走歧，届时自检会拿错法线去比对，
        /// 报出的偏差既不是 0 也不是明显错误 —— 最难查的那种失败。
        /// </summary>
        public const int k_ShRefNormalCount = 8;
        /// <summary>
        /// 法线组 + 2 个均值组（高样本精度参考 / 与投影同方向集的精确参考）。
        /// 与 compute 里的 <c>VISTA_SKY_SH_REF_MEANS</c> 一致。
        /// </summary>
        public const int k_ShRefMeanCount = 2;
        public const int k_ShRefGroupCount = k_ShRefNormalCount + k_ShRefMeanCount;
        public const int k_ShRefElementCount = k_ShRefGroupCount * 3;

        const string k_KernelTransmittance    = "TransmittanceLut";
        const string k_KernelMultiScattering  = "MultiScatteringLut";
        const string k_KernelSkyView          = "SkyViewLut";
        const string k_KernelSkyViewRoundTrip = "SkyViewLutRoundTrip";
        const string k_KernelAp               = "AerialPerspectiveLut";
        const string k_KernelApRoundTrip      = "AerialPerspectiveRoundTrip";
        const string k_KernelApSliceError     = "AerialPerspectiveSliceError";
        const string k_KernelSkyAmbientSh     = "SkyAmbientSh";
        const string k_KernelSkyAmbientShRef  = "SkyAmbientShReference";

        RTHandle m_Transmittance;
        RTHandle m_MultiScattering;
        RTHandle m_SkyView;
        RTHandle m_ApScatter;
        RTHandle m_ApTransmittance;

        GraphicsBuffer m_SkyAmbientSh;
        GraphicsBuffer m_SkyAmbientShRef;

        int m_SkyViewWidth  = k_SkyViewWidthDefault;
        int m_SkyViewHeight = k_SkyViewHeightDefault;

        readonly ComputeShader m_LutCS;
        readonly int m_KernelTransmittanceIdx    = -1;
        readonly int m_KernelMultiScatteringIdx  = -1;
        readonly int m_KernelSkyViewIdx          = -1;
        readonly int m_KernelSkyViewRoundTripIdx = -1;
        readonly int m_KernelApIdx               = -1;
        readonly int m_KernelApRoundTripIdx      = -1;
        readonly int m_KernelApSliceErrorIdx     = -1;
        readonly int m_KernelSkyAmbientShIdx     = -1;
        readonly int m_KernelSkyAmbientShRefIdx  = -1;

        /// <summary>上一次成功烘出静态表时使用的参数副本，用于脏检查。</summary>
        VistaAtmosphereParameters m_BakedParams;

        /// <summary>
        /// 上一次分配 AP 3D 表时的配置副本。只用于**分配**脏检查 ——
        /// <see cref="VistaAerialPerspectiveSettings.Equals"/> 故意只比较尺寸，
        /// 距离范围 / 分布 / 彩色透射率都是每帧推 cbuffer 的，改了不需要重分配。
        /// </summary>
        VistaAerialPerspectiveSettings m_AllocatedAp;

        /// <summary>Transmittance LUT（256×64）。未分配过时为 null。</summary>
        public RTHandle transmittanceLut => m_Transmittance;

        /// <summary>Multi-Scattering LUT（32×32）。未分配过时为 null。</summary>
        public RTHandle multiScatteringLut => m_MultiScattering;

        /// <summary>Sky-View LUT。未分配过时为 null。</summary>
        public RTHandle skyViewLut => m_SkyView;

        /// <summary>Aerial Perspective 散射表（RGB = 累积内散射，A = 灰度透射率）。3D。</summary>
        public RTHandle apScatterLut => m_ApScatter;

        /// <summary>Aerial Perspective 彩色透射率表（RGB）。3D。</summary>
        public RTHandle apTransmittanceLut => m_ApTransmittance;

        /// <summary>
        /// 天空环境光 SH：9 个 float4，存**原始辐射亮度矩** L_i = ∫L·Y_i dω
        /// （不是已卷积余弦瓣的辐照度 SH，理由见 ShaderLibrary/SphericalHarmonics.hlsl 的头注）。
        /// 未分配过时为 null。
        /// </summary>
        public GraphicsBuffer skyAmbientShBuffer => m_SkyAmbientSh;

        /// <summary>自检参考解输出。只在 <see cref="EnsureSkyAmbientShReference"/> 之后非 null。</summary>
        public GraphicsBuffer skyAmbientShRefBuffer => m_SkyAmbientShRef;

        public int skyViewWidth  => m_SkyViewWidth;
        public int skyViewHeight => m_SkyViewHeight;

        /// <summary>compute 资源缺失（未编译 / 平台不支持）时为 false，调用方应降级而不是抛异常。</summary>
        public bool isValid => m_LutCS != null
                            && m_KernelTransmittanceIdx >= 0
                            && m_KernelMultiScatteringIdx >= 0
                            && m_KernelSkyViewIdx >= 0;

        /// <summary>
        /// AP 是否可用。**与 <see cref="isValid"/> 分开**：AP 核缺失时天空仍应正常出图，
        /// 只是远景没有雾。合并成一个开关会让"AP 编译失败"表现为"整个天空黑掉"，
        /// 症状和原因差得太远。
        /// </summary>
        public bool isAerialPerspectiveValid => isValid && m_KernelApIdx >= 0;

        /// <summary>
        /// SH 投影是否可用。同样单独一个开关：这个核挂了，天空与 AP 都还是对的，
        /// 只是间接光不再跟着天空走（表现为暗部偏色/不变），
        /// 不该把整个大气模块一起关掉。
        /// </summary>
        public bool isSkyAmbientShValid => isValid && m_KernelSkyAmbientShIdx >= 0;

        public VistaAtmosphereLuts(ComputeShader lutCS)
        {
            m_LutCS = lutCS;
            if (m_LutCS == null) return;
            if (m_LutCS.HasKernel(k_KernelTransmittance))
                m_KernelTransmittanceIdx = m_LutCS.FindKernel(k_KernelTransmittance);
            if (m_LutCS.HasKernel(k_KernelMultiScattering))
                m_KernelMultiScatteringIdx = m_LutCS.FindKernel(k_KernelMultiScattering);
            if (m_LutCS.HasKernel(k_KernelSkyView))
                m_KernelSkyViewIdx = m_LutCS.FindKernel(k_KernelSkyView);
            // round-trip 是自检核，缺了不影响 isValid
            if (m_LutCS.HasKernel(k_KernelSkyViewRoundTrip))
                m_KernelSkyViewRoundTripIdx = m_LutCS.FindKernel(k_KernelSkyViewRoundTrip);
            if (m_LutCS.HasKernel(k_KernelAp))
                m_KernelApIdx = m_LutCS.FindKernel(k_KernelAp);
            if (m_LutCS.HasKernel(k_KernelApRoundTrip))
                m_KernelApRoundTripIdx = m_LutCS.FindKernel(k_KernelApRoundTrip);
            if (m_LutCS.HasKernel(k_KernelApSliceError))
                m_KernelApSliceErrorIdx = m_LutCS.FindKernel(k_KernelApSliceError);
            if (m_LutCS.HasKernel(k_KernelSkyAmbientSh))
                m_KernelSkyAmbientShIdx = m_LutCS.FindKernel(k_KernelSkyAmbientSh);
            if (m_LutCS.HasKernel(k_KernelSkyAmbientShRef))
                m_KernelSkyAmbientShRefIdx = m_LutCS.FindKernel(k_KernelSkyAmbientShRef);
        }

        /// <summary>
        /// 设置 Sky-View LUT 分辨率（质量分级用）。尺寸变化时会重新分配。
        /// </summary>
        public void SetSkyViewResolution(int width, int height)
        {
            width  = Mathf.Clamp(width,  32, 512);
            height = Mathf.Clamp(height, 18, 288);
            if (width == m_SkyViewWidth && height == m_SkyViewHeight) return;

            m_SkyViewWidth  = width;
            m_SkyViewHeight = height;
            m_SkyView?.Release();
            m_SkyView = null;
        }

        // ====================================================================
        //  记录期（CPU）
        // ====================================================================

        /// <summary>
        /// 记录期调用：保证三张表都已分配，推大气 cbuffer 全局常量，并做静态表脏检查。
        /// </summary>
        /// <returns>
        /// true 表示调用方**必须**在本帧安排 <see cref="RenderTransmittanceLut"/> 与
        /// <see cref="RenderMultiScatteringLut"/>。返回 true 的同时已把参数记为「已烘」，
        /// 所以调用方不能拿到 true 却不排 pass。
        /// </returns>
        public bool PrepareLuts(VistaAtmosphereParameters parameters)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            if (!isValid) return false;

            bool allocated = AllocateStaticIfNeeded();
            AllocateSkyViewIfNeeded();

            // 全局常量每次都推：Shader.SetGlobal* 是跨帧持久的，但其他系统（别的 feature、
            // Editor 预览）可能覆盖同名全局，所以不做"只在脏时推"的优化。代价可忽略。
            // 这些是 CPU 立即生效的全局，记录期设置即可 —— 本帧所有 execute 都在其之后。
            parameters.Bind(k_TransmittanceWidth, k_TransmittanceHeight);

            bool dirty = allocated || m_BakedParams == null || !m_BakedParams.Equals(parameters);
            if (dirty) m_BakedParams = parameters.Clone();
            return dirty;
        }

        /// <summary>强制下一次 <see cref="PrepareLuts"/> 重算（shader 重编译 / 面板点重烘时用）。</summary>
        public void Invalidate() => m_BakedParams = null;

        /// <summary>
        /// 记录期调用：按需分配 AP 的两张 3D 表。
        /// 与 <see cref="PrepareLuts"/> 分开是因为 AP 完全逐帧、没有"脏了才重烘"的概念，
        /// 塞进去会让那个方法的返回值（"必须排静态 pass"）多一层含义。
        /// </summary>
        /// <returns>AP 表是否可用。false 时调用方应跳过 AP pass 并让下游走无雾路径。</returns>
        public bool PrepareAerialPerspective(VistaAerialPerspectiveSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (!isAerialPerspectiveValid) return false;

            if (m_ApScatter == null || m_ApTransmittance == null
                || m_AllocatedAp == null || !m_AllocatedAp.Equals(settings))
            {
                ReleaseAerialPerspective();
                AllocateAerialPerspective(settings);
                m_AllocatedAp = settings.Clone();
            }
            return true;
        }

        /// <summary>
        /// 记录期调用：按需分配天空环境光 SH 缓冲（9 × float4 = 144 B）。
        /// </summary>
        /// <returns>SH 是否可用。false 时下游应回退到 URP 自带的环境光。</returns>
        public bool PrepareSkyAmbientSh()
        {
            if (!isSkyAmbientShValid) return false;

            // Structured 而不是 Constant：数据是 GPU 产出的，走 constant buffer 就得先回 CPU
            // 再 SetGlobalVector 九次，凭空引入一帧以上的延迟 —— 而"天空变了间接光没跟上"
            // 正是这个模块要消灭的问题。stride 16 = float4，与 HLSL 侧 StructuredBuffer<float4> 对齐。
            m_SkyAmbientSh ??= new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, k_ShCoeffCount, sizeof(float) * 4)
            {
                name = "VistaSkyAmbientSh",
            };
            return true;
        }

        /// <summary>
        /// 仅供 Editor 自检：按需分配参考解输出缓冲。运行时路径**不会**调它 ——
        /// 参考核每帧 8×4096 次 LUT 采样，是自检可以接受、线上不能接受的开销。
        /// </summary>
        public bool EnsureSkyAmbientShReference()
        {
            if (!isSkyAmbientShValid || m_KernelSkyAmbientShRefIdx < 0) return false;

            m_SkyAmbientShRef ??= new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, k_ShRefElementCount, sizeof(float) * 4)
            {
                name = "VistaSkyAmbientShRef",
            };
            return true;
        }

        // ====================================================================
        //  执行期（GPU dispatch）
        //
        //  每个方法对应 RenderGraph 里的一个 pass。资源绑定走 IVistaLutDispatcher，
        //  这样同一份 dispatch 代码既能录进 RenderGraph 的 ComputeCommandBuffer，
        //  也能录进 Editor 自检用的原生 CommandBuffer —— 自检验的就是线上那一份。
        // ====================================================================

        public void RenderTransmittanceLut<T>(T d) where T : struct, IVistaLutDispatcher
        {
            if (!isValid) return;

            d.SetTexture(m_LutCS, m_KernelTransmittanceIdx,
                VistaShaderIDs._VistaTransmittanceLutRW, VistaLutSlot.Transmittance);
            d.Dispatch(m_LutCS, m_KernelTransmittanceIdx,
                VistaComputeUtils.DivRoundUp(k_TransmittanceWidth, 8),
                VistaComputeUtils.DivRoundUp(k_TransmittanceHeight, 8), 1);
        }

        /// <summary>读 Transmittance，必须排在 <see cref="RenderTransmittanceLut{T}"/> 之后的独立 pass。</summary>
        public void RenderMultiScatteringLut<T>(T d) where T : struct, IVistaLutDispatcher
        {
            if (!isValid) return;

            d.SetTexture(m_LutCS, m_KernelMultiScatteringIdx,
                VistaShaderIDs._VistaTransmittanceLut, VistaLutSlot.Transmittance);
            d.SetTexture(m_LutCS, m_KernelMultiScatteringIdx,
                VistaShaderIDs._VistaMultiScatteringLutRW, VistaLutSlot.MultiScattering);
            // 一个线程组 = 一个纹素（组内 64 线程跑 64 个球面方向），所以 dispatch 尺寸
            // 就是纹理尺寸，不做 DivRoundUp。
            d.Dispatch(m_LutCS, m_KernelMultiScatteringIdx,
                k_MultiScatteringSize, k_MultiScatteringSize, 1);
        }

        /// <summary>
        /// 渲染逐帧的 Sky-View LUT。读 Transmittance 与 MultiScattering，
        /// 必须排在它们之后的独立 pass。
        /// </summary>
        public void RenderSkyViewLut<T>(T d, in VistaAtmosphereViewData view)
            where T : struct, IVistaLutDispatcher
        {
            if (!isValid) return;

            view.Bind(d, m_SkyViewWidth, m_SkyViewHeight);

            d.SetTexture(m_LutCS, m_KernelSkyViewIdx,
                VistaShaderIDs._VistaTransmittanceLut, VistaLutSlot.Transmittance);
            d.SetTexture(m_LutCS, m_KernelSkyViewIdx,
                VistaShaderIDs._VistaMultiScatteringLut, VistaLutSlot.MultiScattering);
            d.SetTexture(m_LutCS, m_KernelSkyViewIdx,
                VistaShaderIDs._VistaSkyViewLutRW, VistaLutSlot.SkyView);
            d.Dispatch(m_LutCS, m_KernelSkyViewIdx,
                VistaComputeUtils.DivRoundUp(m_SkyViewWidth, 8),
                VistaComputeUtils.DivRoundUp(m_SkyViewHeight, 8), 1);
        }

        /// <summary>
        /// 仅供 Editor 自检：把 Sky-View 参数化的正反映射误差写进 SkyView LUT。
        /// RGB 各存 |Δu|·<see cref="k_RoundTripScale"/>、|Δv|·scale、viewZenithCos，
        /// A 存 lightViewCos。调完必须再跑一次 <see cref="RenderSkyViewLut{T}"/> 覆盖回去。
        /// </summary>
        public void RenderSkyViewRoundTrip<T>(T d, in VistaAtmosphereViewData view)
            where T : struct, IVistaLutDispatcher
        {
            if (!isValid || m_KernelSkyViewRoundTripIdx < 0) return;

            view.Bind(d, m_SkyViewWidth, m_SkyViewHeight);

            d.SetTexture(m_LutCS, m_KernelSkyViewRoundTripIdx,
                VistaShaderIDs._VistaSkyViewLutRW, VistaLutSlot.SkyView);
            d.Dispatch(m_LutCS, m_KernelSkyViewRoundTripIdx,
                VistaComputeUtils.DivRoundUp(m_SkyViewWidth, 8),
                VistaComputeUtils.DivRoundUp(m_SkyViewHeight, 8), 1);
        }

        // ====================================================================
        //  天空环境光 SH（逐帧，读 SkyView）
        //
        //  依赖 SkyView 的**采样**（SRV），所以必须排在 SkyView pass 之后的独立 pass。
        //  与 AP 之间没有依赖，可并行。
        // ====================================================================

        /// <summary>
        /// 把天空投影成 SH9 环境光。读 SkyView LUT（SRV），写 SH 缓冲（UAV）。
        ///
        /// 调 <see cref="VistaAtmosphereViewData.Bind"/> 而不是只推 SH 自己那几个参数：
        /// 核里要用 `_VistaViewPosKm` / `_VistaSunDirection` / `_VistaSkyViewLutSize`
        /// 去采 SkyView，这三个正是 Bind 推的东西。重复推一遍的代价是几个 SetGlobalVector，
        /// 而漏推的代价是采到上一帧甚至上一个相机的参数 —— 立即模式自检里尤其容易踩，
        /// 因为那里的调用顺序是手写的。
        /// </summary>
        public void RenderSkyAmbientSh<T>(T d, in VistaAtmosphereViewData view)
            where T : struct, IVistaLutDispatcher
        {
            if (!isSkyAmbientShValid) return;

            view.Bind(d, m_SkyViewWidth, m_SkyViewHeight);

            d.SetTexture(m_LutCS, m_KernelSkyAmbientShIdx,
                VistaShaderIDs._VistaSkyViewLut, VistaLutSlot.SkyView);
            d.SetBuffer(m_LutCS, m_KernelSkyAmbientShIdx,
                VistaShaderIDs._VistaSkyAmbientShRW, VistaLutBufferSlot.SkyAmbientSh);
            // 整张 SH 就是一个线程组：组内 64 线程分摊 1024 个方向，再 groupshared 归约。
            // 拆多组反而要跨组归约（额外一趟 dispatch 或 atomic），而这活总共只有 1024 次
            // LUT 采样，一个组绰绰有余。
            d.Dispatch(m_LutCS, m_KernelSkyAmbientShIdx, 1, 1, 1);
        }

        /// <summary>
        /// 仅供 Editor 自检：对 8 个测试法线，用 4096 样本的余弦加权直接积分算出参考辐照度，
        /// 与 SH 重建值一起写进 <see cref="skyAmbientShRefBuffer"/>。
        /// **必须在 <see cref="RenderSkyAmbientSh{T}"/> 之后调用**（它要读那份 SH）。
        ///
        /// 这是本模块唯一有实质判定力的正确性检查："系数有限、非负"只能抓到彻底崩掉的情况，
        /// 而真正会犯的错是**尺度错**（少乘 4π、少乘 Â_l、基函数常数写错一个量级）——
        /// 那种错在任何单一场景里都只表现为"环境光偏亮/偏暗"，会被当成美术没调好。
        /// 两侧的 L 都取自同一张 SkyView LUT，所以这一项只测投影与重建，不测大气本身。
        /// </summary>
        public void RenderSkyAmbientShReference<T>(T d, in VistaAtmosphereViewData view)
            where T : struct, IVistaLutDispatcher
        {
            if (!isSkyAmbientShValid || m_KernelSkyAmbientShRefIdx < 0) return;

            view.Bind(d, m_SkyViewWidth, m_SkyViewHeight);

            d.SetTexture(m_LutCS, m_KernelSkyAmbientShRefIdx,
                VistaShaderIDs._VistaSkyViewLut, VistaLutSlot.SkyView);
            // SH 在这里是**只读**输入（重建端），所以绑到 SRV 名字上，不是 RW 名字。
            d.SetBuffer(m_LutCS, m_KernelSkyAmbientShRefIdx,
                VistaShaderIDs._VistaSkyAmbientSh, VistaLutBufferSlot.SkyAmbientSh);
            d.SetBuffer(m_LutCS, m_KernelSkyAmbientShRefIdx,
                VistaShaderIDs._VistaSkyAmbientShRefRW, VistaLutBufferSlot.SkyAmbientShReference);
            // 一个法线一个线程组，外加两组全天球均值。
            d.Dispatch(m_LutCS, m_KernelSkyAmbientShRefIdx, k_ShRefGroupCount, 1, 1);
        }

        // ====================================================================
        //  Aerial Perspective（逐帧，3D froxel）
        //
        //  与 Sky-View 一样读 Transmittance / MultiScattering，所以必须排在
        //  静态表之后的独立 pass；但**与 Sky-View 之间没有依赖**，两者可以并行，
        //  也可以合成同一个 pass。这里保持独立方法，pass 划分由 VistaAtmospherePass 决定。
        // ====================================================================

        /// <summary>
        /// 渲染 AP froxel LUT。散射与彩色透射率两张表都写：
        /// 「灰度 / 彩色」是采样端的运行时开关（<c>_VistaApFlags.x</c>），不是烘焙开关。
        /// 这样才能在同一帧里 A/B 两条路径 —— 也是自检能量化两者差值的前提。
        /// 多写一张 32³ fp16 的代价是 256 KB 与约 0.02 ms，不值得为它引入分支。
        /// </summary>
        public void RenderAerialPerspectiveLut<T>(
            T d, in VistaAtmosphereViewData view, VistaAerialPerspectiveSettings settings)
            where T : struct, IVistaLutDispatcher
        {
            if (!isAerialPerspectiveValid || m_ApScatter == null) return;

            view.BindAerialPerspective(d, settings);

            d.SetTexture(m_LutCS, m_KernelApIdx,
                VistaShaderIDs._VistaTransmittanceLut, VistaLutSlot.Transmittance);
            d.SetTexture(m_LutCS, m_KernelApIdx,
                VistaShaderIDs._VistaMultiScatteringLut, VistaLutSlot.MultiScattering);
            d.SetTexture(m_LutCS, m_KernelApIdx,
                VistaShaderIDs._VistaApScatterLutRW, VistaLutSlot.ApScatter);
            d.SetTexture(m_LutCS, m_KernelApIdx,
                VistaShaderIDs._VistaApTransmittanceLutRW, VistaLutSlot.ApTransmittance);

            // 一个线程负责一整根柱（核内自己循环深度），所以 Z 方向不 dispatch。
            d.Dispatch(m_LutCS, m_KernelApIdx,
                VistaComputeUtils.DivRoundUp(settings.width, 8),
                VistaComputeUtils.DivRoundUp(settings.height, 8), 1);
        }

        /// <summary>
        /// 仅供 Editor 自检：深度分布正反映射 round-trip。
        /// 结果写进 apScatterLut 的 (0, 0, slice) 一列，
        /// RGBA = |Δw|·<see cref="k_RoundTripScale"/>、距离(km)、w、texW。
        /// 调完必须再跑一次 <see cref="RenderAerialPerspectiveLut{T}"/> 覆盖回去。
        /// </summary>
        public void RenderApRoundTrip<T>(
            T d, in VistaAtmosphereViewData view, VistaAerialPerspectiveSettings settings)
            where T : struct, IVistaLutDispatcher
        {
            if (!isAerialPerspectiveValid || m_KernelApRoundTripIdx < 0 || m_ApScatter == null) return;

            view.BindAerialPerspective(d, settings);

            d.SetTexture(m_LutCS, m_KernelApRoundTripIdx,
                VistaShaderIDs._VistaApScatterLutRW, VistaLutSlot.ApScatter);
            d.Dispatch(m_LutCS, m_KernelApRoundTripIdx,
                VistaComputeUtils.DivRoundUp(settings.depth, 64), 1, 1);
        }

        /// <summary>
        /// 仅供 Editor 自检：切片分布质量测量。**必须在 <see cref="RenderAerialPerspectiveLut{T}"/>
        /// 之后调用**，因为它要把散射表当 SRV 读回来做对照。
        /// 结果写进 apTransmittanceLut 的 (0, 0, slice) 一列，
        /// RGBA = 切片中心相对误差、切片中点相对误差、中心距离(km)、中点距离(km)。
        /// 调完必须再跑一次正式核覆盖回去。
        /// </summary>
        public void RenderApSliceError<T>(
            T d, in VistaAtmosphereViewData view, VistaAerialPerspectiveSettings settings)
            where T : struct, IVistaLutDispatcher
        {
            if (!isAerialPerspectiveValid || m_KernelApSliceErrorIdx < 0 || m_ApScatter == null) return;

            view.BindAerialPerspective(d, settings);

            d.SetTexture(m_LutCS, m_KernelApSliceErrorIdx,
                VistaShaderIDs._VistaTransmittanceLut, VistaLutSlot.Transmittance);
            d.SetTexture(m_LutCS, m_KernelApSliceErrorIdx,
                VistaShaderIDs._VistaMultiScatteringLut, VistaLutSlot.MultiScattering);
            // 同一张纹理既当 SRV 读、又不当 UAV 写：读散射、写透射率，没有 RAW 冲突。
            d.SetTexture(m_LutCS, m_KernelApSliceErrorIdx,
                VistaShaderIDs._VistaApScatterLutRead, VistaLutSlot.ApScatter);
            d.SetTexture(m_LutCS, m_KernelApSliceErrorIdx,
                VistaShaderIDs._VistaApTransmittanceLutRW, VistaLutSlot.ApTransmittance);
            d.Dispatch(m_LutCS, m_KernelApSliceErrorIdx,
                VistaComputeUtils.DivRoundUp(settings.depth, 64), 1, 1);
        }

        // ====================================================================
        //  立即模式（Editor 预览 / 自检）
        //
        //  RenderGraph 之外的路径：一个普通 CommandBuffer 里顺序跑完全部三张表。
        //  同一个原生 CommandBuffer 内的 UAV->SRV 转换由 Unity 的图形层自动插入，
        //  所以这里不需要像 RenderGraph 那样拆 pass。全局纹理也只能在这里手动绑
        //  （RenderGraph 侧走 SetGlobalTextureAfterPass）。
        // ====================================================================

        /// <summary>立即模式：脏检查 + 按需重烘静态表 + 绑定全局纹理。</summary>
        /// <returns>本次是否真的重算了静态 LUT。</returns>
        public bool EnsureStaticLuts(CommandBuffer cmd, VistaAtmosphereParameters parameters)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (!isValid) return false;

            bool dirty = PrepareLuts(parameters);

            cmd.SetGlobalTexture(VistaShaderIDs._VistaTransmittanceLut, m_Transmittance);
            cmd.SetGlobalTexture(VistaShaderIDs._VistaMultiScatteringLut, m_MultiScattering);

            if (dirty)
            {
                var d = new VistaImmediateLutDispatcher(cmd, this);
                RenderTransmittanceLut(d);
                RenderMultiScatteringLut(d);
            }
            return dirty;
        }

        /// <summary>立即模式的 Sky-View。</summary>
        public void RenderSkyViewLut(CommandBuffer cmd, in VistaAtmosphereViewData view)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (!isValid) return;

            RenderSkyViewLut(new VistaImmediateLutDispatcher(cmd, this), view);
            cmd.SetGlobalTexture(VistaShaderIDs._VistaSkyViewLut, m_SkyView);
        }

        /// <summary>立即模式的 round-trip 自检。</summary>
        public void RenderSkyViewRoundTrip(CommandBuffer cmd, in VistaAtmosphereViewData view)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            RenderSkyViewRoundTrip(new VistaImmediateLutDispatcher(cmd, this), view);
        }

        /// <summary>立即模式的 AP。调用前需先 <see cref="PrepareAerialPerspective"/>。</summary>
        public void RenderAerialPerspectiveLut(
            CommandBuffer cmd, in VistaAtmosphereViewData view, VistaAerialPerspectiveSettings settings)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (!isAerialPerspectiveValid) return;

            RenderAerialPerspectiveLut(new VistaImmediateLutDispatcher(cmd, this), view, settings);
            cmd.SetGlobalTexture(VistaShaderIDs._VistaApScatterLut, m_ApScatter);
            cmd.SetGlobalTexture(VistaShaderIDs._VistaApTransmittanceLut, m_ApTransmittance);
        }

        /// <summary>立即模式的 AP 深度分布 round-trip 自检。</summary>
        public void RenderApRoundTrip(
            CommandBuffer cmd, in VistaAtmosphereViewData view, VistaAerialPerspectiveSettings settings)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            RenderApRoundTrip(new VistaImmediateLutDispatcher(cmd, this), view, settings);
        }

        /// <summary>立即模式的 AP 切片误差测量。</summary>
        public void RenderApSliceError(
            CommandBuffer cmd, in VistaAtmosphereViewData view, VistaAerialPerspectiveSettings settings)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            RenderApSliceError(new VistaImmediateLutDispatcher(cmd, this), view, settings);
        }

        /// <summary>立即模式的 SH 投影。调用前需先 <see cref="PrepareSkyAmbientSh"/>。</summary>
        public void RenderSkyAmbientSh(CommandBuffer cmd, in VistaAtmosphereViewData view)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (!isSkyAmbientShValid) return;

            RenderSkyAmbientSh(new VistaImmediateLutDispatcher(cmd, this), view);
            // 立即模式下全局绑定只能手动做（RenderGraph 侧走 SetGlobalBufferAfterPass）。
            cmd.SetGlobalBuffer(VistaShaderIDs._VistaSkyAmbientSh, m_SkyAmbientSh);
        }

        /// <summary>立即模式的 SH 参考解自检。调用前需先 <see cref="EnsureSkyAmbientShReference"/>。</summary>
        public void RenderSkyAmbientShReference(CommandBuffer cmd, in VistaAtmosphereViewData view)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            RenderSkyAmbientShReference(new VistaImmediateLutDispatcher(cmd, this), view);
        }

        // ====================================================================
        //  分配
        // ====================================================================

        bool AllocateStaticIfNeeded()
        {
            if (m_Transmittance != null && m_MultiScattering != null)
                return false;

            m_Transmittance ??= RTHandles.Alloc(
                k_TransmittanceWidth, k_TransmittanceHeight,
                // fp16 足够：透射率在 [0,1]，且这张表只做乘法不做累加，没有精度累积问题。
                format: GraphicsFormat.R16G16B16A16_SFloat,
                filterMode: FilterMode.Bilinear,
                // Clamp 是必需的：Bruneton 参数化下 uv 两端就是物理边界，
                // Repeat 会让地平线方向采到天顶的值。
                wrapMode: TextureWrapMode.Clamp,
                enableRandomWrite: true,
                name: "VistaTransmittanceLut");

            m_MultiScattering ??= RTHandles.Alloc(
                k_MultiScatteringSize, k_MultiScatteringSize,
                format: GraphicsFormat.R16G16B16A16_SFloat,
                filterMode: FilterMode.Bilinear,
                wrapMode: TextureWrapMode.Clamp,
                enableRandomWrite: true,
                name: "VistaMultiScatteringLut");

            return true;
        }

        void AllocateSkyViewIfNeeded()
        {
            // fp16 在这里是**有风险的选择**，但成立：SkyView 存的是绝对亮度 (cd/m²)，
            // 天顶蓝天约 5e3、日面附近可到 1e5，都在 fp16 上限 65504 之内 —— 除了
            // 太阳圆盘（1e9 量级），而圆盘本来就不烘进这张表。这也是把圆盘排除在外的
            // 第二个理由：不然这张表必须升到 fp32，显存与带宽翻倍。
            m_SkyView ??= RTHandles.Alloc(
                m_SkyViewWidth, m_SkyViewHeight,
                format: GraphicsFormat.R16G16B16A16_SFloat,
                filterMode: FilterMode.Bilinear,
                wrapMode: TextureWrapMode.Clamp,
                enableRandomWrite: true,
                name: "VistaSkyViewLut");
        }

        public void Dispose()
        {
            m_Transmittance?.Release();
            m_Transmittance = null;
            m_MultiScattering?.Release();
            m_MultiScattering = null;
            m_SkyView?.Release();
            m_SkyView = null;
            ReleaseAerialPerspective();
            // GraphicsBuffer 是 IDisposable 而非 RTHandle，漏掉它不会有 RTHandle 那种
            // "泄漏检测"日志，只会安静地涨显存 —— Editor 里反复域重载时尤其明显。
            m_SkyAmbientSh?.Dispose();
            m_SkyAmbientSh = null;
            m_SkyAmbientShRef?.Dispose();
            m_SkyAmbientShRef = null;
            m_BakedParams = null;
        }

        void AllocateAerialPerspective(VistaAerialPerspectiveSettings settings)
        {
            // 3D 分配走带 slices + dimension 的重载。RTHandle 的 2D 便捷重载
            // 会静默建成 Tex2D，症状是 RWTexture3D 绑定失败 —— 只在 Editor.log 里
            // 有一行 warning，画面上表现为整张 AP 全零（远景完全无雾），极难联想到分配。
            m_ApScatter = RTHandles.Alloc(
                settings.width, settings.height,
                // fp16：散射项是绝对亮度 (cd/m²)，最亮的情况是日面附近的浓雾，
                // 量级 1e5，在 fp16 上限 65504 之上 —— 但 AP 是**相机到物体之间**那一段，
                // 32 km 内的累积散射实测不超过 3e4。太阳圆盘不进这张表（那是 Sky-View 的事），
                // 所以 fp16 成立。真溢出会表现为远山发白饱和，自检里量了峰值。
                format: GraphicsFormat.R16G16B16A16_SFloat,
                slices: settings.depth,
                filterMode: FilterMode.Bilinear,
                // 三个轴都必须 Clamp：深度轴 Repeat 会让"比最远片还远"的像素采回最近片，
                // 症状是远处山体突然变得毫无雾感。
                wrapMode: TextureWrapMode.Clamp,
                dimension: TextureDimension.Tex3D,
                enableRandomWrite: true,
                name: "VistaApScatterLut");

            m_ApTransmittance = RTHandles.Alloc(
                settings.width, settings.height,
                format: GraphicsFormat.R16G16B16A16_SFloat,
                slices: settings.depth,
                filterMode: FilterMode.Bilinear,
                wrapMode: TextureWrapMode.Clamp,
                dimension: TextureDimension.Tex3D,
                enableRandomWrite: true,
                name: "VistaApTransmittanceLut");
        }

        void ReleaseAerialPerspective()
        {
            m_ApScatter?.Release();
            m_ApScatter = null;
            m_ApTransmittance?.Release();
            m_ApTransmittance = null;
            m_AllocatedAp = null;
        }
    }

    internal static class VistaComputeUtils
    {
        public static int DivRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
    }
}
