using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class InkLayerManager : MonoBehaviour
{

    public Material inkLayerMaterial;
    [Range(0, 1)] public float opacity = 0.5f;
    public RenderTexture inkLayerRT;
    private RenderTexture canvasRT;
    private RenderTexture tempRT; // For compositing

    public CanvasManager canvas;

    private Color inkLayerColor = new Vector4(0, 0, 0, 0);

    private void Start() {
        canvasRT = canvas.GetCanvasRT();
        ClearInkLayer();
        inkLayerMaterial.SetTexture("_CanvasTex", canvasRT);
        createTempRT();
    }

    public void ClearInkLayer() {
        RenderTexture.active = inkLayerRT;
        GL.Clear(true, true, inkLayerColor);
        RenderTexture.active = null;
    }

    private void createTempRT() {
        RenderTextureDescriptor desc = canvasRT.descriptor;
        tempRT = new RenderTexture(desc);
        tempRT.filterMode = canvasRT.filterMode;
        tempRT.wrapMode = canvasRT.wrapMode;
        tempRT.anisoLevel = canvasRT.anisoLevel;
        tempRT.Create();
    }

    public void SetOpacity(float opacity) {
        this.opacity = opacity;
        inkLayerMaterial.SetFloat("_Opacity", opacity);
    }

    public RenderTexture GetInkLayerRT() {
        return inkLayerRT;
    }

    public void ApplyInkToCanvas() {
        Graphics.Blit(canvasRT, tempRT); // Copy current canvas to temp
        inkLayerMaterial.SetTexture("_CanvasTex", tempRT); // Set temp as input for compositing
        Graphics.Blit(inkLayerRT, canvasRT, inkLayerMaterial); // Composite ink layer onto canvas using inkLayerMaterial
        ClearInkLayer(); // Clear ink layer for next stroke
    }

    public void SetBlendMode(BlendModeConfig config) {
        if (config != null) {
            config.SetBlendMode(inkLayerMaterial);
        }
    }

}
