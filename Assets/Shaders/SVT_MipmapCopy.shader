Shader "Hidden/SVT_MipmapCopy"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off 
        ZWrite Off 
        ZTest Always
        
        // Exact 1:1 overwrite
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
            
            // Unity automatically populates this variable!
            // x = 1/width, y = 1/height
            float4 _MainTex_TexelSize; 

            v2f vert (appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Find the exact distance to move half-a-pixel diagonally
                float2 halfTexel = _MainTex_TexelSize.xy * 0.5;
                
                // Sample the exact centers of the 4 corresponding high-res pixels
                float4 c1 = tex2D(_MainTex, i.texcoord + float2(-halfTexel.x, -halfTexel.y)); // Bottom-Left
                float4 c2 = tex2D(_MainTex, i.texcoord + float2( halfTexel.x, -halfTexel.y)); // Bottom-Right
                float4 c3 = tex2D(_MainTex, i.texcoord + float2(-halfTexel.x,  halfTexel.y)); // Top-Left
                float4 c4 = tex2D(_MainTex, i.texcoord + float2( halfTexel.x,  halfTexel.y)); // Top-Right
                
                // Add them together and divide by 4 for a mathematically perfect average!
                return (c1 + c2 + c3 + c4) * 0.25;
            }
            ENDCG
        }
    }
}