using System.Runtime.InteropServices;

internal static class DJIAprilTagNative
{
#if UNITY_ANDROID && !UNITY_EDITOR
    [DllImport("djiunity")]
    private static extern void DJI_SetAprilTagTargetId(int tagId);

    [DllImport("djiunity")]
    private static extern void DJI_ReleaseAprilTagDetector();

    [DllImport("djiunity")]
    private static extern int DJI_DetectAprilTagRgba32(byte[] rgbaBytes, int width, int height, float[] outDetection, int outDetectionLength);
#endif

    public static void SetTargetTagId(int tagId)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        DJI_SetAprilTagTargetId(tagId);
#endif
    }

    public static void ReleaseDetector()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        DJI_ReleaseAprilTagDetector();
#endif
    }

    public static bool TryDetect(byte[] rgbaBytes, int width, int height, float[] outDetection)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (rgbaBytes == null || outDetection == null)
            return false;

        return DJI_DetectAprilTagRgba32(rgbaBytes, width, height, outDetection, outDetection.Length) != 0;
#else
        return false;
#endif
    }
}
