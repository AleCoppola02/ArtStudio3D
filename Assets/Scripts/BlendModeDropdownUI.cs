using UnityEngine;
using TMPro;
using System.Collections.Generic;

public class BlendModeUI : MonoBehaviour
{
    public TMP_Dropdown dropdown;
    public InkLayerManager inkLayer;
    public List<BlendModeConfig> availableModes; // The list of SOs

    void Start() {
        dropdown.ClearOptions();

        List<string> options = new List<string>();
        foreach (var mode in availableModes) {
            options.Add(mode.blendModeName);
        }
        dropdown.AddOptions(options);

        // When the value changes, send the object directly
        dropdown.onValueChanged.AddListener(index => {
            inkLayer.SetBlendMode(availableModes[index]);
        });

        // Set initial default
        if (availableModes.Count > 0) inkLayer.SetBlendMode(availableModes[0]);
    }
}