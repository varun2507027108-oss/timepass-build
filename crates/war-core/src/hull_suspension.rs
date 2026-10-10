//! Per-contact suspension for a tracked vehicle hull.
//!
//! Rust mirror of `Assets/Scripts/Gameplay/HullSuspension.cs`. The two must be
//! kept in step: the C# side is what the player sees, and this side is what the
//! parity tests compare against, so a divergence here is a physics bug in one of
//! them.
//!
//! A real tank is not a rigid slab resting on the ground. Every road wheel sits on
//! its own spring or torsion unit, and the hull's pitch, roll and ride height are
//! the *emergent* result of how far each of those compresses. Braking dives the
//! nose because the front springs load up. A hard turn rolls the hull outward.
//! Crossing a camber settles the hull onto an average plane instead of perching it
//! on whichever corner happens to be highest.
//!
//! The previous implementation had none of this. The simulation carried a single
//! point position and a single yaw, and the renderer computed a cosmetic tilt from
//! four terrain samples using the MAXIMUM corner height -- rigid-body resting
//! behaviour, which is correct for a slab and wrong for a tank.
//!
//! Model
//! -----
//! Four contact points, each a spring-damper against the terrain:
//!
//! ```text
//! compression_i = groundHeight_i - (hullHeight + contactOffsetY_i) + staticSag
//! F_i           = k * compression_i + c * compressionRate_i        (>= 0)
//! ```
//!
//! A track cannot pull the hull down, so the force is clamped at zero. Those forces
//! drive three degrees of freedom:
//!
//! ```text
//! m  * y'' = SUM(F_i) - m*g
//! Ip * p'' = -SUM(F_i * z_i) + m * a_long * h_cg
//! Ir * r'' = +SUM(F_i * x_i) + m * a_lat  * h_cg
//! ```
//!
//! The signs on the two spring terms are OPPOSITE, and that is not a typo. Pitch
//! is nose-up positive while a contact force is nose-down positive about +X, so
//! the pitch term is negated; roll is right-side-up positive, which is already the
//! sense `SUM(F*x)` resolves in, so it is not. Getting either backwards turns the
//! springs into an energy source and the hull corkscrews to its stops.
//!
//! The last two terms are weight transfer. Acceleration is applied at the contact
//! patch, which sits *below* the centre of gravity, so a horizontal force becomes a
//! pitching or rolling moment. The attitude then feeds back into the contact
//! heights on the next step, which redistributes the spring loads. That feedback
//! loop is the whole reason to model the contacts separately rather than as one
//! rigid body.

use crate::gameplay::get_terrain_height;
use crate::math::mathf;
use crate::vehicle_dynamics::GRAVITY;

/// Number of suspension contacts. Four corners of a track, treated as one unit
/// each -- the individual road wheels are already accounted for by the roll
/// stiffness of the track itself.
const CONTACTS: usize = 4;

/// A tank cannot fold its own hull up. Generous, and stops a single pathological
/// terrain sample from rolling it onto its roof.
const MAX_TILT_RAD: f32 = 40.0 * mathf::PI / 180.0;

#[derive(Debug, Clone)]
pub struct HullSuspension {
    /// Total sprung mass, kg.
    pub mass: f32,

    /// Spring rate per corner, N/m.
    pub spring_rate: f32,

    /// Damping per corner, N.s/m.
    pub spring_damping: f32,

    /// Height of the centre of gravity above the contact plane, metres.
    pub cg_height: f32,

    /// Maximum suspension travel before the bump stop, metres.
    ///
    /// Real suspension has a finite stroke; beyond it the hull sits on the frame.
    /// Without this the vehicle behaves like a linear spring to infinity and a
    /// steep slope flings it into the air.
    pub max_travel: f32,

    /// Static compression per corner at rest, metres.
    pub static_sag: f32,

    /// Roll inertia about the longitudinal axis, kg.m^2.
    pub roll_inertia: f32,

    /// Pitch inertia about the lateral axis, kg.m^2.
    pub pitch_inertia: f32,

    /// Hull attitude, radians. Positive pitch is nose up, positive roll is right side up.
    pub pitch_rad: f32,
    /// Positive roll is right side up.
    pub roll_rad: f32,

    /// Rate of change of the attitude, radians per second.
    pub pitch_velocity: f32,
    pub roll_velocity: f32,

    /// Ride height of the contact plane, metres.
    pub height: f32,
    pub vertical_velocity: f32,

    contact_x: [f32; CONTACTS],
    contact_z: [f32; CONTACTS],
    last_compression: [f32; CONTACTS],
    /// Rest length of each corner, metres. See [`Self::compute_static_preload`].
    rest_sag: [f32; CONTACTS],
    /// Largest damper force a single corner may exert, N.
    max_damping: f32,
}

impl HullSuspension {
    pub fn new(mass_kg: f32, half_gauge: f32, half_track_width: f32, contact_centre_offset_z: f32) -> Self {
        // Inertias follow the vehicle's own proportions rather than being fixed
        // constants, so scaling the hull up scales its rotational response with it
        // rather than leaving a heavy tank as twitchy as a light one.
        let length = half_gauge * 2.0;
        let width = half_track_width * 2.0;
        const HEIGHT_M: f32 = 2.4;

        let spring_rate = 5.5e5_f32;
        let contacts = 4.0_f32;

        let mut s = Self {
            mass: mass_kg,
            spring_rate,
            spring_damping: 4.0e4,
            cg_height: 1.15,
            max_travel: 0.35,
            // How far the hull sags at rest, per corner: the compression that
            // carries a quarter of the vehicle's weight through one spring.
            //
            // This is subtracted out when measuring compression so that the hull's
            // reference plane rests exactly ON the ground rather than a full static
            // sag below it. Without it the whole vehicle sinks ~14 cm at spawn.
            static_sag: mass_kg * GRAVITY / (contacts * spring_rate),
            roll_inertia: mass_kg * (width * width + HEIGHT_M * HEIGHT_M) / 12.0,
            pitch_inertia: mass_kg * (length * length + HEIGHT_M * HEIGHT_M) / 12.0,
            pitch_rad: 0.0,
            roll_rad: 0.0,
            pitch_velocity: 0.0,
            roll_velocity: 0.0,
            height: 0.0,
            vertical_velocity: 0.0,
            contact_x: [-half_track_width, half_track_width, -half_track_width, half_track_width],
            contact_z: [
                half_gauge + contact_centre_offset_z,
                half_gauge + contact_centre_offset_z,
                -half_gauge + contact_centre_offset_z,
                -half_gauge + contact_centre_offset_z,
            ],
            last_compression: [0.0; CONTACTS],
            rest_sag: [0.0; CONTACTS],
            max_damping: 0.0,
        };
        s.compute_static_preload();
        s
    }

    /// Give each corner its own rest length, so a LEVEL hull on LEVEL ground is an
    /// exact equilibrium.
    ///
    /// This is not a fudge factor; it is what makes the model well-posed. With four
    /// identical springs, a level hull on flat ground compresses all four equally, so
    /// all four push equally -- and if the contact patch is not centred under the
    /// centre of gravity, equal pushes do NOT balance:
    ///
    /// ```text
    /// SUM(F * z) = 2*k*z_front - 2*k*z_rear  !=  0
    /// ```
    ///
    /// The vehicle would then have to tilt until the forces differed, which for the
    /// Panzer IV footprint (z_front = +1.47, z_rear = -4.19) parks it at +1.09 degrees
    /// nose-up on dead flat ground. That is an artefact of assuming every spring is
    /// identical, not a real behaviour.
    ///
    /// Real suspensions are set up so the vehicle sits level: the front carries more
    /// load because the centre of gravity sits nearer it, and the springs are preloaded
    /// accordingly. Solving for the load split that balances both equations gives the
    /// per-corner rest lengths below.
    fn compute_static_preload(&mut self) {
        let mut z_front = 0.0_f32;
        let mut z_rear = 0.0_f32;
        let mut have_front = false;
        let mut have_rear = false;
        for i in 0..CONTACTS {
            if self.contact_z[i] > 0.0 {
                if !have_front || self.contact_z[i] > z_front {
                    z_front = self.contact_z[i];
                }
                have_front = true;
            } else if !have_rear || self.contact_z[i] < z_rear {
                z_rear = self.contact_z[i];
                have_rear = true;
            }
        }

        // Degenerate patch, entirely fore or aft of the origin: every corner shares the
        // same load and the uniform value is already correct.
        if !have_front || !have_rear || (z_front - z_rear).abs() < 1e-3 {
            self.rest_sag = [self.static_sag; CONTACTS];
            self.max_damping = self.static_sag * self.spring_rate;
            return;
        }

        // Per side, the two compressions that carry the weight:
        //     c_front + c_rear = S          (sum, from SUM(F) = m*g)
        //     z_f*c_f  + z_r*c_r = 0        (moment about the CG, so no net pitch)
        let side_sum = self.mass * GRAVITY / (2.0 * self.spring_rate);
        let rear_sag = side_sum * z_front / (z_front - z_rear);
        let front_sag = side_sum - rear_sag;

        for i in 0..CONTACTS {
            self.rest_sag[i] = if self.contact_z[i] > 0.0 { front_sag } else { rear_sag };
        }

        // The damping cap is a structural limit, so key it off the largest preload
        // rather than a single averaged figure.
        self.max_damping = front_sag.max(rear_sag) * self.spring_rate;
    }

    /// Snap the hull onto the ground with no velocity, so the vehicle does not drop
    /// in at spawn or after a respawn.
    pub fn reset(&mut self, x: f32, z: f32, heading_rad: f32) {
        let mut sum = 0.0;
        for i in 0..CONTACTS {
            sum += self.ground_at(i, x, z, heading_rad);
        }
        self.height = sum * 0.25;
        self.pitch_rad = 0.0;
        self.roll_rad = 0.0;
        self.pitch_velocity = 0.0;
        self.roll_velocity = 0.0;
        self.vertical_velocity = 0.0;

        // Seed each damper with the compression it is actually sitting at.
        //
        // Leaving these at zero makes the very first step see a compression rate of
        // static_sag/dt -- roughly fifteen metres per second -- so every damper
        // delivers its capped maximum impulse on frame one and the hull is kicked
        // off the ground as it spawns. The rate is a DIFFERENCE against the
        // previous step, so the previous step has to start from where this one is.
        for i in 0..CONTACTS {
            self.last_compression[i] = self.ground_at(i, x, z, heading_rad) - self.height + self.rest_sag[i];
        }
    }

    /// Advance the suspension by one fixed step.
    pub fn step(
        &mut self,
        dt: f32,
        x: f32,
        z: f32,
        heading_rad: f32,
        accel_long: f32,
        accel_lat: f32,
    ) {
        let mut total_force = 0.0_f32;
        let mut pitch_moment = 0.0_f32;
        let mut roll_moment = 0.0_f32;

        for i in 0..CONTACTS {
            // The contact sits at (ox, 0, oz) in the hull frame: on the ground
            // plane, offset fore/aft and side to side. Rotating it by the hull
            // attitude gives the vertical offset that decides how compressed that
            // corner is.
            //
            // Roll first, about the forward axis:
            //     x' = ox*cos(r),  y' = ox*sin(r),  z' = oz
            // Then pitch, about the lateral axis:
            //     y'' = y'*cos(p) - z'*sin(p)
            // With no attitude this is exactly zero, which is what makes a level
            // hull on flat ground sit still instead of launching itself.
            let ox = self.contact_x[i];
            let oz = self.contact_z[i];

            let ry = ox * mathf::sin(self.roll_rad);
            let local_y = ry * mathf::cos(self.pitch_rad) - oz * mathf::sin(self.pitch_rad);

            let (off_x, off_z) = self.contact_world(i, heading_rad);
            let ground_y = get_terrain_height(x + off_x, z + off_z);

            // Compression is how far the hull has SUNK BELOW the contact, so it is
            // ground minus hull. Writing it the other way round gives an inverted
            // spring -- rise, compress further, push harder -- and the vehicle
            // launches itself within a second.
            let mut compression = ground_y - (self.height + local_y) + self.rest_sag[i];

            // Finite travel. Beyond it the frame sits on a bump stop.
            compression = mathf::clamp(compression, -self.max_travel, self.max_travel);

            // Damping from this corner's own compression rate. Using a single
            // shared rate would let one corner's motion bleed into the others and
            // damp the hull against a motion it is not making.
            let rate = (compression - self.last_compression[i]) / f32::max(dt, 1e-5);
            self.last_compression[i] = compression;

            let mut damping_force = self.spring_damping * rate;
            // The damping term is the stiffest thing in this model. Writing it as
            // c * deltaCompression / dt makes its effective stiffness c/dt, which at
            // 30 Hz is more than DOUBLE the spring rate. Explicitly integrating that
            // is unconditionally unstable: one step of corner compression produces
            // a larger impulse than the spring can absorb and the hull oscillates to
            // its stops within a second.
            //
            // NOTE the sign. Damping ADDS to the spring force while the damper is
            // extending. For a mass on a spring, M*y'' = -k*y - c*y'; substituting
            // compression = -y gives the upward force as k*compression +
            // c*d(compression)/dt. Written as a minus, the damper is negative
            // damping: it pushes the hull onward every time the suspension extends.
            let max_damping = self.max_damping;
            damping_force = mathf::clamp(damping_force, -max_damping, max_damping);

            let mut force = self.spring_rate * compression + damping_force;
            // A track cannot pull the hull down.
            if force < 0.0 {
                force = 0.0;
            }
            // And the bump stop cannot push past its limit either.
            force = f32::min(force, self.spring_rate * self.max_travel * 1.5);

            total_force += force;
            pitch_moment -= force * self.contact_z[i];
            roll_moment += force * self.contact_x[i];
        }

        // Weight transfer: the lateral and longitudinal forces act at the contact
        // patch, which sits below the centre of gravity. A vehicle accelerating
        // forward therefore pitches nose-up, braking pitches nose-down, and a
        // right-hand turn rolls the hull to the left, away from the turn.
        pitch_moment += self.mass * accel_long * self.cg_height;
        roll_moment += self.mass * accel_lat * self.cg_height;

        // --- integrate (semi-implicit Euler) -------------------------------
        let accel_y = (total_force - self.mass * GRAVITY) / self.mass;
        self.vertical_velocity += accel_y * dt;
        self.height += self.vertical_velocity * dt;

        self.pitch_velocity += (pitch_moment / f32::max(self.pitch_inertia, 1.0)) * dt;
        self.roll_velocity += (roll_moment / f32::max(self.roll_inertia, 1.0)) * dt;
        self.pitch_rad += self.pitch_velocity * dt;
        self.roll_rad += self.roll_velocity * dt;

        // --- stabilise -----------------------------------------------------
        // Real suspension is far more heavily damped than an explicit integrator
        // captures at 30 Hz. Without this the hull rings at the integrator's own
        // frequency instead of settling.
        self.vertical_velocity *= 0.94;
        self.pitch_velocity *= 0.88;
        self.roll_velocity *= 0.88;

        self.pitch_rad = clamp_angle(self.pitch_rad, &mut self.pitch_velocity, MAX_TILT_RAD);
        self.roll_rad = clamp_angle(self.roll_rad, &mut self.roll_velocity, MAX_TILT_RAD);

        // Deliberately NO hard floor snapping the hull up to the highest corner.
        //
        // That was the original bug: a rigid slab cannot pass through the ground,
        // so it teleports to sit on whichever corner is highest. Here it fired the
        // hull more than two metres into the air every time the tank met a ledge.
        //
        // Non-penetration is already enforced, and enforced correctly, by the bump
        // stops: a corner driven below ground compresses, saturates at 1.5*k*travel,
        // and four of those exceed the vehicle's weight by a factor of three, so the
        // hull is always pushed back out. That is the same physical constraint,
        // expressed as a force rather than a position edit, which is why the vehicle
        // can now ride UP onto an obstacle instead of teleporting onto it.
    }

    fn contact_world(&self, index: usize, heading_rad: f32) -> (f32, f32) {
        let cy = mathf::cos(heading_rad);
        let sy = mathf::sin(heading_rad);
        (
            self.contact_x[index] * cy + self.contact_z[index] * sy,
            -self.contact_x[index] * sy + self.contact_z[index] * cy,
        )
    }

    fn ground_at(&self, index: usize, x: f32, z: f32, heading_rad: f32) -> f32 {
        let (off_x, off_z) = self.contact_world(index, heading_rad);
        get_terrain_height(x + off_x, z + off_z)
    }
}

fn clamp_angle(angle: f32, velocity: &mut f32, limit: f32) -> f32 {
    if angle > limit {
        if *velocity > 0.0 {
            *velocity = 0.0;
        }
        limit
    } else if angle < -limit {
        if *velocity < 0.0 {
            *velocity = 0.0;
        }
        -limit
    } else {
        angle
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::vehicle_dynamics::{HALF_GAUGE_M, HALF_TRACK_WIDTH_M};

    const STEP: f32 = 1.0 / 30.0;

    fn new_hull() -> HullSuspension {
        // The footprint of the vehicle that actually ships, at the scale the bootstrap
        // renders it. Using the generic wheelbase constants here would test a 3.4 m
        // contact patch on a 9 m tank and prove nothing about the thing the player sees.
        const PLAYER_MASS_KG: f32 = 25000.0;
        HullSuspension::new(
            PLAYER_MASS_KG,
            crate::vehicle_dynamics::contact_half_length_m(PLAYER_MASS_KG),
            crate::vehicle_dynamics::contact_half_width_m(PLAYER_MASS_KG),
            crate::vehicle_dynamics::contact_centre_z_m(PLAYER_MASS_KG),
        )
    }

    fn settle(s: &mut HullSuspension, steps: usize, x: f32, z: f32) {
        for _ in 0..steps {
            s.step(STEP, x, z, 0.0, 0.0, 0.0);
        }
    }

    fn deg(rad: f32) -> f32 {
        rad * 180.0 / mathf::PI
    }

    #[test]
    fn resting_on_flat_ground_sits_level_and_on_the_surface() {
        let mut s = new_hull();
        s.reset(0.0, 0.0, 0.0);
        settle(&mut s, 120, 0.0, 0.0);

        let ground = get_terrain_height(0.0, 0.0);
        assert!(s.vertical_velocity.abs() < 1e-3, "hull never came to rest");
        assert!(deg(s.pitch_rad).abs() < 0.5, "hull should be level on flat ground");
        assert!(deg(s.roll_rad).abs() < 0.5, "hull should be level on flat ground");
        // The contact plane must rest ON the ground, not a full static sag below it.
        assert!((s.height - ground).abs() < 0.25, "hull settled away from the surface");
    }

    #[test]
    fn on_slope_settles_between_corners_not_on_the_highest() {
        let mut s = new_hull();
        let (x, z) = (300.0_f32, 0.0_f32);
        s.reset(x, z, 0.0);
        settle(&mut s, 240, x, z);

        let corners: Vec<f32> = [-1.0_f32, 1.0]
            .iter()
            .flat_map(|sx| {
                [-1.0_f32, 1.0].iter().map(move |sz| {
                    get_terrain_height(
                        x + sz * HALF_GAUGE_M,
                        z + sx * HALF_TRACK_WIDTH_M,
                    )
                })
            })
            .collect();

        let highest = corners.iter().cloned().fold(f32::MIN, f32::max);
        let lowest = corners.iter().cloned().fold(f32::MAX, f32::min);
        let average: f32 = corners.iter().sum::<f32>() * 0.25;

        // Sanity: this spot is actually sloped, or the test proves nothing.
        assert!(highest - lowest > 0.5, "test location is not sloped");

        // Perching on the highest corner is the rigid-slab bug this replaces.
        assert!(s.height < highest - 0.05, "hull is perched on the highest corner");
        assert!(s.height > lowest - 0.75, "hull has fallen through the terrain");
        assert!(
            (s.height - average).abs() < 0.5,
            "hull should settle near the average of its contacts"
        );
    }

    #[test]
    fn emergency_braking_pitches_nose_down() {
        let mut s = new_hull();
        s.reset(0.0, 0.0, 0.0);
        settle(&mut s, 60, 0.0, 0.0);
        for _ in 0..60 {
            s.step(STEP, 0.0, 0.0, 0.0, -12.0, 0.0);
        }
        let p = deg(s.pitch_rad);
        // Pitch stiffness scales with the SQUARE of the contact span, so the static
        // attitude is `sin(p) = M / (k * L^2)`. With the real Panzer IV footprint
        // (span 5.67 m) and a 12 m/s^2 stop, that works out near 1.1 degrees; the old
        // 3.4 m generic footprint gave 3.5 degrees. A tank barely dives under braking
        // -- the dramatic nose-dive is a car characteristic -- so a small angle is the
        // correct answer, not a regression.
        assert!(p < -0.5, "braking must dive the nose (got {p})");
        assert!(p > -2.5, "braking dive is implausibly deep (got {p})");
    }

    #[test]
    fn acceleration_pitches_nose_up() {
        let mut s = new_hull();
        s.reset(0.0, 0.0, 0.0);
        settle(&mut s, 60, 0.0, 0.0);
        for _ in 0..60 {
            s.step(STEP, 0.0, 0.0, 0.0, 12.0, 0.0);
        }
        let p = deg(s.pitch_rad);
        assert!(p > 0.5, "accelerating must squat the nose (got {p})");
        assert!(p < 2.5, "squat is implausibly deep (got {p})");
    }

    #[test]
    fn cornering_rolls_hull_onto_its_outer_suspension() {
        let mut s = new_hull();
        s.reset(0.0, 0.0, 0.0);
        settle(&mut s, 60, 0.0, 0.0);
        // Positive lateral acceleration is a right-hand turn: the vehicle
        // accelerates toward its own right, so it leans LEFT and the right side
        // rises, which is positive roll.
        for _ in 0..60 {
            s.step(STEP, 0.0, 0.0, 0.0, 0.0, 12.0);
        }
        assert!(deg(s.roll_rad) > 1.5, "cornering must roll the hull outward");
        assert!(deg(s.roll_rad) < 12.0, "body roll is implausibly deep");
    }

    #[test]
    fn sustained_driving_stays_glued_to_the_terrain() {
        let mut s = new_hull();
        let mut x = 300.0_f32;
        let z = 0.0_f32;
        s.reset(x, z, 0.0);

        const SPEED: f32 = 8.0; // ~29 km/h cross-country
        let mut peak = -99.0_f32;
        let mut deepest = 99.0_f32;

        for _ in 0..900 {
            // 30 seconds at 30 Hz
            x += SPEED / 30.0;
            s.step(STEP, x, z, 0.0, 0.5, 0.2);
            let gap = s.height - get_terrain_height(x, z);
            peak = peak.max(gap);
            deepest = deepest.min(gap);
        }

        // These are the assertions that catch the original bug: the rigid
        // max-corner floor put the hull over two metres into the air on this path.
        assert!(peak < 0.5, "hull left the ground (peak {peak})");
        // The lower bound is generous because the terrain here is genuinely violent --
        // 2.15 m of height change across one 3.4 m contact spacing, which is a 32 degree
        // ledge. Suspension travel is 0.35 m, so the hull cannot absorb that and its
        // attitude is dominated by the ground, not by the springs. The old 3.4 m
        // footprint kept the hull centred and hid this; the real 5.67 m footprint lets
        // the hull genuinely follow the terrain, which is the point.
        assert!(deepest > -0.9, "hull sank through the terrain ({deepest})");
        assert!(s.height.is_finite(), "suspension went non-finite");
    }

    #[test]
    fn violent_input_remains_finite_and_bounded() {
        let mut s = new_hull();
        s.reset(300.0, 0.0, 0.0);

        for i in 0..3000 {
            let t = i as f32 * STEP;
            s.step(
                STEP,
                300.0 + mathf::sin(t * 3.1) * 60.0,
                mathf::cos(t * 2.3) * 60.0,
                t * 2.0,
                mathf::sin(t) * 40.0,
                mathf::cos(t * 1.7) * 40.0,
            );
        }

        assert!(s.height.is_finite(), "height went non-finite");
        assert!(s.pitch_rad.is_finite(), "pitch went non-finite");
        assert!(s.roll_rad.is_finite(), "roll went non-finite");
        assert!(deg(s.pitch_rad).abs() < 45.0, "pitch escaped its limits");
        assert!(deg(s.roll_rad).abs() < 45.0, "roll escaped its limits");
    }

    #[test]
    fn respawn_does_not_kick_the_hull() {
        // Reset must seed each damper with the compression it is already sitting
        // at. Leaving the history at zero makes the first step see a compression
        // rate of static_sag/dt, roughly 15 m/s, and every damper delivers its full
        // impulse on frame one.
        let mut s = new_hull();
        s.reset(0.0, 0.0, 0.0);
        s.step(STEP, 0.0, 0.0, 0.0, 0.0, 0.0);

        assert!(s.vertical_velocity.abs() < 0.2, "respawn threw the hull");
        assert!(deg(s.pitch_rad).abs() < 2.0, "respawn pitched the hull");
    }
}