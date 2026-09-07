using UnityEngine;

namespace Vista
{
    /// <summary>
    /// 一帧、一相机的局部雾体集合：收集 → 视锥剔除 → 打包成 cbuffer 数组（#24）。
    ///
    /// ------------------------------------------------------------------ 为什么是结构体 + Bind
    /// 与 <see cref="VistaFroxelLocalLightParams"/> 同一条理由：这组值有两个消费者
    /// （线上注入核、判据探针核），各自拼一遍 SetGlobalVectorArray 就是第二份搬运，
    /// 失败模式是「探针测的体集合与线上跑的不是同一批」。
    ///
    /// ------------------------------------------------------------------ 数组的生命周期（环形池）
    /// RenderGraph 的 Bind 发生在 pass 执行时，而 Gather 发生在录制时 ——
    /// 同一帧里两个相机（Game + SceneView）都录完才执行，若共用一组静态数组，
    /// 后录的会覆盖先录的，症状是 SceneView 的体集合出现在 Game 相机的雾里。
    /// 解法是 4 组数组轮转：每次 Gather 取下一组。每帧最多 2 个相机各 Gather 一次
    /// ⇒ 一组数组要隔 2 帧才被复用，那时它所在的命令流早已执行完。
    /// （SetGlobalVectorArray 在**调用时**把数组内容拷进命令流，所以「执行完」
    /// 之后复用是安全的；4 组是 2 相机 × 2 帧的余量。）
    /// </summary>
    public readonly struct VistaFogVolumeSet
    {
        /// <summary>与 FogVolumes.hlsl 的 VISTA_FOG_VOL_MAX 耦合。数量守恒判据 (a) 对账。</summary>
        public const int k_MaxVolumes = 16;

        // 一组打包数组。九个数组的布局与 FogVolumes.hlsl 的 VistaFogVolCB 一一对应。
        sealed class Bundle
        {
            public readonly Vector4[] row0   = new Vector4[k_MaxVolumes];
            public readonly Vector4[] row1   = new Vector4[k_MaxVolumes];
            public readonly Vector4[] row2   = new Vector4[k_MaxVolumes];
            public readonly Vector4[] blend  = new Vector4[k_MaxVolumes];
            public readonly Vector4[] medium = new Vector4[k_MaxVolumes];
            public readonly Vector4[] noise  = new Vector4[k_MaxVolumes];
            public readonly Vector4[] pars   = new Vector4[k_MaxVolumes];
            public readonly Vector4[] probeC = new Vector4[k_MaxVolumes];
            public readonly Vector4[] probeO = new Vector4[k_MaxVolumes];
        }

        static readonly Bundle[] s_Pool = { new Bundle(), new Bundle(), new Bundle(), new Bundle() };
        static int s_PoolNext;

        // 剔除的工作数组。Gather 只在主线程调（录制线程），无并发。
        static readonly Plane[] s_FrustumPlanes = new Plane[6];

        readonly Bundle m_Bundle;   // null = 失能态

        /// <summary>上传的体数（≤ 16）。报表判据 (a) 拿它与 GPU 读到的数对账。</summary>
        public readonly int count;

        /// <summary>因超出上限被丢弃的可见体数。>0 时报表点名 —— 「无声截断读起来像全覆盖」。</summary>
        public readonly int dropped;

        /// <summary>上传各体 σ_t 之和 (1/km)。判据⑪d/⑫b 的介质上界要加上它（重叠取和是安全上界）。</summary>
        public readonly float sigmaSumPerKm;

        /// <summary>上传各体 σ_t 的最大值 (1/km)。报表状态行用。</summary>
        public readonly float sigmaMaxPerKm;

        VistaFogVolumeSet(Bundle bundle, int count, int dropped, float sigmaSum, float sigmaMax)
        {
            m_Bundle = bundle;
            this.count = count;
            this.dropped = dropped;
            sigmaSumPerKm = sigmaSum;
            sigmaMaxPerKm = sigmaMax;
        }

        /// <summary>失能态：体数 0。Bind 仍会下发 count = 0 —— 零态必须显式写，
        /// 否则上一个相机留下的 count 会让这台相机凭空长出别人的雾体。</summary>
        public static VistaFogVolumeSet disabled => default;

        /// <summary>
        /// 收集本相机可见的体并打包。
        /// </summary>
        /// <param name="camera">剔除用。null ⇒ 失能态。</param>
        /// <param name="farDistanceMeters">
        /// froxel 远边界（米）。用设置里的原始值而不是被阴影距离夹过的值 ——
        /// 原始值更大 ⇒ 剔除是保守超集，多传一个体无害（GPU 权重为 0），
        /// 而拿夹后值需要把 desc 一路穿到这里，多一条数据依赖换不来任何画面差异。
        /// </param>
        public static VistaFogVolumeSet Gather(Camera camera, float farDistanceMeters)
        {
            var all = VistaFogVolume.active;
            if (camera == null || all.Count == 0)
                return disabled;

            GeometryUtility.CalculateFrustumPlanes(camera, s_FrustumPlanes);
            Vector3 camPos = camera.transform.position;
            Vector3 camFwd = camera.transform.forward;

            var bundle = s_Pool[s_PoolNext];
            s_PoolNext = (s_PoolNext + 1) & 3;

            // 噪声时钟。Editor 非播放态用编辑器时钟 —— Time.time 在非播放态不走，
            // 雾会冻住，美术调噪声参数时看不到动画。
#if UNITY_EDITOR
            double now = Application.isPlaying
                ? Time.timeAsDouble : UnityEditor.EditorApplication.timeSinceStartup;
#else
            double now = Time.timeAsDouble;
#endif

            int n = 0, droppedCount = 0;
            float sigmaSum = 0f, sigmaMax = 0f;
            float worstDistSqr = float.NegativeInfinity;
            int worstIdx = -1;

            for (int i = 0; i < all.Count; ++i)
            {
                var vol = all[i];
                if (vol == null) continue;

                var t  = vol.transform;
                var he = vol.halfExtentsMeters;

                // OBB 的世界 AABB：|R|·he（R 的各列绝对值加权）。
                var rot = t.rotation;
                Vector3 ax = rot * new Vector3(he.x, 0f, 0f);
                Vector3 ay = rot * new Vector3(0f, he.y, 0f);
                Vector3 az = rot * new Vector3(0f, 0f, he.z);
                Vector3 ext = new Vector3(
                    Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x),
                    Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
                    Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
                var bounds = new Bounds(t.position, ext * 2f);

                if (!GeometryUtility.TestPlanesAABB(s_FrustumPlanes, bounds))
                    continue;

                // 近层之外的体不上传：沿相机前向的最近点已在 froxel 远边界之外。
                float alongFwd = Vector3.Dot(camFwd, t.position - camPos) - ext.magnitude;
                if (alongFwd > farDistanceMeters)
                    continue;

                float distSqr = (t.position - camPos).sqrMagnitude;

                int slot;
                if (n < k_MaxVolumes)
                {
                    slot = n++;
                }
                else
                {
                    // 超上限：留最近的 16 个。替换掉当前最远的那一个 ——
                    // 不排序整表（每帧 O(N log N) 换一个几乎不发生的路径不值），
                    // 只维护「已上传里最远的」这一个候选。
                    droppedCount++;
                    if (worstIdx < 0)
                    {
                        for (int k = 0; k < k_MaxVolumes; ++k)
                        {
                            Vector3 c = new Vector3(bundle.probeC[k].x, bundle.probeC[k].y,
                                                    bundle.probeC[k].z);
                            float d = (c - camPos).sqrMagnitude;
                            if (d > worstDistSqr) { worstDistSqr = d; worstIdx = k; }
                        }
                    }
                    if (distSqr >= worstDistSqr)
                        continue;
                    slot = worstIdx;
                    worstIdx = -1;                        // 槽位内容变了，候选作废重扫
                    worstDistSqr = float.NegativeInfinity;
                    sigmaSum -= bundle.medium[slot].w;    // 被替换体的 σ 退出总和
                }

                Pack(bundle, slot, vol, t, he, now);
                float sigma = bundle.medium[slot].w;
                sigmaSum += sigma;
                if (sigma > sigmaMax) sigmaMax = sigma;
            }

            if (n == 0)
                return disabled;

            // 未用槽位清零：count 门住 GPU 循环，这里再清是防「报表把整组数组
            // 打出来看」时残留上一帧的体污染人工排查（不是正确性需要）。
            for (int k = n; k < k_MaxVolumes; ++k)
            {
                bundle.row0[k] = bundle.row1[k] = bundle.row2[k] = Vector4.zero;
                bundle.blend[k] = bundle.medium[k] = bundle.noise[k] = Vector4.zero;
                bundle.pars[k] = bundle.probeC[k] = bundle.probeO[k] = Vector4.zero;
            }

            // sigmaMax 在有替换发生时可能高估（被换出的体可能正是最大值）——
            // 它只进状态行不进门，如实标注为上界即可；sigmaSum 是精确维护的。
            return new VistaFogVolumeSet(bundle, n, droppedCount, sigmaSum, sigmaMax);
        }

        static void Pack(Bundle b, int slot, VistaFogVolume vol, Transform t, Vector3 he, double now)
        {
            // 局部空间 = 半尺寸折进矩阵的单位盒：|x|,|y|,|z| ≤ 1。
            var worldToLocal = Matrix4x4.TRS(t.position, t.rotation, he).inverse;
            b.row0[slot] = worldToLocal.GetRow(0);
            b.row1[slot] = worldToLocal.GetRow(1);
            b.row2[slot] = worldToLocal.GetRow(2);

            // blendLocal 夹到 [1e-4, 1]：上限 1 保证体中心权重构造性地精确为 1
            //（判据(b) 的门），下限 1e-4 让「硬边缘」是一个很陡的斜坡而不是除零。
            float blendM = Mathf.Max(0f, vol.blendDistanceMeters);
            float rbx = 1f / Mathf.Clamp(blendM / he.x, 1e-4f, 1f);
            float rby = 1f / Mathf.Clamp(blendM / he.y, 1e-4f, 1f);
            float rbz = 1f / Mathf.Clamp(blendM / he.z, 1e-4f, 1f);
            if (vol.shape == VistaFogVolumeShape.Ellipsoid)
            {
                // 径向衰减用最短半轴换算 —— 用最长的会让短轴方向的 blend 超出体外。
                float heMin = Mathf.Min(he.x, Mathf.Min(he.y, he.z));
                rbx = 1f / Mathf.Clamp(blendM / heMin, 1e-4f, 1f);
            }
            b.blend[slot] = new Vector4(rbx, rby, rbz, (float)vol.shape);

            float sigmaT = 1000f / Mathf.Max(1f, vol.meanFreePathMeters);
            var alb = vol.albedo;
            b.medium[slot] = new Vector4(alb.r, alb.g, alb.b, sigmaT);

            // 噪声域偏移 = 速度·t / 尺度，双精度累到回绕（mod 1024 格）再降 fp32 ——
            // 不回绕的话挂机几小时后偏移到 1e5 量级，fp32 的格内插值开始量化，
            // 症状是「雾的翻滚越来越卡顿」且重启就好。
            float rcpScale = 1f / Mathf.Max(0.1f, vol.noiseScaleMeters);
            var v = vol.noiseVelocityMeters;
            const double period = 1024.0;
            double ox = (v.x * now * rcpScale) % period;
            double oy = (v.y * now * rcpScale) % period;
            double oz = (v.z * now * rcpScale) % period;
            b.noise[slot] = new Vector4((float)ox, (float)oy, (float)oz, rcpScale);

            b.pars[slot] = new Vector4(Mathf.Clamp01(vol.noiseAmount), 0f, 0f, 0f);

            // 判据的两个尺子点：体中心（权重构造性为 1）与体外一点
            //（局部 +x 方向、表面再向外 blend + 1 m ⇒ 权重构造性为 0）。
            // CPU 直接给世界坐标 —— GPU 从 worldToLocal 反推就是第二份求逆实现。
            Vector3 c = t.position;
            Vector3 o = c + (t.rotation * Vector3.right) * (he.x + blendM + 1f);
            b.probeC[slot] = new Vector4(c.x, c.y, c.z, 0f);
            b.probeO[slot] = new Vector4(o.x, o.y, o.z, 0f);
        }

        /// <summary>
        /// 下发。count = 0 时**仍然**写 count（零态必须显式），九个数组跳过 ——
        /// 全局数组残留着上一次的内容，但 shader 循环被 count 门死，不会读到。
        /// </summary>
        public void Bind<T>(in T dispatcher) where T : IVistaLutDispatcher
        {
            dispatcher.SetGlobalVector(VistaShaderIDs._VistaFogVolCount,
                new Vector4(count, 0f, 0f, 0f));
            if (m_Bundle == null || count == 0)
                return;

            dispatcher.SetGlobalVectorArray(VistaShaderIDs._VistaFogVolRow0,   m_Bundle.row0);
            dispatcher.SetGlobalVectorArray(VistaShaderIDs._VistaFogVolRow1,   m_Bundle.row1);
            dispatcher.SetGlobalVectorArray(VistaShaderIDs._VistaFogVolRow2,   m_Bundle.row2);
            dispatcher.SetGlobalVectorArray(VistaShaderIDs._VistaFogVolBlend,  m_Bundle.blend);
            dispatcher.SetGlobalVectorArray(VistaShaderIDs._VistaFogVolMedium, m_Bundle.medium);
            dispatcher.SetGlobalVectorArray(VistaShaderIDs._VistaFogVolNoise,  m_Bundle.noise);
            dispatcher.SetGlobalVectorArray(VistaShaderIDs._VistaFogVolParams, m_Bundle.pars);
            dispatcher.SetGlobalVectorArray(VistaShaderIDs._VistaFogVolProbeC, m_Bundle.probeC);
            dispatcher.SetGlobalVectorArray(VistaShaderIDs._VistaFogVolProbeO, m_Bundle.probeO);
        }
    }
}
