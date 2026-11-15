using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;

public sealed class DjiDetectorCaptureDiagnosticsTests
{
    [Serializable] private sealed class CaptureIdentity
    {
        public string telemetrySessionId;
        public long listenerGeneration;
        public long frameSequence;
        public long callbackTimestampNs;
        public long detectorStartTimestampNs;
        public long detectorEndTimestampNs;
        public int width;
        public int height;
        public int rowStride;
    }

    [Test]
    public void EditorConfigurationIsNoOpAndCannotMutateWorldState()
    {
        var state = new WorldLocalizationState();
        Assert.That(state.InitializeFromVisual(new Pose(new Vector3(1, 2, 3), Quaternion.Euler(10, 20, 30)), 42, out _), Is.True);
        var before = state.Snapshot;
        DjiDetectorCaptureDiagnostics.Configure("test-session", true);
        DjiDetectorCaptureDiagnostics.Configure("test-session", false);
        var after = state.Snapshot;
        Assert.That(after.worldFromCamera, Is.EqualTo(before.worldFromCamera));
        Assert.That(after.worldFromBoard, Is.EqualTo(before.worldFromBoard));
        Assert.That(after.phase, Is.EqualTo(before.phase));
        Assert.That(after.lastVisualSourceTimestampNs, Is.EqualTo(42));
    }

    [Test]
    public void BuildOptInIsNotAPersistentProjectDefine()
    {
        var settings = File.ReadAllText(Path.Combine(Application.dataPath, "../ProjectSettings/ProjectSettings.asset"));
        Assert.That(settings.Contains("DJI_EXACT_DETECTOR_CAPTURE"), Is.False);
        var source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts/DjiDetectorCaptureDiagnostics.cs"));
        Assert.That(source.Contains("#if DJI_EXACT_DETECTOR_CAPTURE && UNITY_ANDROID && !UNITY_EDITOR"), Is.True);
    }

    [Test]
    public void AndroidWriterFixtureHasExactYBytesAndMatchingIdentity()
    {
        var path = Environment.GetEnvironmentVariable("DJI_CAPTURE_TEST_OUTPUT_DIR");
        // The integration fixture is emitted by the executed JVM suite; other machines still test the byte contract.
        var header = Encoding.ASCII.GetBytes("P5\n2 2\n255\n");
        var expected = new byte[] { 0, 10, 255, 92 };
        byte[] bytes = string.IsNullOrEmpty(path) ? header.Concat(expected).ToArray() : File.ReadAllBytes(Path.Combine(path, "jvm-exact-y.pgm"));
        Assert.That(bytes.Take(header.Length).ToArray(), Is.EqualTo(header));
        Assert.That(bytes.Skip(header.Length).ToArray(), Is.EqualTo(expected));
        Assert.That(bytes.Length, Is.EqualTo(header.Length + 4));
        if (!string.IsNullOrEmpty(path))
        {
            var record = JsonUtility.FromJson<CaptureIdentity>(File.ReadAllText(Path.Combine(path, "jvm-exact-y.jsonl")));
            Assert.That(record.telemetrySessionId, Is.EqualTo("test-session"));
            Assert.That(record.listenerGeneration, Is.EqualTo(7));
            Assert.That(record.frameSequence, Is.EqualTo(1));
            Assert.That(record.callbackTimestampNs, Is.EqualTo(100));
            Assert.That(record.detectorStartTimestampNs, Is.EqualTo(101));
            Assert.That(record.detectorEndTimestampNs, Is.EqualTo(102));
            Assert.That(record.width * record.height, Is.EqualTo(4));
            Assert.That(record.rowStride, Is.EqualTo(2));
        }
    }
}
