using UnityEngine;

namespace WarArena.Vehicles
{
    /// <summary>
    /// Unity-facing half of the catalogue: turns catalogue data into live physics
    /// configuration.
    ///
    /// Kept separate from <see cref="VehicleCatalog"/> on purpose. The catalogue and
    /// the damage model are pure value-type logic and are verified headlessly
    /// (see Tools/SimVerify); anything that touches WheelCollider lives here so it
    /// can be excluded from that harness rather than stubbed out.
    /// </summary>
    public static class VehicleCatalogPhysics
    {
        /// <summary>
        /// Configures a controller from a catalogue entry. Power-to-weight sets the
        /// acceleration curve, so a 57-tonne Tiger genuinely cannot wind up like a
        /// 26-tonne T-34, and traverse rate scales with mass because that is why
        /// heavy tanks fight hull-down.
        /// </summary>
        public static void ApplyToController(TankController c, VehicleCatalog.VehicleDefinition v)
        {
            if (c == null) throw new System.ArgumentNullException(nameof(c));

            c.RatedEngineKw = v.EngineKw;

            float powerToWeight = v.EngineKw / (v.MassKg / 1000f);
            c.AccelerationTime = Mathf.Clamp(12f - powerToWeight * 0.9f, 3.5f, 14f);
            c.TorqueGain = v.MassKg * 0.09f;
            c.TraverseSpeedDeg = v.MassKg > 40000f ? 22f : 30f;
            c.ElevationSpeedDeg = v.MassKg > 40000f ? 8f : 12f;

            // Suspension stiffness has to scale with mass or a 68-tonne King Tiger
            // bottoms out through its own springs.
            float massT = v.MassKg / 1000f;
            c.SuspensionSpring = massT * 1100f;
            c.SuspensionDamper = massT * 150f;
            c.SuspensionTarget = 0.42f;
            c.MaxTrackForce = massT * 9000f;
        }

        /// <summary>Builds the runtime shell a vehicle's main gun fires.</summary>
        public static Ballistics.Shell BuildLoadedShell(VehicleCatalog.VehicleDefinition v)
        {
            return VehicleCatalog.BuildShell(v);
        }
    }
}