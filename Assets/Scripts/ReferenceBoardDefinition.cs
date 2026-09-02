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
    // The feature-rich AR Foundation image remains 180 mm square at the
    // center. The physical board is larger so DJI-visible fiducials can be
    // printed around it without changing the phone target itself.
    public const float PhysicalWidthMeters = 0.36f;
    public const float PhysicalHeightMeters = 0.36f;
    public const float BuildingReferenceImageWidthMeters = 0.18f;
    public const float BuildingReferenceImageHeightMeters = 0.18f;
    public const float DjiMarkerSizeMeters = 0.06f;

    // ARTrackedImage axes are +X right, +Y outward normal, +Z opposite physical board-up.
    // This is T_trackedImage_board: board +X -> image +X, board +Y -> image -Z, board +Z -> image +Y.
    private static readonly Pose TrackedImageFromBoardPose = new(Vector3.zero, Quaternion.Euler(-90f, 0f, 0f));
    private static readonly IReadOnlyList<FiducialMarkerDefinition> DjiMarkers = new[]
    {
        // Every marker faces out of the same printed board surface. IDs and
        // dimensions are deterministic and shared with the native detector.
        new FiducialMarkerDefinition("0", new Vector3(-0.135f,  0.135f, 0f), Quaternion.identity, DjiMarkerSizeMeters),
        new FiducialMarkerDefinition("1", new Vector3( 0.135f,  0.135f, 0f), Quaternion.identity, DjiMarkerSizeMeters),
        new FiducialMarkerDefinition("2", new Vector3(-0.135f, -0.135f, 0f), Quaternion.identity, DjiMarkerSizeMeters),
        new FiducialMarkerDefinition("3", new Vector3( 0.135f, -0.135f, 0f), Quaternion.identity, DjiMarkerSizeMeters)
    };

    public static ReferenceBoardDefinition Default { get; } = new();

    private ReferenceBoardDefinition()
    {
    }

    public float WidthMeters => PhysicalWidthMeters;
    public float HeightMeters => PhysicalHeightMeters;
    public Vector3 BoardOriginInBoard => Vector3.zero;
    public float PhoneImageWidthMeters => BuildingReferenceImageWidthMeters;
    public float PhoneImageHeightMeters => BuildingReferenceImageHeightMeters;
    public IReadOnlyList<FiducialMarkerDefinition> DjiFiducialMarkers => DjiMarkers;

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
