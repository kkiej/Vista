using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

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

        // ---- 场景全局状态的保存/还原 ----
        //
        // ambientMode / ambientProbe 是**逐场景序列化**状态，而 renderer 资产是全工程
        // 共享的。于是"给作品集场景装了个 feature"会把工程里**任何**被打开过的场景的
        // 环境光改成 Custom，且一旦保存就落盘。实测（Log Ambient Probe State）写
        // ambientProbe 会把场景置脏 —— 所以不能靠"反正不置脏"来免掉还原。
        //
        // 记场景而不是只记值：feature 存活期间可以换场景，拿着 A 场景的原值去
        // 还原 B 场景，会把 B 改成 A 的环境光设置 —— 那比不还原更糟，因为它是**静默**的
        // 数据破坏，而不还原至少还留着我们自己那份可辨认的值。
        //
        // 存 Scene 结构、不存 scene.handle：Unity 6000.4 把 handle 换成了 SceneHandle，
        // 到 int 的隐式转换已标记废弃（CS0618）。Scene 自带 == 且比的就是句柄，
        // 直接存它既躲开了废弃 API，也不用跟着 SceneHandle 的表示形式改。
        bool m_HasSaved;
        Scene m_SavedScene;
        AmbientMode m_SavedMode;
        SphericalHarmonicsL2 m_SavedProbe;

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
        /// <param name="exposure">
        /// 交给引擎前施加的曝光倍率（<c>VistaAtmosphereViewData.exposure</c>）。
        /// 见 <see cref="Publish"/> 里为什么必须在这一层乘。
        /// </param>
        public void Update(GraphicsBuffer buffer, CameraType cameraType, float exposure)
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
                        Publish(m_Probe, exposure);
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
        /// 写引擎出口。首次写入前（以及换场景后）先把原值扣下来。
        /// </summary>
        void Publish(in SphericalHarmonicsL2 probe, float exposure)
        {
            var scene = SceneManager.GetActiveScene();
            if (!m_HasSaved || m_SavedScene != scene)
            {
                m_SavedScene = scene;
                m_SavedMode  = RenderSettings.ambientMode;
                m_SavedProbe = RenderSettings.ambientProbe;
                m_HasSaved = true;
            }

            // 曝光在**这一层**乘，而不是在 SH 投影核里。
            //
            // GPU 侧那份 _VistaSkyAmbientSh 必须留在绝对光度量：自检的参考解、雾（Step 3）
            // 与 PRT relight（Step 4）都按绝对量对账，把曝光折进核里会让那些判据全部
            // 跟着 EV100 漂。而这条 CPU 出口的消费者是 URP Lit 的 unity_SHAr 那套 ——
            // 不是 Vista 的 shader，身上没有曝光级，所以曝光必须在交接点补上。
            //
            // 漏掉这一乘的症状值得记住：环境光是绝对量（天空 ~5e3 cd/m²）而工程里的
            // 平行光是 Unity 常规单位（intensity 3.14），比值约 1600 倍，于是画面全白、
            // **阴影完全看不见**。它看起来像"阴影坏了"，而 shadowmap 一切正常。
            var scaled = probe;
            for (int c = 0; c < 3; ++c)
                for (int i = 0; i < 9; ++i)
                    scaled[c, i] = probe[c, i] * exposure;

            // ambientMode 每次都设：别的系统（Lighting 面板、其他脚本）可能改回去，
            // 而"设了 ambientProbe 但 ambientMode 还是 Skybox"表现为
            // 我们的值被完全忽略、环境光仍然把太阳圆盘卷进来（偏亮约 8 倍），
            // 与"SH 投影写错了"在画面上无法区分。
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = scaled;
        }

        /// <summary>
        /// <c>ambientProbe</c> 是**场景全局**状态，所以不能让任意相机去写它。
        /// 只认 Game 与 SceneView：预览相机（材质球缩略图、Prefab 预览）的位置是任意的，
        /// 用它的海拔投出来的 SH 去覆盖整个场景的环境光，症状是选中一个材质球
        /// 场景亮度就跳一下 —— 这种"操作 A 引起无关现象 B"的 bug 极难被联想到原因。
        /// </summary>
        static bool ShouldDrive(CameraType cameraType)
            => cameraType == CameraType.Game || cameraType == CameraType.SceneView;

        /// <summary>
        /// 把 <c>ambientMode</c> / <c>ambientProbe</c> 还原成本对象第一次写入之前的值。
        /// 由 <see cref="Dispose"/> 调用，也由场景保存前的守卫调用
        /// （见 <c>VistaRenderSettingsGuard</c>）—— 后者调用完不销毁本对象，
        /// 下一帧的 <see cref="Publish"/> 会重新扣一份原值再写。
        /// </summary>
        public void RestoreRenderSettings()
        {
            // 只在场景没换过时还原（理由见字段处）。
            if (m_HasSaved && SceneManager.GetActiveScene() == m_SavedScene)
            {
                RenderSettings.ambientMode  = m_SavedMode;
                RenderSettings.ambientProbe = m_SavedProbe;
            }
            m_HasSaved = false;
        }

        /// <summary>只丢基线、不写回。理由见 <c>VistaAtmospherePass.ForgetRenderSettingsBaseline</c>。</summary>
        public void ForgetRenderSettingsBaseline() => m_HasSaved = false;

        public void Dispose()
        {
            // 必须等：读回是从 buffer 里搬数据，buffer 先被释放就是读已释放显存。
            if (m_HasRequest)
            {
                m_Request.WaitForCompletion();
                m_HasRequest = false;
            }

            // 不还原的代价不是"下次再覆盖一遍"那么轻 —— 关掉 feature 之后场景会**留在**
            // Custom 模式、挂着最后一帧那份 SH，于是"关了 Vista 画面还是过曝的"，
            // 排查方向会被完全带偏。
            RestoreRenderSettings();
            m_HasProbe = false;
        }
    }
}
