using UnityEngine;

public class TileState
{
    public Vector3Int Address; // ADD THIS: So the tile knows where it lives

    public bool IsLoaded = false; // True if the tile is currently loaded in VRAM and can be drawn on
    public bool IsDirty = false; // True if the user drew on it and it needs saving
    public Vector2Int PhysicalSlot;

    // CRITICAL for memory management: tracking when it was last seen
    public ulong LastAccessTime;

    public TileState() {
        IsLoaded = false;
        IsDirty = false;
    }
}