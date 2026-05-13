using System.Collections.Generic;
using UnityEngine;

public class BrushManager : MonoBehaviour
{
    [Header("Brush Settings")]
    private float lockedStrokeBrushSize = 0f;

    // Buffers
    private List<Vector2> pointBuffer = new List<Vector2>();
    private List<Vector2> pendingStampsThisFrame = new List<Vector2>();

    [Header("Materials & Rendering")]
    public Material brushMaterial;
    public BlendModeConfig currentBlendMode;

    [Range(1, 1000)] public float brushSizeUI = 50f;
    public Color brushColor = Color.black;

    // THE PHOTOSHOP DUAL-SYSTEM
    [Range(0.01f, 1f)] public float flow = 1f;
    [Range(0.01f, 1f)] public float opacity = 1f;

    [Header("Performance")]
    [Range(0.01f, 1f)] public float spacingFactor = 0.05f;

    [Header("Connections")]
    public Camera cam;
    public InkLayerManager inkLayerManager;
    public BackingStore backingStore;

    public int activeDocumentLayerID = 0;

    private int currentStrokeID = -1;
    private RenderTexture currentPreviewRT;
    private float distanceSinceLastDraw = 0f;

    void Start() {
        SetBrushColor(brushColor);
        SetBrushFlow(flow);
    }

    // --------------------------------------------------------
    // STROKE LIFECYCLE
    // --------------------------------------------------------

    public void StartStroke(Vector2 worldPos) {
        pointBuffer.Clear();
        pendingStampsThisFrame.Clear();

        lockedStrokeBrushSize = GetCurrentWorldBrushSize();

        var layerData = inkLayerManager.RequestNewPreviewLayer(currentBlendMode, opacity);
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

            // Calculate how many times to sample the mathematical curve.
            // Capping this prevents lag spikes on fast, microscopic strokes!
            float worldSize = GetCurrentWorldBrushSize();
            int segments = Mathf.CeilToInt(distance / (worldSize * 0.25f));
            segments = Mathf.Clamp(segments, 4, 100);

            // CRITICAL FIX 1: Track distance using a separate variable so we don't destroy the Spline Math!
            Vector2 lastEvalPos = p1;

            for (int i = 1; i <= segments; i++) {
                float t = i / (float)segments;
                Vector2 interpolatedWorldPos = GetCatmullRomPosition(t, p0, p1, p2, p3);

                float stepDist = Vector2.Distance(lastEvalPos, interpolatedWorldPos);

                // CRITICAL FIX 2: A while-loop allows us to perfectly interpolate hundreds of stamps
                // along the curve without calculating expensive Catmull-Rom math for every single stamp.
                while (distanceSinceLastDraw + stepDist >= currentSpacing) {
                    float remainder = currentSpacing - distanceSinceLastDraw;
                    float ratio = remainder / stepDist;

                    // Find the exact mathematical point!
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
        if (pointBuffer.Count > 0) {
            AddPointToStroke(worldPos);
        }

        DrawStampAtWorldPos(worldPos);

        pointBuffer.Clear();
        pointBuffer.Add(worldPos);
        pointBuffer.Add(worldPos);
        distanceSinceLastDraw = 0f;

        FlushStampsToBackingStore();
    }

    public void EndStroke() {
        // Cap off the lagging segment of the curve by duplicating the final point
        if (pointBuffer.Count > 0) {
            AddPointToStroke(pointBuffer[pointBuffer.Count - 1]);
        }

        FlushStampsToBackingStore();

        if (backingStore != null) {
            backingStore.EndStroke(currentStrokeID);
        }

        pointBuffer.Clear();
        pendingStampsThisFrame.Clear();
        distanceSinceLastDraw = 0f;
        currentStrokeID = -1;
        currentPreviewRT = null;
    }

    // --------------------------------------------------------
    // DRAWING LOGIC
    // --------------------------------------------------------

    private void DrawStampAtWorldPos(Vector2 worldPos) {
        pendingStampsThisFrame.Add(worldPos);

        Vector3 viewportUV = cam.WorldToViewportPoint(new Vector3(worldPos.x, worldPos.y, 0));
        if (viewportUV.x < 0 || viewportUV.x > 1 || viewportUV.y < 0 || viewportUV.y > 1) return;

        DrawStampToPreview(viewportUV);
    }

    private void DrawStampToPreview(Vector3 viewportUV) {
        if (currentPreviewRT == null) return;

        RenderTexture.active = currentPreviewRT;
        brushMaterial.SetPass(0);

        GL.PushMatrix();
        GL.LoadOrtho();
        GL.Begin(GL.QUADS);

        float camWorldHeight = cam.orthographicSize * 2f;
        float camWorldWidth = camWorldHeight * cam.aspect;

        float actualWorldSize = lockedStrokeBrushSize;

        float halfX = (actualWorldSize / camWorldWidth) / 2f;
        float halfY = (actualWorldSize / camWorldHeight) / 2f;

        GL.TexCoord2(0, 0); GL.Vertex3(viewportUV.x - halfX, viewportUV.y - halfY, 0);
        GL.TexCoord2(1, 0); GL.Vertex3(viewportUV.x + halfX, viewportUV.y - halfY, 0);
        GL.TexCoord2(1, 1); GL.Vertex3(viewportUV.x + halfX, viewportUV.y + halfY, 0);
        GL.TexCoord2(0, 1); GL.Vertex3(viewportUV.x - halfX, viewportUV.y + halfY, 0);

        GL.End();
        GL.PopMatrix();
        RenderTexture.active = null;
    }

    private void FlushStampsToBackingStore() {
        if (pendingStampsThisFrame.Count > 0 && currentStrokeID != -1 && backingStore != null) {
            backingStore.EnqueueStamps(activeDocumentLayerID, currentStrokeID, currentBlendMode, GetCurrentWorldBrushSize(), opacity, flow, new List<Vector2>(pendingStampsThisFrame));
            pendingStampsThisFrame.Clear();
        }
    }

    // --------------------------------------------------------
    // SETTERS & MATH
    // --------------------------------------------------------

    public void SetBrushColor(Color newColor) {
        brushColor = newColor;
        if (brushMaterial != null) brushMaterial.SetColor("_Color", brushColor);
    }

    public void SetBrushFlow(float newFlow) {
        flow = newFlow;
        if (brushMaterial != null) brushMaterial.SetFloat("_Flow", flow);
    }

    public void SetBrushOpacity(float newOpacity) {
        opacity = newOpacity;
    }

    public void SetBrushSize(float newUISize) {
        brushSizeUI = newUISize;
    }

    public float GetCurrentWorldBrushSize() {
        // Enforced strict World Space sizing. 100 UI Units = 1 World Unit.
        return brushSizeUI / 100f;
    }

    public float GetCurrentBrushSpacing() {
        return GetCurrentWorldBrushSize() * spacingFactor;
    }

    private Vector2 GetCatmullRomPosition(float t, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3) {
        Vector2 a = 2f * p1;
        Vector2 b = p2 - p0;
        Vector2 c = 2f * p0 - 5f * p1 + 4f * p2 - p3;
        Vector2 d = -p0 + 3f * p1 - 3f * p2 + p3;
        return 0.5f * (a + (b * t) + (c * t * t) + (d * t * t * t));
    }
}