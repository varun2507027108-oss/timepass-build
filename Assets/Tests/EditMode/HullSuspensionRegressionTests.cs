using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using WarArena.Gameplay;

namespace WarArena.Tests
{
    /// <summary>
    /// Regression tests for two defects found by comparing the C# and Rust
    /// suspension implementations against each other rather than against a spec.
    ///
    /// Both were invisible to the existing suite, and for the same reason: every
    /// suspension scenario in it happens to produce the same answer whichever way
    /// the code is wrong. These tests are written specifically to hit the case that
    /// distinguishes correct from incorrect.
    /// </summary>
    [TestFixture]
    public class HullSuspensionRegressionTests
    {
        private const float Step = 1f / 30f;

        /// <summary>
        /// The Rust clamp, read through the FFI rather than duplicated as a literal.
        ///
        /// Asserting against a hard-coded 50 here would only re-assert the number the
        /// test itself is checking; reading the live Rust value is what actually
        /// catches the two implementations drifting apart.
        /// </summary>
        [DllImport("war_core_ffi")]
        private static extern float wa_battle_max_tilt_deg();

        private const float PlayerMassKg = 26000f;

        private static HullSuspension NewHull()
        {
            // Built the same way HullSuspensionTests does -- from the contact
            // footprint the shipped vehicle actually has, not the generic wheelbase
            // constants, so the layout under test really is the eight-contact one.
            return new HullSuspension(PlayerMassKg,
                TrackedVehicleDynamics.ContactHalfLengthM(PlayerMassKg),
                TrackedVehicleDynamics.ContactHalfWidthM(PlayerMassKg),
                TrackedVehicleDynamics.ContactCentreZM(PlayerMassKg));
        }

        /// <summary>
        /// Reset must place the hull at the MEAN of its contact samples.
        /// </summary>
        /// <remarks>
        /// The reset averaged the eight contact heights with a hard-coded
        /// <c>0.25f</c> -- the reciprocal of the original four-contact layout --
        /// rather than dividing by the contact count. On flat ground every sample is
        /// equal, so the factor cancels and nothing looks wrong; on a slope the
        /// eight samples average to the hull-centre height, and doubling that drops
        /// the hull toward the terrain, which the bump stops then have to fight.
        ///
        /// The assertion below is a direct check of the arithmetic rather than a
        /// settle-and-hope: the hull height set by Reset is compared against the
        /// mean of the same eight ground samples.
        /// </remarks>
        [Test]
        public void ResetPlacesTheHullAtTheMeanOfItsContactsNotTwiceIt()
        {
            // Find a genuinely sloped spot so the mean and double-the-mean differ.
            float x = 0f;
            float z = 0f;
            bool found = false;
            for (float probe = 0f; probe < 400f && !found; probe += 7f)
            {
                float a = BattlefieldBuilder.GetTerrainHeight(probe, 0f);
                float b = BattlefieldBuilder.GetTerrainHeight(probe, 4f);
                if (Mathf.Abs(a - b) > 1.0f) { x = probe; z = 2f; found = true; }
            }
            Assert.IsTrue(found, "no sloped test location found in the arena");

            HullSuspension s = NewHull();
            s.Reset(x, z, 0f);

            // Independent mean over the same eight contacts the suspension samples, rebuilt
            // from the layout it documents: two per track at z = {-h, -h/3, +h/3, +h},
            // each offset by the patch centre, at x = +/- half track width. Reusing
            // the production arithmetic here would make the test tautological, so it
            // is written out longhand -- and it is also a second, independent check
            // that the layout is what the comments claim.
            float h = TrackedVehicleDynamics.ContactHalfLengthM(PlayerMassKg);
            float w = TrackedVehicleDynamics.ContactHalfWidthM(PlayerMassKg);
            float centreZ = TrackedVehicleDynamics.ContactCentreZM(PlayerMassKg);

            float sum = 0f;
            float[] zOffsets = { -h, -h / 3f, h / 3f, h };
            foreach (float zo in zOffsets)
            {
                float cz = zo + centreZ;
                foreach (float cx in new[] { -w, w })
                {
                    sum += BattlefieldBuilder.GetTerrainHeight(x + cx, z + cz);
                }
            }
            float mean = sum / 8f;

            Assert.That(s.height, Is.EqualTo(mean).Within(0.05f),
                $"Reset height {s.height:F3} should be the contact mean {mean:F3}, " +
                "not a multiple of it");
        }

        [Test]
        public void ResetHeightMatchesOnFlatGround()
        {
            // Guards the arithmetic itself: on level ground the mean is exact and
            // any error factor shows up immediately.
            HullSuspension s = NewHull();
            s.Reset(0f, 0f, 0f);
            Assert.That(s.height, Is.EqualTo(BattlefieldBuilder.GetTerrainHeight(0f, 0f)).Within(0.01f));
        }

        /// <summary>
        /// The attitude clamp must agree between the two implementations.
        /// </summary>
        /// <remarks>
        /// C# pinned pitch and roll at 40 degrees while Rust used 50. No test caught
        /// it because the ordinary cases -- braking, cornering, an outer-rim launch
        /// -- all peak far below 40, so both implementations returned identical
        /// values and the parity traces matched. Only a pathological terrain sample
        /// reaches the clamp, and that is precisely where the two languages
        /// diverged.
        ///
        /// Reading the private field directly is deliberate: the clamp is an internal
        /// backstop that no reachable gameplay scenario exercises, and a test that
        /// had to provoke a 40-degree tilt to observe it would be asserting on a
        /// scenario the vehicle cannot actually get into.
        /// </remarks>
        [Test]
        public void AttitudeClampAgreesWithTheRustImplementation()
        {
            System.Reflection.MethodInfo m = typeof(HullSuspension)
                .GetMethod("MaxTiltRad", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(m, "MaxTiltRad not found -- the clamp may have been renamed");

            float csharp = (float)m.Invoke(null, null);
            float rustDegrees = wa_battle_max_tilt_deg();

            Assert.That(Mathf.Abs(csharp * Mathf.Rad2Deg - rustDegrees), Is.LessThan(0.01f),
                $"C# clamp is {csharp * Mathf.Rad2Deg:F2} deg, Rust is {rustDegrees:F2} deg");
            Assert.That(rustDegrees, Is.InRange(45f, 55f),
                "the clamp should sit near 50 deg: tight enough to be a safety net, " +
                "loose enough that ordinary terrain cannot reach it");
        }
    }
}