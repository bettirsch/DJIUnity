Shader "DJI/Board Projected Shadow"
{
    Properties { _ShadowMask("Cached projection mask", 2D) = "black" {} }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent-10" "RenderType"="Transparent" }
        Pass
        {
            Name "BoardShadow"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_ShadowMask); SAMPLER(sampler_ShadowMask);
            struct Attributes { float3 positionOS:POSITION; float2 uv:TEXCOORD0; };
            struct Varyings { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; };
            Varyings Vert(Attributes input) { Varyings o; o.positionCS=TransformObjectToHClip(input.positionOS); o.uv=input.uv; return o; }
            half4 Frag(Varyings input):SV_Target { half alpha=SAMPLE_TEXTURE2D(_ShadowMask,sampler_ShadowMask,input.uv).r; clip(alpha-.001h); return half4(0,0,0,alpha); }
            ENDHLSL
        }
    }
}
