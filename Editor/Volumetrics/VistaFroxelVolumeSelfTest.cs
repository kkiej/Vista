using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// 近层体积雾 froxel 体的**分布与资源口径**判定（#19）。
    ///
    /// 这一步交付的东西里没有任何「雾」—— 逐 froxel 的光照注入是 #20、积分是 #21。
    /// 所以这里能判的、也是必须判的，是三类**在后面每一步都会被默认成立**的事：
    ///   切片 ↔ 距离的映射约定、3D 资源的分配口径、远边界被夹紧时会不会说话。
    ///
    /// ---- 为什么不判「切片中心距离对不对」 ----
    /// 那会是同一条闭式解的第二份实现（C# 侧重算一遍 near·r^((i+0.5)/N) 再和 GPU 比）。
    /// 「同一个量的第二份实现连 8 行的辅助函数也算」—— 两份写错成同一个样子的概率不低，
    /// 而且它只能抓打包/绑定错误，抓不到公式本身选错。
    /// 所以判的是**定义性不变量**：
    ///   相邻存储距离的比处处等于 ρ（这就是「相对分辨率处处相同」的定义）；
    ///   存储距离编码回去正好落在纹素中心（这是「读端不需要半纹素偏移」的全部依据）；
    ///   求值点是两端的几何均值（这是「一个分段用一个介质样本」的取样点定义）。
    /// 这三条里任何一条被换成另一套约定，读数都会离开门，而闭式解重算不会。
    ///
    /// ---- fp16 与 fp32 分开读 ----
    /// 判据②走**纹理往返**：占位核把切片几何写进注入表，判据核再把它当 SRV 读回来。
    /// 它的地板是 fp16 的相对 ulp 2⁻¹¹ ≈ 4.9e-4，所以门只能开到 1e-3。
    /// 判据①③④⑤走**解析**（核内 fp32），地板 ~1e-7，门开在 1e-6。
    /// 混在一起的后果是所有门都被抬到 1e-3，而纹素中心那条恒等式（要求 ~1e-6）
    /// 就会在任何偏移量下都照样通过 —— 那时判据在报表上全绿，约定却是错的。
    /// </summary>
    public static class VistaFroxelVolumeSelfTest
    {
        // ==================================================================== 门
        //
        // 每一道门都写清楚它的地板，以及它「不紧」的地方在哪里。

        /// <summary>
        /// 解析侧（fp32）的门。fp32 的相对 ulp 是 1.19e-7，exp/log 各贡献几个 ulp，
        /// 所以地板在 ~1e-6 量级，这道门大约是地板的 8 倍 —— **不紧**。
        /// 它抓的是 O(1) 级别的错（近远端写反、漏乘 rcpLog、差一片），不是精度。
        /// 想抓精度得换成双精度参考解，而那件事在这里没有价值：
        /// 这套映射的下游消费者是 fp16 的 3D 纹理。
        /// </summary>
        const float k_AnalyticGate = 1e-6f;

        /// <summary>
        /// 纹理往返的门。fp16 半 ulp = 2⁻¹¹ ≈ 4.88e-4，取 2 倍留一点余量。
        /// 它足以抓到「差一片」：相邻片相差一个 ρ，最小的档（N=128、r=100）ρ = 1.0366，
        /// 也就是 3.7%，是这道门的 37 倍。
        /// </summary>
        const float k_TextureGate = 1e-3f;

        /// <summary>fp16 半 ulp，报表里作为地板对照打出来。</summary>
        const float k_Fp16HalfUlp = 4.8828125e-4f;

        /// <summary>
        /// 求值点恒等式的门。它比 <see cref="k_AnalyticGate"/> 松一个量级，理由是
        /// 那条残差要过三次 exp() 加三次乘除，地板在 ~1e-6（实测 round-trip 那条
        /// 单纯两次 exp 的最坏读数是 6.4e-7）。
        ///
        /// 上界从**它必须拒绝的最小错答案**推：把求值点写成度量中点而不是几何均值，
        /// 在比值形式里的偏差是 2·|(√ρ−1)/(ρ−1) − 0.5|·(ρ−1)，D 档（ρ = 1.0366，
        /// 五档里最小）是 3.3e-4 —— 这是五档里最小的一个。门放在 1e-5：
        /// 地板之上 10 倍，最小错答案之下 33 倍。那个倍数每档都打在报表上，
        /// 所以「门紧不紧」是一个能读的数，不是一句话。
        ///
        /// 第一版把这条判据写成减法形式并沿用 1e-6 的解析门，五个档全红 ——
        /// 那是判据自己的抵消误差，不是被测代码的缺陷。归因过程写在
        /// VolumetricFog.compute 的 ③ 处。
        /// </summary>
        const float k_SamplePosGate = 1e-5f;

        // ==================================================================== 档位
        //
        // 五档，每一档都有一件只有它能覆盖的事。

        struct Tier
        {
            public string name;
            public int screenW, screenH, divisor, slices;
            public float nearPlane, farMeters, shadowDistance;
            public string covers;
        }

        static readonly Tier[] k_Tiers =
        {
            new Tier
            {
                name = "A 生产档", screenW = 1920, screenH = 1080, divisor = 8, slices = 64,
                nearPlane = 0.3f, farMeters = 64f, shadowDistance = 500f,
                covers = "HDRP Medium 同口径（屏幕/8、64 片、64 m）",
            },
            new Tier
            {
                name = "B 移动候选", screenW = 1920, screenH = 1080, divisor = 8, slices = 32,
                nearPlane = 0.3f, farMeters = 64f, shadowDistance = 500f,
                covers = "切片减半 —— 几何均值偏离度量中点的量会翻倍，是 #21 的输入",
            },
            new Tier
            {
                name = "C 宽范围", screenW = 2560, screenH = 1440, divisor = 8, slices = 64,
                nearPlane = 0.1f, farMeters = 200f, shadowDistance = 500f,
                covers = "r = 2000（A 档是 213）+ 另一个屏幕尺寸，压 fp16 的动态范围",
            },
            new Tier
            {
                name = "D 密切片", screenW = 1280, screenH = 720, divisor = 4, slices = 128,
                nearPlane = 0.3f, farMeters = 30f, shadowDistance = 500f,
                // 与 C 档同 XY（320×180）但深度不同：顺带验证分配脏检查认得出深度变化。
                // 若 Equals 漏了 depth，这一档会沿用 C 档那张 64 层的表，
                // 症状是后 64 片读回来全是 0 —— 判据②直接抓到。
                covers = "ρ 最接近 1（1.0366），且与 C 档同 XY、不同深度 → 顺带验证脏检查认深度",
            },
            new Tier
            {
                name = "E 夹紧档", screenW = 1920, screenH = 1080, divisor = 8, slices = 64,
                nearPlane = 0.3f, farMeters = 500f, shadowDistance = 200f,
                covers = "远边界被阴影距离夹住，且夹紧之后整条 GPU 路径仍然要正常工作",
            },
        };

        [MenuItem("Window/Vista/Validate Froxel Volume (Slices)", priority = 141)]
        static void RunFromMenu()
        {
            var sb = new StringBuilder();
            bool ok = Run(sb);

            string oneLine = sb.ToString().TrimEnd().Replace("\r", "").Replace("\n", "  |  ");
            if (ok) Debug.Log("[Vista] froxel 体自检通过（分布 + 分配 + 夹紧）  |  " + oneLine);
            else Debug.LogWarning("[Vista] froxel 体自检失败（分布 + 分配 + 夹紧）  |  " + oneLine);
        }

        static bool Run(StringBuilder sb)
        {
            var res = VistaRuntimeResources.Get();
            if (res == null)
            {
                sb.AppendLine("✘ 取不到 VistaRuntimeResources（当前不是 URP？）。");
                return false;
            }

            // ResourcePath 的自动填充是**静默**的：填充失败时字段就是 null，
            // 没有任何日志。这一条把它变成一次显式失败。
            if (res.volumetricFogCS == null)
            {
                sb.AppendLine("✘ VistaRuntimeResources.volumetricFogCS 为 null —— "
                            + "Shaders/Volumetrics/VolumetricFog.compute 没被 ResourcePath 填上（路径写错？未导入？）。");
                return false;
            }
            if (res.atmosphereLutCS == null)
            {
                sb.AppendLine("✘ atmosphereLutCS 为 null（VistaAtmosphereLuts 构造需要它）。");
                return false;
            }

            var luts = new VistaAtmosphereLuts(res.atmosphereLutCS, null, res.volumetricFogCS);
            try
            {
                var vol = luts.froxelVolume;
                if (vol == null)
                {
                    sb.AppendLine("✘ luts.froxelVolume 为 null —— 构造里那句「无条件 new」没生效。");
                    return false;
                }
                if (!vol.isValid)
                {
                    sb.AppendLine("✘ froxelVolume.isValid = false：FroxelPlaceholder / FroxelSliceVerify "
                                + "两个核没全找到（compute 编译失败？平台被 only_renderers 排除？）。");
                    return false;
                }

                sb.Append("　 GPU ").Append(SystemInfo.graphicsDeviceName)
                  .Append("　fp16 半 ulp = ").Append(Sci(k_Fp16HalfUlp))
                  .Append("　解析门 ").Append(Sci(k_AnalyticGate))
                  .Append("　纹理门 ").Append(Sci(k_TextureGate)).AppendLine();

                // 夹紧规则先判：它是纯 CPU 的，不需要 GPU，而后面 E 档的口径依赖它成立。
                bool okClamp = RunClampTable(sb);
                bool okTiers = RunTiers(vol, luts, sb);
                return okClamp && okTiers;
            }
            finally
            {
                luts.Dispose();
            }
        }

        // ==================================================================== 判据①
        //
        // 远边界的夹紧规则。纯 CPU，直接调 static 纯函数 —— 不需要跑一帧渲染。
        //
        // 这一格的价值在两条互为镜像的失效上：
        //   静默夹紧 —— 美术把范围调到 500 m，画面没变，日志里查不到任何线索；
        //   过度夹紧 —— 阴影全关时把体积压成 0，把「没有光柱」升级成「连雾都没有」。
        // 第二条是「让失能态成为零态」这条正向做法的**反例**：这里的零不是安全的零。
        static bool RunClampTable(StringBuilder sb)
        {
            sb.AppendLine("── 判据①｜远边界夹紧（纯 CPU，ResolveFarDistance）");

            // requested, maxShadowDistance, 期望值, 期望是否有诊断串, 这一行在覆盖什么
            var rows = new (float req, float shadow, float expect, bool expectDiag, string why)[]
            {
                (64f,  500f, 64f,  false, "常规：远边界远小于阴影距离 → 不夹、不报"),
                (500f, 200f, 200f, true,  "要夹：500 > 200 → 夹到 200 且**必须**给出诊断串"),
                (64f,  64f,  64f,  false, "边界：正好相等 → 不算超出，不夹（<= 而不是 <）"),
                (64f,  0f,   64f,  false, "阴影全关（URP 把 maxShadowDistance 置 0）→ 不夹"),
                (500f, 0f,   500f, false, "阴影全关 + 本来会被夹 → 仍然不夹，这才是豁免的真正测点"),
                (-1f,  500f, VistaVolumetricFogSettings.k_MinFarDistanceMeters, false,
                       "下界：负数被抬到 k_Min（程序化赋值绕过 [Min] 特性时的唯一防线）"),
            };

            bool ok = true;
            foreach (var r in rows)
            {
                float got = VistaVolumetricFogSettings.ResolveFarDistance(r.req, r.shadow, out string diag);
                bool valueOk = Mathf.Abs(got - r.expect) <= 1e-4f;
                bool diagOk = (diag != null) == r.expectDiag;
                bool rowOk = valueOk && diagOk;
                ok &= rowOk;

                sb.Append("　 ").Append(Mark(rowOk))
                  .Append("req ").Append(r.req.ToString("F1").PadLeft(6))
                  .Append("　shadow ").Append(r.shadow.ToString("F1").PadLeft(6))
                  .Append("　→ ").Append(got.ToString("F1").PadLeft(6))
                  .Append("（期望 ").Append(r.expect.ToString("F1")).Append("）")
                  .Append("　诊断 ").Append(diag != null ? "有" : "无")
                  .Append("（期望 ").Append(r.expectDiag ? "有" : "无").Append("）　")
                  .AppendLine(r.why);
            }
            return ok;
        }

        // ==================================================================== 判据②～⑤
        static bool RunTiers(VistaFroxelVolume vol, VistaAtmosphereLuts luts, StringBuilder sb)
        {
            bool all = true;
            var settings = new VistaVolumetricFogSettings();

            foreach (var tier in k_Tiers)
            {
                settings.screenDivisor = tier.divisor;
                settings.sliceCount = tier.slices;
                settings.farDistanceMeters = tier.farMeters;

                var desc = settings.Resolve(tier.screenW, tier.screenH, tier.nearPlane,
                                            tier.shadowDistance, out string clampDiag);

                sb.Append("── ").Append(tier.name).Append("　").AppendLine(tier.covers);
                sb.Append("　 ").Append(desc.ToString());
                if (clampDiag != null) sb.Append("　[已夹紧]");
                sb.AppendLine();

                var cmd = new CommandBuffer { name = "Vista Froxel SelfTest" };
                bool prepared = vol.Prepare(desc, cmd);
                if (!prepared)
                {
                    cmd.Release();
                    sb.AppendLine("　 ✘ Prepare 返回 false（分配失败）。");
                    all = false;
                    continue;
                }
                vol.EnsureSliceReportBuffer(desc.depth);
                vol.DispatchPlaceholder(new VistaImmediateLutDispatcher(cmd, luts), desc);
                vol.DispatchSliceVerify(new VistaImmediateLutDispatcher(cmd, luts), desc);
                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Release();

                var report = new Vector4[desc.depth * VistaFroxelVolume.k_ReportFloat4PerSlice];
                vol.sliceReportBuffer.GetData(report);

                bool okAlloc = CheckAllocation(vol, desc, sb);
                bool okTex = CheckTextureRoundTrip(report, desc, sb);
                bool okMap = CheckMappingIdentities(report, desc, sb);
                bool okHandoff = CheckHandoff(report, desc, sb);
                all &= okAlloc && okTex && okMap && okHandoff;
            }

            return all;
        }

        // ---------------------------------------------------------------- 判据②
        //
        // 分配口径。RTHandles 的 2D 便捷重载会**静默**建成 Tex2D，症状是 RWTexture3D
        // 绑定失败（Editor.log 一行 warning）、画面上整张表全零 —— 与「雾参数填成 0」
        // 完全无法区分。所以维度/尺寸/格式三项都点名判。
        static bool CheckAllocation(VistaFroxelVolume vol, in VistaFroxelVolumeDesc desc, StringBuilder sb)
        {
            bool ok = true;
            ok &= CheckOneRt(vol.injection, "注入(当前)", desc, sb);
            ok &= CheckOneRt(vol.injectionHistory, "注入(历史)", desc, sb);
            ok &= CheckOneRt(vol.integral, "积分", desc, sb);

            // 三张必须是三个不同的对象。写成同一张的话 #22 的重投影会拿当前帧当历史帧，
            // 症状是「重投影完全没有收敛效果」，而那与「历史权重填错」长得一样。
            bool distinct = !ReferenceEquals(vol.injection, vol.injectionHistory)
                         && !ReferenceEquals(vol.injection, vol.integral)
                         && !ReferenceEquals(vol.injectionHistory, vol.integral);
            ok &= distinct;
            sb.Append("　 ").Append(Mark(distinct)).AppendLine("三张表是三个不同的 RTHandle");

            // 历史表的**内容**路径到 #22 之前没有任何东西写它 —— 这一格是空的，
            // 必须在报表上点名：它今天的证据力在「本该是空的」这件事上，不在读数上。
            sb.AppendLine("　 ⓘ 历史表只判分配口径；写入路径在 #22（时间重投影）之前**未被覆盖**。"
                        + "积分表同理，写入在 #21。");
            return ok;
        }

        static bool CheckOneRt(RTHandle h, string label, in VistaFroxelVolumeDesc desc, StringBuilder sb)
        {
            if (h == null || h.rt == null)
            {
                sb.Append("　 ✘ ").Append(label).AppendLine(" 为 null");
                return false;
            }

            var rt = h.rt;
            bool dim = rt.dimension == TextureDimension.Tex3D;
            bool size = rt.width == desc.width && rt.height == desc.height && rt.volumeDepth == desc.depth;
            bool fmt = rt.graphicsFormat == GraphicsFormat.R16G16B16A16_SFloat;
            bool uav = rt.enableRandomWrite;
            bool ok = dim && size && fmt && uav;

            sb.Append("　 ").Append(Mark(ok)).Append(label)
              .Append("　").Append(rt.dimension)
              .Append("　").Append(rt.width).Append("×").Append(rt.height).Append("×").Append(rt.volumeDepth)
              .Append("　").Append(rt.graphicsFormat)
              .Append("　UAV ").Append(rt.enableRandomWrite);
            if (!ok)
            {
                sb.Append("　←");
                if (!dim) sb.Append(" 维度不是 Tex3D（走了 2D 便捷重载？）");
                if (!size) sb.Append(" 尺寸与 desc 不符（脏检查漏了某个维度？）");
                if (!fmt) sb.Append(" 格式不是 RGBA16F");
                if (!uav) sb.Append(" 没开 enableRandomWrite");
            }
            sb.AppendLine();
            return ok;
        }

        // ---------------------------------------------------------------- 判据③
        //
        // 纹理往返：占位核写进注入表的四个距离，判据核当 SRV 读回来，与 CPU 闭式解比。
        //
        // 这一条**确实**是同一条闭式解的两份实现（GPU 的 VistaFroxelStoredDistance 与
        // C# 的 desc.StoredDistance）。留着它不是为了判公式对不对（那是判据④的事），
        // 而是为了判**这条数据通路通不通**：Tex3D 分配、UAV 绑定、dispatch 维度、
        // SRV 读回、CPU/GPU 两侧的常量是同一份。所以门开在 fp16 的 2 倍 ulp 上就够了。
        static bool CheckTextureRoundTrip(Vector4[] report, in VistaFroxelVolumeDesc desc, StringBuilder sb)
        {
            float worstRel = 0f;
            int worstSlice = -1;
            string worstField = "";
            bool zeroSeen = false;
            bool ok = true;

            for (int i = 0; i < desc.depth; ++i)
            {
                var got = report[i * VistaFroxelVolume.k_ReportFloat4PerSlice + 0];

                // 全零 = 这一片根本没被写过（dispatch 维度算少了 / 绑定失败）。
                // 单独拎出来说，因为它与「差一片」的修法完全不同。
                if (got == Vector4.zero) { zeroSeen = true; ok = false; continue; }

                Rel(ref worstRel, ref worstSlice, ref worstField, got.x, desc.StoredDistance(i), i, "stored");
                Rel(ref worstRel, ref worstSlice, ref worstField, got.y, desc.SampleDistance(i), i, "sample");
                Rel(ref worstRel, ref worstSlice, ref worstField, got.w, desc.SegmentFar(i), i, "segFar");

                if (i == 0)
                {
                    // 分段 0 的近端是**精确的 0**（从相机起，不是从近裁剪面起）。
                    // 相对误差在这里没有定义，所以判绝对相等 —— 而这一格恰好是
                    // 「相机与近裁剪面之间那一小段雾有没有被跳掉」的唯一测点。
                    if (got.z != 0f)
                    {
                        ok = false;
                        sb.Append("　 ✘ 分段 0 的近端读回 ").Append(got.z.ToString("F6"))
                          .AppendLine("，期望精确 0（从相机起算）。若它等于 near，说明近裁剪面前那段雾被跳掉了。");
                    }
                }
                else
                {
                    Rel(ref worstRel, ref worstSlice, ref worstField, got.z, desc.SegmentNear(i), i, "segNear");
                }
            }

            ok &= worstRel <= k_TextureGate;
            sb.Append("　 ").Append(Mark(ok))
              .Append("纹理往返：最坏相对误差 ").Append(Sci(worstRel))
              .Append("（门 ").Append(Sci(k_TextureGate))
              .Append("，fp16 半 ulp ").Append(Sci(k_Fp16HalfUlp)).Append("）");
            if (worstSlice >= 0)
                sb.Append("　@ 片 ").Append(worstSlice).Append(" 的 ").Append(worstField);
            if (zeroSeen)
                sb.Append("　← **有整片读回全零**：那一片没被写过（dispatch 的 XY/Z 维度算少了，"
                        + "或者 UAV 绑定失败 → 检查是不是走了 RTHandles 的 2D 重载）");
            sb.AppendLine();
            return ok;
        }

        static void Rel(ref float worst, ref int worstSlice, ref string worstField,
                        float got, float expect, int slice, string field)
        {
            float d = Mathf.Abs(got - expect) / Mathf.Max(Mathf.Abs(expect), 1e-9f);
            if (d <= worst) return;
            worst = d;
            worstSlice = slice;
            worstField = field;
        }

        // ---------------------------------------------------------------- 判据④
        //
        // 三条**定义性不变量**。全部在核内 fp32 算出来，与纹理无关。
        //
        //   a) 编解码 round-trip：Decode(Encode(d)) / d − 1
        //   b) 纹素中心恒等式：Encode(Stored(i)) − (i+0.5)/N == 0
        //      这一条是「读端不需要半纹素偏移」的全部依据。HDRP 存分段远平面，
        //      于是 Encode 落在 (i+1)/N，读的时候必须回退半个纹素 —— 那个已知的
        //      half-slice bias 就是这么来的。换回 HDRP 的约定，这条读数会变成 0.5/N
        //      （A 档 7.8e-3），是门的 7800 倍。
        //   c) 求值点：sample² == segNear · segFar（几何均值的乘法形式）。
        //      片 0 走度量中点那一支，判 sample == 0.5·segFar。
        //      两条都是比值残差，没有相减 —— 减法形式的地板是 3e-7/(ρ−1)，
        //      会在 ρ 接近 1 的档上伪造失败。
        //   d) 相邻比：Stored(i)/Stored(i−1) 处处等于 ρ。这是「相对分辨率处处相同」的定义。
        //
        // 片 0 在 (d) 里是**空格**：它没有前一片。(c) 里它不是空格，但走的是另一条恒等式，
        // 所以单独一行报出来 —— 那一行恰好是「退化分支有没有被走到」的唯一测点。
        static bool CheckMappingIdentities(Vector4[] report, in VistaFroxelVolumeDesc desc, StringBuilder sb)
        {
            float rho = desc.sliceRatio;
            // 几何均值落在分段里的度量位置。ρ → 1 时它是 0/0，但这里 ρ − 1 最小是 0.0366
            // （D 档），双精度下没有取消灾难。
            // 它**不参与判据** —— 只作为 #21 的输入量打印出来，见下面那条 ⓘ。
            double sqrtRho = System.Math.Sqrt(rho);
            float fracExpect = (float)((sqrtRho - 1.0) / (rho - 1.0));

            float worstRt = 0f, worstTex = 0f, worstPos = 0f, worstRatio = 0f;
            int argRt = -1, argTex = -1, argPos = -1, argRatio = -1;
            float pos0 = float.NaN;

            for (int i = 0; i < desc.depth; ++i)
            {
                var r = report[i * VistaFroxelVolume.k_ReportFloat4PerSlice + 1];
                float rt = Mathf.Abs(r.x);
                float tex = Mathf.Abs(r.y);
                if (rt > worstRt) { worstRt = rt; argRt = i; }
                if (tex > worstTex) { worstTex = tex; argTex = i; }

                if (i == 0)
                {
                    pos0 = r.z;
                    continue;                      // ρ 那一格对片 0 是空的；求值点走另一条恒等式
                }

                float pd = Mathf.Abs(r.z);
                if (pd > worstPos) { worstPos = pd; argPos = i; }

                float rd = Mathf.Abs(r.w / rho - 1f);
                if (rd > worstRatio) { worstRatio = rd; argRatio = i; }
            }

            bool okRt = worstRt <= k_AnalyticGate;
            bool okTex = worstTex <= k_AnalyticGate;
            bool okPos = worstPos <= k_SamplePosGate;
            bool okRatio = worstRatio <= k_AnalyticGate;
            bool okPos0 = Mathf.Abs(pos0) <= k_SamplePosGate;

            // 度量中点这个错答案在本档会给出多大的残差 —— 打出来，
            // 好让「门 1e-5 到底紧不紧」在报表上是一个数而不是一句话。
            float wrongAnswer = 2f * Mathf.Abs(fracExpect - 0.5f) * (rho - 1f);

            sb.Append("　 ").Append(Mark(okRt)).Append("a) 编解码 round-trip　最坏 ")
              .Append(Sci(worstRt)).Append("　@ 片 ").Append(argRt).AppendLine();

            sb.Append("　 ").Append(Mark(okTex)).Append("b) 纹素中心恒等式　最坏 ")
              .Append(Sci(worstTex)).Append("　@ 片 ").Append(argTex)
              .Append("　（换成 HDRP 的「存分段远平面」约定，这个数会是 0.5/N = ")
              .Append(Sci(0.5f / desc.depth)).AppendLine("）");

            sb.Append("　 ").Append(Mark(okPos)).Append("c) 求值点 = 几何均值　")
              .Append("sample²/(segNear·segFar) − 1 的最坏 ").Append(Sci(worstPos))
              .Append("　@ 片 ").Append(argPos)
              .Append("　门 ").Append(Sci(k_SamplePosGate))
              .Append("　（写成度量中点会给出 ").Append(Sci(wrongAnswer))
              .Append("，是门的 ").Append((wrongAnswer / k_SamplePosGate).ToString("F0"))
              .AppendLine(" 倍）");

            sb.Append("　 ").Append(Mark(okRatio)).Append("d) 相邻比恒定　Stored(i)/Stored(i−1) 与 ρ = ")
              .Append(rho.ToString("F6")).Append(" 的最坏相对偏差 ").Append(Sci(worstRatio))
              .Append("　@ 片 ").Append(argRatio).AppendLine();

            sb.Append("　 ").Append(Mark(okPos0)).Append("e) 片 0 的退化支：sample/(0.5·segFar) − 1 = ")
              .Append(Sci(pos0)).AppendLine("　— 片 0 的几何均值退化成 0，走的是度量中点那一支，"
                    + "这一行是它唯一的测点（(d) 那一格对它是**空的**：没有前一片）");

            // ---- 不设门的读数：几何均值偏离度量中点多少 ----
            // #21 的积分要在一个分段里用一个介质样本，这个偏移量决定那一步的一阶偏置。
            // 它是**输入**，不是缺陷，所以带符号打印、不判 —— 一个把负数压成 0 的
            // 格式化会让「偏早」「偏晚」「压线」三种状态在报表上长得一样。
            // 纯 CPU、只由 ρ 推出来，不含任何被测对象的读数。
            float dev = fracExpect - 0.5f;
            sb.Append("　 ⓘ 几何均值 − 度量中点 = ").Append(dev.ToString("+0.000000;-0.000000"))
              .Append(" 个分段（ρ = ").Append(rho.ToString("F6"))
              .AppendLine("）。不设门 —— 这是 #21 积分一阶偏置的输入量。"
                        + "它随切片数下降而增大（N 减半 ⇒ 偏移约翻倍）。");

            return okRt && okTex && okPos && okRatio && okPos0;
        }

        // ---------------------------------------------------------------- 判据⑤
        //
        // 接手点。这是 #19 唯一一个会**改到别的模块**的数：AP LUT 的 nearDistanceKm
        // 必须等于它，不是 farMeters。
        //
        // 判两件事，且第一件用的是与 handoffMeters 的实现**不同**的表达式：
        //   handoff == far · r^(−0.5/N)      （实现里是 near · r^((N−0.5)/N)）
        //   handoff == 最后一片从 GPU 读回来的存储距离（fp16 门）
        // 第二条才是真正重要的：它保证「CPU 交给 AP 的那个数」与「GPU 最后一片实际
        // 覆盖到的距离」是同一个。两者一旦分叉，那段距离会被两层各算一次雾，
        // 症状是那个距离上一圈很淡的亮环。
        static bool CheckHandoff(Vector4[] report, in VistaFroxelVolumeDesc desc, StringBuilder sb)
        {
            float handoff = desc.handoffMeters;

            float viaFar = desc.farMeters * Mathf.Pow(desc.ratio, -0.5f / desc.depth);
            float relAlt = Mathf.Abs(handoff - viaFar) / Mathf.Max(handoff, 1e-9f);
            bool okAlt = relAlt <= k_AnalyticGate;

            float lastStored = report[(desc.depth - 1) * VistaFroxelVolume.k_ReportFloat4PerSlice + 0].x;
            float relGpu = Mathf.Abs(lastStored - handoff) / Mathf.Max(handoff, 1e-9f);
            bool okGpu = relGpu <= k_TextureGate;

            bool inside = handoff < desc.farMeters && handoff > desc.nearMeters;

            sb.Append("　 ").Append(Mark(okAlt && okGpu && inside))
              .Append("接手点 handoff = ").Append(handoff.ToString("F4")).Append(" m")
              .Append("（far ").Append(desc.farMeters.ToString("F1")).Append(" m，差 ")
              .Append((desc.farMeters - handoff).ToString("F4")).Append(" m = ")
              .Append((100f * (1f - handoff / desc.farMeters)).ToString("F2")).Append("%）")
              .Append("　与 far·r^(−0.5/N) 的相对差 ").Append(Sci(relAlt))
              .Append("　与 GPU 最后一片的相对差 ").Append(Sci(relGpu)).AppendLine();
            sb.Append("　 → AP 的 nearDistanceKm 必须是 ")
              .Append((handoff * 0.001f).ToString("F6"))
              .AppendLine(" km（#20 接线）。填成 far 会让这 "
                        + (desc.farMeters - handoff).ToString("F2")
                        + " m 被两层各算一次雾。");
            return okAlt && okGpu && inside;
        }

        static string Sci(float v) => v.ToString("0.000e+0");
        static string Mark(bool ok) => ok ? "✔ " : "✘ ";
    }
}
