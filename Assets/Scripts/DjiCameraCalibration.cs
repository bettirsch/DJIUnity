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

    public enum ValidationState
    {
        UNVALIDATED,
        SUFFICIENT,
        PHYSICAL_CALIBRATION_REQUIRED,
        MEASURED_CALIBRATION
    }

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
        public string crop = "NONE";
        public string resize = "NONE";
        public bool mirrorX;
        public bool provisional = true;
        public string source = "UNCONFIGURED";
    }

    [SerializeField] private bool allowProvisionalCalibrationForValidation;
    [SerializeField] private ValidationState provisionalValidationState = ValidationState.UNVALIDATED;
    [SerializeField] private int physicalValidationSampleCount;
    [SerializeField] private bool physicalValidationCoverageComplete;
    [SerializeField] private string physicalValidationBoardVersion = "";
    [SerializeField] private Data measuredData = new();
    [SerializeField] private Data provisionalData = new();

    /// <summary>Measured data wins whenever it is structurally valid.</summary>
    public Data Current => HasMeasuredCalibration ? measuredData : HasProvisionalPublicCalibration ? provisionalData : measuredData;
    public float[] DistortionCoefficients => new[] { Current.k1, Current.k2, Current.p1, Current.p2, Current.k3 };
    public bool AllowProvisionalCalibrationForValidation => allowProvisionalCalibrationForValidation;
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
    public ValidationState CurrentValidationState => HasMeasuredCalibration
        ? ValidationState.MEASURED_CALIBRATION
        : HasProvisionalPublicCalibration ? provisionalValidationState : ValidationState.UNVALIDATED;
    public bool HasPhysicalValidationEvidence => physicalValidationSampleCount > 0;
    public bool HasSufficientPhysicalValidationEvidence => physicalValidationSampleCount >= 10 && physicalValidationCoverageComplete;

    /// <summary>
    /// The public FC3582 profile may run the detector/PnP only to collect
    /// validation measurements. It never authorizes world initialization.
    /// </summary>
    public bool CanRunBoardPoseDiagnostics => HasMeasuredCalibration ||
                                              (allowProvisionalCalibrationForValidation && HasProvisionalPublicCalibration);

    /// <summary>
    /// Only a measured checkerboard calibration authorizes DJI_WORLD_INITIALIZED.
    /// </summary>
    public bool CanInitializeWorld => HasMeasuredCalibration;

    // Retained for existing callers; it now means safe world initialization.
    public bool HasUsableCalibration => CanInitializeWorld;

    /// <summary>
    /// Verifies that the calibration and detector observe exactly the same
    /// luma-pixel coordinate system. Pixel format is metadata here; it does
    /// not change the optical intrinsics when the packed luma geometry is the
    /// same.
    /// </summary>
    public bool TryValidateRuntimeFrameContract(
        int width,
        int height,
        string format,
        int rotationDegrees,
        string crop,
        string resize,
        bool mirrorX,
        out string mismatch)
    {
        var calibration = Current;
        if (!HasAvailableCalibration)
        {
            mismatch = "CALIBRATION_UNAVAILABLE";
            return false;
        }
        if (calibration.imageWidth != width)
        {
            mismatch = $"WIDTH expected={calibration.imageWidth} actual={width}";
            return false;
        }
        if (calibration.imageHeight != height)
        {
            mismatch = $"HEIGHT expected={calibration.imageHeight} actual={height}";
            return false;
        }
        if (!string.Equals(calibration.detectorFrameFormat, format, StringComparison.Ordinal))
        {
            mismatch = $"FORMAT expected={calibration.detectorFrameFormat} actual={format}";
            return false;
        }
        if (calibration.rotationDegrees != rotationDegrees)
        {
            mismatch = $"ROTATION expected={calibration.rotationDegrees} actual={rotationDegrees}";
            return false;
        }
        if (!string.Equals(calibration.crop, crop, StringComparison.Ordinal))
        {
            mismatch = $"CROP expected={calibration.crop} actual={crop}";
            return false;
        }
        if (!string.Equals(calibration.resize, resize, StringComparison.Ordinal))
        {
            mismatch = $"RESIZE expected={calibration.resize} actual={resize}";
            return false;
        }
        if (calibration.mirrorX != mirrorX)
        {
            mismatch = $"MIRROR expected={calibration.mirrorX} actual={mirrorX}";
            return false;
        }

        mismatch = string.Empty;
        return true;
    }

    public void SetAllowProvisionalCalibrationForValidation(bool allowed)
    {
        allowProvisionalCalibrationForValidation = allowed;
        Debug.Log($"DJI_PROVISIONAL_CALIBRATION_VALIDATION_MODE allowed={allowed} available={HasProvisionalPublicCalibration} state={CurrentValidationState}");
    }

    public void ResetProvisionalValidation()
    {
        if (!HasProvisionalPublicCalibration || HasMeasuredCalibration)
            return;
        physicalValidationSampleCount = 0;
        physicalValidationCoverageComplete = false;
        physicalValidationBoardVersion = ReferenceBoardDefinition.Version;
        provisionalValidationState = ValidationState.UNVALIDATED;
        LogValidationState("RESET");
    }

    public void RegisterPhysicalValidationObservation(int sampleCount, bool coverageComplete)
    {
        if (!HasProvisionalPublicCalibration || !allowProvisionalCalibrationForValidation || HasMeasuredCalibration)
            return;
        physicalValidationSampleCount = Mathf.Max(physicalValidationSampleCount, sampleCount);
        physicalValidationCoverageComplete |= coverageComplete;
    }

    public bool TrySetProvisionalValidationResult(ValidationState requestedState, string evidenceSummary)
    {
        if (!HasProvisionalPublicCalibration || HasMeasuredCalibration)
        {
            Debug.LogWarning("PUBLIC_CALIBRATION_RESULT_REJECTED reason=PUBLIC_PROFILE_NOT_ACTIVE");
            return false;
        }

        if (requestedState == ValidationState.UNVALIDATED)
        {
            ResetProvisionalValidation();
            return true;
        }

        if (requestedState == ValidationState.MEASURED_CALIBRATION ||
            (requestedState != ValidationState.SUFFICIENT && requestedState != ValidationState.PHYSICAL_CALIBRATION_REQUIRED))
        {
            Debug.LogWarning($"PUBLIC_CALIBRATION_RESULT_REJECTED requested={requestedState} reason=INVALID_PUBLIC_TRANSITION");
            return false;
        }

        if (!HasSufficientPhysicalValidationEvidence || string.IsNullOrWhiteSpace(evidenceSummary))
        {
            Debug.LogWarning(
                $"PUBLIC_CALIBRATION_RESULT_REJECTED requested={requestedState} reason=PHYSICAL_VALIDATION_EVIDENCE_INCOMPLETE " +
                $"samples={physicalValidationSampleCount} coverageComplete={physicalValidationCoverageComplete}");
            return false;
        }

        provisionalValidationState = requestedState;
        LogValidationState($"evidence={evidenceSummary}");
        return true;
    }

    private void Awake()
    {
        measuredData = LoadResource(MeasuredResourceName, measuredData);
        provisionalData = LoadResource(ProvisionalResourceName, provisionalData);

        ResetValidationForChangedBoardVersion();

        var current = Current;

        Debug.Log(
            $"DJI_CAMERA_CALIBRATION status={current.status} source={current.source} model={current.sourceModel} frame={current.imageWidth}x{current.imageHeight} " +
            $"fx={current.fx:F2} fy={current.fy:F2} cx={current.cx:F2} cy={current.cy:F2} " +
            $"distortion=[{current.k1:F6},{current.k2:F6},{current.p1:F6},{current.p2:F6},{current.k3:F6}] rms={current.calibrationRms:F3} " +
            $"version={current.calibrationVersion} format={current.detectorFrameFormat} rotationDegrees={current.rotationDegrees} " +
            $"crop={current.crop} resize={current.resize} mirrorX={current.mirrorX} measured={HasMeasuredCalibration} " +
            $"provisionalAvailable={HasProvisionalPublicCalibration} worldInitializationAllowed={CanInitializeWorld}");

        LogValidationState("STARTUP");

        if (HasProvisionalPublicCalibration && !CanInitializeWorld)
            Debug.LogWarning("DJI_PROVISIONAL_CALIBRATION_DIAGNOSTIC_ONLY reason=MEASURED_CALIBRATION_REQUIRED_FOR_WORLD_INITIALIZATION");
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

    private void LogValidationState(string context)
    {
        Debug.Log(
            $"PUBLIC_CALIBRATION_RESULT = {CurrentValidationState} context={context} " +
            $"physicalSamples={physicalValidationSampleCount} coverageComplete={physicalValidationCoverageComplete} " +
            $"boardVersion={physicalValidationBoardVersion}");
    }

    private void ResetValidationForChangedBoardVersion()
    {
        if (!HasProvisionalPublicCalibration || HasMeasuredCalibration ||
            physicalValidationBoardVersion == ReferenceBoardDefinition.Version)
            return;

        physicalValidationSampleCount = 0;
        physicalValidationCoverageComplete = false;
        physicalValidationBoardVersion = ReferenceBoardDefinition.Version;
        provisionalValidationState = ValidationState.UNVALIDATED;
        Debug.Log($"DJI_PROVISIONAL_VALIDATION_RESET reason=REFERENCE_BOARD_VERSION_CHANGED boardVersion={physicalValidationBoardVersion}");
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
               IsSupportedDetectorFrameFormat(calibration.detectorFrameFormat) &&
               !string.IsNullOrWhiteSpace(calibration.crop) &&
               !string.IsNullOrWhiteSpace(calibration.resize) &&
               calibration.rotationDegrees == 0 &&
               !calibration.mirrorX;
    }

    private static bool IsSupportedDetectorFrameFormat(string format) =>
        format == "NV21_LUMA8" || format == "YUV_420_888_LUMA8";
}
