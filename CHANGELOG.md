# Changelog

本文件同时充当技术 Breakdown 的时间线：每个模块落地时记录**参考来源**、**做的取舍**、
**踩的坑**、**性能数据**。作品集评审看的是这些，不是 feature 列表。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### Added

- package 骨架：`Vista.Runtime` / `Vista.Editor` asmdef，
  基于 `IRenderPipelineResources` 的 shader 资源容器（零手动赋值）。
- **大气 Transmittance LUT**（256×64，R16G16B16A16_SFloat，静态）。
  - 参考：Hillaire, *A Scalable and Production Ready Sky and Atmosphere Rendering Technique*,
    EGSR 2020；参数化沿用 Bruneton & Neyret 2008 / Bruneton 2017。
  - 介质模型：Rayleigh 指数剖面（标高 8 km，无吸收）+ Mie 指数剖面（标高 1.2 km，HG g=0.8）
    + 臭氧三角帐篷剖面（中心 25 km，半宽 15 km）。
  - Editor 自检工具 `Window/Vista/Validate Atmosphere LUTs`：读回三个闭式解已知的纹素比对。
    地面·天顶 (0.93799, 0.86523, 0.75928) vs 闭式解 (0.94040, 0.86766, 0.76235)，
    最大误差 0.00307；大气顶·天顶 = (1,1,1)；地面·切线 = (0.10681, 0.00961, 0.00005)。
- **共享 raymarch 积分器** `ShaderLibrary/AtmosphereScattering.hlsl`。
  MS LUT / SkyView LUT / AerialPerspective LUT / 体积雾 共用一个
  `VistaIntegrateScatteredLuminance`，差异全部收进 `VistaRaymarchSettings`。
  动机：单次散射的能量计算若在多处各写一遍，天空与雾会在地平线交界处出现一条缝，
  而这类不一致极难定位。
- **大气 Multi-Scattering LUT**（32×32，R16G16B16A16_SFloat，静态）。
  - 每个纹素一个线程组、组内 64 线程各跑一个球面方向（Marsaglia 均匀球面采样），
    groupshared 归约后用等比级数 `Σ = L₂ / (1 − f_ms)` 一次求出无限阶。
  - 自检：全表有限非负（最大 0.06525，量级符合各向同性相位 1/4π ≈ 0.0796）、
    沿 muSun 严格单调递增（回退 0）、太阳当顶·地面 (0.05191, 0.05573, 0.06464) 偏蓝、
    太阳在地平线下归零。
- **大气 Sky-View LUT**（192×108，R16G16B16A16_SFloat，逐帧）+ **物理天空盒**。
  - 参数化：uv.y ↔ viewZenithCos 以**地平线为分界**双段平方 warp（张角随相机高度变化，
    不是常量 π/2）；uv.x ↔ lightViewCos 平方 warp，uv.x=0 正对太阳。
  - 自检新增 15 项：正反映射 round-trip 最大 uv 误差 3.76e-4（阈值 = 半个纹素 2.6e-3）、
    两个方向的 warp 严格单调（回升 0）、四个端点 +1/−1/+1/−1。
  - 绝对亮度校验：正午天顶 (943.5, 1893, 4184) cd/m² 偏蓝、地平线比天顶亮
    12053 vs 1857、地平线被 Mie 洗白（B/R 1.19 vs 天顶 4.43）；
    **天底辐射对闭式解 `albedo·E☉·sin(elev)·T^(1/sin elev)/π` 偏差 0.4%**
    —— 这一项同时验证了绝对光度单位与曝光链路。
  - 日落（太阳 −2°）：日侧地平线 (52800, 14800, 1875) 红移 R>2B、
    Mie 前向峰日侧/背侧 21946 vs 2240、背侧中高空转蓝紫 (1031, 1259, 1692)、
    天顶比正午暗 5.5 倍。全表峰值 54304 cd/m²，未逼近 fp16 上限 65504。
    （以上数值是修完 `VistaEarthShadow` 偏置方向后的基线，比修之前整体高 6~9%，
    见「坑」里那一条；天顶值不变，因为天顶视线不经过贴地那 10 m。）
- **`VistaAtmospherePass` / `VistaAtmosphereFeature`**：RenderGraph-only 的逐帧调度，
  三张表拆成三个 compute pass（原因见「坑」）。静态表只在参数变化的帧排入。
- **`Window/Vista/Setup Sky (Material + Renderer Feature)`**：一键生成天空盒材质、
  挂到 `RenderSettings.skybox`、并把 feature 装进当前生效管线的所有 Renderer。
- **Aerial-Perspective froxel LUT**（32×32×32，R16G16B16A16_SFloat ×2，逐帧）。
  - 散射表存**累积散射亮度**、透射表存**彩色透射率**，两张分开而不是打包进一张：
    合成时是 `color * T + S`，两者的数值范围与插值行为都不同。
    散射表的 alpha 通道另存 `mean(T.rgb)` 的灰度透射率，供移动端一次采样路径用。
  - 深度切片支持 Log 与 Power(k) 两种分布，运行时可切（Task #6 的 A-B 输入）。
  - 一次行进、逐切片输出：每个线程负责一根 froxel 柱，沿视线只走一遍，
    在每个切片边界把当前累积量写出。步长 `clamp(ceil(segLen/0.25), 2, 16)`。
  - 自检新增 22 项。有限性/单调性/打包一致性：散射沿切片非减（回退 0）、
    透射率沿切片非增（回升 0）、`scatter.a` vs `mean(T.rgb)` 最大偏差 4.07e-4、
    峰值 8816 cd/m² 未逼近 fp16 上限。
  - **参数化 round-trip**：`w → 距离 → w` 最大 |Δw| 1.49e-7（Log）/ 1.19e-7（Power），
    `w = i/(depth−1)` 最大误差 4.57e-4，两端点与 `nearDistanceKm` / `maxDistanceKm` 一致，
    `texW` 半片对齐 0.01563 / 0.98438。
  - **对 256 步参考解的两项误差**，分别测两件不同的事（见下方「取舍」）：
    | 分布 | errCenter（行进循环） | errMid（切片分布） |
    | --- | --- | --- |
    | Log(near 20 m) | 1.19% @ 0.020 km | 0.31% @ 13.9 km |
    | Power(k=2) | 0.30% @ 0.133 km | 0.15% @ 23.4 km |
  - **区间/连续性回归探针**（不判定，只报数）：`tBottom`、`tTop`、相机海拔、`up·ray`、
    `earthShadow` 在 3 m 与 1 km 处的取值、以及线性探针 `ref(0.04)/ref(0.02) = 2.022`。
    这三组数是定位下方那个 24% 偏差的工具，留在自检里当回归网。
  - 灰度透射率近似的代价（移动端分级用）：中心柱最大通道偏差
    **3.8 km 6.1%，12.4 km 22.1%，32 km 71.2%**。
- **天空环境光 SH**（L2 / SH9，`StructuredBuffer<float4>` × 9，逐帧）。
  接管 Unity 的环境光链路，取代 `AmbientMode.Skybox`（后者会把太阳圆盘一起卷进去，
  见「坑」里那条 8 倍偏亮）。
  - 投影：从 **SkyView LUT** 采 **1024 个 Fibonacci 均匀球面方向**，
    一个线程组 64 线程跨步取样 + groupshared 树形归约，权重 `4π/N`。
    存的是**原始辐射亮度矩** `L_i`，余弦卷积留给消费端（理由见「取舍」）。
  - 两个出口，一份系数：**GPU** 侧 `_VistaSkyAmbientSh` 全局 buffer（零延迟，
    供 Step 3 雾与 Step 4 PRT relight 用）；**CPU** 侧 `AsyncGPUReadback` 27 个 float
    → `SphericalHarmonicsL2` + `AmbientMode.Custom`（容忍 2~3 帧延迟）。
  - `Runtime/Core/VistaSphericalHarmonics.cs`：基函数常数、Unity 槽位缩放表
    `k_RadianceToUnitySh`、以及带拒绝条件（长度不足 / DC 全非正 / 非有限值）的
    `TryConvertMomentsToProbe`。运行时与自检共用同一份，不许各抄一遍。
  - Editor 自检 `Window/Vista/Validate Ambient SH`，两节：
    - **Unity `SphericalHarmonicsL2` 约定标定**（3 项）。逐槽位置 1 实测 `Evaluate`
      的权重 `k_i` = 3.54491, 2.04665×3, 0.91529, 0.91529, 3.17066, 0.91529, 1.83058，
      `|k_i·Ŷ_i − 1|` 最大 **6.32e-8** → `Evaluate` 用的是**未归一化多项式基**
      `{1, y, z, x, xy, yz, 3z²−1, xz, x²−y²}`；`AddAmbientLight(white)` 的
      `c_0 = 1.000000` 且各向偏离 1 为 **0** → `Evaluate` 的语义是 albedo=1 的出射亮度
      （辐照度/π）；均匀天空 `L=1` 过缩放表回到 `c_0 = 1.000000`。
    - **SH 投影 vs 参考解**（4096 样本数值积分，8 个测试法线 + 2 个全天球均值组）。
      | | 正午 60° | 日落 3° |
      | --- | --- | --- |
      | `L_00` | (20247, 21893, 25385) | (2681.3, 2165.8, 2187.9) |
      | `c_0` | (5711.6, 6175.8, 7161.0) | (756.38, 610.95, 617.20) |
      | 均值恒等式偏差（GPU / CPU） | 1.36e-7 / 1.36e-7 | 8.29e-8 / 8.29e-8 |
      | GPU 重建 vs CPU 重建 | 8.15e-8 | 1.23e-7 |
      | 逐法线最大偏差（= L2 截断） | **2.17%**（天顶） | **31.25%**（朝下） |
      | 求积收敛 1024 vs 4096 | 0.01% | 2.75% |
      判定只用前三行（与截断无关），第四行在正午档判 5%、日落档只记录 ——
      理由展开在「取舍」里，那是这一节真正的设计内容。
  - Editor 诊断 `Window/Vista/Log Ambient Probe State`：不算任何东西，只报运行期
    实际状态（`ambientMode`、`c_0`、上/下/水平三向重建、场景脏标记）。
    与上面那个自检互补 —— 自检走立即模式验数学，这个验"RenderGraph 那条 pass 真的跑了、
    读回真的把系数灌进了 `RenderSettings`"。两者可以一绿一红，而只看自检报告看不出来。
    Demo 场景实测：`Custom`、`c_0 = (5120, 5551, 6458)`、
    上 (1444, 2721, 5400) / 下 (8163, 7383, 6234) / 水平 (5153, 5626, 6513)。

- **天空镜面反射 cubemap（#5b）**：64²、7 级 mip 的 Cube RenderTexture，
  挂到 `RenderSettings.customReflectionTexture`，于是 URP 的
  `GlossyEnvironmentReflection` 把它当 `unity_SpecCube0` 采 —— **不改任何材质 shader**。
  这是选"产出一张真 cubemap"而不是"塞一个自定义全局 + 改 shader"的全部理由。
  - `SkyReflection.compute` 的 `SkyReflectionFilter`：**每级 mip 直接从 SkyView LUT
    做 GGX 预积分**（Karis 2013 的 `PrefilterEnvMap` 累加，N = V = R，权重 NdotL）。
    七级各一趟 dispatch，级间零依赖。
  - 移动端同一个核，辐射来源换成 #5a 的那份 SH（`VISTA_SKY_REFLECTION_SRC_SH`），
    采样数降到 16。**仍然产出一张真 cubemap**，所以 `unity_SpecCube0` 这条路
    在两个平台上完全一致 —— 消费端不需要知道自己采的是哪一条路径来的图。
  - `mip → 感知粗糙度` 用 URP `PerceptualRoughnessToMipmapLevel` 的**解析反函数**
    `pr = (1.7 − sqrt(2.89 − 2.8·m/6)) / 1.4`，不是线性映射。
    实测七级：0 / 0.1024 / 0.2151 / **0.3424** / 0.4917 / 0.6814 / 1.0 ——
    mip3 是 0.342 而不是朴素线性的 0.5。
  - Editor 自检 `Window/Vista/Validate Sky Reflection`，三条判据全在 GPU 上算
    （C# 侧刻意不重算任何一条：重算就得再抄一遍 mip 映射与面方向约定，
    抄错的那一份与 shader 那份走歧时，报出来的偏差既不是 0 也不是明显错误）。
    Demo 场景实测：

      | | 正午 60° | 日落 3° |
      | --- | --- | --- |
      | ① 逐面 mip0 vs LUT（六面最大） | **0.00%** | **0.04%** |
      | ① 参与比较的取样点 | 64/面，空面 0/6 | 64/面，空面 0/6 |
      | ② cube 整球均值 | (5709.6, 6172.4, 7155.4) | (723.7, 602.5, 615.9) |
      | ② LUT 同方向均值 | (5711.6, 6175.8, 7161.0) | (756.4, 611.0, 617.2) |
      | ② SH 的 `L_00·Y00` | (5711.6, 6175.8, 7161.0) | (756.4, 611.0, 617.2) |
      | ② cube 离散化偏差 | **0.078%**（阈 0.5%） | **4.315%**（阈 6%） |
      | ② 跨模块偏差（LUT vs SH） | **1.36e-7** | **1.61e-7** |
      | ③ mip round-trip 最大 | 3.58e-7 | — |
      | ③ HLSL 常量 SIZE/MIPS/LOD_STEPS | 64 / 7 / 6 | — |

    判据 ② 的第三列与 #5a 的 `c_0` 逐位一致（正午 (5711.6, 6175.8, 7161.0)），
    这不是巧合而是设计：两边用**同一个** 1024 点 Fibonacci 方向集
    （`VISTA_SKY_SH_SAMPLES` == `VISTA_SKY_REFL_VERIFY_MEAN_SAMPLES`），
    所以均值恒等式在有限样本下也精确成立 —— 1.36e-7 的偏差只来自两边归约顺序不同。
  - Editor 诊断 `Window/Vista/Log Sky Reflection State`：报运行期链路的实际状态。
    Demo 场景实测 `defaultReflectionMode = Custom`、
    `VistaSkyReflectionCube_64x64_Mips_R16G16B16A16_SFloat_Cube`、64²、7 级 mip、
    `filter = Trilinear`、**`unity_SpecCube0_HDR = (1, 1, 0, 0)`**、`isDirty = False`。

### 取舍

- **单位用 km 而不是 m**。地球半径 6360 km 写成 6.36e6 后，froxel raymarch 里的 `r*r`
  会到 4e13，逼近 fp32 有效位上限（~1.7e7 整数精度），远处步进会出现明显抖动。
  世界空间(m) 只在边界处乘 `_VistaGround.w = 0.001` 转换。
- **积分采样数 40 而非 Bruneton 的 500**。这张表只在参数变化时重算，采样数不进帧开销；
  40 段梯形法的误差已经压到 3e-3，再加采样是浪费烘焙时间。
- **Transmittance 用 fp16**。值域 [0,1]，且下游只做乘法不做累加，没有精度累积。
  256×64×8B = 128 KB。
- **多次散射用各向同性近似 + 等比级数，不做完整 Monte Carlo**。完整解要在每一阶重解
  辐射传输方程，烘焙从毫秒级涨到分钟级，而差别在 1% 量级、过 tonemap 后不可见。
  UE5 的 SkyAtmosphere 与 Unity HDRP 的 PhysicallyBasedSky 用的都是这套近似——
  这是"业内主流"而非省事。代价是二阶以上散射丢失方向性，逆光时太阳附近的多次散射
  略偏暗，但被一阶的 Mie 前向峰完全盖住。
- **MS LUT 尺寸 32×32 定为算法常量，不做质量分级项**。它对 (muSun, 高度) 是极平滑的
  二维函数，128×128 与 32×32 肉眼无差别。分级项留给真正吃性能的 SkyView / AP。
- **步段内用解析积分 `S·(1−e^(−σ·dt))/σ` 而非矩形法 `S·dt`**。低步数下矩形法在光学厚的
  步段会显著高估，是带状的主因之一。步段内取样位置取 0.3 而非中点 0.5，因为密度沿
  步段指数衰减、能量重心偏近端。
- **天空走 Skybox 材质，不自己画全屏 pass**。Unity 的环境光（`AmbientMode.Skybox`）
  与反射探针都是从 skybox 材质渲一张 cubemap 卷积出来的，走材质等于免费接上这两条链路；
  自己画全屏 pass 就要把这两套都重写。代价是天空只能在 `DrawSkyboxPass` 的时机画
  （不透明之后、透明之前）—— 对我们无影响，aerial perspective 是独立 pass。
- **太阳圆盘不烘进 SkyView LUT，单独解析画**。192×108 的表上圆盘（张角 0.53°）
  占不到一个纹素，烘进去只会得到一个被双线性糊开的方块。更硬的理由是**动态范围**：
  圆盘亮度在 1e9 量级，烘进去这张表必须从 fp16 升到 fp32，显存与带宽翻倍；
  排除圆盘后全表峰值 54304，安全落在 fp16 的 65504 以内（实测值见上）。
- **SkyView 尺寸 192×108 定为质量分级项**（与 MS LUT 的 32×32 相反）。
  它逐帧重算，且地平线附近的纹素密度直接决定日落时会不会看到横向台阶。
  移动端分级降到 128×72 + SPP 8/16（Task #7 落地）。
- **`RenderPassEvent.BeforeRenderingPrePasses`**。LUT 不依赖任何屏幕空间资源，
  而下游消费者（天空盒、雾、不透明物的 aerial perspective、SH 投影）分布在整条管线上。
  排在最早处，所有下游都能无条件拿到当帧的表。
- **一个 RendererFeature 管整个大气模块，不是每个效果一个**。LUT 是共享资源，
  拆成多个 feature 会让用户配得出"开了雾但没开大气"这种必然黑屏的组合。
- **`IVistaLutDispatcher` 抹平两条录制路径，而不是把 dispatch 写两遍**。
  RenderGraph 的 `ComputeCommandBuffer.SetComputeTextureParam` 六个重载**全部只收
  `TextureHandle`**，而 Editor 自检/预览只有原生 `CommandBuffer`（只收 `RTHandle`），
  两者之间没有转换。写两遍的后果是：哪天只改了运行时那份采样数，自检还在验旧参数，
  而它照样全绿 —— 自检就从资产变成了负债。实现是 `readonly struct` + 泛型约束，
  JIT 去虚化，无装箱。
- **不走 `AddUnsafePass` + `GetNativeCommandBuffer` 这条逃生通道**（`GetNativeCommandBuffer`
  只接受 `UnsafeCommandBuffer`，所以想拿原生 cb 就必须放弃 `AddComputePass`）。
  放弃它会同时失去 `EnableAsyncCompute` 与图的依赖校验，而换来的"省一层抽象"并无收益：
  LUT 排在所有光栅工作之前，native render pass 合并对它没意义。
  这三张表本就是持久资源，所以保持 `ImportTexture` + 让图只负责 barrier / 性能剖析 / 异步调度。
- **AP 用 32×32×32 的 froxel 表，而不是在合成 shader 里逐像素 raymarch**。
  逐像素 raymarch 的画面上限更高（能吃阴影贴图、能做逐像素的深度截断），但代价是
  雾的开销随分辨率线性涨；froxel 表把它固定成 32768 个柱子，与屏幕分辨率解耦。
  这是 UE5 SkyAtmosphere 与 HDRP PhysicallyBasedSky 都采用的形态。
  代价是深度方向只有 32 片，靠非线性分布 + 三线性插值补 —— 补得够不够由 errMid 判定。
- **AP 的散射与透射分两张表，不打包成一张 RGBA**。合成式是 `color·T + S`，
  S 在 1e4 量级、T 在 [0,1]，塞进一张 RGBA 就必须牺牲其中一个的精度或色彩
  （灰度透射率）。分两张的实测代价是灰度近似在 32 km 处偏差 71.2%（见上）——
  这个数字本身就是"为什么 PC 档必须存彩色透射率"的论据。
  移动端档位仍可只采一张：散射表的 alpha 里备了灰度透射率。
- **errCenter 与 errMid 测的是两件不同的事，所以用两套不同的分母**。
  这一条是 AP 自检里最容易被写成"随便定个阈值"的地方，展开记一下：
  - errCenter = `|LUT[i] − truth(D(w_i))| / truth(D(w_i))`，**本地**相对误差。
    它比较的是"同一个位置上，AP 的一次行进循环"与"共享积分器跑 256 步"，
    测的是**代码等价性**（步数够不够、重写的循环有没有写歧）。
    代码等价性必须处处成立，所以分母用本地真值、阈值定在 5%。
  - errMid = `|(LUT[i]+LUT[i+1])/2 − truth(D(w_mid))| / LUT[depth−1]`。
    分子是三线性插值在两片中点给出的值与真值之差，测的是**切片分布够不够密**。
    分母**不能**用本地真值：Power(k=2) 的 `d ∝ w²` 在 w=0 处导数为 0，
    第 0 片在 d=0、第 1 片在 d=33 m，而中点 `w=0.5/31` 对应 d=8.3 m，
    插值给 `0.5·L(33m)`、真值约 `0.25·L(33m)` —— 本地误差恒为 **100%**，
    加多少切片都不会变，这是参数化**算术上强制**的，不是欠采样。
    而那里的绝对量只有 1.35 cd/m²，整根柱子要累积到几千，画面上并不存在这个误差。
    改用**柱子在最远片上的累积总量**作分母后，这个数直接读作
    "贴到画面上的雾量错了百分之几"，阈值也就能从"平滑渐变上多少对比度会看出带状"
    （约 1%）定成 2%，而不是拍一个比值。换分母后 argmax 从退化的第一对切片
    移到了 13.9 km / 23.4 km —— 也就是雾真正有量级的地方，这正是该被监控的位置。
  - 顺带得到 Task #6 的结论输入：等切片数下 Power(k=2) 的插值保真度优于 Log
    （0.15% vs 0.31%），但 Log 的第 0 片能落在 `nearDistanceKm`（20 m）而非 0，
    对"相机贴着地形时近处不要有雾"更友好。两者都远在阈值内，最终取哪个按画面定。
- **环境光用 L2（SH9）而不是 L1（SH4）**。判据是日落：那时天空是"天顶暗 + 地平线一圈
  橙红 + 背侧蓝紫"，L1 只有一个方向矢量，会把这一圈塌成单侧色偏 —— 朝太阳与背对太阳
  的物体拿到的环境色几乎一样，日落的方向感全丢。L2 至少能表达"水平方向亮、上下暗"
  这个二阶形状。另外 `RenderSettings.ambientProbe` 本身就是 `SphericalHarmonicsL2`，
  用 L1 是主动丢信息而不省任何东西；UE 的 SkyLight 与 HDRP 的 ambient probe 也都是 L2。
- **buffer 里存原始辐射亮度矩 `L_i`，不存已与余弦瓣卷积过的辐照度 SH**。
  消费端不止一个：漫反射要 `Â_l` 卷积（`VistaShIrradiance`），Step 3 的体积雾要的是
  **各向的入射亮度**（`VistaShRadiance`，不该乘余弦），Step 4 的 PRT relight 要与
  烘出来的可见性 SH 做逐系数点乘（也不该预卷积）。存卷积后的版本等于把三个消费者中的
  两个逼去反解 `Â_l`，而 `Â_2 = π/4` 一旦被漏乘就是 4 倍误差、且只在二阶上体现 ——
  症状是"环境光的方向对比过强"，几乎不可能被认成 bug。
- **太阳圆盘排除在 SH 之外**。这也是不用 `AmbientMode.Skybox` 的**唯一**动机
  （见「坑」：圆盘会让环境光偏亮约 8 倍）。排除是能量守恒的：圆盘的直射由 URP 的
  平行光负责，卷进环境光就是把同一份能量记两遍。
- **投影时采均匀 Fibonacci 方向，不遍历 SkyView LUT 的纹素**。逐纹素累加看着更"省"
  （表就在那儿），但 SkyView 的 uv 参数化在地平线附近是**故意加密**的（双段平方 warp），
  按纹素等权累加等于给地平线一圈几倍的权重 —— 日落时那一圈正是最亮的地方，
  结果是环境光整体偏橙，且**偏多少取决于 LUT 分辨率**：Task #7 把移动端降到 128×72
  时环境色会跟着变，而这种耦合在改分辨率的那一刻不会有任何报错。
  均匀采样把"表的存储密度"与"积分的测度"解耦。UE5 SkyAtmosphere 的 SkyLight capture
  同样是独立方向集，不复用 SkyView 的参数化。
  实现上 z 在 (−1,1) 等距（Archimedes 投影保证面积均匀）+ 黄金角 `π(3−√5)`，
  方位角先 `frac` 再乘 2π，否则高 index 处 fp32 的有效位全被整圈数吃掉。
- **一份系数、两个出口；GPU 消费者绝不走 CPU 那条路**。雾与 PRT relight 如果读
  CPU 读回的结果，环境光就会比天空慢 2~3 帧 —— 快速过渡的日落里表现为"雾的颜色追不上
  天空"，而这种延迟错位在静态截图里完全看不出来，只在动起来时暴露。
  所以 GPU 侧直接绑 buffer（同帧可用），CPU 侧那条只服务 Unity 内建的漫反射环境光
  （`RenderSettings.ambientProbe`），那里本来就有一帧以上的滞后，无所谓。
- **读回请求在记录期发起，不在 SH pass 的 execute 里**。`AsyncGPUReadback.Request`
  是 CPU 侧 API，与图无关；放在记录期最前面，它拿到的必然是"上一帧已经写完"的内容，
  时序不依赖图什么时候提交。放进 execute 反而要处理"同一帧内请求自己刚写的 buffer"，
  多一次隐式同步。代价是延迟从 2 帧变 3 帧 —— 对漫反射环境光无感。
- **`VistaLutBufferSlot` 让 dispatcher 也抹平 buffer 绑定，尽管这次两条路径的类型是兼容的**。
  与纹理那条的处境不同，值得写下来：纹理侧 `SetComputeTextureParam` 的六个重载**只收
  `TextureHandle`**，原生 `CommandBuffer` 只收 `RTHandle`，不抽象就编译不过 —— 抽象是被迫的。
  buffer 侧不是：`ComputeCommandBuffer.SetComputeBufferParam` 有直收 `GraphicsBuffer` 的
  重载，而 `BufferHandle` 又有到 `GraphicsBuffer` 的隐式转换，所以"直接把持久
  `GraphicsBuffer` 递给 dispatch"**两条路径都能编译、都能跑出正确画面**。
  它悄悄丢掉的是：图不知道这个 pass 碰了这块 buffer，于是不插 barrier、不做依赖排序、
  也不会在剖析里显示这条边。这类错误没有任何编译期或运行期信号，只在换厂商驱动或
  开异步 compute 时才炸 —— 正是必须靠约定拦住的那一类。所以 buffer 也走
  `ImportBuffer` + `UseBuffer` + slot 枚举，与纹理保持同一种写法。
- **自检的判据设计：截断误差不能当判据，均值恒等式才能**。
  这是 #5a 里唯一真正需要想的东西，展开记：
  - 表面上最自然的判据是"SH 重建 vs 高精度参考解，逐法线比"。它不行 ——
    SH9 是二阶带限的，日落天空含大量高阶成分，**必然**对不上。实测正午 ≤ 2.17%，
    日落最暗那个法线（朝下、只吃地面反弹）达 **31.25%**。把阈值定在能容下 31% 的地方，
    等于放过任何小于 31% 的缩放错误；定紧了则"日落必红"，而必红的自检会被习惯性忽略，
    等于没有。**这一项测的是 L2 的表达力，不是代码的正确性，两者不该共用一个阈值。**
  - 真正判定的三条，都与截断无关：
    ① **均值恒等式**（唯一的*精确*判据）：
       `mean_n[(1/π)∫L·max(0,n·ω)dω] = (1/π)∫L·mean_n[max(0,n·ω)]dω = (1/4π)∫L dω`，
       而 SH 侧同一个量恰好是 `L_00·Y00`（一阶以上在整球上均值为 0）。
       于是它与"L2 够不够"完全无关，只取决于 `4π/N` 权重、`Ŷ_0`、`Â_0` ——
       也就是唯一会真出 bug 的那部分。实测 **1.36e-7 / 8.29e-8**。
    ② **GPU 重建 == CPU 重建**：两条"矩 → 辐照度"的实现互不相干（GPU 走 `Â_l` 与
       归一化基；CPU 走 `k_RadianceToUnitySh` + Unity 的未归一化多项式基），
       失败模式不重叠。实测 **8.15e-8 / 1.23e-7**。
    ③ 正午档的逐法线偏差判 5%：正午天空足够平滑，截断本就只有 2.17%，
       这一档的紧阈值能抓"某一阶的 `Â_l` 缩放错了"（至少 30% 量级），
       而那种错误会**同时**污染两个太阳高度，截断误差却只在日落放大 —— 可区分。
  - 顺带给出一个设计结论的实测依据：**31% 的截断误差就是镜面反射必须另走 cubemap 的
    理由**（#5b）。这个量级在漫反射上过 tonemap 后可接受，在镜面上就是把高光糊成一团。
- **`VISTA_SKY_SH_SAMPLES` 取 1024**，依据是自检里那条"1024 vs 4096 均值偏差"：
  正午 0.01%、日落 **2.75%**。日落那 2.75% 落在 DC 项上（≈0.04 EV），且方向集是**固定的**
  （不做逐帧抖动），所以误差是太阳角度的平滑函数 —— 不会有时域闪烁，只是一个恒定的
  微小偏色。用不着为它加采样。这个数留在自检里打印，Task #7 分级时直接读。
  当前整趟只 dispatch 一个线程组（64 线程 × 16 次采样），加到 4096 就是 64 次 ——
  单 SM 上的纹理延迟会真实体现在耗时里，而收益是看不见的 0.04 EV。

- **反射走 cubemap 而不是直接用 #5a 的那份 SH**。依据是 #5a 自检里那个 31.25% ——
  L2 在朝下法线上的截断误差。这个量级在**漫反射**上过完 tonemap 可以接受
  （地面反弹本来就是低频的），在**镜面**上就是把日落地平线那圈橙红糊成一团均匀的橙，
  而反射恰恰是观众唯一能逐像素对照"这个方向的天空到底什么颜色"的地方。
  移动端也还是 cubemap（只是内容由 SH 重建），理由见下一条。
- **反射不做「渐进预滤波」（读 mip N−1 写 mip N），每级都直接从 SkyView LUT 积分。**
  渐进预滤波是这件事的标准做法（HDRP 的 `IBLFilterGGX`、Karis 2013 的实现都是），
  但它要求同一张资源在同一趟里既当 SRV 又当 UAV，而 RenderGraph 跟踪的是**整张资源**
  的状态 —— 与三张静态大气表必须拆 pass 是同一类 UB（见「坑」）。绕开只有三条路：
  ① 源 mip 也按 UAV 读（丢掉硬件双线性与跨面无缝滤波，cube 接缝处出硬边）；
  ② 两张纹理 ping-pong（HDRP 的形态，正确但显存翻倍且要在图里 `GenerateMips`）；
  ③ 拆 7 个 pass 串行（正确，但每个 pass 只有几百纹素的工作量，全是调度开销）。
  直接从 LUT 积分把这一整类问题消掉：全程纯写、只有 UAV 一个状态、零 barrier、
  七趟 dispatch 挤在一个 pass 里。而且**质量严格更好** —— 没有 box filter 的近似、
  没有跨级累积误差、mip0 是精确镜面，接缝在结构上不可能出现（每个纹素积的都是
  真实世界方向）。
  能这么做的前提是开销够低：需要滤波的纹素只有 mip1~6 共
  `6 × (1024+256+64+16+4+1) = 8190` 个，配上随 mip 上升的采样数总计约 376k 次
  LUT 双线性**取样**（不是 raymarch），加上 mip0 的 24.6k 次约 0.4M。
  （这个估算是纠正过一次的：第一版把 mip0 也算进滤波、还用了一个平的采样数，
  得出 2.1M，据此差点选了渐进预滤波 —— 改对之后架构结论就翻了。）
- **不做逐帧摊销（HDRP 会把面/mip 分帧）。** HDRP 摊销是因为它在 256² 上滤波、
  且走 42 趟光栅 pass；那个约束不迁移过来 —— 这里 mip0 只有 24576 个纹素，
  全部七级一趟做完。摊销这件事留给 Task #7 当分级旋钮（与采样数、辐射来源一起）。
- **cubemap 尺寸 64² 是被 URP 定死的，不是拍的。** `ImageBasedLighting.hlsl:16` 的
  `UNITY_SPECCUBE_LOD_STEPS = 6`，而 URP 的 `GlobalIllumination.hlsl` 用的是
  **单参**重载 `PerceptualRoughnessToMipmapLevel(pr)` —— 也就是它假设 maxMip 恰好是 6，
  即 mip 0..6 共 7 级，即边长 `1 << 6 = 64`。所以这两个常量都从
  `UNITY_SPECCUBE_LOD_STEPS` 推导（`SkyReflection.hlsl:41-42`），
  而不是各写一个字面量：URP 哪天改了那个 6，我们跟着变，而不是安静地错位一档。
  自检判据 ③ 把 HLSL 侧的实际取值报回 C# 比对，所以"两边不一致"是一条会红的断言。
- **判据 ② 的阈值按太阳高度分两档（正午 0.5% / 日落 6%），不是统一放宽到 6%。**
  实测日落 4.315%、正午 0.078%，差了 55 倍，而这个差是**物理**：
  cube 三通道分别偏暗 4.3% / 1.4% / 0.2% 且一律偏暗 —— 线性插值在凸的亮带上
  系统性下冲的签名。日落地平线那圈橙红是全天角频率最高的结构（64² 下每纹素约 1.4°），
  几乎全在红通道；蓝通道在日落时整片天空近乎平坦，所以几乎不错。
  统一取 6% 的代价是正午档从"回归探测器"退化成"形式上绿着"：真出现 1% 级别的整体
  缩放错（比如曝光被乘了两次），只有正午那一档能抓住它，日落档会被离散化误差淹掉。
  这与 #5a 日落档取 `PositiveInfinity` 的做法**故意不同** —— 那边的截断误差在特定
  法线上无界，给不出有意义的上限；这边的离散化误差是有界的，留一个真阈值它才还是断言。
  另外这 4.3% 只影响 mip0：任何有粗糙度的表面读 mip≥1，那里 GGX lobe 的张角远大于
  1.4°，离散化被滤波宽度盖掉了。
- **太阳圆盘不进反射图。** 两个理由，任一个单独就够：① URP 的平行光已经给了解析
  GGX 高光，圆盘再进反射就是重复计一次；② 圆盘亮度在 1e9 量级而 fp16 上限 65504，
  进来直接饱和成 Inf，而 Inf 会经预滤波的归一化污染整片纹素（不是一个点，是一片）。
- **GGX 预积分用各向同性近似（N = V = R），不做"更物理"的 NdotV 相关 lobe。**
  真实 GGX lobe 掠射时会拉长成各向异性，但采样端（URP 的
  `GlossyEnvironmentReflection`）用的是不带 NdotR 的单参重载 —— 也就是说
  **它本来就假设这张图是各向同性预滤波的**。跟着采样端的假设走，
  而不是把写入端做得更物理然后与它错开；后者的症状是掠射角反射整体偏暗，
  而且查起来会怀疑到积分本身。

### 坑

- `Categorization.CategoryInfo`（Graphics Settings 面板分组用的特性）是**引擎内部 API**，
  只对 Unity 官方 SRP 包开放（`InternalsVisibleTo` 白名单）。第三方 package 实现
  `IRenderPipelineResources` 时不能加这个特性，否则 CS0246。纯显示层，去掉无影响。
- LUT 的单位区间端点必须落在**纹素中心**（Bruneton 的 `0.5/size + x*(1-1/size)` 内缩），
  不做这层映射时双线性采样会把区间外的值混进来，症状是地平线附近出现台阶状色带。
  这是整套 LUT 最容易漏的精度坑，且症状延后到最终天空颜色才暴露 —— 所以先写自检工具
  比先写 RenderGraph pass 更划算。
- **HG 相位函数的 cosθ 符号**。约定 `cosTheta = dot(rayDir, sunDir)`（看向太阳为 +1）时，
  分母必须是 `1 + g² − 2g·cosθ`。写成 `+2g·cosθ` 会把前向散射峰搬到太阳的**反侧**，
  症状是光晕出现在背光方向。推导写在 `AtmosphereScattering.hlsl` 的函数注释里。
- **朝太阳的透射率不能只靠 Transmittance LUT**。太阳在地平线以下时，Bruneton 参数化会
  给出一条穿过星球本体的无意义路径，必须另做 ray-sphere 判定（`VistaEarthShadow`）归零。
- 建 MS LUT 时积分器必须关掉 `useMultiScattering`，否则自引用（读一张正在写的表）。
- `execute_code` 在本工程不可用：Roslyn 未安装，CodeDom 因引用程序集过多导致
  mono 命令行超长。验证逻辑要做成 `[MenuItem]` + `Debug.Log` 才能自动化调用。
  且多行日志经 MCP 转发只保留首行，报告需压成单行。
- **RenderGraph 只在 pass 边界插入资源状态转换**，一个 pass 内同一资源只有一个状态。
  Transmittance 以 UAV 写出 → MultiScattering 当 SRV 读 → SkyView 读前两张，
  因此**必须拆三个 pass**。挤在一个 pass 里在 D3D12 / Vulkan 上就是缺 barrier 的 UB，
  在 NV 驱动上经常"看起来是对的"，换台 AMD 机器才炸。
  立即模式（Editor 预览）没有这个问题：原生 `CommandBuffer` 的状态转换由图形层自动插入
  —— 这也是为什么先写好的立即模式自检是可信的。
- **每个 Vista pass 都要 `AllowPassCulling(false)`**。URP 自带的 `DrawSkyboxPass` 无法声明
  对我们 LUT 的读取（它不认识这些资源），图里于是没有任何消费者，整个大气模块被静默剪掉。
  症状极具误导性：天空一片黑，而 Frame Debugger 里连 pass 都找不到。
- **全局纹理统一在 SkyView pass 里发布**，不是各自在产出 pass 里发布。静态表的 pass 在
  参数不变的帧里根本不存在，那些帧也必须有全局绑定；而 `SetGlobalTextureAfterPass`
  允许一个 pass 为它**读**的资源设全局，正好用上。
- **`SetGlobalTextureAfterPass` 的第一个参数是 `in` 而不是 `ref`**。反射把 `in` 显示成
  `ref`，照着反射结果加 `ref` 会 CS1615。
- **`view.Bind` 在 execute 里写全局 cbuffer，所以 SkyView pass 必须
  `AllowGlobalStateModification(true)`**。
- **太阳方向取 `visibleLights[i].localToWorldMatrix.GetColumn(2)` 而不是 `light.transform`**：
  `VisibleLight.light` 在某些剔除路径下为 null。
- **环境光/反射若设为 `AmbientMode.Skybox`，会把太阳圆盘一起卷积进去**。
  EV100=15 下圆盘约 41000 渲染单位、占球面 1.8e-5，平均贡献 ~0.74，而天空平均只有 ~0.1
  —— 环境光会偏亮约 8 倍。所以 `VistaSkySetup` **故意不动** `ambientMode`，
  只在日志里警告。漫反射那条链路已由 Task #5a 的 SH 投影（不含圆盘）接管
  （`AmbientMode.Custom` + `RenderSettings.ambientProbe`）；反射链路见 #5b。
- **`RTHandles.Alloc` 的 3D 重载必须显式给 `dimension: TextureDimension.Tex3D`**。
  少写这个参数会命中 2D 的便捷重载，静默分配出一张 Tex2D，于是 `RWTexture3D` 绑定失败，
  而**唯一的提示是 Editor.log 里一行 warning**，Console 里什么都没有。
  症状是 AP 表读回来全 0 —— 看起来像 kernel 没跑，其实是绑定就没成。
- **3D 纹理不能用 `RenderTexture.active` + `ReadPixels` 读回**：那套 API 是 2D-only，
  在 3D RT 上**静默只读到 slice 0**，不报错。自检里所有 3D 读回都走
  `Graphics.CopyTexture(src, z, 0, tmp2D, 0, 0)` 逐片拷。
  这个坑的恶劣之处在于"读到的数据看着完全合理"，只是每一片都一样。
- **往 fp16 表里塞诊断数值要先换单位**。相机海拔以 km 存是 9.766e-6，落进 fp16 的
  次正规区，读回来是 0，与"真的为 0"分不开。诊断通道统一换成 m 再写。
- 装 RendererFeature 要同步 `m_RendererFeatureMap`（URP 用其中的 `localFileId` 在引用丢失后
  重建），少写它的症状是重启 Editor 后 feature 变 `None`。`localId` 在
  `AssetDatabase.AddObjectToAsset` 之后立刻可取，不用等 `SaveAssets`。
- **`VistaEarthShadow` 的偏置方向反了，代价是全场亮度低 6~9%，而症状伪装成"求积不准"。**
  这是这个模块最贵的一个坑，完整记一下，因为它示范了"看起来像精度问题的东西其实是
  参数化问题"，以及为什么该停止推理、去加观测。
  - 症状：AP 的一次行进循环与共享积分器的 256 步参考解，在最近的切片上差 **24%**。
    远处切片全部对得上（< 1%）。
  - 三个**猜错**的方向（都很像，都不是）：① AP 的行进循环与共享积分器不等价 ——
    实测差 0.30%，等价；② `c = dot(oc,oc) − Rb²` 在量级 4e7 上算，fp32 那里的间隔是 4，
    所以 c 的符号是噪声、`VistaRaySphereIntersectNearest` 返回一个 ~0.01 km 的伪根
    把 `tMax` 钳掉 —— 实测 `tBottom = −1`，没有伪根；③ `(S − S·T)/σ` 在极小 dt 下
    灾难性抵消 —— 量级上界约 1%，远不够。
  - 真正解决问题的一步不是继续推理，而是注意到**参考解自己就不是线性的**：
    `3.553/0.020 = 177.6` 而 `7.234/0.0333 = 217`。在 20~33 m 的路径上
    `VistaEvaluateScatterSample` 只随 r、muSun 变化，实质是常数，被积函数**必须**线性。
    这把问题从"哪个积分器错了"改写成"为什么被积函数不是常数"，于是加了三个观测：
    区间（`tBottom` / `tTop` / 海拔 / `up·ray`）、`earthShadow` 在阶跃两侧的取值、
    以及线性探针 `ref(0.04)/ref(0.02)`。
  - 读数直接指出原因：相机海拔 **9.766 m**、`up·ray 0.01802`、
    **`earthShadow @3 m = 0` 而 `@1 km = 1`**、线性比值 2.553。
  - 根因：`VistaEarthShadow` 把遮挡球的球心沿 `+up` 推了 10 m，即**朝采样点推近**，
    于是 `c = (r − offset)² − Rb² < 0` —— 海拔不足 10 m 的采样点一律被判成
    "在星球内部"，直射项被整体归零。UE 的 SkyAtmosphere 就是这么写的，在那里不显眼
    （近处切片本就贡献极小），但本项目的自检把它照出来了：相机在 9.766 m、
    中心视线每 km 爬 18 m，阶跃落在 `t = (10 − 9.766)/18.02 = 0.0130 km`，
    而 AP 循环的第 0 片节点正好在 `t = 0.003` 和 `t = 0.013`。
    **两套求积在阶跃两侧取节点，差出几十个百分点是必然的 —— 两个积分器都没错。**
  - 修法：球心改推 `−up`，即**推远**，于是 `c = (r + offset)² − Rb² > 0` 恒成立，
    判定退化成物理上正确的"太阳是否在当地地平线以下"。10 m 的容差同时还兜住 fp32：
    r 在 6360 km 上的 ulp 是 0.49 m，海拔算出负值也不会误判
    （顺便解释了为什么实测海拔读到 9.766 而不是 10 —— 差 20 个 ulp）。
  - 修完每一项预测都对上了：`earthShadow @3 m` 0→1、线性比值 2.553→2.022、
    Log errCenter 23.99%→1.19%、Log errMid 19.96%→1.13%、Power errCenter 5.89%→0.30%。
    天空基线整体上移（正午地平线 11316→12053，日落峰值 49728→54304），
    而**天顶值一个都没动** —— 天顶视线不经过贴地那 10 m，正是该有的签名。
- **共享积分器的均匀分支只覆盖了 `tMax·(N−0.7)/N` 的路径**。
  `tNew = tMax·(i + 0.3)/N` 配 `dt = tNew − t`，最后一段止于 `tMax·(N−0.7)/N`。
  N=256 时短 0.27%（无所谓），N=2 时只覆盖 65%（严重）。
  目前所有走这条分支的调用都用高步数，所以没有实际影响，
  但**低步数分级（Task #7 的移动端档）不能直接复用这个分支**，需要改成
  末段补齐或换成 variableSampleCount 那条（它的 t1 有 `> 1.0 → tMax` 的兜底）。
- **`SphericalHarmonicsL2` 的归一化约定文档没写全，必须实测。**
  两个未知：这个类型存的是"辐射亮度 SH"还是"已与余弦瓣卷积过的辐照度 SH"
  （相差逐阶的 `Â_l` = π、2π/3、π/4），以及基函数常数 `Ŷ_i`（`Y00 = 0.2820948` 等）
  有没有折进去。猜错的症状是环境光整体亮/暗约 3 倍 —— **在任何单一场景里都像
  "美术没调好"**，不会被当成 bug。
  - 第一次尝试是从 `AddDirectionalLight` 的打印结果反解，错了：那样有两个未知量、
    一个方程，而且我当时以为 +y 方向光的 L2 项为 0（并不是）。
    **正确做法是停止推理、去构造观测**：一次只把一个系数置 1，在一个 9 个基全非零的
    通用方向上求值，权重就直接量出来了 —— 完全不需要知道 Unity 内部折了哪些常数。
  - 实测：`k_i` 恰好等于 `1/Ŷ_i`，即 `Evaluate` 用的是**未归一化多项式基**
    `{1, y, z, x, xy, yz, 3z²−1, xz, x²−y²}`（就是 `ShadeSH9` / `unity_SHAr` 那套形态）；
    且 `AddAmbientLight(white)` 后 `Evaluate` 各向恒为 1，说明它返回的是
    **albedo=1 的出射亮度**（辐照度/π）。
  - 于是写入公式是 `c_i = (Â_l/π)·Ŷ_i·L_i`。这三项实测都写成了断言而不是打印：
    Unity 哪天改约定必须在自检里炸，而不是等到看图。
- **RenderGraph 里没有 `SetGlobalBufferAfterPass`。** 纹理有 `SetGlobalTextureAfterPass`，
  buffer 没有对应物（`Runtime/RenderGraph` 整个目录里 "SetGlobalBuffer" 零命中，
  它只存在于 CommandBuffer 包装层：`ComputeCommandBuffer` 上四个重载）。
  所以 buffer 全局只能在 render func 里手动绑，且该 pass 必须
  `AllowGlobalStateModification(true)` 才能过 `ThrowIfGlobalStateNotAllowed()`。
  `BufferHandle → GraphicsBuffer` 的隐式转换要查 `RenderGraphResourceRegistry.current`，
  只有在 execute 阶段才有效 —— 正好也只能在那里绑。
- **compute pass 里绑 `RWStructuredBuffer` 用 `UseBuffer`，不是 `UseBufferRandomAccess`。**
  读实现才分得清：`UseBuffer` 就是 `UseResource(handle, flags)`；
  `UseBufferRandomAccess` 在此之上多调一次 `SetRandomWriteResourceRaw(h, index, ...)`，
  那是给"**光栅** pass 里用 u# 寄存器写 UAV"准备的（对应旧的
  `Graphics.SetRandomWriteTarget`）。compute kernel 是按名字绑的，多绑一次随机写目标
  只会白占一个 UAV 槽。两个都能跑，所以只能靠读源码区分。
- **`RequestAsyncReadback` 不在 `ComputeCommandBuffer` 上**，只声明在
  `IUnsafeCommandBuffer`（四个重载）。想在 compute pass 里发读回是走不通的 ——
  这正好推向"读回在记录期用 `AsyncGPUReadback.Request` 发"那条更简单的路（见「取舍」）。
- **均值恒等式只在两侧用*同一个方向集*时才精确成立。**
  第一版参考解用 4096 样本、投影用 1024，恒等式在日落红通道差 **2.75%** ——
  那不是 bug，是 1024 与 4096 在"地平线几度宽的高对比亮带"上的求积差异。
  一个只在 N→∞ 才成立的等式没法卡 1e-3 的阈值。
  修法是给参考核加**第二个**均值组，用与投影完全相同的 1024 方向集：
  于是两边逐项对应、只差归约顺序，实测降到 1e-7。
  被丢掉的那 2.75% 反而成了有用的东西 —— 它就是采样数的收敛性度量（见「取舍」末条）。
  参考核的输出布局做成**自描述**的（法线也一并写出、均值组的法线写零向量做标记），
  C# 侧就不必镜像一份法线定义；镜像的那份迟早与 shader 走歧，届时自检会拿错法线去比，
  报出的偏差既不是 0 也不是明显错误 —— 最难查的那种失败。
- **Dispose 顺序：先清读回、再放 buffer。** `VistaSkyAmbientProbe.Dispose` 里会
  `WaitForCompletion`，反过来就是让在飞的读回请求从已释放的显存里搬数据。
- **白天环境光的 `c_1`（y 的系数）是**负**的，这不是符号错。** 第一眼一定会怀疑
  ——"天上亮、地上暗，y 矩怎么会是负的"。实测 Demo 场景 `c_1 = (−3359, −2331, −417)`，
  而按法线重建出来是 上 (1444, 2721, 5400) / 下 (8163, 7383, 6234)：
  **朝下确实比朝上亮 5.7 倍（红通道）**。原因是天顶恰好是全天最暗的地方
  （正午 R 仅 ~943 cd/m²），而天底吃的是被太阳直射的地面反弹
  （≈ `albedo·E☉·sin(elev)·T/π`，正午在数千量级）。
  通道比例同时印证：`|R| > |G| >> |B|` —— 蓝天（上）与暖地面（下）在 y 矩上几乎抵消，
  所以蓝通道的 y 矩只剩 −417。把这三向的重建值固定打进诊断里，
  就是为了不必每次重新推一遍。
- **逐帧写 `RenderSettings.ambientProbe` / `ambientMode` 不会把场景标脏**（实测 Demo
  场景连续渲染后 `isDirty` 仍为 `False`）。所以不需要给导出加"仅在值变化时写"的门控。
  这条是写之前担心过、实测证伪的 —— 记下来免得以后又去加没用的门控。

- **Unity 不允许把 Cube RT 绑到 compute 的 `RWTexture2DArray`。** 报错：
  `Property (_VistaSkyReflectionRW) at kernel index (0) has mismatching output texture
  dimension (expected 5, got 4)`（5 = Tex2DArray = HLSL 声明，4 = Cube = 绑上来的 RT）。
  硬件层面 cube 的 UAV view **就是** 2D array view，所以这纯粹是引擎侧的校验，
  不是能力限制。落地方案：compute 写一张 6 层 × 7 级 mip 的 `Tex2DArray` 中转纹理，
  dispatch 完逐面 `CopyTexture` 搬进 cube，代价 6×64²×7级 fp16 ≈ **0.4 MB**。
  另一条路是照 HDRP `IBLFilterGGX` 改成光栅（`SetRenderAttachment` 带
  `mipLevel`/`depthSlice`），那要重写 GGX 积分核；中转纹理让 GGX 积分、
  mip↔粗糙度反函数、逐面方向约定这三处最贵最容易错的东西一个字都不用动。
  顺带**加强**了自检：判据 ① 现在同时验证 `element → CubemapFace` 的映射，
  搬错面的症状是那一面单独炸红，而不是"六面都对但整体转了 90°"这种要看图才发现的错。
  - 先前我写下过一条"core / URP 里没有 compute 写 cubemap 的先例"，
    当时被自己纠正为"分配有先例、绑定没有" —— 这次的报错正好证实了那个拆分：
    `Runtime/PathTracing/Environment/CubemapRender.cs:152-160` 确实
    `dimension = Cube` + `enableRandomWrite = true`，但它自己是用
    `SetRenderTarget(new RenderTargetIdentifier(tex, 0, (CubemapFace)i))` 逐面**光栅**写的，
    从没把它当 compute UAV 绑过。**`enableRandomWrite` 分配得下来 ≠ 绑得上去。**
- **`CopyTexture` 只存在于 `IUnsafeCommandBuffer`**（`IUnsafeCommandBuffer.cs:430-464`），
  `ComputeCommandBuffer` / `RasterCommandBuffer` 上都没有。所以 RenderGraph 侧的
  拷贝必须是 `AddUnsafePass` + `CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd)`。
  这与 `RequestAsyncReadback`、`SetGlobalBuffer` 是同一类不对称 —— 判断一个 API
  在 RenderGraph 里能不能用，看的是它挂在哪个 command buffer 接口上，不是它在
  原生 `CommandBuffer` 上存不存在。
  - 要用的重载是 `CopyTexture(src, srcElement, dst, dstElement)`（:440），
    它一次搬一个 element 的**全部 mip** —— 所以是 **6 次调用，不是 42 次**。
  - 立即模式（Editor 自检 / 预览）不需要拆：原生 `CommandBuffer` 的状态转换由图形层
    自动插，dispatch 与 copy 录在同一条 cmd 里是安全的。所以一个
    `CopySkyReflectionToCube(CommandBuffer)` 同时服务两条路径。
- **`RTHandles.Alloc` 的 `slices` 对 Cube 必须是 1，对 Tex2DArray 才是层数。**
  `RTHandleSystem.cs:881` 把 `slices` 直接转成 `RenderTextureDescriptor.volumeDepth`；
  cube 的六面来自 `dimension`，再给 `slices: 6` 就是要一个 6×6 面的东西。
  这次一张 cube（`slices: 1`）+ 一张 array（`slices: 6`）并排放着，正好是对照。
- **FXC：`GroupMemoryBarrierWithGroupSync` 不能位于任何线程相关 `return` 的下游 ——
  即使那个条件事实上是组内常量。** 自检核最自然的写法是
  `if (group == MEAN_GROUP) { ...; return; }` 三段并列，FXC 直接拒绝：
  `thread sync operation must be in non-varying flow control`。`SV_GroupID` 在组内明明
  是常量，但 FXC 的判据是**语法上**的。而且这不算保守过度：DXIL 层面 barrier 要求
  整组到齐，提前退出的线程永远到不了。
  修法是三段判据顺序排开、barrier 一律留在顶层无条件流里，让八个组全部走完全部
  barrier —— 出结果的组由 `if (group == ...) && tid == 0u` 决定**写不写**，
  而不是决定**走不走**。代价是每组多跑两趟归约，自检核不值一提。
  （备选是拆三个 kernel，但那样 C# 侧要维护三次 dispatch 与三套绑定，
  而这三条判据是互相印证的，拆开反而弱化了它。）
- **`#pragma only_renderers` 没有 `d3d12` 这个 token。** D3D12 走的就是 `d3d11` 的
  编译目标，写上只换来一条 `Unrecognized renderer` 警告。三个文件都清掉了
  （`SkyReflection.compute`、`AtmosphereLut.compute:23`、`VistaSky.shader:40`）。
- **自检输出缓冲的「行基址」不要用组号做算术。** 第一版写的是 `MEAN_GROUP + i` /
  `MIP_GROUP + 3 + m`，看着简洁，实际在第 9 行留了个洞，而 C# 侧按"紧凑排列"去读
  就整段错位。**行基址与组号是两件不同的事，别复用同一个数** ——
  现在 HLSL 与 C# 两侧各有一份显式的 `ROW_*` / `k_ReflVerifyRow*` 常量。
- **`unity_SpecCube0_HDR` 的残余风险已实测排除：`(1, 1, 0, 0)`**，
  也就是 `DecodeHDREnvironment` 是恒等 —— float Cube RenderTexture 经
  `customReflectionTexture` 挂上去时引擎不会塞一个解码系数。
  这条原本列为"待 Task #6 用一个 roughness-0 的球对着背后天空验"的开放风险
  （症状是"反射整体亮度差一个常数倍"），现在由 `Log Sky Reflection State` 直接读出
  全局量解决了 —— 比看图判断可靠，也省掉了 Task #6 的一项。
- **`filterMode` 给中转纹理留 `Trilinear` 而不是 `Point`。** 它不会被采样，
  滤波模式在功能上无关紧要；但它是 RenderDoc / Frame Debugger 里排查反射问题的第一站，
  预览图跟着 cube 的滤波模式走比较不容易看错。
- **cube 上取掉了 `enableRandomWrite`。** 它现在只是 `CopyTexture` 的目标 + SRV，
  开着也能跑。取掉是为了不让"这张图是 compute 直接写的"这个**已经被验伪**的假设
  留在代码里 —— 下一个人（包括三个月后的我）会照它去 debug。
- **在 `file:` 引用的本地 package 里新建文件，Unity 不会自动发现。**
  `refresh_unity(mode: if_dirty)` 返回 `refresh_triggered: false` 就过去了，
  编译看着成功、控制台干净，但新的 `[MenuItem]` 根本没注册（菜单执行报
  "might be invalid, disabled, or context-dependent"）。要 `mode: force` + `scope: all`。
  这个失败形态很坑，因为它**看起来像代码写错了**而不是像资产没导入。

### 待办（Task #6 验收时补）

- LUT **七个** pass 的实测耗时（目标合计 < 0.3 ms）。Transmittance / MultiScattering
  只在参数变化的帧存在，稳态是 SkyView + SH + 反射积分 + 反射拷贝 + AP 五个。
  反射那两个的估算是 0.4M 次 LUT 取样 ≈ 0.03 ms + 6 次整 element 的 `CopyTexture`，
  需要实测确认 —— 尤其是拷贝，0.4 MB 的搬运本身不贵，但它多了一个 pass 边界的 barrier。
- 太阳 0°→90° 扫描的 banding 数值判定。目前只有截图观感：日晕在 8-bit 输出上有极淡的
  等值线，2× 超采样后消失，判断是量化而非 LUT 分辨率不足 —— 但这需要读回数值确认，
  截图（JPEG）不足以定性。
- AP 的分辨率与切片分布定档。数值侧的输入已经齐了（errCenter / errMid 见上），
  但"32³ 够不够、Power 还是 Log"最终要看远山上有没有可见的雾量台阶，
  以及贴地时近处会不会糊 —— 这两件事只有接上 Step 1 的合成才能判。

## [0.1.0] - 2026-08-14

初始化。目标平台 PC（D3D12 / Vulkan），Unity 6000.4 + URP 17.4 RenderGraph。
