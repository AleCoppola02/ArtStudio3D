using UnityEngine;

public class CameraTileRequester : MonoBehaviour
{
    [Header("Connections")]
    public Camera cam;
    public CanvasManager canvas;

    void Update() {
        // We only want to run this if the SVT engine is actually running
        if (canvas != null && canvas.backingStore != null) {
            RequestVisibleTiles();
        }
    }

    private void RequestVisibleTiles() {
        float camHeight = 2f * cam.orthographicSize;
        float camWidth = camHeight * cam.aspect;

        // ==========================================
        // 1. CALCULATE THE OPTIMAL ZOOM LEVEL (LOD)
        // ==========================================
        float camHeightInOriginalTiles = camHeight / canvas.worldUnitsPerTile;

        // Log2 tells us exactly when we double in size! 
        // Example: log2(2) = 1. log2(4) = 2. log2(8) = 3.
        int desiredZoom = Mathf.FloorToInt(Mathf.Log(camHeightInOriginalTiles, 2));

        // Clamp it so we don't ask for a zoom level that doesn't exist
        int maxZoomAllowed = canvas.tables.Length - 1;
        int currentZoomLevel = Mathf.Clamp(desiredZoom, 0, maxZoomAllowed);
        // ==========================================
        // 2. FIND VISIBLE TILES AT THAT ZOOM LEVEL
        // ==========================================
        Vector2 camPos = cam.transform.position;
        float minWorldX = camPos.x - (camWidth / 2f);
        float maxWorldX = camPos.x + (camWidth / 2f);
        float minWorldY = camPos.y - (camHeight / 2f);
        float maxWorldY = camPos.y + (camHeight / 2f);

        // Notice we are passing the new currentZoomLevel here!
        Vector2Int minTile = canvas.WorldToTileCoordinate(new Vector2(minWorldX, minWorldY), currentZoomLevel);
        Vector2Int maxTile = canvas.WorldToTileCoordinate(new Vector2(maxWorldX, maxWorldY), currentZoomLevel);

        // Request the tiles
        for (int x = minTile.x; x <= maxTile.x; x++) {
            for (int y = minTile.y; y <= maxTile.y; y++) {
                // Notice we are requesting the specific Zoom Level!
                canvas.backingStore.RequestTile(x, y, currentZoomLevel);
            }
        }

        canvas.backingStore.SyncGPU();
    }
}