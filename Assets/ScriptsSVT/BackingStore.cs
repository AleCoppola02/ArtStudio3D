using System.Collections.Generic;
using System.IO;
using UnityEngine;

// 1. THE NEW STROKE BUFFER CLASS
public class StrokeBuffer
{
    public int documentLayerID; // The permanent SVT layer we are drawing onto
    public int strokeID;        // The temporary UI layer ID
    
    public BlendModeConfig blendMode;
    public float brushSize; 
    public float opacity;       // NEW: Track stroke opacity
    public float flow;          // NEW: Track brush flow

    public Queue<Vector2> stampQueue = new Queue<Vector2>();
    public bool isStrokeFinished = false;

    public StrokeBuffer(int docLayerID, int strkID, BlendModeConfig mode, float size, float op, float fl) {
        documentLayerID = docLayerID;
        strokeID = strkID;
        blendMode = mode;
        brushSize = size;
        opacity = op;
        flow = fl;
    }
}

public class BackingStore
{
    // ==========================================
    // THE HANDOFF SYSTEM
    // ==========================================
    
    // InkLayerManager will subscribe to this to know when to clear the UI
    public event System.Action<int> OnLayerBakingFinished; // Now passes the temporary StrokeID!

    // --- CORE COMPONENTS ---
    private GhostCanvas ghostCanvas; 
    private PhysicalAtlas atlas;
    private IndirectionTable[] indirectionTables;

    // --- TILE TRACKING ---
    private Dictionary<Vector3Int, TileState> tileDatabase = new Dictionary<Vector3Int, TileState>();
    private List<TileState> activeTiles = new List<TileState>();
    private ulong accessCounter = 0;
    private string saveDirectory;

    // --- BAKING QUEUE ---
    private Queue<StrokeBuffer> pendingStrokes = new Queue<StrokeBuffer>(); 
    private StrokeBuffer receivingStroke = null;  

    public BackingStore(PhysicalAtlas atlas, IndirectionTable[] indirectionTables, GhostCanvas ghostCanvas, string saveDirectory) {
        this.atlas = atlas;
        this.indirectionTables = indirectionTables;
        this.ghostCanvas = ghostCanvas;
        this.saveDirectory = saveDirectory;
    }

    // ==========================================
    // 1. THE BAKING PIPELINE (Called by BrushManager)
    // ==========================================
    
    public void EnqueueStamps(int documentLayerID, int strokeID, BlendModeConfig blendMode, float brushSize, float opacity, float flow, List<Vector2> newStamps) {
        // If there is no active stroke OR the IDs don't match OR the last one finished, make a new buffer!
        if (receivingStroke == null || receivingStroke.strokeID != strokeID || receivingStroke.isStrokeFinished) {
            receivingStroke = new StrokeBuffer(documentLayerID, strokeID, blendMode, brushSize, opacity, flow);
            pendingStrokes.Enqueue(receivingStroke);
        }

        // Add the streamed real-time stamps to the active writing buffer
        foreach (var stamp in newStamps) {
            receivingStroke.stampQueue.Enqueue(stamp); 
        }
    }

    public void EndStroke(int strokeID) {
        // Mark the currently writing stroke as finished.
        // BrushManager tells us when the user lifted the pen!
        if (receivingStroke != null && receivingStroke.strokeID == strokeID) {
            receivingStroke.isStrokeFinished = true;
        }
    }

    private void RefreshVisibleTiles(HashSet<Vector3Int> modifiedTiles) {
        foreach (TileState tile in activeTiles) {
            if (modifiedTiles.Contains(tile.Address)) { 
                byte[] latestData = ghostCanvas.TryGetTileAsync(tile.Address);
                if (latestData != null) UploadToAtlasSlot(latestData, tile.PhysicalSlot);
            }
        }
    }

    // ==========================================
    // 2. THE TILE ROUTER (Called by CameraTileRequester)
    // ==========================================

    public void RequestTile(int x, int y, int zoomLevel) {
        Vector3Int address = new Vector3Int(x, y, zoomLevel);

        if (!tileDatabase.ContainsKey(address)) {
            tileDatabase[address] = new TileState { Address = address, IsLoaded = false };
        }

        TileState tile = tileDatabase[address];
        tile.LastAccessTime = accessCounter++;

        // If it's already in VRAM, do nothing!
        if (tile.IsLoaded) return;

        // 1. Get an empty VRAM slot
        Vector2Int? slot = atlas.AllocateSlot();
        if (slot == null) {
            EvictOldestTile();
            slot = atlas.AllocateSlot();
        }

        // 2. THE FALLBACK LOOKUP
        // GhostCanvas handles both RAM and Disk lookups (and decompression) for us!
        byte[] pixelData = ghostCanvas.TryGetTileAsync(address);


        // 3. Upload to Physical Atlas
        if (pixelData != null) {
            UploadToAtlasSlot(pixelData, slot.Value);
        }
        else {
            UploadBlankToAtlasSlot(slot.Value); // Completely empty area
        }

        // 4. Update the Indirection Table
        tile.PhysicalSlot = slot.Value;
        tile.IsLoaded = true;
        activeTiles.Add(tile);

        indirectionTables[zoomLevel].SetTileMapping(x, y, slot.Value.x, slot.Value.y);
    }

    // ==========================================
    // 3. CLEAN EVICTION (No saving needed!)
    // ==========================================

    private void EvictOldestTile() {
        if (activeTiles.Count == 0) return;

        TileState oldestTile = activeTiles[0];
        for (int i = 1; i < activeTiles.Count; i++) {
            if (activeTiles[i].LastAccessTime < oldestTile.LastAccessTime) {
                oldestTile = activeTiles[i];
            }
        }

        // 1. Just clear the pointer! GhostCanvas/Disk holds the real data now.
        indirectionTables[oldestTile.Address.z].ClearTileMapping(oldestTile.Address.x, oldestTile.Address.y);

        // 2. Free up VRAM
        atlas.FreeSlot(oldestTile.PhysicalSlot);
        oldestTile.IsLoaded = false;
        activeTiles.Remove(oldestTile);
        // 3. Free up System RAM!
        ghostCanvas.UnloadFromRAM(oldestTile.Address);

    }

    // ==========================================
    // 4. DISK & UTILS
    // ==========================================



    // We will define this helper later. It just converts byte[] to a Temp Texture2D 
    // and uses Graphics.CopyTexture to put it in the correct atlas slot.
    private void UploadToAtlasSlot(byte[] data, Vector2Int slot) {
        ghostCanvas.UploadBytesToVRAMSlot(data, slot, atlas);
    }

    private void UploadBlankToAtlasSlot(Vector2Int slot) {
        // Just clear that specific block of the RenderTexture
        ghostCanvas.ClearVRAMSlot(slot, atlas);
    }

    // Allow GhostCanvas to look at the current stroke without removing it
    public StrokeBuffer PeekNextStroke() {
        if (pendingStrokes.Count > 0) {
            return pendingStrokes.Peek();
        }
        return null;
    }

    // Allow GhostCanvas to tell BackingStore when a stroke is fully processed
    public void FinishCurrentStroke() {
        if (pendingStrokes.Count > 0) {
            pendingStrokes.Dequeue();
        }
    }

    // GhostCanvas will call this when a chunk finishes baking!
    public void OnChunkBaked(HashSet<Vector3Int> modifiedTiles, int layerID, bool isStrokeCompletelyDone) {
        RefreshVisibleTiles(modifiedTiles);

        if (isStrokeCompletelyDone) {
            OnLayerBakingFinished?.Invoke(layerID);
        }
    }

    public void SyncGPU() {
        for (int i = 0; i < indirectionTables.Length; i++) {
            indirectionTables[i].ApplyChanges();
        }
    }
}