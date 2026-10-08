using System.Collections.Generic;
using UnityEngine;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Maps vehicle catalogue ids to imported models.
    ///
    /// The battlefield builds every vehicle from primitives at runtime, which is what
    /// lets the simulation and all 71 tests run with no art assets at all. This table
    /// is the single place where an imported model may override that, so adding art
    /// never becomes a reason for the procedural rig to stop working.
    ///
    /// Models are looked up through <c>Resources</c> because the scene is constructed
    /// in code and there is no authored scene to hang prefab references on. That has a
    /// cost worth remembering: everything in here is packed into the player build and
    /// cannot be streamed on demand, which is fine at 19 vehicles and would not be
    /// at 200.
    /// </summary>
    public static class TankModels
    {
        /// <summary>
        /// Catalogue id -> Resources path of the model prefab.
        ///
        /// Only vehicles with an imported model appear here. Anything absent falls
        /// through to the primitive rig, which is the expected state for most of the
        /// catalogue right now.
        /// </summary>
        private static readonly Dictionary<string, string> Paths =
            new Dictionary<string, string>
            {
                { "pziv-h", "Artwork/Models/PanzerIV/PanzerIV" },
            };

        private static readonly Dictionary<string, GameObject> Cache =
            new Dictionary<string, GameObject>();

        /// <summary>
        /// The model for a vehicle id, or null when it has none. Results are cached
        /// because <c>Resources.Load</c> would otherwise be called per tank per scene
        /// build, and the PlayMode suite builds a full bootstrap many times.
        /// </summary>
        public static GameObject ForVehicle(string vehicleId)
        {
            if (string.IsNullOrEmpty(vehicleId)) return null;
            if (!Paths.TryGetValue(vehicleId, out string path)) return null;

            if (Cache.TryGetValue(vehicleId, out GameObject cached)) return cached;

            GameObject loaded = Resources.Load<GameObject>(path);
            Cache[vehicleId] = loaded;
            return loaded;
        }

        /// <summary>
        /// True when a vehicle has an imported model. Used by tests to assert the
        /// primitive fallback still works for everything that has no art.
        /// </summary>
        public static bool HasModel(string vehicleId)
        {
            return !string.IsNullOrEmpty(vehicleId) && Paths.ContainsKey(vehicleId);
        }
    }
}
