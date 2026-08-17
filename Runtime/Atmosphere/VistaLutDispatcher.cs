using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace Vista
{
    /// <summary>LUT 槽位。绑哪个具体资源由 dispatcher 决定，这里只说「哪一张」。</summary>
    public enum VistaLutSlot
    {
        Transmittance,
        MultiScattering,
        SkyView,
        ApScatter,
        ApTransmittance,
    }

    /// <summary>
    /// Buffer 槽位。与 <see cref="VistaLutSlot"/> 分开一个枚举而不是并进去：
    /// 两者的解析结果类型不同（RTHandle/TextureHandle vs GraphicsBuffer/BufferHandle），
    /// 合成一个枚举就必须在 Resolve 里对"纹理槽位传进了 SetBuffer"这种误用做运行时判断，
    /// 而拆开之后它连编译都过不了。
    /// </summary>
    public enum VistaLutBufferSlot
    {
        SkyAmbientSh,
        /// <summary>仅 Editor 自检使用（参考解输出）。运行时路径不分配它。</summary>
        SkyAmbientShReference,
    }

    /// <summary>
    /// 抹平两条命令录制路径的差异：
    ///   RenderGraph 的 <see cref="ComputeCommandBuffer"/> 只接受 <c>TextureHandle</c>；
    ///   原生 <see cref="CommandBuffer"/>（Editor 预览 / 自检）只接受 <c>RTHandle</c>。
    /// 两者之间没有转换。
    ///
    /// 为什么值得多这一层接口，而不是把 dispatch 写两遍：自检跑的必须是**线上那一份**
    /// dispatch 代码。写两遍的话，哪天只改了运行时那份采样数，自检还在验旧参数，
    /// 而它照样全绿 —— 那时自检就从资产变成了负债。
    ///
    /// 实现都是 readonly struct + 泛型约束，JIT 会把虚调用去虚化，没有装箱。
    /// </summary>
    public interface IVistaLutDispatcher
    {
        void SetTexture(ComputeShader cs, int kernelIndex, int nameID, VistaLutSlot slot);

        /// <summary>
        /// Buffer 的情况和纹理**不对称**，而且这个不对称本身就是加这层抽象的理由：
        /// <c>ComputeCommandBuffer.SetComputeBufferParam</c> 有一个直收 <c>GraphicsBuffer</c>
        /// 的重载（纹理那边没有对应物），而 <c>BufferHandle</c> 到 <c>GraphicsBuffer</c>
        /// 又存在隐式转换。于是"在 RenderGraph 路径里直接把持久 GraphicsBuffer 绑上去"
        /// 是**能编译、能跑、且在 NV 上看着正常**的 —— 但图完全不知道这个 pass 碰了它，
        /// 不会插 barrier，下游读到的可能是上一帧的内容。这与三张静态表必须拆 pass
        /// 是同一类 UB（见 CHANGELOG 的坑）。
        /// 走槽位就让"绑什么"只有一个入口：graph 实现只能拿到 import 进来的 handle，
        /// 想绕过去得先改这个文件。
        /// </summary>
        void SetBuffer(ComputeShader cs, int kernelIndex, int nameID, VistaLutBufferSlot slot);

        void SetGlobalVector(int nameID, Vector4 value);
        void Dispatch(ComputeShader cs, int kernelIndex, int groupsX, int groupsY, int groupsZ);
    }

    /// <summary>立即模式：原生 CommandBuffer + LUT 自己持有的 RTHandle。</summary>
    public readonly struct VistaImmediateLutDispatcher : IVistaLutDispatcher
    {
        readonly CommandBuffer m_Cmd;
        readonly VistaAtmosphereLuts m_Luts;

        public VistaImmediateLutDispatcher(CommandBuffer cmd, VistaAtmosphereLuts luts)
        {
            m_Cmd = cmd;
            m_Luts = luts;
        }

        public void SetTexture(ComputeShader cs, int kernelIndex, int nameID, VistaLutSlot slot)
            => m_Cmd.SetComputeTextureParam(cs, kernelIndex, nameID, Resolve(slot));

        public void SetBuffer(ComputeShader cs, int kernelIndex, int nameID, VistaLutBufferSlot slot)
            => m_Cmd.SetComputeBufferParam(cs, kernelIndex, nameID, Resolve(slot));

        public void SetGlobalVector(int nameID, Vector4 value)
            => m_Cmd.SetGlobalVector(nameID, value);

        public void Dispatch(ComputeShader cs, int kernelIndex, int groupsX, int groupsY, int groupsZ)
            => m_Cmd.DispatchCompute(cs, kernelIndex, groupsX, groupsY, groupsZ);

        // 每个槽位都显式列出，default 故意返回 null / 无效 handle：
        // 原本这里是 `_ => skyView` 兜底，加第四张表时会**静默绑错纹理**，
        // 症状是航空透视里出现天空的内容，看起来像参数化写错了 —— 排查成本极高。
        // 让它在绑定处直接炸掉，比兜底安静地跑出错误画面便宜得多。
        RTHandle Resolve(VistaLutSlot slot) => slot switch
        {
            VistaLutSlot.Transmittance   => m_Luts.transmittanceLut,
            VistaLutSlot.MultiScattering => m_Luts.multiScatteringLut,
            VistaLutSlot.SkyView         => m_Luts.skyViewLut,
            VistaLutSlot.ApScatter       => m_Luts.apScatterLut,
            VistaLutSlot.ApTransmittance => m_Luts.apTransmittanceLut,
            _                            => null,
        };

        GraphicsBuffer Resolve(VistaLutBufferSlot slot) => slot switch
        {
            VistaLutBufferSlot.SkyAmbientSh          => m_Luts.skyAmbientShBuffer,
            VistaLutBufferSlot.SkyAmbientShReference => m_Luts.skyAmbientShRefBuffer,
            _                                        => null,
        };
    }

    /// <summary>RenderGraph 模式：ComputeCommandBuffer + pass 声明过的 TextureHandle。</summary>
    public readonly struct VistaGraphLutDispatcher : IVistaLutDispatcher
    {
        readonly ComputeCommandBuffer m_Cmd;
        readonly TextureHandle m_Transmittance;
        readonly TextureHandle m_MultiScattering;
        readonly TextureHandle m_SkyView;
        readonly TextureHandle m_ApScatter;
        readonly TextureHandle m_ApTransmittance;
        readonly BufferHandle m_SkyAmbientSh;

        public VistaGraphLutDispatcher(ComputeCommandBuffer cmd,
                                       TextureHandle transmittance,
                                       TextureHandle multiScattering,
                                       TextureHandle skyView,
                                       TextureHandle apScatter,
                                       TextureHandle apTransmittance,
                                       BufferHandle skyAmbientSh)
        {
            m_Cmd = cmd;
            m_Transmittance = transmittance;
            m_MultiScattering = multiScattering;
            m_SkyView = skyView;
            m_ApScatter = apScatter;
            m_ApTransmittance = apTransmittance;
            m_SkyAmbientSh = skyAmbientSh;
        }

        public void SetTexture(ComputeShader cs, int kernelIndex, int nameID, VistaLutSlot slot)
            => m_Cmd.SetComputeTextureParam(cs, kernelIndex, nameID, Resolve(slot));

        // Resolve 返回 BufferHandle，靠 BufferHandle -> GraphicsBuffer 的隐式转换落到
        // SetComputeBufferParam 上。那个转换是在 execute 阶段查
        // RenderGraphResourceRegistry.current 完成的，所以只在 pass 执行时有效 ——
        // 这里正好在 execute 里，没问题。
        public void SetBuffer(ComputeShader cs, int kernelIndex, int nameID, VistaLutBufferSlot slot)
            => m_Cmd.SetComputeBufferParam(cs, kernelIndex, nameID, Resolve(slot));

        public void SetGlobalVector(int nameID, Vector4 value)
            => m_Cmd.SetGlobalVector(nameID, value);

        public void Dispatch(ComputeShader cs, int kernelIndex, int groupsX, int groupsY, int groupsZ)
            => m_Cmd.DispatchCompute(cs, kernelIndex, groupsX, groupsY, groupsZ);

        TextureHandle Resolve(VistaLutSlot slot) => slot switch
        {
            VistaLutSlot.Transmittance   => m_Transmittance,
            VistaLutSlot.MultiScattering => m_MultiScattering,
            VistaLutSlot.SkyView         => m_SkyView,
            VistaLutSlot.ApScatter       => m_ApScatter,
            VistaLutSlot.ApTransmittance => m_ApTransmittance,
            _                            => default,
        };

        // 参考解那张只在 Editor 立即模式下存在，运行时图里没有对应资源 ——
        // 返回 default（隐式转换成 null）而不是兜底到 SkyAmbientSh：
        // 兜底会让自检核把参考值写进正在被消费的那张 SH buffer 里，
        // 画面上表现为环境光突然变成某个法线的辐照度，与"投影写错了"完全无法区分。
        BufferHandle Resolve(VistaLutBufferSlot slot) => slot switch
        {
            VistaLutBufferSlot.SkyAmbientSh => m_SkyAmbientSh,
            _                               => default,
        };
    }
}
