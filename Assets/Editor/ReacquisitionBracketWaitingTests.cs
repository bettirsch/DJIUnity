using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class ReacquisitionBracketWaitingTests
{
    private const long Ms = 1_000_000;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private GameObject _root;
    private DjiBoardVisionProvider _board;
    private DjiCameraPoseProvider _telemetry;
    private List<DjiCameraPoseProvider.TelemetryOrientationSample> _history;
    private Pose _visual;
    private int _sequence;
    [Serializable] private sealed class Fixture { public DjiBoardVisionProvider.NativeBoardResult result; }
    private DjiBoardVisionProvider.NativeBoardResult _fixture;

    [SetUp] public void SetUp()
    {
        var dir = Environment.GetEnvironmentVariable("DJI_MULTISTART_FIXTURE_DIR");
        Assert.That(dir, Is.Not.Null.And.Not.Empty, "Executed native fixture is required for these integration tests.");
        _fixture = JsonUtility.FromJson<Fixture>(File.ReadAllText(Path.Combine(dir, "selected-native-unity-fixture.json"))).result;
        _root = new GameObject("Asynchronous bracket integration"); _root.SetActive(false);
        _board = _root.AddComponent<DjiBoardVisionProvider>();
        _telemetry = _root.AddComponent<DjiCameraPoseProvider>();
        _history = (List<DjiCameraPoseProvider.TelemetryOrientationSample>)typeof(DjiCameraPoseProvider)
            .GetField("_telemetryOrientationHistory", Private).GetValue(_telemetry);
        typeof(DjiCameraPoseProvider).GetField("<HasTelemetryOrientation>k__BackingField", Private).SetValue(_telemetry, true);
        var args = new object[] { _fixture, Pose.identity };
        Assert.That((bool)Call("TryGetCameraFromBoard", args), Is.True); _visual = (Pose)args[1];
        Set("_telemetryProvider", _telemetry);
        Assert.That(Get("_validatedTrackingSafetyEnabled"), Is.True, "Integration exercises the normal provider default.");
        Set("_shadowPropagationActive", true); Set("_hasLatestShadowPrediction", true);
        Set("_hasShadowRotationPropagationBaseline", true); Set("_propagationStartTimestampNs", 1000 * Ms);
        Set("<CurrentRenderPoseSource>k__BackingField", DjiRenderPoseSource.TelemetryPropagated);
        Set("<CurrentVisualPoseQuality>k__BackingField", DjiBoardVisionProvider.VisualPoseQuality.HighConfidence);
        Set("<HasHighConfidenceCameraFromBoard>k__BackingField", true);
        Set("<CurrentHighConfidenceCameraFromBoard>k__BackingField", _visual);
        Set("_currentHighConfidenceVisualTimestampNs", 1000 * Ms);
        Set("_lastHighConfidenceCameraFromBoardRealtime", Time.realtimeSinceStartup - 10);
        var type = typeof(DjiBoardVisionProvider).GetNestedType("ShadowRotationPropagationBaseline", BindingFlags.NonPublic);
        Set("_shadowRotationPropagationBaseline", Activator.CreateInstance(type, new object[] {
            1000 * Ms, _visual, Quaternion.identity, false, default(TelemetryAircraftPositionSample) }));
    }
    [TearDown] public void TearDown() => Object.DestroyImmediate(_root);

    [Test] public void A_RealProcessResultWaitsForFutureEndpointAndRestoresVisual()
    {
        Add(9900); Process(10000, 10050);
        Assert.That(Get("_evidenceState"), Is.EqualTo("WAITING_FOR_BRACKET")); AssertBaseline();
        Assert.That(Get("_validatedEvidenceCount"), Is.EqualTo(0));
        Arrive(10100, 10120); Assert.That(Get("_validatedEvidenceCount"), Is.EqualTo(1)); AssertBaseline();
        Process(10100, 10150); Process(10200, 10250); AssertBaseline();
        Arrive(10300, 10320); AssertBaseline(); Process(10300, 10350);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(10300 * Ms));
        Assert.That(_board.TryGetRenderCameraFromBoard(2, out var actual, out var source, out _), Is.True);
        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.Visual));
        Assert.That(Quaternion.Angle(actual.rotation, _visual.rotation), Is.LessThan(.01f));
    }

    [Test] public void B_MissingFutureEndpointTimesOutAndLateCallbackCannotPromote()
    {
        Add(9900); Offer(10000, now:10010); Tick(10510);
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.True); AssertBaseline();
        Tick(10511); Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False);
        Arrive(10100, 10520); AssertBaseline();
        Offer(10000, now:10530); Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False);
    }

    [TestCase(45f)] [TestCase(90f)] [TestCase(6.1f)]
    public void C_TrueOrientationDisagreementClearsAllValidatedCredit(float angle)
    {
        Add(9900); Add(10000); Offer(10000, angle:angle);
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False);
        Assert.That(Get("_validatedEvidenceCount"), Is.EqualTo(0)); AssertBaseline();
    }

    [Test] public void D_AlternatingBranchesRestartOnlyTheirValidatedSequence()
    {
        for (int t=9900;t<=10700;t+=100) Add(t);
        foreach (var step in new[] { (10000,0f),(10100,45f),(10200,0f),(10300,90f),(10400,0f),(10500,0f),(10600,0f),(10700,0f) })
        {
            Offer(step.Item1, angle:step.Item2);
            if (step.Item1 < 10700) AssertBaseline();
        }
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(10700 * Ms));
        Assert.That(Get("_propagationStartTimestampNs"), Is.EqualTo(1000 * Ms));
    }

    [Test] public void E_TemporaryMissingEndpointPreservesValidatedPrefix()
    {
        Add(9900); Add(10000); Offer(10000); Offer(10100);
        Assert.That(Get("_validatedEvidenceCount"), Is.EqualTo(1));
        Assert.That(Get("_validatedEvidenceStartNs"), Is.EqualTo(10000 * Ms));
        Arrive(10200, 10210); Assert.That(Get("_validatedEvidenceCount"), Is.EqualTo(2));
        Offer(10200); Offer(10300); AssertBaseline();
        Arrive(10400, 10410);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(10300 * Ms));
    }

    [Test] public void QueuedSourcesRetainOriginalTimesAndCannotBeOverwrittenByNewerFrames()
    {
        Add(9900); Offer(10000); Offer(10100); Offer(10200);
        Assert.That(Get("_validatedEvidenceCount"), Is.EqualTo(0));
        Add(10050);
        Arrive(10250, 10260);
        // Original sources use their own 9900/10050/10250 brackets, not now.
        Assert.That(Get("_validatedEvidenceStartNs"), Is.EqualTo(10000 * Ms));
        Assert.That(Get("_validatedEvidenceLastNs"), Is.EqualTo(10200 * Ms)); AssertBaseline();
    }

    [Test] public void DuplicateCallbacksAndFramesDoNotGrantDwellCredit()
    {
        Add(9900); Add(10000); Offer(10000);
        Arrive(10050, 10060); Call("OnTelemetryOrientationRecordedAtTimestamp", _history[_history.Count-1], 10100 * Ms);
        Offer(10000, now:10110); Tick(10350);
        Assert.That(Get("_validatedEvidenceCount"), Is.EqualTo(1)); AssertBaseline();
    }

    [Test] public void NoMarkersClearsPendingAndValidatedEvidence()
    {
        Add(9900); Add(10000); Process(10000, 10010); Process(10100, 10110);
        var result=Result(10150); result.status=-8; result.markerCount=0;
        Call("ProcessResultWithQualityLossExperiment", result, 10160 * Ms, true);
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False);
        Arrive(10200, 10210); AssertBaseline();
    }

    [Test] public void StalePositiveRemainsBlockedByRealProcessResult()
    {
        Add(9900); Process(10000, 13000);
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False); AssertBaseline();
    }

    [Test] public void OlderSourceOrSequenceDoesNotReplacePendingNewerCandidate()
    {
        Add(9900); Offer(10000, sequence:10, generation:2);
        Offer(10100, sequence:9, generation:2); Offer(9999, sequence:11, generation:2);
        Assert.That(Get("_evidenceLastOfferedNs"), Is.EqualTo(10000 * Ms)); AssertBaseline();
    }

    [Test] public void NewCameraGenerationRestartsDwellAndOldGenerationCannotPromote()
    {
        for(int t=9900;t<=10400;t+=100) Add(t);
        Offer(10000, generation:1, sequence:10); Offer(10100, generation:2, sequence:1);
        Assert.That(Get("_validatedEvidenceStartNs"), Is.EqualTo(10100 * Ms));
        Offer(10200, generation:1, sequence:99); AssertBaseline();
        Offer(10200, generation:2, sequence:2); Offer(10300, generation:2, sequence:3); Offer(10400, generation:2, sequence:4);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(10400 * Ms));
    }

    [TestCase(.5f,true)] [TestCase(10f,true)]
    public void LongOutagePreservesLargeLegitimatePositionCorrection(float displacement, bool accepted)
    {
        for(int t=9900;t<=10300;t+=100) Add(t);
        for(int t=10000;t<=10300;t+=100) Offer(t, distance:displacement);
        Assert.That((long)Get("_currentHighConfidenceVisualTimestampNs")>1000*Ms, Is.EqualTo(accepted));
    }

    [Test] public void ShortOutageStillUsesOneMeterAndSixDegreeBounds()
    {
        Add(9900); Add(10000); Offer(10000,distance:1.1f,age:2f);
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False); AssertBaseline();
    }

    [Test] public void GenuineCameraTurnSupportedByTelemetryIsNotMistakenForWrongBranch()
    {
        var nav=Quaternion.AngleAxis(90,Vector3.up);
        for(int t=9900;t<=10300;t+=100) Add(t,nav);
        var pose=ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(_visual,Quaternion.identity,nav);
        for(int t=10000;t<=10300;t+=100) Offer(t,rotation:pose.rotation);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(10300*Ms));
    }

    [Test] public void TooOldBracketIsNotClassifiedAsRetryable()
    {
        Add(9600); Add(10100); Offer(10000,now:10110);
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False); AssertBaseline();
    }

    [Test] public void EvictedBeforeEndpointCannotWaitForAnImpossibleOlderSample()
    {
        Add(10100); Offer(10000,now:10110);
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False); AssertBaseline();
    }

    [Test] public void CapacityIsBoundedAndNeverDropsAnUnverifiedHeadToGainDwell()
    {
        Set("maximumPendingRotationDiagnosticSamples",2); Add(9900);
        Offer(10000); Offer(10010); Offer(10020);
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False); AssertBaseline();
    }

    [Test] public void SilenceCannotReuseExpiredValidatedEvidence()
    {
        Add(10000); Offer(10000); Tick(10511);
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False); AssertBaseline();
    }

    [Test] public void LargeSourceGapStartsANewValidatedSequence()
    {
        Add(10000); Add(10600); Offer(10000); Offer(10600);
        Assert.That(Get("_validatedEvidenceStartNs"), Is.EqualTo(10600*Ms)); AssertBaseline();
    }

    [Test] public void InvalidTelemetryAndNoneResetCannotLeavePromotableEvidence()
    {
        Add(9900); Offer(10000);
        typeof(DjiCameraPoseProvider).GetField("<HasTelemetryOrientation>k__BackingField",Private).SetValue(_telemetry,false);
        Call("OnTelemetryOrientationRecordedAtTimestamp",new DjiCameraPoseProvider.TelemetryOrientationSample(10100*Ms,Quaternion.identity,false,0,0),10110*Ms);
        Assert.That(Get("_hasVisualReacquisitionCandidate"),Is.False); AssertBaseline();
        Assert.That(_board.TryGetRenderCameraFromBoard(2,out _,out var source,out _),Is.False);
        Assert.That(source,Is.EqualTo(DjiRenderPoseSource.None));
    }

    [TestCase(false)] [TestCase(true)]
    public void LoggingEnabledOrDisabledProducesIdenticalAcceptedPoseAndTimestamp(bool logging)
    {
        var logger = _root.AddComponent<DjiTelemetryValidationLogger>();
        var sink = new StringWriter();
        using var writer = new BoundedDiagnosticJsonlWriter(() => sink,256,false);
        var field = typeof(DjiTelemetryValidationLogger).GetField("_normalWriter",Private);
        try
        {
            if(logging) field.SetValue(logger,writer);
            typeof(DjiTelemetryValidationLogger).GetField("_telemetrySessionId",Private).SetValue(logger,"bracket-test");
            Set("_telemetryValidationLogger",logger);
            A_RealProcessResultWaitsForFutureEndpointAndRestoresVisual();
            Assert.That(_board.CurrentHighConfidenceCameraFromBoard,Is.EqualTo(_visual));
            Assert.That(Get("_propagationStartTimestampNs"),Is.EqualTo(0L)); // Normal VISUAL render ends the outage.
            writer.Dispose();
            Assert.That(sink.ToString().Contains("ReacquisitionEvidence"),Is.EqualTo(logging));
        }
        finally
        {
            field.SetValue(logger,null); Set("_telemetryValidationLogger",null);
            typeof(DjiTelemetryValidationLogger).GetField("_telemetrySessionId",Private).SetValue(logger,null);
        }
    }

    [TestCase(1.999f,false)] [TestCase(2f,false)] [TestCase(2.001f,true)]
    public void TwoSecondBoundaryRetainsTheShortWindowPositionRule(float age,bool accepted)
    {
        for(int t=9900;t<=10300;t+=100) Add(t);
        for(int t=10000;t<=10300;t+=100) Offer(t,distance:10,age:age);
        Assert.That((long)Get("_currentHighConfidenceVisualTimestampNs")>1000*Ms,Is.EqualTo(accepted));
    }

    [Test] public void ExactDwellBoundaryNeedsDistinctValidatedSourcesNotWallWaiting()
    {
        Add(10000); Add(10249); Add(10250);
        Offer(10000); Offer(10249); AssertBaseline();
        Offer(10250);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"),Is.EqualTo(10250*Ms));
    }

    [Test] public void IrrecoverablyOldBeforeEndpointDoesNotWaitForFutureEndpoint()
    {
        Add(9600); Offer(10000);
        Assert.That(Get("_hasVisualReacquisitionCandidate"),Is.False); AssertBaseline();
    }

    [Test] public void InvalidBracketQuaternionCannotBecomeAValidSlerpResult()
    {
        Add(9900,new Quaternion(0,0,0,0)); Add(10100); Offer(10000,now:10110);
        Assert.That(Get("_hasVisualReacquisitionCandidate"),Is.False); AssertBaseline();
    }

    private void Process(int t,int arrival) => Call("ProcessResultWithQualityLossExperiment",Result(t),arrival*Ms,true);
    private DjiBoardVisionProvider.NativeBoardResult Result(int t)
    {
        var r=JsonUtility.FromJson<DjiBoardVisionProvider.NativeBoardResult>(JsonUtility.ToJson(_fixture));
        r.timestampNs=r.visionFrameTimestampNs=t*Ms; r.frameSequence=++_sequence; r.visionFrameId=_sequence; r.listenerGeneration=1;
        return r;
    }
    private void Offer(int t,float angle=0,float distance=0,int? now=null,int? sequence=null,long generation=1,float age=10,Quaternion? rotation=null)
    {
        var result=Result(t); result.listenerGeneration=generation; if(sequence.HasValue)result.frameSequence=sequence.Value;
        var pose=new Pose(_visual.position+Vector3.right*distance,rotation??(_visual.rotation*Quaternion.AngleAxis(angle,Vector3.up)));
        var type=typeof(DjiBoardVisionProvider).GetNestedType("VisualPoseMetrics",BindingFlags.NonPublic);
        var metrics=Activator.CreateInstance(type,new object[]{t*Ms,4,16,"0,1,2,3",.5f,1f,50f,.01f,.02f,90f,10000f,pose,0f,0f,.1f,false,0f,false,0f});
        Call("OfferVisualReacquisitionCandidate",result,metrics,false,(now??t+10)*Ms);
        Tick(now??t+10,age);
    }
    private void Add(int t,Quaternion? nav=null) => _history.Add(new DjiCameraPoseProvider.TelemetryOrientationSample(t*Ms,nav??Quaternion.identity,true,0,0));
    private void Arrive(int t,int now) { Add(t); Call("OnTelemetryOrientationRecordedAtTimestamp",_history[_history.Count-1],now*Ms); }
    private void Tick(int now,float age=10) => Call("TryCompleteVisualReacquisitionHandoverAtTime",age,now*Ms);
    private void AssertBaseline() => Assert.That(Get("_currentHighConfidenceVisualTimestampNs"),Is.EqualTo(1000*Ms));
    private object Get(string name) => typeof(DjiBoardVisionProvider).GetField(name,Private).GetValue(_board);
    private void Set(string name,object value) => typeof(DjiBoardVisionProvider).GetField(name,Private).SetValue(_board,value);
    private object Call(string name,params object[] args) => typeof(DjiBoardVisionProvider).GetMethod(name,Private).Invoke(_board,args);
}
