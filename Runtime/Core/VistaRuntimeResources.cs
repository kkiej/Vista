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

        [SerializeField, ResourcePath("Shaders/Atmosphere/VistaSky.shader")]
        private Shader m_SkyShader;

        /// <summary>物理天空盒 shader。用于生成挂到 RenderSettings.skybox 的材质。</summary>
        public Shader skyShader => m_SkyShader;

        /// <summary>取当前管线下的 Vista 资源容器，不在 URP 下时返回 null。</summary>
        public static VistaRuntimeResources Get()
            => GraphicsSettings.GetRenderPipelineSettings<VistaRuntimeResources>();
    }
}
