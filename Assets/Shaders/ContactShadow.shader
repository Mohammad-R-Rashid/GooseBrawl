// Faint multiplicative contact darkening under the goose (ambient occlusion patch), replaces the old
// alpha-blended black blob. Multiplies the frame buffer so it darkens the real floor instead of painting on it.
Shader "GooseBrawl/ContactShadow"
{
    Properties
    {
        _BaseMap ("Radial mask", 2D) = "white" {}
        _Strength ("Strength", Range(0, 1)) = 0.28
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-11" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "ContactShadow"
            Tags { "LightMode" = "UniversalForward" }
            Blend DstColor Zero
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _Strength;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                half mask = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).a;
                half darken = 1.0h - mask * _Strength;
                return half4(darken, darken, darken, 1.0h);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
