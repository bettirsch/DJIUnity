using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public sealed class SettingsModelPickerTests
{
    private const string Preference = "SelectedARContent";
    private bool _hadPreference;
    private int _previousPreference;

    [SetUp]
    public void PreservePreference()
    {
        _hadPreference = PlayerPrefs.HasKey(Preference);
        _previousPreference = PlayerPrefs.GetInt(Preference);
    }

    [TearDown]
    public void RestorePreference()
    {
        if (_hadPreference) PlayerPrefs.SetInt(Preference, _previousPreference);
        else PlayerPrefs.DeleteKey(Preference);
        PlayerPrefs.Save();
    }

    [Test]
    public void MissingPreferenceDefaultsToCube()
    {
        PlayerPrefs.DeleteKey(Preference);
        Assert.That(ARContentSelection.Selected, Is.EqualTo(ARContentType.Cube));
    }

    [TestCase(1)]
    [TestCase(99)]
    public void RetiredOrInvalidPreferenceIsRewrittenToCube(int stored)
    {
        PlayerPrefs.SetInt(Preference, stored);
        PlayerPrefs.Save();
        Assert.That(ARContentSelection.Selected, Is.EqualTo(ARContentType.Cube));
        Assert.That(PlayerPrefs.GetInt(Preference), Is.Zero);
        Assert.That(ARContentSelection.Selected, Is.EqualTo(ARContentType.Cube));
    }

    [TestCase(0, ARContentType.Cube, "DummyCube")]
    [TestCase(2, ARContentType.RoofCanopy, "RoofCanopyPlacement")]
    public void OldSupportedIdsStillLoadSameDroneViewContent(int stored, ARContentType type, string expectedRoot)
    {
        PlayerPrefs.SetInt(Preference, stored);
        Assert.That(ARContentSelection.Selected, Is.EqualTo(type));
        Assert.That(PlayerPrefs.GetInt(Preference), Is.EqualTo(stored));
        var root = new GameObject("DroneView content root");
        Material ownedMaterial = null;
        try
        {
            var selected = ARContentFactory.CreateSelected(root.transform);
            Assert.That(selected.name, Is.EqualTo(expectedRoot));
            if (type == ARContentType.Cube) ownedMaterial = selected.GetComponent<Renderer>().sharedMaterial;
            else Assert.That(selected.transform.Find("BoardRoofCanopy"), Is.Not.Null);
        }
        finally
        {
            Object.DestroyImmediate(root);
            if (ownedMaterial != null) Object.DestroyImmediate(ownedMaterial);
        }
    }

    [Test]
    public void CardsShowTheMatchingImagesAndRestoreTheSavedSelectionWhenReopened()
    {
        ARContentSelection.Set(ARContentType.Cube);
        var first = BuildMenu();
        try
        {
            var row = first.transform.Find("Settings Canvas/Safe Area/Model cards");
            Assert.That(row.childCount, Is.EqualTo(2));
            var cube = AssertCard(row.Find("CubeButton"), "Kocka", "Cube", true);
            var roof = AssertCard(row.Find("RoofCanopyButton"), "El\u0151tet\u0151", "RoofCanopy", false);
            roof.onClick.Invoke();
            Assert.That(PlayerPrefs.GetInt(Preference), Is.EqualTo(2));
            Assert.That(IsSelected(cube.transform), Is.False);
            Assert.That(IsSelected(roof.transform), Is.True);
            Assert.That(cube.GetComponent<Image>().color, Is.Not.EqualTo(roof.GetComponent<Image>().color));
        }
        finally { Object.DestroyImmediate(first); }

        var reopened = BuildMenu();
        try
        {
            var row = reopened.transform.Find("Settings Canvas/Safe Area/Model cards");
            AssertCard(row.Find("CubeButton"), "Kocka", "Cube", false);
            AssertCard(row.Find("RoofCanopyButton"), "El\u0151tet\u0151", "RoofCanopy", true);
            row.Find("CubeButton").GetComponent<Button>().onClick.Invoke();
            Assert.That(PlayerPrefs.GetInt(Preference), Is.Zero);
        }
        finally { Object.DestroyImmediate(reopened); }
    }

    private static GameObject BuildMenu()
    {
        var root = new GameObject("Settings");
        var controller = root.AddComponent<SettingsSceneController>();
        typeof(SettingsSceneController).GetMethod("BuildUi", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(controller, new object[] { ARContentSelection.Selected });
        return root;
    }

    private static Button AssertCard(Transform card, string name, string image, bool selected)
    {
        Assert.That(card, Is.Not.Null);
        var preview = card.Find("Preview").GetComponent<RawImage>();
        Assert.That(preview.texture, Is.Not.Null);
        Assert.That(preview.texture.name, Is.EqualTo(image));
        Assert.That(preview.texture.width, Is.EqualTo(2 * preview.texture.height));
        var label = card.Find("Model name").GetComponent<Text>();
        Assert.That(label.text, Is.EqualTo(name));
        Assert.That(preview.raycastTarget || label.raycastTarget, Is.False);
        Assert.That(ExecuteEvents.GetEventHandler<IPointerClickHandler>(preview.gameObject), Is.EqualTo(card.gameObject));
        Assert.That(ExecuteEvents.GetEventHandler<IPointerClickHandler>(label.gameObject), Is.EqualTo(card.gameObject));
        Assert.That(IsSelected(card), Is.EqualTo(selected));
        return card.GetComponent<Button>();
    }

    private static bool IsSelected(Transform card) => card.Find("Selected badge").gameObject.activeSelf;
}
