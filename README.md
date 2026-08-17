# Vista Rendering

面向 URP 的大场景户外渲染模块集。Unity 6000.4 / URP 17.4，RenderGraph 路径。

## 归属声明

本 package 内所有代码由 kkiej 编写。参考实现与论文见各模块目录下的 `NOTES.md` 与
仓库根目录的 `CHANGELOG.md`；参考但未复制代码的项目：
Unity HDRP `PhysicallyBasedSky`、`IllusionRP`（阅读参考，未引入）。

## 模块

| 模块 | 状态 | 主要参考 |
|---|---|---|
| Atmosphere（物理大气散射 LUT） | 规划中 | Hillaire, EGSR 2020 |
| TimeOfDay（时间轴驱动系统） | 规划中 | — |
| VolumetricFog（froxel 体积雾 + 光轴） | 规划中 | Wronski 2014 / Hillaire 2016 |
| GlobalIllumination（PRT probe GI） | 规划中 | Sloan et al. 2002 |
| Vegetation（GPU 驱动植被） | 规划中 | Haar & Aaltonen 2015 |
| Terrain（height-blend 地形材质） | 规划中 | Mishkinis |
| Water（Gerstner 波） | 规划中 | — |
| Weather（潮湿系统） | 规划中 | — |

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
Documentation~/     不被 Unity 导入的文档（波浪号后缀）
```

Shader 引用统一由 `Runtime/Core/VistaRuntimeResources.cs` 通过 `ResourcePath` 声明，
不使用 `Resources/`（package 内不可用），也不需要在 Inspector 手动赋值。

HLSL 引用路径示例：

```hlsl
#include "Packages/com.kkiej.vista/ShaderLibrary/Atmosphere.hlsl"
```
