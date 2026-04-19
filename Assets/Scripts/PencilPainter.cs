using TMPro;
using UnityEngine;

public class PencilPainter : MonoBehaviour
{

    [SerializeField] private SliderManagerUI sliderManagerUI;

    // --- CHANGED: We now track World Space instead of UVs ---
    private Vector2 lastWorldPos;
    //private Vector2 lastUV;

    // Define the mathematical plane of our infinite canvas.
    // Vector3.forward means the plane is facing us (normal is along the Z axis).
    // Vector3.zero means the plane sits at the world origin (Z = 0).
    Plane canvasPlane = new Plane(Vector3.forward, Vector3.zero);

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
        HandleLeftClick();
        HandleMouseWheel(); // Your existing zoom logic

        // --- NEW: Dynamic Brush Resizing via Hotkeys ---
        HandleBrushSizeHotkeys();
    }

    // Handles drawing with the left mouse button, including click, drag, release, and pause states.
    /*private void HandleLeftClick() {

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
    }*/

    private void HandleLeftClick() {
        // 1. Get the continuous World Position of the cursor right now
        Vector2 currentWorldPos = GetMouseWorldPosition();

        // 2. Left click is HELD DOWN
        if (Input.GetMouseButton(0)) {
            dragState = dragState == DragState.None ? DragState.Clicked : DragState.Dragging;

            if (!isDrawing) {
                // --- FIRST CLICK (Start Stroke) ---
                dragState = DragState.Clicked;
                lastWorldPos = currentWorldPos;
                isDrawing = true;

                brush.StartStroke(currentWorldPos);

                // Reset pause detection
                previousMousePosition = Input.mousePosition;
                stationaryTimer = 0f;
                isStationaryPaused = false;
            }
            else {
                // --- DRAGGING (Continue Stroke) ---

                // Hardware Pause Detection (We can keep this using screen pixels, 
                // because hesitation is based on physical hand movement)
                if (stationaryTimer >= PAUSE_THRESHOLD) {
                    if (!isStationaryPaused) {
                        // Just triggered the pause! Tell the brush manager to break the spline.
                        brush.PauseStroke(currentWorldPos);
                    }
                    isStationaryPaused = true;
                    dragState = DragState.Paused;
                }
                if (Vector3.Distance(Input.mousePosition, previousMousePosition) < 0.1f) {
                    stationaryTimer += Time.deltaTime;
                    if (stationaryTimer >= PAUSE_THRESHOLD) {
                        isStationaryPaused = true;
                        dragState = DragState.Paused;
                    }
                }
                else {
                    stationaryTimer = 0f;
                    isStationaryPaused = false;
                    dragState = DragState.Dragging;
                }
                previousMousePosition = Input.mousePosition;

                // Add points to the stroke if we moved enough in WORLD SPACE
                if (!isStationaryPaused && Vector2.Distance(lastWorldPos, currentWorldPos) > brush.spacingFactor) {
                    brush.AddPointToStroke(currentWorldPos);
                    lastWorldPos = currentWorldPos;
                }
            }
        }
        // 3. Left click is RELEASED
        else if (Input.GetMouseButtonUp(0) && isDrawing) {
            dragState = DragState.Released;
            isDrawing = false;
            brush.EndStroke();

            // Reset pause state
            stationaryTimer = 0f;
            isStationaryPaused = false;
        }
        // 4. No interactions
        else if (!Input.GetMouseButton(0)) {
            dragState = DragState.None;
        }


    }

    //Handle using the mousewheel to zoom and pan the camera, keeping the cursor anchored to the same world position under the mouse.
    private void HandleMouseWheel() {
        // 1. GetMouseButtonDown(2) is true ONLY on the first frame the left click is pressed.
        // We use this to lock in the starting position so the camera doesn't jump.
        // (Use 1 for right-click, 2 for middle-click).
        if (Input.GetMouseButtonDown(2)) {
            previousMousePosition = Input.mousePosition;
        }

        // 2. GetMouseButton(2) is true as long as the button is HELD down.
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

    private Vector2 GetMouseWorldPosition() {


        // 1. Create a mathematical ray from the camera through the mouse cursor
        Ray mouseRay = cam.ScreenPointToRay(Input.mousePosition);

        // 2. Calculate exactly where the ray intersects our invisible plane
        if (canvasPlane.Raycast(mouseRay, out float distanceToPlane)) {
            // 3. Get the exact 3D point in the world
            Vector3 worldPos = mouseRay.GetPoint(distanceToPlane);

            // Return just the X and Y
            return new Vector2(worldPos.x, worldPos.y);
        }

        // Fallback (should theoretically never happen unless camera is looking away from the canvas)
        return Vector2.zero;
    }

    private void HandleBrushSizeHotkeys() {
        // Standard art software shortcuts: '[' to shrink, ']' to grow
        if (Input.GetKey(KeyCode.LeftBracket)) {
            ChangeBrushSize(-2f); // Shrink by 2 units per frame
        }
        if (Input.GetKey(KeyCode.RightBracket)) {
            ChangeBrushSize(2f);  // Grow by 2 units per frame
        }
    }

    private void ChangeBrushSize(float amount) {
        // Grab the current size, add the amount, and clamp it so it doesn't break
        float currentSize = brush.brushSizeUI;
        float newSize = Mathf.Clamp(currentSize + amount, 1f, 1000f);

        // Update the brush
        brush.SetBrushSize(newSize);

        sliderManagerUI.SetBrushSizeUI(newSize);
    }

}