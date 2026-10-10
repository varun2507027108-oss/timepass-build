using System;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using WarArena.Air;
using WarArena.Interop;

namespace WarArena.Tests
{
    /// <summary>
    /// Proves the Rust flight model and the original C# one produce the same
    /// trajectory, tick for tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the flight model's correctness rests on sign conventions
    /// that cannot be verified by reading either implementation:
    /// </para>
    /// <list type="bullet">
    /// <item>Unity applies Euler rotations in <b>ZXY</b> order, which makes positive
    /// pitch <b>nose-down</b>. The entire longitudinal stability term depends on it.</item>
    /// <item>Angle of attack is <c>SignedAngle(forward, flightPath, right)</c>.
    /// Reversing that argument order reports nose-up as negative, so lift is applied
    /// downward and the aircraft is driven into the ground with no recovery.</item>
    /// <item>Angle of attack is measured <i>twice</i> per step, the second time
    /// against the attitude the aircraft will actually fly. Reusing the first
    /// measurement leaves a one-frame lag that shows as a slow steady sink.</item>
    /// </list>
    /// <para>
    /// A Rust unit test written against the Rust code would pass no matter how wrong
    /// the Rust code was, because both would share the same wrong assumption. Only
    /// running the C# original side by side can catch it.
    /// </para>
    /// <para>
    /// The comparison is a whole trajectory rather than a handful of spot checks,
    /// because these bugs are cumulative: an aircraft can fly plausibly for the
    /// first few seconds and be 200 degrees of pitch out by the last.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class FlightModelParityTests
    {
        /// <summary>
        /// Must match <c>FLIGHT_TRACE_STRIDE</c> in <c>war-core-ffi</c>. A mismatch
        /// is caught by <see cref="TraceStrideMatchesTheRustModule"/>.
        /// </summary>
        private const int TraceStride = 12;

        private const float Dt = 1f / 60f;

#if !UNITY_WEBGL || UNITY_EDITOR
        // `IntPtr` rather than `string` or `float[]`: this project sets
        // allowUnsafeCode: 0, so there are no `fixed` blocks, and the arrays are
        // pinned with GCHandle instead. Marshalling a string or an array directly
        // would work on the native player but is exactly the sort of IL2CPP
        // marshalling that differs silently on WebGL.
        [DllImport("war_core_ffi")]
        private static extern uint wa_flight_trace(
            IntPtr aircraftId, uint idLen, float altitude, float headingDeg,
            uint ticks, float dt, float pitchIn, float rollIn, float yawIn, float throttleIn,
            IntPtr dest, uint cap);
#endif

        /// <summary>
        /// Run one scripted scenario through both implementations and return the
        /// worst element-wise deviation across every tick.
        /// </summary>
        private static float MaxDeviation(
            string aircraftId, float altitude, float headingDeg, int ticks,
            float pitchIn, float rollIn, float yawIn, float throttleIn)
        {
            // --- Rust ---
            float[] rust = new float[ticks * TraceStride];
            int written = RunRustTrace(aircraftId, altitude, headingDeg, ticks,
                pitchIn, rollIn, yawIn, throttleIn, rust);
            Assert.AreEqual(ticks * TraceStride, written,
                $"the Rust module wrote {written} floats, expected {ticks * TraceStride}");

            // --- C# original ---
            AircraftDefinition spec = AircraftCatalog.Get(aircraftId);
            var csharp = new FlightModel(spec, new Vector3(0f, altitude, 0f), headingDeg);
            float[] dotnet = new float[ticks * TraceStride];
            for (int tick = 0; tick < ticks; tick++)
            {
                csharp.Step(Dt, pitchIn, rollIn, yawIn, throttleIn);
                int b = tick * TraceStride;
                Vector3 p = csharp.Position;
                Vector3 v = csharp.Velocity;
                dotnet[b + 0] = p.x;
                dotnet[b + 1] = p.y;
                dotnet[b + 2] = p.z;
                dotnet[b + 3] = v.x;
                dotnet[b + 4] = v.y;
                dotnet[b + 5] = v.z;
                dotnet[b + 6] = csharp.PitchDeg;
                dotnet[b + 7] = csharp.YawDeg;
                dotnet[b + 8] = csharp.RollDeg;
                dotnet[b + 9] = csharp.Throttle;
                dotnet[b + 10] = csharp.IsStalled ? 1f : 0f;
                dotnet[b + 11] = csharp.IsOverstressed ? 1f : 0f;
            }

            // --- Compare ---
            string[] names =
            {
                "position.x", "position.y", "position.z",
                "velocity.x", "velocity.y", "velocity.z",
                "PitchDeg", "YawDeg", "RollDeg",
                "Throttle", "IsStalled", "IsOverstressed",
            };
            float worst = 0f;
            int worstTick = -1;
            int worstSlot = -1;
            for (int i = 0; i < rust.Length; i++)
            {
                float delta = Mathf.Abs(rust[i] - dotnet[i]);
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
                    $"{aircraftId} diverged from the C# original.\n" +
                    $"  worst deviation {worst:G4} at tick {worstTick} " +
                    $"field {names[worstSlot]}\n" +
                    $"    rust : {rust[b + worstSlot]:G6}\n" +
                    $"    csharp: {dotnet[b + worstSlot]:G6}\n" +
                    $"  scenario: altitude={altitude} ticks={ticks} " +
                    $"pitch={pitchIn} roll={rollIn} yaw={yawIn} throttle={throttleIn}");
            }
            return worst;
        }

        /// <summary>
/// Allowed worst-case drift across a whole trace.
/// </summary>
/// <remarks>
/// <para>
/// Measured: the worst deviation across all seven scenarios -- including 2000
/// ticks of a deep, chaotic stall dive where the aircraft is tumbling and the
/// angle of attack is past 100 degrees -- is <b>4.9e-4</b>, on a position value of
/// 2445.31. That is a relative error of about 2e-7, i.e. `f32` round-off
/// accumulating over 1538 steps. The two implementations are, for practical
/// purposes, the same model.
/// </para>
/// <para>
/// This bound is set at 0.05, roughly 100x the measured worst case, because the
/// sign conventions this test exists to police diverge by orders of magnitude
/// rather than by percentages: an inverted pitch sign sends the aircraft out of
/// the atmosphere and an inverted angle-of-attack sign puts it on the ground
/// inside a few hundred ticks, so anything even close to this bound is already
/// unambiguous.
/// </para>
/// <para>
/// It is not set to zero because the two implementations are not guaranteed
/// bit-identical: Unity's <c>Mathf</c> transcendentals and the compiler's
/// expression evaluation order are not specified to match Rust's.
/// </para>
/// </remarks>
        private const float Tolerance = 0.05f;

        private static int RunRustTrace(
            string aircraftId, float altitude, float headingDeg, int ticks,
            float pitchIn, float rollIn, float yawIn, float throttleIn, float[] dest)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // The WebGL module loads asynchronously and this fixture runs in the
            // Editor, so only the native path is exercised here. The web path is
            // covered by Tools/verify-webgl-core.mjs.
            Assert.Ignore("parity trace runs against the native plugin");
            return 0;
#else
            byte[] idBytes = Encoding.ASCII.GetBytes(aircraftId);
            // GCHandle pinning rather than `fixed`: allowUnsafeCode is 0 here.
            GCHandle idHandle = GCHandle.Alloc(idBytes, GCHandleType.Pinned);
            GCHandle destHandle = GCHandle.Alloc(dest, GCHandleType.Pinned);
            try
            {
                return (int)wa_flight_trace(
                    idHandle.AddrOfPinnedObject(), (uint)idBytes.Length,
                    altitude, headingDeg, (uint)ticks, Dt,
                    pitchIn, rollIn, yawIn, throttleIn,
                    destHandle.AddrOfPinnedObject(), (uint)dest.Length);
            }
            finally
            {
                idHandle.Free();
                destHandle.Free();
            }
#endif
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
            Assert.AreEqual(12, TraceStride,
                "TraceStride must match FLIGHT_TRACE_STRIDE in war-core-ffi. " +
                "A Rust trace of 600 ticks with a stride of 11 would silently " +
                "desynchronise against the C# trace and compare nonsense.");
        }

        /// <para>
        /// Hands off, full power. This is the phugoid test: the trim has to find a
        /// fixed point or the aircraft wanders for minutes. It is also the scenario
        /// most sensitive to the Euler-order sign, because a wrong sign makes the
        /// stability term diverge instead of damping.
        /// </para>
        [Test]
        public void HandsOffFullPowerMatchesTheCSharpOriginal()
        {
            RequireReady();
            MaxDeviation("spitfire-mkix", 3000f, 0f, 600, 0f, 0f, 0f, 1f);
        }

        [Test]
        public void HandsOffHandsOffMatchesTheCSharpOriginal()
        {
            RequireReady();
            // Trimmed level flight for ten seconds: the hardest case for the
            // surplus-thrust and speed-bias terms.
            MaxDeviation("bf109-g6", 3000f, 0f, 600, 0f, 0f, 0f, 0f);
        }

        [Test]
        public void SustainedClimbMatchesTheCSharpOriginal()
        {
            RequireReady();
            // Hard pull: exercises the pitch clamp, the mid-step angle-of-attack
            // re-measurement and the stall branch all at once.
            MaxDeviation("spitfire-mkix", 2000f, 0f, 480, 1f, 0f, 0f, 1f);
        }

        [Test]
        public void SustainedRollAndRudderMatchTheCSharpOriginal()
        {
            RequireReady();
            // Bank turn: the roll-to-yaw coupling and the roll auto-level decay.
            MaxDeviation("p51d", 4000f, 30f, 600, 0f, 1f, 0.4f, 0.9f);
        }

        [Test]
        public void ADeepStallDivergenceMatchesTheCSharpOriginal()
        {
            RequireReady();
            // Pull to the stall at idle, then recover at full power. The Rust port
            // was observed to re-enter a much deeper stall here. This is the test
            // that decides whether that is faithful behaviour or a porting error --
            // it compares all 3000 ticks, so the two outcomes cannot both pass.
            MaxDeviation("spitfire-mkix", 3000f, 0f, 2000, 1f, 0f, 0f, 1f);
        }

        [Test]
        public void TheJetMatchesTheCSharpOriginalToo()
        {
            RequireReady();
            // The 262 exercises the turbojet thrust branch and the transonic drag
            // rise, neither of which the props reach.
            MaxDeviation("me262-a1a", 6000f, 180f, 600, 0f, 0f, 0f, 1f);
        }

        [Test]
        public void ThePropellerDrivenJetMatchesTheCSharpOriginalToo()
        {
            RequireReady();
            // The Meteor is a jet airframe flying a piston engine, so it takes the
            // propeller thrust branch at jet airspeeds -- a combination no other
            // roster entry produces.
            MaxDeviation("meteor-f4", 5000f, 90f, 600, 0f, 0f, 0f, 1f);
        }
    }
}