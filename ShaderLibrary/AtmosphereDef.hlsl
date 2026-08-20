#ifndef VISTA_ATMOSPHERE_DEF_INCLUDED
#define VISTA_ATMOSPHERE_DEF_INCLUDED

// ============================================================================
//  单位约定（整套 Vista 大气模块共用，不要在别处引入第三种单位）
// ----------------------------------------------------------------------------
//  长度        : km。地球半径 6360 km 若用米表示是 6.36e6，
//                在 froxel raymarch 里做 r*r 会直接冲到 fp32 有效位边缘，
//                所以大气内部一律 km，世界空间(m) 在边界处乘 _VistaWorldToAtmosphere 转换。
//  散射/消光   : 1/km。
//  太阳照度    : lux（大气顶垂直入射约 120000 lux）。
//  高度 h      : 海拔 km，= r - bottomRadius。
// ============================================================================

CBUFFER_START(VistaAtmosphereCB)
    // xyz: Rayleigh 散射系数 (1/km)，w: 密度指数尺度 (= -1/scaleHeight，故为负)
    float4 _VistaRayleigh;
    // xyz: Mie 散射系数 (1/km)，w: 密度指数尺度
    float4 _VistaMieScatter;
    // xyz: Mie 消光系数 (1/km，>= 散射，差值即吸收)，w: HG 相位函数 g
    float4 _VistaMieExtinct;
    // xyz: 臭氧吸收系数 (1/km)，w: 未用
    float4 _VistaOzone;
    // x: 帐篷剖面中心高度 (km)，y: 1/半宽 (1/km)，zw: 未用
    float4 _VistaOzoneTent;
    // x: 星球半径 (km)，y: 大气顶半径 (km)，z: x^2，w: y^2
    float4 _VistaRadius;
    // xyz: 地面 albedo，w: 世界空间(m) -> 大气空间(km) 缩放 = 0.001
    float4 _VistaGround;
    // xyz: 大气顶太阳照度 (lux)，w: cos(太阳角半径)
    float4 _VistaSun;
    // xy: Transmittance LUT 尺寸，zw: 其倒数
    float4 _VistaTransmittanceLutSize;
CBUFFER_END

// ----------------------------------------------------------------------------
//  逐视图数据（每帧 / 每相机变化）
//
//  大气空间的定义：**与世界空间同朝向，只做平移 + 缩放**。
//  这样视线方向、太阳方向这些单位矢量可以直接跨空间使用，不需要旋转矩阵。
//  星球中心放在世界 -Y 方向 bottomRadius 处，所以世界 +Y 就是地面处的 up。
//  posKm = worldPos_m * 0.001 - _VistaPlanetCenterKm.xyz
//
//  为什么不像很多实现那样直接丢掉水平坐标、只保留高度：512 m 的场景里确实等价，
//  但 froxel AP 的远端会伸到几 km，而且 ARPG 主场景将来可能更大，
//  留住完整位置的成本只是一次向量减法。
// ----------------------------------------------------------------------------
CBUFFER_START(VistaAtmospherePerViewCB)
    // xyz: 星球中心在 km 缩放后的世界坐标系中的位置，w: 未用
    float4 _VistaPlanetCenterKm;
    // xyz: 相机在大气空间的位置 (km)，w: 其长度（即到星球中心的半径 r）
    float4 _VistaViewPosKm;
    // xyz: 世界空间中由着色点指向太阳的单位矢量，w: 曝光倍率（见 VISTA_EXPOSURE）
    float4 _VistaSunDirection;
    // xy: Sky-View LUT 尺寸，zw: 其倒数
    float4 _VistaSkyViewLutSize;

    // ---- Aerial Perspective froxel LUT ----
    // x: 最近切片距离 (km)，Log 模式的起点；y: 最远切片距离 (km)；
    // z: Power 模式的指数 k；w: 分布模式 (0 = Power, 1 = Log)
    float4 _VistaApParams;
    // xyz: AP LUT 尺寸 (w, h, d)，w: 1/(d-1)（切片索引 -> 归一化深度参数的步长）
    float4 _VistaApSize;
    // 视锥四角的世界空间方向，**未归一化**（长度取到同一个平面上）。
    // 必须用未归一化的角向量做双线性插值再 normalize：透视投影下
    // 「远平面上的位置」才是屏幕坐标的线性函数，归一化后的方向不是。
    // 先归一化再插值会在视野边缘产生约 1° 的方向误差 —— 在 32×32 的表上看不出，
    // 但会让自检里「froxel 方向 vs 逐像素重建方向」这条一致性检查永远差一点。
    float4 _VistaApRayBL;
    float4 _VistaApRayBR;
    float4 _VistaApRayTL;
    float4 _VistaApRayTR;
    // x: 1 = 采样端用第二张表里的彩色透射率，0 = 用散射表 alpha 里的灰度透射率（论文做法）
    // y: 1/最近切片距离 (1/km)，用于近端淡出；Power 模式填一个大数
    // zw: 未用
    float4 _VistaApFlags;

    // AP 的消费方开关。
    // x: 1 = Vista 自己的材质在着色末尾合成 AP（变体 B）；
    //    0 = 材质不合成 —— 要么 AP 整个关掉，要么由全屏合成 pass 负责（变体 A）。
    // yzw: 未用
    //
    // 为什么它在这里、而不是塞进 _VistaApFlags 的空位：
    // 上面那一组连同两张 3D 表**只在 AP 启用时**才被写（见 BindAerialPerspective）。
    // 而这个开关必须在 AP 关掉的那一帧也被写成 0，否则材质会拿着上一帧的 1
    // 去采一张已经释放的表。「关掉某功能后画面才坏」是最难反查的一类失效，
    // 所以它由 Sky-View pass 下发 —— 那是唯一一个「一定存在」的逐帧 pass。
    float4 _VistaApConsumer;

    // 平行光颜色里**已经含有**的那一份太阳透射率（参考高度处）。
    // xyz: T_ref，逐通道；w: 1 = 逐像素修正生效，0 = 不生效（比值恒为 1）。
    //
    // 为什么需要它 —— 逐像素透射率不能直接乘 T：
    // VistaTimeOfDay 已经把参考高度处的 T_ref 乘进了 Light.color
    // （见 VistaSunTransmittance.ComputeLightParams）。着色时再乘一次 T 就是乘了两遍，
    // 症状是「整个场景偏暗、越接近日落越暗」—— 一个看起来"很有大气感"的错误，
    // 极容易被当成风格接受下来。正确形式是**比值** T(着色点) / T_ref。
    //
    // 比值形式还有一个不显然的好处 —— **CPU 那份 T 会代数上整项约掉**：
    //   Light.color·intensity × ratio
    //     = (E·T_ref^CPU·exposure/π) × (T^LUT(着色点) / T_ref^CPU)
    //     = E·exposure/π × T^LUT(着色点)
    // 于是最终画面里只剩**一份** T，而且是 GPU LUT 那一份 —— 与天空、AP、天光 SH
    // 用的是同一张表。这正是本项目「同一个量不允许两份实现」那条规矩想要的结果：
    // CPU 那份退化成一个自我消去的载体。
    // 反过来说，若分母改成在 GPU 上重采 LUT，比值在参考高度会精确等于 1，但最终光
    // 变成 T^CPU(ref)·T^LUT(px)/T^LUT(ref) —— 两份 T 相乘。宁可要前者。
    //
    // 代价（必须承认的）：不走 Vista/Lit 的材质 —— URP 自带 Lit、粒子、第三方 shader
    // —— 仍然吃 T^CPU。两条口径的差距实测在项目自己的尺子内：
    // 纹素中心最大绝对误差 5.047E-004；带双线性的实用工况里走相对判据的通道
    // 最大 0.706%（门 1%），另有 2 个通道走绝对豁免（自身 |ΔT| < 1E-003）。
    // 见「Validate Sun Transmittance」的 A/B 两项。
    //
    // 注意：#8 那条「Light.color × intensity == E·T·exposure/π」的接缝验收
    // 对这一层**结构性无法失败** —— 它的布景用的是 Hidden/Vista/SeamProbe，
    // 根本不走 Vista/Lit。所以那条验收的数字不变**不能**用来证明这一层是对的，
    // 它只能证明这一层没有碰到 CPU 侧写灯的那条路。
    //
    // T_ref 必须来自**写灯时用的那一次** Evaluate（CPU 侧上传，不在 GPU 上重算）：
    // 上面那个约分要求分母与 Light.color 里的因子是**同一个 float**。
    // w 位为什么不能省：Light.color 里有没有 T_ref 取决于 VistaTimeOfDay 是否在
    // 驱动光色（!m_DriveColor / 没挂 feature / 组件被禁用时它压根不写灯）。
    // 那些情况下灯是美术手填的裸颜色，除以任何 T_ref 都是错的 —— 必须整条退化成
    // no-op。所以这里与 _VistaApConsumer 同理：**每帧无条件写**，由 Sky-View pass
    // 下发，缺省 (1,1,1,0)。漏写的症状同样是「关掉某功能后画面才坏」。
    float4 _VistaSunTransmittanceRef;
CBUFFER_END

// 物理单位 -> 渲染目标单位的曝光倍率。
//
// 为什么必须有这一步：整套 LUT 存的是绝对光度量（cd/m²，天顶蓝天 ~8e3），
// 而 URP 的渲染目标是"1.0 约等于白"的相对空间。不做转换直接写进去，
// tonemap 之后整个天空是纯白。
// 这里采用摄影式映射 exposure = 1 / (1.2 · 2^EV100)，与 HDRP 的 Exposure 一致 ——
// 好处是大气参数保持物理真实，日夜循环的亮度关系自动正确，
// 不需要靠美术手调一堆倍率去凑（那样一旦改时间就全线崩）。
//
// 约束：**所有写进渲染目标的光照都必须乘同一个曝光**（天空、雾、GI、直接光），
// 否则各通道之间的相对亮度会错。Step 2 的 TimeOfDay 会接管 URP 平行光的强度，
// 让它也走这条链路。
#define VISTA_EXPOSURE _VistaSunDirection.w

// 世界空间 (m) -> 大气空间 (km)
float3 VistaWorldToAtmosphere(float3 worldPosMeters)
{
    return worldPosMeters * _VistaGround.w - _VistaPlanetCenterKm.xyz;
}

// 大气空间 (km) -> 世界空间 (m)
float3 VistaAtmosphereToWorld(float3 posKm)
{
    return (posKm + _VistaPlanetCenterKm.xyz) * (1.0 / _VistaGround.w);
}

#define VISTA_BOTTOM_RADIUS   _VistaRadius.x
#define VISTA_TOP_RADIUS      _VistaRadius.y
#define VISTA_BOTTOM_RADIUS_2 _VistaRadius.z
#define VISTA_TOP_RADIUS_2    _VistaRadius.w

// ----------------------------------------------------------------------------
//  纹理坐标 <-> 单位区间
//  Bruneton 2008/2017 的做法：LUT 的 0 和 1 必须落在**纹素中心**而不是纹素边缘，
//  否则线性采样会在边界把区间外的值混进来。地平线附近的误差被这一步放大得很明显，
//  是这套 LUT 最容易被忽略的精度坑。
// ----------------------------------------------------------------------------
float VistaTexCoordFromUnitRange(float x, float texSize)
{
    return 0.5 / texSize + x * (1.0 - 1.0 / texSize);
}

float VistaUnitRangeFromTexCoord(float u, float texSize)
{
    return (u - 0.5 / texSize) / (1.0 - 1.0 / texSize);
}

// ----------------------------------------------------------------------------
//  几何
// ----------------------------------------------------------------------------

// 从半径 r、天顶角余弦 mu 出发，沿视线到大气顶边界的距离 (km)。
// mu = dot(up, viewDir)，向上为正。
float VistaDistanceToTopAtmosphereBoundary(float r, float mu)
{
    float discriminant = r * r * (mu * mu - 1.0) + VISTA_TOP_RADIUS_2;
    return max(0.0, -r * mu + sqrt(max(0.0, discriminant)));
}

// 到地面边界的距离；无交点时返回负数。
float VistaDistanceToBottomAtmosphereBoundary(float r, float mu)
{
    float discriminant = r * r * (mu * mu - 1.0) + VISTA_BOTTOM_RADIUS_2;
    return -r * mu - sqrt(max(0.0, discriminant));
}

bool VistaRayIntersectsGround(float r, float mu)
{
    return mu < 0.0 && (r * r * (mu * mu - 1.0) + VISTA_BOTTOM_RADIUS_2) >= 0.0;
}

// ----------------------------------------------------------------------------
//  介质采样
// ----------------------------------------------------------------------------
struct VistaMediumSample
{
    float3 scatteringRayleigh;
    float3 scatteringMie;
    float3 extinction;      // Rayleigh 散射 + Mie 消光 + 臭氧吸收
};

VistaMediumSample VistaSampleMedium(float altitudeKm)
{
    // Rayleigh / Mie 指数剖面
    float densityRayleigh = exp(_VistaRayleigh.w * altitudeKm);
    float densityMie      = exp(_VistaMieScatter.w * altitudeKm);
    // 臭氧帐篷剖面（Bruneton）：峰值在 ~25km，线性上下降到 0
    float densityOzone    = saturate(1.0 - abs(altitudeKm - _VistaOzoneTent.x) * _VistaOzoneTent.y);

    VistaMediumSample s;
    s.scatteringRayleigh = _VistaRayleigh.xyz  * densityRayleigh;
    s.scatteringMie      = _VistaMieScatter.xyz * densityMie;
    // Rayleigh 吸收为 0，故其消光 == 散射
    s.extinction = s.scatteringRayleigh
                 + _VistaMieExtinct.xyz * densityMie
                 + _VistaOzone.xyz      * densityOzone;
    return s;
}

// ----------------------------------------------------------------------------
//  Transmittance LUT 参数化（Bruneton 映射）
//  uv.x <- 视线到大气顶的距离在 [dMin, dMax] 中的归一化位置
//  uv.y <- 水平方向到大气顶的距离 rho 在 [0, H] 中的归一化位置
//
//  为什么不用 (mu, altitude) 均匀映射：mu 在地平线附近变化极快，
//  均匀映射会把绝大部分纹素浪费在天顶方向，地平线处出现台阶。
// ----------------------------------------------------------------------------
void VistaTransmittanceLutUvToRMu(float2 uv, out float r, out float mu)
{
    float xMu = uv.x;
    float xR  = uv.y;

    // H: 从地面沿切线到大气顶的距离
    float H   = sqrt(max(0.0, VISTA_TOP_RADIUS_2 - VISTA_BOTTOM_RADIUS_2));
    // rho: 从 r 沿水平切线到大气顶的距离
    float rho = H * xR;
    r = sqrt(max(0.0, rho * rho + VISTA_BOTTOM_RADIUS_2));

    float dMin = VISTA_TOP_RADIUS - r;   // 垂直向上
    float dMax = rho + H;                // 水平切线方向
    float d    = dMin + xMu * (dMax - dMin);

    mu = (d == 0.0) ? 1.0 : (H * H - rho * rho - d * d) / (2.0 * r * d);
    mu = clamp(mu, -1.0, 1.0);
}

float2 VistaRMuToTransmittanceLutUv(float r, float mu)
{
    float H    = sqrt(max(0.0, VISTA_TOP_RADIUS_2 - VISTA_BOTTOM_RADIUS_2));
    float rho  = sqrt(max(0.0, r * r - VISTA_BOTTOM_RADIUS_2));
    float d    = VistaDistanceToTopAtmosphereBoundary(r, mu);
    float dMin = VISTA_TOP_RADIUS - r;
    float dMax = rho + H;

    float xMu = (dMax == dMin) ? 0.0 : (d - dMin) / (dMax - dMin);
    float xR  = (H == 0.0) ? 0.0 : rho / H;

    return float2(
        VistaTexCoordFromUnitRange(saturate(xMu), _VistaTransmittanceLutSize.x),
        VistaTexCoordFromUnitRange(saturate(xR),  _VistaTransmittanceLutSize.y));
}

// ----------------------------------------------------------------------------
//  光学深度积分（梯形法）
// ----------------------------------------------------------------------------
float3 VistaComputeOpticalDepthToTopAtmosphereBoundary(float r, float mu, uint sampleCount)
{
    float dx = VistaDistanceToTopAtmosphereBoundary(r, mu) / (float)sampleCount;

    float3 opticalDepth = 0.0;
    for (uint i = 0; i <= sampleCount; ++i)
    {
        float d_i = (float)i * dx;
        // 余弦定理求该采样点的半径
        float r_i = sqrt(max(0.0, d_i * d_i + 2.0 * r * mu * d_i + r * r));
        float3 extinction = VistaSampleMedium(r_i - VISTA_BOTTOM_RADIUS).extinction;
        // 梯形法：两端点权重 0.5
        float weight = (i == 0u || i == sampleCount) ? 0.5 : 1.0;
        opticalDepth += extinction * weight * dx;
    }
    return opticalDepth;
}

#endif // VISTA_ATMOSPHERE_DEF_INCLUDED
