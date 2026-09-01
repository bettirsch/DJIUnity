using System;
using UnityEngine;

/// <summary>
/// Calibration for the exact decoded DJI CPU-frame geometry used by board
/// vision. A measured checkerboard calibration always takes precedence over
/// the optional, explicitly enabled public FC3582 prototype profile.
/// </summary>
[DisallowMultipleComponent]
public sealed class DjiCameraCalibration : MonoBehaviour
{
    private const string MeasuredResourceName = "DjiCameraCalibration";
    private const string ProvisionalResourceName = "DjiCameraCalibrationProvisionalFc3582";

    [Serializable]
    public sealed class Data
    {
        public string calibrationVersion = "UNCONFIGURED";
        public string status = "unconfigured";
        public string sourceModel = "";
        public string sourceNotes = "";
        public string sourceUrls = "";
        public int imageWidth = 1920;
        public int imageHeight = 1080;
        public string detectorFrameFormat = "YUV_420_888_LUMA8";
        public string pixelFormat = "YUV_420_888_LUMA8";
        public float fx;
        public float fy;
        public float cx;
        public float cy;
        public float[] distortionCoefficients = Array.Empty<float>();
        public float k1;
        public float k2;
        public float p1;
        public float p2;
        public float k3;
        public string distortionModel = "";
        public string distortionNotes = "";
        public float calibrationRms = -1f;
        public string calibrationDate = "";
        public bool valid;
        public bool isMeasuredCalibration;
        public int rotationDegrees;
        public bool mirrorX;
        public bool provisional = true;
        public string source = "UNCONFIGURED";
    }

    [SerializeField] private bool allowProvisionalCalibrationForTesting;
    [SerializeField] private Data measuredData = new();
    [SerializeField] private Data provisionalData = new();

    /// <summary>Measured data wins whenever it is structurally valid.</summary>
    public Data Current => HasMeasuredCalibration ? measuredData : HasProvisionalPublicCalibration ? provisionalData : measuredData;
    public float[] DistortionCoefficients => new[] { Current.k1, Current.k2, Current.p1, Current.p2, Current.k3 };
    public bool AllowProvisionalCalibrationForTesting => allowProvisionalCalibrationForTesting;
    public bool HasMeasuredCalibration =>
        IsStructurallyValid(measuredData) &&
        !measuredData.provisional &&
        (measuredData.isMeasuredCalibration || measuredData.calibrationRms > 0f);
    public bool HasProvisionalPublicCalibration =>
        IsStructurallyValid(provisionalData) &&
        provisionalData.provisional &&
        !provisionalData.isMeasuredCalibration &&
        provisionalData.status == "provisional_public_fc3582";
    public bool HasAvailableCalibration => HasMeasuredCalibration || HasProvisionalPublicCalibration;

    /// <summary>
    /// Only this property authorizes DJI_WORLD_INITIALIZED. The public profile
    /// remains diagnostic-only until a caller deliberately opts in.
    /// </summary>
    public bool CanInitializeWorld => HasMeasuredCalibration ||
                                      (allowProvisionalCalibrationForTesting && HasProvisionalPublicCalibration);

    // Retained for existing callers; it now means safe world initialization.
    public bool HasUsableCalibration => CanInitializeWorld;

    public bool IsRuntimeFrameGeometryCompatible(int width, int height, string format)
    {
        var calibration = Current;
        return HasAvailableCalibration &&
               calibration.imageWidth == width &&
               calibration.imageHeight == height &&
               calibration.detectorFrameFormat == format &&
               calibration.rotationDegrees == 0 &&
               !calibration.mirrorX;
    }

    public void SetAllowProvisionalCalibrationForTesting(bool allowed)
    {
        allowProvisionalCalibrationForTesting = allowed;
        Debug.Log($"DJI_PROVISIONAL_CALIBRATION_MODE allowed={allowed} available={HasProvisionalPublicCalibration} measuredPreferred={HasMeasuredCalibration}");
    }

    private void Awake()
    {
        measuredData = LoadResource(MeasuredResourceName, measuredData);
        provisionalData = LoadResource(ProvisionalResourceName, provisionalData);

        var current = Current;

        Debug.Log(
            $"DJI_CAMERA_CALIBRATION status={current.status} source={current.source} model={current.sourceModel} frame={current.imageWidth}x{current.imageHeight} " +
            $"fx={current.fx:F2} fy={current.fy:F2} cx={current.cx:F2} cy={current.cy:F2} " +
            $"distortion=[{current.k1:F6},{current.k2:F6},{current.p1:F6},{current.p2:F6},{current.k3:F6}] rms={current.calibrationRms:F3} " +
            $"version={current.calibrationVersion} rotationDegrees={current.rotationDegrees} mirrorX={current.mirrorX} measured={HasMeasuredCalibration} " +
            $"provisionalAvailable={HasProvisionalPublicCalibration} worldInitializationAllowed={CanInitializeWorld}");

        if (HasProvisionalPublicCalibration && !CanInitializeWorld)
            Debug.LogWarning("DJI_PROVISIONAL_CALIBRATION_DIAGNOSTIC_ONLY reason=ALLOW_PROVISIONAL_CALIBRATION_FOR_TESTING_FALSE");
        else if (!HasAvailableCalibration)
            Debug.LogWarning("DJI_CAMERA_CALIBRATION_UNUSABLE reason=EXACT_CPU_FRAME_INTRINSICS_REQUIRED");
    }

    private static Data LoadResource(string resourceName, Data fallback)
    {
        var json = Resources.Load<TextAsset>(resourceName);
        if (json == null || string.IsNullOrWhiteSpace(json.text))
            return fallback;
        return JsonUtility.FromJson<Data>(json.text) ?? fallback;
    }

    private static bool IsStructurallyValid(Data calibration)
    {
        return calibration != null &&
               calibration.valid &&
               calibration.imageWidth > 0 &&
               calibration.imageHeight > 0 &&
               calibration.fx > 0f &&
               calibration.fy > 0f &&
               float.IsFinite(calibration.cx) &&
               float.IsFinite(calibration.cy) &&
               float.IsFinite(calibration.k1) &&
               float.IsFinite(calibration.k2) &&
               float.IsFinite(calibration.p1) &&
               float.IsFinite(calibration.p2) &&
               float.IsFinite(calibration.k3) &&
               !string.IsNullOrWhiteSpace(calibration.calibrationVersion) &&
               calibration.detectorFrameFormat == "YUV_420_888_LUMA8" &&
               calibration.rotationDegrees == 0 &&
               !calibration.mirrorX;
    }
}
