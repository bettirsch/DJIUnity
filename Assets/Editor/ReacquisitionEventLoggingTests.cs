using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class ReacquisitionEventLoggingTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private GameObject _root;
    private DjiTelemetryValidationLogger _logger;
    private StringWriter _sink;
    private BoundedDiagnosticJsonlWriter _writer;
    [SetUp] public void SetUp()
    {
        _root = new GameObject("Evidence logger test"); _root.SetActive(false);
        _logger = _root.AddComponent<DjiTelemetryValidationLogger>();
        typeof(DjiTelemetryValidationLogger).GetField("_telemetrySessionId",Private).SetValue(_logger,"event-test");
        _sink = new StringWriter();
        _writer = new BoundedDiagnosticJsonlWriter(() => _sink, 256, false);
        typeof(DjiTelemetryValidationLogger).GetField("_normalWriter", Private).SetValue(_logger, _writer);
    }
    [TearDown] public void TearDown()
    {
        typeof(DjiTelemetryValidationLogger).GetField("_normalWriter", Private).SetValue(_logger, null);
        typeof(DjiTelemetryValidationLogger).GetField("_telemetrySessionId", Private).SetValue(_logger, null);
        _writer.Dispose(); Object.DestroyImmediate(_root);
    }
    private void Evidence(long ns, string state) => _logger.RecordReacquisitionEvidence(ns,state,"test",100,1,4,1,1,100,100,
        Quaternion.identity,Quaternion.identity,0,.5f,90,110,110);
    [Test] public void EvidenceRateLimitRetainsAcceptanceAndContainsNoLocationData()
    {
        Evidence(1_000_000_000,"WAITING_FOR_BRACKET");
        for (int i=0;i<100;i++) Evidence(1_010_000_000+i,"WAITING_FOR_BRACKET");
        Evidence(1_020_000_000,"ACCEPTED");
        _writer.Dispose(); var text=_sink.ToString();
        Assert.That(text.Split('\n').Count(s=>s.Contains("ReacquisitionEvidence")),Is.EqualTo(2));
        Assert.That(text,Does.Contain("\"waitChecksSinceEvent\":100"));
        Assert.That(text,Does.Not.Contain("latitude").And.Not.Contain("longitude").And.Not.Contain("position"));
        Assert.That(_writer.Dropped,Is.Zero);
    }
    [Test] public void AppliedOrientationLogsTransitionsAndRateLimitedSignificantChanges()
    {
        void Record(long ns,DjiRenderPoseSource source,float angle) => _logger.RecordAppliedOrientation(ns,source,100,
            Quaternion.identity,Quaternion.Euler(0,angle,0),Quaternion.identity,ns-10,ns-20,ns-30);
        Record(1_000_000_000,DjiRenderPoseSource.Visual,0);
        Record(1_010_000_000,DjiRenderPoseSource.TelemetryPropagated,0);
        Record(1_020_000_000,DjiRenderPoseSource.TelemetryPropagated,20);
        Record(2_020_000_000,DjiRenderPoseSource.TelemetryPropagated,20);
        Record(4_020_000_000,DjiRenderPoseSource.TelemetryPropagated,21);
        _writer.Dispose(); var text=_sink.ToString();
        Assert.That(text.Split('\n').Count(s=>s.Contains("AppliedOrientationEvent")),Is.EqualTo(3));
        Assert.That(text,Does.Contain("aircraftAgeMs").And.Contain("gimbalAgeMs").And.Contain("appliedCameraFromBoardRotation"));
    }
    [Test] public void NoWriterIsHarmlessAndSessionResetRestoresFirstEvent()
    {
        Evidence(10_000_000_000,"WAITING_FOR_BRACKET");
        typeof(DjiTelemetryValidationLogger).GetMethod("ResetReacquisitionEventLogging",Private).Invoke(_logger,null);
        Evidence(10_010_000_000,"WAITING_FOR_BRACKET");
        typeof(DjiTelemetryValidationLogger).GetField("_normalWriter",Private).SetValue(_logger,null);
        Assert.DoesNotThrow(()=>Evidence(10_020_000_000,"ACCEPTED"));
        _writer.Dispose(); Assert.That(_sink.ToString().Split('\n').Count(s=>s.Contains("ReacquisitionEvidence")),Is.EqualTo(2));
    }
}
