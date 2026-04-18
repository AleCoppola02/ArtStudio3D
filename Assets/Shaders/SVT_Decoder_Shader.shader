Shader "Painting/SVT_Decoder"
{
    Properties
    {
        _IndirectionTable ("Indirection Table (GPU Map)", 2D) = "black" {}
        _PhysicalAtlas ("Physical Atlas (VRAM)", 2D) = "white" {}
        
        _TableSize ("Table Size (Width, Height)", Vector) = (8, 8, 0, 0)
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

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _IndirectionTable;
            sampler2D _PhysicalAtlas;
            float4 _TableSize;
            float _AtlasSlotsAcross;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv; // 0.0 to 1.0 across the ENTIRE mathematical canvas
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                // 1. Ask the Valet: Is this tile loaded? And if so, where is it?
                // We sample the tiny Indirection Table using our Canvas UV.
                float4 tableData = tex2D(_IndirectionTable, i.uv);

                // In C#, we set Alpha to 255 if the tile was mapped. 
                // In the shader, 255 becomes 1.0. If it's 0, there is no ink here!
                if (tableData.a < 0.5) {
                    return float4(1, 1, 1, 1); // Return plain white paper
                }

                // 2. Decode the parking spot coordinates
                // We stored the Slot X and Y in the Red and Green channels out of 255.
                float slotX = round(tableData.r * 255.0);
                float slotY = round(tableData.g * 255.0);

                // 3. Find out exactly where this pixel is INSIDE the tile
                // Multiplying UV by the table size (e.g., 8) gives us coordinates like (4.3, 2.7).
                // frac() throws away the whole numbers, leaving just (0.3, 0.7), 
                // which is exactly where we are inside that specific tile!
                float2 localUV = frac(i.uv * _TableSize.xy);

                // 4. Map this local position into the massive Physical Atlas
                float atlasU = (slotX + localUV.x) / _AtlasSlotsAcross;
                float atlasV = (slotY + localUV.y) / _AtlasSlotsAcross;

                // 5. Grab the actual ink from VRAM!
                return tex2D(_PhysicalAtlas, float2(atlasU, atlasV));
            }
            ENDCG
        }
    }
}