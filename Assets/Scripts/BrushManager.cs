using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;
using static UnityEditor.Experimental.GraphView.Port;

public class BrushManager : MonoBehaviour
{
    public enum BrushSizeMode { WorldSpace, ScreenSpace }
    [Header("Brush Settings")]
    public BrushSizeMode sizeMode = BrushSizeMode.ScreenSpace;
    private float lockedStrokeBrushSize = 0f;

    // Buffers
    private List<Vector2> pointBuffer = new List<Vector2>(); // For the spline
    private List<Vector2> pendingStampsThisFrame = new List<Vector2>(); // For real-time streaming to the SVT[Header("Materials & Rendering")]
    public Material brushMaterial; // Handles the FLOW (alpha buildup per stamp)
    public BlendModeConfig currentBlendMode;

    [Range(1, 1000)] public float brushSizeUI = 50f;
    public Color brushColor = Color.black;

    // THE PHOTOSHOP DUAL-SYSTEM[Range(0.01f, 1f)] public float flow = 1f;    // Alpha per stamp[Range(0.01f, 1f)] public float opacity = 1f; // Max opacity for the whole stroke[Header("Performance")]
    [Range(0.01f, 1f)] public float spacingFactor = 0.05f;

    [Header("Connections")]
    public Camera cam;
    public InkLayerManager inkLayerManager;
    public BackingStore backingStore;

    // Document Layer Tracking
    public int activeDocumentLayerID = 0; // Which permanent SVT layer are we drawing on?

    // Active Stroke Tracking
    private int currentStrokeID = -1;
    private RenderTexture currentPreviewRT;
    private float distanceSinceLastDraw = 0f;

    private float flow = 1f;
    private float opacity = 1f;

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

        // ---> NEW: Lock the brush size so it perfectly matches BackingStore <---
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
            int segments = Mathf.CeilToInt(distance / currentSpacing) * 2;
            segments = Mathf.Max(segments, 1);

            for (int i = 1; i <= segments; i++) {
                float t = i / (float)segments;
                Vector2 interpolatedWorldPos = GetCatmullRomPosition(t, p0, p1, p2, p3);

                distanceSinceLastDraw += Vector2.Distance(p1, interpolatedWorldPos);

                if (distanceSinceLastDraw >= currentSpacing) {
                    DrawStampAtWorldPos(interpolatedWorldPos);
                    distanceSinceLastDraw = 0f;
                }
                p1 = interpolatedWorldPos;
            }

            // Send the newly generated stamps to the background thread
            FlushStampsToBackingStore();
        }
    }

    public void PauseStroke(Vector2 worldPos) {
        // Drop an anchor, break the spline, flush
        DrawStampAtWorldPos(worldPos);

        pointBuffer.Clear();
        pointBuffer.Add(worldPos);
        pointBuffer.Add(worldPos);
        distanceSinceLastDraw = 0f;

        FlushStampsToBackingStore();
    }

    public void EndStroke() {
        // Ensure any remaining stamps are sent
        FlushStampsToBackingStore();

        // Tell the background thread this stroke is officially done receiving new stamps
        backingStore.EndStroke(currentStrokeID);

        // Clear local tracking
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
        // Queue it for the SVT
        pendingStampsThisFrame.Add(worldPos);

        // Calculate screen position for the Live Preview
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

        // ---> NEW: Use the locked size! <---
        float actualWorldSize = lockedStrokeBrushSize;

        float halfX = (actualWorldSize / camWorldWidth) / 2f;
        float halfY = (actualWorldSize / camWorldHeight) / 2f;
        GL.TexCoord2(0, 0); GL.Vertex3(viewportUV.x - halfX, viewportUV.y - halfY, 0);
        GL.TexCoord2(0, 1); GL.Vertex3(viewportUV.x - halfX, viewportUV.y + halfY, 0);
        GL.TexCoord2(1, 1); GL.Vertex3(viewportUV.x + halfX, viewportUV.y + halfY, 0);
        GL.TexCoord2(1, 0); GL.Vertex3(viewportUV.x + halfX, viewportUV.y - halfY, 0);

        GL.End();
        GL.PopMatrix();
        RenderTexture.active = null;
    }

    private void FlushStampsToBackingStore() {
        if (pendingStampsThisFrame.Count > 0 && currentStrokeID != -1) {
            // Stream a copy of the list so we don't accidentally clear it while it's queueing
            backingStore.EnqueueStamps(activeDocumentLayerID, currentStrokeID, currentBlendMode, GetCurrentWorldBrushSize(), opacity, flow, new List<Vector2>(pendingStampsThisFrame));

            pendingStampsThisFrame.Clear();
        }
    }

    // --------------------------------------------------------
    // SETTERS & MATH
    // --------------------------------------------------------

    public void SetBrushColor(Color newColor) {
        brushColor = newColor;
        brushMaterial.SetColor("_Color", brushColor);
    }

    public void SetBrushFlow(float newFlow) {
        flow = newFlow;
        brushMaterial.SetFloat("_Flow", flow);
    }

    public void SetBrushOpacity(float newOpacity) {
        opacity = newOpacity;
        // Opacity is handled by InkLayerManager/SVT Merge, so we just track the float here.
    }

    public void SetBrushSize(float newUISize) {
        brushSizeUI = newUISize;
    }

    public float GetCurrentWorldBrushSize() {
        if (sizeMode == BrushSizeMode.WorldSpace) return brushSizeUI / 100f; // Arbitrary scaling factor to convert UI size to world units
        float screenFraction = brushSizeUI / Screen.height;
        return screenFraction * (2f * cam.orthographicSize);
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