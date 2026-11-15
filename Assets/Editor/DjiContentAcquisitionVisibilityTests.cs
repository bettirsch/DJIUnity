using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class DjiContentAcquisitionVisibilityTests
{
    private GameObject _overlayObject;
    private GameObject _providerObject;
    private GameObject _videoObject;
    private DjiSharedReferenceCubeOverlay _overlay;
    private DjiBoardVisionProvider _provider;
    private ARContentType _previousSelection;

    [SetUp]
    public void SetUp()
    {
        _previousSelection = ARContentSelection.Selected;
        ARContentSelection.Set(ARContentType.RoofCanopy);
        _videoObject = new GameObject("Visibility test video camera", typeof(Camera));
        _providerObject = new GameObject("Visibility test provider");
        _provider = _providerObject.AddComponent<DjiBoardVisionProvider>();
        _overlayObject = new GameObject("Visibility test overlay");
        _overlay = _overlayObject.AddComponent<DjiSharedReferenceCubeOverlay>();
        _overlay.Configure(_videoObject.GetComponent<Camera>(), _provider, true);
    }

    [TearDown]
    public void TearDown()
    {
        var content = Field<GameObject>("_contentRoot");
        var camera = Field<Camera>("_overlayCamera");
        UnityEngine.Object.DestroyImmediate(_overlayObject);
        if (content != null) UnityEngine.Object.DestroyImmediate(content);
        if (camera != null) UnityEngine.Object.DestroyImmediate(camera.gameObject);
        UnityEngine.Object.DestroyImmediate(_providerObject);
        UnityEngine.Object.DestroyImmediate(_videoObject);
        ARContentSelection.Set(_previousSelection);
    }

    [Test]
    public void Creation_WithCachedFalse_ActuallyDisablesEveryRoofRenderer()
    {
        Assert.That(Field<bool>("_cubeVisible"), Is.False);
        Assert.That(Field<GameObject>("_cube").name, Is.EqualTo("RoofCanopyPlacement"));
        AssertVisibility(false);
    }

    [Test]
    public void TelemetryOrPendingVisual_CannotShowContentBeforeAuthoritativeAcquisition()
    {
        RequestVisibility(true);
        AssertVisibility(false);
        Assert.That(_provider.HasHighConfidenceCameraFromBoard, Is.False);
    }

    [Test]
    public void AuthoritativeVisualEvent_AllowsVisibilityOnTheNextResolvedRender()
    {
        PublishAuthoritativeVisual();
        AssertVisibility(false);
        RequestVisibility(true);
        AssertVisibility(true);
    }

    [Test]
    public void LaterVisualLoss_DoesNotClearPermissionForPropagatedRendering()
    {
        PublishAuthoritativeVisual();
        RequestVisibility(true);
        Assert.That(_provider.HasHighConfidenceCameraFromBoard, Is.False);
        RequestVisibility(true);
        AssertVisibility(true);
        RequestVisibility(false);
        AssertVisibility(false);
        RequestVisibility(true);
        AssertVisibility(true);
    }

    [Test]
    public void NewProviderSession_RequiresItsOwnFirstAuthoritativeAcquisition()
    {
        PublishAuthoritativeVisual();
        RequestVisibility(true);
        var newProviderObject = new GameObject("New visibility session");
        try
        {
            var nextProvider = newProviderObject.AddComponent<DjiBoardVisionProvider>();
            _overlay.Configure(_videoObject.GetComponent<Camera>(), nextProvider, true);
            PublishAuthoritativeVisual();
            RequestVisibility(true);
            AssertVisibility(false);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(newProviderObject);
        }
    }

    private void PublishAuthoritativeVisual()
    {
        var accepted = (Action<Pose, long>)typeof(DjiBoardVisionProvider)
            .GetField("AuthoritativeVisualPoseAccepted", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(_provider);
        accepted?.Invoke(new Pose(Vector3.forward, Quaternion.identity), 100_000_000L);
    }

    private void RequestVisibility(bool visible) => typeof(DjiSharedReferenceCubeOverlay)
        .GetMethod("SetCubeVisible", BindingFlags.Instance | BindingFlags.NonPublic)
        .Invoke(_overlay, new object[] { visible, "TEST_RESOLVED_RENDER_SOURCE" });

    private T Field<T>(string name) => (T)typeof(DjiSharedReferenceCubeOverlay)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_overlay);

    private void AssertVisibility(bool enabled)
    {
        var renderers = Field<Renderer[]>("_contentRenderers");
        Assert.That(renderers.Length, Is.EqualTo(3));
        foreach (var renderer in renderers)
            Assert.That(renderer.enabled, Is.EqualTo(enabled), renderer.name);
    }
}
