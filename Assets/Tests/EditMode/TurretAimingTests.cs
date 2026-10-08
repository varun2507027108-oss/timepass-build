using NUnit.Framework;
using UnityEngine;
using WarArena.Gameplay;

namespace WarArena.Tests
{
    /// <summary>
    /// Tests for the aiming and hull behaviour.
    ///
    /// The turret was previously *mathematically* correct and *felt* completely
    /// inverted. Measuring it directly showed why: the gun tracked the mouse in
    /// world space while the hull held whatever heading the driver chose, so the
    /// turret swung the opposite way to the tank every time the hull turned. These
    /// tests pin down the two things that actually matter to a player:
    ///
    /// - the barrel points at the aim point, whichever side of the tank it is
    /// - the hull follows the gun's line, so tank and turret move together
    ///
    /// The first is convention-free, because it compares the barrel against the
    /// target rather than against a sign. That makes it immune to the argument about
    /// whether positive yaw is left or right, which is exactly the kind of thing that
    /// hides an inversion.
    /// </summary>
    public class TurretAimingTests
    {
        private static BattleSim Settled(Vector3 aim, float throttle = 0f, float steer = 0f)
        {
            var sim = new BattleSim("t34-76", "pziv-h");
            // Long enough for the turret to complete its traverse.
            for (int i = 0; i < 240; i++)
            {
                sim.Advance(1f / 60f, throttle, steer, 0f, false, aim);
            }

            return sim;
        }

        /// <summary>
        /// Unit vector the barrel is actually pointing in, reconstructed exactly the
        /// way <c>TankView.Place</c> builds the transform.
        /// </summary>
        private static Vector3 GunDirection(BattleSim sim)
        {
            float gunWorldRad = sim.PlayerHeadingRad + sim.PlayerTurretYawRad;
            return new Vector3(Mathf.Sin(gunWorldRad), 0f, Mathf.Cos(gunWorldRad));
        }

        private static float GunToAimDot(BattleSim sim, Vector3 aim)
        {
            Vector3 pivot = sim.PlayerPosition +
                new Vector3(-Mathf.Sin(sim.PlayerHeadingRad) * 0.3f, 0f,
                    -Mathf.Cos(sim.PlayerHeadingRad) * 0.3f);
            Vector3 toAim = aim - pivot;
            toAim.y = 0f;
            return Vector3.Dot(GunDirection(sim), toAim.normalized);
        }
private static Vector3 AimFrom(Vector3 origin, float bearingDeg, float range)
        {
            float r = bearingDeg * Mathf.Deg2Rad;
            return origin + new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r)) * range;
        }

        [Test]
        public void BarrelPointsAtTheAimPointOnEverySide()
        {
            // The core correctness property, and deliberately free of sign
            // conventions: whatever the hull is doing, the barrel must end up pointing
            // at the target. An inverted turret scores about -1 here.
            //
            // All bearings stay inside the turret's real traverse arc. A target
            // directly astern is deliberately excluded: no real tank can elevate its
            // gun through 180 degrees of traverse, so MaxTraverse correctly refuses it
            // and the barrel lands at the arc limit rather than on the target.
            float[] bearings = { 0f, 30f, 60f, 90f, 110f, -30f, -60f, -90f, -110f };

            foreach (float bearing in bearings)
            {
                var sim = new BattleSim("t34-76", "pziv-h");
                Vector3 aim = AimFrom(sim.PlayerPosition, bearing, 60f);
                for (int i = 0; i < 240; i++)
                {
                    sim.Advance(1f / 60f, 0f, 0f, 0f, false, aim);
                }

                float dot = GunToAimDot(sim, aim);
                Assert.Greater(dot, 0.9f,
                    $"barrel must point at a target {bearing:F0} deg off the bow, " +
                    $"dot was {dot:F3} (negative means the turret is inverted)");
            }
        }

        [Test]
        public void TurretStopsAtItsTraverseLimitRatherThanPassingThrough()
        {
            // A target astern is unreachable, and the correct behaviour is to stop at
            // the arc limit. Overrunning it is what put the barrel on the wrong side of
            // the tank, which reads to a player as the turret having inverted.
            var sim = new BattleSim("t34-76", "pziv-h");
            Vector3 behind = new Vector3(sim.PlayerPosition.x, 0f, sim.PlayerPosition.z - 60f);
            for (int i = 0; i < 600; i++)
            {
                sim.Advance(1f / 60f, 0f, 0f, 0f, false, behind);
            }

            Assert.LessOrEqual(Mathf.Abs(sim.PlayerTurretYawRad), BattleSim.MaxTraverse + 1e-4f,
                "the turret must stop at its arc limit");
            Assert.Greater(Mathf.Abs(sim.PlayerTurretYawRad), BattleSim.MaxTraverse * 0.9f,
                "the turret should be hard against its limit when aimed astern");
        }

        [Test]
        public void HullTurnRateStillRespectsManualSteering()
        {
            // The auto-align must not swallow A/D. Both directions must still rotate
            // the hull in opposite senses.
            //
            // Deliberately a short burst: at 1.8 rad/s the hull covers 180 degrees in
            // about 1.7 s, so a longer test wraps past plus or minus pi and the wrapped
            // sign stops agreeing with the direction actually turned.
            const int ticks = 20;

            BattleSim left = new BattleSim("t34-76", "pziv-h");
            for (int i = 0; i < ticks; i++)
            {
                left.Advance(1f / 60f, 0f, -1f, 0f, false, new Vector3(0f, 0f, 60f));
            }

            BattleSim right = new BattleSim("t34-76", "pziv-h");
            for (int i = 0; i < ticks; i++)
            {
                right.Advance(1f / 60f, 0f, 1f, 0f, false, new Vector3(0f, 0f, 60f));
            }

            float leftChange = left.PlayerHeadingRad;
            float rightChange = right.PlayerHeadingRad;

            Assert.Greater(Mathf.Abs(leftChange), 0.01f, "steering left must turn the hull");
            Assert.Greater(Mathf.Abs(rightChange), 0.01f, "steering right must turn the hull");

            // Positive heading swings the nose from +Z toward +X, which is a turn to
            // the right. So the right-hand key must produce a positive change.
            Assert.Greater(rightChange, 0f,
                $"steering right must swing the hull to positive yaw, got " +
                $"{rightChange * Mathf.Rad2Deg:F1} deg");
            Assert.Less(leftChange, 0f,
                $"steering left must swing the hull to negative yaw, got " +
                $"{leftChange * Mathf.Rad2Deg:F1} deg");
        }

        [Test]
        public void HullFollowsTheGunLineWhileDriving()
        {
            // This is the fix for the turret feeling like it moved opposite to the
            // tank. The aim point tracks alongside the moving tank so the target stays
            // reachable; the point of the test is the turret's angle off the hull
            // centreline shrinking as the vehicle drives.
            var sim = new BattleSim("t34-76", "pziv-h");

            // Gun held 70 degrees off to the right of the hull throughout.
            Vector3 aim = AimFrom(sim.PlayerPosition, 70f, 70f);
            for (int i = 0; i < 240; i++)
            {
                sim.Advance(1f / 60f, 0f, 0f, 0f, false, aim);
            }

            float traverseBefore = sim.PlayerTurretYawRad;
            Assert.Greater(traverseBefore, 0.5f,
                "precondition: the turret should start well off the centreline");

            for (int i = 0; i < 600; i++)
            {
                // Target holds station off the tank's starboard bow as it drives.
                aim = AimFrom(sim.PlayerPosition, 70f, 70f);
                sim.Advance(1f / 60f, 1f, 0f, 0f, false, aim);
            }

            Assert.Less(Mathf.Abs(sim.PlayerTurretYawRad), Mathf.Abs(traverseBefore) * 0.8f,
                $"driving should reduce the turret's angle off the centreline so the " +
                $"hull lines up with the gun. {traverseBefore * Mathf.Rad2Deg:F1} -> " +
                $"{sim.PlayerTurretYawRad * Mathf.Rad2Deg:F1} deg");

            Assert.Greater(GunToAimDot(sim, aim), 0.9f,
                "the gun must still be pointing at the target after lining up");
        }
        [Test]
        public void StationaryTankHoldsItsTraverseForAiming()
        {
            // A parked tank must not slowly swing onto the gun line, or the player
            // loses the ability to set up a shot at a specific angle before moving.
            Vector3 aim = new Vector3(-70f, 0f, -35f);

            BattleSim sim = Settled(aim);
            float before = sim.PlayerTurretYawRad;
            for (int i = 0; i < 600; i++)
            {
                sim.Advance(1f / 60f, 0f, 0f, 0f, false, aim);
            }

            Assert.AreEqual(before * Mathf.Rad2Deg, sim.PlayerTurretYawRad * Mathf.Rad2Deg, 1f,
                "a stationary tank must hold its traverse");
        }

        [Test]
        public void TurretNeverLeavesItsTraverseArc()
        {
            // Regression test. The trim was applied after the arc clamp, so it could
            // push the turret back out through the limit, leaving the barrel pointing
            // on the far side of the tank from the target -- which a player reads as
            // the turret having inverted.
            var sim = new BattleSim("t34-76", "pziv-h");

            Vector3[] aims =
            {
                new Vector3(200f, 0f, -35f), new Vector3(-200f, 0f, -35f),
                new Vector3(0f, 0f, 200f), new Vector3(0f, 0f, -200f),
            };

            for (int step = 0; step < 900; step++)
            {
                Vector3 aim = aims[step % aims.Length];

                // Slam the trim back and forth, which is what used to break the clamp.
                float trim = ((step / 30) % 2 == 0) ? 1f : -1f;

                sim.Advance(1f / 60f, 1f, (step % 7 == 0) ? 1f : -1f, trim, false, aim);

                Assert.LessOrEqual(Mathf.Abs(sim.PlayerTurretYawRad), BattleSim.MaxTraverse + 1e-4f,
                    $"turret left its arc at step {step}: " +
                    $"{sim.PlayerTurretYawRad * Mathf.Rad2Deg:F1} deg");
            }
        }

        [Test]
        public void RenderInterpolationRemovesSimulationStepping()
        {
            // The vehicles were placed at the raw simulation position while the camera
            // moved smoothly, so the tank visibly juddered against a steady
            // background. The drawn position is now interpolated between ticks.
            var sim = new BattleSim("t34-76", "pziv-h");

            Vector3 last = sim.PlayerRenderPosition;
            float biggestJump = 0f;

            for (int frame = 0; frame < 600; frame++)
            {
                sim.Advance(1f / 60f, 1f, 0f, 0f, false, new Vector3(0f, 0f, 60f));
                Vector3 now = sim.PlayerRenderPosition;
                biggestJump = Mathf.Max(biggestJump, Vector3.Distance(last, now));
                last = now;
            }

            // At 12 m/s a 60 Hz simulation step advances 0.2 m. Interpolated rendering
            // at 60 fps should stay at or below that, and critically must not be zero
            // or wildly larger, which is what stepping in whole ticks looks like.
            Assert.Less(biggestJump, 0.5f,
                $"render position moved {biggestJump:F3} m in one frame, which is a visible step");
            Assert.Greater(biggestJump, 0.05f,
                "render position must actually move while driving");
        }

        [Test]
        public void RenderInterpolationStaysBetweenSimulationTicks()
        {
            // Interpolation must never extrapolate past the current simulation state,
            // or the drawn tank leads its own gunnery solution.
            var sim = new BattleSim("t34-76", "pziv-h");

            for (int frame = 0; frame < 300; frame++)
            {
                sim.Advance(1f / 50f, 1f, 0.4f, 0f, false, new Vector3(40f, 0f, 60f));

                Assert.GreaterOrEqual(sim.InterpolationAlpha, 0f);
                Assert.LessOrEqual(sim.InterpolationAlpha, 1f,
                    "interpolation alpha must stay in 0..1");
                Assert.LessOrEqual(Vector3.Distance(sim.PlayerRenderPosition, sim.PlayerPosition), 0.5f,
                    "the drawn position must not run ahead of the simulation state");
            }
        }
    }
}
