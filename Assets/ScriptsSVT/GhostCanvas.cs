using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

public class GhostCanvas : MonoBehaviour
{
    [Header("Processing Settings")]
    public int processLimitPerFrame = 10;
    private int activeReadbacks = 0;

    [Header("References")]
    public int tileSize = 256;
    public string saveDirectory;
    public BackingStore backingStore;
    public CanvasManager canvasManager;
    public Material mipmapMaterial;
    public Material mergeMaterial;
    private Material stampMaterial;
    private RenderTexture scratchpadRT;

    private Texture2D blankTex;
    private Color32[] clearColors;

    // RAM Management
    private TileLRU tileCache;
    private ConcurrentDictionary<Vector3Int, byte[]> flushingCache = new ConcurrentDictionary<Vector3Int, byte[]>();
    private ConcurrentDictionary<Vector3Int, byte> pendingDiskWrites = new ConcurrentDictionary<Vector3Int, byte>();

    private Dictionary<Vector3Int, List<Vector2>> pendingTileJobs = new Dictionary<Vector3Int, List<Vector2>>();
    private HashSet<Vector3Int> currentStrokeTiles = new HashSet<Vector3Int>();

    private ConcurrentDictionary<Vector3Int, byte[]> ghostRamCache = new ConcurrentDictionary<Vector3Int, byte[]>();
    private Dictionary<Vector3Int, RenderTexture> activeCrucibles = new Dictionary<Vector3Int, RenderTexture>();

    private ConcurrentQueue<(Vector3Int address, byte[] data)> diskLoadQueue = new ConcurrentQueue<(Vector3Int, byte[])>();
    private ConcurrentQueue<Vector3Int> diskLoadFailedQueue = new ConcurrentQueue<Vector3Int>();
    private ConcurrentDictionary<Vector3Int, byte> pendingDiskReads = new ConcurrentDictionary<Vector3Int, byte>();
    private ConcurrentQueue<(Vector3Int address, byte[] data)> flushQueue = new ConcurrentQueue<(Vector3Int, byte[])>();

    private Thread ioThread;
    private bool isShuttingDown = false;

    private Texture2D[] stagingTextures;
    private Mesh batchMesh;
    private List<Vector3> batchVertices = new List<Vector3>();
    private List<Vector2> batchUVs = new List<Vector2>();
    private List<int> batchIndices = new List<int>();

    private HashSet<Vector3Int> tempRequiredTiles = new HashSet<Vector3Int>();
    private Stack<List<Vector2>> listPool = new Stack<List<Vector2>>();
    private Dictionary<Vector3Int, List<Vector2>> chunkToBake = new Dictionary<Vector3Int, List<Vector2>>();

    private static readonly SemaphoreSlim diskIOMutex = new SemaphoreSlim(4, 4);
    private static readonly object[] fileLocks = new object[256];

    static GhostCanvas() {
        for (int i = 0; i < fileLocks.Length; i++) fileLocks[i] = new object();
    }

    private object GetFileLock(Vector3Int address) {
        return fileLocks[(address.GetHashCode() & 0x7FFFFFFF) % fileLocks.Length];
    }

    public void Initialize(Material brushMat, Material inkMat, CanvasManager cm, string saveDir) {
        blankTex = new Texture2D(tileSize, tileSize, TextureFormat.RGBAFloat, false);
        clearColors = new Color32[tileSize * tileSize];
        for (int i = 0; i < clearColors.Length; i++) clearColors[i] = new Color32(255, 255, 255, 255);
        blankTex.SetPixels32(clearColors);
        blankTex.Apply();

        stampMaterial = brushMat;
        mergeMaterial = inkMat;
        canvasManager = cm;
        saveDirectory = saveDir;

        if (Directory.Exists(saveDirectory)) {
            Directory.Delete(saveDirectory, true);
        }
        Directory.CreateDirectory(saveDirectory);

        scratchpadRT = new RenderTexture(tileSize, tileSize, 0, RenderTextureFormat.ARGBFloat);
        scratchpadRT.filterMode = FilterMode.Point;
        scratchpadRT.Create();

        stagingTextures = new Texture2D[4];
        for (int i = 0; i < 4; i++) {
            stagingTextures[i] = new Texture2D(tileSize, tileSize, TextureFormat.RGBAFloat, false);
            stagingTextures[i].filterMode = FilterMode.Bilinear;
        }

        batchMesh = new Mesh();
        batchMesh.MarkDynamic();

        InitializeMemoryManager();

        ioThread = new Thread(BackgroundFlusherLoop);
        ioThread.Priority = System.Threading.ThreadPriority.BelowNormal;
        ioThread.Start();
    }

    private void InitializeMemoryManager() {
        int w = canvasManager.canvasWidthInTiles > 0 ? canvasManager.canvasWidthInTiles : 32;
        int h = canvasManager.canvasHeightInTiles > 0 ? canvasManager.canvasHeightInTiles : 32;
        int maxTiles = w * h;

        int mipmapTiles = 0;
        int mw = w;
        int mh = h;
        while (mw > 1 || mh > 1) {
            mw = Mathf.Max(1, Mathf.CeilToInt(mw / 2f));
            mh = Mathf.Max(1, Mathf.CeilToInt(mh / 2f));
            mipmapTiles += (mw * mh);
        }

        int totalExpectedTiles = maxTiles + mipmapTiles + 50;
        long bytesPerTile = tileSize * tileSize * 16;
        long expectedRam = totalExpectedTiles * bytesPerTile;

        long systemRamBytes = (long)SystemInfo.systemMemorySize * 1024L * 1024L;
        long ramLimit = systemRamBytes / 3; // Allows using up to ~33% of total system RAM for tile cache

        int maxCacheTiles = (int)(Math.Min(expectedRam, ramLimit) / bytesPerTile);
        if (maxCacheTiles < 128) maxCacheTiles = 128; // Ensure a fallback minimum of ~128MB cache

        tileCache = new TileLRU(maxCacheTiles);
        tileCache.OnEvictDirty = (key, data) => {
            flushingCache[key] = data; // Keep tracking it safely so we don't drop frames if immediately requested
            pendingDiskWrites.TryAdd(key, 1);
            flushQueue.Enqueue((key, data));
        };
    }

    private void Update() {
        int fetchedThisFrame = 0;
        while (diskLoadQueue.TryDequeue(out var loadedTile)) {
            tileCache.Put(loadedTile.address, loadedTile.data, false);
            if (backingStore != null) {
                backingStore.RefreshVisibleTiles(new HashSet<Vector3Int> { loadedTile.address });
            }
            fetchedThisFrame++;
            if (fetchedThisFrame >= 4) break; // Optional cap for VRAM uploads
        }

        while (diskLoadFailedQueue.TryDequeue(out var failedAddress)) {
            if (backingStore != null) {
                backingStore.MarkTileAsUnloaded(failedAddress);
            }
        }

        if (backingStore == null) return;
        StrokeBuffer stroke = backingStore.PeekNextStroke();
        if (stroke == null) return;

        while (stroke.stampQueue.Count > 0) {
            Vector2 stamp = stroke.stampQueue.Dequeue();
            tempRequiredTiles.Clear();
            AddTilesForStamp(stamp, stroke.brushSize, tempRequiredTiles);

            foreach (var t in tempRequiredTiles) {
                if (!pendingTileJobs.TryGetValue(t, out List<Vector2> stampList)) {
                    stampList = GetListFromPool();
                    pendingTileJobs[t] = stampList;
                }
                stampList.Add(stamp);
            }
        }

        if (!stroke.isStrokeFinished) return;

        if (pendingTileJobs.Count > 0) {
            chunkToBake.Clear();
            int tilesProcessed = 0;

            foreach (var kvp in pendingTileJobs) {
                chunkToBake.Add(kvp.Key, kvp.Value);
                tilesProcessed++;
                if (tilesProcessed >= processLimitPerFrame) break;
            }

            foreach (var tile in chunkToBake.Keys) {
                pendingTileJobs.Remove(tile);
            }

            ProcessTileBatch(chunkToBake, stroke);

            foreach (var kvp in chunkToBake) {
                ReturnListToPool(kvp.Value);
            }
            chunkToBake.Clear();
        }

        bool isCompletelyDone = (stroke.stampQueue.Count == 0 &&
                                 stroke.isStrokeFinished &&
                                 pendingTileJobs.Count == 0 &&
                                 activeReadbacks == 0);

        if (isCompletelyDone) {
            HashSet<Vector3Int> tilesToMipmap = new HashSet<Vector3Int>(currentStrokeTiles);
            currentStrokeTiles.Clear();

            GenerateMipmapsForStroke(tilesToMipmap, () =>
            {
                HashSet<Vector3Int> committedTiles = new HashSet<Vector3Int>();

                foreach (var kvp in ghostRamCache) {
                    // Update cache as dirty (so it flushes when evicted)
                    tileCache.Put(kvp.Key, kvp.Value, true);
                    committedTiles.Add(kvp.Key);
                }

                ghostRamCache.Clear();

                backingStore.FinishCurrentStroke();
                backingStore.OnChunkBaked(committedTiles, stroke.strokeID, true);
            });
        }
    }

    private void ProcessTileBatch(Dictionary<Vector3Int, List<Vector2>> batch, StrokeBuffer stroke) {
        stampMaterial.SetFloat("_Flow", stroke.flow);
        if (stroke.blendMode != null) stroke.blendMode.SetBlendMode(mergeMaterial);
        mergeMaterial.SetFloat("_Opacity", stroke.opacity);

        float canvasWorldWidthHalf = (canvasManager.canvasWidthInTiles * canvasManager.worldUnitsPerTile) / 2f;
        float canvasWorldHeightHalf = (canvasManager.canvasHeightInTiles * canvasManager.worldUnitsPerTile) / 2f;
        float inverseUnits = 1f / canvasManager.worldUnitsPerTile;
        float halfBrush = (stroke.brushSize * inverseUnits) / 2f;

        HashSet<Vector3Int> touchedTiles = new HashSet<Vector3Int>();

        foreach (var kvp in batch) {
            Vector3Int tileAddress = kvp.Key;
            List<Vector2> stampsForThisTile = kvp.Value;

            RenderTexture docRT = GetOrCreateCrucible(tileAddress);

            RenderTexture.active = scratchpadRT;
            GL.Clear(true, true, Color.clear);
            GL.Viewport(new Rect(0, 0, tileSize, tileSize));

            stampMaterial.SetPass(0);
            GL.PushMatrix();
            GL.LoadOrtho();

            batchVertices.Clear();
            batchUVs.Clear();
            batchIndices.Clear();
            int vCount = 0;

            float tileWorldX = tileAddress.x * canvasManager.worldUnitsPerTile;
            float tileWorldY = tileAddress.y * canvasManager.worldUnitsPerTile;

            foreach (Vector2 point in stampsForThisTile) {
                float shiftedX = point.x + canvasWorldWidthHalf;
                float shiftedY = point.y + canvasWorldHeightHalf;
                float localX = (shiftedX - tileWorldX) * inverseUnits;
                float localY = (shiftedY - tileWorldY) * inverseUnits;

                float xMin = localX - halfBrush;
                float xMax = localX + halfBrush;
                float yMin = localY - halfBrush;
                float yMax = localY + halfBrush;

                batchVertices.Add(new Vector3(xMin, yMin, 0));
                batchVertices.Add(new Vector3(xMax, yMin, 0));
                batchVertices.Add(new Vector3(xMax, yMax, 0));
                batchVertices.Add(new Vector3(xMin, yMax, 0));

                batchUVs.Add(new Vector2(0, 0));
                batchUVs.Add(new Vector2(1, 0));
                batchUVs.Add(new Vector2(1, 1));
                batchUVs.Add(new Vector2(0, 1));

                batchIndices.Add(vCount);
                batchIndices.Add(vCount + 1);
                batchIndices.Add(vCount + 2);
                batchIndices.Add(vCount + 3);
                vCount += 4;

                if (vCount >= 65000) {
                    batchMesh.Clear();
                    batchMesh.SetVertices(batchVertices);
                    batchMesh.SetUVs(0, batchUVs);
                    batchMesh.SetIndices(batchIndices, MeshTopology.Quads, 0);
                    Graphics.DrawMeshNow(batchMesh, Matrix4x4.identity);

                    batchVertices.Clear(); batchUVs.Clear(); batchIndices.Clear();
                    vCount = 0;
                }
            }

            if (vCount > 0) {
                batchMesh.Clear();
                batchMesh.SetVertices(batchVertices);
                batchMesh.SetUVs(0, batchUVs);
                batchMesh.SetIndices(batchIndices, MeshTopology.Quads, 0);
                Graphics.DrawMeshNow(batchMesh, Matrix4x4.identity);
            }
            GL.PopMatrix();

            Graphics.Blit(scratchpadRT, docRT, mergeMaterial, 0);

            touchedTiles.Add(tileAddress);
            currentStrokeTiles.Add(tileAddress);
        }
        RenderTexture.active = null;

        foreach (Vector3Int address in touchedTiles) {
            RenderTexture docRT = activeCrucibles[address];

            activeReadbacks++;
            AsyncGPUReadback.Request(docRT, 0, TextureFormat.RGBAFloat, (request) =>
            {
                activeReadbacks--;
                OnCrucibleReadbackComplete(request, address, docRT);
            });
        }
    }

    private void OnCrucibleReadbackComplete(AsyncGPUReadbackRequest request, Vector3Int address, RenderTexture docRT) {
        RenderTexture.ReleaseTemporary(docRT);
        activeCrucibles.Remove(address);
        if (!request.hasError) {
            ghostRamCache[address] = request.GetData<byte>().ToArray();
        }
    }

    private RenderTexture GetOrCreateCrucible(Vector3Int address) {
        if (activeCrucibles.TryGetValue(address, out RenderTexture existingRT)) return existingRT;

        RenderTexture docRT = RenderTexture.GetTemporary(tileSize, tileSize, 0, RenderTextureFormat.ARGBFloat);
        docRT.filterMode = FilterMode.Point;

        byte[] existingData = GetTileSynchronous(address);
        int expectedLength = tileSize * tileSize * 16;

        if (existingData != null && existingData.Length == expectedLength) {
            Texture2D tex = stagingTextures[0];
            tex.LoadRawTextureData(existingData);
            tex.Apply();
            Graphics.Blit(tex, docRT);
        }
        else {
            RenderTexture.active = docRT;
            GL.Clear(true, true, Color.white);
            RenderTexture.active = null;
        }

        activeCrucibles[address] = docRT;
        return docRT;
    }

    public byte[] GetTileSynchronous(Vector3Int address) {
        if (ghostRamCache.TryGetValue(address, out byte[] ghostData)) return ghostData;
        if (tileCache.TryGet(address, out byte[] data)) return data;

        // Failsafe grab if we evicted it and it's currently saving
        if (flushingCache.TryGetValue(address, out byte[] evictData)) {
            tileCache.Put(address, evictData, false);
            return evictData;
        }

        string path = Path.Combine(saveDirectory, $"Tile_{address.z}_{address.x}_{address.y}.dat");
        if (File.Exists(path)) {
            int retries = 10;
            while (retries > 0) {
                try {
                    int expectedSize = tileSize * tileSize * 16;
                    byte[] finalData = new byte[expectedSize];

                    lock (GetFileLock(address)) {
                        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (DeflateStream ds = new DeflateStream(fs, CompressionMode.Decompress)) {
                            int read = 0;
                            while (read < expectedSize) {
                                int bytesRead = ds.Read(finalData, read, expectedSize - read);
                                if (bytesRead == 0) break;
                                read += bytesRead;
                            }

                            if (read == expectedSize) {
                                tileCache.Put(address, finalData, false);
                                return finalData;
                            }
                            else {
                                return null;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                    retries--;
                    if (retries > 0) Thread.Sleep(50);
                }
                catch (Exception) { return null; }
            }
        }
        return null;
    }

    private void BackgroundFlusherLoop() {
        while (!isShuttingDown) {
            if (flushQueue.TryDequeue(out var task)) {
                string finalPath = Path.Combine(saveDirectory, $"Tile_{task.address.z}_{task.address.x}_{task.address.y}.dat");
                string tmpPath = finalPath + ".tmp";

                try {
                    using (FileStream fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (DeflateStream ds = new DeflateStream(fs, CompressionMode.Compress)) {
                        ds.Write(task.data, 0, task.data.Length);
                    }

                    lock (GetFileLock(task.address)) {
                        if (File.Exists(finalPath)) File.Replace(tmpPath, finalPath, null);
                        else File.Move(tmpPath, finalPath);
                    }

                    pendingDiskWrites.TryRemove(task.address, out _);
                    flushingCache.TryRemove(task.address, out _);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                    flushQueue.Enqueue(task);
                    Thread.Sleep(50);
                }
                catch (Exception ex) {
                    Debug.LogError("Error saving evicted tile: " + ex.Message);
                    pendingDiskWrites.TryRemove(task.address, out _);
                    flushingCache.TryRemove(task.address, out _);
                }
            }
            else {
                Thread.Sleep(50);
            }
        }
    }

    public void GenerateMipmapsForStroke(HashSet<Vector3Int> modifiedLevel0Tiles, System.Action onFullyComplete) {
        if (modifiedLevel0Tiles.Count == 0) {
            onFullyComplete?.Invoke();
            return;
        }
        ProcessMipmapLevel(modifiedLevel0Tiles, 1, onFullyComplete);
    }

    private void ProcessMipmapLevel(HashSet<Vector3Int> childTiles, int targetZ, System.Action onFullyComplete) {
        int maxZoomLevel = canvasManager.tables.Length - 1;

        if (childTiles.Count == 0 || targetZ > maxZoomLevel) {
            onFullyComplete?.Invoke();
            return;
        }

        HashSet<Vector3Int> uniqueParents = new HashSet<Vector3Int>();
        foreach (var tile in childTiles) {
            uniqueParents.Add(new Vector3Int(Mathf.FloorToInt(tile.x / 2f), Mathf.FloorToInt(tile.y / 2f), targetZ));
        }

        List<Vector3Int> parentList = new List<Vector3Int>(uniqueParents);
        BakeMipmapBatch(parentList, 0, targetZ, onFullyComplete);
    }

    private void BakeMipmapBatch(List<Vector3Int> parentTiles, int startIndex, int targetZ, System.Action onFullyComplete) {
        if (startIndex >= parentTiles.Count) {
            ProcessMipmapLevel(new HashSet<Vector3Int>(parentTiles), targetZ + 1, onFullyComplete);
            return;
        }

        int mipmapLimit = Mathf.Max(1, processLimitPerFrame / 4);
        int endIndex = Mathf.Min(startIndex + mipmapLimit, parentTiles.Count);
        int pendingBakes = endIndex - startIndex;

        for (int i = startIndex; i < endIndex; i++) {
            BakeMipmapTile(parentTiles[i], () =>
            {
                pendingBakes--;
                if (pendingBakes <= 0) {
                    BakeMipmapBatch(parentTiles, endIndex, targetZ, onFullyComplete);
                }
            });
        }
    }

    private void BakeMipmapTile(Vector3Int parentAddress, System.Action onTileReadbackComplete) {
        RenderTexture mipmapRT = RenderTexture.GetTemporary(tileSize, tileSize, 0, RenderTextureFormat.ARGBFloat);
        RenderTexture.active = mipmapRT;
        GL.Clear(true, true, Color.white);

        int expectedLength = tileSize * tileSize * 16;

        for (int i = 0; i < 4; i++) {
            int cx = parentAddress.x * 2 + (i % 2);
            int cy = parentAddress.y * 2 + (i / 2);
            Vector3Int childAddress = new Vector3Int(cx, cy, parentAddress.z - 1);

            byte[] childData = GetTileSynchronous(childAddress);
            if (childData != null && childData.Length == expectedLength) {
                Texture2D tex = stagingTextures[i];
                tex.LoadRawTextureData(childData);
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
            }
        }
        RenderTexture.active = null;

        activeReadbacks++;
        AsyncGPUReadback.Request(mipmapRT, 0, TextureFormat.RGBAFloat, (request) =>
        {
            activeReadbacks--;
            RenderTexture.ReleaseTemporary(mipmapRT);

            if (!request.hasError) {
                ghostRamCache[parentAddress] = request.GetData<byte>().ToArray();
            }

            onTileReadbackComplete?.Invoke();
        });
    }

    public void ClearVRAMSlot(Vector2Int slot, PhysicalAtlas atlas) {
        Graphics.CopyTexture(blankTex, 0, 0, 0, 0, tileSize, tileSize, atlas.Texture, 0, 0, slot.x * tileSize, slot.y * tileSize);
    }

    public void UploadBytesToVRAMSlot(byte[] data, Vector2Int slot, PhysicalAtlas atlas) {
        if (data == null || data.Length != tileSize * tileSize * 16) return;

        Texture2D tempTex = stagingTextures[0];
        tempTex.LoadRawTextureData(data);
        tempTex.Apply();

        Graphics.CopyTexture(tempTex, 0, 0, 0, 0, tileSize, tileSize, atlas.Texture, 0, 0, slot.x * tileSize, slot.y * tileSize);
    }

    public void UnloadFromRAM(Vector3Int address) {
        // Now entirely handled dynamically by the LRU logic to avoid expensive disk reloading 
        // when the user is rapidly panning around. Memory stays safe naturally.
    }

    public byte[] TryGetTileAsync(Vector3Int address) {
        if (tileCache.TryGet(address, out byte[] data)) return data;
        if (flushingCache.TryGetValue(address, out byte[] evictData)) {
            tileCache.Put(address, evictData, false);
            return evictData;
        }

        string path = Path.Combine(saveDirectory, $"Tile_{address.z}_{address.x}_{address.y}.dat");
        if (File.Exists(path)) {
            if (pendingDiskReads.TryAdd(address, 1)) {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await diskIOMutex.WaitAsync();
                    try {
                        int expectedSize = tileSize * tileSize * 16;
                        byte[] finalData = new byte[expectedSize];
                        bool success = false;
                        int retries = 10;

                        while (!success && retries > 0) {
                            try {
                                lock (GetFileLock(address)) {
                                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                    using (DeflateStream ds = new DeflateStream(fs, CompressionMode.Decompress)) {
                                        int read = 0;
                                        while (read < expectedSize) {
                                            int bytesRead = ds.Read(finalData, read, expectedSize - read);
                                            if (bytesRead == 0) break;
                                            read += bytesRead;
                                        }
                                        if (read == expectedSize) {
                                            diskLoadQueue.Enqueue((address, finalData));
                                            success = true;
                                        }
                                        else {
                                            break;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                                retries--;
                                if (retries > 0) await System.Threading.Tasks.Task.Delay(50);
                            }
                            catch (Exception) { break; }
                        }

                        if (!success) {
                            diskLoadFailedQueue.Enqueue(address);
                        }
                    }
                    catch {
                        diskLoadFailedQueue.Enqueue(address);
                    }
                    finally {
                        diskIOMutex.Release();
                        pendingDiskReads.TryRemove(address, out _);
                    }
                });
            }
        }
        return null;
    }

    private void AddTilesForStamp(Vector2 stampPos, float brushSize, HashSet<Vector3Int> tiles) {
        float radius = brushSize / 2f;
        Vector2Int min2D = canvasManager.WorldToTileCoordinate(new Vector2(stampPos.x - radius, stampPos.y - radius));
        Vector2Int max2D = canvasManager.WorldToTileCoordinate(new Vector2(stampPos.x + radius, stampPos.y + radius));

        for (int tx = min2D.x; tx <= max2D.x; tx++) {
            for (int ty = min2D.y; ty <= max2D.y; ty++) {
                tiles.Add(new Vector3Int(tx, ty, 0));
            }
        }
    }

    private List<Vector2> GetListFromPool() {
        if (listPool.Count > 0) return listPool.Pop();
        return new List<Vector2>();
    }

    private void ReturnListToPool(List<Vector2> list) {
        list.Clear();
        listPool.Push(list);
    }

    private void OnApplicationQuit() {
        if (!string.IsNullOrEmpty(saveDirectory) && Directory.Exists(saveDirectory)) {
            try { Directory.Delete(saveDirectory, true); } catch { }
        }
    }

    private void OnDestroy() {
        isShuttingDown = true;
        if (ioThread != null && ioThread.IsAlive) ioThread.Join(500);

        if (scratchpadRT != null) {
            scratchpadRT.Release();
            Destroy(scratchpadRT);
        }
        if (batchMesh != null) Destroy(batchMesh);
        if (stagingTextures != null) {
            for (int i = 0; i < stagingTextures.Length; i++) {
                if (stagingTextures[i] != null) Destroy(stagingTextures[i]);
            }
        }
    }

    // Core LRU Thread-Safe Cache logic implementation
    private class TileLRU
    {
        private int capacity;
        private LinkedList<Vector3Int> lru = new LinkedList<Vector3Int>();
        private Dictionary<Vector3Int, LinkedListNode<Vector3Int>> cache = new Dictionary<Vector3Int, LinkedListNode<Vector3Int>>();
        private Dictionary<Vector3Int, byte[]> data = new Dictionary<Vector3Int, byte[]>();
        private HashSet<Vector3Int> dirty = new HashSet<Vector3Int>();
        private object sync = new object();

        public Action<Vector3Int, byte[]> OnEvictDirty;

        public TileLRU(int maxCapacity) {
            this.capacity = maxCapacity;
        }

        public void Put(Vector3Int key, byte[] val, bool isDirty) {
            byte[] evictedData = null;
            Vector3Int evictedKey = default;
            bool evictDirty = false;

            lock (sync) {
                if (cache.TryGetValue(key, out var node)) {
                    lru.Remove(node);
                    lru.AddFirst(node);
                    data[key] = val;
                    if (isDirty) dirty.Add(key);
                    return;
                }

                if (cache.Count >= capacity) {
                    var last = lru.Last;
                    evictedKey = last.Value;
                    evictedData = data[evictedKey];
                    evictDirty = dirty.Contains(evictedKey);

                    lru.RemoveLast();
                    cache.Remove(evictedKey);
                    data.Remove(evictedKey);
                    dirty.Remove(evictedKey);
                }

                var newNode = new LinkedListNode<Vector3Int>(key);
                lru.AddFirst(newNode);
                cache[key] = newNode;
                data[key] = val;
                if (isDirty) dirty.Add(key);
            }

            if (evictDirty && evictedData != null) {
                OnEvictDirty?.Invoke(evictedKey, evictedData);
            }
        }

        public bool TryGet(Vector3Int key, out byte[] val) {
            lock (sync) {
                if (cache.TryGetValue(key, out var node)) {
                    lru.Remove(node);
                    lru.AddFirst(node);
                    val = data[key];
                    return true;
                }
                val = null;
                return false;
            }
        }
    }
}
