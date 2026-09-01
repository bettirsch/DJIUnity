using System;
using UnityEngine;

/// <summary>
/// Calibration for the exact decoded DJI CPU-frame geometry used by board
/// vision. It is intentionally unrelated to ARCore phone intrinsics.
/// </summary>
[DisallowMultipleComponent]
public sealed class DjiCameraCalibration : MonoBehaviour
{
    private const string ResourceName = "DjiCameraCalibration";

    [Serializable]
    public sealed class Data
    {
        public string calibrationVersion = "UNCONFIGURED";
        public int imageWidth = 1920;
        public int imageHeight = 1080;
        public string detectorFrameFormat = "YUV_420_888_LUMA8";
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
        public float calibrationRms;
        public string calibrationDate = "";
        public bool valid;
        public int rotationDegrees;
        public bool mirrorX;
        public bool provisional = true;
        public string source = "UNCONFIGURED";
    }

    [SerializeField] private Data data = new();

    public Data Current => data;
    public float[] DistortionCoefficients => new[] { data.k1, data.k2, data.p1, data.p2, data.k3 };
    public bool HasUsableCalibration =>
        data != null &&
        data.valid &&
        !data.provisional &&
        data.imageWidth > 0 &&
        data.imageHeight > 0 &&
        data.fx > 0f &&
        data.fy > 0f &&
        float.IsFinite(data.cx) &&
        float.IsFinite(data.cy) &&
        float.IsFinite(data.k1) &&
        float.IsFinite(data.k2) &&
        float.IsFinite(data.p1) &&
        float.IsFinite(data.p2) &&
        float.IsFinite(data.k3) &&
        float.IsFinite(data.calibrationRms) &&
        data.calibrationRms > 0f &&
        !string.IsNullOrWhiteSpace(data.calibrationVersion) &&
        data.detectorFrameFormat == "YUV_420_888_LUMA8" &&
        data.rotationDegrees == 0 &&
        !data.mirrorX;

    public bool IsRuntimeFrameCompatible(int width, int height, string format) =>
        HasUsableCalibration &&
        data.imageWidth == width &&
        data.imageHeight == height &&
        data.detectorFrameFormat == format;

    private void Awake()
    {
        var json = Resources.Load<TextAsset>(ResourceName);
        if (json != null && !string.IsNullOrWhiteSpace(json.text))
        {
            var parsed = JsonUtility.FromJson<Data>(json.text);
            if (parsed != null)
                data = parsed;
        }

        Debug.Log(
            $"DJI_CAMERA_CALIBRATION source={data.source} frame={data.imageWidth}x{data.imageHeight} " +
            $"fx={data.fx:F2} fy={data.fy:F2} cx={data.cx:F2} cy={data.cy:F2} " +
            $"distortion=[{data.k1:F6},{data.k2:F6},{data.p1:F6},{data.p2:F6},{data.k3:F6}] rms={data.calibrationRms:F3} " +
            $"version={data.calibrationVersion} rotationDegrees={data.rotationDegrees} mirrorX={data.mirrorX} provisional={data.provisional} valid={data.valid} usable={HasUsableCalibration}");
        if (!HasUsableCalibration)
            Debug.LogWarning("DJI_CAMERA_CALIBRATION_UNUSABLE reason=EXACT_CPU_FRAME_INTRINSICS_REQUIRED");
    }
}
