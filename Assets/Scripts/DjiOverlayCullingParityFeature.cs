using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Compensates the DJI proper-rotation view's parity only for camera-space
/// geometry. Shadow maps keep their light-space winding; no matrices change.
/// </summary>
public sealed class DjiOverlayCullingParityFeature : ScriptableRendererFeature
{
    private StatePass _begin;
    private StatePass _end;

    public override void Create()
    {
        _begin = new StatePass("DJI overlay culling parity", RenderPassEvent.BeforeRenderingPrePasses);
        _end = new StatePass("DJI overlay culling restore", RenderPassEvent.AfterRenderingTransparents);
    }

    public static bool ShouldCorrect(Camera camera, CameraRenderType renderType)
    {
        if (camera == null || renderType != CameraRenderType.Overlay ||
            !camera.TryGetComponent<DjiOverlayCullingParity>(out var marker) || !marker.isActiveAndEnabled)
            return false;

        var determinant = camera.worldToCameraMatrix.determinant;
        return float.IsFinite(determinant) && determinant > 0f;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!ShouldCorrect(renderingData.cameraData.camera, renderingData.cameraData.renderType))
            return;

        // Capture once for the paired commands, not again after rendering starts.
        var original = GL.invertCulling;
        _begin.Setup(!original);
        _end.Setup(original);
        renderer.EnqueuePass(_begin);
        renderer.EnqueuePass(_end);
    }

    private sealed class StatePass : ScriptableRenderPass
    {
        private sealed class PassData { public bool invert; }
        private readonly string _name;
        private bool _invert;

        public StatePass(string name, RenderPassEvent evt)
        {
            _name = name;
            renderPassEvent = evt;
        }

        public void Setup(bool invert) => _invert = invert;

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            using var builder = graph.AddUnsafePass<PassData>(_name, out var data);
            data.invert = _invert;
            // These ordered state-only passes must bracket depth/opaque/transparent
            // draws even when they have no texture output of their own.
            builder.AllowGlobalStateModification(true);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (PassData d, UnsafeGraphContext context) => context.cmd.SetInvertCulling(d.invert));
        }

        [Obsolete("Compatibility-mode fallback; production uses RenderGraph.", false)]
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cmd = CommandBufferPool.Get(_name);
            try
            {
                cmd.SetInvertCulling(_invert);
                context.ExecuteCommandBuffer(cmd);
            }
            finally { CommandBufferPool.Release(cmd); }
        }
    }
}
