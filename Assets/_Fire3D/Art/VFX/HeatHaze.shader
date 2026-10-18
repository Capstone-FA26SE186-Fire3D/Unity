// Heat haze for fire particles: refracts the opaque scene behind a soft round mask with two scrolling noise
// layers. Needs the camera opaque texture, which FireQuality enables only on capable devices (it costs a
// full-screen copy). Vertex alpha scales strength so particles fade in/out smoothly.
Shader "Fire3D/HeatHaze"
{
    Properties
    {
        _BaseMap ("Distortion Noise (RG)", 2D) = "gray" {}
        _Strength ("Strength (screen UV)", Range(0, 0.05)) = 0.012
        _Speed ("Rise Speed", Float) = 1.1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+20" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

        Pass
        {
            Name "HeatHaze"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                float4 screen : TEXCOORD1;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _Strength;
                float _Speed;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                o.color = input.color;
                o.screen = ComputeScreenPos(o.positionCS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float t = _Time.y * _Speed;
                half2 a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv * 0.8 + float2(0.0, -t * 0.32)).rg;
                half2 b = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv * 1.6 + float2(0.37, -t * 0.61)).rg;
                float2 offset = (a + b - 1.0);
                float2 d = i.uv - 0.5;
                half mask = saturate(1.0 - dot(d, d) * 4.0);
                mask *= mask;
                float2 suv = i.screen.xy / i.screen.w + offset * (_Strength * mask * i.color.a);
                half3 scene = SampleSceneColor(suv);
                return half4(scene, mask * i.color.a);
            }
            ENDHLSL
        }
    }
}
