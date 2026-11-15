using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Immutable description of the physical board used by DJI vision.
/// Board axes are +X right, +Y up, and +Z outward from the printed surface; all dimensions are meters.
/// </summary>
public sealed class ReferenceBoardDefinition
{
    public const string Version = "LONG_RANGE_V2";
    // The print-tolerant AprilTag-only board leaves 5 mm at each outer edge and
    // a 10 mm gap between the four 170 mm tiles.
    public const float PhysicalWidthMeters = 0.36f;
    public const float PhysicalHeightMeters = 0.36f;
    public const float OuterMarginMeters = 0.005f;
    public const float InnerGapMeters = 0.01f;
    // The complete printed tag artwork is a 9x9 tile. AprilTag's pose API does
    // not use that outside dimension: it uses the four detection corners at
    // the black/white border transition. tagStandard41h12 has 5 border modules.
    public const float PrintedTagTileSizeMeters = 0.17f;
    public const int AprilTagWidthAtBorderModules = 5;
    public const int AprilTagTotalWidthModules = 9;
    public const float AprilTagDetectionSizeMeters =
        PrintedTagTileSizeMeters * AprilTagWidthAtBorderModules / AprilTagTotalWidthModules;
    public const float DjiMarkerCenterOffsetMeters = 0.09f;
    public const float DjiMarkerCenterSpacingMeters = 0.18f;

    // The native AprilTag pose frame is +X print-right, +Y print-down, and +Z
    // into the board. This proper 180-degree X rotation maps it to board axes.
    private static readonly Quaternion BoardFromAprilTagMarkerRotation =
        Quaternion.AngleAxis(180f, Vector3.right);

    private static readonly IReadOnlyList<FiducialMarkerDefinition> DjiMarkers = new[]
    {
        // Every marker is printed upright in board coordinates. The native
        // marker basis differs from board +Y/+Z, so the same rigid basis map is
        // applied once at this board-definition boundary.
        new FiducialMarkerDefinition("0", new Vector3(-DjiMarkerCenterOffsetMeters,  DjiMarkerCenterOffsetMeters, 0f), BoardFromAprilTagMarkerRotation, AprilTagDetectionSizeMeters),
        new FiducialMarkerDefinition("1", new Vector3( DjiMarkerCenterOffsetMeters,  DjiMarkerCenterOffsetMeters, 0f), BoardFromAprilTagMarkerRotation, AprilTagDetectionSizeMeters),
        new FiducialMarkerDefinition("2", new Vector3(-DjiMarkerCenterOffsetMeters, -DjiMarkerCenterOffsetMeters, 0f), BoardFromAprilTagMarkerRotation, AprilTagDetectionSizeMeters),
        new FiducialMarkerDefinition("3", new Vector3( DjiMarkerCenterOffsetMeters, -DjiMarkerCenterOffsetMeters, 0f), BoardFromAprilTagMarkerRotation, AprilTagDetectionSizeMeters)
    };

    public static ReferenceBoardDefinition Default { get; } = new();

    private ReferenceBoardDefinition()
    {
    }

    public float WidthMeters => PhysicalWidthMeters;
    public float HeightMeters => PhysicalHeightMeters;
    public Vector3 BoardOriginInBoard => Vector3.zero;
    public IReadOnlyList<FiducialMarkerDefinition> DjiFiducialMarkers => DjiMarkers;

    /// <summary>Validates the fixed physical LONG_RANGE_V2 layout before it is passed to native vision.</summary>
    public static bool ValidateLongRangeV2Geometry(out string diagnostic)
    {
        const float tolerance = 0.00001f;
        var markers = Default.DjiFiducialMarkers;
        var expectedCenters = new[]
        {
            new Vector3(-0.09f,  0.09f, 0f),
            new Vector3( 0.09f,  0.09f, 0f),
            new Vector3(-0.09f, -0.09f, 0f),
            new Vector3( 0.09f, -0.09f, 0f)
        };

        var geometryMatches =
            Mathf.Abs(PhysicalWidthMeters - 0.36f) <= tolerance &&
            Mathf.Abs(PhysicalHeightMeters - 0.36f) <= tolerance &&
            Mathf.Abs(PrintedTagTileSizeMeters - 0.17f) <= tolerance &&
            Mathf.Abs(AprilTagDetectionSizeMeters - (0.17f * 5f / 9f)) <= tolerance &&
            Mathf.Abs(DjiMarkerCenterSpacingMeters - 0.18f) <= tolerance &&
            Mathf.Abs(OuterMarginMeters - 0.005f) <= tolerance &&
            Mathf.Abs(InnerGapMeters - 0.01f) <= tolerance &&
            markers.Count == expectedCenters.Length;

        var derivedOuterMargin = PhysicalWidthMeters * 0.5f - DjiMarkerCenterOffsetMeters - PrintedTagTileSizeMeters * 0.5f;
        var tileEdge = DjiMarkerCenterOffsetMeters + PrintedTagTileSizeMeters * 0.5f;
        var derivedInnerGap = DjiMarkerCenterSpacingMeters - PrintedTagTileSizeMeters;
        geometryMatches &=
            Mathf.Abs(derivedOuterMargin - OuterMarginMeters) <= tolerance &&
            Mathf.Abs(tileEdge - (PhysicalWidthMeters * 0.5f - OuterMarginMeters)) <= tolerance &&
            Mathf.Abs(derivedInnerGap - InnerGapMeters) <= tolerance;

        for (var index = 0; geometryMatches && index < markers.Count; index++)
        {
            geometryMatches =
                Vector3.Distance(markers[index].CenterInBoard, expectedCenters[index]) <= tolerance &&
                Mathf.Abs(markers[index].AprilTagDetectionSizeMeters - AprilTagDetectionSizeMeters) <= tolerance &&
                Mathf.Abs(Quaternion.Dot(markers[index].RotationInBoard, BoardFromAprilTagMarkerRotation)) >= 1f - tolerance;
        }

        diagnostic = geometryMatches
            ? "LONG_RANGE_V2 geometry validated: board=0.360m printedTag=0.170m detectionTag=0.094444m centers=+/-0.090m outerMargin=0.005m innerGap=0.010m AprilTagOnly=true."
            : "LONG_RANGE_V2 geometry validation failed.";
        return geometryMatches;
    }

    public bool TryGetDjiFiducialMarker(string id, out FiducialMarkerDefinition marker)
    {
        foreach (var candidate in DjiMarkers)
        {
            if (candidate.Id == id)
            {
                marker = candidate;
                return true;
            }
        }

        marker = null;
        return false;
    }

    /// <summary>Extensible marker specification for future DJI visual localization.</summary>
    public sealed class FiducialMarkerDefinition
    {
        public FiducialMarkerDefinition(string id, Vector3 centerInBoard, Quaternion rotationInBoard, float aprilTagDetectionSizeMeters)
        {
            Id = id;
            CenterInBoard = centerInBoard;
            RotationInBoard = Quaternion.Normalize(rotationInBoard);
            AprilTagDetectionSizeMeters = aprilTagDetectionSizeMeters;
        }

        public string Id { get; }
        public Vector3 CenterInBoard { get; }
        public Quaternion RotationInBoard { get; }
        /// <summary>
        /// Physical side length passed to AprilTag pose estimation and used for
        /// board correspondences. For tagStandard41h12 this is the black/white
        /// border-transition width, not the complete printed 9x9 tile width.
        /// </summary>
        public float AprilTagDetectionSizeMeters { get; }
        public Pose BoardFromMarker => new(CenterInBoard, RotationInBoard);
    }
}
