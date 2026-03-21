using JetBrains.Annotations;
using NUnit;
using System.Collections.Generic;
using Unity.VisualScripting;
using Unity.VisualScripting.Antlr3.Runtime.Misc;
using UnityEngine;
using UnityEngine.Rendering;
using static PencilPainter;

public class Brush : MonoBehaviour
{



    private List<Vector2> pointBuffer = new List<Vector2>(); // Stores our 4 spline points

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
    private float brushSpacing;
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



    /*public void UseBrush(Vector2 start, Vector2 end, DragState dragState) {
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

            //DrawLine returns the leftover distance that wasn't enough to draw a full stamp, which is used to continue accumulating distance for the next draw.
            distanceSinceLastDraw =  DrawLine(start, end, steps);
        }
    }

    private float DrawLine(Vector2 start, Vector2 end, int steps) {
        brushMaterial.SetPass(0); // Tell GPU to use this material
        RenderTexture.active = inkLayerRT;

        //Setup Orthographic Space (0.0 to 1.0)
        GL.PushMatrix();
        GL.LoadOrtho();
        // Draw raw quads batched for speed
        GL.Begin(GL.QUADS);

        float distanceLeftover = (distanceSinceLastDraw % brushSpacing);
        float distanceToCover = distanceSinceLastDraw - distanceLeftover;
        Vector2 movementStart = start;
        movementStart.x += brushSpacing / canvasRT.width;
        movementStart.y += brushSpacing / canvasRT.height;
        for (; distanceToCover >= brushSpacing; distanceToCover -= brushSpacing) {
            Vector2 uv = Vector2.Lerp(end, movementStart, distanceToCover / (steps * brushSpacing));
            DrawStamp(uv);
        }

        GL.End();
        GL.PopMatrix();
        RenderTexture.active = null;

        return distanceLeftover;
    }*/


    public void UseBrush(Vector2 start, Vector2 end, DragState dragState) {
        if (dragState == DragState.Clicked) {
            // 1. Reset everything for a brand new stroke
            distanceSinceLastDraw = 0f;
            pointBuffer.Clear();

            // 2. Duplicate the start point twice. 
            // This gives us our P0 (Past) and P1 (Start) so the curve can begin immediately.
            pointBuffer.Add(start);
            pointBuffer.Add(start);

            // 3. Force a single stamp exactly at the click position
            DrawSingleStamp(start);
        }
        else if (dragState == DragState.Dragging) {
            // Calculate the physical distance the mouse moved this frame
            float dx = (end.x - start.x) * canvasRT.width;
            float dy = (end.y - start.y) * canvasRT.height;
            float pixelDistance = Mathf.Sqrt(dx * dx + dy * dy);

            // Early exit: If the mouse barely moved, don't record it to avoid overlapping points
            if (pixelDistance <= 0.0001f) {
                return;
            }

            // Add the new mouse position to our rolling buffer
            pointBuffer.Add(end);

            // Once we have 4 points, we have enough data to draw a curved segment!
            if (pointBuffer.Count >= 4) {
                // Draw the curve specifically between point 1 and point 2
                DrawCurveSegment(pointBuffer[0], pointBuffer[1], pointBuffer[2], pointBuffer[3]);

                // Remove the oldest point (Index 0). 
                // This shifts the buffer down so the next frame can add a new point.
                pointBuffer.RemoveAt(0);
            }
        }
        else if (dragState == DragState.Paused) {
            // The mouse stopped moving.
            // We set up the new curve anchors perfectly, but we DO NOT draw a stamp
            pointBuffer.Clear();
            pointBuffer.Add(end);
            pointBuffer.Add(end);
        }
        else if (dragState == DragState.Released) {
            // The user let go of the mouse. 
            // Do nothing visually, just clear the buffer so it's ready for the next stroke.
            pointBuffer.Clear();

            // It's also good practice to zero this out here, just to be safe!
            distanceSinceLastDraw = 0f;
        }
    }

    private void DrawCurveSegment(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3) {
        // We estimate the length of the curve using the linear distance between p1 and p2. 
        // Because mouse events fire so quickly, this linear distance is 99% accurate to the curve's true length.
        float dx = (p2.x - p1.x) * canvasRT.width;
        float dy = (p2.y - p1.y) * canvasRT.height;
        float segmentPixelLength = Mathf.Sqrt(dx * dx + dy * dy);

        brushMaterial.SetPass(0);
        RenderTexture.active = inkLayerRT;
        GL.PushMatrix();
        GL.LoadOrtho();
        GL.Begin(GL.QUADS);

        // Calculate how far into THIS segment we need to go to drop the first stamp
        float distanceIntoSegment = brushSpacing - distanceSinceLastDraw;

        // Keep dropping stamps as long as they fit on this specific curved segment
        while (distanceIntoSegment <= segmentPixelLength) {

            // Calculate the percentage (0.0 to 1.0) along the segment
            float t = distanceIntoSegment / segmentPixelLength;

            // THE MAGIC: Use the Spline math instead of Lerp!
            Vector2 uv = GetCatmullRomPosition(t, p0, p1, p2, p3);
            DrawStamp(uv);

            // Step forward by exactly one brush spacing
            distanceIntoSegment += brushSpacing;
        }

        // Save whatever distance is left over for the NEXT frame
        distanceSinceLastDraw = (distanceSinceLastDraw + segmentPixelLength) % brushSpacing;

        GL.End();
        GL.PopMatrix();
        RenderTexture.active = null;
    }

    // Helper to draw the initial dot on click, keeping the GL code clean
    private void DrawSingleStamp(Vector2 position) {
        brushMaterial.SetPass(0);
        RenderTexture.active = inkLayerRT;
        GL.PushMatrix();
        GL.LoadOrtho();
        GL.Begin(GL.QUADS);

        DrawStamp(position);

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
        brushSpacing = brushSize * spacingFactor; // i.e. 5% of brush size, so a 100px brush would draw a stamp every 5px.   
    }
    public void SetBrushFlow(float flow) {
        this.flow = flow;
        brushMaterial.SetFloat("_Flow", flow);
    }


    private Vector2 GetCatmullRomPosition(float t, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3) {
        // Calculate the t-squared and t-cubed values
        float t2 = t * t;
        float t3 = t2 * t;

        // The Catmull-Rom polynomial math
        Vector2 a = 2f * p1;
        Vector2 b = p2 - p0;
        Vector2 c = 2f * p0 - 5f * p1 + 4f * p2 - p3;
        Vector2 d = -p0 + 3f * p1 - 3f * p2 + p3;

        return 0.5f * (a + (b * t) + (c * t2) + (d * t3));
    }
}
