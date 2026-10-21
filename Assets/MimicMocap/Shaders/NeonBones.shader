// Neon look for the skeletons: bones are near-black where they face the camera and light up toward their edges
// (fresnel): a broad, dim fill that grows with the grazing angle and a tight, hot rim at the silhouette. Drawn
// opaque so bones hide what is behind them (the spine does not show through the skull and ribs); the bloom pass
// halos the edges against the black stage. _EmissionColor tints and scales the whole
// thing, which is also how the procedural skeleton fades in and out (it drives _EmissionColor through a property
// block).
Shader "MimicMocap/Neon Bones"
{
    Properties
    {
        [HDR] _BaseColor ("Fill colour", Color) = (0.08, 0.32, 1.1, 1)
        [HDR] _RimColor ("Rim colour", Color) = (0.55, 0.9, 2.6, 1)
        _RimPower ("Rim power", Range(0.5, 8)) = 2
        _Fill ("Fill (broad glow toward the edges)", Range(0, 2)) = 1
        _Core ("Core (brightness where a bone faces the camera)", Range(0, 1)) = 0.08
        _MainTex ("Detail (luminance)", 2D) = "white" {}
        _Detail ("Detail strength", Range(0, 1)) = 0
        [HDR] _EmissionColor ("Intensity tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Neon"
            Tags { "LightMode" = "UniversalForward" }
            Blend One Zero
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _RimColor;
                float _RimPower;
                float _Fill;
                float _Core;
                float4 _MainTex_ST;
                float _Detail;
                float4 _EmissionColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.viewWS = GetWorldSpaceViewDir(positionWS);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 n = normalize(i.normalWS);
                float3 v = normalize(i.viewWS);
                float ndv = saturate(dot(n, v));
                float grazing = 1.0 - ndv;            // 0 facing the camera, 1 at the silhouette
                float rim = pow(grazing, _RimPower);  // tight, hot edge
                float lum = dot(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).rgb, float3(0.2126, 0.7152, 0.0722));
                float detail = lerp(1.0, lum, _Detail);
                float3 fill = _BaseColor.rgb * detail * (_Core + _Fill * grazing * grazing); // dark core, broad glow outward
                float3 col = (fill + _RimColor.rgb * rim) * _EmissionColor.rgb;
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
