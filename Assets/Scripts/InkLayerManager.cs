using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class InkLayerManager : MonoBehaviour
{
    // --- INNER CLASS FOR THE POOL ---
    private class PreviewLayer
    {
        public int strokeID;
        public RenderTexture rt;
        public RawImage uiImage;
        public Material uiMaterialInstance;
        public bool isActive;
    }
    [Header("UI Connections")]
    [Tooltip("This RawImage will be used as a template to spawn new overlapping layers as needed.")]
    public RawImage previewOverlayTemplate;

    [Header("Material & Settings")]
    public Material previewMaterial;
    [Range(0, 1)] public float opacity = 0.5f;

    private List<PreviewLayer> layerPool = new List<PreviewLayer>();
    private int strokeCounter = 0;
    private Color clearColor = new Color(0, 0, 0, 0); // Perfectly transparent

    private void Awake() {
        // Hide the template so it doesn't show up as an empty box
        if (previewOverlayTemplate != null) {
            previewOverlayTemplate.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// Called by BrushManager when the user first touches the screen.
    /// Grabs an available UI overlay texture so the stroke can be drawn without lag.
    /// </summary>
    public (int strokeID, RenderTexture rt) RequestNewPreviewLayer(BlendModeConfig blendMode, float strokeOpacity) {
        PreviewLayer layer = GetOrCreateLayerFromPool();

        layer.strokeID = ++strokeCounter;
        layer.isActive = true;
        layer.uiImage.gameObject.SetActive(true);

        // Wipe it clean just in case
        RenderTexture.active = layer.rt;
        GL.Clear(true, true, clearColor);
        RenderTexture.active = null;

        if (layer.uiMaterialInstance != null) {
            // Apply the correct blend mode
            if (blendMode != null) blendMode.SetBlendMode(layer.uiMaterialInstance);

            // NEW: Apply the Opacity limit!
            layer.uiMaterialInstance.SetFloat("_Opacity", strokeOpacity);
        }

        return (layer.strokeID, layer.rt);
    }

    /// <summary>
    /// Called when BackingStore completely finishes saving a stroke to the SVT/Disk.
    /// We can now safely wipe the UI preview, letting the underlying SVT show the permanent data.
    /// </summary>
    public void ReleaseStrokeLayer(int strokeID) {
        PreviewLayer layer = layerPool.Find(l => l.strokeID == strokeID && l.isActive);
        if (layer != null) {
            // Wipe the texture to free memory
            RenderTexture.active = layer.rt;
            GL.Clear(true, true, clearColor);
            RenderTexture.active = null;

            // Deactivate and return to pool
            layer.isActive = false;
            layer.uiImage.gameObject.SetActive(false);
        }
    }

    // --- POOL MANAGEMENT ---

    private PreviewLayer GetOrCreateLayerFromPool() {
        // 1. Try to find a sleeping layer
        foreach (var layer in layerPool) {
            if (!layer.isActive) return layer;
        }

        // 2. If all layers are currently being drawn on/saving, create a new one!
        PreviewLayer newLayer = new PreviewLayer();

        // Create the fullscreen render texture
        newLayer.rt = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
        newLayer.rt.filterMode = FilterMode.Point;
        newLayer.rt.Create();

        // Clone the UI Image so it stacks on top
        if (previewOverlayTemplate != null) {
            GameObject cloneObj = Instantiate(previewOverlayTemplate.gameObject, previewOverlayTemplate.transform.parent);
            newLayer.uiImage = cloneObj.GetComponent<RawImage>();
            newLayer.uiImage.texture = newLayer.rt;

            // We need a unique material instance per layer, so 
            // concurrent strokes can have different blend modes/opacities
            if (previewMaterial != null) {
                newLayer.uiMaterialInstance = new Material(previewMaterial);
                newLayer.uiMaterialInstance.SetTexture("_MainTex", newLayer.rt);
                newLayer.uiImage.material = newLayer.uiMaterialInstance;
                newLayer.uiMaterialInstance.SetFloat("_Opacity", opacity);
            }
        }

        layerPool.Add(newLayer);
        return newLayer;
    }

    // --- UTILITIES ---

    public void SetOpacity(float opacity) {
        this.opacity = opacity;
        foreach (var layer in layerPool) {
            if (layer.uiMaterialInstance != null) {
                layer.uiMaterialInstance.SetFloat("_Opacity", opacity);
            }
        }
    }

    public void ClearAllActiveLayers() {
        foreach (var layer in layerPool) {
            if (layer.isActive) {
                ReleaseStrokeLayer(layer.strokeID);
            }
        }
    }
}