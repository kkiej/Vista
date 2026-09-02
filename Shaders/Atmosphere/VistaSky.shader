// Vista 物理天空盒。采样逐帧的 Sky-View LUT，并解析绘制太阳圆盘。
//
// 为什么做成 Skybox 材质而不是一个全屏 pass：
//   Skybox 材质挂在 RenderSettings 上，Unity 的天空盒反射探针、Scene 视图、
//   材质预览、以及所有"背景是什么"的查询都会自动跟着走。全屏 pass 只能盖住主相机。
// 代价见 CHANGELOG：环境光/反射不能直接用 Unity 对这张天空盒的卷积结果
// （太阳圆盘会造成能量重复计算），环境光由 Task #5 的 SH 投影单独提供。

Shader "Vista/Sky"
{
    Properties
    {
        [Toggle] _VistaDrawSunDisc ("绘制太阳圆盘", Float) = 1
        _VistaSkyMultiplier ("天空亮度倍率（美术微调）", Range(0, 4)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType"     = "Background"
            "Queue"          = "Background"
            "PreviewType"    = "Skybox"
        }

        Cull Off
        ZWrite Off
        ZTest LEqual

        Pass
        {
            Name "VistaSky"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            // 不含 gles：SkyView LUT 由 compute 产出，GLES3.x 走降级路径（见 CHANGELOG）
            #pragma only_renderers d3d11 vulkan metal playstation xboxone xboxseries switch

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.kkiej.vista/ShaderLibrary/AtmosphereScattering.hlsl"
            // 只为了 VistaSkyAmbientMean()（雾的天光环境项）。这个 include 带来一个
            // StructuredBuffer 绑定，所以它在**天空盒**里，不在 AtmosphereScattering.hlsl 里 ——
            // 后者被 Vista/Lit、水面等大量片元着色器 include，见那边 fogAmbientRadiance 的注释。
            #include "Packages/com.kkiej.vista/ShaderLibrary/SphericalHarmonics.hlsl"

            // SRP Batcher 兼容：所有材质属性必须在 UnityPerMaterial 里
            CBUFFER_START(UnityPerMaterial)
                float _VistaDrawSunDisc;
                float _VistaSkyMultiplier;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 eyeRayWS   : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                // 天空盒绘制时 Unity 把相机朝向烘进了 object->world，
                // 所以物体空间顶点位置经这个矩阵旋转后就是世界空间视线方向。
                output.eyeRayWS = mul((float3x3)UNITY_MATRIX_M, input.positionOS.xyz);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // 逐视图 cbuffer 尚未写入（材质 Inspector 预览、AtmospherePass 还没跑）时
                // viewHeight 会是 0，后面 length/normalize 全是 NaN。这里直接兜底为黑，
                // 免得预览缩略图和第一帧出现花屏。
                if (_VistaViewPosKm.w < 1.0)
                    return half4(0.0, 0.0, 0.0, 1.0);

                float3 rayDir = normalize(input.eyeRayWS);
                float3 posKm  = _VistaViewPosKm.xyz;
                float3 sunDir = _VistaSunDirection.xyz;

                float3 luminance = VistaSampleSkyViewLut(posKm, rayDir, sunDir);

                if (_VistaDrawSunDisc > 0.5)
                {
                    float  viewHeight = _VistaViewPosKm.w;
                    float3 up = posKm / viewHeight;
                    float  mu = dot(rayDir, up);
                    // 视线打到星球本体时太阳被地面挡住，不画圆盘
                    if (!VistaRayIntersectsGround(viewHeight, mu))
                    {
                        // 透射率沿**视线**取，而不是沿"到太阳"的方向——看向圆盘时
                        // 两者本就同向，用视线的 mu 才能保证圆盘与周围天空的衰减连续，
                        // 不然日落时圆盘边缘会有一圈色差。
                        float3 transmittance = VistaSampleTransmittanceToSun(viewHeight, mu);
                        luminance += VistaSunDisc(rayDir, sunDir, transmittance);
                    }
                }

                luminance *= _VistaSkyMultiplier;

                // 雾（#18b）。必须在曝光**之前**：VistaApplyFogToSky 加进来的
                // albedo·J·(1−T) 是绝对光度量（J 里的 sunIlluminance 是 12 万 lux）。
                // 放到曝光之后的症状是天空上的雾亮 4 万倍。
                //
                // 太阳圆盘刻意在被衰减的那一项里：浓雾里看不见太阳本体，
                // 只剩一团被 HG 相位抬亮的雾 —— 那正是 albedo·J·(1−T) 给出的东西。
                VistaApplyFogToSky(luminance, posKm, rayDir, sunDir,
                                   _VistaSun.xyz, VistaSkyAmbientMean());

                luminance *= VISTA_EXPOSURE;

                // fp16 渲染目标下 65504 就溢出成 inf，进而污染 bloom / tonemap。
                // 太阳圆盘乘完曝光仍可达 1e4~1e5 量级，所以必须钳。
                luminance = min(luminance, 60000.0);

                return half4(luminance, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
