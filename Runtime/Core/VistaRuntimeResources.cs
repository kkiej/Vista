using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista
{
    /// <summary>
    /// 运行时 shader / compute 资源容器。
    ///
    /// 走 <see cref="IRenderPipelineResources"/> 而不是 ScriptableObject + Inspector 手动赋值，
    /// 原因有两个：
    ///   1. package 里不能用 Resources/ 加载，而 ResourcePath 是相对 package 根目录解析的，
    ///      引用在 Editor 下自动填充，不需要用户手动拖。
    ///   2. Unity 6 会把所有 IRenderPipelineGraphicsSettings 自动收进
    ///      UniversalRenderPipelineGlobalSettings，接入零配置。
    ///
    /// 读取方式：GraphicsSettings.GetRenderPipelineSettings&lt;VistaRuntimeResources&gt;()
    /// 这是 URP 自己的做法（见 UniversalRenderPipelineRuntimeShaders）。
    /// </summary>
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    public class VistaRuntimeResources : IRenderPipelineResources
    {
        [SerializeField, HideInInspector] private int m_Version = 0;

        /// <inheritdoc/>
        public int version => m_Version;

        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => true;

        // --------------------------------------------------------------- Step 1 大气
        //
        // 只读访问器：SetValueAndNotify 是 SRP Core 的内部扩展，第三方 assembly 拿不到，
        // 所以这里不提供 setter——资源由 ResourcePath 在 Editor 下自动填充，运行时不需要改。

        [SerializeField, ResourcePath("Shaders/Atmosphere/AtmosphereLut.compute")]
        private ComputeShader m_AtmosphereLutCS;

        /// <summary>大气 LUT 计算（Transmittance / MultiScattering / SkyView / AerialPerspective）。</summary>
        public ComputeShader atmosphereLutCS => m_AtmosphereLutCS;

        // 单独一个 .compute 而不是塞进 AtmosphereLut.compute：镜面预滤波要
        // ImageBasedLighting.hlsl（SampleGGXDir / PerceptualRoughnessToMipmapLevel），
        // 那个头会连带拖进 BSDF.hlsl 一大片 include 图。让已有的九个大气核为了
        // 两个反射核多编译这些东西，代价是每次改大气 shader 的迭代都变慢。
        [SerializeField, ResourcePath("Shaders/Atmosphere/SkyReflection.compute")]
        private ComputeShader m_SkyReflectionCS;

        /// <summary>天空镜面反射 cubemap 的 GGX 预滤波与自检。</summary>
        public ComputeShader skyReflectionCS => m_SkyReflectionCS;

        [SerializeField, ResourcePath("Shaders/Atmosphere/VistaSky.shader")]
        private Shader m_SkyShader;

        /// <summary>物理天空盒 shader。用于生成挂到 RenderSettings.skybox 的材质。</summary>
        public Shader skyShader => m_SkyShader;

        [SerializeField, ResourcePath("Shaders/Atmosphere/VistaAerialPerspectiveComposite.shader")]
        private Shader m_AerialPerspectiveCompositeShader;

        /// <summary>
        /// Aerial Perspective 的全屏合成（变体 A）。为 null 时 AP 只能走 InShader 模式，
        /// 大气模块的其余部分照常工作。
        /// </summary>
        public Shader aerialPerspectiveCompositeShader => m_AerialPerspectiveCompositeShader;

        // --------------------------------------------------------------- Step 3 体积雾

        // 单独一个 .compute 而不是塞进 AtmosphereLut.compute：#20 之后这里的核要
        // include URP 的 Shadows.hlsl（逐 froxel 采级联阴影）与 Lighting.hlsl，
        // 而那两个头会把 per-object light data 那一整套 include 图拖进来。
        // 让十一个大气核为此多编译一遍，改大气 shader 的迭代成本会直接翻倍 ——
        // 这与反射核当初拆出去是同一条理由。
        [SerializeField, ResourcePath("Shaders/Volumetrics/VolumetricFog.compute")]
        private ComputeShader m_VolumetricFogCS;

        /// <summary>
        /// 近层体积雾的 froxel 体（注入 / 积分 / 自检）。
        /// 为 null 时近层雾不可用，雾整体回落到 AP LUT 那一层 —— 画面上是「没有光柱」，
        /// 不是「没有雾」。
        /// </summary>
        public ComputeShader volumetricFogCS => m_VolumetricFogCS;

        [SerializeField, ResourcePath("Shaders/Volumetrics/VistaFroxelDebug.shader")]
        private Shader m_FroxelDebugShader;

        /// <summary>
        /// froxel 表的调试视图（#21）。为 null 时 <c>debugView</c> 的非 Off 档静默失效 ——
        /// 这条是可以接受的：整个视图本身就是一个诊断工具，它缺席不影响任何出货路径。
        /// 出货路径缺 shader 的那两条（天空、AP 合成）各自都写明了退化行为。
        /// </summary>
        public Shader froxelDebugShader => m_FroxelDebugShader;

        /// <summary>取当前管线下的 Vista 资源容器，不在 URP 下时返回 null。</summary>
        public static VistaRuntimeResources Get()
            => GraphicsSettings.GetRenderPipelineSettings<VistaRuntimeResources>();
    }
}
