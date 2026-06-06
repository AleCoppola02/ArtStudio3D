using System.Collections.Generic;
using UnityEngine;

public class BrushManager : MonoBehaviour
{
    [Header("Brush Settings")]
    private float lockedStrokeBrushSize = 0f;

    private List<Vector2> pointBuffer = new List<Vector2>();
    private List<Vector2> pendingStampsThisFrame = new List<Vector2>();

    [Header("Materials & Rendering")]
    public Material brushMaterial;
    public BlendModeConfig currentBlendMode;

    [Range(1, 1000)] public float brushSizeUI = 50f;
    public Color brushColor = Color.black;

    [Range(0.01f, 1f)] public float flow = 1f;
    [Range(0.01f, 1f)] public float opacity = 1f;

    [Header("Performance")]
    [Range(0.01f, 1f)] public float spacingFactor = 0.05f;

    [Header("Connections")]
    public Camera cam;
    public InkLayerManager inkLayerManager;
    public BackingStore backingStore;
    public CanvasManager canvasManager;

    public int activeDocumentLayerID = 0;

    private int currentStrokeID = -1;
    private RenderTexture currentPreviewRT;
    private float distanceSinceLastDraw = 0f;

    // Tracking the precise mathematical boundaries of the Padded Tile Grid
    private Vector2 previewWorldCenter;
    private Vector2 previewWorldSize;

    void Start() {
        SetBrushColor(brushColor);
        SetBrushFlow(flow);
    }

    public void StartStroke(Vector2 worldPos) {
        pointBuffer.Clear();
        pendingStampsThisFrame.Clear();

        lockedStrokeBrushSize = GetCurrentWorldBrushSize();

        float camHeight = 2f * cam.orthographicSize;
        float camWidth = camHeight * cam.aspect;

        float visibleLevel0PixelsY = (camHeight / canvasManager.worldUnitsPerTile) * canvasManager.tileSize;
        float pixelRatio = visibleLevel0PixelsY / Screen.height;
        int desiredZoom = Mathf.FloorToInt(Mathf.Log(pixelRatio, 2));
        int currentZoomLevel = Mathf.Clamp(desiredZoom, 0, canvasManager.tables.Length - 1);

        Vector2 camPos = cam.transform.position;
        float minWorldX = camPos.x - (camWidth / 2f);
        float maxWorldX = camPos.x + (camWidth / 2f);
        float minWorldY = camPos.y - (camHeight / 2f);
        float maxWorldY = camPos.y + (camHeight / 2f);

        Vector2Int minTile = canvasManager.WorldToTileCoordinate(new Vector2(minWorldX, minWorldY), currentZoomLevel);
        Vector2Int maxTile = canvasManager.WorldToTileCoordinate(new Vector2(maxWorldX, maxWorldY), currentZoomLevel);

        int maxTilesX = Mathf.CeilToInt(canvasManager.canvasWidthInTiles / Mathf.Pow(2, currentZoomLevel)) - 1;
        int maxTilesY = Mathf.CeilToInt(canvasManager.canvasHeightInTiles / Mathf.Pow(2, currentZoomLevel)) - 1;

        int minTileX = Mathf.Max(0, minTile.x - 1);
        int minTileY = Mathf.Max(0, minTile.y - 1);
        int maxTileX = Mathf.Min(maxTilesX, maxTile.x + 1);
        int maxTileY = Mathf.Min(maxTilesY, maxTile.y + 1);

        int rawTilesX = maxTileX - minTileX + 1;
        int rawTilesY = maxTileY - minTileY + 1;

        int p2TilesX = Mathf.NextPowerOfTwo(rawTilesX);
        int p2TilesY = Mathf.NextPowerOfTwo(rawTilesY);

        int rtWidth = p2TilesX * canvasManager.tileSize;
        int rtHeight = p2TilesY * canvasManager.tileSize;

        float unitsPerTileAtLOD = canvasManager.worldUnitsPerTile * Mathf.Pow(2, currentZoomLevel);
        previewWorldSize = new Vector2(p2TilesX * unitsPerTileAtLOD, p2TilesY * unitsPerTileAtLOD);

        float canvasWorldWidth = canvasManager.canvasWidthInTiles * canvasManager.worldUnitsPerTile;
        float canvasWorldHeight = canvasManager.canvasHeightInTiles * canvasManager.worldUnitsPerTile;

        float minXWorld = (minTileX * unitsPerTileAtLOD) - (canvasWorldWidth / 2f);
        float minYWorld = (minTileY * unitsPerTileAtLOD) - (canvasWorldHeight / 2f);

        previewWorldCenter = new Vector2(minXWorld + (previewWorldSize.x / 2f), minYWorld + (previewWorldSize.y / 2f));

        var layerData = inkLayerManager.RequestNewPreviewLayer(currentBlendMode, opacity, previewWorldCenter, previewWorldSize, rtWidth, rtHeight);
        currentStrokeID = layerData.strokeID;
        currentPreviewRT = layerData.rt;

        pointBuffer.Add(worldPos);
        pointBuffer.Add(worldPos);
        distanceSinceLastDraw = 0f;

        DrawStampAtWorldPos(worldPos);
        FlushStampsToBackingStore();
    }

    public void AddPointToStroke(Vector2 worldPos) {
        pointBuffer.Add(worldPos);

        if (pointBuffer.Count >= 4) {
            Vector2 p0 = pointBuffer[pointBuffer.Count - 4];
            Vector2 p1 = pointBuffer[pointBuffer.Count - 3];
            Vector2 p2 = pointBuffer[pointBuffer.Count - 2];
            Vector2 p3 = pointBuffer[pointBuffer.Count - 1];

            float currentSpacing = GetCurrentBrushSpacing();
            float distance = Vector2.Distance(p1, p2);

            float worldSize = GetCurrentWorldBrushSize();
            int segments = Mathf.CeilToInt(distance / (worldSize * 0.25f));
            segments = Mathf.Clamp(segments, 4, 100);

            Vector2 lastEvalPos = p1;

            for (int i = 1; i <= segments; i++) {
                float t = i / (float)segments;
                Vector2 interpolatedWorldPos = GetCatmullRomPosition(t, p0, p1, p2, p3);

                float stepDist = Vector2.Distance(lastEvalPos, interpolatedWorldPos);

                while (distanceSinceLastDraw + stepDist >= currentSpacing) {
                    float remainder = currentSpacing - distanceSinceLastDraw;
                    float ratio = remainder / stepDist;

                    Vector2 exactStampPos = Vector2.Lerp(lastEvalPos, interpolatedWorldPos, ratio);
                    DrawStampAtWorldPos(exactStampPos);

                    distanceSinceLastDraw = 0f;
                    stepDist -= remainder;
                    lastEvalPos = exactStampPos;
                }

                distanceSinceLastDraw += stepDist;
                lastEvalPos = interpolatedWorldPos;
            }

            FlushStampsToBackingStore();
        }
    }

    public void PauseStroke(Vector2 worldPos) {
        if (pointBuffer.Count > 0) AddPointToStroke(worldPos);
        DrawStampAtWorldPos(worldPos);

        pointBuffer.Clear();
        pointBuffer.Add(worldPos);
        pointBuffer.Add(worldPos);
        distanceSinceLastDraw = 0f;

        FlushStampsToBackingStore();
    }

    public void EndStroke() {
        if (pointBuffer.Count > 0) AddPointToStroke(pointBuffer[pointBuffer.Count - 1]);
        FlushStampsToBackingStore();

        if (backingStore != null) backingStore.EndStroke(currentStrokeID);

        pointBuffer.Clear();
        pendingStampsThisFrame.Clear();
        distanceSinceLastDraw = 0f;
        currentStrokeID = -1;
        currentPreviewRT = null;
    }

    private void DrawStampAtWorldPos(Vector2 worldPos) {
        pendingStampsThisFrame.Add(worldPos);
        DrawStampToPreview(worldPos);
    }

    private void DrawStampToPreview(Vector2 worldPos) {
        if (currentPreviewRT == null) return;

        RenderTexture.active = currentPreviewRT;
        GL.Viewport(new Rect(0, 0, currentPreviewRT.width, currentPreviewRT.height));

        brushMaterial.SetPass(0);

        GL.PushMatrix();

        // ==========================================
        // CRITICAL FIX: EXACT ORTHOGRAPHIC PROJECTION
        // ==========================================
        // 1. Calculate the exact physical World Space edges of the Quad
        float minX = previewWorldCenter.x - (previewWorldSize.x / 2f);
        float maxX = previewWorldCenter.x + (previewWorldSize.x / 2f);
        float minY = previewWorldCenter.y - (previewWorldSize.y / 2f);
        float maxY = previewWorldCenter.y + (previewWorldSize.y / 2f);

        // 2. Force the GPU Projection Matrix to match those World Bounds perfectly
        Matrix4x4 ortho = Matrix4x4.Ortho(minX, maxX, minY, maxY, -1f, 1f);
        GL.LoadProjectionMatrix(ortho);

        // 3. Clear ModelView so Unity's Camera position doesn't offset it
        Matrix4x4 backupMatrix = GL.modelview;
        GL.modelview = Matrix4x4.identity;

        GL.Begin(GL.QUADS);

        float halfSize = lockedStrokeBrushSize / 2f;

        // 4. DRAW EXACTLY AT THE MOUSE COORDINATES
        // Because the Matrix natively matches the world, we don't have to convert the mouse 
        // position to 0..1 UV bounds! We just hand the GPU the literal World Coordinates!
        GL.TexCoord2(0, 0); GL.Vertex3(worldPos.x - halfSize, worldPos.y - halfSize, 0);
        GL.TexCoord2(1, 0); GL.Vertex3(worldPos.x + halfSize, worldPos.y - halfSize, 0);
        GL.TexCoord2(1, 1); GL.Vertex3(worldPos.x + halfSize, worldPos.y + halfSize, 0);
        GL.TexCoord2(0, 1); GL.Vertex3(worldPos.x - halfSize, worldPos.y + halfSize, 0);

        GL.End();

        // Restore matrix state
        GL.modelview = backupMatrix;
        GL.PopMatrix();
        RenderTexture.active = null;
    }

    private void FlushStampsToBackingStore() {
        if (pendingStampsThisFrame.Count > 0 && currentStrokeID != -1 && backingStore != null) {
            backingStore.EnqueueStamps(activeDocumentLayerID, currentStrokeID, currentBlendMode, GetCurrentWorldBrushSize(), opacity, flow, new List<Vector2>(pendingStampsThisFrame));
            pendingStampsThisFrame.Clear();
        }
    }

    public void SetBrushColor(Color newColor) { brushColor = newColor; if (brushMaterial != null) brushMaterial.SetColor("_Color", brushColor); }
    public void SetBrushFlow(float newFlow) { flow = newFlow; if (brushMaterial != null) brushMaterial.SetFloat("_Flow", flow); }
    public void SetBrushOpacity(float newOpacity) { opacity = newOpacity; }
    public void SetBrushSize(float newUISize) { brushSizeUI = newUISize; }
    public float GetCurrentWorldBrushSize() { return brushSizeUI / 100f; }
    public float GetCurrentBrushSpacing() { return GetCurrentWorldBrushSize() * spacingFactor; }

    private Vector2 GetCatmullRomPosition(float t, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3) {
        Vector2 a = 2f * p1;
        Vector2 b = p2 - p0;
        Vector2 c = 2f * p0 - 5f * p1 + 4f * p2 - p3;
        Vector2 d = -p0 + 3f * p1 - 3f * p2 + p3;
        return 0.5f * (a + (b * t) + (c * t * t) + (d * t * t * t));
    }
}