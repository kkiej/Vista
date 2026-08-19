using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Vista
{
    /// <summary>
    /// Aerial Perspective 的全屏合成（变体 A）。
    ///
    /// 合成公式与「为什么不读颜色目标、为什么要两趟混合」都写在
    /// <c>Shaders/Atmosphere/VistaAerialPerspectiveComposite.shader</c> 的文件头，
    /// 这里只解释**调度**上的三个决定。
    ///
    /// ── 为什么排在 AfterRenderingSkybox ──
    ///
    /// 必须在天空盒之后：天空盒是不透明几何之后才画的，若在它之前合成，
    /// 天空像素会先被合成一遍、再被天空盒覆盖 —— 结果正确但白做一遍；
    /// 更糟的是天空盒**不写深度**，所以那时也无法用深度把天空剔掉。
    /// 也必须在半透明之前：AP 用的是不透明深度，半透明物体自己带 AP
    /// 是它们各自 shader 的事（Step 3 的雾会统一处理这一层）。
    /// AfterRenderingSkybox 正好是这两个约束之间唯一的位置。
    ///
    /// ── 深度一定拿得到，不需要任何兜底 ──
    ///
    /// <c>ConfigureInput(ScriptableRenderPassInput.Depth)</c> 会让 URP 把
    /// <c>earliestDepthReadEvent</c> 拉到 AfterRenderingSkybox；
    /// <c>UniversalRendererRenderGraph.CalculateDepthCopySchedule</c> 于是选
    /// <c>DepthCopySchedule.AfterSkybox</c>，而 <c>CalculateSplitEventRange</c> 会把
    /// 「事件 ≥ earliestDepthReadEvent」的自定义 pass 全部排到拷贝之后。
    /// 这条链路与用户在 renderer 上选的 <c>CopyDepthMode</c> 无关 ——
    /// 所以这里没有「深度无效就跳过」的分支：那种分支只会把真正的失效藏起来。
    ///
    /// ── 已知不覆盖的路径 ──
    ///
    /// XR 单趟立体：反投影用的 <c>UNITY_MATRIX_I_VP</c> 不是逐眼索引的，
    /// 全屏三角形也没有做 instancing。本项目（PC + 移动端单目）不涉及，
    /// 这里明确记为**不支持**，而不是假装支持。
    /// </summary>
    public sealed class VistaAerialPerspectiveCompositePass : ScriptableRenderPass
    {
        /// <summary>两趟混合的 pass 序号，与 shader 里的声明顺序一一对应。</summary>
        /// <remarks>
        /// public 是为了让 Editor 自检能拿它去比 <c>Material.GetPassName</c> ——
        /// 「有人在 .shader 里调换了 Pass 顺序」不会报任何错，只会让乘和加互换，
        /// 画面表现是远景雾偏暗，而这里两个数字仍然合法。
        /// </remarks>
        public const int k_PassMultiply = 0;
        public const int k_PassAdd      = 1;

        /// <summary>
        /// 自检用（#15 判据②a）：输出变体 A 折出来的距离 (km) 而不是合成结果。
        /// 为什么它必须是本 shader 的一个 pass，见 .shader 里 Pass 2 的注释。
        /// </summary>
        public const int k_PassDebugDistance = 2;

        /// <summary>自检用：上面几个序号对应的 pass 名，与 shader 里的 Name 必须逐字相同。</summary>
        public static readonly string[] k_PassNames =
        {
            "Vista AP Composite (Multiply Transmittance)",
            "Vista AP Composite (Add In-Scattering)",
            "Vista AP Composite (Debug Distance)",
        };

        /// <summary>
        /// 自检用（#15 判据②a）：置 true 时本 pass 只画 <see cref="k_PassDebugDistance"/>，
        /// 不做合成。
        /// </summary>
        /// <remarks>
        /// 为什么是 static：本 pass 实例由 <c>VistaAtmosphereFeature</c> 创建并持有，
        /// 而且刻意没有 Setup（逐帧无可配项）。给它加一条实例级的调试开关，
        /// 就要为一个只在 Editor 自检里活一瞬间的东西，在 feature 上开一条
        /// 逐帧都要走的传参路径 —— 那条路径会永久留在出货代码里。
        /// 静态开关的代价只是「同一进程内所有相机共享它」，
        /// 而自检本来就是把一台相机单独渲一次、渲完立刻在 finally 里复位。
        ///
        /// 它不影响出货：默认 false，且没有任何运行时代码写它。
        /// </remarks>
        public static bool s_DebugDistanceOutput;

        class PassData
        {
            public Material material;
            public bool debugDistance;
        }

        readonly Material m_Material;

        /// <summary>材质创建失败（shader 资源缺失）时为 false，此时本 pass 不该被排入。</summary>
        public bool isValid => m_Material != null;

        public VistaAerialPerspectiveCompositePass(Shader shader)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingSkybox;

            // 不需要中间纹理：本 pass 只往当前颜色附件上混合，从不采样它，
            // 所以直接渲后台缓冲的配置也能用。声明出来是为了不让 URP
            // 为我们多插一次最终 blit —— 那是当初否掉「换掉 cameraColor」那条路的理由。
            requiresIntermediateTexture = false;

            // 见类注释：这一行同时决定了深度拷贝的时机与本 pass 的排序。
            ConfigureInput(ScriptableRenderPassInput.Depth);

            m_Material = CoreUtils.CreateEngineMaterial(shader);
        }

        // 没有 Setup：本 pass 逐帧没有任何可配的东西。
        // 「排不排入」由 VistaAtmosphereFeature 在排入前判掉，
        // 「AP 参数」全部走大气模块的全局 cbuffer。

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_Material == null)
                return;

            var resourceData = frameData.Get<UniversalResourceData>();

            using var builder = renderGraph.AddRasterRenderPass<PassData>(
                "Vista Aerial Perspective Composite", out var data);

            data.material   = m_Material;

            // 在录制期读一次静态开关、存进 PassData，而不是在执行期直接读它：
            // 执行期读的话，图录制与图执行之间的任何一次赋值都会让
            // 「这一帧画的是哪个 pass」与「录制时声明的依赖」对不上。
            data.debugDistance = s_DebugDistanceOutput;

            // AccessFlags.Write 而不是 ReadWrite：RenderGraph 把「Write 且未附加 Discard」
            // 当作 partial write，load action 仍然是 Load（见 core 包
            // NativePassCompiler.cs 里 partialWrite 那一段）。混合需要的正是这个。
            // URP 自己的 DrawSkyboxPass / DrawObjectsPass 也是这么声明的 ——
            // 改成 ReadWrite 不会让画面变对，只会多一条读依赖。
            builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);

            // 深度只读，且是**拷贝出来的那张**（不是深度附件）：
            // 本 pass 不需要深度测试（ZTest Always），要的是能采样的深度值。
            // 显式声明而不是靠全局：这是本 pass 唯一一个在图里可见的输入依赖，
            // 声明它才让「拷贝 → 合成」这条边对图成立。
            builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);

            // AP 的两张 3D 表与整套大气 cbuffer 走全局绑定，图里看不见这条依赖。
            // 这不是偷懒：它们由 VistaAtmospherePass 在 BeforeRenderingPrePasses 产出，
            // 那些 pass 都 AllowPassCulling(false)，而 RenderGraph 不重排 pass 顺序，
            // 所以「先产出、再消费」由事件顺序保证。URP 自带的天空盒 pass 读
            // _VistaSkyViewLut 靠的也是同一条机制。
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                if (d.debugDistance)
                {
                    // 自检档：只画距离，且**替换**掉两趟合成。
                    // 不能在合成之后再画：那样距离会覆盖颜色，颜色档与距离档
                    // 就得靠两次渲染分别取，反而多一次渲染。
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material,
                        k_PassDebugDistance, MeshTopology.Triangles, 3);
                    return;
                }

                // 两趟的顺序是公式的一部分，不能交换：
                // 先乘得到 dst·T，再加得到 dst·T + S。
                // 反过来先加后乘是 (dst + S)·T —— 散射项被多衰减了一次，
                // 症状是远山的雾偏暗、且越远越暗（T 越小、错得越多）。
                ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material,
                    k_PassMultiply, MeshTopology.Triangles, 3);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material,
                    k_PassAdd, MeshTopology.Triangles, 3);
            });
        }

        public void Dispose()
        {
            CoreUtils.Destroy(m_Material);
        }
    }
}
