using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public sealed class DjiScreenshotCaptureGateTests
{
    [Test]
    public void ConcurrentRequestsAreDroppedAndFailureReleasesTheSlot()
    {
        var gate = new DjiScreenshotCaptureGate();
        var first = gate.TryBegin();
        Assert.That(first, Is.Positive);
        Assert.That(gate.TryBegin(), Is.Zero);
        gate.Finish(first);
        Assert.That(gate.TryBegin(), Is.Positive);
    }

    [Test]
    public void LateReadbackCannotUnlockAnotherCapture()
    {
        var gate = new DjiScreenshotCaptureGate();
        var first = gate.TryBegin();
        gate.Finish(first);
        var second = gate.TryBegin();
        Assert.That(second, Is.Positive.And.Not.EqualTo(first));
        gate.Finish(first);
        Assert.That(gate.TryBegin(), Is.Zero);
        gate.Finish(second);
        Assert.That(gate.TryBegin(), Is.Positive);
    }

    [TestCase(0)]
    [TestCase(1)]
    public void DroneViewListensAutomaticallyAfterStartupAndReentry(int oldPreference)
    {
        const string key = "DroneView.RcScreenshot";
        var hadPreference = PlayerPrefs.HasKey(key);
        var previous = PlayerPrefs.GetInt(key);
        var root = new GameObject("First DroneView");
        var reopened = new GameObject("Reopened DroneView");
        try
        {
            PlayerPrefs.SetInt(key, oldPreference);
            var requests = new List<bool>();
            var first = CreateScreenshot(root);
            Assert.That(PlayerPrefs.HasKey(key), Is.False);
            InstallListener(first, requests);
            Invoke(first, "OnEnable");
            Assert.That(requests, Is.EqualTo(new[] { Application.isFocused }));
            Invoke(first, "OnApplicationFocus", true);
            Invoke(first, "OnApplicationPause", true);
            Invoke(first, "OnApplicationPause", false);
            Invoke(first, "OnApplicationFocus", false);
            Invoke(first, "OnDisable");
            Assert.That(requests, Is.EqualTo(new[] { Application.isFocused, true, false, true, false, false }));

            var second = CreateScreenshot(reopened);
            requests.Clear();
            InstallListener(second, requests);
            Invoke(second, "OnEnable");
            Invoke(second, "OnApplicationFocus", true);
            Invoke(second, "OnDisable");
            Assert.That(requests, Is.EqualTo(new[] { Application.isFocused, true, false }));
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(reopened);
            if (hadPreference) PlayerPrefs.SetInt(key, previous);
            else PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }
    }

    [Test]
    public void ScreenshotHudHasOnlyANonInteractiveResultNotice()
    {
        const string key = "DroneView.RcScreenshot";
        var hadPreference = PlayerPrefs.HasKey(key);
        var previous = PlayerPrefs.GetInt(key);
        var root = new GameObject("DroneView screenshot HUD");
        try
        {
            var screenshot = CreateScreenshot(root);
            var safe = root.transform.Find("Screenshot Canvas/Safe Area");
            Assert.That(safe.childCount, Is.EqualTo(1));
            Assert.That(root.GetComponentsInChildren<Button>(true), Is.Empty);
            Assert.That(root.GetComponentsInChildren<GraphicRaycaster>(true), Is.Empty);
            var panel = safe.Find("Screenshot Result");
            Assert.That(panel.gameObject.activeSelf, Is.False);
            Assert.That(panel.GetComponent<RectTransform>().anchoredPosition, Is.EqualTo(new Vector2(24, 24)));
            Invoke(screenshot, "ShowNotice", "Saved to Gallery", true);
            var label = panel.GetComponentInChildren<Text>();
            Assert.That(label.text, Is.EqualTo("Saved to Gallery"));
            Assert.That(label.raycastTarget || panel.GetComponent<Image>().raycastTarget, Is.False);
            Invoke(screenshot, "ShowNotice", "Could not save photo", false);
            Assert.That(label.text, Is.EqualTo("Could not save photo"));
        }
        finally
        {
            Object.DestroyImmediate(root);
            if (hadPreference) PlayerPrefs.SetInt(key, previous);
            else PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static DjiDroneViewScreenshot CreateScreenshot(GameObject root)
    {
        var screenshot = root.AddComponent<DjiDroneViewScreenshot>();
        Invoke(screenshot, "Awake");
        return screenshot;
    }

    private static void InstallListener(DjiDroneViewScreenshot screenshot, List<bool> requests) =>
        typeof(DjiDroneViewScreenshot).GetField("_setRcListening", PrivateInstance)
            .SetValue(screenshot, (System.Action<bool>)requests.Add);

    private static void Invoke(DjiDroneViewScreenshot screenshot, string method, params object[] args) =>
        typeof(DjiDroneViewScreenshot).GetMethod(method, PrivateInstance).Invoke(screenshot, args);
}
