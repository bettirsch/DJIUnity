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
}
