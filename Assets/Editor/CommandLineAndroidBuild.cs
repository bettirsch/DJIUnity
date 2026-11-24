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
        BuildAndroidPlayer(BuildOptions.None, "DJI_EXACT_DETECTOR_CAPTURE");
    }

    public static void BuildRelativePositionDiagnosticApk()
    {
        BuildAndroidPlayer(BuildOptions.None, "DJI_RELATIVE_POSITION_EXPERIMENT");
    }

    // Compatibility entry only; it cannot select a different feature set.
    public static void BuildValidatedTrackingCandidateApk() => BuildApk();

    private static void BuildAndroidPlayer(BuildOptions options, string diagnosticDefine = null)
    {
        var outputPath = GetArgument("-outputPath");
        if (diagnosticDefine != null && string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Missing -outputPath for the diagnostic APK.");
        outputPath ??= "build/DJIUnity.apk";
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        EditorUserBuildSettings.buildAppBundle = false;

        FinalBridgeArtifactGuard.ValidateCurrentProject();
        var build = CreateAndroidBuildOptions(outputPath, options);
        build.extraScriptingDefines = DiagnosticDefines(diagnosticDefine);
        var report = BuildPipeline.BuildPlayer(build);
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

    internal static string[] DiagnosticDefines(string diagnosticDefine)
    {
        if (diagnosticDefine == null)
            return Array.Empty<string>();
        if (diagnosticDefine != "DJI_EXACT_DETECTOR_CAPTURE" &&
            diagnosticDefine != "DJI_RELATIVE_POSITION_EXPERIMENT")
            throw new ArgumentException("Unknown diagnostic capability.", nameof(diagnosticDefine));
        return new[] { diagnosticDefine };
    }

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
