Shader "Hidden/Vista/FroxelDebug"
{
    // ========================================================================
    //  近层 froxel 体的调试视图（#21）
    //
    //  ---------------------------------------------------------- 为什么需要它
    //  #21 交付的是一张 3D 表（累积 in-scatter 与 1 − T），而**消费它的合成还没写**
    //  （合成在 #25 的统一采样函数里落地）。没有这个视图，#21 交付的东西在画面上
    //  完全不存在，唯一的证据是日志里的数字 —— 那等于把一整条写入路径交付成
    //  「一段永远不会被发现写错的代码」。
    //
    //  更重要的是它在合成落地**之后**仍然有用，所以它是永久资产而不是脚手架：
    //  合成之后「画面上的雾不对」有两个来源 —— 表算错了、或者合成读错了
    //  （w 坐标差半个纹素、透射率乘反、分层接手点错位）。这两种失效在最终画面上
    //  长得一模一样，而把表**单独**画出来是唯一能把它们分开的手段。
    //  HDRP 的 Volumetric Fog debug view、UE5 的 VisualizeVolumetricFog 存在的
    //  理由完全相同，两者也都没有在功能完成后删掉。
    //
    //  ---------------------------------------------------------- 为什么整屏替换而不是叠加
    //  叠加（半透明覆盖、画中画）会让「表里的内容」与「场景里的内容」混在一个
    //  像素上，于是「表是空的」与「表很淡」再次变得无法区分 —— 这个视图存在的
    //  全部目的就是消除这类混淆，它自己不能再引入一次。
    //  于是：Blend 关掉，整屏覆盖；Off 档**不记录这一趟 pass**（失能态 = 零态，
    //  不是「记录了但写了个占位内容」）。
    //
    //  ---------------------------------------------------------- 为什么按场景深度采样
    //  三个主档（积分 RGB / 积分 Alpha / 注入 RGB）都在**像素自己的场景深度**上采样，
    //  而不是显示某个固定切片。理由是那正是将来合成要吃的那一个操作数：
    //  看到的就是会被合成进去的量。若这里改成「显示第 32 片」，视图与合成看的
    //  就不是同一个数，那时它担保的是一份只在调试档里存在的取样逻辑
    //  —— 与 AP 合成 shader 里 SampleWorldPos 抽成函数的理由是同一条。
    //
    //  「单片」档回答的是另一个问题：表**自身**在某个深度上有没有洞、有没有条带。
    //  深度耦合的视图回答不了它，因为场景深度只覆盖到 z-buffer 里存在的那些距离，
    //  空中、天空方向上的切片永远不会被任何像素采到。两个档互补，缺一个都会
    //  留下一片没人看过的表。
    //
    //  ---------------------------------------------------------- 越界不静默钳死
    //  VistaFroxelEncodeDistance 故意不 clamp（见 FroxelVolume.hlsl 的注释）：
    //  e > 1 是「这个像素比近层的远边界更远，归 AP LUT 管」，
    //  e < 0 是「比近端更近」。这两种情况在这里各画一个**独立的底色**，
    //  不是钳到端点上 —— 钳死之后「近层没覆盖到这里」与「这里雾很淡」
    //  在画面上就一样了，而前者恰好是分层接手点错位的症状。
    //  顺带：天空的深度是远平面，天然落进 e > 1，不需要单独分支。
    //  这条底色的实用价值在 #27：近层覆盖了画面的多大一块，是能直接看出来的。
    //
    //  ---------------------------------------------------------- 不做色调映射
    //  表里存的是**预曝光**辐亮度，这里原样乘一个 gain 写出去，不套 tonemap。
    //  理由：套了之后「表饱和了」与「tonemap 滚到顶了」在画面上无法区分。
    //  这一趟因此排在后处理**之后**，免得被别人的 tonemap 改写。
    //  代价是暗部要靠 gain 手动抬 —— 一个能读的旋钮，比一条看不见的曲线好。
    // ========================================================================

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        Pass
        {
            Name "Vista Froxel Debug View"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            // 深度 -> w 坐标只允许有一份实现：这里 include 的就是注入/积分核用的那一份。
            #include "Packages/com.kkiej.vista/ShaderLibrary/FroxelVolume.hlsl"

            // 绑定点名字与 VistaShaderIDs 里**已经保留的那两个**一致
            // （_VistaFroxelIntegral 是积分表的 SRV 名，RW 名是 ..IntegralRW）。
            // 自己另起一个名字的症状是绑定静默失效 —— 采到一张全零的默认 3D 纹理，
            // 而全零在这里是一个合法读数（雾很淡），于是「没绑上」与「没有雾」一样。
            TEXTURE3D(_VistaFroxelIntegral);
            TEXTURE3D(_VistaFroxelInjectionRead);

            // x = gain，y = 单片档的切片下标，z = 档位，w 保留。
            // 走 MaterialPropertyBlock 而不是全局：这一趟因此不需要
            // AllowGlobalStateModification，「本 pass 不改全局」在代码里读得出来。
            float4 _VistaFroxelDebugParams;

            #define VISTA_DBG_GAIN  _VistaFroxelDebugParams.x
            #define VISTA_DBG_SLICE _VistaFroxelDebugParams.y
            #define VISTA_DBG_MODE  ((uint)_VistaFroxelDebugParams.z)

            // 与 C# 的 Vista.FroxelDebugView 逐个对应（Off = 0 不会到这里，
            // 那一档整趟 pass 不被记录）。这处两份定义为什么不配判据，见那个枚举的注释。
            #define VISTA_DBG_INTEGRAL_RGB   1u
            #define VISTA_DBG_INTEGRAL_ALPHA 2u
            #define VISTA_DBG_INJECTION_RGB  3u
            #define VISTA_DBG_SINGLE_SLICE   4u

            // 越界底色。刻意选两个在真实雾里不可能出现的颜色：
            // 雾的 in-scatter 是被太阳/天光照亮的介质，永远不会是纯品红或纯青。
            // 「哨兵值与被测量撞车」是本项目记过的坑，这里的哨兵是颜色。
            static const float3 k_BeyondFar  = float3(0.18, 0.00, 0.18);   // 归 AP LUT
            static const float3 k_CloserNear = float3(0.00, 0.14, 0.18);   // 比近端更近

            struct Attributes { uint vertexID : SV_VertexID; };
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

            // 像素的场景深度 -> 近层体的 w 坐标。返回未钳死的 e，越界由调用方处理。
            float PixelEncodedDepth(float2 uv)
            {
                float rawDepth = SampleSceneDepth(uv);
                float3 posWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);

                // 两个都是刻意的选择：
                //
                // ① 起点用 _VistaFroxelCameraWS.xyz，不是 _WorldSpaceCameraPos。
                //    表是从那个点建起来的（注入核：posWS = _VistaFroxelCameraWS.xyz
                //    + d·rayDir），而那条 uniform 存在的理由恰恰是**不信任**引擎全局
                //    （Editor 立即模式下它可能是上一台相机留下的值）。
                //    用引擎全局量出来的距离，与表的构造起点在最需要一致的场合不一致。
                //    ——— 这与 AP 合成端用 _VistaViewPosKm 而不用 _WorldSpaceCameraPos
                //    是同一条理由。
                //
                // ② 用**径向距离**而不是沿视线 z 的深度：注入核里 rayDir 是归一化的
                //    （VistaApFroxelRayDirection 末尾 normalize），所以那里的 d 就是
                //    欧氏距离。混用两者的症状是画面中心对、四角的雾偏薄（差一个 cos），
                //    而「中间是对的」这种偏差最容易被当成正常。
                float dist = length(posWS - _VistaFroxelCameraWS.xyz);
                return VistaFroxelEncodeDistance(dist);
            }

            float4 Frag(Varyings input) : SV_Target
            {
                uint mode = VISTA_DBG_MODE;

                if (mode == VISTA_DBG_SINGLE_SLICE)
                {
                    // 屏幕 uv 直接当表的 XY，切片下标由参数给。
                    // 点采样：这一档要看的是表**自己的**纹素，线性插值会把
                    // 「有一列没被写过」抹成一条平缓的暗带。
                    float w = (VISTA_DBG_SLICE + 0.5) * VISTA_FROXEL_RCP_SLICES;
                    float4 v = SAMPLE_TEXTURE3D_LOD(_VistaFroxelIntegral,
                                                    sampler_PointClamp,
                                                    float3(input.uv, w), 0);
                    return float4(v.rgb * VISTA_DBG_GAIN, 1.0);
                }

                float e = PixelEncodedDepth(input.uv);
                if (e > 1.0) return float4(k_BeyondFar,  1.0);
                if (e < 0.0) return float4(k_CloserNear, 1.0);

                // 线性采样：这三档要与将来的合成看到同一个数，而合成是线性采样的。
                float3 uvw = float3(input.uv, e);
                if (mode == VISTA_DBG_INJECTION_RGB)
                {
                    float4 v = SAMPLE_TEXTURE3D_LOD(_VistaFroxelInjectionRead,
                                                    sampler_LinearClamp, uvw, 0);
                    return float4(v.rgb * VISTA_DBG_GAIN, 1.0);
                }

                float4 integral = SAMPLE_TEXTURE3D_LOD(_VistaFroxelIntegral,
                                                       sampler_LinearClamp, uvw, 0);
                if (mode == VISTA_DBG_INTEGRAL_ALPHA)
                {
                    // alpha 不乘 gain：它已经是归一化的 1 − T ∈ [0,1]，
                    // 乘上去会让「雾很厚（趋近 1）」与「gain 开大了」看起来一样。
                    return float4(integral.aaa, 1.0);
                }

                return float4(integral.rgb * VISTA_DBG_GAIN, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
