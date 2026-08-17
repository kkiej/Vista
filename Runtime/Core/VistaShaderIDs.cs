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
    }
}
