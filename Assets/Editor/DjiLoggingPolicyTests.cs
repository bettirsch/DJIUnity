using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class DjiLoggingPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private string root;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "dji-logging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, true);

    [Test]
    public void AgeLimitRemovesEntireClosedSessionButNotExactlyFourteenDaysOld()
    {
        var position = Add("dji-position-callbacks", -15);
        var visual = Add("dji-visual-latency", -15);
        var velocity = Add("dji-velocity-callbacks", -15);
        var telemetry = Add("dji-telemetry-validation", -15);
        var outage = Add("dji-outage-events", -15);
        var limit = Add("dji-session-events", -14);
        DjiLogRetention.Prune(root, Now, _ => false);
        foreach (var path in new[] { position, visual, velocity, telemetry, outage }) Assert.That(File.Exists(path), Is.False, path);
        Assert.That(File.Exists(limit), Is.True);
    }

    [Test]
    public void BudgetRemovesTheOldestClosedGroupAndCountsActiveBytes()
    {
        Assert.That(DjiLogRetention.BudgetBytes, Is.EqualTo(100_000_000L));
        var active = Add("dji-session-events", -20, 90);
        var oldest = Add("dji-position-callbacks", -3, 15);
        var sibling = Add("dji-visual-latency", -3, 1);
        var newest = Add("dji-outage-events", -1, 10);
        DjiLogRetention.Prune(root, Now, id => id == Id(-20), budgetBytes: 100L);
        Assert.That(File.Exists(active), Is.True);
        Assert.That(File.Exists(oldest), Is.False);
        Assert.That(File.Exists(sibling), Is.False);
        Assert.That(File.Exists(newest), Is.True);
    }

    [Test]
    public void ActiveGroupOverBudgetIsNeverDeletedAndOtherDataIsUntouched()
    {
        var active = Add("dji-visual-latency", -21, 110);
        var activeSibling = Add("dji-outage-events", -21);
        var ended = Add("dji-session-events", -2);
        var unknown = Add("dji-settings-export", -40);
        var incorrect = Path.Combine(root, "dji-visual-latency-INVALID.jsonl");
        File.WriteAllText(incorrect, "user data");
        var archive = Directory.CreateDirectory(Path.Combine(root, "archive"));
        var archiveFile = Path.Combine(archive.FullName, Path.GetFileName(ended));
        File.WriteAllText(archiveFile, "saved flight");
        DjiLogRetention.Prune(root, Now, id => id == Id(-21), budgetBytes: 100L);
        Assert.That(File.Exists(ended), Is.False);
        foreach (var path in new[] { active, activeSibling, unknown, incorrect, archiveFile })
            Assert.That(File.Exists(path), Is.True, path);
    }

    [Test]
    public void DefaultSessionIsNormalAndModeChangeAppliesOnlyAtNextSession()
    {
        var owner = new GameObject("logging mode test");
        var logger = owner.AddComponent<DjiTelemetryValidationLogger>();
        try
        {
            Begin(logger);
            var normal = logger.TelemetrySessionId;
            Assert.That(logger.IsDetailedLoggingEnabled, Is.False);
            logger.WriteOrientationDiagnostic(Sample());
            logger.RecordMotionRenderSourceTransition(1, DjiRenderPoseSource.None, DjiRenderPoseSource.Visual);
            logger.RecordMotionRenderSourceTransition(2, DjiRenderPoseSource.Visual, DjiRenderPoseSource.TelemetryPropagated);
            logger.RecordMotionRenderSourceTransition(3, DjiRenderPoseSource.TelemetryPropagated, DjiRenderPoseSource.Visual);
            Invoke(logger, "OnUnityLog", "simulated problem", null, LogType.Error);
            Invoke(logger, "SampleNormalTelemetry", 4L);
            Invoke(logger, "SampleNormalTelemetry", 5L);
            logger.ConfigureNextSessionLogging(DjiLoggingMode.Diagnostic);
            Assert.That(logger.IsDetailedLoggingEnabled, Is.False);
            End(logger);
            var normalFile = Path.Combine(root, "dji-session-events-" + normal + ".jsonl");
            var normalLines = File.ReadAllLines(normalFile);
            TestContext.WriteLine($"normal-example records={normalLines.Length} bytes={new FileInfo(normalFile).Length}");
            Assert.That(normalLines.Length, Is.EqualTo(7));
            Assert.That(normalLines[1], Does.Contain("VisualAcquired"));
            Assert.That(normalLines[2], Does.Contain("VisualTrackingLost"));
            Assert.That(normalLines[3], Does.Contain("VisualReacquired"));
            Assert.That(normalLines[4], Does.Contain("UnityError"));
            Assert.That(Directory.GetFiles(root).Length, Is.EqualTo(1));

            Begin(logger);
            var diagnostic = logger.TelemetrySessionId;
            Assert.That(diagnostic, Is.Not.EqualTo(normal));
            Assert.That(logger.IsDetailedLoggingEnabled, Is.True);
            logger.WriteOrientationDiagnostic(Sample());
            logger.ConfigureNextSessionLogging(DjiLoggingMode.Normal);
            End(logger);
            var visual = File.ReadAllText(Path.Combine(root, "dji-visual-latency-" + diagnostic + ".jsonl"));
            Assert.That(visual, Does.Contain("\"sessionId\":\"" + diagnostic + "\""));
            Assert.That(visual, Does.Contain("\"recordType\":\"VisualOrientationCandidate\""));
            Assert.That(visual, Does.Contain("\"recordType\":\"OrientationWriterHealth\""));
            Assert.That(Directory.GetFiles(root, "*" + diagnostic + ".jsonl").Length, Is.EqualTo(3));
            Begin(logger);
            Assert.That(logger.IsDetailedLoggingEnabled, Is.False);
            End(logger);
        }
        finally { Object.DestroyImmediate(owner); }
    }

    [Test]
    public void BusyFileSinkDoesNotBlockProducerOrCloseRequest()
    {
        using var sink = new BlockedSink();
        using var writer = new BoundedDiagnosticJsonlWriter(sink, capacity: 2);
        try
        {
            writer.WriteLine("in flight");
            Assert.That(sink.Entered.Wait(3000), Is.True);
            var producer = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 100; i++) writer.WriteLine("queued");
                writer.RequestClose();
            });
            Assert.That(producer.Wait(3000), Is.True);
            Assert.That(writer.Dropped, Is.EqualTo(98L));
        }
        finally { sink.Release.Set(); writer.Dispose(); }
    }

    [Test]
    public void MeasureRepresentativeVisualLoggingProducer()
    {
        const int iterations = 2000;
        var owner = new GameObject("logging benchmark");
        var logger = owner.AddComponent<DjiTelemetryValidationLogger>();
        var field = typeof(DjiTelemetryValidationLogger).GetField("_visualLatencyWriter", Private);
        var session = typeof(DjiTelemetryValidationLogger).GetField("_telemetrySessionId", Private);
        var sample = Sample();
        using var output = new StringWriter();
        try
        {
            session.SetValue(logger, Id(0));
            field.SetValue(logger, TextWriter.Null);
            for (var i = 0; i < 100; ++i) logger.WriteOrientationDiagnostic(sample);
            var allocationControl = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(new byte[8192]);
            bool allocationCounterAvailable = GC.GetAllocatedBytesForCurrentThread() > allocationControl;
            field.SetValue(logger, null);
            var normal = Measure();
            field.SetValue(logger, output);
            var diagnostic = Measure();
            var bytes = Encoding.UTF8.GetByteCount(output.ToString());
            TestContext.WriteLine(FormattableString.Invariant(
                $"visual-producer n={iterations} normalMs={normal.ms:F4} normalAllocatedBytes={Allocation(normal.bytes)} diagnosticMs={diagnostic.ms:F4} diagnosticAllocatedBytes={Allocation(diagnostic.bytes)} diagnosticUtf8Bytes={bytes} diagnosticBytesPerMinuteAt30Hz={bytes * 1800L / iterations}"));
            Assert.That(bytes, Is.GreaterThan(0));

            string Allocation(long count) => allocationCounterAvailable ? count.ToString(CultureInfo.InvariantCulture) : "UNAVAILABLE";
        }
        finally
        {
            field.SetValue(logger, null);
            Object.DestroyImmediate(owner);
        }

        (double ms, long bytes) Measure()
        {
            var start = Stopwatch.GetTimestamp();
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; ++i) logger.WriteOrientationDiagnostic(sample);
            return ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency,
                GC.GetAllocatedBytesForCurrentThread() - bytes);
        }
    }

    private sealed class BlockedSink : StringWriter
    {
        public readonly ManualResetEventSlim Entered = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public override void WriteLine(string value) { Entered.Set(); Release.Wait(); base.WriteLine(value); }
    }

    private void Begin(DjiTelemetryValidationLogger logger) => Invoke(logger, "StartSessionIn", root);

    private static void End(DjiTelemetryValidationLogger logger)
    {
        var id = logger.TelemetrySessionId;
        var maintenance = typeof(DjiLogRetention).Assembly.GetType("DjiLogMaintenance");
        var active = maintenance.GetMethod("IsActive", BindingFlags.Static | BindingFlags.NonPublic);
        Invoke(logger, "StopSession");
        Assert.That(SpinWait.SpinUntil(() => !(bool)active.Invoke(null, new object[] { id }), 3000), Is.True);
    }

    private static void Invoke(DjiTelemetryValidationLogger logger, string method, params object[] args) =>
        typeof(DjiTelemetryValidationLogger).GetMethod(method, Private).Invoke(logger, args);

    private static OrientationDiagnosticRecord Sample() => new()
    {
        recordType = "VisualOrientationCandidate", candidateId = 123, poseValid = true,
        candidate = Quaternion.identity, outcome = "PROMOTED_HIGH_CONFIDENCE"
    };

    private static string Id(int days) => Now.AddDays(days).ToString(DjiLogRetention.SessionFormat, CultureInfo.InvariantCulture);

    private string Add(string prefix, int days, int bytes = 5)
    {
        var path = Path.Combine(root, $"{prefix}-{Id(days)}.jsonl");
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }
}
