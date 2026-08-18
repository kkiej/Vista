// 单位接缝探针。只做一件事：把 URP 着色器里读到的主平行光颜色原样输出。
//
// ── 为什么需要一个专门的 shader，不能拿 URP/Lit 去测 ──
//
// 要验的命题是「CPU 写进 Light.color/intensity 的数，着色器里读到的是不是
// T·lux·exposure/π」。中间夹着引擎的 VisibleLight.finalColor：
//   finalColor = Light.color.linear × intensity × (useColorTemperature ? CCT(K) : 1)
// 这一段没有任何 CPU 侧 API 能读回来，只能渲一次再读像素。
//
// 用 URP/Lit 的话，输出里还会掺进 GGX 高光（smoothness=0 时 roughness=1，
// 正入射仍有几个百分点）、GI、雾。那时候「数不对」就无法唯一归因到接缝上 ——
// 判据必须只有一个可能的失败源。albedo×lightColor×NdotL 那一步是纯算术，
// 已经由透射率自检的 C 项钉住，不需要再走一遍 GPU。
//
// 也不采样阴影：无参的 GetMainLight() 把 shadowAttenuation 定为 1，
// 而 light.color 本身不含衰减项，正是我们要的那个原始量。
Shader "Hidden/Vista/SeamProbe"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "SeamProbe"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // GetMainLight().color == _MainLightColor.rgb，即引擎算好的 finalColor。
                Light mainLight = GetMainLight();
                return float4(mainLight.color, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
