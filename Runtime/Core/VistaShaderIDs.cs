using UnityEngine;

namespace Vista
{
    /// <summary>
    /// Shader 属性 ID 缓存。`Shader.PropertyToID` 是字符串哈希，每帧调用是可测量的浪费，
    /// 统一在此静态化。命名与 HLSL 中的变量名严格一一对应，改名必须两边同步。
    /// </summary>
    public static class VistaShaderIDs
    {
        // ---- Atmosphere: VistaAtmosphereCB (ShaderLibrary/AtmosphereDef.hlsl) ----
        public static readonly int _VistaRayleigh              = Shader.PropertyToID("_VistaRayleigh");
        public static readonly int _VistaMieScatter            = Shader.PropertyToID("_VistaMieScatter");
        public static readonly int _VistaMieExtinct            = Shader.PropertyToID("_VistaMieExtinct");
        public static readonly int _VistaOzone                 = Shader.PropertyToID("_VistaOzone");
        public static readonly int _VistaOzoneTent             = Shader.PropertyToID("_VistaOzoneTent");
        public static readonly int _VistaRadius                = Shader.PropertyToID("_VistaRadius");
        public static readonly int _VistaGround                = Shader.PropertyToID("_VistaGround");
        public static readonly int _VistaSun                   = Shader.PropertyToID("_VistaSun");
        public static readonly int _VistaTransmittanceLutSize  = Shader.PropertyToID("_VistaTransmittanceLutSize");

        // ---- Atmosphere: VistaAtmospherePerViewCB ----
        public static readonly int _VistaPlanetCenterKm        = Shader.PropertyToID("_VistaPlanetCenterKm");
        public static readonly int _VistaViewPosKm             = Shader.PropertyToID("_VistaViewPosKm");
        public static readonly int _VistaSunDirection          = Shader.PropertyToID("_VistaSunDirection");
        public static readonly int _VistaSkyViewLutSize        = Shader.PropertyToID("_VistaSkyViewLutSize");
        public static readonly int _VistaApParams              = Shader.PropertyToID("_VistaApParams");
        public static readonly int _VistaApSize                = Shader.PropertyToID("_VistaApSize");
        public static readonly int _VistaApRayBL               = Shader.PropertyToID("_VistaApRayBL");
        public static readonly int _VistaApRayBR               = Shader.PropertyToID("_VistaApRayBR");
        public static readonly int _VistaApRayTL               = Shader.PropertyToID("_VistaApRayTL");
        public static readonly int _VistaApRayTR               = Shader.PropertyToID("_VistaApRayTR");
        public static readonly int _VistaApFlags               = Shader.PropertyToID("_VistaApFlags");
        public static readonly int _VistaApConsumer            = Shader.PropertyToID("_VistaApConsumer");

        /// <summary>
        /// 平行光颜色里已含的那一份太阳透射率（参考高度处）。xyz = T_ref，w = 逐像素修正是否生效。
        /// 与 <c>_VistaApConsumer</c> 同理，**每帧无条件下发**。
        /// </summary>
        public static readonly int _VistaSunTransmittanceRef   = Shader.PropertyToID("_VistaSunTransmittanceRef");

        // ---- Atmosphere: LUT 绑定点 ----
        public static readonly int _VistaTransmittanceLut       = Shader.PropertyToID("_VistaTransmittanceLut");
        public static readonly int _VistaTransmittanceLutRW     = Shader.PropertyToID("_VistaTransmittanceLutRW");
        public static readonly int _VistaMultiScatteringLut     = Shader.PropertyToID("_VistaMultiScatteringLut");
        public static readonly int _VistaMultiScatteringLutRW   = Shader.PropertyToID("_VistaMultiScatteringLutRW");
        public static readonly int _VistaSkyViewLut             = Shader.PropertyToID("_VistaSkyViewLut");
        public static readonly int _VistaSkyViewLutRW           = Shader.PropertyToID("_VistaSkyViewLutRW");
        public static readonly int _VistaApScatterLut           = Shader.PropertyToID("_VistaApScatterLut");
        public static readonly int _VistaApScatterLutRW         = Shader.PropertyToID("_VistaApScatterLutRW");
        public static readonly int _VistaApTransmittanceLut     = Shader.PropertyToID("_VistaApTransmittanceLut");
        public static readonly int _VistaApTransmittanceLutRW   = Shader.PropertyToID("_VistaApTransmittanceLutRW");
        // 自检专用：切片误差核要把散射表当 SRV 读回来，不能和 UAV 用同一个绑定点
        public static readonly int _VistaApScatterLutRead       = Shader.PropertyToID("_VistaApScatterLutRead");

        // ---- Atmosphere: 天空环境光 SH（StructuredBuffer，非纹理）----
        public static readonly int _VistaSkyAmbientSh            = Shader.PropertyToID("_VistaSkyAmbientSh");
        public static readonly int _VistaSkyAmbientShRW          = Shader.PropertyToID("_VistaSkyAmbientShRW");
        // 自检专用：参考解（逐法线的辐照度对照）
        public static readonly int _VistaSkyAmbientShRefRW       = Shader.PropertyToID("_VistaSkyAmbientShRefRW");

        // ---- Fog: VistaFogCB (ShaderLibrary/FogMedium.hlsl) ----
        // 失能态是全零，所以「没下发」只能表现为没有雾。见 FogMedium.hlsl 的「常量」一节。
        public static readonly int _VistaFogAlbedo                = Shader.PropertyToID("_VistaFogAlbedo");
        public static readonly int _VistaFogExtinct               = Shader.PropertyToID("_VistaFogExtinct");
        public static readonly int _VistaFogHeight                = Shader.PropertyToID("_VistaFogHeight");

        // ---- Volumetrics: VistaFroxelCB (ShaderLibrary/FroxelVolume.hlsl) ----
        // 失能态同样是全零：logRatio = 0 且 rcpLog = 0 ⇒ 编码坐标恒 0、距离恒 0，
        // 不是 NaN。与 VistaFogCB 同一条「关掉 = 零态」的约定。
        public static readonly int _VistaFroxelRange              = Shader.PropertyToID("_VistaFroxelRange");
        public static readonly int _VistaFroxelSize               = Shader.PropertyToID("_VistaFroxelSize");
        // xyz: 相机世界位置 (m)，w: 阴影贴图是否已绑定（1 = 是）。
        //
        // 为什么不复用 _VistaViewPosKm：那个值在 6360 km 量级上，fp32 的 ulp 是 0.49 m，
        // 拿它当阴影查询的起点会让阴影坐标按半米量化 —— 症状是光柱边缘随相机移动跳格。
        // 也不用 URP 的 _WorldSpaceCameraPos：Editor 立即模式（自检）下那个全局
        // 由上一个渲染过的相机留着，而自检根本没有相机。
        //
        // w 分量作阴影可用性开关而不是靠 shader keyword：keyword 漏设的症状是
        // 「整个场景没有光柱、且不报错」（URP 的 MainLightRealtimeShadow 在
        // MAIN_LIGHT_CALCULATE_SHADOWS 未定义时直接 return 1.0），而一个 CPU 下发的
        // uniform 可以被判据直接读出来点名。零态（全零）= 无阴影 = 恒为 1，与其余
        // cbuffer 同一条「关掉 = 零态」的约定。
        public static readonly int _VistaFroxelCameraWS           = Shader.PropertyToID("_VistaFroxelCameraWS");

        // ---- Volumetrics: 时间重投影与抖动（#22，VistaFroxelReprojection 下发）----
        // 整组的零态 = 失能：历史权重 0（纯本帧）、抖动幅度 0（恒在格心）、
        // 上一帧范围全零（logRatio = 0 ⇒ 解码距离恒 0，不是 NaN）。
        // 特别地，历史权重那一位的零态**必须**是「不用历史」——
        // 反过来的话，一个没下发的帧会去混一张未初始化的 fp16 显存，而那里可能是 NaN。
        //
        // 上一帧的 viewProj。Unity 的 GL 风格 clip space（y 向上、w > 0 = 在相机前方），
        // **不**过 GL.GetGPUProjectionMatrix —— 理由写在 VistaFroxelReprojection.Update 里：
        // 这个 uv 是喂给 VistaApFroxelRayDirection 的（uv.y 自下而上），不是采屏幕纹理。
        public static readonly int _VistaFroxelPrevViewProj       = Shader.PropertyToID("_VistaFroxelPrevViewProj");
        // 上一帧的分片范围 (near, far, logRatio, 1/logRatio)，米。
        // 必须是**上一帧那份**：近远距离不进纹理重分配的脏检查，所以历史表里的片
        // 可能是另一套 near/far 下的距离。拿本帧的范围去查历史，症状是改阴影距离的
        // 那一帧雾整体前后错一下 —— 一个「看起来物理上讲得通的漂移」。
        public static readonly int _VistaFroxelPrevRange          = Shader.PropertyToID("_VistaFroxelPrevRange");
        // xyz: 上一帧相机世界位置 (m)；w: **历史**的混合权重 ∈ [0,1]。
        public static readonly int _VistaFroxelPrevCameraWS       = Shader.PropertyToID("_VistaFroxelPrevCameraWS");
        // xyz: R3 塑性常数 Kronecker 序列的本帧相位 frac(frameIndex · α) ∈ [0,1)³。
        public static readonly int _VistaFroxelJitterPhase        = Shader.PropertyToID("_VistaFroxelJitterPhase");
        // x: 横向抖动幅度（单位 = 一格宽），y: 深度抖动幅度（单位 = 一片厚）。
        public static readonly int _VistaFroxelJitter             = Shader.PropertyToID("_VistaFroxelJitter");
        // x: 亮度死区下端，y: 1/(上端 − 下端)。宽度由 C# 保证 > 0（见 ResolveLuminanceReject）。
        public static readonly int _VistaFroxelReprojParams       = Shader.PropertyToID("_VistaFroxelReprojParams");
        // 自检专用：喂给探针核的合成上一帧相机位移 (xyz, 角色标志)。
        // 线上路径一个字节都不下发 —— 零态 = 角色 0 = 探针整核早退。
        //   1 = 在线读数（含抖动的亮度散布）  2 = 静止恒等性
        //   3 = 位移驱动的四条拒绝分支        4 = 合成 hist 驱动的两条（NaN / 亮度）
        // 角色编码写在这里而不是只写在 shader 里，是因为 C# 侧要按角色分七趟派发；
        // 两边各写一份注释的话，「换了编码只改了一边」的症状是某一格计数恒为 0，
        // 而 0 在那些格子里是**合法**读数。
        public static readonly int _VistaFroxelReprojProbe        = Shader.PropertyToID("_VistaFroxelReprojProbe");

        // ---- Volumetrics: froxel 体的三张表 ----
        // 注入表有三个绑定点：本帧写用 RW（RWTexture3D），本帧读用 Read（Texture3D，
        // 给积分与判据），历史帧读用 History（另一张资源的 SRV，给 #22a 的重投影）。
        // 同一张纹理同时绑 UAV 与 SRV 是 UB，与反射那张中转表是同一条教训。
        //
        // 历史帧**没有** RW 绑定点：谁都不写它 —— 双缓冲的交换只改写下标，
        // 本帧写的永远是 _VistaFroxelInjectionRW 指向的那张。留一个没人用的 RW 绑定点
        // 等于「一段永远不会被发现写错的代码」。
        public static readonly int _VistaFroxelInjectionRW        = Shader.PropertyToID("_VistaFroxelInjectionRW");
        public static readonly int _VistaFroxelInjectionRead      = Shader.PropertyToID("_VistaFroxelInjectionRead");
        public static readonly int _VistaFroxelInjectionHistory   = Shader.PropertyToID("_VistaFroxelInjectionHistory");
        public static readonly int _VistaFroxelIntegralRW         = Shader.PropertyToID("_VistaFroxelIntegralRW");
        public static readonly int _VistaFroxelIntegral           = Shader.PropertyToID("_VistaFroxelIntegral");
        // 自检专用：逐片的分布报告
        public static readonly int _VistaFroxelSliceReportRW      = Shader.PropertyToID("_VistaFroxelSliceReportRW");
        // 自检专用：阴影覆盖性探针（min/max 的定点编码 + 关键字状态）
        public static readonly int _VistaFroxelShadowProbeRW      = Shader.PropertyToID("_VistaFroxelShadowProbeRW");
        // 自检专用：逐片的积分报告（原始读数，误差在 C# 侧算）
        public static readonly int _VistaFroxelIntegrationReportRW = Shader.PropertyToID("_VistaFroxelIntegrationReportRW");
        // 自检专用：积分判据的合成介质 (σ_t 1/km, 源项基准 S, 0, 0)。
        // 线上路径**一个字节都不下发** —— 它的零态就是「没有布景」，
        // 所以这条绑定点写错的症状是判据全零，而不是画面上多一层看不出来的雾。
        public static readonly int _VistaFroxelSynthMedium        = Shader.PropertyToID("_VistaFroxelSynthMedium");
        // 调试视图专用：x = gain，y = 单片档的切片下标（已夹紧），z = 档位，w 保留。
        // 走 MaterialPropertyBlock 下发，不是全局 —— 那一趟 pass 因此不声明
        // AllowGlobalStateModification，「它不改全局」在代码里读得出来。
        public static readonly int _VistaFroxelDebugParams         = Shader.PropertyToID("_VistaFroxelDebugParams");

        // ---- Atmosphere: banding 签名（仅 Editor 自检）----
        // 走的是**运行时那个采样入口**，所以它读 _VistaSkyViewLut（SRV），
        // 而不是 _VistaSkyViewLutRW —— 同一张纹理同时绑 UAV 与 SRV 是 UB。
        public static readonly int _VistaSkyBandingParams         = Shader.PropertyToID("_VistaSkyBandingParams");
        public static readonly int _VistaSkyBandingRW             = Shader.PropertyToID("_VistaSkyBandingRW");

        // ---- Atmosphere: 天空像素的雾闭式解判据（#18b，仅 Editor 自检）----
        // 这个核不采任何 LUT（它自己 march），但要读 _VistaSkyAmbientSh —— 雾的
        // 环境项必须与 AP kernel 用同一份天光，否则差值里混进一个与雾无关的常量偏置。
        public static readonly int _VistaSkyFogParams             = Shader.PropertyToID("_VistaSkyFogParams");
        public static readonly int _VistaSkyFogRW                 = Shader.PropertyToID("_VistaSkyFogRW");

        // ---- Atmosphere: 天空镜面反射 cubemap ----
        // 注意 RW 那个在 HLSL 里是 RWTexture2DArray（cube 的 UAV view 就是 2D array view），
        // 而只读那个是 TEXTURECUBE。同一张资源、两种 view，绑定点必须分开。
        public static readonly int _VistaSkyReflection            = Shader.PropertyToID("_VistaSkyReflection");
        public static readonly int _VistaSkyReflectionRW          = Shader.PropertyToID("_VistaSkyReflectionRW");
        public static readonly int _VistaSkyReflectionParams      = Shader.PropertyToID("_VistaSkyReflectionParams");
        // 自检专用：逐面 round-trip / 均值恒等式 / mip↔粗糙度映射
        public static readonly int _VistaSkyReflectionVerifyRW    = Shader.PropertyToID("_VistaSkyReflectionVerifyRW");
    }
}
