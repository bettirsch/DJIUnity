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
        public int imageWidth = 1920;
        public int imageHeight = 1080;
        public float fx;
        public float fy;
        public float cx;
        public float cy;
        public float[] distortionCoefficients = Array.Empty<float>();
        public int rotationDegrees;
        public bool mirrorX;
        public bool provisional = true;
        public string source = "UNCONFIGURED";
    }

    [SerializeField] private Data data = new();

    public Data Current => data;
    public bool HasUsableCalibration =>
        data != null &&
        data.imageWidth > 0 &&
        data.imageHeight > 0 &&
        data.fx > 0f &&
        data.fy > 0f &&
        float.IsFinite(data.cx) &&
        float.IsFinite(data.cy) &&
        data.rotationDegrees == 0 &&
        !data.mirrorX;

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
            $"rotationDegrees={data.rotationDegrees} mirrorX={data.mirrorX} provisional={data.provisional} usable={HasUsableCalibration}");
        if (!HasUsableCalibration)
            Debug.LogWarning("DJI_CAMERA_CALIBRATION_UNUSABLE reason=EXACT_CPU_FRAME_INTRINSICS_REQUIRED");
    }
}
