using UnityEngine;

public class CameraTileRequester : MonoBehaviour
{
    [Header("Connections")]
    public Camera cam;
    public CanvasManager canvas;
    public float zoomOffset = 0f; // Optional offset to adjust when zoom levels change
    private bool isReady = false;
    private int lastZoomLevel = -1;

    public void Initialize() {
        isReady = true;
        RequestVisibleTiles();
    }

    /*void Update() {
        if (!isReady) return;
        //RequestVisibleTiles();
    }*/



    public void RequestVisibleTiles() {
        float camHeight = 2f * cam.orthographicSize;
        float camWidth = camHeight * cam.aspect;


        // Find how many actual Level 0 texture pixels are visible vertically
        float visibleLevel0PixelsY = (camHeight / canvas.worldUnitsPerTile) * canvas.tileSize;

        // Ratio of Texture Pixels to Screen Pixels
        float pixelRatio = visibleLevel0PixelsY / Screen.height;

        // Go up a mipmap level only when Texture Pixels compress too tightly into Screen Pixels
        // If I wanted to go up a mipmap level less often, I could use a threshold like: Mathf.Log(pixelRatio, 2) - 0.5f, which would require the texture to be half as dense before switching levels.
        //and if I wanted to go up more often, I could add a positive offset instead of subtracting one.
 
        int desiredZoom = Mathf.FloorToInt(Mathf.Log(pixelRatio, 2) + zoomOffset);

        int maxZoomAllowed = canvas.tables.Length - 1;
        int currentZoomLevel = Mathf.Clamp(desiredZoom, 0, maxZoomAllowed);

        //UPDATE SHADER USING CANVAS INTERFACE
        if (currentZoomLevel != lastZoomLevel) {
            lastZoomLevel = currentZoomLevel;   
            float scaleMultiplier = Mathf.Pow(2, currentZoomLevel);
            Debug.Log("Scale Multiplier: " + scaleMultiplier);
            Debug.Log("currentZoomLevel: " + currentZoomLevel);
            float virtualWidth = canvas.canvasWidthInTiles / scaleMultiplier; 
            float virtualHeight = canvas.canvasHeightInTiles / scaleMultiplier;
            //virtual width and height are how many tiles the shader should consider as the "full" canvas at this zoom level.
            canvas.UpdateSvtShader(currentZoomLevel, virtualWidth, virtualHeight);
        }

        //FIND VISIBLE TILES FOR CURRENT ZOOM LEVEL
        Vector2 camPos = cam.transform.position;
        float minWorldX = camPos.x - (camWidth / 2f);
        float maxWorldX = camPos.x + (camWidth / 2f);
        float minWorldY = camPos.y - (camHeight / 2f);
        float maxWorldY = camPos.y + (camHeight / 2f);

        Vector2Int minTile = canvas.WorldToTileCoordinate(new Vector2(minWorldX, minWorldY), currentZoomLevel);
        Vector2Int maxTile = canvas.WorldToTileCoordinate(new Vector2(maxWorldX, maxWorldY), currentZoomLevel);

        canvas.RequestTiles(minTile, maxTile, currentZoomLevel);
    }
}