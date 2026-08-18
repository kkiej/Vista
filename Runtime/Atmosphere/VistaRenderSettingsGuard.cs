#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Vista
{
    /// <summary>
    /// 场景保存前，把被大气模块改写过的 <c>RenderSettings</c> 还原回原值；保存完不做任何事
    /// —— 下一帧渲染会自然重新写入。
    ///
    /// 为什么"只在 Dispose 时还原"不够：
    /// <c>ambientMode</c> / <c>ambientProbe</c> / <c>defaultReflectionMode</c> /
    /// <c>customReflectionTexture</c> / <c>reflectionIntensity</c> 全是**逐场景序列化**状态。
    /// Dispose 挡得住"关掉 feature"，挡不住"feature 正常工作时按 Ctrl+S" —— 那一下会把
    /// Custom + 最后一帧那份 SH 落盘。而 renderer 资产是**全工程共享**的，于是
    /// "给作品集场景装了个 feature"会让工程里任何被打开并保存过的场景都中招。
    ///
    /// 这不是"下次运行会覆盖回来"那么轻：关掉 Vista 之后场景**留在** Custom + 绝对光度量
    /// 的探针上，画面全白、阴影不可见，而症状看起来跟 Vista 完全无关 —— 这正是本次
    /// "物体没有阴影了"的排查里最花时间的一段。
    ///
    /// 已知残留：写 <c>ambientProbe</c> 会把场景置脏（实测），太阳一动就会再脏一次，
    /// 所以装了本模块的场景在 Editor 里会长期挂着 <c>*</c>。**不**用
    /// <c>EditorSceneManager.ClearSceneDirtiness</c> 去消它 —— 那会连带清掉用户自己的
    /// 未保存改动的脏标记，于是关闭时不再提示保存，是拿数据丢失换一个星号。
    /// 长期挂 <c>*</c> 是这条设计的既定代价，不是可以顺手消掉的瑕疵。
    ///
    /// 本层**挡不住**的一种情况：场景在守卫存在之前就已经把 Custom 写进磁盘。
    /// 那时首次写入前扣下的"原值"本身就是脏的，还原只会保持污染。
    /// 这种状态无法自动分辨（Custom 也可能是用户有意设的），必须由人显式复位 ——
    /// 那条路走 <see cref="ForgetBaselines"/>，见 <c>VistaSceneSkyStateUtility</c>。
    ///
    /// 只在 Editor 编译：运行时构建里场景不会被保存，这一层没有意义。
    /// </summary>
    public static class VistaRenderSettingsGuard
    {
        static readonly List<IVistaRenderSettingsClient> s_Clients = new List<IVistaRenderSettingsClient>();
        static bool s_Hooked;

        public static void Register(IVistaRenderSettingsClient client)
        {
            if (client == null || s_Clients.Contains(client)) return;
            s_Clients.Add(client);

            if (s_Hooked) return;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            s_Hooked = true;
        }

        public static void Unregister(IVistaRenderSettingsClient client) => s_Clients.Remove(client);

        /// <summary>
        /// 让所有模块丢掉当前基线（不写回任何值）。调用后下一帧渲染会从**实时**值重新扣一份。
        /// 用途：把"已被污染的基线"换成刚复位好的干净值，使随后的保存能真正落盘干净状态。
        /// </summary>
        public static void ForgetBaselines()
        {
            for (int i = s_Clients.Count - 1; i >= 0; --i)
                s_Clients[i]?.ForgetRenderSettingsBaseline();
        }

        static void OnSceneSaving(Scene scene, string path)
        {
            // 不在这里判"scene 是不是活动场景"：每个还原动作内部都带场景校验
            // （见 VistaSkyAmbientProbe.RestoreRenderSettings 与
            // VistaAtmospherePass.RestoreRenderSettings），在外面再判一次只会在
            // "保存非活动场景"这条路上把内层校验绕开。
            //
            // 倒序遍历：还原动作里可能触发 Unregister（feature 在保存期间被销毁）。
            for (int i = s_Clients.Count - 1; i >= 0; --i)
                s_Clients[i]?.RestoreRenderSettings();
        }
    }
}
#endif
