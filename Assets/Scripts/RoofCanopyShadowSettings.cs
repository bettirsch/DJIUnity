using UnityEngine;

/// <summary>Board-space visual configuration only. Wall coordinates are uncalibrated until explicitly measured.</summary>
public sealed class RoofCanopyShadowSettings : ScriptableObject
{
    public Material shadowMaterial;
    public Vector3 sunlightRayDirection = new Vector3(.409576f, .573576f, -.709406f).normalized;
    [Range(0, .6f)] public float opacity = .34f;
    [Range(0, 1)] public float woodStrength = 1;
    [Range(0, 1)] public float roofStrength = .22f;
    [Range(0, 1)] public float contactStrength = .32f;
    [Range(.005f, .15f)] public float softnessMeters = .04f;
    [Range(128, 1024)] public int resolution = 768;
    public Vector2 groundCenter = new Vector2(2.4f, -1.1f);
    public Vector2 groundSize = new Vector2(12, 9);
    public bool wallCalibrated;
    public bool wallEnabled;
    [Tooltip("Provisional Editor calibration, NOT measured: board Y offset of the wall's bottom-center.")]
    public float wallOffset = .25f;
    public float wallYawDegrees;
    public float wallCenterX = 2.4f;
    public float wallWidth = 7;
    public float wallHeight = 3.2f;
    [HideInInspector] public bool useFirstSurfaceProjection;
    [HideInInspector] public Vector3[] woodProxyCorners = new Vector3[0];
    [HideInInspector] public Vector3[] roofProxyCorners = new Vector3[0];
    [HideInInspector] public Vector3[] contacts = new Vector3[0];
    [HideInInspector] public string sourceFbxSha256;

    public ShadowReceiverPlane Ground => new ShadowReceiverPlane(
        new Vector3(groundCenter.x, groundCenter.y, 0), Vector3.right, Vector3.up, groundSize);
    public ShadowReceiverPlane Wall => new ShadowReceiverPlane(
        new Vector3(wallCenterX, wallOffset, wallHeight * .5f),
        Quaternion.AngleAxis(wallYawDegrees, Vector3.forward) * Vector3.right,
        Vector3.forward, new Vector2(wallWidth, wallHeight));
}

public readonly struct ShadowReceiverPlane
{
    public readonly Vector3 center, u, v, normal;
    public readonly Vector2 size;
    public ShadowReceiverPlane(Vector3 center, Vector3 u, Vector3 v, Vector2 size)
    {
        this.center = center; this.u = u.normalized; this.v = v.normalized;
        normal = Vector3.Cross(this.u, this.v).normalized; this.size = size;
    }
    public Vector3 Point(Vector2 uv) => center + u * ((uv.x - .5f) * size.x) + v * ((uv.y - .5f) * size.y);
    public Vector2 Coordinates(Vector3 point) => new Vector2(Vector3.Dot(point - center, u) / size.x + .5f,
        Vector3.Dot(point - center, v) / size.y + .5f);
    public bool IsValid => ShadowProjectionMath.Finite(center) && ShadowProjectionMath.Finite(normal) &&
        size.x > .01f && size.y > .01f && float.IsFinite(size.x) && float.IsFinite(size.y) &&
        normal.sqrMagnitude > .99f && Mathf.Abs(Vector3.Dot(u, v)) < .001f;
}
