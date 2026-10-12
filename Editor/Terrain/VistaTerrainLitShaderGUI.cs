using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// Vista/Terrain/Lit 的材质面板。
    ///
    /// 不继承 URP 的 <c>BaseShaderGUI</c>：那套面板的主体是 Surface Options
    /// （Opaque/Transparent、Blend、Cull、Alpha Clip），地形一项都用不上——
    /// 地形永远不透明、永远双面无关、alpha clip 由 holes 决定而不是材质。
    /// 继承过来只会画出四个改了也不生效的下拉框，那比没有面板更坏。
    ///
    /// 也不实现 <c>ITerrainLayerCustomUI</c>：不实现时 Unity 会画自己那套
    /// TerrainLayer 面板（diffuse / normal / mask / tiling / offset），
    /// 那正是我们要的——层数据是引擎契约的一部分，不该由 Vista 重画。
    /// </summary>
    public class VistaTerrainLitShaderGUI : ShaderGUI
    {
        // 关键字与属性的对应表。属性是真值，关键字是它的投影。
        // 之所以不反过来（关键字当真值）：材质升级、shader 重导入、
        // 以及 `Material.shader = ...` 赋值都可能清掉关键字，属性不会丢。
        static readonly (string prop, string keyword)[] k_Toggles =
        {
            ("_VistaEnableHeightBlend",  "_VISTA_HEIGHT_BLEND"),
            ("_VistaEnableTriplanar",    "_VISTA_TRIPLANAR"),
            ("_VistaEnableDetailNormal", "_VISTA_DETAIL_NORMAL"),
            ("_VistaEnableMacro",        "_VISTA_MACRO_VARIATION"),
            ("_VistaEnableRelief",       "_VISTA_RELIEF"),
            ("_VistaEnableLayerTone",    "_VISTA_LAYER_TONE"),
            ("_VistaMobileTier",         "_VISTA_TERRAIN_MOBILE"),

            // 层数据的投影。真值在 TerrainLayer 上，由 VistaTerrainMaterialTool.Setup
            // 统计后写进这两个属性 —— 材质自己回答不了这件事（理由见 shader 里的注释）。
            // 放进这张表而不是在 Setup 里直接 EnableKeyword：关键字只能有一处决定，
            // 两处都写的话，手改材质与跑工具会得到两套关键字。
            ("_VistaLayerHasNormalMap",  "_NORMALMAP"),
            ("_VistaLayerHasMaskMap",    "_MASKMAP"),
        };

        /// <summary>
        /// 由属性推导关键字。面板与 <see cref="VistaTerrainMaterialTool"/> 共用这一处，
        /// 免得「手改材质」和「跑工具」得到两种不同的关键字集合。
        /// </summary>
        public static void ApplyKeywords(Material material)
        {
            foreach (var (prop, keyword) in k_Toggles)
            {
                bool on = material.HasProperty(prop) && material.GetFloat(prop) > 0.5f;
                CoreUtils.SetKeyword(material, keyword, on);
            }

            bool perPixelNormal = material.HasProperty("_EnableInstancedPerPixelNormal")
                                  && material.GetFloat("_EnableInstancedPerPixelNormal") > 0.5f;
            CoreUtils.SetKeyword(material, "_TERRAIN_INSTANCED_PERPIXEL_NORMAL", perPixelNormal);

            // Debug view 不是布尔开关（有 6 档），所以不在 k_Toggles 里；
            // 关键字只管「这条分支要不要编进去」，具体哪一档由属性值在运行期选。
            bool debug = material.HasProperty("_VistaDebugView")
                         && material.GetFloat("_VistaDebugView") > 0.5f;
            CoreUtils.SetKeyword(material, "_VISTA_TERRAIN_DEBUG", debug);
        }

        static void Header(string label)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
        }

        void Prop(MaterialEditor editor, MaterialProperty[] props, string name, string label, string tooltip = "")
        {
            var p = FindProperty(name, props, false);
            if (p != null)
                editor.ShaderProperty(p, new GUIContent(label, tooltip));
        }

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] props)
        {
            var material = materialEditor.target as Material;
            if (material == null)
                return;

            // 层数上限 4 的提示。shader 不提供 add-pass（理由见 shader 文件头），
            // 所以第 5 层不是「效果差一点」而是「那一层根本不参与」——必须说清楚。
            if (material.HasProperty("_NumLayersCount") && material.GetFloat("_NumLayersCount") > 4.0f)
            {
                EditorGUILayout.HelpBox(
                    $"这块地形有 {(int)material.GetFloat("_NumLayersCount")} 层，而 Vista/Terrain/Lit 只渲染前 4 层。" +
                    "height-blend 的权重归一化无法跨 pass 进行（第二个 pass 看不见第一个 pass 的高度），" +
                    "所以这里没有 add-pass。要么把层数压到 4，要么改用 URP 的 TerrainLit。",
                    MessageType.Error);
            }

            EditorGUI.BeginChangeCheck();

            Header("总体");
            Prop(materialEditor, props, "_BaseColor", "Tint");
            Prop(materialEditor, props, "_EnableInstancedPerPixelNormal", "Instanced Per-pixel Normal",
                 "地形开 Draw Instanced 时，从地形法线图逐像素取几何法线，而不是插值顶点法线。");
            Prop(materialEditor, props, "_VistaMobileTier", "Mobile Tier",
                 "开：三平面降到两面、macro 噪声降到一档。PC 上不要开。");

            Header("① Height Blend");
            Prop(materialEditor, props, "_VistaEnableHeightBlend", "Enable");
            using (new EditorGUI.DisabledScope(!IsOn(material, "_VistaEnableHeightBlend")))
            {
                Prop(materialEditor, props, "_VistaHeightBlendParams", "Params (x = transition)",
                     "过渡带宽度，单位是高度值本身（0~1）。越小越硬、越像真的高低咬合；" +
                     "越大越接近原来的线性混合。0 附近会出锯齿，因为高度代理本身有噪声。");
                EditorGUILayout.HelpBox(
                    "高度来自各层 albedo 的亮度去均值代理；层若带 mask map，则改用 mask.b（作者数据优先）。" +
                    "均值由 Window/Vista/Terrain 里的工具统计后写进 _VistaLayerParams#.z。",
                    MessageType.None);
            }

            Header("② Slope Triplanar");
            Prop(materialEditor, props, "_VistaEnableTriplanar", "Enable");
            using (new EditorGUI.DisabledScope(!IsOn(material, "_VistaEnableTriplanar")))
            {
                Prop(materialEditor, props, "_VistaTriplanarParams", "Params (x = flatNone, y = flatFull, z = sharpness)",
                     "x：|n.y| 大于它时完全不用三平面（纯平地）。y：|n.y| 小于它时完全用三平面（陡崖）。" +
                     "y < x。z：三个投影面的权重指数，越大越偏向主导面、过渡越窄。");
            }

            Header("③ Detail Normal");
            Prop(materialEditor, props, "_VistaEnableDetailNormal", "Enable");
            using (new EditorGUI.DisabledScope(!IsOn(material, "_VistaEnableDetailNormal")))
            {
                Prop(materialEditor, props, "_VistaDetailNormal", "Texture");
                Prop(materialEditor, props, "_VistaDetailParams", "Octave 1 (x = tile m, y = strength, z = fade start m, w = fade end m)",
                     "远处必须淡掉：detail 的特征尺寸小于一个像素时，它贡献的是 shimmer 而不是细节。");
                Prop(materialEditor, props, "_VistaDetailOctave2", "Octave 2 (x = tile m, y = strength, z = fade start m, w = fade end m)",
                     "第二档，补第一档淡完（45 m）到中频起伏细档（9 m）之间那一段。移动档不叠这一档。");
            }

            Header("④ Macro Variation");
            Prop(materialEditor, props, "_VistaEnableMacro", "Enable");
            using (new EditorGUI.DisabledScope(!IsOn(material, "_VistaEnableMacro")))
            {
                Prop(materialEditor, props, "_VistaMacroParams", "Params (x = coarse m, y = fine m, z = luma, w = sat)",
                     "两档低频噪声，以 1 为中心调制亮度与饱和度。饱和度那一档用的是相位偏移过的噪声，" +
                     "免得亮与艳完全同步、看成同一张图案。");
            }

            Header("⑤ Mid-Freq Relief");
            Prop(materialEditor, props, "_VistaEnableRelief", "Enable");
            using (new EditorGUI.DisabledScope(!IsOn(material, "_VistaEnableRelief")))
            {
                Prop(materialEditor, props, "_VistaReliefParams", "Params (x = coarse m, y = coarse slope, z = fine m, w = fine slope)",
                     "y/w 是斜率不是高度：换尺度时视觉强度不变。典型 |梯度| ≈ 0.4，" +
                     "所以 0.5 对应约 11° 的面倾斜。无距离淡出 —— 这是远景唯一还活着的法线来源。");
                EditorGUILayout.HelpBox(
                    "程序化法线由 value noise 的解析导数给出，没有 mip 塌缩。本工程四张层法线图的" +
                    "切向振幅实测只有 0.008 / 0.033 / 0.008 / 0.073，其中两张已落到 8-bit 量化步长" +
                    "（1/127.5 = 0.0078）上 —— 调大 normalScale 只会放大条带。中远距离的形体只能由这一路给。",
                    MessageType.None);
            }

            Header("⑥ Layer Tone");
            Prop(materialEditor, props, "_VistaEnableLayerTone", "Enable");
            using (new EditorGUI.DisabledScope(!IsOn(material, "_VistaEnableLayerTone")))
            {
                Prop(materialEditor, props, "_VistaLayerContrast", "Contrast (rgba = layer 0..3)",
                     "绕该层自己的亮度中心拉伸，不是绕 0.5 —— 绕 0.5 会把偏暗的层连带提亮，那是换曝光不是加对比。");
                Prop(materialEditor, props, "_VistaLayerSaturate", "Saturation (rgba = layer 0..3)");
                EditorGUILayout.HelpBox(
                    "全 1 = 恒等。由 Window/Vista/Terrain 的材质工具按各层实测亮度 p05/p95 标定后写入。",
                    MessageType.None);
            }

            EditorGUILayout.Space(8);
            materialEditor.RenderQueueField();
            materialEditor.EnableInstancingField();

            if (EditorGUI.EndChangeCheck())
            {
                foreach (var obj in materialEditor.targets)
                {
                    if (obj is Material m)
                        ApplyKeywords(m);
                }
            }
        }

        static bool IsOn(Material m, string prop)
            => m.HasProperty(prop) && m.GetFloat(prop) > 0.5f;

        public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
        {
            base.AssignNewShaderToMaterial(material, oldShader, newShader);
            ApplyKeywords(material);
        }

        public override void ValidateMaterial(Material material) => ApplyKeywords(material);
    }
}
