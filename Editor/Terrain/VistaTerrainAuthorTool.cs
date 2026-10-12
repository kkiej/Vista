using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Vista.Editor
{
    /// <summary>
    /// 为 Step 6 生成**测试用的地形内容**：高度场与 splatmap。
    ///
    /// ── 为什么需要这个工具 ──
    ///
    /// 四项技术（height-blend / 陡坡三平面 / detail normal / macro variation）
    /// 都是「对着地表内容做的事」。场景里的四块地形是空白的：没有层、没有起伏。
    /// 一个开着却没有输入的特性与一个没实现的特性在画面上无法区分 —— 在没有
    /// 崖面的地形上，三平面投影恒等于单平面投影；在只有一层的地形上，
    /// height-blend 的归一化恒等于 1。所以内容不是「顺手补的美术」，
    /// 它是这四项技术能不能被看见的前提。
    ///
    /// ── 高度场为什么长这样 ──
    ///
    /// 不是「随便生成点起伏」，而是**按四项技术各自的判据反推**出来的三项叠加：
    ///  · 低频丘陵（±26 m / 240 m）——提供大片缓坡与平地。平地上 triplanar 权重
    ///    必须退化到 0，这是它的「不该生效」那一侧。
    ///  · 山脊（ridged fBm，只在丘陵高处出现）——提供 30°~50° 的中等坡度，
    ///    那是 splat 过渡带最长、height-blend 最看得出来的地方。
    ///  · 硬崖（低频场过一条很窄的 smoothstep）——18 m 落差发生在场值变化 0.06
    ///    所需的水平距离上（约 4 m），坡度 ~77°。这是三平面投影唯一能救回来的一档：
    ///    单平面投影在这里会把贴图沿竖直方向拉成条状。
    ///
    /// 所有噪声都在**世界坐标**上求值，所以四块地形拼接处天然连续 ——
    /// A 块最后一列顶点与 B 块第一列顶点世界坐标相同，取到同一个噪声值。
    /// 按地形局部 UV 生成的话，每条拼缝上都会有一道高度断层。
    ///
    /// ── 覆盖保护 ──
    ///
    /// 两个入口都会先检查地形是不是空白的（起伏 &lt; 5 m / 层数为 0），
    /// 不是就拒绝并指向带 (Force) 的那一项。不用弹窗确认：模态对话框会
    /// 把从脚本触发菜单的调用挂住。
    /// </summary>
    public static class VistaTerrainAuthorTool
    {
        // 四层的语义。工程里只有草×2 + 沙泥×2，没有岩石图，所以陡崖用沙泥：
        // 「太陡了长不住植被，露出下面的土」——这本身就是真实地貌的成因，
        // 而三平面投影要解决的是投影拉伸，与贴图是土还是岩无关。
        static readonly string[] k_LayerPaths =
        {
            "Assets/Art/Terrain/TerrainLayer/1layer.terrainlayer",  // 0 草：缓坡主体
            "Assets/Art/Terrain/TerrainLayer/2layer.terrainlayer",  // 1 沙泥：陡坡裸露
            "Assets/Art/Terrain/TerrainLayer/3Layer.terrainlayer",  // 2 草 02：斑块变化
            "Assets/Art/Terrain/TerrainLayer/4Layer.terrainlayer",  // 3 沙砾：低洼地
        };

        // 高度场的世界纵向映射。地形 size.y 取 140 m、零平面抬到 40 m：
        // HeightMeters() 的输出范围约 [-26, +83]，这样两端都还有余量，
        // 而 16-bit 高度图在 140 m 上的量化台阶是 2 mm —— 远小于 detail normal
        // 的特征尺寸，不会在缓坡上量化出台阶纹。
        const float k_SizeY = 140f;
        const float k_HeightOffset = 40f;

        const int k_AlphamapRes = 512;

        // ====================================================================
        //  菜单
        // ====================================================================
        [MenuItem("Window/Vista/Terrain/1. Sculpt Test Terrain", priority = 100)]
        public static void SculptMenu() => Sculpt(false);

        [MenuItem("Window/Vista/Terrain/1. Sculpt Test Terrain (Force)", priority = 101)]
        public static void SculptForceMenu() => Sculpt(true);

        [MenuItem("Window/Vista/Terrain/2. Paint Test Splatmap", priority = 102)]
        public static void PaintMenu() => Paint(false);

        [MenuItem("Window/Vista/Terrain/2. Paint Test Splatmap (Force)", priority = 103)]
        public static void PaintForceMenu() => Paint(true);

        // ====================================================================
        //  ① 高度场
        // ====================================================================
        public static void Sculpt(bool force)
        {
            var terrains = Object.FindObjectsByType<Terrain>();
            if (terrains.Length == 0)
            {
                Debug.LogWarning("[Vista Terrain] 当前场景里没有 Terrain。");
                return;
            }

            var flatten = BuildFlattenZone(terrains);

            var sb = new StringBuilder();
            sb.AppendLine($"[Vista Terrain] 雕刻完成：{terrains.Length} 块地形");
            if (flatten.HasValue)
                sb.AppendLine($"  保留平坦区（现有场景物件的 xz 包围盒 + 25 m）：{flatten.Value}");
            else
                sb.AppendLine("  场景里没有其它 Renderer，未保留平坦区");

            int done = 0;
            foreach (var t in terrains)
            {
                var td = t.terrainData;
                if (td == null) continue;

                int res = td.heightmapResolution;
                float relief = MeasureRelief(td);
                if (!force && relief > 5f)
                {
                    sb.AppendLine($"  [跳过] {t.name}: 已有起伏 {relief:F1} m（不是空白地形）。" +
                                  "确实要覆盖请用 Window/Vista/Terrain/1. Sculpt Test Terrain (Force)。");
                    continue;
                }

                // size.y 要在采高度之前定好：SetHeights 写的是归一化值。
                var size = td.size;
                if (!Mathf.Approximately(size.y, k_SizeY))
                    td.size = new Vector3(size.x, k_SizeY, size.z);
                size = td.size;

                Vector3 origin = t.transform.position;
                var h = new float[res, res];

                for (int y = 0; y < res; y++)
                {
                    float wz = origin.z + (y / (float)(res - 1)) * size.z;
                    for (int x = 0; x < res; x++)
                    {
                        float wx = origin.x + (x / (float)(res - 1)) * size.x;

                        float m = HeightMeters(wx, wz);
                        if (flatten.HasValue)
                            m *= 1f - FlattenWeight(flatten.Value, wx, wz);

                        h[y, x] = Mathf.Clamp01((m + k_HeightOffset) / k_SizeY);
                    }
                }

                td.SetHeights(0, 0, h);
                td.SyncHeightmap();

                // 渲染侧的地形设定（drawInstanced / basemapDistance / 邻接）不在这里改：
                // 那属于「装配材质」那一步，见 VistaTerrainMaterialTool.Setup。
                // 两处都写的话，以后只改一处就会静默分叉。
                // 这里只动一项内容相关的：像素误差 —— 新雕出来的崖面在默认值 5
                // 下会在中距离塌成一堆三角形，那不是着色的问题却会被当成着色的问题。
                t.heightmapPixelError = 2f;

                EditorUtility.SetDirty(td);
                EditorUtility.SetDirty(t);
                t.Flush();
                done++;

                sb.AppendLine($"  {t.name}: {size.x:F0}×{size.z:F0} m, 高度图 {res}², " +
                              $"起伏 {MeasureRelief(td):F1} m");
            }

            Terrain.SetConnectivityDirty();
            AssetDatabase.SaveAssets();

            sb.AppendLine($"  实际写入 {done} 块。下一步：Window/Vista/Terrain/2. Paint Test Splatmap");
            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// 世界坐标上的高度函数，单位米。三项叠加，各自对应一档坡度区间 ——
        /// 理由见类注释。
        /// </summary>
        static float HeightMeters(float wx, float wz)
        {
            // 丘陵：大尺度起伏，提供平地与缓坡
            float hills = (Fbm(wx / 240f + 11.3f, wz / 240f + 5.7f, 5) - 0.5f) * 2f * 26f;

            // 山脊只长在丘陵高处：低地保持平坦，同一块地形上才会同时存在
            // 「三平面权重 = 0」与「三平面权重 = 1」两种极端。
            float ridgeMask = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-6f, 14f, hills));
            float ridge = Ridged(wx / 110f + 3.1f, wz / 110f + 19.4f, 4) * 30f * ridgeMask;

            // 硬崖 ×2 档，阈值带宽不同 → 一档 ~77°，一档 ~65°，
            // 让三平面的过渡区间（|n.y| 从 0.85 到 0.5）真的被扫到，
            // 而不是只有「完全三平面」一个点。
            float c1 = Fbm(wx / 150f + 71.9f, wz / 150f + 43.2f, 3);
            float cliff = 18f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.50f, 0.56f, c1));

            float c2 = Fbm(wx / 95f + 128.4f, wz / 95f + 91.6f, 3);
            cliff += 9f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.43f, 0.51f, c2));

            return hills + ridge + cliff;
        }

        // ====================================================================
        //  ② Splatmap
        // ====================================================================
        /// <summary>
        /// 一次扫描定阈值，再刷一遍。
        ///
        /// 阈值走**全局分位数**而不是拍死的角度：场景里这四块地形的起伏差了一个
        /// 数量级（28 m ~ 397 m），一个写死的「35° 以上是陡坡」会让三块缓地形
        /// 一点陡坡层都刷不到 —— 那时陡坡三平面在四分之三的场景里恒等于没开，
        /// 而那种失败长得和「功能没实现」一模一样。分位数保证无论地形多缓，
        /// 最陡的那一部分总会被分到陡坡层。
        ///
        /// 分位数之外再压一条**物理下限**（陡坡带至少从 18° 起）：纯分位数在
        /// 一块完全平坦的地形上会把 6° 判成「陡」，把土刷到平地上。两者取大，
        /// 才同时挡住「太缓的地形全是草」和「平地上莫名其妙露土」。
        /// </summary>
        public static void Paint(bool force)
        {
            var terrains = Object.FindObjectsByType<Terrain>();
            if (terrains.Length == 0)
            {
                Debug.LogWarning("[Vista Terrain] 当前场景里没有 Terrain。");
                return;
            }

            var layers = new TerrainLayer[k_LayerPaths.Length];
            for (int i = 0; i < k_LayerPaths.Length; i++)
            {
                layers[i] = AssetDatabase.LoadAssetAtPath<TerrainLayer>(k_LayerPaths[i]);
                if (layers[i] == null)
                {
                    Debug.LogError($"[Vista Terrain] 找不到 TerrainLayer：{k_LayerPaths[i]}");
                    return;
                }
            }

            var targets = new List<Terrain>();
            var skipped = new List<string>();
            foreach (var t in terrains)
            {
                if (t.terrainData == null) continue;
                if (!force && t.terrainData.terrainLayers != null && t.terrainData.terrainLayers.Length > 0)
                {
                    skipped.Add($"  [跳过] {t.name}: 已有 {t.terrainData.terrainLayers.Length} 层。" +
                                "确实要覆盖请用 Window/Vista/Terrain/2. Paint Test Splatmap (Force)。");
                    continue;
                }
                targets.Add(t);
            }

            if (targets.Count == 0)
            {
                Debug.LogWarning("[Vista Terrain] 没有要刷的地形。\n" + string.Join("\n", skipped));
                return;
            }

            // ---- 扫描：坡度与世界高度的全局分布
            var steepSamples = new List<float>(1 << 18);
            var altSamples = new List<float>(1 << 18);
            const int kScan = 128;
            foreach (var t in targets)
            {
                var td = t.terrainData;
                float baseY = t.transform.position.y;
                for (int y = 0; y < kScan; y++)
                {
                    float v = y / (float)(kScan - 1);
                    for (int x = 0; x < kScan; x++)
                    {
                        float u = x / (float)(kScan - 1);
                        steepSamples.Add(td.GetSteepness(u, v));
                        altSamples.Add(baseY + td.GetInterpolatedHeight(u, v));
                    }
                }
            }

            steepSamples.Sort();
            altSamples.Sort();

            float steepLo = Mathf.Max(Percentile(steepSamples, 0.70f), 18f);
            float steepHi = Mathf.Max(Percentile(steepSamples, 0.93f), steepLo + 10f);
            float altLo = Percentile(altSamples, 0.10f);
            float altHi = Percentile(altSamples, 0.45f);
            if (altHi - altLo < 1f) altHi = altLo + 1f;   // 全平场景：低洼带退化成「没有低洼」

            var sb = new StringBuilder();
            sb.AppendLine($"[Vista Terrain] 刷 splatmap 完成：{targets.Count} 块地形，4 层");
            sb.AppendLine("  层分配：0 草(缓坡) / 1 沙泥(陡坡裸露) / 2 草02(斑块) / 3 沙砾(低洼平地)");
            sb.AppendLine($"  坡度分位 p50={Percentile(steepSamples, 0.5f):F1}° " +
                          $"p70={Percentile(steepSamples, 0.70f):F1}° " +
                          $"p93={Percentile(steepSamples, 0.93f):F1}° " +
                          $"max={steepSamples[steepSamples.Count - 1]:F1}°" +
                          $" → 陡坡过渡带 {steepLo:F1}°→{steepHi:F1}°");
            sb.AppendLine($"  世界高度 p10={altLo:F1} m p45={altHi:F1} m " +
                          $"（全局 {altSamples[0]:F1}~{altSamples[altSamples.Count - 1]:F1} m）");

            foreach (var t in targets)
            {
                var td = t.terrainData;
                td.terrainLayers = layers;
                if (td.alphamapResolution != k_AlphamapRes)
                    td.alphamapResolution = k_AlphamapRes;

                int res = td.alphamapResolution;
                var map = new float[res, res, 4];
                Vector3 origin = t.transform.position;
                var size = td.size;

                // 统计各层占比：一块只刷到两层的地形，陡坡三平面在它上面恒等于没开，
                // 报表里要看得出来是哪一块。
                var share = new double[4];

                for (int y = 0; y < res; y++)
                {
                    float v = y / (float)(res - 1);
                    float wz = origin.z + v * size.z;
                    for (int x = 0; x < res; x++)
                    {
                        float u = x / (float)(res - 1);
                        float wx = origin.x + u * size.x;

                        float steep = td.GetSteepness(u, v);
                        float worldY = origin.y + td.GetInterpolatedHeight(u, v);

                        // —— 层 1：陡坡裸露。过渡带故意拉宽（十几度）：
                        //    height-blend 要的就是一条宽的线性过渡带，它才有东西可锐化。
                        float dirt = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(steepLo, steepHi, steep));
                        // 坡脚线不该是一条干净的等高线。扰动只加在过渡带内部
                        // （帐篷权重在 dirt=0.5 处最大），免得把纯平地也染上土。
                        float tent = 1f - Mathf.Abs(dirt * 2f - 1f);
                        dirt = Mathf.Clamp01(dirt + (Fbm(wx / 16f + 5.3f, wz / 16f + 9.1f, 2) - 0.5f) * 0.45f * tent);

                        float remain = 1f - dirt;

                        // —— 层 3：低洼沙砾。低 + 平 + 噪声开洞。
                        //    只用高度不够：山腰上一条等高线会横着刷出一条沙带。
                        float lowW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(altHi, altLo, worldY));
                        float flatW = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(5f, 16f, steep));
                        float sandN = Fbm(wx / 70f + 77.2f, wz / 70f + 53.8f, 3);
                        float sand = remain * lowW * flatW *
                                     Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.42f, 0.58f, sandN));
                        remain -= sand;

                        // —— 层 2：草 02 斑块
                        float patch = Fbm(wx / 55f + 31.6f, wz / 55f + 17.9f, 3);
                        float grass2 = remain * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.46f, 0.61f, patch));

                        // —— 层 0：剩下的都是主草地
                        float grass1 = remain - grass2;

                        map[y, x, 0] = grass1;
                        map[y, x, 1] = dirt;
                        map[y, x, 2] = grass2;
                        map[y, x, 3] = sand;

                        share[0] += grass1; share[1] += dirt; share[2] += grass2; share[3] += sand;
                    }
                }

                td.SetAlphamaps(0, 0, map);
                EditorUtility.SetDirty(td);
                EditorUtility.SetDirty(t);
                t.Flush();

                double total = res * (double)res;
                sb.AppendLine($"  {t.name}: {res}² splat, 占比 " +
                              $"草 {share[0] / total:P1} / 泥 {share[1] / total:P1} / " +
                              $"草02 {share[2] / total:P1} / 沙 {share[3] / total:P1}");
            }

            foreach (var s in skipped) sb.AppendLine(s);

            AssetDatabase.SaveAssets();
            sb.AppendLine("  下一步：Window/Vista/Terrain/Setup Vista Terrain Material");
            Debug.Log(sb.ToString());
        }

        static float Percentile(List<float> sorted, float q)
        {
            if (sorted.Count == 0) return 0f;
            int i = Mathf.Clamp(Mathf.RoundToInt(q * (sorted.Count - 1)), 0, sorted.Count - 1);
            return sorted[i];
        }

        // ====================================================================
        //  平坦保护区
        // ====================================================================
        /// <summary>
        /// 场景里已有的物件（非地形 Renderer）的 xz 包围盒。雕刻时把这块压平，
        /// 否则原来摆在 y≈0 平面上的测试物件会被埋进山里或悬在半空 ——
        /// 那不是「地形做好了」，那是把别人的布景弄坏了。
        /// </summary>
        static Bounds? BuildFlattenZone(Terrain[] terrains)
        {
            var terrainGos = new HashSet<GameObject>();
            foreach (var t in terrains) terrainGos.Add(t.gameObject);

            bool any = false;
            Bounds b = default;
            foreach (var r in Object.FindObjectsByType<Renderer>())
            {
                // 地形本身不是 Renderer，但挂在同一个 GameObject 上的别的
                // Renderer（比如调试用的 gizmo mesh）要排掉，否则保护区会被
                // 撑成整块地形，雕出来是一张平板。
                if (terrainGos.Contains(r.gameObject)) continue;
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;

                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }

            if (!any) return null;
            b.Expand(new Vector3(50f, 0f, 50f));  // Expand 是「两侧各加一半」，这里等于每边 25 m

            // 上限保护：场景里只要有一个巨大的 Renderer（远景片、调试用的大平面），
            // 包围盒就会吞掉整片地形，雕出来是一张平板 —— 而那种失败长得
            // 和「工具没跑」一模一样。超过地形总面积的 35% 就放弃保护区并说明。
            float terrainArea = 0f;
            foreach (var t in terrains)
                if (t.terrainData != null) terrainArea += t.terrainData.size.x * t.terrainData.size.z;
            float zoneArea = b.size.x * b.size.z;
            if (terrainArea > 0f && zoneArea > 0.35f * terrainArea)
            {
                Debug.LogWarning($"[Vista Terrain] 平坦保护区 {zoneArea:F0} m² 超过地形总面积 " +
                                 $"{terrainArea:F0} m² 的 35%，已放弃保护区（否则地形会被压成平板）。");
                return null;
            }

            return b;
        }

        /// <summary>1 = 完全压平，0 = 不受影响。边界外再用 60 m 平滑过渡回原高度。</summary>
        static float FlattenWeight(Bounds b, float wx, float wz)
        {
            // 到包围盒的 xz 距离（盒内为 0）
            float dx = Mathf.Max(b.min.x - wx, 0f, wx - b.max.x);
            float dz = Mathf.Max(b.min.z - wz, 0f, wz - b.max.z);
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            return 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 60f, d));
        }

        static float MeasureRelief(TerrainData td)
        {
            int res = td.heightmapResolution;
            int step = Mathf.Max(1, res / 64);   // 抽样：只要判断「是不是平的」，不需要全量
            float lo = float.MaxValue, hi = float.MinValue;
            var h = td.GetHeights(0, 0, res, res);
            for (int y = 0; y < res; y += step)
            for (int x = 0; x < res; x += step)
            {
                float v = h[y, x];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            return (hi - lo) * td.size.y;
        }

        // ====================================================================
        //  噪声
        // ====================================================================
        // 用 Mathf.PerlinNoise 而不是自己写一套：这份工具产出的是**资产**，
        // 跑一次就固化进 TerrainData，运行期不再求值，所以这里唯一重要的是
        // 「同一台机器上可重复」，而不是速度或与 HLSL 那一套对齐。
        // （shader 里的 macro variation 用的是 VistaTerrainSplat.hlsl 自己的
        //  value noise，两者本来就不该、也不需要一致。）

        static float Fbm(float x, float y, int octaves)
        {
            float amp = 1f, sum = 0f, norm = 0f, fx = x, fy = y;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Mathf.PerlinNoise(fx + i * 37.13f, fy + i * 17.71f);
                norm += amp;
                amp *= 0.5f;
                fx *= 2f; fy *= 2f;
            }
            return sum / norm;
        }

        static float Ridged(float x, float y, int octaves)
        {
            float amp = 1f, sum = 0f, norm = 0f, fx = x, fy = y;
            for (int i = 0; i < octaves; i++)
            {
                float n = Mathf.PerlinNoise(fx + i * 11.37f, fy + i * 29.13f);
                n = 1f - Mathf.Abs(n * 2f - 1f);   // 折一次 → 脊线
                n *= n;                            // 平方 → 脊变尖、谷变平
                sum += amp * n;
                norm += amp;
                amp *= 0.5f;
                fx *= 2f; fy *= 2f;
            }
            return sum / norm;
        }
    }
}
