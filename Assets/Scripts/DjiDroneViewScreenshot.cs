using System;
using System.Collections;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

public sealed class DjiScreenshotCaptureGate
{
    private long _next;
    private long _owner;

    public long TryBegin()
    {
        var token = Interlocked.Increment(ref _next);
        return Interlocked.CompareExchange(ref _owner, token, 0) == 0 ? token : 0;
    }

    public void Finish(long token)
    {
        if (token != 0) Interlocked.CompareExchange(ref _owner, 0, token);
    }
}

[DisallowMultipleComponent]
public sealed class DjiDroneViewScreenshot : MonoBehaviour
{
    private static readonly DjiScreenshotCaptureGate CaptureGate = new();
    private const string RetiredRcPreference = "DroneView.RcScreenshot";
    private const string StoragePermission = "android.permission.WRITE_EXTERNAL_STORAGE";
    private const int MaxPixels = 8_847_360;

    [Serializable]
    private sealed class RcPress
    {
        public string key;
        public bool down;
        public long timestampNs;
        public string cameraMode;
        public long generation;
    }

    [Serializable]
    private sealed class GalleryResult
    {
        public string status;
    }

    private sealed class Capture
    {
        public string id;
        public long token;
        public long requestedNs;
        public bool cancelled;
        public Coroutine wait;
    }

    private DJIGPUBackground _video;
    private DjiSharedReferenceCubeOverlay _overlay;
    private Canvas _canvas;
    private RectTransform _safe;
    private Text _notice;
    private GameObject _noticePanel;
    private Rect _lastArea;
    private Vector2Int _lastSize;
    private bool _hasFocus;
    private bool _paused;
    private float _nextPoll;
    private float _noticeUntil;
    private Capture _capture;
    private string _savingId;
    private AndroidJavaClass _gallery;
    private AndroidJavaClass _rc;
    private Action<bool> _setRcListening;
#if UNITY_ANDROID && !UNITY_EDITOR
    private bool _permissionPending;
#endif

    public void Configure(DJIGPUBackground video, DjiSharedReferenceCubeOverlay overlay)
    {
        _video = video;
        _overlay = overlay;
    }

    private void Awake()
    {
        if (PlayerPrefs.HasKey(RetiredRcPreference))
        {
            PlayerPrefs.DeleteKey(RetiredRcPreference);
            PlayerPrefs.Save();
        }
        BuildNotice();
    }

    private void OnEnable()
    {
        _canvas.enabled = true;
        _hasFocus = Application.isFocused;
        _paused = false;
#if UNITY_ANDROID && !UNITY_EDITOR
        try { _gallery = new AndroidJavaClass("com.sok9hu.djibridge.DjiScreenshotGallery"); }
        catch (Exception e) { Debug.LogWarning($"DJI_SCREENSHOT_BRIDGE result=GALLERY_UNAVAILABLE type={e.GetType().Name}"); }
        try
        {
            _rc = new AndroidJavaClass("com.sok9hu.djibridge.DjiScreenshotRcBridge");
            _setRcListening = enabled => _rc.CallStatic("setEnabled", enabled);
        }
        catch (Exception e) { Debug.LogWarning($"DJI_SCREENSHOT_BRIDGE result=RC_UNAVAILABLE type={e.GetType().Name}"); }
#endif
        SyncRc();
    }

    private void OnDisable()
    {
        _canvas.enabled = false;
        CancelUnsubmittedCapture("SCENE_DISABLED");
        SetRcListening(false);
        _setRcListening = null;
        _rc?.Dispose();
        _gallery?.Dispose();
        _rc = null;
        _gallery = null;
        // An already submitted gallery write owns its bytes and can finish without this scene.
        _savingId = null;
    }

    private void OnApplicationFocus(bool focused)
    {
        _hasFocus = focused;
        if (!focused) CancelUnsubmittedCapture("FOCUS_LOST");
        SyncRc();
    }

    private void OnApplicationPause(bool paused)
    {
        _paused = paused;
        if (paused) CancelUnsubmittedCapture("PAUSED");
        SyncRc();
    }

    private bool InForeground => isActiveAndEnabled && _hasFocus && !_paused;

    private void SyncRc() => SetRcListening(InForeground);

    private void SetRcListening(bool enabled)
    {
        try { _setRcListening?.Invoke(enabled); }
        catch (Exception e) { Debug.LogWarning($"DJI_SCREENSHOT_RC result=LISTENER_BRIDGE_ERROR type={e.GetType().Name}"); }
    }

    private void Update()
    {
        UpdateSafeArea();
        if (_noticePanel.activeSelf && Time.unscaledTime >= _noticeUntil)
            _noticePanel.SetActive(false);
        if (!InForeground || Time.unscaledTime < _nextPoll)
            return;
        _nextPoll = Time.unscaledTime + .1f;
        if (_savingId != null) PollSave();
        if (_rc == null) return;
        try
        {
            var json = _rc.CallStatic<string>("consumePressJson");
            if (string.IsNullOrEmpty(json)) return;
            var press = JsonUtility.FromJson<RcPress>(json);
            if (press != null && press.down) Request(press);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"DJI_SCREENSHOT_RC result=POLL_ERROR type={e.GetType().Name}");
            SetRcListening(false);
            _setRcListening = null;
            _rc.Dispose();
            _rc = null;
        }
    }

    private void PollSave()
    {
        var id = _savingId;
        try
        {
            var json = _gallery.CallStatic<string>("getResultJson", id);
            if (string.IsNullOrEmpty(json)) return;
            _savingId = null;
            var status = JsonUtility.FromJson<GalleryResult>(json)?.status;
            Debug.Log($"DJI_SCREENSHOT_RESULT id={id} timestampNs={DjiBoardVisionBridge.GetMonotonicTimestampNs()} " +
                $"result={status ?? "UNKNOWN"} {VideoState()}");
            ShowNotice(status == "SAVED" ? "Saved to Gallery" : "Could not save photo", status == "SAVED");
        }
        catch (Exception e)
        {
            _savingId = null;
            Debug.LogWarning($"DJI_SCREENSHOT_RESULT id={id} result=RESULT_BRIDGE_ERROR type={e.GetType().Name}");
            ShowNotice("Could not verify photo", false);
        }
    }

    private void Request(RcPress press)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DjiBoardVisionBridge.GetMonotonicTimestampNs();
        var requested = press.timestampNs > 0 ? press.timestampNs : now;
        try
        {
            var mode = press.cameraMode;
            if (string.IsNullOrEmpty(mode) || mode == "UNKNOWN")
                mode = _rc?.CallStatic<string>("getCameraMode") ?? "UNKNOWN";
            var session = FindFirstObjectByType<DjiTelemetryValidationLogger>()?.TelemetrySessionId ?? "-";
            Debug.Log($"DJI_SCREENSHOT_REQUEST id={id} source=RC " +
                $"rcKey={press.key ?? "-"} rcDown={press.down} rcGeneration={press.generation} " +
                $"cameraMode={mode} timestampNs={requested} receivedNs={now} session={session} {VideoState()}");
        }
        catch (Exception e)
        {
            Debug.Log($"DJI_SCREENSHOT_REQUEST id={id} source=RC " +
                $"timestampNs={requested} metadataUnavailable={e.GetType().Name}");
        }

        if (_gallery == null) { Reject(id, "PLATFORM_UNAVAILABLE", "Camera capture unavailable"); return; }
        if (!InForeground || _video == null || !_video.IsReady)
        {
            Reject(id, "VIDEO_UNAVAILABLE", "Live video unavailable");
            return;
        }
        try
        {
            if (_savingId != null || _capture != null || _gallery.CallStatic<bool>("isBusy"))
            {
                Reject(id, "BUSY", "Saving photo...");
                return;
            }
            if (!CheckPermission(id)) return;
            var token = CaptureGate.TryBegin();
            if (token == 0) { Reject(id, "CAPTURE_BUSY", "Saving photo..."); return; }
            var capture = new Capture { id = id, token = token, requestedNs = requested };
            _capture = capture;
            _noticePanel.SetActive(false);
            capture.wait = StartCoroutine(CaptureAtEndOfFrame(capture));
        }
        catch (Exception e)
        {
            if (_capture?.id == id) FinishCapture(_capture);
            Reject(id, $"REQUEST_{e.GetType().Name}", "Could not capture photo");
        }
    }

    private bool CheckPermission(string id)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!_gallery.CallStatic<bool>("needsLegacyWritePermission") ||
            Permission.HasUserAuthorizedPermission(StoragePermission))
            return true;

        if (_permissionPending) return false;
        Reject(id, "PERMISSION_REQUIRED", "Gallery permission required");
        _permissionPending = true;
        var callbacks = new PermissionCallbacks();
        callbacks.PermissionGranted += _ => PermissionResult(id, true);
        callbacks.PermissionDenied += _ => PermissionResult(id, false);
        try { Permission.RequestUserPermission(StoragePermission, callbacks); }
        catch { _permissionPending = false; throw; }
        return false;
#else
        return false;
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void PermissionResult(string id, bool granted)
    {
        _permissionPending = false;
        Debug.Log($"DJI_SCREENSHOT_PERMISSION id={id} granted={granted}");
        if (this != null && isActiveAndEnabled)
            ShowNotice(granted ? "Permission granted. Press RC again." : "Gallery permission denied", granted);
    }
#endif

    private IEnumerator CaptureAtEndOfFrame(Capture capture, bool syncFallback = false)
    {
        yield return new WaitForEndOfFrame();
        capture.wait = null;
        if (capture.cancelled || !InForeground || _video == null || !_video.IsReady)
        {
            FinishCapture(capture);
            if (!capture.cancelled) Reject(capture.id, "VIDEO_UNAVAILABLE", "Live video unavailable");
            yield break;
        }
        var width = Screen.width;
        var height = Screen.height;
        if (width < 1 || height < 1 || (long)width * height > MaxPixels)
        {
            FinishCapture(capture);
            Reject(capture.id, "UNSUPPORTED_SIZE", "Unsupported screenshot size");
            yield break;
        }
        RenderTexture buffer = null;
        try
        {
            var startNs = DjiBoardVisionBridge.GetMonotonicTimestampNs();
            Debug.Log($"DJI_SCREENSHOT_FRAME id={capture.id} timestampNs={startNs} width={width} height={height} " +
                $"orientation={Screen.orientation} unityFrame={Time.frameCount} {VideoState()}");
            buffer = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            ScreenCapture.CaptureScreenshotIntoRenderTexture(buffer);
            if (SystemInfo.supportsAsyncGPUReadback && !syncFallback)
            {
                var owned = buffer;
                AsyncGPUReadback.Request(buffer, 0, TextureFormat.RGBA32,
                    result => ReadbackFinished(capture, owned, width, height, startNs, result));
                buffer = null; // The callback now owns the temporary GPU target.
            }
            else Submit(capture, width, height, ReadPixels(buffer), startNs, "SYNC");
        }
        catch (Exception e)
        {
            FinishCapture(capture);
            Reject(capture.id, $"CAPTURE_{e.GetType().Name}", "Could not capture photo");
        }
        finally
        {
            if (buffer != null) RenderTexture.ReleaseTemporary(buffer);
        }
    }

    private void ReadbackFinished(Capture capture, RenderTexture buffer, int width, int height, long startNs, AsyncGPUReadbackRequest result)
    {
        try
        {
            if (capture.cancelled || this == null || !InForeground)
            {
                FinishCapture(capture);
                return;
            }
            if (result.hasError)
            {
                Debug.LogWarning($"DJI_SCREENSHOT_READBACK id={capture.id} result=ASYNC_FALLBACK");
                capture.wait = StartCoroutine(CaptureAtEndOfFrame(capture, syncFallback: true));
                return;
            }
            Submit(capture, width, height, result.GetData<sbyte>().ToArray(), startNs, "ASYNC");
        }
        catch (Exception e)
        {
            FinishCapture(capture);
            if (!capture.cancelled) Reject(capture.id, $"READBACK_{e.GetType().Name}", "Could not capture photo");
        }
        finally { RenderTexture.ReleaseTemporary(buffer); }
    }

    private static sbyte[] ReadPixels(RenderTexture buffer)
    {
        var old = RenderTexture.active;
        Texture2D pixels = null;
        try
        {
            RenderTexture.active = buffer;
            pixels = new Texture2D(buffer.width, buffer.height, TextureFormat.RGBA32, false);
            pixels.ReadPixels(new Rect(0, 0, buffer.width, buffer.height), 0, 0, false);
            return pixels.GetRawTextureData<sbyte>().ToArray();
        }
        finally
        {
            RenderTexture.active = old;
            if (pixels != null) Destroy(pixels);
        }
    }

    private void Submit(Capture capture, int width, int height, sbyte[] rgba, long startNs, string path)
    {
        try
        {
            var readNs = DjiBoardVisionBridge.GetMonotonicTimestampNs();
            var status = _gallery.CallStatic<string>("trySave", capture.id, width, height, rgba, capture.requestedNs);
            var submittedNs = DjiBoardVisionBridge.GetMonotonicTimestampNs();
            Debug.Log($"DJI_SCREENSHOT_SUBMIT id={capture.id} result={status} path={path} " +
                $"width={width} height={height} timestampNs={submittedNs} captureMs={(readNs - startNs) / 1e6:F2} " +
                $"jniMs={(submittedNs - readNs) / 1e6:F2} {VideoState()}");
            if (status == "QUEUED") _savingId = capture.id;
            else Reject(capture.id, status, status == "BUSY" ? "Saving photo..." : "Could not save photo");
        }
        finally { FinishCapture(capture); }
    }

    private void FinishCapture(Capture capture)
    {
        CaptureGate.Finish(capture.token);
        if (ReferenceEquals(_capture, capture)) _capture = null;
    }

    private void CancelUnsubmittedCapture(string reason)
    {
        var capture = _capture;
        if (capture == null) return;
        capture.cancelled = true;
        _capture = null;
        if (capture.wait != null)
        {
            StopCoroutine(capture.wait);
            FinishCapture(capture);
        }
        Debug.Log($"DJI_SCREENSHOT_RESULT id={capture.id} result={reason}");
    }

    private string VideoState()
    {
        var authority = _overlay != null ? _overlay.TrackingRenderPoseSource : DjiRenderPoseSource.None;
        var state = $"videoReady={(_video != null && _video.IsReady)} authority={authority}";
        try
        {
            var available = DJIGPUBackground.TryGetLatestDisplayedFrameTiming(out var timing);
            return state + $" oesAvailable={available} oesArrivalSequence={(available ? timing.oesFrameAvailableSequence : 0)} " +
                $"oesArrivalNs={(available ? timing.oesFrameAvailableTimestampNs : 0)} " +
                $"oesLatchSequence={(available ? timing.oesLatchSequence : 0)} " +
                $"oesLatchNs={(available ? timing.oesLatchTimestampNs : 0)}";
        }
        catch (Exception) { return state + " oesAvailable=false"; }
    }

    private void Reject(string id, string reason, string notice)
    {
        Debug.Log($"DJI_SCREENSHOT_RESULT id={id} timestampNs={DjiBoardVisionBridge.GetMonotonicTimestampNs()} result={reason}");
        ShowNotice(notice, false);
    }

    private void ShowNotice(string notice, bool success)
    {
        _notice.text = notice;
        _notice.color = success ? new Color32(176, 242, 185, 255) : new Color32(255, 203, 112, 255);
        _noticeUntil = Time.unscaledTime + 3f;
        _noticePanel.SetActive(true);
    }

    private void BuildNotice()
    {
        var root = new GameObject("Screenshot Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        root.transform.SetParent(transform, false);
        _canvas = root.GetComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 3002;
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = .5f;
        _safe = new GameObject("Safe Area", typeof(RectTransform)).GetComponent<RectTransform>();
        _safe.SetParent(root.transform, false);

        _noticePanel = new GameObject("Screenshot Result", typeof(RectTransform), typeof(Image));
        _noticePanel.transform.SetParent(_safe, false);
        var panelRect = _noticePanel.GetComponent<RectTransform>();
        panelRect.anchorMin = panelRect.anchorMax = panelRect.pivot = Vector2.zero;
        panelRect.anchoredPosition = new Vector2(24, 24);
        panelRect.sizeDelta = new Vector2(435, 48);
        var background = _noticePanel.GetComponent<Image>();
        background.color = new Color(.015f, .07f, .1f, .88f);
        background.raycastTarget = false;

        var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(_noticePanel.transform, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        _notice = go.GetComponent<Text>();
        _notice.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _notice.fontStyle = FontStyle.Bold;
        _notice.fontSize = 21;
        _notice.alignment = TextAnchor.MiddleCenter;
        _notice.raycastTarget = false;
        _noticePanel.SetActive(false);
        UpdateSafeArea();
    }

    private void UpdateSafeArea()
    {
        var area = Screen.safeArea;
        var size = new Vector2Int(Screen.width, Screen.height);
        if (_safe == null || size.x <= 0 || size.y <= 0 || (_lastArea == area && _lastSize == size)) return;
        _lastArea = area;
        _lastSize = size;
        _safe.anchorMin = new Vector2(area.xMin / size.x, area.yMin / size.y);
        _safe.anchorMax = new Vector2(area.xMax / size.x, area.yMax / size.y);
        _safe.offsetMin = _safe.offsetMax = Vector2.zero;
    }
}
