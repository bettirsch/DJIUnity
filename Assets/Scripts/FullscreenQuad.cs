using UnityEngine;

[ExecuteAlways]
public class FullscreenQuad : MonoBehaviour
{
    public Camera targetCamera;
    public float distance = 1f;   // how far in front of the camera

    void LateUpdate()
    {
        if (targetCamera == null)
            targetCamera = Camera.main;
        if (targetCamera == null)
            return;

        // Put quad in front of the camera
        transform.position = targetCamera.transform.position +
                             targetCamera.transform.forward * distance;
        transform.rotation = targetCamera.transform.rotation;

        // Make it exactly fit the camera frustum at that distance
        float height = 2f * distance *
                       Mathf.Tan(targetCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float width = height * targetCamera.aspect;

        transform.localScale = new Vector3(width, height, 1f);
    }
}
