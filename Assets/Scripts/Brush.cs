using UnityEngine;
using static PencilPainter;

public class Brush : MonoBehaviour
{
    public Material brushMaterial;
    public Material inkLayerMaterial;
    [Header("Brush Settings")]
    [Range(1, 400)] private float brushSize = 50f;
    private Color brushColor = Color.black;
    [Range(0f, 1f)] private float opacity = 0.5f;
    [Range(0.1f, 1f)] private float flow = 1f;
    [Header("Performance")]
    [Range(0.1f, 60f)]
    public float maxDrawsPerSecond = 3f;
    [Range(930.5f, 933f)]
    public float spacingFactor = 931.4f;

    [Header("Connections")]
    public RenderTexture canvasRT;
    public RenderTexture inkLayerRT;
    private RenderTexture tempRT; // For compositing
    private float secondsSinceLastDraw = 1000f;
    private Color canvasColor = new Vector4(1, 1, 1, 0);
    private Color inkLayerColor = new Vector4(1, 1, 1, 0);

    void Start() {
        ClearCanvas();
        ClearInkLayer();
        inkLayerMaterial.SetTexture("_CanvasTex", canvasRT);
        createTempRT();
    }


    public void DrawLine(Vector2 start, Vector2 end, DragState dragState) {
        // Calculate stamps needed based on distance and brush size

        // Convert UV-space delta into pixel-space delta using both width and height.
        float dx = (end.x - start.x) * canvasRT.width;
        float dy = (end.y - start.y) * canvasRT.height;
        float pixelDistance = Mathf.Sqrt(dx * dx + dy * dy);

        
        int steps = Mathf.Max(1, Mathf.CeilToInt(pixelDistance / (brushSize * spacingFactor / 100000)));
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


        inkLayerMaterial.SetFloat("_Opacity", opacity);
        brushMaterial.SetColor("_Color", brushColor);
        brushMaterial.SetFloat("_Flow", flow);
        brushMaterial.SetPass(0); // Tell GPU to use this material
        RenderTexture.active = inkLayerRT;

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

    public void ApplyInkToCanvas() {
        Graphics.Blit(canvasRT, tempRT); // Copy current canvas to temp
        inkLayerMaterial.SetTexture("_CanvasTex", tempRT); // Set temp as input for compositing
        Graphics.Blit(inkLayerRT, canvasRT, inkLayerMaterial); // Composite ink layer onto canvas using inkLayerMaterial
        ClearInkLayer(); // Clear ink layer for next stroke
    }

    public void ClearCanvas() {
        RenderTexture.active = canvasRT;
        GL.Clear(true, true, canvasColor);
        RenderTexture.active = null;
    }

    public void ClearInkLayer() {
        RenderTexture.active = inkLayerRT;
        GL.Clear(true, true, inkLayerColor);
        RenderTexture.active = null;
    }

    private void createTempRT() {
        RenderTextureDescriptor desc = canvasRT.descriptor;
        tempRT = new RenderTexture(desc);
        tempRT.filterMode = canvasRT.filterMode;
        tempRT.wrapMode = canvasRT.wrapMode;
        tempRT.anisoLevel = canvasRT.anisoLevel;
        tempRT.Create();
    }

    public void SetBrushColor(Color brushColor) {
        this.brushColor = brushColor;
    }

     public void SetBrushSize(float brushSize) {
        this.brushSize = brushSize;
    }
    public void SetBrushFlow(float flow) {
        this.flow = flow;
    }
    public void SetBrushOpacity(float opacity) {
        this.opacity = opacity;
    }
}
