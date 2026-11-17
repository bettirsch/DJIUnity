using System;

/// <summary>Fixed approximate intrinsics for the exact FC3582 detector pixels used by RoofCanopy.</summary>
public static class DjiFc3582CameraModel
{
    public const int ImageWidth = 1920;
    public const int ImageHeight = 1080;
    public const float Fx = 1332.70f;
    public const float Fy = 1332.70f;
    public const float Cx = 960f;
    public const float Cy = 540f;
    public const string DetectorFrameFormat = "NV21_LUMA8";
    public const int RotationDegrees = 0;
    public const string Crop = "NONE";
    public const string Resize = "NONE";
    public const bool MirrorX = false;

    // Brown-Conrady k1, k2, p1, p2, k3; the existing model assumes zero distortion.
    public static float[] CreateDistortionCoefficients() => new float[5];

    public static bool TryValidateRuntimeFrameContract(
        int width, int height, string format, int rotationDegrees, string crop, string resize, bool mirrorX,
        out string mismatch)
    {
        if (width != ImageWidth)
            mismatch = $"WIDTH expected={ImageWidth} actual={width}";
        else if (height != ImageHeight)
            mismatch = $"HEIGHT expected={ImageHeight} actual={height}";
        else if (!string.Equals(DetectorFrameFormat, format, StringComparison.Ordinal))
            mismatch = $"FORMAT expected={DetectorFrameFormat} actual={format}";
        else if (rotationDegrees != RotationDegrees)
            mismatch = $"ROTATION expected={RotationDegrees} actual={rotationDegrees}";
        else if (!string.Equals(Crop, crop, StringComparison.Ordinal))
            mismatch = $"CROP expected={Crop} actual={crop}";
        else if (!string.Equals(Resize, resize, StringComparison.Ordinal))
            mismatch = $"RESIZE expected={Resize} actual={resize}";
        else if (mirrorX != MirrorX)
            mismatch = $"MIRROR expected={MirrorX} actual={mirrorX}";
        else
        {
            mismatch = string.Empty;
            return true;
        }
        return false;
    }
}
