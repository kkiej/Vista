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

耗时（RTX 3060 / D3D11）。两套口径量的**不是同一件事**，所以两套都给：

```
模型 A｜Play 模式 RenderGraph 逐 pass marker，帧内延迟（唯一算进 barrier / pass 边界的口径）
  稳态五 pass  0.206 ms（中位 0.212；300 帧逐帧取最小）　目标 0.300 ms　→ 达标
    Sky Reflection 0.093　Sky-View 0.036　Aerial Perspective 0.036　Refl Copy 0.031　Ambient SH 0.010
  静态两表      300 帧 0 个样本 → 脏标记生效（这两行**有**样本才是 bug：白烧 0.044 ms/帧）

模型 B｜Edit 模式立即提交、N 次背靠背摊销，吞吐下界（相邻 dispatch 允许重叠）
  稳态五 pass  0.170~0.198 ms（两次复测，各 ±2~4%）
    改形状之前  0.494 ms（±3%）　其中 Sky Reflection 占 79%
    「只绑不派」  0.001 ms　→ 开销 ~99% 在 GPU 积分侧，不在命令提交侧

A(单次)/B = 1.21　→ 方向符合预期：帧内延迟不可能低于允许重叠的吞吐下界，
差额就是 pass 边界、barrier、无重叠这三样的代价。
```

反射 pass 曾占 79%，归因到"7 趟逐 mip dispatch 里，粗糙那几级每组只有一条线程
在跑 256 次取样"——占用率问题，不是取样总量问题（mip3~6 只产出 1.6% 的纹素却吃掉
85% 的时间）。修法是加一个 64 线程协作核，门槛定在 `K ≥ 64`（每条 lane 至少一个样本）。

**哪个数字能引用，取决于口径**：模型 B 改完之后反射单 pass 落到 0.08~0.10 ms，
复测离散度 ±5%~±132%，已在这台机器的噪声地板下，逐 pass 只能读占比；
同一个 pass 在模型 A 里却是 0.093 ms（min 0.186 / max 0.202，±9%）——
300 个真实帧、不做固定开销扣减，在 0.09 ms 这个量级比"5×200 轮摊销再减 0.161 ms"稳得多。
所以逐 pass 绝对值只从模型 A 引，模型 B 只用来读占比和当回归基线。

当前瓶颈换成了 7 趟 dispatch 的固定开销（约占反射 pass 的 59%），合并 dispatch 是
已识别但**主动搁置**的下一步 —— 整链已在预算内，为 ~0.03 ms 引入 4 个同时绑定的 UAV
不划算。这里刻意报"不可引用"的项：这套计时器的用途是当回归基线，不是出宣传数据。

两条必须跟数字一起给的限制：① RenderGraph 的逐 pass marker 被
`#if DEVELOPMENT_BUILD || UNITY_EDITOR` 包着（core `RenderGraph.cs:2868-2884`），
**Release 构建里这些 marker 不存在**，模型 A 是 Editor 口径，不是发行版性能；
② Editor 里 Scene View 与 Game View 各渲染一次，marker 值是两者之和，
上面的"单次"是按出现次数除出来的 —— 合法性来自"LUT 尺寸全是定值、与相机无关"，
自检报告会把这个前提连同出现次数一起打出来。

## 安装

两种方式服务的是不同的人，都列在这里。

**A. 使用者 / 评审 —— 钉版本的 git URL（推荐从这条开始）**

在目标工程的 `Packages/manifest.json` 中加入：

```json
"com.kkiej.vista": "https://github.com/kkiej/Vista.git#main"
```

不关心磁盘布局，克隆下来即可跑。生产工程应把 `#main` 换成一个 tag 或 commit
哈希 —— 不钉版本时 UPM 只在**首次解析**那一刻取默认分支的 tip，
之后靠 `packages-lock.json` 冻住，于是「同一份 manifest 在两台机器上装到不同的包」
是可能的（一台已有 lock、一台没有）。

**B. 开发 Vista 本身 —— 本地路径**

```json
"com.kkiej.vista": "file:../../Vista"
```

路径相对 `Packages/` 目录，所以这一行要求 Vista 与目标工程是**兄弟目录**。

为什么开发期必须是 B 而不是 A：git URL 引入的包会被 UPM 克隆进
`Library/PackageCache/`，那份拷贝是**只读**的。于是改一行 shader 的成本从
「存盘」变成「commit → push → 改 manifest 里的哈希 → 重解析」。
`file:` 引入的包在 Package Manager 里显示为本地包，改动存盘即触发重编译。

代价是 `file:` **不可复现**：它指向工作区当前状态，包括未提交的改动。
所以只在开发 Vista 的工程里用 B，其余一律用 A。

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
