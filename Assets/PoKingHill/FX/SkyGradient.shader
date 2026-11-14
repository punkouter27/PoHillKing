// Procedural URP skybox for PoKingHill (#5): zenith/horizon/nadir gradient, sun halo, twilight stars and storm clouds.
Shader "PoKingHill/SkyGradient"
{
    Properties
    {
        _SkyTop ("Sky Zenith", Color) = (0.14, 0.34, 0.68, 1)
        _SkyHorizon ("Sky Horizon", Color) = (0.58, 0.74, 0.88, 1)
        _SkyBottom ("Sky Nadir", Color) = (0.10, 0.16, 0.24, 1)
        _SunColor ("Sun Color", Color) = (1.0, 0.92, 0.75, 1)
        _SunDir ("Sun Direction", Vector) = (0.3, 0.65, -0.7, 0)
        _StarStrength ("Stars", Range(0, 1)) = 0
        _StormMix ("Storm Mix", Range(0, 1)) = 0
        _CloudDensity ("Cloud Density", Range(0, 1)) = 0.25
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _SkyTop, _SkyHorizon, _SkyBottom, _SunColor;
                float4 _SunDir;
                float _StarStrength, _StormMix, _CloudDensity;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 dirWS : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.dirWS = v.positionOS.xyz;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float noise2(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                float a = hash21(i), b = hash21(i + float2(1, 0));
                float c = hash21(i + float2(0, 1)), d = hash21(i + float2(1, 1));
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 d = normalize(i.dirWS);
                float up = d.y;
                half3 sky = up >= 0.0
                    ? lerp(_SkyHorizon.rgb, _SkyTop.rgb, pow(saturate(up), 0.55))
                    : lerp(_SkyHorizon.rgb, _SkyBottom.rgb, pow(saturate(-up), 0.65));

                float3 sunDir = normalize(_SunDir.xyz);
                float sdot = max(0.0, dot(d, sunDir));
                float sunMask = (1.0 - _StormMix * 0.85);
                sky += _SunColor.rgb * (pow(sdot, 420.0) * 1.4 + pow(sdot, 14.0) * 0.28) * sunMask;

                if (up > 0.02 && _StarStrength > 0.01)
                {
                    float2 starUv = d.xz / (up + 0.35) * 42.0;
                    float h = hash21(floor(starUv));
                    float star = step(0.992, h) * smoothstep(0.05, 0.35, up);
                    sky += half3(0.9, 0.94, 1.0) * (star * _StarStrength * (1.0 - _StormMix));
                }

                if (up > 0.0 && _CloudDensity > 0.01)
                {
                    float2 cuv = d.xz / (up + 0.25) * 1.6 + float2(_Time.y * 0.015, _Time.y * 0.008);
                    float c = noise2(cuv) * 0.65 + noise2(cuv * 2.3) * 0.35;
                    float mask = smoothstep(0.45 - _StormMix * 0.25, 0.85, c) * smoothstep(0.0, 0.2, up) * _CloudDensity;
                    half3 cloudCol = lerp(lerp(_SkyHorizon.rgb, half3(1, 1, 1), 0.35), half3(0.20, 0.23, 0.29), _StormMix);
                    sky = lerp(sky, cloudCol, saturate(mask));
                }

                return half4(sky, 1.0);
            }
            ENDHLSL
        }
    }
}
