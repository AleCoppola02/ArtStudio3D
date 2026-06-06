Shader "Painting/ProceduralBrush"
{
    Properties
    {
        _MainTex ("Brush Shape", 2D) = "white" {}
        _Color ("Brush Color", Color) = (0,0,0,1)
        _Flow ("Flow", Range(0,1)) = 0.1
        _Softness ("Edge Softness", Range(0.001, 1.0)) = 0.1
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlendColor ("Source Blend Color", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlendColor ("Destination Blend Color", Float) = 10  
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlendAlpha ("Source Blend Alpha", Float) = 1 
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlendAlpha ("Destination Blend Alpha", Float) = 10 
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        ZWrite Off
        ZTest Always
        Cull Off

        Blend [_SrcBlendColor] [_DstBlendColor], [_SrcBlendAlpha] [_DstBlendAlpha]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            float4 _Color;
            float _Flow;
            float _Softness;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4  frag (v2f i) : SV_Target
            {
                float2 center = float2(0.5, 0.5);
                float dist = distance(i.uv, center);
                float radius = 0.5;


                float edgeBlur = max(0.01, _Softness);
                float shape = smoothstep(radius, radius - edgeBlur, dist);

                // Multiply by _Color.a to respect transparent color choices
                float alpha = _Flow * shape; 

                return float4(_Color.rgb, alpha);
            }
            ENDCG
        }
    }
}