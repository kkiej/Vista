#ifndef VISTA_FOG_MEDIUM_INCLUDED
#define VISTA_FOG_MEDIUM_INCLUDED

// ============================================================================
//  雾介质模型（密度剖面 + 反照率 + 各向异性 + 太阳方向自遮蔽）
//
//  这个文件只回答一个问题：**空间某一点的雾是什么**（散射系数、消光系数、相位的 g）。
//  它**不回答**"这一点被照亮了多少" —— 那是相位函数与积分器的事，
//  统一在 AtmosphereScattering.hlsl 里做。分界线是刻意的：
//  本项目已经踩过一次"同一个量出现第二份实现"的坑，而雾的消费者有两条路
//    档 A（PC 主线）  近层 froxel 体，逐 froxel 采级联阴影（#19~#22）
//    档 D（移动端）    并进现有 32³ AP LUT 的 march（#18）
//  两条路的**循环结构不同**，但物理必须逐位相同 —— 所以循环各写一遍，
//  介质与能量计算只有这一份。这条纪律与 AtmosphereScattering.hlsl 开头
//  "只复制循环，绝不复制物理" 是同一条。
//
//  ------------------------------------------------------------------ 与大气介质的关系
//  大气介质是 AtmosphereDef.hlsl 的 VistaSampleMedium（Rayleigh + Mie + 臭氧），
//  雾是**并列的第四个组分**，不是它的一个参数。理由有三个，都是可以直接指出来的差别：
//    · 尺度差 3 个数量级：Mie 标高 1.2 km，雾标高 10~200 m；
//    · 大气介质进了**静态** Transmittance / MS LUT（烘一次、球对称参数化），
//      而雾是逐帧变、可局部、可被美术 K 曲线的；把雾并进那两张表等于让雾
//      在 6360 km 的球壳上生效，物理上是胡话；
//    · SkyView LUT 的 uv 打包依赖方位对称（见 AtmosphereScattering.hlsl 的参数化注释），
//      局部雾会破坏它。
//  所以雾**只**进 AP LUT 的 march 与近层 froxel 的 march，
//  静态 LUT 与 SkyView LUT 一律传 VistaFogSampleNone()。
//  推论（重要）：天空像素的雾不是靠 SkyView LUT 里含雾拿到的，
//  而是靠雾体远端的 transmittance/inScatter 覆盖上去 —— 与 UE5 的做法一致，
//  也正是"分层"这个结构本来就该有的行为。
//
//  ------------------------------------------------------------------ 为什么高度不能从 posKm 算
//  march 用的位置是大气空间的 posKm，它的 y 在 6360 km 附近。
//  fp32 在 6360 上的 ulp 是 2^12·2^-23 = 4.883e-4 km ≈ **0.49 m**。
//  也就是说 `r - bottomRadius` 或 `VistaAtmosphereToWorld(posKm).y` 拿到的高度，
//  分辨率只有半米 —— 对一个标高 20 m 的地面雾，整条密度剖面会被量化成 ~41 级台阶，
//  症状是雾里出现水平条带，而且**条带位置随相机高度跳**（因为量化格点是绝对的）。
//  正确做法：高度必须从**相机相对量**推出来。march 的位置本来就是 posKm = viewPos + t·dir，
//  所以 t·dir.y 是全精度的小量，只要 CPU 侧把"相机相对雾层底的高度"作为一个
//  精确的米制标量传进来（_VistaFogHeight.x），高度就完全避开了 6360 这个大数。
//  这就是 VistaFogHeightMeters 存在的唯一理由 —— 它看起来只是一个 mad，
//  但换成"从 p 反算"就会坏。
//
//  ------------------------------------------------------------------ 平地近似的误差
//  本文件把雾当作与局部水平面平行的板（高度 = 世界 Y），不跟随星球曲率。
//  曲率带来的高度偏差是弓高 d²/(2R)，R = 6360 km：
//      d = 500 m  → 1.97 cm
//      d = 2 km   → 31.4 cm
//      d = 5 km   → 1.97 m
//  近层雾体的范围绑定 URP 的 shadow distance（典型 150~500 m），
//  那里偏差比标高小两个数量级，平地近似是精确的。
//  档 D 的 AP LUT 铺到 32 km，但雾在那个距离上早已按指数衰减掉；
//  只有"标高设成上千米的准均匀雾"才会让曲率可见，而那种设置本身就该用大气的 Mie 去表达。
//  另外，平地板也正好是"高度雾"作为**美术概念**的定义（一层平的雾），
//  跟随曲率反而让远处的雾层看起来往下掉，不符合预期。
//
//  ------------------------------------------------------------------ 参数怎么给美术
//  shader 侧收的是 σ_t（1/km），但那个数对美术没有意义。CPU 侧（#18 接）按这两个换算：
//      平均自由程 L（m）：σ_t[1/km] = 1000 / L      —— 走 L 米后透射率降到 1/e
//      气象能见度 V（m）：σ_t[1/km] = 3912 / V      —— Koschmieder，2% 对比阈
//  取 L = 100 m 时 σ_t = 10 /km，对应能见度 391 m —— 两个数字都能直接对着场景验。
//  HDRP 的 Fog Attenuation Distance 就是 L，所以这个口径有先例；
//  UE 的 Fog Density 是个没有单位的调参数字，不采用。
//
//  σ_s 由 σ_t × 单次散射反照率给出，而不是各存一份 ——
//  各存一份的话 σ_s > σ_t 这种不物理的组合在数据上就是可表示的，
//  而它的症状（能量放大、雾越远越亮）会被误读成"积分器写错了"。
//  真实的水滴雾吸收极弱，反照率接近 1；留成 RGB 是为了脏雾/烟（偏低、偏色）。
// ============================================================================

#include "Packages/com.kkiej.vista/ShaderLibrary/AtmosphereDef.hlsl"

// ----------------------------------------------------------------------------
//  常量
//
//  失能态 = 全零态。σ_t = 0 时散射与消光都是 0，雾的贡献逐位等于"没有雾"。
//  这条性质是刻意保的：这份 cbuffer 在 #18 之前根本没有写入者，
//  而 Unity 未赋值的全局常量是 0，于是"忘了推 cbuffer"的后果只能是**没有雾**，
//  不可能是"雾长错了"。#12 那边 _VistaApConsumer 需要每帧无条件重写才能保证
//  这一点（因为它的失能态是 0 而使能态是 1，残留会反过来），雾这里不需要，
//  因为使能态本来就要求非零的 σ_t。
// ----------------------------------------------------------------------------

CBUFFER_START(VistaFogCB)
    // xyz 单次散射反照率 (0~1，逐通道)；w HG 相位的 g (-1~1)
    float4 _VistaFogAlbedo;
    // xyz 雾层底部（h = 0）的消光系数 σ_t (1/km)；w 自遮蔽项的掠射放大上限
    float4 _VistaFogExtinct;
    // x 相机相对雾层底的高度 (m)      —— 必须由 CPU 用精确的世界 Y 算出，见文件头
    // y 1/标高 (1/m)                  —— 0 = 无限标高（均匀雾）
    // z 天光环境项强度 (0~1)
    // w > 0.5 = 太阳方向的解析自遮蔽项生效
    float4 _VistaFogHeight;
CBUFFER_END

// ----------------------------------------------------------------------------
//  介质采样
// ----------------------------------------------------------------------------
struct VistaFogSample
{
    float3 scattering;  // σ_s (1/km)
    float3 extinction;  // σ_t (1/km)，逐通道 >= scattering（由反照率 <= 1 保证）
    float  phaseG;      // HG 的 g
    float  density;     // 归一化密度，雾层底 = 1；自遮蔽的解析式要用它
};

// 静态 LUT / SkyView LUT 传这个。见文件头"与大气介质的关系"。
VistaFogSample VistaFogSampleNone()
{
    VistaFogSample f;
    f.scattering = 0.0;
    f.extinction = 0.0;
    f.phaseG     = 0.0;
    f.density    = 0.0;
    return f;
}

// 沿视线走到 tKm 处时，该点相对雾层底的高度（米）。
// rayDirWorldY 是视线方向的世界 Y 分量 —— 大气空间只是把世界平移了，轴向一致，
// 所以 rayDir.y 直接就是它，不需要变换。
// 精度：右项是 t·dir.y 这个小量，左项是 CPU 给的精确米制标量，
// 全程不出现 6360 这个大数。理由见文件头。
float VistaFogHeightMeters(float tKm, float rayDirWorldY)
{
    return _VistaFogHeight.x + tKm * rayDirWorldY * 1000.0;
}

// 指数高度剖面。
// h < 0（雾层底以下）钳到底部密度而不是让 exp 继续涨：
// 不钳的话相机掉进一个坑里就会拿到指数级的密度，画面直接糊成一片纯色，
// 而且那个纯色的亮度依赖坑有多深 —— 是个没有上界的量。
// 钳的代价是雾层底以下密度恒定，这也正是 "雾层底" 作为美术锚点该有的语义。
VistaFogSample VistaSampleFog(float heightMeters)
{
    float density = exp(-max(heightMeters, 0.0) * _VistaFogHeight.y);

    VistaFogSample f;
    f.density    = density;
    f.extinction = _VistaFogExtinct.xyz * density;
    // saturate 在 shader 里做而不是信任 CPU：它保证 σ_s <= σ_t 在数据上就不可表示。
    // _VistaFogAlbedo 是 uniform，这条 saturate 会被提到循环外，逐样本零开销。
    f.scattering = f.extinction * saturate(_VistaFogAlbedo.xyz);
    f.phaseG     = _VistaFogAlbedo.w;
    return f;
}

// ----------------------------------------------------------------------------
//  太阳方向的自遮蔽（雾自己挡住阳光）
//
//  ---- 为什么有这一项 ----
//  雾的太阳光照现在是 大气 Transmittance LUT × 阴影贴图，两者都不含**雾自己的消光**。
//  于是浓雾被照得内外一样亮，看起来像一块发光的均匀体积，
//  缺的正是"雾顶亮、雾底暗"这个最能读出厚度的层次。
//
//  ---- 为什么它可以是解析的 ----
//  指数剖面沿任意直线的光学深度有闭式解。从高度 h 沿垂直分量 sy 向上到无穷：
//      τ = ∫₀^∞ σ_t·exp(-(h + s·sy)/H) ds = σ_t · (H / sy) · exp(-h/H)
//  右边三项分别是 _VistaFogExtinct.xyz、标高/掠射放大、density。
//  一个 rcp + 一次 exp，不需要沿太阳方向再 march。
//
//  ---- 为什么默认关 ----
//  UE5 的 Volumetric Fog 与 HDRP 的 Volumetric Fog **都不做这一项**，
//  所以"业内主流"这一栏的答案是"不做"。它是可选增强，不是欠缺的功能。
//  同时它的模型是**无限大平板**：掠射太阳下 1/sy → ∞，
//  而真实的雾带是有限宽的（阳光从侧面出来了），无限板会把日出日落时的雾压黑 ——
//  那恰好是雾最该好看的时刻。所以放大倍数必须有上限（_VistaFogExtinct.w），
//  这个上限是个观感参数，不是物理常数，写死在代码里会变成一个说不清来历的魔数。
//
//  ---- 它只作用在雾自己身上，不作用在物体表面 ----
//  这一项进的是 VistaEvaluateScatterSample 里雾的散射源项。**不透明物表面**收到的
//  太阳光走的是另一条链路：#12 把大气透射率折进了 Light.color × intensity
//  （CPU 侧 VistaSunTransmittance），那里没有、也不会有雾的这一项。
//  所以浓雾里地面收到的直射光不会被雾衰减，只有相机到地面这一段会被
//  AP / 雾体的 transmittance 衰减。
//  这是 UE5 与 HDRP 同样的取舍（它们的雾对直射光的遮挡只有阴影贴图那一份），
//  写在这里是因为它长得很像一个 bug：#18 接上雾之后"浓雾里地面偏亮"是**预期行为**，
//  不要去 VistaSunTransmittance 里找错。真要补，正确的位置是给平行光加一份
//  雾的垂直柱密度衰减，而那会让同一个量出现第二份实现（CPU 一份、shader 一份），
//  必须像 #12 那样用 ratio 形式让 CPU 项代数上约掉才行 —— 那是独立的一件事。
//
//  ---- 一个已知的坏配置 ----
//  标高 → ∞（均匀雾）时 H 无界，τ 也无界，结果是雾在阳光下变全黑。
//  这在物理上是对的（无限厚的均匀雾里确实没有直射光），但它是个**授权错误**，
//  不是代码错误。这里保留这个行为并写在这儿，是因为"全黑"是能一眼看见的症状；
//  如果改成静默钳一个标高上限，症状会退化成"浓雾偏暗一点"，反而查不出来。
//  CPU 侧（#18）在标高不是有限正数时应当直接把 _VistaFogHeight.w 置 0。
//
//  这一项在 #27 的验收里必须有一条判据点名跑过它，否则就该删掉 ——
//  一个默认关闭、又没有判据覆盖的开关，等于一段永远不会被发现写错的代码。
// ----------------------------------------------------------------------------
float3 VistaFogTransmittanceToSun(VistaFogSample fog, float sunDirWorldY)
{
    if (_VistaFogHeight.w < 0.5)
        return 1.0;

    // 标高换成 km：σ_t 是 1/km，而 _VistaFogHeight.y 是 1/m。
    float scaleHeightKm = 0.001 * rcp(max(_VistaFogHeight.y, 1e-6));

    // sy <= 0（太阳在地平线下）时放大倍数直接钉到上限。
    // 那种情况下大气的 Transmittance LUT 已经把直射光压到接近 0，这里不需要再区分。
    float amplify = min(rcp(max(sunDirWorldY, 1e-3)), _VistaFogExtinct.w);

    float3 tau = _VistaFogExtinct.xyz * (scaleHeightKm * fog.density * amplify);
    return exp(-tau);
}

#endif // VISTA_FOG_MEDIUM_INCLUDED
