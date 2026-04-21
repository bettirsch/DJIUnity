using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

public sealed class DJIBackgroundURPFeature : ScriptableRendererFeature
{
    [Serializable]
    public sealed class Settings
    {
        [Header("Material")]
        public Material backgroundMaterial;

        [Header("When to draw")]
        [Tooltip("Usually AfterRenderingSkybox. If you still see skybox, try BeforeRenderingOpaques.")]
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingSkybox;

        [Header("Camera filtering")]
        [Tooltip("Recommended: only draw on Base cameras.")]
        public bool onlyBaseCamera = true;

        [Header("Logging")]
        public bool enableLogs = false;

        [Min(1)]
        [Tooltip("Logs once every N frames.")]
        public int logEveryNFrames = 120;

        public string logTag = "[DJI][DJIBackgroundURPFeature]";
    }

    [SerializeField] private Settings settings = new Settings();
    private Pass _pass;

    public override void Create()
    {
        _pass = new Pass(settings);
        _pass.renderPassEvent = settings.renderPassEvent;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.backgroundMaterial == null)
        {
            if (settings.enableLogs)
                Debug.LogWarning($"{settings.logTag} Background Material is NULL -> nothing will be drawn.");
            return;
        }

        _pass.UpdateSettings(settings);
        _pass.Setup(settings.backgroundMaterial);
        renderer.EnqueuePass(_pass);
    }

    private sealed class Pass : ScriptableRenderPass
    {
        private Settings _s;
        private Material _mat;

        private readonly ProfilingSampler _sampler = new ProfilingSampler("DJI Background");

        private bool _warnedMissingBg;
        private bool _warnedMissingMat;

        public Pass(Settings s) => _s = s;

        public void UpdateSettings(Settings s)
        {
            _s = s;
            renderPassEvent = _s.renderPassEvent;
        }

        public void Setup(Material mat) => _mat = mat;

        // Unity 6 marks Execute as obsolete; we keep it as a fallback path.
        [Obsolete("Execute is obsolete in Unity 6 URP; kept as a fallback when RenderGraph isn't used.", false)]
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!ShouldRun(ref renderingData))
                return;

            var cmd = CommandBufferPool.Get("DJI Background (Fallback)");
            using (new ProfilingScope(cmd, _sampler))
            {
                if (TryPrepare(out var bg, out var tex))
                {
                    CoreUtils.DrawFullScreen(cmd, _mat);
                    LogDraw("fallback", bg, tex);
                }
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

#if UNITY_6000_0_OR_NEWER
        // Must be reference type for Unity 6 AddRasterRenderPass<T>()
        private sealed class PassData
        {
            public Material mat;
            public Settings settings;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_mat == null)
            {
                WarnOnce(ref _warnedMissingMat, $"{_s.logTag} RecordRenderGraph: material is null.");
                return;
            }

            var camData = frameData.Get<UniversalCameraData>();
            if (_s.onlyBaseCamera && camData.renderType != CameraRenderType.Base)
                return;

            var res = frameData.Get<UniversalResourceData>();
            var colorTarget = res.activeColorTexture;

            using var builder =
                renderGraph.AddRasterRenderPass<PassData>("DJI Background", out var passData, _sampler);

            passData.mat = _mat;
            passData.settings = _s;

            builder.SetRenderAttachment(colorTarget, 0, AccessFlags.Write);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
            {
                if (data.mat == null) return;

                if (!TryPrepare(out var bg, out var tex))
                    return;

                CoreUtils.DrawFullScreen(ctx.cmd, data.mat);

                if (data.settings.enableLogs)
                {
                    int n = Mathf.Max(1, data.settings.logEveryNFrames);
                    if ((Time.frameCount % n) == 0)
                    {
                        Debug.Log(
                            $"{data.settings.logTag} frame={Time.frameCount} RG: drew tex={DescribeTex(tex)} " +
                            $"extTexId={(bg != null ? bg.ExternalTextureId : 0)} evt={data.settings.renderPassEvent}"
                        );
                    }
                }
            });
        }
#endif

        private bool ShouldRun(ref RenderingData renderingData)
        {
            if (_mat == null) return false;
            if (_s.onlyBaseCamera && renderingData.cameraData.renderType != CameraRenderType.Base)
                return false;
            return true;
        }

        private bool TryPrepare(out DJIGPUBackground bg, out Texture tex)
        {
            tex = null;

            // Prefer Instance, but fall back to finding it in scene.
            bg = DJIGPUBackground.Instance;
            if (bg == null)
            {
#if UNITY_2023_1_OR_NEWER
                bg = UnityEngine.Object.FindAnyObjectByType<DJIGPUBackground>();
#else
                bg = UnityEngine.Object.FindObjectOfType<DJIGPUBackground>();
#endif
            }

            if (bg == null)
            {
                WarnOnce(ref _warnedMissingBg, $"{_s.logTag} DJIGPUBackground not found in scene -> skipping.");
                return false;
            }

            // IMPORTANT: latch BEFORE checking IsReady (DJI startup deadlock avoidance)
            DJIGPUBackground.TryLatchOnRenderThread();

            if (!bg.IsReady)
            {
                LogSkip("BG not ready -> skipping.");
                return false;
            }

            // Ensure material is bound to external texture property (_External etc.)
            bg.ApplyToMaterial(_mat);
            if (_s.enableLogs)
            {
                int n = Mathf.Max(1, _s.logEveryNFrames);
                if ((Time.frameCount % n) == 0)
                {
                    var t = _mat.GetTexture("_MainTex");
                    Debug.Log($"{_s.logTag} _MainTex after ApplyToMaterial = {(t ? t.name : "NULL")}");
                }
            }

            tex = bg.ExternalTexture;
            if (tex == null)
            {
                LogSkip("ExternalTexture is null -> skipping.");
                return false;
            }

            return true;
        }

        private void LogDraw(string path, DJIGPUBackground bg, Texture tex)
        {
            if (!_s.enableLogs) return;
            int n = Mathf.Max(1, _s.logEveryNFrames);
            if ((Time.frameCount % n) != 0) return;

            Debug.Log(
                $"{_s.logTag} frame={Time.frameCount} {path}: drew tex={DescribeTex(tex)} " +
                $"extTexId={(bg != null ? bg.ExternalTextureId : 0)} evt={_s.renderPassEvent}"
            );
        }

        private void LogSkip(string msg)
        {
            if (!_s.enableLogs) return;
            int n = Mathf.Max(1, _s.logEveryNFrames);
            if ((Time.frameCount % n) != 0) return;

            Debug.Log($"{_s.logTag} frame={Time.frameCount} {msg}");
        }

        private static void WarnOnce(ref bool flag, string msg)
        {
            if (flag) return;
            flag = true;
            Debug.LogWarning(msg);
        }

        private static string DescribeTex(Texture t)
        {
            if (t == null) return "null";
            return $"{t.name}({t.GetType().Name}) id={t.GetInstanceID()} {t.width}x{t.height}";
        }
    }
}
