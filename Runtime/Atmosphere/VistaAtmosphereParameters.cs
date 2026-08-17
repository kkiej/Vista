using System;
using UnityEngine;

namespace Vista
{
    /// <summary>
    /// 物理大气参数。单位约定与 <c>ShaderLibrary/AtmosphereDef.hlsl</c> 头部注释一致：
    /// 长度 km，散射/消光 1/km，太阳照度 lux。
    ///
    /// 默认值为地球标准大气，取自 Bruneton &amp; Neyret 2008 / Hillaire 2020 的参考实现。
    /// 面板上暴露的是**物理量**而不是艺术化的"雾色/天空色"——艺术化覆盖放在
    /// Step 2 的 TimeOfDayProfile 层，不污染这一层。
    /// </summary>
    [Serializable]
    public class VistaAtmosphereParameters : IEquatable<VistaAtmosphereParameters>
    {
        // ------------------------------------------------------------------ 几何
        [Header("几何")]
        [Tooltip("星球半径 (km)。地球 6360。")]
        [Min(1f)] public float bottomRadius = 6360f;

        [Tooltip("大气层厚度 (km)。地球 100。")]
        [Min(1f)] public float atmosphereThickness = 100f;

        // ---------------------------------------------------------------- Rayleigh
        [Header("Rayleigh（空气分子，天空的蓝色来源）")]
        [Tooltip("散射系数 (1/km)。地球 (5.802, 13.558, 33.100)e-3。Rayleigh 无吸收，消光 == 散射。")]
        public Vector3 rayleighScattering = new Vector3(5.802e-3f, 13.558e-3f, 33.100e-3f);

        [Tooltip("密度标高 (km)。地球 8。")]
        [Min(0.01f)] public float rayleighScaleHeight = 8f;

        // -------------------------------------------------------------------- Mie
        [Header("Mie（气溶胶，太阳周围光晕与雾霾感）")]
        [Tooltip("散射系数 (1/km)，各通道通常相等。地球 3.996e-3。")]
        public Vector3 mieScattering = new Vector3(3.996e-3f, 3.996e-3f, 3.996e-3f);

        [Tooltip("消光系数 (1/km)，须 >= 散射，差值即吸收。地球 4.40e-3。")]
        public Vector3 mieExtinction = new Vector3(4.40e-3f, 4.40e-3f, 4.40e-3f);

        [Tooltip("密度标高 (km)。地球 1.2 —— 比 Rayleigh 低得多，所以雾霾贴地。")]
        [Min(0.01f)] public float mieScaleHeight = 1.2f;

        [Tooltip("Henyey-Greenstein 各向异性因子。0.8 为强前向散射，逆光光晕靠它。")]
        [Range(-0.99f, 0.99f)] public float miePhaseG = 0.8f;

        // ------------------------------------------------------------------ 臭氧
        [Header("臭氧（黄昏天空的蓝紫对侧色）")]
        [Tooltip("吸收系数 (1/km)。地球 (0.650, 1.881, 0.085)e-3。去掉它黄昏会发灰。")]
        public Vector3 ozoneAbsorption = new Vector3(0.650e-3f, 1.881e-3f, 0.085e-3f);

        [Tooltip("帐篷剖面中心高度 (km)。地球 25。")]
        [Min(0f)] public float ozoneTentCenter = 25f;

        [Tooltip("帐篷剖面半宽 (km)。地球 15，即分布在 10~40 km。")]
        [Min(0.01f)] public float ozoneTentHalfWidth = 15f;

        // ------------------------------------------------------------ 地面 / 太阳
        [Header("地面 / 太阳")]
        [Tooltip("地面平均反射率，参与多次散射 LUT。地球平均 0.3。")]
        [Range(0f, 1f)] public float groundAlbedo = 0.3f;

        [Tooltip("大气顶太阳照度 (lux)。垂直入射约 120000。整套管线的绝对亮度基准。")]
        [Min(0f)] public float sunIlluminanceLux = 120000f;

        [Tooltip("太阳视角直径 (度)。地球 0.545。决定阴影半影与日面大小。")]
        [Range(0.01f, 10f)] public float sunAngularDiameterDegrees = 0.545f;

        /// <summary>大气顶半径 (km)。</summary>
        public float topRadius => bottomRadius + atmosphereThickness;

        /// <summary>世界空间 (m) -> 大气空间 (km) 的缩放。</summary>
        public const float worldToAtmosphere = 0.001f;

        public static VistaAtmosphereParameters CreateEarth() => new VistaAtmosphereParameters();

        /// <summary>
        /// 把参数推到全局 shader 常量。LUT compute 与所有采样端（天空盒 / 不透明物 / 雾）
        /// 共用同一份全局 cbuffer，保证不会出现"天空和雾用了不同大气参数"这类不一致。
        /// </summary>
        public void Bind(int transmittanceLutWidth, int transmittanceLutHeight)
        {
            float top = topRadius;

            Shader.SetGlobalVector(VistaShaderIDs._VistaRayleigh,
                new Vector4(rayleighScattering.x, rayleighScattering.y, rayleighScattering.z,
                            -1f / Mathf.Max(1e-4f, rayleighScaleHeight)));

            Shader.SetGlobalVector(VistaShaderIDs._VistaMieScatter,
                new Vector4(mieScattering.x, mieScattering.y, mieScattering.z,
                            -1f / Mathf.Max(1e-4f, mieScaleHeight)));

            // 消光不得小于散射，否则 exp(-opticalDepth) 会 > 1 造成能量增益
            Vector3 extinction = new Vector3(
                Mathf.Max(mieExtinction.x, mieScattering.x),
                Mathf.Max(mieExtinction.y, mieScattering.y),
                Mathf.Max(mieExtinction.z, mieScattering.z));
            Shader.SetGlobalVector(VistaShaderIDs._VistaMieExtinct,
                new Vector4(extinction.x, extinction.y, extinction.z, miePhaseG));

            Shader.SetGlobalVector(VistaShaderIDs._VistaOzone,
                new Vector4(ozoneAbsorption.x, ozoneAbsorption.y, ozoneAbsorption.z, 0f));

            Shader.SetGlobalVector(VistaShaderIDs._VistaOzoneTent,
                new Vector4(ozoneTentCenter, 1f / Mathf.Max(1e-4f, ozoneTentHalfWidth), 0f, 0f));

            Shader.SetGlobalVector(VistaShaderIDs._VistaRadius,
                new Vector4(bottomRadius, top, bottomRadius * bottomRadius, top * top));

            Shader.SetGlobalVector(VistaShaderIDs._VistaGround,
                new Vector4(groundAlbedo, groundAlbedo, groundAlbedo, worldToAtmosphere));

            float cosAngularRadius = Mathf.Cos(sunAngularDiameterDegrees * 0.5f * Mathf.Deg2Rad);
            Shader.SetGlobalVector(VistaShaderIDs._VistaSun,
                new Vector4(sunIlluminanceLux, sunIlluminanceLux, sunIlluminanceLux, cosAngularRadius));

            Shader.SetGlobalVector(VistaShaderIDs._VistaTransmittanceLutSize,
                new Vector4(transmittanceLutWidth, transmittanceLutHeight,
                            1f / transmittanceLutWidth, 1f / transmittanceLutHeight));
        }

        public VistaAtmosphereParameters Clone() => (VistaAtmosphereParameters)MemberwiseClone();

        /// <summary>静态 LUT 的脏检查依据：只有这些值变了才需要重算 Transmittance / MultiScattering。</summary>
        public bool Equals(VistaAtmosphereParameters other)
        {
            if (ReferenceEquals(other, null)) return false;
            if (ReferenceEquals(other, this)) return true;
            return bottomRadius             == other.bottomRadius
                && atmosphereThickness      == other.atmosphereThickness
                && rayleighScattering       == other.rayleighScattering
                && rayleighScaleHeight      == other.rayleighScaleHeight
                && mieScattering            == other.mieScattering
                && mieExtinction            == other.mieExtinction
                && mieScaleHeight           == other.mieScaleHeight
                && miePhaseG                == other.miePhaseG
                && ozoneAbsorption          == other.ozoneAbsorption
                && ozoneTentCenter          == other.ozoneTentCenter
                && ozoneTentHalfWidth       == other.ozoneTentHalfWidth
                && groundAlbedo             == other.groundAlbedo
                && sunIlluminanceLux        == other.sunIlluminanceLux
                && sunAngularDiameterDegrees == other.sunAngularDiameterDegrees;
        }

        public override bool Equals(object obj) => Equals(obj as VistaAtmosphereParameters);

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(bottomRadius); h.Add(atmosphereThickness);
            h.Add(rayleighScattering); h.Add(rayleighScaleHeight);
            h.Add(mieScattering); h.Add(mieExtinction); h.Add(mieScaleHeight); h.Add(miePhaseG);
            h.Add(ozoneAbsorption); h.Add(ozoneTentCenter); h.Add(ozoneTentHalfWidth);
            h.Add(groundAlbedo); h.Add(sunIlluminanceLux); h.Add(sunAngularDiameterDegrees);
            return h.ToHashCode();
        }
    }
}
