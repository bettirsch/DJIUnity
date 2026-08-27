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

    [DllImport("djiunity")]
    private static extern int DJI_DetectAprilTagPoseRgba32(
        byte[] rgbaBytes,
        int width,
        int height,
        float fx,
        float fy,
        float cx,
        float cy,
        float tagSizeMeters,
        float[] outDetection,
        int outDetectionLength,
        float[] outPose,
        int outPoseLength);

    [DllImport("djiunity")]
    private static extern int DJI_DetectAprilTagPoseCandidatesRgba32(
        byte[] rgbaBytes,
        int width,
        int height,
        float fx,
        float fy,
        float cx,
        float cy,
        float tagSizeMeters,
        float[] outDetection,
        int outDetectionLength,
        float[] outPoseCandidates,
        int outPoseCandidatesLength);

    [DllImport("djiunity")]
    private static extern int DJI_RefineAprilTagPoseCandidate(
        int width,
        int height,
        float fx,
        float fy,
        float cx,
        float cy,
        float tagSizeMeters,
        float[] detection,
        int detectionLength,
        float[] initialPose,
        int initialPoseLength,
        float[] outPose,
        int outPoseLength);

    [DllImport("djiunity")]
    private static extern int DJI_ValidateAprilTagPoseConvention();
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

    public static bool TryDetectPose(
        byte[] rgbaBytes,
        int width,
        int height,
        float fx,
        float fy,
        float cx,
        float cy,
        float tagSizeMeters,
        float[] outDetection,
        float[] outPose)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (rgbaBytes == null || outDetection == null || outPose == null)
            return false;

        return DJI_DetectAprilTagPoseRgba32(
            rgbaBytes,
            width,
            height,
            fx,
            fy,
            cx,
            cy,
            tagSizeMeters,
            outDetection,
            outDetection.Length,
            outPose,
            outPose.Length) != 0;
#else
        return false;
#endif
    }

    public static int TryDetectPoseCandidates(
        byte[] rgbaBytes,
        int width,
        int height,
        float fx,
        float fy,
        float cx,
        float cy,
        float tagSizeMeters,
        float[] outDetection,
        float[] outPoseCandidates)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (rgbaBytes == null || outDetection == null || outPoseCandidates == null)
            return 0;

        return DJI_DetectAprilTagPoseCandidatesRgba32(
            rgbaBytes,
            width,
            height,
            fx,
            fy,
            cx,
            cy,
            tagSizeMeters,
            outDetection,
            outDetection.Length,
            outPoseCandidates,
            outPoseCandidates.Length);
#else
        return 0;
#endif
    }

    public static bool TryRefinePoseCandidate(
        int width,
        int height,
        float fx,
        float fy,
        float cx,
        float cy,
        float tagSizeMeters,
        float[] detection,
        float[] initialPose,
        float[] outPose)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (detection == null || initialPose == null || outPose == null)
            return false;

        return DJI_RefineAprilTagPoseCandidate(
            width,
            height,
            fx,
            fy,
            cx,
            cy,
            tagSizeMeters,
            detection,
            detection.Length,
            initialPose,
            initialPose.Length,
            outPose,
            outPose.Length) != 0;
#else
        return false;
#endif
    }

    public static bool ValidatePoseConvention()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return DJI_ValidateAprilTagPoseConvention() != 0;
#else
        return true;
#endif
    }
}
