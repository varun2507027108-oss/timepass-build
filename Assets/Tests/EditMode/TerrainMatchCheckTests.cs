using NUnit.Framework;
using UnityEngine;
using WarArena.Gameplay;

namespace WarArena.Tests.EditMode
{
    public class TerrainMatchCheckTests
    {
        [Test]
        public void BakedTerrain_MatchesAnalyticHeightmap_WithinTolerance()
        {
            var prefab = Resources.Load<GameObject>("BattlefieldTerrain");
            Assert.IsNotNull(prefab, "BattlefieldTerrain prefab must exist in Resources.");

            var go = Object.Instantiate(prefab);
            try
            {
                var terrain = go.GetComponent<Terrain>();
                Assert.IsNotNull(terrain, "Terrain component must exist on BattlefieldTerrain.");

                float worst = 0f;
                Random.InitState(42);
                for (int i = 0; i < 2000; i++)
                {
                    float x = Random.Range(-450f, 450f);
                    float z = Random.Range(-450f, 450f);
                    float a = BattlefieldBuilder.GetTerrainHeight(x, z);
                    float b = terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.transform.position.y;
                    float diff = Mathf.Abs(a - b);
                    worst = Mathf.Max(worst, diff);
                }

                Debug.Log($"[TerrainMatchCheck] 2000 random samples. Max height mismatch: {worst:F3} m");
                Assert.Less(worst, 0.35f, $"Max height mismatch {worst} m is higher than expected tolerance.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
