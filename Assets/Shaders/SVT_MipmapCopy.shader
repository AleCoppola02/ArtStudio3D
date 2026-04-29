Shader "Hidden/SVT_MipmapCopy"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        // No culling or depth reading, just raw 2D pixel copying
        Cull Off ZWrite Off ZTest Always
        
        // "One Zero" means: Final Pixel = (Source * 1) + (Destination * 0)
        // This guarantees an exact 1:1 copy of both Color and Alpha!
        Blend One Zero 

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f {
                float4 vertex : SV_POSITION;
                float2 texcoord : TEXCOORD0;
            };

            sampler2D _MainTex;

            v2f vert (appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.texcoord);
            }
            ENDCG
        }
    }
}