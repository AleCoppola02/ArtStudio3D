using TMPro;
using UnityEngine;

public class PencilPainter : MonoBehaviour
{
    private Vector2 lastUV;
    private bool isDrawing = false;
    public enum DragState { None, Clicked, Dragging }
    private DragState dragState = DragState.None;

    [SerializeField]
    private Brush brush;

    [SerializeField]
    private TextMeshProUGUI opacityText;
    [SerializeField]
    private TextMeshProUGUI sizeText;
    [SerializeField]
    private TextMeshProUGUI flowText;

    void Update() {
        //test();
        HandleInputs();
    }

    // Handle mouse input and drawing logic
    private void HandleInputs() {
        if (Input.GetMouseButton(0)) {
            dragState = dragState == DragState.None ? DragState.Clicked : DragState.Dragging;
            if (GetHitUV(out Vector2 currentUV)) {
                if (!isDrawing) {
                    lastUV = currentUV;
                    isDrawing = true;
                }

                brush.DrawLine(lastUV, currentUV, dragState);



                lastUV = currentUV;

            }
            else {
                isDrawing = false;
            }
        }
        else if(Input.GetMouseButtonUp(0)) {
            if (dragState != DragState.None) {
                brush.ApplyInkToCanvas();

                dragState = DragState.None;
            }
            dragState = DragState.None;
            isDrawing = false;
        }
    }

    bool GetHitUV(out Vector2 uv) {
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit)) {
            uv = hit.textureCoord;
            return true;
        }
        uv = Vector2.zero;
        return false;
    }




}