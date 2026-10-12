#ifndef VISTA_TERRAIN_SPLAT_INCLUDED
#define VISTA_TERRAIN_SPLAT_INCLUDED

// ============================================================================
//  Vista 地形混层 —— Step 6 的四项技术都在这个文件里
//
//  这是本模块**自己写的**那一部分。与之相对，VistaTerrainLitInput.hlsl 里的
//  `_Control` / `_Splat0..3` / `_Terrain` cbuffer 那一组名字不是设计选择，
//  而是 Unity 地形引擎的**外部契约**：TerrainData 每帧按这些名字往材质上塞
//  splatmap、层贴图、per-layer 参数。改名字等于地形不渲染。所以那个文件照抄，
//  这个文件全新。
//
//  ------------------------------------------------------------------ 四项技术
//  ① Height-blend 混层（Mishkinis, Advanced Terrain Texture Splatting）
//     取各层高度的最大值做加权，而不是 linear lerp。效果差别是「岩石从沙土里
//     露出来」和「岩石和沙土按百分比对半透明」。
//  ② 陡坡 triplanar：地形 UV 是 XZ 平面投影，断崖上竖直方向的 UV 密度趋于 0，
//     贴图被拉成条。三平面投影是唯一能在不重拓扑的前提下修掉它的办法。
//  ③ Detail normal 距离淡出：近处补高频法线，远处淡掉。不淡掉的话它在
//     1 像素以下的频率上抖，那是 shimmer 的来源而不是细节。
//  ④ Macro variation noise：大尺度低频噪声乘在 albedo 上，破掉平铺规律。
//     成本几乎为零，对「500 m 视距内无可见平铺规律」这条验收的贡献最大。
//
//  ------------------------------------------------------------------ 高度从哪来
//  height-blend 需要逐层高度。URP 的 TerrainLit 从 **maskMap 的 B 通道** 取，
//  而本工程的四个 TerrainLayer 全都没有 maskMap —— 那条路在这儿是空的。
//
//  所以这里用 albedo 亮度当高度代理，**并减掉每层自己的亮度均值**
//  （均值由 Editor 工具离线统计后写进材质 `_VistaLayerParams#.z`）。
//  为什么必须减均值：直接拿亮度当高度，沙（亮）会在任何地方都压住草和岩（暗），
//  那是一个**由颜色决定的恒定偏置**，不是高度关系 —— 混层边界会整体偏向亮的层，
//  且偏多少完全取决于两张贴图的曝光，与地表形态无关。减掉各自均值之后剩下的是
//  「这张贴图内部的明暗起伏」，缝隙暗、凸起亮，那才近似高度。
//
//  代价要写明：它是**代理，不是高度**。albedo 上暗而实际凸起的特征（湿斑、苔）
//  会被判成凹陷。层若真带了 maskMap，`_LayerHasMask#` 会把高度切回 maskMap.b，
//  作者数据优先 —— 代理只是没有数据时的回落，不是首选。
//
//  ------------------------------------------------------------------ UV 取自世界坐标
//  层 UV 不走顶点插值的 `TRANSFORM_TEX(texcoord, _Splat0)`，而是在片元里由
//  positionWS.xz 除以每层的 tile 尺寸（米）算出。两者只差一个常数相位
//  （terrainPos.xz / tileSize），对贴图没有意义上的区别，但换来两件事：
//   · 本场景有 **4 块地形**。按各自的地形 UV 算，相位在地形边界处重新起算，
//     拼缝两侧的平铺会错开半格；按世界坐标算，四块地形天然连续。
//   · triplanar 本来就要世界坐标，省掉一套并行的 UV 体系。
//  代价：少了 `_Splat#_ST` 的 offset 分量（地形 layer 面板本来也不暴露它）。
// ============================================================================

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

// ---------------------------------------------------------------- 逐层采样结果
struct VistaLayerData
{
    half3 albedo;
    half  albedoAlpha;   // 部分层把 smoothness 塞在 albedo.a
    half3 normalWS;
    half  height;        // [0,1]，maskMap.b 或亮度代理
};

// ---------------------------------------------------------------- 三平面权重
// 由几何法线给出三个投影面的权重。pw.x = YZ 面（法线 ±X），pw.y = XZ 面（±Y），
// pw.z = XY 面（±Z）。指数把权重推向主导面，否则三面各占 1/3 会把贴图糊掉。
half3 VistaTriplanarWeights(half3 n, half sharpness)
{
    half3 w = pow(abs(n), sharpness);
    return w / max(w.x + w.y + w.z, 1e-4h);
}

// 陡坡权重：平地 0，陡面 1。`flatNone` 是开始混入 triplanar 的 |n.y|，
// `flatFull` 是完全用 triplanar 的 |n.y|（flatFull < flatNone）。
half VistaSlopeWeight(half3 n, half flatNone, half flatFull)
{
    return 1.0h - smoothstep(flatFull, flatNone, abs(n.y));
}

// ---------------------------------------------------------------- 三平面 UV
// 侧面两个 UV 按法线符号翻 U 轴。不翻的话朝 −X 与朝 +X 的崖面互为镜像，
// 法线贴图的 X 分量会整体反向 —— 半数崖面的受光方向是错的。
// 这不是可以「近似一下」的项：它是符号错误，不是精度损失。
struct VistaTriUV
{
    float2 xz;
    float2 yz;
    float2 xy;
    half3  sgn;
};

VistaTriUV VistaBuildTriUV(float3 positionWS, half3 n, float2 tile)
{
    VistaTriUV o;
    o.sgn = half3(n.x >= 0.0h ? 1.0h : -1.0h,
                  n.y >= 0.0h ? 1.0h : -1.0h,
                  n.z >= 0.0h ? 1.0h : -1.0h);
    float2 inv = 1.0 / max(tile, 1e-3);
    o.xz = positionWS.xz * inv;
    o.yz = float2(positionWS.z * o.sgn.x, positionWS.y) * inv;
    o.xy = float2(positionWS.x * o.sgn.z, positionWS.y) * inv;
    return o;
}

// ---------------------------------------------------------------- 采样：颜色类
// triW == 0 时只采 XZ 一次。地形绝大多数像素是平地，这个分支在 wave 内高度一致，
// 所以「陡坡 triplanar」在平地上的成本接近零，而不是恒定 3×。
half4 VistaSampleTriplanar(TEXTURE2D_PARAM(tex, samp), VistaTriUV uv,
                           half3 pw, half triW)
{
    half4 cXZ = SAMPLE_TEXTURE2D(tex, samp, uv.xz);
    if (triW <= 0.001h)
        return cXZ;

    half4 cYZ = SAMPLE_TEXTURE2D(tex, samp, uv.yz);
#if defined(_VISTA_TERRAIN_MOBILE)
    // 移动档只留两面：XZ 与「侧面里权重更大的那一面」。第三面的贡献在
    // 断崖上通常 < 0.15，省掉它换一次采样。
    half4 tri = (cYZ * (pw.x + pw.z) + cXZ * pw.y);
#else
    half4 cXY = SAMPLE_TEXTURE2D(tex, samp, uv.xy);
    half4 tri = cYZ * pw.x + cXZ * pw.y + cXY * pw.z;
#endif
    return lerp(cXZ, tri, triW);
}

// ---------------------------------------------------------------- 采样：法线类
// 结果直接是**世界空间**法线。为什么不走 tangent 空间：三个投影面各有自己的
// 切空间，硬要折回一套 mesh 切空间再让 InitializeInputData 转回世界，
// 等于绕一圈还引入一次基变换误差。地形的切空间本来就是由几何法线现算的
// （没有真正的 UV 切线），所以在世界空间里合成是更短也更准的路径。
half3 VistaSampleTriplanarNormalWS(TEXTURE2D_PARAM(tex, samp), VistaTriUV uv,
                                   half3 pw, half triW, half scale, half3 gn)
{
    // XZ 面：U 轴 = +X，V 轴 = +Z，N 轴 = 几何法线。
    // T 对几何法线做一次 Gram-Schmidt，这样平地上的细节法线会跟着地表坡度走，
    // 而不是固定贴在水平面上。
    half3 T = half3(1.0h, 0.0h, 0.0h) - gn * gn.x;
    T = normalize(T + half3(1e-5h, 0.0h, 0.0h));
    half3 B = cross(T, gn);

    half3 nXZ = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, samp, uv.xz), scale);
    half3 wXZ = T * nXZ.x + B * nXZ.y + gn * nXZ.z;
    if (triW <= 0.001h)
        return wXZ;

    // YZ 面：U = +Z·sgn.x，V = +Y，N = +X·sgn.x
    half3 nYZ = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, samp, uv.yz), scale);
    half3 wYZ = half3(uv.sgn.x * nYZ.z, nYZ.y, uv.sgn.x * nYZ.x);

#if defined(_VISTA_TERRAIN_MOBILE)
    half3 tri = wYZ * (pw.x + pw.z) + wXZ * pw.y;
#else
    // XY 面：U = +X·sgn.z，V = +Y，N = +Z·sgn.z
    half3 nXY = UnpackNormalScale(SAMPLE_TEXTURE2D(tex, samp, uv.xy), scale);
    half3 wXY = half3(uv.sgn.z * nXY.x, nXY.y, uv.sgn.z * nXY.z);

    half3 tri = wYZ * pw.x + wXZ * pw.y + wXY * pw.z;
#endif
    return lerp(wXZ, tri, triW);
}

// ---------------------------------------------------------------- 高度代理
// x = tile.x, y = tile.y, z = 高度代理中心, w = 代理对比度
// 中心与对比度由 Editor 工具按该层 albedo 的亮度 p05/p95 标定（见
// VistaTerrainMaterialTool.Setup）：p05 映到 0，p95 映到 1。
half VistaLayerHeight(half3 albedo, half maskB, half hasMask, half lumaCenter, half contrast)
{
    // 去中心：留下的是这张贴图**内部**的明暗起伏，而不是它整体比别的层亮还是暗。
    half proxy = saturate(0.5h + (Luminance(albedo) - lumaCenter) * contrast);
    return lerp(proxy, maskB, hasMask);
}

// ---------------------------------------------------------------- 逐层色调拉伸
// 为什么需要这一路：本工程四层 albedo 的亮度 p05→p95 只有 0.252→0.303，
// **5% 的动态范围**。一张 5% 对比度的贴图无论用什么混法、补多少法线，
// 画出来都是一片均质的塑料 —— 混层技术解决的是「两层之间的边界」，
// 它解决不了「一层内部没有明暗」。那只能由色调拉伸给。
//
// 枢轴用的是该层自己的亮度中心（`_VistaLayerParams#.z`，由 Editor 工具按
// p05/p95 的中点标定），不是 0.5：拿 0.5 当枢轴的话，整体偏暗的层会被
// 连带提亮，那是**换曝光**而不是**加对比**，两件事会在调参时互相掩盖。
//
// 色度按亮度的比例缩放（`c * lt / l`），而不是分通道各自做对比度：
// 分通道做会让 RGB 三条曲线在高对比下分叉，草的绿偏向黄绿、沙的黄偏向橙，
// 那是**色相漂移**，不是对比度。按比例缩放保住色相，只动明度。
//
// contrast = 1、sat = 1 时**恒等**（lt == l ⇒ g == 1，lerp(l,albedo,1) == albedo），
// 所以这一路不加默认值就是「什么也没做」，而不是「悄悄改了画面」。
half3 VistaLayerTone(half3 albedo, half lumaCenter, half contrast, half sat)
{
    half  l  = Luminance(albedo);
    half3 c  = lerp(l.xxx, albedo, sat);
    half  lt = max(0.0h, lumaCenter + (l - lumaCenter) * contrast);
    // l 可以是 0（纯黑 texel）。那时候没有色相可保，按原样返回缩放前的灰度。
    half  g  = lt / max(l, 1e-4h);
    // 上界截到 1：albedo > 1 的表面反射的光比收到的多。Editor 工具已经按
    // 「亮度 p999 拉完不超过 1」定了对比度的上界，但那是**亮度**的约束 ——
    // 一个饱和的红在亮度只有 1 时单通道可以到 4.7，所以通道上界必须在这里
    // 单独守一道。少了它，亮度那条上界就只是一句写在工具里的假设。
    return saturate(c * g);
}

// ---------------------------------------------------------------- Height-blend
// Mishkinis 的做法：谁高谁赢，但留一条 `transition` 宽的过渡带，
// 带外完全被最高层占据，带内按高度差加权。
//
// 退化性质（值得留意，因为它保证了这条路径不会把原来的画面改坏）：
// 没有高度信息可用时，混层必须退回 Unity 原来的线性 splat。
// 具体是哪两种「没有高度信息」，见下面「权重不可能全为 0」的第二条分支
// 与「与线性 splat 的关系」。所以 height-blend 不是「另一种混法」，
// 而是原混法的一个带高度信息的推广。
//
// ── 排名键为什么是 height × control，而不是 height ──
//
// 拿**原始高度**去比「谁最高」，会让一个 control = 0 的层（这个 texel 上根本
// 不存在的层）参与评比。它一旦最高，别的层就全落到 max(0, h + t - maxH) = 0
// 之外，而它自己又被末尾的 * control 乘没 —— 四个权重同时为 0，归一化除到
// 1e-5 的地板上，屏幕上是**纯黑硬边斑块**。这不是理论角落：高度代理的对比度
// 按 ±1σ 标定，实际接近二值，「最高的那层恰好不覆盖」每一帧都有成片像素满足。
// （第一版就是这么写的，拍出来的 00_all_on 上全是黑斑，而 02_no_heightblend
//  干净 —— 所以上面那条「四层高度相等」的恒等式是真的，只是它覆盖不到这个情形。）
//
// 乘上 control 之后：不存在的层键恒为 0，排不到最前（除非全场都是 0）。
// 顺带解决同源的第二个问题 —— 覆盖率 1/255 而高度为 1 的一层，不乘会独吞整个
// texel；乘完它的键只有 0.004，压不住覆盖率 254/255 的那一层。
// URP 的 _TERRAIN_BLEND_HEIGHT 与 Mishkinis 原文同此，理由也正是这两条。
// 代价：原来那条「四层高度相等 ⇒ 精确等于 control」的恒等式随之弱化 ——
// 现在四层高度相等时给出的是按 control 轻微锐化过的分布，只有 t → ∞ 时
// 才精确回到 control（见下）。换来的是永不出黑，以及薄覆盖层不再独吞 texel。
//
// ── 权重不可能全为 0 ──
//
// 设 i* 为键最大的层，则 w[i*] = max(0, key[i*] + t - key[i*]) · c[i*] = t·c[i*]。
//  · maxK > 0：key[i*] = h·c[i*] > 0 ⇒ c[i*] > 0 ⇒ w[i*] > 0。
//  · maxK = 0：所有键为 0 ⇒ w = t·control ⇒ 归一化后精确等于 control。
// 两种情形都只要 dot(control,1) > 0，而这是引擎对已刷 splatmap 的保证。
// 那个 1e-5 的地板因此只在「完全没刷过的 texel」上碰得到，
// 而那种 texel 上任何混法都给不出颜色。
//
// ── 与线性 splat 的关系 ──
//
// t 是「回到线性混合」的连续旋钮：t → ∞ 时 w → t·control，归一化后精确等于
// control；t → 0 时退化成硬切边（键最高的层独占）。默认 0.3 在两者之间。
half4 VistaHeightBlend(half4 control, half4 heights, half transition)
{
    // 排名键是 height × control，不是 height。见上方长注释里的两条理由与不可能全零的证明。
    half4 key = heights * control;
    half maxK = max(max(key.r, key.g), max(key.b, key.a));
    half t = max(transition, 1e-5h);
    half4 w = max(0.0h, key + t - maxK) * control;
    return w / max(dot(w, half4(1, 1, 1, 1)), 1e-5h);
}

// ---------------------------------------------------------------- Macro variation
half VistaHash21(float2 p)
{
    p = frac(p * float2(127.1, 311.7));
    p += dot(p, p + 34.23);
    return (half)frac(p.x * p.y * 95.4307);
}

half VistaValueNoise2D(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    half2 u = (half2)(f * f * (3.0 - 2.0 * f));
    half a = VistaHash21(i);
    half b = VistaHash21(i + float2(1, 0));
    half c = VistaHash21(i + float2(0, 1));
    half d = VistaHash21(i + float2(1, 1));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// ---------------------------------------------------------------- ⑤ 中频起伏（解析导数）
// 返回 (n, ∂n/∂p.x, ∂n/∂p.y)。导数是**噪声域**的（p 为单位时每周期的变化），
// 不是世界空间的 —— 换算关系见 VistaReliefNormalWS 里对 strength 的定义。
//
// 为什么是解析导数而不是「再采三次做中心差分」：
//  · 中心差分要 3 倍的 hash 成本，而 value noise 的导数是闭式的；
//  · 更重要的是差分步长得自己挑 —— 挑小了被 half 的精度吃掉，挑大了
//    算出来的是一个被平滑过的斜率。解析导数没有这个自由度，也就没有这个坑。
//
// ── 这一路为什么必须存在（而不是「把法线图调强一点」）──
//
// 本工程四张层法线图的切向振幅实测只有 0.008 / 0.033 / 0.008 / 0.073，
// 而 8-bit 法线图的量化步长是 1/127.5 = 0.0078。两张草地法线的**信号与
// 量化地板同量级** —— 把 normalScale 调大只会把台阶一起放大，放大的是
// 条带而不是起伏。这条路因此不是锦上添花，是那两层唯一的起伏来源。
//
// 另一件只有程序化能给的事：**没有 mip 塌缩**。同一批法线图从 1×1 盒滤波到
// 16×16，振幅从 0.0079/0.0329/0.0725 掉到 0.0035/0.0185/0.0486 —— 腰斩。
// 所以贴图路线天然在中远距离失效，而那正是大场景的主要画面。解析导数在
// 任何距离给出的都是同一个斜率。
//
// 工业参照：UE5 Landscape 的 procedural noise normal、HDRP 的 normal remapping
// 走的是同一个取舍 —— 远处的形体交给程序化，近处的细节交给贴图。
half3 VistaValueNoiseD(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    half2  u  = (half2)(f * f * (3.0 - 2.0 * f));
    half2  du = (half2)(6.0 * f * (1.0 - f));      // d/df [f²(3−2f)] = 6f(1−f)

    half a = VistaHash21(i);
    half b = VistaHash21(i + float2(1, 0));
    half c = VistaHash21(i + float2(0, 1));
    half d = VistaHash21(i + float2(1, 1));

    // 双线性 + smoothstep 的展开式：n = a + k1·u.x + k2·u.y + k3·u.x·u.y
    half k1 = b - a;
    half k2 = c - a;
    half k3 = a - b - c + d;

    half n = a + k1 * u.x + k2 * u.y + k3 * u.x * u.y;
    return half3(n,
                 du.x * (k1 + k3 * u.y),
                 du.y * (k2 + k3 * u.x));
}

// 把两档噪声的梯度当地表斜率，合成一条世界空间法线。
//
// ── strength 的量纲：斜率，不是高度 ──
//
// 直接用噪声域导数乘 strength，等价于让起伏的**高度振幅随尺度线性增长**，
// 而斜率不变。这是有意的：法线只看斜率，所以改尺度时视觉强度保持不变 ——
// 换尺度不用重调强度，这正是调参时想要的不变量。若改成「高度固定」，
// 把尺度从 40 m 拉到 80 m 会让起伏视觉上减半，每次都得补调。
//
// ── 前两档为什么不做距离淡出，第三档为什么必须做 ──
//
// detail normal 必须淡出，因为它的特征尺寸（1.5 m）在远处会掉到 1 像素以下，
// 那时它贡献的是 shimmer。前两档的特征尺寸是 40 m / 9 m：55° FOV、1280 宽下
// 一个像素在 300 m 处约 0.22 m，9 m 的特征仍有 40 像素，到 2 km 还有 6 像素。
// 它们在整个可视距离内都在 Nyquist 的安全侧 —— 所以不但不该淡出，
// 它们正是远景唯一还活着的法线来源。
//
// 第三档（2 m）不在这个安全侧：700 m 处一像素约 0.52 m，它只剩 4 像素，
// 再往远就是 shimmer。所以淡出与否不是两档之间的风格差异，而是各自的
// 特征尺寸相对像素的位置决定的 —— 同一条 Nyquist 判据给出了两个相反的答案。
//
// ── 为什么只用 XZ 投影 ──
//
// 不走 triplanar：XZ 投影在陡坡上会沿坡向把特征拉长，而拉长的方向恰好是
// 下坡方向 —— 读出来是竖直的冲蚀沟，真实崖壁本来就长这样。这里省掉的不是
// 精度而是一个会把它变糟的修正，顺带省掉 8 次 hash。
half3 VistaReliefNormalWS(float3 positionWS, half3 gn, half4 relief, half2 oct3)
{
    // relief: x = 粗尺度（米/周期）, y = 粗斜率, z = 细尺度, w = 细斜率
    // oct3:   x = 第三档尺度（米/周期）, y = 第三档斜率（**已乘过距离淡出**）
    half3 nc = VistaValueNoiseD(positionWS.xz / max(relief.x, 1e-2h));
    half2 slope = nc.yz * relief.y;
#if !defined(_VISTA_TERRAIN_MOBILE)
    // 第二、三档只在 PC 档叠。移动档留一档：两档之间的差是「有没有次级沟壑」，
    // 远小于「有没有起伏」，而成本正好翻倍（4 次 hash → 8 次）。
    half3 nf = VistaValueNoiseD(positionWS.xz / max(relief.z, 1e-2h));
    slope += nf.yz * relief.w;

    // 第三档。淡出已经乘进 oct3.y，所以这里只需要一个「还有没有强度」的早退 ——
    // 它替掉的是远场里**唯一**还能有 2 m 尺度细节的那条路（贴图在那个距离
    // 已经 mip 塌成平面），所以近场省掉它是不行的，必须一直算到淡出终点。
    if (abs(oct3.y) > 1e-3h)
    {
        half3 n3 = VistaValueNoiseD(positionWS.xz / max(oct3.x, 1e-2h));
        slope += n3.yz * oct3.y;
    }
#endif

    // 与 VistaSampleTriplanarNormalWS 的 XZ 面用同一组基（U = +X、V = +Z、
    // N = 几何法线），这样 relief 与层法线/detail 的切空间约定只有一处定义。
    half3 T = half3(1.0h, 0.0h, 0.0h) - gn * gn.x;
    T = normalize(T + half3(1e-5h, 0.0h, 0.0h));
    half3 B = cross(T, gn);

    // 高度场 h 的法线是 normalize(−∂h/∂u, −∂h/∂v, 1)。负号不是约定问题：
    // 写成正号会让凸起的受光面翻到背光侧，整片地形的明暗与形体相反。
    half3 nt = normalize(half3(-slope.x, -slope.y, 1.0h));
    return T * nt.x + B * nt.y + gn * nt.z;
}

// 两档尺度的低频噪声。输出**以 1 为中心**：`1 + (n − 0.5)·2·strength`。
// 以 1 为中心是必须的 —— 若直接乘 n，地表的平均亮度会掉一半，
// 那不是「破平铺」而是「把地面调暗」，两件事会在调参时互相掩盖。
//
// 除了亮度，还动一点饱和度。实践里真正让人看出平铺的是**颜色的重复**，
// 而不是亮度的重复；只动亮度会得到一层「脏」而不是地貌变化。
half3 VistaMacroVariation(float3 positionWS, half4 macro, half satAmount)
{
    float2 p = positionWS.xz;
    half n = VistaValueNoise2D(p / max(macro.x, 1e-2h));
#if !defined(_VISTA_TERRAIN_MOBILE)
    // 第二档只在 PC 档叠。移动档留一档：两档之间的视觉差远小于一次噪声的成本。
    half n2 = VistaValueNoise2D(p / max(macro.y, 1e-2h));
    n = lerp(n, n2, 0.4h);
#endif
    half lum = 1.0h + (n - 0.5h) * 2.0h * macro.z;
    // 饱和度用同一个 n 的另一段相位，避免亮与艳完全同步（同步会看成单一图案）。
    half ns = VistaValueNoise2D(p / max(macro.x, 1e-2h) + 37.7);
    half sat = 1.0h + (ns - 0.5h) * 2.0h * satAmount;
    return half3(lum, sat, n);
}

half3 VistaApplyMacro(half3 albedo, half3 macro)
{
    half l = Luminance(albedo);
    // 先按 sat 把颜色推离/推向自身灰度，再整体乘亮度。顺序反过来会让
    // 饱和度操作被亮度缩放放大，在暗处过冲。
    half3 c = lerp(l.xxx, albedo, macro.y);
    return max(0.0h, c * macro.x);
}

#endif // VISTA_TERRAIN_SPLAT_INCLUDED
