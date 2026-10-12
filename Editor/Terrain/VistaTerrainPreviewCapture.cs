using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Vista.Editor
{
    /// <summary>
    /// 把地形的四项技术拍成一组 A/B 对照图。
    ///
    /// ── 为什么是「自动找机位」而不是「拍当前 Scene View」──
    ///
    /// 这四项技术各有自己**该生效的那一档几何**：triplanar 只在陡崖上不等于
    /// 单平面投影，height-blend 只在两层权重都非零的过渡带上不等于线性混合。
    /// 随手拍一张缓坡草地，四项全开和四项全关会得到几乎同一张图 —— 那种
    /// 「看不出差别」既可能是功能没生效，也可能是机位没对准，两者在图上无法区分。
    /// 所以机位由**扫描打分**决定，判据见 TryFindShowcaseSpot：要同时够陡
    /// （triplanar 有输入）且够混（height-blend 有第二层可混）。最陡点不行 ——
    /// 崖面按 splatmap 的设计被陡坡层单独占满，那里没有混层可看。
    ///
    /// ── 为什么要关掉再拍一遍 ──
    ///
    /// 单张图回答不了「这个效果是这项技术带来的」。对照组是同机位、同光照、
    /// 只翻一个关键字的另一张图；差分图上亮起来的地方就是这项技术的作用域。
    /// 拍完必定还原材质属性（含异常路径），否则一次「拍照」会把材质改掉。
    /// </summary>
    public static class VistaTerrainPreviewCapture
    {
        const string k_OutDir = "D:/tmp/vista_shots";
        const int k_Width = 1280;
        const int k_Height = 720;

        // 对照组：名字 → 要关掉的开关属性。第一组不关任何东西 = 基准。
        static readonly (string name, string[] off)[] k_Variants =
        {
            ("00_all_on",        new string[0]),
            ("01_no_triplanar",  new[] { "_VistaEnableTriplanar" }),
            ("02_no_heightblend",new[] { "_VistaEnableHeightBlend" }),
            ("03_no_detail",     new[] { "_VistaEnableDetailNormal" }),
            ("04_no_macro",      new[] { "_VistaEnableMacro" }),
            ("05_all_off",       new[] { "_VistaEnableTriplanar", "_VistaEnableHeightBlend",
                                         "_VistaEnableDetailNormal", "_VistaEnableMacro",
                                         "_VistaEnableRelief", "_VistaEnableLayerTone" }),
            // 层法线不是这四项之一，但它和它们共用一条「属性 → 关键字」的通路，
            // 而且它缺席过：_NORMALMAP 一度根本没被置位，四张法线图全被编译掉。
            // 一个关键字在不在，与这个特性没实现，在画面上无法区分 —— 所以它
            // 也要有自己的对照格，而不是靠「工具报了一行开」来担保。
            //
            // 控制量是 _VistaLayerNormalWeight 而**不是** _VistaLayerHasNormalMap：
            // 后者投影成 _NORMALMAP，而地形引擎会在它自己的内部材质副本上按
            // 「层有没有法线图」把这个关键字重新打开 —— 模板上关掉的那一下
            // 到不了渲染。第一版就是用它做的对照，拍回来 06 与基准**逐像素相等**，
            // 我把那读成了「层法线毫无贡献」。那是个恒真的绿：一个关不掉的开关
            // 给出的「关掉也没变化」，与「这项功能没生效」在画面上无法区分。
            ("06_no_layernormal", new[] { "_VistaLayerNormalWeight" }),
            // ⑤⑥ 两项。⑥（色调）在这个特写上就能判：它改的是单层内部的明暗，
            // 与取景尺度无关。⑤（中频起伏）在这里**判不了** —— 它的尺度是
            // 40 m / 9 m，而这张图的视野只有几十米，一整屏可能落在一个起伏的
            // 同一侧。所以它另有一格 11_overview_no_relief：一个为特写挑的机位
            // 拿来判一项 40 m 尺度的技术，等于默认这项技术不需要取景。
            ("07_no_relief",     new[] { "_VistaEnableRelief" }),
            ("08_no_tone",       new[] { "_VistaEnableLayerTone" }),
        };

        [MenuItem("Window/Vista/Terrain/3. Capture Technique A-B Shots", priority = 210)]
        public static void Capture()
        {
            var terrains = Object.FindObjectsByType<Terrain>();
            if (terrains.Length == 0)
            {
                Debug.LogWarning("[Vista Terrain] 当前场景里没有 Terrain。");
                return;
            }

            if (!TryFindShowcaseSpot(terrains, out var target, out var faceDir,
                                     out float steep, out float mix, out string onName))
            {
                Debug.LogWarning("[Vista Terrain] 没找到可用的机位（地形没有 TerrainData？）。");
                return;
            }

            // 机位：站在崖面正对着的那一侧，略高于目标点，俯角小。
            // 25 m 是按 detail normal 的淡出区间（15~45 m）挑的 —— 太近只剩
            // detail，太远 detail 已经淡完，对照组之间会少掉一整项差异。
            const float dist = 25f;
            Vector3 eye = target + faceDir * dist + Vector3.up * 8f;

            var mats = CollectMaterials(terrains);
            if (mats.Count == 0)
            {
                Debug.LogWarning("[Vista Terrain] 地形没有 Vista 材质，先跑 Setup Vista Terrain Material。");
                return;
            }

            // 先存一份原始开关值，finally 里还原 —— 中途抛异常也不能把材质留在
            // 「某一项被关掉」的状态上：那种改动看不出来源，下次打开工程只会
            // 以为这项技术做坏了。
            var saved = new Dictionary<Material, Dictionary<string, float>>();
            foreach (var m in mats)
            {
                var d = new Dictionary<string, float>();
                foreach (var (_, off) in k_Variants)
                    foreach (var p in off)
                        if (m.HasProperty(p) && !d.ContainsKey(p)) d[p] = m.GetFloat(p);
                // debug view 也进这张表：它不在 k_Variants 里，但同样会被这次
                // 「拍照」改掉，而异常路径只还原 saved 里有的键。漏一个就会把
                // 材质留在 debug 档上 —— 那时地形是一片纯色，而那种画面
                // 长得和「shader 编译坏了」一模一样。
                if (m.HasProperty("_VistaDebugView")) d["_VistaDebugView"] = m.GetFloat("_VistaDebugView");
                saved[m] = d;
            }

            Directory.CreateDirectory(k_OutDir);
            var sb = new StringBuilder();
            sb.AppendLine("[Vista Terrain] A/B 对照图");
            sb.AppendLine($"  机位：目标 {target} 坡度 {steep:F1}°、混合度 {mix:F2}（在 {onName} 上），相机 {eye}");
            sb.AppendLine($"  输出目录：{k_OutDir}");

            var camGo = new GameObject("~VistaCaptureCam") { hideFlags = HideFlags.HideAndDontSave };
            // Vector 属性不进 saved（那张表是 float 的）。单独存一份，并且声明在
            // try 之外 —— 异常路径也必须还原，否则一次拍照会把第三档的斜率留在 0。
            var savedOct3 = new Dictionary<Material, Vector4>();
            foreach (var m in mats)
                if (m.HasProperty("_VistaReliefOctave3"))
                    savedOct3[m] = m.GetVector("_VistaReliefOctave3");
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.transform.position = eye;
                cam.transform.rotation = Quaternion.LookRotation((target - eye).normalized, Vector3.up);
                cam.fieldOfView = 55f;
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 2000f;
                cam.clearFlags = CameraClearFlags.Skybox;

                foreach (var (name, off) in k_Variants)
                {
                    foreach (var m in mats)
                    {
                        foreach (var kv in saved[m]) m.SetFloat(kv.Key, kv.Value);
                        foreach (var p in off) if (m.HasProperty(p)) m.SetFloat(p, 0f);
                        VistaTerrainLitShaderGUI.ApplyKeywords(m);
                    }

                    string path = $"{k_OutDir}/{name}.png";
                    RenderToPng(cam, path);
                    sb.AppendLine($"  {name}.png" + (off.Length == 0 ? "  （基准：四项全开）" : $"  关闭 {string.Join(", ", off)}"));

                    // 第一格拍完顺手把材质的**运行期**状态打出来。这些量
                    // （_Normal#、_NormalScale#、_LayerHasMask#）不进 .mat 的
                    // 序列化数据，是地形引擎逐帧塞进来的 —— 只有在引擎确实渲染过
                    // 一次之后读才有意义，所以位置必须在 cam.Render() 之后。
                    // 「工具报了 4/4 层有法线图」说的是 TerrainLayer 资产；
                    // 「材质上真的绑到了那张图」是另一件事，两者在画面上无法区分。
                    if (name == "00_all_on") DumpRuntimeBindings(mats[0], sb);
                }

                // ---- Debug view。回答的不是「好不好看」而是「shader 收到了什么」：
                //      _Normal#/_NormalScale#/_LayerHasMask# 由引擎写进内部材质副本，
                //      C# 侧从 materialTemplate 上读全是 0/<null>（见 DumpRuntimeBindings
                //      的输出），只有 shader 自己拿得到真值。
                //      翻的量是 _VistaLayerNormalWeight 而不是 _NORMALMAP —— 理由同
                //      06_no_layernormal 那条注释：那个关键字关不掉。
                var dbgShots = new (string file, int mode, float lnWeight, string note)[]
                {
                    ("20_dbg_normal_on.png",  1, 1f, "合成世界法线（层法线增益 1）"),
                    ("21_dbg_normal_off.png", 1, 0f, "合成世界法线（层法线增益 0）—— 与 20 逐像素相等即层法线没进画面"),
                    ("22_dbg_normalscale.png",3, 1f, "RGB = _NormalScale0/1/2，全黑即引擎没把 normalScale 写进来"),
                    ("23_dbg_normal0_raw.png",5, 1f, "_Normal0 的原始采样，灰平即贴图没绑上"),
                    ("24_dbg_weights.png",    4, 1f, "RGB = height-blend 后的前三层权重"),
                    ("25_dbg_relief.png",     7, 1f, "只有中频起伏的法线；关掉 ⑤ 时这一格是一片恒定色（几何法线），" +
                                                      "所以它同时回答「这一路有没有输出」和「输出的形体长什么样」"),
                    ("26_dbg_layernormal.png",2, 1f, "只有层法线的合成世界法线（未叠 ⑤/③）"),
                    ("27_dbg_layernormal_dev.png", 8, 1f, "层法线相对几何法线的切向偏移 ×20，0.5 灰 = 零偏移。" +
                                                      "这一格是「层法线到底有没有进画面」的判据 —— 前面几档都回答不了"),
                    ("28_dbg_layernormal_dev_off.png", 8, 0f, "同上但层法线增益 0 —— 必须是一片恒定的 0.5 灰。" +
                                                      "它是 27 那把尺子的零点：若它也有花纹，说明这一档量的不是层法线"),
                };
                foreach (var (file, mode, lnWeight, note) in dbgShots)
                {
                    foreach (var m in mats)
                    {
                        m.SetFloat("_VistaLayerNormalWeight", lnWeight);
                        m.SetFloat("_VistaDebugView", mode);
                        VistaTerrainLitShaderGUI.ApplyKeywords(m);
                    }
                    RenderToPng(cam, $"{k_OutDir}/{file}", postProcessing: false);
                    sb.AppendLine($"  {file}  {note}");
                }
                foreach (var m in mats)
                {
                    m.SetFloat("_VistaDebugView", 0f);
                    foreach (var kv in saved[m]) m.SetFloat(kv.Key, kv.Value);
                    VistaTerrainLitShaderGUI.ApplyKeywords(m);
                }

                // ---- 远景总览。A/B 那六张是「这项技术有没有生效」的判据，它们
                //      按定义取在一个四项都有输入的特写上 —— 那张图回答不了
                //      「整片地形好不好看」：平铺规律、色彩分层、远处有没有糊成
                //      一片单色，都只在 500 m 尺度上才看得出来。两种取景各自
                //      回答一件事，缺一张就得靠想象补。
                foreach (var m in mats)
                {
                    foreach (var kv in saved[m]) m.SetFloat(kv.Key, kv.Value);
                    VistaTerrainLitShaderGUI.ApplyKeywords(m);
                }

                var wide = OverviewBounds(terrains);
                Vector3 wideEye = new Vector3(wide.center.x - wide.extents.x * 1.25f,
                                              wide.max.y + wide.size.y * 0.55f + 60f,
                                              wide.center.z - wide.extents.z * 1.25f);
                cam.transform.position = wideEye;
                cam.transform.rotation = Quaternion.LookRotation((wide.center - wideEye).normalized, Vector3.up);
                cam.fieldOfView = 60f;
                cam.farClipPlane = 8000f;
                RenderToPng(cam, $"{k_OutDir}/10_overview.png");
                sb.AppendLine($"  10_overview.png  远景总览：相机 {wideEye} → 看向 {wide.center}，" +
                              $"地形总包围盒 {wide.size.x:F0}×{wide.size.z:F0} m");

                // ⑤ 的 A/B 必须在这个机位上拍。它的尺度是 40 m / 9 m，而特写那
                // 六张的视野只有几十米 —— 在那儿关掉它，整屏可能只是同一侧斜面
                // 整体亮了一点，与「关掉了一项 40 m 尺度的技术」长得不一样。
                // 同一个机位的两张图才是它的判据。
                foreach (var m in mats)
                {
                    if (m.HasProperty("_VistaEnableRelief")) m.SetFloat("_VistaEnableRelief", 0f);
                    VistaTerrainLitShaderGUI.ApplyKeywords(m);
                }
                RenderToPng(cam, $"{k_OutDir}/11_overview_no_relief.png");
                sb.AppendLine("  11_overview_no_relief.png  同机位关掉 ⑤ 中频起伏 —— 与 10 的差就是这一路" +
                              "在远景贡献的全部形体（层法线图在这个距离已经 mip 塌掉一半）");
                foreach (var m in mats)
                {
                    foreach (var kv in saved[m]) m.SetFloat(kv.Key, kv.Value);
                    VistaTerrainLitShaderGUI.ApplyKeywords(m);
                }

                // ⑤ 第三档单独的一对。它和 11 回答的不是同一件事：11 问的是
                // 「中频起伏这一路有没有用」，这一对问的是「新加的 2 m 档填上了
                // 45 m 以外那段空白没有」。两者必须分开 —— 把三档一起关掉，
                // 远山当然会变平，那个差里看不出第三档自己贡献了多少。
                //
                // 机位也必须另挑。第一版把它挂在总览机位上，拍回来与基准
                // **逐像素相等** —— 总览站在 1800 m 外，而这一档在 700 m 就淡完了，
                // 那个 0 是判据站错了地方，不是功能没生效。两者在图上无法区分，
                // 所以机位要落在这一档的作用域（45~700 m）里：220 m。
                const float midDist = 220f;
                Vector3 midEye = target + faceDir * midDist + Vector3.up * 70f;
                cam.transform.position = midEye;
                cam.transform.rotation = Quaternion.LookRotation((target - midEye).normalized, Vector3.up);
                cam.fieldOfView = 55f;
                cam.farClipPlane = 4000f;
                RenderToPng(cam, $"{k_OutDir}/14_mid.png");
                sb.AppendLine($"  14_mid.png  中距机位（{midDist:F0} m）：45~700 m 这一段的基准，" +
                              "detail 已经淡完、层法线的 mip 也塌了，这里的表面细节只能来自 ⑤");
                foreach (var m in mats)
                {
                    if (!savedOct3.TryGetValue(m, out var v)) continue;
                    m.SetVector("_VistaReliefOctave3", new Vector4(v.x, 0f, v.z, v.w));
                }
                RenderToPng(cam, $"{k_OutDir}/15_mid_no_relief3.png");
                sb.AppendLine("  15_mid_no_relief3.png  同机位只把 ⑤ 第三档（2 m）的斜率清零 —— " +
                              "与 14 的差就是这一档补上的全部细节");
                foreach (var kv in savedOct3) kv.Key.SetVector("_VistaReliefOctave3", kv.Value);

                // 近景机位也补一格第三档的对照：2 m 的特征在近处同样存在，
                // 若它在近处就已经压过 detail，那说明斜率定高了 —— 远景的空白
                // 要补，但不能靠把一个全距离都生效的量调大来补。
                cam.transform.position = eye;
                cam.transform.rotation = Quaternion.LookRotation((target - eye).normalized, Vector3.up);
                cam.fieldOfView = 55f;
                cam.farClipPlane = 2000f;
                foreach (var m in mats)
                {
                    if (!savedOct3.TryGetValue(m, out var v)) continue;
                    m.SetVector("_VistaReliefOctave3", new Vector4(v.x, 0f, v.z, v.w));
                }
                RenderToPng(cam, $"{k_OutDir}/13_close_no_relief3.png");
                sb.AppendLine("  13_close_no_relief3.png  特写机位关掉 ⑤ 第三档。判据是**同一机位内部的比值**：" +
                              "diff(00,13) 应当明显小于 diff(00,03)（关掉 detail），否则这一档在近处抢了 detail 的戏。" +
                              "不要拿它去和 15 在中距机位的绝对差比大小 —— 两个取景的像素内容不同，" +
                              "绝对 diff 本来就不可比，那样比等于用一把没标定过的尺子。");
                foreach (var kv in savedOct3) kv.Key.SetVector("_VistaReliefOctave3", kv.Value);

                foreach (var m in mats)
                {
                    foreach (var kv in saved[m]) m.SetFloat(kv.Key, kv.Value);
                    VistaTerrainLitShaderGUI.ApplyKeywords(m);
                }
            }
            finally
            {
                foreach (var kv in savedOct3) kv.Key.SetVector("_VistaReliefOctave3", kv.Value);
                foreach (var m in mats)
                {
                    foreach (var kv in saved[m]) m.SetFloat(kv.Key, kv.Value);
                    VistaTerrainLitShaderGUI.ApplyKeywords(m);
                    EditorUtility.SetDirty(m);
                }
                Object.DestroyImmediate(camGo);
            }

            AssetDatabase.SaveAssets();
            Debug.Log(sb.ToString());
        }

        static void RenderToPng(Camera cam, string path, bool postProcessing = true)
        {
            // 调试档必须关掉后处理。debug 值是直接写进相机颜色缓冲的，整条
            // tonemap + 曝光会作用在上面 —— 一个 1.0 和一个 3.0 都会被顶成纯白，
            // 两者在图上无法区分。22（normalScale 全白）与 20/21（两张全白因而
            // 「逐像素相等」）都曾经因此给出恒真的绿：一张被曝光压饱和的图，
            // 长得和一张「这个量确实是 1」的图一模一样。
            var ucd = cam.GetComponent<UniversalAdditionalCameraData>();
            if (ucd == null) ucd = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            ucd.renderPostProcessing = postProcessing;

            // 线性工作流下，默认的 RT 就是 sRGB 目标；ReadPixels 到
            // linear:false 的 Texture2D 再 EncodeToPNG，得到的是可以直接看的图。
            // 用浮点 RT 会拿到线性数值，存成 PNG 会整体偏暗，而那种偏暗
            // 长得和「曝光没配好」一模一样。
            var rt = RenderTexture.GetTemporary(k_Width, k_Height, 24, RenderTextureFormat.ARGB32,
                                                RenderTextureReadWrite.sRGB);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;

            cam.targetTexture = rt;
            cam.Render();

            var tex = new Texture2D(k_Width, k_Height, TextureFormat.RGB24, false, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, k_Width, k_Height), 0, 0);
            tex.Apply(false, false);

            RenderTexture.active = prevActive;
            cam.targetTexture = prevTarget;
            RenderTexture.ReleaseTemporary(rt);

            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        /// <summary>
        /// 把地形引擎**逐帧写进材质**的那批量打出来。
        ///
        /// 为什么需要这一段：`_Normal0..3` / `_NormalScale0..3` / `_LayerHasMask0..3`
        /// 不在 .mat 里，是引擎在渲染时塞的。所以「TerrainLayer 上有法线图」
        /// 与「shader 采样到了那张图」是两件事 —— 中间任何一环断掉，画面表现都是
        /// 「法线不起作用」，而那跟「法线图本身是平的」长得一模一样。
        /// </summary>
        static void DumpRuntimeBindings(Material m, StringBuilder sb)
        {
            sb.AppendLine("  ── 渲染后材质的运行期状态 ──");
            sb.AppendLine($"     _NORMALMAP {(m.IsKeywordEnabled("_NORMALMAP") ? "开" : "关")}" +
                          $"，_MASKMAP {(m.IsKeywordEnabled("_MASKMAP") ? "开" : "关")}" +
                          $"，_VISTA_TRIPLANAR {(m.IsKeywordEnabled("_VISTA_TRIPLANAR") ? "开" : "关")}");
            for (int i = 0; i < 4; i++)
            {
                var nrm = m.GetTexture($"_Normal{i}");
                var spl = m.GetTexture($"_Splat{i}");
                sb.AppendLine($"     层{i}: _Splat{i} = {(spl == null ? "<null>" : spl.name)}" +
                              $", _Normal{i} = {(nrm == null ? "<null>" : nrm.name)}" +
                              $", _NormalScale{i} = {m.GetFloat($"_NormalScale{i}"):F3}" +
                              $", _LayerHasMask{i} = {m.GetFloat($"_LayerHasMask{i}"):F2}" +
                              $", _Smoothness{i} = {m.GetFloat($"_Smoothness{i}"):F2}");
            }
            sb.AppendLine($"     _Control = {(m.GetTexture("_Control") == null ? "<null>" : m.GetTexture("_Control").name)}" +
                          $", _Control_TexelSize = {m.GetVector("_Control_TexelSize")}" +
                          $", _NumLayersCount = {m.GetFloat("_NumLayersCount"):F0}");
        }

        /// <summary>所有地形的世界包围盒。用 TerrainData.size 而不是 Renderer.bounds ——
        /// 地形不是 Renderer，而 terrain.bounds 是局部空间的。</summary>
        static Bounds OverviewBounds(Terrain[] terrains)
        {
            bool any = false;
            Bounds b = default;
            foreach (var t in terrains)
            {
                if (t.terrainData == null) continue;
                var o = t.transform.position;
                var s = t.terrainData.size;
                var one = new Bounds(o + s * 0.5f, s);
                if (!any) { b = one; any = true; } else b.Encapsulate(one);
            }
            return any ? b : new Bounds(Vector3.zero, Vector3.one * 100f);
        }

        static List<Material> CollectMaterials(Terrain[] terrains)
        {
            var list = new List<Material>();
            foreach (var t in terrains)
            {
                var m = t.materialTemplate;
                if (m == null || m.shader == null) continue;
                if (!m.shader.name.StartsWith("Vista/Terrain")) continue;
                if (!list.Contains(m)) list.Add(m);
            }
            return list;
        }

        /// <summary>
        /// 扫描出一个**四项技术同时有输入**的点，并给出它正对着的水平方向。
        ///
        /// 判据不是「最陡」。第一版就是最陡，拍回来的是一面 78.7° 的崖 ——
        /// 而崖面按 splatmap 的设计恰好被陡坡层单独占满，height-blend 在那里
        /// 没有第二层可混，四项里有一项必然看不出差别。「最陡」是为 triplanar
        /// 挑的判据，拿它当全部四项的机位，等于默认另外三项不需要取景。
        ///
        /// 所以打分是**乘积**而不是取大：
        ///   score = saturate(steep / 60°) × (1 - max control)
        /// 两个因子任一为 0 都出局 —— 平地上 triplanar 恒等于单平面投影，
        /// 纯色 texel 上 height-blend 恒等于线性混合。乘积只在两者都成立时才高。
        /// 60° 作分母：triplanar 的权重在 |n.y| 从 0.85 降到 0.5 的区间内起作用，
        /// 也就是 31.8°~60°，60° 之后已经是满权重，再陡不增加可看性。
        /// </summary>
        static bool TryFindShowcaseSpot(Terrain[] terrains, out Vector3 target, out Vector3 faceDir,
                                        out float steep, out float mix, out string terrainName)
        {
            target = Vector3.zero; faceDir = Vector3.forward;
            steep = 0f; mix = 0f; terrainName = null;
            float best = -1f;
            const int kScan = 192;

            foreach (var t in terrains)
            {
                var td = t.terrainData;
                if (td == null) continue;
                var origin = t.transform.position;
                var size = td.size;

                // 整张 alphamap 一次取回。逐点 GetAlphamaps(x,y,1,1) 在 192² 上
                // 是 36864 次资源读取，那会让「拍张图」变成几十秒的事。
                int ares = td.alphamapResolution;
                int alayers = td.alphamapLayers;
                var amap = alayers > 0 ? td.GetAlphamaps(0, 0, ares, ares) : null;

                for (int y = 0; y < kScan; y++)
                {
                    float v = y / (float)(kScan - 1);
                    for (int x = 0; x < kScan; x++)
                    {
                        float u = x / (float)(kScan - 1);

                        float s = td.GetSteepness(u, v);
                        float steepScore = Mathf.Clamp01(s / 60f);
                        if (steepScore <= 0f) continue;

                        float m = 0f;
                        if (amap != null)
                        {
                            int ax = Mathf.Clamp(Mathf.RoundToInt(u * (ares - 1)), 0, ares - 1);
                            int ay = Mathf.Clamp(Mathf.RoundToInt(v * (ares - 1)), 0, ares - 1);
                            float top = 0f;
                            for (int l = 0; l < alayers; l++) top = Mathf.Max(top, amap[ay, ax, l]);
                            m = 1f - top;
                        }

                        float score = steepScore * m;
                        if (score <= best) continue;

                        best = score; steep = s; mix = m;
                        target = new Vector3(origin.x + u * size.x,
                                             origin.y + td.GetInterpolatedHeight(u, v),
                                             origin.z + v * size.z);
                        var n = td.GetInterpolatedNormal(u, v);
                        var h = new Vector3(n.x, 0f, n.z);
                        // 坡度接近 90° 时水平分量才可靠；退化时给一个固定朝向，
                        // 而不是让 normalize(0) 产生 NaN 把相机送到无穷远。
                        faceDir = h.sqrMagnitude > 1e-4f ? h.normalized : Vector3.forward;
                        terrainName = t.name;
                    }
                }
            }

            return terrainName != null;
        }
    }
}
