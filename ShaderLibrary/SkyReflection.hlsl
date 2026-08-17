#ifndef VISTA_SKY_REFLECTION_INCLUDED
#define VISTA_SKY_REFLECTION_INCLUDED

// 天空镜面反射 cubemap 的**约定层**。
//
// 这个文件里只有"URP 会怎么采这张图"这一件事，没有任何积分逻辑（那在
// Shaders/Atmosphere/SkyReflection.compute 里，因为它要同时看见两种辐射来源）。
// 单独拆出来的理由：下面每一条都是**被 URP 的采样端逼死的**，不是我们的自由选择，
// 而"逼死的约定"与"我们的算法"混在一个文件里，后者的取舍会被误读成前者。
//
// ---- 为什么漫反射走 SH、镜面必须另走 cubemap ----
// SH9 是二阶带限的。Task #5a 的自检实测：日落时朝下法线上的 L2 截断误差达 31%
// （正午 2.17%）。31% 这个量级在漫反射上过完 tonemap 是可接受的色偏，
// 在镜面上就是把高光整团糊掉 —— 地平线那一圈橙色环带在 L2 里根本不存在，
// 而它恰好是湿地面 / 金属 / 水面上最显眼的那道反光。
// 所以这不是"顺手也做一个 cubemap"，是 SH 的表达力在镜面这条链路上不够用。

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
// UNITY_SPECCUBE_LOD_STEPS / PerceptualRoughnessToMipmapLevel / SampleGGXDir /
// CubemapTexelToDirection（经 Sampling.hlsl）/ GetLocalFrame（经 CommonLighting.hlsl）
// 全部来自这一个头。刻意用 URP 自己的那份而不是照抄公式：
// **建这张图的代码与采这张图的代码必须共用同一个约定**，抄一份迟早走歧，
// 而走歧的症状是"某个粗糙度区间的反射不对"，没人会怀疑到烘焙端。
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ImageBasedLighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/Hammersley.hlsl"

// ============================================================================
//  分辨率：**被 mip 级数反推出来的，不是挑的**
//
//  URP 的 GlossyEnvironmentReflection（GlobalIllumination.hlsl:464）走
//      mip = PerceptualRoughnessToMipmapLevel(perceptualRoughness)
//  而那个函数（ImageBasedLighting.hlsl:27-37）的单参重载把 maxMipLevel 固定成
//      UNITY_SPECCUBE_LOD_STEPS = 6
//  于是 pr ∈ [0,1] 会映到 mip ∈ [0,6]，**必须存在 mip 0..6 共 7 级**。
//  少一级（32² 只有 6 级）时 pr=1 会采到不存在的 mip，硬件 clamp 回最后一级，
//  表现为"粗糙度 0.8 以上的反射不再继续变模糊" —— 一个只在高粗糙度材质上
//  才看得见、且很容易被当成"就该这样"的错。
//  7 级 → 边长 64（64,32,16,8,4,2,1）。这就是 64² 的全部理由。
// ============================================================================

#define VISTA_SKY_REFLECTION_MIPS (UNITY_SPECCUBE_LOD_STEPS + 1)
#define VISTA_SKY_REFLECTION_SIZE (1 << UNITY_SPECCUBE_LOD_STEPS)

/// 辐射来源。运行期切换（不是编译期宏），Demo 视频要在同一帧里 A/B 两条路径。
#define VISTA_SKY_REFLECTION_SRC_LUT 0u
#define VISTA_SKY_REFLECTION_SRC_SH  1u

// ============================================================================
//  mip -> 感知粗糙度：URP 那条映射的**精确反解**
//
//      mip = pr·(1.7 − 0.7·pr)·M ,  M = UNITY_SPECCUBE_LOD_STEPS
//  =>  0.7·pr² − 1.7·pr + mip/M = 0
//  =>  pr = (1.7 − sqrt(2.89 − 2.8·mip/M)) / 1.4        （取负根，正根 > 1）
//
//  校验两端：mip=0 -> (1.7−1.7)/1.4 = 0；mip=M -> (1.7−0.3)/1.4 = 1。
//
//  ---- 为什么不能图省事写 pr = mip/M ----
//  URP 那条 pr·(1.7−0.7·pr) 是刻意的非线性（低粗糙度段压缩、高段拉伸）。
//  线性猜在 mip3 上给 0.5，精确值是 **0.342**，差 46%。
//  症状：中等粗糙度的金属 / 湿地面反射比该有的糊一档，而两端（镜面与全漫）
//  完全正常 —— 于是看起来像"美术把 smoothness 调低了"，
//  是那种能在项目里活到上线的错。这也是本文件必须与采样端共用同一个头的原因。
// ============================================================================
float VistaMipToPerceptualRoughness(float mip)
{
    const float m = (float)UNITY_SPECCUBE_LOD_STEPS;
    // 判别式在 mip ∈ [0, M] 内恒为正（最小值 0.09）；夹一次只为防调用方传越界的 mip。
    float disc = max(2.89 - 2.8 * mip / m, 0.0);
    return saturate((1.7 - sqrt(disc)) / 1.4);
}

// ============================================================================
//  每级 mip 的采样数
//
//  与直觉相反地**随 mip 上升**：GGX lobe 的宽度随粗糙度单调增长（mip6 是 α=1
//  的半球级 lobe），而纹素数每级降 4 倍。两者相乘的结果是总开销由最细的几级
//  主导，所以在最粗的几级上加采样几乎不要钱。
//
//  实际开销（PC 档）：
//      mip1  6×1024 纹素 ×  32 =  196k
//      mip2  6× 256      ×  64 =   98k
//      mip3  6×  64      × 128 =   49k
//      mip4~6                   =   32k
//      合计 ≈ 376k 次 SkyView LUT 采样（一次双线性取样，不是 raymarch）
//  另加 mip0 的 6×4096 = 24.6k 次直接取样。总计 ≈ 0.4M，实测远在 0.1 ms 内。
//
//  mip0 只取 1 个样本不是近似：α=0 时 SampleGGXDir 的 cosθ = sqrt((1−u)/(1−u)) ≡ 1，
//  即 H ≡ N、L ≡ R，所有样本返回同一个方向。所以 1 个样本就是**精确**的镜面值。
//
//  移动端固定 16：那条路的输入是 SH9，被带限在 l ≤ 2，与任何 lobe 的卷积都是
//  低阶光滑函数，蒙特卡洛收敛极快。这个数字的依据是**输入的带限**，
//  不是"移动端凑合一下" —— 换句话说加到 128 也不会更准。
// ============================================================================
uint VistaSkyReflectionSampleCount(uint mip, uint source)
{
    if (mip == 0u)
        return 1u;
    if (source == VISTA_SKY_REFLECTION_SRC_SH)
        return 16u;
    return min(256u, 16u << mip);
}

/// 纹素 -> 世界方向。走 URP 的 CubemapTexelToDirection（Sampling.hlsl:100）而不是
/// 自己列六个面的基：cube 的面序与 v 轴朝向是平台约定，写错的表现是某几个面
/// 上下翻转 / 旋转 90°，在天空这种大面积平滑内容上**不一定一眼看得出**。
/// 用引擎自己那份，再让自检把"写进去 -> 硬件采出来"整条 round-trip 验一遍
/// （见 SkyReflectionVerify 核），就完全不依赖我对约定的记忆是否正确。
float3 VistaSkyReflectionTexelToDirection(uint2 texel, uint size, uint face)
{
    float2 nvc = (texel + 0.5) / (float)size * 2.0 - 1.0;
    return CubemapTexelToDirection(nvc, face);
}

#endif // VISTA_SKY_REFLECTION_INCLUDED
