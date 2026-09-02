using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// #18b 的验收：**天空像素**的雾。
    ///
    /// ── 为什么天空要单独一套判据 ──
    ///
    /// AP LUT 是「相机到不透明物」这一段的量，<c>AerialPerspectiveComposite.hlsl</c> 的
    /// <c>VISTA_AP_IS_SKY_DEPTH</c> 刻意排除天空像素。于是 #18 把雾并进 AP 之后，
    /// 浓雾场景里地平线**以上**仍是干净的蓝天。补法是在天空盒里套两项式
    ///     L' = L_sky·T_fog + albedo·J̄·(1 − T_fog)
    /// —— 与 UE5 的 ExponentialHeightFog 叠在 SkyAtmosphere 上是同一条路。
    ///
    /// ── 为什么是逐像素闭式解，而不是再存一张表 ──
    ///
    /// 三个位置都试算过：
    ///   ① 天空盒里逐像素闭式解（本方案）：~10 ALU，不多一张纹理，梯度精确。
    ///   ② AP LUT 里多存一组「只有雾」的 T/S，天空像素采它的最远片。
    ///      否决：AP 在屏幕空间只有 32×32，而雾沿天空射线的透射率是 exp(−k/dir.y)，
    ///      它对 dir.y 的导数在地平线处**发散** —— 台阶必然正好落在地平线上，
    ///      而那恰恰是唯一需要它准的地方。外加 +512 KB fp16，压在最需要省的那一档上。
    ///   ③ 把雾折进 Sky-View LUT。否决理由同上（192×108 一样兜不住那个梯度），
    ///      而且 FogMedium.hlsl 里已经写下了三条独立否决理由（尺度不匹配、
    ///      静态表的球面性、方位对称的 uv 打包）。
    ///
    /// ── 这套判据的结构 ──
    ///
    /// 闭式解公开了两个近似（第三个 —— 自遮蔽沿射线取常数 —— 已在 #18b 里换成了
    /// 精确的入散射加权闭式解），判据必须能分别证伪，所以拆成两条，各配**独立**的尺子：
    ///   判据4  T_fog 的闭式解 vs 按雾的 e 折定步长的高步数数值积分（尺子地板 1E−7）
    ///   判据5  整个两项式    vs 同一条射线上的高步数含雾 march（尺子地板逐方向打印）
    ///   判据6  雾关 ⇒ 两项式的输出与无雾 march 逐位相同（零态）
    ///   判据7  覆盖性：四条「默认不走 / 刻意偏离平板解」的分支各要被走到 ——
    ///          Chapman 上界、dir.y 地板（这两条被判据4 排除在外）、
    ///          自遮蔽开关、自遮蔽的掠射上限（这两条只出现在被测侧）。
    ///
    /// 判据5 的两个操作数刻意都取自**同一次 march**（refOff / refOn），而不是拿
    /// Sky-View LUT 当 L_sky：那张表自己有 fp16 + 双线性误差，混进来之后差值里
    /// 就有一份与被测近似无关的东西，而且两个操作数用的还是不同的尺子。
    /// 表自己的误差另有判据（#7 的 round-trip / 台阶签名），不在这里重复。
    /// </summary>
    public static partial class VistaAtmosphereSelfTest
    {
        // ───────────────────────── 门槛 ─────────────────────────

        /// <summary>
        /// 判据4 的门：|T_闭式 − T_数值| 的**绝对**差（T 本身就是 [0,1] 上的量，
        /// 绝对差即相对可见度，不需要再挑分母）。
        ///
        /// 这里不该有任何模型误差 —— 闭式解 σ_t·ρ(h_cam)·H/dir.y 是平板指数剖面的
        /// **精确**柱密度，数值参考积的是同一个 <c>VistaSampleFogAlongRay</c>。
        /// 剩下的只有两处数值噪声：
        ///   · 参考解 16384 项 fp32 朴素求和，τ 的相对误差最坏 n·eps/2 = 4.9E−4，
        ///     换成 T 的绝对差是 T·τ·4.9E−4，在 τ=1 处取极大 0.368·4.9E−4 = 1.8E−4；
        ///   · 截断在 25 个 e 折，尾巴 e^−25 = 1.4E−11，可忽略。
        /// 门取 1E−3 ≈ 最坏界的 5.5 倍。韦伯 1% 比它还松一个数量级，
        /// 所以这条门是「实现对不对」，不是「看不看得出来」。
        /// </summary>
        const float k_SkyFogTauMax = 1e-3f;

        /// <summary>
        /// 判据5 的门：|两项式 − 含雾 march| / max(含雾 march) 的相对差。
        ///
        /// 门的量级是**推出来的**，不是试出来的。剩下的最大近似是「把整个 L_sky 都乘上
        /// T_fog」—— 它多衰减了那部分**在雾层内部产生**的大气内散射。误差 ≈
        /// 「大气内散射有多少份额是在雾的沿线跨度内产生的」×(1 − T_fog)：
        ///   雾的沿线跨度 H/dy，大气内散射的 H_Rayleigh/dy ⇒ 份额 = H/H_R
        ///                                                = 50 m / 8 km ≈ 0.6%。
        /// 另两项（T_atmSun 与 earthShadow 沿射线取常数；雾的内散射不被大气透射率
        /// 衰减，后者在 1 km 内 > 0.97）都更小。自遮蔽那一项**不在这个清单里** ——
        /// 它已经换成了精确的入散射加权闭式解（VistaFogSunTransmittanceMean）。
        /// 门取 3%：比 0.6% 的主项留了 5 倍余量，因为「H/H_R」本身是个量级估计。
        ///
        /// 掠射区（Chapman 上界生效）**不适用这个推导**：那里两个跨度都被曲率封顶成
        /// sqrt(2πRH)，份额变成 sqrt(H/H_R) = 7.9%，是比值的平方根。那个区间由布景
        /// 自己的 <see cref="SkyFogScene.ceiling"/> 兜，见那个字段的注释。
        ///
        /// 另一个不适用的区间是**均匀雾**（H → ∞）：那里跨度由 1/σ_t 而不是 H/dy 定，
        /// 见 <see cref="k_SkyFogUniformBaseline"/>。两个例外合起来说明这条门的适用域是
        /// 「有限标高 + 非掠射」—— 也正是会实际出货的那一块。
        /// </summary>
        const float k_SkyFogModelMax = 0.03f;

        /// <summary>
        /// 判据5 在**均匀雾**（标高 → ∞）上的实测基线。
        ///
        /// <see cref="k_SkyFogModelMax"/> 那条推导有一个没写出来的前提：雾的**沿线跨度**
        /// 是 H/dy。那只在「密度衰减比消光更早终止积分」时成立，即 H/dy &lt; 1/σ_t。
        /// 均匀雾把 H 推到无穷，跨度就完全由消光定 —— 1/σ_t。于是被过度衰减的份额
        ///     min(H, 1/σ_t) / H_Rayleigh
        /// 在 MFP = 1000 m（σ_t = 1/km）这一档变成 1 km / (8 km/sinθ)：天顶 12.5%、
        /// 仰角 55° 约 10%，比有限标高档的 0.6% 大二十倍 —— 3% 的门套上去必然假失败。
        /// 实测最差 **5.65%**（布景②、仰角 55.5°）。
        ///
        /// 这个模型的**结构**在这里得到了一次独立印证：它预测均匀雾的最差方向在
        /// 天顶附近（那里 Rayleigh 的沿线跨度最短），而**掠射反而更好**（曲率把
        /// Rayleigh 跨度封在 316 km，份额掉到 0.3%）—— 与有限标高档的最差方向
        /// 恰好相反。实测的 55.5° 落在预测的一侧，差 O(1) 的分布因子。
        ///
        /// 取 8%（实测 × 1.4）而不是把 12.5% 写成门：那条份额公式在另一头偏了十倍
        /// （B 档掠射预测 0.13%、实测 1.30%），它是量级估计而不是紧的上界，而
        /// 「一个不紧的解析上界写成门，等于没有门」。所以这一档也印「未判达标」，
        /// 与布景③ 同样的处理。
        ///
        /// 均匀雾对高度雾本来就是**退化的授权配置**（FogMedium.hlsl 已经把它在自遮蔽
        /// 那一节标成「授权错误」）。它留在这套判据里只为覆盖两件事：
        /// VistaFogScaleHeightKm() 的 1E−6 地板，以及两项式在 T → 0 处的饱和分支。
        /// 真正要判画质的是有限标高那几档。
        ///
        /// 记一笔它的来历：这一档在 #18b 第一版的 8% 平门下读 5.65%、**静默通过**。
        /// 把门收紧到推导支持的 3% 之后它才暴露 —— 门收紧找出了一个此前没被覆盖的
        /// 配置，而不是制造了一个新问题。
        /// </summary>
        const float k_SkyFogUniformBaseline = 0.08f;

        /// <summary>
        /// 判据5 那把尺子的地板上限。超过它的方向被**排除并计数**，不参与判定。
        ///
        /// refOn/refOff 走 <c>VistaIntegrateScatteredLuminance</c>，它没有
        /// <c>VistaFogStepMaxKm</c>（那是 AP 专用的），步长是 tMax 均分。
        /// 所以它对雾的中点法误差是 x²/24，x = dt / 雾沿射线的 e 折。
        /// 核里逐方向输出 x（见 <c>_VistaSkyFogRW[b+5].y</c>），这里换算成 x²/24。
        ///
        /// 注意这个数字是 τ 的相对误差的**上界**，传到被比较的量上只会更小
        /// （τ 小时误差按 τ 缩，τ 大时结果已经饱和成纯雾色、对 τ 不敏感）。
        /// 所以「按它排除」是保守的：宁可少判几个方向，也不要让
        /// 「尺子的地板与被测量同量级」这件事无声地通过。
        /// 门取 1%：比判据5 的 3% 低 3 倍。
        /// 它触发的次数在报表末尾点名 —— 若一轮都是 0，这段排除逻辑本轮没被执行过。
        /// </summary>
        const float k_SkyFogRulerMax = 0.01f;

        /// <summary>
        /// 判据4 每个布景至少要判到的方向数（共 64 个）。
        ///
        /// 判据4 要排除三类方向（dir.y 被地板兜住、Chapman 上界生效、无限标高），
        /// 排除逻辑一旦写宽，症状**不是失败**，而是「判据在一条更窄的路径上照样全绿」。
        /// 实际上每个方位只有最低 1~2 级仰角会被排除，所以 64 个里应当剩 ≥ 60 个；
        /// 门取 48（留 25% 余量）足以抓住「排除条件写反了」这种错。
        /// </summary>
        const int k_SkyFogMinJudged = 48;

        // ───────────────────────── 布景 ─────────────────────────

        /// <summary>
        /// 方向由核自己按几何阶梯造（0.05°→90°，两个方位），所以布景里**不含**朝向 ——
        /// 相机俯仰角对这条路径完全无关。真正会变的只有两件事：
        ///   · 相机在雾层里的高度 ⇒ 决定闭式解那个 ρ(h_cam) 因子；
        ///   · 太阳仰角 ⇒ 决定 HG 相位跨的区间与自遮蔽那条掠射钳位。
        /// </summary>
        struct SkyFogScene
        {
            public string name;
            public Vector3 cameraPos;
            public float sunElevDeg;

            /// <summary>
            /// 判据5 的**实测基线**，只在推导给不出紧的上限时才设。0 = 用
            /// <see cref="k_SkyFogModelMax"/> 那道真正的质量门。
            ///
            /// 设了它的布景在报表上印成「未判达标」而不是「达标」—— 这一条是刻意的：
            /// 一个把「未判达标」印成「达标」的判据，比一个平门更危险。
            /// </summary>
            public float ceiling;
        }

        static readonly SkyFogScene[] k_SkyFogScenes =
        {
            // ① 雾里面：ρ(2 m) ≈ 1（H=50 时 0.96，H=20 时 0.90），闭式解的满密度端。
            new SkyFogScene { name = "① 雾内 cam2m 太阳60°",  cameraPos = new Vector3(0f,   2f, 0f), sunElevDeg = 60f },
            // ② 低太阳：1/sin5° = 11.5 > grazingAmplifyMax 8，覆盖自遮蔽的钳位；
            //   同时 HG 在近地平方向上跨到相位的另一端。
            new SkyFogScene { name = "② 雾内 cam2m 太阳5°",   cameraPos = new Vector3(0f,   2f, 0f), sunElevDeg =  5f },
            // ③ 雾层上方：ρ(300 m) = e^−6 = 2.5E−3（H=50）。闭式解的低密度端，
            //   同时验证 ρ(h_cam) 这个因子真的进了公式 —— 若漏了它，这一档会亮得离谱。
            //
            //   这一档带 ceiling，因为它是两项式的**结构性上限**所在的区间，
            //   不是可以调参调掉的东西：相机在雾层上方看掠射方向时，雾的沿线跨度与
            //   大气内散射的沿线跨度**都**被曲率封顶成 sqrt(2πRH)，于是「被过度衰减的
            //   份额」从 H/H_R 变成 sqrt(H/H_R) = 7.9%，再乘 O(1) 的分布因子就到十几个
            //   百分点。实测最差 16.4%（浓雾档、仰角 0.06°）。
            //   基线取 20% = 实测 + 1.2 倍余量。要真正修掉它必须知道大气内散射沿线的
            //   分布，也就是 #18b 一开始否决掉的「再存一张仅雾的 AP 子表」——
            //   那张表在地平线上必然有台阶，而这里说的正是地平线附近。
            //   #27 的验收里改成量**画面上**的差（档 A 的近层体 vs 档 D），
            //   而不是继续往这个数上加余量。
            new SkyFogScene { name = "③ 雾上 cam300m 太阳60°", cameraPos = new Vector3(0f, 300f, 0f), sunElevDeg = 60f,
                              ceiling = 0.20f },
        };

        // ───────────────────────── 入口 ─────────────────────────

        /// <summary>
        /// 判据4~7。挂在 <c>Validate Fog (AP + Sky)</c> 里跟 AP 的雾判据同一次跑：
        /// 两者共用同一组雾配置与同一批预热好的静态表，而且「地平线上下必须接得上」
        /// 正是这两条路径唯一的耦合点 —— 分成两个菜单项就没法保证它们用的是同一组参数。
        /// </summary>
        static bool ValidateSkyFog(
            VistaAtmosphereLuts luts, VistaAtmosphereParameters p, FogCase[] cases, StringBuilder sb)
        {
            if (!luts.EnsureSkyFogError())
            {
                sb.AppendLine(Mark(false) + " 判据4~7 天空雾：EnsureSkyFogError 失败"
                            + "（SkyFogError kernel 没找到，或 compute 无效）");
                return false;
            }

            int n      = VistaAtmosphereLuts.k_SkyFogDirCount;
            int stride = VistaAtmosphereLuts.k_SkyFogStride;
            int elev   = VistaAtmosphereLuts.k_SkyFogElevCount;
            var data   = new Vector4[n * stride];

            bool ok = true;
            // 判据7 的四个见证计数器，跨布景累计 —— 单个配置里它们可能一次都不生效
            // （H=20 m 时 Chapman 上界 28.3 km 永远大于最长平板光程 20 km；
            //   自遮蔽只有档 G 开；掠射上限只有布景② 的太阳 5° 会顶到），
            // 所以覆盖性只能在整组配置上判，不能逐档判。
            int chapmanHits = 0, flooredHits = 0, sunShadowHits = 0, sunCapHits = 0;
            // 判据5 那道「尺子地板过高就排除该方向」的守卫，跨布景累计触发次数。
            // 它**不参与**通过与否 —— 记它是因为「本轮无法失败的守卫要在报告里点名」：
            // 若一整轮都是 0，那段排除逻辑本轮根本没执行过，报表必须说出来，
            // 而不是让人以为「排除条件写对了」。
            int rulerExclHits = 0;

            for (int si = 0; si < k_SkyFogScenes.Length; ++si)
            {
                var sc = k_SkyFogScenes[si];
                var view = MakeView(p, sc.cameraPos, sc.sunElevDeg);

                // 天光 SH：雾的环境项 J̄ 里那一份。不烘的话它恒为 0，
                // 「环境项那一行执行了但贡献恒零」在报表上和「写对了」长得一样。
                var cmd = new CommandBuffer { name = "Vista SkyFog sky (SelfTest)" };
                luts.RenderSkyViewLut(cmd, view);
                luts.RenderSkyAmbientSh(cmd, view);
                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Release();

                sb.AppendLine("　── 天空雾 " + sc.name);

                // 判据6 只在第一个布景跑一遍：它验的是「三个 cbuffer 全零 ⇒ 输出逐位不变」，
                // 与相机高度、太阳角度都无关。跑三遍只是多花两次 dispatch。
                if (si == 0)
                    ok &= MeasureSkyFogZeroState(luts, view, data, n, stride, sb);

                for (int i = 0; i < cases.Length; ++i)
                {
                    // signature 那四档（B/C/D/G）+ H：前者跨密度、标高、自遮蔽，
                    // 后者是无限标高的退化端（密度恒 1 ⇒ 柱密度真的是无穷 ⇒ T→0），
                    // 用来覆盖两项式在饱和处的行为。其余几档（等向/极前向/能见度标定）
                    // 测的是相位与单位换算，那两件事在 AP 那边已经判过，天空这边不重复。
                    bool selected = cases[i].signature || cases[i].label[0] == 'H';
                    if (!selected) continue;

                    ok &= MeasureSkyFogCase(luts, view, cases[i], sc.ceiling,
                                            data, n, stride, elev,
                                            ref chapmanHits, ref flooredHits,
                                            ref sunShadowHits, ref sunCapHits,
                                            ref rulerExclHits, sb);
                }
            }

            // ── 判据7：覆盖性 ──
            // 这四条分支都是「默认不走 / 刻意偏离平板精确解」的地方：前两条被判据4
            // **排除在外**，后两条只改判据5 的被测值、不改任何参考值。若布景里一次都没
            // 走到，它们就是「永远不会被发现写错」的状态 —— 与「一个默认关闭又没有判据
            // 覆盖的开关」同一个坑。VistaFogSunPathKm 的两样东西（`.w < 0.5` 开关、
            // _VistaFogExtinct.w 掠射上限）就是靠后两个计数器脱离那个状态的，
            // 所以 #27 里那条「跑不出就删掉这个函数」到这里作废。
            bool okCover = chapmanHits > 0 && flooredHits > 0
                        && sunShadowHits > 0 && sunCapHits > 0;
            ok &= okCover;
            sb.AppendLine(Mark(okCover) + " 判据7 覆盖　Chapman 上界 " + chapmanHits + " 次"
                        + Mark(chapmanHits > 0) + "　dir.y 地板 " + flooredHits + " 次"
                        + Mark(flooredHits > 0) + "　自遮蔽开 " + sunShadowHits + " 次"
                        + Mark(sunShadowHits > 0) + "　掠射上限 " + sunCapHits + " 次"
                        + Mark(sunCapHits > 0)
                        + "（四者都必须 > 0；判据4 把前两条排除、判据5 只在被测侧用后两条，"
                        + "所以它们只能靠这条见证）");

            // 点名本轮无法失败的守卫（不参与通过与否）。
            sb.AppendLine("　　守卫　判据5 尺子不足排除 " + rulerExclHits + " 次"
                        + (rulerExclHits > 0
                            ? "（本轮执行过）"
                            : "（**本轮一次都没触发** —— 那段排除逻辑本轮未被执行，"
                              + "不构成「它写对了」的证据。全部 " + (k_SkyFogScenes.Length)
                              + " 布景的尺子地板都在门以下，说明参考步数足够，不是判据放宽了）"));
            return ok;
        }

        // ───────────────────────── 判据6：零态 ─────────────────────────

        /// <summary>
        /// 雾关（三个 cbuffer 全零）时 <c>VistaApplyFogToSky</c> 的输出必须与无雾 march
        /// **逐位**相同，而且闭式解的 T 必须逐位为 1。
        ///
        /// 逐位是可以要求的，不是运气：σ_t = 0 ⇒ τ = 0 ⇒ exp(0) = 1 精确；
        /// albedo = saturate(0) = 0 ⇒ 加的那一项精确为 +0.0；L·1.0 与 L+0.0 在 fp32 下
        /// 都是恒等变换。所以任何非零读数都只能是「关态不是零态」——
        /// 那正是 <c>VistaFogSettings</c> 全零打包想排除的东西。
        /// </summary>
        static bool MeasureSkyFogZeroState(
            VistaAtmosphereLuts luts, in VistaAtmosphereViewData view,
            Vector4[] data, int n, int stride, StringBuilder sb)
        {
            var cmd = new CommandBuffer { name = "Vista SkyFog zero (SelfTest)" };
            luts.RenderSkyFogError(cmd, view, new VistaFogSettings());
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            luts.skyFogErrorBuffer.GetData(data);

            float maxDiff = 0f, maxTErr = 0f;
            for (int i = 0; i < n; ++i)
            {
                Vector4 tAna  = data[i * stride + 0];
                Vector4 refOf = data[i * stride + 2];
                Vector4 model = data[i * stride + 4];
                maxDiff = Mathf.Max(maxDiff, Mathf.Abs(model.x - refOf.x));
                maxDiff = Mathf.Max(maxDiff, Mathf.Abs(model.y - refOf.y));
                maxDiff = Mathf.Max(maxDiff, Mathf.Abs(model.z - refOf.z));
                maxTErr = Mathf.Max(maxTErr, Mathf.Abs(tAna.x - 1f));
                maxTErr = Mathf.Max(maxTErr, Mathf.Abs(tAna.y - 1f));
                maxTErr = Mathf.Max(maxTErr, Mathf.Abs(tAna.z - 1f));
            }

            bool ok = maxDiff == 0f && maxTErr == 0f;
            sb.AppendLine(Mark(ok) + " 判据6 零态　雾关 max|模型−无雾 march| = " + maxDiff.ToString("E3")
                        + "　max|T_闭式 − 1| = " + maxTErr.ToString("E3")
                        + "（两者都必须逐位为 0）");
            return ok;
        }

        // ───────────────────────── 判据4 + 判据5 ─────────────────────────

        static bool MeasureSkyFogCase(
            VistaAtmosphereLuts luts, in VistaAtmosphereViewData view, in FogCase c,
            float ceiling,
            Vector4[] data, int n, int stride, int elevCount,
            ref int chapmanHits, ref int flooredHits,
            ref int sunShadowHits, ref int sunCapHits,
            ref int rulerExclHits, StringBuilder sb)
        {
            var cmd = new CommandBuffer { name = "Vista SkyFog case (SelfTest)" };
            luts.RenderSkyFogError(cmd, view, c.fog);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            luts.skyFogErrorBuffer.GetData(data);

            // 无限标高必须整档退出判据4：真实柱密度是**无穷**，而
            // VistaFogScaleHeightKm() 的 1E−6 地板给它编了一个 1000 km 的 H，
            // 密度采样器根本不用那个 H。拿一个双方物理都不一致的量去比，
            // 产生的假失败比真失败更贵。判据5 照跑 —— 那一档两边都饱和到 T≈0，
            // 恰好是两项式在饱和处的覆盖。
            bool infiniteH = float.IsInfinity(c.fog.scaleHeightMeters);

            float worstT = 0f;       int worstTIdx = -1;
            float worstM = 0f;       int worstMIdx = -1;
            float worstRuler = 0f;
            // 这条视线上雾最多吃掉多少透射率。它不参与判定，只用来识别**空判据**的格子：
            // 相机远在雾层之上时 ρ(h_cam) 可以小到 e^−15，两侧都精确退化成"无雾"，
            // 于是 max|ΔT| = 0、相对差 ≈ 0，报表上和"写对了"长得一样。
            float worstFogLoss = 0f;
            int judged4 = 0, judged5 = 0;
            int excl4Floor = 0, excl4Chap = 0, excl5Ruler = 0;
            bool finite = true;

            for (int i = 0; i < n; ++i)
            {
                Vector4 tAna   = data[i * stride + 0];   // xyz T_闭式,   w pathAnaKm
                Vector4 tRef   = data[i * stride + 1];   // xyz T_数值,   w pathSlabKm
                Vector4 refOff = data[i * stride + 2];   // xyz L_无雾,   w pathFlooredKm
                Vector4 refOn  = data[i * stride + 3];   // xyz L_含雾,   w elevDeg
                Vector4 model  = data[i * stride + 4];   // xyz 两项式
                Vector4 rulers = data[i * stride + 5];   // x ①的 dt/L, y ②的 dt/L, z tTopKm
                Vector4 sunPth = data[i * stride + 6];   // x 太阳向光程 km, y 该光程的掠射上限

                finite &= IsFinite(tAna) && IsFinite(tRef) && IsFinite(refOff)
                       && IsFinite(refOn) && IsFinite(model) && IsFinite(rulers)
                       && IsFinite(sunPth);

                // ── 分类：只做比较，不在 C# 侧重算物理 ──
                // 三条路径长度都是核输出的：
                //   tRef.w   = pathSlabKm     （平板精确解 H/dir.y，没有任何钳位）
                //   refOff.w = pathFlooredKm  （dir.y 被 1E−3 兜住之后的光程）
                //   tAna.w   = pathAnaKm      （再套上 Chapman 掠射上界，即闭式解真正用的）
                // 于是：dir.y 地板生效 ⟺ pathFloored < pathSlab；
                //       Chapman  生效 ⟺ pathAna     < pathFloored。
                // dir.y > 1E−3 时两个 rcp 走的是同一个分母，相等是精确的，不需要容差。
                // 两个条件**独立**判、独立计数：最低那一级仰角往往两条同时生效，
                // 用 else-if 串起来会让其中一条永远数不到。
                bool dyFloored = refOff.w < tRef.w;
                bool chapman   = tAna.w   < refOff.w;
                if (dyFloored) ++flooredHits;
                if (chapman)   ++chapmanHits;

                // ── VistaFogSunPathKm 那两条分支的见证 ──
                // 参考值同样取自**被测函数自己**：核里 sunPth.y = VistaFogSunPathKm(0)，
                // 那一支必然顶到 min 的上界（rcp(max(0,1E−3)) = 1000 大于任何合理的
                // grazingAmplifyMax）。所以「上限值」是同一个函数体产出的，
                // 不需要在这里重写 H·grazingMax —— 少一份实现。
                //   自遮蔽开 ⟺ 光程 > 0（关态是零态，函数返回 0）
                //   掠射上限 ⟺ 光程 >= 那个上限值（相等即顶到，rcp 同分母、精确）
                if (sunPth.x > 0f)
                {
                    ++sunShadowHits;
                    if (sunPth.x >= sunPth.y) ++sunCapHits;
                }

                worstFogLoss = Mathf.Max(worstFogLoss,
                                         1f - Mathf.Min(Mathf.Min(tAna.x, tAna.y), tAna.z));

                // ── 判据4：T 的闭式解 vs 按雾 e 折定步长的数值积分 ──
                // 排除掉两条**刻意偏离平板解**的分支：拿平板数值解去判它们是拿
                // 两套不同物理做比较，产生的假失败比真失败更贵。它们的见证在判据7。
                if (infiniteH)
                {
                    // 整档排除，不逐方向计数（见上面 infiniteH 的注释）
                }
                else if (dyFloored || chapman)
                {
                    if (dyFloored) ++excl4Floor;
                    if (chapman)   ++excl4Chap;
                }
                else
                {
                    ++judged4;
                    float d = Mathf.Max(Mathf.Max(Mathf.Abs(tAna.x - tRef.x),
                                                  Mathf.Abs(tAna.y - tRef.y)),
                                        Mathf.Abs(tAna.z - tRef.z));
                    if (d > worstT) { worstT = d; worstTIdx = i; }
                }

                // ── 判据5：两项式 vs 含雾 march ──
                // 尺子地板 = (dt/L)²/24，L 是雾沿射线的 e 折。超门就排除并计数。
                float ruler = rulers.y * rulers.y / 24f;
                worstRuler = Mathf.Max(worstRuler, ruler);
                if (ruler > k_SkyFogRulerMax) { ++excl5Ruler; ++rulerExclHits; continue; }

                float scale = Mathf.Max(Mathf.Max(refOn.x, refOn.y), refOn.z);
                if (scale < 1f) continue;   // 分母趋零时相对差自己造结论；日照布景下不会触发
                ++judged5;
                float m = Mathf.Max(Mathf.Max(Mathf.Abs(model.x - refOn.x),
                                              Mathf.Abs(model.y - refOn.y)),
                                    Mathf.Abs(model.z - refOn.z)) / scale;
                if (m > worstM) { worstM = m; worstMIdx = i; }
            }

            // 判据5 的有效门。两条「推导给不出紧上限」的通道各自把门抬到自己的实测基线，
            // 两者可以同时命中（布景③ 的 H 档），取 max。
            //   · ceiling > 0   —— 布景在掠射/曲率区间（sqrt(H/H_R) 那一支）
            //   · infiniteH     —— 均匀雾，沿线跨度由 1/σ_t 而不是 H/dy 定
            // 走了任一条的格子只能证伪「回归」，不能证明「达标」，报表上必须印成两回事。
            bool  baseline = ceiling > 0f || infiniteH;
            float gate5    = k_SkyFogModelMax;
            if (ceiling > 0f) gate5 = Mathf.Max(gate5, ceiling);
            if (infiniteH)    gate5 = Mathf.Max(gate5, k_SkyFogUniformBaseline);

            // 空判据的识别门：雾在整条视线上最多吃掉 0.1% 的透射率，就是韦伯阈的十分之一 ——
            // 这格子里两项式与参考解都退化成"无雾"，读数为 0 不构成任何证据。
            bool empty = worstFogLoss < 1e-3f;

            bool okSane   = finite;
            bool okT      = judged4 >= (infiniteH ? 0 : k_SkyFogMinJudged) && worstT <= k_SkyFogTauMax;
            bool okModel  = judged5 >= k_SkyFogMinJudged && worstM <= gate5;
            bool ok       = okSane && okT && okModel;

            // 「达标」只能挂在**真的过了那道质量门**的格子上。
            // #18b 第一次跑时这里把 baseline 当成唯一分支，于是一个 5.65% ✘ 的格子
            // 后面照样印着「达标」—— 与本项目点过名的「把未判达标印成达标」同一个坑，
            // 只是方向相反，而且更糟：它出现在一个已经判失败的格子上。
            string verdict = empty    ? "（空判据）"
                           : baseline ? "（未判达标）"
                           : okModel  ? "（达标）"
                                      : "（**未达标**）";

            sb.AppendLine("　　" + Mark(ok) + " " + c.label
                        + "　判据4 max|ΔT| = " + (infiniteH ? "整档排除(无限标高)"
                                                            : worstT.ToString("E2") + Mark(okT)
                                                              + " @" + DirLabel(data, worstTIdx, stride, elevCount)
                                                              + "　判 " + judged4 + "/" + n
                                                              + "（排除 地板" + excl4Floor + " Chapman" + excl4Chap + "）")
                        + "　判据5 max相对差 = " + Pct(worstM) + Mark(okModel)
                        + " @" + DirLabel(data, worstMIdx, stride, elevCount)
                        + "　" + (baseline ? "实测基线 " : "质量门 ") + Pct(gate5)
                        + " 余量 " + SlackPct(gate5 - worstM) + verdict
                        + "　判 " + judged5 + "/" + n + "（尺子不足排除 " + excl5Ruler + "）"
                        + "　尺子地板最差 " + Pct(worstRuler) + "（门 " + Pct(k_SkyFogRulerMax) + "）"
                        + "　雾最多吃掉 T 的 " + Pct(worstFogLoss)
                        + (empty ? "　⚠ **这一格是空判据** —— 相机远在雾层之上，两侧都精确退化成无雾；"
                                 + "它的证据力在「本该是空的」这件事上（若闭式解漏了 ρ(h_cam) 这个因子，"
                                 + "这一格会亮得离谱），不在那两个读数上"
                                 : "")
                        + (okSane ? "" : "　**非有限值**"));
            return ok;
        }

        /// <summary>
        /// 报表里的方向标签。仰角从**核输出**里取（<c>[b+3].w</c>），不在这里重算几何阶梯：
        /// 那个公式重写一遍就是第二份实现，写错的症状是「哪个仰角超标」指错方向，
        /// 而所有判据照样绿。方位只是 index 与 elevCount 的比较，不涉及公式。
        /// </summary>
        static string DirLabel(Vector4[] data, int i, int stride, int elevCount)
        {
            if (i < 0) return "无判定方向";
            float elevDeg = data[i * stride + 3].w;
            return "仰角" + elevDeg.ToString("F3") + "° 方位" + (i < elevCount ? "0°" : "180°");
        }

        static bool IsFinite(Vector4 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z)
                && !float.IsNaN(v.w) && !float.IsInfinity(v.w);
        }
    }
}
