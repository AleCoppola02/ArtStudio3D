Shader "Unlit/SimpleBrush"
{
    Properties
    {
        _MainTex ("Brush Shape", 2D) = "white" {}
        _Color ("Brush Color", Color) = (0,0,0,1)
        _Opacity ("Opacity", Range(0,1)) = 0.5
    }
    SubShader
    {
        // Safe transparent rendering tags
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        
        // Ignores the scene depth entirely
        ZWrite Off
        ZTest Always
        Cull Off
        
        // Change your Blend settings to these for the Ink Layer
        BlendOp Min
        Blend One One
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
            float _Opacity;

            v2f vert (appdata v)
            {
                v2f o;
                // GL.LoadOrtho() makes this perfectly map to the RenderTexture
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float mask = tex2D(_MainTex, i.uv).r;


                float finalStrength = mask * _Opacity;


                float3 finalRGB = lerp(float3(1, 1, 1), _Color.rgb, finalStrength);

                return float4(finalRGB, 1.0);
            }
            ENDCG
        }
    }
}