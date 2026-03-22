using TMPro;
using UnityEngine;

public class PencilPainter : MonoBehaviour
{
    private Vector2 lastUV;
    private bool isDrawing = false;
    public enum DragState { None, Clicked, Dragging, Released, Paused }
    private DragState dragState = DragState.None;


    // --- PAUSE DETECTION VARIABLES ---
    private float stationaryTimer = 0f;
    private bool isStationaryPaused = false;
    private const float PAUSE_THRESHOLD = 0.15f; // 50ms (Just enough to bypass hardware polling stutter)

    [SerializeField]
    private BrushManager brush;
    [SerializeField]
    private InkLayerManager inkLayer;
    [SerializeField]
    private Camera cam;

    void Update() {
        //test();
        HandleInputs();
    }

    private void HandleInputs() {
        if (Input.GetMouseButton(0)) {
            dragState = dragState == DragState.None ? DragState.Clicked : DragState.Dragging;

            if (GetHitUV(out Vector2 currentUV)) {
                if (!isDrawing) {
                    dragState = DragState.Clicked;
                    lastUV = currentUV;
                    isDrawing = true;
                    stationaryTimer = 0f;
                    isStationaryPaused = false;
                }

                // 1. Check if the mouse is perfectly still
                if (currentUV == lastUV) {
                    if (dragState == DragState.Dragging) {
                        stationaryTimer += Time.deltaTime;

                        if (stationaryTimer >= PAUSE_THRESHOLD && !isStationaryPaused) {

                            // THE FIX: Just send Paused! 
                            // This drops the silent anchor but preserves the distance.
                            brush.UseBrush(lastUV, lastUV, DragState.Paused);
                            isStationaryPaused = true;
                        }
                    }
                }
                else {
                    // The mouse is moving! Reset the pause state.
                    stationaryTimer = 0f;
                    isStationaryPaused = false;
                }

                // 2. ALWAYS pass the input to the brush!
                if (!isStationaryPaused || dragState == DragState.Clicked) {
                    brush.UseBrush(lastUV, currentUV, dragState);
                }

                lastUV = currentUV;
            }
            else {
                if (isDrawing) {
                    brush.UseBrush(lastUV, lastUV, DragState.Released);
                    isDrawing = false;
                }
            }
        }
        else if (Input.GetMouseButtonUp(0)) {
            if (dragState != DragState.None) {
                if (isDrawing) {
                    brush.UseBrush(lastUV, lastUV, DragState.Released);
                }

                inkLayer.ApplyInkToCanvas();
                dragState = DragState.None;
            }
            isDrawing = false;
        }
        else if (Input.GetMouseButton(1)) {
            cam.transform.position = Vector3.Lerp(cam.transform.position, cam.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, cam.nearClipPlane)), Time.deltaTime * 5f);
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