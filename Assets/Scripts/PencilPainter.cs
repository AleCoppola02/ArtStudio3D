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

    private Vector2 lastUV;
    private bool isDrawing = false;

    void Start() {
        ClearCanvas();
        ClearInkLayer();
    }

    void Update() {
        if (Input.GetMouseButton(0)) {
            if (GetHitUV(out Vector2 currentUV)) {
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
        RenderTexture.active = canvasRT;
        GL.Clear(true, true, Color.white);
        RenderTexture.active = null;

        Graphics.Blit(inkLayerRT, canvasRT, compositeMaterial);
    }

    void DrawLine(Vector2 start, Vector2 end) {
        // Calculate stamps needed based on distance and brush size
        float distance = Vector2.Distance(start, end);
        int steps = Mathf.Max(1, Mathf.CeilToInt(distance * canvasRT.width / (brushSize * 0.2f)));

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
        GL.Clear(true, true, Color.white); // Clear to white
        RenderTexture.active = null;
    }
}