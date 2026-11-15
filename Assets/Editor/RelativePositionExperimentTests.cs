using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class RelativePositionExperimentTests
{
    private const string Header = "\"recordType\":\"RelativePositionExperiment\",\"telemetrySessionId\":\"s1\",\"appliedToLocalization\":false";

    [Test]
    public void BatchPreservesCallbackTimestampsValuesAndSdkErrors()
    {
        string line = "{" + Header + ",\"event\":\"GetResult\",\"sourceTimestampNs\":4793398671554241,\"xyz\":{\"x\":1,\"y\":-2,\"z\":null},\"errorCode\":\"REQUEST_HANDLER_NOT_FOUND\"}";
        var writer = new StringWriter();
        Assert.That(DjiTelemetryValidationLogger.WriteRelativePositionExperimentBatch(writer, "s1", line), Is.EqualTo(1));
        Assert.That(writer.ToString().Trim(), Is.EqualTo(line));
    }

    [Test]
    public void TwoCallbacksProduceTwoRecords()
    {
        string line = "{" + Header + ",\"event\":\"ListenerCallback\"}";
        var writer = new StringWriter();
        Assert.That(DjiTelemetryValidationLogger.WriteRelativePositionExperimentBatch(writer, "s1", line + "\n" + line), Is.EqualTo(2));
        Assert.That(DjiTelemetryValidationLogger.WriteRelativePositionExperimentBatch(writer, "s1", ""), Is.Zero);
    }

    [TestCase("s2", false), TestCase("s1", true)]
    public void InvalidSessionOrLocalizationFlagRejected(string id, bool applied)
    {
        string line = "{" + Header.Replace("s1", id).Replace("false", applied ? "true" : "false") + "}";
        Assert.Throws<InvalidDataException>(() =>
            DjiTelemetryValidationLogger.WriteRelativePositionExperimentBatch(new StringWriter(), "s1", line));
    }

    [Test]
    public void LoggingDoesNotMutatePersistentWorld()
    {
        var state = new WorldLocalizationState();
        Assert.That(state.InitializeFromVisual(new Pose(new Vector3(1, 2, 3), Quaternion.Euler(5, 10, 15)), 100, out _), Is.True);
        var before = state.Snapshot;
        DjiTelemetryValidationLogger.WriteRelativePositionExperimentBatch(new StringWriter(), "s1",
            "{" + Header + ",\"xyz\":{\"x\":999,\"y\":999,\"z\":999}} ");
        Assert.That(state.Snapshot.worldFromCamera, Is.EqualTo(before.worldFromCamera));
        Assert.That(state.Snapshot.worldFromBoard, Is.EqualTo(before.worldFromBoard));
        Assert.That(state.Snapshot.phase, Is.EqualTo(before.phase));
    }

    public static void BuildDiagnosticApk()
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-outputPath");
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing -outputPath");
        string output = Path.GetFullPath(args[index + 1]);
        if (File.Exists(output)) throw new IOException("Refusing to overwrite an existing APK.");
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android || EditorUserBuildSettings.buildAppBundle ||
            PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) != ScriptingImplementation.IL2CPP ||
            PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64 ||
            (int)PlayerSettings.Android.minSdkVersion != 24 || (int)PlayerSettings.Android.targetSdkVersion != 36 ||
            !PlayerSettings.GetGraphicsAPIs(BuildTarget.Android).SequenceEqual(new[] { GraphicsDeviceType.OpenGLES3 }))
            throw new InvalidOperationException("Expected existing Android production configuration.");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
            locationPathName = output, target = BuildTarget.Android, options = BuildOptions.None,
            // Preserve the last validated physical APK's RawTarget profile.
            extraScriptingDefines = new[] { "DJI_VERTICAL_RAW_EXPERIMENT", "DJI_RELATIVE_POSITION_EXPERIMENT" }
        });
        if (report.summary.result != BuildResult.Succeeded) throw new Exception("Relative-position diagnostic build failed: " + report.summary.result);
        Debug.Log("RELATIVE_POSITION_DIAGNOSTIC_APK_BUILD_SUCCESS path=" + output);
    }
}
