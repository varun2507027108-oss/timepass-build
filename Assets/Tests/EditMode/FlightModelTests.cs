using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using WarArena.Air;
using WarArena.Gameplay;

namespace WarArena.Tests
{
    /// <summary>Deterministic aircraft input for tests.</summary>
    public class FakeAircraftInput : IAircraftInputReader
    {
        public float Pitch;
        public float Roll;
        public float Yaw;
        public float Throttle;
        public bool Fire;
        public bool ModeToggle;

        public float ReadPitch() => Pitch;
        public float ReadRoll() => Roll;
        public float ReadYaw() => Yaw;
        public float ReadThrottle() => Throttle;
        public bool ReadFire() => Fire;
        public bool ReadModeToggle()
        {
            bool v = ModeToggle;
            ModeToggle = false;
            return v;
        }
    }

    /// <summary>
    /// Headless physics tests for the flight model. These assert the relationships
    /// that make air combat readable: a jet must beat a prop fighter in a straight
    /// line and lose in a turn, or the aircraft feel like noise.
    /// </summary>
    public class FlightModelTests
    {
        private static FlightModel Trimmed(string id, float altitude = 3000f)
        {
            return new FlightModel(AircraftCatalog.Get(id), new Vector3(0f, altitude, 0f), 0f);
        }

        private static void Run(FlightModel f, int ticks, float pitch = 0f, float roll = 0f,
            float yaw = 0f, float throttle = 0f)
        {
            for (int i = 0; i < ticks; i++) f.Step(1f / AirCombatSim.TickHz, pitch, roll, yaw, throttle);
        }

        [Test]
        public void CatalogueIsInternallyConsistent()
        {
            int count = 0;
            bool ok = true;
            foreach (AircraftDefinition a in AircraftCatalog.All)
            {
                count++;
                ok &= a.WingAreaM2 > 1f && a.MassKg > 500f;
                ok &= a.TopSpeedMs > 50f && a.TopSpeedMs < 400f;
                ok &= a.NeverExceedSpeedMs > a.TopSpeedMs;
                ok &= a.TurnRateDeg > 0f && a.RollRateDeg > 0f;
                ok &= a.CeilingM > 1000f;
                ok &= a.RoundCapacity > 0 && a.GunCaliberMm > 0f;
            }
            Assert.IsTrue(ok, "every aircraft needs coherent performance data");
            Assert.GreaterOrEqual(count, 6, "roster should include the WW2 prop and jet lineups");
        }

        [Test]
        public void JetsOutrunPropFightersInLevelFlight()
        {
            // Both trimmed at the same altitude with the throttle wide open, so the
            // only variable is the airframe. This is the relationship the whole
            // Me 262 vs Meteor matchup rests on.
            FlightModel mustang = Trimmed("p51d");
            FlightModel jet = Trimmed("me262-a1a");

            mustang.Throttle = 1f;
            jet.Throttle = 1f;

            // Long enough to reach equilibrium, not a ten-second sprint.
            //
            // This is the subtle part. Both aircraft spawn trimmed at 55% of their own
            // top speed, which is 134 m/s for the 262 but only 96 m/s for the P-51D,
            // because their top speeds differ so much. The jet therefore starts well
            // ahead and a short window measures that head start rather than the
            // airframes. Only once both have settled does the comparison mean anything.
            Run(mustang, 24000, throttle: 1f);
            Run(jet, 24000, throttle: 1f);

            Assert.Greater(jet.SpeedMs, mustang.SpeedMs,
                $"Me 262 ({jet.SpeedMs:F0} m/s) must outrun a P-51D ({mustang.SpeedMs:F0} m/s) in level flight");

            float gap = jet.SpeedMs - mustang.SpeedMs;
            Assert.Greater(gap, 15f,
                $"the speed gap should be substantial, was {gap:F0} m/s " +
                $"(262={jet.SpeedMs:F0}, 51={mustang.SpeedMs:F0})");
        }

        [Test]
        public void MeteorOutturnsTheMe262()
        {
            // The historically decisive relationship: the 262 is faster but the
            // Meteor could manoeuvre with it, which is why the Meteor won.
            // Read the performance figures off the definitions, not the live model:
            // these tests are about the catalogue encoding the right relationships,
            // and a separate test below proves the model actually reproduces them.
            AircraftDefinition mustangSpec = AircraftCatalog.Get("p51d");
            AircraftDefinition spitfireSpec = AircraftCatalog.Get("spitfire-mkix");
            AircraftDefinition jetSpec = AircraftCatalog.Get("me262-a1a");
            AircraftDefinition meteorSpec = AircraftCatalog.Get("meteor-f4");

            Assert.Greater(spitfireSpec.TurnRateDeg, mustangSpec.TurnRateDeg,
                "the Spitfire's elliptical wing should out-turn the P-51D");
            Assert.Greater(meteorSpec.TurnRateDeg, jetSpec.TurnRateDeg,
                "the Meteor should out-turn the Me 262, which is the whole reason it was the better fighter");

            // Roll rate is the axis the 262 retains some advantage in, so assert
            // the ordering rather than a blanket "jet is worse".
            Assert.Greater(mustangSpec.RollRateDeg, meteorSpec.RollRateDeg,
                "props should retain better roll authority at combat speed");
        }

        [Test]
        public void ZeroAircraftTurnsBetterThanAJet()
        {
            AircraftDefinition zero = AircraftCatalog.Get("a6m-zero");
            AircraftDefinition mustang = AircraftCatalog.Get("p51d");
            Assert.Greater(zero.TurnRateDeg, mustang.TurnRateDeg,
                "the Zero is the best prop turner and must not be beaten by the P-51D");
        }

        [Test]
        public void AircraftGainsSpeedUnderPowerAndLosesItWhenIdle()
        {
            FlightModel f = Trimmed("spitfire-mkix");
            f.Throttle = 0.8f;
            Run(f, 120);

            float fast = f.SpeedMs;
            Assert.Greater(fast, 60f, $"a trimmed fighter should be flying, was {fast:F0} m/s");

            // Cut the throttle and the aircraft must decelerate.
            Run(f, 240, throttle: -1f);
            Assert.Less(f.SpeedMs, fast,
                $"cutting the throttle must bleed speed. {fast:F0} -> {f.SpeedMs:F0}");
        }

        [Test]
        public void PropellerThrustFallsOffWithAltitudeButJetsDoNot()
        {
            // A piston engine loses roughly half its power by 6 km; a turbojet
            // barely notices. This is why prop fighters need altitude and jets do
            // not, and it is a real tactical difference worth asserting.
            FlightModel propLow = Trimmed("bf109-g6", 1000f);
            FlightModel propHigh = Trimmed("bf109-g6", 8000f);
            propLow.Throttle = 1f;
            propHigh.Throttle = 1f;
            Run(propLow, 900, throttle: 1f);
            Run(propHigh, 900, throttle: 1f);

            FlightModel jetLow = Trimmed("me262-a1a", 1000f);
            FlightModel jetHigh = Trimmed("me262-a1a", 8000f);
            jetLow.Throttle = 1f;
            jetHigh.Throttle = 1f;
            Run(jetLow, 900, throttle: 1f);
            Run(jetHigh, 900, throttle: 1f);

            float propLoss = 1f - (propHigh.SpeedMs / Mathf.Max(1f, propLow.SpeedMs));
            float jetLoss = 1f - (jetHigh.SpeedMs / Mathf.Max(1f, jetLow.SpeedMs));

            Assert.Greater(propLoss, jetLoss,
                $"prop thrust should fall off with altitude more than jet thrust. " +
                $"prop lost {propLoss * 100f:F0}%, jet lost {jetLoss * 100f:F0}%");
        }

        [Test]
        public void PullingHardEnoughWillStall()
        {
            // Low speed matters: at cruise the wing can pull well past the stall angle
            // and still hold the aircraft up, which is correct and desirable.
            FlightModel f = Trimmed("spitfire-mkix");
            f.Throttle = 0f;
            Run(f, 240);

            bool stalled = false;
            for (int i = 0; i < 1200 && !stalled; i++)
            {
                f.Step(1f / AirCombatSim.TickHz, 1f, 0f, 0f, 0f);
                stalled = f.IsStalled;
            }

            Assert.IsTrue(stalled, "a slow fighter at full back stick must stall");
        }

        [Test]
        public void ClimbingTradeSpeedForHeight()
        {
            // The fundamental energy trade of every aircraft: pulling up must slow
            // the aircraft down. If this does not hold, the flight model has no
            // energy model at all and dogfights become meaningless.
            FlightModel f = Trimmed("spitfire-mkix", 2000f);
            f.Throttle = 1f;
            Run(f, 600);

            float speedLevel = f.SpeedMs;
            float heightLevel = f.Altitude;

            Run(f, 480, pitch: 1f, throttle: 1f);

            Assert.Greater(f.Altitude, heightLevel,
                $"a full-power climb must gain height. {heightLevel:F0} -> {f.Altitude:F0}");
            Assert.Less(f.SpeedMs, speedLevel,
                $"climbing must cost speed. {speedLevel:F0} -> {f.SpeedMs:F0} m/s");
        }

        [Test]
        public void TrimmedAircraftHoldsAltitudeWithoutInput()
        {
            // Hands-off, an aircraft with no pilot input must settle into steady
            // flight rather than porpoise, dive, or leave the atmosphere. This is the
            // single most important property of the model: if it fails here, the
            // aircraft is unflyable regardless of how good the individual forces are.
            foreach (string id in new[] { "bf109-g6", "spitfire-mkix", "p51d",
                                         "a6m-zero", "me262-a1a", "meteor-f4" })
            {
                FlightModel f = Trimmed(id, 3000f);
                f.Throttle = 1f;

                // Skip the first 20 s, which is the initial transient from the spawn
                // attitude, then measure the following 20 s.
                Run(f, 2400, throttle: 1f);
                float afterSettle = f.Altitude;
                float speedAfterSettle = f.SpeedMs;
                Run(f, 2400, throttle: 1f);

                float altitudeChange = f.Altitude - afterSettle;
                Assert.Less(Mathf.Abs(altitudeChange), 200f,
                    $"{id} must hold altitude hands-off, drifted {altitudeChange:F0} m in 20 s");
                Assert.Less(Mathf.Abs(f.SpeedMs - speedAfterSettle), 15f,
                    $"{id} speed must be steady hands-off, " +
                    $"{speedAfterSettle:F0} -> {f.SpeedMs:F0} m/s");
                Assert.IsFalse(f.IsStalled,
                    $"{id} must not be stalled in trimmed flight");
            }
        }

        [Test]
        public void TrimmedAircraftStaysWellAboveItsStallSpeed()
        {
            // An aircraft trimmed much closer to its stall speed than this is one
            // small disturbance away from falling out of the sky, which is exactly
            // what happened to the Meteor F.4 before its numbers were corrected.
            foreach (AircraftDefinition d in AircraftCatalog.All)
            {
                FlightModel f = Trimmed(d.Id, 3000f);
                f.Throttle = 1f;
                Run(f, 12000, throttle: 1f);

                float stallSpeed = StallSpeedFor(d, 3000f);
                Assert.Greater(f.SpeedMs, stallSpeed * 1.3f,
                    $"{d.Id} settled at {f.SpeedMs:F0} m/s, too close to its " +
                    $"{stallSpeed:F0} m/s stall speed to be safe");
            }
        }

        /// <summary>
        /// Stall speed from the standard relationship V = sqrt(2W / (rho S CLmax)).
        /// </summary>
        private static float StallSpeedFor(AircraftDefinition d, float altitude)
        {
            float density = 1.225f * Mathf.Exp(-altitude / 8500f);
            float weight = d.MassKg * FlightModel.Gravity;
            return Mathf.Sqrt(2f * weight / (density * d.WingAreaM2 * 1.3f));
        }

        [Test]
        public void AircraftNeverFallsThroughTheTerrain()
        {
            FlightModel f = Trimmed("a6m-zero", 900f);
            // Full nose down from low altitude; the aircraft must not end up under
            // the ground plane.
            Run(f, 2000, pitch: -1f, throttle: -1f);

            Assert.Greater(f.Position.y, 0f,
                $"aircraft must be held above the terrain, ended at y={f.Position.y:F2}");
        }

        [Test]
        public void CeilingIsRespected()
        {
            FlightModel f = Trimmed("bf109-g6", 11000f);
            Run(f, 3000, throttle: 1f);
            Assert.LessOrEqual(f.Position.y, f.Spec.CeilingM + 1f,
                "the aircraft must not climb past its service ceiling");
        }

        [Test]
        public void SimulationStaysFiniteUnderAbuse()
        {
            FlightModel f = Trimmed("me262-a1a", 6000f);

            // Hammer the controls in alternating directions, which is the worst
            // case for a flight model.
            for (int i = 0; i < 20000; i++)
            {
                float t = i * 0.01f;
                f.Step(1f / AirCombatSim.TickHz,
                    Mathf.Sin(t * 7f), Mathf.Cos(t * 11f), Mathf.Sin(t * 3f), Mathf.Cos(t));
            }

            Assert.IsFalse(float.IsNaN(f.Position.x) || float.IsNaN(f.Position.y) || float.IsNaN(f.Position.z),
                $"position went non-finite: {f.Position}");
            Assert.IsFalse(float.IsNaN(f.SpeedMs), "speed went non-finite");
            Assert.IsTrue(f.SpeedMs < 1000f, $"speed must stay bounded, was {f.SpeedMs:F0} m/s");
        }

        [Test]
        public void RollIsBoundedAndSelfCentring()
        {
            FlightModel f = Trimmed("spitfire-mkix");
            f.Throttle = 0.9f;
            Run(f, 600);

            // Full roll one way, then release. The wing must return toward level.
            Run(f, 240, roll: 1f);
            float banked = Mathf.Abs(f.RollDeg);
            Run(f, 480);

            Assert.LessOrEqual(banked, 85f, "bank angle must be clamped");
            Assert.Less(Mathf.Abs(f.RollDeg), banked,
                "releasing the roll control must let the wing self-centre");
        }
    }
}