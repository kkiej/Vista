using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Vista
{
    /// <summary>
    /// 近层体积雾的 froxel 体资源持有者：三张 3D 表 + 一个判据报告 buffer。
    ///
    /// 为什么单独一个类而不是继续塞进 <see cref="VistaAtmosphereLuts"/>：那边持有的七张表
    /// 全部是「大气」的，生命周期由 <c>VistaAtmosphereParameters</c> 的脏检查驱动；
    /// froxel 体的生命周期由**屏幕尺寸**驱动，两者的脏检查输入没有交集。
    /// 合在一起的直接后果是改分辨率会连带重烘三张静态大气表（Transmittance 那张要 march
    /// 40 步 × 256×64），而那是纯浪费。
    ///
    /// 但它由 <see cref="VistaAtmosphereLuts"/> **持有**（作为字段），
    /// 这样 <see cref="VistaImmediateLutDispatcher"/> 解析槽位时仍然只有一个入口。
    ///
    /// #19 阶段这个类只被 Editor 自检驱动 —— RenderGraph pass 的接线在 #20，
    /// 那时才会有真正要写进这三张表的内容。
    /// </summary>
    public sealed class VistaFroxelVolume : IDisposable
    {
        // ---- 判据报告的布局常量 ----
        // 每片两个 float4：r0 = 从注入表读回来的四个距离，r1 = 解析侧的四个恒等式读数。
        // 语义写在 VistaFroxelVolumeSelfTest 里（唯一消费者）。
        public const int k_ReportFloat4PerSlice = 2;

        readonly ComputeShader m_Cs;
        readonly int m_KernelPlaceholderIdx = -1;
        readonly int m_KernelSliceVerifyIdx = -1;

        RTHandle m_Injection;
        RTHandle m_InjectionHistory;
        RTHandle m_Integral;
        GraphicsBuffer m_SliceReport;

        // 可空而不是直接存 struct：null 表示「还没分配过」。
        // 存 struct 的话就得靠一个哨兵值（比如 depth == 0）表达同一件事，
        // 而 VistaFroxelVolumeDesc 的构造保证不了 depth 一定非零 —— 哨兵会撞车。
        VistaFroxelVolumeDesc? m_Allocated;

        public VistaFroxelVolume(ComputeShader volumetricFogCS)
        {
            m_Cs = volumetricFogCS;
            if (m_Cs == null) return;

            // FindKernel 在核不存在时会**抛异常**（不是返回 -1），所以走 HasKernel 先问。
            // 这条与大气那批核同源：shader 编译失败时整个 ComputeShader 资源仍然非 null，
            // 只是一个核都没有 —— 那时抛出来会把整条渲染循环打断。
            if (m_Cs.HasKernel("FroxelPlaceholder"))
                m_KernelPlaceholderIdx = m_Cs.FindKernel("FroxelPlaceholder");
            if (m_Cs.HasKernel("FroxelSliceVerify"))
                m_KernelSliceVerifyIdx = m_Cs.FindKernel("FroxelSliceVerify");
        }

        /// <summary>
        /// 两个核都在。分开判「资源在不在」（<see cref="isAllocated"/>）与「核在不在」，
        /// 理由与 <c>VistaAtmosphereLuts</c> 那四个独立的 valid 属性相同：
        /// 前者是每帧可变的状态，后者在构造之后就是常量，混成一个属性
        /// 会让「shader 编译坏了」与「这一帧还没分配」在日志上长得一样。
        /// </summary>
        public bool isValid => m_Cs != null && m_KernelPlaceholderIdx >= 0 && m_KernelSliceVerifyIdx >= 0;

        public bool isAllocated => m_Injection != null && m_InjectionHistory != null && m_Integral != null;

        public RTHandle injection => m_Injection;
        public RTHandle injectionHistory => m_InjectionHistory;
        public RTHandle integral => m_Integral;
        public GraphicsBuffer sliceReportBuffer => m_SliceReport;

        /// <summary>这一帧实际分配下来的口径。没分配过时为 null。</summary>
        public VistaFroxelVolumeDesc? allocatedDesc => m_Allocated;

        /// <summary>
        /// 按需分配三张表，并把分布常量下发成全局。
        ///
        /// 常量**每次都推**、不做脏检查：<c>Shader.SetGlobalVector</c> 是跨帧持久的，
        /// 但同名全局可能被别的 feature 覆盖，而这里写错的症状是「雾整体近了/远了」，
        /// 不是报错。省下的两次 SetGlobalVector 不值这个风险。
        /// （这与 <c>VistaAtmosphereLuts.PrepareLuts</c> 里那段注释是同一条理由。）
        /// </summary>
        /// <returns>三张表是否可用。false 时调用方必须走无近层雾的路径。</returns>
        public bool Prepare(in VistaFroxelVolumeDesc desc, CommandBuffer cmd)
        {
            if (!isValid) return false;

            if (!isAllocated || !m_Allocated.HasValue || !m_Allocated.Value.Equals(desc))
            {
                Release();
                Allocate(desc);
                m_Allocated = desc;
            }

            // 距离范围不进分配脏检查（见 VistaFroxelVolumeDesc.Equals），
            // 所以这里下发的常量可能与 m_Allocated 里存的距离不同 —— 这是**刻意**的：
            // 纹理只依赖三个尺寸，距离每帧推 cbuffer 即刻生效。
            // 因此下面用的是入参 desc，不是 m_Allocated。写成后者会让
            // 「相机的阴影距离变了但分辨率没变」这一帧沿用旧距离，
            // 症状是雾的远端有一帧滞后 —— 一个看起来像 TAA 抖动的东西。
            if (cmd != null)
            {
                cmd.SetGlobalVector(VistaShaderIDs._VistaFroxelRange, desc.packedRange);
                cmd.SetGlobalVector(VistaShaderIDs._VistaFroxelSize, desc.packedSize);
            }
            else
            {
                Shader.SetGlobalVector(VistaShaderIDs._VistaFroxelRange, desc.packedRange);
                Shader.SetGlobalVector(VistaShaderIDs._VistaFroxelSize, desc.packedSize);
            }

            return true;
        }

        /// <summary>
        /// 按需分配判据报告 buffer（<paramref name="sliceCount"/> × 2 个 float4）。
        ///
        /// 与三张表分开一个方法：这个 buffer **只有 Editor 自检用**，
        /// 运行时路径一个字节都不该分配它。合进 <see cref="Prepare"/> 就变成
        /// 「线上每帧多 2 KB 显存 + 一次没人读的分配」，而且那笔开销在 profiler 里
        /// 会被归到体积雾头上。
        /// </summary>
        public void EnsureSliceReportBuffer(int sliceCount)
        {
            int count = Mathf.Max(1, sliceCount) * k_ReportFloat4PerSlice;
            if (m_SliceReport != null && m_SliceReport.count == count) return;

            m_SliceReport?.Dispose();
            m_SliceReport = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, count, sizeof(float) * 4)
            {
                name = "VistaFroxelSliceReport",
            };
        }

        /// <summary>
        /// 占位派发：把切片几何写满整张注入表。
        ///
        /// 铺满整个体积（而不是只写判据要读的那一列）是为了让 dispatch 维度本身也被走一遍 ——
        /// 「布景不走被测代码路径的自检，其数字不变是空判据」。240×135×64 全写一遍，
        /// 判据只读 (0,0) 那一列；如果 XY 的 group 数算错，越界那部分会被 kernel 里的
        /// 边界检查挡掉，但**少写**的部分会让读回来的值仍然是清空后的 0，判据抓得到。
        /// </summary>
        public void DispatchPlaceholder<T>(in T dispatcher, in VistaFroxelVolumeDesc desc)
            where T : IVistaLutDispatcher
        {
            if (!isValid || !isAllocated) return;

            dispatcher.SetTexture(m_Cs, m_KernelPlaceholderIdx,
                VistaShaderIDs._VistaFroxelInjectionRW, VistaLutSlot.FroxelInjection);
            dispatcher.Dispatch(m_Cs, m_KernelPlaceholderIdx,
                VistaComputeUtils.DivRoundUp(desc.width, 8),
                VistaComputeUtils.DivRoundUp(desc.height, 8),
                desc.depth);
        }

        /// <summary>
        /// 判据派发：把注入表当 SRV 读回来 + 解析恒等式，一个线程一片。
        /// </summary>
        public void DispatchSliceVerify<T>(in T dispatcher, in VistaFroxelVolumeDesc desc)
            where T : IVistaLutDispatcher
        {
            if (!isValid || !isAllocated || m_SliceReport == null) return;

            // SRV 绑定点与 UAV 分开：同一张纹理同时绑两种 view 是 UB。
            dispatcher.SetTexture(m_Cs, m_KernelSliceVerifyIdx,
                VistaShaderIDs._VistaFroxelInjectionRead, VistaLutSlot.FroxelInjectionRead);
            dispatcher.SetBuffer(m_Cs, m_KernelSliceVerifyIdx,
                VistaShaderIDs._VistaFroxelSliceReportRW, VistaLutBufferSlot.FroxelSliceReport);
            dispatcher.Dispatch(m_Cs, m_KernelSliceVerifyIdx,
                VistaComputeUtils.DivRoundUp(desc.depth, 64), 1, 1);
        }

        void Allocate(in VistaFroxelVolumeDesc desc)
        {
            m_Injection = AllocVolume(desc, "VistaFroxelInjection");
            m_InjectionHistory = AllocVolume(desc, "VistaFroxelInjectionHistory");
            m_Integral = AllocVolume(desc, "VistaFroxelIntegral");
        }

        static RTHandle AllocVolume(in VistaFroxelVolumeDesc desc, string name)
        {
            // 必须走带 slices + dimension 的重载。2D 便捷重载会**静默**建成 Tex2D，
            // 症状是 RWTexture3D 绑定失败：Editor.log 里一行 warning，画面上整张表全零
            // （= 完全没有近层雾），几乎不可能联想到分配口径。
            return RTHandles.Alloc(
                desc.width, desc.height,
                // fp16。注入表存的是 (σ_s·L 预曝光后, σ_t)：
                //   前三通道预曝光 —— 与 AP LUT 同一条约定（见 AerialPerspective.hlsl），
                //   绝对单位的太阳照度 1.2e5 lux 乘上 2.542e-5 的曝光后是 O(1)，fp16 富余；
                //   σ_t 的单位是 1/km，最浓的雾（能见度 50 m）是 78 /km，也在范围内。
                // 积分表存的是 (累积内散射 预曝光, 累积透射率)，两者都 ≤ O(1)。
                format: GraphicsFormat.R16G16B16A16_SFloat,
                slices: desc.depth,
                // 三线性：读端逐像素按深度采样，深度方向必须插值 ——
                // Point 会让雾在切片边界上出现同心的环带（近处最明显，那里切片最短）。
                filterMode: FilterMode.Bilinear,
                // 三个轴都 Clamp。深度轴 Repeat 会让「比最远片还远」的像素采回最近片：
                // 症状是超出近层范围的物体突然只剩一点点雾，而它恰好发生在
                // AP 接手的那个距离上 —— 会被误判成分层接缝。
                wrapMode: TextureWrapMode.Clamp,
                dimension: TextureDimension.Tex3D,
                enableRandomWrite: true,
                name: name);
        }

        public void Release()
        {
            m_Injection?.Release();
            m_Injection = null;
            m_InjectionHistory?.Release();
            m_InjectionHistory = null;
            m_Integral?.Release();
            m_Integral = null;
            m_Allocated = null;
        }

        public void Dispose()
        {
            Release();
            // GraphicsBuffer 是 IDisposable 而非 RTHandle：漏掉它不会有 RTHandle 那种
            // 泄漏检测日志，只会安静地涨显存（Editor 里反复域重载时尤其明显）。
            m_SliceReport?.Dispose();
            m_SliceReport = null;
        }
    }
}
