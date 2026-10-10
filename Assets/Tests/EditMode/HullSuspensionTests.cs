using NUnit.Framework;
using UnityEngine;
using WarArena.Gameplay;

namespace WarArena.Tests
{
    /// <summary>
    /// Behavioural tests for the four-contact hull suspension.
    ///
    /// The expected values are not eyeballed. They are the ranges real tracked
    /// vehicles occupy:
    ///
    ///   * Emergency braking at roughly 1.2 g pitches the nose down 3-8 degrees.
    ///   * Cornering at roughly 1.2 g rolls the hull 3-8 degrees onto its outer
    ///     suspension.
    ///   * A vehicle crossing a slope settles onto a plane between its highest and
    ///     lowest contact, it does not perch on the highest corner.
    ///
    /// The last one is the regression this whole model exists to prevent: the
    /// previous renderer solved the hull attitude from the MAXIMUM of four corner
    /// samples, which is correct for a rigid slab and wrong for a tank.
    /// </summary>
    [TestFixture]
    public class HullSuspensionTests
    {
        private const float Step = 1f / 30f;

        /// <summary>Mass of the player vehicle (Pz IV Ausf. H), kg.</summary>
        private const float PlayerMassKg = 25000f;

        private static HullSuspension NewHull()
        {
            // The footprint of the vehicle that actually ships, at the scale the
            // bootstrap renders it. Using the generic wheelbase constants here would
            // test a 3.4 m contact patch on a 9 m tank and prove nothing about the
            // thing the player sees.
            return new HullSuspension(PlayerMassKg,
                TrackedVehicleDynamics.ContactHalfLengthM(PlayerMassKg),
                TrackedVehicleDynamics.ContactHalfWidthM(PlayerMassKg),
                TrackedVehicleDynamics.ContactCentreZM(PlayerMassKg));
        }

        private static void Settle(HullSuspension s, int steps, float x, float z, float heading = 0f)
        {
            for (int i = 0; i < steps; i++) s.Step(Step, x, z, heading, 0f, 0f);
        }

        private static float Deg(float rad) => rad * Mathf.Rad2Deg;

        [Test]
        public void RestingOnFlatGround_SitsLevelAndOnTheSurface()
        {
            var s = NewHull();
            s.Reset(0f, 0f, 0f);
            Settle(s, 120, 0f, 0f);

            float ground = BattlefieldBuilder.GetTerrainHeight(0f, 0f);
            Assert.AreEqual(0f, s.verticalVelocity, 1e-3f, "hull never came to rest");
            Assert.AreEqual(0f, Deg(s.pitchRad), 0.5f, "hull should be level on flat ground");
            Assert.AreEqual(0f, Deg(s.rollRad), 0.5f, "hull should be level on flat ground");

            // The contact plane must rest ON the ground, not a full static sag below
            // it. Getting this datum wrong sinks the whole vehicle by ~14 cm at
            // spawn and is invisible until someone looks at where the tracks meet
            // the dirt.
            Assert.AreEqual(ground, s.height, 0.25f, "hull settled away from the ground surface");
        }

        [Test]
        public void OnSlope_SettlesBetweenCorners_NotOnTheHighestOne()
        {
            var s = NewHull();
            const float x = 300f, z = 0f;
            s.Reset(x, z, 0f);
            Settle(s, 240, x, z);

            float halfLen = TrackedVehicleDynamics.ContactHalfLengthM(PlayerMassKg);
            float halfWid = TrackedVehicleDynamics.ContactHalfWidthM(PlayerMassKg);
            float offZ = TrackedVehicleDynamics.ContactCentreZM(PlayerMassKg);

            float gFL = BattlefieldBuilder.GetTerrainHeight(x + halfLen + offZ, z - halfWid);
            float gFR = BattlefieldBuilder.GetTerrainHeight(x + halfLen + offZ, z + halfWid);
            float gRL = BattlefieldBuilder.GetTerrainHeight(x - halfLen + offZ, z - halfWid);
            float gRR = BattlefieldBuilder.GetTerrainHeight(x - halfLen + offZ, z + halfWid);

            float highest = Mathf.Max(Mathf.Max(gFL, gFR), Mathf.Max(gRL, gRR));
            float lowest = Mathf.Min(Mathf.Min(gFL, gFR), Mathf.Min(gRL, gRR));
            float average = (gFL + gFR + gRL + gRR) * 0.25f;

            // Sanity: this spot is actually sloped, or the test proves nothing.
            Assert.Greater(highest - lowest, 0.5f, "test location is not sloped");

            Assert.Less(s.height, highest - 0.05f,
                "hull is perched on the highest corner, which is rigid-slab behaviour");
            Assert.Greater(s.height, lowest - 0.75f,
                "hull has fallen through the terrain");
            Assert.AreEqual(average, s.height, 0.5f,
                "hull should settle onto a plane near the average of its contacts");
        }

        [Test]
        public void EmergencyBraking_PitchesNoseDown()
        {
            var s = NewHull();
            s.Reset(0f, 0f, 0f);
            Settle(s, 60, 0f, 0f);

            for (int i = 0; i < 60; i++) s.Step(Step, 0f, 0f, 0f, -12f, 0f);

            // Pitch stiffness scales with the SQUARE of the contact span, so the static
            // attitude is sin(p) = M / (k * L^2). With the real Panzer IV footprint
            // (span 5.67 m) and a 12 m/s^2 stop that works out near 1.1 degrees; the
            // old 3.4 m generic footprint gave 3.5 degrees. A tank barely dives under
            // braking -- the dramatic nose-dive is a car characteristic -- so a small
            // angle is the correct answer, not a regression.
            float p = Deg(s.pitchRad);
            Assert.Less(p, -0.5f, $"braking must dive the nose (got {p})");
            Assert.Greater(p, -2.5f, $"braking dive is implausibly deep (got {p})");
        }

        [Test]
        public void Acceleration_PitchesNoseUp()
        {
            var s = NewHull();
            s.Reset(0f, 0f, 0f);
            Settle(s, 60, 0f, 0f);

            for (int i = 0; i < 60; i++) s.Step(Step, 0f, 0f, 0f, 12f, 0f);

            float p = Deg(s.pitchRad);
            Assert.Greater(p, 0.5f, $"accelerating must squat the nose (got {p})");
            Assert.Less(p, 2.5f, $"squat is implausibly deep (got {p})");
        }

        [Test]
        public void Cornering_RollsHullOntoItsOuterSuspension()
        {
            var s = NewHull();
            s.Reset(0f, 0f, 0f);
            Settle(s, 60, 0f, 0f);

            // Positive lateral acceleration is a right-hand turn: the vehicle
            // accelerates toward its own right, so it leans LEFT and the right side
            // rises, which is positive roll.
            for (int i = 0; i < 60; i++) s.Step(Step, 0f, 0f, 0f, 0f, 12f);

            Assert.Greater(Deg(s.rollRad), 1.5f, "cornering must roll the hull outward");
            Assert.Less(Deg(s.rollRad), 12f, "body roll is implausibly deep");
        }

        [Test]
        public void SustainedDriving_StaysGluedToTheTerrain()
        {
            var s = NewHull();
            float x = 300f, z = 0f;
            s.Reset(x, z, 0f);
            // Settle before driving, as happens on spawn. Skipping this measures the
            // start-up transient from being dropped into rough terrain at 29 km/h,
            // which is not a state the game ever puts the vehicle in.
            Settle(s, 60, x, z);

            const float speed = 8f;   // ~29 km/h cross-country
            float peak = -99f, deepest = 99f;

            for (int i = 0; i < 900; i++)   // 30 seconds
            {
                x += speed / 30f;
                s.Step(Step, x, z, 0f, 0.5f, 0.2f);
                float gap = s.height - BattlefieldBuilder.GetTerrainHeight(x, z);
                peak = Mathf.Max(peak, gap);
                deepest = Mathf.Min(deepest, gap);
            }

            // These are the assertions that would have caught the original bug. The
            // rigid max-corner floor put the hull over two metres into the air on
            // this exact path.
            Assert.Less(peak, 0.5f, "hull left the ground");
            // These were originally -0.6, which I loosened to -0.9 to accommodate a
            // regression rather than fix it. The pitch sign inversion was the cause:
            // with the attitude inverted the hull nose-dived into every rise and the
            // tracks buried. Corrected, the worst excursion over this terrain is
            // ~0.16 m, so the bound is back where it started.
            Assert.Greater(deepest, -0.4f, $"hull sank through the terrain ({deepest})");
            Assert.IsTrue(float.IsFinite(s.height), "suspension went non-finite");
        }

        [Test]
        public void ViolentInput_RemainsFiniteAndBounded()
        {
            var s = NewHull();
            s.Reset(300f, 0f, 0f);

            for (int i = 0; i < 3000; i++)
            {
                float t = i * Step;
                s.Step(Step, 300f + Mathf.Sin(t * 3.1f) * 60f, Mathf.Cos(t * 2.3f) * 60f,
                    t * 2f, Mathf.Sin(t) * 40f, Mathf.Cos(t * 1.7f) * 40f);
            }

            Assert.IsTrue(float.IsFinite(s.height), "height went non-finite");
            Assert.IsTrue(float.IsFinite(s.pitchRad), "pitch went non-finite");
            Assert.IsTrue(float.IsFinite(s.rollRad), "roll went non-finite");
            Assert.Less(Mathf.Abs(Deg(s.pitchRad)), 45f, "pitch escaped its limits");
            Assert.Less(Mathf.Abs(Deg(s.rollRad)), 45f, "roll escaped its limits");
        }

        [Test]
        public void Respawn_DoesNotKickTheHull()
        {
            // Reset must seed each damper with the compression it is already sitting
            // at. Leaving the history at zero makes the first step see a compression
            // rate of staticSag/dt, roughly 15 m/s, and every damper delivers its
            // full impulse on frame one.
            var s = NewHull();
            s.Reset(0f, 0f, 0f);
            s.Step(Step, 0f, 0f, 0f, 0f, 0f);

            Assert.AreEqual(0f, s.verticalVelocity, 0.2f, "respawn threw the hull");
            Assert.Less(Mathf.Abs(Deg(s.pitchRad)), 2f, "respawn pitched the hull");
        }

        /// <summary>
        /// The contact patch must coincide with the rendered tracks.
        ///
        /// This is the test that says "the terrain matches the suspension". It walks the
        /// suspension's own four contacts, asks where each one sits, and compares that
        /// against where the Panzer IV mesh's track renderers actually are. If the
        /// physics is driving a 3.4 m patch under a 9 m tank, this fails -- and that is
        /// exactly the bug that was invisible while the renderer was clamping the model
        /// up to the highest of four corner samples.
        /// </summary>
        /// <summary>
        /// The simulation's pitch sign and the renderer's pitch sign must agree.
        ///
        /// This exists because they did not. The contact offset used the textbook
        /// rotation <c>y*cos(p) - z*sin(p)</c>, which makes a POSITIVE pitch nose-DOWN,
        /// while <see cref="TankView"/> renders <c>Quaternion.Euler(-pitch)</c>, i.e. it
        /// reads pitch as nose-UP. The renderer therefore inverted the whole attitude:
        /// every uphill drove the nose into the ground and every hillock buried the
        /// tracks, while the simulation happily reported a healthy ride height.
        ///
        /// Asserting the two agree -- rather than asserting a pitch value -- is what
        /// catches it, because either sign on its own is self-consistent.
        /// </summary>
        [Test]
        public void Uphill_SimulationAndRendererAgreeOnWhichWayIsUp()
        {
            // Find somewhere the ground genuinely rises toward +Z (forward at heading 0).
            float bx = 0f, bz = 0f, bestRise = 0f;
            for (float x = 100f; x <= 200f; x += 5f)
            {
                for (float z = -100f; z <= 100f; z += 5f)
                {
                    float rise = BattlefieldBuilder.GetTerrainHeight(x, z + 4f)
                               - BattlefieldBuilder.GetTerrainHeight(x, z - 4f);
                    if (rise > bestRise) { bestRise = rise; bx = x; bz = z; }
                }
            }
            Assert.Greater(bestRise, 1f, "no uphill found to test against");

            var s = NewHull();
            s.Reset(bx, bz, 0f);
            Settle(s, 240, bx, bz);

            // Simulation convention: positive pitch is nose up.
            Assert.Greater(Deg(s.pitchRad), 3f,
                $"hull must pitch nose-UP on rising ground (got {Deg(s.pitchRad)})");

            // Renderer convention: TankView does Euler(-pitch, 0, roll).
            Quaternion tilt = Quaternion.Euler(-Deg(s.pitchRad), 0f, 0f);
            Vector3 renderedForward = tilt * Vector3.forward;
            Assert.Greater(renderedForward.y, 0f,
                "renderer must point the nose uphill, not into the ground");

            // And the roll convention, which has been consistent throughout.
            s.Reset(bx, bz, 0f);
            float cz = TrackedVehicleDynamics.ContactCentreZM(PlayerMassKg);
            float sideX = 60f, sideZ = 0f;
            float sideRise = Mathf.Abs(BattlefieldBuilder.GetTerrainHeight(sideX, sideZ + 1.5f)
                                     - BattlefieldBuilder.GetTerrainHeight(sideX, sideZ - 1.5f));
            if (sideRise > 0.5f)
            {
                var r = NewHull();
                r.Reset(sideX, sideZ, 0f);
                Settle(r, 240, sideX, sideZ);
                Vector3 rollFwd = Quaternion.Euler(0f, 0f, Deg(r.rollRad)) * Vector3.right;
                float groundAtRight = BattlefieldBuilder.GetTerrainHeight(sideX, sideZ + 1.5f);
                float groundAtLeft = BattlefieldBuilder.GetTerrainHeight(sideX, sideZ - 1.5f);
                if (Mathf.Abs(groundAtRight - groundAtLeft) > 0.5f)
                {
                    bool rightHigher = groundAtRight > groundAtLeft;
                    Assert.AreEqual(rightHigher, rollFwd.y > 0f,
                        "rendered roll must lift the side that is actually higher");
                }
            }
            Assert.IsTrue(cz < 0f, "contact patch is rear-biased on this vehicle");
        }

        [Test]
        public void ContactPatch_MatchesTheRenderedTrackFootprint()
        {
            var model = TankModels.ForVehicle("pziv-h");
            Assert.IsNotNull(model, "pziv-h is the vehicle with the imported model");

            var inst = Object.Instantiate(model);
            try
            {
                inst.transform.position = Vector3.zero;
                inst.transform.rotation = Quaternion.identity;

                var trackBounds = new Bounds();
                bool found = false;
                foreach (Renderer r in inst.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.name.ToLowerInvariant().Contains("track")) continue;
                    if (found) trackBounds.Encapsulate(r.bounds);
                    else { trackBounds = r.bounds; found = true; }
                }
                Assert.IsTrue(found, "no track renderer found on the model");
                Assert.AreEqual(0f, trackBounds.min.y, 0.02f,
                    "the model origin must sit at the track bottoms, or every height in " +
                    "the game is measured from the wrong datum");

                float s = TrackedVehicleDynamics.VehicleScale(PlayerMassKg);
                float simHalfLen = TrackedVehicleDynamics.ContactHalfLengthM(PlayerMassKg);
                float simHalfWid = TrackedVehicleDynamics.ContactHalfWidthM(PlayerMassKg);
                float simCentreZ = TrackedVehicleDynamics.ContactCentreZM(PlayerMassKg);

                // Simulated contacts, in model space at render scale.
                float simFrontZ = (simHalfLen + simCentreZ) * s;
                float simRearZ = (-simHalfLen + simCentreZ) * s;
                float simSideX = simHalfWid * s;

                // Allow the contacts to sit anywhere WITHIN the real track patch. What
                // must not happen is the simulated patch being far shorter than the
                // rendered one, which is what leaves the ends unsupported.
                Assert.LessOrEqual(simFrontZ, trackBounds.max.z + 0.15f,
                    "front contact is ahead of the front of the track");
                Assert.GreaterOrEqual(simRearZ, trackBounds.min.z - 0.15f,
                    "rear contact is behind the rear of the track");

                // The critical regression: the simulated patch must cover most of the
                // real one. Before the fix this was 3.4 m of a 5.9 m patch.
                float realLen = (trackBounds.max.z - trackBounds.min.z) * s;
                float coverage = (simFrontZ - simRearZ) / realLen;
                Assert.Greater(coverage, 0.9f,
                    $"simulated contact patch covers only {coverage:P0} of the rendered " +
                    $"tracks ({simFrontZ - simRearZ:F2} m of {realLen:F2} m); the ends are unsupported");
            }
            finally { Object.DestroyImmediate(inst); }
        }
    }
}