public static class AprilTagScanSession
{
    public static bool HasConfirmedMarker { get; private set; }
    public static int TargetTagId { get; private set; }

    public static void Confirm(int tagId)
    {
        TargetTagId = tagId;
        HasConfirmedMarker = true;
    }

    public static void Clear()
    {
        TargetTagId = 0;
        HasConfirmedMarker = false;
    }
}
