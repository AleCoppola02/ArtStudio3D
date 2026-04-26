using System.Collections.Generic;
using System.IO;
using UnityEngine;

// 1. THE NEW STROKE BUFFER CLASS
// Holds a queue of stamps for a specific ink layer until they are baked.
public class StrokeBuffer
{
    public int inkLayerID;
    public BlendModeConfig blendMode;
    public float brushSize; // NEW: Track the size!
    public Queue<Vector2> stampQueue = new Queue<Vector2>();
    public bool isStrokeFinished = false;

    public StrokeBuffer(int layerID, BlendModeConfig mode, float size) {
        inkLayerID = layerID;
        blendMode = mode;
        brushSize = size;
    }
}

public class BackingStore
{
    // ==========================================
    // THE HANDOFF SYSTEM
    // ==========================================
    
    // Your InkLayerManager will subscribe to this event to know when to clear the UI
    public event System.Action<int> OnLayerBakingFinished;


    private HashSet<Vector3Int> currentlyBakingTiles = new HashSet<Vector3Int>();
    // --- CORE COMPONENTS ---
    private GhostCanvas ghostCanvas; // The new VRAM Crucible & RAM Manager
    private PhysicalAtlas atlas;
    private IndirectionTable[] indirectionTables;

    // --- TILE TRACKING ---
    private Dictionary<Vector3Int, TileState> tileDatabase = new Dictionary<Vector3Int, TileState>();
    private List<TileState> activeTiles = new List<TileState>();
    private ulong accessCounter = 0;
    private string saveDirectory;

    // --- BAKING QUEUE ---
    private Queue<StrokeBuffer> pendingStrokes = new Queue<StrokeBuffer>(); // Each item is a full stroke's worth of stamps for a specific ink layer, waiting to be baked.
    private StrokeBuffer processingStroke = null; // Used by ProcessBakingQueue (Reader)
    private StrokeBuffer receivingStroke = null;  // Used by EnqueueStamps (Writer)
    private int stampsPerFrame = 500; // Engine processing budget

    public BackingStore(PhysicalAtlas atlas, IndirectionTable[] indirectionTables, GhostCanvas ghostCanvas, string saveDirectory) {
        this.atlas = atlas;
        this.indirectionTables = indirectionTables;
        this.ghostCanvas = ghostCanvas;
        this.saveDirectory = saveDirectory;
    }

    // ==========================================
    // 1. THE BAKING PIPELINE (Called by BrushManager)
    // ==========================================
    public void EnqueueStamps(int layerID, BlendModeConfig blendMode, float brushSize, List<Vector2> newStamps) {
        if (receivingStroke == null || receivingStroke.inkLayerID != layerID || receivingStroke.isStrokeFinished) {
            receivingStroke = new StrokeBuffer(layerID, blendMode, brushSize); // Pass it in!
            pendingStrokes.Enqueue(receivingStroke);
        }

        // 2. Add the new stamps to the active writing buffer
        foreach (var stamp in newStamps) {
            receivingStroke.stampQueue.Enqueue(stamp); 
        }
    }

    public void EndStroke(int layerID) {
        // Mark the currently writing stroke as finished so the next brush touch creates a new queue item
        if (receivingStroke != null && receivingStroke.inkLayerID == layerID) {
            receivingStroke.isStrokeFinished = true;
        }
    }

    // Call this from a MonoBehaviour Update() or CanvasManager
    public void ProcessBakingQueue() {
        // 1. Get the next stroke to process
        if (processingStroke == null) {
            if (pendingStrokes.Count > 0) processingStroke = pendingStrokes.Dequeue();
            else return; // Completely idle! 
        }

        // 2. Pop our budget of stamps
        List<Vector2> batchToBake = new List<Vector2>();
        int count = 0;

        while (processingStroke.stampQueue.Count > 0 && count < stampsPerFrame) {
            Vector2 stamp = processingStroke.stampQueue.Dequeue();
            batchToBake.Add(stamp);
            // Track roughly which tiles are being touched (assuming LOD 0)
            currentlyBakingTiles.Add(ghostCanvas.WorldToTileAddress(stamp));
            count++;
        }

        // 3. Send to the Crucible!
        if (batchToBake.Count > 0) {
            ghostCanvas.BakeStamps(batchToBake, processingStroke.inkLayerID, processingStroke.blendMode, processingStroke.brushSize);
        }

        // 4. Is this stroke completely done sending stamps to the GPU?
        if (processingStroke.stampQueue.Count == 0 && processingStroke.isStrokeFinished) {
            int finishedLayerID = processingStroke.inkLayerID;
            HashSet<Vector3Int> tilesToRefresh = new HashSet<Vector3Int>(currentlyBakingTiles); // Copy the list
            currentlyBakingTiles.Clear();

            ghostCanvas.FinalizeLayer(finishedLayerID, () => {
                RefreshVisibleTiles(tilesToRefresh); // ONLY refresh these!
                OnLayerBakingFinished?.Invoke(finishedLayerID);
            });
            processingStroke = null;
        }
    }
    private void RefreshVisibleTiles(HashSet<Vector3Int> modifiedTiles) {
        foreach (TileState tile in activeTiles) {
            if (modifiedTiles.Contains(tile.Address)) { // <--- THE CRITICAL CHECK
                byte[] latestData = ghostCanvas.TryGetTile(tile.Address);
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
        // Ask GhostCanvas first (RAM), fallback to Disk
        byte[] pixelData = ghostCanvas.TryGetTile(address);
        if (pixelData == null) {
            pixelData = LoadFromDisk(address);
        }

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

    private byte[] LoadFromDisk(Vector3Int address) {
        string path = Path.Combine(saveDirectory, $"Tile_{address.z}_{address.x}_{address.y}.dat");
        if (File.Exists(path)) {
            return File.ReadAllBytes(path);
        }
        return null;
    }

    // We will define this helper later. It just converts byte[] to a Temp Texture2D 
    // and uses Graphics.CopyTexture to put it in the correct atlas slot.
    private void UploadToAtlasSlot(byte[] data, Vector2Int slot) {
        ghostCanvas.UploadBytesToVRAMSlot(data, slot, atlas);
    }

    private void UploadBlankToAtlasSlot(Vector2Int slot) {
        // Just clear that specific block of the RenderTexture
        ghostCanvas.ClearVRAMSlot(slot, atlas);
    }
}