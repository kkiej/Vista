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

            // 两趟共用的取值：clip 掉天空，反投影出世界坐标，取回两个乘子。
            // 写成一个函数是为了让两趟看到**完全相同**的中间量 ——
            // 若各自重算一遍反投影，浮点差异会让 T 与 S 落在相邻切片上，
            // 交界处出现 1 个纹素宽的暗边，而那种缝极难反查。
            void SampleTerms(float2 uv, out float3 addTerm, out float3 mulTerm)
            {
                float rawDepth = SampleSceneDepth(uv);
                if (VISTA_AP_IS_SKY_DEPTH(rawDepth))
                    clip(-1.0);

                float3 positionWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                VistaGetAerialPerspectiveTerms(uv, positionWS, addTerm, mulTerm);
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
    }

    Fallback Off
}
