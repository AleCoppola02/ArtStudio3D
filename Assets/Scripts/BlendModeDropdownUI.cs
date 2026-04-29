using UnityEngine;
using TMPro;
using System.Collections.Generic;

public class BlendModeUI : MonoBehaviour
{
    public TMP_Dropdown dropdown;

    // CHANGED: We now talk to the BrushManager instead of the InkLayerManager
    public BrushManager brushManager;

    public List<BlendModeConfig> availableModes; // The list of ScriptableObjects

    void Start() {
        dropdown.ClearOptions();

        List<string> options = new List<string>();
        foreach (var mode in availableModes) {
            options.Add(mode.blendModeName);
        }
        dropdown.AddOptions(options);

        // When the value changes, update the active setting in BrushManager
        dropdown.onValueChanged.AddListener(index => {
            brushManager.currentBlendMode = availableModes[index];
        });

        // Set initial default
        if (availableModes.Count > 0) {
            brushManager.currentBlendMode = availableModes[0];
        }
    }
} 