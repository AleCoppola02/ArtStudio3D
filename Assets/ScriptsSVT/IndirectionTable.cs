using UnityEngine;

public class IndirectionTable : System.IDisposable
{
    public int ZoomLevel { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    // The actual texture we will pass to the Shader
    public Texture2D TableTexture { get; private set; }

    // A C# array to hold our data so we don't have to read back from the GPU
    private Color32[] pixelData;

    public IndirectionTable(int zoomLevel, int widthInTiles, int heightInTiles) {
        ZoomLevel = zoomLevel;
        Width = widthInTiles;
        Height = heightInTiles;

        // 1. Create the Texture
        TableTexture = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true);

        // CRITICAL: Prevent Unity from blurring our data!
        TableTexture.filterMode = FilterMode.Point;
        TableTexture.wrapMode = TextureWrapMode.Clamp;

        // 2. Initialize our CPU cache
        int totalPixels = Width * Height;
        pixelData = new Color32[totalPixels];

        // 3. Fill the table with "0" (Alpha = 0 means Not Loaded)
        Color32 unloadedColor = new Color32(0, 0, 0, 0);
        for (int i = 0; i < totalPixels; i++) {
            pixelData[i] = unloadedColor;
        }

        // 4. Push the blank data to the GPU
        ApplyChanges();
    }

    // Method to link a Virtual Tile to a Physical Atlas Slot
    public void SetTileMapping(int virtualX, int virtualY, int physicalSlotX, int physicalSlotY) {
        if (virtualX < 0 || virtualX >= Width || virtualY < 0 || virtualY >= Height) return;

        // Find the 1D array index for this 2D coordinate
        int index = virtualY * Width + virtualX;

        // METHOD 1: Pack the slot coordinates into the Red and Green bytes
        // Alpha = 255 flags it as "Loaded" for the shader
        pixelData[index] = new Color32((byte)physicalSlotX, (byte)physicalSlotY, 0, 255); //byte 
    }

    // Method to sever the link (used when a tile is evicted from VRAM)
    public void ClearTileMapping(int virtualX, int virtualY) {
        if (virtualX < 0 || virtualX >= Width || virtualY < 0 || virtualY >= Height) return;

        int index = virtualY * Width + virtualX;

        // Reset to completely blank
        pixelData[index] = new Color32(0, 0, 0, 0);
    }

    // Call this at the end of the frame if you mapped/unmapped any tiles
    public void ApplyChanges() {
        TableTexture.SetPixels32(pixelData);
        TableTexture.Apply(); // This actually sends the data to the GPU!
    }

    public void Dispose() {
        if (TableTexture != null) {
            // Destroy the texture to free up VRAM
            Object.Destroy(TableTexture);
        }
    }
}