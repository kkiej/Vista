using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vista.Editor
{
    /// <summary>
    /// 把当前场景的环境光 / 反射全局状态复位成引擎默认（Skybox 驱动）。
    ///
    /// 为什么需要这个菜单、而不是靠 <c>VistaRenderSettingsGuard</c> 自动收拾干净：
    /// 守卫还原的是"本次运行首次写入前扣下的原值"。如果某个场景在守卫存在之前
    /// 就已经被写进磁盘（<c>m_AmbientMode: 4</c> / <c>m_DefaultReflectionMode: 1</c>），
    /// 那么这次打开场景时扣下来的"原值"**本身就是被污染的那份**，守卫会忠实地
    /// 把 Custom 还原回去 —— 越保存越干净是不会发生的，它只会保持污染。
    /// 这种"基线本身错了"的状态没法自动分辨（Custom 可能是用户自己有意设的），
    /// 只能由人显式说一句"复位"。
    ///
    /// 用法：点一次本菜单即可 —— 它会复位实时值、让守卫丢掉旧基线、然后保存场景。
    /// 顺序很关键：丢基线必须在保存**之前**，否则守卫会在写盘前把脏基线还原回去，
    /// 表现为"复位了、也保存了，但磁盘上还是 Custom"。
    ///
    /// 不动 <c>m_SkyboxMaterial</c>：那是用户选的天空盒（作品集场景里就是 VistaSky），
    /// 与"谁在驱动环境光"是两件事，一起清掉会让场景变成纯黑天。
    /// </summary>
    static class VistaSceneSkyStateUtility
    {
        [MenuItem("Window/Vista/Reset Scene Ambient && Reflection", priority = 123)]
        static void Reset()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();

            var beforeAmbient = RenderSettings.ambientMode;
            var beforeReflect = RenderSettings.defaultReflectionMode;
            float beforeIntensity = RenderSettings.reflectionIntensity;

            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 1f;
            RenderSettings.customReflectionTexture = null;

            // 让正在运行的模块丢掉基线，改从刚复位好的实时值重新扣。
            VistaRenderSettingsGuard.ForgetBaselines();

            // 显式置脏：复位的意义在于**落盘**，而 RenderSettings 的赋值是否置脏
            // 不该由我们来赌（这条项目里已经踩过一次 —— 曾经有段注释断言
            // "环境光那几个属性不会置脏"，实测是错的）。
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);

            bool saved = false;
            if (!string.IsNullOrEmpty(scene.path))
                saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);

            Debug.Log(("[Vista] 场景环境光/反射已复位为 Skybox 驱动"
                     + "\n　 ambientMode " + beforeAmbient + " → Skybox"
                     + "\n　 defaultReflectionMode " + beforeReflect + " → Skybox"
                     + "\n　 reflectionIntensity " + beforeIntensity.ToString("G6") + " → 1"
                     + "\n　 场景 " + (string.IsNullOrEmpty(scene.name) ? "(未命名)" : scene.name)
                     + (saved ? " 已保存（落盘干净值）"
                              : " **未保存**：场景还没有磁盘路径，请手动另存一次")
                     + "\n　 下一帧渲染会从复位后的值重新扣基线，此后保存不会再写入 Custom。")
                     .Replace("\r", "").Replace("\n", "  |  "));
        }
    }
}
