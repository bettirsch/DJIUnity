using UnityEngine;

/// <summary>
/// Build-time diagnostic settings for DJI reference-board calibration runs.
/// The asset lives in Resources so DroneView can read it before creating its
/// runtime-only vision provider.
/// </summary>
[CreateAssetMenu(
    fileName = "DjiBoardVisionValidationSettings",
    menuName = "DJI/Board Vision Validation Settings")]
public sealed class DjiBoardVisionValidationSettings : ScriptableObject
{
    public const string ResourceName = "DjiBoardVisionValidationSettings";

    [Tooltip(
        "Uses DjiCameraCalibrationProvisionalFc3582.json for ImageReader board detection and PnP diagnostics only. " +
        "This never enables DJI_WORLD_INITIALIZED.")]
    public bool allowProvisionalCalibrationForValidation;

    [Tooltip(
        "Renders the shared board-local dummy cube over DJI video while the provisional profile is in validation mode. " +
        "This never enables DJI_WORLD_INITIALIZED.")]
    public bool allowProvisionalCalibrationForSharedCubePreview;
}
