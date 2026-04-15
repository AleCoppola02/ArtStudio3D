using System.Collections.Generic;
using UnityEngine;

public class CanvasManager : MonoBehaviour
{
    public RenderTexture canvasRT;
    private Color canvasColor = new Vector4(1, 1, 1, 1);



    public RenderTexture GetCanvasRT() {
        return canvasRT;
    }

    private void Start() {
        ClearCanvas();
    }

    public void ClearCanvas() {
        RenderTexture.active = canvasRT;
        GL.Clear(true, true, canvasColor);
        RenderTexture.active = null;
    }




}
