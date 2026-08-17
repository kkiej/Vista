using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista
{
    /// <summary>
    /// L2 实数球谐（SH9）的 C# 侧常量与转换。
    /// 与 <c>ShaderLibrary/SphericalHarmonics.hlsl</c> **逐项对应**，改一边必须改另一边。
    ///
    /// 放在 Runtime 而不是 Editor：运行时的 <see cref="VistaSkyAmbientProbe"/> 要用
    /// <see cref="k_RadianceToUnitySh"/> 做读回转换，Editor 自检要用它做交叉验证。
    /// 各写一份的后果是自检验的是自检自己那份表 —— 线上那份写错了它照样全绿。
    /// </summary>
    public static class VistaSphericalHarmonics
    {
        /// <summary>与 HLSL 的 <c>VISTA_SH_COEFF_COUNT</c> 一致。</summary>
        public const int k_CoeffCount = 9;

        // 归一化基函数常数 Ŷ。与 HLSL 的 VISTA_SH_Y* 一致。
        public const float k_Y0  = 0.2820948f;
        public const float k_Y1  = 0.4886025f;
        public const float k_Y2a = 1.0925484f;
        public const float k_Y2b = 0.3153916f;
        public const float k_Y2c = 0.5462742f;

        /// <summary>基函数归一化常数，按 Unity 槽位顺序（<see cref="Basis"/> 里每一项的系数）。</summary>
        public static readonly float[] k_ShNorm =
        {
            k_Y0, k_Y1, k_Y1, k_Y1, k_Y2a, k_Y2a, k_Y2b, k_Y2a, k_Y2c,
        };

        /// <summary>
        /// 从"辐射亮度矩 L_i = ∫L·Y_i dω"到"Unity <see cref="SphericalHarmonicsL2"/> 系数"
        /// 的逐槽位缩放，即 (Â_l/π)·Ŷ_i，其中 Â = {π, 2π/3, π/4}。
        ///
        /// 这条公式的依据是**实测**而非文档：<c>SphericalHarmonicsL2.Evaluate</c> 用的是
        /// 未归一化多项式基 {1, y, z, x, xy, yz, 3z²−1, xz, x²−y²}，返回值语义是
        /// albedo=1 的朗伯面出射亮度（辐照度/π）。标定过程与断言见
        /// <c>Editor/Atmosphere/VistaAmbientShSelfTest.ProbeUnityConvention</c>。
        /// 猜错的症状是环境光整体亮/暗约 3 倍 —— 在任何单一场景里都像"美术没调好"。
        /// </summary>
        public static readonly float[] k_RadianceToUnitySh =
        {
            k_Y0,
            k_Y1 * 2f / 3f, k_Y1 * 2f / 3f, k_Y1 * 2f / 3f,
            k_Y2a * 0.25f, k_Y2a * 0.25f, k_Y2b * 0.25f, k_Y2a * 0.25f, k_Y2c * 0.25f,
        };

        /// <summary>
        /// 标准实数球谐基在方向 <paramref name="d"/>（须归一化）上的取值，Unity 槽位顺序。
        /// 与 HLSL 的 <c>VistaShBasis</c> 必须逐项一致。
        /// </summary>
        public static void Basis(Vector3 d, float[] y)
        {
            float x = d.x, yy = d.y, z = d.z;
            y[0] = k_Y0;
            y[1] = k_Y1 * yy;
            y[2] = k_Y1 * z;
            y[3] = k_Y1 * x;
            y[4] = k_Y2a * x * yy;
            y[5] = k_Y2a * yy * z;
            y[6] = k_Y2b * (3f * z * z - 1f);
            y[7] = k_Y2a * x * z;
            y[8] = k_Y2c * (x * x - yy * yy);
        }

        /// <summary>
        /// 把 GPU 读回的 9 个辐射亮度矩转成 <see cref="SphericalHarmonicsL2"/>。
        /// </summary>
        /// <returns>
        /// false 表示这批数据不可用（长度不足 / 含非有限值 / DC 项非正），调用方应**保留上一次的值**
        /// 而不是写一个坏概率进去。这三项都不是理论上不可能的：GPU 侧首帧未写时全 0，
        /// 而单个 NaN 一旦进了 ambientProbe 会让全场间接光变黑或变白，
        /// 且回溯不到来源 —— 拦在这里是最便宜的位置。
        /// </returns>
        public static bool TryConvertMomentsToProbe(NativeArray<Vector4> moments,
                                                    ref SphericalHarmonicsL2 sh)
        {
            if (!moments.IsCreated || moments.Length < k_CoeffCount) return false;

            // DC 项是"整个天球的平均亮度×4π×Y00"，任何有光的天空都必须 > 0。
            // 全 0 就是 GPU 还没写过（或写失败了）。
            Vector4 dc = moments[0];
            if (!(dc.x > 0f) && !(dc.y > 0f) && !(dc.z > 0f)) return false;

            for (int i = 0; i < k_CoeffCount; ++i)
            {
                Vector4 m = moments[i];
                if (!IsFinite(m.x) || !IsFinite(m.y) || !IsFinite(m.z)) return false;
            }

            for (int i = 0; i < k_CoeffCount; ++i)
            {
                Vector4 m = moments[i];
                float s = k_RadianceToUnitySh[i];
                sh[0, i] = m.x * s;
                sh[1, i] = m.y * s;
                sh[2, i] = m.z * s;
            }
            return true;
        }

        static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
