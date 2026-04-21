using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public sealed class TapToPlaceMarker : MonoBehaviour
{
    [Header("Placement")]
    [SerializeField] private Camera targetCamera;
    [SerializeField] [Min(0.5f)] private float anchorDistance = 8f;
    [SerializeField] private bool placeOnStart;
    [SerializeField] private bool allowReposition = true;
    [SerializeField] private bool preferGroundPlane = true;
    [SerializeField] private bool hideUntilPlaced = true;

    [Header("Look")]
    [SerializeField] private bool faceCamera = false;

    [Header("Debug")]
    [SerializeField] private bool verboseLogs;

    private bool _hasAnchor;
    private bool _loggedMissingGroundPlane;
    private Renderer[] _renderers;
    private DJICameraPoseDriver _poseDriver;

    private void Start()
    {
        if (targetCamera == null)
            targetCamera = Camera.main;

        _renderers = GetComponentsInChildren<Renderer>(true);
        if (targetCamera != null)
            _poseDriver = targetCamera.GetComponent<DJICameraPoseDriver>();

        if (hideUntilPlaced)
            SetVisible(false);

        if (placeOnStart)
            PlaceAtViewportPoint(new Vector2(0.5f, 0.5f));
    }

    private void Update()
    {
        if (targetCamera == null)
            return;

        if (allowReposition && TryGetPlacementScreenPoint(out var screenPoint))
            PlaceAtScreenPoint(screenPoint);

        if (faceCamera && _hasAnchor)
            transform.rotation = Quaternion.LookRotation(transform.position - targetCamera.transform.position, Vector3.up);
    }

    private bool TryGetPlacementScreenPoint(out Vector2 screenPoint)
    {
        screenPoint = default;

#if ENABLE_INPUT_SYSTEM
        if (Touchscreen.current != null)
        {
            var primaryTouch = Touchscreen.current.primaryTouch;
            if (primaryTouch.press.wasPressedThisFrame)
            {
                screenPoint = primaryTouch.position.ReadValue();

                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(primaryTouch.touchId.ReadValue()))
                    return false;

                if (verboseLogs)
                    Debug.Log($"[DJI] TapToPlace touch at {screenPoint}");

                return true;
            }
        }
#endif

        if (Input.touchCount > 0)
        {
            var touch = Input.GetTouch(0);
            if (touch.phase != UnityEngine.TouchPhase.Began)
                return false;

            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(touch.fingerId))
                return false;

            screenPoint = touch.position;

            if (verboseLogs)
                Debug.Log($"[DJI] TapToPlace legacy touch at {screenPoint}");

            return true;
        }

#if UNITY_EDITOR || UNITY_STANDALONE
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return false;

            screenPoint = Mouse.current.position.ReadValue();

            if (verboseLogs)
                Debug.Log($"[DJI] TapToPlace mouse at {screenPoint}");

            return true;
        }
#endif

        if (Input.GetMouseButtonDown(0))
        {
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return false;

            screenPoint = Input.mousePosition;

            if (verboseLogs)
                Debug.Log($"[DJI] TapToPlace legacy mouse at {screenPoint}");

            return true;
        }
#endif

        return false;
    }

    private void PlaceAtViewportPoint(Vector2 viewportPoint)
    {
        if (targetCamera == null)
            return;

        PlaceAtScreenPoint(targetCamera.ViewportToScreenPoint(new Vector3(viewportPoint.x, viewportPoint.y, 0f)));
    }

    private void PlaceAtScreenPoint(Vector2 screenPoint)
    {
        var ray = targetCamera.ScreenPointToRay(screenPoint);
        var usedGroundPlane = false;
        Plane plane;

        if (preferGroundPlane && _poseDriver != null && _poseDriver.HasGroundPlaneEstimate)
        {
            plane = new Plane(Vector3.up, new Vector3(0f, _poseDriver.GroundPlaneWorldY, 0f));
            usedGroundPlane = true;
        }
        else
        {
            if (preferGroundPlane && verboseLogs && !_loggedMissingGroundPlane)
            {
                _loggedMissingGroundPlane = true;

                var reason = _poseDriver == null
                    ? "pose driver missing on target camera"
                    : _poseDriver.HasValidPose
                        ? "ground plane estimate not initialized"
                        : "no valid DJI pose yet";

                Debug.Log($"[DJI] TapToPlace using camera plane until ground plane is ready ({reason})");
            }

            plane = new Plane(
                -targetCamera.transform.forward,
                targetCamera.transform.position + targetCamera.transform.forward * anchorDistance
            );
        }

        if (!plane.Raycast(ray, out var enter))
        {
            if (usedGroundPlane)
            {
                if (verboseLogs)
                    Debug.Log("[DJI] TapToPlace ground plane miss, falling back to camera plane");

                plane = new Plane(
                    -targetCamera.transform.forward,
                    targetCamera.transform.position + targetCamera.transform.forward * anchorDistance
                );
                if (!plane.Raycast(ray, out enter))
                    return;
            }
            else
            {
                return;
            }
        }

        if (enter <= 0f)
            return;

        transform.position = ray.GetPoint(enter);
        _hasAnchor = true;
        _loggedMissingGroundPlane = false;
        SetVisible(true);

        if (verboseLogs)
            Debug.Log($"[DJI] TapToPlace placed marker at {transform.position} using {(usedGroundPlane ? "ground plane" : "camera plane")}");
    }

    private void SetVisible(bool visible)
    {
        if (_renderers == null)
            return;

        foreach (var currentRenderer in _renderers)
        {
            if (currentRenderer != null)
                currentRenderer.enabled = visible;
        }
    }
}
