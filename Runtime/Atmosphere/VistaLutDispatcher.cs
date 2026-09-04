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

        // ---- 近层体积雾的 froxel 体（#19 起）----
        // #19 时这四个槽位只在立即模式下可解析；#20 接上了 RenderGraph 那一侧
        // （注入 pass 有真正要写进去的内容了）。两侧现在都能解析，但**都依赖
        // 对应的 pass 事先声明过资源**：graph 侧拿到的是 pass import 进来的 handle，
        // 没 import 就是 default（无效），绑定处直接炸 —— 这正是 default 不兜底的意义。
        //
        // 历史帧（#22a）：它**没有写入路径** —— 谁都不往「历史」这张资源里写。
        // 双缓冲的交换（VistaFroxelVolume.SwapInjectionBuffers）只改写下标，
        // 本帧写的永远是 FroxelInjection 指向的那张，下一帧交换后它就成了历史。
        // 所以要覆盖的不是「写」，而是「读到的是不是上一帧那张」——
        // 判据⑬（静止恒等性）与⑮（失效路径计数）盯的就是这一条。
        // 积分表的写入路径在 #21 落地，两侧都有判据（立即模式的合成介质判数值，
        // 真实帧的探针槽 14~18 判 RenderGraph 那条路）。
        /// <summary>注入表的 UAV view（写）。</summary>
        FroxelInjection,
        /// <summary>
        /// 注入表的 SRV view（读）。解析到**同一张**资源，但绑定点不同 ——
        /// 同一张纹理同时绑 UAV 与 SRV 是 UB，所以调用方必须在两趟 dispatch 里分别用。
        /// 单独一个槽位而不是复用 <see cref="FroxelInjection"/>：写成同一个的话，
        /// 「在一趟 dispatch 里既绑 RW 又绑 Read」就变成一件看不出来的事。
        /// </summary>
        FroxelInjectionRead,
        /// <summary>
        /// 注入表的历史帧（另一张资源的 SRV）。#22a 起由注入核与重投影探针核读，
        /// 只读、无 RW 绑定点 —— 留一个没人写的 RW 绑定点等于一段永远不会被发现写错的代码。
        /// </summary>
        FroxelInjectionHistory,
        /// <summary>
        /// 沿视线累积的内散射 rgb + <b>1 − 累积透射率</b>（不是 T 本身，
        /// 与 HDRP/UE5 刻意相反，理由见 FroxelVolume.hlsl 的头注：
        /// 这张表清空态全 0，存 T 会把「表没被写」升级成全黑）。写入在 #21。
        /// </summary>
        FroxelIntegral,

        /// <summary>
        /// 蓝噪声瓦片（64×64、8 bit、单通道），来源是 URP 自带的
        /// <c>blueNoise64LTex</c>，见 <see cref="VistaBlueNoise"/>。**只读、无生产者。**
        ///
        /// ---------------------------------------------------------------- 为什么它是一个槽位而不是一句全局
        /// #22b 一开始走的是 <c>Shader.SetGlobalTexture</c>，而那**到不了 compute kernel**：
        /// 它写的是 Unity 的**立即态**全局属性表，不进命令流；compute dispatch 的纹理绑定
        /// 是在命令流里解析的。症状是核里逐像素读到 0（D3D11 上未绑定的 SRV 读 0，
        /// **不是**引擎默认白图 —— 那条「读到全 1」的经验只对 material shader 成立），
        /// 而 CPU 侧的一切表面证据都显示接上了。
        ///
        /// 而 <c>ComputeCommandBuffer</c> 的 <c>SetGlobalTexture</c> / <c>SetComputeTextureParam</c>
        /// 两组重载**只吃 TextureHandle**，没有任何入口接受裸 <c>Texture</c> ——
        /// 于是这张外部资产必须被 import 进图，也就必须有一个槽位。
        ///
        /// ---------------------------------------------------------------- 它与其余槽位的两点不同
        /// 1. 解析源不是 <c>m_Luts</c>，而是静态的 <see cref="VistaBlueNoise.handle"/>
        ///    （立即模式）/ pass import 进来的 handle（graph 模式）。
        /// 2. 它是唯一一个**允许解析不到**的纹理槽位（资产缺失时回落到程序化档），
        ///    所以调用方必须先问 <see cref="IVistaLutDispatcher.HasTexture"/> ——
        ///    无条件绑一个 null RTHandle 会经由隐式转换变成
        ///    <c>default(RenderTargetIdentifier)</c>（= CameraTarget），
        ///    那是一个静默绑错、且画面上完全说不出成因的形态。
        /// </summary>
        BlueNoise,
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
        /// <summary>仅 Editor 自检使用（天空雾闭式解 vs 数值参考）。运行时路径不分配它。</summary>
        SkyFogError,
        /// <summary>仅 Editor 自检使用（froxel 体逐片的分布报告）。运行时路径不分配它。</summary>
        FroxelSliceReport,
        /// <summary>
        /// 仅 Editor 自检使用（#20 的阴影覆盖性探针）。
        ///
        /// 与其余「仅 Editor」槽位不同，这一个**必须能在 graph 侧解析** ——
        /// 阴影贴图与注入表的内容只在真正渲染的一帧里存在，立即模式下没有相机、
        /// 也没有 MainLightShadowCasterPass 跑过。所以它由 pass 按需 import 进图，
        /// 没请求探针时留在 default（null），dispatch 由 C# 侧的门挡掉。
        /// </summary>
        FroxelShadowProbe,
        /// <summary>仅 Editor 自检使用（#21 的逐片积分报告）。运行时路径不分配它。</summary>
        FroxelIntegrationReport,
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
        /// 这个槽位这一趟解析得到吗。**唯一的用途是 <see cref="VistaLutSlot.BlueNoise"/>**：
        /// 它是唯一一个允许缺失的纹理槽位（资产取不到时回落到程序化抖动）。
        ///
        /// 为什么不在派发处直接问 <c>VistaBlueNoise.available</c>：那会变成两份真相 ——
        /// graph 侧还要求「本趟 pass 确实 import + UseTexture 过它」，而
        /// <c>available</c> 对此一无所知。少声明一趟的症状会是那一趟静默绑到
        /// default handle 上。问 dispatcher 则是问**被测的那份绑定源自己**，
        /// 于是「资产缺失」与「pass 忘了声明」这两种缺失走同一条回落路径。
        /// </summary>
        bool HasTexture(VistaLutSlot slot);

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

        /// <summary>
        /// 唯一的消费者是 #22 的时间重投影（上一帧的 viewProj）。
        ///
        /// 为什么是矩阵而不是拆成四个 Vector4 自己在 shader 里凑：把一个 4×4 拆成
        /// 四行下发，等于让「行主序还是列主序」成为一个只能靠画面对不对来验的约定 ——
        /// 而错了的症状是重投影偏一点点，正好长得像「历史权重高了」。
        /// </summary>
        void SetGlobalMatrix(int nameID, Matrix4x4 value);

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

        public bool HasTexture(VistaLutSlot slot) => Resolve(slot) != null;

        public void SetBuffer(ComputeShader cs, int kernelIndex, int nameID, VistaLutBufferSlot slot)
            => m_Cmd.SetComputeBufferParam(cs, kernelIndex, nameID, Resolve(slot));

        public void SetGlobalVector(int nameID, Vector4 value)
            => m_Cmd.SetGlobalVector(nameID, value);

        public void SetGlobalMatrix(int nameID, Matrix4x4 value)
            => m_Cmd.SetGlobalMatrix(nameID, value);

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
            VistaLutSlot.FroxelInjection        => m_Luts.froxelInjection,
            VistaLutSlot.FroxelInjectionRead    => m_Luts.froxelInjection,
            VistaLutSlot.FroxelInjectionHistory => m_Luts.froxelInjectionHistory,
            VistaLutSlot.FroxelIntegral         => m_Luts.froxelIntegral,
            // 不来自 m_Luts：这张图是引擎的资产，Vista 只包了个 RTHandle
            // （见 VistaBlueNoise.handle 的注释 —— 那里记着为什么不能走
            //  Shader.SetGlobalTexture）。取不到时是 null，由 HasTexture 挡住。
            VistaLutSlot.BlueNoise              => VistaBlueNoise.handle,
            _                               => null,
        };

        GraphicsBuffer Resolve(VistaLutBufferSlot slot) => slot switch
        {
            VistaLutBufferSlot.SkyAmbientSh          => m_Luts.skyAmbientShBuffer,
            VistaLutBufferSlot.SkyAmbientShReference => m_Luts.skyAmbientShRefBuffer,
            VistaLutBufferSlot.SkyReflectionVerify   => m_Luts.skyReflectionVerifyBuffer,
            VistaLutBufferSlot.SkyViewBanding        => m_Luts.skyViewBandingBuffer,
            VistaLutBufferSlot.SkyFogError           => m_Luts.skyFogErrorBuffer,
            VistaLutBufferSlot.FroxelSliceReport     => m_Luts.froxelSliceReportBuffer,
            VistaLutBufferSlot.FroxelShadowProbe     => m_Luts.froxelShadowProbeBuffer,
            VistaLutBufferSlot.FroxelIntegrationReport => m_Luts.froxelIntegrationReportBuffer,
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
        readonly TextureHandle m_FroxelInjection;
        readonly TextureHandle m_FroxelInjectionHistory;
        readonly TextureHandle m_FroxelIntegral;
        readonly TextureHandle m_BlueNoise;
        readonly BufferHandle m_SkyAmbientSh;
        readonly BufferHandle m_FroxelShadowProbe;

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
            m_FroxelInjection = handles.froxelInjection;
            m_FroxelInjectionHistory = handles.froxelInjectionHistory;
            m_FroxelIntegral = handles.froxelIntegral;
            m_BlueNoise = handles.blueNoise;
            m_SkyAmbientSh = handles.skyAmbientSh;
            m_FroxelShadowProbe = handles.froxelShadowProbe;
        }

        public void SetTexture(ComputeShader cs, int kernelIndex, int nameID, VistaLutSlot slot)
            => m_Cmd.SetComputeTextureParam(cs, kernelIndex, nameID, Resolve(slot));

        public void SetTextureMip(ComputeShader cs, int kernelIndex, int nameID, VistaLutSlot slot, int mipLevel)
            => m_Cmd.SetComputeTextureParam(cs, kernelIndex, nameID, Resolve(slot), mipLevel);

        // IsValid() 而不是 != default：一个没被本趟 pass 声明过的槽位拿到的就是
        // default handle，而它到 RenderTargetIdentifier 的隐式转换是**合法**的
        // （落到 CameraTarget 上），所以缺失必须在这里就被判出来。
        public bool HasTexture(VistaLutSlot slot) => Resolve(slot).IsValid();

        // Resolve 返回 BufferHandle，靠 BufferHandle -> GraphicsBuffer 的隐式转换落到
        // SetComputeBufferParam 上。那个转换是在 execute 阶段查
        // RenderGraphResourceRegistry.current 完成的，所以只在 pass 执行时有效 ——
        // 这里正好在 execute 里，没问题。
        public void SetBuffer(ComputeShader cs, int kernelIndex, int nameID, VistaLutBufferSlot slot)
            => m_Cmd.SetComputeBufferParam(cs, kernelIndex, nameID, Resolve(slot));

        public void SetGlobalVector(int nameID, Vector4 value)
            => m_Cmd.SetGlobalVector(nameID, value);

        public void SetGlobalMatrix(int nameID, Matrix4x4 value)
            => m_Cmd.SetGlobalMatrix(nameID, value);

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
            // 注入表的两个 view 解析到**同一个** handle：graph 侧一个资源只有一个 handle，
            // UAV / SRV 的区别由 pass 声明时的 AccessFlags 决定，不是由 handle 决定。
            // 两个槽位仍然分开的意义在调用方那边（不能在一趟 dispatch 里同时绑两种 view），
            // 见 VistaLutSlot.FroxelInjectionRead 的注释。
            VistaLutSlot.FroxelInjection        => m_FroxelInjection,
            VistaLutSlot.FroxelInjectionRead    => m_FroxelInjection,
            VistaLutSlot.FroxelInjectionHistory => m_FroxelInjectionHistory,
            VistaLutSlot.FroxelIntegral         => m_FroxelIntegral,
            // 只有 import + UseTexture 过它的那几趟 pass 才拿得到有效 handle；
            // 其余趟是 default ⇒ HasTexture 为 false ⇒ 派发处不绑（回落到程序化档）。
            VistaLutSlot.BlueNoise              => m_BlueNoise,
            _                               => default,
        };

        // 参考解那张只在 Editor 立即模式下存在，运行时图里没有对应资源 ——
        // 返回 default（隐式转换成 null）而不是兜底到 SkyAmbientSh：
        // 兜底会让自检核把参考值写进正在被消费的那张 SH buffer 里，
        // 画面上表现为环境光突然变成某个法线的辐照度，与"投影写错了"完全无法区分。
        BufferHandle Resolve(VistaLutBufferSlot slot) => slot switch
        {
            VistaLutBufferSlot.SkyAmbientSh      => m_SkyAmbientSh,
            // 唯一一个「仅 Editor 自检」却必须在 graph 侧可解析的槽位：
            // 阴影贴图与注入表的内容只在真正渲染的一帧里存在。没请求探针时
            // pass 不 import 它，这里拿到的就是 default（null），
            // 而 dispatch 本身也被 C# 侧的门挡掉 —— 两道都在。
            VistaLutBufferSlot.FroxelShadowProbe => m_FroxelShadowProbe,
            _                                    => default,
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

        // ---- 近层体积雾（#20 起）----
        // 注入表**只有一个** handle：UAV 与 SRV 的区别在 graph 里由 pass 声明的
        // AccessFlags 决定，不由 handle 决定。所以这里没有 froxelInjectionRead。
        public TextureHandle froxelInjection;
        public TextureHandle froxelInjectionHistory;
        public TextureHandle froxelIntegral;
        /// <summary>
        /// 蓝噪声瓦片（#22b）。**只读、无生产者**，来源是 URP 的资产 ——
        /// 由消费它的那几趟 pass 自己 import + UseTexture。没声明过的 pass
        /// 在这里拿到 default，<c>HasTexture</c> 判 false，派发处不绑。
        /// 为什么它必须进图而不是一句 <c>Shader.SetGlobalTexture</c>：
        /// 见 <see cref="VistaBlueNoise.handle"/> 的注释。
        /// </summary>
        public TextureHandle blueNoise;
        /// <summary>阴影覆盖性探针（仅在 Editor 请求探针的那一帧被 import）。</summary>
        public BufferHandle froxelShadowProbe;
    }
}
