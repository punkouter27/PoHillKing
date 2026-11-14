// Fighter surface for PoKingHill (BodyLook): URP physically based lighting (main light with shadows, additional lights,
// Forward+ cluster loop, fog) plus four match-driven layers, all set per renderer through a MaterialPropertyBlock:
//   _RimColor   fresnel rim in the seat colour (alpha = strength); BodyLook turns it red while the fighter is at the edge
//   _Wear       0..1 scratches and grime that build up from the contacts a body part took this match (object space,
//               so they stay on the part as it moves)
//   _Wet        0..1 darker, glossier surface after the sea; dries over about 20 s
//   _Flash      0..1 a short hot glow on a part that was just hit
// _BaseMap / _BumpMap are optional (the scanned humans have them, the G1 parts are plain colours).
// Life layer (LifeSigns, the humans' skins only; all zero = off). The meshes have no face, hair or cloth bones, so
// these are vertex offsets in world space, the same in every pass:
//   _LifeChest / _LifeBreath   the rib cage swells along the normal with each breath
//   _LifeJigC / _LifeJigD      three soft regions (belly, hair, chest) lag behind the body on springs
//   _LifeHead, _LifeMouth, _LifeFace   the jaw drops, the brow moves, the open mouth is shaded dark, the face flushes
Shader "PoKingHill/Fighter"
{
    Properties
    {
        _BaseColor ("Base colour", Color) = (0.5, 0.5, 0.5, 1)
        _BaseMap ("Base map", 2D) = "white" {}
        [Normal] _BumpMap ("Normal map", 2D) = "bump" {}
        _BumpScale ("Normal scale", Float) = 1
        _Metallic ("Metallic", Range(0, 1)) = 0.5
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5
        [HDR] _EmissionColor ("Emission", Color) = (0, 0, 0, 1)
        _RimColor ("Rim colour (a = strength)", Color) = (1, 1, 1, 0)
        _RimPower ("Rim power", Range(0.5, 8)) = 3
        _Wear ("Wear", Range(0, 1)) = 0
        _Wet ("Wet", Range(0, 1)) = 0
        _Flash ("Hit flash", Range(0, 1)) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor, _EmissionColor, _RimColor;
            half _BumpScale, _Metallic, _Smoothness, _RimPower, _Wear, _Wet, _Flash, _Cull;
            float4x4 _LifeHead;          // world -> head frame (x forward, y up, z left), metres
            float4 _LifeHeadUp;          // the head frame's up in world space
            float4 _LifeMouth;           // xyz: the mouth in the head frame; w: head height (chin to crown, m), 0 = no face
            float4 _LifeFace;            // x: jaw open 0..1, y: brow -1 (furrowed) .. 1 (raised), z: flush 0..1
            float4 _LifeChest;           // xyz: world centre of the rib cage, w: radius (0 = off)
            float4 _LifeBreath;          // xyz: the chest's forward in world space, w: swell (m)
            float4 _LifeJigC[3];         // xyz: world centre, w: radius (0 = off)
            float4 _LifeJigD[3];         // xyz: world offset at the centre
        CBUFFER_END

        // Position in the head frame relative to the mouth, in head heights.
        float3 LifeFaceCoord(float3 pWS) { return (mul(_LifeHead, float4(pWS, 1.0)).xyz - _LifeMouth.xyz) / max(_LifeMouth.w, 1e-4); }

        float3 LifeDisplace(float3 p, float3 n)
        {
            if (_LifeChest.w > 0.0)
            {
                float3 d = p - _LifeChest.xyz; float w = saturate(1.0 - dot(d, d) / (_LifeChest.w * _LifeChest.w));
                p += n * (_LifeBreath.w * w * w * (0.35 + 0.65 * saturate(dot(n, _LifeBreath.xyz))));
            }
            [unroll] for (int i = 0; i < 3; i++)
                if (_LifeJigC[i].w > 0.0)
                {
                    float3 d = p - _LifeJigC[i].xyz; float w = saturate(1.0 - dot(d, d) / (_LifeJigC[i].w * _LifeJigC[i].w));
                    p += _LifeJigD[i].xyz * (w * w);
                }
            if (_LifeMouth.w > 0.0)
            {
                float3 m = LifeFaceCoord(p); float front = smoothstep(-0.45, -0.12, m.x);
                // jaw: everything below the mouth line near the chin drops
                float3 c = m - float3(-0.06, -0.14, 0.0);
                float jaw = smoothstep(0.02, -0.05, m.y) * saturate(1.0 - dot(c, c) / (0.30 * 0.30)) * front;
                // brow: a band above the eyes
                float3 b = (m - float3(-0.02, 0.37, 0.0)) * float3(1.0, 2.2, 0.75);
                float brow = saturate(1.0 - dot(b, b) / (0.26 * 0.26)) * front;
                p += _LifeHeadUp.xyz * (_LifeMouth.w * (-0.085 * _LifeFace.x * jaw + 0.012 * _LifeFace.y * brow));
            }
            return p;
        }
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            // multi_compile, not shader_feature: BodyLook enables it on materials made at runtime (a build strips unused features)
            #pragma multi_compile_local _ _NORMALMAP
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 tangentOS : TANGENT; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float4 tangentWS : TEXCOORD2;
                float2 uv : TEXCOORD3; float3 positionOS : TEXCOORD4; half fog : TEXCOORD5; float3 face : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v); UNITY_TRANSFER_INSTANCE_ID(v, o);
                VertexNormalInputs n = GetVertexNormalInputs(v.normalOS, v.tangentOS);
                float3 rest = TransformObjectToWorld(v.positionOS.xyz);
                o.face = _LifeMouth.w > 0.0 ? LifeFaceCoord(rest) : float3(0, 9, 0);     // before the jaw moves: the mouth stays on the lips
                VertexPositionInputs p = GetVertexPositionInputs(TransformWorldToObject(LifeDisplace(rest, n.normalWS)));
                o.positionCS = p.positionCS; o.positionWS = p.positionWS; o.normalWS = n.normalWS;
                o.tangentWS = float4(n.tangentWS, v.tangentOS.w * GetOddNegativeScale());
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap); o.positionOS = v.positionOS.xyz; o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            float Hash(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }

            // Short straight scratches in three directions, more of them as wear grows; plus a soft grime mask.
            void Scratches(float3 p, half wear, out half scratch, out half grime)
            {
                scratch = 0;
                const float3 dirs[3] = { float3(0.80, 0.55, 0.24), float3(-0.30, 0.90, -0.31), float3(0.45, -0.20, 0.87) };
                [unroll] for (int i = 0; i < 3; i++)
                {
                    float3 a = dirs[i], b = normalize(cross(a, float3(0.31, 0.47, 0.83)));
                    float across = dot(p, b) * 110.0, along = dot(p, a) * 14.0;
                    float cell = Hash(float2(floor(across), floor(along) + i * 17.0));
                    float stroke = 1.0 - saturate(abs(frac(across) - 0.5) * 6.0);
                    // fade lines out once they get thinner than a pixel (far away), or they alias into glitter
                    float aa = saturate(1.5 - fwidth(across) * 3.0);
                    scratch = max(scratch, stroke * aa * step(cell, wear * 0.3));
                }
                grime = wear * (0.6 + 0.4 * sin(dot(p, float3(23.1, 17.3, 29.7))) * sin(dot(p, float3(-11.0, 31.0, 7.0))));
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                half3 albedo = tex.rgb * _BaseColor.rgb;
                half metallic = _Metallic, smooth = _Smoothness;
                float3 n = normalize(i.normalWS);
                #if defined(_NORMALMAP)
                    half3 nTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, i.uv), _BumpScale);
                    float3 bitan = i.tangentWS.w * cross(n, i.tangentWS.xyz);
                    n = normalize(TransformTangentToWorld(nTS, half3x3(i.tangentWS.xyz, bitan, n)));
                #endif

                half scratch = 0, grime = 0, mouthDark = 0;
                if (_Wear > 0.001) Scratches(i.positionOS, _Wear, scratch, grime);
                albedo = lerp(albedo, albedo * 0.72, grime * 0.6);                    // grime darkens
                albedo = lerp(albedo, half3(0.62, 0.62, 0.64), scratch * 0.85);        // bare metal shows through
                metallic = lerp(metallic, 0.9, scratch); smooth = lerp(smooth * (1 - 0.35 * grime), 0.35, scratch);
                albedo *= lerp(1.0, 0.62, _Wet); smooth = lerp(smooth, 0.93, _Wet);   // wet: darker and glossy
                if (_LifeMouth.w > 0.0)
                {
                    float3 f = i.face; half front = smoothstep(-0.30, -0.08, f.x);
                    // exertion: the face flushes and shines
                    half skin = saturate(1.0 - length(f - float3(-0.12, 0.16, 0.0)) / 0.55) * front * _LifeFace.z;
                    albedo = lerp(albedo, albedo * half3(1.22, 0.80, 0.78), skin * 0.7); smooth = lerp(smooth, max(smooth, 0.7), skin * 0.6);
                    // the open mouth: a dark ellipse hanging from the lip line (the upper lip stays), growing with the jaw
                    half tall = 0.004 + 0.030 * _LifeFace.x;
                    half2 e = half2(f.z / (0.060 + 0.025 * _LifeFace.x), (f.y + 0.004 + tall) / tall);
                    mouthDark = smoothstep(1.0, 0.6, dot(e, e)) * front * saturate(_LifeFace.x * 4.0);
                    albedo = lerp(albedo, half3(0.05, 0.015, 0.015), mouthDark * 0.95); smooth = lerp(smooth, 0.1, mouthDark); metallic *= 1.0 - mouthDark;
                }

                InputData d = (InputData)0;
                d.positionWS = i.positionWS; d.positionCS = i.positionCS; d.normalWS = n;
                d.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                d.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                d.fogCoord = i.fog; d.bakedGI = SampleSH(n);
                d.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                d.shadowMask = half4(1, 1, 1, 1);

                SurfaceData s = (SurfaceData)0;
                s.albedo = albedo; s.metallic = metallic; s.smoothness = smooth; s.occlusion = 1; s.alpha = 1; s.specular = 0;
                half rim = pow(1.0 - saturate(dot(n, d.viewDirectionWS)), _RimPower) * _RimColor.a * (1.0 - mouthDark);
                s.emission = _EmissionColor.rgb + _RimColor.rgb * rim + half3(1.0, 0.62, 0.28) * (_Flash * _Flash * 4.0);
                half4 c = UniversalFragmentPBR(d, s);
                c.rgb *= 1.0 - 0.8 * mouthDark;                                     // no light gets in there
                c.rgb = MixFog(c.rgb, i.fog);
                return half4(c.rgb, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull [_Cull]
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection, _LightPosition;
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            float4 vert(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                float3 n = TransformObjectToWorldNormal(v.normalOS), p = LifeDisplace(TransformObjectToWorld(v.positionOS.xyz), n);
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 l = normalize(_LightPosition - p);
                #else
                    float3 l = _LightDirection;
                #endif
                float4 c = TransformWorldToHClip(ApplyShadowBias(p, n, l));
                #if UNITY_REVERSED_Z
                    c.z = min(c.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    c.z = max(c.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return c;
            }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            float4 vert(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                return TransformWorldToHClip(LifeDisplace(TransformObjectToWorld(v.positionOS.xyz), TransformObjectToWorldNormal(v.normalOS)));
            }
            half frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };
            Varyings vert(Attributes v) { UNITY_SETUP_INSTANCE_ID(v); Varyings o; o.normalWS = TransformObjectToWorldNormal(v.normalOS); o.positionCS = TransformWorldToHClip(LifeDisplace(TransformObjectToWorld(v.positionOS.xyz), o.normalWS)); return o; }
            half4 frag(Varyings i) : SV_Target { return half4(NormalizeNormalPerPixel(i.normalWS), 0); }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
