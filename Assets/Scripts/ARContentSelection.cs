using UnityEngine;

public enum ARContentType
{
    Cube = 0,
    // Value 1 was Sphere. Keep it reserved for persisted selections.
    RoofCanopy = 2
}

/// <summary>Persistent, scene-independent selection for the single board-local AR object.</summary>
public static class ARContentSelection
{
    private const string PlayerPrefsKey = "SelectedARContent";

    public static ARContentType Selected
    {
        get
        {
            var value = PlayerPrefs.GetInt(PlayerPrefsKey, (int)ARContentType.Cube);
            if (value == (int)ARContentType.RoofCanopy)
                return ARContentType.RoofCanopy;
            if (value != (int)ARContentType.Cube)
                Set(ARContentType.Cube);
            return ARContentType.Cube;
        }
    }

    public static void Set(ARContentType type)
    {
        if (type != ARContentType.RoofCanopy)
            type = ARContentType.Cube;
        PlayerPrefs.SetInt(PlayerPrefsKey, (int)type);
        PlayerPrefs.Save();
        Debug.Log($"AR_CONTENT_SELECTED type={type}");
    }
}

/// <summary>Creates exactly one selected object beneath an existing board-content root.</summary>
public static class ARContentFactory
{
    private const string RoofCanopyResourcePath = "ARContent/RoofAssets/roof_canopy1";
    // Scale-1 FBX wood vertex 322 after Rx(+90): bottom corner of the left-rear upright.
    // The front feet extend 59 mm below this rear support; this is not a floating AABB corner.
    // Validated by geometry tests rather than reading non-readable mesh vertices in the player.
    private static readonly Vector3 CanopyLeftRearBottomAnchorMeters =
        new Vector3(0.099755391f, 0.014564678f, 0.086760163f);

    public static GameObject CreateSelected(Transform parent, int layer = 0) =>
        Create(ARContentSelection.Selected, parent, layer);

    public static GameObject Create(ARContentType type, Transform parent, int layer = 0)
    {
        if (type != ARContentType.RoofCanopy)
            type = ARContentType.Cube;
        var content = type == ARContentType.RoofCanopy ? CreateRoofCanopy(parent, layer) : CreateCube(parent, layer);

        Debug.Log(
            $"AR_CONTENT_INSTANTIATED type={type} coordinateSpace=BOARD " +
            $"root={content.name}");
        return content;
    }

    public static void SetRenderersEnabled(GameObject content, bool enabled)
    {
        foreach (var renderer in content.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = enabled;
    }

    private static GameObject CreateCube(Transform parent, int layer)
    {
        return ReferenceBoardContentDefinition.CreateDummyCube(parent, layer);
    }

    private static GameObject CreateRoofCanopy(
        Transform parent,
        int layer)
    {
        var prefab = Resources.Load<GameObject>(RoofCanopyResourcePath);
        if (prefab == null)
        {
            Debug.LogError($"AR_CONTENT_INSTANTIATION_FAILED type=RoofCanopy resource={RoofCanopyResourcePath}");
            return CreateCube(parent, layer);
        }

        var placement = new GameObject("RoofCanopyPlacement");
        placement.layer = layer;
        placement.transform.SetParent(parent, false);
        placement.transform.localPosition = -CanopyLeftRearBottomAnchorMeters;
        placement.transform.localRotation = Quaternion.identity;
        placement.transform.localScale = Vector3.one;

        var canopy = Object.Instantiate(prefab, placement.transform, false);
        var sourceRootPosition = canopy.transform.localPosition;
        var sourceRootRotation = canopy.transform.localRotation;
        var sourceRootScale = canopy.transform.localScale;
        canopy.name = "BoardRoofCanopy";
        SetLayerRecursively(canopy.transform, layer);
        canopy.transform.localPosition = Vector3.zero;
        // The imported model has local +Y as its height axis; board +Z is outward.
        canopy.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        canopy.transform.localScale = Vector3.one;

        var boardBounds = CalculateBoundsInParent(canopy, parent);
        Debug.Log(
            $"AR_CONTENT_CANOPY_CONFIG source={RoofCanopyResourcePath} sourceRootPosition={sourceRootPosition} " +
            $"sourceRootRotation={sourceRootRotation.eulerAngles} sourceRootScale={sourceRootScale} " +
            $"rootScale={canopy.transform.localScale} boardBounds={boardBounds.size} " +
            $"anchorBoardMeters={CanopyLeftRearBottomAnchorMeters} placementOffset={placement.transform.localPosition} " +
            $"pivot={canopy.transform.localPosition} localRotation={canopy.transform.localRotation.eulerAngles}");

        return placement;
    }

    private static Bounds CalculateBoundsInParent(GameObject content, Transform parent)
    {
        var hasBounds = false;
        var bounds = new Bounds();
        foreach (var renderer in content.GetComponentsInChildren<Renderer>(true))
        {
            var worldBounds = renderer.bounds;
            var min = worldBounds.min;
            var max = worldBounds.max;
            for (var x = 0; x <= 1; x++)
            for (var y = 0; y <= 1; y++)
            for (var z = 0; z <= 1; z++)
            {
                var point = parent.InverseTransformPoint(new Vector3(
                    x == 0 ? min.x : max.x,
                    y == 0 ? min.y : max.y,
                    z == 0 ? min.z : max.z));
                if (hasBounds)
                    bounds.Encapsulate(point);
                else
                {
                    bounds = new Bounds(point, Vector3.zero);
                    hasBounds = true;
                }
            }
        }

        return bounds;
    }

    private static void SetLayerRecursively(Transform transform, int layer)
    {
        transform.gameObject.layer = layer;
        foreach (Transform child in transform)
            SetLayerRecursively(child, layer);
    }
}
