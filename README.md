# DJIUnity

Main Unity Android application that consumes the `DJIBridge` plugin.

---

## Project Role

`DJIUnity` is the app that runs on the Android phone. It:

- initializes the Android-side DJI plugin from Unity
- receives the external video texture from the native plugin bundled into `DJIBridge`
- renders the DJI live feed inside Unity

---

## Plugin Inputs

Expected Android plugins in:

```text
Assets/Plugins/Android/
```

Required file:

- `DJIUnityBridge.aar`

The `djiunity` native render plugin is now bundled inside `DJIUnityBridge.aar`, so the old standalone `DJIUnityNative-release.aar` should not be kept in the plugin folder.

---

## Android Package Name

The Unity Android package name is configured here:

- `Edit -> Project Settings -> Player -> Android -> Identification -> Package Name`

Current value in this workspace:

```text
com.sok9hu
```

This must match the package name registered for the DJI API key in the DJI developer portal.

If it does not match, DJI SDK registration fails and the app will not receive the video feed.

---

## DJI API Key Note

The DJI API key is not stored in Unity project files.

It is injected from the `DJIBridge` project during the Android AAR build, using:

- `DJIBridge/local.properties`
- or `DJI_API_KEY` from Gradle/environment

So before rebuilding the Unity app, make sure the `DJIBridge` AAR was built locally with a valid key.
