using UnityEngine;
using UnityEditor;
using WarArena.Gameplay;

namespace WarArena.Editor
{
    public static class SlopeReport
    {
        [MenuItem("War Arena/Slope Report")]
        public static void Run()
        {
            int steep = 0, total = 0;
            float max = 0f;
            for (float x = -450f; x <= 450f; x += 5f)
            {
                for (float z = -450f; z <= 450f; z += 5f)
                {
                    if (x * x + z * z < 85f * 85f || x * x + z * z > 450f * 450f) continue;
                    float dx = BattlefieldBuilder.GetTerrainHeight(x + 1f, z) - BattlefieldBuilder.GetTerrainHeight(x - 1f, z);
                    float dz = BattlefieldBuilder.GetTerrainHeight(x, z + 1f) - BattlefieldBuilder.GetTerrainHeight(x, z - 1f);
                    float slope = Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz) / 2f) * Mathf.Rad2Deg;
                    max = Mathf.Max(max, slope);
                    total++;
                    if (slope > 30f) steep++;
                }
            }
            Debug.Log($"[SlopeReport] Max slope {max:F1}°, {100f * steep / total:F1}% of hills steeper than 30° (steep={steep}/{total})");
        }
    }
}
