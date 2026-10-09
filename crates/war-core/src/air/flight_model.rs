//! Forces, stability, trim, stall, integrator.
//!
//! Ported from `Assets/Scripts/Air/FlightModel.cs`. Every tuning constant is
//! reproduced exactly; see `PORT-NOTES.md` for what was deliberately not carried
//! over.

use crate::math::{mathf, Quat, Vec3};

use super::catalog::AircraftDefinition;

/// Standard-atmosphere density at sea level, kg/m3.
pub const AIR_DENSITY_SEA_LEVEL: f32 = 1.225;
/// Scale height of the standard atmosphere, metres.
pub const SCALE_HEIGHT: f32 = 8500.0;
/// Gravity, m/s2.
pub const GRAVITY: f32 = 9.81;
/// Speed of sound at sea level, m/s. Sets the Mach number.
pub const SPEED_OF_SOUND: f32 = 340.0;
/// Drag rise starts here. Below it, compressibility is negligible.
pub const TRANSONIC_MACH: f32 = 0.85;
/// How steeply drag climbs past the transonic threshold.
pub const COMPRESSIBILITY_DRAG_RISE: f32 = 3.2;
/// Peak lift coefficient, reached just before the stall.
pub const MAX_LIFT_COEFFICIENT: f32 = 1.3;
/// Oswald span efficiency. Around 0.8 is typical for a fighter wing.
pub const OSWALD_EFFICIENCY: f32 = 0.80;
/// Angle of attack at which the wing stalls, degrees.
pub const STALL_ANGLE_DEG: f32 = 16.0;
/// Cap on the velocity change a single step may make, m/s.
pub const MAX_VELOCITY_CHANGE_PER_STEP_MS: f32 = 8.0;
/// Longitudinal stability gain.
pub const PITCH_STABILITY_GAIN: f32 = 3.2;
/// Pitch rate damping. Turns a bare spring into something that settles.
pub const PITCH_DAMPING_GAIN: f32 = 0.35;
/// How hard the trim works the actual path onto the commanded path.
pub const FLIGHT_PATH_DAMPING_GAIN: f32 = 3.0;
/// Surplus thrust to commanded climb angle.
pub const SURPLUS_THRUST_PITCH_GAIN: f32 = 0.001;
/// Speed error to path bias.
pub const SPEED_TO_PATH_GAIN: f32 = 0.03;
/// Reference wing loading that the surplus gain is normalised against, N/m2.
pub const REFERENCE_WING_LOADING: f32 = 2800.0;
/// Steepest shallow climb the trim will command, degrees.
pub const MAX_TRIM_CLIMB_ANGLE_DEG: f32 = 4.0;
/// Steepest shallow descent the trim will command, degrees.
pub const MAX_TRIM_DIVE_ANGLE_DEG: f32 = 8.0;

/// Minimum altitude the model will let an aircraft reach, metres. A hard floor
/// that stands in for terrain.
pub const TERRAIN_FLOOR_M: f32 = 1.5;

/// Standard-atmosphere density at altitude.
pub fn air_density(altitude_m: f32) -> f32 {
    AIR_DENSITY_SEA_LEVEL * mathf::exp(-mathf::max(0.0, altitude_m) / SCALE_HEIGHT)
}

/// One aircraft in flight.
///
/// C# held all state in public fields on a mutable reference type. Here the state
/// is private with read accessors, so the write ordering inside [`Self::step`] --
/// which the C# relied on to keep `velocity` as a consistent snapshot -- is an
/// implementation detail rather than something any caller can accidentally break.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FlightModel {
    spec: &'static AircraftDefinition,
    position: Vec3,
    velocity: Vec3,
    /// Body orientation, as Euler angles in degrees for readability.
    pitch_deg: f32,
    /// Pitch rate, degrees per second. Feeds the damping term.
    pitch_deg_rate: f32,
    yaw_deg: f32,
    roll_deg: f32,
    throttle: f32,
    /// Propeller RPM-ish state used only for the visual.
    propeller_angle: f32,
    /// True when the wing has stalled this frame.
    is_stalled: bool,
    /// True when the airframe is over never-exceed speed and is breaking up.
    is_overstressed: bool,
}

impl FlightModel {
    /// The catalogue entry this aircraft flies.
    pub fn spec(&self) -> &'static AircraftDefinition {
        self.spec
    }

    pub fn position(&self) -> Vec3 {
        self.position
    }

    pub fn velocity(&self) -> Vec3 {
        self.velocity
    }

    pub fn altitude(&self) -> f32 {
        self.position.y
    }

    pub fn speed_ms(&self) -> f32 {
        self.velocity.magnitude()
    }

    pub fn pitch_deg(&self) -> f32 {
        self.pitch_deg
    }

    pub fn yaw_deg(&self) -> f32 {
        self.yaw_deg
    }

    pub fn roll_deg(&self) -> f32 {
        self.roll_deg
    }

    pub fn throttle(&self) -> f32 {
        self.throttle
    }

    pub fn propeller_angle(&self) -> f32 {
        self.propeller_angle
    }

    pub fn is_stalled(&self) -> bool {
        self.is_stalled
    }

    pub fn is_overstressed(&self) -> bool {
        self.is_overstressed
    }

    /// Indicated airspeed: what the pitot tube reads, corrected for compressibility.
///
/// **Bug fix.** The C# computed `SpeedMs * sqrt(rho0 / rho)`, which at 3000 m
/// turns 162 m/s of true airspeed into 193 m/s -- a *faster* reading at altitude.
/// Its own comment admitted as much: *"Compression makes it read faster than TAS
/// at altitude."* Indicated airspeed works the other way: the lower the density,
/// the slower the pitot tube reads, because a given dynamic pressure needs a
/// higher true speed to produce it. The ratio is inverted here.
///
/// It currently has zero consumers, so the fix is inert today; it is corrected
/// rather than dropped because it is a genuinely useful HUD quantity and a
/// one-character fix, and because a corrected-but-unused helper is less of a trap
/// than a wrong one that someone eventually wires up.
pub fn indicated_speed_ms(&self) -> f32 {
    let density = air_density(self.position.y);
    let ratio = density / AIR_DENSITY_SEA_LEVEL;
    self.speed_ms() * mathf::sqrt(ratio)
}

    /// Body axes, all derived from a single shared rotation.
    ///
    /// Deriving each independently would produce three axes that are not
    /// mutually perpendicular, because Euler rotations do not commute. Lift would
    /// then be applied in a direction disagreeing with the nose, and an aircraft
    /// sitting level and unloaded would still sink.
    pub fn forward(&self) -> Vec3 {
        Quat::euler_xyz_unity(self.pitch_deg, self.yaw_deg, self.roll_deg).rotate(Vec3::FORWARD)
    }

    pub fn right(&self) -> Vec3 {
        Quat::euler_xyz_unity(self.pitch_deg, self.yaw_deg, self.roll_deg).rotate(Vec3::RIGHT)
    }

    pub fn up(&self) -> Vec3 {
        Quat::euler_xyz_unity(self.pitch_deg, self.yaw_deg, self.roll_deg).rotate(Vec3::UP)
    }

    /// Spawn an aircraft.
    ///
    /// It starts at its own trim speed, already established in the catalogue,
    /// rather than at some fraction of top speed. That matters more than it looks:
    /// a fraction of top speed is a different number for every airframe, so some
    /// spawn below their stall speed and cannot generate enough lift to fly. The
    /// Me 262 spawned at 134 m/s, decelerated, and was still below its 75 m/s stall
    /// speed several minutes later with the nose level and full power on. Starting
    /// at the trim speed means the aircraft begins in equilibrium.
    pub fn new(
        spec: &'static AircraftDefinition,
        spawn_position: Vec3,
        heading_deg: f32,
    ) -> Self {
        let mut model = FlightModel {
            spec,
            position: spawn_position,
            velocity: Vec3::ZERO,
            pitch_deg: 2.0,
            pitch_deg_rate: 0.0,
            yaw_deg: heading_deg,
            roll_deg: 0.0,
            throttle: 0.8,
            propeller_angle: 0.0,
            is_stalled: false,
            is_overstressed: false,
        };
        // Forward() must be evaluated with the attitude just set above.
        model.velocity = model.forward() * spec.trim_speed_ms;
        model
    }

    /// The angle of attack at which lift exactly balances weight for the current
    /// speed and dynamic pressure.
    ///
    /// Deriving it is what lets the same wing-area and mass numbers produce a
    /// slow-turning A6M and a fast-turning Bf 109 without per-aircraft fudge
    /// factors.
    fn trim_angle_of_attack_for(&self, dynamic_pressure: f32, wing_area: f32) -> f32 {
        // Lift needed to hold the aircraft up equals its weight.
        let weight = self.spec.mass_kg * GRAVITY;
        let required_coefficient = weight / mathf::max(1e-3, dynamic_pressure * wing_area);

        // Invert CL = 2*pi*alpha (radians). If the wing genuinely cannot generate
        // this much lift at this speed, the aircraft is below its stall speed:
        // clamp rather than letting the trim chase an impossible value.
        let radians = required_coefficient / (2.0 * mathf::PI);

        // The ceiling on the trim target must sit below the stall angle. Letting it
        // reach or exceed the stall means the trim actively commands a stall
        // whenever the aircraft slows down, turning routine deceleration into
        // unrecoverable loss. The Me 262 stalled itself to destruction at 3000 m.
        let max_trim_angle = STALL_ANGLE_DEG - 3.0;
        mathf::min(radians * mathf::RAD2DEG, max_trim_angle)
    }

    /// Maximum thrust available right now, in newtons.
    ///
    /// The two engine classes need genuinely different treatment. A propeller is a
    /// momentum device: thrust is power over airspeed, so it is enormous on the
    /// takeoff roll and small at 200 m/s. A turbojet produces nearly the same thrust
    /// at any subsonic speed, which is precisely why it outruns a piston fighter in
    /// level flight.
    fn thrust_at(&self, density: f32, speed: f32) -> f32 {
        if !self.spec.propeller_driven {
            // Turbojets lose thrust with altitude as the compressor sees less air,
            // but far less than a piston engine, and they keep it at speed.
            let altitude_factor = mathf::lerp(1.0, density / AIR_DENSITY_SEA_LEVEL, 0.35);
            return self.spec.max_thrust_n * altitude_factor;
        }

        // Piston engines are altitude-limited: roughly half the power is gone by
        // 6 km, which is why prop fighters want height and jets do not.
        let power = self.spec.engine_power_w * (density / AIR_DENSITY_SEA_LEVEL);

        // Thrust = power * efficiency / airspeed, capped by the propeller's static
        // thrust. The floor on airspeed keeps this finite at a standstill.
        let velocity_thrust =
            power * self.spec.propeller_efficiency / mathf::max(speed, 25.0);
        mathf::min(velocity_thrust, self.spec.max_thrust_n)
    }

    /// Total drag at the given speed and density, for an aircraft holding itself
    /// level.
    ///
    /// Shares its constants with the main aerodynamics on purpose. If the two
    /// drifted apart, the trim would aim at a speed the aircraft could never
    /// actually sustain.
    fn drag_at(&self, density: f32, speed: f32, wing_area: f32) -> f32 {
        let dynamic_pressure = mathf::max(1e-3, 0.5 * density * speed * speed);
        let required_lift = self.spec.mass_kg * GRAVITY;

        let lift_coefficient =
            mathf::min(required_lift / (dynamic_pressure * wing_area), MAX_LIFT_COEFFICIENT);
        let lift = lift_coefficient * dynamic_pressure * wing_area;

        let aspect_ratio = mathf::max(
            1.0,
            (self.spec.wing_span_m * self.spec.wing_span_m) / wing_area,
        );
        let induced_drag = (lift * lift)
            / (dynamic_pressure * wing_area * mathf::PI * OSWALD_EFFICIENCY * aspect_ratio);

        let parasite_drag = dynamic_pressure * wing_area * self.spec.parasite_drag_coefficient;

        let mach = speed / SPEED_OF_SOUND;
        let mut compressibility_drag = 0.0;
        if mach > TRANSONIC_MACH {
            compressibility_drag =
                parasite_drag * (mach - TRANSONIC_MACH) * COMPRESSIBILITY_DRAG_RISE;
        }

        induced_drag + parasite_drag + compressibility_drag
    }

    /// Advances the aircraft by one fixed step.
    ///
    /// * `pitch_input`: -1 nose down .. +1 nose up
    /// * `roll_input`: -1 roll left .. +1 roll right
    /// * `yaw_input`: rudder, -1..1
    /// * `throttle_input`: -1..1, adjusts the throttle setting
    ///
    /// The ordering below is load-bearing and was transcribed step by step from
    /// the C# original. Three things in particular will silently break the model
    /// if they are "tidied":
    ///
    /// 1. `velocity` is a **snapshot**. Nothing reads `self.velocity` for physics
    ///    until the final write-back, so every force sees the same start-of-step
    ///    velocity.
    /// 2. Angle of attack is measured **twice** -- once before the control update
    ///    and once after `pitch_deg` has changed, against the attitude the aircraft
    ///    will actually fly this step. Reusing the first measurement leaves a
    ///    one-frame lag that shows up as a slow steady sink in level flight.
    /// 3. `forward` is captured *after* the roll auto-level decay, so it includes
    ///    it. `up` is evaluated later still, from the same decayed roll.
    pub fn step(
        &mut self,
        dt: f32,
        pitch_input: f32,
        roll_input: f32,
        yaw_input: f32,
        throttle_input: f32,
    ) {
        self.throttle = mathf::clamp01(self.throttle + throttle_input * dt);

        // --- Kinematics, sampled before control ---
        // Stability has to be judged against the airflow as it is now, so the angle
        // of attack is measured up front. Reading it after the control update would
        // make the aerodynamics chase the elevator by one frame.
        let mut velocity = self.velocity;
        let speed = velocity.magnitude();
        let density = air_density(self.position.y);
        let wing_area = self.spec.wing_area_m2;

        // Zero airspeed produces no dynamic pressure, hence no lift, which is how
        // an aircraft ends up falling. The small floor keeps the stall maths
        // well-conditioned instead of dividing by zero at the top of a vertical
        // climb.
        let dynamic_pressure = mathf::max(0.01, 0.5 * density * speed * speed);

        // Angle of attack: the angle between the nose and the direction the aircraft
        // is actually travelling through the air.
        //
        // The argument order matters and is easy to get backwards. Measured as
        // SignedAngle(flight_path, nose, right) a nose-up attitude reports a
        // NEGATIVE angle of attack, so lift is computed downward and the aircraft
        // is driven into the ground with no way to recover. Measuring nose against
        // flight path gives the conventional sign: nose above the airflow is
        // positive, and therefore lift-up.
        let mut angle_of_attack = 0.0f32;
        if speed > 1.0 {
            let flight_path = Vec3::project_on_plane(velocity, self.right()).normalized();
            angle_of_attack = Vec3::signed_angle(self.forward(), flight_path, self.right());
        }
        angle_of_attack = mathf::clamp(angle_of_attack, -20.0, 24.0);

        // --- Control authority ---
        // Roll is the most agile axis and is scaled by speed: a stalled plane
        // cannot roll, which is the single most important thing a player learns in
        // a flight sim.
        let speed_factor =
            mathf::clamp01(self.speed_ms() / mathf::max(20.0, self.spec.top_speed_ms * 0.5));

        self.roll_deg += roll_input * self.spec.roll_rate_deg * speed_factor * dt;
        self.roll_deg = mathf::clamp(self.roll_deg, -80.0, 80.0);

        // Pitch and roll induce turn. Bank angle is what actually turns a fighter:
        // pull back and roll, and the lift vector tilts.
        let bank_turn = -self.roll_deg * 0.18;
        self.yaw_deg += (bank_turn + yaw_input * 8.0) * speed_factor * dt;

        // --- Elevator, and static longitudinal stability ---
        // A tailplane pulls the nose back toward the angle of attack the wing is
        // trimmed for. Without this term the model has no stable point at all: any
        // speed change moves the airflow, lift stops matching weight, and the
        // aircraft sinks from cruise forever with no elevator input to correct it.
        let trim_angle_of_attack = self.trim_angle_of_attack_for(dynamic_pressure, wing_area);

        // Sign matters here, and getting it wrong makes the aircraft diverge
        // instead of fly. Unity's ZXY Euler order puts a *positive* X pitch
        // nose-down, so a nose-up correction is a negative pitch change. With the
        // signs the other way round the stability term pushes the nose further down
        // every frame and the aircraft leaves the atmosphere.
        let stability_pitch = -(trim_angle_of_attack - angle_of_attack) * PITCH_STABILITY_GAIN;

        // Speed stability: the aircraft is trimmed for the speed its power and drag
        // can actually sustain at this altitude, not merely the speed it happens to
        // be doing when it spawns. This is what removes the phugoid -- matching lift
        // to weight alone leaves speed free to wander and the aircraft trades height
        // for speed for minutes on end.
        //
        // Level-flight trim is a cascade of two error terms. Surplus thrust buys a
        // *commanded climb angle*, not a raw pitch rate: commanding pitch rate
        // directly has no fixed point, because spare power keeps asking for nose-up,
        // induced drag climbs, speed bleeds away, and the aircraft ends up stalled
        // at the ceiling. And surplus thrust alone still does not converge, so the
        // result is corrected toward the catalogue trim speed.
        let mut trim_pitch = 0.0f32;
        if mathf::abs(pitch_input) < 0.05 && speed > 10.0 {
            let flight_path_angle =
                mathf::asin(mathf::clamp(velocity.y / speed, -1.0, 1.0)) * mathf::RAD2DEG;

            let available_thrust = self.thrust_at(density, speed) * self.throttle;
            let surplus_thrust = available_thrust - self.drag_at(density, speed, wing_area);
            let speed_error = speed - self.spec.trim_speed_ms;

            // Scaled by wing loading, because that is what makes the response
            // comparable across very different airframes. Wing loading ranges from
            // about 1900 N/m2 on the A6M Zero to 4600 N/m2 on the P-51D, so a fixed
            // gain made the heavy jets pitch violently for a surplus a light prop
            // handled calmly.
            let wing_loading = self.spec.mass_kg * GRAVITY / mathf::max(1.0, wing_area);
            let surplus_gain =
                SURPLUS_THRUST_PITCH_GAIN * REFERENCE_WING_LOADING / wing_loading;

            let commanded_climb = mathf::clamp(
                -surplus_thrust * surplus_gain,
                -MAX_TRIM_DIVE_ANGLE_DEG,
                MAX_TRIM_CLIMB_ANGLE_DEG,
            );

            // Speed error is a *bias* on the path, not a rate term of its own.
            //
            // Adding it straight into the pitch rate looks reasonable and is
            // catastrophic. A 60 m/s error then demands about 3 deg/s of nose down,
            // which swamps the path correction: the aircraft flies itself into the
            // ground at full power with the nose pinned down, because thrust is being
            // pointed at the floor instead of along the flight path. The Meteor did
            // precisely this and stalled out every time.
            let speed_bias_angle = mathf::clamp(
                speed_error * SPEED_TO_PATH_GAIN,
                -MAX_TRIM_DIVE_ANGLE_DEG,
                MAX_TRIM_DIVE_ANGLE_DEG,
            );

            trim_pitch = (flight_path_angle - commanded_climb - speed_bias_angle)
                * FLIGHT_PATH_DAMPING_GAIN;
        }

        let elevator_pitch = -pitch_input * self.spec.turn_rate_deg * 0.5 * speed_factor;

        // Damped, or the tailplane overshoots and the aircraft porpoises.
        let mut pitch_rate =
            (elevator_pitch + stability_pitch + trim_pitch) - self.pitch_deg_rate
                * PITCH_DAMPING_GAIN;

        // Cap the total pitch rate at the elevator's authority. The surplus-thrust
        // term does not know or care about angle of attack, so an aircraft with
        // plenty of power keeps commanding "nose up" indefinitely and flies into a
        // stall. Bounding the rate by the same authority the elevator has
        // reproduces the real limit: excess power buys a climb angle only up to the
        // stall.
        let max_pitch_rate = self.spec.turn_rate_deg * 0.5;
        pitch_rate = mathf::clamp(pitch_rate, -max_pitch_rate, max_pitch_rate);

        self.pitch_deg_rate = mathf::lerp(self.pitch_deg_rate, pitch_rate, 0.35);

        self.pitch_deg += self.pitch_deg_rate * dt;
        self.pitch_deg = mathf::clamp(self.pitch_deg, -45.0, 40.0);

        // Re-measure the airflow against the attitude the aircraft will actually fly
        // this step. Reusing the pre-control angle leaves a one-frame lag, which
        // shows up as a slow steady sink in otherwise level flight.
        if speed > 1.0 {
            let settled = Vec3::project_on_plane(velocity, self.right()).normalized();
            angle_of_attack = Vec3::signed_angle(self.forward(), settled, self.right());
            angle_of_attack = mathf::clamp(angle_of_attack, -20.0, 24.0);
        }

        // Roll naturally levels out, which is how real aircraft behave and stops the
        // plane from spiralling uncontrollably.
        if mathf::abs(roll_input) < 0.01 {
            self.roll_deg *= 1.0 - 1.6 * dt;
        }

        let forward = self.forward();

        // --- Lift ---
        // Thin-aerofoil theory gives CL = 2*pi*alpha for small angles, which is the
        // relation that makes wing area meaningful: a small wing (Spitfire, 12.4 m2)
        // needs a higher coefficient and therefore a lower speed to fly than a big
        // wing (P-51D, 18.1 m2) of the same mass.
        //
        // Past the stall angle the coefficient collapses. Modelling that collapse is
        // what makes a stall recoverable rather than an unrecoverable fall.
        let abs_aoa = mathf::abs(angle_of_attack);
        let lift_coefficient;

        if abs_aoa > STALL_ANGLE_DEG {
            self.is_stalled = true;
            // Lose roughly 1.2 CL per 10 degrees past the stall, floored so the wing
            // keeps some authority instead of becoming a brick.
            let over = (abs_aoa - STALL_ANGLE_DEG) * 0.12;
            lift_coefficient = mathf::max(1.2 - mathf::min(over, 0.9), 0.3);
        } else {
            self.is_stalled = false;
            // Thin-aerofoil theory over-predicts near the stall; taper it so the peak
            // lands at a believable CLmax.
            lift_coefficient =
                mathf::min(2.0 * mathf::PI * angle_of_attack * mathf::DEG2RAD, MAX_LIFT_COEFFICIENT);
        }

        let lift = self.up() * (lift_coefficient * dynamic_pressure * wing_area);

        // --- Drag ---
        // Induced drag grows with lift, parasite drag with speed. Induced drag
        // follows D = L^2 / (q * S * pi * e * AR); the aspect ratio is what
        // separates a wide, efficient wing from a narrow one, which is why the
        // Meteor can dogfight the 262 despite the 262's speed advantage.
        let lift_magnitude = lift.magnitude();

        let aspect_ratio =
            mathf::max(1.0, (self.spec.wing_span_m * self.spec.wing_span_m) / wing_area);
        let induced_drag = (lift_magnitude * lift_magnitude)
            / (dynamic_pressure * wing_area * mathf::PI * OSWALD_EFFICIENCY * aspect_ratio);

        let parasite_drag = dynamic_pressure * wing_area * self.spec.parasite_drag_coefficient;
        let mut compressibility_drag = 0.0f32;

        // Transonic drag rise. Below Mach 0.85 it is negligible; past it the drag
        // climbs steeply, which is why the 262 needs altitude to go fast.
        let mach = speed / SPEED_OF_SOUND;
        if mach > TRANSONIC_MACH {
            compressibility_drag =
                parasite_drag * (mach - TRANSONIC_MACH) * COMPRESSIBILITY_DRAG_RISE;
        }

        let drag_force = -velocity.normalized()
            * (induced_drag + parasite_drag + compressibility_drag);

        // --- Thrust ---
        let max_thrust = self.thrust_at(density, speed);
        let thrust = forward * (max_thrust * self.throttle);

        // --- Gravity ---
        let gravity = Vec3::DOWN * (self.spec.mass_kg * GRAVITY);

        // --- Integrate ---
        //
        // Explicit Euler on lift is only conditionally stable. Dynamic pressure
        // scales with the square of speed, so the lift term grows faster than the
        // integration can absorb it, and once an aircraft is pushed past a few
        // hundred m/s the lift for one step is enough to throw it to the ceiling and
        // far beyond. This is a genuine solver limitation, not a tuning problem:
        // the Me 262 reached 3720 m/s at the ceiling before this was caught.
        //
        // The fix caps the change in velocity a single step may make, which is
        // standard practice for explicit integrators. The cap is generous enough to
        // be inert during ordinary flight.
        let acceleration = (lift + drag_force + thrust + gravity) / self.spec.mass_kg;

        let mut velocity_change = acceleration * dt;
        let max_change = MAX_VELOCITY_CHANGE_PER_STEP_MS * self.spec.mass_kg;
        if velocity_change.sqr_magnitude() > max_change * max_change {
            velocity_change = velocity_change.normalized() * max_change;
        }

        velocity += velocity_change;

        // Hard speed ceiling. The per-step clamp above is not sufficient on its own,
        // because lift grows with the square of speed: past a certain speed an
        // aircraft needs so much lift that even a bounded step accelerates it
        // further, and the error compounds. Unbounded, the Me 262 and the Meteor
        // both reached over 3000 m/s at the service ceiling while stalled.
        //
        // This is a numerical backstop rather than a flight rule. It sits above
        // every aircraft's never-exceed speed, so it can never engage in normal
        // flight or even in a full-power dive.
        let speed_after_step = velocity.magnitude();
        let speed_limit = self.spec.never_exceed_speed_ms * 1.5;
        if speed_after_step > speed_limit {
            velocity = velocity.normalized() * speed_limit;
        }

        self.position += velocity * dt;

        // Never let the aircraft go below the terrain.
        if self.position.y < TERRAIN_FLOOR_M {
            self.position.y = TERRAIN_FLOOR_M;
            if velocity.y < 0.0 {
                velocity.y = 0.0;
            }
        }

        // Ceiling: thrust stops being able to overcome drag.
        if self.position.y > self.spec.ceiling_m {
            self.position.y = self.spec.ceiling_m;
        }

        // Write-back last: nothing above read the live velocity field for physics.
        self.velocity = velocity;

        self.is_overstressed = self.speed_ms() > self.spec.never_exceed_speed_ms;
        self.propeller_angle += self.throttle * 1800.0 * dt;
    }

    /// Whether the aircraft is pointed close enough to `target` to be putting
    /// rounds on it. Used to gate the gun so the HUD can show convergence rather
    /// than letting players spray.
    pub fn is_aimed_at(&self, target: Vec3, max_angle_deg: f32) -> bool {
        let to_target = target - self.position;
        if to_target.sqr_magnitude() < 1.0 {
            return true;
        }
        Vec3::angle(self.forward(), to_target) <= max_angle_deg
    }

    /// Angular offset of the gunsight line to a target at `range`. This is the lead
    /// the player has to compute, so the HUD can show it.
    pub fn lead_angle_deg(
        &self,
        target_velocity: Vec3,
        target: Vec3,
        range: f32,
    ) -> f32 {
        if range < 1.0 {
            return 0.0;
        }
        let time_of_flight = range / mathf::max(1.0, self.spec.gun_muzzle_vel_ms);
        let lead = target + target_velocity * time_of_flight;
        let to_lead = lead - self.position;
        if to_lead.sqr_magnitude() < 1.0 {
            return 0.0;
        }
        Vec3::signed_angle(self.forward(), to_lead, self.up())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::air::catalog::AircraftCatalog;

    /// Build a trimmed aircraft at altitude, as the C# test helper did.
    fn trimmed(id: &str, altitude: f32) -> FlightModel {
        FlightModel::new(
            AircraftCatalog::get(id).expect("unknown aircraft id"),
            Vec3::new(0.0, altitude, 0.0),
            0.0,
        )
    }

    fn run(model: &mut FlightModel, ticks: u32, pitch: f32, roll: f32, yaw: f32, throttle: f32) {
        for _ in 0..ticks {
            model.step(1.0 / 60.0, pitch, roll, yaw, throttle);
        }
    }

    #[test]
    fn air_density_follows_the_standard_atmosphere() {
        assert!((air_density(0.0) - 1.225).abs() < 1e-4);
        // Halving density takes roughly one scale height of climb.
        let at_half = air_density(SCALE_HEIGHT * 0.693);
        assert!((at_half - 0.6125).abs() < 1e-3, "got {at_half}");
        // Below sea level the model clamps rather than extrapolating.
        assert_eq!(air_density(-500.0), air_density(0.0));
    }

    #[test]
    fn spawns_at_trim_speed_pointing_very_slightly_down() {
        for id in ["bf109-g6", "spitfire-mkix", "me262-a1a", "meteor-f4"] {
            let spec = AircraftCatalog::get(id).expect("known id");
            let model = FlightModel::new(spec, Vec3::new(0.0, 3000.0, 0.0), 0.0);
            assert!(
                (model.speed_ms() - spec.trim_speed_ms).abs() < 1e-3,
                "{id}: spawned at {} m/s, trim speed is {}",
                model.speed_ms(),
                spec.trim_speed_ms
            );
            // The model spawns at pitch = +2 degrees, and Unity's Euler order makes
            // positive pitch nose-DOWN, so the initial velocity tilts slightly
            // below the horizon. If this ever inverts, the ZXY Euler order is wrong.
            assert!(
                model.velocity().y < 0.0,
                "{id}: +2 deg pitch must point the nose slightly down, got {:?}",
                model.velocity()
            );
            assert!(model.velocity().z > 0.0, "{id} should start heading +Z");
            // A 2 degree tilt is tiny; the velocity must still be dominated by yaw.
            assert!(
                (model.velocity().z / model.speed_ms() - 0.999).abs() < 0.01,
                "{id}: yaw component should be cos(2 deg)"
            );
        }
    }

    /// The Me 262 spawned at 134 m/s (a fraction of top speed) and was still below
    /// its 75 m/s stall speed minutes later. Spawning at the catalogue trim speed is
    /// what fixes that.
    #[test]
    fn every_aircraft_spawns_above_its_own_stall_speed() {
        for spec in AircraftCatalog::all() {
            let model = FlightModel::new(spec, Vec3::new(0.0, 3000.0, 0.0), 0.0);
            let density = air_density(3000.0);
            let stall_speed =
                mathf::sqrt(2.0 * spec.mass_kg * GRAVITY / (density * spec.wing_area_m2 * MAX_LIFT_COEFFICIENT));
            assert!(
                model.speed_ms() > stall_speed,
                "{} spawns at {} m/s, below its {stall_speed:.0} m/s stall speed",
                spec.id,
                model.speed_ms()
            );
        }
    }

    #[test]
    fn throttle_follows_its_input_as_a_rate() {
        let mut model = trimmed("bf109-g6", 3000.0);
        assert!((model.throttle() - 0.8).abs() < 1e-6);
        run(&mut model, 60, 0.0, 0.0, 0.0, 1.0);
        assert!((model.throttle() - 1.0).abs() < 1e-4, "one second full up");
        run(&mut model, 120, 0.0, 0.0, 0.0, -1.0);
        assert!((model.throttle() - 0.0).abs() < 1e-4, "two seconds full down");
        // And it clamps rather than going negative.
        run(&mut model, 120, 0.0, 0.0, 0.0, -1.0);
        assert_eq!(model.throttle(), 0.0);
    }

    /// Port of `JetsOutrunPropFightersInLevelFlight`. Unlike most of the roster
    /// relationships this one has to be settled by *simulating* rather than by
    /// reading the catalogue, because the whole jet-versus-prop distinction lives
    /// in how thrust is modelled rather than in any single published figure.
    ///
    /// 400 s of hands-off level flight at full power. If the turbojet thrust model
    /// were wrong -- constant thrust everywhere, say -- the 262 and the P-51D would
    /// converge, which is exactly what "you can jet out-climb a well-powered prop"
    /// is supposed to prevent.
    #[test]
    fn a_jet_outruns_a_piston_fighter_in_level_flight() {
        let mut jet = FlightModel::new(
            AircraftCatalog::get("me262-a1a").expect("me262 exists"),
            Vec3::new(0.0, 3000.0, 0.0),
            0.0,
        );
        let mut prop = FlightModel::new(
            AircraftCatalog::get("p51d").expect("mustang exists"),
            Vec3::new(0.0, 3000.0, 0.0),
            0.0,
        );

        for _ in 0..24000 {
            jet.step(1.0 / 60.0, 0.0, 0.0, 0.0, 1.0);
            prop.step(1.0 / 60.0, 0.0, 0.0, 0.0, 1.0);
        }

        let gap = jet.speed_ms() - prop.speed_ms();
        assert!(
            jet.speed_ms() > prop.speed_ms(),
            "the Me 262 must be faster than the P-51D in level flight: {} vs {}",
            jet.speed_ms(),
            prop.speed_ms()
        );
        assert!(
            gap > 15.0,
            "and by a decisive margin, not a rounding error: gap was only {gap:.1} m/s"
        );
    }

    #[test]
    fn hands_off_flight_holds_altitude_and_speed() {
        // The phugoid test: settle, then watch for 20 seconds with no input.
        for spec in AircraftCatalog::all() {
            let mut model = FlightModel::new(spec, Vec3::new(0.0, 3000.0, 0.0), 0.0);
            run(&mut model, 2400, 0.0, 0.0, 0.0, 0.0); // 40 s to settle

            let alt_before = model.altitude();
            let speed_before = model.speed_ms();
            run(&mut model, 1200, 0.0, 0.0, 0.0, 0.0); // 20 s hands off

            let d_alt = model.altitude() - alt_before;
            let d_speed = model.speed_ms() - speed_before;
            assert!(
                d_alt.abs() < 200.0,
                "{}: altitude drifted {d_alt:.0} m over 20 s",
                spec.id
            );
            assert!(
                d_speed.abs() < 15.0,
                "{}: speed drifted {d_speed:.1} m/s over 20 s",
                spec.id
            );
            assert!(!model.is_stalled(), "{} stalled hands-off", spec.id);
        }
    }

    #[test]
    fn sustained_flight_stays_well_above_stall_speed() {
        // 20 000 steps is 333 s. Without the trim's speed-bias term this is where
        // the Meteor used to settle down to 72 m/s.
        for spec in AircraftCatalog::all() {
            let mut model = FlightModel::new(spec, Vec3::new(0.0, 3000.0, 0.0), 0.0);
            let density = air_density(3000.0);
            let stall_speed =
                mathf::sqrt(2.0 * spec.mass_kg * GRAVITY / (density * spec.wing_area_m2 * MAX_LIFT_COEFFICIENT));
            run(&mut model, 12000, 0.0, 0.0, 0.0, 1.0);
            assert!(
                model.speed_ms() > 1.3 * stall_speed,
                "{} settled at {} m/s, only 1.3x its {stall_speed:.0} m/s stall speed",
                spec.id,
                model.speed_ms()
            );
        }
    }

    #[test]
    fn hard_pull_up_stalls_and_the_stall_clears_on_recovery() {
        let mut model = trimmed("spitfire-mkix", 3000.0);
        // The throttle must actually reach zero, which means driving the *input*
        // negative -- passing a throttle input of 0 holds the spawn value of 0.8.
        // With thrust available the aircraft simply trades speed for climb forever
        // and never bleeds down to a stall.
        run(&mut model, 240, 0.0, 0.0, 0.0, -1.0);
        assert_eq!(model.throttle(), 0.0, "precondition: throttle at idle");
        assert!(!model.is_stalled(), "precondition: trimmed flight is not stalled");

        let mut stalled_ever = false;
        for _ in 0..1200 {
            model.step(1.0 / 60.0, 1.0, 0.0, 0.0, 0.0); // full back stick, idle
            if model.is_stalled() {
                stalled_ever = true;
                break;
            }
        }
        assert!(stalled_ever, "full back stick at idle must eventually stall the wing");

        // Recovery is deliberately NOT asserted here, because the C# suite does not
        // assert it either -- and probing it shows the model can recover briefly and
        // then re-enter a much deeper stall, with the angle of attack climbing past
        // 100 degrees and the nose pinned at the -45 degree clamp while the aircraft
        // descends. That is either a faithful property of this explicitly
        // "conditionally stable" integrator or a porting error, and it cannot be
        // told apart by reading the code.
        //
        // It is resolved by measurement instead:
        // `FlightModelParityTests.StepTraceMatchesTheCSharpOriginal` runs this exact
        // scenario through both implementations and compares every tick.
    }

    /// The scenario above, kept as a test so the deep-stall behaviour stays visible
    /// rather than being rediscovered later. Records what the model does; the
    /// parity test is what decides whether it is correct.
    #[test]
    fn a_stall_that_recovers_can_re_enter_a_deep_stall() {
        let mut model = trimmed("spitfire-mkix", 3000.0);
        run(&mut model, 240, 0.0, 0.0, 0.0, -1.0);
        for _ in 0..1200 {
            model.step(1.0 / 60.0, 1.0, 0.0, 0.0, 0.0);
            if model.is_stalled() {
                break;
            }
        }
        assert!(model.is_stalled(), "precondition: the wing is stalled");

        run(&mut model, 300, 0.0, 0.0, 0.0, 1.0);
        assert!(!model.is_stalled(), "it does recover once the stick is released");

        // And then it does not stay recovered. Recorded, not endorsed.
        run(&mut model, 600, 0.0, 0.0, 0.0, 1.0);
        assert!(
            model.is_stalled(),
            "KNOWN BEHAVIOUR: full power from a shallow stall re-enters a deep stall"
        );
    }

    /// Positive pitch input is nose-up. Because Unity's Euler order makes positive
    /// Euler pitch nose-down, the model must drive `pitch_deg` **negative** to
    /// climb. Getting this sign wrong is the single most damaging error available
    /// in this file: the stability term inverts and the aircraft flies away.
    #[test]
    fn nose_up_pulls_the_euler_pitch_negative() {
        let mut model = trimmed("spitfire-mkix", 3000.0);
        run(&mut model, 120, 0.0, 0.0, 0.0, 1.0);
        let level = model.pitch_deg();
        assert!(level.abs() < 10.0, "precondition: roughly level, got {level}");

        run(&mut model, 120, 1.0, 0.0, 0.0, 1.0);
        assert!(
            model.pitch_deg() < level,
            "positive pitch input must drive the Euler angle DOWN (nose up): \
             {level} -> {}",
            model.pitch_deg()
        );
        assert!(model.altitude() > 3000.0, "and it must actually climb");
    }

    #[test]
    fn climb_trades_speed_for_height() {
        let mut model = trimmed("spitfire-mkix", 2000.0);
        run(&mut model, 600, 0.0, 0.0, 0.0, 1.0);
        let alt_before = model.altitude();
        let speed_before = model.speed_ms();

        run(&mut model, 480, 1.0, 0.0, 0.0, 1.0);
        assert!(
            model.altitude() > alt_before,
            "pitching up must gain height: {} -> {}",
            alt_before,
            model.altitude()
        );
        assert!(
            model.speed_ms() < speed_before,
            "and must cost speed: {speed_before} -> {}",
            model.speed_ms()
        );
    }

    #[test]
    fn full_power_gains_speed_and_idling_loses_it() {
        let mut model = trimmed("spitfire-mkix", 0.0);
        run(&mut model, 120, 0.0, 0.0, 0.0, 0.8);
        assert!(model.speed_ms() > 60.0, "full throttle must accelerate");
        let fast = model.speed_ms();

        run(&mut model, 240, 0.0, 0.0, 0.0, -1.0);
        assert!(model.speed_ms() < fast, "closing the throttle must decelerate");
    }

    #[test]
    fn propeller_thrust_falls_off_with_altitude_but_jets_do_not() {
        // Relative loss of speed climbing from 1 km to 8 km.
        let climb_loss = |id: &str| {
            let spec = AircraftCatalog::get(id).expect("known id");
            let mut low = FlightModel::new(spec, Vec3::new(0.0, 1000.0, 0.0), 0.0);
            let mut high = FlightModel::new(spec, Vec3::new(0.0, 8000.0, 0.0), 0.0);
            run(&mut low, 900, 0.0, 0.0, 0.0, 1.0);
            run(&mut high, 900, 0.0, 0.0, 0.0, 1.0);
            1.0 - high.speed_ms() / low.speed_ms()
        };

        let prop_loss = climb_loss("bf109-g6");
        let jet_loss = climb_loss("me262-a1a");
        assert!(
            prop_loss > jet_loss,
            "the prop must lose more to altitude: {prop_loss} vs {jet_loss}"
        );
    }

    #[test]
    fn the_ceiling_is_respected() {
        let mut model = trimmed("bf109-g6", 11000.0);
        run(&mut model, 3000, 0.0, 0.0, 0.0, 1.0);
        assert!(
            model.altitude() <= model.spec().ceiling_m + 1.0,
            "{} climbed to {} m, ceiling is {}",
            model.spec().id,
            model.altitude(),
            model.spec().ceiling_m
        );
    }

    #[test]
    fn the_aircraft_never_falls_through_the_terrain() {
        let mut model = trimmed("a6m-zero", 900.0);
        for _ in 0..2000 {
            model.step(1.0 / 60.0, -1.0, 0.0, 0.0, -1.0); // nose down, idle
            assert!(
                model.position().y > 0.0,
                "fell through the floor to {}",
                model.position().y
            );
        }
        assert!(
            model.position().y >= TERRAIN_FLOOR_M,
            "must rest on the floor, not below it"
        );
    }

    /// The regression that motivated the per-step velocity cap and the hard speed
    /// ceiling: the Me 262 reached 3720 m/s at the ceiling before they existed.
    #[test]
    fn sustained_abuse_never_produces_nan_or_runaway_speed() {
        for spec in AircraftCatalog::all() {
            let mut model = FlightModel::new(spec, Vec3::new(0.0, 6000.0, 0.0), 0.0);
            for tick in 0..20000u32 {
                let t = tick as f32 * 0.01;
                model.step(1.0 / 60.0, mathf::sin(t), mathf::cos(t * 1.7), mathf::sin(t * 0.3), 1.0);
                let p = model.position();
                assert!(
                    p.x.is_finite() && p.y.is_finite() && p.z.is_finite(),
                    "{} went non-finite at tick {tick}: {p:?}",
                    spec.id
                );
                assert!(model.speed_ms().is_finite(), "{} speed went non-finite", spec.id);
                assert!(
                    model.speed_ms() < 1000.0,
                    "{} reached {} m/s at tick {tick}, above the 1.5x Vne backstop",
                    spec.id,
                    model.speed_ms()
                );
            }
        }
    }

    #[test]
    fn roll_is_bounded_and_self_centres() {
        let mut model = trimmed("spitfire-mkix", 3000.0);
        run(&mut model, 600, 0.0, 0.0, 0.0, 0.0);

        for _ in 0..240 {
            model.step(1.0 / 60.0, 0.0, 1.0, 0.0, 0.0);
            assert!(model.roll_deg().abs() <= 80.0 + 1e-3, "roll ran away to {}", model.roll_deg());
        }
        let banked = model.roll_deg().abs();

        for _ in 0..480 {
            model.step(1.0 / 60.0, 0.0, 0.0, 0.0, 0.0);
        }
        assert!(
            model.roll_deg().abs() < banked,
            "releasing roll must reduce bank: {banked} -> {}",
            model.roll_deg()
        );
    }

    #[test]
    fn indicated_airspeed_reads_slower_than_true_airspeed() {
        // The C# had this inverted: it multiplied by sqrt(rho0/rho), returning a
        // FASTER reading at altitude. Indicated airspeed must be lower.
        let model = FlightModel::new(
            AircraftCatalog::get("spitfire-mkix").expect("known id"),
            Vec3::new(0.0, 3000.0, 0.0),
            0.0,
        );
        let tas = model.speed_ms();
        let ias = model.indicated_speed_ms();
        assert!(ias < tas, "IAS must read lower than TAS at altitude: {ias} vs {tas}");

        // Compression-corrected: IAS = TAS * sqrt(rho / rho0).
        let expected = tas * mathf::sqrt(air_density(3000.0) / AIR_DENSITY_SEA_LEVEL);
        assert!((ias - expected).abs() < 1e-2, "got {ias}, expected {expected}");
    }

    #[test]
    fn indicated_airspeed_equals_true_airspeed_at_sea_level() {
        let model = FlightModel::new(
            AircraftCatalog::get("spitfire-mkix").expect("known id"),
            Vec3::new(0.0, 0.0, 0.0),
            0.0,
        );
        // Sea level is where the two definitions coincide.
        assert!((model.indicated_speed_ms() - model.speed_ms()).abs() < 1e-3);
    }

    #[test]
    fn is_aimed_at_respects_its_cone() {
        let model = FlightModel::new(
            AircraftCatalog::get("spitfire-mkix").expect("known id"),
            Vec3::ZERO,
            0.0,
        );
        // Directly ahead.
        assert!(model.is_aimed_at(Vec3::new(0.0, 0.0, 500.0), 6.0));
        // Directly astern.
        assert!(!model.is_aimed_at(Vec3::new(0.0, 0.0, -500.0), 6.0));
        // Degenerate: on top of the target counts as aimed.
        assert!(model.is_aimed_at(Vec3::new(0.0, 0.0, 0.5), 6.0));
    }

    #[test]
    fn lead_angle_is_zero_out_of_range_and_signed_about_up() {
        let model = FlightModel::new(
            AircraftCatalog::get("spitfire-mkix").expect("known id"),
            Vec3::ZERO,
            0.0,
        );
        // Out of range: no lead.
        assert_eq!(model.lead_angle_deg(Vec3::ZERO, Vec3::new(0.0, 0.0, 0.5), 0.5), 0.0);
        // A crossing target to starboard gives a non-zero, finite lead.
        let lead = model.lead_angle_deg(Vec3::new(50.0, 0.0, 0.0), Vec3::new(0.0, 0.0, 800.0), 800.0);
        assert!(lead.is_finite());
        assert!(lead.abs() > 0.5, "a crossing target must need lead, got {lead}");
    }

    #[test]
    fn overstressed_tracks_never_exceed_speed() {
        let mut model = trimmed("bf109-g6", 3000.0);
        assert!(!model.is_overstressed(), "trimmed flight must not be overstressed");
        run(&mut model, 20000, 0.0, 0.0, 0.0, 1.0);
        let vne = model.spec().never_exceed_speed_ms;
        assert!(
            model.speed_ms() <= vne * 1.5 + 1e-3,
            "the 1.5x Vne backstop must hold: {} vs {}",
            model.speed_ms(),
            vne * 1.5
        );
    }
}