using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.XR.CoreUtils;

public static class ReferenceImageTrackingDiagnostics
{
    private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
    private const string LibraryPath = "Assets/AR/ReferenceImages/BuildingReferenceImageLibrary.asset";
    private const string TargetTexturePath = "Assets/AR/ReferenceImages/BuildingReference.png";
    private const string TargetName = "BuildingReference";
    private const float ExpectedWidthMeters = ReferenceBoardDefinition.PhysicalWidthMeters;

    [MenuItem("Tools/DJI/Validate Reference Image Tracking Setup")]
    public static void Validate()
    {
        var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(LibraryPath);
        var targetTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(TargetTexturePath);
        Debug.Log($"[Reference Image] EDITOR_LIBRARY assigned={library != null} count={library?.count ?? 0} targetTextureExists={targetTexture != null} targetTexturePath={TargetTexturePath}");

        var targetFound = false;
        var textureMatches = false;
        var physicalWidthMatches = false;
        if (library != null)
        {
            for (var index = 0; index < library.count; index++)
            {
                var image = library[index];
                var matchesTarget = image.name == TargetName;
                targetFound |= matchesTarget;
                if (matchesTarget)
                {
                    textureMatches = image.texture == targetTexture;
                    physicalWidthMatches = Mathf.Approximately(image.size.x, ExpectedWidthMeters) && Mathf.Approximately(image.size.y, ExpectedWidthMeters);
                }

                Debug.Log($"[Reference Image] EDITOR_LIBRARY_IMAGE index={index} name={image.name} size={image.size} specifiedSize={image.specifySize} texture={image.texture?.name} matchesBuildingReference={matchesTarget}");
            }
        }

        Debug.Log($"[Reference Image] EDITOR_LIBRARY_RESULT buildingReferenceExists={targetFound} referencedTextureMatchesGeneratedPng={textureMatches} physicalWidthIs180mm={physicalWidthMatches}");

        var scene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
        var origins = Object.FindObjectsByType<XROrigin>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var managers = Object.FindObjectsByType<ARTrackedImageManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var controllers = Object.FindObjectsByType<ReferenceImageAnchorController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Debug.Log($"[Reference Image] EDITOR_SCENE name={scene.name} path={scene.path} buildSceneIncluded={IsBuildSceneEnabled(SampleScenePath)} xrOriginCount={origins.Length} trackedImageManagerCount={managers.Length} controllerCount={controllers.Length}");

        foreach (var manager in managers)
            Debug.Log($"[Reference Image] EDITOR_MANAGER object={manager.name} active={manager.gameObject.activeInHierarchy} enabled={manager.enabled} libraryMatches={manager.referenceLibrary == library} requestedMaxMovingImages={manager.requestedMaxNumberOfMovingImages}");

        foreach (var controller in controllers)
        {
            var managerMatches = controller.ConfiguredTrackedImageManager != null && managers.Length == 1 && controller.ConfiguredTrackedImageManager == managers[0];
            Debug.Log($"[Reference Image] EDITOR_CONTROLLER object={controller.name} active={controller.gameObject.activeInHierarchy} enabled={controller.enabled} managerMatchesSceneManager={managerMatches} targetName={controller.TargetReferenceImageName} targetNameExact={controller.TargetReferenceImageName == TargetName} configuredWidth={controller.ConfiguredImageWidthMeters:F3}");
        }

        if (origins.Length != 1 || managers.Length != 1 || controllers.Length != 1 || !targetFound || !textureMatches || !physicalWidthMatches)
            Debug.LogError("[Reference Image] EDITOR_VALIDATION_FAILED Fix the reported scene/library wiring before testing on device.");
        else
            Debug.Log("[Reference Image] EDITOR_VALIDATION_PASSED Scene and serialized reference wiring are valid. A fresh Android Build And Run is still required after image/library changes.");
    }

    private static bool IsBuildSceneEnabled(string scenePath)
    {
        foreach (var scene in EditorBuildSettings.scenes)
        {
            if (scene.enabled && scene.path == scenePath)
                return true;
        }

        return false;
    }
}
