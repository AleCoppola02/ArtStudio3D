Shader "Unlit/InkLayer"
{
    Properties
    {
        _MainTex ("Brush Shape", 2D) = "white" {}
        _Color ("Brush Color", Color) = (0,0,0,1)
        _Opacity ("Opacity", Range(0,1)) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        

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
    
                float3 pencilRGB = lerp(float3(1, 1, 1), _Color.rgb, mask);

                return float4(pencilRGB, 1.0);
            }
            ENDCG
        }
    }
}