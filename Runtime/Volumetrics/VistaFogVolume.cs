using System.Collections.Generic;
using UnityEngine;

namespace Vista
{
    /// <summary>局部雾体的形状。两种共用同一个 world→local 矩阵与同一套内侧衰减。</summary>
    public enum VistaFogVolumeShape
    {
        /// <summary>OBB 盒（HDRP Local Volumetric Fog 的形状）。山谷、房间、街区。</summary>
        Box = 0,

        /// <summary>
        /// 椭球（局部空间的单位球）。雾团、湖面雾。盒的局部矩阵下几乎白送 ——
        /// 局部空间里就是一次 length 判距。
        /// </summary>
        Ellipsoid = 1,
    }

    /// <summary>
    /// 局部雾体（Step 3 #24）。挂在场景物体上，把一块盒形/椭球形区域的介质
    /// 加进近层体积雾（σ_t 与 σ_s 相加，重叠的体相加 —— 与 HDRP 同语义）。
    ///
    /// ------------------------------------------------------------------ 作用范围
    /// **只进近层 froxel 体，不进 AP LUT**（HDRP 的局部体只写 VBuffer、UE5 的
    /// 体素化只写体积雾视锥体，同一个理由：几十米尺度的体放进 km 级的表必然混叠）。
    /// 推论：超出 froxel 远边界（默认 64 m，被阴影距离夹过后见报表）的部分
    /// 不参与任何计算 —— 体不要压在远边界上，会被切开。
    ///
    /// ------------------------------------------------------------------ 密度口径
    /// 与全局雾同一个口径：平均自由程 L（米）⇒ σ_t = 1000/L (1/km)。
    /// 走 L 米透射率降到 1/e（HDRP 的 Fog Distance 就是这个量）。
    /// 体内密度均匀 × 噪声，**不**乘全局的指数高度剖面 —— 体本身就是
    /// 「这里例外」的表达，再叠全局剖面会让「把体抬高密度就变淡」
    /// 成为一个美术查不出来的行为。
    ///
    /// ------------------------------------------------------------------ 注册表
    /// OnEnable/OnDisable 进出静态表，逐帧由 <see cref="VistaFogVolumeSet.Gather"/>
    /// 收集 + 视锥剔除 + 打包。与 HDRP 的 LocalVolumetricFogManager 同模式 ——
    /// 不走 FindObjectsOfType（每帧一次场景遍历），也不走 Volume 框架
    /// （那是全局插值语义，体要的是空间叠加语义，两者混用过的项目
    /// 都长出过「雾体权重互相稀释」这类查不出来的行为）。
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Vista/Vista Fog Volume")]
    public sealed class VistaFogVolume : MonoBehaviour
    {
        [Tooltip("形状。盒 = OBB（山谷/房间），椭球 = 局部空间单位球（雾团/湖面雾）。")]
        public VistaFogVolumeShape shape = VistaFogVolumeShape.Box;

        [Tooltip("体的全尺寸（米，局部空间）。transform 的缩放会再乘上去 ——\n"
               + "缩放父物体时雾体跟着变是美术预期的行为。")]
        public Vector3 sizeMeters = new Vector3(10f, 10f, 10f);

        [Tooltip("边缘衰减距离（米）。从体表面向内这么多米权重从 0 升到 1。\n"
               + "会被夹到不超过各轴半尺寸 —— 夹住之后体中心的权重恒为 1，\n"
               + "这条构造性质是判据(b) 的门。0 = 硬边缘。")]
        [Min(0f)] public float blendDistanceMeters = 1f;

        [Tooltip("体内的平均自由程 L（米）：σ_t = 1000/L。与全局雾同口径\n"
               + "（HDRP 的 Fog Distance）。走 L 米透射率降到 1/e。\n"
               + "参考：浓雾团 10~30，山谷晨雾 50~150。")]
        [Min(1f)] public float meanFreePathMeters = 50f;

        [Tooltip("单次散射反照率（σ_s/σ_t，逐通道）。水雾接近 1（默认 0.98），\n"
               + "调低偏烟/沙尘。shader 侧会再 saturate 一次。")]
        [ColorUsage(false, false)] public Color albedo = new Color(0.98f, 0.98f, 0.98f, 1f);

        [Header("噪声（0 = 关，整段编译不采样）")]
        [Tooltip("噪声调制量 ∈ [0,1]。密度乘 (1 − amount·noise)，噪声 ∈ [0,1] ⇒\n"
               + "密度在 [1−amount, 1] 倍之间起伏。0 = 均匀体（省 16 次哈希）。")]
        [Range(0f, 1f)] public float noiseAmount = 0.5f;

        [Tooltip("噪声特征尺度（米）。一团起伏的空间尺寸。")]
        [Min(0.1f)] public float noiseScaleMeters = 8f;

        [Tooltip("噪声漂移速度（米/秒，世界空间）。雾的翻滚感。\n"
               + "调大要留意时间重投影的亮度死区：看报表里的 rel 直方图（判据⑮d 那一段），"
               + "「rel ≥ luminanceRejectStart 的格子占几成」就是这条死区当前设定的代价。")]
        public Vector3 noiseVelocityMeters = new Vector3(0.3f, 0.05f, 0.2f);

        // --------------------------------------------------------------------
        //  注册表。s_Active 只在主线程被 OnEnable/OnDisable/Gather 碰，无锁。
        // --------------------------------------------------------------------
        static readonly List<VistaFogVolume> s_Active = new List<VistaFogVolume>(16);

        /// <summary>当前启用的体。只读视图 —— 收集方（Gather）不许改表。</summary>
        public static IReadOnlyList<VistaFogVolume> active => s_Active;

        void OnEnable()  { s_Active.Add(this); }
        void OnDisable() { s_Active.Remove(this); }

        /// <summary>
        /// 各轴半尺寸（米，含 transform 缩放）。打包与剔除共用这一份 ——
        /// 两处各写一遍 Scale(size, lossyScale) 的话，改缩放语义会只改到一处，
        /// 症状是「体被剔了但画面上它明明在视锥里」。
        /// </summary>
        public Vector3 halfExtentsMeters
        {
            get
            {
                var s = transform.lossyScale;
                return new Vector3(
                    Mathf.Max(1e-3f, Mathf.Abs(sizeMeters.x * s.x)) * 0.5f,
                    Mathf.Max(1e-3f, Mathf.Abs(sizeMeters.y * s.y)) * 0.5f,
                    Mathf.Max(1e-3f, Mathf.Abs(sizeMeters.z * s.z)) * 0.5f);
            }
        }

#if UNITY_EDITOR
        // 选中时画线框。颜色带一点雾青，与 Unity 自家 volume 类 gizmo 的惯例一致；
        // 只画选中的（OnDrawGizmosSelected）—— 场景里十几个体常驻线框会糊满视口。
        void OnDrawGizmosSelected()
        {
            var he = halfExtentsMeters;
            Gizmos.color = new Color(0.4f, 0.8f, 0.9f, 0.9f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            if (shape == VistaFogVolumeShape.Ellipsoid)
            {
                // Gizmos 没有椭球，用非均匀缩放的单位球画
                Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, he);
                Gizmos.DrawWireSphere(Vector3.zero, 1f);
            }
            else
            {
                Gizmos.DrawWireCube(Vector3.zero, he * 2f);
            }
        }
#endif
    }
}
