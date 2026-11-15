using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

public sealed class MobilePhoneArRemovalTests
{
    [Test]
    public void BuildSettings_ContainOnlyTheRemainingApplicationScenes()
    {
        var scenePaths = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Assets/Scenes/Boot.unity",
                "Assets/Scenes/DroneView.unity",
                "Assets/Scenes/SettingsScene.unity"
            },
            scenePaths);
    }

    [Test]
    public void PhoneArSceneAndReferenceAssets_AreAbsent()
    {
        Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>("Assets/Scenes/SampleScene.unity"), Is.Null);
        Assert.That(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/AR/ReferenceImages/BuildingReferenceImageLibrary.asset"), Is.Null);
        Assert.That(
            typeof(HomeScreenController).GetField(
                "PhoneArSceneName",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            Is.Null);
    }
}
