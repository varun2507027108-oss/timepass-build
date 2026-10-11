//! The ground battle: two vehicles driving a fixed-step arena, shells as
//! travelling projectiles, and hit resolution through the real armour model.
//!
//! Ported from `Assets/Scripts/Gameplay/BattleSim.cs`.

use crate::ballistics::{ArmourPlate, ImpactResult, Penetration, Shell, ShellType};
use crate::math::{mathf, Vec3};
use crate::vehicle_dynamics;
use crate::vehicle_dynamics::input_shaping;
use crate::vehicles::{VehicleCatalog, VehicleDefinition};

/// Simulation rate. 30 Hz is plenty for a 40 km/h vehicle.
pub const TICK_HZ: f32 = 30.0;
pub const FIXED_DT: f32 = 1.0 / TICK_HZ;

/// Radius of the circular playable arena, metres.
pub const ARENA_RADIUS: f32 = 440.0;
/// Compatibility alias for legacy tests and scripts.
pub const ARENA_HALF_EXTENT: f32 = ARENA_RADIUS;

pub fn clamp_to_arena(x: f32, z: f32) -> (f32, f32) {
    let r = mathf::sqrt(x * x + z * z);
    if r <= ARENA_RADIUS {
        return (x, z);
    }
    if r < 1e-4 {
        return (0.0, 0.0);
    }
    let k = ARENA_RADIUS / r;
    (x * k, z * k)
}

pub fn get_terrain_height(x: f32, z: f32) -> f32 {
    let r = mathf::sqrt(x * x + z * z);
    if r <= 85.0 {
        return 0.0;
    }

    let t = mathf::clamp01((r - 85.0) / (450.0 - 85.0));
    let w = mathf::smooth_step(0.0, 1.0, t * 2.2);

    let h1 = mathf::sin(x * 0.016 + 1.2) * mathf::cos(z * 0.014 + 0.7) * 16.0;
    let h2 = mathf::cos(x * 0.030 - z * 0.026) * 9.0;
    let ridge = mathf::abs(mathf::sin(x * 0.011 + z * 0.009 + 0.5)) * 14.0;
    let outer_rim = mathf::pow(t, 1.8) * 44.0;

    mathf::max(0.0, w * (h1 + h2 + ridge + outer_rim))
}

pub fn slope_speed_factor(x: f32, z: f32, move_dir_x: f32, move_dir_z: f32) -> f32 {
    const D: f32 = 1.5;
    let ahead = get_terrain_height(x + move_dir_x * D, z + move_dir_z * D);
    let behind = get_terrain_height(x - move_dir_x * D, z - move_dir_z * D);
    let deg = mathf::atan2(ahead - behind, 2.0 * D) * mathf::RAD2DEG;

    if deg <= 0.0 {
        return 1.0;
    }
    if deg >= 32.0 {
        return 0.0;
    }
    if deg <= 20.0 {
        return mathf::lerp(1.0, 0.6, deg / 20.0);
    }
    mathf::lerp(0.6, 0.25, (deg - 20.0) / 12.0)
}

/// Hard ceiling on catch-up ticks so a stalled tab cannot teleport units.
const MAX_STEPS_PER_FRAME: i32 = 4;

/// Turret traverse rate, radians per second.
pub const TURRET_SLEW_RATE: f32 = 2.6;

/// Maximum turret travel relative to the hull, radians.
pub const MAX_TRAVERSE: f32 = 2.1;

/// Gun depression limit, radians.
pub const MIN_GUN_PITCH_RAD: f32 = -10.0 * mathf::DEG2RAD;
/// Gun elevation limit, radians.
pub const MAX_GUN_PITCH_RAD: f32 = 20.0 * mathf::DEG2RAD;
/// Gun elevation slew rate, radians per second.
pub const GUN_ELEVATION_SLEW_RATE: f32 = 1.8;

const MAX_SPEED_MPS: f32 = 12.0;
const MAX_TURN_RATE_RAD: f32 = 1.8;
const ACCELERATION_MPS2: f32 = 12.0;
const COAST_DECEL_MPS2: f32 = 8.0;

/// Speed above which available acceleration becomes power-limited and falls off as
/// 1/v. Real traction effort is roughly constant below this and constant power
/// above it.
const POWER_LIMITED_SPEED_MPS: f32 = 6.0;

/// Coefficient of rolling resistance on firm ground. The documented band for
/// tracked vehicles is 0.03-0.09 (DTIC TR-06-6); 0.055 is mid-range for a tank on
/// packed earth.
const ROLLING_RESISTANCE_COEFF: f32 = 0.055;

/// Friction coefficient resisting sideways motion. Much higher than rolling
/// resistance: tracks do not skid sideways easily, which is why a tank holds a line
/// across a camber.
const LATERAL_FRICTION_COEFF: f32 = 0.85;

/// Total friction budget available from the tracks, m/s^2.
const TRACKED_GRIP_ACCEL_MPS2: f32 = 9.81 * 0.85;

/// Deceleration when the throttle opposes the direction of travel.
const BRAKE_ACCEL_MPS2: f32 = 12.0;

/// Speed at which full steering input still produces the full turn rate, m/s.
/// Above this the differential must push harder for the same radius.
const SPEED_AT_MAX_YAW_MPS: f32 = 4.5;

/// How far the two track speeds separate under full steering, as a fraction of the
/// vehicle's own speed. This is the physical meaning of the steering axis: a
/// skid-steered vehicle turns by driving one track faster than the other.
pub const DIFFERENTIAL_SKEW: f32 = 0.5;

/// How strongly the hull is pulled onto the gun's line while driving.
///
/// 1.0 would snap the hull directly at the gun every frame and make the turret's
/// local angle collapse to zero, throwing away manual traverse entirely. At 0.35
/// the vehicle lines up over a couple of seconds of driving, so the hull visibly
/// follows the turret without the turret feeling welded to the centreline.
const HULL_AUTO_ALIGN_STRENGTH: f32 = 0.35;

/// Where the turret ring sits relative to the hull origin: matches the rig built in
/// `TankView`, which parents the turret at local z = -0.3, slightly behind the
/// hull centre.
const TURRET_PIVOT_BEHIND_M: f32 = 0.3;

/// Height of the gunner's eye above the ground, metres. Used when converting an
/// aim point into a gun elevation.
const AIM_EYE_HEIGHT_M: f32 = 0.15;
/// Muzzle height above the ground for the player, metres.
const PLAYER_MUZZLE_HEIGHT_M: f32 = 2.2;
/// Muzzle height above the ground for the enemy, metres.
const ENEMY_MUZZLE_HEIGHT_M: f32 = 1.6;

/// Fraction of the round's muzzle velocity a shell actually leaves the gun at.
///
/// Deliberately far below 1: this is the projectile's retained velocity over the
/// barrel, and it is what makes long-range matchesup results depend on drag rather
/// than on a flat damage number.
const SHELL_VELOCITY_SCALE: f32 = 0.42;

/// Seconds a shell stays in flight before expiring.
const SHELL_LIFE_S: f32 = 4.0;

/// Hit radius in the horizontal plane, metres: roughly the hull footprint plus the
/// turret.
const HIT_RADIUS_M: f32 = 3.2;
/// How far above a vehicle's own ground contact a shell may pass and still strike
/// the hull, metres.
///
/// This is a RELATIVE ceiling, measured from the target's position. It must never be
/// compared against an absolute world height: since the arena became a 440 m circle
/// with analytic terrain, vehicles sit anywhere from 0 m to ~74 m above sea level,
/// and an absolute ceiling silently makes every shot on high ground miss.
const HIT_CEILING_M: f32 = 4.5;
/// How far below a vehicle's own ground contact a shell may pass and still strike
/// the hull, metres. Guards against a shell arriving from a downhill position.
const HIT_FLOOR_M: f32 = 1.0;
/// How far outside the arena a shell may travel before being discarded.
const SHELL_CULL_MARGIN_M: f32 = 60.0;

/// Damage a penetrating player shell does.
const PLAYER_SHELL_DAMAGE: i32 = 34;
/// Damage a penetrating enemy shell does.
const ENEMY_SHELL_DAMAGE: i32 = 26;
/// Seconds before a destroyed vehicle returns.
const RESPAWN_DELAY_S: f32 = 2.0;
/// Extra reload delay given to the AI, seconds. It is a target-drill opponent.
const ENEMY_RELOAD_HANDICAP_S: f32 = 1.5;
/// Range at which the AI opens fire, metres.
const ENEMY_FIRE_RANGE_M: f32 = 60.0;
/// Distance inside which the AI throttles back, metres.
const AI_CLOSE_RANGE_M: f32 = 26.0;
/// The AI's normal throttle.
const AI_THROTTLE: f32 = 0.45;
/// The AI's throttle when close.
const AI_THROTTLE_CLOSE: f32 = 0.1;
/// The AI is slower than the player, by design.
/// How hard the AI slews its hull, radians per second.
/// Half-angle of the frontal arc: a round arriving within this of head-on (or of
/// dead astern) strikes the front (or rear) plate rather than the side.
pub const FRONT_PLATE_CONE_DEG: f32 = 45.0;

/// Amplitude of the AI's weaving weave, radians.
const AI_WEAVE_RAD: f32 = 0.7;
/// Tick rate the AI weave is driven at.
const AI_WEAVE_PHASE: f32 = 0.02;
/// Impulse recoil applies against the player's current speed, m/s.
const RECOIL_IMPULSE_MPS: f32 = 1.2;
/// Newest events kept in the feed.
const FEED_CAPACITY: usize = 4;

/// A shell in flight.
#[derive(Debug, Clone, Copy, PartialEq)]
struct ActiveShell {
    position: Vec3,
    velocity: Vec3,
    life: f32,
    from_player: bool,
    ordnance: Shell,
}

/// The ground battle.
///
/// State is private with read accessors, where C# exposed public mutable fields on
/// a class. That is not pedantry: several of the bugs fixed during this port were
/// only possible because C# could mutate fields from outside, and because the
/// interpolation history was updated from `Advance` rather than from the step it
/// describes.
#[derive(Debug, Clone)]
pub struct BattleSim {
    player_def: &'static VehicleDefinition,
    enemy_def: &'static VehicleDefinition,

    player_position: Vec3,
    player_heading_rad: f32,
    player_turret_yaw_rad: f32,
    player_gun_pitch_rad: f32,
    player_speed_kph: f32,

    enemy_position: Vec3,
    enemy_heading_rad: f32,
    /// Enemy forward and sideways velocity in its hull frame, m/s. Held as state so
    /// the AI obeys the same terrain forces and friction circle as the player.
    enemy_speed_mps: f32,
    enemy_lat_speed_mps: f32,
    enemy_turret_yaw_rad: f32,

    player_health: i32,
    enemy_health: i32,
    kills: i32,
    deaths: i32,

    reload_remaining: f32,

    /// Start of the current render frame, for interpolation.
    player_position_prev: Vec3,
    enemy_position_prev: Vec3,
    player_heading_prev_rad: f32,
    player_gun_pitch_prev_rad: f32,
    /// Fraction of the way from the previous simulation state to the current one.
    ///
    /// Without it the vehicles visibly step: the simulation runs at a fixed rate
    /// while the camera moves smoothly, so the tank judders against a steady
    /// background. Interpolating the drawn transform removes the stepping and costs
    /// nothing.
    interpolation_alpha: f32,

    accumulator: f32,
    enemy_reload: f32,
    respawn_timer: f32,
    tick: i32,
    player_current_speed_mps: f32,
    /// Sideways velocity in the hull's frame, m/s. Non-zero only when the tracks
    /// are sliding - over a side slope, or while skidding out of a turn.
    player_lat_speed_mps: f32,
    /// Filtered throttle actually handed to the traction model, -1..1. The raw
    /// input axis is rate-limited before it reaches `drive_player`. Held as state so
    /// it persists across ticks: `advance` is called once per render frame but steps
    /// up to `MAX_STEPS_PER_FRAME` times, so shaping inside `drive_player` would
    /// advance the ramp at the render rate rather than the tick rate and the two would
    /// disagree whenever the frame rate dipped. Mirrors `_shapedThrottle` in C#.
    shaped_throttle: f32,
    /// Filtered steering actually handed to the differential, -1..1. Mirrors
    /// `_shapedSteer` in C#.
    shaped_steer: f32,
    /// Ground speed of each track, m/s, positive forwards. Drives the track
    /// animation so the wheels read out the simulation rather than replaying input.
    player_left_track_mps: f32,
    player_right_track_mps: f32,
    enemy_left_track_mps: f32,
    enemy_right_track_mps: f32,
    /// Hull pitch and roll at the last tick, degrees. Positive pitch is nose up,
    /// positive roll is right side up.
    ///
    /// Owned by [`crate::hull_suspension`] rather than derived from the terrain
    /// slope: the vehicle's attitude is an emergent property of four independent
    /// spring-dampers, so it survives a slope the suspension would have absorbed
    /// and it dips under braking.
    hull_pitch_deg: f32,
    hull_roll_deg: f32,
    /// Terrain slope under the hull in the hull's frame, degrees.
    slope_forward_deg: f32,
    slope_lateral_deg: f32,
    /// Four independent spring-dampers driving heave, pitch and roll.
    player_suspension: crate::hull_suspension::HullSuspension,
    /// Acceleration the hull is experiencing in its own frame, m/s^2. Fed to the
    /// suspension, which turns it into pitch and roll moments because the centre of
    /// gravity sits above the track plane.
    player_accel_long: f32,
    player_accel_lat: f32,
    /// Solid obstacles the vehicles collide against. Regenerated once at construction
    /// so it is never rebuilt per tick.
    obstacles: Vec<crate::battlefield::Obstacle>,
    /// True when the player touched an obstacle on the most recent step.
    did_collide_with_obstacle: bool,
    in_flight: Vec<ActiveShell>,
    feed: Vec<String>,
    respawn_counter: u32,
    /// Replaces the C# `event System.Action OnPlayerFired`.
    ///
    /// The only subscriber was `WarArenaBootstrap.HandlePlayerFired`, which takes no
    /// argument and just triggers a muzzle flash. A counter detects exactly the same
    /// edge and crosses the FFI boundary without needing a managed callback, which
    /// IL2CPP cannot invoke from native code without `AOT.MonoPInvokeCallback`.
    player_fired_count: u32,
}

impl BattleSim {
    // Mirrored as associated constants because the C# original exposed them as
    // `public const` on the class, and call sites -- tests, tuning, the FFI --
    // read them as properties of the sim rather than as free-floating values.
    pub const TICK_HZ: f32 = TICK_HZ;
    pub const FIXED_DT: f32 = FIXED_DT;
    pub const ARENA_RADIUS: f32 = ARENA_RADIUS;
    pub const ARENA_HALF_EXTENT: f32 = ARENA_HALF_EXTENT;
    pub const TURRET_SLEW_RATE: f32 = TURRET_SLEW_RATE;
    pub const MAX_TRAVERSE: f32 = MAX_TRAVERSE;
    pub const MIN_GUN_PITCH_RAD: f32 = MIN_GUN_PITCH_RAD;
    pub const MAX_GUN_PITCH_RAD: f32 = MAX_GUN_PITCH_RAD;
    pub const GUN_ELEVATION_SLEW_RATE: f32 = GUN_ELEVATION_SLEW_RATE;

    /// Build an engagement between two vehicles from the catalogue.
    ///
    /// # Panics
    /// Panics if either id is not in the catalogue, matching the C# constructor
    /// throwing a `KeyNotFoundException`.
    pub fn new(player_vehicle_id: &str, enemy_vehicle_id: &str) -> Self {
        let player = VehicleCatalog::get(player_vehicle_id)
            .unwrap_or_else(|| panic!("unknown vehicle id: {player_vehicle_id}"));
        let enemy = VehicleCatalog::get(enemy_vehicle_id)
            .unwrap_or_else(|| panic!("unknown vehicle id: {enemy_vehicle_id}"));

        let player_position = Vec3::new(0.0, 0.0, -35.0);
        let enemy_position = Vec3::new(0.0, 0.0, 35.0);

        let player_mass = f32::max(player.mass_kg, 1000.0);
        // Footprint measured from the RENDERED vehicle, at the same scale the
        // bootstrap applies to the rig. Using the generic track constants here instead
        // put the contacts at +/-1.7 m on a 9 m tank, so the rear third of the tracks
        // drove on no physics at all.
        let mut player_suspension = crate::hull_suspension::HullSuspension::new(
            player_mass,
            vehicle_dynamics::contact_half_length_m(player_mass),
            vehicle_dynamics::contact_half_width_m(player_mass),
            vehicle_dynamics::contact_centre_z_m(player_mass),
        );
        player_suspension.reset(player_position.x, player_position.z, 0.0);

        BattleSim {
            player_def: player,
            enemy_def: enemy,

            player_position,
            player_heading_rad: 0.0,
            player_turret_yaw_rad: 0.0,
            player_gun_pitch_rad: 0.0,
            player_speed_kph: 0.0,

            enemy_position,
            enemy_heading_rad: mathf::PI,
            enemy_speed_mps: 0.0,
            enemy_lat_speed_mps: 0.0,
            enemy_turret_yaw_rad: 0.0,

            player_health: 100,
            enemy_health: 100,
            kills: 0,
            deaths: 0,

            reload_remaining: 0.0,

            // Seed the interpolation history so the very first frame does not lerp
            // from the origin and teleport the tank across the map.
            player_position_prev: player_position,
            enemy_position_prev: enemy_position,
            player_heading_prev_rad: 0.0,
            player_gun_pitch_prev_rad: 0.0,
            interpolation_alpha: 1.0,

            accumulator: 0.0,
            enemy_reload: 0.0,
            respawn_timer: 0.0,
            tick: 0,
            player_current_speed_mps: 0.0,
            player_lat_speed_mps: 0.0,
            shaped_throttle: 0.0,
            shaped_steer: 0.0,
            player_left_track_mps: 0.0,
            player_right_track_mps: 0.0,
            enemy_left_track_mps: 0.0,
            enemy_right_track_mps: 0.0,
            hull_pitch_deg: 0.0,
            hull_roll_deg: 0.0,
            slope_forward_deg: 0.0,
            slope_lateral_deg: 0.0,
            player_suspension,
            player_accel_long: 0.0,
            obstacles: crate::battlefield::build(),
            did_collide_with_obstacle: false,
            player_accel_lat: 0.0,
            in_flight: Vec::new(),
            feed: Vec::with_capacity(FEED_CAPACITY),
            respawn_counter: 0,
            player_fired_count: 0,
        }
    }

    pub fn player_spec(&self) -> &'static VehicleDefinition {
        self.player_def
    }

    pub fn enemy_spec(&self) -> &'static VehicleDefinition {
        self.enemy_def
    }

    pub fn player_position(&self) -> Vec3 {
        self.player_position
    }

    pub fn enemy_position(&self) -> Vec3 {
        self.enemy_position
    }

    pub fn player_heading_rad(&self) -> f32 {
        self.player_heading_rad
    }

    pub fn player_turret_yaw_rad(&self) -> f32 {
        self.player_turret_yaw_rad
    }

    pub fn player_gun_pitch_rad(&self) -> f32 {
        self.player_gun_pitch_rad
    }

    /// True when the player touched an obstacle on the most recent step.
    /// Mirrors BattleSim.DidCollideWithObstacle.
    pub fn did_collide_with_obstacle(&self) -> bool {
        self.did_collide_with_obstacle
    }

    pub fn player_speed_kph(&self) -> f32 {
        self.player_speed_kph
    }

    /// Hull pitch, degrees, nose-up positive. Emergent from the four-contact
    /// suspension rather than copied from the terrain slope.
    pub fn hull_pitch_deg(&self) -> f32 {
        self.hull_pitch_deg
    }

    /// Hull roll, degrees, right-side-up positive.
    pub fn hull_roll_deg(&self) -> f32 {
        self.hull_roll_deg
    }

    /// Ground speed of the left track, m/s, positive forwards. The two tracks
    /// diverge under steering, which is the whole mechanism by which a tracked
    /// vehicle turns.
    pub fn player_left_track_mps(&self) -> f32 {
        self.player_left_track_mps
    }
    /// Ground speed of the right track, m/s, positive forwards.
    pub fn player_right_track_mps(&self) -> f32 {
        self.player_right_track_mps
    }
    /// Terrain slope under the hull along its heading, degrees. Positive is uphill.
    pub fn slope_forward_deg(&self) -> f32 {
        self.slope_forward_deg
    }
    /// Terrain slope across the hull, degrees. Positive means the ground falls away
    /// to the right.
    pub fn slope_lateral_deg(&self) -> f32 {
        self.slope_lateral_deg
    }
    /// Longitudinal speed in the hull frame, m/s. Negative when reversing.
    pub fn player_forward_speed_mps(&self) -> f32 {
        self.player_current_speed_mps
    }

    pub fn enemy_heading_rad(&self) -> f32 {
        self.enemy_heading_rad
    }

    pub fn enemy_turret_yaw_rad(&self) -> f32 {
        self.enemy_turret_yaw_rad
    }

    pub fn player_health(&self) -> i32 {
        self.player_health
    }

    pub fn enemy_health(&self) -> i32 {
        self.enemy_health
    }

    pub fn kills(&self) -> i32 {
        self.kills
    }

    pub fn deaths(&self) -> i32 {
        self.deaths
    }

    pub fn reload_remaining(&self) -> f32 {
        self.reload_remaining
    }

    pub fn reload_complete(&self) -> bool {
        self.reload_remaining <= 0.0
    }

    /// Newest-first event feed.
    pub fn feed(&self) -> &[String] {
        &self.feed
    }

    /// Increments each time the player's gun fires. See the field docs for why this
    /// is a counter and not an event.
    pub fn player_fired_count(&self) -> u32 {
        self.player_fired_count
    }

    pub fn interpolation_alpha(&self) -> f32 {
        self.interpolation_alpha
    }

    pub fn player_position_prev(&self) -> Vec3 {
        self.player_position_prev
    }

    pub fn enemy_position_prev(&self) -> Vec3 {
        self.enemy_position_prev
    }

    pub fn player_heading_prev_rad(&self) -> f32 {
        self.player_heading_prev_rad
    }

    pub fn player_gun_pitch_prev_rad(&self) -> f32 {
        self.player_gun_pitch_prev_rad
    }

    /// Interpolated player position for drawing this frame.
    pub fn player_render_position(&self) -> Vec3 {
        Vec3::lerp(
            self.player_position_prev,
            self.player_position,
            self.interpolation_alpha,
        )
    }

    /// Interpolated enemy position for drawing this frame.
    pub fn enemy_render_position(&self) -> Vec3 {
        Vec3::lerp(
            self.enemy_position_prev,
            self.enemy_position,
            self.interpolation_alpha,
        )
    }

    /// Interpolated player hull heading for drawing this frame.
    ///
    /// Angle-aware, because heading is wrapped to plus or minus pi and a plain lerp
    /// across the wrap would spin the tank the long way round.
    pub fn player_render_heading_rad(&self) -> f32 {
        let delta = mathf::delta_angle(
            self.player_heading_prev_rad * mathf::RAD2DEG,
            self.player_heading_rad * mathf::RAD2DEG,
        ) * mathf::DEG2RAD;
        self.player_heading_prev_rad + delta * self.interpolation_alpha
    }

    /// Interpolated player gun pitch for drawing this frame.
    pub fn player_render_gun_pitch_rad(&self) -> f32 {
        mathf::lerp(
            self.player_gun_pitch_prev_rad,
            self.player_gun_pitch_rad,
            self.interpolation_alpha,
        )
    }

    /// Shells currently in flight, for the renderer to draw tracers.
    ///
    /// **Bug fix.** The C# `InFlightPositions` getter cleared and refilled a private
    /// `List` on *every access* and returned it by reference, so a caller's copy was
    /// invalidated by the next read and was mutable by the caller -- an aliasing bug
    /// in C# and a borrow-checker violation in Rust. Now an owned `Vec`.
    pub fn in_flight_positions(&self) -> Vec<Vec3> {
        self.in_flight.iter().map(|s| s.position).collect()
    }

    /// How many shells are currently in the air.
    pub fn in_flight_count(&self) -> usize {
        self.in_flight.len()
    }

    /// Advance the battle. `aim_point` is the world point the player is aiming at,
    /// normally the mouse's ground intersection.
    pub fn advance(
        &mut self,
        frame_delta: f32,
        throttle: f32,
        steer: f32,
        turret_trim: f32,
        fire: bool,
        aim_point: Vec3,
    ) {
        // Remember where the vehicles were at the start of this frame so the renderer
        // can interpolate between simulation ticks.
        self.player_position_prev = self.player_position;
        self.enemy_position_prev = self.enemy_position;
        self.player_heading_prev_rad = self.player_heading_rad;
        self.player_gun_pitch_prev_rad = self.player_gun_pitch_rad;

        // Rolling accumulator, held across calls, so the simulation rate is constant
        // regardless of render frame rate.
        self.accumulator += mathf::min(frame_delta, 0.1);
        let mut steps = 0;
        while self.accumulator >= FIXED_DT && steps < MAX_STEPS_PER_FRAME {
            self.accumulator -= FIXED_DT;
            steps += 1;
            self.step(FIXED_DT, throttle, steer, turret_trim, fire, aim_point);
        }
        if self.accumulator > FIXED_DT * MAX_STEPS_PER_FRAME as f32 {
            self.accumulator = 0.0;
        }

        self.interpolation_alpha = if steps == 0 {
            1.0
        } else {
            mathf::clamp01(self.accumulator / FIXED_DT)
        };
    }

    fn step(
        &mut self,
        dt: f32,
        throttle: f32,
        steer: f32,
        turret_trim: f32,
        fire: bool,
        aim_point: Vec3,
    ) {
        self.tick += 1;
        if self.reload_remaining > 0.0 {
            self.reload_remaining -= dt;
        }
        if self.enemy_reload > 0.0 {
            self.enemy_reload -= dt;
        }
        if self.respawn_timer > 0.0 {
            self.respawn_timer -= dt;
            if self.respawn_timer <= 0.0 {
                if self.player_health <= 0 {
                    self.player_health = 100;
                }
                if self.enemy_health <= 0 {
                    self.enemy_health = 100;
                }

                // Zero the filtered driver axes on respawn. While the player is out,
                // the shaping block below is skipped entirely, so whatever throttle was
                // last commanded is still held in `shaped_throttle` when control
                // returns. With the throttle key no longer pressed the ramp decays it
                // to zero over a third of a second, so the vehicle would lurch forward
                // on respawn for no reason the player could see or stop.
                self.shaped_throttle = 0.0;
                self.shaped_steer = 0.0;
            }
        }

        if self.player_health > 0 {
            // Shape the driver axes before anything consumes them, so the filtered
            // command is what the movement model sees.
            self.shaped_throttle = input_shaping::ramp(
                self.shaped_throttle,
                throttle,
                dt,
                input_shaping::DEFAULT_THROTTLE_ATTACK_PER_SEC,
                input_shaping::DEFAULT_THROTTLE_RELEASE_PER_SEC,
            );

            // The steer time constant widens when the driver is reversing across
            // neutral, so the hull carries its old rotation less willingly into the
            // new direction.
            let steer_tau = input_shaping::steer_blend(
                self.shaped_steer,
                steer,
                input_shaping::DEFAULT_STEER_LERP_SECONDS,
                input_shaping::DEFAULT_DIRECTION_CHANGE_SECONDS,
            );
            self.shaped_steer =
                input_shaping::lerp_axis(self.shaped_steer, steer, dt, steer_tau);

            let throttle_in = self.shaped_throttle;
            let steer_in = self.shaped_steer;

            self.drive_player(dt, throttle_in, steer_in);
            self.aim_turret(dt, turret_trim, aim_point);

            if fire && self.reload_complete() {
                self.fire_player();
            }
        }

        if self.enemy_health > 0 {
            self.drive_enemy(dt);
        }

        self.advance_shells(dt);
    }

    fn drive_player(&mut self, dt: f32, throttle: f32, steer: f32) {
        let throttle = mathf::clamp(throttle, -1.0, 1.0);
        let steer = mathf::clamp(steer, -1.0, 1.0);

        let fx = mathf::sin(self.player_heading_rad);
        let fz = mathf::cos(self.player_heading_rad);
        let rx = fz;
        let rz = -fx;

        // --- terrain frame -------------------------------------------------
        let slope_fwd_deg = vehicle_dynamics::slope_along_terrain(
            self.player_position.x,
            self.player_position.z,
            (fx, fz),
            vehicle_dynamics::HALF_GAUGE_M,
        );
        let slope_lat_deg = vehicle_dynamics::slope_along_terrain(
            self.player_position.x,
            self.player_position.z,
            (rx, rz),
            vehicle_dynamics::HALF_TRACK_WIDTH_M,
        );

        let mut normal_load_g = 1.0f32;
        let (g_along, g_across) =
            vehicle_dynamics::gravity_on_ground_plane(slope_fwd_deg, slope_lat_deg, &mut normal_load_g);
        let _ = g_across;

        // --- climb capability ---------------------------------------------
        // Progressive reduction of engine effort on steep ground. This shapes the
        // command only; it is applied to the traction demand below and never to the
        // brake or coast checks, because a vehicle that cannot get up a hill can
        // certainly still stop on one.
        //
        // Deliberately not applied to the steering axis: a tracked vehicle loses
        // climbing ability on a slope, not the ability to turn on it.
        let drive_throttle = throttle * input_shaping::climb_capability(slope_fwd_deg);

        // --- longitudinal forces ------------------------------------------
        let a_engine = vehicle_dynamics::engine_acceleration(
            drive_throttle,
            self.player_current_speed_mps,
            ACCELERATION_MPS2,
            POWER_LIMITED_SPEED_MPS,
            MAX_SPEED_MPS,
        );
        let a_roll = vehicle_dynamics::rolling_resistance(
            self.player_current_speed_mps,
            normal_load_g,
            ROLLING_RESISTANCE_COEFF,
        );

        let mut desired_long = a_engine + g_along + a_roll;

        // Braking: the tracks are the brakes.
        if throttle * self.player_current_speed_mps < -0.02 {
            desired_long = -mathf::sign(self.player_current_speed_mps) * BRAKE_ACCEL_MPS2;
        }

        // --- lateral: tracks resist sideways motion strongly -------------
        let a_lat_grip = vehicle_dynamics::lateral_grip_accel(
            self.player_lat_speed_mps,
            normal_load_g,
            LATERAL_FRICTION_COEFF,
            vehicle_dynamics::LATERAL_SLIP_STIFFNESS_PER_MPS,
        );

        // Friction circle: whatever the tracks spend sideways is unavailable for
        // acceleration.
        let a_long =
            vehicle_dynamics::apply_friction_circle(desired_long, a_lat_grip, TRACKED_GRIP_ACCEL_MPS2);

        self.player_current_speed_mps += a_long * dt;
        self.player_lat_speed_mps += a_lat_grip * dt;

        // Scrub loss while skidding to turn.
        self.player_current_speed_mps +=
            vehicle_dynamics::skid_scrub_speed_loss(steer, self.player_current_speed_mps) * dt;

        if mathf::abs(throttle) < 0.01 {
            self.player_current_speed_mps =
                mathf::move_towards(self.player_current_speed_mps, 0.0, COAST_DECEL_MPS2 * dt);
        }

        if mathf::abs(self.player_current_speed_mps) < 1e-3 {
            self.player_current_speed_mps = 0.0;
        }
        if mathf::abs(self.player_lat_speed_mps) < 1e-3 {
            self.player_lat_speed_mps = 0.0;
        }

        // --- steering ------------------------------------------------------
        // `to_gun_line` is an ANGLE in radians, so it is scaled into the same
        // normalised [-1,1] domain as the steering input. Adding raw radians to a
        // normalised demand meant the "0.35 strength" was really 73% of full lock at
        // full traverse, and any hard steering input suppressed alignment entirely.
        let moving = if mathf::abs(throttle) > 0.01 { 1.0 } else { 0.0 };
        let gun_world_heading = wrap_angle(self.player_heading_rad + self.player_turret_yaw_rad);
        let to_gun_line = wrap_angle(gun_world_heading - self.player_heading_rad);

        let align =
            mathf::clamp(to_gun_line / MAX_TRAVERSE, -1.0, 1.0) * HULL_AUTO_ALIGN_STRENGTH * moving;
        let demand = mathf::clamp(align + steer, -1.0, 1.0);

        let mut yaw_rate = vehicle_dynamics::skid_steer_yaw_rate(
            self.player_current_speed_mps,
            demand,
            vehicle_dynamics::TRACK_GAUGE_M,
            MAX_TURN_RATE_RAD,
            SPEED_AT_MAX_YAW_MPS,
        );
        yaw_rate *= vehicle_dynamics::lateral_slope_steering_scale(slope_lat_deg);

        self.player_heading_rad = wrap_angle(self.player_heading_rad + yaw_rate * dt);

        // The velocity components live in the HULL frame and the hull has just
        // rotated, so carry them round with it.
        let (vs, vc) = (mathf::sin(yaw_rate * dt), mathf::cos(yaw_rate * dt));
        let v_long_new = self.player_current_speed_mps * vc + self.player_lat_speed_mps * vs;
        let v_lat_new = -self.player_current_speed_mps * vs + self.player_lat_speed_mps * vc;
        self.player_current_speed_mps = v_long_new;
        self.player_lat_speed_mps = v_lat_new;

        // --- integrate position --------------------------------------------
        let fx = mathf::sin(self.player_heading_rad);
        let fz = mathf::cos(self.player_heading_rad);
        let rx = fz;
        let rz = -fx;

        let vx = fx * self.player_current_speed_mps + rx * self.player_lat_speed_mps;
        let vz = fz * self.player_current_speed_mps + rz * self.player_lat_speed_mps;

        let from_x = self.player_position.x;
        let from_z = self.player_position.z;
        let (clamped_x, clamped_z) = clamp_to_arena(from_x + vx * dt, from_z + vz * dt);
        let mut resolved = (clamped_x, clamped_z);

        // Solid obstacles. Terrain height and obstacle collision are different
        // problems: the suspension keeps the hull at the right height while the vehicle
        // still drives straight through a tree trunk.
        self.did_collide_with_obstacle = crate::battlefield::resolve(
            &self.obstacles,
            &mut resolved,
            crate::battlefield::VEHICLE_COLLISION_RADIUS_M,
        );

        let (clamped_x, clamped_z) = resolved;
        let pos_y = get_terrain_height(clamped_x, clamped_z);

        self.player_position = Vec3::new(clamped_x, pos_y, clamped_z);

        // Weight transfer: the acceleration the hull is actually experiencing in its
        // own frame. Fed to the suspension, which turns it into pitch and roll
        // moments because the centre of gravity sits above the track plane.
        self.player_accel_long = a_long;
        self.player_accel_lat = a_lat_grip;

        self.step_suspension(dt);

        // Cancel velocity driven into the boundary so the vehicle slides along the
        // edge instead of pressing into it and snapping.
        let inv_dt = 1.0 / f32::max(dt, 1e-5);
        let ax = clamped_x - from_x;
        let az = clamped_z - from_z;
        self.player_current_speed_mps = (ax * fx + az * fz) * inv_dt;
        self.player_lat_speed_mps = (ax * rx + az * rz) * inv_dt;

        // Ground speed, not |longitudinal|.
        self.player_speed_kph = (ax * ax + az * az).sqrt() * inv_dt * 3.6;

        // Differential steering, exposed for the track animation.
        // The RIGHT track is the outside of a right-hand turn, so it runs faster.
        // Positive yaw rate turns toward +X, which is the local right axis.
        let half = self.player_current_speed_mps * DIFFERENTIAL_SKEW;
        self.player_right_track_mps = self.player_current_speed_mps + half;
        self.player_left_track_mps = self.player_current_speed_mps - half;

        self.slope_forward_deg = slope_fwd_deg;
        self.slope_lateral_deg = slope_lat_deg;
    }

    /// Advance the four-contact suspension and take the hull's attitude and ride
    /// height from it.
    ///
    /// This is what replaces the old `hull_pitch_deg = slope_fwd_deg`. Copying the
    /// terrain slope into the hull attitude makes the tank rigid: it cannot absorb a
    /// camber, it cannot squat under power or dive under braking, and it heaves over
    /// every undulation. Worse, it disagrees with the renderer's own corner solve,
    /// so the hull visibly rolled away from the ground it was standing on.
    fn step_suspension(&mut self, dt: f32) {
        let (px, pz) = (self.player_position.x, self.player_position.z);

        self.player_suspension.step(
            dt,
            px,
            pz,
            self.player_heading_rad,
            self.player_accel_long,
            self.player_accel_lat,
        );

        // The hull's Y now comes from its own ride height rather than from the terrain
        // height under the centre. Those differ whenever the hull is tilted or
        // bridging a dip, and taking the centre value alone was one of the reasons it
        // looked like it was skating over the ground.
        self.player_position.y = self.player_suspension.height;

        self.hull_pitch_deg = self.player_suspension.pitch_rad * 180.0 / mathf::PI;
        self.hull_roll_deg = self.player_suspension.roll_rad * 180.0 / mathf::PI;
        self.slope_forward_deg = mathf::asin(mathf::clamp(mathf::sin(self.player_suspension.pitch_rad), -1.0, 1.0)) * 180.0 / mathf::PI;
        self.slope_lateral_deg = mathf::asin(mathf::clamp(mathf::sin(self.player_suspension.roll_rad), -1.0, 1.0)) * 180.0 / mathf::PI;
    }
    /// Turret follows the aim point, clamped to a realistic traverse arc and slewed
    /// at a finite rate.
    fn aim_turret(&mut self, dt: f32, trim: f32, aim_point: Vec3) {
        // Traverse about the turret ring, not the hull centre. A real turret rotates
        // around the ring it is mounted on, and aiming from the hull centre makes the
        // barrel appear to lag the cursor by the length of the hull whenever the tank
        // is sideways to the target.
        let turret_pivot = self.player_position + turret_pivot_offset(self.player_heading_rad);
        let mut delta = aim_point - turret_pivot;

        let horizontal_dist = mathf::sqrt(delta.x * delta.x + delta.z * delta.z);
        if horizontal_dist > 0.1 {
            let dy = delta.y - AIM_EYE_HEIGHT_M;
            let mut wanted_pitch = mathf::atan2(dy, horizontal_dist);
            wanted_pitch = mathf::clamp(wanted_pitch, MIN_GUN_PITCH_RAD, MAX_GUN_PITCH_RAD);
            self.player_gun_pitch_rad = slew_bounded(
                self.player_gun_pitch_rad,
                wanted_pitch,
                GUN_ELEVATION_SLEW_RATE * dt,
            );
            self.player_gun_pitch_rad = mathf::clamp(
                self.player_gun_pitch_rad,
                MIN_GUN_PITCH_RAD,
                MAX_GUN_PITCH_RAD,
            );
        }

        delta.y = 0.0;
        if delta.sqr_magnitude() < 0.01 {
            return;
        }

        let mut wanted = wrap_angle(mathf::atan2(delta.x, delta.z) - self.player_heading_rad);
        wanted += trim;

        // Clamp AFTER the trim is added. Clamping before it meant the trim could push
        // the requested angle straight back out through the traverse limit, which is
        // how the turret ended up past MaxTraverse and swinging the wrong way before
        // it caught itself.
        wanted = mathf::clamp(wanted, -MAX_TRAVERSE, MAX_TRAVERSE);

        self.player_turret_yaw_rad =
            slew_bounded(self.player_turret_yaw_rad, wanted, TURRET_SLEW_RATE * dt);

        // Belt and braces: the state is clamped too, so no combination of inputs can
        // leave the barrel outside its arc. An out-of-arc turret looks exactly like an
        // inverted one to the player, because the gun points at the opposite side of
        // the tank from the target.
        self.player_turret_yaw_rad =
            mathf::clamp(self.player_turret_yaw_rad, -MAX_TRAVERSE, MAX_TRAVERSE);
    }

    fn drive_enemy(&mut self, dt: f32) {
        // Orbit behaviour: close the distance, then circle. Deterministic, driven
        // only by the tick counter, so replays are identical.
        let phase = self.tick as f32 * AI_WEAVE_PHASE;
        let to_player = self.player_position - self.enemy_position;

        // Horizontal distance only. Using the 3D magnitude made a target on a hill
        // read as kilometres away, so the AI would refuse to fire at, or close on,
        // anything not on the same contour.
        let to_player_x = to_player.x;
        let to_player_z = to_player.z;
        let dist = (to_player_x * to_player_x + to_player_z * to_player_z).sqrt();

        let throttle = if dist < AI_CLOSE_RANGE_M {
            AI_THROTTLE_CLOSE
        } else {
            AI_THROTTLE
        };

        let desired_heading = if dist > 0.1 {
            mathf::atan2(to_player_x, to_player_z) + mathf::sin(phase) * AI_WEAVE_RAD
        } else {
            self.enemy_heading_rad
        };

        self.enemy_heading_rad = slew(
            self.enemy_heading_rad,
            wrap_angle(desired_heading - self.enemy_heading_rad) + self.enemy_heading_rad,
            1.2 * dt,
        );

        // The AI drives through the same terrain-aware force model as the player.
        // It previously moved at a constant `throttle * MAX_SPEED_MPS * 0.8` with no
        // inertia, no braking and no slope response, so it climbed hills at full
        // speed and could not be out-driven.
        let fx = mathf::sin(self.enemy_heading_rad);
        let fz = mathf::cos(self.enemy_heading_rad);
        let rx = fz;
        let rz = -fx;

        let slope_fwd_deg = vehicle_dynamics::slope_along_terrain(
            self.enemy_position.x,
            self.enemy_position.z,
            (fx, fz),
            vehicle_dynamics::HALF_GAUGE_M,
        );
        let slope_lat_deg = vehicle_dynamics::slope_along_terrain(
            self.enemy_position.x,
            self.enemy_position.z,
            (rx, rz),
            vehicle_dynamics::HALF_TRACK_WIDTH_M,
        );

        let mut normal_load_g = 1.0f32;
        let (g_along, _g_across) =
            vehicle_dynamics::gravity_on_ground_plane(slope_fwd_deg, slope_lat_deg, &mut normal_load_g);

        let a_engine = vehicle_dynamics::engine_acceleration(
            throttle,
            self.enemy_speed_mps,
            ACCELERATION_MPS2 * 0.8,
            POWER_LIMITED_SPEED_MPS,
            MAX_SPEED_MPS,
        );
        let a_roll = vehicle_dynamics::rolling_resistance(
            self.enemy_speed_mps,
            normal_load_g,
            ROLLING_RESISTANCE_COEFF,
        );
        let a_lat_grip = vehicle_dynamics::lateral_grip_accel(
            self.enemy_lat_speed_mps,
            normal_load_g,
            LATERAL_FRICTION_COEFF,
            vehicle_dynamics::LATERAL_SLIP_STIFFNESS_PER_MPS,
        );
        let a_long =
            vehicle_dynamics::apply_friction_circle(a_engine + g_along + a_roll, a_lat_grip, TRACKED_GRIP_ACCEL_MPS2);

        self.enemy_speed_mps += a_long * dt;
        self.enemy_lat_speed_mps += a_lat_grip * dt;
        if mathf::abs(self.enemy_speed_mps) < 1e-3 {
            self.enemy_speed_mps = 0.0;
        }
        if mathf::abs(self.enemy_lat_speed_mps) < 1e-3 {
            self.enemy_lat_speed_mps = 0.0;
        }

        let vx = fx * self.enemy_speed_mps + rx * self.enemy_lat_speed_mps;
        let vz = fz * self.enemy_speed_mps + rz * self.enemy_lat_speed_mps;

        let from_x = self.enemy_position.x;
        let from_z = self.enemy_position.z;
        let (clamped_x, clamped_z) = clamp_to_arena(from_x + vx * dt, from_z + vz * dt);
        let mut resolved = (clamped_x, clamped_z);
        crate::battlefield::resolve(
            &self.obstacles,
            &mut resolved,
            crate::battlefield::VEHICLE_COLLISION_RADIUS_M,
        );
        let (clamped_x, clamped_z) = resolved;
        let enemy_y = get_terrain_height(clamped_x, clamped_z);
        self.enemy_position = Vec3::new(clamped_x, enemy_y, clamped_z);

        let inv_dt = 1.0 / f32::max(dt, 1e-5);
        let ax = clamped_x - from_x;
        let az = clamped_z - from_z;
        self.enemy_speed_mps = (ax * fx + az * fz) * inv_dt;
        self.enemy_lat_speed_mps = (ax * rx + az * rz) * inv_dt;

        let e_half = self.enemy_speed_mps * DIFFERENTIAL_SKEW * 0.5;
        self.enemy_right_track_mps = self.enemy_speed_mps + e_half;
        self.enemy_left_track_mps = self.enemy_speed_mps - e_half;

        // The AI turret slews at a finite rate like the player's, rather than
        // snapping, so leading a target on a turn is a real skill.
        let wanted_yaw =
            wrap_angle(mathf::atan2(-to_player_x, -to_player_z) - self.enemy_heading_rad);
        let wanted_yaw = mathf::clamp(wanted_yaw, -MAX_TRAVERSE, MAX_TRAVERSE);
        self.enemy_turret_yaw_rad = slew_bounded(
            self.enemy_turret_yaw_rad,
            wanted_yaw,
            TURRET_SLEW_RATE * 0.7 * dt,
        );
        self.enemy_turret_yaw_rad = mathf::clamp(self.enemy_turret_yaw_rad, -MAX_TRAVERSE, MAX_TRAVERSE);

        if self.enemy_reload <= 0.0 && dist < ENEMY_FIRE_RANGE_M {
            self.enemy_reload = self.enemy_def.reload_seconds + ENEMY_RELOAD_HANDICAP_S;
            let origin = self.enemy_position + Vec3::new(0.0, ENEMY_MUZZLE_HEIGHT_M, 0.0);
            let target = self.player_position + Vec3::new(0.0, ENEMY_MUZZLE_HEIGHT_M, 0.0);
            let direction = (target - origin).normalize();
            self.spawn_shell(
                origin,
                direction,
                false,
                VehicleCatalog::build_shell(self.enemy_def),
            );
        }
    }
    fn fire_player(&mut self) {
        self.reload_remaining = self.player_def.reload_seconds;

        let origin = self.player_position + Vec3::new(0.0, PLAYER_MUZZLE_HEIGHT_M, 0.0);
        let absolute_yaw = self.player_heading_rad + self.player_turret_yaw_rad;
        let cos_pitch = mathf::cos(self.player_gun_pitch_rad);
        let sin_pitch = mathf::sin(self.player_gun_pitch_rad);
        let direction = Vec3::new(
            mathf::sin(absolute_yaw) * cos_pitch,
            sin_pitch,
            mathf::cos(absolute_yaw) * cos_pitch,
        );

        // Cannon recoil impulse pushes the vehicle slightly backwards.
        //
        // Applied as a CAP rather than a subtraction. A 120 mm gun firing on a
        // stationary tank used to drive it backwards at 1.2 m/s, where it then rolled
        // away on its own forever; a tank's tracks and the ground hold it.
        self.player_current_speed_mps = f32::min(self.player_current_speed_mps, -RECOIL_IMPULSE_MPS);

        self.spawn_shell(
            origin,
            direction,
            true,
            VehicleCatalog::build_shell(self.player_def),
        );
        self.player_fired_count += 1;
    }

    fn spawn_shell(&mut self, origin: Vec3, direction: Vec3, from_player: bool, ordnance: Shell) {
        self.in_flight.push(ActiveShell {
            position: origin,
            velocity: direction.normalize() * (ordnance.muzzle_velocity * SHELL_VELOCITY_SCALE),
            life: SHELL_LIFE_S,
            from_player,
            ordnance,
        });
    }

    fn advance_shells(&mut self, dt: f32) {
        let mut index = self.in_flight.len() as isize - 1;
        while index >= 0 {
            let i = index as usize;
            let mut shell = self.in_flight[i];
            shell.life -= dt;
            let previous = shell.position;
            shell.position += shell.velocity * dt;

            let target = if shell.from_player {
                self.enemy_position
            } else {
                self.player_position
            };
            let target_alive = if shell.from_player {
                self.enemy_health > 0
            } else {
                self.player_health > 0
            };

            // Hit volume is the hull footprint plus the turret, measured RELATIVE to
            // the target's own ground contact. Comparing against an absolute world
            // height made every shot on the hills (0..74 m) a guaranteed miss.
            //
            // The test is SWEPT along the segment travelled this tick, not sampled at
            // the endpoint. A round leaves the gun at ~0.42 * 800 m/s = 336 m/s, which
            // at the 30 Hz tick rate is 11.2 m of travel per tick, against a hit
            // cylinder only 6.4 m across. Sampling the endpoint alone therefore missed
            // roughly 43% of shots that flew straight through the target.
            let hit = target_alive && segment_hits_target(previous, shell.position, target);

            if hit {
                let mut at_impact = shell;
                at_impact.position = shell.position;
                self.resolve_hit(&at_impact);
                self.in_flight.remove(i);
                index -= 1;
                continue;
            }

            let r_sq = shell.position.x * shell.position.x + shell.position.z * shell.position.z;
            let max_shell_r = ARENA_RADIUS + SHELL_CULL_MARGIN_M;
            if shell.life <= 0.0 || r_sq > max_shell_r * max_shell_r {
                self.in_flight.remove(i);
                index -= 1;
                continue;
            }

            self.in_flight[i] = shell;
            index -= 1;
        }
    }

    /// Resolve a shell hit through the real armour model, so the outcome depends on
    /// calibre, velocity, plate slope and impact angle rather than a flat damage
    /// number.
    fn resolve_hit(&mut self, shell: &ActiveShell) {
        let target_def = if shell.from_player {
            self.enemy_def
        } else {
            self.player_def
        };
        let target_pos = if shell.from_player {
            self.enemy_position
        } else {
            self.player_position
        };
        let target_heading = if shell.from_player {
            self.enemy_heading_rad
        } else {
            self.player_heading_rad
        };

        let incoming = shell.velocity.normalize();
        let from_shooter = -incoming * (target_pos - shell.position).magnitude();

        // Plate selection and impact angle both come from `struck_plate`, which is the
        // same helper the aiming reticle uses. See its documentation for the full
        // account of what the C# got wrong here: the classification expression was a
        // typo that ignored the target's heading, and even corrected it could not
        // distinguish head-on from dead astern, so rear shots were classified as front
        // hits, measured at 180 degrees, and therefore always ricocheted -- making
        // rear armour impenetrable. The glacis slope was also read but never applied.
        let struck = struck_plate(target_def, target_heading, from_shooter);
        let plate_impact_deg = struck.impact_angle_deg;

        let travelled = Vec3::new(
            shell.position.x - target_pos.x,
            0.0,
            shell.position.z - target_pos.z,
        )
        .magnitude();
        let pen_at_impact = shell.ordnance.penetration_at(travelled);
        let effective = struck
            .plate
            .effective_thickness_vs(&shell.ordnance, plate_impact_deg);

        // Ricochet is a front-plate phenomenon in this model. The C# tested the
        // hull-facing angle for *every* plate, which is why nothing could be hit
        // from the side or the rear.
        if struck.ricochets(&shell.ordnance.shell_type) {
            self.push(format!(
                "RICOCHET @ {plate_impact_deg:.0}deg - angle too steep for {:?} \
                 (limit {:.0}deg)",
                shell.ordnance.shell_type,
                shell.ordnance.critical_ricochet_deg()
            ));
            return;
        }

        let plate_name = struck.plate.name;

        let penetrates = shell.ordnance.shell_type == ShellType::Heat || pen_at_impact >= effective;

        if !penetrates {
            self.push(format!(
                "BOUNCE - {pen_at_impact:.0}mm vs {effective:.0}mm effective ({plate_name})"
            ));
            return;
        }

        if shell.from_player {
            self.enemy_health = (self.enemy_health - PLAYER_SHELL_DAMAGE).max(0);
            self.push(format!(
                "PENETRATION - {pen_at_impact:.0}mm vs {effective:.0}mm @ \
                 {plate_impact_deg:.0}deg"
            ));
            if self.enemy_health <= 0 {
                self.kills += 1;
                self.respawn_timer = RESPAWN_DELAY_S;
                self.push(format!("DESTROYED - {}", target_def.display_name));
                let rx = self.randomize(45.0);
                let rz = self.randomize(45.0);
                self.enemy_position = Vec3::new(rx, get_terrain_height(rx, rz), rz);
            }
        } else {
            self.player_health = (self.player_health - ENEMY_SHELL_DAMAGE).max(0);
            self.push(format!(
                "HIT TAKEN - penetrated {plate_name} @ {plate_impact_deg:.0}deg"
            ));
            if self.player_health <= 0 {
                self.deaths += 1;
                self.respawn_timer = RESPAWN_DELAY_S;
                self.push("KNOCKED OUT - respawning".to_string());
            }
        }
    }

    /// Offsets a respawn deterministically.
    ///
    /// **Bug fix: no longer `static`.** The C# counter was a `private static int`,
    /// i.e. process-global mutable state shared by every `BattleSim` in existence.
    /// Two live engagements advanced the same counter, so the same engagement could
    /// respawn to different positions depending on what else was running -- which
    /// makes the simulation impossible to replay deterministically, and was in
    /// direct contradiction of the "safe to replay identically" claim made
    /// elsewhere throughout the same codebase. It is now per-instance.
    ///
    /// `clippy::excessive_precision` is suppressed deliberately: `12.9898` and
    /// `43758.5453` are transcribed verbatim from the C# hash, and any "cleanup" to
    /// the shortest round-tripping literal would change the generated sequence and
    /// silently invalidate the parity trace.
    #[allow(clippy::excessive_precision)]
    fn randomize(&mut self, limit: f32) -> f32 {
        let n = mathf::sin(self.respawn_counter as f32 * 12.9898) * 43758.5453;
        self.respawn_counter += 1;
        let n = n - mathf::floor(n);
        (n * 2.0 - 1.0) * limit
    }

    fn push(&mut self, message: String) {
        self.feed.insert(0, message);
        if self.feed.len() > FEED_CAPACITY {
            self.feed.pop();
        }
    }

    /// Whether the player's current aim would penetrate the enemy where it stands.
    /// Drives the on-screen reticle colour, so the player can see that a sloped front
    /// plate is refusing the shot before pulling the trigger.
    ///
    /// Resolves the plate through the same [`struck_plate`] helper as
    /// [`Self::resolve_hit`], so the reticle and the shot cannot disagree -- which is
    /// the whole point of the check.
    pub fn aim_would_penetrate(&self, aim_point: Vec3) -> bool {
        let mut delta = aim_point - self.enemy_position;
        delta.y = 0.0;
        if delta.sqr_magnitude() < HIT_RADIUS_M * HIT_RADIUS_M {
            return true;
        }

        let ordnance = VehicleCatalog::build_shell(self.player_def);
        let struck = struck_plate(self.enemy_def, self.enemy_heading_rad, delta);

        // Per-shell ricochet limit, matching what resolve_hit will actually do. Using
        // a different threshold here would show the player "green" on a shot that
        // then ricochets, which is worse than no feedback at all.
        if struck.ricochets(&ordnance.shell_type) {
            return false;
        }

        // Resolved once against the real plate, rather than re-deriving the
        // effective-thickness maths inline. Duplicating that calculation is exactly
        // the kind of duplication that drifts: an earlier version ignored the
        // cast-hull coefficient and the plate's own slope, so the reticle showed
        // green on shots that would then bounce.
        let report = Penetration::resolve(&ordnance, &struck.plate, struck.impact_angle_deg);
        report.result == ImpactResult::Penetration || report.result == ImpactResult::OverPenetration
    }
}

/// The plate a round meets, and the angle it meets it at.
struct StruckPlate {
    plate: ArmourPlate,
    /// Angle between the round and the normal of the plate it struck, which is
    /// *not* the same as the angle to the hull's facing.
    impact_angle_deg: f32,
}

impl StruckPlate {
    /// Whether a round arriving at this angle off this plate would ricochet.
    ///
    /// Front plates only. See [`struck_plate`] for why this is not simply a
    /// comparison on `impact_angle_deg`.
    fn ricochets(&self, ordnance: &ShellType) -> bool {
        self.plate.name == "front" && self.impact_angle_deg >= ordnance.critical_ricochet_deg()
    }
}

/// Classify which plate of `def` a round meets, given the vehicle's heading and the
/// horizontal offset from its origin *to* the shooter.
///
/// **This is the single source of truth for plate selection.** Both the shot
/// resolution and the aiming reticle go through it, because the alternative -- one
/// assuming the front plate and the other classifying -- is how the reticle ends up
/// contradicting the shot. It did exactly that: when the enemy turned its back, the
/// shot correctly struck the thin rear plate, while the reticle still assumed the
/// sloped front and reported "no penetration" on the one shot that was easiest.
///
/// Only the direction of `from_shooter` matters, not its length.
fn struck_plate(def: &VehicleDefinition, heading_rad: f32, from_shooter: Vec3) -> StruckPlate {
    let target_forward = Vec3::new(mathf::sin(heading_rad), 0.0, mathf::cos(heading_rad));

    let mut flat = from_shooter;
    flat.y = 0.0;
    let hull_impact_deg = mathf::acos(mathf::clamp(
        -flat.normalize().dot(target_forward),
        -1.0,
        1.0,
    )) * mathf::RAD2DEG;

    let (name, mm, slope_deg) = if hull_impact_deg < FRONT_PLATE_CONE_DEG {
        ("front", def.armor_front_mm, def.front_slope_deg)
    } else if hull_impact_deg > 180.0 - FRONT_PLATE_CONE_DEG {
        ("rear", def.armor_rear_mm, 0.0)
    } else {
        ("side", def.armor_side_mm, 0.0)
    };

    let impact_angle_deg = match name {
        // The glacis leans back, so a shot square to the hull meets it at the hull
        // angle *plus* the slope. This is what makes a Panther's 120 mm behave
        // like the 55 degree glacis it actually was.
        "front" => hull_impact_deg + slope_deg,
        // The side plate's normal is the hull's right axis, 90 degrees around.
        "side" => (hull_impact_deg - 90.0).abs(),
        // The rear plate faces backwards.
        _ => (180.0 - hull_impact_deg).abs(),
    };

    let plate = if def.is_cast_hull {
        ArmourPlate::cast(name, mm)
    } else {
        ArmourPlate::new(name, mm)
    };
    StruckPlate {
        plate,
        impact_angle_deg,
    }
}

/// Where the turret ring sits relative to the hull origin.
fn turret_pivot_offset(heading_rad: f32) -> Vec3 {
    Vec3::new(
        -mathf::sin(heading_rad) * TURRET_PIVOT_BEHIND_M,
        0.0,
        -mathf::cos(heading_rad) * TURRET_PIVOT_BEHIND_M,
    )
}

/// Wrap an angle to plus or minus pi.
fn wrap_angle(a: f32) -> f32 {
    let mut a = a;
    while a > mathf::PI {
        a -= mathf::PI * 2.0;
    }
    while a < -mathf::PI {
        a += mathf::PI * 2.0;
    }
    a
}

/// Whether the segment travelled by a shell this tick passes through the target's
/// hit volume.
///
/// The volume is a vertical cylinder of radius [`HIT_RADIUS_M`] about the target's
/// ground contact, spanning `-HIT_FLOOR_M .. HIT_CEILING_M` relative to it. This
/// solves for the closest approach of the *segment* rather than sampling its end
/// point, so a round moving further than the cylinder is wide in a single tick
/// cannot pass straight through the target unseen.
pub fn segment_hits_target(from: Vec3, to: Vec3, target: Vec3) -> bool {
    let dx = to.x - from.x;
    let dz = to.z - from.z;
    let len_sq = dx * dx + dz * dz;

    // Parameter along the segment at which the horizontal distance to the target is
    // smallest. A shell fired straight up (len_sq ~ 0) keeps t at its start value,
    // which is the correct limit.
    let t = if len_sq > 1e-12 {
        let px = from.x - target.x;
        let pz = from.z - target.z;
        mathf::clamp01(-(px * dx + pz * dz) / len_sq)
    } else {
        0.0
    };

    let cx = from.x + dx * t;
    let cz = from.z + dz * t;
    let horiz_sq = (cx - target.x) * (cx - target.x) + (cz - target.z) * (cz - target.z);
    if horiz_sq >= HIT_RADIUS_M * HIT_RADIUS_M {
        return false;
    }

    // Height is interpolated at the same parameter, so a descending round is judged
    // where it actually crosses the target rather than where the tick ended.
    let cy = from.y + (to.y - from.y) * t;
    let dy = cy - target.y;
    dy < HIT_CEILING_M && dy > -HIT_FLOOR_M
}

/// Slew `current` toward `target` by at most `max_step`, taking the short way round.
///
/// Only correct for a quantity that is genuinely unbounded over a full circle — the
/// AI's hull heading. For a quantity confined to an arc, use [`slew_bounded`].
fn slew(current: f32, target: f32, max_step: f32) -> f32 {
    let delta = wrap_angle(target - current);
    if mathf::abs(delta) <= max_step {
        return wrap_angle(target);
    }
    wrap_angle(current + mathf::sign(delta) * max_step)
}

/// Slew `current` toward `target` by at most `max_step`, without wrapping.
///
/// Both operands are already confined to the same bounded interval, so the direct
/// difference is the meaningful one. [`slew`] must NOT be used here: it takes the
/// short way round an unbounded circle, and when the two traverse stops are further
/// apart than pi (2 * MAX_TRAVERSE = 4.2 rad > pi) the short way round can point
/// *out through the back* of the arc, leaving the clamp to pin the turret at its
/// stop forever. With the turret parked at the right-hand stop and a target
/// demanded over the left shoulder, every step pushed it further out and it could
/// never return.
fn slew_bounded(current: f32, target: f32, max_step: f32) -> f32 {
    let delta = target - current;
    if mathf::abs(delta) <= max_step {
        return target;
    }
    current + mathf::sign(delta) * max_step
}
