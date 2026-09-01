using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.ARSubsystems;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public static class ReferenceImageTrackingSetup
{
    private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
    private const string LibraryPath = "Assets/AR/ReferenceImages/BuildingReferenceImageLibrary.asset";
    private const string TargetTexturePath = "Assets/AR/ReferenceImages/BuildingReference.png";
    public const float TargetWidthMeters = 0.16f;

    [MenuItem("Tools/DJI/Configure Reference Image Tracking")]
    public static void Configure()
    {
        var library = ConfigureReferenceImageLibrary();
        var scene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
        var origin = Object.FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
        var mainCamera = Camera.main;
        if (origin == null || mainCamera == null)
            throw new MissingReferenceException("SampleScene must contain an XR Origin and Main Camera.");

        var trackedImageManager = GetOrAddComponent<ARTrackedImageManager>(origin.gameObject);
        trackedImageManager.referenceLibrary = library;
        trackedImageManager.requestedMaxNumberOfMovingImages = 0;
        trackedImageManager.trackedImagePrefab = null;
        trackedImageManager.enabled = true;

        var anchorManager = GetOrAddComponent<ARAnchorManager>(origin.gameObject);
        anchorManager.enabled = true;

        var oldPlacementController = origin.GetComponent<ARPlacementPrototypeController>();
        if (oldPlacementController != null)
            oldPlacementController.enabled = false;

        var cameraManager = GetOrAddComponent<ARCameraManager>(mainCamera.gameObject);
        cameraManager.enabled = true;
        cameraManager.autoFocusRequested = true;
        cameraManager.requestedFacingDirection = CameraFacingDirection.World;
        GetOrAddComponent<ARCameraBackground>(mainCamera.gameObject).enabled = true;

        var djiBackground = mainCamera.GetComponent<DJIGPUBackground>();
        if (djiBackground != null)
            djiBackground.enabled = false;

        var djiPoseDriver = mainCamera.GetComponent<DJICameraPoseDriver>();
        if (djiPoseDriver != null)
            djiPoseDriver.enabled = false;

        var canvas = GameObject.Find("AR Placement Canvas")?.GetComponent<Canvas>();
        var statusText = FindComponentByName<Text>("StatusText");
        var actionUi = ReferenceActionUi.FindOrCreate();
        var controller = GetOrAddComponent<ReferenceImageAnchorController>(origin.gameObject);
        controller.Configure(trackedImageManager, anchorManager, canvas, statusText, actionUi, TargetWidthMeters);
        controller.enabled = true;

        EditorUtility.SetDirty(origin.gameObject);
        EditorUtility.SetDirty(mainCamera.gameObject);
        EditorUtility.SetDirty(library);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[Reference Image] ARTrackedImageManager and BuildingReference library configured in SampleScene.");
    }

    private static XRReferenceImageLibrary ConfigureReferenceImageLibrary()
    {
        var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(LibraryPath);
        if (library == null)
        {
            library = ScriptableObject.CreateInstance<XRReferenceImageLibrary>();
            AssetDatabase.CreateAsset(library, LibraryPath);
        }

        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TargetTexturePath);
        if (texture == null)
            throw new MissingReferenceException($"Reference image texture is missing: {TargetTexturePath}");

        while (library.count > 0)
            library.RemoveAt(0);

        library.Add();
        library.SetTexture(0, texture, keepTexture: true);
        library.SetName(0, "BuildingReference");
        library.SetSpecifySize(0, true);
        library.SetSize(0, new Vector2(TargetWidthMeters, TargetWidthMeters));
        return library;
    }

    private static T FindComponentByName<T>(string objectName) where T : Component
    {
        var gameObject = GameObject.Find(objectName);
        return gameObject != null ? gameObject.GetComponent<T>() : null;
    }

    private static T GetOrAddComponent<T>(GameObject gameObject) where T : Component
    {
        var component = gameObject.GetComponent<T>();
        return component != null ? component : gameObject.AddComponent<T>();
    }
}
