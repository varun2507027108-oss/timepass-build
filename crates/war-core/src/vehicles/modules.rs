//! Damageable subsystems and crew.
//!
//! Ported from `Assets/Scripts/Vehicles/DamageModule.cs`.

use crate::math::{mathf, Vec3};

/// A damageable subsystem. Modules are what actually kill a tank: armour only
/// delays the outcome, and a shot that defeats the front plate still has to reach
/// something that matters.
///
/// **Discriminants changed from the C# original.** `FuelTank` and `MachineGun`
/// were declared but never constructed or referenced anywhere in the project, and
/// removing them renumbers `AmmoRack` from 5 to 4, `Tracks` from 6 to 5 and
/// `Optic` from 7 to 6. Nothing serialises this enum, so the change is
/// behaviour-preserving; the values are pinned below so it cannot drift silently.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum ModuleKind {
    Engine = 0,
    Transmission = 1,
    TurretDrive = 2,
    GunBreech = 3,
    AmmoRack = 4,
    Tracks = 5,
    Optic = 6,
}

impl ModuleKind {
    pub const ALL: [ModuleKind; 7] = [
        ModuleKind::Engine,
        ModuleKind::Transmission,
        ModuleKind::TurretDrive,
        ModuleKind::GunBreech,
        ModuleKind::AmmoRack,
        ModuleKind::Tracks,
        ModuleKind::Optic,
    ];

    pub fn as_u8(self) -> u8 {
        self as u8
    }

    /// True for the module whose destruction immobilises the vehicle.
    pub fn is_mobility_critical(self) -> bool {
        matches!(self, ModuleKind::Tracks | ModuleKind::Transmission)
    }
}

/// The outcome of applying damage to one module.
///
/// The C# method returned a `bool` meaning "this hit destroyed it", which
/// collapsed three distinct outcomes -- ignored, damaged, destroyed -- into one
/// flag. `VehicleDamageModel` branches on that flag to trigger fire and crew
/// casualties, so an ambiguous return was load-bearing. The three states are
/// named here instead.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DamageOutcome {
    /// Nothing happened: already destroyed, no damage offered, or out of range.
    Ignored,
    /// Damage was applied and the module survived.
    Damaged,
    /// Damage was applied and this hit destroyed the module.
    Destroyed,
}

/// A damageable subsystem.
///
/// C# declared this as a `[Serializable] class` with public mutable fields, where
/// `Hp` was left uninitialised and relied upon the constructor to set it. A
/// `Default` derive would have produced `hp = 0.0` and made every module
/// instantly destroyed, so the fields are private-by-convention here and the
/// constructor is the only way in.
#[derive(Debug, Clone, PartialEq)]
pub struct DamageModule {
    pub name: &'static str,
    pub kind: ModuleKind,
    pub max_hp: f32,
    hp: f32,
    /// Hazard geometry. Used by the spall cone test, in local metres.
    pub local_position: Vec3,
    /// Blast radius for explosive damage, m. Zero for non-hazard modules.
    pub hazard_radius: f32,
    /// Chance of catastrophic failure once destroyed, 0..1.
    pub fire_chance_on_destroy: f32,
    /// Fraction of full effect remaining at the edge of its radius.
    pub edge_damage_floor: f32,
}

impl DamageModule {
    /// Build a module at full health.
    ///
    /// `hazard_radius` is the blast radius in metres; a value at or below 0.001
    /// disables distance falloff entirely.
    pub fn new(
        name: &'static str,
        kind: ModuleKind,
        local_position: Vec3,
        max_hp: f32,
        hazard_radius: f32,
    ) -> Self {
        DamageModule {
            name,
            kind,
            max_hp,
            // Set here, never defaulted: see the type docs.
            hp: max_hp,
            local_position,
            hazard_radius,
            fire_chance_on_destroy: 0.0,
            edge_damage_floor: 0.35,
        }
    }

    /// Builder for [`Self::fire_chance_on_destroy`], which the C# original set
    /// through an object initialiser (`new DamageModule(...) { FireChanceOnDestroy = ... }`).
    #[must_use]
    pub fn with_fire_chance(mut self, chance: f32) -> Self {
        self.fire_chance_on_destroy = chance;
        self
    }

    /// Current hit points.
    pub fn hp(&self) -> f32 {
        self.hp
    }

    /// Current health as a 0..1 fraction. Zero when destroyed.
    pub fn health_fraction(&self) -> f32 {
        if self.max_hp > 0.0 {
            self.hp / self.max_hp
        } else {
            0.0
        }
    }

    pub fn is_destroyed(&self) -> bool {
        self.hp <= 0.0
    }

    /// Apply damage with linear falloff across the hazard radius.
    ///
    /// A module whose hazard radius is at or below 0.001 m takes full damage at
    /// any distance. Otherwise a hit at or beyond the radius is ignored entirely
    /// -- which is deliberate: the round simply did not reach it.
    pub fn apply_damage(&mut self, amount: f32, distance_m: f32) -> DamageOutcome {
        if self.is_destroyed() || amount <= 0.0 {
            return DamageOutcome::Ignored;
        }

        let factor = if self.hazard_radius <= 0.001 {
            1.0
        } else if distance_m >= self.hazard_radius {
            return DamageOutcome::Ignored;
        } else {
            let t = 1.0 - (distance_m / self.hazard_radius);
            mathf::lerp(self.edge_damage_floor, 1.0, t)
        };

        self.hp -= amount * factor;
        if self.hp < 0.0 {
            self.hp = 0.0;
        }

        if self.is_destroyed() {
            DamageOutcome::Destroyed
        } else {
            DamageOutcome::Damaged
        }
    }
}

/// Seconds a wounded crew member is out of action.
pub const CREW_RECOVERY_SECONDS: f32 = 6.0;

/// A crew position.
///
/// Multi-crew vehicle. Crew are individually vulnerable, which is why a
/// penetrating round is often lethal even when it misses every module: the spall
/// cone catches whoever is sitting in it.
///
/// # Wounding is permanent -- and that may be a bug, but it is not one to fix here
///
/// [`Self::wound`] sets `is_alive = false` and nothing in the project ever sets it
/// back, so [`Self::is_available`] can never become true again for a wounded crew
/// member. The six-second [`CREW_RECOVERY_SECONDS`] timer is therefore dead: it
/// counts down and is never read for a gameplay decision.
///
/// The naming strongly suggests the original intent was *temporary*
/// incapacitation -- otherwise there would be no timer and no `IsAvailable`
/// distinct from `IsAlive`. But reviving is **not** a safe silent fix, because
/// `VehicleDamageModel` derives `IsDestroyed` from `AliveCrewCount`: a tank whose
/// crew were all wounded by an ammo-rack hit would stop counting as destroyed and
/// would come back after six seconds.
///
/// So the C# behaviour is reproduced exactly here and the question is escalated
/// rather than answered. See `PORT-NOTES.md` "Open: is crew wounding meant to be
/// permanent?".
#[derive(Debug, Clone, PartialEq)]
pub struct CrewMember {
    pub name: &'static str,
    pub local_position: Vec3,
    pub is_alive: bool,
    /// Seconds before a wounded crew member is available again.
    pub recovery_timer: f32,
}

impl CrewMember {
    pub fn new(name: &'static str, local_position: Vec3) -> Self {
        CrewMember { name, local_position, is_alive: true, recovery_timer: 0.0 }
    }

    /// Alive and recovered: can man a station.
    pub fn is_available(&self) -> bool {
        self.is_alive && self.recovery_timer <= 0.0
    }

    /// Wound this crew member and start the recovery timer.
    pub fn wound(&mut self) {
        self.is_alive = false;
        self.recovery_timer = CREW_RECOVERY_SECONDS;
    }

    /// Advance the recovery timer.
///
/// **Bug fix.** The C# original was `if (RecoveryTimer > 0f) RecoveryTimer -= dt;`,
/// which lets the timer run deeply negative -- one `tick(1000.0)` left it at
/// -994. Nothing read the magnitude (only `<= 0` was checked), so it was harmless,
/// but a timer that counts past zero is not what "6 seconds of recovery" means and
/// it makes the field a lie in any inspector or debug view.
pub fn tick(&mut self, dt: f32) {
        if self.recovery_timer > 0.0 {
            self.recovery_timer = (self.recovery_timer - dt).max(0.0);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn engine() -> DamageModule {
        DamageModule::new("Engine", ModuleKind::Engine, Vec3::new(0.0, -0.1, -2.1), 120.0, 1.6)
    }

    #[test]
    fn discriminants_are_contiguous_after_dropping_the_dead_variants() {
        for (index, kind) in ModuleKind::ALL.iter().enumerate() {
            assert_eq!(kind.as_u8() as usize, index, "{kind:?} is out of order");
        }
    }

    #[test]
    fn a_new_module_starts_at_full_health() {
        // The C# trap: `Hp` had no initialiser and relied on the constructor.
        // A `Default` derive would make this 0.0, i.e. instantly destroyed.
        let module = engine();
        assert_eq!(module.hp(), 120.0);
        assert_eq!(module.max_hp, 120.0);
        assert!(!module.is_destroyed());
        assert_eq!(module.health_fraction(), 1.0);
    }

    #[test]
    fn defaults_match_the_csharp_field_initialisers() {
        let module = engine();
        assert_eq!(module.edge_damage_floor, 0.35);
        assert_eq!(module.fire_chance_on_destroy, 0.0);
    }

    #[test]
    fn fire_chance_builder_sets_the_object_initialiser_field() {
        let module = engine().with_fire_chance(0.85);
        assert_eq!(module.fire_chance_on_destroy, 0.85);
        assert_eq!(engine().fire_chance_on_destroy, 0.0, "must not leak between builds");
    }

    #[test]
    fn a_hit_that_kills_reports_destroyed_exactly_once() {
        let mut module = engine();
        assert_eq!(module.apply_damage(60.0, 0.0), DamageOutcome::Damaged);
        assert_eq!(module.apply_damage(60.0, 0.0), DamageOutcome::Destroyed);
        // Further hits on a corpse are ignored, not reported as kills.
        assert_eq!(module.apply_damage(60.0, 0.0), DamageOutcome::Ignored);
        assert_eq!(module.hp(), 0.0);
    }

    #[test]
    fn zero_and_negative_damage_are_ignored() {
        let mut module = engine();
        assert_eq!(module.apply_damage(0.0, 0.0), DamageOutcome::Ignored);
        assert_eq!(module.apply_damage(-10.0, 0.0), DamageOutcome::Ignored);
        assert_eq!(module.hp(), 120.0);
    }

    #[test]
    fn damage_falls_off_linearly_within_the_hazard_radius() {
        let mut at_centre = engine();
        let mut midway = engine();

        at_centre.apply_damage(100.0, 0.0);
        midway.apply_damage(100.0, 0.8); // halfway across a 1.6 m radius

        // t = 0.5 at midway, so factor = lerp(0.35, 1.0, 0.5) = 0.675.
        assert!((at_centre.hp() - 20.0).abs() < 1e-3, "got {}", at_centre.hp());
        assert!((midway.hp() - (120.0 - 67.5)).abs() < 1e-3, "got {}", midway.hp());
    }

    #[test]
    fn a_hit_at_or_beyond_the_hazard_radius_is_ignored_entirely() {
        let mut module = engine();
        assert_eq!(module.apply_damage(100.0, 1.6), DamageOutcome::Ignored);
        assert_eq!(module.apply_damage(100.0, 99.0), DamageOutcome::Ignored);
        assert_eq!(module.hp(), 120.0, "an out-of-range hit must do nothing at all");
    }

    #[test]
    fn a_module_with_no_hazard_radius_takes_full_damage_at_any_distance() {
        // A hit at the centre of the radius takes the full amount.
        let mut centred = DamageModule::new("Optic", ModuleKind::Optic, Vec3::ZERO, 40.0, 0.5);
        centred.apply_damage(10.0, 0.0);
        assert!((centred.hp() - 30.0).abs() < 1e-3, "got {}", centred.hp());

        // HazardRadius <= 0.001 short-circuits the falloff entirely in C#, so the
        // same damage lands at any distance.
        let mut no_radius = DamageModule::new("Optic", ModuleKind::Optic, Vec3::ZERO, 40.0, 0.0);
        no_radius.apply_damage(10.0, 500.0);
        assert!((no_radius.hp() - 30.0).abs() < 1e-3, "no radius means no falloff");

        // At the very edge of a real radius the factor is the floor, not zero: a
        // graze still does a third of the damage.
        let mut edge = DamageModule::new("Optic", ModuleKind::Optic, Vec3::ZERO, 40.0, 1.0);
        edge.apply_damage(10.0, 0.999);
        // factor = lerp(0.35, 1.0, 1 - 0.999) ~= 0.35
        assert!(edge.hp() > 36.0 && edge.hp() < 37.0, "edge hit took {}.hp()", 40.0 - edge.hp());
    }

    #[test]
    fn hp_never_goes_negative() {
        let mut module = engine();
        module.apply_damage(10_000.0, 0.0);
        assert_eq!(module.hp(), 0.0);
        assert_eq!(module.health_fraction(), 0.0);
    }

    #[test]
    fn mobility_critical_modules_are_tracks_and_transmission() {
        assert!(ModuleKind::Tracks.is_mobility_critical());
        assert!(ModuleKind::Transmission.is_mobility_critical());
        for kind in ModuleKind::ALL {
            if kind != ModuleKind::Tracks && kind != ModuleKind::Transmission {
                assert!(!kind.is_mobility_critical(), "{kind:?} must not be mobility critical");
            }
        }
    }

    #[test]
    fn a_new_crew_member_is_alive_and_available() {
        let crew = CrewMember::new("Commander", Vec3::new(0.45, 0.7, -0.2));
        assert!(crew.is_alive);
        assert!(crew.is_available());
        assert_eq!(crew.recovery_timer, 0.0);
    }

    #[test]
    fn wounding_kills_and_starts_a_six_second_recovery() {
        let mut crew = CrewMember::new("Gunner", Vec3::new(-0.35, 0.6, 0.55));
        crew.wound();

        assert!(!crew.is_alive);
        assert!(!crew.is_available(), "a dead crew member is not available even at zero timer");
        assert_eq!(crew.recovery_timer, CREW_RECOVERY_SECONDS);
        assert_eq!(CREW_RECOVERY_SECONDS, 6.0, "the C# magic number must be preserved");
    }

    #[test]
    fn recovery_never_revives_a_wounded_crew_member() {
        // Documented C# behaviour, reproduced deliberately rather than "fixed".
        // See the type-level note on why reviving is a balance decision.
        let mut crew = CrewMember::new("Loader", Vec3::new(-0.4, 0.5, 0.9));
        crew.wound();
        assert!(!crew.is_alive);
        assert!(!crew.is_available(), "wounded: not alive");

        crew.tick(5.0);
        assert!(!crew.is_available(), "still recovering after 5 s");
        assert!(crew.recovery_timer > 0.0, "the timer must be counting down");

        crew.tick(1.5);
        assert_eq!(crew.recovery_timer, 0.0, "the timer itself does reach zero");

        // The timer expires, but the crew member is still dead: nothing revives
        // them. `is_available` therefore stays false forever.
        assert!(!crew.is_alive, "C# behaviour: nothing ever sets IsAlive back to true");
        assert!(!crew.is_available());
    }

    #[test]
    fn an_unwounded_crew_member_is_immediately_available() {
        // The one case where the timer is not in the way.
        let mut crew = CrewMember::new("Driver", Vec3::new(-0.5, 0.0, 1.6));
        assert!(crew.is_available());
        crew.tick(10.0);
        assert!(crew.is_available(), "a healthy crew member stays available");
        assert_eq!(crew.recovery_timer, 0.0);
    }

    #[test]
    fn ticking_an_unwounded_crew_member_is_a_no_op() {
        let mut crew = CrewMember::new("Driver", Vec3::new(-0.5, 0.0, 1.6));
        for _ in 0..10 {
            crew.tick(1.0);
        }
        assert!(crew.is_alive);
        assert!(crew.is_available());
        assert_eq!(crew.recovery_timer, 0.0);
    }

    #[test]
    fn the_recovery_timer_never_goes_negative() {
        let mut crew = CrewMember::new("RadioOp", Vec3::new(0.5, 0.1, 0.1));
        crew.wound();
        crew.tick(1000.0);
        assert_eq!(crew.recovery_timer, 0.0);
    }
}