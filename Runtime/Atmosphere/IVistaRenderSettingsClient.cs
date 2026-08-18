namespace Vista
{
    /// <summary>
    /// 一个会改写场景全局 <c>RenderSettings</c> 的模块。
    ///
    /// 用接口而不是注册一对 <c>Action</c>：这两个动作必须**成对**且指向同一个实例
    /// （还原用的基线和要忘掉的基线得是同一份），委托对很容易在某次重构里被拆开注册，
    /// 而那种错误的表现是"复位之后保存仍然落盘 Custom"—— 又一个静默的数据问题。
    ///
    /// 不裹 <c>#if UNITY_EDITOR</c>（虽然唯一的消费者 <c>VistaRenderSettingsGuard</c> 裹了）：
    /// 实现方的这两个方法在运行时也要用（<c>Teardown</c> 会调还原），
    /// 让类声明里的基类列表随平台变化只会换来一串条件编译的接缝。
    /// </summary>
    public interface IVistaRenderSettingsClient
    {
        /// <summary>把改过的属性还原成本模块首次写入前的值。之后基线归零，下一帧会重新扣。</summary>
        void RestoreRenderSettings();

        /// <summary>
        /// 只丢掉基线，**不**写回任何值。
        /// 给"当前场景的基线本身就是脏的"这种情况用（见 <c>VistaSceneSkyStateUtility</c>）。
        /// </summary>
        void ForgetRenderSettingsBaseline();
    }
}
