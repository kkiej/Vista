using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Vista
{
    /// <summary>
    /// 天空镜面反射 cubemap 的辐射来源。**运行期**开关而不是编译期宏：
    /// Demo 视频要在同一帧里对比 PC 与移动端两条路径的画面与帧时间。
    /// </summary>
    public enum VistaSkyReflectionMode
    {
        /// <summary>不产出 cubemap。下游回落到场景自带的反射探针。</summary>
        Off,
        /// <summary>逐纹素从 Sky-View LUT 积分（PC）。含地平线那圈高频亮带。</summary>
        SkyViewLut,
        /// <summary>从环境光 SH9 重建辐射（移动端）。零 LUT 依赖，但被带限在 l ≤ 2。</summary>
        AmbientSh,
    }

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

        /// <summary>
        /// 与 compute 里的 <c>VISTA_AP_ERR_SCALE</c> 一致。
        /// AP 透射率的插值误差实测在 1e-6 量级，落在 fp16 的次正规区里；
        /// 存之前放大、读回来再除，否则"误差极小"与"通道没写"读不出区别。
        /// </summary>
        public const float k_ApErrorScale = 4096f;

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

        // ---- 天空镜面反射 cubemap ----

        /// <summary>
        /// mip 级数。**被 URP 的采样端定死的**：<c>GlossyEnvironmentReflection</c> 走
        /// <c>PerceptualRoughnessToMipmapLevel</c> 的单参重载，maxMipLevel 固定为
        /// <c>UNITY_SPECCUBE_LOD_STEPS = 6</c>，于是 pr ∈ [0,1] 映到 mip ∈ [0,6]，
        /// 必须存在 0..6 共 7 级。少一级时 pr=1 采到不存在的 mip 被硬件 clamp，
        /// 表现为"粗糙度 0.8 以上不再继续变模糊"。
        /// 与 <c>VISTA_SKY_REFLECTION_MIPS</c> 一致，自检核会把 HLSL 侧那份报出来比对。
        /// </summary>
        public const int k_SkyReflectionMipCount = 7;

        /// <summary>边长 = 2^6 = 64。由 mip 级数反推，不是挑的（7 级 → 64,32,16,8,4,2,1）。</summary>
        public const int k_SkyReflectionSize = 1 << (k_SkyReflectionMipCount - 1);

        /// <summary>
        /// 自检输出的行布局，与 compute 里的 <c>VISTA_SKY_REFL_ROW_*</c> 一一对应。
        /// 0..5 逐面误差，6/7/8 = cube / LUT / SH 的整球均值，9..15 逐 mip 的
        /// 粗糙度 round-trip，16 = HLSL 侧常量导出，17..23 = wide 核归约等价性（判据 4a，
        /// 下标 = mip，mip0 那行不参与），24 = wide mip 的 round-trip（判据 4b）。
        /// </summary>
        public const int k_ReflVerifyRowFace  = 0;
        public const int k_ReflVerifyRowMean  = 6;
        public const int k_ReflVerifyRowMip   = 9;
        public const int k_ReflVerifyRowConst = k_ReflVerifyRowMip + k_SkyReflectionMipCount;
        public const int k_ReflVerifyRowWide   = k_ReflVerifyRowConst + 1;
        public const int k_ReflVerifyRowWideRt = k_ReflVerifyRowWide + k_SkyReflectionMipCount;
        public const int k_ReflVerifyElementCount = k_ReflVerifyRowWideRt + 1;
        /// <summary>
        /// 逐面 6 组 + 均值 1 组 + mip 映射 1 组 + wide 核 1 组。
        /// 与 <c>VISTA_SKY_REFL_VERIFY_GROUPS</c> 一致。
        /// </summary>
        public const int k_ReflVerifyGroupCount = 9;

        /// <summary>
        /// 判据 4a 里"K ≥ 多少才值得走 wide 核"的门槛，也就是 wide 核的线程数。
        /// 与 <c>VISTA_SKY_REFL_WIDE_THREADS</c> 一致。
        /// <see cref="RenderSkyReflection{T}"/> 的分界写成 mip 序号（不重算 K），
        /// 自检拿 HLSL 导出的 K 与这个门槛对账，把那个隐式绑定变成会红的判据。
        /// </summary>
        public const int k_SkyReflectionWideThreshold = 64;

        /// <summary>判据 4b 取样的 mip。与 <c>VISTA_SKY_REFL_VERIFY_WIDE_MIP</c> 一致。</summary>
        public const int k_ReflVerifyWideMip = 2;

        /// <summary>
        /// banding 签名核 mode 0 的固定方向数：4 个仰角环 × 16 个方位。
        /// 与 <c>VISTA_BANDING_RINGS</c> / <c>VISTA_BANDING_AZIMUTHS</c> 一致。
        /// </summary>
        public const int k_SkyBandingRings    = 4;
        public const int k_SkyBandingAzimuths = 16;
        public const int k_SkyBandingDirCount = k_SkyBandingRings * k_SkyBandingAzimuths;

        /// <summary>
        /// 签名缓冲容量。mode 0 只用前 64 个，mode 1（沿大圆走弧）可以用满 ——
        /// 一次分配吃住两种 mode，省掉"换 mode 要重分配"这个状态。
        /// 256 × float4 = 4 KB，不值得为它做动态尺寸。
        /// </summary>
        public const int k_SkyBandingMaxCount = 256;

        const string k_KernelTransmittance    = "TransmittanceLut";
        const string k_KernelMultiScattering  = "MultiScatteringLut";
        const string k_KernelSkyView          = "SkyViewLut";
        const string k_KernelSkyViewRoundTrip = "SkyViewLutRoundTrip";
        const string k_KernelAp               = "AerialPerspectiveLut";
        const string k_KernelApRoundTrip      = "AerialPerspectiveRoundTrip";
        const string k_KernelApSliceError     = "AerialPerspectiveSliceError";
        const string k_KernelSkyAmbientSh     = "SkyAmbientSh";
        const string k_KernelSkyAmbientShRef  = "SkyAmbientShReference";
        const string k_KernelSkyBanding       = "SkyViewBandingSignature";
        const string k_KernelSkyReflection       = "SkyReflectionFilter";
        const string k_KernelSkyReflectionWide   = "SkyReflectionFilterWide";
        const string k_KernelSkyReflectionVerify = "SkyReflectionVerify";

        RTHandle m_Transmittance;
        RTHandle m_MultiScattering;
        RTHandle m_SkyView;
        RTHandle m_ApScatter;
        RTHandle m_ApTransmittance;
        RTHandle m_SkyReflection;
        RTHandle m_SkyReflectionArray;

        GraphicsBuffer m_SkyAmbientSh;
        GraphicsBuffer m_SkyAmbientShRef;
        GraphicsBuffer m_SkyReflectionVerify;
        GraphicsBuffer m_SkyViewBanding;

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
        readonly int m_KernelSkyBandingIdx       = -1;

        // 反射走**另一个** .compute（见 VistaRuntimeResources 的理由：ImageBasedLighting.hlsl
        // 的 include 图会拖慢那九个大气核的每次迭代）。IVistaLutDispatcher 的每个方法
        // 都吃一个 ComputeShader 参数，所以多一份 CS 不需要改接口。
        readonly ComputeShader m_ReflectionCS;
        readonly int m_KernelSkyReflectionIdx       = -1;
        readonly int m_KernelSkyReflectionWideIdx   = -1;
        readonly int m_KernelSkyReflectionVerifyIdx = -1;

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

        /// <summary>
        /// 天空镜面反射 cubemap（64²、7 级 mip、RGBA16F）。挂到
        /// <c>RenderSettings.customReflectionTexture</c> 后由 URP 当 <c>unity_SpecCube0</c> 采。
        /// 未分配过时为 null。
        /// </summary>
        public RTHandle skyReflectionCube => m_SkyReflection;

        /// <summary>
        /// 反射的 UAV 中转纹理（6 层 × 7 级 mip 的 Tex2DArray）。compute 写它，
        /// 然后逐面 CopyTexture 进 <see cref="skyReflectionCube"/>。
        /// 存在的理由见 <see cref="VistaLutSlot.SkyReflectionArray"/> 的注释（Unity 不允许
        /// 把 Cube RT 绑到 RWTexture2DArray）。未分配过时为 null。
        /// </summary>
        public RTHandle skyReflectionArray => m_SkyReflectionArray;

        /// <summary>自检报告输出。只在 <see cref="EnsureSkyReflectionVerify"/> 之后非 null。</summary>
        public GraphicsBuffer skyReflectionVerifyBuffer => m_SkyReflectionVerify;

        /// <summary>banding 签名采样输出。只在 <see cref="EnsureSkyViewBanding"/> 之后非 null。</summary>
        public GraphicsBuffer skyViewBandingBuffer => m_SkyViewBanding;

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

        /// <summary>
        /// 反射 cubemap 是否可用。第四个独立开关，理由同上一条 ——
        /// 这个核挂了，天空、AP、漫反射间接光全都还是对的，只是镜面反射回落到
        /// 场景自带的反射探针（通常是一张静态天空盒），表现为"金属材质不跟着时间变"。
        /// 把它并进 <see cref="isValid"/> 会让"反射核编译失败"表现为"整个天空黑掉"。
        ///
        /// **两个滤波核都必须在**，不是"有一个就降级跑"。narrow 核只产 mip0，
        /// 缺了 wide 核会让 mip1~6 完全没被写过 —— 采样端按粗糙度取到那几级时读到的是
        /// 未初始化内容（黑或上一帧残留），比干脆回落到场景探针**更糟**：
        /// 前者是随机的错，后者是可解释的、美术能自己看出来的错。
        /// </summary>
        public bool isSkyReflectionValid => m_ReflectionCS != null
                                            && m_KernelSkyReflectionIdx >= 0
                                            && m_KernelSkyReflectionWideIdx >= 0;

        /// <param name="reflectionCS">
        /// 镜面反射预滤波核。可选 —— 只用大气 LUT 的调用方（LUT 预览窗口、大气数值自检）
        /// 传 null 即可，那时 <see cref="isSkyReflectionValid"/> 为 false，其余功能不受影响。
        /// </param>
        public VistaAtmosphereLuts(ComputeShader lutCS, ComputeShader reflectionCS = null)
        {
            m_ReflectionCS = reflectionCS;
            if (m_ReflectionCS != null)
            {
                if (m_ReflectionCS.HasKernel(k_KernelSkyReflection))
                    m_KernelSkyReflectionIdx = m_ReflectionCS.FindKernel(k_KernelSkyReflection);
                if (m_ReflectionCS.HasKernel(k_KernelSkyReflectionWide))
                    m_KernelSkyReflectionWideIdx = m_ReflectionCS.FindKernel(k_KernelSkyReflectionWide);
                if (m_ReflectionCS.HasKernel(k_KernelSkyReflectionVerify))
                    m_KernelSkyReflectionVerifyIdx = m_ReflectionCS.FindKernel(k_KernelSkyReflectionVerify);
            }

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
            if (m_LutCS.HasKernel(k_KernelSkyBanding))
                m_KernelSkyBandingIdx = m_LutCS.FindKernel(k_KernelSkyBanding);
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
        /// <remarks>
        /// 契约：这个返回值必须恒等于 <see cref="isAerialPerspectiveValid"/>。
        /// <c>VistaAtmosphereFeature</c> 用后者在**排入之前**决定要不要排入全屏合成 pass
        /// （那时本方法还没被调用），两者一旦分叉，合成 pass 就会去采一张这一帧没被写、
        /// 甚至已经释放的 3D 表。要加新的失败路径，得同时反映到那个属性上。
        /// </remarks>
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

        /// <summary>
        /// 记录期调用：按需分配反射 cubemap，并把请求的模式**解析成实际可用的模式**。
        ///
        /// 返回解析后的模式而不是 bool，是因为这里有一次真实的降级：
        /// <see cref="VistaSkyReflectionMode.AmbientSh"/> 需要 SH 缓冲存在，而 SH 有它自己的
        /// 有效性开关。让调用方去做这个判断，等于把"两个模块的可用性如何组合"复制到 pass 里，
        /// 而 pass 还要拿这个结论去决定声明哪些资源依赖 —— 两处判断走歧的症状是
        /// RenderGraph 抛"资源未声明"，或者更糟：读到一个未绑定的 StructuredBuffer。
        /// </summary>
        public VistaSkyReflectionMode PrepareSkyReflection(VistaSkyReflectionMode mode)
        {
            if (mode == VistaSkyReflectionMode.Off || !isSkyReflectionValid)
                return VistaSkyReflectionMode.Off;

            // SH 模式退到 LUT 模式而不是退到 Off：退到 Off 意味着镜面反射回落到场景自带的
            // 静态反射探针，画面上是"金属不跟时间变"；退到 LUT 只是多花 0.03 ms，
            // 而画面完全正确。移动端分级本来就是性能取舍，不是正确性前提。
            if (mode == VistaSkyReflectionMode.AmbientSh && (!isSkyAmbientShValid || m_SkyAmbientSh == null))
                mode = VistaSkyReflectionMode.SkyViewLut;

            AllocateSkyReflectionIfNeeded();
            return mode;
        }

        /// <summary>
        /// 仅供 Editor 自检：按需分配反射自检报告缓冲（17 × float4）。
        /// </summary>
        public bool EnsureSkyReflectionVerify()
        {
            if (!isSkyReflectionValid || m_KernelSkyReflectionVerifyIdx < 0) return false;

            m_SkyReflectionVerify ??= new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, k_ReflVerifyElementCount, sizeof(float) * 4)
            {
                name = "VistaSkyReflectionVerify",
            };
            return true;
        }

        /// <summary>
        /// 仅供 Editor 自检：按需分配 banding 签名缓冲（256 × float4）。
        /// </summary>
        public bool EnsureSkyViewBanding()
        {
            if (!isValid || m_KernelSkyBandingIdx < 0) return false;

            m_SkyViewBanding ??= new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, k_SkyBandingMaxCount, sizeof(float) * 4)
            {
                name = "VistaSkyViewBanding",
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

        /// <summary>
        /// 仅供 Editor 自检：按一组固定方向采样**已烘好的** Sky-View 表，把结果写进
        /// <see cref="skyViewBandingBuffer"/>。**必须在 <see cref="RenderSkyViewLut{T}"/> 之后调**。
        ///
        /// 关键约束：这个核绑的是 <c>_VistaSkyViewLut</c>（SRV），**不绑 RW 名字** ——
        /// 它要走的正是天空盒 shader 运行时那个硬件双线性入口，因为 banding 是
        /// **采样之后**才出现的现象（表里的值可以完全单调，而双线性 + 参数化 warp +
        /// fp16 存储叠起来仍能产出可见台阶）。绑成 UAV 就得手写取样，测的对象就跑掉了。
        /// </summary>
        /// <param name="mode">0 = 64 个固定世界方向（扫太阳用）；1 = 沿正对太阳的竖直大圆走弧。</param>
        /// <param name="arcStartDeg">mode 1 的起始仰角。</param>
        /// <param name="arcStepDeg">mode 1 的仰角步长。</param>
        /// <param name="count">采样点数，上限 <see cref="k_SkyBandingMaxCount"/>。</param>
        public void RenderSkyViewBanding<T>(
            T d, in VistaAtmosphereViewData view,
            int mode, float arcStartDeg, float arcStepDeg, int count)
            where T : struct, IVistaLutDispatcher
        {
            if (!isValid || m_KernelSkyBandingIdx < 0 || m_SkyViewBanding == null) return;

            count = Mathf.Clamp(count, 1, k_SkyBandingMaxCount);

            // Bind 推的是 _VistaViewPosKm / _VistaSunDirection / _VistaSkyViewLutSize，
            // 而 VistaSampleSkyViewLut 三个都要用。少推一次就会采到上一次的太阳方向 ——
            // 而这个自检恰好是在**扫太阳**，那种错的症状是"曲线整体平移一格"，
            // 二阶差分几乎不受影响，于是报告照样全绿。
            view.Bind(d, m_SkyViewWidth, m_SkyViewHeight);

            d.SetGlobalVector(VistaShaderIDs._VistaSkyBandingParams,
                new Vector4(mode, arcStartDeg, arcStepDeg, count));

            d.SetTexture(m_LutCS, m_KernelSkyBandingIdx,
                VistaShaderIDs._VistaSkyViewLut, VistaLutSlot.SkyView);
            d.SetBuffer(m_LutCS, m_KernelSkyBandingIdx,
                VistaShaderIDs._VistaSkyBandingRW, VistaLutBufferSlot.SkyViewBanding);
            d.Dispatch(m_LutCS, m_KernelSkyBandingIdx,
                VistaComputeUtils.DivRoundUp(count, 64), 1, 1);
        }

        // ====================================================================
        //  天空镜面反射 cubemap（逐帧）
        //
        //  LUT 模式读 SkyView（SRV），SH 模式读 SH 缓冲（SRV）；两种模式都只写
        //  cubemap（UAV）。**七级 mip 全在同一个 pass 里**：每级都独立地从源积分，
        //  级与级之间没有依赖，这张 cubemap 全程只有 UAV 一个状态。
        //  见 SkyReflection.compute 的头注（为什么不做渐进预滤波）。
        // ====================================================================

        /// <summary>
        /// 逐 mip 一趟 dispatch，GGX 预积分整张反射 cubemap。
        /// <paramref name="mode"/> 必须是 <see cref="PrepareSkyReflection"/> 返回的那个值 ——
        /// 传原始请求值会在 SH 不可用时去读一个未绑定的 buffer。
        /// </summary>
        /// <param name="mipMask">
        /// 仅供 Editor 耗时诊断：第 m 位为 0 时**跳过该 mip 的 dispatch**，但参数与 UAV
        /// 照常绑。默认 <c>~0</c>（全派），生产路径永远不传这个参数。
        ///
        /// 绑定刻意留在掩码**外面**，这是这个旋钮能用来做归因的前提：无论掩码是什么，
        /// CPU 侧发出的命令数完全相同（7 次 SetGlobalVector + 7 次 SetTextureMip），
        /// 于是两次测量相减得到的是纯 GPU 差值，不掺命令提交的抖动。
        /// 顺带把原来那个 <c>bindsOnly</c> 收成了本掩码的 <c>0</c> 这一个取值 ——
        /// 两个正交开关退化成一个，少一处组合状态要维护。
        ///
        /// 为什么需要它：这个 pass 原先占稳态链路的 79%（0.391 ms），而
        /// <c>mipMask: 0</c> 量到 0.000 ms —— 也就是说开销 100% 在 GPU 积分侧。
        /// 我原先猜是 CPU 侧命令重放（7 次 SetTextureMip 意味着逐 mip 建 UAV view），
        /// 这个诊断把那个假设否掉了。位掩码而不是单个 mip 序号，是因为归因需要两种口径 ——
        /// 单级隔离（<c>1 &lt;&lt; m</c>）与前缀累积（<c>(1 &lt;&lt; m+1) - 1</c>）——
        /// 前者干净但每次重复都打同一级，后者每次重复是真实的混合序列且
        /// 满掩码那一档必须回到整 pass 的耗时（一条自洽校验）。一个掩码把两种都表达了。
        ///
        /// 归因结论（RTX 3060 / D3D11，逐级隔离，min of 5×200）：mip3~6 合计 0.351 ms，
        /// 占 85%，而它们只产出 510/32766 个纹素（1.6%）；吞吐从 mip0 的 2.96 降到
        /// mip6 的 0.02 G样本/s，每次循环迭代暴露 295 ns —— 一次纹理取样的延迟，
        /// 完全没被隐藏。**病因是占用率，不是取样总量。** 逐级隔离之和 0.413 ms
        /// 与满掩码 0.414 ms 几乎相等，说明 7 趟 dispatch 之间没有重叠。
        /// 修法见下面的形状分界注释（引入 wide 核）。
        ///
        /// 引用口径：改完之后**反射单 pass 的数字不可引用** —— 三次复测给出
        /// 0.095（±37%）／0.101（±5%）／0.076（±132%），已经掉到这台机器的噪声地板下面。
        /// 可引用的是整链：稳态五 pass 从 0.494 ms（±3%）降到 0.170~0.198 ms（±2~4%）。
        /// 逐级 iso 现在只能读**占比**，不能读绝对值（多数级都带 ⚠）。
        /// 这个旋钮留着当回归工具：形状再改一次，同一份报告能立刻说出改到了哪几级。
        ///
        /// 用默认参数而不是复制一份诊断专用的方法：那两份的绑定序列必须永远一致，
        /// 而复制体一旦漏跟一次改动，诊断给出的分解就是错的 ——
        /// 且错的方向是"CPU 侧看起来更便宜"，恰好会把结论带反。
        /// 掩码是编译期常量传入，热路径上这个 <c>&amp;</c> 会被折掉。
        /// </param>
        public void RenderSkyReflection<T>(
            T d, in VistaAtmosphereViewData view, VistaSkyReflectionMode mode,
            int mipMask = ~0)
            where T : struct, IVistaLutDispatcher
        {
            if (!isSkyReflectionValid || m_SkyReflection == null || m_SkyReflectionArray == null) return;
            if (mode == VistaSkyReflectionMode.Off) return;

            // 两种模式共用一个 Bind，不按模式分支。SH 模式确实用不到 _VistaViewPosKm /
            // _VistaSkyViewLutSize（它不采 LUT），但省下这几个 SetGlobalVector 换来的是
            // "两条路径各推一套参数"，而漏推只会在其中一条上出错 —— 那种错在切模式做
            // A/B 对比时才暴露，恰好是 Demo 视频里最不想踩的时机。
            view.Bind(d, m_SkyViewWidth, m_SkyViewHeight);

            bool useSh = mode == VistaSkyReflectionMode.AmbientSh;
            // 两个 kernel 各绑一次。辐射来源的绑定是 per-kernel 状态，不是全局 ——
            // 只绑 narrow 那一个，wide 核读到的会是未绑定资源（D3D11 返回 0，
            // D3D12/Vulkan 未定义），症状是 mip1~6 全黑而 mip0 正常。
            if (useSh)
            {
                d.SetBuffer(m_ReflectionCS, m_KernelSkyReflectionIdx,
                    VistaShaderIDs._VistaSkyAmbientSh, VistaLutBufferSlot.SkyAmbientSh);
                d.SetBuffer(m_ReflectionCS, m_KernelSkyReflectionWideIdx,
                    VistaShaderIDs._VistaSkyAmbientSh, VistaLutBufferSlot.SkyAmbientSh);
            }
            else
            {
                d.SetTexture(m_ReflectionCS, m_KernelSkyReflectionIdx,
                    VistaShaderIDs._VistaSkyViewLut, VistaLutSlot.SkyView);
                d.SetTexture(m_ReflectionCS, m_KernelSkyReflectionWideIdx,
                    VistaShaderIDs._VistaSkyViewLut, VistaLutSlot.SkyView);
            }

            float source = useSh ? 1f : 0f;   // 与 VISTA_SKY_REFLECTION_SRC_* 对应

            for (int mip = 0; mip < k_SkyReflectionMipCount; ++mip)
            {
                int size = k_SkyReflectionSize >> mip;

                // ---- 形状分界：K ≥ 64 才走 wide ----
                // 门槛的含义是"64 条 lane 每条至少分到一个样本、没有空转"。
                // LUT 模式下 K = min(256, 16 << mip)，于是 mip2 起满足（64/128/256/256/256）；
                // mip1 的 K = 32 只喂得饱一半 lane，SH 模式 K 恒为 16 更喂不饱 ——
                // 那两种情况一律回 narrow。
                //
                // 这个分界是**量出来的，不是推出来的**。第一版写的是 `mip > 0`，
                // 结果 mip1 从 0.021 退化到 0.041 ms（反而成了最大单项），
                // 而 mip2 从 0.032 改善到 0.017 —— 分水岭正好落在 K=32 与 K=64 之间。
                // 原因是 mip1 在 narrow 下本来就是全场吞吐最高的一级（8.95 G样本/s）：
                // 96 个组、6144 条线程、每条 32 深，延迟早就被别的 warp 藏住了，
                // wide 只是给它多加了一轮 6 次 barrier 的归约。
                //
                // 上面这几个逐级数字**只有 2× 这个量级可信**：单级耗时在 0.01~0.02 ms 档，
                // 复测离散度到 ±27~130%，绝对值不可引用。之所以还敢下结论，是因为
                // mip1 的 0.041 vs 0.021 差了一倍、远超那一级的离散度；而改完之后
                // 反射整 pass 的总数在噪声内**没变**（0.095 ±37% → 0.101 ±5% → 0.076 ±132%），
                // 所以正确的说法是"mip1 这一级快了 2×，pass 总数没有可测的变化"，
                // 不是"改快了"。
                //
                // 也没有为 K < 64 再切一层「一组多纹素 + 组内分段归约」：按 mip2 达到的
                // 5.89 G样本/s 折算，mip1 那 196.6k 个样本要 0.033 ms，比 narrow 的
                // 0.021 还慢 —— 多一套索引换更差的结果。
                //
                // 门槛这里写成 mip 序号而不是重算一遍 K：K 的定义在
                // SkyReflection.hlsl 的 VistaSkyReflectionSampleCount，在运行时抄第二份
                // 就多一处会走歧的真源。代价是这个 2 与那个函数隐式绑着，
                // 所以自检的**判据 4a** 会把 HLSL 侧的 K 逐 mip 导出到该行的 .x，
                // C# 侧拿它跟 k_SkyReflectionWideThreshold 和这里的 `mip >= 2` 对账 ——
                // K 的定义改了而分界没跟上，那一行会红。
                // （必须让它会红：症状是某一级性能悄悄退化，不是错图，画面上看不出来。）
                bool wide = !useSh && mip >= 2;
                int kernel = wide ? m_KernelSkyReflectionWideIdx : m_KernelSkyReflectionIdx;

                d.SetGlobalVector(VistaShaderIDs._VistaSkyReflectionParams,
                    new Vector4(size, mip, source, 0f));

                // 必须指到具体 mip。不指的话默认写 mip 0，七趟全打在同一级上，
                // 其余六级保持分配后的未初始化内容 —— 见 IVistaLutDispatcher.SetTextureMip。
                //
                // 绑的是 Tex2DArray 中转纹理，不是 cube 本身：Unity 的绑定校验不接受
                // Cube RT 绑到 RWTexture2DArray（"expected 5, got 4"）。
                // 见 VistaLutSlot.SkyReflectionArray。
                d.SetTextureMip(m_ReflectionCS, kernel,
                    VistaShaderIDs._VistaSkyReflectionRW, VistaLutSlot.SkyReflectionArray, mip);

                if ((mipMask & (1 << mip)) == 0)
                    continue;

                if (wide)
                {
                    // (size·size, 6, 1) 组，**精确覆盖**：一组一纹素，y = 面。
                    // 精确不只是省一个边界 if —— wide 核里三处 GroupMemoryBarrier 要求
                    // 之前不存在线程相关的 early return（FXC 的判据是语法上的），
                    // 所以"整除"是那个核能编过的前提，改这里的形状要连带看核。
                    d.Dispatch(m_ReflectionCS, kernel, size * size, 6, 1);
                }
                else
                {
                    d.Dispatch(m_ReflectionCS, kernel,
                        VistaComputeUtils.DivRoundUp(size, 8),
                        VistaComputeUtils.DivRoundUp(size, 8), 6);
                }
            }
        }

        /// <summary>
        /// 仅供 Editor 自检：逐面 round-trip / 均值恒等式 / mip↔粗糙度映射。
        /// **必须在 <see cref="RenderSkyReflection{T}"/> 与 <see cref="RenderSkyAmbientSh{T}"/>
        /// 之后调用**（要把 cubemap 当 SRV 读回来，并与 SH 的 L_00·Y00 对照）。
        /// </summary>
        public void RenderSkyReflectionVerify<T>(T d, in VistaAtmosphereViewData view)
            where T : struct, IVistaLutDispatcher
        {
            if (!isSkyReflectionValid || m_KernelSkyReflectionVerifyIdx < 0) return;
            if (m_SkyReflection == null || m_SkyReflectionVerify == null) return;

            view.Bind(d, m_SkyViewWidth, m_SkyViewHeight);

            // 自检永远按 LUT 模式的口径比对：判据 1 要的是"cube mip0 == LUT"，
            // 而 SH 模式下 cube 本来就不该等于 LUT（它带限在 l≤2，正是 #5b 存在的理由）。
            // size 传满级边长，mip 传 0 —— 组 7 会把这个 size 报出来给 C# 比常量。
            d.SetGlobalVector(VistaShaderIDs._VistaSkyReflectionParams,
                new Vector4(k_SkyReflectionSize, 0f, 0f, 0f));

            d.SetTexture(m_ReflectionCS, m_KernelSkyReflectionVerifyIdx,
                VistaShaderIDs._VistaSkyViewLut, VistaLutSlot.SkyView);
            // 同一张 cubemap 在这里是 **SRV**（TEXTURECUBE），绑到只读那个名字上 ——
            // 绑到 RW 名字上会让硬件双线性与 mip 采样整个失效，而判据 1 恰好靠它们。
            d.SetTexture(m_ReflectionCS, m_KernelSkyReflectionVerifyIdx,
                VistaShaderIDs._VistaSkyReflection, VistaLutSlot.SkyReflection);
            d.SetBuffer(m_ReflectionCS, m_KernelSkyReflectionVerifyIdx,
                VistaShaderIDs._VistaSkyAmbientSh, VistaLutBufferSlot.SkyAmbientSh);
            d.SetBuffer(m_ReflectionCS, m_KernelSkyReflectionVerifyIdx,
                VistaShaderIDs._VistaSkyReflectionVerifyRW, VistaLutBufferSlot.SkyReflectionVerify);

            d.Dispatch(m_ReflectionCS, m_KernelSkyReflectionVerifyIdx, k_ReflVerifyGroupCount, 1, 1);
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

            // 逐视图常量也要在这里推。AP 的三个核都要读相机位置与太阳方向，
            // 而这条链路**不能**假定 Sky-View 先跑过：本方法自己的注释就写着
            // 「与 Sky-View 之间没有依赖，两者可以并行」，一旦真的重排或并行，
            // AP 读到的就是上一个绑定者留下的视图。多相机下更直接 —— 反射探针
            // 那六个面各绑一次自己的视图，主相机的 AP 排在后面就会拿到探针的位置。
            // （这个 bug 是 Task #7 换视角复核时暴露的：换了相机高度与太阳仰角，
            //   误差曲线一个数都没变，因为核根本没看到新视图。）
            view.Bind(d, m_SkyViewWidth, m_SkyViewHeight);
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

            // 与正式核同理，逐视图常量必须自己推一遍，不能指望别的 pass 先绑过。
            view.Bind(d, m_SkyViewWidth, m_SkyViewHeight);
            view.BindAerialPerspective(d, settings);

            d.SetTexture(m_LutCS, m_KernelApRoundTripIdx,
                VistaShaderIDs._VistaApScatterLutRW, VistaLutSlot.ApScatter);
            d.Dispatch(m_LutCS, m_KernelApRoundTripIdx,
                VistaComputeUtils.DivRoundUp(settings.depth, 64), 1, 1);
        }

        /// <summary>
        /// 仅供 Editor 自检：切片分布质量测量。**必须在 <see cref="RenderAerialPerspectiveLut{T}"/>
        /// 之后调用**，因为它要把散射表当 SRV 读回来做对照。
        /// 结果写进 apTransmittanceLut 的两列（<c>y = 0</c>，要求表宽 ≥ 4）：
        /// <c>(0, 0, i)</c> = errCenter、errMid、参考解灰度亮度、LUT 灰度亮度；
        /// <c>(3, 0, i)</c> = errMidT·<see cref="k_ApErrorScale"/>、参考解灰度 T、LUT 灰度 T、参考解中点亮度。
        /// 另有 <c>(1, 0, 0)</c> / <c>(2, 0, 0)</c> 两个区间诊断（见核内注释）。
        /// 调完必须再跑一次正式核覆盖回去。
        /// </summary>
        public void RenderApSliceError<T>(
            T d, in VistaAtmosphereViewData view, VistaAerialPerspectiveSettings settings)
            where T : struct, IVistaLutDispatcher
        {
            if (!isAerialPerspectiveValid || m_KernelApSliceErrorIdx < 0 || m_ApScatter == null) return;

            // 与正式核同理，逐视图常量必须自己推一遍，不能指望别的 pass 先绑过。
            view.Bind(d, m_SkyViewWidth, m_SkyViewHeight);
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

        /// <summary>立即模式的 banding 签名采样。调用前需先 <see cref="EnsureSkyViewBanding"/>。</summary>
        public void RenderSkyViewBanding(
            CommandBuffer cmd, in VistaAtmosphereViewData view,
            int mode, float arcStartDeg, float arcStepDeg, int count)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            RenderSkyViewBanding(new VistaImmediateLutDispatcher(cmd, this), view,
                mode, arcStartDeg, arcStepDeg, count);
        }

        /// <summary>
        /// 立即模式的反射 cubemap。调用前需先 <see cref="PrepareSkyReflection"/>，
        /// 并把它的返回值原样传进来。
        /// </summary>
        public void RenderSkyReflection(
            CommandBuffer cmd, in VistaAtmosphereViewData view, VistaSkyReflectionMode mode)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (!isSkyReflectionValid || mode == VistaSkyReflectionMode.Off) return;

            RenderSkyReflection(new VistaImmediateLutDispatcher(cmd, this), view, mode);
            // 立即模式不需要显式 barrier：原生 CommandBuffer 的状态转换由图形层自动插，
            // 所以 dispatch 与 copy 录在同一个 cmd 里是安全的（与三张静态表能不能同 pass
            // 是同一条区别 —— 那个约束只存在于 RenderGraph）。
            CopySkyReflectionToCube(cmd);
            cmd.SetGlobalTexture(VistaShaderIDs._VistaSkyReflection, m_SkyReflection);
        }

        /// <summary>立即模式的反射自检。调用前需先 <see cref="EnsureSkyReflectionVerify"/>。</summary>
        public void RenderSkyReflectionVerify(CommandBuffer cmd, in VistaAtmosphereViewData view)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            RenderSkyReflectionVerify(new VistaImmediateLutDispatcher(cmd, this), view);
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

        void AllocateSkyReflectionIfNeeded()
        {
            if (m_SkyReflection != null) return;

            m_SkyReflection = RTHandles.Alloc(
                k_SkyReflectionSize, k_SkyReflectionSize,
                // fp16：与 SkyView 同一份数据同一个量级（天顶约 5e3、日面附近 1e5），
                // 圆盘不进这张图，所以不会碰 65504 上限。理由与 AllocateSkyViewIfNeeded 相同。
                format: GraphicsFormat.R16G16B16A16_SFloat,
                // **slices: 1，不是 6。** RTHandles.Alloc 把 slices 直接塞给
                // RenderTextureDescriptor.volumeDepth，而 Cube 维度下六个面不是 volume 切片，
                // volumeDepth 必须留在 1（core 自己的 PathTracing/CubemapRender.cs:152-160
                // 建 Cube RT 时就是 volumeDepth = 1）。写 6 的话描述符自相矛盾。
                slices: 1,
                // **Trilinear 而不是 Bilinear**：URP 的 GlossyEnvironmentReflection 用
                // SAMPLE_TEXTURECUBE_LOD 传一个连续的 mip 值（粗糙度算出来的），
                // Bilinear 会把它吸附到最近一级 —— 症状是粗糙度渐变的物体上出现横向"档位",
                // 尤其在 0.34（mip3）附近，因为 mip↔粗糙度是非线性的、中段最密。
                filterMode: FilterMode.Trilinear,
                // Clamp：cubemap 的 uv 在面内，越界由硬件的跨面寻址处理，
                // 但 Repeat 会在面边缘绕回同一面的另一侧，表现为接缝处的镜像条纹。
                wrapMode: TextureWrapMode.Clamp,
                dimension: TextureDimension.Cube,
                // **不开 enableRandomWrite。** 它现在只是 CopyTexture 的目标 + SRV。
                // 开着也能跑，但那会让"这张图是 compute 直接写的"这个已经被验伪的假设
                // 留在代码里 —— 下一个人（包括三个月后的我）会照它去 debug。
                useMipMap: true,
                // 七级 mip 全是自己积出来的。开 autoGenerateMips 会在每次渲染到它之后
                // 由驱动做一遍 box downsample，把 mip1..6 覆盖成"mip0 的模糊"——
                // 那正是被否掉的渐进预滤波方案的近似，而且是在背后悄悄发生的。
                autoGenerateMips: false,
                name: "VistaSkyReflectionCube");

            // UAV 中转：与 cube 逐字段同规格（尺寸/格式/mip 级数必须一致，
            // 否则 CopyTexture 会在运行时报 "dimensions must match"）。
            // slices 这里**才是** 6 —— Tex2DArray 的层数就是 volumeDepth。
            m_SkyReflectionArray = RTHandles.Alloc(
                k_SkyReflectionSize, k_SkyReflectionSize,
                format: GraphicsFormat.R16G16B16A16_SFloat,
                slices: 6,
                // 中转纹理不会被采样，filterMode 无关紧要 —— 但也不留 Point：
                // 它在 RenderDoc / Frame Debugger 里是排查反射问题的第一站，
                // 预览图跟着 cube 的滤波模式走比较不容易看错。
                filterMode: FilterMode.Trilinear,
                wrapMode: TextureWrapMode.Clamp,
                dimension: TextureDimension.Tex2DArray,
                enableRandomWrite: true,
                useMipMap: true,
                autoGenerateMips: false,
                name: "VistaSkyReflectionArray");
        }

        /// <summary>
        /// 把 UAV 中转纹理逐面搬进 cubemap。
        ///
        /// 六次调用而不是一次整体拷：<c>CopyTexture(src, srcElement, dst, dstElement)</c>
        /// 一次只搬一个 element，但它搬的是那个 element 的**全部 mip**（见
        /// core 的 IUnsafeCommandBuffer.cs:440 的文档），所以 6 次而不是 42 次。
        ///
        /// element 序号直接当 CubemapFace 用（0=+X 1=-X 2=+Y 3=-Y 4=+Z 5=-Z），
        /// 与核里 VistaSkyReflectionTexelToDirection 的 face 参数是同一个约定。
        /// 这一层映射对不对**不靠我记**：自检判据 1 是逐面比对 cube 的 SRV 采样值与
        /// LUT 值，任何一面搬错位置都会让那一面单独炸红。
        ///
        /// 需要原生 CommandBuffer：CopyTexture 只存在于 IUnsafeCommandBuffer，
        /// ComputeCommandBuffer / RasterCommandBuffer 上都没有 —— 所以 RenderGraph 侧
        /// 必须是 AddUnsafePass。（同一类不对称还有 RequestAsyncReadback 与 SetGlobalBuffer。）
        /// </summary>
        public void CopySkyReflectionToCube(CommandBuffer cmd)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (m_SkyReflection == null || m_SkyReflectionArray == null) return;

            for (int face = 0; face < 6; ++face)
                cmd.CopyTexture(m_SkyReflectionArray, face, m_SkyReflection, face);
        }

        public void Dispose()
        {
            m_Transmittance?.Release();
            m_Transmittance = null;
            m_MultiScattering?.Release();
            m_MultiScattering = null;
            m_SkyView?.Release();
            m_SkyView = null;
            m_SkyReflection?.Release();
            m_SkyReflection = null;
            m_SkyReflectionArray?.Release();
            m_SkyReflectionArray = null;
            ReleaseAerialPerspective();
            // GraphicsBuffer 是 IDisposable 而非 RTHandle，漏掉它不会有 RTHandle 那种
            // "泄漏检测"日志，只会安静地涨显存 —— Editor 里反复域重载时尤其明显。
            m_SkyAmbientSh?.Dispose();
            m_SkyAmbientSh = null;
            m_SkyAmbientShRef?.Dispose();
            m_SkyAmbientShRef = null;
            m_SkyReflectionVerify?.Dispose();
            m_SkyReflectionVerify = null;
            m_SkyViewBanding?.Dispose();
            m_SkyViewBanding = null;
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
