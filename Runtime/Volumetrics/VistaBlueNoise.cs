using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Vista
{
    /// <summary>
    /// 蓝噪声瓦片的解析与绑定（#22b）。
    ///
    /// 拿的是 URP 自己那张：<c>UniversalRenderPipelineRuntimeTextures.blueNoise64LTex</c>
    /// = <c>Textures/BlueNoise64/L/LDR_LLL1_0.png</c>，64×64、8 bit、
    /// <c>sRGBTexture: 0</c>、<c>filterMode: 0</c>（Point）。来源是 Christoph Peters 那套
    /// 免费的 void-and-cluster 图，**环形（toroidal）**的，所以平铺无缝。
    ///
    /// ------------------------------------------------------------------ 数据在哪个通道
    /// **在 <c>.a</c>，不在 <c>.r</c>** —— 这是 #22b 里读到全 0 的真凶。
    /// 文件名里的「LLL1」说的是**源 PNG** 里 R=G=B=L、A=1，但**通道是导入设置决定的，
    /// 不是文件名决定的**：那张 <c>.meta</c> 里
    /// <c>textureType: 10</c>（SingleChannel）+ <c>singleChannelComponent: 0</c>（Alpha）
    /// + <c>alphaUsage: 2</c>（FromGrayScale）⇒ 导入后是 <c>TextureFormat.Alpha8</c>
    /// （D3D11 侧 <c>DXGI_FORMAT_A8_UNORM</c>；注意 Unity 的 <c>GraphicsFormat</c> 枚举里
    /// **没有**任何 alpha-only 成员，所以那一侧只能印出一个无名的裸数字 54），
    /// 只有 A 通道存着数据，D3D11 上读 <c>.r</c> 逐像素得 0。
    /// 铁证是 URP 自己的消费点：<c>ShaderLibrary/LODCrossFade.hlsl:19</c> 拿**同一张资产**
    /// 读的就是 <c>.a</c>。判据⑰d 会把格式打印出来，⑰c 的「均值精确 0.5」是通道守卫。
    ///
    /// 结论不变（**只有一个独立通道**），所以三个抖动轴必须靠三次固定偏移抽头去解相关
    /// （Georgiev &amp; Fajardo 2016），而不是取 rgb 三个分量 —— 只是理由从
    /// 「R=G=B」换成了「格式里压根只有一个通道」。
    ///
    /// ------------------------------------------------------------------ 为什么 Vista 不自带一张
    /// 至今零二进制资产，而这是唯一一次差点要开这个头。放弃自烘的三条理由：
    /// 1. 引擎里已经有一张，而且是**可以从公开 API 拿到**的
    ///    （<c>IRenderPipelineResources</c> + <c>isAvailableInPlayerBuild =&gt; true</c>，
    ///    所以它进 Player 包，不是 Editor-only 资源）。
    /// 2. 自烘只在「要 &gt; 1 个独立通道」或「要 &gt; 64 的瓦片」时才有意义，
    ///    froxel 这个分辨率（屏幕/8）两条都用不到。
    /// 3. 一个生成器要能写回 package 目录，而 git URL 引用的 package 落在只读的
    ///    <c>Library/PackageCache</c> 里 —— 那会变成「file: 引用下能跑、换成 URL 就炸」。
    ///
    /// ------------------------------------------------------------------ 8 bit 够不够
    /// 够，而且这一条正好是判据⑰要量的东西：64×64 = 4096 个像素、256 个灰阶，
    /// void-and-cluster 的排序是**秩均匀**的 ⇒ 每一阶正好 16 个像素 ⇒ 均值精确 0.5
    /// ⇒ 抖动偏移 <c>(v − 0.5)</c> 的期望精确为 0，也就是**开抖动不引入密度偏差**。
    /// 这条会被一次 sRGB 重导入毁掉（直方图不再均匀 ⇒ 期望 ≠ 0 ⇒ 症状是
    /// 「开抖动之后雾整体偏浓或偏淡」），所以判据⑰量的是直方图与均值，不是「图存在」。
    /// </summary>
    public static class VistaBlueNoise
    {
        static Texture2D s_Texture;
        static RTHandle s_Handle;
        static bool s_Resolved;
        static string s_LastFailure;

        /// <summary>瓦片的边长（像素）。HLSL 侧用它把整数下标换成 uv。</summary>
        public const int k_TileSize = 64;

        /// <summary>这一次解析拿到了图吗。取不到时调用方必须回落到程序化档。</summary>
        public static bool available => Resolve() != null;

        /// <summary>取不到时的原因（拿到时为 null）。状态日志直接打印它。</summary>
        public static string lastFailure
        {
            get { Resolve(); return s_LastFailure; }
        }

        /// <summary>解析到的那张图（可能为 null）。判据⑰要读它的尺寸/格式。</summary>
        public static Texture2D texture => Resolve();

        /// <summary>
        /// 这张图的 <see cref="RTHandle"/> 包装（取不到图时为 null）。
        ///
        /// ------------------------------------------------------------------ 为什么不是 Shader.SetGlobalTexture
        /// #22b 一开始写的就是那一句，而它**到不了 compute kernel** ——
        /// 症状是核里 <c>_VistaFroxelBlueNoise.Load</c> 逐像素读到 0，
        /// 而 CPU 侧 <see cref="available"/> = true、绑定函数确实被调过、
        /// HLSL 里声明也在、shader ID 也对得上，**所有表面证据都显示接上了**。
        ///
        /// 因果是：<c>Shader.SetGlobalTexture</c> 写的是 Unity 的**立即态**全局属性表，
        /// 它**不进命令流**；而一次 compute dispatch 的纹理绑定是在命令流里解析的。
        /// 所以正确的那句话不是「compute 读不到全局」，而是
        /// **「全局纹理必须被写进命令流」**。
        ///
        /// 反例看起来存在：URP 的 <c>_MainLightShadowmapTexture</c> 确实能被 Vista 的
        /// 注入核读到（判据⑤a/⑤b 量到了 1312 个被遮挡点）。但那不是因为立即态全局管用 ——
        /// 而是 RenderGraph 的 <c>SetGlobalTextureAfterPass</c> 会在**每一趟 pass 之前**
        /// 用 <c>cmd.SetGlobalTexture</c> 把它**重新下发进命令流**
        /// （MainLightShadowCasterPass.cs:413）。拿它去论证 <c>Shader.SetGlobalTexture</c>
        /// 也行是错的。
        ///
        /// 而 <c>ComputeCommandBuffer.SetGlobalTexture</c> 的四个重载**只吃
        /// TextureHandle**，<c>SetComputeTextureParam</c> 的六个重载也一样 ——
        /// 没有任何入口接受一个裸 <c>Texture</c>。所以要让 RenderGraph 侧能拿到它，
        /// 唯一的路是 <c>RenderGraph.ImportTexture(RTHandle)</c>，
        /// 而那需要先把 <c>Texture2D</c> 包成 RTHandle，就是这里。
        /// （<c>RenderGraphResourceRegistry.ImportTexture</c> 显式允许「包着一张普通 2D
        /// 纹理、不能往里渲」的 RTHandle，见那里 :462-465 的分支。）
        ///
        /// 包装是**非持有**的（<c>RTHandles.Alloc(Texture)</c> 不设
        /// <c>m_RTHasOwnership</c>），所以 <see cref="Invalidate"/> 里 Release 它
        /// 不会销毁 URP 那张资产。
        /// </summary>
        public static RTHandle handle
        {
            get
            {
                var tex = Resolve();
                if (tex == null)
                    return null;

                // 懒创建而不是在 Resolve 里一起做：Resolve 会被 available / lastFailure
                // 在任意时机调到（含没有渲染管线的 Editor 早期），而 RTHandles 的默认
                // 实例要等管线初始化。绑定点才是真正需要 handle 的地方。
                s_Handle ??= RTHandles.Alloc(tex);
                return s_Handle;
            }
        }

        /// <summary>
        /// 域重载/管线切换之后重新解析。<c>GetRenderPipelineSettings</c> 的结果绑在
        /// 当前 <c>RenderPipelineGlobalSettings</c> 上，换一份 URP 全局设置资产
        /// （或从内置管线切过来）会让缓存的引用失效。
        ///
        /// RTHandle 包装也要一起还掉：它指着旧那张资产，留着就是一个指向
        /// 可能已被卸载的 <c>Texture2D</c> 的句柄。Release 只把它从 RTHandle 系统的
        /// 记录里摘掉（<c>m_RTHasOwnership</c> 为 false ⇒ 不销毁被包的纹理）。
        ///
        /// 调用点：<c>VistaAtmosphereFeature.Dispose</c>（唯一一处）。选它是因为那一趟
        /// 正好覆盖了让缓存失效的三件事 —— 换 URP 资产、改全局设置、shader 重编译，
        /// 于是不需要再单独挂 <c>RenderPipelineManager.activeRenderPipelineDisposed</c>。
        /// 「一个没有调用者的清理函数」等于一段永远不会被发现写错的代码。
        /// </summary>
        public static void Invalidate()
        {
            s_Handle?.Release();
            s_Handle = null;
            s_Resolved = false;
            s_Texture = null;
            s_LastFailure = null;
        }

        static Texture2D Resolve()
        {
            if (s_Resolved)
                return s_Texture;

            s_Resolved = true;
            s_Texture = null;

            // 官方给的取法（这个类的 XML 文档里就是这段）：
            //   GraphicsSettings.GetRenderPipelineSettings<UniversalRenderPipelineRuntimeTextures>()
            // 它在**非 URP** 管线下返回 null —— 那是一个正常返回值，不是异常。
            var res = GraphicsSettings.GetRenderPipelineSettings<UniversalRenderPipelineRuntimeTextures>();
            if (res == null)
            {
                s_LastFailure = "GraphicsSettings 里没有 UniversalRenderPipelineRuntimeTextures"
                              + "（当前不是 URP，或 URP 全局设置资产缺失）";
                return null;
            }

            var tex = res.blueNoise64LTex;
            if (tex == null)
            {
                s_LastFailure = "URP 的 RuntimeTextures 里 blueNoise64LTex 为空"
                              + "（全局设置资产里那一项被清掉了，Reset 一下可恢复）";
                return null;
            }

            // 尺寸不合就**不用**它，而不是「按实际尺寸算 uv」——
            // HLSL 侧的三路抽头偏移（见 VistaFroxelJitterOffset）是按 64 选的：
            // z 方向步进 17 与 64 互素，所以 z·17 mod 64 在 64 片上是双射。
            // 换成另一个边长这条互素性可能不再成立，症状是某些片的偏移重复 ——
            // 一个只有靠判据⑲才看得见、且在画面上表现为「某几片糊在一起」的问题。
            if (tex.width != k_TileSize || tex.height != k_TileSize)
            {
                s_LastFailure = $"blueNoise64LTex 尺寸是 {tex.width}×{tex.height}，"
                              + $"而抽头偏移是按 {k_TileSize} 选的（17 与 64 互素）";
                return null;
            }

            s_LastFailure = null;
            s_Texture = tex;
            return tex;
        }
    }
}
