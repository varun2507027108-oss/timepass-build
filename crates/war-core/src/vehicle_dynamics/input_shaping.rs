//! Driver-input shaping for the tank: the rate limiters between the raw input
//! axis and the accelerations `vehicle_dynamics` integrates.
//!
//! Ported from `Assets/Scripts/Gameplay/TankInputShaping.cs`. The two
//! implementations must stay numerically identical: the C#-vs-Rust parity tests
//! compare traces tick for tick, so any divergence in this layer shows up as a
//! mismatch in every downstream force.
//!
//! Why this exists
//! ---------------
//! Every term in `vehicle_dynamics` is physically motivated - drawbar pull, the
//! Kamm circle, slope gradeability, the neutral turn - and the simulation is
//! faithful because of that. But faithfulness is not the same as feel. A raw
//! digital axis fed straight into a traction model produces a vehicle that snaps
//! to full thrust and stops just as abruptly, and the player experiences that as
//! unresponsive even though nothing is wrong.
//!
//! Real vehicles have inertia between the driver's right foot and the tracks:
//! the engine spools up, the transmission takes up slack, and the tracks have to
//! build slip before the hull actually moves. The gap between the command and
//! the response is what a player reads as "heavy", or as heavy-vehicle-like.
//!
//! This is the one layer in the pipeline that is deliberately NOT physics. It
//! models the driver and driveline, not the tank. Everything downstream is
//! unchanged: shaping decides how fast the command moves, the traction model
//! decides what the vehicle then does about it.
//!
//! Frame-rate independence
//! ----------------------
//! Ramp rates are expressed in axis units per second and converted to a per-tick
//! increment with `rate * dt`. A frame-rate-independent exponential
//! (`1 - exp(-k*dt)`) was rejected deliberately: at a fixed 30 Hz tick the two are
//! numerically near-identical, but the linear form is exactly reproducible in
//! both languages with identical float operations, and it gives the driver a
//! constant, predictable rate of build rather than one that quietly depends on
//! how often the loop happened to run.

use crate::math::mathf;

/// Default throttle build rate, axis units per second.
///
/// 3.0 means a full-throttle press takes 1/3 s to reach full thrust. That is the
/// difference between a tank that "gets going" and one that snaps; faster than
/// about 5.0 reads as arcade, slower than about 1.5 reads as sluggish.
pub const DEFAULT_THROTTLE_ATTACK_PER_SEC: f32 = 3.0;

/// Default throttle decay rate on release, axis units per second.
///
/// Deliberately slower than the attack. A driver easing off does not expect the
/// thrust to vanish as fast as it arrived.
pub const DEFAULT_THROTTLE_RELEASE_PER_SEC: f32 = 2.0;

/// Default steering lerp time constant, seconds.
///
/// Expressed so the number is interpretable: 0.12 s means the traverse error
/// halves roughly every 120 ms. An exponential is used for steering (rather than
/// the linear ramp of the throttle) because the steering axis is bipolar and
/// centred, and an exponential settles smoothly onto a new target instead of
/// arriving at a constant speed, which would be felt as the hull drifting out of
/// a turn even after the driver stopped asking for it.
pub const DEFAULT_STEER_LERP_SECONDS: f32 = 0.12;

/// Default time constant for crossing through neutral into reverse, seconds.
///
/// Larger than the steer lerp because reversing is a genuine driveline event.
pub const DEFAULT_DIRECTION_CHANGE_SECONDS: f32 = 0.35;

/// Curvature of the steering lerp for a reversal.
const MIN_STEER_BLEND: f32 = 0.35;

/// Ramp the throttle axis toward the commanded value.
///
/// `current` is the filtered axis from the previous tick, `commanded` the raw
/// axis, `dt` the tick length in seconds. Growth and decay use different rates;
/// the direction of travel of the axis decides which. Comparing magnitudes (not
/// raw values) is what makes this work through a sign change as well as within
/// one half of travel.
pub fn ramp(current: f32, commanded: f32, dt: f32, attack_per_sec: f32, release_per_sec: f32) -> f32 {
    let target = mathf::clamp(commanded, -1.0, 1.0);

    // Growth and decay use different rates; the direction of travel of the axis
    // decides which.
    //
    // A reversal needs the guard explicitly. Comparing magnitudes ALONE gets it
    // wrong: after a single tick of decay from full ahead, |target| (1.0) exceeds
    // |current| (0.93), so the comparison reports "growing" and the axis snaps over to
    // the attack rate -- unwinding a full reversal at full attack speed, which is the
    // opposite of the intended driveline lag. The strict product test says that while
    // the axis is still heading towards zero from the other side it is decaying, and
    // only once it has actually crossed does the sign agree and the attack rate take
    // over.
    let heading_towards_zero = current * target < 0.0;
    let growing = !heading_towards_zero && mathf::abs(target) > mathf::abs(current);
    let rate = if growing { attack_per_sec } else { release_per_sec };

    if rate <= 0.0 {
        return current;
    }

    // Never step past the target. MoveTowards on the signed axis rather than on
    // the magnitude, so a reversal crosses zero at the same rate rather than
    // teleporting across the gap.
    mathf::move_towards(current, target, rate * dt)
}

/// Frame-rate-independent exponential filter of the traverse axis.
///
/// Uses the exact `1 - exp(-dt/tau)` form rather than the common `t * dt`
/// approximation. The approximation is only valid for `dt << tau` and silently
/// becomes non-normalising when it is not: at dt = 2*tau it returns 2.0, which
/// overshoots the target and oscillates. The sim runs a fixed 30 Hz so it does not
/// currently hit those cases, but an exact form costs one `exp` and cannot break
/// if the tick rate is ever changed.
pub fn lerp_axis(current: f32, commanded: f32, dt: f32, tau_seconds: f32) -> f32 {
    let target = mathf::clamp(commanded, -1.0, 1.0);
    if tau_seconds <= 1e-5 {
        return target;
    }

    let k = 1.0 - mathf::exp(-dt / tau_seconds);
    current + (target - current) * k
}

/// Blend factor for the steering filter, widening when the driver is reversing
/// direction.
///
/// Traversing straight through from hard left to hard right is the one
/// transition a fixed lerp constant makes feel wrong: the hull is carrying
/// rotational momentum in the old direction, and it should be slower to accept
/// the new command than it is to settle onto a steady turn.
///
/// Expressed as a floor on the lerp's rate rather than a separate path, so the
/// filter stays a single exponential and cannot be defeated by sign changes at the
/// exact tick they occur.
pub fn steer_blend(current: f32, commanded: f32, steer_tau: f32, direction_tau: f32) -> f32 {
    let target = mathf::clamp(commanded, -1.0, 1.0);

    // A reversal is a genuine disagreement in sign, not merely a smaller error.
    let reversing = current * target < 0.0;
    if !reversing {
        return steer_tau;
    }

    // Blend the time constant toward the (slower) direction-change value. Using
    // the constant rather than scaling the blend keeps the result a well-formed
    // exponential in both languages.
    mathf::lerp(steer_tau, direction_tau, MIN_STEER_BLEND)
}

/// Commanded-throttle multiplier for a given forward slope, 0..1.
///
/// This is the one genuinely non-physical term in the shaping layer, and it is
/// here as a deliberate design decision rather than a derived result.
///
/// The traction model already handles gradeability correctly: past the drawbar-pull
/// limit the tracks slip and the vehicle simply fails to climb, which is the
/// physically honest outcome. That is also a poor *game* outcome - the vehicle
/// grinds against a slope with no feedback, and the player cannot tell a marginal
/// climb from a broken one.
///
/// Reducing commanded thrust with slope gives the driver progressive feedback
/// before the physical limit, so the approach to a climb is legible, while the
/// traction model remains the authority on whether it actually succeeds. If this
/// term were removed the vehicle would still behave correctly; it would simply
/// feel unresponsive against a hill.
///
/// Note this is a function of slope angle ALONE and does not feed back into
/// `vehicle_dynamics`, so it cannot mask a traction or friction bug - it only
/// shapes the command going in.
///
/// Below [`CLIMB_FLAT_DEG`] there is no penalty at all, and the multiplier decays
/// smoothly to [`CLIMB_MIN_CAPABILITY`] at the limit.
pub fn climb_capability(forward_slope_deg: f32) -> f32 {
    let s = mathf::abs(forward_slope_deg);
    if s <= CLIMB_FLAT_DEG {
        return 1.0;
    }
    if s >= CLIMB_LIMIT_DEG {
        return CLIMB_MIN_CAPABILITY;
    }

    mathf::lerp(
        1.0,
        CLIMB_MIN_CAPABILITY,
        (s - CLIMB_FLAT_DEG) / (CLIMB_LIMIT_DEG - CLIMB_FLAT_DEG),
    )
}

/// Slope below which no capability penalty applies, degrees.
pub const CLIMB_FLAT_DEG: f32 = 6.0;

/// Slope at which capability has decayed to the floor, degrees.
///
/// Set near the ~29 degree gradeability limit of a real main battle tank
/// (TOP 2-2-610 gives roughly 29-30 degrees for a dry, firm surface), so the
/// shaping and the physical model agree about where the wall is.
pub const CLIMB_LIMIT_DEG: f32 = 28.0;

/// Throttle retained at and beyond the gradeability limit.
pub const CLIMB_MIN_CAPABILITY: f32 = 0.35;

#[cfg(test)]
mod tests {
    use super::*;

    const DT: f32 = 1.0 / 30.0;

    #[test]
    fn ramp_takes_exactly_the_reciprocal_of_the_rate_to_full() {
        // Full throttle from rest at 3.0 units/s must take 1/3 s.
        let mut v = 0.0_f32;
        for _ in 0..10 {
            v = ramp(v, 1.0, DT, DEFAULT_THROTTLE_ATTACK_PER_SEC, DEFAULT_THROTTLE_RELEASE_PER_SEC);
        }
        assert!(
            (v - 1.0).abs() < 1e-5,
            "ten ticks at 30 Hz should saturate, got {v}"
        );
    }

    #[test]
    fn ramp_never_overshoots_the_command() {
        let mut v = 0.0_f32;
        for _ in 0..100 {
            v = ramp(v, 0.4, DT, DEFAULT_THROTTLE_ATTACK_PER_SEC, DEFAULT_THROTTLE_RELEASE_PER_SEC);
            assert!(v <= 0.4 + 1e-6, "overshot a 0.4 command: {v}");
        }
    }

    #[test]
    fn a_full_reversal_unwinds_at_the_release_rate() {
        // Regression: comparing magnitudes alone made a reversal jump to the ATTACK
        // rate after a single tick, because |target| (1.0) exceeds |current| (0.93).
        // A full ahead-to-astern command then unwound at full attack speed instead of
        // taking the slower path through neutral.
        let mut v = 1.0_f32;
        let mut ticks = 0_f32;
        while v > 0.0 && ticks < 5.0 {
            v = ramp(
                v,
                -1.0,
                DT,
                DEFAULT_THROTTLE_ATTACK_PER_SEC,
                DEFAULT_THROTTLE_RELEASE_PER_SEC,
            );
            ticks += DT;
        }

        // 1.0 of axis at the release rate of 2.0/s is half a second.
        assert!(
            (ticks - 0.5).abs() < DT * 2.0,
            "crossing zero should take about half a second, took {ticks}"
        );
    }

#[test]
    fn reversal_settles_at_full_reverse() {
        let mut v = 1.0_f32;
        for _ in 0..200 {
            v = ramp(
                v,
                -1.0,
                DT,
                DEFAULT_THROTTLE_ATTACK_PER_SEC,
                DEFAULT_THROTTLE_RELEASE_PER_SEC,
            );
        }
        assert!((v + 1.0).abs() < 1e-3, "should settle at full reverse, got {v}");
    }

#[test]
    fn release_is_slower_than_attack() {
        // Compare the RATE OF CHANGE, not the absolute value. The two ramps start
        // from opposite states, so comparing levels says nothing about which is
        // slower: after three ticks, "build" has moved 0.1 towards 1.0 while
        // "release" has moved 0.2 down from 1.0, and the released value is simply
        // still nearer its own target.
        let attack_step = ramp(
            0.0,
            1.0,
            DT,
            DEFAULT_THROTTLE_ATTACK_PER_SEC,
            DEFAULT_THROTTLE_RELEASE_PER_SEC,
        ) - 0.0;
        let release_step = 1.0
            - ramp(
                1.0,
                0.0,
                DT,
                DEFAULT_THROTTLE_ATTACK_PER_SEC,
                DEFAULT_THROTTLE_RELEASE_PER_SEC,
            );

        assert!(
            attack_step > release_step,
            "attack {attack_step} should move further per tick than release {release_step}"
        );
        assert!((release_step - DEFAULT_THROTTLE_RELEASE_PER_SEC * DT).abs() < 1e-6);
        assert!((attack_step - DEFAULT_THROTTLE_ATTACK_PER_SEC * DT).abs() < 1e-6);
    }

    #[test]
    fn reversal_crosses_zero_rather_than_teleporting() {
        let mut v = 1.0_f32;
        v = ramp(v, -1.0, DT, DEFAULT_THROTTLE_ATTACK_PER_SEC, DEFAULT_THROTTLE_RELEASE_PER_SEC);
        assert!(v > 0.5, "a single tick must not jump across to reverse: {v}");
    }

    #[test]
    fn lerp_axis_halves_the_error_over_one_tau() {
        let v = lerp_axis(0.0, 1.0, DEFAULT_STEER_LERP_SECONDS, DEFAULT_STEER_LERP_SECONDS);
        assert!((v - 0.632).abs() < 0.01, "expected 1 - exp(-1), got {v}");
    }

    #[test]
    fn lerp_axis_is_bounded_at_large_dt() {
        // The classic `t * dt` approximation returns 2.0 here and overshoots.
        let v = lerp_axis(0.0, 1.0, 10.0, 1.0);
        assert!((v - 1.0).abs() < 1e-4, "large dt overshot: {v}");
    }

    #[test]
    fn steer_blend_is_slower_through_neutral() {
        let same = steer_blend(1.0, 1.0, DEFAULT_STEER_LERP_SECONDS, DEFAULT_DIRECTION_CHANGE_SECONDS);
        let flip = steer_blend(1.0, -1.0, DEFAULT_STEER_LERP_SECONDS, DEFAULT_DIRECTION_CHANGE_SECONDS);
        assert_eq!(same, DEFAULT_STEER_LERP_SECONDS);
        assert!(flip > same, "reversal should use the longer constant");
    }

    #[test]
    fn climb_is_flat_then_decays_to_the_floor() {
        assert_eq!(climb_capability(0.0), 1.0);
        assert_eq!(climb_capability(6.0), 1.0);
        assert_eq!(climb_capability(-6.0), 1.0, "sign must not matter");
        assert_eq!(climb_capability(28.0), CLIMB_MIN_CAPABILITY);
        assert_eq!(climb_capability(60.0), CLIMB_MIN_CAPABILITY, "clamped past the limit");

        let mid = climb_capability(17.0);
        assert!(
            (CLIMB_MIN_CAPABILITY + 0.01) < mid && mid < 0.99,
            "mid-slope should be strictly between the endpoints, got {mid}"
        );
    }
}