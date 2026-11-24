using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using Object = UnityEngine.Object;

public sealed class ValidatedTrackingCandidateTests
{
    [Test]
    public void CandidateBuildUsesExplicitFeaturesWithoutCaptureOrPersistentDefines()
    {
        var method = typeof(CommandLineAndroidBuild).GetMethod("CreateAndroidBuildOptions", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        var alias = typeof(CommandLineAndroidBuild).GetMethod("BuildValidatedTrackingCandidateApk");
        var aliasBody = alias.GetMethodBody().GetILAsByteArray();
        var calls = 0;
        for (var i = 0; i < aliasBody.Length; i++)
        {
            if (aliasBody[i] == 0x00 || aliasBody[i] == 0x2a) continue; // nop / ret
            Assert.That(aliasBody[i], Is.EqualTo(0x28), "The compatibility entry must only call the normal builder.");
            Assert.That(alias.Module.ResolveMethod(BitConverter.ToInt32(aliasBody, i + 1)),
                Is.EqualTo(typeof(CommandLineAndroidBuild).GetMethod("BuildApk")));
            i += 4;
            calls++;
        }
        Assert.That(calls, Is.EqualTo(1));
        // The historical candidate name is now an alias of this normal builder.
        var persistent = PlayerSettings.GetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget.Android);
        var candidate = (BuildPlayerOptions)method.Invoke(null, new object[] { "candidate.apk", BuildOptions.None, false });
        var normal = (BuildPlayerOptions)method.Invoke(null, new object[] { "normal.apk", BuildOptions.None, false });
        var capture = (BuildPlayerOptions)method.Invoke(null, new object[] { "capture.apk", BuildOptions.None, true });
        Assert.That(candidate.extraScriptingDefines, Is.Empty);
        Assert.That(normal.extraScriptingDefines, Is.Empty);
        Assert.That(capture.extraScriptingDefines, Is.EqualTo(new[] { "DJI_EXACT_DETECTOR_CAPTURE" }));
        Assert.That(candidate.scenes, Is.EqualTo(normal.scenes));
        Assert.That(normal.scenes, Is.EqualTo(EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray()));
        Assert.That(candidate.locationPathName, Is.EqualTo("candidate.apk"));
        Assert.That(normal.locationPathName, Is.EqualTo("normal.apk"));
        Assert.That(candidate.options, Is.EqualTo(BuildOptions.None));
        Assert.That(normal.options, Is.EqualTo(candidate.options));
        Assert.That(candidate.target, Is.EqualTo(BuildTarget.Android));
        Assert.That(normal.target, Is.EqualTo(candidate.target));
        Assert.That(PlayerSettings.GetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget.Android), Is.EqualTo(persistent));
        Assert.That(DjiValidatedTrackingCandidate.Enabled, Is.True);
        Assert.That(DjiAprilTagBorderExperiment.Enabled, Is.True);
        Assert.That(DjiAprilTagBorderRetentionExperiment.Enabled, Is.True);
        Assert.That(DjiAprilTagStaleJumpExperiment.Enabled, Is.True);
        Assert.That(DjiAprilTagNoMarkersQualityLossExperiment.Enabled, Is.True);
        Assert.That(RoofCanopyWallShadowExperiment.Enabled, Is.False, "Editor wall rendering stays disabled.");
    }

    [TestCase(100, 100, 0, true)]
    [TestCase(100, 2000000100, 0, true)]
    [TestCase(100, 2000000101, 0, false)]
    [TestCase(100, 99, 0, false)]
    [TestCase(0, 100, 0, false)]
    [TestCase(100, 110, 100, false)]
    [TestCase(99, 110, 100, false)]
    public void PositiveFrameUsesSourceAgeAndCannotRewind(long source, long arrival, long accepted, bool valid) =>
        Assert.That(DjiValidatedTrackingCandidate.IsFreshPositiveSource(source, arrival, accepted), Is.EqualTo(valid));

    [TestCase(0f, true)] [TestCase(5.9f, true)] [TestCase(6.1f, false)]
    [TestCase(45f, false)] [TestCase(90f, false)] [TestCase(180f, false)]
    public void IndependentOrientationCheckDoesNotUsePositionDistance(float difference, bool valid)
    {
        var predicted = new Pose(Vector3.zero, Quaternion.Euler(170, 20, 30));
        var candidate = new Pose(Vector3.one * 100, predicted.rotation * Quaternion.AngleAxis(difference, Vector3.up));
        Assert.That(DjiValidatedTrackingCandidate.IsRotationConsistent(predicted, candidate, 6f, out _), Is.EqualTo(valid));
    }

    [Test]
    public void LargeActualCameraRotationCanPassWhenIndependentTelemetryAgrees()
    {
        var baseline = new Pose(new Vector3(0, 0, 2), Quaternion.Euler(170, 5, 2));
        var nav = Quaternion.AngleAxis(90, Vector3.up);
        var predicted = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(baseline, Quaternion.identity, nav);
        Assert.That(DjiValidatedTrackingCandidate.IsRotationConsistent(predicted, predicted, 6, out _), Is.True);
        Assert.That(Quaternion.Angle(predicted.rotation, baseline.rotation), Is.EqualTo(90).Within(.01f));
        Assert.That(DjiValidatedTrackingCandidate.IsRotationConsistent(predicted,
            new Pose(Vector3.zero, new Quaternion(0, 0, 0, 0)), 6, out _), Is.False);
    }

    [Test]
    public void ModelDerivedWallAndGroundShareCornerReferenceAndUnchangedRay()
    {
        var source = Resources.Load<RoofCanopyShadowSettings>("RoofCanopyShadowSettings");
        var geometry = Resources.Load<RoofCanopyWallShadowExperiment>("RoofCanopyWallShadowExperiment");
        Assert.That(RoofCanopyWallShadowExperiment.TryCreateSettings(source, geometry, true, out var settings), Is.True);
        try
        {
            Assert.That(settings.wallOffset, Is.EqualTo(-.0036760657f).Within(1e-7f));
            Assert.That(settings.wallEnabled && settings.wallCalibrated && settings.useFirstSurfaceProjection, Is.True);
            Assert.That(settings.sunlightRayDirection, Is.EqualTo(source.sunlightRayDirection));
            var reference = ReferenceBoardContentDefinition.BoardFromRoofCanopyReference;
            Assert.That(reference.position.y + settings.wallOffset, Is.EqualTo(.1763239343f).Within(1e-7f));
            foreach (var point in new[] { new Vector3(1, -.2f, 2), new Vector3(1, -1, .1f) })
            {
                Assert.That(ShadowProjectionMath.TryFirstReceiver(point, settings.sunlightRayDirection,
                    settings.Ground, settings.Wall, out var wallFirst, out var hit), Is.True);
                Assert.That(wallFirst, Is.EqualTo(point.z > 1));
                var boardHit = reference.position + reference.rotation * hit;
                if (wallFirst) Assert.That(boardHit.y, Is.EqualTo(.1763239343f).Within(1e-6f));
                else Assert.That(boardHit.z, Is.EqualTo(0).Within(1e-6f));
            }
            Assert.That(source.wallEnabled, Is.False, "The placeholder production asset is not mutated.");
        }
        finally { Object.DestroyImmediate(settings); }
    }
}
