using UnityEngine;

/// <summary>Isolated geometry configuration; never changes the normal shadow settings asset.</summary>
public sealed class RoofCanopyWallShadowExperiment : ScriptableObject
{
    public bool geometryVerified;
    public string sourceFbxSha256;
    public float wallOffset;
    public float wallCenterX = 2.4f;
    public float wallWidth = 7f;
    public float wallHeight = 3.2f;

    public static bool Enabled
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return true;
#else
            return false;
#endif
        }
    }

    public static bool TryCreateSettings(RoofCanopyShadowSettings source,
        RoofCanopyWallShadowExperiment geometry, bool requested, out RoofCanopyShadowSettings result)
    {
        result = null;
        if (!requested || source == null || source.shadowMaterial == null || geometry == null || !geometry.geometryVerified ||
            string.IsNullOrEmpty(geometry.sourceFbxSha256) || geometry.sourceFbxSha256 != source.sourceFbxSha256 ||
            !float.IsFinite(geometry.wallOffset) || !float.IsFinite(geometry.wallCenterX) ||
            !float.IsFinite(geometry.wallWidth) || !float.IsFinite(geometry.wallHeight) ||
            geometry.wallWidth <= .01f || geometry.wallHeight <= .01f) return false;
        result = Instantiate(source);
        result.wallOffset = geometry.wallOffset;
        result.wallCenterX = geometry.wallCenterX;
        result.wallWidth = geometry.wallWidth;
        result.wallHeight = geometry.wallHeight;
        result.wallYawDegrees = 0;
        result.wallEnabled = result.wallCalibrated = result.useFirstSurfaceProjection = true;
        return true;
    }
}
