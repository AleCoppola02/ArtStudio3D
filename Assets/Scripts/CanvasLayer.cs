using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class CanvasLayer
{
    public string layerName;
    public float opacity = 1.0f;
    public Material blendMaterial;

    // The Dictionary provides instant lookup for our Brush Manager
    public Dictionary<Vector2Int, CanvasTile> tiles = new Dictionary<Vector2Int, CanvasTile>();
    private int tileSize;

    public CanvasLayer(string name, int tileSize, Material defaultBlend) {
        this.layerName = name;
        this.tileSize = tileSize;

        // THE FIX: Create a unique instance of the material for this specific layer!
        this.blendMaterial = new Material(defaultBlend);
    }

    // --- THE MISSING FUNCTION ---
    // The BrushManager calls this to find a tile. If it doesn't exist, it makes one!
    public CanvasTile GetOrCreateTile(Vector2Int gridPos) {
        if (!tiles.TryGetValue(gridPos, out CanvasTile tile)) {
            tile = new CanvasTile();
            tile.gridPosition = gridPos;
            // Calculate exactly where this tile sits on the master canvas
            tile.pixelBounds = new Rect(gridPos.x * tileSize, gridPos.y * tileSize, tileSize, tileSize);

            tiles.Add(gridPos, tile);
        }
        return tile;
    }
}