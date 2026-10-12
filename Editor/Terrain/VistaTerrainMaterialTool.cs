using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// Step 6 的装配工具：把场景里的 Terrain 切到 Vista/Terrain/Lit，并把
    /// **shader 算不出来的那些常量**离线算好写进材质。
    ///
    /// ── 为什么需要一个工具，而不是让 shader 自己算 ──
    ///
    /// 有两个量 shader 拿不到：
    ///  ① 每层 albedo 的**亮度均值与标准差**。高度代理要减掉均值（见
    ///     VistaTerrainSplat.hlsl 的长注释），而片元只看得到自己那一个 texel，
    ///     求不出整张图的统计量。这是典型的「离线一次、运行期零成本」。
    ///  ② 每层的 **tile 尺寸（米）**。层 UV 走世界坐标，需要把 TerrainLayer 的
    ///     tileSize 搬进材质常量；引擎只写 `_Splat#_ST`，而那是基于地形 UV 的，
    ///     两者差一个地形尺寸的换算。
    ///
    /// 对比度取 `0.5 / σ`：这样 ±1σ 的亮度起伏正好映射到高度 [0, 1] 的边界。
    /// 不这么定就得手拍一个常数，而一张对比度低的沙土图和一张对比度高的岩石图
    /// 需要的常数差一个数量级——拍一个值等于让其中一张的高度代理退化成常量，
    /// 高度混层在那一层上就等于没开。
    /// </summary>
    public static class VistaTerrainMaterialTool
    {
        const string k_ShaderName = "Vista/Terrain/Lit";
        const string k_OutputDir = "Assets/Vista/Generated";
        const string k_DetailNormalPath = k_OutputDir + "/VistaTerrainDetailNormal.png";

        // 统计用的采样窗口上限。取 1024 而不是 512，并且**按 1:1 的纹素比例**采样
        // （见 MeasureAlbedo）：把 1024² 缩到 512² 会让 GPU 选到 mip 1，而 mip 是
        // 逐级平均——它平均掉的正好就是要量的那个高频方差。工程里
        // terrain_shazi01a 的真值 σ 是 0.0335，缩采样量出来是 0.0191，
        // 对比度因此从 14.9 被抬到 26.2，高度代理直接滑向「最亮那一小片赢」。
        // 一把尺子不该把被测量先平均掉再读数。
        const int k_StatSize = 1024;

        // ==================================================================== ⑥ 逐层色调的标定常数
        //
        // 目标跨度 0.26：真实干草地/砂土在同一张 albedo 内的亮度 p05→p95 大致
        // 在 0.2~0.35 之间（地表的明暗主要来自草缝与颗粒自阴影）。取 0.26 是
        // 这个区间的中段 —— 本工程四层实测只有 0.047~0.089。
        const float k_ToneTargetSpan = 0.26f;

        // 目标色度 0.32（(max−min)/max 的均值）。同上，取自干草/砂土的常见区间；
        // 它与跨度是两件独立的事：一层可以亮暗分明而颜色发灰。
        const float k_ToneTargetChroma = 0.32f;

        // 饱和度的上限 2.0 是一个**声明的安全界，不是推出来的**。
        // 色度外推（`lerp(l, albedo, s)`，s > 1）会把最暗的那个通道推到负数，
        // 被 shader 里的 max(0, ·) 截掉 —— 截掉之后那个像素既掉饱和又偏色相，
        // 而「掉饱和」正好与这一路要做的事相反。不写成从某个判据推出来的数：
        // 拍一个阈值然后给它编一套推导，比直接承认它是拍的更坏。
        // 真正能判定「拉开后会不会出台阶」的量是报表里那个**实测**的级数。
        const float k_ToneSaturateCap = 2.0f;

        // ====================================================================
        //  主入口
        // ====================================================================
        [MenuItem("Window/Vista/Terrain/Setup Vista Terrain Material", priority = 200)]
        public static void Setup()
        {
            s_StatCache.Clear();

            var terrains = Object.FindObjectsByType<Terrain>();
            if (terrains == null || terrains.Length == 0)
            {
                Debug.LogWarning("[Vista Terrain] 当前场景里没有 Terrain。");
                return;
            }

            var shader = Shader.Find(k_ShaderName);
            if (shader == null)
            {
                Debug.LogError($"[Vista Terrain] 找不到 shader \"{k_ShaderName}\"。包是否已被工程引用？");
                return;
            }

            Directory.CreateDirectory(k_OutputDir);

            var detailNormal = EnsureDetailNormal();

            // 按「层集合」分组。四块地形如果用同一组层，就共用一份材质——
            // 每块地形各建一份不但多三份资产，还会让以后调参得改四次。
            var groups = new Dictionary<string, List<Terrain>>();
            foreach (var t in terrains)
            {
                var data = t.terrainData;
                if (data == null) continue;
                string sig = LayerSignature(data.terrainLayers);
                if (!groups.TryGetValue(sig, out var bucket))
                {
                    bucket = new List<Terrain>();
                    groups.Add(sig, bucket);
                }
                bucket.Add(t);
            }

            var sb = new StringBuilder();
            sb.AppendLine("[Vista Terrain] 装配报告");
            sb.AppendLine($"  地形 {terrains.Length} 块，层集合 {groups.Count} 组");
            sb.AppendLine($"  detail normal: {(detailNormal != null ? AssetDatabase.GetAssetPath(detailNormal) : "缺失")}");

            int groupIndex = 0;
            foreach (var kv in groups)
            {
                var list = kv.Value;
                var layers = list[0].terrainData.terrainLayers;
                string matName = groups.Count == 1 ? "VistaTerrainLit" : $"VistaTerrainLit_{groupIndex}";
                string matPath = $"{k_OutputDir}/{matName}.mat";

                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null)
                {
                    mat = new Material(shader) { name = matName };
                    AssetDatabase.CreateAsset(mat, matPath);
                }
                else if (mat.shader != shader)
                {
                    mat.shader = shader;
                }

                sb.AppendLine();
                sb.AppendLine($"  ── {matName}（{list.Count} 块地形，{layers.Length} 层）");
                if (layers.Length > 4)
                {
                    sb.AppendLine($"     [错误] 层数 {layers.Length} > 4，第 5 层起不会被渲染（没有 add-pass，理由见 shader 文件头）。");
                }

                // 层数据里有没有法线图 / mask 图 —— 这两件事只有 TerrainLayer 知道。
                // 材质上的 _Normal0..3 / _Mask0..3 是地形引擎运行期塞的，不进
                // 序列化数据，所以 ShaderGUI 拿着材质也推不出来；这里统计完写进
                // 两个属性，由 ApplyKeywords 投影成 _NORMALMAP / _MASKMAP。
                int nNormal = 0, nMask = 0;

                // ⑥ 逐层色调的标定结果。默认 1 = 恒等，所以缺层/量不到的层
                // 走的是「什么也不做」，而不是一个猜来的值。
                var toneContrast = Vector4.one;
                var toneSaturate = Vector4.one;

                for (int i = 0; i < 4; i++)
                {
                    var prop = $"_VistaLayerParams{i}";
                    if (i >= layers.Length || layers[i] == null)
                    {
                        mat.SetVector(prop, new Vector4(10f, 10f, 0.5f, 2f));
                        continue;
                    }

                    var layer = layers[i];
                    if (layer.normalMapTexture != null) nNormal++;
                    if (layer.maskMapTexture != null) nMask++;

                    var tile = layer.tileSize;
                    // tileSize 是「一次重复覆盖多少米」。0 会让 UV 除零，
                    // 引擎允许它（等于不平铺），这里回落到 1 m 而不是让 shader 里 clamp 出
                    // 一个看不出来源的巨大平铺。
                    float tx = tile.x > 1e-3f ? tile.x : 1f;
                    float ty = tile.y > 1e-3f ? tile.y : 1f;

                    var st = MeasureAlbedoCached(layer.diffuseTexture);

                    // 高度代理 = saturate(0.5 + (L - center) * contrast)，
                    // 要求把 p05 映到 0、p95 映到 1 —— 解出来就是下面两行。
                    // 不用 (均值, 0.5/σ)：那是「假定正态」的标定，而这几张图重尾
                    // （草缝的暗像素拖着一条长尾），±1σ 会把三成以上纹素压到端点，
                    // 高度代理退化成二值。分位数不假定分布，按定义只压掉一成。
                    // 中心取 (p05+p95)/2 而不是均值：均值同样会被那条暗尾拽下去。
                    //
                    // 宽度下限 0.02：低于这个值，「高度起伏」读到的就是 BC3 的
                    // 块量化台阶，不是贴图内容。纯色层落到这里 → 对比度 25，
                    // 代理接近「最亮的一小片赢」，而对一张没有高度信息的图，
                    // 这正是它该有的行为。
                    float width = Mathf.Max(st.lumaP95 - st.lumaP05, 0.02f);
                    float center = st.valid ? (st.lumaP05 + st.lumaP95) * 0.5f : 0.5f;
                    float contrast = 1f / width;

                    mat.SetVector(prop, new Vector4(tx, ty, center, contrast));

                    // ---- ⑥ 逐层色调的标定
                    // 把这一层的实测跨度拉到 k_ToneTargetSpan。下限 1：工具只拉开、
                    // 不压缩 —— 一层本来对比度够的贴图被工具压扁，是工具在改美术意图，
                    // 而「拉开」才是这里要解决的那个问题（本工程四层只有 5% 跨度）。
                    //
                    // 上限来自一个**硬约束**而不是一个挑出来的数：albedo 不能大于 1
                    // （反射的光比收到的还多）。拉伸绕 center 做，所以最亮那档落到
                    // center + (p999 − center)·c，要求它 ≤ 1 就解出 c 的上界。
                    // 用 p999 而不是真正的 max：单个异常纹素不该决定整层的拉伸幅度。
                    float span = st.LumaSpan;
                    float clipCap = st.valid
                        ? (1f - center) / Mathf.Max(st.lumaP999 - center, 1e-3f)
                        : 1f;
                    float toneC = st.valid
                        ? Mathf.Clamp(k_ToneTargetSpan / Mathf.Max(span, 0.01f), 1f, Mathf.Max(clipCap, 1f))
                        : 1f;
                    float toneS = st.valid
                        ? Mathf.Clamp(k_ToneTargetChroma / Mathf.Max(st.chromaMean, 0.01f), 1f, k_ToneSaturateCap)
                        : 1f;
                    toneContrast[i] = toneC;
                    toneSaturate[i] = toneS;

                    // smoothness 的两个因子一起打：最终值 = _Smoothness# * albedo.a，
                    // 只印乘子的话，一层「乘子 1 但 alpha≈0.03」和一层「乘子 1 且
                    // alpha≡1」在报表上长得一模一样，而画面上一个是干草、一个是镜子。
                    float sLo = layer.smoothness * st.alphaMin;
                    float sHi = layer.smoothness * st.alphaMax;
                    sb.AppendLine($"     层{i} {layer.name}: tile {tx:F2}×{ty:F2} m, " +
                                  $"亮度 p05/p50/p95 = {st.lumaP05:F4}/{st.lumaP50:F4}/{st.lumaP95:F4} " +
                                  $"(均值 {st.lumaMean:F4}, σ {st.lumaSigma:F4}) " +
                                  $"→ 中心 {center:F4}, 对比度 {contrast:F2}" +
                                  (layer.normalMapTexture != null
                                      ? $", 法线图 {layer.normalMapTexture.name}（scale {layer.normalScale:F2}）"
                                      : ", 无法线图") +
                                  (layer.maskMapTexture != null ? ", 有 mask map（高度改用 mask.b）" : ""));
                    sb.AppendLine($"          smoothness = 乘子 {layer.smoothness:F2} × albedo.a " +
                                  $"[{st.alphaMin:F3}, {st.alphaMax:F3}] → [{sLo:F3}, {sHi:F3}]" +
                                  (st.AlphaIsMap && layer.smoothness < 0.05f
                                      ? "   ← alpha 是一张有内容的 smoothness 图，却被乘子 0 整张丢掉"
                                      : ""));
                    // 印的是**实测级数**，不是从贴图位宽推的理论值：拉伸把这些
                    // 级数摊到更宽的范围上，所以「拉开后一级有多大」= 拉开后的跨度
                    // ÷ 实测级数 —— 那才是会不会读成台阶的那个量。只印「对比度 5.07」
                    // 的话，一层「有 200 级可摊」和一层「只有 6 级可摊」在报表上
                    // 长得一模一样，而画面上一个是起伏、一个是色阶。
                    int lv = Mathf.Max(st.levelsInSpan, 1);
                    sb.AppendLine($"          色调: 跨度 {span:F4} → ×{toneC:F2} = {span * toneC:F4}" +
                                  (toneC >= Mathf.Max(clipCap, 1f) - 1e-3f && toneC > 1.001f
                                      ? $"（受 albedo≤1 封顶 {clipCap:F2}）" : "") +
                                  $"; 实测 {st.levelsInSpan} 级（桶上限 {st.levelsCeiling}）" +
                                  $"→ 拉开后每级 {span * toneC / lv:F4}" +
                                  (st.levelsInSpan * 4 >= st.levelsCeiling * 3 ? "（级数读到了桶宽地板，真实级数更高）" : "") +
                                  $"; 色度 {st.chromaMean:F4} → ×{toneS:F2}" +
                                  (toneS >= k_ToneSaturateCap - 1e-3f ? $"（已封顶 {k_ToneSaturateCap:F2}）" : ""));
                }

                // 两个向量一次写完，而不是在循环里逐分量 SetVector —— 逐分量写
                // 要么读回材质再改一个通道（引擎可能还没有这个属性），要么写四遍。
                mat.SetVector("_VistaLayerContrast", toneContrast);
                mat.SetVector("_VistaLayerSaturate", toneSaturate);

                if (detailNormal != null)
                    mat.SetTexture("_VistaDetailNormal", detailNormal);

                // 「有一层带就开」而不是「全都带才开」：关键字是整个材质一份的，
                // 而 shader 里逐层还有 _LayerHasMask# 做二级门；法线那一路更简单 ——
                // 没有法线图的层绑的是默认 "bump"，解出来正好是几何法线，是恒等的。
                mat.SetFloat("_VistaLayerHasNormalMap", nNormal > 0 ? 1f : 0f);
                mat.SetFloat("_VistaLayerHasMaskMap", nMask > 0 ? 1f : 0f);
                sb.AppendLine($"     法线图 {nNormal}/{Mathf.Min(layers.Length, 4)} 层 → _NORMALMAP {(nNormal > 0 ? "开" : "关")}；" +
                              $"mask 图 {nMask}/{Mathf.Min(layers.Length, 4)} 层 → _MASKMAP {(nMask > 0 ? "开" : "关")}");
                if (nNormal == 0)
                    sb.AppendLine("     [注意] 一张法线图都没有：地表只有几何法线 + detail normal，近处会偏塑料。");

                mat.SetFloat("_VistaEnableHeightBlend", 1f);
                mat.SetFloat("_VistaEnableTriplanar", 1f);
                mat.SetFloat("_VistaEnableDetailNormal", 1f);
                mat.SetFloat("_VistaEnableMacro", 1f);
                mat.SetFloat("_VistaEnableRelief", 1f);
                mat.SetFloat("_VistaEnableLayerTone", 1f);
                mat.SetFloat("_VistaMobileTier", 0f);
                mat.SetFloat("_EnableInstancedPerPixelNormal", 1f);
                VistaTerrainLitShaderGUI.ApplyKeywords(mat);

                EditorUtility.SetDirty(mat);

                foreach (var t in list)
                {
                    Undo.RecordObject(t, "Assign Vista Terrain Material");
                    t.materialTemplate = mat;

                    // 逐像素几何法线要 drawInstanced 才成立：shader 里
                    // ENABLE_TERRAIN_PERPIXEL_NORMAL = UNITY_INSTANCING_ENABLED
                    // && _TERRAIN_INSTANCED_PERPIXEL_NORMAL。只开关键字不开
                    // drawInstanced，崖面拿到的还是插值顶点法线 —— 三平面的
                    // 输入本身就先糊了一道，而材质面板上那个勾是打着的。
                    t.drawInstanced = true;

                    // shader 文件头断言「basemap 在本工程永不生效」。把它变成
                    // 工具保证的事实，而不是一句写在注释里的假设：没有
                    // BaseMapGenShader 时走到 basemap 路径上是未定义行为。
                    t.basemapDistance = 100000f;

                    // 相邻地块的 LOD 缝：同一 groupingID + 自动连接，
                    // 否则两块地形交界处会在不同 LOD 下裂开一条缝。
                    t.groupingID = 0;
                    t.allowAutoConnect = true;

                    EditorUtility.SetDirty(t);
                }

                groupIndex++;
            }

            AssetDatabase.SaveAssets();
            Terrain.SetConnectivityDirty();
            Debug.Log(sb.ToString());
        }

        static string LayerSignature(TerrainLayer[] layers)
        {
            if (layers == null) return "<null>";
            var sb = new StringBuilder();
            foreach (var l in layers)
                sb.Append(l != null ? l.GetInstanceID().ToString() : "0").Append('|');
            return sb.ToString();
        }

        // ====================================================================
        //  albedo 统计
        // ====================================================================
        /// <summary>
        /// 一张 albedo 的统计量。亮度部分给 height-blend 的高度代理用；
        /// alpha 部分给 smoothness 判据用——URP 地形的 smoothness 是
        /// `_Smoothness# * albedo.a`，所以「这一层最后有多亮」取决于两个数，
        /// 只看 TerrainLayer 面板上那个乘子回答不了。
        /// </summary>
        public struct AlbedoStats
        {
            public bool  valid;
            public float lumaMean, lumaSigma;
            public float lumaP05, lumaP50, lumaP95;
            public float alphaMin, alphaMax, alphaMean;

            /// <summary>
            /// 平均色度 (max−min)/max。量它是为了让饱和度那一路也有标定依据：
            /// 「觉得有点灰就乘 1.15」是把结论写进参数，而饱和度的目标值
            /// 必须对着实测的色度说 —— 一张本来就很艳的贴图再乘一遍会溢出色域。
            /// 用 (max−min)/max 而不是 HSV 的 S：两者在这里同义，但前者不需要
            /// 先把线性值转回 sRGB 再求 HSV，少一道与 shader 不一致的变换。
            /// </summary>
            public float chromaMean;

            /// <summary>
            /// p05~p95 窗口内**实际被占用**的直方图桶数，即这张图在那段范围里
            /// 真正有多少个不同的亮度值。色调拉伸把这些值摊到更宽的范围上，
            /// 所以它决定了「拉开之后会不会读成台阶」—— 而这是量出来的，
            /// 不是从贴图格式的位宽推出来的（luminance 是三通道的组合，
            /// 按单通道位宽推会算得比实际悲观）。
            /// </summary>
            public int levelsInSpan;

            /// <summary>
            /// 同一窗口内桶数的上限（= span / 桶宽）。两者接近时说明读到的是
            /// **桶宽的地板**而不是贴图的级数 —— 尺子的地板与被测量同量级时，
            /// 尺子会自己伪造一个结论，所以这个数必须和上面那个一起印。
            /// </summary>
            public int levelsCeiling;

            /// <summary>p99.9。拿它当「最亮」而不是真正的 max：单个异常纹素不该决定整层的拉伸上限。</summary>
            public float lumaP999;

            /// <summary>alpha 是不是一张**有内容**的图，而不是一个常数。</summary>
            public bool AlphaIsMap => valid && (alphaMax - alphaMin) > 0.25f;
            /// <summary>alpha 恒等于 1：此时最终 smoothness 就等于 layer 上那个乘子。</summary>
            public bool AlphaIsOne => valid && alphaMin > 0.95f;

            /// <summary>亮度动态范围 p05→p95。这是「地表有没有明暗起伏」的那把尺子。</summary>
            public float LumaSpan => valid ? Mathf.Max(lumaP95 - lumaP05, 0f) : 0f;
        }

        // 单次菜单调用内的缓存：四块地形共用同一组层，不缓存的话同一张图要量四遍。
        // 每个菜单入口开头清空——跨调用留着会在贴图重导入后给出陈旧的数。
        static readonly Dictionary<Texture2D, AlbedoStats> s_StatCache = new();

        static AlbedoStats MeasureAlbedoCached(Texture2D tex)
        {
            if (tex == null) return default;
            if (s_StatCache.TryGetValue(tex, out var cached)) return cached;
            var st = MeasureAlbedo(tex);
            s_StatCache[tex] = st;
            return st;
        }

        /// <summary>
        /// 量一张 albedo 的**线性**亮度均值/标准差，以及 alpha 的范围。
        ///
        /// 走 Blit 到线性 RT 再 ReadPixels，而不是 <c>Texture2D.GetPixels</c>：
        /// 后者要求贴图 Read/Write Enabled，而工程里的地形贴图都是压缩且不可读的；
        /// 为统计几个常数去翻所有贴图的导入设置，代价远大于一次 Blit。
        /// 线性 RT 是关键——shader 里 `Luminance()` 吃的是采样后的线性值，
        /// 在 sRGB 数值上求均值会得到一个偏高的数，混层边界会整体偏移。
        /// （alpha 不参与 sRGB 解码，读回来就是原始 [0,1]，这正是 shader 里的值。）
        ///
        /// **采样窗口按 1:1 纹素比例取**，不做缩放：缩放会触发 mip 选择，
        /// 而 mip 是逐级平均，平均掉的恰好是要量的高频方差。贴图大于窗口时
        /// 取居中的一块——地形贴图本来就是平铺图，任何一块都是同一个分布的样本。
        /// </summary>
        static AlbedoStats MeasureAlbedo(Texture2D tex)
        {
            if (tex == null)
                return new AlbedoStats { valid = false, lumaMean = 0.5f, lumaSigma = 0.25f };

            int sw = Mathf.Max(tex.width, 1);
            int sh = Mathf.Max(tex.height, 1);
            int size = Mathf.Clamp(Mathf.Min(sw, sh), 4, k_StatSize);

            // uv = uv * scale + offset，居中裁一块 size×size 的原始纹素
            var scale = new Vector2(size / (float)sw, size / (float)sh);
            var offset = new Vector2(0.5f - scale.x * 0.5f, 0.5f - scale.y * 0.5f);

            // depthBuffer = 0，浮点格式 → RT 是线性的，Blit 时源贴图的 sRGB 会被解码。
            var rt = RenderTexture.GetTemporary(size, size, 0, GraphicsFormat.R16G16B16A16_SFloat);

            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt, scale, offset);

            var readback = new Texture2D(size, size, TextureFormat.RGBAHalf, false, true);
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            readback.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            var px = readback.GetPixels();
            Object.DestroyImmediate(readback);

            // 单趟 Welford：不是为了速度，是为了不在 size=1024 时让
            // Σx² 与 (Σx)²/n 相减掉有效位——那种写法在均值远大于方差时会给出负方差。
            //
            // 同时打一张亮度直方图。σ 回答不了「对比度该摆哪儿」：地形 albedo 是
            // 草叶间的暗缝 + 大片中调，重尾，按 ±1σ 标定会把三成以上的纹素压到
            // 0 或 1 上，高度代理退化成二值掩膜——而那正是黑斑能成片出现的成因。
            // 分位数是分布无关的：p05/p95 映到 [0,1]，按定义只有一成纹素被压。
            // 4096 桶在 [0,1] 上的精度是 2.4e-4，比这几张图的 σ（0.015~0.04）
            // 细两个数量级，桶宽不会成为结论的来源。
            const int kBins = 4096;
            var hist = new int[kBins];

            double mean = 0.0, m2 = 0.0, aSum = 0.0, cSum = 0.0;
            float aMin = float.MaxValue, aMax = float.MinValue;
            int n = 0;
            foreach (var c in px)
            {
                // 与 HLSL 的 Luminance() 同一组系数（Rec.709）。
                double y = 0.2126729 * c.r + 0.7151522 * c.g + 0.0721750 * c.b;
                n++;
                double d = y - mean;
                mean += d / n;
                m2 += d * (y - mean);

                hist[Mathf.Clamp((int)(y * kBins), 0, kBins - 1)]++;

                // 色度搭这一趟的车。单独再跑一趟只为了一个均值，等于把同一批
                // 纹素读两遍 —— 而且两趟之间若有人改了裁剪窗口，两个统计量
                // 就会悄悄建立在不同的样本上。
                float hi = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                float lo = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                cSum += (hi - lo) / Mathf.Max(hi, 1e-4f);

                aSum += c.a;
                if (c.a < aMin) aMin = c.a;
                if (c.a > aMax) aMax = c.a;
            }

            // 级数与桶上限在初始化器外面算：`out` 声明写在对象初始化器里，
            // 成员赋值顺序与变量作用域都要靠编译器的细则担保，不值得赌。
            int occupied = CountOccupiedBins(hist, n, 0.05f, 0.95f, out int binCeiling);

            return new AlbedoStats
            {
                valid     = n > 1,
                lumaMean  = Mathf.Clamp01((float)mean),
                lumaSigma = n > 1 ? Mathf.Sqrt((float)(m2 / (n - 1))) : 0f,
                lumaP05   = HistPercentile(hist, n, 0.05f),
                lumaP50   = HistPercentile(hist, n, 0.50f),
                lumaP95   = HistPercentile(hist, n, 0.95f),
                lumaP999  = HistPercentile(hist, n, 0.999f),
                levelsInSpan  = occupied,
                levelsCeiling = binCeiling,
                alphaMin  = n > 0 ? Mathf.Clamp01(aMin) : 1f,
                alphaMax  = n > 0 ? Mathf.Clamp01(aMax) : 1f,
                alphaMean = n > 0 ? Mathf.Clamp01((float)(aSum / n)) : 1f,
                chromaMean = n > 0 ? Mathf.Clamp01((float)(cSum / n)) : 0f,
            };
        }

        /// <summary>
        /// 数 [qLo, qHi] 分位之间有多少个**非空**桶，并同时给出该区间的桶数上限。
        /// 两个一起返回而不是只返回前者：只给占用数的话，「这张图只有 12 级」
        /// 和「这段区间总共只放得下 12 个桶」在报表上无法区分。
        /// </summary>
        static int CountOccupiedBins(int[] hist, int total, float qLo, float qHi, out int ceiling)
        {
            ceiling = 0;
            if (total <= 0) return 0;

            long lo = (long)(qLo * total);
            long hi = (long)(qHi * total);
            long acc = 0;
            int occupied = 0;
            for (int i = 0; i < hist.Length; i++)
            {
                long next = acc + hist[i];
                // 这个桶里有落在 [lo, hi] 区间内的样本吗
                if (hist[i] > 0 && next > lo && acc < hi)
                {
                    occupied++;
                }
                if (next > lo && acc < hi)
                    ceiling++;
                acc = next;
            }
            return occupied;
        }

        /// <summary>直方图分位数。返回桶中心，所以结果永远落在 (0, 1) 开区间内。</summary>
        static float HistPercentile(int[] hist, int total, float q)
        {
            if (total <= 0) return 0.5f;
            long want = (long)(q * total);
            long acc = 0;
            for (int i = 0; i < hist.Length; i++)
            {
                acc += hist[i];
                if (acc > want) return (i + 0.5f) / hist.Length;
            }
            return 1f - 0.5f / hist.Length;
        }

        // ====================================================================
        //  程序化 detail normal
        // ====================================================================
        /// <summary>
        /// 生成一张**可无缝平铺**的细粒法线图。
        ///
        /// 为什么要生成而不是留空：`_VistaDetailNormal` 的默认值是 "bump"（平法线），
        /// 此时 detail 那一路算出来的 `nd == geomNormalWS`，加法项恒为 0——
        /// 功能开着，画面上什么也不会发生。一个开着却没有输入的特性
        /// 与一个没实现的特性在画面上无法区分，所以这里补上默认输入。
        ///
        /// 平铺性由「哈希在整数格上按频率取模」保证：每一档八度的格子数都整除
        /// 纹理宽度，因此 u=0 与 u=1 取到同一个格点。不取模的 fBm 在接缝处
        /// 会有一条可见的法线突变线，而 detail normal 是逐米平铺的——
        /// 那条线会每米出现一次，比不加 detail 更糟。
        /// </summary>
        static Texture2D EnsureDetailNormal()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(k_DetailNormalPath);
            if (existing != null)
                return existing;

            const int N = 512;
            var height = new float[N * N];

            // 四档八度，频率都是 2 的幂且整除 N → 逐档平铺
            int[] freqs = { 16, 32, 64, 128 };
            float[] amps = { 1.0f, 0.5f, 0.25f, 0.125f };
            float ampSum = 0f;
            foreach (var a in amps) ampSum += a;

            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float h = 0f;
                for (int o = 0; o < freqs.Length; o++)
                    h += amps[o] * PeriodicValueNoise(x / (float)N * freqs[o], y / (float)N * freqs[o], freqs[o]);
                height[y * N + x] = h / ampSum;
            }

            // Sobel 求梯度。bumpScale 决定法线的陡度；1.5 是试出来的
            // 「近处看得出颗粒、不至于把地表打成砂纸」的量级，运行期还有
            // `_VistaDetailParams.y` 可以整体缩放，所以这里不必精确。
            const float bumpScale = 1.5f;
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float hl = height[y * N + ((x - 1 + N) % N)];
                float hr = height[y * N + ((x + 1) % N)];
                float hd = height[((y - 1 + N) % N) * N + x];
                float hu = height[((y + 1) % N) * N + x];

                var n = new Vector3((hl - hr) * bumpScale * N / 512f,
                                    (hd - hu) * bumpScale * N / 512f,
                                    1f).normalized;
                px[y * N + x] = new Color32(
                    (byte)Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255f),
                    (byte)Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255f),
                    (byte)Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255f),
                    255);
            }

            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false, true);
            tex.SetPixels32(px);
            tex.Apply(false, false);
            var png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);

            Directory.CreateDirectory(k_OutputDir);
            File.WriteAllBytes(k_DetailNormalPath, png);
            AssetDatabase.ImportAsset(k_DetailNormalPath, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(k_DetailNormalPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.sRGBTexture = false;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(k_DetailNormalPath);
        }

        static float Hash(int x, int y)
        {
            // 整数哈希：同一格点无论从哪个方向绕过来都给同一个值，
            // 这是平铺性的前提。浮点 frac 哈希在大坐标上会因精度丢失而破缝。
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }

        static float PeriodicValueNoise(float x, float y, int period)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float xf = x - xi, yf = y - yi;
            float ux = xf * xf * (3f - 2f * xf);
            float uy = yf * yf * (3f - 2f * yf);

            int x0 = ((xi % period) + period) % period;
            int y0 = ((yi % period) + period) % period;
            int x1 = (x0 + 1) % period;
            int y1 = (y0 + 1) % period;

            float a = Hash(x0, y0), b = Hash(x1, y0), c = Hash(x0, y1), d = Hash(x1, y1);
            return Mathf.Lerp(Mathf.Lerp(a, b, ux), Mathf.Lerp(c, d, ux), uy);
        }

        // ====================================================================
        //  TerrainLayer 的 smoothness 体检
        // ====================================================================
        /// <summary>
        /// 报告（并可选修正）TerrainLayer 的 smoothness 配置。
        ///
        /// ── 判据为什么不是「乘子高 = 可疑」 ──
        ///
        /// 最终 smoothness = `_Smoothness#` × `albedo.a`，两个因子缺一不可判。
        /// 一个高乘子在一张 alpha≈0.03 的图上得到 0.03（完全正常），在一张
        /// alpha≡1 的图上得到 1（整块地形是镜面）。光看乘子分不开这两种情况，
        /// **而这恰好就是要分开的那两种解释**——所以这里先把 alpha 量出来。
        ///
        /// 于是有两类真问题，方向相反：
        ///  · **A 类**：乘子 ≈ 0，而 alpha 有实质动态范围 → 作者给了一张
        ///    smoothness 图（工程里 `*_as.tga` 的 a 就是 albedo+smoothness 的 s），
        ///    却被整张乘没了。画面上是「这一层永远不反光」，而那不是作者的意思。
        ///  · **B 类**：乘子高，而 alpha 恒 ≈ 1 → 没有 smoothness 图，最终值
        ///    就是乘子本身，地表镜面化。
        ///
        /// 两类都不是 Vista shader 引入的，URP 的 TerrainLit 完全一样；
        /// 只是原来地形关着看不出来。
        ///
        /// 单独一个菜单项、不塞进装配流程：它改的是**共享的 TerrainLayer 资产**，
        /// 可能被别的场景引用。把它和「建材质」绑在一起，等于让一次材质装配
        /// 顺手改掉别处的画面。
        /// </summary>
        [MenuItem("Window/Vista/Terrain/Report TerrainLayer Smoothness", priority = 201)]
        public static void ReportSmoothness()
        {
            s_StatCache.Clear();

            var sb = new StringBuilder();
            sb.AppendLine("[Vista Terrain] TerrainLayer smoothness 报告");
            sb.AppendLine("  最终 smoothness = 乘子 × albedo.a；两个因子都印出来，" +
                          "因为只看乘子分不开「干草」和「镜子」。");

            int a = 0, b = 0, total = 0;
            foreach (var (layer, st) in EnumerateLayers())
            {
                total++;
                var cls = ClassifyLayer(layer, st);
                if (cls == SmoothnessIssue.MapDiscarded) a++;
                else if (cls == SmoothnessIssue.Mirror) b++;

                sb.AppendLine($"  {layer.name}: 乘子 {layer.smoothness:F2} × " +
                              $"a[{st.alphaMin:F3}, {st.alphaMax:F3}] 均值 {st.alphaMean:F3} " +
                              $"→ [{layer.smoothness * st.alphaMin:F3}, {layer.smoothness * st.alphaMax:F3}]" +
                              $"，metallic {layer.metallic:F2}{IssueNote(cls)}");
            }

            sb.AppendLine($"  层 {total}：A 类（smoothness 图被丢弃）{a}，B 类（镜面化）{b}");
            if (a + b > 0)
                sb.AppendLine("  修正：Window/Vista/Terrain/Fix TerrainLayer Smoothness" +
                              "（A 类写 1.00 放开贴图，B 类写 0.20，均可撤销）");
            else
                sb.AppendLine("  没有需要改的层。");
            Debug.Log(sb.ToString());
        }

        [MenuItem("Window/Vista/Terrain/Fix TerrainLayer Smoothness", priority = 202)]
        public static void FixSmoothness()
        {
            s_StatCache.Clear();

            var changed = new List<string>();
            foreach (var (layer, st) in EnumerateLayers())
            {
                var cls = ClassifyLayer(layer, st);
                if (cls == SmoothnessIssue.None) continue;

                // A 类写 1.00 而不是某个中间值：alpha 已经是作者给的完整
                // [0,1] 分布，乘子的职责是「让它原样生效」，任何 < 1 的值
                // 都是在二次调色，而那属于美术，不属于修正工具。
                float target = cls == SmoothnessIssue.MapDiscarded ? 1.0f : 0.2f;
                Undo.RecordObject(layer, "Fix TerrainLayer Smoothness");
                changed.Add($"  {layer.name}: {layer.smoothness:F2} → {target:F2}" +
                            $"（{(cls == SmoothnessIssue.MapDiscarded ? "A 类：放开 albedo.a 里的 smoothness 图" : "B 类：alpha 恒 1，压掉镜面")}）");
                layer.smoothness = target;
                EditorUtility.SetDirty(layer);
            }

            AssetDatabase.SaveAssets();
            Debug.Log(changed.Count == 0
                ? "[Vista Terrain] 没有需要修正的层，未改动。"
                : "[Vista Terrain] 已修正 TerrainLayer smoothness：\n" + string.Join("\n", changed) +
                  "\n  改完请重跑 Window/Vista/Terrain/Setup Vista Terrain Material 看报表确认。");
        }

        enum SmoothnessIssue { None, MapDiscarded, Mirror }

        static SmoothnessIssue ClassifyLayer(TerrainLayer l, AlbedoStats st)
        {
            if (!st.valid) return SmoothnessIssue.None;
            if (st.AlphaIsMap && l.smoothness < 0.05f) return SmoothnessIssue.MapDiscarded;
            if (st.AlphaIsOne && l.smoothness > 0.6f)  return SmoothnessIssue.Mirror;
            return SmoothnessIssue.None;
        }

        static string IssueNote(SmoothnessIssue c) => c switch
        {
            SmoothnessIssue.MapDiscarded =>
                "   ← A 类：albedo.a 是一张有内容的 smoothness 图，被乘子 0 整张丢掉",
            SmoothnessIssue.Mirror =>
                "   ← B 类：albedo.a 恒为 1，最终 smoothness 就是这个乘子，地表会镜面化",
            _ => "",
        };

        /// <summary>场景里所有地形用到的 TerrainLayer，去重，带 albedo 统计。</summary>
        static IEnumerable<(TerrainLayer layer, AlbedoStats stats)> EnumerateLayers()
        {
            var seen = new HashSet<TerrainLayer>();
            foreach (var t in Object.FindObjectsByType<Terrain>())
            {
                if (t.terrainData == null || t.terrainData.terrainLayers == null) continue;
                foreach (var l in t.terrainData.terrainLayers)
                {
                    if (l == null || !seen.Add(l)) continue;
                    yield return (l, MeasureAlbedoCached(l.diffuseTexture));
                }
            }
        }

        // ====================================================================
        //  层法线振幅：量 + 标定
        // ====================================================================

        /// <summary>8-bit 通道映射到 [-1,1] 后的量化步长。这是法线图能表达的最小切向增量。</summary>
        const float k_NormalQuantStep = 2f / 255f;   // 0.00784

        /// <summary>
        /// 目标切向 RMS 振幅。0.30 对应约 17° 的典型面扰动 —— 砂土/碎石类
        /// 地表法线图的常见量级，近处能看出颗粒感而不至于把几何法线盖掉。
        /// </summary>
        const float k_NormalTargetAmp = 0.30f;

        /// <summary>
        /// 量化地板的判定倍数。振幅低于 2× 步长时，「起伏」与「量化台阶」
        /// 同量级 —— 一把尺子的地板与被测量同量级时，尺子会自己伪造一个结论，
        /// 而放大它放大的是条带不是起伏。这类层必须走程序化那一路（⑤ 中频起伏），
        /// 程序化没有量化地板，所以不存在这个下限。
        /// </summary>
        const float k_NormalFloorFactor = 2f;

        /// <summary>
        /// normalScale 的上限。**声明的界，不是推出来的**：放大同时放大信号与
        /// 量化噪声，信噪比与 scale 无关，所以「会不会出条带」推不出一个 scale 上界。
        /// 定在 4 的真实理由是**有更好的工具**：4× 之后放大后的步长到 0.031
        /// （≈1.8° 的面阶跃），而需要比这更大振幅的层，该由 ⑤ 中频起伏去给 ——
        /// 那一路没有量化地板也没有 mip 塌缩。放大只负责把贴图里**已经有**的
        /// 细节推到可见，不负责无中生有。
        /// </summary>
        const float k_NormalScaleCap = 4f;

        struct NormalStats
        {
            public bool  valid;
            public float ampX, ampY;      // 切向分量的 RMS（已映射到 [-1,1]）
            public bool  dxt5nm;          // X 存在 alpha 里（R 恒为 1）
            public int   width, height;

            /// <summary>两轴合成的 RMS。法线只看斜率大小，分轴区分对标定没有意义。</summary>
            public float Amp => Mathf.Sqrt((ampX * ampX + ampY * ampY) * 0.5f);

            /// <summary>振幅是不是已经掉到 8-bit 量化地板上。</summary>
            public bool AtQuantFloor => valid && Amp < k_NormalFloorFactor * k_NormalQuantStep;
        }

        static readonly Dictionary<Texture2D, NormalStats> s_NormalCache = new();

        static NormalStats MeasureNormalCached(Texture2D tex)
        {
            if (tex == null) return new NormalStats { valid = false };
            if (s_NormalCache.TryGetValue(tex, out var s)) return s;
            s = MeasureNormal(tex);
            s_NormalCache[tex] = s;
            return s;
        }

        /// <summary>
        /// 量一张法线图的切向振幅。与 <see cref="MeasureAlbedo"/> 同一条路：
        /// Blit 到线性 float RT、1:1 纹素比例裁一块，**不缩放** —— mip 是逐级平均，
        /// 平均掉的恰好是要量的那个高频振幅（实测这几张图从 1×1 到 16×16 盒滤波，
        /// 振幅腰斩）。
        ///
        /// 通道布局要**量出来**而不是假定：桌面端法线图走 DXT5nm（X 在 alpha、
        /// R 恒为 1），而未压缩或 BC5 的图是 X 在 R。按 R 是否恒为 1 判，
        /// 判错的话两个振幅会一个恒为 0、一个是真值 —— 而「恒为 0」与
        /// 「这张图真的没有 X 方向起伏」在报表上无法区分，所以把判据印出来。
        /// </summary>
        static NormalStats MeasureNormal(Texture2D tex)
        {
            if (tex == null) return new NormalStats { valid = false };

            int sw = Mathf.Max(tex.width, 1);
            int sh = Mathf.Max(tex.height, 1);
            int size = Mathf.Clamp(Mathf.Min(sw, sh), 4, k_StatSize);

            var scale = new Vector2(size / (float)sw, size / (float)sh);
            var offset = new Vector2(0.5f - scale.x * 0.5f, 0.5f - scale.y * 0.5f);

            var rt = RenderTexture.GetTemporary(size, size, 0, GraphicsFormat.R16G16B16A16_SFloat);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt, scale, offset);

            var readback = new Texture2D(size, size, TextureFormat.RGBAHalf, false, true);
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            readback.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            var px = readback.GetPixels();
            Object.DestroyImmediate(readback);

            float rMin = float.MaxValue;
            foreach (var c in px) if (c.r < rMin) rMin = c.r;
            bool dxt5nm = rMin > 0.98f;

            double sx = 0.0, sy = 0.0;
            int n = 0;
            foreach (var c in px)
            {
                float x = (dxt5nm ? c.a : c.r) * 2f - 1f;
                float y = c.g * 2f - 1f;
                sx += (double)x * x;
                sy += (double)y * y;
                n++;
            }

            return new NormalStats
            {
                valid  = n > 0,
                ampX   = n > 0 ? Mathf.Sqrt((float)(sx / n)) : 0f,
                ampY   = n > 0 ? Mathf.Sqrt((float)(sy / n)) : 0f,
                dxt5nm = dxt5nm,
                width  = sw,
                height = sh,
            };
        }

        /// <summary>
        /// 这一层的 normalScale 该是多少，以及为什么。返回 scale ≤ 0 表示「不要动」。
        /// </summary>
        static (float scale, string note) PlanNormalScale(TerrainLayer l, NormalStats ns)
        {
            if (l.normalMapTexture == null || !ns.valid)
                return (-1f, "无法线图");

            if (ns.AtQuantFloor)
                return (-1f, $"   ← 拒绝放大：振幅 {ns.Amp:F4} 已在 8-bit 量化步长 " +
                             $"{k_NormalQuantStep:F4} 的 {ns.Amp / k_NormalQuantStep:F1} 倍以内，" +
                             "放大只放大条带。这一层的起伏交给 ⑤ 中频起伏（程序化，无量化地板）");

            float want = Mathf.Clamp(k_NormalTargetAmp / Mathf.Max(ns.Amp, 1e-4f), 1f, k_NormalScaleCap);
            if (Mathf.Abs(want - l.normalScale) < 0.05f)
                return (-1f, "   ← 已经是目标值");

            return (want, $"   → normalScale {l.normalScale:F2} → {want:F2}" +
                          (want >= k_NormalScaleCap - 1e-3f
                              ? $"（封顶 {k_NormalScaleCap:F1}，剩下的振幅由 ⑤ 中频起伏补）"
                              : "") +
                          $"；放大后量化步长 {k_NormalQuantStep * want:F4}");
        }

        /// <summary>
        /// 层法线振幅报告。单独一项、与 Setup 分开，理由同 smoothness 那一对：
        /// 它要改的是**共享的 TerrainLayer 资产**，可能被别的场景引用。
        /// </summary>
        [MenuItem("Window/Vista/Terrain/Report TerrainLayer Normal Amplitude", priority = 203)]
        public static void ReportNormalAmplitude()
        {
            s_NormalCache.Clear();

            var sb = new StringBuilder();
            sb.AppendLine("[Vista Terrain] TerrainLayer 法线振幅报告");
            sb.AppendLine($"  切向 RMS 的目标 {k_NormalTargetAmp:F2}；8-bit 量化步长 {k_NormalQuantStep:F4}，" +
                          $"振幅低于它的 {k_NormalFloorFactor:F0} 倍时拒绝放大。");

            int n = 0, fixable = 0, refused = 0;
            foreach (var (layer, _) in EnumerateLayers())
            {
                if (layer.normalMapTexture == null)
                {
                    sb.AppendLine($"  {layer.name}: 无法线图");
                    continue;
                }

                n++;
                var ns = MeasureNormalCached(layer.normalMapTexture);
                var (want, note) = PlanNormalScale(layer, ns);
                if (want > 0f) fixable++;
                else if (ns.AtQuantFloor) refused++;

                sb.AppendLine($"  {layer.name}: {layer.normalMapTexture.name} {ns.width}×{ns.height} " +
                              $"[{(ns.dxt5nm ? "DXT5nm: X 在 alpha" : "X 在 R")}] " +
                              $"振幅 x/y = {ns.ampX:F4}/{ns.ampY:F4} → 合成 {ns.Amp:F4}" +
                              $"（= 量化步长的 {ns.Amp / k_NormalQuantStep:F1} 倍），" +
                              $"当前 scale {layer.normalScale:F2}{note}");
            }

            sb.AppendLine($"  有法线图的层 {n}：可放大 {fixable}，因量化地板拒绝 {refused}");
            if (fixable > 0)
                sb.AppendLine("  修正：Window/Vista/Terrain/Fix TerrainLayer Normal Amplitude（可撤销）");
            if (refused > 0)
                sb.AppendLine("  被拒绝的层不是「没救」：它们的中远距离起伏本来就该由 ⑤ 中频起伏给 —— " +
                              "贴图法线还有 mip 塌缩（实测 1×1→16×16 振幅腰斩），程序化解析导数没有。");
            Debug.Log(sb.ToString());
        }

        [MenuItem("Window/Vista/Terrain/Fix TerrainLayer Normal Amplitude", priority = 204)]
        public static void FixNormalAmplitude()
        {
            s_NormalCache.Clear();

            var changed = new List<string>();
            foreach (var (layer, _) in EnumerateLayers())
            {
                if (layer.normalMapTexture == null) continue;
                var ns = MeasureNormalCached(layer.normalMapTexture);
                var (want, _) = PlanNormalScale(layer, ns);
                if (want <= 0f) continue;

                Undo.RecordObject(layer, "Fix TerrainLayer Normal Amplitude");
                changed.Add($"  {layer.name}: scale {layer.normalScale:F2} → {want:F2}" +
                            $"（振幅 {ns.Amp:F4} → {ns.Amp * want:F4}）");
                layer.normalScale = want;
                EditorUtility.SetDirty(layer);
            }

            AssetDatabase.SaveAssets();
            Debug.Log(changed.Count == 0
                ? "[Vista Terrain] 没有可放大的层（或都已在目标值 / 被量化地板拒绝），未改动。"
                : "[Vista Terrain] 已标定 TerrainLayer 法线振幅：\n" + string.Join("\n", changed) +
                  "\n  注意：normalScale 改的是**运行期**的解包强度，贴图本身没动，" +
                  "所以量到的振幅不会变，变的是它进画面的幅度。");
        }

        // ------------------------------------------------------------------
        //  各向异性过滤 + 三线性
        //
        //  为什么这是地形**必须**改的导入设置，而不是一项可选的画质开关：
        //
        //  地形层 UV 是世界 XZ 投影。相机离地 1.7 m 看向远处时，地面像素的
        //  UV 足迹长短轴之比约等于 1/sin(掠射角)，在 200 m 外轻易超过 10:1。
        //  各向同性过滤只有一个 mip 等级可选，为了不在长轴上出摩尔纹，硬件
        //  必须按**长轴**挑 mip —— 于是短轴方向被连带糊掉同样的倍数。
        //  画出来就是沿视线方向的「拉丝」：远处地面像刷过的金属，而不是地面。
        //
        //  aniso 是唯一能修它的东西，而且它的成本是**按需**的：硬件只在足迹
        //  真的被拉长的像素上多取样，正对着看的地面一次都不多采。
        //  所以它不是「用性能换画质」的旋钮，而是「只在需要的地方花钱」。
        //  UE5 landscape、HDRP 的 terrain 预设都默认 8，这里取同一个值。
        //
        //  filterMode 顺带一起修：本工程有两张 albedo 是 Bilinear。Bilinear
        //  **没有 mip 之间的插值**，远景会出可见的 mip 环 —— 一圈一圈的清晰度
        //  突变。那不是「略差一点」，是一个结构性的瑕疵。
        //
        //  只改地形层用到的贴图，不全局扫 —— 一个工具改到它没在量的资产上，
        //  下次出问题时没人知道是谁改的。
        [MenuItem("Window/Vista/Terrain/Fix Terrain Texture Filtering", priority = 205)]
        public static void FixTextureFiltering()
        {
            const int k_Aniso = 8;

            var texes = new HashSet<Texture2D>();
            foreach (var (layer, _) in EnumerateLayers())
            {
                if (layer.diffuseTexture != null) texes.Add(layer.diffuseTexture);
                if (layer.normalMapTexture != null) texes.Add(layer.normalMapTexture);
                if (layer.maskMapTexture != null) texes.Add(layer.maskMapTexture);
            }

            var changed = new List<string>();
            var skipped = new List<string>();
            foreach (var tex in texes)
            {
                var path = AssetDatabase.GetAssetPath(tex);
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) { skipped.Add($"  {tex.name}: 不是 TextureImporter 管的资产"); continue; }

                bool needAniso  = imp.anisoLevel < k_Aniso;
                // FilterMode.Trilinear == 2。Point/Bilinear 都要改；已经是 Trilinear 的不动。
                bool needFilter = imp.filterMode != FilterMode.Trilinear;
                // mipmap 关掉的话 aniso 与 trilinear 都无从谈起 —— 那是另一个问题，
                // 单独报出来而不是顺手打开：地形层贴图没有 mip 是个该让人看见的异常。
                if (!imp.mipmapEnabled)
                {
                    skipped.Add($"  {tex.name}: **没有 mipmap** —— aniso/trilinear 对它无效，请先在导入设置里开 Generate Mip Maps");
                    continue;
                }
                if (!needAniso && !needFilter) continue;

                var before = $"aniso {imp.anisoLevel} / {imp.filterMode}";
                if (needAniso)  imp.anisoLevel = k_Aniso;
                if (needFilter) imp.filterMode = FilterMode.Trilinear;
                EditorUtility.SetDirty(imp);
                imp.SaveAndReimport();
                changed.Add($"  {tex.name}: {before} → aniso {imp.anisoLevel} / {imp.filterMode}");
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(changed.Count == 0
                ? "[Vista Terrain] 地形层贴图的过滤设置已经达标，未改动。"
                : "[Vista Terrain] 已修正地形层贴图的过滤设置：\n" + string.Join("\n", changed));
            if (skipped.Count > 0) sb.AppendLine("  跳过：\n" + string.Join("\n", skipped));

            // 工程级开关。没有它，上面逐贴图改的 aniso 被整体忽略 —— 那一步就
            // 成了一个「开着却没有输入的特性」，在画面上与没做过无法区分。
            // 这不是顺手多改一个设置：它是上面那些改动**能不能被判定**的前提。
            // （第一次跑出来正是 Disable，7 张贴图的 aniso 值一直是死的。）
            //
            // 选 Enable（面板上叫 Per Texture）而不是 ForceEnable：前者只是「尊重
            // 每张贴图自己写的值」，所以它对没要求 aniso 的贴图**完全没有影响**；
            // ForceEnable 会把全工程每张贴图都拉到至少 9，那是在一个地形工具里
            // 偷偷改掉 UI、粒子、天空盒的采样成本。
            if (QualitySettings.anisotropicFiltering == AnisotropicFiltering.Disable)
            {
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
                sb.AppendLine("  **工程设置**：Anisotropic Textures 原本是 Disable —— 贴图上的 aniso 值被整体忽略。" +
                              "已改为 Per Texture（只尊重各贴图自己的值，不影响其它资产）。");
            }
            else
            {
                sb.AppendLine($"  工程设置 Anisotropic Textures = {QualitySettings.anisotropicFiltering}，未改动。");
            }
            Debug.Log(sb.ToString());
        }
    }
}
