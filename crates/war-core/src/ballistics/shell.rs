//! A single ordnance round.
//!
//! Ported from `Assets/Scripts/Ballistics/Shell.cs`. Tuning values are reproduced
//! exactly; see `PORT-NOTES.md` for the list of things deliberately *not* carried
//! over.

use crate::math::mathf;

pub use super::shell_type::ShellType;

/// Drag coefficient for a typical WWII tank round, chosen so residual velocity
/// matches the documented figures: an 88 mm round doing about 800 m/s at
/// 1000 m, an 85 mm about 690 m/s.
pub const TYPICAL_TANK_ROUND_DRAG: f32 = 0.00013;

/// Sub-calibre rounds are long and slim, so they hold velocity better than a
/// full-calibre shell of the same size, which is a large part of why they
/// dominate at long range despite their small spall signature.
pub const SUBCALIBRE_DRAG: f32 = 0.00008;

/// Solid shot is shorter and blunter, so it sheds a little more velocity than a
/// capped round of the same calibre.
pub const SOLID_SHOT_DRAG: f32 = 0.00016;


/// A single ordnance round.
///
/// In C# this was a `[Serializable] struct` with public mutable fields, described
/// as "immutable after construction" only by convention. It is a `Copy` value
/// type here, so the "immutable by construction" contract is enforced by the type
/// system rather than by a comment.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Shell {
    pub shell_type: ShellType,
    pub caliber_mm: f32,
    pub mass_kg: f32,
    /// Initial muzzle velocity, m/s.
    pub muzzle_velocity: f32,
    /// Armour penetration at 0 m and 90 degrees of impact, in mm of RHA.
    pub penetration_0_mm: f32,
    /// Explosive filler mass, kg. Drives post-penetration damage and HE radius.
    pub explosive_mass_kg: f32,
    /// Drag coefficient. Higher loses velocity faster over distance.
    pub drag_coefficient: f32,
    /// Fuse depth past the penetration point before a bouncing round detonates, m.
    pub fuse_depth_m: f32,
}

/// Resolves the optional drag override the C# factories expressed as a defaulted
/// parameter (`float drag = 0f`), where anything `<= 0` meant "use the default".
///
/// A `None` is the Rust equivalent of omitting the argument; `Some(0.0)` is
/// treated exactly as C# treated an explicit `0f`, i.e. as "use the default".
fn drag_or_default(override_drag: Option<f32>, default: f32) -> f32 {
    match override_drag {
        Some(d) if d > 0.0 => d,
        _ => default,
    }
}

impl Shell {
    /// Armour-piercing, capped. Fragile if it fails to penetrate.
    pub fn apcbc(caliber: f32, mass: f32, v0: f32, pen0: f32, drag: Option<f32>) -> Self {
        Shell {
            shell_type: ShellType::Apcbc,
            caliber_mm: caliber,
            mass_kg: mass,
            muzzle_velocity: v0,
            penetration_0_mm: pen0,
            explosive_mass_kg: 0.0,
            drag_coefficient: drag_or_default(drag, SOLID_SHOT_DRAG),
            fuse_depth_m: 0.05,
        }
    }

    /// Armour-piercing, high-explosive filler.
    pub fn aphe(
        caliber: f32,
        mass: f32,
        v0: f32,
        pen0: f32,
        filler: f32,
        drag: Option<f32>,
    ) -> Self {
        Shell {
            shell_type: ShellType::Aphe,
            caliber_mm: caliber,
            mass_kg: mass,
            muzzle_velocity: v0,
            penetration_0_mm: pen0,
            explosive_mass_kg: filler,
            drag_coefficient: drag_or_default(drag, TYPICAL_TANK_ROUND_DRAG),
            fuse_depth_m: 1.0,
        }
    }

    /// Armour-piercing, composite, no explosive.
    pub fn apc(caliber: f32, mass: f32, v0: f32, pen0: f32, drag: Option<f32>) -> Self {
        Shell {
            shell_type: ShellType::Apc,
            caliber_mm: caliber,
            mass_kg: mass,
            muzzle_velocity: v0,
            penetration_0_mm: pen0,
            explosive_mass_kg: 0.0,
            drag_coefficient: drag_or_default(drag, TYPICAL_TANK_ROUND_DRAG),
            fuse_depth_m: 0.05,
        }
    }

    /// Ballistically-capped HE filler. The default WWII tank round.
    ///
    /// No drag override exists for this factory in C# either -- the round is
    /// pinned to [`TYPICAL_TANK_ROUND_DRAG`] -- so the signature has none here
    /// rather than accepting an argument that was ignored.
    pub fn aphebc(caliber: f32, mass: f32, v0: f32, pen0: f32) -> Self {
        // Filler mass is roughly 2.5% of the projectile mass for APHEBC.
        Shell {
            shell_type: ShellType::Aphebc,
            caliber_mm: caliber,
            mass_kg: mass,
            muzzle_velocity: v0,
            penetration_0_mm: pen0,
            explosive_mass_kg: mass * 0.025,
            drag_coefficient: TYPICAL_TANK_ROUND_DRAG,
            fuse_depth_m: 0.9,
        }
    }

    /// Armour-piercing, capped, sub-calibre core.
    pub fn apcr(caliber: f32, mass: f32, v0: f32, pen0: f32, drag: Option<f32>) -> Self {
        Shell {
            shell_type: ShellType::Apcr,
            caliber_mm: caliber,
            mass_kg: mass,
            muzzle_velocity: v0,
            penetration_0_mm: pen0,
            explosive_mass_kg: 0.0,
            drag_coefficient: drag_or_default(drag, SUBCALIBRE_DRAG),
            fuse_depth_m: 0.05,
        }
    }

    /// Shaped-charge.
    ///
    /// Filler scales with the square of calibre because the jet forms from the
    /// explosive, not the projectile.
    pub fn heat(caliber: f32, mass: f32, v0: f32, pen0: f32, drag: Option<f32>) -> Self {
        Shell {
            shell_type: ShellType::Heat,
            caliber_mm: caliber,
            mass_kg: mass,
            muzzle_velocity: v0,
            penetration_0_mm: pen0,
            explosive_mass_kg: caliber * caliber / 250.0,
            drag_coefficient: drag_or_default(drag, TYPICAL_TANK_ROUND_DRAG),
            fuse_depth_m: 0.0,
        }
    }

    /// High explosive.
    ///
    /// The fourth argument is **filler mass, not penetration**. In the C# original
    /// this factory was
    /// `HE(float caliber, float mass, float v0, float filler, float drag = 0f)`
    /// while all six of its siblings took `pen0` in that position -- a trap that
    /// silently built a shell with the charge weight in the penetration slot. The
    /// name here says filler, `penetration_0_mm` is forced to 0 (HE has no
    /// meaningful penetration figure), and the test below pins that.
    pub fn he(caliber: f32, mass: f32, v0: f32, filler: f32, drag: Option<f32>) -> Self {
        Shell {
            shell_type: ShellType::He,
            caliber_mm: caliber,
            mass_kg: mass,
            muzzle_velocity: v0,
            penetration_0_mm: 0.0,
            explosive_mass_kg: filler,
            drag_coefficient: drag_or_default(drag, TYPICAL_TANK_ROUND_DRAG),
            fuse_depth_m: 0.0,
        }
    }

    /// Normalisation angle for this round, degrees.
    pub fn normalisation_deg(&self) -> f32 {
        self.shell_type.normalisation_deg()
    }

    /// Critical ricochet angle for this round, degrees of impact.
    pub fn critical_ricochet_deg(&self) -> f32 {
        self.shell_type.critical_ricochet_deg()
    }

    /// True when this round ignores slope entirely.
    pub fn ignores_slope(&self) -> bool {
        self.shell_type.ignores_slope()
    }

    /// Impact-angle multiplier for the plate, from geometry alone: pure
    /// line-of-sight depth, thickness divided by cosine.
    ///
    /// Deliberately free of any shell-specific behaviour. It is the geometry, and
    /// callers that want normalisation or a shaped-charge exemption ask for it
    /// explicitly via [`crate::ballistics::ArmourPlate::effective_thickness_vs`].
    ///
    /// The angle is clamped to 89 degrees so a grazing hit cannot divide by a
    /// cosine of zero.
    pub fn slope_multiplier(impact_angle_deg: f32) -> f32 {
        let a = mathf::clamp(mathf::abs(impact_angle_deg), 0.0, 89.0);
        let rad = a * mathf::DEG2RAD;
        1.0 / mathf::cos(rad)
    }

    /// Whether an HE burst can breach a plate of the given effective thickness
    /// and detonate INSIDE the hull rather than against the outside.
    ///
    /// HE has no meaningful penetration figure, so it cannot use the de Marre
    /// path. Modelling it as never breaching would be wrong in the opposite
    /// direction: a 75 mm HE round absolutely does defeat 8 mm of rolled plate,
    /// which is why HE was devastating against light tanks and Type 95 Ha-Gos.
    ///
    /// The threshold is an approximation of the observed relationship between
    /// charge weight and plate it can defeat, and is the piece to re-tune against
    /// firing data.
    pub fn breaches_plate(&self, effective_thickness_mm: f32) -> bool {
        if self.shell_type != ShellType::He {
            return false;
        }
        if effective_thickness_mm <= 0.0 {
            return true;
        }
        effective_thickness_mm <= self.caliber_mm * 0.12
    }

    /// Armour penetration after travelling `distance_m`.
    ///
    /// Uses the Jacob de Marre ballistic relationship: penetration falls off with
    /// the velocity the round has lost to drag, which is why a long-range shot at
    /// a sloped plate that would have punched through up close simply fails.
    ///
    /// This is the round's capability and nothing else. Obliquity is applied by
    /// the plate, because that is where normalisation and the shaped-charge
    /// exception physically belong. HEAT is the exception that does not decay
    /// with range.
    ///
    /// The C# original also took an `impactAngleDeg` argument and never read it.
    /// It has been dropped rather than translated, since carrying a parameter that
    /// is documented as belonging to the *plate* invites someone to use it.
    pub fn penetration_at(&self, distance_m: f32) -> f32 {
        let v = self.velocity_at(distance_m);

        // de Marre: pen ~ (m * v^2 / d^3)^(1/3), normalised to the 0 m value.
        let caliber_cubed = self.caliber_mm * self.caliber_mm * self.caliber_mm;
        let raw = self.mass_kg * v * v / caliber_cubed;
        let zero_reference = self.mass_kg * self.muzzle_velocity * self.muzzle_velocity / caliber_cubed;
        let ratio = if zero_reference > 0.0 {
            mathf::pow(raw / zero_reference, 1.0 / 3.0)
        } else {
            0.0
        };

        self.penetration_0_mm * ratio
    }

    /// Velocity after `distance_m`. Exponential decay model:
    /// `v = v0 * exp(-k * d)`, which matches the shape of published
    /// velocity-vs-range tables far better than linear falloff at tank
    /// engagement ranges.
    pub fn velocity_at(&self, distance_m: f32) -> f32 {
        self.muzzle_velocity * mathf::exp(-self.drag_coefficient * distance_m)
    }

    /// Kinetic energy in joules: `0.5 * m * v^2`.
    pub fn kinetic_energy_j(&self) -> f32 {
        0.5 * self.mass_kg * self.muzzle_velocity * self.muzzle_velocity
    }

    /// True when this is a solid projectile, i.e. one that throws a coherent
    /// spall cone rather than fragmenting and cooking off.
    ///
    /// A spall cone is a feature of solid projectiles only. An APHE or APHEBC
    /// round fragments the plate and then cooks off inside the hull; it does not
    /// throw a coherent cone. Modelling it as one would make high-penetration
    /// rounds lethal at absurd range.
    pub fn is_solid_projectile(&self) -> bool {
        self.explosive_mass_kg <= 0.01
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn drag_override_follows_the_csharp_semantics() {
        // `None` means the caller omitted the argument -> the family default.
        assert_eq!(Shell::apcbc(88.0, 6.8, 800.0, 150.0, None).drag_coefficient, SOLID_SHOT_DRAG);
        // An explicit zero was also "use the default" in C# (`drag > 0f ? drag : ...`).
        assert_eq!(Shell::apcbc(88.0, 6.8, 800.0, 150.0, Some(0.0)).drag_coefficient, SOLID_SHOT_DRAG);
        // A negative override is likewise ignored, not applied.
        assert_eq!(
            Shell::apcbc(88.0, 6.8, 800.0, 150.0, Some(-1.0)).drag_coefficient,
            SOLID_SHOT_DRAG
        );
        // A positive override wins.
        assert_eq!(Shell::apcbc(88.0, 6.8, 800.0, 150.0, Some(0.0015)).drag_coefficient, 0.0015);
    }

    #[test]
    fn each_family_uses_its_own_default_drag() {
        assert_eq!(Shell::aphebc(75.0, 6.8, 750.0, 111.0).drag_coefficient, TYPICAL_TANK_ROUND_DRAG);
        assert_eq!(Shell::apc(88.0, 6.8, 800.0, 150.0, None).drag_coefficient, TYPICAL_TANK_ROUND_DRAG);
        assert_eq!(Shell::apcr(88.0, 3.0, 1200.0, 190.0, None).drag_coefficient, SUBCALIBRE_DRAG);
        assert_eq!(Shell::heat(88.0, 17.0, 800.0, 200.0, None).drag_coefficient, TYPICAL_TANK_ROUND_DRAG);
        assert_eq!(Shell::he(88.0, 4.5, 450.0, 1.9, None).drag_coefficient, TYPICAL_TANK_ROUND_DRAG);
    }

    /// Pins the C# `HE(caliber, mass, v0, filler, drag)` footgun shut: the fourth
    /// argument is charge weight, and must never land in the penetration slot.
    #[test]
    fn he_takes_filler_not_penetration() {
        let shell = Shell::he(88.0, 4.5, 450.0, 1.9, None);
        assert_eq!(shell.explosive_mass_kg, 1.9, "the filler must be the charge weight");
        assert_eq!(
            shell.penetration_0_mm, 0.0,
            "HE has no penetration figure; it must not borrow one from the argument"
        );
        assert_eq!(shell.shell_type, ShellType::He);
    }

    #[test]
    fn aphebc_filler_is_two_and_a_half_percent_of_mass() {
        let shell = Shell::aphebc(75.0, 6.8, 750.0, 111.0);
        assert!((shell.explosive_mass_kg - 6.8 * 0.025).abs() < 1e-6);
        assert_eq!(shell.fuse_depth_m, 0.9);
    }

    #[test]
    fn heat_filler_scales_with_calibre_squared() {
        let shell = Shell::heat(88.0, 17.0, 800.0, 200.0, None);
        assert!((shell.explosive_mass_kg - 88.0 * 88.0 / 250.0).abs() < 1e-3);
    }

    #[test]
    fn fuse_depths_match_the_csharp_values() {
        assert_eq!(Shell::apcbc(88.0, 6.8, 800.0, 150.0, None).fuse_depth_m, 0.05);
        assert_eq!(Shell::aphe(88.0, 6.8, 800.0, 150.0, 1.0, None).fuse_depth_m, 1.0);
        assert_eq!(Shell::apc(88.0, 6.8, 800.0, 150.0, None).fuse_depth_m, 0.05);
        assert_eq!(Shell::apcr(88.0, 3.0, 1200.0, 190.0, None).fuse_depth_m, 0.05);
        assert_eq!(Shell::heat(88.0, 17.0, 800.0, 200.0, None).fuse_depth_m, 0.0);
        assert_eq!(Shell::he(88.0, 4.5, 450.0, 1.9, None).fuse_depth_m, 0.0);
    }

    #[test]
    fn slope_multiplier_is_clamped_short_of_the_singularity() {
        // Face on is 1.0.
        assert!((Shell::slope_multiplier(0.0) - 1.0).abs() < 1e-6);
        // 60 degrees is 2x.
        assert!((Shell::slope_multiplier(60.0) - 2.0).abs() < 0.01);
        // Exactly 90 would divide by zero; the clamp keeps it finite.
        let grazing = Shell::slope_multiplier(90.0);
        assert!(grazing.is_finite(), "slope_multiplier(90) must not be infinite");
        assert!(grazing > 50.0);
        // Sign is irrelevant: the angle is an absolute value.
        assert_eq!(Shell::slope_multiplier(-60.0), Shell::slope_multiplier(60.0));
    }

    #[test]
    fn velocity_and_penetration_fall_off_with_range() {
        let round = Shell::aphebc(75.0, 6.8, 750.0, 111.0);
        assert!((round.velocity_at(0.0) - 750.0).abs() < 1e-3);
        assert!(round.velocity_at(2500.0) < round.velocity_at(0.0));
        assert!(round.penetration_at(2500.0) < round.penetration_at(0.0));
        assert!(round.penetration_at(2500.0) > 0.0);
    }

    #[test]
    fn a_zero_velocity_round_reports_zero_penetration_rather_than_nan() {
        // Division by a zero reference is guarded in C# with `zeroReference > 0f`.
        let dud = Shell {
            shell_type: ShellType::Apcbc,
            caliber_mm: 88.0,
            mass_kg: 0.0,
            muzzle_velocity: 0.0,
            penetration_0_mm: 150.0,
            explosive_mass_kg: 0.0,
            drag_coefficient: SOLID_SHOT_DRAG,
            fuse_depth_m: 0.05,
        };
        let pen = dud.penetration_at(100.0);
        assert!(pen.is_finite(), "must not produce NaN");
        assert_eq!(pen, 0.0);
    }

    #[test]
    fn breaches_plate_only_applies_to_he() {
        let he = Shell::he(88.0, 4.5, 450.0, 1.9, None);
        // 88 mm HE breaches 0.12 * 88 = 10.56 mm, and any zero-thickness plate.
        assert!(he.breaches_plate(8.0));
        assert!(he.breaches_plate(0.0));
        assert!(!he.breaches_plate(30.0));

        // A kinetic round of the same calibre never breaches by this path.
        let aphebc = Shell::aphebc(88.0, 9.9, 900.0, 160.0);
        assert!(!aphebc.breaches_plate(0.0));
        assert!(!aphebc.breaches_plate(1.0));
    }

    #[test]
    fn solid_projectile_test_is_about_filler_mass() {
        assert!(Shell::apc(88.0, 6.8, 800.0, 150.0, None).is_solid_projectile());
        assert!(Shell::apcr(88.0, 3.0, 1200.0, 190.0, None).is_solid_projectile());
        assert!(Shell::apcbc(88.0, 6.8, 800.0, 150.0, None).is_solid_projectile());
        assert!(!Shell::aphebc(75.0, 6.8, 750.0, 111.0).is_solid_projectile());
        assert!(!Shell::heat(88.0, 17.0, 800.0, 200.0, None).is_solid_projectile());
    }

    #[test]
    fn kinetic_energy_matches_the_half_m_v_squared_formula() {
        let round = Shell::apcbc(88.0, 6.8, 800.0, 150.0, None);
        assert!((round.kinetic_energy_j() - 0.5 * 6.8 * 800.0 * 800.0).abs() < 0.5);
    }
}
