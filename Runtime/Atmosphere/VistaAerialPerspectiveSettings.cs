using System;
using UnityEngine;

namespace Vista
{
    /// <summary>
    /// Aerial Perspective froxel LUT 的配置。
    /// 算法说明、分层理由、彩色 vs 灰度透射率的取舍见
    /// <c>ShaderLibrary/AerialPerspective.hlsl</c> 的文件头。
    /// </summary>
    [Serializable]
    public class VistaAerialPerspectiveSettings : IEquatable<VistaAerialPerspectiveSettings>
    {
        /// <summary>深度切片的距离分布。</summary>
        public enum Distribution
        {
            /// <summary>D(w) = far · w^k。切片 0 精确落在距离 0。</summary>
            Power = 0,
            /// <summary>D(w) = near · (far/near)^w。切片间距恒为距离的固定百分比。</summary>
            Logarithmic = 1,
        }

        [Header("分辨率")]
        [Tooltip("froxel 体积尺寸 (宽, 高, 深度切片数)。\n"
               + "32×32×32 是 Hillaire 论文与 UE5 SkyAtmosphere 的默认值。\n"
               + "横向分辨率只需要分辨太阳光晕：32 列覆盖 90° FOV 约 2.8°/纹素，"
               + "HG(g=0.8) 的光晕半宽约 12°，即约 4 个纹素，够用。\n"
               + "把 g 调到 0.9 以上或用超宽 FOV 时才需要提到 64。")]
        public Vector3Int resolution = new Vector3Int(32, 32, 32);

        [Header("深度范围")]
        [Tooltip("最远切片的距离 (km)。超出这个距离的像素会被钉在最后一片，"
               + "由 Step 1 的合成负责淡入 Sky-View LUT。")]
        [Min(0.1f)] public float maxDistanceKm = 32f;

        [Tooltip("最近切片的距离 (km)，仅 Logarithmic 分布使用。\n"
               + "比它更近的距离按路径长度线性淡出到无雾。\n"
               + "Step 3 的体积雾接手近层后，这个值会被抬到雾 froxel 的远端。")]
        [Min(0.001f)] public float nearDistanceKm = 0.02f;

        [Tooltip("切片分布方式。\n"
               + "Logarithmic：相对分辨率处处相同。32 片 / near 20 m / far 32 km 时，"
               + "640 m 以内有 15 片、之外 16 片 —— 适合「脚下几百米 + 远景几十 km 同屏」。\n"
               + "Power：切片 0 精确落在距离 0，但 k=2 时 512 m 内只有 4 片。\n"
               + "哪种更好取决于场景尺度，用自检里的 AP slice error 项量，不要猜。")]
        public Distribution distribution = Distribution.Logarithmic;

        [Tooltip("Power 分布的指数 k。仅 Power 分布使用。")]
        [Range(1f, 6f)] public float powerExponent = 2f;

        [Header("质量")]
        [Tooltip("开：透射率存 RGB（第二张 3D 表，采样端多一次取样）。\n"
               + "关：透射率取灰度存进散射表的 alpha（论文做法，一次取样）。\n"
               + "空气消光强波长相关（地表 Rayleigh 蓝/红相差 5.7 倍），"
               + "灰度近似在十几 km 处让远山丢暖色。移动端关，PC 开。\n"
               + "两者的差值由自检数值量化，不是拍脑袋。")]
        public bool coloredTransmittance = true;

        /// <summary>切片数下界为 2：分布映射里有 1/(depth-1)。</summary>
        public int depth => Mathf.Max(2, resolution.z);
        public int width => Mathf.Max(1, resolution.x);
        public int height => Mathf.Max(1, resolution.y);

        /// <summary>Logarithmic 分布的起点，已保证 &gt; 0 且 &lt; <see cref="maxDistanceKm"/>。</summary>
        public float effectiveNearKm =>
            distribution == Distribution.Logarithmic
                ? Mathf.Clamp(nearDistanceKm, 1e-4f, maxDistanceKm * 0.5f)
                : 0f;

        /// <summary>x: nearKm, y: farKm, z: 指数 k, w: 分布模式。</summary>
        public Vector4 packedParams => new Vector4(
            effectiveNearKm,
            Mathf.Max(0.1f, maxDistanceKm),
            Mathf.Max(1f, powerExponent),
            distribution == Distribution.Logarithmic ? 1f : 0f);

        /// <summary>xyz: 尺寸, w: 1/(depth-1)。</summary>
        public Vector4 packedSize => new Vector4(width, height, depth, 1f / (depth - 1));

        /// <summary>
        /// x: 是否用彩色透射率, y: 1/nearKm。
        /// Power 模式 near = 0，这里填一个大数，于是近端淡出在 d &gt; 0 时立刻取 1
        /// （Power 模式 D(0) = 0，本来就无雾，两者一致），且不会除零。
        /// </summary>
        public Vector4 packedFlags => new Vector4(
            coloredTransmittance ? 1f : 0f,
            effectiveNearKm > 0f ? 1f / effectiveNearKm : 1e8f,
            0f, 0f);

        /// <summary>只有影响 3D 纹理分配的字段才算：其余每帧推 cbuffer 即可生效。</summary>
        public bool Equals(VistaAerialPerspectiveSettings other)
        {
            if (ReferenceEquals(other, null)) return false;
            if (ReferenceEquals(other, this)) return true;
            return width == other.width && height == other.height && depth == other.depth;
        }

        public override bool Equals(object obj) => Equals(obj as VistaAerialPerspectiveSettings);

        public override int GetHashCode() => HashCode.Combine(width, height, depth);

        public VistaAerialPerspectiveSettings Clone()
            => (VistaAerialPerspectiveSettings)MemberwiseClone();
    }
}
