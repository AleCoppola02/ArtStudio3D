using System.Collections.Generic;
using UnityEngine;

public class CanvasManager : MonoBehaviour
{
    [Header("SVT Settings")]
    public int tileSize = 256;
    public float worldUnitsPerTile = 1f;

    [Header("SVT Rendering")]
    public Material svtCanvasMaterial; // <--- //drag in inspector

    public BrushManager brushManager; //drag in inspector
    public InkLayerManager inkLayerManager; //drag in inspector

    // The SVT "Mathematical Canvas" bounds (e.g., 2048x2048)
    public int canvasWidthInTiles = 8;
    public int canvasHeightInTiles = 8;

    // The Engine Components
    public PhysicalAtlas atlas;
    public IndirectionTable[] tables;
    public BackingStore backingStore;

    [Header("Debug View")]
    public RenderTexture debugAtlasRT;

    private RenderTexture scratchpadRT; //The 256x256 localized inklayer
    // We can keep this for compatibility if needed elsewhere temporarily
    public RenderTexture canvasRT;
    private Color canvasColor = new Vector4(1, 1, 1, 1);

    private void Start() {
        // 1. Create the Atlas
        atlas = new PhysicalAtlas(4096, tileSize);
        debugAtlasRT = atlas.Texture;

        RenderTexture.active = atlas.Texture;
        GL.Clear(true, true, Color.white);
        RenderTexture.active = null;

        // Create the scratchpad 
        scratchpadRT = new RenderTexture(tileSize, tileSize, 0, RenderTextureFormat.ARGB32);
        scratchpadRT.filterMode = FilterMode.Point; 
        scratchpadRT.Create();


        // 2. Create tables and CPU manager
        tables = new IndirectionTable[1];
        tables[0] = new IndirectionTable(0, canvasWidthInTiles, canvasHeightInTiles);
        backingStore = new BackingStore(atlas, tables);

        // Link VRAM resources to the shader
        if (svtCanvasMaterial != null) {
            // Give the shader the maps
            svtCanvasMaterial.SetTexture("_PhysicalAtlas", atlas.Texture);
            svtCanvasMaterial.SetTexture("_IndirectionTable", tables[0].TableTexture);

            // Tell the shader the dimensions
            svtCanvasMaterial.SetVector("_TableSize", new Vector4(canvasWidthInTiles, canvasHeightInTiles, 0, 0));
            svtCanvasMaterial.SetFloat("_AtlasSlotsAcross", atlas.AtlasSize / (float)tileSize);
        }
        // ------------------------------------

        ClearCanvas();
    }

    public RenderTexture GetCanvasRT() {
        return canvasRT;
    }

    public void ClearCanvas() {
        if (canvasRT != null) {
            RenderTexture.active = canvasRT;
            GL.Clear(true, true, canvasColor);
            RenderTexture.active = null;
        }
    }

    public void BakeStroke(List<Vector2> stampBuffer) {
        if (stampBuffer.Count == 0) return;

        float canvasWorldWidth = canvasWidthInTiles * worldUnitsPerTile;
        float canvasWorldHeight = canvasHeightInTiles * worldUnitsPerTile;
        float brushRadius = brushManager.brushSize / 2f;

        // 1. FIND ALL TOUCHED TILES
        HashSet<Vector2Int> touchedTiles = new HashSet<Vector2Int>();
        foreach (Vector2 point in stampBuffer) {
            float shiftedX = point.x + (canvasWorldWidth / 2f);
            float shiftedY = point.y + (canvasWorldHeight / 2f);

            int minX = Mathf.FloorToInt((shiftedX - brushRadius) / worldUnitsPerTile);
            int maxX = Mathf.FloorToInt((shiftedX + brushRadius) / worldUnitsPerTile);
            int minY = Mathf.FloorToInt((shiftedY - brushRadius) / worldUnitsPerTile);
            int maxY = Mathf.FloorToInt((shiftedY + brushRadius) / worldUnitsPerTile);

            for (int x = minX; x <= maxX; x++) {
                for (int y = minY; y <= maxY; y++) {
                    touchedTiles.Add(new Vector2Int(x, y));
                }
            }
        }

        // 2. ALLOCATE VRAM 
        foreach (Vector2Int tile in touchedTiles) {
            if (tile.x >= 0 && tile.x < canvasWidthInTiles && tile.y >= 0 && tile.y < canvasHeightInTiles) {
                backingStore.RequestTile(tile.x, tile.y, 0);
            }
        }
        backingStore.SyncGPU();

        // 3. RENDER VIA SCRATCHPAD
        foreach (Vector2Int tile in touchedTiles) {
            // Skip tiles outside our mathematical canvas
            if (tile.x < 0 || tile.x >= canvasWidthInTiles || tile.y < 0 || tile.y >= canvasHeightInTiles) continue;

            Vector2Int? physicalSlot = backingStore.GetPhysicalSlot(tile.x, tile.y, 0);

            if (physicalSlot != null) {

                // ==========================================
                // PASS A: WIPE THE SCRATCHPAD
                // ==========================================
                RenderTexture.active = scratchpadRT;
                GL.Clear(true, true, new Color(0, 0, 0, 0)); // Wipe perfectly transparent
                // Reset the GPU's focus perfectly to the edges of the tiny scratchpad.
                GL.Viewport(new Rect(0, 0, tileSize, tileSize)); 
                // ==========================================
                // PASS B: DRAW BRUSH STAMPS (FLOW)
                // ==========================================
                brushManager.brushMaterial.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, 1, 0, 1); // this makes 0,0 the bottom-left of the scratchpad and 1,1 the top-right, regardless of tile size
                GL.LoadOrtho(); // 0 to 1 maps to the 256x256 scratchpad
                GL.Begin(GL.QUADS);

                foreach (Vector2 point in stampBuffer) {
                    float shiftedX = point.x + (canvasWorldWidth / 2f);
                    float shiftedY = point.y + (canvasWorldHeight / 2f);

                    float tileWorldX = tile.x * worldUnitsPerTile;
                    float tileWorldY = tile.y * worldUnitsPerTile;

                    float localX = (shiftedX - tileWorldX) / worldUnitsPerTile;
                    float localY = (shiftedY - tileWorldY) / worldUnitsPerTile;
                    float halfBrush = (brushManager.brushSize / worldUnitsPerTile) / 2f;

                    GL.TexCoord2(0, 0); GL.Vertex3(localX - halfBrush, localY - halfBrush, 0);
                    GL.TexCoord2(0, 1); GL.Vertex3(localX - halfBrush, localY + halfBrush, 0);
                    GL.TexCoord2(1, 1); GL.Vertex3(localX + halfBrush, localY + halfBrush, 0);
                    GL.TexCoord2(1, 0); GL.Vertex3(localX + halfBrush, localY - halfBrush, 0);
                }
                GL.End();
                GL.PopMatrix();

                // ==========================================
                // PASS C: MERGE TO ATLAS (OPACITY & BLEND)
                // ==========================================
                RenderTexture.active = atlas.Texture;

                // Restrict GPU to ONLY the target Physical Slot in the Atlas
                GL.Viewport(new Rect(physicalSlot.Value.x * tileSize, physicalSlot.Value.y * tileSize, tileSize, tileSize));

                // Inject the Scratchpad into the InkLayer Shader!
                inkLayerManager.inkLayerMaterial.SetTexture("_MainTex", scratchpadRT);
                inkLayerManager.inkLayerMaterial.SetPass(0);

                GL.PushMatrix();
                GL.LoadOrtho();
                GL.LoadPixelMatrix(0, 1, 0, 1);
                GL.Begin(GL.QUADS);

                // Draw a full-screen quad (0 to 1). The Viewport handles the scaling/positioning!
                GL.TexCoord2(0, 0); GL.Vertex3(0, 0, 0);
                GL.TexCoord2(0, 1); GL.Vertex3(0, 1, 0);
                GL.TexCoord2(1, 1); GL.Vertex3(1, 1, 0);
                GL.TexCoord2(1, 0); GL.Vertex3(1, 0, 0);

                GL.End();
                GL.PopMatrix();
            }
        }

        RenderTexture.active = null;
        Debug.Log($"Baked {stampBuffer.Count} stamps via Scratchpad permanently into VRAM!");
    }

   /* public void BakeStroke(List<Vector2> stampBuffer) {
        if (stampBuffer.Count == 0) return;

        float canvasWorldWidth = canvasWidthInTiles * worldUnitsPerTile;
        float canvasWorldHeight = canvasHeightInTiles * worldUnitsPerTile;

        // The radius of our brush in world units
        float brushRadius = brushManager.brushSize / 2f;

        // 1. FIND ALL TOUCHED TILES (Considering Brush Radius!)
        HashSet<Vector2Int> touchedTiles = new HashSet<Vector2Int>();

        foreach (Vector2 point in stampBuffer) {
            float shiftedX = point.x + (canvasWorldWidth / 2f);
            float shiftedY = point.y + (canvasWorldHeight / 2f);

            // Calculate the bounding box of the brush stamp to catch neighboring tiles
            int minX = Mathf.FloorToInt((shiftedX - brushRadius) / worldUnitsPerTile);
            int maxX = Mathf.FloorToInt((shiftedX + brushRadius) / worldUnitsPerTile);
            int minY = Mathf.FloorToInt((shiftedY - brushRadius) / worldUnitsPerTile);
            int maxY = Mathf.FloorToInt((shiftedY + brushRadius) / worldUnitsPerTile);

            for (int x = minX; x <= maxX; x++) {
                for (int y = minY; y <= maxY; y++) {
                    touchedTiles.Add(new Vector2Int(x, y));
                }
            }
        }

        // 2. ALLOCATE VRAM 
        foreach (Vector2Int tile in touchedTiles) {
            if (tile.x >= 0 && tile.x < canvasWidthInTiles && tile.y >= 0 && tile.y < canvasHeightInTiles) {
                backingStore.RequestTile(tile.x, tile.y, 0);
            }
        }
        backingStore.SyncGPU();

        // 3. RENDER WITH STRICT CLIPPING
        RenderTexture.active = atlas.Texture;
        brushManager.brushMaterial.SetPass(0);

        foreach (Vector2Int tile in touchedTiles) {
            // Skip tiles outside our mathematical canvas
            if (tile.x < 0 || tile.x >= canvasWidthInTiles || tile.y < 0 || tile.y >= canvasHeightInTiles) continue;

            Vector2Int? physicalSlot = backingStore.GetPhysicalSlot(tile.x, tile.y, 0);

            if (physicalSlot != null) {
                // THE MAGIC: Restrict the GPU to only draw strictly inside this specific physical tile!
                // It is physically impossible to bleed into neighbor slots now.
                GL.Viewport(new Rect(physicalSlot.Value.x * tileSize, physicalSlot.Value.y * tileSize, tileSize, tileSize));

                GL.PushMatrix();
                GL.LoadOrtho(); // 0.0 to 1.0 now maps perfectly to the edges of this specific tile
                GL.Begin(GL.QUADS);

                // Replay ALL stamps, mapping them relative to THIS tile
                foreach (Vector2 point in stampBuffer) {
                    float shiftedX = point.x + (canvasWorldWidth / 2f);
                    float shiftedY = point.y + (canvasWorldHeight / 2f);

                    // Find bottom-left corner of this specific tile
                    float tileWorldX = tile.x * worldUnitsPerTile;
                    float tileWorldY = tile.y * worldUnitsPerTile;

                    // Map stamp to 0.0 - 1.0 inside this tile
                    float localX = (shiftedX - tileWorldX) / worldUnitsPerTile;
                    float localY = (shiftedY - tileWorldY) / worldUnitsPerTile;

                    float halfBrush = (brushManager.brushSize / worldUnitsPerTile) / 2f;

                    // Draw it! GL.Viewport will automatically chop off anything outside 0-1
                    GL.TexCoord2(0, 0); GL.Vertex3(localX - halfBrush, localY - halfBrush, 0);
                    GL.TexCoord2(0, 1); GL.Vertex3(localX - halfBrush, localY + halfBrush, 0);
                    GL.TexCoord2(1, 1); GL.Vertex3(localX + halfBrush, localY + halfBrush, 0);
                    GL.TexCoord2(1, 0); GL.Vertex3(localX + halfBrush, localY - halfBrush, 0);
                }

                GL.End();
                GL.PopMatrix();
            }
        }

        RenderTexture.active = null;
        Debug.Log($"Baked {stampBuffer.Count} stamps across {touchedTiles.Count} tiles safely into VRAM!");
    }

    private Vector2Int WorldToTileCoordinate(Vector2 worldPos) {
        // 1. Calculate the total size of the canvas in world units
        float canvasWorldWidth = canvasWidthInTiles * worldUnitsPerTile;
        float canvasWorldHeight = canvasHeightInTiles * worldUnitsPerTile;

        // 2. Shift the math so the Unity Origin (0,0) is exactly in the middle of the canvas
        float shiftedX = worldPos.x + (canvasWorldWidth / 2f);
        float shiftedY = worldPos.y + (canvasWorldHeight / 2f);

        // 3. Calculate the grid index
        int tileX = Mathf.FloorToInt(shiftedX / worldUnitsPerTile);
        int tileY = Mathf.FloorToInt(shiftedY / worldUnitsPerTile);

        return new Vector2Int(tileX, tileY);
    }
   */

}
