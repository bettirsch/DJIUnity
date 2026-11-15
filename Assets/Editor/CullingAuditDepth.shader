Shader "Hidden/DJI/CullingAuditDepth"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D_FLOAT(_AuditDepth);
            float4 _AuditDepthSize;
            struct Varyings { float4 positionCS:SV_POSITION;float2 uv:TEXCOORD0; };
            Varyings Vert(uint id:SV_VertexID){Varyings o;o.positionCS=GetFullScreenTriangleVertexPosition(id);o.uv=GetFullScreenTriangleTexCoord(id);return o;}
            float4 Frag(Varyings i):SV_Target{float d=LOAD_TEXTURE2D(_AuditDepth,(uint2)min(i.uv*_AuditDepthSize.xy,_AuditDepthSize.xy-1)).r;return float4(d,d,d,1);}
            ENDHLSL
        }
    }
}
