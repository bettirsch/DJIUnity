using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class DroneViewStartupDefaultsTests
{
    [Test]
    public void ProviderAwake_AppliesDeterministicDroneViewModes()
    {
        var gameObject = new GameObject("DroneViewStartupDefaultsTest");
        try
        {
            var provider = gameObject.AddComponent<DjiBoardVisionProvider>();

            AssertDeterministicModes(provider);
        }
        finally
        {
            Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void ReentryDefaults_ReplaceChangedDevelopmentModes()
    {
        var gameObject = new GameObject("DroneViewStartupReentryTest");
        try
        {
            var provider = gameObject.AddComponent<DjiBoardVisionProvider>();
            provider.SetHorizontalTranslationRenderMode(HorizontalTranslationRenderMode.GuardedGnssRendered);
            provider.SetVisualTemporalCompensationMode(VisualTemporalCompensationMode.Enabled);
            provider.SetVisualRenderPath(VisualRenderPath.LegacyBoardPath);
            SetPrivateField(
                provider,
                "rotationPropagationMode",
                DjiBoardVisionProvider.RotationPropagationMode.Disabled);

            provider.ApplyDroneViewStartupDefaults();

            AssertDeterministicModes(provider);
        }
        finally
        {
            Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void StartupDefaults_PreserveSettingsContentSelection()
    {
        var original = ARContentSelection.Selected;
        var gameObject = new GameObject("DroneViewContentSelectionPreservationTest");
        try
        {
            ARContentSelection.Set(ARContentType.RoofCanopy);
            var provider = gameObject.AddComponent<DjiBoardVisionProvider>();

            provider.ApplyDroneViewStartupDefaults();

            Assert.That(ARContentSelection.Selected, Is.EqualTo(ARContentType.RoofCanopy));
        }
        finally
        {
            ARContentSelection.Set(original);
            Object.DestroyImmediate(gameObject);
        }
    }

    private static void AssertDeterministicModes(DjiBoardVisionProvider provider)
    {
        var providerType = typeof(DjiBoardVisionProvider);
        const BindingFlags privateFields = BindingFlags.Instance | BindingFlags.NonPublic;
        Assert.That(providerType.GetField("_validatedTrackingSafetyEnabled", privateFields).GetValue(provider), Is.True);
        Assert.That(providerType.GetField("allowUnvalidatedRotationPropagationForDeveloperTesting", privateFields), Is.Null,
            "Normal rotation propagation must not depend on a serialized developer override.");
        Assert.That(
            provider.CurrentHorizontalTranslationRenderMode,
            Is.EqualTo(HorizontalTranslationRenderMode.ShadowOnly));
        Assert.That(
            provider.CurrentVisualTemporalCompensationMode,
            Is.EqualTo(VisualTemporalCompensationMode.Disabled));
        Assert.That(
            provider.CurrentVisualRenderPath,
            Is.EqualTo(VisualRenderPath.PersistentWorldPath));
        Assert.That(
            provider.CurrentRotationPropagationMode,
            Is.EqualTo(DjiBoardVisionProvider.RotationPropagationMode.Enabled));
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
        field.SetValue(target, value);
    }
}
