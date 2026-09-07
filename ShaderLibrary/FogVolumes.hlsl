#ifndef VISTA_FOG_VOLUMES_INCLUDED
#define VISTA_FOG_VOLUMES_INCLUDED

// ============================================================================
//  局部雾体（Step 3 #24）
//
//  给定一个世界空间采样点，把所有覆盖它的局部体（盒 / 椭球）的介质贡献
//  加进 VistaFogSample —— σ_t 与 σ_s 各加各的，其余字段不动。
//
//  ------------------------------------------------------------------ 为什么是内联枚举
//  三条业内路线：HDRP 的独立密度体素化 pass（为 3D 遮罩纹理与上百个体准备）、
//  UE5 的 Volume 域材质逐切片光栅化（为「任意材质当雾用」准备）、
//  Frostbite 的粒子 splat（为上千个粒子级小体准备）。Vista 的场景条件是
//  外景 + 少量解析体（N ≤ 16），三家的前提一个都不满足 —— 而内联枚举有一条
//  它们都给不了的性质：**介质求值只有一个入口**（VistaFroxelSampleAt 里那一次
//  取雾），太阳 / 天光 / #23 的局部灯三条光路吃的是同一份 fog 采样，
//  局部体的浓度对三者自动一致。独立密度 RT 则是「解析高度雾 + RT 里的局部体」
//  两份真相，还多一张 3D RT 的带宽往返。
//
//  ------------------------------------------------------------------ 只进近层，不进 AP
//  HDRP 的 Local Volumetric Fog 只写 VBuffer，UE5 的体素化只写体积雾视锥体 ——
//  几十米尺度的体放进 km 级的 AP LUT（32³）必然混叠成一团脏色。Vista 沿用：
//  局部体只存在于近层 froxel 体内。**已知语义**：超出 froxel 远边界的部分
//  不参与任何计算，一个骑在边界上的体会在交接处被切开 —— 布景时体不要压在
//  远边界上（报表会印出当前远边界）。
//
//  ------------------------------------------------------------------ 组合语义
//    σ_t += σ_vol · w · n        （w = 边缘衰减 ∈ [0,1]，n = 噪声调制 ∈ [1−amt, 1]）
//    σ_s += 上式 · albedo_vol
//  重叠的体**相加**（HDRP 同语义）。加法可交换 ⇒ 上传顺序不影响结果
//  （fp32 求和次序的末位差除外），所以 CPU 侧不需要排序保证。
//
//  三个**不**动的字段，各有一条写下来的理由：
//    · phaseG —— 全局。HDRP 的局部体不带各向异性、UE5 的相位 g 挂在
//      ExponentialHeightFog 上也是全局的；逐点混相位要按 σ_s 加权、且会让
//      #25 的统一采样函数多背一个逐 froxel 变量，业内两家都没付这个钱。
//    · density —— 太阳方向自遮蔽是**无限大平板**的闭式解（FogMedium.hlsl），
//      一个盒子装不进那个模型。局部体不喂 density ⇒ 自遮蔽只看全局雾。
//      已知近似：浓雾团自己不挡自己的阳光，症状是雾团顶部比物理值略亮。
//    · 高度剖面 —— 体内密度是均匀 × 噪声，不再乘全局的 exp 剖面。
//      体本身就是「这里例外」的表达，再叠全局剖面会让「把体抬高密度就变淡」
//      成为一个美术查不出来的行为。
//
//  ------------------------------------------------------------------ 包含关系
//  与 FroxelLocalLights.hlsl 同一条纪律：本文件自足，不依赖调用点的 include 顺序。
#include "Packages/com.kkiej.vista/ShaderLibrary/FogMedium.hlsl"
// VistaFroxelHash3（PCG3D）。噪声的格点哈希**复用**抖动那一份 ——
// 「同一个量的第二份实现连 8 行的辅助函数也算」。
#include "Packages/com.kkiej.vista/ShaderLibrary/FroxelVolume.hlsl"

// ----------------------------------------------------------------------------
//  常量与 cbuffer
//
//  上限 16：与 C# 侧 VistaFogVolumeSet.k_MaxVolumes 耦合，⓿ 类的容量对账
//  由报表判据 (a) 的数量守恒承担。固定数组走 cbuffer（SetGlobalVectorArray），
//  与 URP 自己的 zbin/tile 同风格；16 × 9 = 144 个 float4 = 2.3 KB，
//  远在 D3D11 的 64 KB cbuffer 上限之下。
//
//  零态 = 失能：_VistaFogVolCount.x = 0 ⇒ 循环体一次都不进，
//  其余数组无论装着什么都不会被读 —— 「忘了下发」与「明确没有体」殊途同归。
// ----------------------------------------------------------------------------
#define VISTA_FOG_VOL_MAX 16

CBUFFER_START(VistaFogVolCB)
    float4 _VistaFogVolCount;        // x = 体数（≤ 16），yzw 保留
    // worldToLocal 的三行（.w 为平移）。局部空间：体占据 |x|,|y|,|z| ≤ 1（盒）
    // 或 |p| ≤ 1（椭球）—— 半尺寸与旋转都折在矩阵里，shader 侧不再区分。
    float4 _VistaFogVolRow0[VISTA_FOG_VOL_MAX];
    float4 _VistaFogVolRow1[VISTA_FOG_VOL_MAX];
    float4 _VistaFogVolRow2[VISTA_FOG_VOL_MAX];
    // xyz = 逐轴 rcp(blendLocal)（椭球只用 .x，径向），w = 形状（0 盒 / 1 椭球）
    float4 _VistaFogVolBlend[VISTA_FOG_VOL_MAX];
    // rgb = 单次散射反照率，w = σ_t (1/km)
    float4 _VistaFogVolMedium[VISTA_FOG_VOL_MAX];
    // xyz = 噪声域偏移（噪声格单位，CPU 侧已按周期回绕），w = rcp(噪声尺度) (1/m)
    float4 _VistaFogVolNoise[VISTA_FOG_VOL_MAX];
    // x = 噪声调制量 ∈ [0,1]（0 = 不采噪声），yzw 保留（未来的逐体相位等）
    float4 _VistaFogVolParams[VISTA_FOG_VOL_MAX];
    // 判据探针的尺子输入（#24 判据 (b)(c)(d)）：CPU 算好的体中心与体外一点。
    // 是尺子输入不是载荷 —— GPU 从 worldToLocal 反推中心得再实现一遍求逆，
    // 而「参考值要用另一条独立路径造」正是这两个点存在的理由。
    float4 _VistaFogVolProbeC[VISTA_FOG_VOL_MAX];   // xyz = 中心（世界 m）
    float4 _VistaFogVolProbeO[VISTA_FOG_VOL_MAX];   // xyz = 体外点（世界 m）
CBUFFER_END

// ----------------------------------------------------------------------------
//  噪声：两阶 value noise
//
//  格点哈希 = VistaFroxelHash3（PCG3D，跨驱动逐位稳定），右移 8 位再转 float
//  （与抖动那条「先移 8 再除 2^24」同一个约定 —— fp32 尾数装不下 32 位，
//  直接除 2^32 会让低 8 位静默丢失且丢法依赖舍入模式）。
//  两阶固定振幅 2/3 + 1/3，第二阶频率 ×2.02（避开整倍频的格点重合）。
//  选 value noise 而不是 gradient/simplex：这里要的只是低频的密度起伏，
//  value noise 的 8 次哈希比 gradient 的 8 次哈希 + 8 次点积便宜一半，
//  而它的方向性伪影在 |三线性 + 两阶叠加| 之后低于雾的 fp16 量化。
// ----------------------------------------------------------------------------
float VistaFogVolHash01(int3 cell)
{
    return (VistaFroxelHash3(asuint(cell)).x >> 8u) * (1.0 / 16777216.0);
}

float VistaFogVolValueNoise(float3 p)
{
    float3 f = floor(p);
    float3 t = p - f;
    t = t * t * (3.0 - 2.0 * t);          // smoothstep 插值，C1 连续
    int3 c = (int3)f;

    float n000 = VistaFogVolHash01(c + int3(0, 0, 0));
    float n100 = VistaFogVolHash01(c + int3(1, 0, 0));
    float n010 = VistaFogVolHash01(c + int3(0, 1, 0));
    float n110 = VistaFogVolHash01(c + int3(1, 1, 0));
    float n001 = VistaFogVolHash01(c + int3(0, 0, 1));
    float n101 = VistaFogVolHash01(c + int3(1, 0, 1));
    float n011 = VistaFogVolHash01(c + int3(0, 1, 1));
    float n111 = VistaFogVolHash01(c + int3(1, 1, 1));

    return lerp(lerp(lerp(n000, n100, t.x), lerp(n010, n110, t.x), t.y),
                lerp(lerp(n001, n101, t.x), lerp(n011, n111, t.x), t.y), t.z);
}

// ----------------------------------------------------------------------------
//  单个体的求值。判据探针要能**隔离**每个体（体外零泄漏那条判据在两个体
//  重叠时只能对单个体成立），所以逐体函数是公开入口、循环只是套壳 ——
//  探针与线上走的是同一份逐体实现。
// ----------------------------------------------------------------------------

// 边缘权重 ∈ [0,1]。体中心恒为 1：blendLocal 在 CPU 侧被夹到 ≤ 1，
// 于是中心处 (1 − 0) · rcpBlend ≥ 1，saturate 后精确等于 1 ——
// 判据(b) 的门（中心权重 == 1e6 定点）就押在这条构造性质上。
float VistaFogVolumeWeightAt(uint i, float3 posWS)
{
    float3 p = float3(dot(_VistaFogVolRow0[i], float4(posWS, 1.0)),
                      dot(_VistaFogVolRow1[i], float4(posWS, 1.0)),
                      dot(_VistaFogVolRow2[i], float4(posWS, 1.0)));

    float4 blend = _VistaFogVolBlend[i];
    if (blend.w > 0.5)   // 椭球：径向衰减
        return saturate((1.0 - length(p)) * blend.x);

    // 盒：逐轴线性衰减的积（HDRP 的 fade 同为逐轴线性）
    float3 w3 = saturate((1.0 - abs(p)) * blend.xyz);
    return w3.x * w3.y * w3.z;
}

// 该体在该点贡献的 σ_t (1/km)。权重为 0 时**不采噪声**（16 次哈希省下来，
// 也让「体外恒零」成为构造性质而不是数值巧合 —— 判据(d) 押在这上面）。
float VistaFogVolumeSigmaAt(uint i, float3 posWS)
{
    float w = VistaFogVolumeWeightAt(i, posWS);
    if (w <= 0.0)
        return 0.0;

    float amount = _VistaFogVolParams[i].x;
    float n = 1.0;
    if (amount > 0.0)
    {
        float4 nz = _VistaFogVolNoise[i];
        float3 q  = posWS * nz.w + nz.xyz;
        float  v  = 0.66667 * VistaFogVolValueNoise(q)
                  + 0.33333 * VistaFogVolValueNoise(q * 2.02 + 17.31);
        n = 1.0 - amount * v;   // ∈ [1 − amount, 1]，期望 1 − amount/2
    }

    return _VistaFogVolMedium[i].w * w * n;
}

// ----------------------------------------------------------------------------
//  应用到 VistaFogSample。挂载点是 VistaFroxelSampleAt 里
//  VistaSampleFogAlongRay 之后、VistaEvaluateScatterSample 之前 ——
//  σ 在进能量计算前就合流，下游（太阳项、σ_s 分量、#23 逐灯重算相位）
//  拿到的就是合成后的介质，一行都不用改。
// ----------------------------------------------------------------------------
void VistaApplyFogVolumes(float3 posWS, inout VistaFogSample fog)
{
    uint count = min((uint)_VistaFogVolCount.x, (uint)VISTA_FOG_VOL_MAX);
    for (uint i = 0u; i < count; ++i)
    {
        float sigmaT = VistaFogVolumeSigmaAt(i, posWS);
        if (sigmaT <= 0.0)
            continue;
        fog.extinction += sigmaT;
        // saturate 与全局雾的反照率同一条纪律：σ_s > σ_t 在数据上不可表示
        fog.scattering += sigmaT * saturate(_VistaFogVolMedium[i].rgb);
    }
}

// 判据探针的辅助：该点全部体贡献的 σ_t 之和（不含全局雾）。
// 它调用的是**线上同一份**逐体函数，不是第二份实现 —— 存在的理由是
// 探针要在不减掉全局雾的情况下单独量「体加了多少」。
float VistaFogVolumesSigmaAt(float3 posWS)
{
    uint count = min((uint)_VistaFogVolCount.x, (uint)VISTA_FOG_VOL_MAX);
    float sum = 0.0;
    for (uint i = 0u; i < count; ++i)
        sum += VistaFogVolumeSigmaAt(i, posWS);
    return sum;
}

#endif // VISTA_FOG_VOLUMES_INCLUDED
