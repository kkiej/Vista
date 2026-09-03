using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Vista
{
    /// <summary>
    /// 把 froxel 体的三个 RenderGraph 句柄从产出方（<see cref="VistaAtmospherePass"/>，
    /// 记录在 BeforeRenderingPrePasses）传给帧内更晚的消费方。
    ///
    /// ------------------------------------------------------------------ 为什么不用全局绑定
    /// 大气那批 LUT 走的是 <c>SetGlobalTextureAfterPass</c> + 消费方不声明依赖，
    /// 靠「产出 pass 关了裁剪 + RenderGraph 不重排」保证顺序。那条路对**顺序**是够的，
    /// 但它在图里没有边，于是 RenderGraph 也不会为「compute 写 UAV → 光栅 pass 读 SRV」
    /// 插资源状态转换。D3D11 上驱动自己管，看不出问题；Vulkan/Metal 的 native pass
    /// compiler 依赖声明出来的边。
    ///
    /// 这不是「大气那边写错了」—— 那是一条已经在跑的、有顺序论证的路径。
    /// 但**新**的消费点没有理由继续扩大这个暴露面，尤其是这三张表接下来还要被
    /// #25 的合成（AfterRenderingSkybox）读一次。把句柄传下去，消费方就能
    /// <c>UseTexture(..., Read)</c>，边是真的，屏障也是真的。
    ///
    /// 为什么不让消费方自己 <c>ImportTexture</c> 一遍：同一个 RTHandle 导入两次会在
    /// 图里产生**两条互不相识的资源记录**，指向同一块显存。顺序保证一点没多，
    /// 还额外把「这两个句柄是同一张纹理」这件事藏起来了 —— 比全局绑定更糟。
    ///
    /// ------------------------------------------------------------------ 失能态
    /// froxel 体没分配、或注入开关关着时，本项本帧**根本不会被 Create** ——
    /// 不是「Create 出来但句柄无效」。消费方于是用 <c>Contains</c> 判，
    /// 而 <c>Reset</c> 把句柄清成默认值只是为了池化复用时不留上一帧的脏句柄。
    /// </summary>
    public class VistaFroxelFrameData : ContextItem
    {
        /// <summary>当前帧的注入表（σ_s·J, σ_t），由注入 pass 写。</summary>
        public TextureHandle injection;

        /// <summary>累积积分表（累积内散射, 1 − T），由积分 pass 写。</summary>
        public TextureHandle integral;

        /// <summary>解析后的分配口径。消费方要用它把切片下标夹到 [0, N−1]。</summary>
        public VistaFroxelVolumeDesc desc;

        public override void Reset()
        {
            injection = TextureHandle.nullHandle;
            integral  = TextureHandle.nullHandle;
            desc      = default;
        }
    }
}
