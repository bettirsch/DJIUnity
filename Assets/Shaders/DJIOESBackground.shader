Shader "DJI/OESBackgroundURP"
{
    Properties
    {
        _MainTex ("External", 2D) = "white" {}
        _FlipY ("Flip Y", Float) = 1
    }

    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        // --- PASS 0: Android GLES3 external OES ---
        Pass
        {
            Name "GLES3_OES"
            Tags { "LightMode"="SRPDefaultUnlit" }

            Cull Off
            ZWrite Off
            ZTest Always

            GLSLPROGRAM
            #pragma only_renderers gles3
            #include "UnityCG.glslinc"

            #ifdef SHADER_API_GLES3
            #extension GL_OES_EGL_image_external_essl3 : require
            #endif

            uniform float _FlipY;
            uniform mat4 _TexTransform;

            #ifdef VERTEX
            varying vec2 textureCoord;

            void main()
            {
                // Fullscreen triangle using gl_VertexID (0,1,2)
                int id = gl_VertexID;
                vec2 uv = vec2((id << 1) & 2, (id & 2));

                // Position in clip space (oversized triangle)
                gl_Position = vec4(uv * vec2(2.0, -2.0) + vec2(-1.0, 1.0), 0.0, 1.0);

                // UVs: keep 0..2 at verts; inside screen it interpolates to 0..1
                vec2 sampleUv = uv;
                if (_FlipY > 0.5) sampleUv.y = 1.0 - sampleUv.y;
                textureCoord = (_TexTransform * vec4(sampleUv, 0.0, 1.0)).xy;
            }
            #endif

            #ifdef FRAGMENT
            varying vec2 textureCoord;
            uniform samplerExternalOES _MainTex;

            out vec4 outColor;

            void main()
            {
                vec3 col = texture(_MainTex, textureCoord).xyz;
                outColor = vec4(col, 1.0);
            }
            #endif

            ENDGLSL
        }


        // --- PASS 1: Editor / non-GLES fallback (so you don't see magenta in Scene/Game view) ---
        Pass
        {
            Name "Fallback"
            Tags { "LightMode"="SRPDefaultUnlit" }

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma exclude_renderers gles3
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float _FlipY;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
            };

            Varyings Vert(Attributes a)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(a.positionOS.xyz);
                o.uv = a.uv;
                if (_FlipY > 0.5) o.uv.y = 1.0 - o.uv.y;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
