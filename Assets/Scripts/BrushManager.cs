using System.Collections.Generic;
using UnityEngine;
using static UnityEngine.InputSystem.LowLevel.InputStateHistory;

public class BrushManager : MonoBehaviour
{
    //Brush Size Mode allows us to choose whether the brush size is in World Space (e.g. 50 units) or Screen Space (e.g. 50 pixels). This is a common feature in digital painting software.
    public enum BrushSizeMode { WorldSpace, ScreenSpace }
    [Header("Brush Settings")]
    public BrushSizeMode sizeMode = BrushSizeMode.ScreenSpace; // Default to Screen Space

    private List<Vector2> pointBuffer = new List<Vector2>(); // Stores our spline points
    private List<Vector2> stampBuffer = new List<Vector2>(); // Stores every single brush stamp

    public Material brushMaterial;
    public Material inkLayerMaterial;

    [Range(1, 1000)]
    public float brushSizeUI = 50f;
    private Color brushColor = Color.black;
    [Range(0.1f, 1f)] private float flow = 1f;

    [Header("Performance")]
    [Range(0.01f, 1f)]
    public float spacingFactor = 0.05f;

    [Header("Connections")]
    public Camera cam; // WE NEED THE CAMERA TO CONVERT COORDINATES!
    public InkLayerManager inkLayer;
    public CanvasManager canvas;

    private float distanceSinceLastDraw = 0f;
    private RenderTexture inkLayerRT;

    void Start() {
        SetBrushColor(brushColor);
        SetBrushFlow(flow);

        // Grab the screen-space render texture from the ink layer
        inkLayerRT = inkLayer.GetInkLayerRT();
    }

    // --------------------------------------------------------
    // STROKE LIFECYCLE
    // --------------------------------------------------------

    public void StartStroke(Vector2 worldPos) {
        pointBuffer.Clear();

        // Add the first point twice to act as the "anchor" for the Catmull-Rom spline
        pointBuffer.Add(worldPos);
        pointBuffer.Add(worldPos);

        distanceSinceLastDraw = 0f;

        // Draw the very first stamp immediately
        DrawStampAtWorldPos(worldPos);
    }

    public void AddPointToStroke(Vector2 worldPos) {
        pointBuffer.Add(worldPos);

        // We need at least 4 points to draw a beautiful Catmull-Rom curve
        if (pointBuffer.Count >= 4) {
            Vector2 p0 = pointBuffer[pointBuffer.Count - 4];
            Vector2 p1 = pointBuffer[pointBuffer.Count - 3];
            Vector2 p2 = pointBuffer[pointBuffer.Count - 2];
            Vector2 p3 = pointBuffer[pointBuffer.Count - 1];


            float currentSpacing = GetCurrentBrushSpacing();

            // 1. Calculate how many segments we need to check based on distance
            float distance = Vector2.Distance(p1, p2);
            //segments are how many times we will interpolate between p1 and p2.
            //We multiply by 2 because we want to be extra sure we don't miss any spots when the brush is moving quickly. This is a common technique in line rendering algorithms.
            int segments = Mathf.CeilToInt(distance / currentSpacing) * 2;
            segments = Mathf.Max(segments, 1);

            // 2. Walk along the mathematical curve
            for (int i = 1; i <= segments; i++) {
                float t = i / (float)segments;
                Vector2 interpolatedWorldPos = GetCatmullRomPosition(t, p0, p1, p2, p3);

                // 3. Keep track of how far we've traveled along the curve
                distanceSinceLastDraw += Vector2.Distance(p1, interpolatedWorldPos);

                // 4. Drop a stamp if we've traveled far enough!
                if (distanceSinceLastDraw >= currentSpacing) {
                    DrawStampAtWorldPos(interpolatedWorldPos);
                    distanceSinceLastDraw = 0f;
                }

                p1 = interpolatedWorldPos; // Step forward
            }
        }
    }

    public void EndStroke() {
        // Send the full list of stamps to the Engine
        canvas.BakeStroke(new List<Vector2>(stampBuffer));

        // Clear the screen-space UI preview
        inkLayer.ClearInkLayer();

        // Clear the buffers ready for the next stroke
        pointBuffer.Clear();
        stampBuffer.Clear();

        distanceSinceLastDraw = 0f;
    }

    public void PauseStroke(Vector2 worldPos) {
        // The user stopped moving. We break the current spline 
        // and set up new anchors so they can draw a sharp corner.

        // 1. Draw a single stamp exactly at the corner just in case
        DrawStampAtWorldPos(worldPos);

        // 2. Clear the buffer and start a fresh spline at this exact coordinate
        pointBuffer.Clear();
        pointBuffer.Add(worldPos);
        pointBuffer.Add(worldPos);

        distanceSinceLastDraw = 0f;
    }
    // --------------------------------------------------------
    // DRAWING LOGIC
    // --------------------------------------------------------


    private void DrawStampAtWorldPos(Vector2 worldPos) {
        //Record this exact coordinate for the SVT Bake
        stampBuffer.Add(worldPos);

        // Convert the infinite World Space into a 0.0 - 1.0 Viewport coordinate
        Vector3 viewportUV = cam.WorldToViewportPoint(new Vector3(worldPos.x, worldPos.y, 0));

        // If the coordinate is off-screen, don't bother rendering it to the Live Preview
        if (viewportUV.x < 0 || viewportUV.x > 1 || viewportUV.y < 0 || viewportUV.y > 1) return;

        DrawStampToPreview(viewportUV);
    }

    private void DrawStampToPreview(Vector3 viewportUV) {
        RenderTexture.active = inkLayerRT;

        // Tell the GPU we are using the brush material
        brushMaterial.SetPass(0);

        GL.PushMatrix();
        GL.LoadOrtho();
        GL.Begin(GL.QUADS);

        // --- DYNAMIC ZOOM SCALING ---
        float camWorldHeight = cam.orthographicSize * 2f;
        float camWorldWidth = camWorldHeight * cam.aspect;

        // ---Ask the manager how big the brush physically is right now ---
        float actualWorldSize = GetCurrentWorldBrushSize();

        float halfX = (actualWorldSize / camWorldWidth) / 2f;
        float halfY = (actualWorldSize / camWorldHeight) / 2f;

        // Draw the square using the Viewport Coordinates
        GL.TexCoord2(0, 0); GL.Vertex3(viewportUV.x - halfX, viewportUV.y - halfY, 0);
        GL.TexCoord2(0, 1); GL.Vertex3(viewportUV.x - halfX, viewportUV.y + halfY, 0);
        GL.TexCoord2(1, 1); GL.Vertex3(viewportUV.x + halfX, viewportUV.y + halfY, 0);
        GL.TexCoord2(1, 0); GL.Vertex3(viewportUV.x + halfX, viewportUV.y - halfY, 0);

        GL.End();
        GL.PopMatrix();
        RenderTexture.active = null;
    }

    // --------------------------------------------------------
    // SETTERS & MATH
    // --------------------------------------------------------

    public void SetBrushColor(Color brushColor) {
        this.brushColor = brushColor;
        brushMaterial.SetColor("_Color", brushColor);
    }



    public void SetBrushFlow(float flow) {
        this.flow = flow;
        brushMaterial.SetFloat("_Flow", flow);
    }

    public void SetBrushSize(float newUISize) {
        // Update the new UI variable instead of the old one
        brushSizeUI = newUISize;

        // Notice we COMPLETELY REMOVED the old brushSpacing calculation here!
        // Spacing is now calculated dynamically in GetCurrentBrushSpacing()
    }

    // Calculates the ACTUAL size of the brush in 3D World Units at this exact moment
    public float GetCurrentWorldBrushSize() {
        if (sizeMode == BrushSizeMode.WorldSpace) {
            // UI 100 = 1 World Unit. (Tweak the 100f if you want a different baseline scale!)
            return brushSizeUI / 100f;
        }
        else {
            // Screen Space: UI value represents Screen Pixels
            float screenFraction = brushSizeUI / Screen.height;
            float camWorldHeight = 2f * cam.orthographicSize;
            return screenFraction * camWorldHeight;
        }
    }

    // Since brush size can change dynamically when zooming (in Screen Space mode), 
    // we also need to calculate spacing dynamically!
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