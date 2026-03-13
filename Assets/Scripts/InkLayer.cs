using UnityEngine;

public class InkLayer : MonoBehaviour
{
    public Material inkLayerMaterial;
    [Range(0, 1)] public float opacity = 0.5f;
    public RenderTexture inkLayerRT;
    private RenderTexture tempRT; // For compositing
    private Color inkLayerColor = new Vector4(1, 1, 1, 0);



    public void ClearInkLayer() {
        RenderTexture.active = inkLayerRT;
        GL.Clear(true, true, inkLayerColor);
        RenderTexture.active = null;
    }
}
