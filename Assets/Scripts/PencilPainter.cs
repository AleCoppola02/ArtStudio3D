using TMPro;
using UnityEngine;
using UnityEngine.EventSystems; // <--- REQUIRED FOR UI DETECTION

public class PencilPainter : MonoBehaviour
{
    [SerializeField] private SliderManagerUI sliderManagerUI;

    private Vector2 lastWorldPos;
    private Plane canvasPlane = new Plane(Vector3.forward, Vector3.zero);

    private bool isDrawing = false;
    public enum DragState { None, Clicked, Dragging, Released, Paused }
    private DragState dragState = DragState.None;
    private Vector3 previousMousePosition;

    private float stationaryTimer = 0f;
    private bool isStationaryPaused = false;
    private const float PAUSE_THRESHOLD = 0.15f;

    // ---> NEW: Tracks if the current click started on a UI element <---
    private bool isPointerOverUI = false;

    [SerializeField] private BrushManager brush;
    [SerializeField] private Camera cam;

    [Header("Zoom Settings")]
    public float zoomSpeed = 3f;
    public float minZoom = 0.01f;
    public float maxZoom = 20f;

    void Update() {
        HandleLeftClick();
        HandleMouseWheel();
        HandleBrushSizeHotkeys();
    }

    private void HandleLeftClick() {
        Vector2 currentWorldPos = GetMouseWorldPosition();

        if (Input.GetMouseButton(0)) {
            // 1. Detect if this is the absolutely first frame of a new click
            if (dragState == DragState.None && !isDrawing) {
                // Ask Unity if the mouse is currently touching a UI Canvas element
                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) {
                    isPointerOverUI = true;
                }
                else {
                    isPointerOverUI = false;
                }
            }

            // 2. If the click started on a UI element, IGNORE drawing entirely until they let go!
            if (isPointerOverUI) return;

            dragState = dragState == DragState.None ? DragState.Clicked : DragState.Dragging;

            if (!isDrawing) {
                // --- FIRST CLICK ---
                dragState = DragState.Clicked;
                lastWorldPos = currentWorldPos;
                isDrawing = true;

                brush.StartStroke(currentWorldPos);

                previousMousePosition = Input.mousePosition;
                stationaryTimer = 0f;
                isStationaryPaused = false;
            }
            else {
                // --- DRAGGING ---
                if (stationaryTimer >= PAUSE_THRESHOLD) {
                    if (!isStationaryPaused) {
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

                // Calculate exactly 3 screen pixels converted to World Space!
                float worldUnitsPerPixel = (2f * cam.orthographicSize) / Screen.height;
                float minPointDistance = worldUnitsPerPixel * 3f;

                // Prevent over-sampling on massive brushes
                minPointDistance = Mathf.Max(minPointDistance, brush.GetCurrentBrushSpacing());

                if (!isStationaryPaused && Vector2.Distance(lastWorldPos, currentWorldPos) >= minPointDistance) {
                    brush.AddPointToStroke(currentWorldPos);
                    lastWorldPos = currentWorldPos;
                }
            }
        }
        else if (Input.GetMouseButtonUp(0)) {
            // Reset the UI lock when the mouse is released
            isPointerOverUI = false;

            if (isDrawing) {
                dragState = DragState.Released;
                isDrawing = false;
                brush.EndStroke();

                stationaryTimer = 0f;
                isStationaryPaused = false;
            }
        }
        else if (!Input.GetMouseButton(0)) {
            dragState = DragState.None;
            isPointerOverUI = false;
        }
    }

    private void HandleMouseWheel() {
        if (Input.GetMouseButtonDown(2)) {
            previousMousePosition = Input.mousePosition;
        }

        if (Input.GetMouseButton(2)) {
            Vector3 currentMousePosition = Input.mousePosition;
            Vector3 mouseDelta = currentMousePosition - previousMousePosition;

            float unitsPerPixel = (2f * cam.orthographicSize) / Screen.height;
            Vector3 worldDelta = new Vector3(mouseDelta.x, mouseDelta.y, 0f) * unitsPerPixel;

            cam.transform.position -= worldDelta;
            previousMousePosition = currentMousePosition;
        }

        float scrollDelta = Input.GetAxis("Mouse ScrollWheel");

        if (Mathf.Abs(scrollDelta) > 0f) {
            Vector3 mouseWorldPosBeforeZoom = cam.ScreenToWorldPoint(Input.mousePosition);

            cam.orthographicSize -= cam.orthographicSize * scrollDelta * zoomSpeed;
            cam.orthographicSize = Mathf.Clamp(cam.orthographicSize, minZoom, maxZoom);

            Vector3 mouseWorldPosAfterZoom = cam.ScreenToWorldPoint(Input.mousePosition);
            Vector3 difference = mouseWorldPosBeforeZoom - mouseWorldPosAfterZoom;

            cam.transform.position += new Vector3(difference.x, difference.y, 0f);
        }
    }

    private Vector2 GetMouseWorldPosition() {
        Ray mouseRay = cam.ScreenPointToRay(Input.mousePosition);

        if (canvasPlane.Raycast(mouseRay, out float distanceToPlane)) {
            Vector3 worldPos = mouseRay.GetPoint(distanceToPlane);
            return new Vector2(worldPos.x, worldPos.y);
        }
        return Vector2.zero;
    }

    private void HandleBrushSizeHotkeys() {
        if (Input.GetKey(KeyCode.LeftBracket)) {
            ChangeBrushSize(-2f);
        }
        if (Input.GetKey(KeyCode.RightBracket)) {
            ChangeBrushSize(2f);
        }
    }

    private void ChangeBrushSize(float amount) {
        float currentSize = brush.brushSizeUI;
        float newSize = Mathf.Clamp(currentSize + amount, 1f, 1000f);

        brush.SetBrushSize(newSize);
        if (sliderManagerUI != null) sliderManagerUI.SetBrushSizeUI(newSize);
    }
}