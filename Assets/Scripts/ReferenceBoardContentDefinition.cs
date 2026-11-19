using UnityEngine;

/// <summary>
/// Authoritative board-local content for the DJI board frame.
/// T_board_cube places the cube with its bottom face on the board's printed
/// surface (+Z is outward), independent of either camera implementation.
/// </summary>
public static class ReferenceBoardContentDefinition
{
    public const string SharedContentRootName = "SharedReferenceContent";
    public const string DummyCubeName = "DummyCube";
    public const float DummyCubeSizeMeters = 0.14f;

    // Placement reference only: vision/world coordinates remain board-centered.
    public static Pose BoardFromRoofCanopyReference => new(
        new Vector3(-ReferenceBoardDefinition.Default.WidthMeters * 0.5f,
            ReferenceBoardDefinition.Default.HeightMeters * 0.5f, 0f),
        Quaternion.identity);

    private static readonly Color DummyCubeColor = new(0.12f, 0.74f, 0.58f, 1f);

    // This rotation keeps the cube upright in the established board frame.
    public static Pose BoardFromDummyCube => new(
        Vector3.forward * (DummyCubeSizeMeters * 0.5f),
        Quaternion.Euler(90f, 0f, 0f));

    public static Vector3 DummyCubeScale => Vector3.one * DummyCubeSizeMeters;

    public static Matrix4x4 WorldFromDummyCube(Pose worldFromBoard) =>
        ReferenceFrameTransforms.Compose(
            ReferenceFrameTransforms.WorldFromBoard(worldFromBoard),
            ReferenceFrameTransforms.MatrixFromPose(BoardFromDummyCube));

    public static GameObject CreateDummyCube(Transform parent, int layer = 0)
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = DummyCubeName;
        cube.layer = layer;
        cube.transform.SetParent(parent, false);
        ApplyBoardLocalTransform(cube.transform);

        var renderer = cube.GetComponent<Renderer>();
        if (renderer != null)
            renderer.material = CreateRuntimeMaterial(DummyCubeColor);

        return cube;
    }

    public static void ApplyBoardLocalTransform(Transform cubeTransform)
    {
        var boardFromCube = BoardFromDummyCube;
        cubeTransform.localPosition = boardFromCube.position;
        cubeTransform.localRotation = boardFromCube.rotation;
        cubeTransform.localScale = DummyCubeScale;
    }

    public static void ApplyWorldTransform(Transform cubeTransform, Pose worldFromBoard)
    {
        var worldFromCube = ReferenceFrameTransforms.PoseFromMatrix(WorldFromDummyCube(worldFromBoard));
        cubeTransform.SetPositionAndRotation(worldFromCube.position, worldFromCube.rotation);
        cubeTransform.localScale = DummyCubeScale;
    }

    public static void LogBoardLocal(string prefix)
    {
        var boardFromCube = BoardFromDummyCube;
        Debug.Log(
            $"{prefix} SHARED_CUBE_BOARD_LOCAL position={boardFromCube.position} " +
            $"rotation={boardFromCube.rotation.eulerAngles} scale={DummyCubeScale}");
    }

    public static void LogDefinitionAndBoardContact(string prefix)
    {
        var boardFromCube = BoardFromDummyCube;
        var minZ = float.PositiveInfinity;
        var maxZ = float.NegativeInfinity;
        var contactMinZ = float.PositiveInfinity;
        var contactMaxZ = float.NegativeInfinity;
        var boardFromCubeMatrix = Matrix4x4.TRS(boardFromCube.position, boardFromCube.rotation, DummyCubeScale);
        foreach (var localVertex in CubeVertices())
        {
            var boardVertex = boardFromCubeMatrix.MultiplyPoint3x4(localVertex);
            minZ = Mathf.Min(minZ, boardVertex.z);
            maxZ = Mathf.Max(maxZ, boardVertex.z);
            if (localVertex.y < 0f)
            {
                contactMinZ = Mathf.Min(contactMinZ, boardVertex.z);
                contactMaxZ = Mathf.Max(contactMaxZ, boardVertex.z);
            }
        }

        Debug.Log(
            $"{prefix} SHARED_CUBE_DEFINITION localPosition={boardFromCube.position} " +
            $"localRotation={boardFromCube.rotation.eulerAngles} localScale={DummyCubeScale} physicalSizeM={DummyCubeSizeMeters:F3}");
        Debug.Log(
            $"{prefix} CUBE_BOARD_CONTACT minZ={minZ:F6} maxZ={maxZ:F6} " +
            $"contactFaceZ=[{contactMinZ:F6},{contactMaxZ:F6}]");
    }

    private static Vector3[] CubeVertices()
    {
        const float half = 0.5f;
        return new[]
        {
            new Vector3(-half, -half, -half), new Vector3(half, -half, -half),
            new Vector3(-half, -half, half), new Vector3(half, -half, half),
            new Vector3(-half, half, -half), new Vector3(half, half, -half),
            new Vector3(-half, half, half), new Vector3(half, half, half)
        };
    }

    private static Material CreateRuntimeMaterial(Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader);
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        else if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);
        return material;
    }
}
