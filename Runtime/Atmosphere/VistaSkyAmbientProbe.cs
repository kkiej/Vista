using UnityEngine;
using UnityEngine.Rendering;

namespace Vista
{
    /// <summary>
    /// 把 GPU 上的天空 SH 导出到 <see cref="RenderSettings.ambientProbe"/>（CPU 侧那一条出口）。
    ///
    /// 为什么需要这条 CPU 旁路：GPU 侧那份 <c>_VistaSkyAmbientSh</c> 只有我们自己的
    /// shader 会读。URP 自带的光探针 / <c>unity_SHAr</c> 路径、以及所有第三方 shader，
    /// 读的都是引擎从 <c>ambientProbe</c> 打包出来的那组常量。想让"天空一变，
    /// 整个场景的间接光跟着变"对**所有**材质成立，就必须把值送回引擎。
    ///
    /// 为什么这条路可以慢、而 GPU 那条不行：雾（Step 3）与 PRT relight（Step 4）
    /// 每帧都要用当帧的天空，走 CPU 就会让间接光比天空晚几帧，日落时能直接看出
    /// "光跟不上天"。所以两条口是**并列**的，不是"CPU 算完再喂 GPU"。
    ///
    /// 延迟预算：读回请求在**记录期**发出，那时本帧的 SH dispatch 还没提交，
    /// 所以拿到的是上一帧的内容；再加读回本身的 1~2 帧，合计 2~3 帧。
    /// 这里**故意不**为了省那一帧去开 <c>AddUnsafePass</c>：
    /// <c>ComputeCommandBuffer</c> 根本没有 <c>RequestAsyncReadback</c>（只有
    /// <c>UnsafeCommandBuffer</c> 有），换过去要放弃图对该 pass 的依赖校验，
    /// 而收益是"太阳角度晚 1/60 秒"—— 天空的变化以秒计，这一帧在画面上不存在。
    /// </summary>
    public sealed class VistaSkyAmbientProbe : System.IDisposable
    {
        AsyncGPUReadbackRequest m_Request;
        bool m_HasRequest;
        int m_LastRequestFrame = -1;
        SphericalHarmonicsL2 m_Probe;
        bool m_HasProbe;

        /// <summary>已经成功导出过至少一次。</summary>
        public bool hasProbe => m_HasProbe;

        /// <summary>最近一次成功转换出的探针（自检/调试用）。</summary>
        public SphericalHarmonicsL2 probe => m_Probe;

        /// <summary>
        /// 记录期调用。先消费已完成的请求，再按需发起下一次。
        ///
        /// 用轮询而不是回调：回调版在 buffer 被释放（域重载 / feature 关闭）时会
        /// 带着已失效的资源触发，而轮询版的生命周期完全由本对象控制。
        /// 反正每帧都会走到这里，轮询不额外花什么。
        /// </summary>
        /// <param name="buffer">SH 缓冲。null 时只做请求回收，不发新请求。</param>
        /// <param name="cameraType">相机类型，用于过滤（见 <see cref="ShouldDrive"/>）。</param>
        public void Update(GraphicsBuffer buffer, CameraType cameraType)
        {
            if (m_HasRequest && m_Request.done)
            {
                m_HasRequest = false;
                if (!m_Request.hasError)
                {
                    var data = m_Request.GetData<Vector4>();
                    if (VistaSphericalHarmonics.TryConvertMomentsToProbe(data, ref m_Probe))
                    {
                        m_HasProbe = true;
                        // ambientMode 每次都设：别的系统（Lighting 面板、其他脚本）可能改回去，
                        // 而"设了 ambientProbe 但 ambientMode 还是 Skybox"表现为
                        // 我们的值被完全忽略、环境光仍然把太阳圆盘卷进来（偏亮约 8 倍），
                        // 与"SH 投影写错了"在画面上无法区分。
                        RenderSettings.ambientMode = AmbientMode.Custom;
                        RenderSettings.ambientProbe = m_Probe;
                    }
                }
            }

            if (buffer == null || m_HasRequest) return;
            if (!ShouldDrive(cameraType)) return;

            // 一帧只发一次：多相机（Game + SceneView + 材质预览）会让 RecordRenderGraph
            // 在同一帧里跑好几遍，每遍都发请求就变成了每帧 N 次回读。
            int frame = Time.frameCount;
            if (frame == m_LastRequestFrame) return;
            m_LastRequestFrame = frame;

            m_Request = AsyncGPUReadback.Request(buffer);
            m_HasRequest = true;
        }

        /// <summary>
        /// <c>ambientProbe</c> 是**场景全局**状态，所以不能让任意相机去写它。
        /// 只认 Game 与 SceneView：预览相机（材质球缩略图、Prefab 预览）的位置是任意的，
        /// 用它的海拔投出来的 SH 去覆盖整个场景的环境光，症状是选中一个材质球
        /// 场景亮度就跳一下 —— 这种"操作 A 引起无关现象 B"的 bug 极难被联想到原因。
        /// </summary>
        static bool ShouldDrive(CameraType cameraType)
            => cameraType == CameraType.Game || cameraType == CameraType.SceneView;

        public void Dispose()
        {
            // 必须等：读回是从 buffer 里搬数据，buffer 先被释放就是读已释放显存。
            if (m_HasRequest)
            {
                m_Request.WaitForCompletion();
                m_HasRequest = false;
            }
            m_HasProbe = false;
        }
    }
}
