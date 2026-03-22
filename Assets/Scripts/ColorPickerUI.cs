using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class ColorPickerUI : MonoBehaviour, IPointerDownHandler, IDragHandler, IInitializePotentialDragHandler
{
    public Material paletteMaterial;
    public RectTransform paletteRect;
    public BrushManager brush;
    public Image hueSliderHandle;
    public Slider hueSlider;

    private float currentSaturation = 1f;
    private float currentValue = 1f;

    // --- STRINGS FOR PLAYERPREFS KEYS ---
    private const string PREF_HUE = "ColorPicker_Hue";
    private const string PREF_SAT = "ColorPicker_Sat";
    private const string PREF_VAL = "ColorPicker_Val";

    private void Start() {
        // 1. Load saved values (Default to 0 Hue, 1 Saturation, 1 Value if no save exists)
        float savedHue = PlayerPrefs.GetFloat(PREF_HUE, 0f);
        currentSaturation = PlayerPrefs.GetFloat(PREF_SAT, 1f);
        currentValue = PlayerPrefs.GetFloat(PREF_VAL, 1f);

        // 2. Update the physical UI Slider position without triggering its event yet
        if (hueSlider != null) {
            hueSlider.SetValueWithoutNotify(savedHue);
        }

        // 3. Force the system to update the material, brush, and cursor visually
        OnHueChanged(savedHue);
        UpdateShaderCursor();
    }

    // --- NEW: SAVE DATA WHEN APP CLOSES OR SCENE CHANGES ---
    private void SaveColorPreferences() {
        if (paletteMaterial != null) {
            PlayerPrefs.SetFloat(PREF_HUE, paletteMaterial.GetFloat("_Hue"));
        }
        PlayerPrefs.SetFloat(PREF_SAT, currentSaturation);
        PlayerPrefs.SetFloat(PREF_VAL, currentValue);

        // Force Unity to write this to the disk immediately
        PlayerPrefs.Save();
    }

    private void OnDestroy() {
        SaveColorPreferences();
    }

    private void OnApplicationQuit() {
        // On mobile devices, OnDestroy isn't always called when the app is swiped away.
        // OnApplicationQuit acts as a reliable backup.
        SaveColorPreferences();
    }

    public void OnInitializePotentialDrag(PointerEventData eventData) {
        eventData.useDragThreshold = false;
    }

    public void OnHueChanged(float newHue) {
        paletteMaterial.SetFloat("_Hue", newHue);

        if (hueSliderHandle != null) {
            hueSliderHandle.color = Color.HSVToRGB(newHue, 1f, 1f);
        }

        UpdateBrushColor();
    }

    public void OnPointerDown(PointerEventData eventData) { UpdateColor(eventData); }
    public void OnDrag(PointerEventData eventData) { UpdateColor(eventData); }

    private void UpdateColor(PointerEventData eventData) {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            paletteRect,
            eventData.position,
            eventData.pressEventCamera,
            out Vector2 localPoint
        );

        currentSaturation = Mathf.Clamp01(Mathf.InverseLerp(paletteRect.rect.xMin, paletteRect.rect.xMax, localPoint.x));
        currentValue = Mathf.Clamp01(Mathf.InverseLerp(paletteRect.rect.yMin, paletteRect.rect.yMax, localPoint.y));

        UpdateShaderCursor();
        UpdateBrushColor();
    }

    private void UpdateShaderCursor() {
        paletteMaterial.SetVector("_CursorPos", new Vector2(currentSaturation, currentValue));
    }

    private void UpdateBrushColor() {
        float currentHue = paletteMaterial.GetFloat("_Hue");
        Color finalColor = Color.HSVToRGB(currentHue, currentSaturation, currentValue);

        if (brush != null) {
            brush.SetBrushColor(finalColor);
        }
    }
}