Shader "Painting/SVT_Decoder"
{
    Properties
    {
        _IndirectionTable ("Indirection Table (GPU Map)", 2D) = "black" {}
        _PhysicalAtlas ("Physical Atlas (VRAM)", 2D) = "white" {}
        
        _TableSize ("Virtual Table Size", Vector) = (8, 8, 0, 0)
        _TableResolution ("Actual Table Pixels", Vector) = (8, 8, 0, 0)
        _AtlasSlotsAcross ("Atlas Slots Across", Float) = 16.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

            sampler2D _IndirectionTable;
            sampler2D _PhysicalAtlas;
            float4 _TableSize;
            float4 _TableResolution;
            float _AtlasSlotsAcross;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv; 
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                // CRITICAL FIX: Map the fractional Quad perfectly to the whole-number Array!
                float2 tableUV = (i.uv * _TableSize.xy) / _TableResolution.xy;

                float4 tableData = tex2D(_IndirectionTable, tableUV);

                if (tableData.a < 0.5) {
                    return float4(1, 1, 1, 1); 
                }

                float slotX = round(tableData.r * 255.0);
                float slotY = round(tableData.g * 255.0);

                float2 localUV = frac(i.uv * _TableSize.xy);

                float atlasU = (slotX + localUV.x) / _AtlasSlotsAcross;
                float atlasV = (slotY + localUV.y) / _AtlasSlotsAcross;

                float4 atlasColor = tex2D(_PhysicalAtlas, float2(atlasU, atlasV));
                float3 finalColor = float3(1.0, 1.0, 1.0) * (1.0 - atlasColor.a) + atlasColor.rgb;

                return float4(finalColor, 1.0);
            }
            ENDCG
        }
    }
}