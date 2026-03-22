using System.Collections.Generic;
using UnityEngine;

public class CanvasManager : MonoBehaviour
{
    public RenderTexture canvasRT;
    private Color canvasColor = new Vector4(1, 1, 1, 1);


    // Using a dictionary to look up tiles using their grid coordinates
    public Dictionary<Vector2Int, CanvasTile> tiles = new Dictionary<Vector2Int, CanvasTile>();

    public int tileSize = 256; //tile size in pixels

    public CanvasTile GetOrCreateTile(Vector2Int gridPos) {
        if (!tiles.ContainsKey(gridPos)) {
            CanvasTile newTile = new CanvasTile();
            newTile.gridPosition = gridPos;
            newTile.pixelBounds = new Rect(gridPos.x * tileSize, gridPos.y * tileSize, tileSize, tileSize);
            tiles.Add(gridPos, newTile);
        }
        return tiles[gridPos];
    }

    public RenderTexture GetCanvasRT() {
        return canvasRT;
    }

    private void Start() {
        ClearCanvas();
    }

    public void ClearCanvas() {
        RenderTexture.active = canvasRT;
        GL.Clear(true, true, canvasColor);
        RenderTexture.active = null;
    }




}
