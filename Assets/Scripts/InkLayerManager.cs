using System.Collections.Generic;
using UnityEngine;

public class InkLayerManager : MonoBehaviour
{
    private class PreviewLayer
    {
        public int strokeID;
        public RenderTexture rt;
        public GameObject quadObj;
        public Material uiMaterialInstance;
        public bool isActive;
    }

    [Header("Material & Settings")]
    public Material previewMaterial;
    [Range(0, 1)] public float opacity = 0.5f;

    private List<PreviewLayer> layerPool = new List<PreviewLayer>();
    private int strokeCounter = 0;
    private Color clearColor = new Color(0, 0, 0, 0);

    public (int strokeID, RenderTexture rt) RequestNewPreviewLayer(BlendModeConfig blendMode, float strokeOpacity, Vector2 center, Vector2 size, int rtWidth, int rtHeight) {
        PreviewLayer layer = GetOrCreateLayerFromPool(rtWidth, rtHeight);

        layer.strokeID = ++strokeCounter;
        layer.isActive = true;
        layer.quadObj.SetActive(true);

        layer.quadObj.transform.position = new Vector3(center.x, center.y, -1f); 
        layer.quadObj.transform.localScale = new Vector3(size.x, size.y, 1f);

        RenderTexture.active = layer.rt;
        GL.Clear(true, true, clearColor);
        RenderTexture.active = null;

        if (layer.uiMaterialInstance != null) {
            if (blendMode != null) blendMode.SetBlendMode(layer.uiMaterialInstance);
            layer.uiMaterialInstance.SetFloat("_Opacity", strokeOpacity);
        }

        return (layer.strokeID, layer.rt);
    }

    public void ReleaseStrokeLayer(int strokeID) {
        PreviewLayer layer = layerPool.Find(l => l.strokeID == strokeID && l.isActive);
        if (layer != null) {
            layer.isActive = false;
            layer.quadObj.SetActive(false);
        }
    }

    private PreviewLayer GetOrCreateLayerFromPool(int rtWidth, int rtHeight) {
        foreach (var layer in layerPool) {
            if (!layer.isActive) {
                if (layer.rt.width != rtWidth || layer.rt.height != rtHeight) {
                    layer.rt.Release();
                    layer.rt = new RenderTexture(rtWidth, rtHeight, 0, RenderTextureFormat.ARGB32);
                    layer.rt.filterMode = FilterMode.Point;
                    layer.rt.Create();
                    layer.uiMaterialInstance.SetTexture("_MainTex", layer.rt);
                }
                return layer;
            }
        }

        PreviewLayer newLayer = new PreviewLayer();
        
        newLayer.rt = new RenderTexture(rtWidth, rtHeight, 0, RenderTextureFormat.ARGB32);
        newLayer.rt.filterMode = FilterMode.Point;
        newLayer.rt.Create();

        newLayer.quadObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(newLayer.quadObj.GetComponent<Collider>()); 
        newLayer.quadObj.name = "WorldSpace_InkPreview";
        
        // Ensure Quad ignores all GameObject scaling hierarchies
        newLayer.quadObj.transform.SetParent(null); 

        newLayer.uiMaterialInstance = new Material(previewMaterial);
        newLayer.uiMaterialInstance.SetTexture("_MainTex", newLayer.rt);
        newLayer.uiMaterialInstance.SetFloat("_Opacity", opacity);
        
        newLayer.quadObj.GetComponent<MeshRenderer>().material = newLayer.uiMaterialInstance;

        layerPool.Add(newLayer);
        return newLayer;
    }
    public void SetOpacity(float opacity) {
        this.opacity = opacity;
        foreach (var layer in layerPool) {
            if (layer.uiMaterialInstance != null) {
                layer.uiMaterialInstance.SetFloat("_Opacity", opacity);
            }
        }
    }
}