using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
using System.Text.RegularExpressions;

public class StartupManager : MonoBehaviour
{
    [Header("UI References")]
    public TMP_InputField widthInput;
    public TMP_InputField heightInput;

    [Header("Scene Routing")]
    public string paintingSceneName = "PaintingScene";

    [Header("Safety Limits")]
    public int minCanvasSize = 256;
    public int maxCanvasSize = 300000; // Hard cap to prevent memory overflow crashes

    private void Start() {
        // 1. Force TMP to open the numeric keyboard on mobile devices
        widthInput.contentType = TMP_InputField.ContentType.IntegerNumber;
        heightInput.contentType = TMP_InputField.ContentType.IntegerNumber;

        // 2. Hook up Real-Time listeners (Web equivalent: 'oninput')
        widthInput.onValueChanged.AddListener(delegate { SanitizeRealTimeInput(widthInput); });
        heightInput.onValueChanged.AddListener(delegate { SanitizeRealTimeInput(heightInput); });

        // 3. Hook up Focus Lost listeners (Web equivalent: 'onblur')
        widthInput.onEndEdit.AddListener(delegate { FormatOnFocusLost(widthInput); });
        heightInput.onEndEdit.AddListener(delegate { FormatOnFocusLost(heightInput); });
    }

    /// <summary>
    /// Fires every single time the user presses a key.
    /// </summary>
    private void SanitizeRealTimeInput(TMP_InputField inputField) {
        // Use Regex to instantly strip out anything that is NOT a number 0-9.
        // Even if they paste "100px" or "500A" from their clipboard, it becomes "100" and "500".
        string filtered = Regex.Replace(inputField.text, "[^0-9]", "");

        // Only assign it back if it changed, to avoid infinite event loops
        if (inputField.text != filtered) {
            inputField.text = filtered;
        }
    }

    /// <summary>
    /// Fires when the user clicks away from the input field or presses Enter.
    /// </summary>
    private void FormatOnFocusLost(TMP_InputField inputField) {
        // Check 1: Did they leave it completely blank?
        if (string.IsNullOrEmpty(inputField.text)) {
            inputField.text = minCanvasSize.ToString();
            return;
        }

        // Check 2: Parse the number. 
        // We use 'long' instead of 'int' here because if they typed a 50-digit number,
        // it would crash an 'int'. Long gives us the headroom to catch the overflow safely.
        if (long.TryParse(inputField.text, out long parsedValue)) {
            // Clamp it between our safe limits. 
            // Parsing it to a number and back to a string automatically strips all leading zeros!
            // Example: "0001080" -> 1080
            long clampedValue = System.Math.Clamp(parsedValue, minCanvasSize, maxCanvasSize);
            inputField.text = clampedValue.ToString();
        }
        else {
            // If it failed to parse even as a long, the number is absurdly huge. Set to max safe size.
            inputField.text = maxCanvasSize.ToString();
        }
    }

    public void OnCreateCanvasButtonClicked() {
        // Because of our strict listeners above, we now have a 100% guarantee 
        // that the text in these fields are valid, perfectly formatted integers.
        int w = int.Parse(widthInput.text);
        int h = int.Parse(heightInput.text);

        // Save to the static bridge and load the scene
        CanvasConfig.Width = w;
        CanvasConfig.Height = h;

        SceneManager.LoadScene(paintingSceneName);
    }
}