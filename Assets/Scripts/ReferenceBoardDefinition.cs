using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Immutable description of the physical board shared by phone AR tracking and future DJI vision.
/// Board axes are +X right, +Y up, and +Z outward from the printed surface; all dimensions are meters.
/// </summary>
public sealed class ReferenceBoardDefinition
{
    public const string BuildingReferenceImageName = "BuildingReference";
    public const float PhysicalWidthMeters = 0.18f;
    public const float PhysicalHeightMeters = 0.18f;

    // ARTrackedImage axes are +X right, +Y outward normal, +Z opposite physical board-up.
    // This is T_trackedImage_board: board +X -> image +X, board +Y -> image -Z, board +Z -> image +Y.
    private static readonly Pose TrackedImageFromBoardPose = new(Vector3.zero, Quaternion.Euler(-90f, 0f, 0f));
    private static readonly IReadOnlyList<FiducialMarkerDefinition> EmptyMarkers = Array.Empty<FiducialMarkerDefinition>();

    public static ReferenceBoardDefinition Default { get; } = new();

    private ReferenceBoardDefinition()
    {
    }

    public float WidthMeters => PhysicalWidthMeters;
    public float HeightMeters => PhysicalHeightMeters;
    public Vector3 BoardOriginInBoard => Vector3.zero;
    public IReadOnlyList<FiducialMarkerDefinition> FutureDjiMarkers => EmptyMarkers;

    /// <summary>T_trackedImage_board, used with T_world_trackedImage to produce T_world_board.</summary>
    public Pose TrackedImageFromBoard => TrackedImageFromBoardPose;

    /// <summary>T_board_trackedImage, retained explicitly for future board-relative image layouts.</summary>
    public Pose BoardFromTrackedImage => ReferenceFrameTransforms.Invert(TrackedImageFromBoardPose);

    public bool MatchesPhoneReferenceImage(string imageName) => imageName == BuildingReferenceImageName;

    /// <summary>Extensible marker specification for future DJI visual localization.</summary>
    public sealed class FiducialMarkerDefinition
    {
        public FiducialMarkerDefinition(string id, Vector3 centerInBoard, Quaternion rotationInBoard, float physicalSizeMeters)
        {
            Id = id;
            CenterInBoard = centerInBoard;
            RotationInBoard = Quaternion.Normalize(rotationInBoard);
            PhysicalSizeMeters = physicalSizeMeters;
        }

        public string Id { get; }
        public Vector3 CenterInBoard { get; }
        public Quaternion RotationInBoard { get; }
        public float PhysicalSizeMeters { get; }
        public Pose BoardFromMarker => new(CenterInBoard, RotationInBoard);
    }
}
