using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Reports Unity's imported dependency closure for the Roof Canopy resource.
/// This tool is editor-only and never changes project assets.
/// </summary>
public static class RoofCanopyDependencyAudit
{
    private const string PrefabPath = "Assets/Resources/ARContent/RoofAssets/roof_canopy1.prefab";
    private const string RoofAssetsPath = "Assets/Resources/ARContent/RoofAssets";
    private const string ResourcesPath = "Assets/Resources";
    private const string ResourceLoadPath = "ARContent/RoofAssets/roof_canopy1";
    private const string ReportRelativePath = "Temp/RoofCanopyDependencyAudit.txt";

    [MenuItem("Tools/DJI/Audit Roof Canopy Dependencies")]
    public static void Audit()
    {
        var report = new StringBuilder();
        var success = false;

        try
        {
            var dependencies = AssetDatabase.GetDependencies(PrefabPath, true)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var dependencyPaths = new HashSet<string>(dependencies, StringComparer.OrdinalIgnoreCase);
            var packageAssets = EnumerateAssetFiles(RoofAssetsPath);
            var resourceAssets = EnumerateAssetFiles(ResourcesPath);
            var requiredPackageAssets = packageAssets.Where(dependencyPaths.Contains).ToArray();
            var unusedPackageAssets = packageAssets.Where(path => !dependencyPaths.Contains(path)).ToArray();
            var unusedResourceAssets = resourceAssets.Where(path => !dependencyPaths.Contains(path)).ToArray();
            var rendererAudit = InspectPrefabRenderers();
            var resourcesLoadResult = Resources.Load<GameObject>(ResourceLoadPath);
            var resourcesLoadResolves = resourcesLoadResult != null;

            AppendHeader(report);
            report.AppendLine($"prefab = {PrefabPath}");
            report.AppendLine($"prefab resource path = {ResourceLoadPath}");
            report.AppendLine($"dependency count = {dependencies.Length}");
            report.AppendLine($"dependency source size = {FormatBytes(dependencies.Sum(SourceFileSize))}");
            report.AppendLine($"Resources.Load resolves = {resourcesLoadResolves}");
            report.AppendLine();

            AppendRendererAudit(report, rendererAudit);
            AppendDependencies(report, dependencies);
            AppendPackageComparison(report, "REQUIRED_BY_PREFAB", requiredPackageAssets);
            AppendPackageComparison(report, "NOT_IN_PREFAB_DEPENDENCY_CLOSURE", unusedPackageAssets);
            AppendUnusedResources(report, unusedResourceAssets);
            AppendMovePlan(report, dependencyPaths);

            success = resourcesLoadResolves && rendererAudit.NullMaterialCount == 0 && rendererAudit.NullMeshCount == 0;
            report.AppendLine();
            report.AppendLine($"SAFE_TO_CLEAN = {(success ? "YES" : "NO")}");
            report.AppendLine("Note: source-file sizes are not APK/build-size measurements.");
            report.AppendLine("Note: this audit never deletes, moves, or edits project assets.");
        }
        catch (Exception exception)
        {
            report.AppendLine("ROOF CANOPY UNITY DEPENDENCY AUDIT");
            report.AppendLine($"AUDIT_FAILED = {exception.GetType().Name}: {exception.Message}");
            Debug.LogException(exception);
        }

        var reportText = report.ToString();
        var reportPath = Path.Combine(ProjectRootPath(), ReportRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ProjectRootPath());
        File.WriteAllText(reportPath, reportText);
        Debug.Log($"{reportText}\nROOF_CANOPY_DEPENDENCY_AUDIT_REPORT={reportPath}");

        if (!success)
            Debug.LogWarning("ROOF_CANOPY_DEPENDENCY_AUDIT_COMPLETE SAFE_TO_CLEAN=NO");
    }

    private static void AppendHeader(StringBuilder report)
    {
        report.AppendLine("ROOF CANOPY UNITY DEPENDENCY AUDIT");
        report.AppendLine();
    }

    private static void AppendRendererAudit(StringBuilder report, RendererAudit rendererAudit)
    {
        report.AppendLine("PREFAB_RENDERER_AUDIT");
        report.AppendLine($"rendererCount = {rendererAudit.RendererCount}");
        report.AppendLine($"meshRendererCount = {rendererAudit.MeshRendererCount}");
        report.AppendLine($"skinnedMeshRendererCount = {rendererAudit.SkinnedMeshRendererCount}");
        report.AppendLine($"materialSlotCount = {rendererAudit.MaterialSlotCount}");
        report.AppendLine($"missingMaterials = {rendererAudit.NullMaterialCount}");
        report.AppendLine($"missingMeshes = {rendererAudit.NullMeshCount}");
        report.AppendLine($"localBounds = {rendererAudit.LocalBounds}");
        report.AppendLine();
    }

    private static void AppendDependencies(StringBuilder report, IEnumerable<string> dependencies)
    {
        report.AppendLine("REQUIRED_ASSETS");
        foreach (var group in dependencies.GroupBy(ClassifyAsset).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            report.AppendLine($"[{group.Key}]");
            foreach (var path in group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                report.AppendLine(
                    $"{path} | type={type?.Name ?? "Unknown"} | sourceSize={FormatBytes(SourceFileSize(path))} " +
                    $"| insideRoofAssets={IsUnder(path, RoofAssetsPath)} | insideResources={IsUnder(path, ResourcesPath)}");
            }
        }

        report.AppendLine();
    }

    private static void AppendPackageComparison(StringBuilder report, string title, IEnumerable<string> assets)
    {
        var assetList = assets.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        report.AppendLine(title);
        report.AppendLine($"count = {assetList.Length}");
        report.AppendLine($"sourceSize = {FormatBytes(assetList.Sum(SourceFileSize))}");
        foreach (var path in assetList)
            report.AppendLine($"{path} | type={ClassifyAsset(path)} | sourceSize={FormatBytes(SourceFileSize(path))}");
        report.AppendLine();
    }

    private static void AppendUnusedResources(StringBuilder report, IEnumerable<string> assets)
    {
        var assetList = assets.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        report.AppendLine("UNUSED_ASSETS_CURRENTLY_UNDER_RESOURCES");
        report.AppendLine($"unusedResourcesFileCount = {assetList.Length}");
        report.AppendLine($"unusedResourcesSourceSize = {FormatBytes(assetList.Sum(SourceFileSize))}");
        foreach (var path in assetList)
            report.AppendLine($"{path} | type={ClassifyAsset(path)} | sourceSize={FormatBytes(SourceFileSize(path))}");
        report.AppendLine();
    }

    private static void AppendMovePlan(StringBuilder report, HashSet<string> dependencyPaths)
    {
        report.AppendLine("PROPOSED_MINIMAL_STRUCTURE");
        report.AppendLine("Retain the runtime entry point:");
        report.AppendLine($"  {PrefabPath} -> Assets/Resources/ARContent/RoofCanopy.prefab");
        report.AppendLine("Place non-prefab dependencies outside Resources while retaining their prefab references:");

        foreach (var path in dependencyPaths.Where(path => IsUnder(path, RoofAssetsPath) && !string.Equals(path, PrefabPath, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = path.Substring(RoofAssetsPath.Length).TrimStart('/');
            report.AppendLine($"  {path} -> Assets/ARContent/RoofCanopy/{relativePath}");
        }

        report.AppendLine("Do not perform this plan until dependency closure and prefab renderer results are reviewed in Unity.");
    }

    private static RendererAudit InspectPrefabRenderers()
    {
        GameObject prefabRoot = null;
        try
        {
            prefabRoot = PrefabUtility.LoadPrefabContents(PrefabPath);
            var renderers = prefabRoot.GetComponentsInChildren<Renderer>(true);
            var meshFilters = prefabRoot.GetComponentsInChildren<MeshFilter>(true);
            var skinnedRenderers = prefabRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var materialSlots = 0;
            var nullMaterials = 0;

            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                materialSlots += materials.Length;
                nullMaterials += materials.Count(material => material == null);
            }

            var nullMeshes = meshFilters.Count(filter => filter.sharedMesh == null) +
                skinnedRenderers.Count(renderer => renderer.sharedMesh == null);
            return new RendererAudit(
                renderers.Length,
                renderers.OfType<MeshRenderer>().Count(),
                skinnedRenderers.Length,
                materialSlots,
                nullMaterials,
                nullMeshes,
                CalculateLocalBounds(prefabRoot.transform, renderers));
        }
        finally
        {
            if (prefabRoot != null)
                PrefabUtility.UnloadPrefabContents(prefabRoot);
        }
    }

    private static Bounds CalculateLocalBounds(Transform root, IEnumerable<Renderer> renderers)
    {
        var hasBounds = false;
        var result = new Bounds();
        foreach (var renderer in renderers)
        {
            var bounds = renderer.bounds;
            for (var x = 0; x <= 1; x++)
            for (var y = 0; y <= 1; y++)
            for (var z = 0; z <= 1; z++)
            {
                var point = root.InverseTransformPoint(new Vector3(
                    x == 0 ? bounds.min.x : bounds.max.x,
                    y == 0 ? bounds.min.y : bounds.max.y,
                    z == 0 ? bounds.min.z : bounds.max.z));
                if (hasBounds)
                    result.Encapsulate(point);
                else
                {
                    result = new Bounds(point, Vector3.zero);
                    hasBounds = true;
                }
            }
        }

        return result;
    }

    private static string[] EnumerateAssetFiles(string assetDirectory)
    {
        var absoluteDirectory = ToAbsolutePath(assetDirectory);
        if (!Directory.Exists(absoluteDirectory))
            return Array.Empty<string>();

        return Directory.GetFiles(absoluteDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            .Select(ToAssetPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ClassifyAsset(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".prefab")
            return "PREFAB";
        if (extension == ".blend" || extension == ".fbx" || extension == ".obj")
            return "MODEL";
        if (extension == ".mat" || typeof(Material).IsAssignableFrom(AssetDatabase.GetMainAssetTypeAtPath(path)))
            return "MATERIAL";
        if (typeof(Texture).IsAssignableFrom(AssetDatabase.GetMainAssetTypeAtPath(path)))
            return "TEXTURE";
        if (typeof(Shader).IsAssignableFrom(AssetDatabase.GetMainAssetTypeAtPath(path)))
            return "SHADER";
        if (typeof(Mesh).IsAssignableFrom(AssetDatabase.GetMainAssetTypeAtPath(path)))
            return "MESH / GENERATED MODEL CONTENT";
        return "OTHER";
    }

    private static bool IsUnder(string path, string directory)
    {
        return path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, directory, StringComparison.OrdinalIgnoreCase);
    }

    private static long SourceFileSize(string assetPath)
    {
        var absolutePath = ToAbsolutePath(assetPath);
        return File.Exists(absolutePath) ? new FileInfo(absolutePath).Length : 0L;
    }

    private static string ToAbsolutePath(string assetPath)
    {
        return Path.Combine(ProjectRootPath(), assetPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ToAssetPath(string absolutePath)
    {
        var relativePath = absolutePath.Substring(ProjectRootPath().Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string ProjectRootPath()
    {
        return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }

    private static string FormatBytes(long bytes)
    {
        return $"{bytes:N0} bytes ({bytes / (1024f * 1024f):F2} MiB)";
    }

    private readonly struct RendererAudit
    {
        public RendererAudit(
            int rendererCount,
            int meshRendererCount,
            int skinnedMeshRendererCount,
            int materialSlotCount,
            int nullMaterialCount,
            int nullMeshCount,
            Bounds localBounds)
        {
            RendererCount = rendererCount;
            MeshRendererCount = meshRendererCount;
            SkinnedMeshRendererCount = skinnedMeshRendererCount;
            MaterialSlotCount = materialSlotCount;
            NullMaterialCount = nullMaterialCount;
            NullMeshCount = nullMeshCount;
            LocalBounds = localBounds;
        }

        public int RendererCount { get; }
        public int MeshRendererCount { get; }
        public int SkinnedMeshRendererCount { get; }
        public int MaterialSlotCount { get; }
        public int NullMaterialCount { get; }
        public int NullMeshCount { get; }
        public Bounds LocalBounds { get; }
    }
}
