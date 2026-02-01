// Assets/Shaders/OESBackground.shader
Shader "Hidden/DJI/OESBackground"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} }
    SubShader {
        Tags { "Queue"="Background" "RenderType"="Opaque" }
        Pass {
            ZTest Always Cull Off ZWrite Off
            GLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #extension GL_OES_EGL_image_external : require
            #ifdef SHADER_API_GLES3
            #define TEXTURE samplerExternalOES
            #else
            #define TEXTURE samplerExternalOES
            #endif

            uniform mat4 unity_MatrixVP;
            attribute vec4 position;
            attribute vec2 texcoord;
            varying vec2 vUV;

            TEXTURE _MainTex;

            void vert() {
                gl_Position = vec4(position.xy, 0.0, 1.0);
                vUV = texcoord;
            }

            void frag() {
                vec4 c = texture2D(_MainTex, vUV);
                gl_FragColor = c;
            }
            ENDGLSL
        }
    }
}
