using UnityEngine;
using System.Collections.Generic;

public class PhysicalAtlas : System.IDisposable
{
    // The massive VRAM canvas
    public RenderTexture Texture { get; private set; }
    public int TileSize { get; private set; }
    public int AtlasSize { get; private set; }

    // The queue of empty slots in the Atlas, represented as (x,y) coordinates of the tile grid
    private Queue<Vector2Int> freeSlots;

    public PhysicalAtlas(int atlasSize = 4096, int tileSize = 256) {
        AtlasSize = atlasSize;
        TileSize = tileSize;

        // 1. Create the RenderTexture
        // ARGB32 gives us standard color channels.
        Texture = new RenderTexture(AtlasSize, AtlasSize, 0, RenderTextureFormat.ARGB32);

        // CRITICAL: If you are drawing onto this with Compute Shaders or Pixel Shaders, 
        // you often need this flag enabled so the GPU can write to it directly.
        Texture.enableRandomWrite = true;

        // Point filtering prevents tiles from bleeding into each other at the edges
        Texture.filterMode = FilterMode.Point;
        Texture.Create();

        // 2. Initialize the Parking Lot tracking
        int slotsAcross = AtlasSize / TileSize;
        freeSlots = new Queue<Vector2Int>();

        // Fill the queue with every available slot coordinate (e.g., (0,0) to (15,15))
        for (int y = 0; y < slotsAcross; y++) {
            for (int x = 0; x < slotsAcross; x++) {
                freeSlots.Enqueue(new Vector2Int(x, y));
            }
        }
    }

    // Call this when the CPU needs to load a tile into VRAM
    public Vector2Int? AllocateSlot() {
        if (freeSlots.Count > 0) {
            return freeSlots.Dequeue(); // Hand out an empty slot
        }

        // If the queue is empty, the VRAM Atlas is 100% full!
        return null;
    }

    // Call this when a tile is evicted from VRAM to make room
    public void FreeSlot(Vector2Int slot) {
        freeSlots.Enqueue(slot); // Put the slot back in the pool
    }

    // Inside PhysicalAtlas.cs
public void Dispose()
{
    if (Texture != null) {
        Texture.Release();
        Object.Destroy(Texture); // Use UnityEngine.Object
    }
}
}