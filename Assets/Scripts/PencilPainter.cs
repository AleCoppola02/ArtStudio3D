using TMPro;
using UnityEngine;

public class PencilPainter : MonoBehaviour
{
    private Vector2 lastUV;
    private bool isDrawing = false;
    public enum DragState { None, Clicked, Dragging, Released, Paused }
    private DragState dragState = DragState.None;
    private Vector3 previousMousePosition;
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

    [Header("Zoom Settings")]
    public float zoomSpeed = 3f;
    public float minZoom = 0.01f;
    public float maxZoom = 20f;

    void Update() {
        //test();
        HandleInputs();
    }

    private void HandleInputs() {

        //left click 
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

        // 1. GetMouseButtonDown(0) is true ONLY on the first frame the left click is pressed.
        // We use this to lock in the starting position so the camera doesn't jump.
        // (Use 1 for right-click, 2 for middle-click).
        if (Input.GetMouseButtonDown(2)) {
            previousMousePosition = Input.mousePosition;
        }

        // 2. GetMouseButton(0) is true as long as the button is HELD down.
        if (Input.GetMouseButton(2)) {
            Vector3 currentMousePosition = Input.mousePosition;

            // Calculate how many pixels the mouse moved
            Vector3 mouseDelta = currentMousePosition - previousMousePosition;

            // Calculate the world-to-pixel ratio based on current zoom
            float unitsPerPixel = (2f * cam.orthographicSize) / Screen.height;

            // Convert the pixel delta to world units (ignoring Z axis)
            Vector3 worldDelta = new Vector3(mouseDelta.x, mouseDelta.y, 0f) * unitsPerPixel;

            // Move the camera in the opposite direction
            cam.transform.position -= worldDelta;

            // Update the previous position for the next frame
            previousMousePosition = currentMousePosition;
        }

        // Input.GetAxis("Mouse ScrollWheel") returns positive when scrolling up, negative when down.
        float scrollDelta = Input.GetAxis("Mouse ScrollWheel");

        if (Mathf.Abs(scrollDelta) > 0f) {
            // 1. Get world position under the mouse BEFORE the zoom
            // ScreenToWorldPoint translates screen pixels to world coordinates based on current zoom
            Vector3 mouseWorldPosBeforeZoom = cam.ScreenToWorldPoint(Input.mousePosition);

            // 2. Adjust the zoom level
            // We subtract because scrolling up (positive) should zoom IN (smaller orthographic size)
            cam.orthographicSize -= cam.orthographicSize * scrollDelta * zoomSpeed;

            // Clamp it so we don't invert the camera or zoom out to infinity
            cam.orthographicSize = Mathf.Clamp(cam.orthographicSize, minZoom, maxZoom);

            // 3. Get the new world position under the mouse AFTER the zoom
            Vector3 mouseWorldPosAfterZoom = cam.ScreenToWorldPoint(Input.mousePosition);

            // 4. Calculate how much the world "slipped" away from the cursor
            Vector3 difference = mouseWorldPosBeforeZoom - mouseWorldPosAfterZoom;

            // 5. Shift the camera by that difference to keep the cursor anchored
            // We ensure Z stays at 0 so we don't accidentally push the camera forward/backward
            cam.transform.position += new Vector3(difference.x, difference.y, 0f);
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