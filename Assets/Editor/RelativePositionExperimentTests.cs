using System.IO;
using NUnit.Framework;
using UnityEngine;

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
        CommandLineAndroidBuild.BuildRelativePositionDiagnosticApk();
    }
}
