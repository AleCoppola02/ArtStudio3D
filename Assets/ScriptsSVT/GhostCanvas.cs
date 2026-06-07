using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

public class GhostCanvas : MonoBehaviour
{
    public int processLimitPerFrame = 10;

    private int activeReadbacks = 0;

    Texture2D blankTex;

    private ConcurrentDictionary<Vector3Int, byte> pendingDiskWrites = new ConcurrentDictionary<Vector3Int, byte>();
    private Dictionary<Vector3Int, List<Vector2>> pendingTileJobs = new Dictionary<Vector3Int, List<Vector2>>();

    private HashSet<Vector3Int> currentStrokeTiles = new HashSet<Vector3Int>();

    private ConcurrentQueue<(Vector3Int address, byte[] data)> diskLoadQueue = new ConcurrentQueue<(Vector3Int, byte[])>();
    private ConcurrentDictionary<Vector3Int, byte> pendingDiskReads = new ConcurrentDictionary<Vector3Int, byte>();

    private class StrokeCountdown { public int count; }

    [Header("Settings")]
    public int tileSize = 256;
    public string saveDirectory;

    [Header("References")]
    public BackingStore backingStore;
    public CanvasManager canvasManager;
    public Material mipmapMaterial;

    private ConcurrentDictionary<Vector3Int, byte[]> ramCache = new ConcurrentDictionary<Vector3Int, byte[]>();
    private ConcurrentDictionary<Vector3Int, byte[]> ghostRamCache = new ConcurrentDictionary<Vector3Int, byte[]>();

    // ==========================================
    // CRITICAL FIX: ELIMINATED CRUCIBLESTATE
    // We now just track the raw RenderTextures directly!
    // ==========================================
    private Dictionary<Vector3Int, RenderTexture> activeCrucibles = new Dictionary<Vector3Int, RenderTexture>();

    private Material stampMaterial;
    public Material mergeMaterial;
    private RenderTexture scratchpadRT;

    private Thread ioThread;
    private bool isShuttingDown = false;
    private ConcurrentQueue<(Vector3Int address, byte[] data)> flushQueue = new ConcurrentQueue<(Vector3Int, byte[])>();
    Color32[] clearColors;

    public void Initialize(Material brushMat, Material inkMat, CanvasManager cm, string saveDir) {
        blankTex = new Texture2D(tileSize, tileSize, TextureFormat.RGBAFloat, false);
        clearColors = new Color32[tileSize * tileSize];
        blankTex.SetPixels32(clearColors);
        blankTex.Apply();
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

        scratchpadRT = new RenderTexture(tileSize, tileSize, 0, RenderTextureFormat.ARGBFloat);
        scratchpadRT.filterMode = FilterMode.Point;
        scratchpadRT.Create();

        ioThread = new Thread(BackgroundFlusherLoop);
        ioThread.Priority = System.Threading.ThreadPriority.BelowNormal;
        ioThread.Start();
    }

    private void Update() {
        while (diskLoadQueue.TryDequeue(out var loadedTile)) {
            //We reach this when a tile finished loading from the hard drive
            //diskLoadQueue handles the camera panning case where we need to load new tiles into the VRAM Atlas
            //so we want to push it to the backing store immediately instead of waiting for a stroke to trigger a refresh
            ramCache[loadedTile.address] = loadedTile.data;
            if (backingStore != null) {
                backingStore.RefreshVisibleTiles(new HashSet<Vector3Int> { loadedTile.address });
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

            int tilesToProcess = Mathf.Min(keys.Count, processLimitPerFrame);

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
            HashSet<Vector3Int> tilesToMipmap = new HashSet<Vector3Int>(currentStrokeTiles);
            currentStrokeTiles.Clear();

            GenerateMipmapsForStroke(tilesToMipmap, () =>
            {
                HashSet<Vector3Int> committedTiles = new HashSet<Vector3Int>();

                foreach (var kvp in ghostRamCache) {
                    ramCache[kvp.Key] = kvp.Value;
                    pendingDiskWrites.TryAdd(kvp.Key, 1);

                    flushQueue.Enqueue((kvp.Key, kvp.Value));
                    committedTiles.Add(kvp.Key);
                }

                ghostRamCache.Clear();
                // Allow the next stroke in the queue to begin processing
                backingStore.FinishCurrentStroke();

                //Trigger one massive VRAM burst-update and release the UI Preview
                backingStore.OnChunkBaked(committedTiles, stroke.strokeID, true);
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

            RenderTexture docRT = GetOrCreateCrucible(tileAddress);

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
                float shiftedX = point.x + (canvasWorldWidth / 2f);
                float shiftedY = point.y + (canvasWorldHeight / 2f);
                float tileWorldX = tileAddress.x * canvasManager.worldUnitsPerTile;
                float tileWorldY = tileAddress.y * canvasManager.worldUnitsPerTile;
                float localX = (shiftedX - tileWorldX) / canvasManager.worldUnitsPerTile;
                float localY = (shiftedY - tileWorldY) / canvasManager.worldUnitsPerTile;
                float xMin = localX - halfBrush;
                float xMax = localX + halfBrush;
                float yMin = localY - halfBrush;
                float yMax = localY + halfBrush;

                GL.TexCoord2(0, 0); GL.Vertex3(xMin, yMin, 0);
                GL.TexCoord2(1, 0); GL.Vertex3(xMax, yMin, 0);
                GL.TexCoord2(1, 1); GL.Vertex3(xMax, yMax, 0);
                GL.TexCoord2(0, 1); GL.Vertex3(xMin, yMax, 0);
            }
            GL.End();
            GL.PopMatrix();

            // ==========================================
            // PASS 2: MERGE TO PERMANENT TILE
            // ==========================================
            RenderTexture.active = docRT;
            GL.Viewport(new Rect(0, 0, tileSize, tileSize));
            mergeMaterial.SetTexture("_MainTex", scratchpadRT);
            mergeMaterial.SetPass(0);

            GL.PushMatrix();
            GL.LoadOrtho();

            GL.Begin(GL.QUADS);
            GL.TexCoord2(0, 0); GL.Vertex3(0, 0, 0);
            GL.TexCoord2(1, 0); GL.Vertex3(1, 0, 0);
            GL.TexCoord2(1, 1); GL.Vertex3(1, 1, 0);
            GL.TexCoord2(0, 1); GL.Vertex3(0, 1, 0);
            GL.End();
            GL.PopMatrix();

            touchedTiles.Add(tileAddress);
            currentStrokeTiles.Add(tileAddress);
            readbacksTriggered++;
        }
        RenderTexture.active = null;

        foreach (Vector3Int address in touchedTiles) {
            RenderTexture docRT = activeCrucibles[address];

            activeReadbacks++; 
            //A readback is when we ask the GPU to send data back to the CPU. This is slow, so we track how many are in flight.
            //this will then be checked before we consider the stroke fully complete, to ensure all data is back in RAM before we finalize the stroke.
            AsyncGPUReadback.Request(docRT, 0, TextureFormat.RGBAFloat, (request) =>
            {
                activeReadbacks--;
                OnCrucibleReadbackComplete(request, address, docRT);
            });
        }
    }

    private void OnCrucibleReadbackComplete(AsyncGPUReadbackRequest request, Vector3Int address, RenderTexture docRT) {
        // Safely free the VRAM and remove from dictionary
        RenderTexture.ReleaseTemporary(docRT);
        activeCrucibles.Remove(address);
        if (!request.hasError) {
            var nativeArray = request.GetData<byte>();
            ghostRamCache[address] = nativeArray.ToArray();
        }
    }

    private RenderTexture GetOrCreateCrucible(Vector3Int address) {
        if (activeCrucibles.ContainsKey(address)) return activeCrucibles[address];

        RenderTexture docRT = RenderTexture.GetTemporary(tileSize, tileSize, 0, RenderTextureFormat.ARGBFloat);
        docRT.filterMode = FilterMode.Point;

        byte[] existingData = GetTileSynchronous(address);
        if (existingData != null) {
            Texture2D tex = new Texture2D(tileSize, tileSize, TextureFormat.RGBAFloat, false);
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

        activeCrucibles[address] = docRT;
        return docRT;
    }

    public byte[] GetTileSynchronous(Vector3Int address) {
        if (ghostRamCache.TryGetValue(address, out byte[] ghostData)) return ghostData;

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

        int endIndex = Mathf.Min(startIndex + processLimitPerFrame, parentTiles.Count);
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

        for (int i = 0; i < 4; i++) {
            int cx = parentAddress.x * 2 + (i % 2);
            int cy = parentAddress.y * 2 + (i / 2);
            Vector3Int childAddress = new Vector3Int(cx, cy, parentAddress.z - 1);

            byte[] childData = GetTileSynchronous(childAddress);
            if (childData != null) {
                Texture2D tex = new Texture2D(tileSize, tileSize, TextureFormat.RGBAFloat, false);
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

    // ==========================================
    // MEMORY & VRAM MANAGEMENT
    // ==========================================

    public void ClearVRAMSlot(Vector2Int slot, PhysicalAtlas atlas) {
        Graphics.CopyTexture(blankTex, 0, 0, 0, 0, tileSize, tileSize, atlas.Texture, 0, 0, slot.x * tileSize, slot.y * tileSize);
    }

    public void UploadBytesToVRAMSlot(byte[] data, Vector2Int slot, PhysicalAtlas atlas) {
        Texture2D tempTex = new Texture2D(tileSize, tileSize, TextureFormat.RGBAFloat, false);
        tempTex.LoadRawTextureData(data);
        tempTex.Apply();

        Graphics.CopyTexture(tempTex, 0, 0, 0, 0, tileSize, tileSize, atlas.Texture, 0, 0, slot.x * tileSize, slot.y * tileSize);
        Destroy(tempTex);
    }

    public void UnloadFromRAM(Vector3Int address) {
        if (!pendingDiskWrites.ContainsKey(address)) {
            ramCache.TryRemove(address, out _);
        }
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

    private void OnApplicationQuit() {
        if (!string.IsNullOrEmpty(saveDirectory) && Directory.Exists(saveDirectory)) {
            try { Directory.Delete(saveDirectory, true); }
            catch { }
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
}