using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class OrientationDiagnosticsTests
{
    private GameObject boardObject, cameraObject, loggerObject;
    private DjiBoardVisionProvider board;
    private DjiCameraPoseProvider camera;
    private DjiTelemetryValidationLogger logger;
    private StringWriter output;
    private const long Ms = 1_000_000;
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [SetUp]
    public void SetUp()
    {
        boardObject = new GameObject("diagnostic board"); boardObject.SetActive(false);
        cameraObject = new GameObject("diagnostic camera"); cameraObject.SetActive(false);
        loggerObject = new GameObject("diagnostic logger");
        board = boardObject.AddComponent<DjiBoardVisionProvider>();
        camera = cameraObject.AddComponent<DjiCameraPoseProvider>();
        logger = loggerObject.AddComponent<DjiTelemetryValidationLogger>();
        output = new StringWriter();
        Set(logger, "_visualLatencyWriter", output); Set(logger, "_telemetrySessionId", "synthetic-unity");
        Set(board, "_telemetryValidationLogger", logger); Set(board, "_telemetryProvider", camera);
        Set(camera, "<HasTelemetryOrientation>k__BackingField", true);
    }

    [TearDown]
    public void TearDown()
    {
        Set(logger, "_visualLatencyWriter", null);
        Object.DestroyImmediate(boardObject); Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(loggerObject);
    }

    private List<DjiCameraPoseProvider.TelemetryOrientationSample> History =>
        (List<DjiCameraPoseProvider.TelemetryOrientationSample>)Get(camera, "_telemetryOrientationHistory");

    [Test]
    public void QuaternionPrecisionOrderAndFrames_RoundTripExactly()
    {
        var q = Quaternion.Euler(12.34567f, -45.67891f, 1.13001f);
        var r = new OrientationDiagnosticRecord { recordType = "VisualOrientationCandidate", candidateId = 1234567890123,
            poseValid = true, candidate = q, outcome = "VALID_POSE_REJECTED", reason = "EXISTING_QUALITY_GATE" };
        var json = DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("s", r);
        var parsed = JsonUtility.FromJson<Row>(json);
        Assert.That(parsed.candidateQuaternion, Is.EqualTo(q));
        Assert.That(parsed.quaternionOrder, Is.EqualTo("XYZW"));
        Assert.That(parsed.candidateFrame, Does.Contain("Board_to_OpenCV"));
        Assert.That(parsed.originalAdmissionOutcome, Is.EqualTo("VALID_POSE_REJECTED"));
        Assert.That(parsed.candidateId, Is.EqualTo(r.candidateId));
    }

    [Test]
    public void ActualProductionSelector_SerializesBothSelectedEndpointsAndSlerp()
    {
        Add(100, Quaternion.Euler(3, 8, 1)); Add(200, Quaternion.Euler(7, 32, -2));
        Assert.That(Select(150), Is.True);
        var r = Rows()[0];
        Assert.That(r.endpointA.timestampNs, Is.EqualTo(100 * Ms));
        Assert.That(r.endpointB.timestampNs, Is.EqualTo(200 * Ms));
        Assert.That(r.endpointA.quaternion, Is.EqualTo(History[0].navigationFromCameraRotation));
        Assert.That(r.endpointB.quaternion, Is.EqualTo(History[1].navigationFromCameraRotation));
        Assert.That(r.interpolationFactor, Is.EqualTo(.5f));
        Assert.That(Quaternion.Angle(r.actualSelectedOrientation, Quaternion.Slerp(r.endpointA.quaternion, r.endpointB.quaternion, .5f)), Is.LessThan(.001));
        Assert.That(r.candidateId, Is.EqualTo(150 * Ms));
        Assert.That(r.bracketStatus, Is.EqualTo("EXACT_BRACKET"));
    }

    [Test]
    public void MissingEndpoint_RemainsExplicitlyMissing()
    {
        Add(100, Quaternion.identity);
        Assert.That(Select(150), Is.False);
        var json = output.ToString(); var r = Rows()[0];
        Assert.That(r.endpointA.present, Is.True); Assert.That(r.endpointB.present, Is.False);
        Assert.That(r.selectedValid, Is.False); Assert.That(r.bracketStatus, Is.EqualTo("UNAVAILABLE"));
        Assert.That(json, Does.Contain("\"x\":null"));
    }

    [Test]
    public void StaleEndpoints_AreSerializedButNotSelected()
    {
        Add(100, Quaternion.identity); Add(1000, Quaternion.Euler(0, 40, 0));
        Assert.That(Select(500), Is.False);
        var r = Rows()[0]; Assert.That(r.bracketStatus, Is.EqualTo("STALE"));
        Assert.That(r.endpointA.present && r.endpointB.present, Is.True);
        Assert.That(r.selectedValid, Is.False);
    }

    [Test]
    public void RepeatedLookup_DoesNotRepeatUnchangedDiagnosticState()
    {
        Add(100, Quaternion.identity); Select(150); Select(150); Select(150);
        Assert.That(Rows().Length, Is.EqualTo(1));
        Add(200, Quaternion.identity); Select(150);
        Assert.That(Rows().Length, Is.EqualTo(2));
    }

    [Test]
    public void DiagnosticsEnabledDisabled_SelectorOutputAndProductionStateUnchanged()
    {
        Add(100, Quaternion.Euler(13, 18, 2)); Add(200, Quaternion.Euler(14, 19, 3));
        var args = new object[] { 150 * Ms, null, null, "VISUAL_QUALITY" };
        var method = typeof(DjiBoardVisionProvider).GetMethod("TryInterpolateTelemetryRotation", Flags);
        var before = board.CurrentRenderPoseSource;
        var success = method.Invoke(board, args); var selected = args[1];
        Set(board, "_telemetryValidationLogger", null);
        var other = new object[] { 150 * Ms, null, null, "VISUAL_QUALITY" };
        Assert.That(method.Invoke(board, other), Is.EqualTo(success));
        Assert.That(other[1], Is.EqualTo(selected));
        Assert.That(board.CurrentRenderPoseSource, Is.EqualTo(before));
        Assert.That(board.HasHighConfidenceCameraFromBoard, Is.False);
    }

    [Test]
    public void Provenance_PreservesRawGimbalAircraftAndExtrinsic()
    {
        var s = new DJIPoseSnapshot { aircraftAttitudeArrivalTimestampNs = 111, gimbalAttitudeArrivalTimestampNs = 222 };
        s.aircraft.pitch = 1.234567890123; s.gimbal.roll = -2.345678901234;
        var p = new OrientationTelemetryProvenance(s, Quaternion.Euler(1,2,3), Quaternion.Euler(4,5,6), new Quaternion(.5f,.5f,.5f,.5f));
        var r = new OrientationDiagnosticRecord { endpointA = new DjiCameraPoseProvider.TelemetryOrientationSample(333, Quaternion.identity,true,0,0,p) };
        var parsed = JsonUtility.FromJson<Row>(DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("s", r));
        Assert.That(parsed.endpointA.aircraftPitchDeg, Is.EqualTo(s.aircraft.pitch));
        Assert.That(parsed.endpointA.gimbalRollDeg, Is.EqualTo(s.gimbal.roll));
        Assert.That(parsed.endpointA.aircraftArrivalNs, Is.EqualTo(111));
        Assert.That(parsed.endpointA.gimbalArrivalNs, Is.EqualTo(222));
        Assert.That(parsed.endpointA.gimbalFromCamera, Is.EqualTo(p.gimbalFromCamera));
    }

    [Test]
    public void InvalidPose_DoesNotSerializeIdentityAsAnObservation()
    {
        var json = DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("s", new OrientationDiagnosticRecord { poseValid = false, outcome = "INVALID_POSE" });
        Assert.That(json, Does.Contain("\"candidateQuaternion\":{\"x\":null,\"y\":null,\"z\":null,\"w\":null}"));
    }

    [TestCase(2, true, "VALID_POSE_REJECTED")]
    [TestCase(2, false, "INVALID_POSE")]
    [TestCase(0, false, "NO_POSE")]
    public void ActualCandidateHook_PreservesRejectedOrMissingPoseWithoutPromotion(int status,bool valid,string outcome)
    {
        var result = new DjiBoardVisionProvider.NativeBoardResult { status=status,markerCount=3,markerIds=new[]{0,1,3},
            visionFrameId=45,visionFrameTimestampNs=150*Ms,reprojectionRms=3.1234567f,
            cameraFromBoardPosition=valid?new[]{1f,2f,3f}:null,
            cameraFromBoardRotationMatrix=valid?new[]{1f,0f,0f,0f,1f,0f,0f,0f,1f}:null };
        Invoke(board,"BeginOrientationCandidate",result,175*Ms);
        Invoke(board,"EndOrientationCandidate",result,175*Ms);
        var r=Rows()[0];
        Assert.That(r.candidateId,Is.EqualTo(150*Ms));
        Assert.That(r.originalAdmissionOutcome,Is.EqualTo(outcome));
        Assert.That(board.HasHighConfidenceCameraFromBoard,Is.False);
        if(valid) Assert.That(r.candidateQuaternion,Is.EqualTo(Quaternion.identity));
        else Assert.That(output.ToString(),Does.Contain("\"candidateQuaternion\":{\"x\":null"));
    }

    [Test]
    public void ActualCandidateHook_RecordsFourToThreeTagSetChange()
    {
        var r=new DjiBoardVisionProvider.NativeBoardResult {status=0,markerIds=new[]{0,1,2,3},markerCount=4,visionFrameTimestampNs=100*Ms};
        Invoke(board,"EndOrientationCandidate",r,110*Ms);
        r.markerIds=new[]{0,1,3};r.markerCount=3;r.visionFrameTimestampNs=200*Ms;
        Invoke(board,"EndOrientationCandidate",r,210*Ms);
        var lines=output.ToString().Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines[1],Does.Contain("\"previousTagIds\":\"0,1,2,3\""));
        Assert.That(lines[1],Does.Contain("\"detectedTagIds\":\"0,1,3\""));
    }

    [Test]
    public void ReferenceTrace_UsesActualAuthoritativePoseAndPreviousReference()
    {
        var old = new DjiOutageVisualGroundTruthTracker.TrustedVisualPose(new Pose(Vector3.one, Quaternion.Euler(1,2,3)),100*Ms);
        Set(board,"_lastAuthoritativeVisualPose",old);
        Set(board,"_currentHighConfidenceVisualTimestampNs",200*Ms);
        var q = Quaternion.Euler(3,4,5);
        Set(board,"<CurrentHighConfidenceCameraFromBoard>k__BackingField",new Pose(Vector3.zero,q));
        Invoke(board,"TraceOrientationReference");
        var r=Rows()[0]; Assert.That(r.previousReferenceId,Is.EqualTo(100*Ms));
        Assert.That(r.referenceId,Is.EqualTo(200*Ms));
        Assert.That(r.previousReferenceQuaternion,Is.EqualTo(old.cameraFromBoard.rotation));
        Assert.That(r.newReferenceQuaternion,Is.EqualTo(q));
    }

    [Test]
    public void GeneratedUnityJsonl_ReconstructsKnownCameraYaw()
    {
        var reference = Quaternion.Euler(5,11,7);
        var a = Quaternion.identity; var b = Quaternion.Euler(0,20,0);
        Add(100,a); Add(200,b); Select(100); Select(150);
        var records = output.ToString();
        var q = Quaternion.Inverse(Quaternion.Slerp(a,b,.5f))*reference;
        records += DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("synthetic-unity", new OrientationDiagnosticRecord {
            recordType="VisualTrustedReferenceTrace", candidateId=100*Ms, referenceId=100*Ms,referenceValid=true,reference=reference })+"\n";
        records += DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("synthetic-unity", new OrientationDiagnosticRecord {
            recordType="VisualOrientationCandidate",candidateId=150*Ms,imageTimeNs=150*Ms,referenceId=100*Ms,
            poseValid=true,candidate=q,outcome="VALID_POSE_REJECTED",tagIds=new[]{0,1,2},markerCount=3 })+"\n";
        var dir=Environment.GetEnvironmentVariable("DJI_ORIENTATION_FIXTURE_DIR") ?? Path.GetTempPath();
        Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir,"generated-unity.jsonl"),records);
        Assert.That(records,Does.Contain("\"endpointB\":{\"present\":true"));
    }

    [Test]
    public void BoundedWriter_DrainsAndReportsDropsWithoutProducerDiskWrite()
    {
        var sink=new SlowWriter();
        var writer=new BoundedDiagnosticJsonlWriter(sink,2);
        for(var i=0;i<500;i++) writer.WriteLine("{\"test\":"+i+"}");
        writer.Flush(); writer.Dispose();
        Assert.That(writer.Dropped,Is.GreaterThan(0));
        Assert.That(sink.ToString(),Does.Contain("OrientationWriterHealth"));
        Assert.That(sink.ToString(),Does.Contain("\"final\":true"));
        Assert.That(sink.ProducerThreadWrote,Is.False);
        Assert.That(writer.WriteErrors,Is.Zero);
    }

    [Test]
    public void BoundedWriter_WriteErrorsAreCounted()
    {
        var writer=new BoundedDiagnosticJsonlWriter(new BrokenWriter());
        writer.WriteLine("x"); writer.Dispose(); Assert.That(writer.WriteErrors,Is.GreaterThan(0));
    }

    [Test]
    public void TraceOnly_DoesNotMutatePropagationAuthorityOrAuthoritativePose()
    {
        var pose = new Pose(new Vector3(1,2,3),Quaternion.Euler(3,5,7));
        Set(board,"<CurrentHighConfidenceCameraFromBoard>k__BackingField",pose);
        Set(board,"_currentHighConfidenceVisualTimestampNs",100*Ms);
        Set(board,"_shadowPropagationActive",true);
        var source=board.CurrentRenderPoseSource;
        Invoke(board,"TraceOrientationReference");
        Invoke(board,"TraceOrientationAuthorityTransition",DjiRenderPoseSource.TelemetryPropagated,DjiRenderPoseSource.Visual);
        Assert.That(board.CurrentRenderPoseSource,Is.EqualTo(source));
        Assert.That(Get(board,"_shadowPropagationActive"),Is.True);
        Assert.That(board.CurrentHighConfidenceCameraFromBoard,Is.EqualTo(pose));
        Assert.That(((DjiOutageVisualGroundTruthTracker.TrustedVisualPose)Get(board,"_lastAuthoritativeVisualPose")).IsValid,Is.False);
    }

    [Test]
    public void SerializationBenchmark_RecordsDesktopCostWithoutClaimingDeviceCost()
    {
        var q=Quaternion.Euler(3,11,2);
        var r=new OrientationDiagnosticRecord { recordType="TelemetryOrientationBracket",candidateId=150*Ms,
            imageTimeNs=150*Ms,endpointA=new DjiCameraPoseProvider.TelemetryOrientationSample(100*Ms,q,true,0,0),
            endpointB=new DjiCameraPoseProvider.TelemetryOrientationSample(200*Ms,q,true,0,0),selected=q,selectedValid=true,alpha=.5f };
        const int count=1000;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        long bytes=0;
        for(var i=0;i<count;i++) bytes+=System.Text.Encoding.UTF8.GetByteCount(DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("benchmark",r));
        watch.Stop();
        var dir=Environment.GetEnvironmentVariable("DJI_ORIENTATION_FIXTURE_DIR")??Path.GetTempPath();
        File.WriteAllText(Path.Combine(dir,"serialization-benchmark.json"),"{\"records\":"+count+",\"averageBytes\":"+(bytes/count)+
            ",\"totalMs\":"+watch.Elapsed.TotalMilliseconds.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+",\"platform\":\"UNITY_EDITOR_DESKTOP_NOT_ANDROID\"}");
        Assert.That(bytes,Is.GreaterThan(0));
    }

    private void Add(long ms,Quaternion q)=>History.Add(new DjiCameraPoseProvider.TelemetryOrientationSample(ms*Ms,q,true,0,0));
    private bool Select(long ms)=>(bool)Invoke(board,"TryInterpolateTelemetryRotation",ms*Ms,null,null,"VISUAL_QUALITY");
    private Row[] Rows()=>Array.ConvertAll(output.ToString().Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries),JsonUtility.FromJson<Row>);
    private static object Get(object o,string n)=>o.GetType().GetField(n,Flags).GetValue(o);
    private static void Set(object o,string n,object v)=>o.GetType().GetField(n,Flags).SetValue(o,v);
    private static object Invoke(object o,string n,params object[] args)=>o.GetType().GetMethod(n,Flags).Invoke(o,args);
    [Serializable] private sealed class Row
    {
        public long candidateId,referenceId,previousReferenceId;
        public string quaternionOrder,candidateFrame,originalAdmissionOutcome,bracketStatus;
        public bool selectedValid;
        public float interpolationFactor;
        public Quaternion candidateQuaternion,actualSelectedOrientation,previousReferenceQuaternion,newReferenceQuaternion;
        public Endpoint endpointA,endpointB;
    }
    [Serializable] private sealed class Endpoint
    {
        public bool present; public long timestampNs,aircraftArrivalNs,gimbalArrivalNs;
        public Quaternion quaternion,gimbalFromCamera;
        public double aircraftPitchDeg,gimbalRollDeg;
    }
    private sealed class SlowWriter:StringWriter
    {
        private readonly int producer=Thread.CurrentThread.ManagedThreadId;
        public bool ProducerThreadWrote;
        public override void WriteLine(string s) { ProducerThreadWrote|=Thread.CurrentThread.ManagedThreadId==producer; Thread.Sleep(2); base.WriteLine(s); }
    }
    private sealed class BrokenWriter:StringWriter { public override void WriteLine(string s)=>throw new IOException("synthetic disk failure"); }
}
