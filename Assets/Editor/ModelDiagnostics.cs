using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using WarArena.Gameplay;
using WarArena.Vehicles;

public static class ModelDiagnostics
{
    /// <summary>
    /// Reports whether imported tank models are actually resolvable at runtime.
    ///
    /// The failure mode this exists to catch is silent. TankView falls back to the
    /// procedural rig whenever a model is missing, so a bad Resources path produces
    /// a green build, an empty log, and a box-shaped tank — with nothing anywhere
    /// reporting that the art was dropped on the floor.
    /// </summary>
    public static void Run()
    {
        var report = new StringBuilder();

        report.AppendLine("[ModelDiagnostics] model assets found under Assets/Resources:");
        foreach (string guid in AssetDatabase.FindAssets("", new[] { "Assets/Resources" }))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!assetPath.Contains("Models")) continue;
            UnityEngine.Object main = AssetDatabase.LoadMainAssetAtPath(assetPath);
            report.AppendLine($"  {assetPath}");
            report.AppendLine($"      main asset = {main?.GetType().Name ?? "null"}");
        }

        report.AppendLine();
        report.AppendLine("[ModelDiagnostics] importer actually selected per file:");
        foreach (string guid in AssetDatabase.FindAssets("", new[] { "Assets" }))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!assetPath.EndsWith(".glb", System.StringComparison.OrdinalIgnoreCase)) continue;

            AssetImporter importer = AssetImporter.GetAtPath(assetPath);
            report.AppendLine($"  {assetPath}");
            report.AppendLine($"      importer   = {importer?.GetType().Name ?? "null"}");
            report.AppendLine($"      isReadable = {AssetDatabase.IsValidFolder(assetPath)}");
        }

        report.AppendLine();
        report.AppendLine("[ModelDiagnostics] direct Resources.Load probes:");
        foreach (string path in new[]
                 {
                     "Artwork/Models/PanzerIV/panzer_iv_low",
                     "Artwork/Models/PanzerIV/panzer_iv",
                 })
        {
            GameObject loaded = Resources.Load<GameObject>(path);
            report.AppendLine($"  '{path}' -> {(loaded == null ? "NULL" : "OK")}");
        }

        report.AppendLine();
        report.AppendLine("[ModelDiagnostics] catalogue sweep:");
        var seen = new HashSet<string>();
        int total = 0;
        int registered = 0;

        foreach (VehicleCatalog.VehicleDefinition def in VehicleCatalog.All)
        {
            total++;
            if (!seen.Add(def.Id)) continue;

            bool has = TankModels.HasModel(def.Id);
            if (!has) continue;
            registered++;

            GameObject model = TankModels.ForVehicle(def.Id);
            report.AppendLine($"  {def.Id,-14} {(model == null ? "REGISTERED BUT LOAD FAILED" : "model OK")}");
        }

        report.AppendLine($"  vehicles={total}  distinct ids={seen.Count}  with model={registered}");
        Debug.Log(report.ToString());
    }
}
