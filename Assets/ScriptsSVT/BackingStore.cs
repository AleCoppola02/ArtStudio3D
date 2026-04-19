using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using UnityEngine.Rendering;
using Unity.Collections;

public class BackingStore
{
    // --- DISK I/O VARIABLES ---
    private Texture2D blankTile;
    private string saveDirectory;

    // The Master Dictionary
    // Key: Vector3Int (X, Y, Zoom Level)
    // Value: The tile's current status
    private Dictionary<Vector3Int, TileState> tileDatabase;

    // References to our GPU components
    private PhysicalAtlas atlas;
    private IndirectionTable[] indirectionTables; // One for each zoom level

    // A list to track which tiles are currently active (loaded in VRAM) for quick access during eviction
    private List<TileState> activeTiles;

    // Tracks tile access order for eviction purposes. Incremented on each tile request.
    private ulong accessCounter = 0;

    // ASYNC MIPMAP VARIABLES ---
    private MonoBehaviour context; // Borrows CanvasManager's ability to run Coroutines
    private Queue<Vector3Int> mipmapQueue;
    private bool isWorkerRunning = false;

    public BackingStore(MonoBehaviour coroutineContext, PhysicalAtlas physicalAtlas, IndirectionTable[] tables) {
        // We need the MonoBehaviour context to run Coroutines for our async mipmap generation
        context = coroutineContext;
        mipmapQueue = new Queue<Vector3Int>();

        tileDatabase = new Dictionary<Vector3Int, TileState>();
        activeTiles = new List<TileState>();
        atlas = physicalAtlas;
        indirectionTables = tables;

        // 1. Setup the Blank Tile for "Cleaning" slots
        blankTile = new Texture2D(atlas.TileSize, atlas.TileSize, TextureFormat.RGBA32, false);
        Color32[] clearColors = new Color32[atlas.TileSize * atlas.TileSize];
        for (int i = 0; i < clearColors.Length; i++) {
            clearColors[i] = new Color32(255, 255, 255, 255); // White canvas
        }
        blankTile.SetPixels32(clearColors);
        blankTile.Apply();

        // 2. Setup the Save Directory on your Hard Drive
        saveDirectory = Path.Combine(Application.persistentDataPath, "SVT_Saves");
        if (!Directory.Exists(saveDirectory)) {
            Directory.CreateDirectory(saveDirectory);
        }

        ClearAllSavedTiles();
    }
    public void RequestTile(int x, int y, int zoomLevel, bool forceAllocate = false) {
        Vector3Int tileAddress = new Vector3Int(x, y, zoomLevel);

        if (!tileDatabase.ContainsKey(tileAddress)) {
            tileDatabase.Add(tileAddress, new TileState { Address = tileAddress });
        }

        TileState tile = tileDatabase[tileAddress];
        tile.LastAccessTime = ++accessCounter;

        if (!tile.IsLoaded) {
            string path = GetTilePath(tileAddress);
            bool fileExists = File.Exists(path);

            // ==========================================
            // TRUE SPARSE LOGIC
            // ==========================================
            // If the tile is totally blank (no PNG file) AND we aren't 
            // actively drawing on it right now... ABORT! Do not waste VRAM!
            if (!fileExists && !forceAllocate) {
                return;
            }
            // ==========================================

            Vector2Int? newSlot = atlas.AllocateSlot();

            if (newSlot == null) {
                EvictOldestTile();
                newSlot = atlas.AllocateSlot();
            }

            if (newSlot != null) {
                tile.PhysicalSlot = newSlot.Value;
                tile.IsLoaded = true;
                activeTiles.Add(tile);

                if (fileExists) {
                    LoadTileFromHardDrive(path, tile.PhysicalSlot);
                }
                else {
                    // We only reach this if forceAllocate is TRUE.
                    // Wipe the recycled VRAM slot clean so the user can start drawing!
                    Graphics.CopyTexture(blankTile, 0, 0, 0, 0, atlas.TileSize, atlas.TileSize, atlas.Texture, 0, 0, tile.PhysicalSlot.x * atlas.TileSize, tile.PhysicalSlot.y * atlas.TileSize);
                }

                indirectionTables[zoomLevel].SetTileMapping(x, y, tile.PhysicalSlot.x, tile.PhysicalSlot.y);
            }
        }
    }




    private void EvictOldestTile() {
        if (activeTiles.Count == 0) return;

        TileState oldestTile = activeTiles[0];
        foreach (TileState tile in activeTiles) {
            if (tile.LastAccessTime < oldestTile.LastAccessTime) {
                oldestTile = tile;
            }
        }

        if (oldestTile != null) {
            Vector3Int oldestAddress = oldestTile.Address;

            // --- NEW: SAVE TO DISK BEFORE EVICTING ---
            if (oldestTile.IsDirty) {
                SaveTileToHardDrive(oldestAddress, oldestTile.PhysicalSlot);
                oldestTile.IsDirty = false;
            }

            // 2. Remove it from the GPU Map
            indirectionTables[oldestAddress.z].ClearTileMapping(oldestAddress.x, oldestAddress.y);

            // 3. Give the slot back to the Atlas
            atlas.FreeSlot(oldestTile.PhysicalSlot);

            // 4. Update the CPU records
            oldestTile.IsLoaded = false;
            activeTiles.Remove(oldestTile); // Remove it from the active list
        }
    }

    //Sync the indirection tables to the GPU at the end of the frame after all tile requests and evictions are done
    public void SyncGPU() {
        // Call this exactly ONCE at the very end of your frame (e.g., in LateUpdate)
        // It will push the updated maps to the GPU in one clean batch.
        for (int i = 0; i < indirectionTables.Length; i++) {
            indirectionTables[i].ApplyChanges();
        }
    }

    // Helper so the CanvasManager can find where a tile lives in VRAM
    public Vector2Int? GetPhysicalSlot(int x, int y, int zoomLevel) {
        Vector3Int address = new Vector3Int(x, y, zoomLevel);

        // If the tile exists and is currently loaded in the GPU, return its slot!
        if (tileDatabase.TryGetValue(address, out TileState state) && state.IsLoaded) {
            return state.PhysicalSlot;
        }
        return null;
    }

    private string GetTilePath(Vector3Int address) {
        // Creates a file name like: "Tile_0_2_4.png"
        return Path.Combine(saveDirectory, $"Tile_{address.z}_{address.x}_{address.y}.png");
    }

    private void SaveTileToHardDrive(Vector3Int address, Vector2Int slot) {
        string path = GetTilePath(address);

        // Ask the GPU to grab the exact 256x256 square for this slot in the background
        AsyncGPUReadback.Request(atlas.Texture, 0,
            slot.x * atlas.TileSize, atlas.TileSize,
            slot.y * atlas.TileSize, atlas.TileSize,
            0, 1,
            (request) => {
                if (request.hasError) {
                    Debug.LogError("Failed to read GPU texture for saving!");
                    return;
                }

                // When the GPU is done, convert the data to a PNG and save it
                Texture2D temp = new Texture2D(atlas.TileSize, atlas.TileSize, TextureFormat.RGBA32, false);
                temp.SetPixelData(request.GetData<byte>(), 0);
                temp.Apply();

                File.WriteAllBytes(path, temp.EncodeToPNG());
                Object.Destroy(temp); // Clean up memory!
            });
    }
    private void LoadTileFromHardDrive(string path, Vector2Int slot) {
        byte[] bytes = File.ReadAllBytes(path);
        Texture2D temp = new Texture2D(atlas.TileSize, atlas.TileSize);
        temp.LoadImage(bytes); // Auto-parses the PNG

        // Fast copy from CPU memory directly into the VRAM Atlas
        Graphics.CopyTexture(temp, 0, 0, 0, 0, atlas.TileSize, atlas.TileSize, atlas.Texture, 0, 0, slot.x * atlas.TileSize, slot.y * atlas.TileSize);
        Object.Destroy(temp); // Clean up memory!
    }

    // Call this from the CanvasManager whenever a tile is painted on so we know to save it later
    public void MarkTileDirty(int x, int y, int zoomLevel) {
        Vector3Int address = new Vector3Int(x, y, zoomLevel);
        if (tileDatabase.ContainsKey(address)) {
            tileDatabase[address].IsDirty = true;
        }
    }

    public void ClearAllSavedTiles() {
        if (Directory.Exists(saveDirectory)) {
            // Find all PNG files in our save folder
            string[] files = Directory.GetFiles(saveDirectory, "*.png");

            foreach (string file in files) {
                try {
                    File.Delete(file);
                }
                catch (System.Exception e) {
                    Debug.LogWarning($"Failed to delete {file}: {e.Message}");
                }
            }

            Debug.Log($"Successfully deleted {files.Length} temporary tile files.");
        }
    }

    // ==========================================
    // ASYNC DOWNSAMPLER & QUEUE
    // ==========================================

    // Call this whenever a stroke finishes on a specific Zoom 0 tile
    public void QueueMipmapUpdate(int sourceX, int sourceY, int sourceZoom) {
        int parentX = sourceX / 2;
        int parentY = sourceY / 2;
        int parentZoom = sourceZoom + 1;

        // Stop if we hit the top of the pyramid
        if (parentZoom >= indirectionTables.Length) return;

        Vector3Int parentCoord = new Vector3Int(parentX, parentY, parentZoom);

        // Prevent duplicate jobs if it's already in the queue
        if (!mipmapQueue.Contains(parentCoord)) {
            mipmapQueue.Enqueue(parentCoord);
        }

        // Boot up the assembly line if it's currently asleep
        if (!isWorkerRunning) {
            context.StartCoroutine(MipmapWorkerCoroutine());
        }
    }

    private IEnumerator MipmapWorkerCoroutine() {
        isWorkerRunning = true;

        while (mipmapQueue.Count > 0) {
            Vector3Int parentCoord = mipmapQueue.Dequeue();
            // Wait for this specific tile to finish baking before starting the next
            yield return context.StartCoroutine(BakeParentTileAsync(parentCoord.x, parentCoord.y, parentCoord.z));
        }

        isWorkerRunning = false;
    }

    private IEnumerator BakeParentTileAsync(int destX, int destY, int destZoom) {
        int childZoom = destZoom - 1;
        int tileSize = atlas.TileSize;

        RenderTexture tempRT = RenderTexture.GetTemporary(tileSize, tileSize, 0, RenderTextureFormat.ARGB32);
        RenderTexture.active = tempRT;
        GL.Clear(true, true, Color.white);

        GL.PushMatrix();
        GL.LoadOrtho();

        // Stamping the 4 children into the temp canvas (pulling from VRAM if possible!)
        DrawChildToQuadrant(destX * 2, destY * 2, childZoom, 0f, 0f);
        DrawChildToQuadrant(destX * 2 + 1, destY * 2, childZoom, 0.5f, 0f);
        DrawChildToQuadrant(destX * 2, destY * 2 + 1, childZoom, 0f, 0.5f);
        DrawChildToQuadrant(destX * 2 + 1, destY * 2 + 1, childZoom, 0.5f, 0.5f);

        GL.PopMatrix();

        // Request the pixels asynchronously
        AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(tempRT, 0, TextureFormat.RGBA32);

        yield return new WaitUntil(() => request.done);

        if (!request.hasError) {
            NativeArray<byte> nativeArray = request.GetData<byte>();

            Texture2D readbackTex = new Texture2D(tileSize, tileSize, TextureFormat.RGBA32, false);
            readbackTex.LoadRawTextureData(nativeArray);
            readbackTex.Apply();

            // Save to disk
            string parentPath = GetTilePath(new Vector3Int(destX, destY, destZoom));
            File.WriteAllBytes(parentPath, readbackTex.EncodeToPNG());

            // ==========================================
            // --- NEW: LIVE VRAM REFRESH ---
            // ==========================================
            // Check if the camera is actively looking at this lower-resolution tile
            Vector3Int parentAddress = new Vector3Int(destX, destY, destZoom);
            if (tileDatabase.ContainsKey(parentAddress)) {
                TileState state = tileDatabase[parentAddress];

                if (state.IsLoaded) {
                    // It is in VRAM! Instantly overwrite the old visual data with the new squished ink!
                    Graphics.CopyTexture(readbackTex, 0, 0, 0, 0, tileSize, tileSize, atlas.Texture, 0, 0, state.PhysicalSlot.x * tileSize, state.PhysicalSlot.y * tileSize);
                }
            }
            // ==========================================

            Object.Destroy(readbackTex);

            // Chain Reaction: Queue the next zoom level up!
            QueueMipmapUpdate(destX, destY, destZoom);
        }
        else {
            Debug.LogError("AsyncGPUReadback failed while baking mipmap!");
        }

        // Clean up
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(tempRT);
    }

    private void DrawChildToQuadrant(int childX, int childY, int childZoom, float xOffset, float yOffset) {
        // ==========================================
        // 1. CHECK VRAM (UN-EVICTED / ACTIVE TILES)
        // ==========================================
        Vector2Int? physicalSlot = GetPhysicalSlot(childX, childY, childZoom);

        if (physicalSlot != null) {
            // Calculate the UV coordinates (0.0 to 1.0) of this specific slot inside the Atlas
            float uvWidth = (float)atlas.TileSize / atlas.Texture.width;
            float uvHeight = (float)atlas.TileSize / atlas.Texture.height;
            float uvX = physicalSlot.Value.x * uvWidth;
            float uvY = physicalSlot.Value.y * uvHeight;

            Rect sourceUV = new Rect(uvX, uvY, uvWidth, uvHeight);
            Rect destRect = new Rect(xOffset, yOffset, 0.5f, 0.5f);

            // Fast GPU-to-GPU copy: Draw this exact slice of the Atlas directly onto our parent tile
            Graphics.DrawTexture(destRect, atlas.Texture, sourceUV, 0, 0, 0, 0, Color.white, null, -1);

            return; // Success! We bypass the hard drive entirely.
        }

        // ==========================================
        // 2. CHECK HARD DRIVE (EVICTED / SAVED TILES)
        // ==========================================
        string childPath = GetTilePath(new Vector3Int(childX, childY, childZoom));

        // If it's neither in VRAM nor on the hard drive, it's a completely blank tile area.
        if (!File.Exists(childPath)) return;

        // Load the saved tile from disk
        byte[] bytes = File.ReadAllBytes(childPath);
        Texture2D childTex = new Texture2D(atlas.TileSize, atlas.TileSize);
        childTex.LoadImage(bytes);

        // Draw it into the quadrant
        Graphics.DrawTexture(new Rect(xOffset, yOffset, 0.5f, 0.5f), childTex);

        Object.Destroy(childTex); // Clean up memory to prevent leaks
    }
}