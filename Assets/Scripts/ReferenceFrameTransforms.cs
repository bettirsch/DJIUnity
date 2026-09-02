using UnityEngine;

/// <summary>
/// Named rigid-transform helpers. Every matrix is named "parentFromChild" and maps child coordinates into parent coordinates.
/// </summary>
public static class ReferenceFrameTransforms
{
    private const float MatrixTolerance = 0.0001f;

    public static Matrix4x4 MatrixFromPose(Pose parentFromChild) =>
        Matrix4x4.TRS(parentFromChild.position, parentFromChild.rotation, Vector3.one);

    public static Pose PoseFromMatrix(Matrix4x4 parentFromChild)
    {
        var translation = parentFromChild.GetColumn(3);
        return new Pose(new Vector3(translation.x, translation.y, translation.z), parentFromChild.rotation);
    }

    public static Pose Invert(Pose parentFromChild) => PoseFromMatrix(MatrixFromPose(parentFromChild).inverse);

    public static Matrix4x4 Compose(Matrix4x4 parentFromChild, Matrix4x4 childFromGrandchild) =>
        parentFromChild * childFromGrandchild;

    public static Matrix4x4 WorldFromBoard(Pose worldFromBoard) => MatrixFromPose(worldFromBoard);
    public static Matrix4x4 BoardFromWorld(Pose worldFromBoard) => WorldFromBoard(worldFromBoard).inverse;
    public static Matrix4x4 WorldFromCamera(Pose worldFromCamera) => MatrixFromPose(worldFromCamera);
    public static Matrix4x4 CameraFromWorld(Pose worldFromCamera) => WorldFromCamera(worldFromCamera).inverse;

    public static Matrix4x4 CameraFromBoard(Matrix4x4 cameraFromWorld, Matrix4x4 worldFromBoard) =>
        Compose(cameraFromWorld, worldFromBoard);

    public static Matrix4x4 BoardFromCamera(Matrix4x4 boardFromWorld, Matrix4x4 worldFromCamera) =>
        Compose(boardFromWorld, worldFromCamera);

    public static Pose CalculateWorldFromBoard(Pose worldFromTrackedImage, ReferenceBoardDefinition boardDefinition) =>
        PoseFromMatrix(Compose(MatrixFromPose(worldFromTrackedImage), MatrixFromPose(boardDefinition.TrackedImageFromBoard)));

    public static bool ValidateWorldBoardRoundTrip(Pose worldFromBoard, out string result)
    {
        var worldFromBoardMatrix = WorldFromBoard(worldFromBoard);
        var boardFromWorldMatrix = BoardFromWorld(worldFromBoard);
        var identityError = MatrixDifference(worldFromBoardMatrix * boardFromWorldMatrix, Matrix4x4.identity);
        var points = new[]
        {
            Vector3.zero,
            new Vector3(0.04f, -0.03f, 0.02f),
            new Vector3(-0.08f, 0.06f, -0.01f)
        };
        var maximumRoundTripError = 0f;
        foreach (var boardPoint in points)
        {
            var worldPoint = worldFromBoardMatrix.MultiplyPoint3x4(boardPoint);
            var roundTrippedBoardPoint = boardFromWorldMatrix.MultiplyPoint3x4(worldPoint);
            maximumRoundTripError = Mathf.Max(maximumRoundTripError, Vector3.Distance(boardPoint, roundTrippedBoardPoint));
        }

        var valid = identityError <= MatrixTolerance && maximumRoundTripError <= MatrixTolerance;
        result = $"identityError={identityError:E3} maxPointRoundTripError={maximumRoundTripError:E3} valid={valid}";
        return valid;
    }

    private static float MatrixDifference(Matrix4x4 first, Matrix4x4 second)
    {
        var maximumDifference = 0f;
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 4; column++)
            maximumDifference = Mathf.Max(maximumDifference, Mathf.Abs(first[row, column] - second[row, column]));

        return maximumDifference;
    }
}
