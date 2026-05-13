using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

public class GhostCanvas : MonoBehaviour
{
    private int activeReadbacks = 0;

    public class CrucibleState
    {
        public RenderTexture documentRT;
        public int readbackVersion;
    }

    private ConcurrentDictionary<Vector3Int, byte> pendingDiskWrites = new ConcurrentDictionary<Vector3Int, byte>();
    private Dictionary<Vector3Int, List<Vector2>> pendingTileJobs = new Dictionary<Vector3Int, List<Vector2>>();

    private HashSet<Vector3Int> currentStrokeTiles = new HashSet<Vector3Int>();

    private ConcurrentQueue<(Vector3Int address, byte[] data)> diskLoadQueue = new ConcurrentQueue<(Vector3Int, byte[])>();
    private ConcurrentDictionary<Vector3Int, byte> pendingDiskReads = new ConcurrentDictionary<Vector3Int, byte>();

    private class StrokeCountdown { public int count; }

    [Header("Settings")]
    public int tileSize = 256;
    public string saveDirectory;
    public int maxRamCacheSizeMB = 500;
    public int maxCruciblesPerFrame = 30;
    [Header("Settings")]

    [Header("References")]
    public BackingStore backingStore;
    public CanvasManager canvasManager;
    public Material mipmapMaterial;

    private ConcurrentDictionary<Vector3Int, byte[]> ramCache = new ConcurrentDictionary<Vector3Int, byte[]>();
    private Dictionary<Vector3Int, CrucibleState> activeCrucibles = new Dictionary<Vector3Int, CrucibleState>();

    private Material stampMaterial;
    public Material mergeMaterial;
    private RenderTexture scratchpadRT;

    private Thread ioThread;
    private bool isShuttingDown = false;
    private ConcurrentQueue<(Vector3Int address, byte[] data)> flushQueue = new ConcurrentQueue<(Vector3Int, byte[])>();
    Color32[] clearColors;

    public void Initialize(Material brushMat, Material inkMat, CanvasManager cm, string saveDir) {
        clearColors = new Color32[tileSize * tileSize];

        for (int i = 0; i < clearColors.Length; i++) {
            clearColors[i] = new Color32(255, 255, 255, 255);
        }

        stampMaterial = brushMat;
        mergeMaterial = inkMat;
        canvasManager = cm;
        saveDirectory = saveDir;

        if (Directory.Exists(saveDirectory)) {
            Directory.Delete(saveDirectory, true);
        }
        Directory.CreateDirectory(saveDirectory);

        // ALWAYS match ARGB32!
        scratchpadRT = new RenderTexture(tileSize, tileSize, 0, RenderTextureFormat.ARGB32);
        scratchpadRT.filterMode = FilterMode.Point;
        scratchpadRT.Create();

        ioThread = new Thread(BackgroundFlusherLoop);
        ioThread.Priority = System.Threading.ThreadPriority.BelowNormal;
        ioThread.Start();
    }

    private void Update() {
        while (diskLoadQueue.TryDequeue(out var loadedTile)) {
            ramCache[loadedTile.address] = loadedTile.data;
            if (backingStore != null) {
                backingStore.OnChunkBaked(new HashSet<Vector3Int> { loadedTile.address }, -1, false);
            }
        }

        if (backingStore == null) return;

        StrokeBuffer stroke = backingStore.PeekNextStroke();
        if (stroke == null) return;

        while (stroke.stampQueue.Count > 0) {
            Vector2 stamp = stroke.stampQueue.Dequeue();
            HashSet<Vector3Int> requiredTiles = CalculateTilesForStamp(stamp, stroke.brushSize);

            foreach (var t in requiredTiles) {
                if (!pendingTileJobs.ContainsKey(t)) {
                    pendingTileJobs[t] = new List<Vector2>();
                }
                pendingTileJobs[t].Add(stamp);
            }
        }

        if (!stroke.isStrokeFinished) return;

        if (pendingTileJobs.Count > 0) {
            Dictionary<Vector3Int, List<Vector2>> chunkToBake = new Dictionary<Vector3Int, List<Vector2>>();
            List<Vector3Int> keys = new List<Vector3Int>(pendingTileJobs.Keys);

            int tilesToProcess = Mathf.Min(keys.Count, 10);

            for (int i = 0; i < tilesToProcess; i++) {
                Vector3Int tile = keys[i];
                chunkToBake[tile] = pendingTileJobs[tile];
                pendingTileJobs.Remove(tile);
            }

            ProcessTileBatch(chunkToBake, stroke);
        }

        bool isCompletelyDone = (stroke.stampQueue.Count == 0 &&
                                 stroke.isStrokeFinished &&
                                 pendingTileJobs.Count == 0 &&
                                 activeReadbacks == 0);

        if (isCompletelyDone) {
            // 1. Copy the list of tiles so we can clear the global tracker immediately
            HashSet<Vector3Int> tilesToMipmap = new HashSet<Vector3Int>(currentStrokeTiles);
            currentStrokeTiles.Clear();

            // 2. Tell the BackingStore to drop the stroke from the queue
            backingStore.FinishCurrentStroke();

            // 3. Kick off the Mipmap Chain! 
            // We pass a callback so the UI Preview stays visible until the final Mipmap is safely on the disk!
            GenerateMipmapsForStroke(tilesToMipmap, () =>
            {
                backingStore.OnChunkBaked(new HashSet<Vector3Int>(), stroke.strokeID, true);
            });
        }
    }

    private void ProcessTileBatch(Dictionary<Vector3Int, List<Vector2>> batch, StrokeBuffer stroke) {
        stampMaterial.SetFloat("_Flow", stroke.flow);
        if (stroke.blendMode != null) stroke.blendMode.SetBlendMode(mergeMaterial);
        mergeMaterial.SetFloat("_Opacity", stroke.opacity);

        float canvasWorldWidth = canvasManager.canvasWidthInTiles * canvasManager.worldUnitsPerTile;
        float canvasWorldHeight = canvasManager.canvasHeightInTiles * canvasManager.worldUnitsPerTile;


        float halfBrush = (stroke.brushSize / canvasManager.worldUnitsPerTile) / 2f;
        int readbacksTriggered = 0;
        HashSet<Vector3Int> touchedTiles = new HashSet<Vector3Int>();

        foreach (var kvp in batch) {
            Vector3Int tileAddress = kvp.Key;
            List<Vector2> stampsForThisTile = kvp.Value;

            CrucibleState state = GetOrCreateCrucible(tileAddress);

            // ==========================================
            // PASS 1: SCRATCHPAD
            // ==========================================
            RenderTexture.active = scratchpadRT;
            GL.Clear(true, true, Color.clear);
            GL.Viewport(new Rect(0, 0, tileSize, tileSize));

            stampMaterial.SetPass(0);
            GL.PushMatrix();


            GL.LoadOrtho();

            GL.Begin(GL.QUADS);
            foreach (Vector2 point in stampsForThisTile) {
                // Convert world coordinates to local tile coordinates
                float shiftedX = point.x + (canvasWorldWidth / 2f);
                float shiftedY = point.y + (canvasWorldHeight / 2f);
                // Calculate the world position of the tile's bottom-left corner
                float tileWorldX = tileAddress.x * canvasManager.worldUnitsPerTile;
                float tileWorldY = tileAddress.y * canvasManager.worldUnitsPerTile;
                float localX = (shiftedX - tileWorldX) / canvasManager.worldUnitsPerTile;
                float localY = (shiftedY - tileWorldY) / canvasManager.worldUnitsPerTile;
                float xMin = localX - halfBrush;
                float xMax = localX + halfBrush;
                float yMin = localY - halfBrush;
                float yMax = localY + halfBrush;

                // FIX 1: Counter-Clockwise Winding Order so it doesn't get culled!

                GL.TexCoord2(0, 0); GL.Vertex3(xMin, yMin, 0); // Bottom-Left 
                GL.TexCoord2(1, 0); GL.Vertex3(xMax, yMin, 0); // Bottom-Right 
                GL.TexCoord2(1, 1); GL.Vertex3(xMax, yMax, 0); // Top-Right
                GL.TexCoord2(0, 1); GL.Vertex3(xMin, yMax, 0); // Top-Left
            }
            GL.End();
            GL.PopMatrix();
            // at this point the scratchpadRT contains the blended result of all stamps that affect this tile, but it's not yet merged with the existing tile data in state.documentRT


            // ==========================================
            // PASS 2: MERGE TO PERMANENT TILE
            // ==========================================
            RenderTexture.active = state.documentRT;
            GL.Viewport(new Rect(0, 0, tileSize, tileSize));
            mergeMaterial.SetTexture("_MainTex", scratchpadRT);
            mergeMaterial.SetPass(0);

            GL.PushMatrix();
            GL.LoadOrtho();
            // FIX 2: Removed GL.LoadPixelMatrix(0, 1, 0, 1); which was squishing the quad

            GL.Begin(GL.QUADS);
            // FIX 1: Counter-Clockwise Winding Order
            GL.TexCoord2(0, 0); GL.Vertex3(0, 0, 0); // Bottom-Left
            GL.TexCoord2(1, 0); GL.Vertex3(1, 0, 0); // Bottom-Right
            GL.TexCoord2(1, 1); GL.Vertex3(1, 1, 0); // Top-Right
            GL.TexCoord2(0, 1); GL.Vertex3(0, 1, 0); // Top-Left

            GL.End();
            GL.PopMatrix();


            touchedTiles.Add(tileAddress);
            currentStrokeTiles.Add(tileAddress);
            readbacksTriggered++;
        }
        RenderTexture.active = null;

        StrokeCountdown countdown = new StrokeCountdown { count = readbacksTriggered };
        foreach (Vector3Int address in touchedTiles) {
            CrucibleState state = activeCrucibles[address];
            state.readbackVersion++;
            activeReadbacks++;

            AsyncGPUReadback.Request(state.documentRT, 0, TextureFormat.ARGB32, (request) =>
            {
                activeReadbacks--;
                OnCrucibleReadbackComplete(request, address, state, state.readbackVersion, countdown, () =>
                {
                    backingStore.OnChunkBaked(touchedTiles, stroke.strokeID, false);
                });
            });
        }
    }
    private HashSet<Vector3Int> CalculateTilesForStamp(Vector2 stampPos, float brushSize) {
        HashSet<Vector3Int> tiles = new HashSet<Vector3Int>();
        float radius = brushSize / 2f;

        Vector2Int min2D = canvasManager.WorldToTileCoordinate(new Vector2(stampPos.x - radius, stampPos.y - radius));
        Vector2Int max2D = canvasManager.WorldToTileCoordinate(new Vector2(stampPos.x + radius, stampPos.y + radius));

        for (int tx = min2D.x; tx <= max2D.x; tx++) {
            for (int ty = min2D.y; ty <= max2D.y; ty++) {
                tiles.Add(new Vector3Int(tx, ty, 0));
            }
        }
        return tiles;
    }

    private void OnCrucibleReadbackComplete(AsyncGPUReadbackRequest request, Vector3Int address, CrucibleState state, int callbackVersion, StrokeCountdown countdown, System.Action onLayerFullyBaked) {
        if (state.readbackVersion == callbackVersion) {
            RenderTexture.ReleaseTemporary(state.documentRT);
            activeCrucibles.Remove(address);
        }

        if (!request.hasError) {
            var nativeArray = request.GetData<byte>();
            byte[] rawBytes = nativeArray.ToArray();
            ramCache[address] = rawBytes;
            pendingDiskWrites.TryAdd(address, 1);
            flushQueue.Enqueue((address, rawBytes));
        }

        countdown.count--;
        if (countdown.count <= 0) {
            onLayerFullyBaked?.Invoke();
        }
    }

    private CrucibleState GetOrCreateCrucible(Vector3Int address) {
        if (activeCrucibles.ContainsKey(address)) return activeCrucibles[address];

        RenderTexture docRT = RenderTexture.GetTemporary(tileSize, tileSize, 0, RenderTextureFormat.ARGB32);

        docRT.filterMode = FilterMode.Point;

        byte[] existingData = GetTileSynchronous(address);
        if (existingData != null) {
            // CRITICAL FIX: Match Format!
            Texture2D tex = new Texture2D(tileSize, tileSize, TextureFormat.ARGB32, false);
            tex.LoadRawTextureData(existingData);
            tex.Apply();
            Graphics.Blit(tex, docRT);
            Destroy(tex);
        }
        else {
            RenderTexture.active = docRT;
            GL.Clear(true, true, Color.white);
            RenderTexture.active = null;
        }

        CrucibleState newState = new CrucibleState { documentRT = docRT, readbackVersion = 0 };
        activeCrucibles[address] = newState;
        return newState;
    }

    public byte[] TryGetTileAsync(Vector3Int address) {
        if (ramCache.TryGetValue(address, out byte[] data)) return data;

        string path = Path.Combine(saveDirectory, $"Tile_{address.z}_{address.x}_{address.y}.dat");
        if (File.Exists(path)) {
            if (pendingDiskReads.TryAdd(address, 1)) {
                System.Threading.Tasks.Task.Run(() =>
                {
                    try {
                        byte[] compressedBytes = File.ReadAllBytes(path);
                        using (MemoryStream ms = new MemoryStream(compressedBytes))
                        using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                        using (MemoryStream outMs = new MemoryStream()) {
                            ds.CopyTo(outMs);
                            diskLoadQueue.Enqueue((address, outMs.ToArray()));
                        }
                    }
                    catch { }
                    finally { pendingDiskReads.TryRemove(address, out _); }
                });
            }
        }
        return null;
    }

    public byte[] GetTileSynchronous(Vector3Int address) {
        if (ramCache.TryGetValue(address, out byte[] data)) return data;

        string path = Path.Combine(saveDirectory, $"Tile_{address.z}_{address.x}_{address.y}.dat");
        if (File.Exists(path)) {
            try {
                byte[] compressedBytes = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(compressedBytes))
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (MemoryStream outMs = new MemoryStream()) {
                    ds.CopyTo(outMs);
                    byte[] finalData = outMs.ToArray();
                    ramCache[address] = finalData;
                    return finalData;
                }
            }
            catch { return null; }
        }
        return null;
    }

    private void BackgroundFlusherLoop() {
        while (!isShuttingDown) {
            if (flushQueue.TryDequeue(out var task)) {
                string finalPath = Path.Combine(saveDirectory, $"Tile_{task.address.z}_{task.address.x}_{task.address.y}.dat");
                string tmpPath = finalPath + ".tmp";

                using (FileStream fs = new FileStream(tmpPath, FileMode.Create))
                using (DeflateStream ds = new DeflateStream(fs, CompressionMode.Compress)) {
                    ds.Write(task.data, 0, task.data.Length);
                }

                if (File.Exists(finalPath)) File.Replace(tmpPath, finalPath, null);
                else File.Move(tmpPath, finalPath);

                pendingDiskWrites.TryRemove(task.address, out _);
            }
            else Thread.Sleep(50);
        }
    }

    public void UploadBytesToVRAMSlot(byte[] data, Vector2Int slot, PhysicalAtlas atlas) {
        // CRITICAL FIX: Match Format!
        Texture2D tempTex = new Texture2D(tileSize, tileSize, TextureFormat.ARGB32, false);
        tempTex.LoadRawTextureData(data);
        tempTex.Apply();

        Graphics.CopyTexture(tempTex, 0, 0, 0, 0, tileSize, tileSize, atlas.Texture, 0, 0, slot.x * tileSize, slot.y * tileSize);
        Destroy(tempTex);
    }

    public void ClearVRAMSlot(Vector2Int slot, PhysicalAtlas atlas) {
        // CRITICAL FIX: Match Format!
        Texture2D blankTex = new Texture2D(tileSize, tileSize, TextureFormat.ARGB32, false);
        blankTex.SetPixels32(clearColors);
        blankTex.Apply();
        Graphics.CopyTexture(blankTex, 0, 0, 0, 0, tileSize, tileSize, atlas.Texture, 0, 0, slot.x * tileSize, slot.y * tileSize);
        Destroy(blankTex);
    }

    public void UnloadFromRAM(Vector3Int address) {
        if (!pendingDiskWrites.ContainsKey(address)) {
            ramCache.TryRemove(address, out _);
        }
    }

    private void OnDestroy() {
        isShuttingDown = true;
        if (ioThread != null && ioThread.IsAlive) ioThread.Join(500);

        if (scratchpadRT != null) {
            scratchpadRT.Release();
            Destroy(scratchpadRT);
        }
    }

    private void OnApplicationQuit() {
        if (!string.IsNullOrEmpty(saveDirectory) && Directory.Exists(saveDirectory)) {
            try { Directory.Delete(saveDirectory, true); }
            catch { }
        }
    }
    // ==========================================
    // MIPMAP GENERATOR
    // ==========================================
    public void GenerateMipmapsForStroke(HashSet<Vector3Int> modifiedLevel0Tiles, System.Action onFullyComplete) {
        if (modifiedLevel0Tiles.Count == 0) {
            onFullyComplete?.Invoke();
            return;
        }

        // Start the async chain at Zoom Level 1
        ProcessMipmapLevel(modifiedLevel0Tiles, 1, onFullyComplete);
    }

    private void ProcessMipmapLevel(HashSet<Vector3Int> childTiles, int targetZ, System.Action onFullyComplete) {
        int maxZoomLevel = canvasManager.tables.Length - 1;

        if (childTiles.Count == 0 || targetZ > maxZoomLevel) {
            onFullyComplete?.Invoke();
            return;
        }

        // 1. Gather all unique parent tiles
        HashSet<Vector3Int> uniqueParents = new HashSet<Vector3Int>();
        foreach (var tile in childTiles) {
            uniqueParents.Add(new Vector3Int(Mathf.FloorToInt(tile.x / 2f), Mathf.FloorToInt(tile.y / 2f), targetZ));
        }

        // Convert to a List so we can process them in batches by index
        List<Vector3Int> parentList = new List<Vector3Int>(uniqueParents);

        // 2. Start baking this level in safe batches!
        BakeMipmapBatch(parentList, 0, targetZ, onFullyComplete);
    }

    private void BakeMipmapBatch(List<Vector3Int> parentTiles, int startIndex, int targetZ, System.Action onFullyComplete) {
        if (startIndex >= parentTiles.Count) {
            // This zoom level is 100% finished! Move up to the next zoom level.
            ProcessMipmapLevel(new HashSet<Vector3Int>(parentTiles), targetZ + 1, onFullyComplete);
            return;
        }

        // CRITICAL FIX: Only bake 10 mipmaps at a time to prevent GPU TDR crashes
        int batchSize = 10;
        int endIndex = Mathf.Min(startIndex + batchSize, parentTiles.Count);
        int pendingBakes = endIndex - startIndex;

        for (int i = startIndex; i < endIndex; i++) {
            BakeMipmapTile(parentTiles[i], () =>
            {
                pendingBakes--;

                // Once this batch finishes downloading from the GPU, trigger the next batch!
                if (pendingBakes <= 0) {
                    BakeMipmapBatch(parentTiles, endIndex, targetZ, onFullyComplete);
                }
            });
        }
    }

    private void BakeMipmapTile(Vector3Int parentAddress, System.Action onTileReadbackComplete) {
        RenderTexture mipmapRT = RenderTexture.GetTemporary(tileSize, tileSize, 0, RenderTextureFormat.ARGB32);
        RenderTexture.active = mipmapRT;
        GL.Clear(true, true, Color.white);

        for (int i = 0; i < 4; i++) {
            int cx = parentAddress.x * 2 + (i % 2);
            int cy = parentAddress.y * 2 + (i / 2);
            Vector3Int childAddress = new Vector3Int(cx, cy, parentAddress.z - 1);

            byte[] childData = GetTileSynchronous(childAddress);
            if (childData != null) {
                Texture2D tex = new Texture2D(tileSize, tileSize, TextureFormat.ARGB32, false);
                tex.LoadRawTextureData(childData);
                tex.filterMode = FilterMode.Bilinear;
                tex.Apply();

                mipmapMaterial.SetTexture("_MainTex", tex);
                mipmapMaterial.SetPass(0);

                float xMin = (i % 2) * 0.5f;
                float yMin = (i / 2) * 0.5f;
                float xMax = xMin + 0.5f;
                float yMax = yMin + 0.5f;

                GL.PushMatrix();
                GL.LoadOrtho();
                GL.Begin(GL.QUADS);
                GL.TexCoord2(0, 0); GL.Vertex3(xMin, yMin, 0);
                GL.TexCoord2(1, 0); GL.Vertex3(xMax, yMin, 0);
                GL.TexCoord2(1, 1); GL.Vertex3(xMax, yMax, 0);
                GL.TexCoord2(0, 1); GL.Vertex3(xMin, yMax, 0);
                GL.End();
                GL.PopMatrix();

                Destroy(tex);
            }
        }
        RenderTexture.active = null;

        activeReadbacks++;
        AsyncGPUReadback.Request(mipmapRT, 0, TextureFormat.ARGB32, (request) =>
        {
            activeReadbacks--;
            RenderTexture.ReleaseTemporary(mipmapRT);

            if (!request.hasError) {
                byte[] data = request.GetData<byte>().ToArray();
                ramCache[parentAddress] = data;
                pendingDiskWrites.TryAdd(parentAddress, 1);
                flushQueue.Enqueue((parentAddress, data));

                backingStore?.OnChunkBaked(new HashSet<Vector3Int> { parentAddress }, -1, false);
            }

            onTileReadbackComplete?.Invoke();
        });
    }
}