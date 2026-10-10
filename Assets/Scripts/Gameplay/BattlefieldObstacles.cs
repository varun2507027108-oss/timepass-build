using System.Collections.Generic;
using UnityEngine;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Deterministic obstacle table shared by rendering and collision.
    ///
    /// The point of this class is that there is exactly ONE definition of where a
    /// tree is. Previously <c>BattlefieldBuilder</c> computed tree positions inline
    /// while the simulation knew nothing about them, so the tank drove through solid
    /// geometry: terrain height and obstacle collision are different problems, and
    /// getting the height right does nothing about the trunk.
    ///
    /// Placement and collision now read the same table, so they cannot drift apart.
    ///
    /// Everything here is a pure function of its index -- no <c>UnityEngine.Random</c>
    /// anywhere -- because the Rust mirror has to regenerate the identical table. If a
    /// position is ever changed here it must be changed in
    /// <c>crates/war-core/src/battlefield.rs</c> as well, or the two simulations stop
    /// agreeing and the parity harness fails.
    ///
    /// Each obstacle is a vertical cylinder. That is a poor fit for walls, which are
    /// long and thin, and an approximation for tree canopies, but it is exact enough
    /// for a trunk and cheap enough to evaluate four times per simulation tick.
    /// </summary>
    public static class BattlefieldObstacles
    {
        /// <summary>An upright cylinder the vehicle must not enter.</summary>
        public struct Obstacle
        {
            public float X;
            public float Z;
            public float Radius;

            /// <summary>True once the vehicle's centre is inside this obstacle.</summary>
            public bool Contains(float px, float pz, float vehicleRadius)
            {
                float dx = px - X;
                float dz = pz - Z;
                float reach = Radius + vehicleRadius;
                return dx * dx + dz * dz < reach * reach;
            }
        }

        /// <summary>Radius of a pine tree's trunk at unit scale, metres.</summary>
        private const float TreeTrunkRadius = 1.15f;

        private static Obstacle[] _cached;

        /// <summary>
        /// The obstacle table, built once and cached.
        /// </summary>
        public static IReadOnlyList<Obstacle> All
        {
            get
            {
                if (_cached == null) _cached = Build();
                return _cached;
            }
        }

        /// <summary>Count of perimeter trees in the forest belt.</summary>
        public const int PerimeterTreeCount = 68;

        /// <summary>World X of perimeter tree <paramref name="/index"/>.</summary>
        public static float PerimeterTreeX(int index)
        {
            float a = (float)index / PerimeterTreeCount * Mathf.PI * 2f;
            float r = PerimeterTreeRadius(index);
            return Mathf.Cos(a) * r;
        }

        /// <summary>World Z of perimeter tree <paramref name="/index"/>.</summary>
        public static float PerimeterTreeZ(int index)
        {
            float a = (float)index / PerimeterTreeCount * Mathf.PI * 2f;
            float r = PerimeterTreeRadius(index);
            return Mathf.Sin(a) * r;
        }

        /// <summary>Render scale of perimeter tree <paramref name="/index"/>.</summary>
        public static float PerimeterTreeScale(int index)
        {
            return 0.85f + (index % 7) * 0.12f;
        }

        private static float PerimeterTreeRadius(int index)
        {
            return 90f + (index % 5) * 26f + Mathf.Sin(index * 3.7f) * 14f;
        }

        /// <summary>Number of collidable obstacles.</summary>
        public static int Count => All.Count;

        /// <summary>Test seam: forces the table to be rebuilt.</summary>
        public static void Invalidate()
        {
            _cached = null;
        }

        private static Obstacle[] Build()
        {
            var list = new List<Obstacle>(PerimeterTreeCount + 5 + StumpCount);

            // --- Perimeter forest belt ---------------------------------------
            // Identical formula to the one the renderer uses to place the trees.
            for (int i = 0; i < PerimeterTreeCount; i++)
            {
                float a = (float)i / PerimeterTreeCount * Mathf.PI * 2f;
                float r = 90f + (i % 5) * 26f + Mathf.Sin(i * 3.7f) * 14f;
                float scale = 0.85f + (i % 7) * 0.12f;
                list.Add(new Obstacle
                {
                    X = Mathf.Cos(a) * r,
                    Z = Mathf.Sin(a) * r,
                    Radius = TreeTrunkRadius * scale,
                });
            }

            // --- Flanking stands near the church hill ------------------------
            AddStand(list, 75f, 85f);
            AddStand(list, 98f, 82f);
            AddStand(list, 105f, 65f);
            AddStand(list, 82f, 58f);
            AddStand(list, 68f, 72f);

            // --- Artillery-blasted stumps in the combat zones ----------------
            // These sit inside the playable bowl, so they are the ones a player is
            // most likely to actually hit.
            for (int i = 0; i < StumpCount; i++)
            {
                list.Add(new Obstacle
                {
                    X = StumpX[i],
                    Z = StumpZ[i],
                    Radius = StumpRadius,
                });
            }

            return list.ToArray();
        }

        private static void AddStand(List<Obstacle> list, float x, float z)
        {
            // Stand trees are placed at scale 1.15 by the renderer.
            list.Add(new Obstacle
            {
                X = x,
                Z = z,
                Radius = TreeTrunkRadius * 1.15f,
            });
        }

        /// <summary>Number of blasted stumps.</summary>
        public const int StumpCount = 14;

        /// <summary>Radius of a blasted stump, metres.</summary>
        private const float StumpRadius = 0.75f;

        // Stump positions, identical to BattlefieldBuilder.BuildVegetation.
        private static readonly float[] StumpX =
        {
            -18f, -12f, 14f, 22f, -20f, -28f, 32f, 38f, -35f, -42f, 28f, 16f, -8f, 8f,
        };

        private static readonly float[] StumpZ =
        {
            -10f, -24f, 26f, 18f, 40f, 50f, -22f, -34f, 10f, -5f, -10f, -42f, 32f, -20f,
        };
    }
}