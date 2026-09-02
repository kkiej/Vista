using UnityEngine;
using UnityEngine.Rendering;

namespace Vista
{
    /// <summary>
    /// 大气模块的逐视图数据：相机在大气空间中的位置、太阳方向、星球中心。
    ///
    /// 独立于 <see cref="VistaAtmosphereParameters"/> 的原因很实际：那些是"星球长什么样"，
    /// 静态 LUT 只依赖它；这里是"这一帧从哪儿看、太阳在哪儿"，逐帧变化，
    /// 决定 SkyView / AerialPerspective 两张表。混在一起会让脏检查退化成每帧重烘静态表。
    ///
    /// 空间约定见 <c>ShaderLibrary/AtmosphereDef.hlsl</c>：大气空间与世界空间同朝向，
    /// 只做平移 + 0.001 缩放，因此方向矢量可以跨空间直接使用。
    /// </summary>
    public struct VistaAtmosphereViewData
    {
        /// <summary>
        /// 采样点离星球表面的最小距离 (km)，= 10 m。
        /// 相机贴地时 r 恰好等于 bottomRadius，切线方向的 ray-sphere 判别式会在 0 附近抖动，
        /// 症状是地平线一圈噪点。必须与 HLSL 的 VISTA_PLANET_RADIUS_OFFSET 一致。
        /// </summary>
        public const float k_PlanetRadiusOffsetKm = 0.01f;

        /// <summary>星球中心在「km 缩放后的世界坐标系」中的位置。</summary>
        public Vector3 planetCenterKm;

        /// <summary>相机在大气空间中的位置 (km)，已钳制在大气层内。</summary>
        public Vector3 viewPosKm;

        /// <summary>到星球中心的半径 (km)，即 <see cref="viewPosKm"/> 的长度。</summary>
        public float viewHeightKm;

        /// <summary>世界空间中由着色点指向太阳的单位矢量。</summary>
        public Vector3 sunDirection;

        /// <summary>
        /// 物理单位 (cd/m²) -> 渲染目标单位的曝光倍率。
        /// 见 <c>AtmosphereDef.hlsl</c> 的 VISTA_EXPOSURE 说明。
        /// </summary>
        public float exposure;

        /// <summary>
        /// 视锥四角的世界空间方向，**未归一化**，长度取到相机前方 1 单位的平面上。
        /// AP froxel 核用它双线性插值出每根柱子的视线方向。
        /// 必须未归一化：透视投影下「同一平面上的位置」才是屏幕坐标的线性函数。
        /// 详见 <c>AtmosphereDef.hlsl</c> 中 _VistaApRayBL 的注释。
        /// </summary>
        public Vector3 rayBottomLeft, rayBottomRight, rayTopLeft, rayTopRight;

        /// <summary>
        /// 相机的世界 Y (m)，**未经过 km 缩放**。
        ///
        /// 为什么要单独存一份而不是从 <see cref="viewPosKm"/> 反算：
        /// viewPosKm.y 在 6360 km 附近，fp32 在那个量级上的 ulp 是
        /// 2^12 · 2^-23 = 4.883e-4 km ≈ **0.49 m**。雾的标高可以只有 20 m，
        /// 用反算出来的相机高度会把整条密度剖面量化成 ~41 级台阶，
        /// 症状是雾里出现随相机高度跳动的水平条带。
        /// 见 <c>ShaderLibrary/FogMedium.hlsl</c> 的「为什么高度不能从 posKm 算」。
        ///
        /// 已知偏差：<see cref="Create"/> 会把 viewPosKm 钳制在大气层内，而这个字段
        /// **不钳** —— 相机飞出大气顶时两者会不一致。那是刻意的：钳制的目的是保住
        /// SkyView LUT 的参数化，而雾的高度剖面在那个高度上密度早已是 0，
        /// 钳过的高度反而会让雾在大气顶外重新变浓。
        /// </summary>
        public float cameraWorldY;

        /// <summary>
        /// 摄影式曝光：exposure = 1 / (1.2 · 2^EV100)。
        /// EV100 = 15 对应"晴天正午"（Sunny 16 法则），是最常用的基准。
        /// </summary>
        public static float ExposureFromEV100(float ev100)
        {
            return 1f / (1.2f * Mathf.Pow(2f, ev100));
        }

        /// <summary>晴天正午基准 EV100。</summary>
        public const float k_DefaultEV100 = 15f;

        /// <summary>
        /// 从相机、地面基准高度、太阳方向构造。
        /// </summary>
        /// <param name="cameraWorldPos">相机世界位置 (m)。</param>
        /// <param name="groundLevelWorldY">世界空间中哪个 Y 对应星球表面 (m)。通常是场景海平面。</param>
        /// <param name="sunDirectionWorld">指向太阳的方向，无需预先归一化。</param>
        /// <param name="ev100">摄影曝光值。</param>
        public static VistaAtmosphereViewData Create(
            VistaAtmosphereParameters parameters,
            Vector3 cameraWorldPos,
            float groundLevelWorldY,
            Vector3 sunDirectionWorld,
            float ev100 = k_DefaultEV100)
        {
            float toKm = VistaAtmosphereParameters.worldToAtmosphere;

            var data = new VistaAtmosphereViewData();
            // 在做 km 缩放之前先取世界 Y：雾的高度剖面必须避开 6360 km 这个大数。
            data.cameraWorldY = cameraWorldPos.y;
            // 星球中心正在相机脚下 bottomRadius 处，所以世界 +Y 就是地面处的 up。
            data.planetCenterKm = new Vector3(
                0f, groundLevelWorldY * toKm - parameters.bottomRadius, 0f);

            Vector3 posKm = cameraWorldPos * toKm - data.planetCenterKm;

            // 钳制在大气层内。相机飞出大气顶时 SkyView LUT 的参数化不再成立
            // （地平线张角的定义变了），钳制比让表悄悄出错好——本项目相机也不会飞那么高。
            float minR = parameters.bottomRadius + k_PlanetRadiusOffsetKm;
            float maxR = parameters.topRadius - k_PlanetRadiusOffsetKm;
            float r = posKm.magnitude;
            if (r < 1e-4f)
            {
                posKm = new Vector3(0f, minR, 0f);
                r = minR;
            }
            else
            {
                float clamped = Mathf.Clamp(r, minR, maxR);
                if (clamped != r)
                {
                    posKm *= clamped / r;
                    r = clamped;
                }
            }

            data.viewPosKm    = posKm;
            data.viewHeightKm = r;

            Vector3 sun = sunDirectionWorld;
            // 太阳方向退化时给一个正头顶的兜底，避免整套 LUT 被 NaN 污染
            data.sunDirection = sun.sqrMagnitude < 1e-8f ? Vector3.up : sun.normalized;
            data.exposure = ExposureFromEV100(ev100);

            // 视锥兜底：正对 +Z、60° 垂直 FOV、16:9。
            // 没有这一步，未调用 SetFrustumRays 的路径（自检、反射探针）会让
            // AP 核对零矢量做 normalize，整张表变成 NaN —— 而 NaN 会顺着
            // 三线性插值蔓延到全屏，症状是画面成片发黑，极难反查到这里。
            data.SetFrustumRays(Vector3.forward, Vector3.right, Vector3.up,
                                Mathf.Tan(30f * Mathf.Deg2Rad) * (16f / 9f),
                                Mathf.Tan(30f * Mathf.Deg2Rad));

            return data;
        }

        static readonly Vector3[] s_FrustumCorners = new Vector3[4];

        /// <summary>
        /// 从相机取视锥四角方向。
        ///
        /// 用 <see cref="Camera.CalculateFrustumCorners"/> 而不是自己拿 fieldOfView / aspect 算：
        /// 物理相机（sensor shift、gate fit）下手算会错，而这个 API 直接给出相机空间的角点。
        /// 已知限制：它按 fov/aspect 推导，不反映 TAA 抖动那样的非对称投影修正 ——
        /// 但抖动是亚像素量级，在 32 列的 froxel 上是 1/60 个纹素，可以忽略。
        /// </summary>
        public void SetFrustumRays(Camera camera)
        {
            // z = 1：取相机前方 1 单位平面上的角点，正好是需要的未归一化方向
            camera.CalculateFrustumCorners(
                new Rect(0f, 0f, 1f, 1f), 1f, Camera.MonoOrStereoscopicEye.Mono, s_FrustumCorners);

            Matrix4x4 m = camera.transform.localToWorldMatrix;
            // CalculateFrustumCorners 的顺序：0 左下，1 左上，2 右上，3 右下
            rayBottomLeft  = m.MultiplyVector(s_FrustumCorners[0]);
            rayTopLeft     = m.MultiplyVector(s_FrustumCorners[1]);
            rayTopRight    = m.MultiplyVector(s_FrustumCorners[2]);
            rayBottomRight = m.MultiplyVector(s_FrustumCorners[3]);
        }

        /// <summary>
        /// 手动构造视锥四角（自检 / 无 Camera 对象的场合）。
        /// tanHalfFovX/Y 是半视角的正切，即前方 1 单位平面上的半宽 / 半高。
        /// </summary>
        public void SetFrustumRays(Vector3 forward, Vector3 right, Vector3 up,
                                   float tanHalfFovX, float tanHalfFovY)
        {
            Vector3 dx = right * tanHalfFovX;
            Vector3 dy = up * tanHalfFovY;
            rayBottomLeft  = forward - dx - dy;
            rayBottomRight = forward + dx - dy;
            rayTopLeft     = forward - dx + dy;
            rayTopRight    = forward + dx + dy;
        }

        /// <summary>
        /// 推送逐视图 cbuffer。走命令缓冲而不是 <c>Shader.SetGlobal*</c>：
        /// 这些值每个相机都不同，多相机（主相机 + 反射探针 + Editor 预览）下
        /// 全局静态赋值会串台。静态大气参数没有这个问题，所以那边仍用 Shader.SetGlobal*。
        ///
        /// 参数走 <see cref="IVistaLutDispatcher"/> 而不是具体的 CommandBuffer 类型：
        /// RenderGraph 的 compute pass 只给 ComputeCommandBuffer，Editor 预览只有原生
        /// CommandBuffer，两者没有共同基类。原因详见该接口的注释。
        /// </summary>
        public void Bind<T>(T cmd, int skyViewLutWidth, int skyViewLutHeight)
            where T : struct, IVistaLutDispatcher
        {
            cmd.SetGlobalVector(VistaShaderIDs._VistaPlanetCenterKm,
                new Vector4(planetCenterKm.x, planetCenterKm.y, planetCenterKm.z, 0f));
            cmd.SetGlobalVector(VistaShaderIDs._VistaViewPosKm,
                new Vector4(viewPosKm.x, viewPosKm.y, viewPosKm.z, viewHeightKm));
            cmd.SetGlobalVector(VistaShaderIDs._VistaSunDirection,
                new Vector4(sunDirection.x, sunDirection.y, sunDirection.z, exposure));
            cmd.SetGlobalVector(VistaShaderIDs._VistaSkyViewLutSize,
                new Vector4(skyViewLutWidth, skyViewLutHeight,
                            1f / skyViewLutWidth, 1f / skyViewLutHeight));
        }

        /// <summary>
        /// 只推视锥四角。
        ///
        /// 从 <see cref="BindAerialPerspective{T}"/> 里拆出来是因为出现了第二个消费者：
        /// 近层 froxel 体（#20）也用 <c>VistaApFroxelRayDirection</c> 求视线方向 ——
        /// 那是同一个相机的同一个视锥，再写一份插值等于给「两处视锥推导漂移」留门，
        /// 而那种漂移的症状是「近雾与远雾在画面边缘对不上」，会被误判成分层接缝。
        ///
        /// 但 froxel 体**不该**顺手下发 AP 的切片分布（_VistaApParams / Size / Flags）：
        /// 那三个决定 AP 表自己的深度映射，跟着雾体一起推就变成「改雾体的分辨率
        /// 会动 AP 的分布」。RenderGraph 还可能重排两个 pass，届时谁最后推谁生效。
        ///
        /// 四角本身没有这个问题：两条路径推的是**逐位相同的值**（同一个 view 对象），
        /// 所以重复下发与重排都无害。
        /// </summary>
        public void BindFrustumRays<T>(T cmd)
            where T : struct, IVistaLutDispatcher
        {
            cmd.SetGlobalVector(VistaShaderIDs._VistaApRayBL,
                new Vector4(rayBottomLeft.x, rayBottomLeft.y, rayBottomLeft.z, 0f));
            cmd.SetGlobalVector(VistaShaderIDs._VistaApRayBR,
                new Vector4(rayBottomRight.x, rayBottomRight.y, rayBottomRight.z, 0f));
            cmd.SetGlobalVector(VistaShaderIDs._VistaApRayTL,
                new Vector4(rayTopLeft.x, rayTopLeft.y, rayTopLeft.z, 0f));
            cmd.SetGlobalVector(VistaShaderIDs._VistaApRayTR,
                new Vector4(rayTopRight.x, rayTopRight.y, rayTopRight.z, 0f));
        }

        /// <summary>
        /// 推送 AP froxel 相关的逐视图常量。与 <see cref="Bind{T}"/> 分开是因为
        /// 视锥四角只有 AP 需要，而 Sky-View 那条链路（含反射探针的六个面）不需要，
        /// 合在一起会让"哪些相机必须调 SetFrustumRays"变得含糊。
        /// </summary>
        public void BindAerialPerspective<T>(T cmd, VistaAerialPerspectiveSettings settings)
            where T : struct, IVistaLutDispatcher
        {
            cmd.SetGlobalVector(VistaShaderIDs._VistaApParams, settings.packedParams);
            cmd.SetGlobalVector(VistaShaderIDs._VistaApSize, settings.packedSize);
            cmd.SetGlobalVector(VistaShaderIDs._VistaApFlags, settings.packedFlags);
            BindFrustumRays(cmd);
        }

        /// <summary>
        /// 推送雾的 cbuffer。
        ///
        /// 为什么和 <see cref="BindAerialPerspective{T}"/> 分开：雾有两个消费者
        /// （档 D 的 AP LUT、档 A 的近层 froxel 体），后者不需要 AP 的切片分布与视锥四角。
        /// 合成一个函数会让「近层雾体也得先配好 AP 的分布」这种伪依赖固化下来。
        ///
        /// <paramref name="fog"/> 为 null 时下发全零 —— 与 Off 档同一条路径。
        /// 零态是 <c>FogMedium.hlsl</c> 刻意保住的性质：σ_t = 0 时消光与散射都精确为 0，
        /// 于是「没配雾」「明确关雾」「忘了下发」三者殊途同归，都只能是没有雾。
        ///
        /// **每帧无条件调用**：cbuffer 里的相机高度逐帧变，而且雾从开到关的那一帧
        /// 如果跳过下发，shader 会拿着上一帧的 σ_t 继续算 —— 那正是
        /// <c>_VistaApConsumer</c> 踩过的坑（见 VistaShaderIDs 里那条注释）。
        /// </summary>
        public void BindFog<T>(T cmd, VistaFogSettings fog)
            where T : struct, IVistaLutDispatcher
        {
            if (fog == null)
            {
                cmd.SetGlobalVector(VistaShaderIDs._VistaFogAlbedo, Vector4.zero);
                cmd.SetGlobalVector(VistaShaderIDs._VistaFogExtinct, Vector4.zero);
                cmd.SetGlobalVector(VistaShaderIDs._VistaFogHeight, Vector4.zero);
                return;
            }

            cmd.SetGlobalVector(VistaShaderIDs._VistaFogAlbedo, fog.packedAlbedo);
            cmd.SetGlobalVector(VistaShaderIDs._VistaFogExtinct, fog.packedExtinct);
            cmd.SetGlobalVector(VistaShaderIDs._VistaFogHeight, fog.PackedHeight(cameraWorldY));
        }
    }
}
