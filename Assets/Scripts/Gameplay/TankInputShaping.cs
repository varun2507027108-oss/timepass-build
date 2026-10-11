using UnityEngine;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Driver-input shaping for the tank: the rate limiters between the raw
    /// keyboard axis and the accelerations <see cref="TrackedVehicleDynamics"/>
    /// integrates.
    ///
    /// Why this exists
    /// ---------------
    /// Every term in <see cref="TrackedVehicleDynamics"/> is physically motivated --
    /// drawbar pull, the Kamm circle, slope gradeability, the neutral turn -- and
    /// the simulation is faithful because of that. But faithfulness is not the same
    /// as feel. A raw digital axis fed straight into a traction model produces a
    /// vehicle that snaps to full thrust and stops just as abruptly, and the player
    /// experiences that as unresponsive even though nothing is wrong.
    ///
    /// Real vehicles have inertia between the driver's right foot and the tracks:
    /// the engine spools up, the transmission takes up slack, and the tracks have to
    /// build slip before the hull actually moves. The gap between the command and
    /// the response is what a player reads as "heavy" or "heavy-vehicle-like".
    ///
    /// This is the one place in the pipeline that is deliberately NOT physics. It
    /// models the driver and driveline, not the tank, and everything downstream of it
    /// is unchanged: shaping decides how fast the command moves, the traction model
    /// decides what the vehicle then does about it.
    ///
    /// The five behaviours modelled here
    /// ---------------------------------
    ///  1. <b>Throttle ramp</b> -- thrust builds over time rather than instantly.
    ///  2. <b>Release ramp</b> -- a separate, slower decay when the driver lets go.
    ///     Asymmetric with (1) on purpose: coasting to a stop should feel different
    ///     from spooling down under engine braking.
    ///  3. <b>Steering lerp</b> -- the traverse axis is filtered, so the hull swings
    ///     through rather than snapping to full lock.
    ///  4. <b>Direction-change inertia</b> -- reversing through neutral is the slowest
    ///     transition of all, because the driveline has to unwind first. Without it a
    ///     tank flips from full ahead to full astern instantly and any accumulated
    ///     momentum feels unearned.
    ///  5. <b>Climb-angle capability</b> -- commanded thrust is reduced on steep
    ///     ground rather than the vehicle being allowed to spin its tracks at full
    ///     effort. This one is a design curve, not physics, and is documented as such
    ///     at <see cref="ClimbCapability"/>.
    ///
    /// Frame-rate independence
    /// ----------------------
    /// Ramp rates are expressed in <b>axis units per second</b> and converted to a
    /// per-tick increment with <c>rate * dt</c>. A frame-rate-independent exponential
    /// (<c>1 - exp(-k*dt)</c>) was rejected deliberately: at a fixed 30 Hz tick the two
    /// are numerically near-identical, but the linear form is exactly reproducible in
    /// both C# and Rust with identical float operations, and it gives the driver a
    /// constant, predictable rate of build rather than one that quietly depends on
    /// how often the loop happened to run.
    ///
    /// Everything here is a pure static function of state and dt, so it is unit
    /// testable with no scene, no Rigidbody and no input device.
    /// </summary>
    public static class TankInputShaping
    {
        // ------------------------------------------------------------- constants

        /// <summary>
        /// Default throttle build rate, axis units per second.
        ///
        /// 3.0 means a full-throttle press takes 1/3 s to reach full thrust. That is
        /// the difference between a tank that "gets going" and one that snaps; faster
        /// than about 5.0 reads as arcade, slower than about 1.5 reads as sluggish.
        /// </summary>
        public const float DefaultThrottleAttackPerSec = 3.0f;

        /// <summary>
        /// Default throttle decay rate on release, axis units per second.
        ///
        /// Deliberately slower than the attack. A driver easing off does not expect
        /// the thrust to vanish as fast as it arrived.
        /// </summary>
        public const float DefaultThrottleReleasePerSec = 2.0f;

        /// <summary>
        /// Default steering lerp time constant, seconds.
        ///
        /// Expressed as a half-life so the number is interpretable: 0.12 s means the
        /// traverse error halves roughly every 120 ms. An exponential lerp is used
        /// here (rather than the linear ramp of the throttle) because the steering axis
        /// is bipolar and centred, and an exponential settles smoothly onto a new
        /// target instead of arriving at a constant speed, which would be felt as the
        /// hull drifting out of a turn even after the driver stopped asking for it.
        /// </summary>
        public const float DefaultSteerLerpSeconds = 0.12f;

        /// <summary>
        /// Default time constant for crossing through neutral into reverse, seconds.
        ///
        /// Larger than the steer lerp because reversing is a genuine driveline event.
        /// </summary>
        public const float DefaultDirectionChangeSeconds = 0.35f;

        /// <summary>Curvature of the steering lerp for a reversal.</summary>
        private const float MinSteerBlend = 0.35f;

        // ---------------------------------------------------------------- throttle

        /// <summary>
        /// Ramp the throttle axis toward the commanded value, m/s^2-free axis units.
        /// </summary>
        /// <param name="current">Filtered axis from the previous tick.</param>
        /// <param name="commanded">Raw axis, already clamped to [-1, 1].</param>
        /// <param name="dt">Tick length, seconds.</param>
        /// <param name="attackPerSec">Rate while the magnitude is growing.</param>
        /// <param name="releasePerSec">Rate while the magnitude is shrinking.</param>
        public static float Ramp(
            float current, float commanded, float dt, float attackPerSec, float releasePerSec)
        {
            float target = Mathf.Clamp(commanded, -1f, 1f);

            // Growth and decay use different rates; the direction of travel of the
            // axis decides which.
            //
            // A reversal needs the guard explicitly. Comparing magnitudes ALONE gets
            // it wrong: after a single tick of decay from full ahead, |target| (1.0)
            // exceeds |current| (0.93), so the comparison reports "growing" and the
            // axis snaps over to the attack rate -- unwinding a full reversal at full
            // attack speed, which is the opposite of the intended driveline lag. The
            // strict product test says that while the axis is still heading towards
            // zero from the other side it is decaying, and only once it has actually
            // crossed does the sign agree and the attack rate take over.
            bool headingTowardsZero = current * target < 0f;
            bool growing = !headingTowardsZero && Mathf.Abs(target) > Mathf.Abs(current);
            float rate = growing ? attackPerSec : releasePerSec;

            if (rate <= 0f) return current;

            // Never step past the target: MoveTowards on the signed axis rather than
            // on the magnitude, so a reversal crosses zero at the same rate rather
            // than teleporting across the gap.
            return Mathf.MoveTowards(current, target, rate * dt);
        }

        // ---------------------------------------------------------------- steering

        /// <summary>
        /// Frame-rate-independent exponential filter of the traverse axis.
        /// </summary>
        /// <remarks>
        /// Uses the exact <c>1 - exp(-dt/tau)</c> form rather than the common
        /// <c>t * dt</c> approximation. The approximation is only valid for
        /// <c>dt &lt;&lt; tau</c> and silently becomes non-normalising when it is not:
        /// at dt = tau it returns 1.0 (fine) but at dt = 2*tau it returns 2.0, which
        /// overshoots the target and oscillates. The sim runs a fixed 30 Hz so it does
        /// not currently hit those cases, but an exact form costs one <c>exp</c> and
        /// cannot break if the tick rate is ever changed.
        /// </remarks>
        public static float LerpAxis(float current, float commanded, float dt, float tauSeconds)
        {
            float target = Mathf.Clamp(commanded, -1f, 1f);
            if (tauSeconds <= 1e-5f) return target;

            float k = 1f - Mathf.Exp(-dt / tauSeconds);
            return current + (target - current) * k;
        }

        /// <summary>
        /// Blend factor for the steering filter, widening when the driver is reversing
        /// direction.
        /// </summary>
        /// <remarks>
        /// Traversing straight through from hard left to hard right is the one
        /// transition a fixed lerp constant makes feel wrong: the hull is carrying
        /// rotational momentum in the old direction, and it should be slower to
        /// accept the new command than it is to settle onto a steady turn.
        ///
        /// Expressed as a floor on the lerp's rate rather than a separate path, so the
        /// filter stays a single exponential and cannot be defeated by sign changes at
        /// the exact tick they occur.
        /// </remarks>
        public static float SteerBlend(float current, float commanded, float steerTau, float directionTau)
        {
            float target = Mathf.Clamp(commanded, -1f, 1f);

            // A reversal is a genuine disagreement in sign, not merely a smaller error.
            bool reversing = current * target < 0f;
            if (!reversing) return steerTau;

            // Blend the time constant toward the (slower) direction-change value.
            // Using the constant rather than scaling the blend keeps the result a
            // well-formed exponential in both languages.
            return Mathf.Lerp(steerTau, directionTau, MinSteerBlend);
        }

        // ------------------------------------------------------------------- climb

        /// <summary>
        /// Commanded-throttle multiplier for a given forward slope, 0..1.
        /// </summary>
        /// <remarks>
        /// This is the one genuinely non-physical term in the shaping layer, and it is
        /// here as a deliberate design decision rather than a derived result.
        ///
        /// The traction model already handles gradeability correctly: past the
        /// drawbar-pull limit the tracks slip and the vehicle simply fails to climb,
        /// which is the physically honest outcome. That is also a poor <i>game</i>
        /// outcome -- the vehicle grinds against a slope with no feedback, and the
        /// player cannot tell a marginal climb from a broken one.
        ///
        /// Reducing commanded thrust with slope gives the driver progressive feedback
        /// before the physical limit, so the approach to a climb is legible, while the
        /// traction model remains the authority on whether it actually succeeds. If
        /// this term were removed the vehicle would still behave correctly; it would
        /// simply feel unresponsive against a hill.
        ///
        /// Note this is a function of slope angle ALONE and does not feed back into
        /// <see cref="TrackedVehicleDynamics"/>, so it cannot mask a traction or
        /// friction bug -- it only shapes the command going in.
        ///
        /// Below <see cref="ClimbFlatDeg"/> there is no penalty at all, and the
        /// multiplier decays smoothly to <see cref="ClimbMinCapability"/> at the limit.
        /// </remarks>
        public static float ClimbCapability(float forwardSlopeDeg)
        {
            float s = Mathf.Abs(forwardSlopeDeg);
            if (s <= ClimbFlatDeg) return 1f;
            if (s >= ClimbLimitDeg) return ClimbMinCapability;

            return Mathf.Lerp(1f, ClimbMinCapability, (s - ClimbFlatDeg) / (ClimbLimitDeg - ClimbFlatDeg));
        }

        /// <summary>Slope below which no capability penalty applies, degrees.</summary>
        public const float ClimbFlatDeg = 6f;

        /// <summary>
        /// Slope at which capability has decayed to the floor, degrees.
        ///
        /// Set near the ~29 degree gradeability limit of a real main battle tank
        /// (TOP 2-2-610 gives roughly 29-30 degrees for a dry, firm surface), so the
        /// shaping and the physical model agree about where the wall is.
        /// </summary>
        public const float ClimbLimitDeg = 28f;

        /// <summary>Throttle retained at and beyond the gradeability limit.</summary>
        public const float ClimbMinCapability = 0.35f;
    }
}