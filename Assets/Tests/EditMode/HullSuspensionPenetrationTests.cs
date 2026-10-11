using NUnit.Framework;
using UnityEngine;
using WarArena.Gameplay;

namespace WarArena.Tests
{
    /// <summary>
    /// Probes for the failure reported from gameplay: the hull sinking into a slope
    /// while one track hangs in the air.
    ///
    /// The two-layer model fits a plane to all EIGHT sampled contacts and rides the
    /// hull at the mean of them. That is correct while the vehicle sits flat on the
    /// ground, because every contact is touching. It is wrong the moment one track
    /// is airborne: the contacts over the drop are still included in the fit and in
    /// the heave average even though they are carrying nothing, so the plane is
    /// dragged down toward the dip and the loaded track is pushed into the hillside.
    ///
    /// This is a DIAGNOSTIC, not a passing assertion. It scans a spread of positions
    /// and reports the worst penetration it can find, which is how the location for
    /// the pinned regression below was chosen. It is kept because a silent pass here
    /// means the model has regressed back to the same failure.
    /// </summary>
    [TestFixture]
    public class HullSuspensionPenetrationTests
    {
        private const float Step = 1f / 30f;
        private const float PlayerMassKg = 26000f;

        private static HullSuspension NewHull()
        {
            return new HullSuspension(PlayerMassKg,
                TrackedVehicleDynamics.ContactHalfLengthM(PlayerMassKg),
                TrackedVehicleDynamics.ContactHalfWidthM(PlayerMassKg),
                TrackedVehicleDynamics.ContactCentreZM(PlayerMassKg));
        }

        /// <summary>
        /// Worst amount by which any contact sits BELOW the terrain, metres.
        /// Positive means the hull has sunk into the ground at that corner.
        /// </summary>
        private static float WorstPenetration(HullSuspension s, float x, float z, float heading)
        {
            float worst = 0f;
            for (int i = 0; i < 8; i++)
            {
                // Contact offsets as the suspension lays them out: two per track at
                // z = {-h, -h/3, +h/3, +h} offset by the patch centre, x = +/-w.
                int perTrack = 4;
                int track = i / perTrack;
                int idx = i % perTrack;
                float h = TrackedVehicleDynamics.ContactHalfLengthM(PlayerMassKg);
                float third = h / 3f;
                float lz;
                switch (idx)
                {
                    case 0: lz = -h; break;
                    case 1: lz = -third; break;
                    case 2: lz = third; break;
                    default: lz = h; break;
                }
                float lzOff = lz + TrackedVehicleDynamics.ContactCentreZM(PlayerMassKg);
                float lx = track == 0
                    ? -TrackedVehicleDynamics.ContactHalfWidthM(PlayerMassKg)
                    : TrackedVehicleDynamics.ContactHalfWidthM(PlayerMassKg);

                float cy = Mathf.Cos(heading);
                float sy = Mathf.Sin(heading);
                float wx = x + lx * cy + lzOff * sy;
                float wz = z - lx * sy + lzOff * cy;

                float ground = BattlefieldBuilder.GetTerrainHeight(wx, wz);

                // Hull surface height directly over this contact.
                float localY = lx * Mathf.Sin(s.rollRad) * Mathf.Cos(s.pitchRad)
                             + lzOff * Mathf.Sin(s.pitchRad);
                float hullSurface = s.height + localY;

                float penetration = ground - hullSurface;
                if (penetration > worst) worst = penetration;
            }
            return worst;
        }

        [Test]
        public void Diagnostic_WorstPenetrationAcrossTheArena()
        {
            HullSuspension worstOverall = null;
            float worstValue = 0f;
            float wx = 0f, wz = 0f, wh = 0f;

            // Sweep headings too: the reported failure is a CROSS-slope, so it only
            // appears when the vehicle is broadside to the gradient.
            for (float headingDeg = 0f; headingDeg < 360f; headingDeg += 30f)
            {
                float heading = headingDeg * Mathf.Deg2Rad;
                for (float x = -320f; x <= 320f; x += 16f)
                {
                    for (float z = -320f; z <= 320f; z += 16f)
                    {
                        HullSuspension s = NewHull();
                        s.Reset(x, z, heading);
                        for (int i = 0; i < 180; i++)
                        {
                            s.Step(Step, x, z, heading, 0f, 0f);
                        }

                        float pen = WorstPenetration(s, x, z, heading);
                        if (pen > worstValue)
                        {
                            worstValue = pen;
                            worstOverall = s;
                            wx = x; wz = z; wh = headingDeg;
                        }
                    }
                }
            }

            Debug.Log($"[Penetration] worst = {worstValue:F3} m at ({wx:F0}, {wz:F0}) heading {wh:F0} deg");

            // Regression guard for the load-weighted ride height.
            //
            // Worst-case penetration over the arena sweep, measured:
            //   blend 0.0 (plain mean, the old behaviour) = 1.243 m
            //   blend 1.0 (fully load-weighted)           = 0.735 m, but the Rust
            //       sustained-driving test then failed at -0.91 m against a -0.6 m bound
            //   blend 0.5                                 = failed at -0.75 m
            //   blend 0.25 (shipped)                      = 0.817 m, all tests passing
            //
            // So the shipped value is a partial blend, not the full weighting the
            // reference game's `_terrainContactCount` implies: full weighting wins on
            // static penetration but makes the hull follow the rearward loaded contacts
            // during driving and buries the nose. 0.25 keeps most of the static gain
            // with no driving regression.
            //
            // TIGHTEN THIS. 0.85 is a regression guard against going back to 1.243,
            // not a statement that 0.817 is acceptable -- 0.817 m of hull plane inside
            // a 41 degree face is still more than the model should allow.
            Assert.Less(worstValue, 0.85f,
                $"worst penetration regressed to {worstValue:F3} m at ({wx:F0}, {wz:F0}) " +
                $"heading {wh:F0} (shipped blend 0.25 measures 0.817 m)");
        }
    }
}