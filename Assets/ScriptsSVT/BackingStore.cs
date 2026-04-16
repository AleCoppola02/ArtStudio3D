using System.Collections.Generic;
using UnityEngine;

public class BackingStore
{
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

    public BackingStore(PhysicalAtlas physicalAtlas, IndirectionTable[] tables) {
        tileDatabase = new Dictionary<Vector3Int, TileState>();
        activeTiles = new List<TileState>();
        atlas = physicalAtlas;
        indirectionTables = tables;
    }

    // --------------------------------------------------------
    // CORE FUNCTION: The CPU asks for a tile
    // --------------------------------------------------------
    public void RequestTile(int x, int y, int zoomLevel) {
        Vector3Int tileAddress = new Vector3Int(x, y, zoomLevel);

        // 1. If we've never seen this tile before, create a blank record for it
        if (!tileDatabase.ContainsKey(tileAddress)) {
            tileDatabase.Add(tileAddress, new TileState());
        }

        TileState tile = tileDatabase[tileAddress];

        // Update the accesscounter so the engine knows this tile is actively being used
        accessCounter++;
        tile.LastAccessTime = accessCounter; // Change

        // 2. If it's already in the GPU, do nothing! We're good.
        if (tile.IsLoaded) return;

        // 3. If it's NOT loaded, we have a "Page Fault". We must load it.
        LoadTileIntoVRAM(tileAddress, tile);
    }

    // --------------------------------------------------------
    // THE LOADING LOGIC
    // --------------------------------------------------------
    private void LoadTileIntoVRAM(Vector3Int address, TileState tile) {
        // 1. Ask the Atlas for an empty parking spot
        Vector2Int? slot = atlas.AllocateSlot();

        if (slot == null) {
            // THE ATLAS IS FULL! We must kick an old tile out to make room.
            EvictOldestTile();

            // Now try again
            slot = atlas.AllocateSlot();
        }

        // --- NEW SAFETY CHECK ---
        if (slot == null) {
            // If it's STILL null, we are in a completely deadlocked state.
            // Log an error so we can debug it, but abort gracefully instead of crashing.
            Debug.LogError($"CRITICAL: Physical Atlas deadlock! Could not find or free a slot for tile {address}.");
            return;
        }
        // ------------------------

        tile.PhysicalSlot = slot.Value;

        // 2. TODO: Load the raw pixels from the Hard Drive / RAM into the Atlas at this slot
        // LoadPixelsToAtlas(address, tile.PhysicalSlot);

        // 3. Update the GPU Map (The Indirection Table)
        indirectionTables[address.z].SetTileMapping(address.x, address.y, tile.PhysicalSlot.x, tile.PhysicalSlot.y);

        // 4. Mark it as loaded and add to active list (from our previous fix)
        tile.IsLoaded = true;
        tile.Address = address;
        activeTiles.Add(tile);
    }

    // --------------------------------------------------------
    // THE EVICTION LOGIC (Least Recently Used)
    // --------------------------------------------------------
    private void EvictOldestTile() {
        TileState oldestTile = null;
        ulong oldestTime = ulong.MaxValue;

        // NEW LOOP: Only iterate through the max ~256 tiles actually in VRAM
        foreach (TileState activeTile in activeTiles) {
            if (activeTile.LastAccessTime < oldestTime) {
                oldestTime = activeTile.LastAccessTime;
                oldestTile = activeTile;
            }
        }

        if (oldestTile != null) {
            Vector3Int oldestAddress = oldestTile.Address; // Grab the address we saved

            // 1. If the user drew on this tile, save it
            if (oldestTile.IsDirty) {
                // SaveTileToHardDrive(oldestAddress, oldestTile.PhysicalSlot);
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
}