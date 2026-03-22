using UnityEngine;
using UnityEngine.Rendering; // This contains the 'BlendMode' type

[CreateAssetMenu(fileName = "NewBlendMode", menuName = "Painting/BlendMode")]
public class BlendModeConfig : ScriptableObject
{
    public string blendModeName;
    public BlendMode sourceFactor = BlendMode.SrcAlpha;
    public BlendMode destinationFactor = BlendMode.OneMinusSrcAlpha;

    public void SetBlendMode(Material mat) {
        mat.SetFloat("_SrcBlend", (float)sourceFactor);
        mat.SetFloat("_DstBlend", (float)destinationFactor);
    }
}