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
        /// <summary>
        /// 天空镜面反射 cubemap。**只做 SRV**（自检核按 TEXTURECUBE 读它，
        /// 运行时挂到 unity_SpecCube0）。写走 <see cref="SkyReflectionArray"/>。
        /// </summary>
        SkyReflection,
        /// <summary>
        /// 反射的 UAV 目标：一张 6 层 × 7 级 mip 的 Tex2DArray，内容与 cubemap 逐面一致，
        /// dispatch 完由 CopyTexture 逐面搬进 cube。唯一一个需要按 mip 绑定的槽位。
        ///
        /// 为什么不直接把 Cube RT 绑到 <c>RWTexture2DArray</c>：Unity 的绑定校验拒绝它 ——
        ///   Property (_VistaSkyReflectionRW) ... has mismatching output texture dimension
        ///   (expected 5, got 4)
        /// （5 = Tex2DArray = HLSL 声明，4 = Cube = 绑上来的 RT）。硬件层面 cube 的 UAV view
        /// 就是 2D array view，但 Unity 不给这条路：core / URP 两个包里没有任何 compute
        /// 写 cubemap 的先例，引擎自己的那条路（Runtime/PathTracing/Environment/CubemapRender.cs）
        /// 是 SetRenderTarget 逐面**光栅**。
        /// 于是要么改成光栅（要重写 GGX 积分核 + 逐面逐 mip 的 attachment 管理），
        /// 要么多一张中转纹理。选后者：GGX 积分、mip↔粗糙度反函数、逐面方向约定
        /// 这三处最贵最容易错的东西一个字都不用动，代价是 6×64²×7级 fp16 ≈ 0.4 MB 显存。
        /// 而且它顺带**加强**了自检 —— 判据 1 现在还同时验证 element→CubemapFace 的映射。
        /// </summary>
        SkyReflectionArray,
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
        /// <summary>仅 Editor 自检使用（反射 round-trip 报告）。运行时路径不分配它。</summary>
        SkyReflectionVerify,
        /// <summary>仅 Editor 自检使用（banding 签名采样结果）。运行时路径不分配它。</summary>
        SkyViewBanding,
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
        /// 绑定某一级 mip。反射的 UAV 目标每级 mip 是一趟独立 dispatch，
        /// 而 UAV 绑定必须指到具体 mip —— 不指的话两条路径的默认行为都是 mip 0，
        /// 于是七趟 dispatch 全写在 mip0 上、其余六级保持清空后的黑，
        /// 症状是"粗糙度一上去反射就变黑"。
        ///
        /// 单独开一个方法而不是给上面那个加默认参数：默认参数会让"忘了传 mip"
        /// 这件事悄悄编译过去，而它恰好就是上面那个症状的成因。
        /// </summary>
        void SetTextureMip(ComputeShader cs, int kernelIndex, int nameID, VistaLutSlot slot, int mipLevel);

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

        public void SetTextureMip(ComputeShader cs, int kernelIndex, int nameID, VistaLutSlot slot, int mipLevel)
            => m_Cmd.SetComputeTextureParam(cs, kernelIndex, nameID, Resolve(slot), mipLevel);

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
            VistaLutSlot.Transmittance      => m_Luts.transmittanceLut,
            VistaLutSlot.MultiScattering    => m_Luts.multiScatteringLut,
            VistaLutSlot.SkyView            => m_Luts.skyViewLut,
            VistaLutSlot.ApScatter          => m_Luts.apScatterLut,
            VistaLutSlot.ApTransmittance    => m_Luts.apTransmittanceLut,
            VistaLutSlot.SkyReflection      => m_Luts.skyReflectionCube,
            VistaLutSlot.SkyReflectionArray => m_Luts.skyReflectionArray,
            _                               => null,
        };

        GraphicsBuffer Resolve(VistaLutBufferSlot slot) => slot switch
        {
            VistaLutBufferSlot.SkyAmbientSh          => m_Luts.skyAmbientShBuffer,
            VistaLutBufferSlot.SkyAmbientShReference => m_Luts.skyAmbientShRefBuffer,
            VistaLutBufferSlot.SkyReflectionVerify   => m_Luts.skyReflectionVerifyBuffer,
            VistaLutBufferSlot.SkyViewBanding        => m_Luts.skyViewBandingBuffer,
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
        readonly TextureHandle m_SkyReflection;
        readonly TextureHandle m_SkyReflectionArray;
        readonly BufferHandle m_SkyAmbientSh;

        /// <summary>
        /// 用 <see cref="VistaLutHandles"/> 打包而不是继续加位置参数：这个构造在每个
        /// pass 的 render func 里都要写一遍（现在六处），八个同类型的 <c>TextureHandle</c>
        /// 位置参数一旦写错顺序，编译器什么都不会说，而症状是"某张表里出现了另一张表的内容"。
        /// 打包之后顺序错误变成字段名错误，编译期就挡住了。
        /// </summary>
        public VistaGraphLutDispatcher(ComputeCommandBuffer cmd, in VistaLutHandles handles)
        {
            m_Cmd = cmd;
            m_Transmittance = handles.transmittance;
            m_MultiScattering = handles.multiScattering;
            m_SkyView = handles.skyView;
            m_ApScatter = handles.apScatter;
            m_ApTransmittance = handles.apTransmittance;
            m_SkyReflection = handles.skyReflection;
            m_SkyReflectionArray = handles.skyReflectionArray;
            m_SkyAmbientSh = handles.skyAmbientSh;
        }

        public void SetTexture(ComputeShader cs, int kernelIndex, int nameID, VistaLutSlot slot)
            => m_Cmd.SetComputeTextureParam(cs, kernelIndex, nameID, Resolve(slot));

        public void SetTextureMip(ComputeShader cs, int kernelIndex, int nameID, VistaLutSlot slot, int mipLevel)
            => m_Cmd.SetComputeTextureParam(cs, kernelIndex, nameID, Resolve(slot), mipLevel);

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
            VistaLutSlot.Transmittance      => m_Transmittance,
            VistaLutSlot.MultiScattering    => m_MultiScattering,
            VistaLutSlot.SkyView            => m_SkyView,
            VistaLutSlot.ApScatter          => m_ApScatter,
            VistaLutSlot.ApTransmittance    => m_ApTransmittance,
            VistaLutSlot.SkyReflection      => m_SkyReflection,
            VistaLutSlot.SkyReflectionArray => m_SkyReflectionArray,
            _                               => default,
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

    /// <summary>
    /// 一帧内所有 LUT 的 RenderGraph handle。纯数据打包，没有行为 ——
    /// 存在的唯一目的是让 <see cref="VistaGraphLutDispatcher"/> 的构造在六个 pass
    /// 里都写成一行，且顺序错误在编译期被字段名挡住。
    /// </summary>
    public struct VistaLutHandles
    {
        public TextureHandle transmittance;
        public TextureHandle multiScattering;
        public TextureHandle skyView;
        public TextureHandle apScatter;
        public TextureHandle apTransmittance;
        public TextureHandle skyReflection;
        public TextureHandle skyReflectionArray;
        public BufferHandle skyAmbientSh;
    }
}
