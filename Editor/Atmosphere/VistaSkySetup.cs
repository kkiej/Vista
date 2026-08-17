using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista.Editor
{
    /// <summary>
    /// 一键接入：生成物理天空盒材质、挂到当前场景的 <see cref="RenderSettings.skybox"/>、
    /// 并把 <see cref="VistaAtmosphereFeature"/> 装到当前管线的 Renderer 上。
    ///
    /// 为什么走「Skybox 材质」而不是自己画一个全屏 pass：
    ///   Unity 的环境光（AmbientMode.Skybox）与反射探针都是从 skybox 材质渲一张 cubemap
    ///   卷积出来的。走材质等于免费接上这两条链路；自己画全屏 pass 就得把这两套都重写。
    ///   代价是天空只能在 DrawSkyboxPass 那个时机画（不透明之后、透明之前），
    ///   对我们没有影响 —— aerial perspective 是独立 pass。
    ///
    /// 为什么连 RendererFeature 也一起装，而不是让用户去 Inspector 里点：
    ///   材质和 feature 少一个天空就是黑的，而两者的报错完全不同（少材质是"什么都没画"，
    ///   少 feature 是"采样到未初始化的 LUT"）。分两步做等于给自己留一个高频踩坑点。
    /// </summary>
    static class VistaSkySetup
    {
        const string k_Folder       = "Assets/Settings/Vista";
        const string k_MaterialPath = k_Folder + "/VistaSky.mat";

        [MenuItem("Window/Vista/Setup Sky (Material + Renderer Feature)", priority = 100)]
        static void Setup()
        {
            var resources = VistaRuntimeResources.Get();
            if (resources == null || resources.skyShader == null)
            {
                Debug.LogError("[Vista] 找不到 VistaRuntimeResources 或 Vista/Sky shader。" +
                               "确认当前管线是 URP，且 package 已正确导入。");
                return;
            }

            var log = new List<string>();
            SetupSkyboxMaterial(resources, log);
            SetupRendererFeature(log);

            // MCP 侧多行 Debug.Log 只会显示第一行，统一压平
            Debug.Log("[Vista] " + string.Join("  |  ", log));
        }

        static void SetupSkyboxMaterial(VistaRuntimeResources resources, List<string> log)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(k_MaterialPath);
            if (material == null)
            {
                Directory.CreateDirectory(k_Folder);
                material = new Material(resources.skyShader);
                AssetDatabase.CreateAsset(material, k_MaterialPath);
                log.Add("已创建天空盒材质 " + k_MaterialPath);
            }
            else if (material.shader != resources.skyShader)
            {
                // shader 被换掉（比如 package 路径变了）时修回来，而不是新建一份，
                // 避免场景引用指向旧资产
                material.shader = resources.skyShader;
                EditorUtility.SetDirty(material);
                log.Add("已修复天空盒材质的 shader 引用");
            }
            else
            {
                log.Add("天空盒材质已存在");
            }
            AssetDatabase.SaveAssets();

            if (RenderSettings.skybox != material)
            {
                // RenderSettings 是场景内的隐藏单例，拿不到公开的 Object 引用，
                // 所以没法 Undo.RecordObject，只能标脏让用户存盘。
                RenderSettings.skybox = material;
                EditorSceneManager.MarkSceneDirty(
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene());
                log.Add("已设为当前场景 skybox（场景已标脏，记得保存）");
            }
            else
            {
                log.Add("当前场景 skybox 已指向它");
            }
        }

        /// <summary>
        /// 把 feature 装到**当前生效的**管线资产的所有 Renderer 上。
        /// 不遍历工程里所有 UniversalRendererData：那会连移动端/其他分支的 renderer 一起改，
        /// 而那些分支的质量分级还没做（Task #7）。
        /// </summary>
        static void SetupRendererFeature(List<string> log)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
            {
                log.Add("⚠ 当前渲染管线不是 URP，跳过 RendererFeature 安装");
                return;
            }

            var pipelineSO = new SerializedObject(pipeline);
            var dataList = pipelineSO.FindProperty("m_RendererDataList");
            if (dataList == null || dataList.arraySize == 0)
            {
                log.Add("⚠ 管线资产上没有 Renderer，跳过 RendererFeature 安装");
                return;
            }

            for (int i = 0; i < dataList.arraySize; i++)
            {
                var data = dataList.GetArrayElementAtIndex(i).objectReferenceValue
                           as ScriptableRendererData;
                if (data == null) continue;

                if (AddFeature(data, out string message))
                    log.Add(data.name + "：" + message);
                else
                    log.Add("⚠ " + data.name + "：" + message);
            }
        }

        /// <summary>
        /// 复刻 URP <c>ScriptableRendererDataEditor.AddComponent</c>：
        /// feature 是 RendererData 的**子资产**，除了列表本身还要同步 m_RendererFeatureMap
        /// （URP 用它在资产迁移 / 引用丢失后按 localFileId 重建引用）。
        /// 少写 map 的症状是重启 Editor 后 feature 变 None。
        /// </summary>
        static bool AddFeature(ScriptableRendererData data, out string message)
        {
            var so = new SerializedObject(data);
            var features = so.FindProperty("m_RendererFeatures");
            var map      = so.FindProperty("m_RendererFeatureMap");
            if (features == null || map == null)
            {
                message = "找不到 m_RendererFeatures / m_RendererFeatureMap 字段（URP 版本不兼容？）";
                return false;
            }

            for (int i = 0; i < features.arraySize; i++)
            {
                if (features.GetArrayElementAtIndex(i).objectReferenceValue is VistaAtmosphereFeature)
                {
                    message = "Vista Atmosphere feature 已存在";
                    return true;
                }
            }

            var feature = ScriptableObject.CreateInstance<VistaAtmosphereFeature>();
            feature.name = nameof(VistaAtmosphereFeature);
            Undo.RegisterCreatedObjectUndo(feature, "Add Vista Atmosphere Feature");

            if (EditorUtility.IsPersistent(data))
                AssetDatabase.AddObjectToAsset(feature, data);
            EditorUtility.SetDirty(data);

            // AddObjectToAsset 之后 localId 立刻可取（不用等 SaveAssets），
            // URP 自己的 AddComponent 也是这么写的。
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;

            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;

            so.ApplyModifiedProperties();

            if (EditorUtility.IsPersistent(data))
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            message = "已装入 Vista Atmosphere feature";
            return true;
        }
    }
}
