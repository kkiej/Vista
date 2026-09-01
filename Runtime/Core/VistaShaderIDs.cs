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

        // ---- Atmosphere: banding 签名（仅 Editor 自检）----
        // 走的是**运行时那个采样入口**，所以它读 _VistaSkyViewLut（SRV），
        // 而不是 _VistaSkyViewLutRW —— 同一张纹理同时绑 UAV 与 SRV 是 UB。
        public static readonly int _VistaSkyBandingParams         = Shader.PropertyToID("_VistaSkyBandingParams");
        public static readonly int _VistaSkyBandingRW             = Shader.PropertyToID("_VistaSkyBandingRW");

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
