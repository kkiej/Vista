Shader "Hidden/Vista/AerialPerspectiveComposite"
{
    // ========================================================================
    //  变体 A：全屏合成 Aerial Perspective
    //
    //  ------------------------------------------------------------ 为什么要一个全屏 pass
    //  AP LUT 只有被消费才会出现在画面上。URP 没有任何官方的「不透明着色之后」
    //  钩子：MixFog 写在 LitForwardPass.hlsl 的 frag 尾巴上，它所在的
    //  ShaderVariablesFunctions.hlsl 的 include guard 覆盖整个文件（劫持不了），
    //  Shader Graph 的 Lit 目标也只暴露表面属性块、没有 post-lighting 块。
    //  所以想覆盖任意材质（含第三方资源、URP 自带 Lit）只剩屏幕空间这一条路。
    //  Hillaire 2020 与 Frostbite 就是这么做的（它们是 deferred），
    //  HDRP 的 PhysicallyBasedSky 也是一次全屏合成到不透明上。
    //
    //  ------------------------------------------------------------ 为什么不读颜色目标
    //  最直觉的写法是「采样 cameraColor -> 算 -> 写回」，但同一张 RT 不能同时采样和写入，
    //  于是要么申请临时 RT 再拷回（双倍带宽），要么把 cameraColor 换成新贴图
    //  （得强制 requiresIntermediateTexture，多一次最终 blit）。
    //  这里两条都不走：**合成的两个乘子都可以由混合器完成**，
    //  于是本 pass 根本不采样颜色目标 ——
    //      Pass 0  Blend Zero SrcColor  ->  dst = dst · T
    //      Pass 1  Blend One One        ->  dst = dst + S
    //  合起来正好是 dst·T + S。代价是颜色目标被 ROP 读改写两遍。
    //
    //  这条路的额外好处不只是省一次拷贝：不读颜色目标意味着
    //  直接渲后台缓冲时也能用、MSAA 下逐样本混合天然正确、
    //  移动端上两趟画在同一个 RenderPass 里不会打断 tile（不额外 load/store）。
    //
    //  ------------------------------------------------------------ 单趟写法：查过，不存在
    //  硬件层面本来有一条：双源混合（dst = S·One + dst·Src1Color，
    //  SV_Target0 = S、SV_Target1 = T），一趟就能算完。
    //  **但 Unity 没有暴露它** —— UnityEngine.Rendering.BlendMode 里根本没有
    //  Src1Color/Src1Alpha（反射实测：只有 Zero/One/Src|Dst 的 Color|Alpha 那十一个），
    //  ShaderLab 的 Blend 解析器也拒收 Src1Color（Parse error: unexpected TVAL_ID）。
    //  这一条是先写进 shader、被自检报出编译错误之后才查清的，记在这里免得再试一遍。
    //
    //  其余混合方程也都表达不出 dst·T + S：Blend One SrcAlpha 只能给出灰度 T，
    //  Blend One OneMinusSrcColor 里的 dst 系数是 1-S 而不是 T。
    //  所以两趟不是「先图省事」，而是在 Unity 里唯一的可移植形式。
    //
    //  ------------------------------------------------------------ 天空
    //  在 AfterRenderingSkybox 执行，靠远平面深度 clip 掉天空像素。
    //  用 clip 而不是让 T=1/S=0 自然收敛：clip 会连 ROP 写入一起跳过，
    //  天空占屏比例高的构图（本项目的远景）省掉的是真带宽。
    //  为什么必须排除天空：见 AerialPerspectiveComposite.hlsl 的注释。
    // ========================================================================

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
            #pragma target 3.5
            #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch

            #include "Packages/com.kkiej.vista/ShaderLibrary/AerialPerspectiveComposite.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                o.uv         = GetFullScreenTriangleTexCoord(input.vertexID);
                return o;
            }

            // 反投影出世界坐标，并 clip 掉天空。
            //
            // 抽成一个函数而不是让每个 pass 各写一遍，有两个不同的理由：
            //   · 两趟混合必须看到**完全相同**的中间量。若各自重算一遍反投影，
            //     浮点差异会让 T 与 S 落在相邻切片上，交界处出现 1 个纹素宽的暗边，
            //     而那种缝极难反查。
            //   · 下面那个调试档（Pass 2）要量的正是**这个操作数本身**。
            //     若调试档自己再写一遍反投影，它量到的就不是真实路径用的
            //     那个 positionWS —— 那样「A 与 B 的距离一致」这条判据
            //     担保的是一份只在调试档里存在的代码。
            float3 SampleWorldPos(float2 uv)
            {
                float rawDepth = SampleSceneDepth(uv);
                if (VISTA_AP_IS_SKY_DEPTH(rawDepth))
                    clip(-1.0);

                return ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
            }

            // 两趟共用的取值。
            void SampleTerms(float2 uv, out float3 addTerm, out float3 mulTerm)
            {
                VistaGetAerialPerspectiveTerms(uv, SampleWorldPos(uv), addTerm, mulTerm);
            }
        ENDHLSL

        // -------------------------------------------------------------- Pass 0
        //  dst = dst · T
        Pass
        {
            Name "Vista AP Composite (Multiply Transmittance)"
            Blend Zero SrcColor

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragMul

            float4 FragMul(Varyings input) : SV_Target
            {
                float3 addTerm, mulTerm;
                SampleTerms(input.uv, addTerm, mulTerm);
                return float4(mulTerm, 1.0);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------- Pass 1
        //  dst = dst + S
        Pass
        {
            Name "Vista AP Composite (Add In-Scattering)"
            Blend One One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragAdd

            float4 FragAdd(Varyings input) : SV_Target
            {
                float3 addTerm, mulTerm;
                SampleTerms(input.uv, addTerm, mulTerm);
                return float4(addTerm, 0.0);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------- Pass 2
        //  自检专用（#15 判据②a）：输出**变体 A 折出来的距离 (km)**，不做合成。
        //
        //  为什么把它做成本 shader 的一个 pass，而不是自检里另写一个全屏 shader：
        //  ②a 要判的是「A 与 B 折出来的距离是否一致」，而 A 的那个距离取决于
        //  深度图在**哪一个时刻**被拷贝、UNITY_MATRIX_I_VP 在**哪一个 pass** 里是什么、
        //  以及反投影用的是哪一份代码。另写一个 shader 就得把这三件事重新对齐一遍，
        //  而"对齐了"只能靠假设。做成 Pass 2 之后它由同一个 pass 实例、
        //  在同一个 RenderPassEvent、用同一份 SampleWorldPos 画出来 ——
        //  判据担保的就是真实路径。
        //
        //  运行时代价为零：一个从不被 Draw 的 pass 不产生任何 GPU 工作，
        //  它唯一的成本是一份编译产物（自检开关见 VistaAerialPerspectiveCompositePass）。
        //
        //  Blend Off + 写 alpha=1：它是一次纯覆盖写，不参与任何混合方程。
        Pass
        {
            Name "Vista AP Composite (Debug Distance)"
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDebugDistance

            float4 FragDebugDistance(Varyings input) : SV_Target
            {
                // 与 SampleTerms 调的是同一个 SampleWorldPos、同一个 VistaApDistanceKm，
                // 所以这一档与 Pass 0/1 实际用的距离**是同一个数**，不是它的等价复现。
                return float4(VistaApDistanceKm(SampleWorldPos(input.uv)).xxx, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
