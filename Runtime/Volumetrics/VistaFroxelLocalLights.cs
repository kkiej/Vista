using UnityEngine;

namespace Vista
{
    /// <summary>
    /// 局部灯（点光 / 聚光 / 副平行光）参与介质散射时，要下发给注入核的那一组 uniform（#23）。
    ///
    /// ------------------------------------------------------------------ 为什么单独一个结构体
    /// 与 <see cref="VistaFroxelReprojection.Data"/> 同一条理由：这一组值有**两个**消费者
    /// —— 线上注入核，和判据探针核。让两个派发点各自拼一遍 <c>SetGlobalVector</c>，
    /// 「档位/缩放/遮罩」这三个字段的搬运就有了第二份实现，而它的失败模式是
    /// 「探针测的是 A 档、线上跑的是 B 档」—— 判据会给出一个**看起来全绿的错误结论**。
    ///
    /// 更要紧的是不能靠「探针在注入之后派发，所以继承线上的 uniform」这条顺序性论证：
    /// 本项目已经吃过这个亏（前一趟已经覆写过同一组 cbuffer 时，顺序论证是假的）。
    /// 探针核**自己**调一次 <see cref="Bind{T}"/>，才是「这一趟用的是哪一组值」在代码里读得出来。
    ///
    /// ------------------------------------------------------------------ 一致性的三条规则
    /// 这里做的事跟 <c>VistaFroxelReprojection.Data.Bind</c> 里那句
    /// <c>if (!dispatcher.HasTexture(BlueNoise)) effJitter.z = 0f;</c> 是同构的：
    /// **下发的值必须与实际绑上去/编译进去的东西自洽**，否则报表会印一个不成立的状态。
    ///
    /// 1. <b>阴影</b>：<c>additionalShadowsBound == false</c> 时强制关。URP 线上路径其实
    ///    **永远**会绑 <c>_AdditionalLightsShadowmapTexture</c>（空的时候绑
    ///    <c>defaultShadowTexture</c>，AdditionalLightsShadowCasterPass.cs:934/944），
    ///    所以这一条不是防「绑了 null」——它防的是**立即模式**（Editor 自检）：
    ///    那条路上没有 URP 的阴影 pass，全局里装的是上一个渲染过的相机留下的脏值。
    ///    读脏阴影图的症状是「自检里局部灯的光锥形状随便变」，而且与「阴影本来就该这样」
    ///    长得一样。
    /// 2. <b>渲染层遮罩</b>：<c>renderingLayersEnabled == false</c> 时强制成全 1。
    ///    关键字 <c>_LIGHT_LAYERS</c> 没开时，HLSL 侧的过滤**整段编译掉**
    ///    （理由见 FroxelLocalLights.hlsl：URP 只在 supportsLightLayers 时才下发
    ///    <c>_AdditionalLightsLayerMasks</c>，关掉时那个数组里是别的相机留下的脏 cbuffer 字节）。
    ///    此时若照原样下发美术填的遮罩，报表就会印出一个「美术选了 3 号层」而画面上
    ///    所有灯都过 —— 「一个选了什么都不发生的枚举值等于把哑档位摆给美术」的变体。
    ///    强制成全 1 之后，报表印的就是**真实发生的事**；同时
    ///    <see cref="layerMaskIgnored"/> 让判据(e) 能把这一档点名成「未覆盖」。
    /// 3. <b>档位</b>：<c>mode</c> **原样下发，CPU 一个字都不猜。</b>
    ///    「Forward+ 到底开没开」这件事 CPU 侧读不到 ——
    ///    <c>UniversalRenderer.usesClusterLightLoop</c> 是 internal，而
    ///    <c>_CLUSTER_LIGHT_LOOP</c> 是个 GlobalKeyword（编译期选变体）。
    ///    CPU 若在这里「贴心地」降档，就把一个**应当显式失败**的配置错误改成了
    ///    静默降级。所以这一条交给 GPU 侧的哨兵（判据(a) 两层）去点名。
    /// </summary>
    public readonly struct VistaFroxelLocalLightParams
    {
        /// <summary>相机前向**单位**向量（世界空间）。</summary>
        public readonly Vector3 cameraForward;
        /// <summary>相机近裁剪面（m）。URP 的 zbin 下标 0 就落在这里。</summary>
        public readonly float cameraNear;
        /// <summary>相机远裁剪面（m）。只作远端 ⓘ 诊断，不设门。</summary>
        public readonly float cameraFar;
        /// <summary>剔除档位。</summary>
        public readonly LocalLightMode mode;
        /// <summary>是否查 additional shadow atlas（已按规则 1 收敛）。</summary>
        public readonly bool shadows;
        /// <summary>全局强度缩放（candela 逃生口）。</summary>
        public readonly float intensityScale;
        /// <summary>渲染层遮罩（已按规则 2 收敛，0 已在设置侧解释成全 1）。</summary>
        public readonly uint layerMask;
        /// <summary>
        /// 美术填的遮罩被规则 2 覆盖掉了（<c>_LIGHT_LAYERS</c> 关着）。
        /// 判据(e) 拿它把「逐灯开关」这一档标成未覆盖 ——
        /// 「一个默认关闭、又没有判据覆盖的开关，等于一段永远不会被发现写错的代码」。
        /// </summary>
        public readonly bool layerMaskIgnored;

        VistaFroxelLocalLightParams(Vector3 cameraForward, float cameraNear, float cameraFar,
                                    LocalLightMode mode, bool shadows, float intensityScale,
                                    uint layerMask, bool layerMaskIgnored)
        {
            this.cameraForward = cameraForward;
            this.cameraNear = cameraNear;
            this.cameraFar = cameraFar;
            this.mode = mode;
            this.shadows = shadows;
            this.intensityScale = intensityScale;
            this.layerMask = layerMask;
            this.layerMaskIgnored = layerMaskIgnored;
        }

        /// <summary>
        /// 失能态：档位 0、前向零向量、缩放 0。
        ///
        /// 前向为零是**故意**的，不是「反正用不到」：判据(b) 的哨兵之一就是
        /// <c>dot(fwd, fwd) == 1</c>。零态若填成 (0,0,1)，一个「忘了下发」的帧
        /// 会长得跟「相机正朝着 +Z」一模一样；填零则那个哨兵立刻变红。
        /// 这与 VistaFroxelCB 那条「关掉 = 零态」的约定是同一条。
        /// </summary>
        public static VistaFroxelLocalLightParams disabled =>
            new VistaFroxelLocalLightParams(Vector3.zero, 0f, 0f,
                LocalLightMode.Off, false, 0f, uint.MaxValue, false);

        /// <summary>
        /// 从相机 + 设置里收敛出本帧要下发的那一组值。
        /// </summary>
        /// <param name="camera">取前向与近/远裁剪面。<c>null</c> ⇒ 失能态。</param>
        /// <param name="settings">
        /// 体积雾设置。<c>null</c> ⇒ 失能态（与其余 froxel 路径一致：没设置 = 没功能）。
        /// </param>
        /// <param name="renderingLayersEnabled">
        /// URP 的 <c>UniversalLightData.supportsLightLayers</c>。这**正是**
        /// ForwardLights.cs:624/654/727 用来决定要不要上传
        /// <c>_AdditionalLightsLayerMasks</c> 的那个开关，也正是
        /// <c>ShaderGlobalKeywords.LightLayers</c> 的下发条件 ——
        /// 所以这里读它，而不是读渲染管线资产上的 <c>useRenderingLayers</c>：
        /// 后者是**同一件事的第二个来源**，两者若哪天不再等价，症状是遮罩
        /// 静默失效而报表说它生效了。
        /// </param>
        /// <param name="additionalShadowsBound">
        /// 本帧是否真的有 URP 的 additional shadow atlas 可读（见规则 1）。
        /// </param>
        public static VistaFroxelLocalLightParams Resolve(Camera camera,
                                                          VistaVolumetricFogSettings settings,
                                                          bool renderingLayersEnabled,
                                                          bool additionalShadowsBound)
        {
            if (camera == null || settings == null)
                return disabled;

            var mode = settings.localLightMode;
            if (mode == LocalLightMode.Off)
                return disabled;

            // 规则 1
            bool shadows = settings.localLightShadows && additionalShadowsBound;

            // 规则 2
            uint requested = settings.ResolveLocalLightLayerMask();
            bool ignored = !renderingLayersEnabled && requested != uint.MaxValue;
            uint mask = renderingLayersEnabled ? requested : uint.MaxValue;

            return new VistaFroxelLocalLightParams(
                camera.transform.forward,   // Transform.forward = rotation * Vector3.forward，恒为单位向量
                camera.nearClipPlane,
                camera.farClipPlane,
                mode,
                shadows,
                Mathf.Max(0f, settings.localLightIntensityScale),
                mask,
                ignored);
        }

        /// <summary>
        /// 下发。注意 <c>_VistaFroxelViewForward</c> 属于 <c>VistaFroxelCB</c>
        /// （它描述的是相机，不是局部灯），但由本结构体下发 ——
        /// 因为它**只有**局部灯这一条路在读，放在唯一的消费者旁边，
        /// 「不走这条路的路径看到的是零态」才成立。
        /// </summary>
        public void Bind<T>(in T dispatcher) where T : IVistaLutDispatcher
        {
            dispatcher.SetGlobalVector(VistaShaderIDs._VistaFroxelViewForward,
                new Vector4(cameraForward.x, cameraForward.y, cameraForward.z, cameraNear));

            dispatcher.SetGlobalVector(VistaShaderIDs._VistaFroxelLocalLight,
                new Vector4((float)(int)mode, intensityScale, shadows ? 1f : 0f, cameraFar));

            VistaVolumetricFogSettings.SplitLayerMask(layerMask, out float lo, out float hi);
            dispatcher.SetGlobalVector(VistaShaderIDs._VistaFroxelLocalLightMask,
                new Vector4(lo, hi, 0f, 0f));
        }
    }
}
