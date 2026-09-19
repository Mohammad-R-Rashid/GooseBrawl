// Invisible floor that only shows the shadows the goose, egg and nest cast on the real floor.
// Designed for AR: ZTest is off and the two depth tests happen in the shader:
//  - virtual occluders (the goose's own feet) via the virtual-only depth prepass (_CameraDepthTexture)
//  - real occluders (a foot, a chair) via the ARKit environment depth published by AREnvDepthPublisher
//    (keyword _GB_ENVDEPTH), with a soft tolerance so LiDAR noise never speckles the shadow.
Shader "GooseBrawl/ShadowCatcher"
{
    Properties
    {
        _Strength ("Shadow strength", Range(0, 1)) = 0.55
        _Tint ("Shadow tint", Color) = (0.02, 0.015, 0.03, 1)
        _EdgeFade ("Edge fade (0-1 of half size)", Range(0.05, 1)) = 0.35
        _EnvTolerance ("Env depth tolerance (m)", Range(0.02, 0.5)) = 0.12
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "ShadowCatcher"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _GB_ENVDEPTH
            #define _SURFACE_TYPE_TRANSPARENT 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Strength;
                half4 _Tint;
                float _EdgeFade;
                float _EnvTolerance;
            CBUFFER_END

            TEXTURE2D(_GB_EnvDepth);
            SAMPLER(sampler_GB_EnvDepth);
            float4x4 _GB_DisplayTransform;
            float _GB_EnvDepthValid;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.uv = v.uv;
                o.screenPos = ComputeScreenPos(p.positionCS);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 screenUV = i.screenPos.xy / max(i.screenPos.w, 1e-5);
                float fragEye = i.screenPos.w; // view-space depth of this floor fragment

                // 1. Virtual occluders: anything the depth prepass drew in front of the floor hides the shadow.
                float rawScene = SampleSceneDepth(screenUV);
                float sceneEye = LinearEyeDepth(rawScene, _ZBufferParams);
                float virtualVisible = step(fragEye - 0.004, sceneEye);

                // 2. Real occluders: ARKit environment depth (metres), soft tolerance.
                float realVisible = 1.0;
                #if defined(_GB_ENVDEPTH)
                if (_GB_EnvDepthValid > 0.5)
                {
                    float2 envUV = mul(float4(screenUV, 1.0, 1.0), _GB_DisplayTransform).xy;
                    float envDist = SAMPLE_TEXTURE2D(_GB_EnvDepth, sampler_GB_EnvDepth, envUV).r;
                    realVisible = smoothstep(-_EnvTolerance * 1.6, -_EnvTolerance * 0.4, envDist - fragEye);
                }
                #endif

                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                half shadow = MainLightRealtimeShadow(shadowCoord);
                shadow = lerp(shadow, 1.0h, GetMainLightShadowFade(i.positionWS));

                // Fade toward the quad edges so the catcher never shows a hard border.
                float2 c = abs(i.uv * 2.0 - 1.0);
                float edge = 1.0 - smoothstep(1.0 - _EdgeFade, 1.0, max(c.x, c.y));

                float a = (1.0 - shadow) * _Strength * edge * virtualVisible * realVisible;
                return half4(_Tint.rgb, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
