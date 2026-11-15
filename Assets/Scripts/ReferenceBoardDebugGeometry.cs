using UnityEngine;

/// <summary>
/// Diagnostic-only geometry authored entirely in ReferenceBoardDefinition
/// coordinates. Its parent must be T_world_board; it never changes tracking
/// poses or shared content placement.
/// </summary>
public static class ReferenceBoardDebugGeometry
{
    public static Transform CreateBoardFrame(
        string name,
        Transform boardRoot,
        int layer,
        bool includeMarkerCenters)
    {
        var diagnosticsRoot = new GameObject(name).transform;
        diagnosticsRoot.SetParent(boardRoot, false);
        CreateAxis(diagnosticsRoot, "Board +X", Vector3.right, Color.red, layer);
        CreateAxis(diagnosticsRoot, "Board +Y", Vector3.up, Color.green, layer);
        CreateAxis(diagnosticsRoot, "Board +Z", Vector3.forward, Color.blue, layer);
        CreateBoardRectangle(diagnosticsRoot, layer);
        if (includeMarkerCenters)
            CreateMarkerCenters(diagnosticsRoot, layer);
        return diagnosticsRoot;
    }

    private static void CreateAxis(Transform parent, string name, Vector3 direction, Color color, int layer)
    {
        const float length = 0.10f;
        const float thickness = 0.006f;
        var axis = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        axis.name = name;
        axis.layer = layer;
        axis.transform.SetParent(parent, false);
        axis.transform.localPosition = direction * (length * 0.5f);
        axis.transform.localRotation = Quaternion.FromToRotation(Vector3.up, direction);
        axis.transform.localScale = new Vector3(thickness, length * 0.5f, thickness);
        SetMaterial(axis, color);
    }

    private static void CreateBoardRectangle(Transform parent, int layer)
    {
        const float thickness = 0.003f;
        const float offset = 0.0015f;
        var width = ReferenceBoardDefinition.PhysicalWidthMeters;
        var height = ReferenceBoardDefinition.PhysicalHeightMeters;
        CreateLine(parent, "Board Top", new Vector3(0f, height * 0.5f, offset), Vector3.right, width, thickness, Color.white, layer);
        CreateLine(parent, "Board Bottom", new Vector3(0f, -height * 0.5f, offset), Vector3.right, width, thickness, Color.white, layer);
        CreateLine(parent, "Board Left", new Vector3(-width * 0.5f, 0f, offset), Vector3.up, height, thickness, Color.white, layer);
        CreateLine(parent, "Board Right", new Vector3(width * 0.5f, 0f, offset), Vector3.up, height, thickness, Color.white, layer);
    }

    private static void CreateLine(Transform parent, string name, Vector3 position, Vector3 direction, float length, float thickness, Color color, int layer)
    {
        var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
        line.name = name;
        line.layer = layer;
        line.transform.SetParent(parent, false);
        line.transform.localPosition = position;
        line.transform.localRotation = Quaternion.FromToRotation(Vector3.right, direction);
        line.transform.localScale = new Vector3(length, thickness, thickness);
        SetMaterial(line, color);
    }

    private static void CreateMarkerCenters(Transform parent, int layer)
    {
        foreach (var marker in ReferenceBoardDefinition.Default.DjiFiducialMarkers)
        {
            const float armLength = 0.025f;
            const float thickness = 0.0025f;
            const float offset = 0.0055f;
            var center = marker.CenterInBoard + Vector3.forward * offset;
            CreateLine(parent, $"Marker {marker.Id} Center Cross Horizontal", center, Vector3.right, armLength, thickness, Color.yellow, layer);
            CreateLine(parent, $"Marker {marker.Id} Center Cross Vertical", center, Vector3.up, armLength, thickness, Color.yellow, layer);
        }
    }

    private static void SetMaterial(GameObject gameObject, Color color)
    {
        var renderer = gameObject.GetComponent<Renderer>();
        if (renderer == null)
            return;
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader);
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        else if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);
        renderer.material = material;
    }
}
