using System.Collections.Generic;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

public class CanvasManager : MonoBehaviour
{
    [Header("SVT Settings")]
    public int tileSize = 256;
    public float worldUnitsPerTile = 1f;

    [Header("References")]
    public CameraTileRequester tileRequester;

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
        // 1. Read from the bridge
        float requestedWidth = CanvasConfig.Width;
        float requestedHeight = CanvasConfig.Height;

        // 2. Calculate the exact float dimensions in World Units
        float exactWidthInTiles = requestedWidth / tileSize;
        float exactHeightInTiles = requestedHeight / tileSize;

        float widthInWorldUnits = exactWidthInTiles * worldUnitsPerTile;
        float heightInWorldUnits = exactHeightInTiles * worldUnitsPerTile;

        // 3. Scale the Quad correctly!
        transform.localScale = new Vector3(widthInWorldUnits, heightInWorldUnits, 1f);

        // 4. Calculate the Memory Grid (Padding the void for the Indirection Table)
        canvasWidthInTiles = Mathf.CeilToInt(exactWidthInTiles);
        canvasHeightInTiles = Mathf.CeilToInt(exactHeightInTiles);

        // 5. Calculate Mipmap Levels
        int maxDim = Mathf.Max(canvasWidthInTiles, canvasHeightInTiles);
        int numMipmapLevels = Mathf.FloorToInt(Mathf.Log(maxDim, 2)) + 1;


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



        tables = new IndirectionTable[numMipmapLevels];
        int currentWidth = canvasWidthInTiles;
        int currentHeight = canvasHeightInTiles;

        for (int i = 0; i < numMipmapLevels; i++) {
            tables[i] = new IndirectionTable(i, currentWidth, currentHeight);

            // Shrink the grid by 50% for the next zoom level
            currentWidth = Mathf.Max(1, currentWidth / 2);
            currentHeight = Mathf.Max(1, currentHeight / 2);
        }

        backingStore = new BackingStore(this, atlas, tables);

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

        FrameCanvasPerfectly(widthInWorldUnits, heightInWorldUnits);

        if (tileRequester != null) {
            tileRequester.Initialize();
        }
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
        float brushRadius = brushManager.GetCurrentWorldBrushSize() / 2f;

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
                backingStore.RequestTile(tile.x, tile.y, 0, true);
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
                    float halfBrush = (brushManager.GetCurrentWorldBrushSize() / worldUnitsPerTile) / 2f;

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

                backingStore.MarkTileDirty(tile.x, tile.y, 0);

                backingStore.QueueMipmapUpdate(tile.x, tile.y, 0);
            }
        }

        RenderTexture.active = null;
        Debug.Log($"Baked {stampBuffer.Count} stamps via Scratchpad permanently into VRAM!");
    }

    public Vector2Int WorldToTileCoordinate(Vector2 worldPos, int zoomLevel = 0) {
        // At Zoom 0, multiplier is 1. Zoom 1 = 2. Zoom 2 = 4. Zoom 3 = 8.
        float scaleMultiplier = Mathf.Pow(2, zoomLevel);
        float currentUnitsPerTile = worldUnitsPerTile * scaleMultiplier;

        // Calculate how many tiles exist at this specific zoom level
        int currentWidthInTiles = Mathf.Max(1, canvasWidthInTiles >> zoomLevel);
        int currentHeightInTiles = Mathf.Max(1, canvasHeightInTiles >> zoomLevel);

        // The absolute physical bounds of the canvas NEVER change
        float canvasWorldWidth = canvasWidthInTiles * worldUnitsPerTile;
        float canvasWorldHeight = canvasHeightInTiles * worldUnitsPerTile;

        float shiftedX = worldPos.x + (canvasWorldWidth / 2f);
        float shiftedY = worldPos.y + (canvasWorldHeight / 2f);

        // Divide by our new scaled tile size
        int tileX = Mathf.FloorToInt(shiftedX / currentUnitsPerTile);
        int tileY = Mathf.FloorToInt(shiftedY / currentUnitsPerTile);

        // Clamp using the new, smaller grid dimensions
        tileX = Mathf.Clamp(tileX, 0, currentWidthInTiles - 1);
        tileY = Mathf.Clamp(tileY, 0, currentHeightInTiles - 1);

        return new Vector2Int(tileX, tileY);
    }

    private void OnApplicationQuit() {
        if (backingStore != null) {
            Debug.Log("Application closing. Wiping the SVT hard drive cache...");
            backingStore.ClearAllSavedTiles();
        }
    }

    private void FrameCanvasPerfectly(float widthInWorldUnits, float heightInWorldUnits) {
        Camera cam = Camera.main;

        // 1. Center the camera over the quad (Assuming Quad is at 0,0,0)
        cam.transform.position = new Vector3(0, 0, -10f);

        // 2. Calculate the required Orthographic Size (which is half the vertical height)
        float sizeToFitHeight = heightInWorldUnits / 2f;
        float sizeToFitWidth = (widthInWorldUnits / 2f) / cam.aspect;

        // 3. Apply the largest requirement + 5% visual padding
        cam.orthographicSize = Mathf.Max(sizeToFitHeight, sizeToFitWidth) * 1.05f;
    }

}
