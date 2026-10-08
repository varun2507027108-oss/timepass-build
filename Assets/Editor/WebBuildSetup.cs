using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Prepares project configuration for a WebGL player build.
///
/// Two things must be true before a browser build can render anything, and
/// neither is obvious from the Editor because both systems behave differently
/// at runtime in a player build:
///
/// <para>1. The playable scene must appear in the build scene list. A build with
/// an empty scene list produces a blank player.</para>
///
/// <para>2. Every material in this project is constructed at runtime through
/// <c>Shader.Find</c>. The build strips shaders that are not statically
/// referenced by a scene or asset, so those lookups resolve in the Editor and
/// return null in the player, leaving every tank, tracer and skybox untextured.</para>
///
/// Shader entries are resolved by name through the asset database rather than
/// written into the YAML by hand, because builtin shader fileIDs differ per
/// editor version and a stale ID is silently ignored rather than reported.
/// </summary>
public static class WebBuildSetup
{
    private const string ScenePath = "Assets/Scenes/Kursk.unity";
    private const string GraphicsSettingsPath = "ProjectSettings/GraphicsSettings.asset";
    private const string BuildPath = "Builds/WebGL";

    /// <summary>
    /// Shaders reached only through <c>Shader.Find</c> at runtime.
    /// These are the built-in pipeline equivalents of the URP shaders that
    /// <c>VisualMaterials</c> and <c>ShellImpactManager</c> probe for first.
    /// </summary>
    private static readonly string[] RuntimeShaders =
    {
        "Standard",
        "Unlit/Color",
        "Skybox/Procedural",
    };

    /// <summary>
    /// Registers the playable scene and guarantees the runtime-referenced
    /// shaders survive stripping. Safe to run repeatedly.
    /// </summary>
    public static void Run()
    {
        var report = new List<string>();

        RegisterPlayableScene(report);
        IncludeRuntimeShaders(report);
        ReportWebGLModule(report);

        Debug.Log("[WebBuildSetup]\n" + string.Join("\n", report));
    }

    /// <summary>
    /// Configures WebGL player settings, switches the active build target, and
    /// produces a player build.
    ///
    /// Compression is deliberately left off for this first build. A Brotli build
    /// is only servable by a host that returns <c>Content-Encoding: br</c>, so a
    /// failed load under Brotli is indistinguishable from a genuine graphics
    /// failure. Validating uncompressed first separates the two, at the cost of a
    /// larger download. Brotli should be enabled once the page is confirmed to
    /// render and the deployment host is known to set the header.
    /// </summary>
    public static void BuildWeb()
    {
        var report = new List<string>();

        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
        {
            Debug.LogError("[WebBuildSetup] WebGL support is not installed for the editor running this build.");
            EditorApplication.Exit(2);
            return;
        }

        Run();

        ConfigureWebGLPlayerSettings(report);

        report.Add("Switching active build target to WebGL...");
        if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL))
        {
            Debug.LogError("[WebBuildSetup] SwitchActiveBuildTarget(WebGL) failed.");
            EditorApplication.Exit(3);
            return;
        }

        report.Add("Active build target is now WebGL.");

        var options = new BuildPlayerOptions
        {
            scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray(),
            locationPathName = BuildPath,
            target = BuildTarget.WebGL,
            targetGroup = BuildTargetGroup.WebGL,
            options = BuildOptions.None,
        };

        report.Add($"Building {options.scenes.Length} scene(s) to {BuildPath}");

        BuildReport build = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = build.summary;

        report.Add($"Result: {summary.result}");
        report.Add($"Size: {summary.totalSize / (1024f * 1024f):0.0} MB");
        report.Add($"Time: {summary.totalTime.TotalMinutes:0.0} min");
        report.Add($"Errors: {summary.totalErrors}, Warnings: {summary.totalWarnings}");

        Debug.Log("[WebBuildSetup] " + string.Join("\n", report));

        EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 4);
    }

    private static void ConfigureWebGLPlayerSettings(List<string> report)
    {
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.WebGL.debugSymbolMode = WebGLDebugSymbolMode.Off;
        PlayerSettings.WebGL.powerPreference = WebGLPowerPreference.HighPerformance;

        PlayerSettings.SetManagedStrippingLevel(
            BuildTargetGroup.WebGL,
            ManagedStrippingLevel.Low);

        report.Add("Player settings: compression Disabled, data caching on, symbols off, stripping Low.");
    }

    private static void RegisterPlayableScene(List<string> report)
    {
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);

        int existing = scenes.FindIndex(s => s.path == ScenePath);
        if (existing >= 0)
        {
            if (!scenes[existing].enabled)
            {
                scenes[existing] = new EditorBuildSettingsScene(ScenePath, true);
                EditorBuildSettings.scenes = scenes.ToArray();
                report.Add($"Scene {ScenePath} was present but disabled; enabled.");
            }
            else
            {
                report.Add($"Scene {ScenePath} already registered at index {existing}.");
            }

            return;
        }

        scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        report.Add($"Scene {ScenePath} registered at index 0.");
    }

    private static void IncludeRuntimeShaders(List<string> report)
    {
        Object[] targets = AssetDatabase.LoadAllAssetsAtPath(GraphicsSettingsPath);
        if (targets == null || targets.Length == 0)
        {
            report.Add($"FAILED to load {GraphicsSettingsPath}.");
            return;
        }

        var serialized = new SerializedObject(targets[0]);
        SerializedProperty shaders = serialized.FindProperty("m_AlwaysIncludedShaders");
        if (shaders == null)
        {
            report.Add("FAILED to locate m_AlwaysIncludedShaders.");
            return;
        }

        bool changed = false;

        foreach (string name in RuntimeShaders)
        {
            Shader shader = Shader.Find(name);
            if (shader == null)
            {
                report.Add($"Shader '{name}' NOT FOUND in this editor; cannot include it.");
                continue;
            }

            if (Contains(shaders, shader))
            {
                report.Add($"Shader '{name}' already included.");
                continue;
            }

            shaders.InsertArrayElementAtIndex(shaders.arraySize);
            shaders.GetArrayElementAtIndex(shaders.arraySize - 1).objectReferenceValue = shader;
            changed = true;
            report.Add($"Shader '{name}' ADDED (was being stripped).");
        }

        if (!changed)
        {
            report.Add("Shader list unchanged.");
            return;
        }

        serialized.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
    }

    private static bool Contains(SerializedProperty array, Object target)
    {
        for (int i = 0; i < array.arraySize; i++)
        {
            if (array.GetArrayElementAtIndex(i).objectReferenceValue == target)
            {
                return true;
            }
        }

        return false;
    }

    private static void ReportWebGLModule(List<string> report)
    {
        bool installed = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL);
        report.Add($"WebGL build support installed: {installed}");

        if (!installed)
        {
            report.Add(
                "ACTION REQUIRED: Unity Hub > Installs > 6000.6.4f1 > Add modules > Web Build Support. " +
                "Scene and shader configuration above is complete but cannot be built until this is installed.");
        }
    }
}
