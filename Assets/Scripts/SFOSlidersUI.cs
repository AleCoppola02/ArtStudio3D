using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SliderManagerUI : MonoBehaviour
{
    [SerializeField]
    private TextMeshProUGUI opacityText;
    [SerializeField]
    private Slider opacitySlider;

    [SerializeField]
    private TextMeshProUGUI sizeText;
    [SerializeField] 
    private Slider sizeSlider;

    [SerializeField]
    private TextMeshProUGUI flowText;
    [SerializeField]
    private Slider flowSlider;

    [SerializeField]
    private BrushManager brush;
    [SerializeField]
    private InkLayerManager inkLayer;

    // We store these locally so the sliders can update them constantly 
    // without hitting the hard drive every frame.
    private float currentOpacity;
    private float currentFlow;
    private float currentSize;

    private const string OPACITY_PREFS = "InkLayerOpacity";
    private const string FLOW_PREFS = "BrushFlow";
    private const string SIZE_PREFS = "BrushSize";

    private void Start() {
        // 1. Load the values from storage. 
        // We provide a "default" value (like 50f) in case it's the first time playing.
        float savedOpacity = PlayerPrefs.GetFloat(OPACITY_PREFS, 50f);
        float savedFlow = PlayerPrefs.GetFloat(FLOW_PREFS, 10f);
        float savedSize = PlayerPrefs.GetFloat(SIZE_PREFS, 20f);

        // 2. Set the Slider handles to the saved positions
        opacitySlider.value = savedOpacity;
        flowSlider.value = savedFlow;
        sizeSlider.value = savedSize;

        // 3. Manually call the functions once to sync the brush and text
        SetInkLayerOpacityUI(savedOpacity);
        SetBrushFlowUI(savedFlow);
        SetBrushSizeUI(savedSize);
    }

    public void SetInkLayerOpacityUI(float opacity) {
        inkLayer.SetOpacity(opacity / 100f);
        opacityText.text = $"{(int)opacity}%";

        currentOpacity = opacity;
    }

    public void SetBrushFlowUI(float flow) {
        brush.SetBrushFlow(flow / 100f);
        flowText.text = $"{(int)flow}%";

        currentFlow = flow; 
    }

    public void SetBrushSizeUI(float size) {
        brush.SetBrushSize(size);
        sizeText.text = size.ToString("F0");

        currentSize = size;
    }

    private void OnDisable() {
        // This runs when the UI panel is closed or the game exits
        PlayerPrefs.SetFloat(OPACITY_PREFS, currentOpacity);
        PlayerPrefs.SetFloat(FLOW_PREFS, currentFlow);
        PlayerPrefs.SetFloat(SIZE_PREFS, currentSize);

        // Finalize the save to disk
        PlayerPrefs.Save();
    }

}
