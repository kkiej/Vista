using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Vista
{
    /// <summary>
    /// froxel 表的调试视图（#21）。整屏替换，不叠加。
    ///
    /// 「为什么要有这个视图」「为什么整屏替换」「为什么按场景深度采样」「越界为什么不钳死」
    /// 四条都写在 <c>Shaders/Volumetrics/VistaFroxelDebug.shader</c> 的文件头，
    /// 这里只解释**调度**上的三个决定。
    ///
    /// ── 为什么排在 AfterRenderingPostProcessing ──
    ///
    /// 表里存的是预曝光辐亮度，而这个视图刻意不套色调映射（套了之后「表饱和了」
    /// 与「tonemap 滚到顶了」在画面上无法区分）。排在后处理**之前**的话，
    /// 写出去的值会被别人的 tonemap + 颜色分级改写一遍，那时屏幕上的亮度
    /// 与表里的数就没有关系了 —— 一个「看起来像在看数据、其实在看曲线」的视图，
    /// 比没有视图更坏。
    ///
    /// 落到目标里的就是 raw × gain；再往后交换链自己做的显示编码不在本 pass 管辖内，
    /// 所以这里说的是「不做色调映射」，不是「屏幕像素值等于表里的数」。
    ///
    /// ── 为什么 Off 档整趟 pass 不排入 ──
    ///
    /// 失能态 = 零态：不是「排进来但画个占位内容」，也不是「排进来但 shader 里
    /// 早退」。后两种都会让一个关掉的调试功能继续占一趟全屏 pass 的带宽，
    /// 而且会让「我关掉了却还有开销」这件事只能靠读 shader 才发现。
    ///
    /// ── 为什么 froxel 体没开时要报警而不是静默 ──
    ///
    /// 选了调试档、但 <c>enableInjection</c> 关着（或体积没分配）时，两张表根本不存在，
    /// 本 pass 无从可画。静默跳过的症状是「我选了 Debug View，画面一点变化都没有」——
    /// 一个查不出线索的问题。所以这里点名缺的是哪个开关，并按诊断串去重
    /// （逐帧无条件 LogWarning 是每秒 60 条）。
    /// </summary>
    public sealed class VistaFroxelDebugPass : ScriptableRenderPass
    {
        class PassData
        {
            public Material material;
            public MaterialPropertyBlock properties;
            public TextureHandle injection;
            public TextureHandle integral;
            public Vector4 parameters;
        }

        readonly Material m_Material;

        // 逐 pass 实例一个，不是每帧 new：MaterialPropertyBlock 是托管对象，
        // 每帧新建 = 每帧一次 GC 分配，而这一趟连画面都不参与出货。
        readonly MaterialPropertyBlock m_Properties = new MaterialPropertyBlock();

        /// <summary>本帧要画的档位，由 <see cref="Setup"/> 每帧给。</summary>
        FroxelDebugView m_View;
        int m_Slice;
        float m_Gain;

        string m_LastDiagnostic;

        /// <summary>材质创建失败（shader 资源缺失）时为 false，此时本 pass 不该被排入。</summary>
        public bool isValid => m_Material != null;

        public VistaFroxelDebugPass(Shader shader)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;

            // 本 pass 只往当前颜色附件上整屏覆盖，从不采样它。
            requiresIntermediateTexture = false;

            // 三个深度耦合档要采场景深度。与 AP 合成同一条链路：
            // 这一行会把 URP 的 earliestDepthReadEvent 拉到本事件之前，
            // 深度拷贝因此必然排在本 pass 之前，不需要任何「深度无效就跳过」的兜底。
            ConfigureInput(ScriptableRenderPassInput.Depth);

            m_Material = CoreUtils.CreateEngineMaterial(shader);
        }

        /// <summary>
        /// 每帧下发档位。与 AP 合成那个「没有 Setup」的 pass 不同，本 pass 逐帧有可配项
        /// （美术在 feature 上换档），所以档位必须逐帧传进来，而不是让 pass 自己去
        /// 反查 settings —— 后者会让 pass 依赖 feature 的生命周期。
        /// </summary>
        public void Setup(FroxelDebugView view, int slice, float gain)
        {
            m_View  = view;
            m_Slice = slice;
            m_Gain  = Mathf.Max(0f, gain);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_Material == null || m_View == FroxelDebugView.Off)
                return;

            if (!frameData.Contains<VistaFroxelFrameData>())
            {
                // 排入这一趟的门（VistaAtmosphereFeature）已经挡掉了反射探针相机，
                // 所以走到这里只剩一个原因：注入开关关着（或体积分配失败）。
                // 能点出**唯一**的原因，是那个相机类型门与产出方保持一致换来的。
                Report($"[Vista] Debug View 选了 {m_View}，但近层 froxel 体这一帧不存在，"
                     + "画面不会有任何变化。打开 Vista Atmosphere feature 上的"
                     + "「逐 froxel 的光照注入 + 深度积分」开关。");
                return;
            }

            Report(null);

            var froxel = frameData.Get<VistaFroxelFrameData>();
            var resourceData = frameData.Get<UniversalResourceData>();

            using var builder = renderGraph.AddRasterRenderPass<PassData>(
                "Vista Froxel Debug View", out var data);

            data.material   = m_Material;
            data.properties = m_Properties;
            data.injection  = froxel.injection;
            data.integral   = froxel.integral;

            // 切片下标在这里就夹好，不留给 shader 的 clamp 采样：
            // PointClamp 会把越界的下标静默显示成最后一片，那时「我填了 200」与
            // 「我填了 63」在画面上一样。夹紧规则与状态日志共用同一份实现。
            data.parameters = new Vector4(
                m_Gain,
                VistaVolumetricFogSettings.ResolveDebugSlice(m_Slice, froxel.desc.depth),
                (int)m_View,
                0f);

            // AccessFlags.WriteAll（= Write | Discard）而不是 Write：本 pass 覆盖每一个
            // 像素（全屏三角形 + ZTest Always + Blend Off），所以目标的旧内容一个都不需要。
            // 声明成 WriteAll 让「整屏替换」这件事对图成立 —— tile 架构上省掉一次 load，
            // 而且这条断言写错的后果是画面出现未初始化内容，会立刻被看到。
            builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.WriteAll);

            builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);

            // 两张表都声明成读依赖，即使某一档只用其中一张：
            // 按档位挑着声明能省一条边，但也会让「这一档读了哪张表」分散到两处
            // （builder 与 shader 的分支）。而边多一条的代价是 0 —— 两张表在本帧
            // 都已经被写完了，多一条读依赖不会让图多等任何东西。
            builder.UseTexture(froxel.injection, AccessFlags.Read);
            builder.UseTexture(froxel.integral,  AccessFlags.Read);

            // 没有 AllowGlobalStateModification：档位与两张表全部走 MaterialPropertyBlock，
            // 这一趟一个全局都不改。「本 pass 不改全局」因此是代码里读得出来的事实，
            // 不是一句注释。
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                d.properties.SetVector(VistaShaderIDs._VistaFroxelDebugParams, d.parameters);
                d.properties.SetTexture(VistaShaderIDs._VistaFroxelInjectionRead, d.injection);
                d.properties.SetTexture(VistaShaderIDs._VistaFroxelIntegral,      d.integral);

                ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, 0,
                    MeshTopology.Triangles, 3, 1, d.properties);
            });
        }

        /// <summary>诊断串去重。串变了才报一次；传 null 表示「问题解除」。</summary>
        void Report(string diagnostic)
        {
            if (diagnostic == m_LastDiagnostic)
                return;

            m_LastDiagnostic = diagnostic;
            if (diagnostic != null)
                Debug.LogWarning(diagnostic);
        }

        public void Dispose()
        {
            CoreUtils.Destroy(m_Material);
        }
    }
}
