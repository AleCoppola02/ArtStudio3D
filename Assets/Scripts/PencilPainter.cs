using UnityEngine;

public class TruePencil : MonoBehaviour
{
    [Header("Connections")]
    public RenderTexture canvasRT;
    public RenderTexture inkLayerRT; 
    public Material brushMaterial; 
    public Material compositeMaterial;
    [Header("Brush Settings")]
    [Range(1, 400)]public float brushSize = 50f;
    public Color brushColor = Color.black;
    [Range(0, 1)] public float opacity = 0.5f;
    [Header("Performance")]
    [Range(0.1f, 60f)]
    public float maxDrawsPerSecond = 3f;
    [Range(930.5f, 933f)]
    public float spacingFactor = 931.4f;


    private float secondsSinceLastDraw = 1000f;

    private Vector2 lastUV;
    private bool isDrawing = false;
    private enum DragState {None, Clicked, Dragging }
    private DragState dragState = DragState.None;


    void Start() {
        ClearCanvas();
        ClearInkLayer();
    }

    void Update() {
        //test();
        HandleInputs();
    }

    // Handle mouse input and drawing logic
    private void HandleInputs() {
        if (Input.GetMouseButton(0)) {
            dragState = dragState == DragState.None ? DragState.Clicked : DragState.Dragging;
            if (GetHitUV(out Vector2 currentUV)) {
                if (!isDrawing) {
                    lastUV = currentUV;
                    isDrawing = true;
                }

                DrawLine(lastUV, currentUV);

                

                lastUV = currentUV;

            }
            else {
                isDrawing = false;
            }
        }
        else {
            if(dragState != DragState.None) {
                ApplyInkToCanvas();
                ClearInkLayer();
            }
            dragState = DragState.None;
            isDrawing = false;
        }
    }

    private void test() {
        if (Input.GetMouseButton(0)) {
            // check if we hit the canvas
            if (GetHitUV(out Vector2 currentUV)) {
                // If we weren't already drawing, start a new stroke
                if (!isDrawing) {
                    lastUV = currentUV;
                    isDrawing = true;
                }

                DrawLine(lastUV, currentUV);

                ApplyInkToCanvas();

                lastUV = currentUV;
            }
        }
        else {
            isDrawing = false;
        }
    }

void ApplyInkToCanvas() {
        Graphics.Blit(inkLayerRT, canvasRT, compositeMaterial);
    }

    void DrawLine(Vector2 start, Vector2 end) {
        // Calculate stamps needed based on distance and brush size
        
        // Convert UV-space delta into pixel-space delta using both width and height.
        float dx = (end.x - start.x) * canvasRT.width;
        float dy = (end.y - start.y) * canvasRT.height;
        float pixelDistance = Mathf.Sqrt(dx * dx + dy * dy);

        Debug.Log(dragState.ToString());
        int steps = Mathf.Max(1, Mathf.CeilToInt(pixelDistance / (brushSize * spacingFactor/100000)));
        Debug.Log(steps);
        // Throttle when there's only a single stamp (short moves).
        if (dragState != DragState.Clicked && steps == 1) {
            if (secondsSinceLastDraw < 1f / maxDrawsPerSecond) {
                secondsSinceLastDraw += Time.deltaTime;
                return;
            }
            else {
                secondsSinceLastDraw = 0f;
            }
        }
        else {
            // A multi-stamp stroke is drawn immediately, reset timer so subsequent single-stamp moves are throttled.
            secondsSinceLastDraw = 0f;
        }
        RenderTexture.active = inkLayerRT;


        brushMaterial.SetColor("_Color", brushColor);
        brushMaterial.SetFloat("_Opacity", opacity);
        brushMaterial.SetPass(0); // Tell GPU to use this material

        //Setup Orthographic Space (0.0 to 1.0)
        GL.PushMatrix();
        GL.LoadOrtho();

        // Draw raw quads batched for speed
        GL.Begin(GL.QUADS);

        for (int i = 0; i <= steps; i++) {
            Vector2 uv = Vector2.Lerp(start, end, (float)i / steps);
            DrawStamp(uv);
        }

        GL.End();

        GL.PopMatrix();
        RenderTexture.active = null;
    }

    void DrawStamp(Vector2 uv) {
        // Convert pixel size to 0-1 UV space
        float halfX = (brushSize / canvasRT.width) / 2f;
        float halfY = (brushSize / canvasRT.height) / 2f;

        // Draw a square around the UV center
        // Bottom Left
        GL.TexCoord2(0, 0); GL.Vertex3(uv.x - halfX, uv.y - halfY, 0);
        // Top Left
        GL.TexCoord2(0, 1); GL.Vertex3(uv.x - halfX, uv.y + halfY, 0);
        // Top Right
        GL.TexCoord2(1, 1); GL.Vertex3(uv.x + halfX, uv.y + halfY, 0);
        // Bottom Right
        GL.TexCoord2(1, 0); GL.Vertex3(uv.x + halfX, uv.y - halfY, 0);
    }

    bool GetHitUV(out Vector2 uv) {
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit)) {
            uv = hit.textureCoord;
            return true;
        }
        uv = Vector2.zero;
        return false;
    }

    public void ClearCanvas() {
        RenderTexture.active = canvasRT;
        GL.Clear(true, true, Color.white);
        RenderTexture.active = null;
    }

    public void ClearInkLayer() {
        RenderTexture.active = inkLayerRT;
        GL.Clear(true, true, Color.clear);
        RenderTexture.active = null;
    }
}