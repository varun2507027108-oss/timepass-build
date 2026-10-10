using NUnit.Framework;
using UnityEngine;
using WarArena.Gameplay;

namespace WarArena.Tests
{
    /// <summary>
    /// The vehicle used to drive straight through solid trees: terrain height and
    /// obstacle collision are different problems, and only the first was solved.
    ///
    /// These tests pin the shared obstacle table (the same one the renderer places
    /// geometry from) and the circle-vs-cylinder resolution both language
    /// implementations use.
    /// </summary>
    [TestFixture]
    public class BattlefieldObstacleTests
    {
        [Test]
        public void Table_HasTheExpectedShape()
        {
            Assert.AreEqual(68 + 5 + 14, BattlefieldObstacles.Count, "obstacle count");
        }

        [Test]
        public void Table_MatchesTheRendererPlacementFormula()
        {
            // The renderer now reads these helpers, so if they ever diverge the trees
            // are drawn in one place and collided in another.
            for (int i = 0; i < 68; i++)
            {
                float a = (float)i / 68f * Mathf.PI * 2f;
                float r = 90f + (i % 5) * 26f + Mathf.Sin(i * 3.7f) * 14f;
                Assert.AreEqual(Mathf.Cos(a) * r, BattlefieldObstacles.PerimeterTreeX(i), 1e-3f);
                Assert.AreEqual(Mathf.Sin(a) * r, BattlefieldObstacles.PerimeterTreeZ(i), 1e-3f);
                Assert.AreEqual(0.85f + (i % 7) * 0.12f,
                    BattlefieldObstacles.PerimeterTreeScale(i), 1e-4f);
            }
        }

        [Test]
        public void Stumps_SitInsideThePlayableBowl()
        {
            // The stumps are the obstacles a player actually drives into, so they must
            // be reachable rather than out in the forest belt. They are the LAST entries
            // in the table -- filtering by radius does not work, because a perimeter
            // tree at scale 0.85 has a smaller radius than a stump.
            const int StumpCount = 14;
            int firstStump = BattlefieldObstacles.Count - StumpCount;

            for (int i = firstStump; i < BattlefieldObstacles.Count; i++)
            {
                var o = BattlefieldObstacles.All[i];
                float r = Mathf.Sqrt(o.X * o.X + o.Z * o.Z);
                Assert.Less(r, 60f, $"stump {i - firstStump} at radius {r} is unreachable");
            }
        }

        [Test]
        public void Resolve_PushesAVehicleOutOfATree()
        {
            var obstacles = BattlefieldObstacles.All;
            var target = obstacles[0];
            Vector2 pos = new Vector2(target.X, target.Z);

            bool hit = InvokeResolve(ref pos);

            Assert.IsTrue(hit, "a vehicle centred in a trunk must register a hit");
            float dist = Vector2.Distance(pos, new Vector2(target.X, target.Z));
            Assert.GreaterOrEqual(dist, target.Radius + 1.5f - 1e-3f,
                "vehicle must end up outside the trunk it started inside");
        }

        [Test]
        public void Resolve_LeavesOpenGroundAlone()
        {
            Vector2 pos = Vector2.zero;
            bool hit = InvokeResolve(ref pos);

            Assert.IsFalse(hit, "open ground must not register a hit");
            Assert.AreEqual(0f, pos.x, 1e-5f);
            Assert.AreEqual(0f, pos.y, 1e-5f);
        }

        [Test]
        public void Resolve_EndsOutsideEveryObstacle()
        {
            var obstacles = BattlefieldObstacles.All;
            const float VehicleRadius = 1.5f;

            for (int start = 0; start < obstacles.Count; start++)
            {
                Vector2 pos = new Vector2(obstacles[start].X, obstacles[start].Z);
                InvokeResolve(ref pos);

                for (int i = 0; i < obstacles.Count; i++)
                {
                    var o = obstacles[i];
                    float dist = Vector2.Distance(pos, new Vector2(o.X, o.Z));
                    float reach = o.Radius + VehicleRadius;
                    Assert.GreaterOrEqual(dist, reach - 1e-3f,
                        $"escaping obstacle {start} left the vehicle {dist:F4} inside obstacle {i} (reach {reach:F4})");
                }
            }
        }

        /// <summary>
        /// The vehicle used to drive straight through solid trees. This drives the real
        /// simulation from spawn and asserts the invariant that matters to a player:
        /// at no point is the hull inside an obstacle.
        ///
        /// No test-only setter is added to <see cref="BattleSim"/> for this -- the sim is
        /// driven through its public entry point exactly as the game drives it.
        /// </summary>
        [Test]
        public void Driving_TheTankIsNeverInsideAnObstacle()
        {
            const float VehicleRadius = 1.5f;
            var sim = new BattleSim("pziv-h", "panther-g");

            int worstOverlaps = 0;
            float worstDepth = 0f;

            foreach (float steer in new[] { 0f, 0.6f, -0.6f })
            {
                var run = new BattleSim("pziv-h", "panther-g");
                for (int i = 0; i < 900; i++)   // 30 seconds at full throttle
                {
                    run.Advance(1f / 30f, 1f, steer, 0f, false, run.PlayerPosition);

                    Vector3 p = run.PlayerPosition;
                    foreach (var o in BattlefieldObstacles.All)
                    {
                        float dx = p.x - o.X;
                        float dz = p.z - o.Z;
                        float reach = o.Radius + VehicleRadius;
                        float distSq = dx * dx + dz * dz;
                        if (distSq >= reach * reach) continue;

                        worstOverlaps++;
                        worstDepth = Mathf.Max(worstDepth, reach - Mathf.Sqrt(distSq));
                    }
                }
            }

            Assert.LessOrEqual(worstDepth, 0.02f,
                $"the tank ended up {worstDepth:F3} m inside a solid obstacle " +
                $"({worstOverlaps} overlapping samples)");
        }

        private static bool InvokeResolve(ref Vector2 pos)
        {
            var m = typeof(BattleSim).GetMethod("ResolveObstacles",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(m, "ResolveObstacles not found");
            var args = new object[] { pos, 1.5f };
            bool result = (bool)m.Invoke(null, args);
            pos = (Vector2)args[0];
            return result;
        }
    }
}