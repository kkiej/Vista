# Vista Rendering

面向 URP 的大场景户外渲染模块集。Unity 6000.4 / URP 17.4，RenderGraph 路径。

## 归属声明

本 package 内所有代码由 kkiej 编写。每个取舍的理由、参考的论文、以及踩过的坑
都记在仓库根目录的 [CHANGELOG.md](CHANGELOG.md) 里；参考但**未复制代码**的项目：
Unity HDRP `PhysicallyBasedSky`、`IllusionRP`（阅读参考，未引入工程）。

## 模块

| 模块 | 状态 | 主要参考 |
|---|---|---|
| Atmosphere（物理大气散射 LUT） | **可用** | Hillaire, EGSR 2020 |
| TimeOfDay（时间轴驱动系统） | 规划中 | — |
| VolumetricFog（froxel 体积雾 + 光轴） | 规划中 | Wronski 2014 / Hillaire 2016 |
| GlobalIllumination（PRT probe GI） | 规划中 | Sloan et al. 2002 |
| Vegetation（GPU 驱动植被） | 规划中 | Haar & Aaltonen 2015 |
| Terrain（height-blend 地形材质） | 规划中 | Mishkinis |
| Water（Gerstner 波） | 规划中 | — |
| Weather（潮湿系统） | 规划中 | — |

## Atmosphere

Hillaire 2020 的四表结构，加两条给 URP 供光的链路，共七个 RenderGraph pass
（稳态五个 —— 两张静态表只在参数变化时重算）：

| pass | 产出 | 规格 | 频率 |
|---|---|---|---|
| Transmittance | 视线透过率 | 256×64 | 静态 |
| Multi-Scattering | 多次散射项 | 32×32 | 静态 |
| Sky-View | 天顶方向天空亮度 | 192×108 | 每帧 |
| Sky Ambient SH | L2 环境光探针 | SH9 buffer | 每帧 |
| Sky Reflection (+Copy) | 镜面反射 cubemap | 64²×7 mip | 每帧 |
| Aerial Perspective | 空气透视 froxel | 32³ | 每帧 |

工作单位是**绝对光度单位**，全链路只有一处曝光换算（`EV100 = 15`）。
环境光 SH 与反射 cubemap 直接接 Unity 的 `SphericalHarmonicsL2` /
`RenderSettings.customReflectionTexture`，**不改任何材质 shader** 即可让
URP 的 Lit 走这套天空照明。

### 验收

数值正确性由 GPU 侧判据 + Editor 菜单驱动，判据一律写在 compute 里，
C# 只负责摆参数、读回、判阈值。`Window/Vista/` 下三个 `Validate`（LUT / 环境光 SH /
天空反射）判数学，两个 `Log ... State` 判运行期链路有没有真的接通 ——
立即模式全绿而运行期没接通是完全可能的，只看自检报告看不出来。
判据的设计理由与实测数据在 [CHANGELOG.md](CHANGELOG.md)。

耗时（RTX 3060 / D3D11 / **Editor 立即模式摊销计时**，非帧内延迟）：

```
稳态五 pass 整链  0.170~0.198 ms（两次复测，各 ±2~4%）　目标 0.300 ms　→ 达标
  改形状之前      0.494 ms（±3%）　其中 Sky Reflection 占 79%
  「只绑不派」对照  0.001 ms　→ 开销 ~99% 在 GPU 积分侧，不在命令提交侧
```

反射 pass 曾占 79%，归因到"7 趟逐 mip dispatch 里，粗糙那几级每组只有一条线程
在跑 256 次取样"——占用率问题，不是取样总量问题（mip3~6 只产出 1.6% 的纹素却吃掉
85% 的时间）。修法是加一个 64 线程协作核，门槛定在 `K ≥ 64`（每条 lane 至少一个样本）。

**只引用整链数字**：改完之后反射单 pass 落到 0.08~0.10 ms，复测离散度 ±5%~±132%，
已在这台机器的噪声地板下，逐 pass 只能读占比。当前瓶颈换成了 7 趟 dispatch 的
固定开销（约占反射 pass 的 59%），合并 dispatch 是已识别但**主动搁置**的下一步 ——
整链已在预算内，为 ~0.03 ms 引入 4 个同时绑定的 UAV 不划算。
这里刻意报"不可引用"的项：这套计时器的用途是当回归基线，不是出宣传数据。

## 安装

在目标工程的 `Packages/manifest.json` 中加入（路径相对 `Packages/` 目录）：

```json
"com.kkiej.vista": "file:../../Vista"
```

## 目录约定

```
Runtime/            C# 运行时（Vista.Runtime.asmdef）
Editor/             C# 编辑器工具（Vista.Editor.asmdef）
Shaders/            .shader / .compute
ShaderLibrary/      跨模块共享的 .hlsl
```

Shader 引用统一由 `Runtime/Core/VistaRuntimeResources.cs` 通过 `ResourcePath` 声明，
不使用 `Resources/`（package 内不可用），也不需要在 Inspector 手动赋值。

HLSL 引用路径示例：

```hlsl
#include "Packages/com.kkiej.vista/ShaderLibrary/AtmosphereScattering.hlsl"
```
