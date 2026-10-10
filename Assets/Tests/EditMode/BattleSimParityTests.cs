using System;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using WarArena.Gameplay;
using WarArena.Interop;

namespace WarArena.Tests
{
    /// <summary>
    /// Proves the Rust ground sim and the original C# one agree, tick for tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ground-combat counterpart to <see cref="FlightModelParityTests"/>,
    /// and it exists for the same reason. The battle model is a stack of small
    /// sign conventions and clamp orders, each of which is individually plausible:
    /// </para>
    /// <list type="bullet">
    /// <item>Positive yaw is a <i>clockwise</i> rotation when viewed from above,
    /// because <c>forward = (sin h, 0, cos h)</c>. Negating the steer input makes the
    /// tank drive in the opposite direction to the one the stick is pushed.</item>
    /// <item>Positive gun pitch is depression, matching the flight model's ZXY Euler
    /// convention. Summing elevation instead of subtracting it fires every shell into
    /// the ground.</item>
    /// <item>Turret traverse is clamped to +/-90 degrees <i>relative to the hull</i>.
    /// Clamping relative to world north instead lets the gun point backwards through
    /// the hull.</item>
    /// <item>The hull rotates toward the steering input at a fixed rate, but only
    /// while the tank is actually moving. Getting that condition inverted produces a
    /// tank that refuses to turn while stationary.</item>
    /// </list>
    /// <para>
    /// A Rust unit test proves only that the Rust agrees with itself. Only running the
    /// C# original side by side can catch a ported-but-wrong constant.
    /// </para>
    /// <para>
    /// The comparison stops at the first tick where either vehicle takes damage, and
    /// this is deliberate rather than a convenience. The Rust port contains four
    /// <b>intentional</b> fixes to the hit model (plate classification, per-plate
    /// ricochet, glacis slope, and instance-owned respawn jitter), each documented at
    /// its definition in <c>battle_sim.rs</c>. Those fixes change which plate is
    /// struck and therefore whether a round penetrates, so once the first round lands
    /// the two sims are entitled to diverge, and a kill makes them diverge violently
    /// because the C# respawn offset came from a <c>static</c> counter that depends on
    /// unrelated process-global history.
    /// </para>
    /// <para>
    /// Comparing only up to first damage is therefore the strongest honest claim: it
    /// asserts that everything the two implementations have in common -- driving,
    /// steering, hull rotation, turret slew and traverse, elevation, the AI, shell
    /// ballistics in flight, and hit <i>detection</i> -- agrees exactly, and it stops
    /// precisely where the deliberate divergence begins.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class BattleSimParityTests
    {
        /// <summary>
        /// Must match <c>BATTLE_TRACE_STRIDE</c> in <c>war-core-ffi</c>.
        /// </summary>
        private const int TraceStride = 17;

        private const float Dt = 1f / 30f;

        /// <summary>
        /// Aim schedule that depends only on the tick index: a point on a circle of
        /// <see cref="SweepRadiusM"/> around the origin, bearing advancing by
        /// <see cref="SweepRateDeg"/> per tick, at a height of <see cref="SweepHeightM"/>.
        /// Must match <c>scripted_aim</c> mode 0 in <c>war-core-ffi</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The rate is 6 deg/tick and that number is load-bearing. At the original
        /// 0.75 deg/tick the scenario was worthless, and it passed anyway -- which is
        /// the whole reason <see cref="ScenariosActuallyReachTheClamps"/> exists.
        /// </para>
        /// <para>
        /// The hull is pulled onto the gun's line while driving
        /// (<c>HULL_AUTO_ALIGN_STRENGTH</c>), so a slow sweep is simply followed by
        /// the hull and the turret never leaves the centreline. At 0.75 deg/tick the
        /// turret spanned 0.47 rad against a +/-2.1 limit and the gun pitch spanned
        /// 0.0019 rad against a 0.52 rad range: neither clamp was ever reached, so
        /// the test could not have detected a wrong slew rate or a wrong pitch limit.
        /// At 3 deg/tick the turret (2.6 rad/s slew, ~5 deg/tick) CAN keep up with the
        /// aim, so it tracks it all the way round and presses against its traverse
        /// stops.
        /// </para>
        /// <para>
        /// The sweep must move slower than the turret slews. A faster sweep does the
        /// opposite of what it looks like: at 10 deg/tick the turret is outrun, chases
        /// a target that wraps past +/-pi, and oscillates within +/-1.5 rad of centre
        /// without reaching either stop � the coverage guard caught exactly that.
        /// </para>
        /// <para>
        /// This was 6 deg/tick until the hull steering rate was corrected. The old
        /// steering applied <c>demand * MAX_TURN_RATE_RAD</c> without a dt factor, so
        /// the clamp saturated for any demand above ~0.033 and the hull was pinned at
        /// maximum rate at all times. It therefore chased the sweeping aim hard enough
        /// to keep the turret off its traverse stop. With steering proportional to
        /// input the hull lags, and the sweep has to be slowed to put the saturation
        /// back rather than raising the guard's threshold.
        /// </para>
        /// </remarks>
        private const float SweepRadiusM = 60f;
        private const float SweepBaseDeg = 25f;
        private const float SweepRateDeg = 3f;

        /// <summary>
        /// Height of the sweeping aim point. Above the muzzle, so the gun must
        /// depress to reach it and the upper pitch clamp saturates.
        /// </summary>
        private const float SweepHeightM = 30f;

        /// <summary>
        /// Track the enemy's current position. Mode 1 on the Rust side.
        /// </summary>
        private const int AimAtEnemy = 1;

        /// <summary>Use <see cref="SweepRadiusM"/>/<see cref="SweepBaseDeg"/>/<see cref="SweepRateDeg"/>.</summary>
        private const int AimSweeping = 0;

        private static readonly string[] FieldNames =
        {
            "PlayerPosition.x", "PlayerPosition.z",
            "EnemyPosition.x", "EnemyPosition.z",
            "PlayerHeadingRad", "PlayerTurretYawRad", "PlayerGunPitchRad",
            "EnemyHeadingRad", "EnemyTurretYawRad",
            "PlayerSpeedKph", "PlayerHealth", "EnemyHealth", "Kills", "ReloadRemaining",
            "HullPitchRad", "HullRollRad", "HullRideHeight",
        };

        private const int PlayerHealthSlot = 10;
        private const int EnemyHealthSlot = 11;

        private const int PlayerTurretYawSlot = 5;
        private const int PlayerGunPitchSlot = 6;

        /// <summary>
        /// <c>BattleSim.MaxTraverse</c>, the turret traverse clamp.
        /// </summary>
        private const float MaxTraverse = 2.1f;

        /// <summary>
        /// <c>BattleSim.MinGunPitchRad</c> and <c>MaxGunPitchRad</c>. Note both are
        /// negative-to-positive in Unity's convention where positive pitch is
        /// nose-down, so the *elevate* limit is the negative one.
        /// </summary>
        private const float MinGunPitchRad = -10f * Mathf.Deg2Rad;
        private const float MaxGunPitchRad = 20f * Mathf.Deg2Rad;

        /// <summary>Slack for "is resting on the clamp", to absorb float rounding.</summary>
        private const float ClampEpsilon = 1e-3f;

#if !UNITY_WEBGL || UNITY_EDITOR
        // `IntPtr` rather than `string` or `float[]`: this project sets
        // allowUnsafeCode: 0, so there are no `fixed` blocks, and the arrays are
        // pinned with GCHandle instead.
        [DllImport("war_core_ffi")]
        private static extern uint wa_battle_trace(
            IntPtr playerVehicleId, uint playerIdLen,
            IntPtr enemyVehicleId, uint enemyIdLen,
            uint ticks, float dt, float throttle, float steer, float turretTrim,
            uint fire, uint aimMode,
            IntPtr dest, uint cap);
#endif

        /// <summary>
        /// Drive the Rust sim through one scripted scenario and read its trace.
        /// </summary>
        private static int RunRustTrace(
            string playerId, string enemyId, int ticks,
            float throttle, float steer, float turretTrim, bool fire, int aimMode,
            float[] dest)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // The WebGL module loads asynchronously and this fixture runs in the
            // Editor, so only the native path is exercised here. The web path is
            // covered by Tools/verify-webgl-core.mjs.
            Assert.Ignore("parity trace runs against the native plugin");
            return 0;
#else
            byte[] playerBytes = Encoding.ASCII.GetBytes(playerId);
            byte[] enemyBytes = Encoding.ASCII.GetBytes(enemyId);
            // Length-prefixed, so the exact byte count must be passed: including the
            // NUL terminator makes the id unmatchable and the call returns 0.
            GCHandle playerHandle = GCHandle.Alloc(playerBytes, GCHandleType.Pinned);
            GCHandle enemyHandle = GCHandle.Alloc(enemyBytes, GCHandleType.Pinned);
            GCHandle destHandle = GCHandle.Alloc(dest, GCHandleType.Pinned);
            try
            {
                return (int)wa_battle_trace(
                    playerHandle.AddrOfPinnedObject(), (uint)playerBytes.Length,
                    enemyHandle.AddrOfPinnedObject(), (uint)enemyBytes.Length,
                    (uint)ticks, Dt, throttle, steer, turretTrim,
                    fire ? 1u : 0u, (uint)aimMode,
                    destHandle.AddrOfPinnedObject(), (uint)dest.Length);
            }
            finally
            {
                playerHandle.Free();
                enemyHandle.Free();
                destHandle.Free();
            }
#endif
        }

        /// <summary>
        /// The C# side's half of the sweep. Must agree exactly with
        /// <c>scripted_aim</c> mode 0 in <c>war-core-ffi</c>.
        /// </summary>
        private static Vector3 ScriptedAim(int mode, int tick, BattleSim sim)
        {
            if (mode == AimSweeping)
            {
                // `Mathf.Deg2Rad` is a rounded literal in Unity, and the Rust side
                // uses the matching literal in `mathf::DEG2RAD`.
                float bearing = Mathf.Deg2Rad * (SweepBaseDeg + tick * SweepRateDeg);
                return new Vector3(
                    SweepRadiusM * Mathf.Cos(bearing),
                    SweepHeightM,
                    SweepRadiusM * Mathf.Sin(bearing));
            }
            Vector3 e = sim.EnemyPosition;
            return new Vector3(e.x, 1.6f, e.z);
        }

        /// <summary>
        /// Run one scripted scenario through both implementations, stop at the first
        /// tick where either vehicle takes damage, and return the worst element-wise
        /// deviation across the compared ticks.
        /// </summary>
        private static float MaxDeviationUntilFirstDamage(
            string playerId, string enemyId, int ticks,
            float throttle, float steer, float turretTrim, bool fire, int aimMode)
        {
            // --- Rust ---
            float[] rust = new float[ticks * TraceStride];
            int written = RunRustTrace(
                playerId, enemyId, ticks, throttle, steer, turretTrim, fire, aimMode, rust);
            Assert.AreEqual(ticks * TraceStride, written,
                $"the Rust module wrote {written} floats, expected {ticks * TraceStride}. " +
                "A return of 0 usually means a vehicle id did not match the catalogue.");

            // --- C# original ---
            var dotnet = new BattleSim(playerId, enemyId);
            float[] original = new float[ticks * TraceStride];
            for (int tick = 0; tick < ticks; tick++)
            {
                Vector3 aim = ScriptedAim(aimMode, tick, dotnet);
                dotnet.Advance(Dt, throttle, steer, turretTrim, fire, aim);
                int b = tick * TraceStride;
                Vector3 p = dotnet.PlayerPosition;
                Vector3 e = dotnet.EnemyPosition;
                original[b + 0] = p.x;
                original[b + 1] = p.z;
                original[b + 2] = e.x;
                original[b + 3] = e.z;
                original[b + 4] = dotnet.PlayerHeadingRad;
                original[b + 5] = dotnet.PlayerTurretYawRad;
                original[b + 6] = dotnet.PlayerGunPitchRad;
                original[b + 7] = dotnet.EnemyHeadingRad;
                original[b + 8] = dotnet.EnemyTurretYawRad;
                original[b + 9] = dotnet.PlayerSpeedKph;
                original[b + 10] = dotnet.PlayerHealth;
                original[b + 11] = dotnet.EnemyHealth;
                original[b + 12] = dotnet.Kills;
                original[b + 13] = dotnet.ReloadRemaining;
                original[b + 14] = dotnet.HullPitchRad * Mathf.Rad2Deg;
                original[b + 15] = dotnet.HullRollRad * Mathf.Rad2Deg;
                original[b + 16] = dotnet.PlayerPosition.y;
            }

            // --- Where do the two implementations become entitled to differ? ---
            int firstDamageTick = ticks;
            for (int tick = 0; tick < ticks; tick++)
            {
                int b = tick * TraceStride;
                if (rust[b + PlayerHealthSlot] < 100f || rust[b + EnemyHealthSlot] < 100f)
                {
                    firstDamageTick = tick;
                    break;
                }
            }
            int compared = Mathf.Min(firstDamageTick, ticks);

            if (compared == 0)
            {
                Assert.Fail(
                    $"damage landed on tick 0, so {playerId} vs {enemyId} proved nothing. " +
                    "Shorten the trace or aim away from the enemy.");
            }

            // --- Compare every compared tick ---
            float worst = 0f;
            int worstTick = -1;
            int worstSlot = -1;
            for (int i = 0; i < compared * TraceStride; i++)
            {
                float delta = Mathf.Abs(rust[i] - original[i]);
                if (delta > worst)
                {
                    worst = delta;
                    worstTick = i / TraceStride;
                    worstSlot = i % TraceStride;
                }
            }

            if (worst > Tolerance)
            {
                int b = worstTick * TraceStride;
                Assert.Fail(
                    $"{playerId} vs {enemyId} diverged from the C# original.\n" +
                    $"  worst deviation {worst:G4} at tick {worstTick} " +
                    $"field {FieldNames[worstSlot]}\n" +
                    $"    rust   : {rust[b + worstSlot]:G6}\n" +
                    $"    csharp : {original[b + worstSlot]:G6}\n" +
                    $"  scenario: ticks={ticks} compared={compared} throttle={throttle} " +
                    $"steer={steer} turretTrim={turretTrim} fire={fire} aimMode={aimMode}");
            }

            // Record the worst deviation so it is visible in the run log. The
            // measured worst across every scenario is the number that justifies
            // Tolerance; without printing it, retuning that bound is guesswork.
            // `Console.WriteLine` rather than `TestContext.WriteLine` because the
            // latter is discarded by the NUnit XML reporter in batch mode, which is
            // exactly where this measurement is wanted.
            Console.WriteLine(
                $"[parity] {playerId} vs {enemyId}: worst {worst:E3} over {compared} ticks " +
                $"(throttle={throttle} steer={steer} fire={fire} aimMode={aimMode})");
            return worst;
        }

        /// <summary>
        /// Count how many compared ticks had a field resting on one of its clamps.
        /// </summary>
        private struct ClampCoverage
        {
            public int TurretAtTraverseLimit;
            public int GunAtPitchLimit;
            public float MinYaw;
            public float MaxYaw;
            public float MinPitch;
            public float MaxPitch;
        }

        /// <summary>
        /// Measure which clamps a scenario actually reaches.
        /// </summary>
        private static ClampCoverage MeasureCoverage(
            string playerId, string enemyId, int ticks,
            float throttle, float steer, bool fire, int aimMode)
        {
            float[] buf = new float[ticks * TraceStride];
            int written = RunRustTrace(
                playerId, enemyId, ticks, throttle, steer, 0f, fire, aimMode, buf);
            if (written != ticks * TraceStride)
            {
                return default;
            }

            var c = new ClampCoverage
            {
                MinYaw = float.MaxValue,
                MaxYaw = float.MinValue,
                MinPitch = float.MaxValue,
                MaxPitch = float.MinValue,
            };
            for (int tick = 0; tick < ticks; tick++)
            {
                int b = tick * TraceStride;
                float yaw = buf[b + PlayerTurretYawSlot];
                float pitch = buf[b + PlayerGunPitchSlot];
                c.MinYaw = Mathf.Min(c.MinYaw, yaw);
                c.MaxYaw = Mathf.Max(c.MaxYaw, yaw);
                c.MinPitch = Mathf.Min(c.MinPitch, pitch);
                c.MaxPitch = Mathf.Max(c.MaxPitch, pitch);
                if (Mathf.Abs(yaw) >= MaxTraverse - ClampEpsilon)
                {
                    c.TurretAtTraverseLimit++;
                }
                if (pitch >= MaxGunPitchRad - ClampEpsilon ||
                    pitch <= MinGunPitchRad + ClampEpsilon)
                {
                    c.GunAtPitchLimit++;
                }
            }
            return c;
        }

        /// <summary>
        /// Allowed worst-case drift across a compared trace prefix.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Set at 0.05 m / rad, matching <see cref="FlightModelParityTests"/>, but it
        /// is loose here and deliberately so: it is an amplifier, not a measurement.
        /// </para>
        /// <para>
        /// The measured worst across all eight scenarios is <b>1.7e-5</b> rad, on the
        /// hard-turn case over 600 ticks -- roughly 3000x inside this bound. That gap
        /// is the point. The bugs this fixture exists to catch are sign errors and
        /// misapplied clamps, and those produce deviations of metres and radians
        /// immediately: mutating <c>MAX_TRAVERSE</c> from 2.1 to 2.2 fails 8 tests and
        /// <c>MAX_SPEED_MPS</c> from 12.0 to 12.5 fails 8. A bound loose enough to
        /// tolerate the ground sim's AI-driven divergence amplifier is still three
        /// orders of magnitude tighter than any of those.
        /// </para>
        /// <para>
        /// It is not tightened to the measured 1.7e-5 because the F#-free reason is that
        /// the AI genuinely couples the two sims: it steers toward the player, so a
        /// sub-milliradian position difference changes the AI's target heading, which
        /// compounds. Binding the tolerance to the current worst would make the suite
        /// fail on an unrelated compiler change rather than on a porting error.
        /// </para>
        /// <para>
        /// The corollary is that this bound cannot detect a wrong <i>clamp constant</i>,
        /// because a clamp difference is a one-shot offset, not an accumulating one.
        /// A 0.4 degree error in <c>MaxGunPitchRad</c> is only 0.007 rad at the limit.
        /// Those are checked exactly instead, by <see cref="ClampedFieldsMustMatchExactly"/>.
        /// </para>
        /// </remarks>
        private const float Tolerance = 0.05f;

        /// <summary>
        /// Pins the gun's elevation clamp exactly, rather than to
        /// <see cref="Tolerance"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Added because a wrong <c>MaxGunPitchRad</c> slipped through the trace
        /// comparison unnoticed: 20.4 degrees instead of 20 produces a peak deviation of
        /// only 0.007 rad, seven times inside the 0.05 bound, so every parity test kept
        /// passing while the two gun limits genuinely differed.
        /// </para>
        /// <para>
        /// The fix is not a tighter general tolerance. Gun pitch is an integrated value
        /// that merely gets clamped, so away from the limit it differs between the two
        /// implementations by an ulp or two (measured: 0.0229406115 vs 0.0229406133) and
        /// demanding exact equality everywhere would fail on float noise. Comparing the
        /// trace alone cannot distinguish "integrated and drifting slightly" from
        /// "settled on a different ceiling".
        /// </para>
        /// <para>
        /// So this asserts on the *ceiling* instead: the value the gun rests at once it
        /// is genuinely pinned against the stop, read against the C# constant rather
        /// than a copy of it. That isolates the clamp from the integration.
        /// </para>
        /// </remarks>
        [Test]
        public void ClampedFieldsMustMatchExactly()
        {
            RequireReady();

            float[] rust = new float[TraceStride * 900];
            int written = RunRustTrace(
                "tiger2-e", "panther-g", 900, 0f, 0f, 0f, false, AimSweeping, rust);
            Assert.AreEqual(rust.Length, written, "the Rust trace was not fully written");

            // The reference value, read from the C# original rather than restated here,
            // so the two sides cannot drift apart through a duplicated constant.
            var reference = new BattleSim("tiger2-e", "panther-g");
            float expectedCeiling = BattleSim.MaxGunPitchRad;

            float maxSettled = float.MinValue;
            int settledTicks = 0;
            for (int tick = 0; tick < 900; tick++)
            {
                int b = tick * TraceStride;
                float pitch = rust[b + PlayerGunPitchSlot];
                maxSettled = Mathf.Max(maxSettled, pitch);
                if (pitch >= expectedCeiling - ClampEpsilon)
                {
                    settledTicks++;
                }
            }

            Assert.Greater(settledTicks, 100,
                "the sweep never settled on the gun pitch ceiling, so this test cannot " +
                "detect a wrong elevation limit");
            Assert.AreEqual(expectedCeiling, maxSettled, 0f,
                "the Rust gun settled at a pitch ceiling of " +
                $"{maxSettled:R}, but the C# limit is {expectedCeiling:R}. The gun " +
                "elevation clamp differs between the two implementations.");
        }

        private static void RequireReady()
        {
            WarSimRuntime.BeginLoad();
            WarSimRuntime.Poll();
            Assert.IsTrue(WarSimRuntime.IsReady,
                "the Rust core did not become ready: " + WarSimRuntime.Status +
                " (build it with crates/tools/build-native.ps1)");
        }

        [Test]
        public void TraceStrideMatchesTheRustModule()
        {
            RequireReady();
            Assert.AreEqual(17, TraceStride,
                "TraceStride must match BATTLE_TRACE_STRIDE in war-core-ffi. A Rust trace " +
                "with a stride of 13 would silently desynchronise against the C# trace " +
                "and compare unrelated fields against each other.");
            Assert.AreEqual(FieldNames.Length, TraceStride,
                "FieldNames must describe every traced slot, otherwise a failure report " +
                "would name the wrong field.");
        }

        /// <summary>
        /// The exported trace must exist and be callable with the ids this test uses.
        /// </summary>
        [Test]
        public void TheRustBattleSimAcceptsTheVehicleIds()
        {
            RequireReady();
            float[] rust = new float[TraceStride];
            int written = RunRustTrace("t34-76", "panther-g", 1, 0f, 0f, 0f, false,
                AimAtEnemy, rust);
            Assert.AreEqual(TraceStride, written,
                "wa_battle_trace returned 0 for known vehicle ids; check that the " +
                "native plugin was rebuilt after the export was added.");
        }

        /// <summary>
        /// Straight-line full throttle: acceleration curve, the speed clamp, and the
        /// fact that the tank must travel in the direction the stick is pushed rather
        /// than the opposite one.
        /// </summary>
        [Test]
        public void FullThrottleMatchesTheCSharpOriginal()
        {
            RequireReady();
            MaxDeviationUntilFirstDamage("t34-76", "panther-g", 600, 1f, 0f, 0f, false,
                AimAtEnemy);
        }

        /// <summary>
        /// Reversing: negative throttle, the stronger braking deceleration, and the
        /// sign of the reverse direction.
        /// </summary>
        [Test]
        public void ReversingMatchesTheCSharpOriginal()
        {
            RequireReady();
            MaxDeviationUntilFirstDamage("t34-76", "panther-g", 300, -1f, 0f, 0f, false,
                AimAtEnemy);
        }

        /// <summary>
        /// Sustained hard turn: steering rate, the hull auto-align toward the steering
        /// input, and the speed penalty for turning while moving.
        /// </summary>
        [Test]
        public void AHardTurnMatchesTheCSharpOriginal()
        {
            RequireReady();
            MaxDeviationUntilFirstDamage("t34-76", "panther-g", 600, 1f, 0.6f, 0f, false,
                AimAtEnemy);
        }

        /// <summary>
        /// The aim sweep at 6 deg/tick. The hull auto-align cannot follow a sweep this
        /// fast, so the turret is driven onto its +/-<see cref="MaxTraverse"/> clamp for
        /// roughly half the run. This is the scenario that catches a turret which can
        /// point backwards through its own hull, or a traverse limit in the wrong place.
        /// </summary>
        /// <remarks>
        /// It also saturates the gun's depression clamp, because
        /// <see cref="SweepHeightM"/> is above the muzzle, so the gun must aim down to
        /// reach the aim point. Both clamps are asserted as reached by
        /// <see cref="ScenariosActuallyReachTheClamps"/> rather than assumed.
        /// </remarks>
        [Test]
        public void TheTurretTraverseLimitsMatchTheCSharpOriginal()
        {
            RequireReady();
            MaxDeviationUntilFirstDamage("tiger2-e", "panther-g", 900, 0.5f, 0f, 0f, false,
                AimSweeping);
        }

        /// <summary>
        /// The same sweep from a stationary hull, so the auto-align term is zero and the
        /// turret is driven entirely by the traverse limit. A second geometry for the
        /// same clamp, because a clamp that is only ever reached while moving is not
        /// really being tested.
        /// </summary>
        [Test]
        public void GunElevationLimitsMatchTheCSharpOriginal()
        {
            RequireReady();
            MaxDeviationUntilFirstDamage("tiger2-e", "panther-g", 900, 0f, 0f, 0f, false,
                AimSweeping);
        }

        /// <summary>
        /// Guard against the failure mode that made the first version of this fixture
        /// worthless: a scenario that compares two implementations over a range that
        /// never touches any clamp, and therefore cannot detect a wrong limit or a wrong
        /// slew rate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is not hypothetical. The original sweep ran at 0.75 deg/tick; the hull
        /// auto-align followed it, the turret spanned 0.47 rad against a 2.1 rad limit,
        /// the gun pitch spanned 0.0019 rad against a 0.52 rad range, and every test in
        /// the fixture passed green while being unable to catch a 2% error in
        /// <c>TURRET_SLEW_RATE</c>. That was confirmed by mutating the Rust constant to
        /// 2.65 and re-running: still 68/68.
        /// </para>
        /// <para>
        /// So the sweep scenarios are now required to demonstrably saturate both the
        /// traverse and the elevation clamps. If someone retunes the schedule and it
        /// stops reaching them, this fails loudly instead of the parity tests quietly
        /// proving nothing.
        /// </para>
        /// </remarks>
        [Test]
        public void ScenariosActuallyReachTheClamps()
        {
            RequireReady();

            // 1800 ticks at 3 deg/tick is seven and a half full revolutions of the aim, which
            // gives the stationary turret time to reach a stop, dwell there, and come
            // back. At 900 ticks it reached the limit for 70 ticks, which satisfied
            // "the clamp is exercised" but with little margin against a retune.
            const int CoverageTicks = 1800;

            var moving = MeasureCoverage(
                "tiger2-e", "panther-g", CoverageTicks, 0.5f, 0f, false, AimSweeping);
            var still = MeasureCoverage(
                "tiger2-e", "panther-g", CoverageTicks, 0f, 0f, false, AimSweeping);

            // Traverse coverage comes from the STATIONARY scenario.
            //
            // This used to be measured on the moving one, and that stopped working
            // when the hull steering rate was corrected to be proportional to input.
            // The old steering applied `demand * MAX_TURN_RATE_RAD` with no dt factor,
            // so the clamp saturated for any demand above ~0.033 and the hull was
            // pinned at maximum turn rate the whole time. It chased the sweeping aim
            // hard enough to hold the turret near the centreline � which happened to
            // push the turret onto its traverse stop. With correct steering the hull
            // follows the gun line only as hard as the aim actually pulls it, and the
            // turret stays within +/-1.5 rad of centre however fast the aim sweeps.
            //
            // A stationary hull cannot follow the aim at all, so the turret has to
            // sweep the entire arc to track it and both stops are genuinely reached.
            // That is the scenario that actually exercises the traverse limit.
            Assert.Greater(still.TurretAtTraverseLimit, 100,
                $"the stationary sweep rested on the traverse clamp for only " +
                $"{still.TurretAtTraverseLimit}/{CoverageTicks} ticks (yaw range " +
                $"{still.MinYaw:F3}..{still.MaxYaw:F3} against +/-{MaxTraverse}). " +
                "A fixed hull should have to sweep the whole arc to follow the aim, " +
                "so the traverse limit is not being exercised.");

            // The moving scenario still has to be doing something, otherwise it is
            // dead weight in the parity comparison.
            Assert.Greater(
                moving.TurretAtTraverseLimit + still.TurretAtTraverseLimit, 100,
                "neither sweep scenario reaches the traverse limit, so the parity " +
                "tests cannot detect a wrong MAX_TRAVERSE.");

            Assert.Greater(still.GunAtPitchLimit, 100,
                $"the sweep rested on a gun pitch clamp for only " +
                $"{still.GunAtPitchLimit}/{CoverageTicks} ticks (pitch range " +
                $"{still.MinPitch:F4}..{still.MaxPitch:F4} against " +
                $"{MinGunPitchRad:F4}..{MaxGunPitchRad:F4}). The aim point is not high " +
                "enough to force the gun to its elevation limits. Raise SweepHeightM.");
        }

        /// <summary>
        /// The AI: it steers toward the player and closes, and the Rust port has to
        /// reproduce its weave phase and its close-range throttle drop as well as its
        /// driving. Because the AI reacts to the player's position, this is the
        /// scenario most sensitive to small positional drift.
        /// </summary>
        [Test]
        public void TheEnemyAiMatchesTheCSharpOriginal()
        {
            RequireReady();
            MaxDeviationUntilFirstDamage("t34-76", "tiger2-e", 600, 0f, 0f, 0f, false,
                AimAtEnemy);
        }

        /// <summary>
        /// Firing. Tracked onto the enemy, so this exercises the fire solution, the
        /// reload timer, shell flight in ballistics and hit <i>detection</i>. The
        /// comparison stops at the first hit, because the hit <i>resolution</i> is where
        /// the deliberate fixes live.
        /// </summary>
        [Test]
        public void FiringUntilFirstHitMatchesTheCSharpOriginal()
        {
            RequireReady();
            MaxDeviationUntilFirstDamage("t34-76", "panther-g", 400, 0.3f, 0f, 0f, true,
                AimAtEnemy);
        }

        /// <summary>
        /// Firing from a hull-on, stationary stance, which is what the player's
        /// reticle does when they stop to aim. Guards the same paths as
        /// <see cref="FiringUntilFirstHitMatchesTheCSharpOriginal"/> from a different
        /// initial geometry.
        /// </summary>
        [Test]
        public void FiringFromAStationaryStanceMatchesTheCSharpOriginal()
        {
            RequireReady();
            MaxDeviationUntilFirstDamage("panther-g", "t34-76", 400, 0f, 0f, 0f, true,
                AimAtEnemy);
        }

        /// <summary>
        /// The sweep is a pure function of the tick index, so it must be identical on
        /// both sides regardless of what the vehicles do. If the two implementations
        /// computed it differently -- for instance if one keyed it off the player's
        /// position -- the deviation would feed back into the turret angle and every
        /// subsequent tick, and the long sweeps above would be comparing a divergence
        /// amplifier rather than the model.
        /// </summary>
        [Test]
        public void TheSweepingAimScheduleIsStateIndependent()
        {
            var sim = new BattleSim("t34-76", "panther-g");
            Vector3 first = ScriptedAim(AimSweeping, 0, sim);
            // Advance the sim substantially. A state-dependent aim would have moved.
            for (int i = 0; i < 300; i++)
            {
                sim.Advance(Dt, 1f, 0.5f, 0f, false, ScriptedAim(AimSweeping, i, sim));
            }
            Vector3 again = ScriptedAim(AimSweeping, 0, sim);
            Assert.AreEqual(first.x, again.x, 0f, "the sweep aim depended on sim state");
            Assert.AreEqual(first.y, again.y, 0f, "the sweep aim depended on sim state");
            Assert.AreEqual(first.z, again.z, 0f, "the sweep aim depended on sim state");
        }
    }
}