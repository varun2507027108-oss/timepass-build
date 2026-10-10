using System.IO;
using UnityEditor;
using UnityEngine;
using WarArena.Gameplay;

namespace WarArena.Editor
{
    public static class BakeTerrain
    {
        private const int Resolution = 1025;
        private const float Size = 1000f;
        private const float MaxHeight = 85f;
        private const string TerrainDataAssetPath = "Assets/Resources/BattlefieldTerrainData.asset";
        private const string PrefabAssetPath = "Assets/Resources/BattlefieldTerrain.prefab";
        private const string GroundLayerAssetPath = "Assets/Resources/BattlefieldGroundLayer.terrainlayer";

        [MenuItem("War Arena/Bake Terrain")]
        public static void Bake()
        {
            if (!Directory.Exists("Assets/Resources"))
            {
                Directory.CreateDirectory("Assets/Resources");
            }

            TerrainData terrainData = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataAssetPath);
            if (terrainData == null)
            {
                terrainData = new TerrainData();
                AssetDatabase.CreateAsset(terrainData, TerrainDataAssetPath);
            }

            terrainData.heightmapResolution = Resolution;
            terrainData.size = new Vector3(Size, MaxHeight, Size);

            float[,] heights = new float[Resolution, Resolution];
            for (int z = 0; z < Resolution; z++)
            {
                float wz = (float)z / (Resolution - 1) * Size - (Size * 0.5f);
                for (int x = 0; x < Resolution; x++)
                {
                    float wx = (float)x / (Resolution - 1) * Size - (Size * 0.5f);
                    float h = BattlefieldBuilder.GetTerrainHeight(wx, wz);
                    heights[z, x] = Mathf.Clamp01(h / MaxHeight);
                }
            }

            terrainData.SetHeights(0, 0, heights);

            // Configure base terrain layer from existing CC0 scanned textures
            TerrainLayer groundLayer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(GroundLayerAssetPath);
            if (groundLayer == null)
            {
                groundLayer = new TerrainLayer();
                AssetDatabase.CreateAsset(groundLayer, GroundLayerAssetPath);
            }

            var diffTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/Artwork/Ground/brown_mud_02_diff_2k.jpg");
            var normTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/Artwork/Ground/brown_mud_02_norm_2k.jpg");
            if (diffTex != null) groundLayer.diffuseTexture = diffTex;
            if (normTex != null) groundLayer.normalMapTexture = normTex;
            groundLayer.tileSize = new Vector2(34f, 34f);
            groundLayer.smoothness = 0.06f;
            EditorUtility.SetDirty(groundLayer);

            terrainData.terrainLayers = new TerrainLayer[] { groundLayer };

            EditorUtility.SetDirty(terrainData);
            AssetDatabase.SaveAssets();

            GameObject existingGo = GameObject.Find("BattlefieldTerrain");
            if (existingGo != null)
            {
                Object.DestroyImmediate(existingGo);
            }

            GameObject terrainGo = Terrain.CreateTerrainGameObject(terrainData);
            terrainGo.name = "BattlefieldTerrain";
            terrainGo.transform.position = new Vector3(-Size * 0.5f, 0f, -Size * 0.5f);

            var terrain = terrainGo.GetComponent<Terrain>();
            terrain.drawHeightmap = true;
            terrain.drawTreesAndFoliage = true;

            var collider = terrainGo.GetComponent<TerrainCollider>();
            if (collider != null)
            {
                collider.terrainData = terrainData;
            }

            // Verify match against analytic function
            float worst = 0f;
            Random.InitState(42);
            for (int i = 0; i < 2000; i++)
            {
                float x = Random.Range(-450f, 450f);
                float z = Random.Range(-450f, 450f);
                float a = BattlefieldBuilder.GetTerrainHeight(x, z);
                float b = terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.transform.position.y;
                worst = Mathf.Max(worst, Mathf.Abs(a - b));
            }
            Debug.Log($"[BakeTerrain] Max height mismatch: {worst:F3} m");

            PrefabUtility.SaveAsPrefabAsset(terrainGo, PrefabAssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[BakeTerrain] Baked terrain: res={Resolution}, size={Size}x{MaxHeight}x{Size}, saved to {PrefabAssetPath}");
        }
    }
}
