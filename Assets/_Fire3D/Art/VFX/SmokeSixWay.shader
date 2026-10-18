// Smoke lit from six directions (after Unity's "Realistic smoke with 6-way lighting"), for URP particle systems.
// Two linear atlases hold what each billow looks like when lit from right/top/back (A.rgb), left/bottom/front (B.rgb)
// and its transparency (A.a). Every light (main, fire lights, ambient) is turned into a direction in view space and
// blends the six values by squared direction components, so the side facing a flame glows orange and the far side
// stays dark, and it moves correctly as the camera or the fire moves. Flipbook frames blend through the particle
// vertex streams (Position, Normal, Color, UV, UV2, AnimBlend). Soft particles and a camera near-fade are built in.
Shader "Fire3D/SmokeSixWay"
{
    Properties
    {
        _LightmapA ("Lightmap A: R right, G top, B back, A transparency", 2D) = "white" {}
        _LightmapB ("Lightmap B: R left, G bottom, B front", 2D) = "white" {}
        _BaseColor ("Smoke Tint (rgb) and Opacity (a)", Color) = (1, 1, 1, 1)
        _Ambient ("Ambient Strength", Range(0, 2)) = 0.9
        _LightGain ("Light Gain", Range(0, 4)) = 1.6
        _SoftDistance ("Soft Particle Distance", Float) = 2.0
        _OccludeFade ("Fade when behind geometry (m)", Float) = 0.9
        _CameraFade ("Camera Fade Near, Far", Vector) = (0.3, 1.6, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

        Pass
        {
            Name "SmokeSixWay"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _ADDITIONAL_LIGHTS _ADDITIONAL_LIGHTS_VERTEX
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float4 uv : TEXCOORD0;      // xy = this frame, zw = next frame
                float blend : TEXCOORD1;    // flipbook blend factor
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float4 uv : TEXCOORD0;
                float blend : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float4 screen : TEXCOORD3;
                half fog : TEXCOORD4;
            };

            TEXTURE2D(_LightmapA);
            SAMPLER(sampler_LightmapA);
            TEXTURE2D(_LightmapB);
            SAMPLER(sampler_LightmapB);

            CBUFFER_START(UnityPerMaterial)
                float4 _LightmapA_ST;
                half4 _BaseColor;
                half _Ambient;
                half _LightGain;
                float _SoftDistance;
                float _OccludeFade;
                float4 _CameraFade;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.screen = ComputeScreenPos(p.positionCS);
                o.color = input.color;
                o.uv = input.uv;
                o.blend = input.blend;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            // Light value for a light shining from direction L (world space, towards the light).
            half SixWay(float3 lightDirWS, half4 a, half4 b)
            {
                float3 l = normalize(mul((float3x3)UNITY_MATRIX_V, lightDirWS));   // view space: +x right, +y up, +z towards the camera
                float3 w = l * l;
                half x = l.x > 0 ? a.r : b.r;   // right : left
                half y = l.y > 0 ? a.g : b.g;   // top   : bottom
                half z = l.z > 0 ? b.b : a.b;   // front (camera side) : back
                return w.x * x + w.y * y + w.z * z;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 a0 = SAMPLE_TEXTURE2D(_LightmapA, sampler_LightmapA, i.uv.xy);
                half4 b0 = SAMPLE_TEXTURE2D(_LightmapB, sampler_LightmapB, i.uv.xy);
                half4 a1 = SAMPLE_TEXTURE2D(_LightmapA, sampler_LightmapA, i.uv.zw);
                half4 b1 = SAMPLE_TEXTURE2D(_LightmapB, sampler_LightmapB, i.uv.zw);
                half4 a = lerp(a0, a1, i.blend);
                half4 b = lerp(b0, b1, i.blend);

                // Ambient: sky/probe light from above and a floor bounce, through the same six-way lookup.
                half3 ambient = SampleSH(float3(0, 1, 0));
                half3 lit = ambient * _Ambient * (0.5 * SixWay(float3(0, 1, 0), a, b) + 0.5 * SixWay(float3(0, -1, 0), a, b));

                Light mainLight = GetMainLight();
                lit += mainLight.color * mainLight.distanceAttenuation * SixWay(mainLight.direction, a, b) * _LightGain;

                #if defined(_ADDITIONAL_LIGHTS)
                uint count = GetAdditionalLightsCount();
                for (uint li = 0u; li < count; ++li)
                {
                    Light l = GetAdditionalLight(li, i.positionWS);
                    lit += l.color * l.distanceAttenuation * SixWay(l.direction, a, b) * _LightGain;
                }
                #endif

                half alpha = a.a * i.color.a * _BaseColor.a;

                // Soft particles: fade where the sprite meets geometry behind it; camera near-fade avoids a wall of grey.
                float2 suv = i.screen.xy / i.screen.w;
                float sceneEye = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
                float fragEye = i.screen.w;
                // Depth test is off (ZTest Always): instead of the wall slicing the puff with a hard edge, the part of
                // the puff behind a surface dissolves over _OccludeFade metres, and the part in front of a surface
                // fades in over _SoftDistance. Both smoothstepped so a wall crossed at a grazing angle leaves no line.
                float depthDelta = sceneEye - fragEye; // > 0: puff in front of the wall; < 0: behind it
                float soft = saturate((depthDelta + _OccludeFade) / max(_SoftDistance + _OccludeFade, 0.01));
                alpha *= soft * soft * (3.0 - 2.0 * soft);
                float nearFade = saturate((fragEye - _CameraFade.x) / max(_CameraFade.y - _CameraFade.x, 0.01));
                alpha *= nearFade * nearFade * (3.0 - 2.0 * nearFade);

                half3 rgb = lit * i.color.rgb * _BaseColor.rgb;
                rgb = MixFog(rgb, i.fog);
                return half4(rgb, alpha);
            }
            ENDHLSL
        }
    }
}
