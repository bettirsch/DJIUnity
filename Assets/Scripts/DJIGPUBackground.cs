using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

public sealed class DJIGPUBackground : MonoBehaviour
{
#if UNITY_ANDROID && !UNITY_EDITOR
    [DllImport("djiunity")] private static extern IntPtr DJI_GetRenderEventFunc();
    [DllImport("djiunity")] private static extern void DJI_BeginCreateOESTexture(int reqW, int reqH);
    [DllImport("djiunity")] private static extern int DJI_GetTextureId();
    [DllImport("djiunity")] private static extern void DJI_GetSurfaceTextureTransform([Out] float[] outMatrix16);
    [DllImport("djiunity")] private static extern void DJI_SetSurfaceTexture(IntPtr surfaceTex);
    [DllImport("djiunity")] private static extern void DJI_ClearSurfaceTexture();
#endif

    [Header("Material")]
    [Tooltip("Material using shader DJI/OESBackgroundURP (or your background shader).")]
    public Material backgroundMat;

    [Header("Stream Size / OES Texture Size")]
    [Tooltip("Requested OES texture width. Must match what your native + decoder pipeline expects.")]
    public int streamWidth = 1920;

    [Tooltip("Requested OES texture height. Must match what your native + decoder pipeline expects.")]
    public int streamHeight = 1080;

    [Tooltip("Apply an extra Y flip after the SurfaceTexture transform. Usually keep this off.")]
    public bool extraFlipY = true;

    [Header("Update Driving")]
    [Tooltip("If you use a URP Renderer Feature to call TryLatchOnRenderThread() every frame, leave this OFF.")]
    public bool driveUpdateFromUpdate = false;

    [Header("Debug")]
    public bool verboseLogs = true;

    // ---- Public API ---------------------------------------------------------

    public static DJIGPUBackground Instance { get; private set; }

    /// <summary> True once OES texture + SurfaceTexture + decoder surface are set. </summary>
    public bool IsReady => _ready;

    /// <summary> The Unity external texture wrapping the OES texture. </summary>
    public Texture ExternalTexture
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return _unityTex;
#else
            return null;
#endif
        }
    }

    /// <summary> The GL texture id created by native (OES). Useful for debugging. </summary>
    public int ExternalTextureId => _texId;

    /// <summary>
    /// Bind the current external texture to the given material.
    /// This is the "perfect" place to match your shader property names.
    /// </summary>
    public void ApplyToMaterial(Material m)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (m == null || _unityTex == null) return;

        // Your shader inspector shows a texture slot named "External"
        // -> very likely property is _External.
        if (m.HasProperty(_PID_External)) m.SetTexture(_PID_External, _unityTex);

        // Safe fallbacks in case shader uses a different name.
        if (m.HasProperty(_PID_MainTex)) m.SetTexture(_PID_MainTex, _unityTex);
        if (m.HasProperty(_PID_BaseMap)) m.SetTexture(_PID_BaseMap, _unityTex);
        if (m.HasProperty(_PID_ExternalTex)) m.SetTexture(_PID_ExternalTex, _unityTex);
        if (m.HasProperty(_PID_OESTex)) m.SetTexture(_PID_OESTex, _unityTex);
        if (m.HasProperty(_PID_OESTexture)) m.SetTexture(_PID_OESTexture, _unityTex);

        if (m.HasProperty(_PID_FlipY)) m.SetFloat(_PID_FlipY, extraFlipY ? 1f : 0f);
        m.SetMatrix(_PID_TexTransform, GetSurfaceTextureTransformMatrix());
#endif
    }

    /// <summary>
    /// Called by URP pass (RenderGraph) or by Update().
    /// Ensures: ready + valid pointer + frame gate (if enabled) + prevents double latch per frame.
    /// MUST be invoked from render thread context for best behavior (URP pass is ideal).
    /// </summary>
    public static void TryLatchOnRenderThread()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        var bg = Instance;
        if (bg == null || !bg._ready) return;
        if (_renderEventFunc == IntPtr.Zero) return;

        if (_useFrameGate)
        {
            // Consume the pending flag. If 0, no new frame available yet.
            if (Interlocked.Exchange(ref _framePending, 0) == 0)
                return;
        }

        GL.IssuePluginEvent(_renderEventFunc, EVT_UPDATE);
#endif
    }

    // ---- Internal -----------------------------------------------------------

    // plugin event IDs (must match native)
    private const int EVT_CREATE_TEX = 1;
    private const int EVT_UPDATE = 2;
    private const int EVT_DESTROY = 3;

    // Shader property IDs (fast + typo-resistant)
    private static readonly int _PID_External = Shader.PropertyToID("_External");
    private static readonly int _PID_ExternalTex = Shader.PropertyToID("_ExternalTex");
    private static readonly int _PID_OESTex = Shader.PropertyToID("_OESTex");
    private static readonly int _PID_OESTexture = Shader.PropertyToID("_OESTexture");
    private static readonly int _PID_MainTex = Shader.PropertyToID("_MainTex");
    private static readonly int _PID_BaseMap = Shader.PropertyToID("_BaseMap");
    private static readonly int _PID_FlipY = Shader.PropertyToID("_FlipY");
    private static readonly int _PID_TexTransform = Shader.PropertyToID("_TexTransform");

#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject _surfaceTexture;
    private AndroidJavaObject _surface;
    private Texture2D _unityTex;
    private readonly float[] _surfaceTextureTransform = new float[16];
#endif

    private static IntPtr _renderEventFunc = IntPtr.Zero;

    private bool _ready;
    private bool _initializing;
    private Coroutine _initCo;

    private int _texId;

    // FrameAvailable gating:
    // 0 = no new frame pending, 1 = new frame pending.
    private static int _framePending = 0;

    // If frame listener registration fails, we fall back to "always update".
    private static bool _useFrameGate = true;

    private sealed class FrameListener : AndroidJavaProxy
    {
        public FrameListener() : base("android.graphics.SurfaceTexture$OnFrameAvailableListener") { }

        // Called on a Java thread chosen by SurfaceTexture (or handler thread if provided).
        public void onFrameAvailable(AndroidJavaObject st)
        {
            Interlocked.Exchange(ref _framePending, 1);
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            _renderEventFunc = DJI_GetRenderEventFunc();
            if (verboseLogs) Debug.Log($"[DJI] RenderEventFunc={_renderEventFunc}");
        }
        catch (Exception e)
        {
            Debug.LogError("[DJI] Failed to load native plugin: " + e);
            _renderEventFunc = IntPtr.Zero;
        }
#endif
    }

    private IEnumerator Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (backgroundMat == null)
        {
            Debug.LogError("[DJI] backgroundMat not assigned.");
            yield break;
        }
        if (_renderEventFunc == IntPtr.Zero)
        {
            Debug.LogError("[DJI] Native render event func pointer is null.");
            yield break;
        }

        _initCo = StartCoroutine(InitWhenFocused());
#endif
        yield break;
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private IEnumerator InitWhenFocused()
    {
        while (!Application.isFocused) yield return null;
        yield return InitVideoPipeline();
    }

    private IEnumerator InitVideoPipeline()
    {
        if (_initializing) yield break;
        _initializing = true;

        // Ensure old resources are gone
        TeardownVideoPipeline(stopJavaDecoder: true);

        // Give Unity one frame to ensure a valid GL context after resume/focus.
        yield return new WaitForEndOfFrame();

        // 1) Ask native to create OES texture on render thread
        DJI_BeginCreateOESTexture(streamWidth, streamHeight);
        GL.IssuePluginEvent(_renderEventFunc, EVT_CREATE_TEX);

        // 2) Wait until native created the texture
        _texId = 0;
        for (int i = 0; i < 180 && _texId == 0; i++)
        {
            yield return null;
            _texId = DJI_GetTextureId();
        }

        if (_texId == 0)
        {
            Debug.LogError("[DJI] Failed to create OES texture.");
            _initializing = false;
            yield break;
        }

        // 3) Create SurfaceTexture + Surface bound to that OES texture
        _surfaceTexture = new AndroidJavaObject("android.graphics.SurfaceTexture", _texId);
        _surfaceTexture.Call("setDefaultBufferSize", streamWidth, streamHeight);

        // Prefer gating updates by OnFrameAvailable.
        _useFrameGate = true;
        Interlocked.Exchange(ref _framePending, 0);

        bool listenerOk = TrySetFrameAvailableListener(_surfaceTexture);
        if (!listenerOk)
        {
            _useFrameGate = false;
            Debug.LogWarning("[DJI] OnFrameAvailable listener failed; fallback to always latch.");
        }

        // Native needs the SurfaceTexture jobject
        DJI_SetSurfaceTexture(_surfaceTexture.GetRawObject());

        // Java decoder needs a Surface wrapping the SurfaceTexture
        _surface = new AndroidJavaObject("android.view.Surface", _surfaceTexture);

        // 4) Tell Java decoder to render to this Surface
        using (var bridge = new AndroidJavaClass("com.sok9hu.djibridge.DJIUnityVideoBridge"))
        {
            bridge.CallStatic("setDecoderSurface", _surface, streamWidth, streamHeight);
        }

        // 5) Create/refresh Unity external texture wrapping the same OES texture id
        if (_unityTex == null)
        {
            _unityTex = Texture2D.CreateExternalTexture(
                streamWidth, streamHeight,
                TextureFormat.RGBA32,
                false, false,
                (IntPtr)_texId
            );
            _unityTex.name = "DJI_OES_ExternalTexture";
        }
        else
        {
            _unityTex.UpdateExternalTexture((IntPtr)_texId);
        }

        // 6) Bind it to the material’s *correct* property (External)
        ApplyToMaterial(backgroundMat);

        _ready = true;
        _initializing = false;

        if (verboseLogs)
            Debug.Log($"[DJI] Video pipeline ready. texId={_texId} frameGate={_useFrameGate} driveUpdateFromUpdate={driveUpdateFromUpdate}");
    }

    private bool TrySetFrameAvailableListener(AndroidJavaObject surfaceTexture)
    {
        try
        {
            var listener = new FrameListener();

            // Prefer overload with Handler (more reliable across devices):
            // setOnFrameAvailableListener(OnFrameAvailableListener, Handler) (API 21+)
            try
            {
                using (var looperClass = new AndroidJavaClass("android.os.Looper"))
                {
                    AndroidJavaObject mainLooper = looperClass.CallStatic<AndroidJavaObject>("getMainLooper");
                    using (var handler = new AndroidJavaObject("android.os.Handler", mainLooper))
                    {
                        surfaceTexture.Call("setOnFrameAvailableListener", listener, handler);
                        return true;
                    }
                }
            }
            catch
            {
                // Fallback to single-arg version
                surfaceTexture.Call("setOnFrameAvailableListener", listener);
                return true;
            }
        }
        catch (Exception e)
        {
            if (verboseLogs)
                Debug.LogWarning("[DJI] setOnFrameAvailableListener failed: " + e.Message);
            return false;
        }
    }

    private Matrix4x4 GetSurfaceTextureTransformMatrix()
    {
        try
        {
            DJI_GetSurfaceTextureTransform(_surfaceTextureTransform);
        }
        catch (Exception e)
        {
            if (verboseLogs)
                Debug.LogWarning("[DJI] DJI_GetSurfaceTextureTransform failed: " + e.Message);
            return Matrix4x4.identity;
        }

        var matrix = Matrix4x4.identity;
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                matrix[row, col] = _surfaceTextureTransform[col * 4 + row];
            }
        }

        return matrix;
    }

    private void Update()
    {
        // Legacy path: drive latching from Update().
        // Safe even if URP pass is also enabled, because TryLatchOnRenderThread() consumes the frame flag once.
        if (driveUpdateFromUpdate && _ready && _renderEventFunc != IntPtr.Zero)
        {
            TryLatchOnRenderThread();
        }

        // If some other script swaps material at runtime, keep it bound.
        // (cheap, safe; remove if you don't want it)
        if (_ready && backgroundMat != null && _unityTex != null)
        {
            // Ensure _External is set (some pipelines may recreate material instances)
            if (backgroundMat.HasProperty(_PID_External) && backgroundMat.GetTexture(_PID_External) != _unityTex)
                backgroundMat.SetTexture(_PID_External, _unityTex);
        }
    }

    private void OnApplicationPause(bool pause)
    {
        if (pause)
        {
            if (verboseLogs) Debug.Log("[DJI] OnApplicationPause(true) -> teardown");
            TeardownVideoPipeline(stopJavaDecoder: true);
        }
        else
        {
            if (verboseLogs) Debug.Log("[DJI] OnApplicationPause(false) -> re-init");
            RestartInit();
        }
    }

    private void OnApplicationFocus(bool focus)
    {
        if (!focus)
        {
            if (verboseLogs) Debug.Log("[DJI] OnApplicationFocus(false) -> teardown");
            TeardownVideoPipeline(stopJavaDecoder: true);
        }
        else
        {
            if (verboseLogs) Debug.Log("[DJI] OnApplicationFocus(true) -> re-init");
            if (!_ready && !_initializing)
                RestartInit();
        }
    }

    private void RestartInit()
    {
        if (_initCo != null) StopCoroutine(_initCo);
        _initCo = StartCoroutine(InitWhenFocused());
    }

    private void TeardownVideoPipeline(bool stopJavaDecoder)
    {
        _ready = false;
        _initializing = false;

        Interlocked.Exchange(ref _framePending, 0);

        if (stopJavaDecoder)
        {
            try
            {
                using (var bridge = new AndroidJavaClass("com.sok9hu.djibridge.DJIUnityVideoBridge"))
                {
                    bridge.CallStatic("stopVideo");
                }
            }
            catch (Exception e)
            {
                if (verboseLogs)
                    Debug.LogWarning("[DJI] stopVideo() failed (ok if not implemented): " + e.Message);
            }
        }

        try { DJI_ClearSurfaceTexture(); } catch { }

        try { _surface?.Call("release"); } catch { }
        try { _surface?.Dispose(); } catch { }
        _surface = null;

        try { _surfaceTexture?.Call("release"); } catch { }
        try { _surfaceTexture?.Dispose(); } catch { }
        _surfaceTexture = null;

        // IMPORTANT: delete/reset GL texture on render thread (handles context loss on resume)
        if (_renderEventFunc != IntPtr.Zero)
            GL.IssuePluginEvent(_renderEventFunc, EVT_DESTROY);

        _texId = 0;
    }
#endif

    private void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        TeardownVideoPipeline(stopJavaDecoder: true);
#endif
        if (Instance == this) Instance = null;
    }
}
