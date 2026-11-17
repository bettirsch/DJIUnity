using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class CommandLineAndroidBuild
{
    public static void BuildApk()
    {
        BuildAndroidPlayer(BuildOptions.None);
    }

    public static void BuildAndRun()
    {
        BuildAndroidPlayer(BuildOptions.AutoRunPlayer);
    }

    public static void BuildDetectorCaptureDiagnosticApk()
    {
        BuildAndroidPlayer(BuildOptions.None, detectorCaptureDiagnostic: true);
    }

    private static void BuildAndroidPlayer(BuildOptions options, bool detectorCaptureDiagnostic = false)
    {
        var outputPath = GetArgument("-outputPath");
        if (detectorCaptureDiagnostic && string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Missing -outputPath for the detector capture APK.");
        outputPath ??= "build/DJIUnity.apk";
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        EditorUserBuildSettings.buildAppBundle = false;

        var report = BuildPipeline.BuildPlayer(
            CreateAndroidBuildOptions(outputPath, options, detectorCaptureDiagnostic));
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new Exception(
                $"Android build failed: {report.summary.result} ({report.summary.totalErrors} errors)"
            );
        }

        UnityEngine.Debug.Log($"Android APK built: {Path.GetFullPath(outputPath)}");
    }

    internal static BuildPlayerOptions CreateAndroidBuildOptions(
        string outputPath, BuildOptions options, bool detectorCaptureDiagnostic = false) => new()
    {
        scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray(),
        locationPathName = outputPath,
        target = BuildTarget.Android,
        options = options,
        extraScriptingDefines = detectorCaptureDiagnostic
            ? new[] { "DJI_EXACT_DETECTOR_CAPTURE" }
            : Array.Empty<string>()
    };

    private static string GetArgument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }
}
