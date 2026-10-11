//! Terrain-dependent motion for a tracked vehicle.
//!
//! Rust mirror of `Assets/Scripts/Gameplay/TrackedVehicleDynamics.cs`. The two must
//! stay in step: the parity harness drives a Rust `BattleSim` and a C# `BattleSim`
//! through identical scripted scenarios and compares them tick for tick, so any
//! divergence in this model shows up as a position divergence.
//!
//! Physics basis
//! -------------
//! The model is the standard terrain-vehicle decomposition used for military mobility
//! analysis (US Army Corps of Engineers ERDC/GSL TR-05-6 "Standard for Ground
//! Vehicle Mobility"; TOP 2-2-610 "Gradeability and Side Slope Performance"). A
//! vehicle on a slope has TWO independent limits, and conflating them is the single
//! most common error in arcade tank handling:
//!
//!  1. **Traction** -- the tracks must develop enough force to climb. Governed by the
//!     drawbar pull the vehicle can generate against its own weight:
//!     `sin(theta) = P / W`. Past this the tracks simply slip.
//!  2. **Tipping** -- the centre of gravity must stay inside the support polygon. For
//!     a longitudinal slope the critical angle is `tan(theta_max) = h_cg / d_half`,
//!     the CG height over the half-width of the track contact patch.
//!
//! The previous implementation had neither. It multiplied displacement by a scalar
//! between 1.0 and 0, applied **only uphill** (so gravity was absent entirely and a
//! tank rolled downhill faster and faster), never touched the speed state, and
//! ignored side slope completely.
//!
//! Everything below is expressed as an acceleration in m/s^2 rather than as a
//! multiplier on distance, so the two integrate together and the HUD speed and the
//! actual motion can no longer disagree.

use crate::gameplay::get_terrain_height;
use crate::math::mathf;

/// Driver-input shaping: the rate limiters between the raw input axis and the
/// accelerations above. Lives here because it is the other half of the same
/// driver-to-traction pipeline, and it has to be mirrored in lockstep.
pub mod input_shaping;

pub use input_shaping as shaping;

/// Standard gravity, m/s^2.
pub const GRAVITY: f32 = 9.81;

// ------------------------------------------------------------- contact geometry

/// Distance between front and rear road wheels, metres.
pub const TRACK_GAUGE_M: f32 = 3.4;
/// Overall track width, metres.
pub const TRACK_WIDTH_M: f32 = 3.0;
/// Half the wheelbase, for the front/rear contact samples.
pub const HALF_GAUGE_M: f32 = TRACK_GAUGE_M * 0.5;
/// Half the track width, for the left/right contact samples.
pub const HALF_TRACK_WIDTH_M: f32 = TRACK_WIDTH_M * 0.5;
/// Height of the centre of gravity above the ground plane, metres. Sets the tipping
/// limit: `tan(theta_max) = h_cg / d_half`.
pub const CG_HEIGHT_M: f32 = 1.15;

/// Lateral slip stiffness, m/s^2 per m/s of sideways velocity.
///
/// Set so the full friction limit is reached at about half a metre per second of
/// slip.
pub const LATERAL_SLIP_STIFFNESS_PER_MPS: f32 = 16.7;

/// Slowest yaw rate achievable by pivoting on the spot, rad/s.
///
/// A skid-steered vehicle genuinely cannot turn without moving -- its tracks have to
/// push against something. But it CAN neutral-turn, by driving one track backwards
/// against the other and scrubbing both across the ground. That costs an enormous
/// amount of energy and is very slow, which is exactly why it is modelled as a small
/// floor rather than either "no turn at all" or a free 360-degree pivot.
pub const MIN_NEUTRAL_TURN_RATE_RAD: f32 = 0.35;

// --- Suspension footprint ---------------------------------------------
//
// The contact geometry the SUSPENSION uses is measured from the rendered vehicle, and
// is deliberately separate from TRACK_GAUGE_M/TRACK_WIDTH_M above. Those feed the
// skid-steer yaw rate, where the number means "distance between track centrelines"
// and is used as a pure turning geometry.
//
// These three are measured off the Panzer IV rig that actually ships:
//   * the track contact patch is 5.89 m long, not 3.4 m;
//   * it is 2.97 m wide;
//   * and its centre sits 1.42 m BEHIND the model origin, because the origin is not
//     the hull centre.
//
// The old constants put the simulated contacts at +/-1.70 m around a 9 m tank, so the
// suspension only ever felt the middle third of the vehicle. The last 2.5 m of track
// at the rear was invisible to the physics: it could bury itself in a rise, or hang
// in the air over a dip, while the hull reported a healthy ride height. The renderer
// used to hide this by clamping the model up to the highest of four corner samples;
// once that clamp went away -- correctly, it was rigid-body behaviour -- the mismatch
// became visible.
//
// At scale 1, measured from the Panzer IV mesh.
pub const BASE_CONTACT_HALF_LENGTH_M: f32 = 2.947;
pub const BASE_CONTACT_HALF_WIDTH_M: f32 = 1.485;
pub const BASE_CONTACT_CENTRE_Z_M: f32 = -1.418;

/// Render scale for a vehicle, mirroring `WarArenaBootstrap.BuildTank`.
///
/// The renderer scales the whole rig by this, so the suspension's footprint has to
/// scale with it. When these two drift apart the vehicle drives on a contact patch of
/// the wrong size, which is the same class of bug as measuring a car with a bus's
/// wheelbase.
pub fn vehicle_scale(mass_kg: f32) -> f32 {
    mathf::clamp(mass_kg / 26000.0, 0.7, 1.35)
}

/// Contact half-length in metres for a vehicle of the given mass.
pub fn contact_half_length_m(mass_kg: f32) -> f32 {
    BASE_CONTACT_HALF_LENGTH_M * vehicle_scale(mass_kg)
}

/// Contact half-width in metres for a vehicle of the given mass.
pub fn contact_half_width_m(mass_kg: f32) -> f32 {
    BASE_CONTACT_HALF_WIDTH_M * vehicle_scale(mass_kg)
}

/// Longitudinal offset of the contact patch centre from the model origin. Negative is
/// rearward.
pub fn contact_centre_z_m(mass_kg: f32) -> f32 {
    BASE_CONTACT_CENTRE_Z_M * vehicle_scale(mass_kg)
}

// ---------------------------------------------------------------------- terrain

/// Signed angle of the ground along a horizontal direction, degrees.
///
/// Positive means rising in that direction. Exact, via atan2.
///
/// `probe` is a HALF offset from the centre of the hull -- pass [`HALF_GAUGE_M`] and
/// [`HALF_TRACK_WIDTH_M`], not the overall wheelbase and track width. Passing the full
/// dimensions samples the terrain twice as far out, which on any curvature reports a
/// different slope and silently desynchronises the two language implementations.
pub fn slope_along_terrain(x: f32, z: f32, dir: (f32, f32), probe: f32) -> f32 {
    let ahead = get_terrain_height(x + dir.0 * probe, z + dir.1 * probe);
    let behind = get_terrain_height(x - dir.0 * probe, z - dir.1 * probe);
    mathf::atan2(ahead - behind, 2.0 * probe) * mathf::RAD2DEG
}

/// Gravity's component along the ground surface, resolved into the vehicle's heading
/// frame. Returns `(along_heading, across)`, and writes the normal load in G into
/// `normal_load_g`.
///
/// Computed from the surface normal rather than as `-g*sin(slope)` so it stays correct
/// on a side slope, where the vehicle accelerates *sideways* down the fall line. That
/// sideways pull is what makes a tank on a hillside want to slide off, and it is the
/// reason a real one turns into the contour rather than driving straight across.
///
/// As a useful side effect this makes the result conservative at zero input: the tank
/// runs away downhill on its own, exactly as it should, and the player has to brake or
/// steer into the slope.
pub fn gravity_on_ground_plane(
    forward_slope_deg: f32,
    lateral_slope_deg: f32,
    normal_load_g: &mut f32,
) -> (f32, f32) {
    let along = -mathf::sin(forward_slope_deg * mathf::DEG2RAD) * GRAVITY;
    let across = -mathf::sin(lateral_slope_deg * mathf::DEG2RAD) * GRAVITY;

    *normal_load_g =
        mathf::cos(forward_slope_deg * mathf::DEG2RAD) * mathf::cos(lateral_slope_deg * mathf::DEG2RAD);

    (along, across)
}

// ----------------------------------------------------------------- longitudinal

/// Engine acceleration along the heading, m/s^2.
///
/// Modelled as tractive effort rather than a target speed, so the same force budget
/// pays for climbing. A tank delivers roughly constant tractive force at low speed and
/// constant *power* above the point where resistance balances it, so available force
/// falls as 1/v.
///
/// The `max_speed_mps` governor matters: without it the terminal speed is simply
/// max_accel / rolling_resistance, which for the catalogue values works out around
/// 98 km/h -- more than double the fastest tank in the roster. In the game the vehicle
/// would accelerate without limit and every distance in the ballistics model would be
/// scaled wrongly.
///
/// The rated top speed IS the speed at which engine power balances resistance; past it
/// the engine is simply cut, which is what the governor expresses.
pub fn engine_acceleration(
    throttle: f32,
    speed_mps: f32,
    max_accel: f32,
    speed_at_max_power: f32,
    max_speed_mps: f32,
) -> f32 {
    let t = mathf::clamp(throttle, -1.0, 1.0);
    if mathf::abs(t) < 1e-4 {
        return 0.0;
    }

    // Past the rated top speed in the direction being asked for, the engine makes no
    // further effort.
    let v = speed_mps;
    if mathf::abs(v) >= max_speed_mps && t * v > 0.0 {
        return 0.0;
    }

    let spd = mathf::max(mathf::abs(v), 0.5);
    let falloff = mathf::clamp(speed_at_max_power / spd, 0.35, 1.0);
    t * max_accel * falloff
}

/// Rolling resistance acceleration, m/s^2.
///
/// The coefficient band of 0.03-0.09 is the documented range for tracked vehicles on
/// firm ground (DTIC TR-06-6 and tank-theory sources). The resistance is proportional
/// to the normal load rather than to weight, so it correctly falls away as the vehicle
/// climbs onto a slope and unloads its tracks.
///
/// Unlike the old model this term is signed by the direction of travel and therefore
/// applies when coasting, reversing, or rolling backwards -- not only under throttle.
pub fn rolling_resistance(speed_mps: f32, normal_load_g: f32, coeff: f32) -> f32 {
    if mathf::abs(speed_mps) < 1e-3 {
        return 0.0;
    }
    let mag = coeff * normal_load_g * GRAVITY;
    -mathf::sign(speed_mps) * mag
}

// --------------------------------------------------------------------- steering

/// Turn rate available from a skid-steered (differential) input, rad/s.
///
/// A track-laying vehicle has no steering wheel. It turns by driving one track faster
/// than the other. If the two track speeds are `v(1 +/- s*k)`, the instantaneous centre
/// of rotation sits at `R = L / (2*s*k)` -- independent of speed -- so the turn RADIUS
/// is roughly constant and the YAW RATE is proportional to speed:
///
/// `yaw = (|v| / gauge) * s`
///
/// That is the opposite of the intuitive "cars turn better when slow", and it is what
/// makes a tank feel like a tank: at a crawl it barely rotates, at speed it swings round
/// hard. It is also why the vehicle cannot rotate at all when stationary -- there is
/// nothing for the tracks to push against.
///
/// On top of that sits a small floor representing the NEUTRAL TURN: the driver can lock
/// one track and drive the other, pivoting on the spot. Real tanks do this, but it
/// scrubs both tracks across the ground, so it is very slow and expensive. Without the
/// floor the vehicle would be unable to rotate while parked, which combined with the
/// hull auto-align being gated on motion would leave a player broadside with no way to
/// turn around.
pub fn skid_steer_yaw_rate(
    speed_mps: f32,
    steer_norm: f32,
    gauge_m: f32,
    max_yaw_rate_rad: f32,
    speed_at_max_yaw_rad: f32,
) -> f32 {
    let s = mathf::clamp(steer_norm, -1.0, 1.0);
    if mathf::abs(s) < 1e-4 {
        return 0.0;
    }

    let v = mathf::abs(speed_mps);

    // Differential-limited: the yaw the track-speed difference can support.
    let mut differential = if gauge_m > 1e-3 { v / gauge_m } else { max_yaw_rate_rad };

    // Above the speed at which full lock no longer uses the whole differential, the
    // excess authority is not usable.
    let authority = 1.0 / (1.0 + v / mathf::max(speed_at_max_yaw_rad, 0.5));
    differential = mathf::min(differential, max_yaw_rate_rad * authority);

    // Neutral turn: one track locked, the other driven, scrubbing on the spot. This
    // takes OVER from the differential as speed falls away rather than adding to it --
    // they are alternative mechanisms for turning the same hull, and summing them would
    // let a crawling vehicle exceed the yaw its tracks could physically produce.
    let neutral =
        MIN_NEUTRAL_TURN_RATE_RAD * mathf::exp(-v / mathf::max(speed_at_max_yaw_rad, 0.5));

    mathf::clamp(
        mathf::max(differential, neutral) * s,
        -max_yaw_rate_rad,
        max_yaw_rate_rad,
    )
}

/// Fraction of top speed lost to track scrub while skidding to turn.
///
/// Pivot steering at low speed drags the whole track width sideways across the ground.
/// That is a real and substantial energy loss and it is the reason neutral turns cost
/// you momentum. Modelling it keeps tight low-speed handling from being strictly
/// dominant.
pub fn skid_scrub_speed_loss(steer_norm: f32, speed_mps: f32) -> f32 {
    let scrub = mathf::abs(mathf::clamp(steer_norm, -1.0, 1.0));
    -scrub * scrub * mathf::abs(speed_mps) * 0.35
}

// ------------------------------------------------------------------------ limits

/// Critical tilt angle from the CG height over the track contact half-width, degrees.
/// Past this the vehicle cannot stay on its wheels.
///
/// TOP 2-2-610 gives the critical angle as `B = atan(h / d)`. With a CG height of about
/// 1.15 m and a 1.2 m contact half-width this lands near 44 degrees, which is why tanks
/// can be driven onto very steep ground and why the limit in practice is traction, not
/// balance, on firm terrain.
pub fn critical_tilt_deg(cg_height_m: f32, contact_half_width_m: f32) -> f32 {
    if contact_half_width_m <= 1e-3 {
        return 89.0;
    }
    mathf::atan2(cg_height_m, contact_half_width_m) * mathf::RAD2DEG
}

/// Steering authority multiplier on a given side slope.
///
/// On a side slope a real vehicle is steered partly by the differential and partly by
/// sliding: the driver turns *into* the slope. A vehicle already sliding down the fall
/// line has some of its lateral friction spent before it turns at all, so the effective
/// authority drops. This is what produces the characteristic "fight the tank round a
/// hillside" behaviour.
pub fn lateral_slope_steering_scale(lateral_slope_deg: f32) -> f32 {
    let a = mathf::abs(lateral_slope_deg);
    if a <= 12.0 {
        return 1.0;
    }
    let t = mathf::clamp((a - 12.0) / 20.0, 0.0, 1.0);
    1.0 + (0.55 - 1.0) * t
}

/// Lateral acceleration from track friction, m/s^2.
///
/// This is a STIFFNESS-limited restoring force, not a bang-bang controller:
///
/// `a = -clamp(k * v_lat, -grip, +grip)`
///
/// A small sideways slip must produce a small correcting force. Returning the full
/// friction limit for any non-zero slip looks harmless but is not: the Kamm circle is
/// then saturated by the tiniest cross-slope, leaving zero budget for longitudinal
/// force, and the vehicle stops accelerating while still rolling. On any terrain with a
/// lateral gradient that is a permanent lock-up, not a handling characteristic.
///
/// The stiffness is set so the full friction limit is reached at roughly half a metre
/// per second of slip, which is firm without being twitchy.
pub fn lateral_grip_accel(
    lateral_speed_mps: f32,
    normal_load_g: f32,
    coeff: f32,
    slip_stiffness_per_mps: f32,
) -> f32 {
    let demand = -lateral_speed_mps * slip_stiffness_per_mps;
    let grip = coeff * normal_load_g * GRAVITY;
    mathf::clamp(demand, -grip, grip)
}

/// Longitudinal acceleration actually available once the friction circle is applied,
/// m/s^2.
///
/// The Kamm circle: `sqrt(F_long^2 + F_lat^2) <= mu * N`. Longitudinal and lateral
/// demand compete for a single friction budget, so a vehicle that is already sliding
/// sideways cannot accelerate as hard. Without this, traction is infinite in any
/// direction at once and the handling feels like driving on rails.
pub fn apply_friction_circle(
    desired_long_accel: f32,
    lateral_demand_accel: f32,
    grip_accel: f32,
) -> f32 {
    if grip_accel <= 1e-3 {
        return 0.0;
    }
    let remaining = grip_accel * grip_accel - lateral_demand_accel * lateral_demand_accel;
    if remaining <= 0.0 {
        return 0.0;
    }
    let cap = mathf::sqrt(remaining);
    mathf::clamp(desired_long_accel, -cap, cap)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slope_along_terrain_is_signed_and_zero_on_flat_ground() {
        let flat = slope_along_terrain(0.0, 0.0, (1.0, 0.0), HALF_GAUGE_M);
        assert_eq!(flat, 0.0, "the inner bowl is dead flat");
    }

    #[test]
    fn slope_along_terrain_sign_follows_the_direction_of_travel() {
        // Find a spot that genuinely rises along +X.
        let (x, z) = (300.0_f32, 0.0_f32);
        let a = slope_along_terrain(x, z, (1.0, 0.0), HALF_GAUGE_M);
        let b = slope_along_terrain(x, z, (-1.0, 0.0), HALF_GAUGE_M);
        assert!(a.abs() > 0.5 || b.abs() > 0.5, "test location is flat");
        assert!((a + b).abs() < 1e-3, "reversing the probe must negate the slope");
    }

    #[test]
    fn gravity_on_ground_plane_pulls_downhill_and_unloads_the_tracks() {
        let mut n = 0.0;
        let (uphill_facing, _) = gravity_on_ground_plane(10.0, 0.0, &mut n);
        let (downhill_facing, _) = gravity_on_ground_plane(-10.0, 0.0, &mut n);
        assert!(uphill_facing < 0.0, "facing uphill, gravity must pull backwards");
        assert!(downhill_facing > 0.0, "facing downhill, gravity must pull forwards");
        assert!(
            (uphill_facing + downhill_facing).abs() < 1e-3,
            "the two must be symmetric"
        );
    }

    #[test]
    fn gravity_on_ground_plane_matches_the_closed_form_on_a_uniform_slope() {
        let mut n = 0.0;
        let (descent, _) = gravity_on_ground_plane(-15.0, 0.0, &mut n);
        let expected = GRAVITY * mathf::sin(15.0 * mathf::DEG2RAD);
        assert!((descent - expected).abs() < 1e-3, "{descent} vs {expected}");
    }

    #[test]
    fn gravity_on_ground_plane_reduces_the_normal_load_on_a_slope() {
        let mut flat = 0.0;
        let _ = gravity_on_ground_plane(0.0, 0.0, &mut flat);
        let mut steep = 0.0;
        let _ = gravity_on_ground_plane(-30.0, 0.0, &mut steep);
        assert!((flat - 1.0).abs() < 1e-4, "flat ground carries full weight");
        assert!(steep < flat, "a slope unloads the tracks");
        assert!(steep > 0.0, "the normal load must stay positive");
    }

    #[test]
    fn gravity_on_ground_plane_drives_a_vehicle_sideways_down_a_side_slope() {
        let mut n = 0.0;
        let (_, right_falling) = gravity_on_ground_plane(0.0, -10.0, &mut n);
        assert!(
            right_falling > 0.0,
            "a right-hand fall line must pull the vehicle to its right"
        );
    }

    #[test]
    fn engine_acceleration_is_proportional_to_throttle_and_bounded() {
        assert_eq!(engine_acceleration(0.0, 0.0, 4.0, 8.0, 12.0), 0.0);
        let half = engine_acceleration(0.5, 0.0, 4.0, 8.0, 12.0);
        let full = engine_acceleration(1.0, 0.0, 4.0, 8.0, 12.0);
        assert!((half - full * 0.5).abs() < 1e-5);
        assert!(full > 0.0 && full <= 4.0);
    }

    #[test]
    fn engine_acceleration_cuts_out_past_the_rated_top_speed() {
        assert_eq!(engine_acceleration(1.0, 12.0, 4.0, 8.0, 12.0), 0.0);
        // ...but reversing from top speed is still allowed.
        assert!(
            engine_acceleration(-1.0, 12.0, 4.0, 8.0, 12.0) < 0.0,
            "braking must not be governed by the top-speed limiter"
        );
    }

    #[test]
    fn engine_acceleration_falls_off_as_a_1_over_v_power_curve() {
        // The falloff is clamped to 1.0 at and below the power-limited speed, so the
        // curve only bites ABOVE it. Sampling inside that band shows a flat curve and
        // proves nothing.
        let rated = engine_acceleration(1.0, 8.0, 4.0, 8.0, 12.0);
        let above = engine_acceleration(1.0, 11.0, 4.0, 8.0, 12.0);
        assert!((rated - 4.0).abs() < 1e-4, "full effort up to the rated speed");
        assert!(above < rated, "tractive force must fall with speed");
        assert!(above >= rated * 0.35 - 1e-5, "the falloff floor must hold");
        assert!((above - 4.0 * 8.0 / 11.0).abs() < 1e-4);
    }

    #[test]
    fn rolling_resistance_opposes_motion_and_scales_with_normal_load() {
        assert_eq!(rolling_resistance(0.0, 1.0, 0.06), 0.0);
        let forward = rolling_resistance(5.0, 1.0, 0.06);
        let backward = rolling_resistance(-5.0, 1.0, 0.06);
        assert!(forward < 0.0, "resistance must oppose forward motion");
        assert!(backward > 0.0, "resistance must oppose reverse motion");
        let unloaded = rolling_resistance(5.0, 0.5, 0.06);
        assert!(unloaded.abs() < forward.abs(), "less load, less resistance");
    }

    #[test]
    fn lateral_grip_accel_is_a_stiffness_not_a_bang_bang_controller() {
        let tiny = lateral_grip_accel(0.05, 1.0, 0.85, LATERAL_SLIP_STIFFNESS_PER_MPS);
        assert!(
            tiny.abs() < 1.0,
            "a small slip must give a small force, or the friction circle is always saturated"
        );
        let saturated = lateral_grip_accel(5.0, 1.0, 0.85, LATERAL_SLIP_STIFFNESS_PER_MPS);
        assert!((saturated + 0.85 * GRAVITY).abs() < 1e-3, "must clamp at -grip");
        assert!(lateral_grip_accel(-5.0, 1.0, 0.85, LATERAL_SLIP_STIFFNESS_PER_MPS) > 0.0);
    }

    #[test]
    fn friction_circle_trades_longitudinal_against_lateral() {
        let grip = 8.34;
        assert!((apply_friction_circle(100.0, 0.0, grip) - grip).abs() < 1e-4);
        let reduced = apply_friction_circle(100.0, 5.0, grip);
        assert!(reduced < grip, "sliding sideways must cost longitudinal grip");
        let expected = (grip * grip - 25.0).sqrt();
        assert!((reduced - expected).abs() < 1e-4);
        assert_eq!(apply_friction_circle(100.0, grip, grip), 0.0, "fully saturated");
    }

    #[test]
    fn skid_steer_yaw_rate_grows_with_speed() {
        let slow = skid_steer_yaw_rate(1.0, 1.0, TRACK_GAUGE_M, 1.8, 4.5);
        let quicker = skid_steer_yaw_rate(3.0, 1.0, TRACK_GAUGE_M, 1.8, 4.5);
        assert!(quicker > slow, "a tracked vehicle turns harder the faster it goes");
    }

    #[test]
    fn skid_steer_yaw_rate_has_a_neutral_turn_floor() {
        let parked = skid_steer_yaw_rate(0.0, 1.0, TRACK_GAUGE_M, 1.8, 4.5);
        assert!(parked > 0.0, "a tank can pivot on the spot, just very slowly");
        assert!(
            parked <= MIN_NEUTRAL_TURN_RATE_RAD + 1e-5,
            "and it must be no quicker than the neutral turn rate"
        );
        assert_eq!(skid_steer_yaw_rate(5.0, 0.0, TRACK_GAUGE_M, 1.8, 4.5), 0.0);
    }

    #[test]
    fn skid_steer_yaw_rate_is_clamped_and_proportional_to_steer() {
        for speed in [0.0_f32, 1.0, 5.0, 20.0, 100.0] {
            let full = skid_steer_yaw_rate(speed, 1.0, TRACK_GAUGE_M, 1.8, 4.5);
            assert!(full <= 1.8 + 1e-5, "must never exceed the mechanical limit");
            assert!(full >= -1e-5);
        }
        let half = skid_steer_yaw_rate(3.0, 0.5, TRACK_GAUGE_M, 1.8, 4.5);
        let full = skid_steer_yaw_rate(3.0, 1.0, TRACK_GAUGE_M, 1.8, 4.5);
        assert!((half - full * 0.5).abs() < 1e-5);
    }

    #[test]
    fn skid_scrub_costs_more_the_harder_you_turn() {
        let straight = skid_scrub_speed_loss(0.0, 6.0);
        let gentle = skid_scrub_speed_loss(0.5, 6.0);
        let hard = skid_scrub_speed_loss(1.0, 6.0);
        assert_eq!(straight, 0.0);
        assert!(gentle < straight && hard < gentle, "scrub must grow with steer");
        assert!(hard < 0.0, "scrub always removes speed");
    }

    #[test]
    fn lateral_slope_steering_scale_reduces_authority_on_a_hillside() {
        assert_eq!(lateral_slope_steering_scale(0.0), 1.0);
        assert_eq!(lateral_slope_steering_scale(-12.0), 1.0);
        let mid = lateral_slope_steering_scale(22.0);
        let steep = lateral_slope_steering_scale(40.0);
        assert!(mid < 1.0 && mid > 0.55, "authority tapers off");
        assert!(steep <= 0.55 + 1e-5, "and bottoms out");
    }

    #[test]
    fn critical_tilt_is_about_44_degrees_for_a_real_tank() {
        let tilt = critical_tilt_deg(CG_HEIGHT_M, 1.2);
        assert!((tilt - 43.8).abs() < 1.0, "got {tilt}");
        assert_eq!(critical_tilt_deg(CG_HEIGHT_M, 0.0), 89.0, "degenerate guard");
    }

    #[test]
    fn suspension_footprint_tracks_the_rendered_vehicle_scale() {
        // The bootstrap renders at clamp(mass/26000, 0.7, 1.35). A 25 t Panzer IV
        // must come out at 0.9615, so its simulated contact patch is 5.67 m long --
        // not the 3.4 m the generic wheelbase constants imply.
        let s = vehicle_scale(25000.0);
        assert!((s - 25000.0 / 26000.0).abs() < 1e-4);
        assert!(contact_half_length_m(25000.0) > 2.8);
        assert!(
            (contact_half_length_m(25000.0) - 2.947 * s).abs() < 1e-5,
            "the footprint must scale with the rig"
        );
        // The patch is rear-biased on the real model.
        assert!(contact_centre_z_m(25000.0) < 0.0);
    }

    #[test]
    fn suspension_footprint_is_clamped_like_the_renderer() {
        assert_eq!(vehicle_scale(1.0), 0.7, "a light vehicle does not shrink to nothing");
        assert_eq!(vehicle_scale(1_000_000.0), 1.35, "nor grow without limit");
        assert!(contact_half_length_m(1_000_000.0) < contact_half_length_m(1.0).max(4.2));
    }
}