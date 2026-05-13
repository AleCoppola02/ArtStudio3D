using UnityEngine;

public class CameraTileRequester : MonoBehaviour
{
    [Header("Connections")]
    public Camera cam;
    public CanvasManager canvas;

    private bool isReady = false;
    private int lastZoomLevel = -1;

    public void Initialize() {
        isReady = true;
        RequestVisibleTiles();
    }

    void Update() {
        if (!isReady) return;
        RequestVisibleTiles();
    }

    private void RequestVisibleTiles() {
        float camHeight = 2f * cam.orthographicSize;
        float camWidth = camHeight * cam.aspect;

        // ==========================================
        // CRITICAL FIX: PROPER SCREEN-PIXEL LOD MATH
        // ==========================================
        // How many actual Level 0 texture pixels are visible vertically?
        float visibleLevel0PixelsY = (camHeight / canvas.worldUnitsPerTile) * canvas.tileSize;

        // Ratio of Texture Pixels to Screen Pixels
        float pixelRatio = visibleLevel0PixelsY / Screen.height;

        // Go up a mipmap level ONLY when Texture Pixels compress too tightly into Screen Pixels
        int desiredZoom = Mathf.FloorToInt(Mathf.Log(pixelRatio, 2));

        int maxZoomAllowed = canvas.tables.Length - 1;
        int currentZoomLevel = Mathf.Clamp(desiredZoom, 0, maxZoomAllowed);
        //log the max zoom and current zoom
        Debug.Log($"Max Zoom Allowed: {maxZoomAllowed}, Desired Zoom: {desiredZoom}, Current Zoom Level: {currentZoomLevel}");
        // =======================
        // --- SHADER UPDATE ---
        // =======================
        if (currentZoomLevel != lastZoomLevel) {
            lastZoomLevel = currentZoomLevel;
            IndirectionTable currentTable = canvas.tables[currentZoomLevel];

            canvas.svtCanvasMaterial.SetTexture("_IndirectionTable", currentTable.TableTexture);

            // CRITICAL FIX: Pass the true fractional sizes to the shader!
            float scaleMultiplier = Mathf.Pow(2, currentZoomLevel);
            float virtualWidth = canvas.canvasWidthInTiles / scaleMultiplier;
            float virtualHeight = canvas.canvasHeightInTiles / scaleMultiplier;

            canvas.svtCanvasMaterial.SetVector("_TableSize", new Vector4(virtualWidth, virtualHeight, 0, 0));
            canvas.svtCanvasMaterial.SetVector("_TableResolution", new Vector4(currentTable.Width, currentTable.Height, 0, 0));
        }

        // ==========================================
        // 2. FIND VISIBLE TILES AT THAT ZOOM LEVEL
        // ==========================================
        Vector2 camPos = cam.transform.position;
        float minWorldX = camPos.x - (camWidth / 2f);
        float maxWorldX = camPos.x + (camWidth / 2f);
        float minWorldY = camPos.y - (camHeight / 2f);
        float maxWorldY = camPos.y + (camHeight / 2f);

        Vector2Int minTile = canvas.WorldToTileCoordinate(new Vector2(minWorldX, minWorldY), currentZoomLevel);
        Vector2Int maxTile = canvas.WorldToTileCoordinate(new Vector2(maxWorldX, maxWorldY), currentZoomLevel);

        for (int x = minTile.x; x <= maxTile.x; x++) {
            for (int y = minTile.y; y <= maxTile.y; y++) {
                canvas.backingStore.RequestTile(x, y, currentZoomLevel);
            }
        }
        canvas.backingStore.SyncGPU();
    }
}