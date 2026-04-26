using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression; // Required for pure C# background zipping
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

public class GhostCanvas : MonoBehaviour
{
    private readonly object diskLock = new object();

    public class CrucibleState
    {
        public RenderTexture rt;
        public bool isDirty; // Has anyone drawn on this since the last readback request?
        public int readbackVersion; // Which version of the tile are we currently on?
    }


    private class StrokeCountdown { public int count; }

    [Header("Settings")]
    public int tileSize = 256;
    public string saveDirectory;
    public int maxRamCacheSizeMB = 500; // Safety valve

    // ==========================================
    // 1. THE WAITING ROOM (Thread-Safe System RAM)
    // ==========================================
    // Key: Address (X, Y, ZoomLevel). Value: Compressed PNG bytes
    private ConcurrentDictionary<Vector3Int, byte[]> ramCache = new ConcurrentDictionary<Vector3Int, byte[]>();


    // Tiles currently being drawn on. Only accessed on Unity's Main Thread.
    // Our new tracking dictionary
    private Dictionary<Vector3Int, CrucibleState> activeCrucibles = new Dictionary<Vector3Int, CrucibleState>();
    private Material stampMaterial; // Material to draw the brush stamps

    // ==========================================
    // 3. THE FLUSHER (Background I/O Thread)
    // ==========================================
    private Thread ioThread;
    private bool isShuttingDown = false;
    // A separate queue to know which tiles are oldest
    private ConcurrentQueue<(Vector3Int address, byte[] data)> flushQueue = new ConcurrentQueue<(Vector3Int, byte[])>();

    public void Initialize(Material brushMaterial, string saveDir) {
        stampMaterial = brushMaterial;
        saveDirectory = saveDir;
        if (!Directory.Exists(saveDirectory)) Directory.CreateDirectory(saveDirectory);

        // Spin up the background hard drive flusher
        ioThread = new Thread(BackgroundFlusherLoop);
        ioThread.Priority = System.Threading.ThreadPriority.BelowNormal;
        ioThread.Start();
    }

    // ==========================================
    // THE ROUTER (Called by BackingStore.RequestTile)
    // ==========================================
    public byte[] TryGetTile(Vector3Int address) {
        if (ramCache.TryGetValue(address, out byte[] data)) return data;

        string path = Path.Combine(saveDirectory, $"Tile_{address.z}_{address.x}_{address.y}.dat");
        if (File.Exists(path)) {
            // No lock needed! The OS guarantees if the .dat file exists, it is whole.
            byte[] compressedBytes;
            try {
                compressedBytes = File.ReadAllBytes(path);
            }
            catch (IOException) {
                // In the astronomically rare 1-microsecond window where File.Move is swapping the file
                // while we try to read it, just fail gracefully and let it load blank for one frame.
                return null;
            }

            using (MemoryStream ms = new MemoryStream(compressedBytes))
            using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (MemoryStream outMs = new MemoryStream()) {
                ds.CopyTo(outMs);
                return outMs.ToArray();
            }
        }
        return null;
    }
    // ==========================================
    // THE BAKING FOUNDRY (Called by BackingStore queue)
    // ==========================================
    public void BakeStamps(List<Vector2> stamps, int layerID, BlendModeConfig blendMode, float brushSize) {
        // 1. Setup the GPU Material
        blendMode.SetBlendMode(stampMaterial);

        // 2. THE SORTING BUCKETS (Now with Overlap!)
        Dictionary<Vector3Int, List<Vector2>> groupedStamps = new Dictionary<Vector3Int, List<Vector2>>();
        float radius = brushSize / 2f; // Assuming brushSize is a diameter

        foreach (Vector2 stampPos in stamps) {
            // Calculate the Bounding Box of the stamp in world coordinates
            Vector2 minBounds = new Vector2(stampPos.x - radius, stampPos.y - radius);
            Vector2 maxBounds = new Vector2(stampPos.x + radius, stampPos.y + radius);

            // Find the minimum tile and maximum tile this rectangle touches
            // (Assuming Z/ZoomLevel is 0 for the base canvas baking)
            Vector3Int minTile = WorldToTileAddress(minBounds);
            Vector3Int maxTile = WorldToTileAddress(maxBounds);

            // Add this stamp to EVERY tile it touches!
            for (int tx = minTile.x; tx <= maxTile.x; tx++) {
                for (int ty = minTile.y; ty <= maxTile.y; ty++) {
                    Vector3Int overlapAddress = new Vector3Int(tx, ty, minTile.z);

                    if (!groupedStamps.ContainsKey(overlapAddress)) {
                        groupedStamps[overlapAddress] = new List<Vector2>();
                    }
                    groupedStamps[overlapAddress].Add(stampPos);
                }
            }
        }


        // 3. THE OPTIMIZED BATCH DRAWING
        foreach (var kvp in groupedStamps) {
            Vector3Int tileAddress = kvp.Key;
            List<Vector2> stampsForThisTile = kvp.Value;

            // Fetch the state wrapper
            CrucibleState state = GetOrCreateCrucible(tileAddress);

            // MARK AS DIRTY!
            state.isDirty = true;

            // BIND THE RENDER TEXTURE EXACTLY ONCE PER TILE!
            RenderTexture.active = state.rt;

            GL.PushMatrix();
            GL.LoadOrtho();
            stampMaterial.SetPass(0);

            // Blast all the stamps for this tile in one go
            foreach (Vector2 stampPos in stampsForThisTile) {
                Vector2 localUV = GetLocalTileUV(stampPos, tileAddress);
                DrawQuad(localUV);
            }

            GL.PopMatrix();
        }

        // Unbind at the very end
        RenderTexture.active = null;
    }

    // Track how many readbacks are currently happening for a specific layer
    private Dictionary<int, int> pendingReadbacksPerLayer = new Dictionary<int, int>();

    public void FinalizeLayer(int layerID, System.Action onLayerFullyBaked) {
        int dirtyCount = 0;

        // 1. First pass to see how many tiles this specific stroke actually modified
        foreach (var kvp in activeCrucibles) {
            if (kvp.Value.isDirty) dirtyCount++;
        }

        if (dirtyCount == 0) {
            onLayerFullyBaked?.Invoke();
            return;
        }

        // 2. Create a dedicated countdown just for THIS stroke
        StrokeCountdown countdown = new StrokeCountdown { count = dirtyCount };

        // 3. Trigger the readbacks
        foreach (var kvp in activeCrucibles) {
            Vector3Int address = kvp.Key;
            CrucibleState state = kvp.Value;

            if (state.isDirty) {
                state.isDirty = false;
                state.readbackVersion++;
                int versionForThisCallback = state.readbackVersion;

                AsyncGPUReadback.Request(state.rt, 0, TextureFormat.RGBA32, (request) => {
                    OnCrucibleReadbackComplete(request, address, state, versionForThisCallback, countdown, onLayerFullyBaked);
                });
            }
        }
    }

    private void OnCrucibleReadbackComplete(AsyncGPUReadbackRequest request, Vector3Int address, CrucibleState state, int callbackVersion, StrokeCountdown countdown, System.Action onLayerFullyBaked) {
        if (request.hasError) return;

        // 1. Grab the RAW bytes directly from the GPU memory (Lightning fast)
        var nativeArray = request.GetData<byte>();
        byte[] rawBytes = nativeArray.ToArray(); // Instantly copy to normal C# array

        // 2. Put it straight into the RAM Waiting Room!
        ramCache[address] = rawBytes;
        flushQueue.Enqueue(address);

        // 3. INLINE BUBBLING (Async Mipmaps) placeholder...
        // GenerateMipmapsAsync(address);

        // SAFETY CHECK
        if (!state.isDirty && state.readbackVersion == callbackVersion) {
            RenderTexture.ReleaseTemporary(state.rt);
            activeCrucibles.Remove(address);
        }

        // THE ISOLATED HANDOFF COUNTDOWN
        countdown.count--;
        if (countdown.count <= 0) {
            onLayerFullyBaked?.Invoke();
        }
    }

    // ==========================================
    // CRUCIBLE UTILITIES
    // ==========================================
    private CrucibleState GetOrCreateCrucible(Vector3Int address) {
        if (activeCrucibles.ContainsKey(address)) {
            return activeCrucibles[address]; // It's already hot and ready!
        }

        // Create a temporary VRAM workspace
        RenderTexture rt = RenderTexture.GetTemporary(tileSize, tileSize, 0, RenderTextureFormat.ARGB32);
        rt.enableRandomWrite = true;
        rt.filterMode = FilterMode.Point;

        // Load the existing artwork into it before we start drawing
        byte[] existingData = TryGetTile(address);
        if (existingData != null) {
            Texture2D tex = new Texture2D(tileSize, tileSize, TextureFormat.RGBA32, false);

            // Dump the raw bytes instantly
            tex.LoadRawTextureData(existingData);
            tex.Apply(); // Must be applied for Graphics.Blit to see it

            Graphics.Blit(tex, rt);
            Destroy(tex);
        }
        else {
            // It's a blank tile
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.clear);
            RenderTexture.active = null;
        }

        // Wrap it in our new State Machine!
        CrucibleState newState = new CrucibleState
        {
            rt = rt,
            isDirty = false,
            readbackVersion = 0
        };

        activeCrucibles[address] = newState;
        return newState;
    }

    // ==========================================
    // THE BACKGROUND FLUSHER (Thread-Safe)
    // ==========================================
    private void BackgroundFlusherLoop() {
        while (!isShuttingDown) {
            // We now dequeue the whole task! We don't even need to ask the ramCache anymore!
            if (flushQueue.TryDequeue(out var task)) {
                string finalPath = Path.Combine(saveDirectory, $"Tile_{task.address.z}_{task.address.x}_{task.address.y}.dat");
                string tmpPath = finalPath + ".tmp";

                using (FileStream fs = new FileStream(tmpPath, FileMode.Create))
                using (DeflateStream ds = new DeflateStream(fs, CompressionMode.Compress)) {
                    ds.Write(task.data, 0, task.data.Length);
                }

                if (File.Exists(finalPath)) {
                    File.Replace(tmpPath, finalPath, null);
                }
                else {
                    File.Move(tmpPath, finalPath);
                }
            }
            else {
                Thread.Sleep(50);
            }
        }
    }

    // ==========================================
    // BACKING STORE UTILITIES
    // ==========================================
    public void UploadBytesToVRAMSlot(byte[] data, Vector2Int slot, PhysicalAtlas atlas) {
        Texture2D tempTex = new Texture2D(tileSize, tileSize, TextureFormat.RGBA32, false);

        // Dumps the raw bytes instantly instead of decoding a PNG
        tempTex.LoadRawTextureData(data);

        Graphics.CopyTexture(tempTex, 0, 0, 0, 0, tileSize, tileSize, atlas.Texture, 0, 0, slot.x * tileSize, slot.y * tileSize);
        Destroy(tempTex);
    }

    public void ClearVRAMSlot(Vector2Int slot, PhysicalAtlas atlas) {
        // For performance, we create a tiny black texture and copy it over the slot
        Texture2D blankTex = new Texture2D(tileSize, tileSize, TextureFormat.RGBA32, false);
        Color32[] clearColors = new Color32[tileSize * tileSize];
        blankTex.SetPixels32(clearColors);
        blankTex.Apply();

        Graphics.CopyTexture(blankTex, 0, 0, 0, 0, tileSize, tileSize, atlas.Texture, 0, 0, slot.x * tileSize, slot.y * tileSize);
        Destroy(blankTex);
    }

    private void OnDestroy() {
        isShuttingDown = true;
        // In a real app, do a blocking flush of the queue here to ensure no data loss on exit!
    
    }

    public void UnloadFromRAM(Vector3Int address) {
        ramCache.TryRemove(address, out _);
    }

    // Stub Helpers
    public Vector3Int WorldToTileAddress(Vector2 pos) { return Vector3Int.zero; /* Implement based on CanvasConfig */ }
    private Vector2 GetLocalTileUV(Vector2 pos, Vector3Int tile) { return Vector2.zero; /* Implement based on CanvasConfig */ }
    private void DrawQuad(Vector2 centerUV) { /* Standard GL.Vertex3 Quad around center */ }


}