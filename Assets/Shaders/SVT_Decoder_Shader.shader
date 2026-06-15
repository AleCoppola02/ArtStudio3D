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

            sampler2D _IndirectionTable; // The indirection table texture that encodes which slot in the physical atlas to sample for each virtual slot
            sampler2D _PhysicalAtlas; // The physical atlas texture that contains the actual texture data for all slots
            float4 _TableSize; // The size of the virtual texture in terms of how many slots it has (e.g., 8x8)
            float4 _TableResolution; // The actual pixel resolution of the indirection table texture (e.g. 32x32)
            float _AtlasSlotsAcross; // The number of slots across the physical atlas texture (e.g ., if the atlas is 256x256 and each slot is 16x16, then this would be 16)

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv; 
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                //Map the fractional Quad perfectly to the whole-number Array
                //tableUV represents the UV coordinates used to sample the indirection table, which is derived from the input UVs of the quad and scaled by the virtual table size and resolution
                float2 tableUV = (i.uv * _TableSize.xy) / _TableResolution.xy; 
                float4 tableData = tex2D(_IndirectionTable, tableUV);
                if (tableData.a < 0.5) {
                    return float4(1, 1, 1, 1); 
                }
                //Decode the slot index from the indirection table's RG channels
                //slot means the position in the physical atlas where the actual texture data is stored
                //Assuming the indirection table encodes slot indices in the range [0, 255] for both R and G
                float slotX = round(tableData.r * 255.0); 
                float slotY = round(tableData.g * 255.0);
                //i is the UV coordinate of the quad, multiplied by the table size to get the local UV within the slot
                float2 localUV = frac(i.uv * _TableSize.xy); //frac gives the fractional part, which represents the local UV within the slot
                //Calculate the UVs for the physical atlas
                //The atlas UVs are calculated by taking the slot position and adding the local UV, then dividing by the total number of slots across the atlas
                float atlasU = (slotX + localUV.x) / _AtlasSlotsAcross;
                float atlasV = (slotY + localUV.y) / _AtlasSlotsAcross;
                //Sample the physical atlas using the calculated UVs
                float4 atlasColor = tex2D(_PhysicalAtlas, float2(atlasU, atlasV));
                float3 finalColor = float3(1.0, 1.0, 1.0) * (1.0 - atlasColor.a) + atlasColor.rgb;

                //this is executed for every pixel in the quad to reconstruct the final image based on the indirection table and physical atlas
                return float4(finalColor, 1.0);
            }
            ENDCG
        }
    }
}