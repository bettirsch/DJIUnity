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

The tracked `Assets/Plugins/Android/DJIUnityBridge.aar` contains the
bridge and its native plugin, but no key. When building an Android application,
provide `DJI_API_KEY` in the environment before starting Unity, or set it in
the local Gradle user `gradle.properties`. The launcher resolves the manifest
placeholder during packaging. Keep the key out of both repositories.

See [development validation](Docs/DevelopmentValidation.md) for the bridge
build and optional replay test inputs.

Use `CommandLineAndroidBuild.BuildApk` for the complete normal Release application.
The accepted AprilTag/reacquisition policies and Android wall shadow no longer
require candidate defines. Native multi-start is mandatory and the AAR is checked
before packaging. See [final build configuration](Docs/FINAL_BUILD_CONFIGURATION.md).
