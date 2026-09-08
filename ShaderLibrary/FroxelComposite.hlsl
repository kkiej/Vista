#ifndef VISTA_FROXEL_COMPOSITE_INCLUDED
#define VISTA_FROXEL_COMPOSITE_INCLUDED

// ============================================================================
//  近层积分表的采样（Step 3 #25）
//
//  #21 把近层的 (∫σ_s·J·T ds, 1 − T) 沿深度累积进了一张 3D 表，但直到这一步
//  之前**没有任何消费者** —— 这个文件就是那个消费者的一半，另一半是
//  AerialPerspective.hlsl 里的合成。
//
//  ------------------------------------------------------------------ 为什么单独一个文件
//  它要被两处包含：AerialPerspective.hlsl（线上合成）与自检。而
//  AerialPerspective.hlsl 属于大气模块、FroxelVolume.hlsl 属于体积雾模块 ——
//  把 TEXTURE3D 声明塞进任何一边，都会让另一个模块的 shader 为了一个
//  自己不用的资源多背一条依赖。夹在中间的这一层是两者唯一的交集。
//
//  ------------------------------------------------------------------ 存的是什么
//    rgb = 从相机到该深度累积的内散射，**已乘 VISTA_EXPOSURE**（fp16 不饱和，
//          与 AP 表同一条约定，所以两者可以直接相加，中间不需要任何换算）
//    a   = 1 − T（不是 T）。清空态因此是「透传」而不是「全黑」——
//          表没被写的那一帧画面只是没有近层雾，不是一片死黑。
//
//  ------------------------------------------------------------------ 已知近似：T 是灰度
//  #21 的积分把 σ_t collapse 成了标量，所以近层的透射率没有颜色，
//  而 AP 那张表是彩色的（远景的暖色恰恰是靠彩色 T 保住的，见
//  AerialPerspective.hlsl 的「彩色 vs 灰度透射率」）。
//  这在近层是可接受的，理由是可以算的：近层只有 [0, D]（默认 48 m），
//  地表 Rayleigh 蓝/红消光差 27.3e-3 /km ⇒ 48 m 上两通道的 T 相差
//  1 − exp(−27.3e-3 × 0.048) = **0.13%**，在 fp16 的量化之下。
//  真正会有色的是雾自己的 σ_t（美术可以把它调成任意颜色），
//  那一项目前被灰度化了 —— 记为已知偏差，触发条件是「雾的 σ_t 三通道差异大
//  且近层很厚」，归 #27 量化。
// ============================================================================

// FroxelVolume.hlsl 只有数学与 cbuffer（没有任何资源声明），所以包含它是廉价的。
// 本文件自足，不依赖调用点的 include 顺序 —— 与 FogVolumes.hlsl 同一条纪律。
// GlobalSamplers.hlsl 给的是 sampler_LinearClamp：它有自己的 include guard，
// 与 AtmosphereScattering.hlsl 那份重复包含是无害的。写在这里而不是靠调用方，
// 正是「自足」的含义 —— 否则本文件能不能编译取决于谁先被 include。
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
#include "Packages/com.kkiej.vista/ShaderLibrary/FroxelVolume.hlsl"

TEXTURE3D(_VistaFroxelIntegral);

// ----------------------------------------------------------------------------
//  近层的开关与近端淡入
//
//  x  > 0.5 = 本帧有近层积分表可读（零态 = 没有，_VistaFroxelIntegral 可能
//             绑的是上一帧的、甚至是引擎的默认白 —— 所以必须先看这一位再采样）
//  y  1 / 第一片存储距离 (1/m)：近端淡入用
//  zw 保留
//
//  为什么这一位不能省掉、改成「看 _VistaFroxelSize.z 是不是 0」：
//  那个 cbuffer 由 froxel 的 Prepare 在**记录期**推，而合成 pass 在执行期跑；
//  froxel 这一帧没排入时，Prepare 也没跑，cbuffer 里留着的是上一帧的合法值。
//  「上一帧的合法值」正是 _VistaApConsumer 踩过的那个坑。
// ----------------------------------------------------------------------------
CBUFFER_START(VistaFroxelCompositeCB)
    float4 _VistaFroxelComposite;
CBUFFER_END

bool VistaNearVolumeEnabled()
{
    return _VistaFroxelComposite.x > 0.5;
}

// ----------------------------------------------------------------------------
//  采样
//
//  ---- w 坐标为什么可以直接用 VistaFroxelEncodeDistance ----
//  切片映射是纯指数、且**没有半片偏置**（FroxelVolume.hlsl 里那条刻意的选择）：
//  e = log(d/near)/log(far/near) 直接就是纹理的 w。于是
//  d = VistaFroxelStoredDistance(s) 时 e 精确落在 (s+0.5)/N，即纹素中心。
//  「编码函数就是纹理坐标」是这条映射唯一存在的理由，也是它不能被改成
//  含半片偏置的版本的原因。
//
//  ---- 两端 ----
//  · d 超过体的远端（e > 1）：clamp 到最后一个纹素 = [0, handoff] 的**全部**累积。
//    这正是「背景物体要吃满整层近雾」，不是截断。
//  · d 小于第一片存储距离（e < 0.5/N）：clamp 会给出 [0, d_0] 的累积，
//    对一个更近的物体是多算的。按 d/d_0 线性淡入 —— 与 AP 的 nearFade
//    是同一条处理（AerialPerspective.hlsl 里 slice 0 存 [0, near] 的那个问题），
//    连写法都保持一致，好让「两层的近端各自怎么收尾」在两个文件里对得上。
// ----------------------------------------------------------------------------
void VistaSampleNearVolume(float2 screenUv, float distanceMeters,
                           out float3 inScatter, out float3 transmittance)
{
    float e = saturate(VistaFroxelEncodeDistance(distanceMeters));
    float4 v = SAMPLE_TEXTURE3D_LOD(_VistaFroxelIntegral, sampler_LinearClamp,
                                    float3(screenUv, e), 0);

    float nearFade = saturate(distanceMeters * _VistaFroxelComposite.y);

    inScatter    = v.rgb * nearFade;
    // 表里存的是 1 − T，这里还原成 T。淡入作用在 1 − T 上而不是 T 上：
    // 要淡的是「雾做了多少事」，而 T 的中性值是 1 不是 0。
    transmittance = 1.0 - v.a * nearFade;
}

#endif // VISTA_FROXEL_COMPOSITE_INCLUDED
