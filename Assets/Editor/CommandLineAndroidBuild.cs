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

    private static void BuildAndroidPlayer(BuildOptions options)
    {
        var outputPath = GetArgument("-outputPath") ?? "build/DJIUnity.apk";
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        EditorUserBuildSettings.buildAppBundle = false;

        var scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();

        var buildOptions = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = BuildTarget.Android,
            options = options
        };

        var report = BuildPipeline.BuildPlayer(buildOptions);
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new Exception(
                $"Android build failed: {report.summary.result} ({report.summary.totalErrors} errors)"
            );
        }

        UnityEngine.Debug.Log($"Android APK built: {Path.GetFullPath(outputPath)}");
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
