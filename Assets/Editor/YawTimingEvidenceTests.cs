using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class YawTimingEvidenceTests
{
    private const long Ms=1_000_000L;
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private GameObject _cameraObject,_loggerObject,_boardObject;
    private DjiCameraPoseProvider _camera;
    private DjiTelemetryValidationLogger _logger;
    private DjiBoardVisionProvider _board;
    private StringWriter _output;

    [SetUp]
    public void SetUp()
    {
        _cameraObject=new GameObject("Yaw timing camera");_cameraObject.SetActive(false);_camera=_cameraObject.AddComponent<DjiCameraPoseProvider>();
        _boardObject=new GameObject("Yaw timing board");_boardObject.SetActive(false);_board=_boardObject.AddComponent<DjiBoardVisionProvider>();
        _loggerObject=new GameObject("Yaw timing logger");_logger=_loggerObject.AddComponent<DjiTelemetryValidationLogger>();_output=new StringWriter();
        Set(_logger,"_visualLatencyWriter",_output);Set(_logger,"_telemetrySessionId","yaw-test");Set(_logger,"_telemetryProvider",_camera);
        Set(_board,"_telemetryProvider",_camera);Set(_camera,"<HasTelemetryOrientation>k__BackingField",true);
    }

    [TearDown]
    public void TearDown(){Set(_logger,"_visualLatencyWriter",null);Object.DestroyImmediate(_loggerObject);Object.DestroyImmediate(_cameraObject);Object.DestroyImmediate(_boardObject);}
    private static void Set(object target,string name,object value)=>target.GetType().GetField(name,Private).SetValue(target,value);
    private List<DjiCameraPoseProvider.TelemetryOrientationSample> History=>(List<DjiCameraPoseProvider.TelemetryOrientationSample>)typeof(DjiCameraPoseProvider).GetField("_telemetryOrientationHistory",Private).GetValue(_camera);
    private static Quaternion Attitude(float yaw)=>DjiNavigationFrameTransforms.RotationFromDjiNedAttitudeDegrees(0,0,yaw);
    private static Quaternion Compose(Quaternion aircraft,Quaternion absoluteGimbal)
    {
        var relative=Quaternion.Normalize(Quaternion.Inverse(aircraft)*absoluteGimbal);
        return DjiNavigationFrameTransforms.PoseFromMatrix(DjiNavigationFrameTransforms.NavigationFromCamera(
            DjiNavigationFrameTransforms.NavigationFromAircraft(new Pose(Vector3.zero,aircraft)),
            DjiNavigationFrameTransforms.AircraftFromGimbal(new Pose(Vector3.zero,relative)),
            DjiNavigationFrameTransforms.GimbalFromCamera(new Pose(Vector3.zero,DjiNavigationFrameTransforms.DefaultGimbalFromOpenCvCameraRotation)))).rotation;
    }
    private static DjiCameraPoseProvider.TelemetryOrientationSample Sample(long aggregateMs,long aircraftMs,long gimbalMs,float aircraftYaw=0,float gimbalYaw=0)
    {
        var snapshot=new DJIPoseSnapshot{aircraftAttitudeArrivalTimestampNs=aircraftMs*Ms,gimbalAttitudeArrivalTimestampNs=gimbalMs*Ms,
            aircraft=new DJIPoseSnapshot.AircraftPose{hasAttitude=true,yaw=aircraftYaw},gimbal=new DJIPoseSnapshot.GimbalPose{hasAttitude=true,yaw=gimbalYaw}};
        var a=Attitude(aircraftYaw);var g=Attitude(gimbalYaw);var mounting=DjiNavigationFrameTransforms.DefaultGimbalFromOpenCvCameraRotation;
        return new DjiCameraPoseProvider.TelemetryOrientationSample(aggregateMs*Ms,Compose(a,g),true,aircraftYaw,0,
            new OrientationTelemetryProvenance(snapshot,a,Quaternion.Inverse(a)*g,mounting),(aggregateMs+5)*Ms);
    }
    private Row[] Rows()=>Array.ConvertAll(_output.ToString().Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries),JsonUtility.FromJson<Row>);

    [TestCase(0f),TestCase(30f),TestCase(90f),TestCase(-120f)]
    public void AircraftYawWithStabilizedGimbal_DoesNotInventCameraYaw(float yaw)
    {
        var baseline=Compose(Attitude(0),Attitude(15));var result=Compose(Attitude(yaw),Attitude(15));
        Assert.That(Quaternion.Angle(baseline,result),Is.LessThan(.05f));
    }

    [TestCase(-30f),TestCase(30f)]
    public void GenuineFreshCameraYaw_IsPreservedByProductionFrameComposition(float yaw)
    {
        var baseline=Compose(Attitude(10),Attitude(0));var changed=Compose(Attitude(10),Attitude(yaw));
        Assert.That(Quaternion.Angle(baseline,changed),Is.EqualTo(Mathf.Abs(yaw)).Within(.01));
        Assert.That(Quaternion.Angle(changed,Attitude(yaw)*DjiNavigationFrameTransforms.DefaultGimbalFromOpenCvCameraRotation),Is.LessThan(.05f));
    }

    [Test]
    public void StationaryCameraAndQuaternionSignEquivalence_DoNotJump()
    {
        var a=Quaternion.Euler(4,20,-3);var g=Quaternion.Euler(2,12,1);var minus=new Quaternion(-g.x,-g.y,-g.z,-g.w);
        Assert.That(Quaternion.Angle(Compose(a,g),Compose(a,minus)),Is.LessThan(.05f));
        Assert.That(Compose(a,g),Is.EqualTo(Compose(a,g)));
    }

    [Test]
    public void FreshAircraftWithOldGimbal_ReportsIndependentAgesWithoutFresheningGimbal()
    {
        var sample=Sample(1000,1000,100,30,0);
        var json=DjiTelemetryValidationLogger.BuildCameraOrientationTimingJson("s",1100*Ms,DjiRenderPoseSource.TelemetryPropagated,Pose.identity,sample,true);
        var row=JsonUtility.FromJson<Row>(json);
        Assert.That(row.aggregateAgeMs,Is.EqualTo(100));Assert.That(row.aircraftComponentAgeMs,Is.EqualTo(100));Assert.That(row.gimbalComponentAgeMs,Is.EqualTo(1000));
        Assert.That(row.cameraOrientationCalculationTimestampNs,Is.EqualTo(1005*Ms));
        Assert.That(row.selectedTelemetry.gimbalArrivalNs,Is.EqualTo(100*Ms));Assert.That(row.selectedTelemetry.quaternion,Is.EqualTo(sample.navigationFromCameraRotation));
        Assert.That(json,Does.Contain("NO_NEW_COMPONENT_AGE_LIMIT_DIAGNOSTIC_ONLY"));
    }

    [Test]
    public void SelectedSamplePublication_PreservesSourceCalculationAndRenderTimes()
    {
        History.Add(Sample(1000,980,990));
        Set(_logger,"_latestMotionTrace",new HorizontalAdmissionTrace(1050*Ms,default,default,default,1000*Ms,50,default,900*Ms));
        var pose=new Pose(Vector3.one,Quaternion.Euler(1,25,2));_logger.RecordMotionPoseApplied(1100*Ms,DjiRenderPoseSource.TelemetryPropagated,pose);
        var row=Rows()[0];Assert.That(row.renderApplicationTimestampNs,Is.EqualTo(1100*Ms));Assert.That(row.selectedTelemetry.timestampNs,Is.EqualTo(1000*Ms));
        Assert.That(row.selectedTelemetryUsedForPropagation,Is.True);Assert.That(row.appliedCameraFromBoardRotation,Is.EqualTo(pose.rotation));
    }

    [Test]
    public void VisualAuthority_IsNotRelabeledAsTelemetryOrientation()
    {
        History.Add(Sample(1000,1000,1000));var pose=new Pose(Vector3.zero,Quaternion.Euler(2,37,1));
        _logger.RecordMotionPoseApplied(1100*Ms,DjiRenderPoseSource.Visual,pose);var row=Rows()[0];
        Assert.That(row.selectedTelemetryUsedForPropagation,Is.False);Assert.That(row.renderAuthority,Is.EqualTo("Visual"));
        Assert.That(row.appliedCameraFromBoardRotation,Is.EqualTo(pose.rotation));
    }

    [Test]
    public void FutureOrUnavailableTelemetry_DoesNotFabricateFreshComponentData()
    {
        History.Add(Sample(1200,1200,1200));_logger.RecordMotionPoseApplied(1100*Ms,DjiRenderPoseSource.Visual,Pose.identity);
        Assert.That(Rows()[0].selectedTelemetry.present,Is.False);Assert.That(_output.ToString(),Does.Contain("\"gimbalComponentAgeMs\":null"));
        var json=DjiTelemetryValidationLogger.BuildCameraOrientationTimingJson("s",1100*Ms,DjiRenderPoseSource.TelemetryPropagated,Pose.identity,History[0],true);
        Assert.That(json,Does.Contain("\"selectedTelemetryUsedForPropagation\":false"));
    }

    [Test]
    public void RepeatedRenderedFrames_AreThrottledButAuthorityTransitionIsObservable()
    {
        History.Add(Sample(900,900,900));
        for(int i=0;i<=22;i++)_logger.RecordMotionPoseApplied((1000+i*10)*Ms,DjiRenderPoseSource.Visual,Pose.identity);
        Assert.That(Rows().Length,Is.EqualTo(2));
        _logger.RecordMotionPoseApplied(1230*Ms,DjiRenderPoseSource.TelemetryPropagated,Pose.identity);
        Assert.That(Rows().Length,Is.EqualTo(3));
    }

    [Test]
    public void DiagnosticsDisabled_DoNotPublishOrMutateCameraPose()
    {
        History.Add(Sample(1000,1000,1000));var before=_camera.NavigationFromCamera;Set(_logger,"enableMotionDiagnostics",false);
        _logger.RecordMotionPoseApplied(1100*Ms,DjiRenderPoseSource.Visual,Pose.identity);
        Assert.That(_output.ToString(),Is.Empty);Assert.That(_camera.NavigationFromCamera,Is.EqualTo(before));
        Assert.That(History.Count,Is.EqualTo(1));
    }

    [Test]
    public void DiagnosticWriterFailure_DoesNotEscapeIntoRenderApplication()
    {
        Set(_logger,"_visualLatencyWriter",new ThrowingWriter());
        Assert.DoesNotThrow(()=>_logger.RecordMotionPoseApplied(1100*Ms,DjiRenderPoseSource.Visual,Pose.identity));
    }

    [Test]
    public void ExistingTimestampSelector_UsesSlerpWithoutAnAddedDelay()
    {
        History.Add(Sample(100,100,100,0,0));History.Add(Sample(200,200,200,0,30));
        var args=new object[]{150*Ms,null,null,"ROTATION_COMPARISON"};
        Assert.That(_board.GetType().GetMethod("TryInterpolateTelemetryRotation",Private).Invoke(_board,args),Is.EqualTo(true));
        var selected=(Quaternion)args[1].GetType().GetField("navigationFromCameraRotation").GetValue(args[1]);
        Assert.That(Quaternion.Angle(selected,Quaternion.Slerp(History[0].navigationFromCameraRotation,History[1].navigationFromCameraRotation,.5f)),Is.LessThan(.05f));
    }

    [Test]
    public void StaleBracket_RemainsRejectedByExistingPolicy()
    {
        History.Add(Sample(100,100,100));History.Add(Sample(1000,1000,1000));
        var args=new object[]{500*Ms,null,null,"ROTATION_COMPARISON"};
        Assert.That(_board.GetType().GetMethod("TryInterpolateTelemetryRotation",Private).Invoke(_board,args),Is.EqualTo(false));
        Assert.That((string)args[2],Does.Contain("TOO_OLD"));
    }

    [Test]
    public void ThrottledRenderFrames_DoNotAllocateOrDuplicateOrientationEvents()
    {
        History.Add(Sample(1000,1000,1000));_logger.RecordMotionPoseApplied(1100*Ms,DjiRenderPoseSource.Visual,Pose.identity);
        for(int i=0;i<100;i++)_logger.RecordMotionPoseApplied(1110*Ms,DjiRenderPoseSource.Visual,Pose.identity);
        var controlBefore=GC.GetAllocatedBytesForCurrentThread();var control=new byte[8192];GC.KeepAlive(control);
        var counterAvailable=GC.GetAllocatedBytesForCurrentThread()>controlBefore;
        var before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)_logger.RecordMotionPoseApplied(1110*Ms,DjiRenderPoseSource.Visual,Pose.identity);
        var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        if(counterAvailable)Assert.That(allocated,Is.Zero);
        else TestContext.WriteLine("Per-thread allocation counter unavailable; zero-byte reading is not allocation proof.");
        Assert.That(Rows().Length,Is.EqualTo(1));
    }

    [Test]
    public void OrientationTimingSerialization_RecordsMeasuredDiagnosticCost()
    {
        var sample=Sample(1000,1000,100);var pose=Pose.identity;
        for(int i=0;i<50;i++)DjiTelemetryValidationLogger.BuildCameraOrientationTimingJson("s",1100*Ms,DjiRenderPoseSource.TelemetryPropagated,pose,sample,true);
        var controlBefore=GC.GetAllocatedBytesForCurrentThread();var control=new byte[8192];GC.KeepAlive(control);
        var counterAvailable=GC.GetAllocatedBytesForCurrentThread()>controlBefore;
        var watch=System.Diagnostics.Stopwatch.StartNew();var allocatedBefore=GC.GetAllocatedBytesForCurrentThread();int length=0;
        for(int i=0;i<2000;i++)length=DjiTelemetryValidationLogger.BuildCameraOrientationTimingJson("s",1100*Ms,DjiRenderPoseSource.TelemetryPropagated,pose,sample,true).Length;
        var allocated=GC.GetAllocatedBytesForCurrentThread()-allocatedBefore;watch.Stop();
        var directory=Environment.GetEnvironmentVariable("DJI_VERTICAL_REPLAY_DIR");
        if(!string.IsNullOrEmpty(directory))File.WriteAllText(Path.Combine(directory,"diagnostic-timing-benchmark.json"),
            "{\"iterations\":2000,\"meanSerializationMs\":"+(watch.Elapsed.TotalMilliseconds/2000).ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
            ",\"allocationCounterAvailable\":"+(counterAvailable?"true":"false")+",\"allocatedBytesPerRecord\":"+(counterAvailable?(allocated/2000d).ToString("R",System.Globalization.CultureInfo.InvariantCulture):"null")+
            ",\"charactersPerRecord\":"+length+",\"steadyRecordRateHz\":5}");
        TestContext.WriteLine($"Orientation trace mean ms={watch.Elapsed.TotalMilliseconds/2000:R}; bytes/record={allocated/2000d:R}; characters={length}");
    }

    private sealed class ThrowingWriter:TextWriter{public override Encoding Encoding=>Encoding.UTF8;public override void WriteLine(string value)=>throw new IOException("Synthetic writer failure");}
    [Serializable]private sealed class Row
    {
        public string renderAuthority;public long renderApplicationTimestampNs,cameraOrientationCalculationTimestampNs;
        public double aggregateAgeMs,aircraftComponentAgeMs,gimbalComponentAgeMs;public bool selectedTelemetryUsedForPropagation;
        public Endpoint selectedTelemetry;public Quaternion appliedCameraFromBoardRotation;
    }
    [Serializable]private sealed class Endpoint{public bool present;public long timestampNs,gimbalArrivalNs;public Quaternion quaternion;}
}
