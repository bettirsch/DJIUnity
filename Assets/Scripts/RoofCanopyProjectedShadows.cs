using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Two optional cached board-space receivers, following only the overlay's existing visibility.</summary>
[DisallowMultipleComponent]
[ExecuteAlways]
public sealed class RoofCanopyProjectedShadows : MonoBehaviour
{
    private GameObject _ground, _wall;
    private readonly Object[] _owned = new Object[6];
    private bool _wallConfigured;
    public int GeometryBuildCount { get; private set; }
    public bool GroundVisible => _ground != null && _ground.activeSelf;
    public bool WallVisible => _wall != null && _wall.activeSelf;
    public static RoofCanopyProjectedShadows Create(Transform board, int layer, RoofCanopyShadowSettings settings = null)
    {
        var existing = board.GetComponent<RoofCanopyProjectedShadows>();
        if (existing != null) return existing;
        bool ownsSettings = false;
        if (settings == null)
        {
            settings = Resources.Load<RoofCanopyShadowSettings>("RoofCanopyShadowSettings");
            if (RoofCanopyWallShadowExperiment.Enabled && RoofCanopyWallShadowExperiment.TryCreateSettings(settings,
                Resources.Load<RoofCanopyWallShadowExperiment>("RoofCanopyWallShadowExperiment"), true, out var experimental))
            { settings = experimental; ownsSettings = true; }
        }
        if (settings == null || settings.shadowMaterial == null) return null;
        var result = board.gameObject.AddComponent<RoofCanopyProjectedShadows>();
        try { result.Initialize(settings, layer); return result; }
        finally { if (ownsSettings) Release(settings); }
    }
    private void Initialize(RoofCanopyShadowSettings settings, int layer)
    {
        _wallConfigured = settings.wallEnabled && settings.wallCalibrated;
        _ground = Receiver("RoofCanopy Ground Shadow", settings.Ground, settings, layer, 0);
        if (_wallConfigured) _wall = Receiver("RoofCanopy Wall Shadow", settings.Wall, settings, layer, 3);
        SetVisible(false); GeometryBuildCount++;
    }
    private GameObject Receiver(string name, ShadowReceiverPlane plane, RoofCanopyShadowSettings settings, int layer, int slot)
    {
        if (!plane.IsValid) return null;
        var bytes = ShadowProjectionMath.Bake(settings, plane, out int width, out int height);
        var texture = new Texture2D(width, height, TextureFormat.R8, false, true) { name = name+" Mask", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        texture.LoadRawTextureData(bytes); texture.Apply(false, true);
        var material = new Material(settings.shadowMaterial) { name = name+" Material" };
        material.SetTexture("_ShadowMask", texture);
        var polygon = new System.Collections.Generic.List<Vector3> {
            plane.Point(Vector2.zero), plane.Point(Vector2.right), plane.Point(Vector2.one), plane.Point(Vector2.up) };
        if (settings.useFirstSurfaceProjection && _wallConfigured && slot == 0)
            polygon = ShadowProjectionMath.ClipToFront(polygon, settings.Wall);
        var offset = settings.useFirstSurfaceProjection ? Vector3.zero : plane.normal*.002f;
        Vector3[] vertices; Vector2[] uv; int[] triangles;
        if (!settings.useFirstSurfaceProjection)
        {
            vertices = new[] { plane.Point(Vector2.zero)+offset, plane.Point(Vector2.right)+offset,
                plane.Point(Vector2.one)+offset, plane.Point(Vector2.up)+offset };
            uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            triangles = new[] {0,1,2,0,2,3};
        }
        else
        {
            vertices = new Vector3[polygon.Count]; uv = new Vector2[polygon.Count];
            triangles = new int[Mathf.Max(0, polygon.Count - 2) * 3];
            for (int i = 0; i < polygon.Count; i++) { vertices[i] = polygon[i] + offset; uv[i] = plane.Coordinates(polygon[i]); }
            for (int i = 0; i < polygon.Count - 2; i++) { triangles[3*i] = 0; triangles[3*i+1] = i+1; triangles[3*i+2] = i+2; }
        }
        var mesh = new Mesh { name = name+" Quad", vertices = vertices, uv = uv, triangles = triangles };
        mesh.RecalculateBounds();
        _owned[slot] = texture; _owned[slot+1] = material; _owned[slot+2] = mesh;
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.layer = layer; go.transform.SetParent(transform, false);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        go.SetActive(false); return go;
    }
    public void SetVisible(bool visible)
    {
        visible &= isActiveAndEnabled;
        if (_ground != null && _ground.activeSelf != visible) _ground.SetActive(visible);
        if (_wall != null && _wall.activeSelf != (visible && _wallConfigured)) _wall.SetActive(visible && _wallConfigured);
    }
    public bool RebuildExperiment(RoofCanopyShadowSettings settings, int layer)
    {
        if (settings == null || settings.shadowMaterial == null || !settings.useFirstSurfaceProjection || !settings.wallEnabled || !settings.wallCalibrated ||
            !settings.Ground.IsValid || !settings.Wall.IsValid) return false;
        bool wasVisible = GroundVisible || WallVisible;
        ReleaseReceivers(); Initialize(settings, layer); SetVisible(wasVisible); return true;
    }
    private void OnDisable() => SetVisible(false);
    private void OnDestroy()
    {
        ReleaseReceivers();
    }
    private void ReleaseReceivers()
    {
        // Destroy is deferred in Player builds; hide old receivers immediately
        // so an explicit rebuild cannot draw old and new masks in the same frame.
        if (_ground != null) { _ground.SetActive(false); Release(_ground); }
        if (_wall != null) { _wall.SetActive(false); Release(_wall); }
        for (int i = 0; i < _owned.Length; i++) { if (_owned[i] != null) Release(_owned[i]); _owned[i] = null; }
        _ground = _wall = null;
    }
    private static void Release(Object value) { if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
}
