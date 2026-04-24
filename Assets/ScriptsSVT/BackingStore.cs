using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class BackingStore
{
    private static readonly object fileLock = new object();
    private volatile bool isShuttingDown = false;

    // NEW: Track tiles that are currently in the background thread!
    private HashSet<Vector3Int> inFlightSaves = new HashSet<Vector3Int>();
    // --- DISK I/O VARIABLES ---
    private RenderTexture blankRT;
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
        // Setup the Save Directory on your Hard Drive
        saveDirectory = Path.Combine(Application.persistentDataPath, "SVT_Saves");
        if (!Directory.Exists(saveDirectory)) {
            Directory.CreateDirectory(saveDirectory);
        }

        // We need the MonoBehaviour context to run Coroutines for our async mipmap generation
        context = coroutineContext;
        mipmapQueue = new Queue<Vector3Int>();

        tileDatabase = new Dictionary<Vector3Int, TileState>();
        activeTiles = new List<TileState>();
        atlas = physicalAtlas;
        indirectionTables = tables;

        blankRT = new RenderTexture(atlas.TileSize, atlas.TileSize, 0, atlas.Texture.format);
        blankRT.enableRandomWrite = true;
        blankRT.Create();

        RenderTexture.active = blankRT;
        // set it to pure white (transparent in our shader) so when we copy it to a new slot, it's a clean slate for drawing
        GL.Clear(false, true, Color.white);
        RenderTexture.active = null;



        ClearAllSavedTiles();
    }
    public void RequestTile(int x, int y, int zoomLevel, bool forceAllocate = false) {
        Vector3Int tileAddress = new Vector3Int(x, y, zoomLevel);

        // --- THE DEADLOCK FIX ---
        lock (inFlightSaves) {
            if (inFlightSaves.Contains(tileAddress)) {
                return;
            }
        }
        // ---------------------------------

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
            if (!fileExists && !forceAllocate) {
                return;
            }
            // ==========================================

            Vector2Int? newSlot = atlas.AllocateSlot();

            // VRAM IS FULL! We need to evict...
            if (newSlot == null) {

                // ========================================================
                // --- THE CORRECT SAFETY VALVE ---
                // If the graphics card is overwhelmed with saves, refuse to evict!
                lock (inFlightSaves) {
                    if (inFlightSaves.Count > 15) {
                        return; // Abort this request completely. Do not trigger EvictOldestTile()!
                    }
                }
                // ========================================================

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
                    Graphics.CopyTexture(blankRT, 0, 0, 0, 0, atlas.TileSize, atlas.TileSize, atlas.Texture, 0, 0, tile.PhysicalSlot.x * atlas.TileSize, tile.PhysicalSlot.y * atlas.TileSize);
                }

                indirectionTables[zoomLevel].SetTileMapping(x, y, tile.PhysicalSlot.x, tile.PhysicalSlot.y);
            }
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



    private void EvictOldestTile() {
        
        if (activeTiles.Count == 0) return;

        // 1. Find the least recently accessed tile in VRAM
        TileState oldestTile = activeTiles[0];
        foreach (TileState tile in activeTiles) {
            if (tile.LastAccessTime < oldestTile.LastAccessTime) {
                oldestTile = tile;
            }
        }

        
        if (oldestTile != null) {
            // Store the address before we wipe the slot for the new tile
            Vector3Int oldestAddress = oldestTile.Address;

            // 2. If it's dirty, save it to the hard drive before we lose the data!
            if (oldestTile.IsDirty) {
                Debug.Log("Saving dirty tile to hard drive: " + oldestAddress);
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

    private void SaveTileToHardDrive(Vector3Int address, Vector2Int slot) {
        // BREADCRUMB 1: Did the engine even try to save this tile?
        Debug.Log($"[SVT Debug] 1. SaveTileToHardDrive CALLED for Tile: {address}");

        lock (inFlightSaves) {
            inFlightSaves.Add(address);
        }
        string path = GetTilePath(address);

        RenderTexture snapshotRT = new RenderTexture(atlas.TileSize, atlas.TileSize, 0, atlas.Texture.format);
        snapshotRT.enableRandomWrite = true;
        snapshotRT.Create();

        Graphics.CopyTexture(atlas.Texture, 0, 0, slot.x * atlas.TileSize, slot.y * atlas.TileSize, atlas.TileSize, atlas.TileSize, snapshotRT, 0, 0, 0, 0);

        Debug.Log($"[SVT Debug] 2. Requesting GPU Readback for Tile: {address}");
        AsyncGPUReadback.Request(snapshotRT, 0, TextureFormat.RGBA32, (request) => {

            // BREADCRUMB 2: Did the GPU successfully hand the data back to the CPU?
            Debug.Log($"[SVT Debug] 3. GPU Readback CALLBACK FIRED for Tile: {address}");

            if (request.hasError) {
                Debug.LogError($"[SVT Debug] ERROR: GPU Readback failed for {address}!");
                Object.Destroy(snapshotRT);
                lock (inFlightSaves) { inFlightSaves.Remove(address); }
                return;
            }

            byte[] rawData = request.GetData<byte>().ToArray();
            int width = atlas.TileSize;
            int height = atlas.TileSize;

            Debug.Log($"[SVT Debug] 4. Spinning up Background Thread for Tile: {address}");
            System.Threading.Tasks.Task.Run(() => {
                try {
                    // BREADCRUMB 3: Did the thread start, or was it silently aborted?
                    Debug.Log($"[SVT Debug] 5. Background Thread STARTED for Tile: {address}");

                    if (isShuttingDown) {
                        Debug.LogWarning($"[SVT Debug] ABORTED: isShuttingDown is TRUE!");
                        return;
                    }

                    byte[] pngBytes = ImageConversion.EncodeArrayToPNG(
                        rawData,
                        UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                        (uint)width,
                        (uint)height
                    );

                    lock (fileLock) {
                        if (!isShuttingDown) {
                            File.WriteAllBytes(path, pngBytes);
                            // BREADCRUMB 4: The finish line!
                            Debug.Log($"[SVT Debug] 6. SUCCESS! File actually written to: {path}");
                        }
                    }
                }
                catch (System.Exception e) {
                    Debug.LogError($"[SVT Debug] THREAD CRASH: {e.Message}\n{e.StackTrace}");
                }
                finally {
                    lock (inFlightSaves) {
                        inFlightSaves.Remove(address);
                    }
                }
            });

            Object.Destroy(snapshotRT);
        });
    }
    private void LoadTileFromHardDrive(string path, Vector2Int slot) {
        

        byte[] bytes;
        // LOCK THE HARD DRIVE: Prevent the main thread from reading a file while a background thread is saving it!
        lock (fileLock) {
            if (!File.Exists(path)) return;
            bytes = File.ReadAllBytes(path);
        }

        Texture2D temp = new Texture2D(atlas.TileSize, atlas.TileSize);
        temp.LoadImage(bytes);

        RenderTexture bridgeRT = new RenderTexture(atlas.TileSize, atlas.TileSize, 0, atlas.Texture.format);
        bridgeRT.enableRandomWrite = true;
        bridgeRT.Create();

        Graphics.Blit(temp, bridgeRT);

        // THE WARNING FIX: Unbind the canvas before destroying it!
        RenderTexture.active = null;

        Graphics.CopyTexture(bridgeRT, 0, 0, 0, 0, atlas.TileSize, atlas.TileSize, atlas.Texture, 0, 0, slot.x * atlas.TileSize, slot.y * atlas.TileSize);

        Object.Destroy(bridgeRT);
        Object.Destroy(temp);
    }

    // Call this from the CanvasManager whenever a tile is painted on so we know to save it later
    public void MarkTileDirty(int x, int y, int zoomLevel) {
        Vector3Int address = new Vector3Int(x, y, zoomLevel);
        if (tileDatabase.ContainsKey(address)) {
            tileDatabase[address].IsDirty = true;
        }
    }

    public void MarkTileDirty(Vector3Int address) {
        if (tileDatabase.ContainsKey(address)) {
            tileDatabase[address].IsDirty = true;
        }
    }

    public void ClearAllSavedTiles() {
        // 1. Signal all background threads to immediately abort their saving tasks
        isShuttingDown = true;

        // 2. Lock the hard drive so no active thread can write while we are deleting
        lock (fileLock) {
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
        isShuttingDown = false;
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

        // Calculate the addresses of the 4 children
        Vector3Int c1 = new Vector3Int(destX * 2, destY * 2, childZoom);
        Vector3Int c2 = new Vector3Int(destX * 2 + 1, destY * 2, childZoom);
        Vector3Int c3 = new Vector3Int(destX * 2, destY * 2 + 1, childZoom);
        Vector3Int c4 = new Vector3Int(destX * 2 + 1, destY * 2 + 1, childZoom);

        // --- FIX 1: THE IN-FLIGHT GUARD ---
        // Wait for all 4 children to safely land on the hard drive before reading them!
        yield return new WaitUntil(() =>
            !IsTileInFlight(c1) &&
            !IsTileInFlight(c2) &&
            !IsTileInFlight(c3) &&
            !IsTileInFlight(c4)
        );

        // 1. Create a dedicated, UAV-compatible canvas for the bake
        RenderTexture tempRT = new RenderTexture(tileSize, tileSize, 0, atlas.Texture.format);
        tempRT.enableRandomWrite = true;
        tempRT.Create();

        RenderTexture.active = tempRT;

        // ==========================================
        //  CLEAR TO TRANSPARENT! 
        // We only want to save the ink, not the paper!
        GL.Clear(false, true, Color.clear);
        // ==========================================

        // ==========================================
        // FIX 2: ORTHO PERCENTAGE MATH
        // This stops Unity from physically scrambling the tiles upside down
        GL.PushMatrix();
        GL.LoadOrtho();

        DrawChildToQuadrant(c1.x, c1.y, childZoom, atlas, 0f, 0f);       // Bottom-Left
        DrawChildToQuadrant(c2.x, c2.y, childZoom, atlas, 0.5f, 0f);     // Bottom-Right
        DrawChildToQuadrant(c3.x, c3.y, childZoom, atlas, 0f, 0.5f);     // Top-Left
        DrawChildToQuadrant(c4.x, c4.y, childZoom, atlas, 0.5f, 0.5f);   // Top-Right

        GL.PopMatrix();
        // ==========================================

        RenderTexture.active = null;

        Vector3Int parentAddress = new Vector3Int(destX, destY, destZoom);

        // ==========================================
        // --- INSTANT VRAM REFRESH ---
        // ==========================================
        if (tileDatabase.TryGetValue(parentAddress, out TileState parentState)) {
            if (parentState.IsLoaded) {
                Graphics.CopyTexture(tempRT, 0, 0, 0, 0, tileSize, tileSize, atlas.Texture, 0, 0, parentState.PhysicalSlot.x * tileSize, parentState.PhysicalSlot.y * tileSize);
            }
        }

        // --- FIX 3: PROTECT THE PARENT TILE ---
        // Lock this parent tile so the Camera doesn't try to load it while it's writing!
        lock (inFlightSaves) {
            inFlightSaves.Add(parentAddress);
        }

        string parentPath = GetTilePath(parentAddress);

        // 2. Ask the GPU to download the pixels for our hard drive save
        AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(tempRT, 0, TextureFormat.RGBA32);

        yield return new WaitUntil(() => request.done);

        if (!request.hasError) {
            // 3. Rip the raw bytes instantly to avoid Texture2D main-thread stalling
            byte[] rawData = request.GetData<byte>().ToArray();

            // 4. Send the heavy PNG encoding to a background CPU thread
            System.Threading.Tasks.Task.Run(() => {
                try {
                    // If the app closed while we were compressing, abort the save!
                    if (isShuttingDown) return;

                    byte[] pngBytes = ImageConversion.EncodeArrayToPNG(
                        rawData,
                        UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                        (uint)tileSize,
                        (uint)tileSize
                    );

                    // LOCK THE HARD DRIVE!
                    lock (fileLock) {
                        if (!isShuttingDown) {
                            File.WriteAllBytes(parentPath, pngBytes);
                        }
                    }
                }
                catch (System.Exception e) {
                    Debug.LogError("Mipmap Save Thread Failed: " + e.Message);
                }
                finally {
                    // --- FIX 4: UNLOCK ON SUCCESS ---
                    lock (inFlightSaves) {
                        inFlightSaves.Remove(parentAddress);
                    }
                }
            });

            // Chain Reaction: Queue the next zoom level up!
            QueueMipmapUpdate(destX, destY, destZoom);
        }
        else {
            Debug.LogError("AsyncGPUReadback failed while baking mipmap!");
            // --- FIX 5: UNLOCK ON ERROR ---
            lock (inFlightSaves) {
                inFlightSaves.Remove(parentAddress);
            }
        }

        // Clean up our dedicated canvas
        Object.Destroy(tempRT);
    }

    private void DrawChildToQuadrant(int childX, int childY, int childZoom, PhysicalAtlas atlas, float xOffset, float yOffset) {
        // Back to your original, perfect 0.0 to 1.0 percentage coordinates!
        Rect destRect = new Rect(xOffset, yOffset, 0.5f, 0.5f);

        Vector3Int childAddress = new Vector3Int(childX, childY, childZoom);

        // 1. CHECK VRAM
        if (tileDatabase.TryGetValue(childAddress, out TileState childState) && childState.IsLoaded) {
            float uvWidth = (float)atlas.TileSize / atlas.Texture.width;
            float uvHeight = (float)atlas.TileSize / atlas.Texture.height;
            float uvX = childState.PhysicalSlot.x * uvWidth;
            float uvY = childState.PhysicalSlot.y * uvHeight;

            Rect sourceUV = new Rect(uvX, uvY, uvWidth, uvHeight);

            Graphics.DrawTexture(destRect, atlas.Texture, sourceUV, 0, 0, 0, 0, Color.white, null, -1);
            return;
        }

        // 2. CHECK HARD DRIVE
        string childPath = GetTilePath(childAddress);

        byte[] bytes;
        lock (fileLock) {
            if (!File.Exists(childPath)) return;
            bytes = File.ReadAllBytes(childPath);
        }

        Texture2D childTex = new Texture2D(atlas.TileSize, atlas.TileSize);
        childTex.LoadImage(bytes);

        Graphics.DrawTexture(destRect, childTex);

        Object.Destroy(childTex);
    }
    public bool IsTileInFlight(Vector3Int address) {
        lock (inFlightSaves) {
            return inFlightSaves.Contains(address);
        }
    }
}