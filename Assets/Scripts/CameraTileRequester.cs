using UnityEngine;

public class CameraTileRequester : MonoBehaviour
{
    [Header("Connections")]
    public Camera cam;
    public CanvasManager canvas;

    // 1. Our explicit lock flag
    private bool isReady = false;

    // ADD THIS: Track the zoom level so we know when it changes!
    private int lastZoomLevel = -1;

    // 2. The explicit starting gun, called by CanvasManager
    public void Initialize() {
        isReady = true;

        // Optionally: Force the first tile request immediately upon initialization
        // so the camera doesn't have to wait for the next Update() cycle to start loading.
        RequestVisibleTiles();
    }

    void Update() {
        // 3. A much cheaper check! We just check a single boolean instead of multiple object references.
        if (!isReady) return;

        RequestVisibleTiles();
    }

    private void RequestVisibleTiles() {
        float camHeight = 2f * cam.orthographicSize;
        float camWidth = camHeight * cam.aspect;

        // ==========================================
        // 1. CALCULATE THE OPTIMAL ZOOM LEVEL (LOD)
        // ==========================================
        float camHeightInOriginalTiles = camHeight / canvas.worldUnitsPerTile;

        // Log2 tells us exactly when we double in size! 
        int desiredZoom = Mathf.FloorToInt(Mathf.Log(camHeightInOriginalTiles, 2));

        // Clamp it so we don't ask for a zoom level that doesn't exist
        int maxZoomAllowed = canvas.tables.Length - 1;
        int currentZoomLevel = Mathf.Clamp(desiredZoom, 0, maxZoomAllowed);

        // =======================
        // --- SHADER UPDATE ---
        // =======================
        // If the zoom level just changed, we MUST update the material!
        if (currentZoomLevel != lastZoomLevel) {
            lastZoomLevel = currentZoomLevel;

            IndirectionTable currentTable = canvas.tables[currentZoomLevel];

            // 1. Give the GPU the correct Indirection Table for this zoom level
            canvas.svtCanvasMaterial.SetTexture("_IndirectionTable", currentTable.TableTexture);

            // 2. Tell the Shader the new grid size so the fractional UV math doesn't explode!
            canvas.svtCanvasMaterial.SetVector("_TableSize", new Vector4(currentTable.Width, currentTable.Height, 0, 0));
        }
        // ======================

        // ==========================================
        // 2. FIND VISIBLE TILES AT THAT ZOOM LEVEL
        // ==========================================
        Vector2 camPos = cam.transform.position;
        float minWorldX = camPos.x - (camWidth / 2f);
        float maxWorldX = camPos.x + (camWidth / 2f);
        float minWorldY = camPos.y - (camHeight / 2f);
        float maxWorldY = camPos.y + (camHeight / 2f);

        // Convert those world coordinates to tile coordinates at the current zoom level
        Vector2Int minTile = canvas.WorldToTileCoordinate(new Vector2(minWorldX, minWorldY), currentZoomLevel);
        Vector2Int maxTile = canvas.WorldToTileCoordinate(new Vector2(maxWorldX, maxWorldY), currentZoomLevel);

        // Request the tiles
        for (int x = minTile.x; x <= maxTile.x; x++) {
            for (int y = minTile.y; y <= maxTile.y; y++) {
                canvas.backingStore.RequestTile(x, y, currentZoomLevel);
            }
        }
        Debug.Log($"Requested tiles from ({minTile.x}, {minTile.y}) to ({maxTile.x}, {maxTile.y}) at zoom level {currentZoomLevel}");
        canvas.backingStore.SyncGPU();
    }
}