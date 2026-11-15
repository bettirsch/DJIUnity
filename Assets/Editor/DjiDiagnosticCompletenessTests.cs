using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class DjiDiagnosticCompletenessTests
{
    private GameObject _object;
    private DjiTelemetryValidationLogger _logger;
    private StringWriter _output;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [SetUp]
    public void SetUp()
    {
        _object = new GameObject("Diagnostic identity test");
        _logger = _object.AddComponent<DjiTelemetryValidationLogger>();
        _output = new StringWriter();
        Set("_visualLatencyWriter", _output);
    }

    [TearDown]
    public void TearDown()
    {
        Set("_visualLatencyWriter", null);
        Object.DestroyImmediate(_object);
        _output.Dispose();
    }

    [Test]
    public void SequenceRestart_WithNewerCallbackTimestamp_WritesBothMeasurements()
    {
        Write(453, 1000); Write(22, 2000);
        Assert.That(Lines.Length, Is.EqualTo(2));
        StringAssert.Contains("\"cpuDetectorFrameSequence\":22", Lines[1]);
    }

    [Test]
    public void DuplicateDelivery_WithLaterApplicationTime_IsSuppressed()
    {
        Write(1, 1000); Write(1, 1000, applied: 5000);
        Assert.That(Lines.Length, Is.EqualTo(1));
    }

    [Test]
    public void OutOfOrderDelivery_CannotRegressDiagnosticHighWaterMark()
    {
        Write(10, 2000); Write(999, 1000); Write(1, 3000);
        Assert.That(Lines.Length, Is.EqualTo(2));
        StringAssert.Contains("\"sourceFrameTimestampNs\":3000", Lines[1]);
    }

    [Test]
    public void TimestampCollision_DistinctIncreasingSequencesAreNotDuplicates()
    {
        Write(10, 1000); Write(11, 1000); Write(11, 1000); Write(9, 1000);
        Assert.That(Lines.Length, Is.EqualTo(2));
        StringAssert.Contains("ANDROID_CALLBACK_TIMESTAMP_AND_CPU_DETECTOR_SEQUENCE", Lines[1]);
    }

    [Test]
    public void MultipleDetectorGenerations_CanReuseSequenceNumbers()
    {
        Write(1, 1000); Write(500, 2000); Write(1, 3000); Write(1, 4000);
        Assert.That(Lines.Length, Is.EqualTo(4));
    }

    [TestCase(DjiBoardVisionProvider.VisualPoseQuality.Degraded)]
    [TestCase(DjiBoardVisionProvider.VisualPoseQuality.Unavailable)]
    public void RejectedOrPendingQuality_DoesNotWriteOrConsumeIdentity(DjiBoardVisionProvider.VisualPoseQuality quality)
    {
        Write(1, 1000, quality); Assert.That(Lines, Is.Empty);
        Write(1, 1000); Assert.That(Lines.Length, Is.EqualTo(1));
    }

    [TestCase(0, 1000, 2000)]
    [TestCase(1, 0, 2000)]
    [TestCase(1, 1000, 0)]
    public void InvalidIdentityOrApplication_DoesNotAdvanceDeduplication(long sequence, long time, long application)
    {
        Write(sequence, time, applied: application); Assert.That(Lines, Is.Empty);
        Write(1, 1000); Assert.That(Lines.Length, Is.EqualTo(1));
    }

    [Test]
    public void DisabledWriter_DoesNotConsumeMeasurementIdentity()
    {
        Set("_visualLatencyWriter", null); Write(1, 1000);
        Set("_visualLatencyWriter", _output); Write(1, 1000);
        Assert.That(Lines.Length, Is.EqualTo(1));
    }

    [Test]
    public void NewLoggerSession_DoesNotShareDiagnosticIdentityState()
    {
        Write(1, 1000);
        Set("_visualLatencyWriter", null); Object.DestroyImmediate(_object);
        _object = new GameObject("New diagnostic session");
        _logger = _object.AddComponent<DjiTelemetryValidationLogger>();
        Set("_visualLatencyWriter", _output); Write(1, 1000);
        Assert.That(Lines.Length, Is.EqualTo(2));
    }

    [TestCase(DjiRenderPoseSource.TelemetryPropagated, true)]
    [TestCase(DjiRenderPoseSource.TelemetryPropagated, false)]
    public void VerticalLabel_IdentifiesAltitudeEvenWhenItsContributionIsHeld(DjiRenderPoseSource source, bool fresh)
    {
        var estimate = new VerticalAltitudeSmoothingEstimate(
            fresh ? VerticalAltitudeLocalizationState.Active : VerticalAltitudeLocalizationState.DegradedStale,
            "", 2000, 1000, -1, -0.5f, 0, 0, 0.4f, fresh);
        var snapshot = new VerticalAltitudeProductionSnapshot(true, fresh, 100, 101, 500, 1000,
            fresh ? 100 : 400, 1, -1, -0.5f, Vector3.down * 0.5f, estimate);
        Assert.That(DjiTelemetryValidationLogger.ResolveVerticalDiagnosticSource(source, snapshot), Is.EqualTo("Location3DAltitude"));
    }

    [TestCase(DjiRenderPoseSource.Visual, "AuthoritativeVisual")]
    [TestCase(DjiRenderPoseSource.None, "UNAVAILABLE")]
    [TestCase(DjiRenderPoseSource.TelemetryPropagated, "VisualBaselineHeld")]
    public void VerticalLabel_DistinguishesVisualUnavailableAndNoAltitudeBaseline(DjiRenderPoseSource source, string expected)
    {
        Assert.That(DjiTelemetryValidationLogger.ResolveVerticalDiagnosticSource(source, default), Is.EqualTo(expected));
    }

    [Test]
    public void ActualPeriodicBoardSerialization_ExposesVerticalSourceStateAndFreshness()
    {
        var jsonType = typeof(DjiTelemetryValidationLogger).GetNestedType("JsonLine", BindingFlags.NonPublic);
        var json = Activator.CreateInstance(jsonType, true);
        jsonType.GetMethod("BeginObject").Invoke(json, null);
        typeof(DjiTelemetryValidationLogger).GetMethod("WriteBoardState", Private).Invoke(_logger, new[] { json });
        jsonType.GetMethod("EndObject").Invoke(json, null);
        var text = json.ToString();
        StringAssert.Contains("\"verticalSource\":\"UNAVAILABLE\"", text);
        StringAssert.Contains("\"verticalLocalizationState\":\"BaselineUnavailable\"", text);
        StringAssert.Contains("\"verticalSourceFresh\":false", text);
    }

    private string[] Lines => _output.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
    private void Set(string field, object value) => typeof(DjiTelemetryValidationLogger).GetField(field, Private).SetValue(_logger, value);
    private void Write(long sequence, long time,
        DjiBoardVisionProvider.VisualPoseQuality quality = DjiBoardVisionProvider.VisualPoseQuality.HighConfidence,
        long applied = 10000)
    {
        var native = new DjiBoardVisionProvider.NativeBoardResult
        {
            visionFrameId = sequence, visionFrameTimestampNs = time,
            detectorStartTimestampNs = time + 1, detectorEndTimestampNs = time + 2,
            pnpCompletedTimestampNs = time + 2, markerCount = 4, reprojectionRms = 1
        };
        _logger.WriteVisualLatencySample(new DjiBoardVisionProvider.VisualPoseTimingSample(native, Pose.identity, quality, time + 3),
            applied, default, default, default);
    }
}
