using UnityEngine;

public class CanvasTile
{
    public Vector2Int gridPosition; // e.g., Tile (0,0) is bottom-left, Tile (1,0) is next to it
    public Rect pixelBounds;        // The physical coordinates on the canvas (e.g., x:256, y:0, w:256, h:256)
    private Color canvasColor = new Vector4(1, 1, 1, 1);

    //This is null until the user draws here
    public RenderTexture texture;

    public bool isDirty = false;    // Did the user draw here this frame? If so, we need to composite it.

    public void AllocateTexture(int tileSize) {
        if (texture == null) {
            // Only create the 256x256 texture when absolutely needed
            texture = new RenderTexture(tileSize, tileSize, 0, RenderTextureFormat.ARGB32);
            texture.Create();

            // Clear it to transparent
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = texture;
            GL.Clear(true, true, canvasColor);
            RenderTexture.active = active;
        }
    }
}