using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI; // REQUIRED: Lets us talk to the UI RawImage

public class InkLayerManager : MonoBehaviour
{
    [Header("UI Connections")]
    public RawImage previewOverlay; // Drag your new UI LivePreviewOverlay here!

    [Header("Material & Settings")]
    public Material inkLayerMaterial;
    [Range(0, 1)] public float opacity = 0.5f;
    
    // This is now strictly managed by code, no longer assigned in the Inspector
    private RenderTexture inkLayerRT; 
    
    private Color inkLayerColor = new Vector4(0, 0, 0, 0);

    private void Awake() {
        // 1. Create a texture that perfectly matches the current monitor resolution
        inkLayerRT = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
        
        // Prevent blending artifacts on the UI overlay
        inkLayerRT.filterMode = FilterMode.Point; 
        inkLayerRT.Create();

        // 2. Slap it onto the UI Raw Image
        if (previewOverlay != null) {
            previewOverlay.texture = inkLayerRT;
        }

        ClearInkLayer();
    }

    public void ClearInkLayer() {
        if (inkLayerRT != null) {
            RenderTexture.active = inkLayerRT;
            GL.Clear(true, true, inkLayerColor);
            RenderTexture.active = null;
        }
    }

    public void SetOpacity(float opacity) {
        this.opacity = opacity;
        inkLayerMaterial.SetFloat("_Opacity", opacity);
    }

    public RenderTexture GetInkLayerRT() {
        return inkLayerRT;
    }

    // ------------------------------------------------------------------
    // NOTE: ApplyInkToCanvas() HAS BEEN COMPLETELY DELETED!
    // The SVT Bake process in CanvasManager will handle saving the stroke.
    // ------------------------------------------------------------------

    // Fully preserved for the SVT Bake process
    public void SetBlendMode(BlendModeConfig config) {
        if (config != null) {
            // We use YOUR ScriptableObject's built-in method!
            config.SetBlendMode(inkLayerMaterial);
            Debug.Log($"Blend mode set to {config.blendModeName}");
        }
    }
}