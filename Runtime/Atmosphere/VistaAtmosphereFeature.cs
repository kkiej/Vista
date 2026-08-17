using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista
{
    /// <summary>
    /// 大气模块的接入点。挂到 UniversalRendererData 上即生效。
    ///
    /// 只有一个 RendererFeature 而不是每个效果一个：LUT 是共享资源（天空盒、雾、
    /// aerial perspective、环境光 SH 都读同一批表），分成多个 feature 会让用户能配出
    /// "开了雾但没开大气"这种必然黑屏的组合。质量分级与算法切换（Task #7）也统一在这里。
    /// </summary>
    [DisallowMultipleRendererFeature("Vista Atmosphere")]
    public sealed class VistaAtmosphereFeature : ScriptableRendererFeature
    {
        [SerializeField]
        [Tooltip("星球与大气的物理参数。只影响静态 LUT，改动会触发一次重烘。")]
        VistaAtmosphereParameters m_Parameters = VistaAtmosphereParameters.CreateEarth();

        [SerializeField]
        [Tooltip("世界空间中哪个 Y 值对应星球表面 (m)。通常填场景的海平面高度。")]
        float m_GroundLevelWorldY = 0f;

        [SerializeField]
        [Tooltip("摄影曝光值。15 = 晴天正午（Sunny 16）。整条管线共用这一个曝光，"
               + "改这里等于改整个画面的亮度基准。")]
        [Range(5f, 20f)]
        float m_EV100 = VistaAtmosphereViewData.k_DefaultEV100;

        [SerializeField]
        [Tooltip("Sky-View LUT 分辨率。192×108 是论文推荐值；移动端分级降到 128×72。"
               + "太低会在日落时的地平线上看到横向台阶。")]
        Vector2Int m_SkyViewResolution = new Vector2Int(
            VistaAtmosphereLuts.k_SkyViewWidthDefault,
            VistaAtmosphereLuts.k_SkyViewHeightDefault);

        [SerializeField]
        [Tooltip("Aerial Perspective froxel LUT 设置。控制远景雾感的精度与深度范围。")]
        VistaAerialPerspectiveSettings m_AerialPerspective = new VistaAerialPerspectiveSettings();

        VistaAtmosphereLuts m_Luts;
        VistaAtmospherePass m_Pass;

        /// <summary>大气参数。运行时改会在下一帧触发静态 LUT 重烘。</summary>
        public VistaAtmosphereParameters parameters => m_Parameters;

        /// <summary>AP froxel 设置。改尺寸会在下一帧重新分配 3D 表，其余立即生效。</summary>
        public VistaAerialPerspectiveSettings aerialPerspective => m_AerialPerspective;

        public override void Create()
        {
            var resources = VistaRuntimeResources.Get();
            if (resources == null || resources.atmosphereLutCS == null)
            {
                // 不抛异常：换管线 / 资源还没导入完的中间态会走到这里，
                // 报错弹窗会盖住真正的问题。
                m_Luts?.Dispose();
                m_Luts = null;
                return;
            }

            // Create 会在 shader 重编译后重新调用，旧的 RTHandle 必须先还回去
            m_Luts?.Dispose();
            m_Luts = new VistaAtmosphereLuts(resources.atmosphereLutCS);
            m_Luts.SetSkyViewResolution(m_SkyViewResolution.x, m_SkyViewResolution.y);

            m_Pass ??= new VistaAtmospherePass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_Luts == null || !m_Luts.isValid || m_Pass == null)
                return;

            // Preview 相机（材质球缩略图、Inspector 预览）不需要物理天空，
            // 而且它们每帧可能有十几个，逐个刷 SkyView 是纯浪费。
            // Reflection 相机要留着：反射探针烘天空靠它。
            var cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview)
                return;

            m_Luts.SetSkyViewResolution(m_SkyViewResolution.x, m_SkyViewResolution.y);
            m_Pass.Setup(m_Luts, m_Parameters, m_AerialPerspective, m_GroundLevelWorldY, m_EV100);
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            // 先清读回、再放 buffer：VistaSkyAmbientProbe.Dispose 里会 WaitForCompletion，
            // 反过来就是让在飞的读回从已释放的显存里搬数据。
            m_Pass?.ambientProbe?.Dispose();
            m_Luts?.Dispose();
            m_Luts = null;
        }
    }
}
