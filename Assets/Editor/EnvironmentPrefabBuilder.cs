using System.IO;
using UnityEditor;
using UnityEngine;
using WarArena.Gameplay;

namespace WarArena.Editor
{
    public static class EnvironmentPrefabBuilder
    {
        private const string RuinsDir = "Assets/Artwork/Environment/Ruins";
        private const string PropsDir = "Assets/Artwork/Environment/Props";
        private const string OutDir = "Assets/Resources/Artwork/Environment";

        private const string MatDir = "Assets/Artwork/Environment/Materials";

        [MenuItem("WarArena/Build Environment Prefabs", false, 30)]
        public static void BuildAll()
        {
            Directory.CreateDirectory(OutDir);
            Directory.CreateDirectory(MatDir);

            BuildRuinedHouse();
            BuildRuinedChurch();
            BuildWatchtower();
            BuildTankWreck();
            BuildRocks();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[EnvironmentPrefabBuilder] All environment prefabs built successfully!");
        }

        private static Material GetOrCreateMaterial(string matName)
        {
            Directory.CreateDirectory(MatDir);
            string path = $"{MatDir}/{matName}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(VisualMaterials.LitShader) { name = matName };
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                mat.shader = VisualMaterials.LitShader;
            }
            return mat;
        }

        private static void BuildRuinedHouse()
        {
            string fbxPath = $"{RuinsDir}/vhouse_DM.FBX";
            string texPath = $"{RuinsDir}/vhouse_diff.png";
            string outPrefab = $"{OutDir}/RuinedHouse.prefab";

            ConfigureTexture(texPath);

            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (modelAsset == null)
            {
                Debug.LogWarning($"[EnvironmentPrefabBuilder] Could not find {fbxPath}");
                return;
            }

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            var root = Object.Instantiate(modelAsset);
            root.name = "RuinedHouse";

            var mat = GetOrCreateMaterial("RuinedHouse_Mat");
            if (texture != null) mat.mainTexture = texture;
            mat.color = new Color(0.9f, 0.88f, 0.85f);
            mat.SetFloat("_Glossiness", 0.12f);
            mat.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(mat);

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                r.sharedMaterial = mat;
            }

            PrefabUtility.SaveAsPrefabAsset(root, outPrefab);
            Object.DestroyImmediate(root);
            Debug.Log($"[EnvironmentPrefabBuilder] Saved {outPrefab}");
        }

        private static void BuildRuinedChurch()
        {
            string fbxPath = $"{RuinsDir}/church.FBX";
            string texPath = $"{RuinsDir}/blr205_Church.png";
            string outPrefab = $"{OutDir}/RuinedChurch.prefab";

            ConfigureTexture(texPath);

            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (modelAsset == null)
            {
                Debug.LogWarning($"[EnvironmentPrefabBuilder] Could not find {fbxPath}");
                return;
            }

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            var root = Object.Instantiate(modelAsset);
            root.name = "RuinedChurch";

            var mat = GetOrCreateMaterial("RuinedChurch_Mat");
            if (texture != null) mat.mainTexture = texture;
            mat.color = new Color(0.85f, 0.83f, 0.80f);
            mat.SetFloat("_Glossiness", 0.15f);
            mat.SetFloat("_Metallic", 0.05f);
            EditorUtility.SetDirty(mat);

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                r.sharedMaterial = mat;
            }

            PrefabUtility.SaveAsPrefabAsset(root, outPrefab);
            Object.DestroyImmediate(root);
            Debug.Log($"[EnvironmentPrefabBuilder] Saved {outPrefab}");
        }

        private static void BuildWatchtower()
        {
            string objPath = $"{PropsDir}/tower.obj";
            string outPrefab = $"{OutDir}/Watchtower.prefab";

            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(objPath);
            if (modelAsset == null)
            {
                Debug.LogWarning($"[EnvironmentPrefabBuilder] Could not find {objPath}");
                return;
            }

            var root = Object.Instantiate(modelAsset);
            root.name = "Watchtower";

            // Weathered military timber and rusted steel scaffolding
            var mat = GetOrCreateMaterial("Watchtower_Mat");
            mat.color = new Color(0.26f, 0.23f, 0.20f);
            mat.SetFloat("_Glossiness", 0.20f);
            mat.SetFloat("_Metallic", 0.40f);
            EditorUtility.SetDirty(mat);

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                r.sharedMaterial = mat;
            }

            PrefabUtility.SaveAsPrefabAsset(root, outPrefab);
            Object.DestroyImmediate(root);
            Debug.Log($"[EnvironmentPrefabBuilder] Saved {outPrefab}");
        }

        private static void BuildTankWreck()
        {
            string fbxPath = $"{PropsDir}/T-26_Wreck.FBX";
            string texPath = $"{PropsDir}/T-26_AM.png";
            string outPrefab = $"{OutDir}/TankWreck.prefab";

            ConfigureTexture(texPath);

            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (modelAsset == null)
            {
                Debug.LogWarning($"[EnvironmentPrefabBuilder] Could not find {fbxPath}");
                return;
            }

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            var root = Object.Instantiate(modelAsset);
            root.name = "TankWreck";

            // Burnt, rusted steel hull
            var mat = GetOrCreateMaterial("TankWreck_Mat");
            if (texture != null)
            {
                mat.mainTexture = texture;
                // Tint darker to look scorched/charred
                mat.color = new Color(0.45f, 0.42f, 0.40f);
            }
            else
            {
                mat.color = new Color(0.20f, 0.18f, 0.16f);
            }
            mat.SetFloat("_Glossiness", 0.25f);
            mat.SetFloat("_Metallic", 0.65f);
            EditorUtility.SetDirty(mat);

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                r.sharedMaterial = mat;
            }

            PrefabUtility.SaveAsPrefabAsset(root, outPrefab);
            Object.DestroyImmediate(root);
            Debug.Log($"[EnvironmentPrefabBuilder] Saved {outPrefab}");
        }

        private static void BuildRocks()
        {
            string[] rockNames = { "Rock4", "Rock6", "Rock11" };
            var mat = GetOrCreateMaterial("Rock_Mat");
            var rubbleTex = Resources.Load<Texture2D>("Artwork/Rubble/rubble_diff_2k");
            if (rubbleTex != null)
            {
                mat.mainTexture = rubbleTex;
                mat.mainTextureScale = Vector2.one * 2.5f;
            }
            mat.color = new Color(0.60f, 0.58f, 0.54f);
            mat.SetFloat("_Glossiness", 0.10f);
            mat.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(mat);

            foreach (var name in rockNames)
            {
                string path = $"{PropsDir}/{name}.obj";
                string outPrefab = $"{OutDir}/{name}.prefab";

                var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (modelAsset == null) continue;

                var root = Object.Instantiate(modelAsset);
                root.name = name;

                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    r.sharedMaterial = mat;
                }

                PrefabUtility.SaveAsPrefabAsset(root, outPrefab);
                Object.DestroyImmediate(root);
                Debug.Log($"[EnvironmentPrefabBuilder] Saved {outPrefab}");
            }
        }

        [MenuItem("WarArena/Verify Environment Prefabs", false, 31)]
        public static void VerifyAll()
        {
            string[] prefabs = { "RuinedHouse", "RuinedChurch", "Watchtower", "TankWreck", "Rock4", "Rock6", "Rock11" };
            foreach (var name in prefabs)
            {
                var go = Resources.Load<GameObject>($"Artwork/Environment/{name}");
                if (go == null)
                {
                    Debug.LogWarning($"[EnvironmentPrefabBuilder] {name} NOT FOUND in Resources!");
                    continue;
                }
                var inst = Object.Instantiate(go);
                var bounds = new Bounds();
                bool first = true;
                foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
                {
                    if (first) { bounds = r.bounds; first = false; }
                    else bounds.Encapsulate(r.bounds);
                }
                Debug.Log($"[EnvironmentPrefabBuilder] {name}: size = ({bounds.size.x:F2}, {bounds.size.y:F2}, {bounds.size.z:F2})");
                Object.DestroyImmediate(inst);
            }
        }

        private static void ConfigureTexture(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                bool dirty = false;
                if (!importer.isReadable) { importer.isReadable = true; dirty = true; }
                if (importer.maxTextureSize > 2048) { importer.maxTextureSize = 2048; dirty = true; }
                if (importer.wrapMode != TextureWrapMode.Repeat) { importer.wrapMode = TextureWrapMode.Repeat; dirty = true; }
                if (dirty) importer.SaveAndReimport();
            }
        }
    }
}
