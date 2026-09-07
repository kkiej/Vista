#ifndef VISTA_FROXEL_LOCAL_LIGHTS_INCLUDED
#define VISTA_FROXEL_LOCAL_LIGHTS_INCLUDED

// ============================================================================
//  局部灯（点光 / 聚光 / 副平行光）参与介质散射（Step 3 #23）
//
//  太阳之外的灯也要在雾里留下光锥。这个文件只做一件事：给定一个 froxel 采样点，
//  把所有影响它的局部灯的**散射源项**加出来：
//
//      J_local = Σ_i [ σ_s,R·P_R(cosθ_i)
//                    + σ_s,Mie·P_HG(g_mie, cosθ_i)
//                    + σ_s,fog·P_HG(g_fog, cosθ_i) ] · E_i
//
//      cosθ_i = dot(rayDir, L_i)     L_i = 指向灯的单位向量（与 sunDir 同约定：
//                                    正对着灯看时前向散射 = 1）
//      E_i    = light.color · light.distanceAttenuation · light.shadowAttenuation · scale
//
//  与太阳那一项（VistaEvaluateScatterSample）的差别只有两处：入射照度换成了逐灯的
//  E_i，相位角换成了逐灯的 cosθ_i。三个组分的 σ_s 与两个 g **完全复用**太阳那一份，
//  所以「这一点的介质是什么」在整个注入核里只有一份真相。
//
//  ------------------------------------------------------------------ 为什么三个组分都算
//  UE5 与 HDRP 的体积雾只让 punctual 灯散射在**雾**上（大气那两个组分只吃太阳）。
//  这里三个都算，理由是**系数已经在手里**，不多一次介质采样：
//  VistaScatterSample 已经把 σ_s,R / σ_s,Mie 逐组分带出来了（见那个结构体的注释）。
//
//  代价是零，而收益是「晴空里没有灯光锥」变成**物理的结论**而不是一行硬编码的省略：
//    · 地面附近 σ_s,R ≈ 5.8e-3 /km、σ_s,Mie ≈ 4.0e-3 /km；
//    · 雾（能见度 200 m）σ_t = 3912/200 = 19.56 /km —— 强 ~2000 倍；
//    · 近层体只有 64 m 长 ⇒ 晴空段的光学深度 ~6.4e-4。
//  所以晴空下灯的光锥本来就看不见，这是算出来的，不是抹掉的。
//  美术若问「为什么关了雾就没有光柱」，答案有物理量可以摆。
//
//  ------------------------------------------------------------------ 单位约定（必须知道）
//  URP **没有** punctual 灯的物理单位 —— 那是 HDRP 才有的东西
//  （grep 过 UniversalAdditionalLightData.cs，没有任何 LightUnit / Candela）。
//  URP 侧只有 `_AdditionalLightsColor.rgb = color.linear * intensity`，
//  而着色时乘的是 DistanceAttenuation ≈ 1/d²。于是 intensity 在量纲上**就是坎德拉**，
//  只是没有标定过。
//
//  Vista 的选择：**把 URP 的 intensity 直接当 cd 用**，全局缩放默认 1.0，
//  只留一个逃生口（localLightIntensityScale）。这与 HDRP 的做法一致。
//  直接后果，也是**物理正确**而不是 bug：
//    · Vista 的太阳是 120000 lux、曝光 EV100 15 ⇒ Unity 默认强度 1 的点光
//      在 1 m 处只有 ~1 lux 的量级，白天**完全看不见**；
//    · 夜景 EV100 ≈ 5 时它才浮出来。
//  美术必须按真实值填：60 W 白炽灯 ≈ 800 lm ≈ 64 cd，路灯 ≈ 1e4 cd。
//  这一条要写在文档里，否则「灯在白天不发光」会被当成接线坏了。
//
//  ------------------------------------------------------------------ 剔除结构：复用 URP 的 cluster
//  两档，运行时可切（VistaVolumetricFogSettings.LocalLightMode）：
//    · Cluster            —— 走 URP 自己的 ClusterInit/ClusterNext（出货档）
//    · BruteForceReference —— 遍历全部局部灯（**参考解**，不是备选方案）
//  参考解的身份是给判据用的：cluster 给出的灯集合必须是「真正影响该 froxel 的
//  灯集合」的**超集**。这是一条**定义性**的判据，不比辐亮度 —— 两条路径的求和次序
//  不同，fp32 下不可能逐位相同，拿辐亮度做门会得到一个永远差一点的假失败。
//
//  为什么不自己写一套剔除：URP 每帧已经把 zbin/tile 位图算好并下发了
//  （ForwardLights.cs:498-513 全部走 cmd.SetGlobal*，**进命令流**，所以 compute 读得到 ——
//  正好与 #22b 那个 Shader.SetGlobalTexture 的坑相反）。再写一套就是
//  「同一个量的第二份实现」，而且它的失败模式是「某些灯在雾里少了」，
//  在低频的雾上几乎看不出来。
//
//  ------------------------------------------------------------------ 两档都依赖 cluster 变体
//  BruteForceReference 也要 `_CLUSTER_LIGHT_LOOP`：局部灯的**总数**只有
//  `URP_FP_PROBES_BEGIN` 这一个可靠来源（= 局部灯数，铁证见 Clustering.hlsl:84 与
//  GlobalIllumination.hlsl:297）。经典 Forward 下的 `GetAdditionalLightsCount()` 读的是
//  `unity_LightData.y` —— 一个**逐 draw** 的全局，compute 里根本不存在；
//  而 `_AdditionalLightsCount.x` 是**逐物体上限**（ForwardLights.cs:731 传的是
//  maxPerObjectAdditionalLightsCount），不是场景里的灯数。
//  所以 Forward+ 关掉时两档都失效，且是**显式**失效（判据(a) 的两层哨兵）。
//  经典 Forward 的移动端路径推到 #26，那里是 CPU 挑 N 盏最近的灯逐灯解析注入。
//
//  ------------------------------------------------------------------ 相机全局的隐患与代理位置
//  ClusterInit 的 zbin 下标依赖**三个 Unity 内建全局**：
//    GetCameraPositionWS() = _WorldSpaceCameraPos
//    GetViewForwardDir()   = -UNITY_MATRIX_V[2].xyz（unity_MatrixV）
//    IsPerspectiveProjection() = (unity_OrthoParams.w == 0)
//  而 grep 过 URP 与 core 两个包的**全部** .compute：**没有任何一个**碰过这三个全局，
//  也没有任何一个 include 过 Clustering.hlsl / RealtimeLights.hlsl。
//  也就是说 URP 的 cluster 循环从来没有在 compute 里跑过，Vista 是第一个 ——
//  「这三个全局在 compute dispatch 时是不是 Vista 这台相机的」只能**实测**，不能假设。
//
//  解法不是去赌，也不是把 zbin 算术抄一遍（那是「同一个量的第二份实现」）。
//  喂给 ClusterInit 一个**代理位置**：
//
//      viewZ' = max(dot(vistaFwd, posWS − vistaCam), cameraNear)     ← Vista 自己的真相
//      p'     = GetCameraPositionWS() + viewZ' · GetViewForwardDir()
//
//  于是 URP 自己那行 `dot(gFwd, p' − gCam)` 精确等于 `viewZ' · |gFwd|²`，
//  **与这三个全局属于哪台相机无关**。正确性条件从「三个全局都得对」塌缩成一条：
//
//      |GetViewForwardDir()|² == 1
//
//  这是一个一行的 GPU 哨兵（判据(b)）。相机**位置**对不上则变成无害的 ⓘ ——
//  它只从 viewZ 这一条路进去，而 viewZ 已经被换成 Vista 自己算的了。
//  另一条哨兵盯 unity_OrthoParams.w：一台残留的正交相机会让 ClusterInit 走线性 zbin
//  而 URP 建的是 log zbin —— 症状是灯落进错的 bin，画面上只是「有些灯在雾里没了」。
//
//  tile 下标那一路不需要代理：ClusterInit 的 uv 是**我们传进去的**，
//  而 Vista 的 froxel XY 就是屏幕 uv。
//
//  ------------------------------------------------------------------ 近裁剪面那个静默钳位
//  URP 的 zbin 从 log2(near) 起编号（ForwardLights.cs:259-260：
//  `zBinScale = maxZBinWords / ((log2(far) − log2(near))·(2+wordsPerTile))`、
//  `zBinOffset = −log2(near)·zBinScale`）⇒ **z = near 处下标恰好 0**。
//  froxel 的第 0 片求值点是 0.5·d_0 ≈ 0.156 m（分段 0 从相机而不是从 near 起算），
//  比相机近裁剪面 0.30 m **更近** ⇒ 下标为负 ⇒ `(uint)` 转换 + `min(·, last)`
//  会把它送到**最后一个 bin** ⇒ 近处的灯静默消失。
//
//  所以上面的 max(·, cameraNear) 不是保险丝，是**每帧都会生效**的那一步。
//  它是一次**保守放宽**：把比 near 更近的采样点归到第 0 个 bin，
//  那个 bin 装的是与最近一层 slab 相交的全部灯 —— 仍然是超集，判据(d) 不会因此变红。
//  判据(c) 反过来要求这条守卫的命中数 **> 0**：一条本轮无法失败的守卫等于没写。
//
//  远端（viewZ > 相机远裁剪面）不设门，只作 ⓘ：那是 URP **有意**钳到最后一个 bin 的
//  行为，而且 Vista 的近层体只有 64 m，远远短于相机远裁剪面。
// ============================================================================

// ---------------------------------------------------------------------------- 包含关系
// 这个文件是**自足**的，不依赖调用点的 include 顺序。
//
// URP 的 RealtimeLights.hlsl 必须排第一：它自己 include 了 Core.hlsl（进而 core 的
// Common.hlsl），而下面两个 Vista 头都要用 CBUFFER_START / TEXTURE3D 之类的宏。
// 它同时把 Input.hlsl（URP_FP_* 解码宏、_AdditionalLights* 数组）、Shadows.hlsl
// （AdditionalLightShadow）、Clustering.hlsl（ClusterInit/ClusterNext）、
// LightCookie.hlsl、AmbientOcclusion.hlsl 一并带进来。
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"
// IsMatchingLightLayer（CommonLighting.hlsl:550，无条件定义、自带 include 守卫）。
//
// 【更正】上一行原本写的是「这一行就是 #23 需要的全部引擎侧」—— 那句话是错的，
// 打开 _LIGHT_LAYERS 变体之后才暴露：RealtimeLights.hlsl 的 include 链上**没有**
// CommonLighting.hlsl（grep 过整个 URP 包，一处引用都没有）。URP 自己能用到这个函数
// 是靠 Lighting.hlsl → BRDF.hlsl → ImageBasedLighting.hlsl → CommonLighting.hlsl
// 这条**偶然**的链，而那条链会把整套 BRDF 拖进来 —— 一个注入核不需要。
// 所以这里直接 include 函数真正住的那个文件，而不是去 include Lighting.hlsl。
//
// 它为什么必须放在这里而不是 VolumetricFog.compute：本文件声明了自己是自足的
// （上面第一行注释），而 _LIGHT_LAYERS 是 VistaLocalLightPasses 的内部实现细节。
// 放到调用点等于「这个头能不能编译取决于调用者开了哪个关键字」。
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonLighting.hlsl"
// 相位函数（VistaRayleighPhase / VistaHenyeyGreensteinPhase）。它自己带 AtmosphereDef + FogMedium。
#include "Packages/com.kkiej.vista/ShaderLibrary/AtmosphereScattering.hlsl"
// _VistaFroxelCameraWS / _VistaFroxelViewForward。
#include "Packages/com.kkiej.vista/ShaderLibrary/FroxelVolume.hlsl"

// ----------------------------------------------------------------------------
//  常量
//
//  失能态：全零 ⇒ mode = 0 = Off ⇒ 整个累加返回 0，逐位等于没有局部灯。
//  与 VistaFogCB / VistaFroxelCB 同一条「关掉 = 零态」的约定。
// ----------------------------------------------------------------------------
CBUFFER_START(VistaFroxelLocalLightCB)
    // x: 档位（见下面的 VISTA_LL_MODE_* 枚举）
    // y: 全局强度缩放（cd 的标定逃生口，默认 1）
    // z: 是否查 additional shadow atlas（1 = 查，0 = 恒为 1）
    // w: 相机远裁剪面（m）。只被远端 ⓘ 诊断用；为 0 时那一格自己让路（见下面的守卫）。
    float4 _VistaFroxelLocalLight;

    // 渲染层遮罩，**拆成两个 16 位**放在 x/y 上。
    //
    // 为什么不像 URP 那样 asfloat 整个 uint：URP 那边是
    // `_AdditionalLightsLayerMasks[i] = math.asfloat(mask)`，一个 float **数组**，
    // 走 SetGlobalFloatArray，Unity 不会去动它的位。而这里要走 SetGlobalVector
    // （Vector4）：asfloat(1) 是**非正规数** 1.4e-45、asfloat(0xFFFFFFFF) 是 **NaN** ——
    // 前者可能被 flush 到 0，后者在任何一次比较里都是雷。
    // 16 位一半在 float 里是精确整数，拼回去逐位无损。
    float4 _VistaFroxelLocalLightMask;
CBUFFER_END

#define VISTA_LL_SCALE       _VistaFroxelLocalLight.y
#define VISTA_LL_SHADOWS     (_VistaFroxelLocalLight.z > 0.5)
#define VISTA_LL_CAMERA_FAR  _VistaFroxelLocalLight.w

// 档位。与 C# 侧 VistaVolumetricFogSettings.LocalLightMode 逐值对应。
// 用命名常量而不是散在代码里的 0/1/2：一个报表或分支里的魔数等于没有印。
#define VISTA_LL_MODE_OFF      0u
#define VISTA_LL_MODE_CLUSTER  1u
#define VISTA_LL_MODE_BRUTE    2u

// 交给 ClusterInit 的 uv 上界留的余量。1 − 1e-6 在 fp32 里是精确可表示的，
// 而 tile 数最大也就几百，乘完之后落在最后一个 tile 内部（不会等于 tile 数）。
#define VISTA_LL_UV_EPS 1e-6

// cbuffer 里的档位。float → uint 走一次显式舍入：
// max(·, 0) 是因为负数转 uint 在 HLSL 里是未定义的，而 cbuffer 未绑定时读到的是 0（= Off）。
uint VistaLocalLightMode()
{
    return (uint)(max(_VistaFroxelLocalLight.x, 0.0) + 0.5);
}

// 渲染层遮罩：两个 16 位拼回 32 位。CPU 侧保证两半都是 [0, 65535] 的精确整数。
uint VistaLocalLightLayerMask()
{
    return (uint)max(_VistaFroxelLocalLightMask.x, 0.0)
         | ((uint)max(_VistaFroxelLocalLightMask.y, 0.0) << 16);
}

// ----------------------------------------------------------------------------
//  逐灯的介质参数
//
//  单独一个结构体而不是六个参数：调用点（VistaFroxelInject）已经把它们全部算好了，
//  而累加函数与判据探针**两个**消费者要拿到同一组东西。
//  σ_s 三份 + 两个 g 全部来自已有的采样，这里不做任何介质求值 ——
//  这个文件里一次 VistaSampleMedium / VistaSampleFogAlongRay 都没有，是刻意的。
// ----------------------------------------------------------------------------
struct VistaLocalLightMedium
{
    float3 scatteringRayleigh;  // σ_s,R   (1/km)
    float3 scatteringMie;       // σ_s,Mie (1/km)
    float3 scatteringFog;       // σ_s,fog (1/km)
    float  mieG;                // 大气 Mie 的 HG g
    float  fogG;                // 雾的 HG g
};

// 从已有的两次采样里装配。放在这里而不是让两个消费者各写一遍字段搬运：
// 「哪个 σ_s 对哪一份、哪个 g 对哪一个组分」正是那条「介质只有一份真相」要守的缝，
// 抄第二遍的失败模式是**把 fogG 用在了大气 Mie 上** —— 两个 g 都在 0.6~0.8 量级，
// 画面上只是光锥稍微紧了或松了，几乎不可归因。
VistaLocalLightMedium VistaLocalLightMediumFrom(VistaScatterSample smp, VistaFogSample fog)
{
    VistaLocalLightMedium med;
    med.scatteringRayleigh = smp.scatteringRayleigh;
    med.scatteringMie      = smp.scatteringMie;
    med.scatteringFog      = fog.scattering;
    med.mieG               = _VistaMieExtinct.w;   // 与 VistaEvaluateScatterSample 同一个来源
    med.fogG               = fog.phaseG;
    return med;
}

// ----------------------------------------------------------------------------
//  诊断
//
//  只被探针核消费；注入核传一个自己丢掉的实例，编译器会把这些写全部消掉。
//
//  两张位图而不是一张，因为判据(d) 的超集比较要**两个不同含义**的集合：
//    enumerated —— 这一档「枚举到」的灯（cluster 档就是 cluster 给出的集合）；
//    affecting  —— 枚举到的灯里 distanceAttenuation > 0 的那些（= 真正影响这一点）。
//  判据(d)：`brute.affecting ⊄ cluster.enumerated` 的个数必须为 0。
//  注意 affecting **不看**渲染层遮罩 —— 那是 Vista 自己的美术过滤，两档里逐字一致，
//  把它算进去会让这条判据从「剔除对不对」变成「过滤对不对」，冲淡了它的指向性。
//
//  位图用 uint4×2 而不是 uint[8]：数组要动态下标 ⇒ indexable temp ⇒
//  就算被 DCE 掉也可能在注入核里留下 scratch。八个三目选择是纯 ALU。
// ----------------------------------------------------------------------------
struct VistaLocalLightDiag
{
    uint enumerated;    // 枚举到的灯数（含被层遮罩挡掉的）
    uint accumulated;   // 真正累加进去的灯数
    uint shadowed;      // shadowAttenuation < 0.999 的灯数
    uint layerSkipped;  // 被渲染层遮罩挡掉的灯数
    uint nearClamped;   // viewZ 被 max(·, cameraNear) 抬起来过（判据(c) 要它 > 0）
    uint farClamped;    // viewZ > 相机远裁剪面（只作 ⓘ）
    // 钳前/钳后的 viewZ 本身。放在 diag 里而不是让探针核自己再算一遍：
    // 那一行 dot(fwd, posWS − camWS) 写两遍就是「同一个量的第二份实现」，
    // 而它的失败模式最阴 —— 探针印出来的 viewZ 与累加器实际用来查 zbin 的
    // 不是同一个数，于是判据(c) 会用一个正确的读数去为一个错误的路径背书。
    // 失能态（!USE_CLUSTER_LIGHT_LOOP 的桩）留 0，由判据(a) 负责归因。
    float viewZraw;
    float viewZ;
    uint4 enumLo;       // 枚举位图，下标 0..127
    uint4 enumHi;       // 下标 128..255
    uint4 affectLo;     // 影响位图，下标 0..127
    uint4 affectHi;     // 下标 128..255
};

VistaLocalLightDiag VistaLocalLightDiagInit()
{
    VistaLocalLightDiag d;
    d.enumerated   = 0u;
    d.accumulated  = 0u;
    d.shadowed     = 0u;
    d.layerSkipped = 0u;
    d.nearClamped  = 0u;
    d.farClamped   = 0u;
    d.viewZraw     = 0.0;
    d.viewZ        = 0.0;
    d.enumLo       = uint4(0u, 0u, 0u, 0u);
    d.enumHi       = uint4(0u, 0u, 0u, 0u);
    d.affectLo     = uint4(0u, 0u, 0u, 0u);
    d.affectHi     = uint4(0u, 0u, 0u, 0u);
    return d;
}

void VistaLocalLightBitSet(inout uint4 lo, inout uint4 hi, uint index)
{
    if (index >= 256u)
        return;
    uint w = index >> 5;
    uint b = 1u << (index & 31u);
    lo.x |= (w == 0u) ? b : 0u;
    lo.y |= (w == 1u) ? b : 0u;
    lo.z |= (w == 2u) ? b : 0u;
    lo.w |= (w == 3u) ? b : 0u;
    hi.x |= (w == 4u) ? b : 0u;
    hi.y |= (w == 5u) ? b : 0u;
    hi.z |= (w == 6u) ? b : 0u;
    hi.w |= (w == 7u) ? b : 0u;
}

bool VistaLocalLightBitGet(uint4 lo, uint4 hi, uint index)
{
    if (index >= 256u)
        return false;
    uint w = index >> 5;
    uint b = 1u << (index & 31u);
    uint word = (w == 0u) ? lo.x : (w == 1u) ? lo.y
              : (w == 2u) ? lo.z : (w == 3u) ? lo.w
              : (w == 4u) ? hi.x : (w == 5u) ? hi.y
              : (w == 6u) ? hi.z : hi.w;
    return (word & b) != 0u;
}

// 判据(d)：brute 认定「影响这一点」的灯，cluster 必须全都给出来。
// 返回漏掉的个数（必须为 0）。
uint VistaLocalLightCullMisses(VistaLocalLightDiag cluster, VistaLocalLightDiag brute)
{
    uint misses = 0u;
    [unroll] for (uint w = 0u; w < 4u; ++w)
    {
        uint affect = (w == 0u) ? brute.affectLo.x : (w == 1u) ? brute.affectLo.y
                    : (w == 2u) ? brute.affectLo.z : brute.affectLo.w;
        uint given  = (w == 0u) ? cluster.enumLo.x : (w == 1u) ? cluster.enumLo.y
                    : (w == 2u) ? cluster.enumLo.z : cluster.enumLo.w;
        misses += countbits(affect & ~given);
    }
    [unroll] for (uint v = 0u; v < 4u; ++v)
    {
        uint affect = (v == 0u) ? brute.affectHi.x : (v == 1u) ? brute.affectHi.y
                    : (v == 2u) ? brute.affectHi.z : brute.affectHi.w;
        uint given  = (v == 0u) ? cluster.enumHi.x : (v == 1u) ? cluster.enumHi.y
                    : (v == 2u) ? cluster.enumHi.z : cluster.enumHi.w;
        misses += countbits(affect & ~given);
    }
    return misses;
}

// ----------------------------------------------------------------------------
//  单盏灯的源项
//
//  返回 σ_s·P·E（与 VistaScatterSample.scattered 同量纲、同「未乘曝光」状态）。
//  曝光由调用点统一乘 VISTA_EXPOSURE，与太阳那一项走同一次乘法。
// ----------------------------------------------------------------------------
float3 VistaLocalLightSource(Light light, float3 rayDir, VistaLocalLightMedium med)
{
    // light.direction 是**指向灯**的单位向量（GetAdditionalPerObjectLight 里
    // lightVector = lightPos − posWS·w 再归一化），与 sunDir 同约定 ⇒
    // 正对着灯看时 cosθ = 1 = 前向散射峰。若这里写成 −light.direction，
    // 症状是「光锥只在背对灯的时候亮」，而那在低频的雾上很容易被误判成 g 的符号错。
    float cosTheta = dot(rayDir, light.direction);

    float3 sigmaPhase =
          med.scatteringRayleigh * VistaRayleighPhase(cosTheta)
        + med.scatteringMie      * VistaHenyeyGreensteinPhase(med.mieG, cosTheta)
        + med.scatteringFog      * VistaHenyeyGreensteinPhase(med.fogG, cosTheta);

    // 入射照度 E（lux）= 灯色·强度（当 cd 用）· 1/d²·角衰减 · 阴影
    float3 illuminance = (float3)light.color
                       * (light.distanceAttenuation * (float)light.shadowAttenuation);

    return sigmaPhase * illuminance;
}

#if USE_CLUSTER_LIGHT_LOOP

// 取一盏局部灯。shadowMask 传 (1,1,1,1)：Vista 的核里没有声明
// CALCULATE_BAKED_SHADOWS，所以 AdditionalLightShadow 里 bakedShadow 恒为 1，
// MixRealtimeAndBakedShadows(rt, 1, fade) = lerp(rt, 1, fade) ——
// 形状与 VistaFroxelSunShadow 结尾那个 lerp(s, 1, fade) 完全一致。
//
// 两个重载的区别就是「查不查 additional shadow atlas」：
// 三参重载走 AdditionalLightShadow，两参重载里 GetAdditionalPerObjectLight
// 直接把 shadowAttenuation 置 1。运行时切档就是挑重载，不需要宏。
Light VistaGetLocalLight(uint index, float3 positionWS, bool withShadows)
{
    if (withShadows)
        return GetAdditionalLight(index, positionWS, half4(1.0h, 1.0h, 1.0h, 1.0h));
    return GetAdditionalLight(index, positionWS);
}

// 一盏灯要不要参与体积散射：渲染层遮罩。
//
// ---- 为什么是二值开关，而不是 UE5 那种连续的 VolumetricScatteringIntensity ----
// 连续旋钮要一个**逐灯**的数组，而下标必须与 URP 的 `_AdditionalLights*` 打包序
// 对齐。那个序是 ForwardLights.cs:697-724 那个 `if (mainLight != i) lightIter++`
// 的循环决定的 —— 在 CPU 侧复现它就是「同一个引擎内部不变量的第二份实现」，
// 失败模式是**旋钮作用到了别的灯上**，静默且几乎无法归因。
// 而 `_AdditionalLightsLayerMasks` 是 URP **自己**按同一个下标下发的，
// 拿的就是手里已有的 index：零映射、零新组件、零新数组、零漂移。
// 这满足了「局部灯的体积贡献要能逐灯关掉」这条需求；连续调光推迟，理由就是上面这段。
bool VistaLocalLightPasses(Light light, uint layerMask)
{
#ifdef _LIGHT_LAYERS
    // IsMatchingLightLayer 来自 core 的 CommonLighting.hlsl:550。
    // 只在 _LIGHT_LAYERS 变体里编译：`_AdditionalLightsLayerMasks` 只在
    // lightData.supportsLightLayers 为真时才被下发（ForwardLights.cs:624/654/727），
    // 关键字关掉时那个数组**根本没上传**，而 GetAdditionalPerObjectLight 仍然会
    // 无条件 asuint 读它一格（RealtimeLights.hlsl:151）—— 也就是 light.layerMask
    // 在关键字关掉时装的是别的相机留下的脏值。所以过滤必须跟着关键字一起消失。
    // 这条守卫在关键字关掉的一档是**未覆盖**的，判据(e) 会点名它。
    return IsMatchingLightLayer(light.layerMask, layerMask);
#else
    return true;
#endif
}

// 一盏灯的累加 + 记账。三个循环共用，避免同一段逐灯逻辑写三遍。
void VistaLocalLightTally(
    uint index, float3 positionWS, float3 rayDir, VistaLocalLightMedium med,
    bool withShadows, uint layerMask, inout float3 result, inout VistaLocalLightDiag diag)
{
    Light light = VistaGetLocalLight(index, positionWS, withShadows);

    diag.enumerated += 1u;
    VistaLocalLightBitSet(diag.enumLo, diag.enumHi, index);
    if (light.distanceAttenuation > 0.0)
        VistaLocalLightBitSet(diag.affectLo, diag.affectHi, index);

    if (!VistaLocalLightPasses(light, layerMask))
    {
        diag.layerSkipped += 1u;
        return;
    }

    result += VistaLocalLightSource(light, rayDir, med);
    diag.accumulated += 1u;
    if (light.shadowAttenuation < 0.999h)
        diag.shadowed += 1u;
}

// ----------------------------------------------------------------------------
//  累加
//
//  mode 是**参数**而不是直接读 cbuffer：判据(d) 的探针要在**同一趟 dispatch 里**
//  把两档都跑一遍再比集合。如果档位藏在 cbuffer 里读，探针就只能测「当前那一档」，
//  而那条超集判据也就不存在了。线上路径由下面那个薄封装 VistaAccumulateLocalLights
//  读 cbuffer，两者的逐灯逻辑是同一份（VistaLocalLightTally）。
//
//  三个循环，各自的存在理由：
//   1. 副平行光：URP 自己的 cluster 路径也是**单独**一个 [0, DIR_COUNT) 的循环
//      （Lighting.hlsl:332 与 :430），因为平行光不进 zbin/tile 位图。
//      不镜像它的症状是「月亮在雾里没了」—— 一盏本来该有的灯静默缺席。
//   2. cluster：出货档。
//   3. 暴力：参考解，遍历 [0, URP_FP_PROBES_BEGIN)。
//  1 在两档里都跑（它与剔除结构无关），2/3 二选一。
// ----------------------------------------------------------------------------
float3 VistaAccumulateLocalLightsMode(
    uint mode, float2 normalizedScreenUV, float3 positionWS, float3 rayDir,
    VistaLocalLightMedium med, inout VistaLocalLightDiag diag)
{
    if (mode == VISTA_LL_MODE_OFF)
        return 0.0;

    // Forward+ 的数据没下发时 URP_FP_WORDS_PER_TILE 为 0 ⇒ 位图全 0 ⇒
    // 一盏局部灯都不会被枚举。这里**不**提前 return：判据(a) 要靠探针把这个 0
    // 读出来并显式失败，提前 return 会让「Forward+ 关着」与「场景里真没灯」长得一样。
    bool  withShadows = VISTA_LL_SHADOWS;
    uint  layerMask   = VistaLocalLightLayerMask();
    float3 result     = 0.0;

    // ---- 1. 副平行光（主光已由太阳那一项处理，且不在 _AdditionalLights* 里）----
    uint dirCount = min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, (uint)MAX_VISIBLE_LIGHTS);
    [loop] for (uint di = 0u; di < dirCount; ++di)
        VistaLocalLightTally(di, positionWS, rayDir, med, withShadows, layerMask, result, diag);

    // ---- viewZ 与代理位置（见文件头「代理位置」与「近裁剪面」两段）----
    float cameraNear = _VistaFroxelViewForward.w;
    float viewZraw   = dot(_VistaFroxelViewForward.xyz, positionWS - _VistaFroxelCameraWS.xyz);
    float viewZ      = max(viewZraw, cameraNear);
    diag.viewZraw    = viewZraw;
    diag.viewZ       = viewZ;
    if (viewZraw < cameraNear)
        diag.nearClamped += 1u;
    // 远端只作 ⓘ。cameraFar 为 0（cbuffer 没绑）时这一格自己让路，而不是
    // 把每一个采样点都算成「超出远裁剪面」—— 一个读数在失能态下满格，
    // 会让报表上「真的越界了」与「这一格没接线」长得一样。
    if (VISTA_LL_CAMERA_FAR > 0.0 && viewZraw > VISTA_LL_CAMERA_FAR)
        diag.farClamped += 1u;

    float3 proxyPos = GetCameraPositionWS() + viewZ * GetViewForwardDir();

    if (mode == VISTA_LL_MODE_BRUTE)
    {
        // ---- 3. 暴力参考解 ----
        // URP_FP_PROBES_BEGIN == 局部灯数（Clustering.hlsl:84 / GlobalIllumination.hlsl:297：
        // cluster 实体空间里 [0, PROBES_BEGIN) 是灯、其后是反射探针）。
        // 加 dirCount 是因为 LIGHT_LOOP_BEGIN 里那句 `lightIndex += DIR_COUNT` ——
        // 实体下标 0 对应 _AdditionalLights* 里第一盏**局部**灯，
        // 也就是说局部灯在数组里排在副平行光**之后**。
        uint localCount = min(URP_FP_PROBES_BEGIN, (uint)MAX_VISIBLE_LIGHTS);
        [loop] for (uint bi = 0u; bi < localCount; ++bi)
        {
            uint index = bi + dirCount;
            if (index >= (uint)MAX_VISIBLE_LIGHTS)
                break;
            VistaLocalLightTally(index, positionWS, rayDir, med, withShadows, layerMask, result, diag);
        }
        return result * VISTA_LL_SCALE;
    }

    // ---- 2. cluster（出货档）----
    // uv 夹到 [0, 1) 再交给 ClusterInit。ClusterInit 里那行是
    // `uint2 tileCoord = uint2(uv * URP_FP_TILE_SCALE)` —— 一个**无符号**转换：
    // uv 为负会绕成天文数字下标，读到 cbuffer 界外（D3D11 上返回 0），
    // 症状是画面边缘那一圈 froxel「一盏局部灯都没有」，而且只在抖动幅度被调大之后出现。
    //
    // 正常档位下这个夹紧不会生效（横向抖动幅度 ≤ 1 时 pixelCenter ∈ [id, id+1)
    // ⇒ uv ∈ [0, 1) 已经成立），它是给「幅度被脚本改到 > 1」那一档兜底的。
    // 这与 VistaFroxelInject 里「横向抖动**不夹紧**」不矛盾：那里夹紧会改变
    // 采样点本身（把边缘期望往画面内拉）；这里改的只是**去哪个 tile 取灯列表**，
    // 而取到相邻 tile 仍然是一个超集 —— 判据(d) 不会因此变红。
    float2 clusterUv = clamp(normalizedScreenUV, 0.0, 1.0 - VISTA_LL_UV_EPS);

    ClusterIterator it = ClusterInit(clusterUv, proxyPos, /*headerIndex*/ 0);
    uint entityIndex;
    [loop] while (ClusterNext(it, entityIndex))
    {
        uint index = entityIndex + dirCount;   // 与 LIGHT_LOOP_BEGIN 一致
        if (index >= (uint)MAX_VISIBLE_LIGHTS)
            continue;
        VistaLocalLightTally(index, positionWS, rayDir, med, withShadows, layerMask, result, diag);
    }

    return result * VISTA_LL_SCALE;
}

#else // !USE_CLUSTER_LIGHT_LOOP

// 经典 Forward 变体：编译得出来，但恒返回 0。
// 见文件头「两档都依赖 cluster 变体」—— 这里**不能**回落到一个自己数灯的循环，
// 那个数会是错的（`unity_LightData.y` 在 compute 里不存在，
// `_AdditionalLightsCount.x` 是逐物体上限而不是场景灯数）。
// 「一个默认关闭、又没有判据覆盖的开关等于永远不会被发现写错的代码」，
// 所以判据(a) 会把「当前跑的是哪个变体」印出来，并在选错时红。
float3 VistaAccumulateLocalLightsMode(
    uint mode, float2 normalizedScreenUV, float3 positionWS, float3 rayDir,
    VistaLocalLightMedium med, inout VistaLocalLightDiag diag)
{
    return 0.0;
}

#endif // USE_CLUSTER_LIGHT_LOOP

// 线上路径：档位取自 cbuffer。注入核用这一个。
float3 VistaAccumulateLocalLights(
    float2 normalizedScreenUV, float3 positionWS, float3 rayDir,
    VistaLocalLightMedium med, inout VistaLocalLightDiag diag)
{
    return VistaAccumulateLocalLightsMode(
        VistaLocalLightMode(), normalizedScreenUV, positionWS, rayDir, med, diag);
}

#endif // VISTA_FROXEL_LOCAL_LIGHTS_INCLUDED
