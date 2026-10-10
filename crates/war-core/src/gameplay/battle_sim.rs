//! The ground battle: two vehicles driving a fixed-step arena, shells as
//! travelling projectiles, and hit resolution through the real armour model.
//!
//! Ported from `Assets/Scripts/Gameplay/BattleSim.cs`.

use crate::ballistics::{ArmourPlate, ImpactResult, Penetration, Shell, ShellType};
use crate::math::{mathf, Vec3};
use crate::vehicles::{VehicleCatalog, VehicleDefinition};

/// Simulation rate. 30 Hz is plenty for a 40 km/h vehicle.
pub const TICK_HZ: f32 = 30.0;
pub const FIXED_DT: f32 = 1.0 / TICK_HZ;

/// Half-extent of the playable arena, metres.
pub const ARENA_HALF_EXTENT: f32 = 90.0;

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
const BRAKING_DECEL_MPS2: f32 = 45.0;

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
/// Highest shell position that can still hit a hull, metres.
const HIT_CEILING_M: f32 = 4.5;
/// How far outside the arena a shell may travel before being discarded.
const SHELL_CULL_MARGIN_M: f32 = 40.0;

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
const AI_SPEED_SCALE: f32 = 0.8;
/// How hard the AI slews its hull, radians per second.
const AI_SLEW_RATE_RAD_PER_S: f32 = 1.2;
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

    pub fn player_speed_kph(&self) -> f32 {
        self.player_speed_kph
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
            }
        }

        if self.player_health > 0 {
            self.drive_player(dt, throttle, steer);
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
        let target_speed = throttle * MAX_SPEED_MPS;
        let mut rate = ACCELERATION_MPS2;
        if mathf::abs(throttle) < 0.01 {
            rate = COAST_DECEL_MPS2;
        } else if (self.player_current_speed_mps > 0.1 && throttle < -0.01)
            || (self.player_current_speed_mps < -0.1 && throttle > 0.01)
        {
            rate = BRAKING_DECEL_MPS2;
        }
        self.player_current_speed_mps =
            mathf::move_towards(self.player_current_speed_mps, target_speed, rate * dt);
        let speed = self.player_current_speed_mps;

        // The hull steers onto the gun's line while driving.
        //
        // Without this the tank and its turret always disagree: the gun tracks the
        // mouse in world space while the hull keeps whatever heading the driver
        // chose, so the turret visibly swings the opposite way to the tank every
        // time the hull turns. It reads as an inverted control even though the gun
        // is in fact pointing at the target. Real tracked vehicles do exactly this.
        //
        // Only while actually moving: a stationary tank needs to be able to hold
        // its traverse for aiming, and it cannot steer anyway.
        let moving = if mathf::abs(throttle) > 0.01 {
            1.0
        } else {
            0.0
        };
        let gun_world_heading = wrap_angle(self.player_heading_rad + self.player_turret_yaw_rad);
        let to_gun_line = wrap_angle(gun_world_heading - self.player_heading_rad);

        let mut demand = (to_gun_line * HULL_AUTO_ALIGN_STRENGTH * moving) + steer;
        demand = mathf::clamp(demand, -1.0, 1.0);

        let max_step = MAX_TURN_RATE_RAD * dt;
        self.player_heading_rad = wrap_angle(
            self.player_heading_rad + mathf::clamp(demand * MAX_TURN_RATE_RAD, -max_step, max_step),
        );

        let forward = Vec3::new(
            mathf::sin(self.player_heading_rad),
            0.0,
            mathf::cos(self.player_heading_rad),
        );
        let next = self.player_position + forward * (speed * dt);

        self.player_position = Vec3::new(
            mathf::clamp(next.x, -ARENA_HALF_EXTENT, ARENA_HALF_EXTENT),
            0.0,
            mathf::clamp(next.z, -ARENA_HALF_EXTENT, ARENA_HALF_EXTENT),
        );
        self.player_speed_kph = mathf::abs(speed) * 3.6;
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
            self.player_gun_pitch_rad = slew(
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
            slew(self.player_turret_yaw_rad, wanted, TURRET_SLEW_RATE * dt);

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
        let dist = to_player.magnitude();

        let throttle = if dist < AI_CLOSE_RANGE_M {
            AI_THROTTLE_CLOSE
        } else {
            AI_THROTTLE
        };

        let desired_heading = if dist > 0.1 {
            mathf::atan2(to_player.x, to_player.z) + mathf::sin(phase) * AI_WEAVE_RAD
        } else {
            self.enemy_heading_rad
        };

        self.enemy_heading_rad = slew(
            self.enemy_heading_rad,
            wrap_angle(desired_heading - self.enemy_heading_rad) + self.enemy_heading_rad,
            AI_SLEW_RATE_RAD_PER_S * dt,
        );

        let speed = throttle * MAX_SPEED_MPS * AI_SPEED_SCALE;
        let forward = Vec3::new(
            mathf::sin(self.enemy_heading_rad),
            0.0,
            mathf::cos(self.enemy_heading_rad),
        );
        let next = self.enemy_position + forward * (speed * dt);

        self.enemy_position = Vec3::new(
            mathf::clamp(next.x, -ARENA_HALF_EXTENT, ARENA_HALF_EXTENT),
            0.0,
            mathf::clamp(next.z, -ARENA_HALF_EXTENT, ARENA_HALF_EXTENT),
        );

        self.enemy_turret_yaw_rad =
            wrap_angle(mathf::atan2(-to_player.x, -to_player.z) - self.enemy_heading_rad);
        self.enemy_turret_yaw_rad =
            mathf::clamp(self.enemy_turret_yaw_rad, -MAX_TRAVERSE, MAX_TRAVERSE);

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
        self.player_current_speed_mps -= RECOIL_IMPULSE_MPS;

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

            // Hit volume is roughly the hull footprint plus the turret.
            let dx = shell.position.x - target.x;
            let dz = shell.position.z - target.z;
            let hit = target_alive
                && (dx * dx + dz * dz) < HIT_RADIUS_M * HIT_RADIUS_M
                && shell.position.y < HIT_CEILING_M;

            if hit {
                self.resolve_hit(&shell);
                self.in_flight.remove(i);
                index -= 1;
                continue;
            }

            if shell.life <= 0.0
                || mathf::abs(shell.position.x) > ARENA_HALF_EXTENT + SHELL_CULL_MARGIN_M
                || mathf::abs(shell.position.z) > ARENA_HALF_EXTENT + SHELL_CULL_MARGIN_M
            {
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
                self.enemy_position = Vec3::new(self.randomize(45.0), 0.0, self.randomize(45.0));
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

/// Slew `current` toward `target` by at most `max_step`, taking the short way round.
fn slew(current: f32, target: f32, max_step: f32) -> f32 {
    let delta = wrap_angle(target - current);
    if mathf::abs(delta) <= max_step {
        return wrap_angle(target);
    }
    wrap_angle(current + mathf::sign(delta) * max_step)
}
