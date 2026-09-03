using UnityEngine;

namespace Vista
{
    /// <summary>
    /// 近层 froxel 体的时间重投影状态（#22）：持有上一帧的视图，并算出本帧要下发的
    /// 那一组 uniform。
    ///
    /// ------------------------------------------------------------------ 为什么不用 URP 自己的历史机制
    /// URP 有一套 <c>UniversalCameraHistory</c>（<c>RequestAccess&lt;T&gt;</c> /
    /// <c>GetHistoryForRead&lt;T&gt;</c>，<c>TaaHistory</c> 是它的参考实现），能按相机管
    /// 历史纹理的生命周期。这里**不用**它，三条理由：
    ///
    /// 1. 它的入口只在渲染循环里（<c>UniversalCameraData.historyManager</c>）。本项目的
    ///    froxel 判据有一半跑在立即模式下 —— 那里连相机都没有。走 URP 的历史就等于
    ///    「线上一条路、判据另一条路」，而那是本项目反复吃过的亏。
    /// 2. 它的分配口径是「屏幕参考尺寸的 2D 纹理」（<c>SwapAndSetReferenceSize(w,h)</c>
    ///    + <c>BufferedRTHandleSystem</c> 里 buffer[0] 自动 resize）。froxel 体是
    ///    屏幕/8 × N 片的 Tex3D + <c>enableRandomWrite</c>，套进去要绕的比自己写的多。
    /// 3. 「按相机隔离」这件事本身只值一次整数比较（见 <see cref="m_PrevCameraId"/>），
    ///    而自己写的版本可以**把失效原因打印出来** —— URP 那套失效是静默的。
    ///
    /// 代价点名：Game 与 SceneView 同时开着时，两个相机轮流走这里，
    /// <see cref="m_PrevCameraId"/> 每帧都不匹配 ⇒ 历史每帧失效 ⇒ 累积退化成单帧，
    /// 但**永远不会错**（不会拿另一个相机的历史去混）。而且那种情况下 froxel 体
    /// 本来就在每帧重分配（两个相机的分辨率不同），退化不是本类引入的。
    ///
    /// ------------------------------------------------------------------ 为什么历史表不需要跟着分片布局失效
    /// 近/远距离改变（相机阴影距离变了）**不**触发纹理重分配 —— 分配脏检查只看三个尺寸。
    /// 所以历史表里那 64 片存的是「上一帧那套 near/far 下的距离」。本类把上一帧的
    /// <c>(near, far, logRatio, 1/logRatio)</c> 一起下发，shader 用**它**去反解历史的片坐标，
    /// 于是布局变化不需要任何失效逻辑就是正确的。
    /// 如果只下发本帧的范围、拿它去查历史，症状是雾在改阴影距离的那一帧整体前后错一下 ——
    /// 一个「看起来物理上讲得通的漂移」。
    /// </summary>
    public sealed class VistaFroxelReprojection
    {
        // ---- R3 塑性常数的 Kronecker 序列 ----
        //
        // φ₃ 是 x⁴ = x + 1 的实根（1.2207440846...），α = (1/φ₃, 1/φ₃², 1/φ₃³)。
        // 三个分量互相无理独立，所以「横向 x、横向 y、深度」三个抖动轴在时间上不会同步 ——
        // 同步的症状是抖动退化成沿对角线的一条线，等效采样数从 N³ 掉回 N。
        //
        // 为什么不是「每帧一个随机数」：累积窗口就是 N ≈ τ·fps 帧（默认 τ=0.33 s、60 fps
        // ⇒ 20 帧），随机数在 20 个样本里的覆盖有洞（生日碰撞），洞的症状是残影里
        // 一层低频斑。Kronecker 序列的差异度有下界，1D 上是可证最优的一类。
        //
        // 为什么不是一张 z=64 的 3D 噪声纹理：那个每 64 帧循环一次，而这个**永不循环**。
        // 而且「对每个像素加同一个 t·α」是平移变换，所以空间上的蓝噪声性质逐帧保持 ——
        // 2D 蓝噪声 + 这条时间轴在**两个边缘轴**上都是最优的，真 3D STBN 多买到的
        // 只有斜对角频率，而体积雾这种低频信号对斜对角最不敏感。
        const double k_Plastic = 1.2207440846057594753616853503;

        static readonly Vector3 k_R3Alpha = new Vector3(
            (float)(1.0 / k_Plastic),
            (float)(1.0 / (k_Plastic * k_Plastic)),
            (float)(1.0 / (k_Plastic * k_Plastic * k_Plastic)));

        /// <summary>Δt 的夹紧区间（秒）。上端 1 s 是为了让暂停/断点之后的第一帧不至于
        /// 直接把历史权重压到 0（那会在恢复的一瞬间闪一下噪声）。下端防除零。</summary>
        const float k_MinDeltaTime = 1e-4f;
        const float k_MaxDeltaTime = 1f;

        /// <summary>本帧要下发给注入核的那一组值。由 <see cref="Update"/> 产出。</summary>
        public readonly struct Data
        {
            /// <summary>上一帧的 viewProj（Unity 的 GL 风格 clip space，见 <see cref="Update"/>）。</summary>
            public readonly Matrix4x4 prevViewProj;

            /// <summary>上一帧的分片范围 (near, far, logRatio, 1/logRatio)，单位米。</summary>
            public readonly Vector4 prevRange;

            /// <summary>xyz = 上一帧相机世界位置 (m)；w = **历史**的混合权重 ∈ [0,1]。</summary>
            public readonly Vector4 prevCameraWS;

            /// <summary>xyz = R3 序列的本帧相位 ∈ [0,1)³；w 保留。</summary>
            public readonly Vector4 jitterPhase;

            /// <summary>x = 横向抖动幅度（格），y = 深度抖动幅度（片），zw 保留。</summary>
            public readonly Vector4 jitter;

            /// <summary>x = 亮度死区下端，y = 1/(上端 − 下端)，zw 保留。</summary>
            public readonly Vector4 rejectParams;

            public Data(Matrix4x4 prevViewProj, Vector4 prevRange, Vector4 prevCameraWS,
                        Vector4 jitterPhase, Vector4 jitter, Vector4 rejectParams)
            {
                this.prevViewProj = prevViewProj;
                this.prevRange    = prevRange;
                this.prevCameraWS = prevCameraWS;
                this.jitterPhase  = jitterPhase;
                this.jitter       = jitter;
                this.rejectParams = rejectParams;
            }

            /// <summary>历史权重为 0 ⇒ 本帧纯新样本。判据与状态日志都读这一条。</summary>
            public bool usesHistory => prevCameraWS.w > 0f;

            /// <summary>
            /// 下发。走全局而不是逐核参数：注入核与探针核都要读同一组值，
            /// 而探针核必须读到**注入核实际吃进去的那一份**（同一条理由让
            /// <c>RenderFroxelShadowProbe</c> 刻意不重推 <c>_VistaFroxelCameraWS</c>）。
            /// </summary>
            public void Bind<T>(in T dispatcher) where T : IVistaLutDispatcher
            {
                dispatcher.SetGlobalMatrix(VistaShaderIDs._VistaFroxelPrevViewProj, prevViewProj);
                dispatcher.SetGlobalVector(VistaShaderIDs._VistaFroxelPrevRange, prevRange);
                dispatcher.SetGlobalVector(VistaShaderIDs._VistaFroxelPrevCameraWS, prevCameraWS);
                dispatcher.SetGlobalVector(VistaShaderIDs._VistaFroxelJitterPhase, jitterPhase);
                dispatcher.SetGlobalVector(VistaShaderIDs._VistaFroxelJitter, jitter);
                dispatcher.SetGlobalVector(VistaShaderIDs._VistaFroxelReprojParams, rejectParams);
            }

            /// <summary>
            /// 完全失能态：全零 + 单位矩阵。历史权重 0 ⇒ 纯本帧，抖动幅度 0 ⇒ 恒在格心。
            ///
            /// 「关掉 = 零态」这条约定在这里尤其要紧：历史权重那一位若零态是 1，
            /// 一个没下发的帧就会去混一张未初始化的 fp16 显存，而那里面可能是 NaN。
            /// </summary>
            public static Data disabled => new Data(
                Matrix4x4.identity, Vector4.zero, Vector4.zero,
                Vector4.zero, Vector4.zero, Vector4.zero);
        }

        Matrix4x4 m_PrevViewProj = Matrix4x4.identity;
        Vector3 m_PrevCameraWS;
        Vector4 m_PrevRange;

        // 0 = 还没记过任何相机。Camera.GetInstanceID() 不会是 0。
        int m_PrevCameraId;

        // 自己数帧，不用 Time.frameCount：判据要能在一次菜单调用里把它推进受控的步数，
        // 而 Time.frameCount 在 Editor 里由编辑器自己的重绘节奏推动 ——
        // 那会让「上一帧矩阵的新鲜度」这一格的读数取决于鼠标有没有在动。
        uint m_FrameIndex;

        // 上一帧的状态是在哪一个 m_FrameIndex 上捕获的。−1 = 没捕获过。
        // 判据⑭读它：新鲜的历史必须满足 captured == frameIndex − 1。
        long m_PrevCapturedAtFrame = -1;

        /// <summary>连续多少帧历史都是有效的。判据⑯（收敛性）用它决定什么时候可以开始判。</summary>
        public int framesSinceValid { get; private set; }

        /// <summary>本帧的序号（<see cref="Update"/> 每调一次 +1）。</summary>
        public uint frameIndex => m_FrameIndex;

        /// <summary>上一帧状态的捕获帧号；−1 = 没捕获过。判据⑭的输入。</summary>
        public long prevCapturedAtFrame => m_PrevCapturedAtFrame;

        /// <summary>本帧历史为什么不可用（可用时为 null）。状态日志直接打印它。</summary>
        public string lastInvalidReason { get; private set; }

        /// <summary>
        /// 丢弃历史。相机切换、体积重分配、模式改变之外，Editor 里域重载后也该调它。
        /// </summary>
        public void Invalidate(string reason)
        {
            m_PrevCapturedAtFrame = -1;
            m_PrevCameraId = 0;
            framesSinceValid = 0;
            lastInvalidReason = reason;
        }

        /// <summary>
        /// 推进一帧：算出本帧的 uniform，并把本帧的视图记下来给下一帧用。
        ///
        /// ------------------------------------------------------------------ 为什么矩阵不过 GL.GetGPUProjectionMatrix
        /// 那个函数会在「渲染进纹理」时按平台翻转 Y。但这里算出来的 uv **不是**用来采
        /// 屏幕纹理的，而是喂给 <c>VistaApFroxelRayDirection</c> 那套 uv 约定
        /// （uv.y 自下而上，因为它以 <c>lerp(bottom, top, uv.y)</c> 结尾）。
        /// 用 <c>camera.projectionMatrix * camera.worldToCameraMatrix</c>（Unity 的
        /// GL 风格 clip space：y 向上、w &gt; 0 表示在相机前方）就与那套约定天然一致，
        /// 而且**与平台无关** —— 不需要在 shader 里补一个「D3D 上翻一下」的分支，
        /// 那种分支写错的症状是重投影上下颠倒，在低频雾上看起来只是「历史权重太高」。
        ///
        /// 用的是**非抖动**的投影矩阵：抖动只服务本帧的采样点，历史表里存的是那个
        /// froxel 的累积均值，其标称位置就是格心。拿抖动后的矩阵去反查等于给历史
        /// 加一个每帧变化的偏移，症状是静止画面也在抖。
        /// </summary>
        /// <param name="camera">本帧的相机。</param>
        /// <param name="desc">本帧的分配口径（用来记下一帧要用的 prevRange）。</param>
        /// <param name="historyContentValid">
        /// 历史那张纹理里是否有上一帧写进去的内容，见
        /// <see cref="VistaFroxelVolume.historyContentValid"/>。
        /// </param>
        /// <param name="settings">抖动幅度、τ、亮度死区。</param>
        /// <param name="deltaTime">
        /// 本帧时长（秒）。传 <c>Time.unscaledDeltaTime</c> 而不是 <c>deltaTime</c>：
        /// 时间缩放（子弹时间）不该改雾的收敛速度 —— 那会让慢动作里的残影变长。
        /// </param>
        public Data Update(Camera camera, in VistaFroxelVolumeDesc desc,
                           bool historyContentValid, VistaVolumetricFogSettings settings,
                           float deltaTime)
        {
            m_FrameIndex++;

            bool jitterOn = settings != null && settings.jitterMode != JitterMode.Off;

            int cameraId = camera != null ? camera.GetInstanceID() : 0;
            Matrix4x4 viewProj = ViewProjOf(camera);
            Vector3 cameraWS = camera != null ? camera.transform.position : Vector3.zero;

            // 失效判定按「最先命中的那一条」报，不合并 —— 状态日志要能点名唯一原因。
            string reason = null;
            if (!jitterOn)                                  reason = "抖动/重投影档位为 Off";
            else if (camera == null)                         reason = "没有相机（立即模式）";
            else if (!historyContentValid)                   reason = "历史表这一帧还没被写过（刚重分配或首帧）";
            else if (m_PrevCapturedAtFrame < 0)              reason = "上一帧的视图没被捕获过";
            else if (m_PrevCapturedAtFrame != m_FrameIndex - 1) reason = "上一帧的视图不是紧邻上一帧捕获的";
            else if (m_PrevCameraId != cameraId)             reason = "换了相机";

            float historyWeight = 0f;
            if (reason == null)
            {
                // alpha = 新样本的权重 = 1 − exp(−Δt/τ)；历史权重 = 1 − alpha = exp(−Δt/τ)。
                // 直接下发历史权重（而不是 alpha）是为了让零态 = 纯本帧，见 Data.disabled。
                float dt  = Mathf.Clamp(deltaTime, k_MinDeltaTime, k_MaxDeltaTime);
                float tau = Mathf.Max(settings.historyTimeConstant, k_MinDeltaTime);
                historyWeight = Mathf.Exp(-dt / tau);
                framesSinceValid++;
            }
            else
            {
                framesSinceValid = 0;
            }

            lastInvalidReason = reason;

            Vector4 phase = Vector4.zero;
            Vector4 jitter = Vector4.zero;
            if (jitterOn)
            {
                // frac(frameIndex · α)。乘法在 double 上做：float 下 frameIndex 到
                // 2^24 就开始丢整数位，相位会**冻住** —— 一个跑了 78 小时（60 fps）
                // 之后才出现、且表现为「抖动突然没了」的问题。
                phase = new Vector4(
                    (float)Frac(m_FrameIndex * (double)k_R3Alpha.x),
                    (float)Frac(m_FrameIndex * (double)k_R3Alpha.y),
                    (float)Frac(m_FrameIndex * (double)k_R3Alpha.z),
                    0f);
                jitter = new Vector4(
                    Mathf.Clamp01(settings.lateralJitterAmount),
                    Mathf.Clamp01(settings.depthJitterAmount),
                    0f, 0f);
            }

            Vector4 reject = RejectParamsOf(settings);

            var data = new Data(
                m_PrevViewProj,
                m_PrevRange,
                new Vector4(m_PrevCameraWS.x, m_PrevCameraWS.y, m_PrevCameraWS.z, historyWeight),
                phase, jitter, reject);

            // 捕获本帧，给下一帧用。**在算完 data 之后**做 —— 反过来的话本帧就会
            // 拿本帧的矩阵当「上一帧」，而那种错误在静止画面上一个像素都不差
            // （判据⑬会全绿），只有相机一动才露出来。判据⑭盯的就是这一行。
            if (camera != null)
            {
                m_PrevViewProj        = viewProj;
                m_PrevCameraWS        = cameraWS;
                m_PrevRange           = desc.packedRange;
                m_PrevCameraId        = cameraId;
                m_PrevCapturedAtFrame = m_FrameIndex;
            }

            return data;
        }

        static double Frac(double x) => x - System.Math.Floor(x);

        /// <summary>
        /// 本类**唯一**一处构造 viewProj 的地方。判据⑬（静止恒等性）的精确性依赖于
        /// 它与 <c>Camera.CalculateFrustumCorners</c>（<see cref="VistaAtmosphereViewData.SetFrustumRays"/>
        /// 用它推视锥四角）同源于 <c>camera.projectionMatrix</c> —— 抽成一处是为了让
        /// 「探针用的矩阵和线上用的不是同一个」不可能发生。
        /// </summary>
        static Matrix4x4 ViewProjOf(Camera camera) => camera != null
            ? camera.projectionMatrix * camera.worldToCameraMatrix
            : Matrix4x4.identity;

        /// <summary>
        /// 亮度死区的下发形式 (下端, 1/(上端 − 下端))。线上与探针共用这一份 ——
        /// 各写一份的话，探针那一份的宽度倒数写反了都不会被发现（角色 4 靠 rel ≡ 1
        /// 驱动，倒数写反时 t 仍然 ≥ 1，那一格照样绿）。
        ///
        /// 宽度 &gt; 0 由 <see cref="VistaVolumetricFogSettings.ResolveLuminanceReject"/> 保证，
        /// 所以这里不兜底 —— 兜底会让「两个滑条被拖成上端 ≤ 下端」变成看不出来的事。
        /// </summary>
        static Vector4 RejectParamsOf(VistaVolumetricFogSettings settings)
        {
            if (settings == null) return Vector4.zero;

            settings.ResolveLuminanceReject(out float start, out float full);
            return new Vector4(start, 1f / (full - start), 0f, 0f);
        }

        /// <summary>
        /// 探针的历史权重。任意正数都行 —— 角色 2 只读 uvw，角色 3 只需要它 &gt; 0
        /// （否则第一条谓词就返回 NO_HISTORY），角色 4 的两条守卫都在
        /// <c>weight</c> 被赋值之前就 return 了。取 0.5 而不是 1.0 是为了让
        /// 「探针那一趟把线上的历史权重覆盖成了 1」在读代码时显眼。
        /// </summary>
        const float k_ProbeHistoryWeight = 0.5f;

        /// <summary>
        /// **仅供判据**：构造一份「上一帧 = 本帧」的 <see cref="Data"/>。
        ///
        /// 为什么需要它：静止恒等性本可以「等相机不动的那一帧顺手量一下」，
        /// 但那样相机一动这一格就是空判据。把 prev 覆盖成 current 之后，
        /// 「格心投回去正好落在自己那一格的纹素中心」这条恒等式**无条件**成立、
        /// 无条件可判。它是必要不充分的（prev = current 时成立不证明
        /// prev ≠ current 时矩阵是上一帧那个）—— 补上另一半的是判据⑭。
        ///
        /// 位移驱动的那几条拒绝分支也用这一份：先让重投影在「零位移下恒等成立」，
        /// 再由探针在调用点加合成位移，于是「被拒绝」只能是位移造成的。
        ///
        /// <paramref name="settings"/> 的亮度死区**必须**照实带上：角色 4 靠
        /// rel ≡ 1 去驱动亮度守卫，而死区全零时 y = 0 ⇒ t = 0 ⇒ 不拒绝 ⇒ 假失败。
        /// 抖动那两项则刻意留零 —— 角色 2~4 都不调 <c>VistaFroxelInject</c>，
        /// 带上一份不被读的抖动幅度只会让「这一趟到底依赖什么」变模糊。
        /// </summary>
        public static Data MakeStaticIdentityData(
            Camera camera, in VistaFroxelVolumeDesc desc, VistaVolumetricFogSettings settings)
        {
            Vector3 cameraWS = camera != null ? camera.transform.position : Vector3.zero;

            return new Data(
                ViewProjOf(camera),
                desc.packedRange,
                new Vector4(cameraWS.x, cameraWS.y, cameraWS.z, k_ProbeHistoryWeight),
                Vector4.zero, Vector4.zero, RejectParamsOf(settings));
        }
    }
}
