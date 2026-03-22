using UnityEngine;
using UnityEngine.Rendering; // This contains the 'BlendMode' type

[CreateAssetMenu(fileName = "NewBlendMode", menuName = "Painting/BlendMode")]
public class BlendModeConfig : ScriptableObject
{
    public string blendModeName;
    public BlendMode colorSourceFactor = BlendMode.SrcAlpha;
    public BlendMode colorDestinationFactor = BlendMode.OneMinusSrcAlpha;
    public BlendMode alphaSourceFactor = BlendMode.OneMinusSrcAlpha;
    public BlendMode alphaDestinationFactor = BlendMode.OneMinusSrcAlpha;

    public void SetBlendMode(Material mat) {
        mat.SetFloat("_SrcBlendColor", (float)colorSourceFactor);
        mat.SetFloat("_DstBlendColor", (float)colorDestinationFactor);
        mat.SetFloat("_SrcBlendAlpha", (float)alphaSourceFactor);
        mat.SetFloat("_DstBlendAlpha", (float)alphaDestinationFactor);
    }
}