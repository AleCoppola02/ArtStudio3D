using JetBrains.Annotations;
using NUnit;
using Unity.VisualScripting;
using Unity.VisualScripting.Antlr3.Runtime.Misc;
using UnityEngine;
using UnityEngine.Rendering;
using static PencilPainter;

public class Brush : MonoBehaviour
{

    public Material brushMaterial;
    public Material inkLayerMaterial;
    [Header("Brush Settings")]
    [Range(1, 400)] private float brushSize = 50f;
    private Color brushColor = Color.black;
    [Range(0.1f, 1f)] private float flow = 1f;
    [Header("Performance")]
    [Range(0.1f, 60f)]
    public float maxDrawsPerSecond = 3f;
    //[Range(930.5f, 933f)]
    [Range(0.01f, 1f)]
    public float spacingFactor = 0.05f;

    [Header("Connections")]
    public RenderTexture canvasRT;
    private float secondsSinceLastDraw = 1000f;

    private float distanceSinceLastDraw = 0f;

    public InkLayer inkLayer;
    private RenderTexture inkLayerRT;

    public CanvasManager canvas;

    void Start() {
        //ClearCanvas();
        inkLayerRT = inkLayer.GetInkLayerRT();
    }

    // Calculate stamps needed based on distance and brush size
    //to do: revamp this so that brush strokes are always drawn at the same distance from each other.
    public int CalculateSteps(Vector2 start, Vector2 end) {
        // Convert UV-space delta into pixel-space delta using both width and height.
        float dx = (end.x - start.x) * canvasRT.width;
        float dy = (end.y - start.y) * canvasRT.height;
        float pixelDistance = Mathf.Sqrt(dx * dx + dy * dy);
        return Mathf.Max(1, Mathf.CeilToInt(pixelDistance / (brushSize * spacingFactor / 100000)));
    }

    public void UseBrush(Vector2 start, Vector2 end, DragState dragState) {
        //int steps = CalculateSteps(start, end);

        /*
        //This if block calculates whether to draw or not, based on how long it's been since the last stamp was drawn
        if(dragState == DragState.Clicked) {
            secondsSinceLastDraw = 0f;
        }
        else if (dragState == DragState.Dragging && steps == 1) {
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
        }*/
        float brushSpacing = brushSize * spacingFactor; // i.e. 5% of brush size, so a 100px brush would draw a stamp every 5px.   

        if (dragState == DragState.Clicked) {
            distanceSinceLastDraw = brushSpacing; // Force a stamp to be drawn on click, even if the user doesn't move the mouse.
            DrawLine(start, end, 1);
            distanceSinceLastDraw = 0f;
        }
        else {
            // Convert UV-space delta into pixel-space delta using both width and height.
            float dx = (end.x - start.x) * canvasRT.width;
            float dy = (end.y - start.y) * canvasRT.height;
            float pixelDistance = Mathf.Sqrt(dx * dx + dy * dy);
            distanceSinceLastDraw += pixelDistance;
            
            if (distanceSinceLastDraw < (brushSpacing)) {
                return;
            }
            int steps = distanceSinceLastDraw / (brushSpacing) < 1 ? 1 : Mathf.CeilToInt(distanceSinceLastDraw / brushSpacing);
            DrawLine(start, end, steps);
            //DrawLine(steps, start, end);
        }
    }

    private void DrawLine(Vector2 start, Vector2 end, int steps) {
        brushMaterial.SetPass(0); // Tell GPU to use this material
        RenderTexture.active = inkLayerRT;

        //Setup Orthographic Space (0.0 to 1.0)
        GL.PushMatrix();
        GL.LoadOrtho();
        // Draw raw quads batched for speed
        GL.Begin(GL.QUADS);

        for (; distanceSinceLastDraw >= (brushSize*spacingFactor); distanceSinceLastDraw -= (brushSize*spacingFactor)) {
            Vector2 uv = Vector2.Lerp(start, end, (float)distanceSinceLastDraw / steps);
            DrawStamp(uv);
        }

        GL.End();
        GL.PopMatrix();
        RenderTexture.active = null;
    }

    private void DrawLine(int steps, Vector2 start, Vector2 end) {
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


    public void SetBrushColor(Color brushColor) {
        this.brushColor = brushColor;
        brushMaterial.SetColor("_Color", brushColor);
    }

     public void SetBrushSize(float brushSize) {
        this.brushSize = brushSize;
    }
    public void SetBrushFlow(float flow) {
        this.flow = flow;
        brushMaterial.SetFloat("_Flow", flow);
    }
}
