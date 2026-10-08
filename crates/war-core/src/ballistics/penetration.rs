//! Plate resolution, ricochet, and spall cones.
//!
//! Ported from `Assets/Scripts/Ballistics/Penetration.cs`.

use crate::math::mathf;

use super::shell::{Shell, ShellType};

/// The outcome of resolving one plate hit.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ImpactResult {
    /// Round passed through the plate and continues with reduced energy.
    Penetration,
    /// Plate held. A bouncing round detonates here instead of passing on.
    NonPenetration,
    /// Impact too oblique, over the critical angle. Round skips away.
    Ricochet,
    /// Round passed a plate that could not stop it; overkill.
    OverPenetration,
}

/// One armour plate in the hit path. Thickness is at the plate normal, so the
/// sloped glacis of a Panther is modelled as thick plate presented at an angle
/// rather than as a thin plate plus a fudge factor.
///
/// The C# original carried a `StructuralIntegrity` field that was documented,
/// defaulted to 1 and never read anywhere in the project. It is dropped here
/// rather than translated; see `PORT-NOTES.md`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ArmourPlate {
    /// Stable identifier, used as the key in the damage model's plate map
    /// (`"front"`, `"side"`, `"rear"`). Always a literal, hence `'static`.
    pub name: &'static str,
    /// Plate thickness perpendicular to its own normal, mm of RHA.
    pub thickness_mm: f32,
    /// True for cast hulls, which spall differently from rolled plate.
    pub is_cast: bool,
    /// Spall cone half-angle in degrees. 76 mm AP throws roughly 15.
    pub spall_cone_deg: f32,
}

impl ArmourPlate {
    /// A welded rolled plate -- the default for almost every tank in the roster.
    pub fn new(name: &'static str, thickness_mm: f32) -> Self {
        ArmourPlate {
            name,
            thickness_mm,
            is_cast: false,
            spall_cone_deg: 15.0,
        }
    }

    /// A cast homogeneous plate: harder, and resists perforation better than
    /// rolled plate of the same nominal thickness.
    pub fn cast(name: &'static str, thickness_mm: f32) -> Self {
        ArmourPlate { is_cast: true, ..ArmourPlate::new(name, thickness_mm) }
    }

    /// Relative resistance of the plate's material against rolled homogeneous
    /// armour, which is the unit all published thicknesses are quoted in.
    ///
    /// Cast homogeneous armour is harder and resists perforation better than
    /// rolled plate of the same nominal thickness, so it is modelled as slightly
    /// thicker. This is a modest effect next to slope, but it is the reason a
    /// Panther's cast hull and its rolled front are not interchangeable.
    pub fn material_coefficient(&self) -> f32 {
        if self.is_cast {
            1.10
        } else {
            1.0
        }
    }

    /// Effective thickness presented to an incoming round at
    /// `impact_angle_deg` away from the plate normal, against pure geometry with
    /// no shell involved.
    ///
    /// Use this only when you genuinely have no ordnance in hand. Every gameplay
    /// path should call [`Self::effective_thickness_vs`], because normalisation
    /// and the shaped-charge exception both belong here.
    pub fn effective_thickness(&self, impact_angle_deg: f32) -> f32 {
        self.thickness_mm * self.material_coefficient() * Shell::slope_multiplier(impact_angle_deg)
    }

    /// Effective thickness this plate presents to a specific round.
    ///
    /// Normalisation belongs on the plate, not on the projectile, and that is the
    /// subtle part. The round's capability does not change when its nose bites in;
    /// what changes is how much plate the round has to chew through. A blunt APBC
    /// nose rotating toward the normal means the plate is met at a less oblique
    /// angle than the firing line suggested, so it presents less.
    ///
    /// Applying it here rather than in [`Shell::penetration_at`] also means a
    /// single plate resolves correctly against the whole roster without any
    /// per-shell special case at the call sites, and it keeps the two quantities
    /// separable: one is what the shell can do, the other is what the armour
    /// makes it do.
    pub fn effective_thickness_vs(&self, shell: &Shell, impact_angle_deg: f32) -> f32 {
        // A shaped-charge jet collapses onto the plate normal however oblique the
        // impact, so slope costs it nothing at all.
        if shell.ignores_slope() {
            return self.thickness_mm * self.material_coefficient();
        }

        let angle = mathf::abs(impact_angle_deg);

        // Clamped at zero so a denormalising round cannot be handed *less*
        // effective thickness than face-on geometry, which would be a straight
        // inversion of the physics.
        let effective_angle = mathf::max(0.0, angle - shell.normalisation_deg());

        self.thickness_mm * self.material_coefficient() * Shell::slope_multiplier(effective_angle)
    }
}

/// Everything the damage model and the HUD need to know about one resolved hit.
///
/// Deliberately **not** `Default`. In C# this was a struct whose fields were
/// zero-initialised, which meant an unset `Result` silently read as
/// `Penetration` -- a report claiming a round got through when nothing had been
/// decided. Every constructor here sets all eight fields explicitly instead.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ImpactReport {
    pub result: ImpactResult,
    pub impact_angle_deg: f32,
    pub effective_armor_mm: f32,
    pub shell_penetration_mm: f32,
    /// Residual penetration after eating the plate. Drives over-penetration.
    pub residual_penetration_mm: f32,
    /// Kinetic energy delivered to modules behind the plate, joules.
    pub kinetic_energy_j: f32,
    /// True when the round went through and a spall cone is created.
    pub created_spall_cone: bool,
    /// Spall cone half-angle, degrees. 0 when no cone.
    pub spall_cone_deg: f32,
}

/// Resolves one plate hit. Stateless and side-effect free so it is safe to run on
/// the server and replay identically for hit-regret corrections.
pub struct Penetration;

impl Penetration {
    /// Fallback critical ricochet angle in degrees, used only by callers that
    /// have no shell to hand.
    ///
    /// [`Shell::critical_ricochet_deg`] is the real answer and every gameplay path
    /// should use it. This constant remains as a conservative default so that a
    /// shell-less call site cannot accidentally inherit the most permissive
    /// threshold in the roster.
    pub const CRITICAL_RICOCHET_ANGLE_DEG: f32 = 65.0;

    /// Damage multiplier applied when a round fails to penetrate. A brittle APCBC
    /// breaks into fragments inside the plate and is nearly harmless; an APHE round
    /// still cooks off, which is why capped AP is dangerous on light tanks.
    fn non_penetration_multiplier(shell_type: ShellType) -> f32 {
        match shell_type {
            ShellType::Apcbc => 0.15,
            ShellType::Apc => 0.25,
            ShellType::Aphe => 0.85,
            ShellType::Aphebc => 0.80,
            ShellType::Apcr => 0.10,
            ShellType::Heat => 1.0,
            ShellType::He => 1.0,
        }
    }

    /// Resolve one shell against one plate at one impact angle.
    ///
    /// The branch order is load-bearing and is preserved exactly from the C#
    /// original:
    ///   1. ricochet check -- above the critical angle nothing else matters
    ///   2. HE never "penetrates" in this sense: it fragments and blasts
    ///   3. penetration below the effective thickness fails
    ///   4. otherwise the round eats the plate and may still fail on the next one
    pub fn resolve(shell: &Shell, plate: &ArmourPlate, impact_angle_deg: f32) -> ImpactReport {
        let angle = mathf::abs(impact_angle_deg);

        // Per-shell threshold, not one global angle. A sharp APCR rod skips off
        // plate at 60 degrees where a blunt APCBC still punches in at 73, and a
        // HEAT jet does not bounce at all. A single global value made the sharp
        // rounds usable against sloped armour and made HEAT wrongly afraid of it.
        let critical_ricochet = shell.critical_ricochet_deg();

        if angle >= critical_ricochet {
            return ImpactReport {
                result: ImpactResult::Ricochet,
                impact_angle_deg: angle,
                effective_armor_mm: plate.effective_thickness_vs(shell, angle),
                shell_penetration_mm: shell.penetration_at(0.0),
                residual_penetration_mm: 0.0,
                kinetic_energy_j: 0.0,
                created_spall_cone: false,
                spall_cone_deg: 0.0,
            };
        }

        let effective = plate.effective_thickness_vs(shell, angle);
        let penetration = shell.penetration_at(0.0);

        // HE rounds never "penetrate" in this sense: they fragment and blast.
        if shell.shell_type == ShellType::He {
            return ImpactReport {
                result: ImpactResult::NonPenetration,
                impact_angle_deg: angle,
                effective_armor_mm: effective,
                shell_penetration_mm: 0.0,
                residual_penetration_mm: 0.0,
                kinetic_energy_j: 0.0,
                created_spall_cone: false,
                spall_cone_deg: 0.0,
            };
        }

        if penetration < effective {
            return ImpactReport {
                result: ImpactResult::NonPenetration,
                impact_angle_deg: angle,
                effective_armor_mm: effective,
                shell_penetration_mm: penetration,
                residual_penetration_mm: 0.0,
                kinetic_energy_j: shell.kinetic_energy_j()
                    * Self::non_penetration_multiplier(shell.shell_type),
                created_spall_cone: false,
                spall_cone_deg: 0.0,
            };
        }

        // Penetration: the round loses energy proportional to plate thickness,
        // exactly as it does on real armour, so it may still fail on the plate
        // behind this one.
        let consumed = effective;
        let residual = mathf::max(0.0, penetration - consumed);
        let energy =
            shell.kinetic_energy_j() * mathf::clamp01(residual / mathf::max(1.0, penetration));

        // A spall cone is a feature of SOLID projectiles only.
        let solid_projectile = shell.is_solid_projectile();

        ImpactReport {
            result: if residual > 0.0 {
                ImpactResult::Penetration
            } else {
                ImpactResult::OverPenetration
            },
            impact_angle_deg: angle,
            effective_armor_mm: effective,
            shell_penetration_mm: penetration,
            residual_penetration_mm: residual,
            kinetic_energy_j: energy,
            created_spall_cone: solid_projectile,
            spall_cone_deg: if solid_projectile { plate.spall_cone_deg } else { 0.0 },
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn apcbc() -> Shell {
        Shell::apcbc(88.0, 6.8, 800.0, 150.0, Some(0.0015))
    }

    #[test]
    fn material_coefficient_distinguishes_cast_from_rolled() {
        assert_eq!(ArmourPlate::new("rolled", 100.0).material_coefficient(), 1.0);
        assert!((ArmourPlate::cast("cast", 100.0).material_coefficient() - 1.1).abs() < 0.001);
    }

    #[test]
    fn cast_plate_resists_more_than_rolled_of_equal_thickness() {
        let rolled = ArmourPlate::new("rolled", 100.0);
        let cast = ArmourPlate::cast("cast", 100.0);
        assert!(cast.effective_thickness(0.0) > rolled.effective_thickness(0.0));
    }

    #[test]
    fn default_spall_cone_is_fifteen_degrees() {
        assert_eq!(ArmourPlate::new("front", 100.0).spall_cone_deg, 15.0);
        assert_eq!(ArmourPlate::cast("front", 100.0).spall_cone_deg, 15.0);
    }

    #[test]
    fn ricochet_reports_zero_energy_and_no_cone() {
        let report = Penetration::resolve(&apcbc(), &ArmourPlate::new("front", 200.0), 80.0);
        assert_eq!(report.result, ImpactResult::Ricochet);
        assert_eq!(report.kinetic_energy_j, 0.0);
        assert!(!report.created_spall_cone);
        assert_eq!(report.spall_cone_deg, 0.0);
        assert_eq!(report.residual_penetration_mm, 0.0);
        // The angle is normalised to a positive value in the report.
        assert_eq!(report.impact_angle_deg, 80.0);
        let mirrored = Penetration::resolve(&apcbc(), &ArmourPlate::new("front", 200.0), -80.0);
        assert_eq!(mirrored.impact_angle_deg, 80.0);
    }

    #[test]
    fn he_never_penetrates_and_reports_zero_penetration() {
        let he = Shell::he(88.0, 4.5, 450.0, 1.9, None);
        let report = Penetration::resolve(&he, &ArmourPlate::new("thin", 5.0), 0.0);
        assert_eq!(report.result, ImpactResult::NonPenetration);
        assert_eq!(report.shell_penetration_mm, 0.0);
        assert_eq!(report.kinetic_energy_j, 0.0);
        assert!(!report.created_spall_cone);
    }

    #[test]
    fn a_failed_penetration_applies_the_shell_specific_energy_multiplier() {
        let thick = ArmourPlate::new("thick", 400.0);
        let brittleness = |shell: &Shell| {
            let report = Penetration::resolve(shell, &thick, 0.0);
            assert_eq!(report.result, ImpactResult::NonPenetration);
            report.kinetic_energy_j / shell.kinetic_energy_j()
        };
        // APCBC is the most brittle: it fragments and is nearly harmless.
        let apcbc = brittleness(&Shell::apcbc(88.0, 6.8, 800.0, 150.0, None));
        let apcr = brittleness(&Shell::apcr(88.0, 3.0, 1200.0, 190.0, None));
        let aphebc = brittleness(&Shell::aphebc(88.0, 9.9, 900.0, 160.0));
        assert!((apcbc - 0.15).abs() < 1e-5);
        assert!((apcr - 0.10).abs() < 1e-5);
        assert!((aphebc - 0.80).abs() < 1e-5);
        // A cooking-off APHE round is far more dangerous on a plate it cannot defeat.
        let aphe = brittleness(&Shell::aphe(88.0, 9.9, 900.0, 160.0, 1.0, None));
        assert!((aphe - 0.85).abs() < 1e-5);
        assert!(aphe > aphebc, "APHE must cook off harder than APHEBC");
    }

    #[test]
    fn exact_match_is_over_penetration_not_penetration() {
        // A round whose penetration exactly equals the effective plate resolves to
        // OverPenetration: residual is 0. Pinned because the difference matters to
        // the damage model, which branches on it.
        let round = Shell::aphebc(100.0, 10.0, 800.0, 100.0);
        let plate = ArmourPlate::new("exact", 100.0);
        let report = Penetration::resolve(&round, &plate, 0.0);
        assert_eq!(report.result, ImpactResult::OverPenetration);
        assert_eq!(report.residual_penetration_mm, 0.0);
    }

    #[test]
    fn penetration_residual_and_energy_are_consistent() {
        let round = Shell::apcbc(88.0, 6.8, 800.0, 150.0, Some(0.0015));
        let plate = ArmourPlate::new("plate", 100.0);
        let report = Penetration::resolve(&round, &plate, 0.0);
        assert_eq!(report.result, ImpactResult::Penetration);
        assert!((report.effective_armor_mm - 100.0).abs() < 0.01);
        // Residual is what is left of the round's penetration after the plate.
        assert!((report.residual_penetration_mm - (report.shell_penetration_mm - 100.0)).abs() < 0.01);
        // Energy delivered is the fraction of the round's energy that survived.
        let expected_fraction =
            report.residual_penetration_mm / (report.shell_penetration_mm.max(1.0));
        let actual_fraction = report.kinetic_energy_j / round.kinetic_energy_j();
        assert!((expected_fraction - actual_fraction).abs() < 1e-4);
    }

    #[test]
    fn only_solid_projectiles_throw_a_spall_cone() {
        // Solid round against a plate it defeats.
        let solid = Shell::apc(88.0, 6.8, 800.0, 200.0, None);
        let plate = ArmourPlate::new("plate", 100.0);
        let report = Penetration::resolve(&solid, &plate, 0.0);
        assert!(report.created_spall_cone);
        assert_eq!(report.spall_cone_deg, 15.0);

        // Cooking-off round: no coherent cone, however deep it got.
        let hebc = Shell::aphebc(88.0, 9.9, 900.0, 200.0);
        let report = Penetration::resolve(&hebc, &plate, 0.0);
        assert_eq!(report.result, ImpactResult::Penetration);
        assert!(!report.created_spall_cone, "APHEBC must not throw a spall cone");
        assert_eq!(report.spall_cone_deg, 0.0);
    }

    #[test]
    fn resolve_is_pure_and_repeatable() {
        let round = Shell::apcbc(88.0, 6.8, 800.0, 150.0, Some(0.0015));
        let plate = ArmourPlate::new("plate", 120.0);
        let a = Penetration::resolve(&round, &plate, 30.0);
        let b = Penetration::resolve(&round, &plate, 30.0);
        assert_eq!(a, b, "resolution must be bit-reproducible for server replay");
    }

    #[test]
    fn extreme_angles_stay_finite() {
        let round = Shell::apcbc(88.0, 6.8, 800.0, 150.0, Some(0.0015));
        for angle in [0.0f32, 89.0, 90.0, 180.0, 360.0, 720.0] {
            for thickness in [0.0f32, 1.0, 100.0, 1000.0] {
                let report =
                    Penetration::resolve(&round, &ArmourPlate::new("p", thickness), angle);
                assert!(
                    report.effective_armor_mm.is_finite() && report.kinetic_energy_j.is_finite(),
                    "angle {angle}, thickness {thickness} produced non-finite output"
                );
            }
        }
    }
}