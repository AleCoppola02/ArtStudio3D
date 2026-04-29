using System.IO;
using UnityEngine;

public class CanvasManager : MonoBehaviour
{
    [Header("SVT Settings")]
    public int tileSize = 256;
    public float worldUnitsPerTile = 1f; 
    [Header("References")]
    public CameraTileRequester tileRequester;
    public Material svtCanvasMaterial;
    public GhostCanvas ghostCanvas;         // NEW: The Background Baker
    public InkLayerManager inkLayerManager; // NEW: To hook up the events
    public Material inkLayerMaterial;
    // The SVT "Mathematical Canvas" bounds
    [HideInInspector] public int canvasWidthInTiles = 8;
    [HideInInspector] public int canvasHeightInTiles = 8;

    // The Engine Core
    public PhysicalAtlas atlas;
    public IndirectionTable[] tables;
    public BackingStore backingStore;
    public BrushManager brushManager;

    private void Start() {
        // 1. Calculate the physical size of the canvas
        float requestedWidth = CanvasConfig.Width;
        float requestedHeight = CanvasConfig.Height;

        // CRITICAL FIX: The internal SVT Canvas must perfectly align with the whole-number tile grid!
        canvasWidthInTiles = Mathf.CeilToInt(requestedWidth / tileSize);
        canvasHeightInTiles = Mathf.CeilToInt(requestedHeight / tileSize);

        float widthInWorldUnits = canvasWidthInTiles * worldUnitsPerTile;
        float heightInWorldUnits = canvasHeightInTiles * worldUnitsPerTile;

        transform.localScale = new Vector3(widthInWorldUnits, heightInWorldUnits, 1f);

        // 2. Calculate Mipmap Levels
        int maxDim = Mathf.Max(canvasWidthInTiles, canvasHeightInTiles);
        int numMipmapLevels = Mathf.FloorToInt(Mathf.Log(maxDim, 2)) + 1;

        // 3. Initialize VRAM Resources
        atlas = new PhysicalAtlas(4096, tileSize);

        RenderTexture.active = atlas.Texture;
        GL.Clear(true, true, Color.white);
        RenderTexture.active = null;

        tables = new IndirectionTable[numMipmapLevels];
        for (int z = 0; z < numMipmapLevels; z++) {
            // CRITICAL FIX: Use CeilToInt with Pow so odds don't get truncated!
            int w = Mathf.CeilToInt(canvasWidthInTiles / Mathf.Pow(2, z));
            int h = Mathf.CeilToInt(canvasHeightInTiles / Mathf.Pow(2, z));
            tables[z] = new IndirectionTable(z, w, h);
        }

        // 4. Initialize the Background Workers
        string saveDir = System.IO.Path.Combine(Application.persistentDataPath, "SVT_Cache");

        backingStore = new BackingStore(atlas, tables, ghostCanvas, saveDir);

        if (FindObjectOfType<BrushManager>() != null) {
            FindObjectOfType<BrushManager>().backingStore = backingStore;
        }

        ghostCanvas.backingStore = backingStore;
        ghostCanvas.Initialize(FindObjectOfType<BrushManager>().brushMaterial, inkLayerMaterial, this, saveDir);

        backingStore.OnLayerBakingFinished += inkLayerManager.ReleaseStrokeLayer;

        // 5. Give the Shaders the VRAM Maps
        if (svtCanvasMaterial != null) {
            svtCanvasMaterial.SetTexture("_PhysicalAtlas", atlas.Texture);
            svtCanvasMaterial.SetTexture("_IndirectionTable", tables[0].TableTexture);
            svtCanvasMaterial.SetVector("_TableSize", new Vector4(canvasWidthInTiles, canvasHeightInTiles, 0, 0));
            svtCanvasMaterial.SetFloat("_AtlasSlotsAcross", atlas.AtlasSize / (float)tileSize);
        }

        // Frame the exact requested size (so the extra padding tiles hang off-screen naturally)
        float exactRequestedWorldWidth = (requestedWidth / tileSize) * worldUnitsPerTile;
        float exactRequestedWorldHeight = (requestedHeight / tileSize) * worldUnitsPerTile;
        FrameCanvasPerfectly(exactRequestedWorldWidth, exactRequestedWorldHeight);

        if (tileRequester != null) tileRequester.Initialize();
    }
    public Vector2Int WorldToTileCoordinate(Vector2 worldPos, int zoomLevel = 0) {
        float scaleMultiplier = Mathf.Pow(2, zoomLevel);
        float currentUnitsPerTile = worldUnitsPerTile * scaleMultiplier;

        // CRITICAL FIX: Match the exact math of the Table Generator!
        int currentWidthInTiles = Mathf.CeilToInt(canvasWidthInTiles / scaleMultiplier);
        int currentHeightInTiles = Mathf.CeilToInt(canvasHeightInTiles / scaleMultiplier);

        float canvasWorldWidth = canvasWidthInTiles * worldUnitsPerTile;
        float canvasWorldHeight = canvasHeightInTiles * worldUnitsPerTile;

        float shiftedX = worldPos.x + (canvasWorldWidth / 2f);
        float shiftedY = worldPos.y + (canvasWorldHeight / 2f);

        int tileX = Mathf.FloorToInt(shiftedX / currentUnitsPerTile);
        int tileY = Mathf.FloorToInt(shiftedY / currentUnitsPerTile);

        tileX = Mathf.Clamp(tileX, 0, currentWidthInTiles - 1);
        tileY = Mathf.Clamp(tileY, 0, currentHeightInTiles - 1);

        return new Vector2Int(tileX, tileY);
    }

    private void FrameCanvasPerfectly(float widthInWorldUnits, float heightInWorldUnits) {
        Camera cam = Camera.main;
        cam.transform.position = new Vector3(0, 0, -10f);
        float sizeToFitHeight = heightInWorldUnits / 2f;
        float sizeToFitWidth = (widthInWorldUnits / 2f) / cam.aspect;
        cam.orthographicSize = Mathf.Max(sizeToFitHeight, sizeToFitWidth) * 1.05f;
    }

    private void OnDestroy() {
        if (backingStore != null) {
            backingStore.OnLayerBakingFinished -= inkLayerManager.ReleaseStrokeLayer;
        }
    }
}