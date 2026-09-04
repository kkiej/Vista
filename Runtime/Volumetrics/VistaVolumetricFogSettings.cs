using System;
using UnityEngine;

namespace Vista
{
    /// <summary>
    /// froxel 表的调试视图档位。
    ///
    /// ------------------------------------------------------------------ 数值与 shader 的耦合
    /// 这里的整数值被原样下发给 <c>Shaders/Volumetrics/VistaFroxelDebug.shader</c>，
    /// 与那里的 <c>VISTA_DBG_*</c> 宏必须逐个对应。C# 与 HLSL 之间没有共享枚举的办法，
    /// 所以这是一处**真实的两份定义**，不能用「同一个量只写一份」抹掉。
    ///
    /// 为什么不给它配一条判据：改错了的症状是「选积分 RGB 却显示了注入 RGB」，
    /// 一个整屏、立刻可见、且不可能被误读成正确的画面。本项目要判据的是
    /// **静默**失效（关键字漏设、半个纹素偏移、单位差 1000 倍）；
    /// 一个大声喊出来的错，判据买不到任何东西。
    ///
    /// <see cref="Off"/> 必须是 0：它是失能态，而失能态在本项目里恒等于零态
    /// （那一趟 pass 根本不被记录，不是「记录了但画占位内容」）。
    /// </summary>
    public enum FroxelDebugView
    {
        /// <summary>不记录调试 pass。默认值，也是出货值。</summary>
        Off = 0,
        /// <summary>积分表的 rgb（累积内散射，预曝光辐亮度 × gain），按场景深度采样。</summary>
        IntegralRgb = 1,
        /// <summary>积分表的 alpha（1 − 累积透射率，已在 [0,1]），按场景深度采样。</summary>
        IntegralAlpha = 2,
        /// <summary>注入表的 rgb（σ_s·J × gain），按场景深度采样。</summary>
        InjectionRgb = 3,
        /// <summary>积分表某一片的 rgb 铺满屏幕，点采样。切片下标见 debugSlice。</summary>
        SingleSlice = 4,
    }

    /// <summary>
    /// froxel 采样点的抖动序列来源（#22）。
    ///
    /// <see cref="Off"/> 仍然一个字节都不下发：它的效果完全由「抖动幅度 0 +
    /// 历史权重 0」表达，也就是项目里那条「失能态 = 零态」。#22b 加了第二个**在线**
    /// 档位之后，档位选择走 <c>_VistaFroxelJitter.z</c>（一个 uniform 分支），
    /// 而**不是**编译期宏 —— 与项目里「运行时可切算法档，不用宏」那条约定一致：
    /// 宏会让「两个档位的画面看起来一模一样」在美术那边变成一个查不出成因的问题
    /// （变体没编、或者资产没重导）。
    ///
    /// 零态下 <c>.z</c> = 0 ⇒ 走程序化那一支 ⇒ <b>一次纹理读都不发</b>。
    /// 这条是「失能态整趟 dispatch 一次纹理读都不发」的具体形式。
    /// </summary>
    public enum JitterMode
    {
        /// <summary>不抖动，也不做时间重投影。零态，也是 A/B 对照档。</summary>
        Off = 0,

        /// <summary>
        /// 程序化：空间上一个 hash（PCG3D），时间上 R3 塑性常数的 Kronecker 序列。
        ///
        /// 为什么时间轴用低差异序列而不是每帧一个新随机数：随机数在 N 帧窗口内的
        /// 覆盖是有洞的（生日碰撞），而累积窗口就是 N ≈ τ·fps 帧 ——
        /// 洞的症状是残影里带一层低频斑。Kronecker 序列的差异度有下界，
        /// 而且**永不循环**（对比：一张 z=64 的 3D 噪声纹理每 64 帧重复一次）。
        ///
        /// hash 在**空间**上是白噪声。它与 <see cref="BlueNoise"/> 的差别只在
        /// 历史失效的那些区域可见（见后者的说明）。
        /// </summary>
        Procedural = 1,

        /// <summary>
        /// 蓝噪声：空间上采 URP 自带的 64×64 void-and-cluster 瓦片
        /// （<c>UniversalRenderPipelineRuntimeTextures.blueNoise64LTex</c>），
        /// 时间轴仍然是同一条 R3 序列。
        ///
        /// ---------------------------------------------------------------- 为什么不自己烘一张
        /// 引擎包里已经有一张**环形（toroidal）**的 void-and-cluster 瓦片，
        /// 无缝平铺、8 bit、Point 采样、<c>sRGBTexture: 0</c>。自己烘只在两种情况下
        /// 才有意义：要 &gt; 1 个独立通道，或者要 &gt; 64 的尺寸。前者由「同一张图的
        /// 三次固定偏移抽头」解决（Georgiev &amp; Fajardo 2016 的做法，由判据⑲盯着
        /// 三路之间的相关性）；后者在 froxel 这个分辨率（屏幕/8）上用不到。
        /// 于是 Vista 至今**零二进制资产**这件事也保住了 —— 而它顺带绕开一个真实的坑：
        /// 一个 git URL 引用的 package 落在只读的 <c>Library/PackageCache</c> 里，
        /// 生成器写不回去。
        ///
        /// ---------------------------------------------------------------- 它到底买到什么
        /// 诚实地说：**不是更好的收敛结果**。两个档位的时间极限是同一个
        /// （都是抖动分布上的期望），所以累积收敛之后画面一样。
        /// 它买到的是**历史失效那些区域的观感**（脱遮挡、快速转身、亮度死区被打穿——
        /// 这些区域的占比由判据⑮数出来）：那里只有本帧一个样本，
        /// 白噪声给的是一层随机颗粒，蓝噪声给的是把能量推到高频的颗粒，
        /// 而双线性上采样（屏幕/8）本身就是一个低通 —— 它把高频那部分大半吃掉。
        ///
        /// 业内基准要点名：UE5 的 <c>View.VolumetricFogTemporalJitter</c> 与 HDRP 的
        /// <c>_VBufferSampleOffset</c> **都是逐帧一个 float3 常量**，对所有 froxel
        /// 相同，空间上没有任何变化。所以本档位是在引擎基准之上多做一层，
        /// 它必须自己给出理由 —— 上面那一段就是理由，判据⑱⑳是它的度量。
        /// </summary>
        BlueNoise = 2,
    }

    /// <summary>
    /// 横向抖动沿深度方向的形态（#22b）。**这一档决定蓝噪声到底有没有收益。**
    ///
    /// ------------------------------------------------------------------ 为什么这是个开关而不是一个定论
    /// #22a 的横向抖动是<b>逐片独立</b>的（hash 的输入带 <c>id.z</c>），
    /// 理由写在当时的注释里：同一列上 N 个样本在横向也互相独立，方差按 N 降。
    /// 那条理由本身没错，但它与蓝噪声**互斥**：
    ///
    /// 一个像素最终看到的是沿列积分的结果，也就是 N ≈ 64 个横向偏移场的**和**。
    /// 逐片独立时这 N 个场不再是同一个场，聚合偏移的空间形态因此被改写 ——
    /// 而**改写成什么样，取决于源图是程序化 hash 还是一张瓦片**。这两种情况的
    /// 结论一致（蓝噪声的收益都没了），机制却完全不同，必须分开写：
    ///
    /// · **程序化 hash**：每一片的偏移场彼此解相关，中心极限定理把它们的和
    ///   推向高斯白噪声 —— 聚合场≈白。
    ///
    /// · **蓝噪声瓦片**：#22a 的实现是**固定的逐片瓦片步进** s = (17, 17)。
    ///   于是 Ā(p) = (1/N)·Σ_z F(v(p + s·z)) 是 w = F(v) 的一个**线性滤波**，
    ///   其 Fourier 乘子精确地是 K(f) = (1/64)Σ_{z=0..63} e^{2πi f·s z/64}
    ///   = δ[f_x + f_y ≡ 0 (mod 64)]（精确，因为 17 在 mod 64 下可逆、z 又跑满整个周期）。
    ///   剩下的基函数只有 e^{2πi f_x(x−y)/64}，也就是 **Ā(x,y) = h((x−y) mod 64)**：
    ///   一个只依赖反对角的一维函数 —— **对角条纹，不是白噪声**。
    ///   初等版本更好核对：17·49 = 833 = 13·64 + 1 ⇒ {17z mod 64} = Z₆₄ ⇒
    ///   一列的 64 个抽样点**正好是整条反对角线** {(x+k, y+k)}，
    ///   于是列均值当然只是 (x−y) 的函数。这条对**任意**逐点函数 F 都成立，
    ///   所以 <c>frac</c> 那次绕回救不了它。
    ///
    /// 也就是说：这里曾经写过的「CLT 把它抹成白噪声」这条论证，对蓝噪声档是**错的**，
    /// 真实症状比白噪声更糟（结构化的斜纹比无结构的颗粒更容易被眼睛抓到）。
    /// 保留这段更正是有用的 —— 它记着「任何固定的逐片步进都逃不掉，
    /// 只有逐片**随机**偏移才能把 K(f) 压回 O(1/√N)」。那条后续（随机逐片偏移）
    /// 没有实现：它要求蓝噪声档的收益先被证明值得，而那正是判据⑳的职责。
    ///
    /// 要让蓝噪声在屏幕上仍然是蓝的，横向偏移必须**沿 z 恒定**（逐列一致），
    /// 这也正好是 UE5 / HDRP 那个「全屏一个常量偏移」的结构，只是从「全屏一个」
    /// 升级成「逐屏幕瓦片一个」。
    ///
    /// 两档都留成运行时开关，因为「抹掉」这件事本身就是一格能量出来的判据：
    /// 判据⑳并排量两档的聚合偏移。它量的**不是**相关系数 —— 归一化的一阶相关 ρ₁
    /// 判不出「变白了」（N 个场平均之后分子分母同比缩小，ρ₁ 不变）。它量的是
    /// 上面那条条纹预言的**逐点恒等式** max|Ā(p+(1,1)) − Ā(p)| ≈ 0，
    /// 并配一格必需的对照 max|Ā(p+(1,0)) − Ā(p)| = O(0.1)：
    /// 少了对照，一个「Ā 恒为常数」的 bug（= 抖动整个失效）会把恒等式假通过。
    /// 同一格因此兼任 A/B 对照。
    ///
    /// 深度抖动**不受本档影响**，它永远逐片独立：深度方向上每一片本来就是
    /// 一个独立的积分子区间，让它们同步等于让整条射线一起前后平移，
    /// 那才是真正丢掉方差收益（症状：切片台阶不消失，只是整体前后晃）。
    /// </summary>
    public enum LateralJitterShape
    {
        /// <summary>
        /// 逐列一致：同一条屏幕列（同一个 froxel 的 xy）上所有切片用**同一个**横向偏移。
        /// 默认档 —— 它让蓝噪声的空间谱在屏幕上活下来，也是 UE5/HDRP 的结构。
        /// </summary>
        PerColumn = 0,

        /// <summary>
        /// 逐片独立：#22a 的行为。横向方差按 N 降，但屏幕上的聚合偏移不再是源图的形态 ——
        /// 程序化 hash 下被 CLT 抹成白噪声，蓝噪声下退化成沿反对角的**对角条纹**
        /// （固定瓦片步进的精确后果，推导见本枚举的头注）。
        /// **这是对照档，不是推荐档**：保留它唯一的目的是让上面那条论证可被度量（判据⑳）。
        /// </summary>
        PerSlice = 1,
    }

    /// <summary>
    /// 近层体积雾 froxel 体的配置。
    ///
    /// 本类只做两件事：把「屏幕比例 + 远边界」换成一份**分配口径**
    /// （<see cref="VistaFroxelVolumeDesc"/>），以及把远边界按阴影距离夹紧并给出诊断串。
    /// 它不持有 GPU 资源，也不知道体积里装的是什么。
    ///
    /// 介质本身的参数（σ_t、标高、反照率、HG g）不在这里 —— 那些是
    /// <see cref="VistaFogSettings"/> 的，近层与 AP LUT 共用**同一份介质定义**。
    /// 分开的理由：换分辨率不该动介质，换介质不该重分配纹理。
    ///
    /// ------------------------------------------------------------------ 分层归属
    /// 近层负责 [0, handoff]，AP LUT 负责 (handoff, 32 km]，两者在 handoff 处对接 ——
    /// 也就是说 AP 的 <c>nearDistanceKm</c> 由本类推出来，而不是美术填的。
    /// 为什么不是「AP 关掉雾、远场另写一份解析式」：那会产生第二份远场雾实现，
    /// 而两份实现漂移的症状是「远景雾感不对」，会被误判成切片不够密。
    /// 为什么不是「从 AP 里把近段减掉」：那要除以一个在浓雾里趋近 0 的 T_near，
    /// 数值上是灾难。
    /// 推 near 的代价：#7 的 AP 档位扫描（Log vs Power、d=16/32/48/64）必须在
    /// <c>near = handoff</c> 下重跑，且 Log 优于 Power 的结论**可能翻转**
    /// （近场不再需要密切片了）。那次重跑本来就是 #7 留下的待办。
    /// </summary>
    [Serializable]
    public class VistaVolumetricFogSettings
    {
        [Header("分辨率")]
        [Tooltip("XY 相对屏幕的降采样倍数。8 = 1920×1080 下 240×135。\n"
               + "UE5 的 VolumetricFogGridPixelSize 默认 8，HDRP 的 V-Buffer 也是屏幕 /8。\n"
               + "为什么是「屏幕比例」而不是「固定 240×135」：后者在 2560×1440 下变成 1/10.7，"
               + "效果会随分辨率漂移 —— 同一套参数在两台机器上不是同一个画面。")]
        [Range(2, 16)] public int screenDivisor = 8;

        [Tooltip("深度切片数。64 = HDRP 的 Medium 档。\n"
               + "UE5 用 128，但它没有时间重投影；本项目在 #22 会加抖动 + 重投影，"
               + "所以 64 片配抖动的等效采样密度不低于 128 片不抖动。")]
        [Range(8, 256)] public int sliceCount = 64;

        [Header("深度范围")]
        [Tooltip("近层体的远边界（米）。HDRP 默认 64 m，UE5 默认 60 m —— 业内量级是几十米。\n"
               + "运行时会被相机的阴影距离夹住（超了会报错，不静默夹）。\n"
               + "别把它直接填成阴影距离：最后一级级联本身就低分辨率（光柱在那儿已经糊了），"
               + "而 64 片摊到 500 m 会让相机前方第一片从 0.3 m 长到 ~4 m，"
               + "近处会看到切片台阶。")]
        [Min(1f)] public float farDistanceMeters = 64f;

        [Header("开发中（#21/#22）")]
        [Tooltip("逐 froxel 的光照注入 + 深度积分。\n\n"
               + "开了**最终画面仍然不变**，但两张表都已经在逐帧计算了：\n"
               + "注入表存 (σ_s·J, σ_t)（#20），积分表存 (累积内散射, 1 − T)（#21）。\n"
               + "把积分结果贴到画面上是 #25 的统一采样函数 —— 那一步还要处理"
               + "半透明物体吃雾、以及近层与 AP LUT 的接手，不属于这里。\n\n"
               + "所以下面的 Debug View 是现在唯一能看到这两张表的地方，"
               + "它也是关掉这个开关时唯一的症状来源：表全 0 ⇒ 积分 Alpha 档全黑。\n\n"
               + "为什么注入与积分要分两步做、而不是一趟算完：\n"
               + "注入表里的量是**局部**的（换个相机位置还是那个值），可以做时间重投影；"
               + "积分表是「从相机走到这里」的累积量，重投影它在物理上没有意义。"
               + "#22 的重投影因此必须插在两者之间 —— 这条两趟的划分是它的结构前提，"
               + "不是实现顺序上的偶然。\n\n"
               + "美术不需要动它：VistaFogSettings.Mode 里**没有** Froxel 档，"
               + "在 #25 的合成落地之前也不会有。")]
        public bool enableInjection = false;

        [Header("时间重投影与抖动（#22）")]
        [Tooltip("每帧把采样点在 froxel 内部错开，再用上一帧的注入表做时间累积。\n\n"
               + "Off 是 A/B 对照档，不是「省性能档」—— 关掉之后每个 froxel 恒在同一个点"
               + "取样，切片边界与阴影边界都会露出台阶，而开销只省下一次历史表采样。\n\n"
               + "为什么抖动与重投影是**同一个**开关：抖动单独开 = 逐帧换采样点却不累积，"
               + "画面上是纯噪声闪烁，比不抖动更差；重投影单独开 = 每帧混同一个采样点，"
               + "混完还是那个点，只多花一次采样。两者只有一起才有意义。\n\n"
               + "两个在线档（程序化 hash / 蓝噪声瓦片）在**收敛之后是同一个画面** ——"
               + "时间极限都是抖动分布上的期望。差别只在历史失效的区域（脱遮挡、快速转身）"
               + "那唯一一个样本上：白噪声给随机颗粒，蓝噪声把能量推到高频，"
               + "而屏幕/8 的双线性上采样本身是低通，会把高频那部分大半吃掉。\n"
               + "蓝噪声档取不到 URP 那张图时会自动回落到程序化，并在状态日志里打印原因。")]
        public JitterMode jitterMode = JitterMode.Procedural;

        [Tooltip("横向（屏幕 XY）抖动幅度，单位是一个 froxel 的宽度。1 = 在整格内均匀取样。\n\n"
               + "横向与深度分开两个旋钮，是因为两者的格子尺寸差一个量级：60 m 处"
               + "横向约 0.94 m，而深度方向那一片长 4.93 m —— 深度是横向的 5.2 倍。"
               + "共用一个幅度会让「调到够遮住切片台阶」的同时把横向抖过头，"
               + "症状是雾在物体边缘渗出去。")]
        [Range(0f, 1f)] public float lateralJitterAmount = 1f;

        [Tooltip("横向抖动沿深度方向是「逐列一致」还是「逐片独立」。\n\n"
               + "默认逐列一致。理由不是省一次 hash，而是：一个像素看到的是沿列积分的和，"
               + "逐片独立时这 64 个偏移场不再是同一个场，聚合出来的形态就不是源图的形态了 ——"
               + "程序化 hash 下被中心极限定理抹成白噪声；蓝噪声下更糟，"
               + "固定的逐片瓦片步进会让聚合偏移退化成沿反对角的**斜条纹**"
               + "（结构化的纹路比无结构的颗粒更容易被眼睛抓到）。\n"
               + "换句话说，逐片独立这个「方差按 N 降」的优化会把蓝噪声档的全部收益换掉。\n\n"
               + "逐片独立（= #22a 的行为）保留成对照档：Window/Vista/Log Volumetric Fog State "
               + "里有一格并排量两档的聚合偏移，逐片独立必须满足那条对角恒等式、逐列一致必须不满足。\n\n"
               + "深度抖动不受这个开关影响，它永远逐片独立。")]
        public LateralJitterShape lateralJitterShape = LateralJitterShape.PerColumn;

        [Tooltip("深度（切片方向）抖动幅度，单位是一片的厚度。1 = 在整片内均匀取样。\n\n"
               + "这是本项目里更重要的那一个：切片台阶是近层雾最显眼的瑕疵，"
               + "而深度格子比横向格子大 5.2 倍。")]
        [Range(0f, 1f)] public float depthJitterAmount = 1f;

        [Tooltip("历史累积的时间常数 τ（秒）。新样本的权重 = 1 − exp(−Δt/τ)。\n\n"
               + "为什么是时间常数而不是 HDRP/UE5 那样直接填一个「历史权重 0.95」："
               + "定权重的收敛速度绑死在帧率上 —— 30 fps 下要 20 帧（0.67 s）才收敛，"
               + "144 fps 下只要 0.14 s，同一套参数在两台机器上是两个残影长度。\n\n"
               + "默认 0.33 s 就是拿 HDRP 那个 0.95 在 60 fps 下反解出来的"
               + "（1 − exp(−1/60/0.33) ≈ 0.05），所以默认档与业内基准是同一个观感，"
               + "只是不再随帧率漂。")]
        [Range(0.02f, 2f)] public float historyTimeConstant = 0.33f;

        [Tooltip("历史失效的亮度死区下端：相对亮度变化小于这个值时**完全不降权**。\n\n"
               + "为什么必须有死区：抖动本身就是一种逐帧亮度变化。没有死区的话，"
               + "这条规则会把抖动噪声当成「场景变了」而降权，亲手毁掉它本该保护的累积。\n"
               + "所以这个下端要摆在**实测的抖动引起的亮度散布**之上，不能凭感觉填 ——"
               + "Window/Vista/Log Volumetric Fog State 里有一格专门量它并与这个值比较。")]
        [Range(0f, 1f)] public float luminanceRejectStart = 0.25f;

        [Tooltip("历史失效的亮度死区上端：相对亮度变化到达这个值时权重降到 0（纯本帧）。\n\n"
               + "两端之间线性过渡。不做硬阈值是因为硬阈值会在阈值附近让相邻 froxel"
               + "一半累积一半不累积，症状是运动物体边缘出现一条抖动的亮边。")]
        [Range(0f, 1f)] public float luminanceRejectFull = 0.9f;

        [Header("调试视图")]
        [Tooltip("把 froxel 表直接画到屏幕上（整屏替换，不叠加）。\n\n"
               + "Off 之外的档位都会**盖掉整个画面** —— 这是故意的：叠加会让"
               + "「表是空的」与「表很淡」在同一个像素上混起来，而这个视图存在的"
               + "全部目的就是消除那种混淆。\n\n"
               + "三个深度耦合档（积分 RGB / 积分 Alpha / 注入 RGB）在**像素自己的场景深度**"
               + "上采样，也就是 #25 的合成将来会吃的那一个操作数。\n"
               + "「单片」档改为直接把某一片铺满屏幕，回答的是另一个问题：表自身有没有"
               + "空洞或条带 —— 场景深度只覆盖 z-buffer 里存在的距离，天空方向上的切片"
               + "永远不会被任何像素采到。\n\n"
               + "越界不钳死：比远边界更远的像素画品红（归 AP LUT 管），"
               + "比近端更近的画青色。品红区占画面多大一块，就是近层实际覆盖了多少。")]
        public FroxelDebugView debugView = FroxelDebugView.Off;

        [Tooltip("「单片」档要看的切片下标。超过 N−1 会被夹到最后一片，"
               + "夹紧后的实际值由 Window/Vista/Log Volumetric Fog State 打印。")]
        [Range(0, 255)] public int debugSlice = 32;

        [Tooltip("调试视图的 RGB 增益。\n\n"
               + "表里存的是**预曝光**辐亮度，而这个视图刻意不套色调映射 ——"
               + "套了之后「表饱和了」与「tonemap 滚到顶了」在画面上无法区分。"
               + "代价就是暗部要靠这个旋钮手动抬：一个能读的数，比一条看不见的曲线好。\n\n"
               + "积分 Alpha 档不受它影响：那一路已经是归一化的 1 − T ∈ [0,1]，"
               + "乘上去会让「雾很厚」与「增益开大了」看起来一样。")]
        [Min(0f)] public float debugGain = 1f;

        /// <summary>
        /// 「单片」档的实际切片下标。夹到 [0, depth−1]。
        ///
        /// 抽成静态函数是为了让**渲染路径与状态日志共用同一份夹紧规则** ——
        /// 各写一份的症状是日志说「看的是第 63 片」而画面上是第 127 片，
        /// 而这个视图的全部价值就是「屏幕上这一片到底是哪一片」。
        /// </summary>
        public static int ResolveDebugSlice(int requested, int depth)
            => Mathf.Clamp(requested, 0, Mathf.Max(0, depth - 1));

        /// <summary>
        /// 亮度死区的两端，保证 <paramref name="full"/> 严格大于 <paramref name="start"/>。
        ///
        /// 为什么要夹：两个独立 Range 滑条可以被拖成 full ≤ start，那时过渡区宽度为 0，
        /// 规则退化成硬阈值 —— 而硬阈值正是上面 tooltip 里说明**不要**的那种形状。
        /// 让它在这里变成不可表达，比在 shader 里除以一个可能为 0 的宽度要好。
        /// 抽成静态函数的理由与 <see cref="ResolveDebugSlice"/> 相同：渲染路径与状态日志
        /// 必须共用同一份规则，否则日志会说「死区 0.25~0.9」而 GPU 上是别的数。
        /// </summary>
        public void ResolveLuminanceReject(out float start, out float full)
        {
            start = Mathf.Clamp01(luminanceRejectStart);
            full  = Mathf.Max(Mathf.Clamp01(luminanceRejectFull), start + k_MinRejectWidth);
        }

        /// <summary>死区过渡区的最小宽度。见 <see cref="ResolveLuminanceReject"/>。</summary>
        public const float k_MinRejectWidth = 1e-3f;

        // --------------------------------------------------------------------
        //  切片分布：纯指数
        //
        //  约定（写在这里，因为写反的症状是「雾整体近了/远了半片」，只能靠判据抓）：
        //
        //    编码坐标 e = ln(d / near) / ln(far / near)，**e 直接就是 3D 纹理的 w 坐标**。
        //    切片 i 的纹素中心在 w = (i + 0.5) / N，于是它存的是
        //      「从相机到 d_i 的累积」，d_i = near · (far/near)^((i+0.5)/N)。
        //
        //  为什么这么定，而不是像 AP LUT 那样让两端精确（w_i = i/(N-1)）：
        //  这里的读端是**逐像素按深度采样**，采样坐标必须是 e(d) 本身。
        //  若切片存的是分段远平面 t(i+1)（HDRP 的做法），那么 e(t(i+1)) = (i+1)/N
        //  而纹素中心在 (i+0.5)/N，读的时候必须显式回退半个纹素 —— HDRP 那个
        //  已知的 half-slice bias 就是这么来的。把「存的距离」直接放在纹素中心上，
        //  读端就是 w = e(d)，一个偏移都不用记。
        //
        //  代价：体积的实际远端是 d_{N-1} = far · (far/near)^(-0.5/N)，**不是 far**。
        //  默认档（near 0.3 m / far 64 m / N 64）下 d_63 = 61.374 m，差 2.6 m。
        //  所以 AP 的接手点是 d_{N-1} 而不是 far —— 见 VistaFroxelVolumeDesc.handoffMeters。
        //  这个差值是判据②抓的东西：把 AP 的 near 填成 far 会在 61.4~64 m 之间留一段
        //  两层都算过的雾，症状是那个距离上一圈很淡的亮环。
        //
        //  分段 i 的介质求值点取两个存储距离的几何均值，闭式解正好是 e = i/N：
        //    sample(i) = near · (far/near)^(i/N)
        //  它落在分段的**度量**中点附近，偏差 = (√ρ − 1)/(ρ − 1) − 0.5，ρ 是相邻切片比。
        //  默认档 ρ = 1.0874 ⇒ 0.4895，即比中点早 0.0105 个分段 ——
        //  这条与项目既有的「采样点重心律」是同一件事，由判据④按恒等式校验。
        // --------------------------------------------------------------------

        /// <summary>切片数下界为 2：分布映射里有 1/N，且判据要能取到相邻两片。</summary>
        public int depth => Mathf.Clamp(sliceCount, 2, 256);

        /// <summary>
        /// 解析这一帧的分配口径。
        ///
        /// <paramref name="maxShadowDistance"/> 传 <c>UniversalCameraData.maxShadowDistance</c>
        /// （URP 里它是 <c>min(asset.shadowDistance, camera.farClipPlane)</c>），
        /// 不是 asset 上那个原始值 —— 远裁剪面也会把阴影距离砍掉，而近层体越过阴影范围
        /// 之后光柱会在一条硬边上消失。
        /// </summary>
        public VistaFroxelVolumeDesc Resolve(int screenWidth, int screenHeight,
                                             float cameraNearPlane, float maxShadowDistance,
                                             out string clampDiagnostic)
        {
            int div = Mathf.Clamp(screenDivisor, 2, 16);
            float far = ResolveFarDistance(farDistanceMeters, maxShadowDistance, out clampDiagnostic);

            return new VistaFroxelVolumeDesc(
                Mathf.Max(1, VistaComputeUtils.DivRoundUp(Mathf.Max(1, screenWidth), div)),
                Mathf.Max(1, VistaComputeUtils.DivRoundUp(Mathf.Max(1, screenHeight), div)),
                depth,
                // 近端取相机近裁剪面：比它更近的东西不会被画出来，为不可见的距离
                // 留切片等于白扔分辨率。下界 1 cm 是防「近裁剪面填了 0」——
                // 那时 ln(d/near) 会变成 -inf，整张体积变 NaN。
                Mathf.Max(0.01f, cameraNearPlane),
                far);
        }

        /// <summary>
        /// 远边界的夹紧规则。抽成 static 纯函数**只为一件事**：判据能直接调它，
        /// 不需要跑一帧真渲染 —— 而「D 被夹了却没人报错」正是要抓的失效。
        ///
        /// 硬约束：<c>D ≤ maxShadowDistance</c>。越过阴影距离就没有阴影贴图了，
        /// 那里的介质会被当成全亮，于是光柱在一个平面上**整齐地消失**。
        /// 那条硬边恰好坐在最浓的近雾外侧，是所有失效形态里最显眼的一种。
        ///
        /// 例外：<paramref name="maxShadowDistance"/> ≤ 0 表示这台相机**根本没有阴影**
        /// （URP 在阴影全关时把它置 0）。那时整个画面都没有光柱，也就没有「硬边」可言，
        /// 夹紧只会把体积压成 0，把「没有光柱」升级成「连雾都没有」。所以不夹。
        ///
        /// 静默夹紧是不可接受的：美术把范围调到 500 m、画面却没变，
        /// 这个问题在日志里查不到任何线索。
        /// </summary>
        /// <param name="clampDiagnostic">被夹紧时是人类可读的原因串；未夹紧时为 null。</param>
        public static float ResolveFarDistance(float requested, float maxShadowDistance,
                                              out string clampDiagnostic)
        {
            clampDiagnostic = null;
            float far = Mathf.Max(k_MinFarDistanceMeters, requested);

            if (maxShadowDistance <= 0f)
                return far;

            if (far <= maxShadowDistance)
                return far;

            clampDiagnostic =
                $"[Vista] 体积雾的远边界 {far:F1} m 超过了相机的阴影距离 {maxShadowDistance:F1} m，"
                + $"已夹到 {maxShadowDistance:F1} m。阴影距离之外没有阴影贴图，"
                + "那里的雾会被当成全亮，光柱会在一个平面上整齐消失。"
                + "要么把 URP Asset 的 Shadow Distance 调大，要么把远边界调小。";
            return maxShadowDistance;
        }

        /// <summary>
        /// 远边界的下界。1 m 不是「够用」，是「1/N 的指数比还有意义」的下界：
        /// far 掉到近裁剪面以下时 ln(far/near) 变负，切片顺序会整体翻转。
        /// </summary>
        public const float k_MinFarDistanceMeters = 1f;

        public VistaVolumetricFogSettings Clone()
            => (VistaVolumetricFogSettings)MemberwiseClone();
    }

    /// <summary>
    /// froxel 体的**分配口径 + 分布常量**。settings 是美术填的，本结构是解析后的结果 ——
    /// 分开的理由：分辨率依赖屏幕尺寸与相机，不是设置对象自己能算出来的，
    /// 而分配脏检查必须比较「解析后的东西」。
    ///
    /// 是 readonly struct 而不是 class：它每帧都要构造一次，且要能用 == 做脏检查。
    /// </summary>
    public readonly struct VistaFroxelVolumeDesc : IEquatable<VistaFroxelVolumeDesc>
    {
        public readonly int width;
        public readonly int height;
        public readonly int depth;
        /// <summary>体积近端（米）= 相机近裁剪面。</summary>
        public readonly float nearMeters;
        /// <summary>远边界参数（米），已夹紧。**不是**体积实际的远端，见 <see cref="handoffMeters"/>。</summary>
        public readonly float farMeters;

        public VistaFroxelVolumeDesc(int width, int height, int depth,
                                     float nearMeters, float farMeters)
        {
            this.width = width;
            this.height = height;
            this.depth = depth;
            this.nearMeters = nearMeters;
            // 保证 far > near：相等时 ln(far/near) = 0，编码坐标除零。
            this.farMeters = Mathf.Max(nearMeters * 1.001f, farMeters);
        }

        /// <summary>far / near。指数分布的总比值。</summary>
        public float ratio => farMeters / nearMeters;

        /// <summary>ln(far / near)。编码/解码两个方向都要它，所以打包下发而不是在 shader 里算。</summary>
        public float logRatio => Mathf.Log(ratio);

        /// <summary>相邻两个存储距离的比 ρ = (far/near)^(1/N)。判据④的输入。</summary>
        public float sliceRatio => Mathf.Exp(logRatio / depth);

        /// <summary>切片 i 存的累积距离（米）= near · (far/near)^((i+0.5)/N)。</summary>
        public float StoredDistance(int slice)
            => nearMeters * Mathf.Exp(logRatio * (slice + 0.5f) / depth);

        /// <summary>分段 i 的介质求值点（米）= near · (far/near)^(i/N)。i = 0 时退化成分段中点。</summary>
        public float SampleDistance(int slice)
            => slice == 0
                ? 0.5f * StoredDistance(0)
                : nearMeters * Mathf.Exp(logRatio * slice / depth);

        /// <summary>分段 i 的近端（米）。分段 0 从相机（0）开始。</summary>
        public float SegmentNear(int slice) => slice == 0 ? 0f : StoredDistance(slice - 1);

        /// <summary>分段 i 的远端（米）。</summary>
        public float SegmentFar(int slice) => StoredDistance(slice);

        /// <summary>
        /// 体积实际的远端（米）= 最后一片存的距离 = far · (far/near)^(-0.5/N)。
        /// **AP LUT 的 near 必须填这个数**，不是 <see cref="farMeters"/> ——
        /// 填后者会在这两个数之间留一段两层都算过的雾。
        /// </summary>
        public float handoffMeters => StoredDistance(depth - 1);

        /// <summary>x: near (m), y: far (m), z: ln(far/near), w: 1/ln(far/near)。</summary>
        public Vector4 packedRange => new Vector4(
            nearMeters, farMeters, logRatio, 1f / Mathf.Max(1e-6f, logRatio));

        /// <summary>xyz: 尺寸, w: 1/N。</summary>
        public Vector4 packedSize => new Vector4(width, height, depth, 1f / depth);

        /// <summary>
        /// 只比较影响 3D 纹理分配的三个尺寸。距离范围每帧推 cbuffer 即可生效 ——
        /// 相机走动时近裁剪面不变但阴影距离可能变，把距离并进来会让体积在
        /// 那一帧被整体重分配（三张 RGBA16F 3D 表，实打实的卡顿）。
        /// </summary>
        public bool Equals(VistaFroxelVolumeDesc other)
            => width == other.width && height == other.height && depth == other.depth;

        public override bool Equals(object obj)
            => obj is VistaFroxelVolumeDesc other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(width, height, depth);

        public static bool operator ==(VistaFroxelVolumeDesc a, VistaFroxelVolumeDesc b) => a.Equals(b);
        public static bool operator !=(VistaFroxelVolumeDesc a, VistaFroxelVolumeDesc b) => !a.Equals(b);

        public override string ToString()
            => $"{width}×{height}×{depth}, near {nearMeters:F2} m, far {farMeters:F1} m, "
             + $"handoff {handoffMeters:F3} m, ρ {sliceRatio:F6}";
    }
}
