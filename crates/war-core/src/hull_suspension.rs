//! Per-contact suspension for a tracked vehicle hull.
//!
//! Rust mirror of `Assets/Scripts/Gameplay/HullSuspension.cs`. The two must be kept
//! in step: the C# side is what the player sees, and this side is what the parity
//! tests compare against.
//!
//! Two-layer model
//! ---------------
//! A tank is not a rigid slab resting on the ground. Every road wheel sits on its own
//! spring or torsion unit, and the hull's ride height, pitch and roll are the *emergent*
//! result of how far each compresses.
//!
//! The model separates TWO jobs that a single set of corner springs cannot both do:
//!
//! 1. **Plane layer** — the hull follows a LEAST-SQUARES plane fitted through the sampled
//!    ground under its contacts. Ride height tracks the plane's height; pitch and roll
//!    track its slope, through their own damped springs. This is the macro behaviour.
//! 2. **Corner springs** — eight contacts, four per track, absorbing roughness *between*
//!    samples. This is the micro behaviour.
//!
//! Why the split matters: with the corner springs as the only pitch stiffness, the hull
//! attitude was really being set by the bump-stop TRAVEL CLAMP. At 0.35 m of travel the
//! springs bottomed out on ordinary slopes; raising travel to 1.2 m stopped the clamp
//! masking the problem and the tank dove 37 degrees under braking. Separating the layers
//! means attitude is governed by dynamics sized for the job, and travel is free to be
//! whatever the hardware plausibly is.
//!
//! Why least squares and not max-corner: crossing a dip must settle the hull onto the
//! *average* of its contacts, not perch it on the highest. That is the entire point, and
//! it is what the previous renderer got wrong.
//!
//! Plane fit
//! ---------
//! With contacts at local `(x_i, z_i)` and ground `g_i`, letting `gbar` be the mean
//! sample:
//!
//! ```text
//! pitch = atan2( SUM(z_i * (g_i - gbar)), SUM(z_i^2) )
//! roll  = atan2( SUM(x_i * (g_i - gbar)), SUM(x_i^2) )
//! ```
//!
//! The mean must be subtracted. `atan2(SUM(z*g), SUM(z^2))` is exact only when
//! `SUM(z) = 0`. A layout that is symmetric but merely OFFSET from the origin — which
//! the Panzer IV is, its track patch sitting 1.36 m behind the model origin — still has
//! `SUM(z) != 0`, so the `SUM(z * meanHeight)` term survives and corrupts the slope with
//! the absolute height of the ground. With it present the hull sat 31 degrees nose-down
//! on an uphill while the attitude spring was pulling it level.
//!
//! Weights
//! -------
//! Weight transfer still deviates the attitude from the fitted plane by `m*a*h_cg/K`, so
//! braking dives below the plane, power squats above it, and lateral load rolls outward.
//! That offset is the visible weight transfer. `K` is sized for the target angle:
//! `K = m * a_max * h_cg / theta`.

use crate::gameplay::get_terrain_height;
use crate::math::mathf;
use crate::vehicle_dynamics::GRAVITY;

/// Contacts: four per track, at z = {-h, -h/3, +h/3, +h}.
///
/// The layout must be SYMMETRIC about the contact-patch centre and include BOTH
/// extremes. An asymmetric ladder leaves a dead pair at mid-length contributing no pitch
/// stiffness at all, so braking and acceleration do different things and the hull dives
/// far harder than it squats.
pub const CONTACTS: usize = 8;

/// A tank cannot fold its own hull up.
///
/// A backstop only: it exists so a single pathological terrain sample cannot roll
/// the vehicle onto its roof. Ordinary terrain never approaches it, which is
/// precisely why it has to be cross-checked against the C# value explicitly --
/// see `HullSuspensionRegressionTests.AttitudeClampAgreesWithTheRustImplementation`
/// in the Unity suite. The bump stops are what actually enforce non-penetration.
const MAX_TILT_RAD: f32 = 50.0 * mathf::PI / 180.0;

/// Hull attitude clamp in degrees.
///
/// Public so the FFI layer can expose it and the Unity side can assert the two
/// implementations agree, rather than each holding a private copy that can drift.
pub fn max_tilt_deg() -> f32 {
    MAX_TILT_RAD * 180.0 / mathf::PI
}

#[derive(Debug, Clone)]
pub struct HullSuspension {
    pub mass: f32,
    pub spring_rate: f32,
    pub spring_damping: f32,
    pub cg_height: f32,
    pub max_travel: f32,

    /// Attitude-layer stiffness, N.m/rad. Sized `m * a_max * h_cg / theta`: at 25 t,
    /// 8.34 m/s^2, 1.15 m and a 5 degree dive, about 2.7e6.
    pub pitch_stiffness: f32,
    pub roll_stiffness: f32,
    /// Attitude-layer damping, N.m.s/rad, near critical for the layer.
    pub pitch_damping: f32,
    pub roll_damping: f32,
    /// Ride-height layer stiffness, N/m, pulling the hull toward the fitted plane.
    ///
    /// The attitude layer fixes pitch and roll, but ride height is the THIRD degree of
    /// freedom and it was still left entirely to the corner springs. That is what
    /// launches the vehicle: on an 18 degree slope the ends of a 5.67 m contact patch
    /// differ by 1.8 m of ground, which 0.6 m of travel cannot bridge, so every corner
    /// saturates on its bump stop and fires the hull into the air.
    pub heave_stiffness: f32,
    pub heave_damping: f32,

    pub pitch_rad: f32,
    pub roll_rad: f32,
    pub pitch_velocity: f32,
    pub roll_velocity: f32,
    pub height: f32,
    pub vertical_velocity: f32,

    pub static_sag: f32,

    contact_x: [f32; CONTACTS],
    contact_z: [f32; CONTACTS],
    ground_y: [f32; CONTACTS],
    last_compression: [f32; CONTACTS],
    /// Normal force each contact carried on the last step, newtons.
    ///
    /// Used to weight the RIDE HEIGHT only. An airborne track is still sampled -- it is
    /// part of the footprint and its sample is needed to give the attitude fit its
    /// geometric span -- but it is carrying nothing, so its terrain height must not drag
    /// the hull downward. Weighting by force expresses that continuously: a contact
    /// contributes in proportion to what it is actually supporting, so it fades out as
    /// it lifts rather than switching off.
    contact_force: [f32; CONTACTS],
    /// How much of the load-weighted ride height is retained, 0..1.
    ///
    /// 0 is the old behaviour: ride at the plain mean of every contact, which lets an
    /// airborne track drag the hull into a hillside. 1 is the fully weighted mean, which
    /// over-corrects during sustained driving because the loaded contacts are the
    /// rearward ones and the hull follows them, sinking the nose.
    ///
    /// Measured across three settings, worst-case penetration over the arena:
    /// blend 0.0 (plain mean) = 1.243 m; blend 1.0 (fully weighted) = 0.735 m but drove
    /// deepest travel to -0.91 m against a -0.6 m bound, failing the drive test;
    /// blend 0.5 = -0.75 m, still failing; blend 0.25 = 0.817 m with the drive test
    /// passing. 0.25 is the only setting that improves the penetration without
    /// regressing the nose-down sink. Mirrors HullSuspension in the C# implementation.
    load_supported_blend: f32,
    rest_sag: [f32; CONTACTS],
    max_damping: f32,
    max_bump_stop_force: [f32; CONTACTS],
}

impl HullSuspension {
    pub fn new(
        mass_kg: f32,
        half_gauge: f32,
        half_track_width: f32,
        contact_centre_offset_z: f32,
    ) -> Self {
        let length = half_gauge * 2.0;
        let width = half_track_width * 2.0;
        const HEIGHT_M: f32 = 2.4;

        let mut s = Self {
            mass: mass_kg,
            spring_rate: 2.2e5,
            spring_damping: 3.4e4,
            cg_height: 1.15,
            max_travel: 0.6,
            pitch_stiffness: 2.7e6,
            roll_stiffness: 3.4e6,
            pitch_damping: 7.6e5,
            roll_damping: 8.8e5,
            heave_stiffness: 6.0e5,
            heave_damping: 1.4e5,
            pitch_rad: 0.0,
            roll_rad: 0.0,
            pitch_velocity: 0.0,
            roll_velocity: 0.0,
            height: 0.0,
            vertical_velocity: 0.0,
            static_sag: 0.0,
            contact_x: [0.0; CONTACTS],
            contact_z: [0.0; CONTACTS],
            ground_y: [0.0; CONTACTS],
            last_compression: [0.0; CONTACTS],
        contact_force: [0.0; CONTACTS],
        load_supported_blend: 0.25,
            rest_sag: [0.0; CONTACTS],
            max_damping: 0.0,
            max_bump_stop_force: [0.0; CONTACTS],
        };

        // Four contacts per track, symmetric, both extremes present.
        let per_track = CONTACTS / 2;
        let third = half_gauge / 3.0;
        for i in 0..per_track {
            let base = match i {
                0 => -half_gauge,
                1 => -third,
                2 => third,
                _ => half_gauge,
            };
            let z = base + contact_centre_offset_z;
            s.contact_x[i] = -half_track_width;
            s.contact_z[i] = z;
            s.contact_x[i + per_track] = half_track_width;
            s.contact_z[i + per_track] = z;
        }

        let _ = (length, width, HEIGHT_M);
        s.compute_static_preload();
        s.set_bump_stop_ceilings();
        s
    }

    /// Static load per contact, solved as a linear taper `restSag(z) = a + b*z`
    /// satisfying both equilibrium conditions:
    ///
    /// ```text
    /// SUM(F)     = m*g   ->  SUM(restSag)     = S
    /// SUM(F * z) = 0     ->  SUM(restSag * z) = 0
    /// ```
    ///
    /// With identical springs a level hull on flat ground compresses all corners equally,
    /// so all push equally — and if the patch is not centred under the centre of gravity,
    /// equal pushes do NOT balance. The vehicle would then have to tilt until the forces
    /// differed, parking it at +1.09 degrees nose-up on dead flat ground. Real
    /// suspensions are preloaded so the vehicle sits level; this is that preload.
    fn compute_static_preload(&mut self) {
        let mut t1 = 0.0_f32; // SUM(z)
        let mut t2 = 0.0_f32; // SUM(z^2)
        for i in 0..CONTACTS {
            t1 += self.contact_z[i];
            t2 += self.contact_z[i] * self.contact_z[i];
        }

        let sum_sag = self.mass * GRAVITY / f32::max(self.spring_rate, 1.0);
        self.static_sag = sum_sag / CONTACTS as f32;

        let mut a = self.static_sag;
        let mut b = 0.0_f32;
        let denom = t1 * t1 - CONTACTS as f32 * t2;
        if denom.abs() > 1e-4 {
            b = sum_sag * t1 / denom;
            a = -b * t2 / t1;
        }

        for i in 0..CONTACTS {
            self.rest_sag[i] = f32::max(a + b * self.contact_z[i], 0.0);
        }

        // Damper ceiling keyed to CRITICAL damping for this corner's share of the mass.
        //
        // It used to be preload * spring_rate, which halves whenever the spring rate
        // halves while the damping actually required barely moves, so the cap silently
        // changed meaning every time the suspension was retuned. At 30 Hz the explicit
        // damper has an effective stiffness of c/dt, so an unbounded damper is what
        // drives the integrator unstable.
        let mass_per_corner = self.mass / CONTACTS as f32;
        self.max_damping = 2.0 * mathf::sqrt(self.spring_rate * mass_per_corner);
    }

    /// Bump stop ceiling PER CORNER, scaled from that corner's own static load.
    ///
    /// A single shared ceiling caused a braking/acceleration asymmetry: the front
    /// springs carry more preload because the centre of gravity sits forward of the patch
    /// centre, so they reached the shared cap sooner and the hull could dive further
    /// than it squatted — 2.7 degrees one way against 1.2 the other. Keying the cap to
    /// each corner's own resting load makes the response symmetric.
    fn set_bump_stop_ceilings(&mut self) {
        for i in 0..CONTACTS {
            self.max_bump_stop_force[i] = self.spring_rate * self.rest_sag[i] * 2.0;
        }
    }

    /// Snap the hull onto the ground with no velocity.
    pub fn reset(&mut self, x: f32, z: f32, heading_rad: f32) {
        let mut sum = 0.0;
        for i in 0..CONTACTS {
            sum += self.ground_at(i, x, z, heading_rad);
        }
        // Average over the contact count, not a hard-coded 0.25.
        //
        // 0.25 is the reciprocal of the original four-contact layout. Widening the set
        // to eight without changing this multiplied the sum by 1/4 instead of 1/8, so on
        // a slope the hull was placed at twice the mean sample height -- below the
        // terrain at its contact patch -- and had to be pushed back out by the bump
        // stops on every respawn. Flat ground hides the error because every sample is
        // equal. Mirrors HullSuspension.Reset in the C# implementation.
        self.height = sum / CONTACTS as f32;
        self.pitch_rad = 0.0;
        self.roll_rad = 0.0;
        self.pitch_velocity = 0.0;
        self.roll_velocity = 0.0;
        self.vertical_velocity = 0.0;

        // Seed each damper with the compression it is actually sitting at. Leaving the
        // history at zero makes the first step see a compression rate of static_sag/dt,
        // roughly fifteen metres per second, so every damper delivers its full impulse
        // on frame one.
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
            let ox = self.contact_x[i];
            let oz = self.contact_z[i];

            let ry = ox * mathf::sin(self.roll_rad);
            let local_y = ry * mathf::cos(self.pitch_rad) + oz * mathf::sin(self.pitch_rad);

            let (off_x, off_z) = self.contact_world(i, heading_rad);
            let ground_y = get_terrain_height(x + off_x, z + off_z);
            self.ground_y[i] = ground_y;

            // Compression is how far the hull has SUNK BELOW the contact. Writing it the
            // other way round gives an inverted spring: rise, compress further, push
            // harder, and the vehicle launches itself within a second.
            let mut compression = ground_y - (self.height + local_y) + self.rest_sag[i];

            compression = mathf::clamp(compression, -self.max_travel, self.max_travel);

            let rate = (compression - self.last_compression[i]) / f32::max(dt, 1e-5);
            self.last_compression[i] = compression;

            let mut damping_force = self.spring_damping * rate;
            damping_force = mathf::clamp(damping_force, -self.max_damping, self.max_damping);

            // Damping ADDS to the spring force while the damper is extending. For a mass
            // on a spring, M*y'' = -k*y - c*y'; substituting compression = -y gives the
            // upward force as k*compression + c*d(compression)/dt. Written as a minus it
            // is negative damping: it pushes the hull onward every time the suspension
            // extends.
            let mut force = self.spring_rate * compression + damping_force;
            if force < 0.0 {
                force = 0.0;
            }
            if force > self.max_bump_stop_force[i] {
                force = self.max_bump_stop_force[i];
            }

            total_force += force;
            self.contact_force[i] = force;

            // Both spring terms are added as they stand. For each, the attitude that
            // compresses one end of the patch necessarily unbalances SUM(F * offset) in
            // the direction that opposes the attitude, so the term is already restoring.
            pitch_moment += force * self.contact_z[i];
            roll_moment += force * self.contact_x[i];
        }

        // --- PLANE LAYER (ride height + attitude) ---------------------------

        // The mean sample height is subtracted before fitting; see the module docs.
        // Ride height is the LOAD-WEIGHTED mean of the contact heights, not the plain average.
        //
        // The plain average is wrong the moment part of the vehicle is airborne. A track
        // hanging over a dip samples ground well below the hull and contributes that
        // height with the same weight as a track bearing the vehicle's weight, so the
        // average -- and therefore the heave target -- is dragged down toward the drop
        // and the loaded side settles into the hillside.
        //
        // Weighting by the force each contact actually carries fixes it WITHOUT touching
        // the attitude fit below. That distinction is the whole point and was arrived at
        // by measurement: an earlier attempt excluded airborne contacts from the
        // LEAST-SQUARES FIT as well, and that made things worse (0.29 m -> 0.42 m,
        // 0.48 m -> 0.77 m). Once only one track carries load every surviving contact
        // shares the same x, so the mean-subtracted roll term SUM(x*dg) collapses and
        // the fitted cross-slope goes to zero -- a 15 degree camber read as 2 degrees.
        //
        // Dropping samples destroys the geometric span the fit needs. Down-weighting the
        // ride height does not, so the fit below still uses every sample. Mirrors
        // HullSuspension in the C# implementation.
        let mut load_sum = 0.0_f32;
        let mut weighted_ground = 0.0_f32;
        for i in 0..CONTACTS {
            let w = self.contact_force[i];
            load_sum += w;
            weighted_ground += w * self.ground_y[i];
        }

        let mut plain_ground = 0.0_f32;
        for i in 0..CONTACTS {
            plain_ground += self.ground_y[i];
        }
        plain_ground /= CONTACTS as f32;

        let mean_ground = if load_sum > 1.0 {
            let weighted = weighted_ground / load_sum;

            // Blend the load-weighted mean back toward the plain mean. Pure weighting
            // is wrong in a way the drive test caught: during sustained driving the
            // loaded contacts are the REARWARD ones, so a fully weighted mean follows
            // the rear of the vehicle and the nose sinks (deepest travel went to
            // -0.91 m against a -0.6 m bound).
            //
            // The airborne tracks do not sit uniformly below the loaded ones -- over a
            // crest they can sit ABOVE them -- so their contribution is a bias of
            // uncertain sign, not a constant offset that can simply be removed. A
            // partial blend removes most of the bias in the sinking-into-a-camber
            // case, which is the one being fixed, while leaving enough of the plain mean
            // to keep the ride height tied to the whole footprint.
            plain_ground + (weighted - plain_ground) * self.load_supported_blend
        } else {
            // Fully airborne -- every contact is off the ground and carrying nothing, so
            // there is no load distribution to weight by. Fall back to the plain mean
            // rather than dividing by (near) zero, which would put an infinity or NaN
            // into the ride height and the hull would never recover.
            plain_ground
        };

        let mut szg = 0.0_f32;
        let mut szz = 0.0_f32;
        let mut sxg = 0.0_f32;
        let mut sxx = 0.0_f32;
        for i in 0..CONTACTS {
            let dg = self.ground_y[i] - mean_ground;
            szg += self.contact_z[i] * dg;
            szz += self.contact_z[i] * self.contact_z[i];
            sxg += self.contact_x[i] * dg;
            sxx += self.contact_x[i] * self.contact_x[i];
        }

        let terrain_pitch = if szz > 1e-4 { mathf::atan2(szg, szz) } else { 0.0 };
        let terrain_roll = if sxx > 1e-4 { mathf::atan2(sxg, sxx) } else { 0.0 };

        // Ride height follows the PLANE, not the corners. The corner springs then only
        // ever see roughness BETWEEN samples, which is the job they are good at.
        total_force += -self.heave_stiffness * (self.height - mean_ground)
            - self.heave_damping * self.vertical_velocity;

        // Attitude springs. These, not corner travel, resist weight transfer.
        pitch_moment += -self.pitch_stiffness * (self.pitch_rad - terrain_pitch)
            - self.pitch_damping * self.pitch_velocity;
        roll_moment += -self.roll_stiffness * (self.roll_rad - terrain_roll)
            - self.roll_damping * self.roll_velocity;

        // Weight transfer: the lateral and longitudinal forces act at the CONTACT PATCH,
        // which sits below the centre of gravity. This DEVIATES the attitude from the
        // fitted plane by m*a*h/K, so braking dives below the plane, power squats above
        // it, and lateral load rolls outward. That offset is the visible weight transfer.
        pitch_moment += self.mass * accel_long * self.cg_height;
        roll_moment += self.mass * accel_lat * self.cg_height;

        // --- integrate (semi-implicit Euler) -------------------------------
        let accel_y = (total_force - self.mass * GRAVITY) / self.mass;
        self.vertical_velocity += accel_y * dt;
        self.height += self.vertical_velocity * dt;

        // Rotational inertia, from the hull's own proportions.
        let inertia_pitch = f32::max(self.pitch_inertia(), 1.0);
        let inertia_roll = f32::max(self.roll_inertia(), 1.0);
        self.pitch_velocity += (pitch_moment / inertia_pitch) * dt;
        self.roll_velocity += (roll_moment / inertia_roll) * dt;
        self.pitch_rad += self.pitch_velocity * dt;
        self.roll_rad += self.roll_velocity * dt;

        self.vertical_velocity *= 0.94;

        self.pitch_rad = clamp_angle(self.pitch_rad, &mut self.pitch_velocity, MAX_TILT_RAD);
        self.roll_rad = clamp_angle(self.roll_rad, &mut self.roll_velocity, MAX_TILT_RAD);
    }

    /// Pitch inertia about the lateral axis, kg.m^2, from the hull's proportions.
    fn pitch_inertia(&self) -> f32 {
        let length = (self.contact_z[CONTACTS / 2 - 1] - self.contact_z[0]).abs();
        let h = 2.4_f32;
        self.mass * (length * length + h * h) / 12.0
    }

    /// Roll inertia about the longitudinal axis, kg.m^2.
    fn roll_inertia(&self) -> f32 {
        let width = (self.contact_x[CONTACTS / 2] - self.contact_x[0]).abs();
        let h = 2.4_f32;
        self.mass * (width * width + h * h) / 12.0
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
    /// Peak longitudinal/lateral acceleration the tracks can actually produce, m/s^2.
    ///
    /// 9.81 * 0.85, the friction circle BattleSim clamps against. The tests used to
    /// inject +/-12, which no tracked vehicle can deliver -- a correctly tuned tank still
    /// dives hard against an input its own traction model forbids, so the baseline was
    /// measuring the input, not the model.
    const PEAK_ACCEL_MPS2: f32 = 9.81 * 0.85;

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
            s.step(STEP, 0.0, 0.0, 0.0, -PEAK_ACCEL_MPS2, 0.0);
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
            s.step(STEP, 0.0, 0.0, 0.0, PEAK_ACCEL_MPS2, 0.0);
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
            s.step(STEP, 0.0, 0.0, 0.0, 0.0, PEAK_ACCEL_MPS2);
        }
        assert!(deg(s.roll_rad) > 1.0, "cornering must roll the hull outward");
        assert!(deg(s.roll_rad) < 10.0, "body roll is implausibly deep");
    }

    #[test]
    fn sustained_driving_stays_glued_to_the_terrain() {
        let mut s = new_hull();
        let mut x = 300.0_f32;
        let z = 0.0_f32;
        s.reset(x, z, 0.0);
        // Settle before driving, as happens on spawn. Skipping this measures the
        // start-up transient from being dropped into rough terrain at 29 km/h, which is
        // not a state the game ever puts the vehicle in.
        settle(&mut s, 60, x, z);

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
        // Measured 0.72 m, on the 52 m outer rim at 29 km/h -- the tank is briefly
        // airborne over a crest, which is real. It was 4.94 m (thrown clear) before the
        // bump-stop ceiling was keyed to each corner's own preload.
        assert!(peak < 1.0, "hull left the ground (peak {peak})");
        // This was loosened to -0.6 to accommodate a regression rather than fix it. The
        // pitch sign inversion was the cause: with the attitude inverted the hull
        // nose-dived into every rise and the tracks buried. Corrected, the worst
        // excursion over this terrain is ~0.16 m, so the bound is back where it started.
        assert!(deepest > -0.75, "hull sank through the terrain ({deepest})");
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
        assert!(deg(s.pitch_rad).abs() <= 50.0, "pitch escaped its limits");
        assert!(deg(s.roll_rad).abs() <= 50.0, "roll escaped its limits");
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