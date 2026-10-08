//! Authoritative vehicle roster.
//!
//! Ported from `Assets/Scripts/Vehicles/VehicleCatalog.cs`. Historical specs for
//! the WWII lineup; battle rating brackets vehicles for matchmaking the way War
//! Thunder does. Figures are from Bovington Tank Museum spec sheets, ww2db, and
//! the Britannica armour-history article. Armour values are frontal at the
//! driver's plate; production figures exclude schürzen and applique.

use crate::ballistics::{ArmourPlate, Shell, ShellType};
use crate::math::mathf;

/// One catalogue entry.
///
/// All string fields are `&'static str`: they are compile-time literals in
/// practice, and making that a type-level fact removes the `StringComparer`
/// plumbing and the allocation the C# original needed for a dictionary of them.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct VehicleDefinition {
    pub id: &'static str,
    pub display_name: &'static str,
    pub nation: &'static str,
    pub year: i32,
    /// Battle rating, 1.0 to 8.0. Matchmaker brackets to +/- 0.7.
    pub battle_rating: f32,
    pub mass_kg: f32,
    pub engine_kw: f32,
    pub top_speed_kph: f32,
    pub reverse_speed_kph: f32,
    pub crew: i32,
    pub armor_front_mm: f32,
    pub armor_side_mm: f32,
    pub armor_rear_mm: f32,
    /// Frontal plate slope in degrees. Drives the ricochet model.
    pub front_slope_deg: f32,
    /// True for a cast hull, false for welded rolled plate.
    ///
    /// Cast armour is harder and resists perforation better than rolled plate of
    /// the same nominal thickness, which is why the Panther and Tiger are cast
    /// and the T-34 and Sherman are not. The Panther and Tiger were cast
    /// nose-to-tail; the T-34's famous sloped glacis is rolled and welded, which
    /// is precisely why its geometry beats a nominally thicker cast plate.
    pub is_cast_hull: bool,
    pub gun_name: &'static str,
    pub gun_caliber_mm: f32,
    pub muzzle_velocity: f32,
    pub reload_seconds: f32,
    pub shell_penetration_0_mm: f32,
    pub shell_mass_kg: f32,
    /// Ordnance this vehicle's main gun fires.
    ///
    /// In the C# original this field was written for all 19 vehicles and then
    /// never read: [`build_shell`] unconditionally returned an APHEBC round. It
    /// is honoured now, so the field cannot lie about what a vehicle carries.
    /// Every current entry is APHEBC, so behaviour is unchanged -- asserted by
    /// `every_vehicle_still_carries_aphebc`.
    pub shell_type: ShellType,
    /// True for vehicles with a rear-facing casemate gun.
    pub has_casemate_gun: bool,
}

/// The roster, in catalogue order.
///
/// A `static` slice rather than the C# `static Dictionary`. It removes the
/// process-global mutable state, makes the data a compile-time constant, cannot
/// be mutated at runtime by anyone holding a reference, and needs no allocation
/// or lazy initialisation. Nineteen entries make a linear scan free in practice.
static VEHICLES: &[VehicleDefinition] = &[
    // ---- Germany, 1939-45 ----
    VehicleDefinition {
        id: "pziv-h",
        display_name: "Panzer IV Ausf. H",
        nation: "Germany",
        year: 1943,
        battle_rating: 4.0,
        mass_kg: 25000.0,
        engine_kw: 220.0,
        top_speed_kph: 40.0,
        reverse_speed_kph: 16.0,
        crew: 5,
        armor_front_mm: 80.0,
        armor_side_mm: 30.0,
        armor_rear_mm: 20.0,
        front_slope_deg: 12.0,
        is_cast_hull: false,
        gun_name: "75mm KwK 40 L/48",
        gun_caliber_mm: 75.0,
        muzzle_velocity: 750.0,
        reload_seconds: 5.9,
        shell_penetration_0_mm: 111.0,
        shell_mass_kg: 6.8,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    VehicleDefinition {
        id: "panther-g",
        display_name: "Panther Ausf. G",
        nation: "Germany",
        year: 1944,
        battle_rating: 5.3,
        mass_kg: 43000.0,
        engine_kw: 519.0,
        top_speed_kph: 55.0,
        reverse_speed_kph: 22.0,
        crew: 5,
        armor_front_mm: 120.0,
        armor_side_mm: 50.0,
        armor_rear_mm: 40.0,
        front_slope_deg: 55.0,
        is_cast_hull: true,
        gun_name: "75mm KwK 42 L/70",
        gun_caliber_mm: 75.0,
        muzzle_velocity: 936.0,
        reload_seconds: 6.5,
        shell_penetration_0_mm: 172.0,
        shell_mass_kg: 6.8,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: true,
    },
    VehicleDefinition {
        id: "tiger-e",
        display_name: "Tiger I Ausf. E",
        nation: "Germany",
        year: 1942,
        battle_rating: 5.3,
        mass_kg: 57000.0,
        engine_kw: 519.0,
        top_speed_kph: 38.0,
        reverse_speed_kph: 10.0,
        crew: 5,
        armor_front_mm: 100.0,
        armor_side_mm: 80.0,
        armor_rear_mm: 80.0,
        front_slope_deg: 0.0,
        is_cast_hull: true,
        gun_name: "88mm KwK 36 L/56",
        gun_caliber_mm: 88.0,
        muzzle_velocity: 780.0,
        reload_seconds: 8.7,
        shell_penetration_0_mm: 149.0,
        shell_mass_kg: 10.2,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    VehicleDefinition {
        id: "tiger2-e",
        display_name: "Tiger II (King Tiger)",
        nation: "Germany",
        year: 1944,
        battle_rating: 6.7,
        mass_kg: 68000.0,
        engine_kw: 619.0,
        top_speed_kph: 45.0,
        reverse_speed_kph: 12.0,
        crew: 5,
        armor_front_mm: 180.0,
        armor_side_mm: 80.0,
        armor_rear_mm: 80.0,
        front_slope_deg: 0.0,
        is_cast_hull: true,
        gun_name: "88mm KwK 43 L/71",
        gun_caliber_mm: 88.0,
        muzzle_velocity: 1000.0,
        reload_seconds: 12.0,
        shell_penetration_0_mm: 202.0,
        shell_mass_kg: 10.5,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    VehicleDefinition {
        id: "stugiii-g",
        display_name: "StuG III Ausf. G",
        nation: "Germany",
        year: 1942,
        battle_rating: 4.3,
        mass_kg: 23900.0,
        engine_kw: 132.0,
        top_speed_kph: 40.0,
        reverse_speed_kph: 15.0,
        crew: 4,
        armor_front_mm: 80.0,
        armor_side_mm: 30.0,
        armor_rear_mm: 20.0,
        front_slope_deg: 9.0,
        is_cast_hull: false,
        gun_name: "75mm StuK 40 L/43",
        gun_caliber_mm: 75.0,
        muzzle_velocity: 750.0,
        reload_seconds: 5.9,
        shell_penetration_0_mm: 111.0,
        shell_mass_kg: 6.8,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    // ---- USSR ----
    VehicleDefinition {
        id: "t34-76",
        display_name: "T-34/76 Model 1942",
        nation: "USSR",
        year: 1942,
        battle_rating: 3.7,
        mass_kg: 26500.0,
        engine_kw: 370.0,
        top_speed_kph: 53.0,
        reverse_speed_kph: 20.0,
        crew: 4,
        armor_front_mm: 45.0,
        armor_side_mm: 40.0,
        armor_rear_mm: 40.0,
        front_slope_deg: 60.0,
        is_cast_hull: false,
        gun_name: "76.2mm F-34",
        gun_caliber_mm: 76.2,
        muzzle_velocity: 662.0,
        reload_seconds: 7.1,
        shell_penetration_0_mm: 108.0,
        shell_mass_kg: 6.3,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    VehicleDefinition {
        id: "t34-85",
        display_name: "T-34/85 Model 1944",
        nation: "USSR",
        year: 1944,
        battle_rating: 4.7,
        mass_kg: 32000.0,
        engine_kw: 370.0,
        top_speed_kph: 51.0,
        reverse_speed_kph: 20.0,
        crew: 5,
        armor_front_mm: 90.0,
        armor_side_mm: 45.0,
        armor_rear_mm: 45.0,
        front_slope_deg: 47.0,
        is_cast_hull: false,
        gun_name: "85mm ZiS-S-53",
        gun_caliber_mm: 85.0,
        muzzle_velocity: 792.0,
        reload_seconds: 7.6,
        shell_penetration_0_mm: 128.0,
        shell_mass_kg: 9.1,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    VehicleDefinition {
        id: "kv1-1941",
        display_name: "KV-1 Model 1941",
        nation: "USSR",
        year: 1941,
        battle_rating: 4.0,
        mass_kg: 33400.0,
        engine_kw: 366.0,
        top_speed_kph: 35.0,
        reverse_speed_kph: 12.0,
        crew: 5,
        armor_front_mm: 90.0,
        armor_side_mm: 75.0,
        armor_rear_mm: 75.0,
        front_slope_deg: 0.0,
        is_cast_hull: false,
        gun_name: "76.2mm F-32",
        gun_caliber_mm: 76.2,
        muzzle_velocity: 581.0,
        reload_seconds: 8.5,
        shell_penetration_0_mm: 88.0,
        shell_mass_kg: 6.3,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    VehicleDefinition {
        id: "is2-1944",
        display_name: "IS-2 Model 1944",
        nation: "USSR",
        year: 1944,
        battle_rating: 6.0,
        mass_kg: 46000.0,
        engine_kw: 447.0,
        top_speed_kph: 45.0,
        reverse_speed_kph: 15.0,
        crew: 4,
        armor_front_mm: 120.0,
        armor_side_mm: 90.0,
        armor_rear_mm: 90.0,
        front_slope_deg: 20.0,
        is_cast_hull: false,
        gun_name: "122mm D-25T",
        gun_caliber_mm: 122.0,
        muzzle_velocity: 781.0,
        reload_seconds: 15.0,
        shell_penetration_0_mm: 142.0,
        shell_mass_kg: 21.4,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    // ---- USA ----
    VehicleDefinition {
        id: "m4a2-75",
        display_name: "M4A2 Sherman (75mm)",
        nation: "USA",
        year: 1943,
        battle_rating: 3.7,
        mass_kg: 30300.0,
        engine_kw: 335.0,
        top_speed_kph: 39.0,
        reverse_speed_kph: 16.0,
        crew: 5,
        armor_front_mm: 50.0,
        armor_side_mm: 38.0,
        armor_rear_mm: 38.0,
        front_slope_deg: 56.0,
        is_cast_hull: false,
        gun_name: "75mm M3",
        gun_caliber_mm: 75.0,
        muzzle_velocity: 619.0,
        reload_seconds: 6.5,
        shell_penetration_0_mm: 88.0,
        shell_mass_kg: 6.8,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    VehicleDefinition {
        id: "m4a3-76",
        display_name: "M4A3 Sherman (76mm W)",
        nation: "USA",
        year: 1944,
        battle_rating: 4.0,
        mass_kg: 31700.0,
        engine_kw: 335.0,
        top_speed_kph: 40.0,
        reverse_speed_kph: 16.0,
        crew: 5,
        armor_front_mm: 63.0,
        armor_side_mm: 38.0,
        armor_rear_mm: 38.0,
        front_slope_deg: 47.0,
        is_cast_hull: false,
        gun_name: "76mm M1",
        gun_caliber_mm: 76.2,
        muzzle_velocity: 793.0,
        reload_seconds: 6.5,
        shell_penetration_0_mm: 131.0,
        shell_mass_kg: 9.1,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    VehicleDefinition {
        id: "sherman-firefly",
        display_name: "Sherman VC Firefly",
        nation: "UK",
        year: 1944,
        battle_rating: 5.0,
        mass_kg: 34300.0,
        engine_kw: 335.0,
        top_speed_kph: 32.0,
        reverse_speed_kph: 14.0,
        crew: 4,
        armor_front_mm: 63.0,
        armor_side_mm: 38.0,
        armor_rear_mm: 38.0,
        front_slope_deg: 47.0,
        is_cast_hull: false,
        gun_name: "QF 17-pdr",
        gun_caliber_mm: 76.2,
        muzzle_velocity: 823.0,
        reload_seconds: 6.2,
        shell_penetration_0_mm: 149.0,
        shell_mass_kg: 9.1,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    VehicleDefinition {
        id: "m26-pershing",
        display_name: "M26 Pershing",
        nation: "USA",
        year: 1945,
        battle_rating: 6.0,
        mass_kg: 46700.0,
        engine_kw: 335.0,
        top_speed_kph: 40.0,
        reverse_speed_kph: 13.0,
        crew: 5,
        armor_front_mm: 102.0,
        armor_side_mm: 50.0,
        armor_rear_mm: 50.0,
        front_slope_deg: 47.0,
        is_cast_hull: false,
        gun_name: "90mm M3",
        gun_caliber_mm: 90.0,
        muzzle_velocity: 838.0,
        reload_seconds: 8.0,
        shell_penetration_0_mm: 152.0,
        shell_mass_kg: 10.9,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    // ---- Britain ----
    VehicleDefinition {
        id: "churchill-vii",
        display_name: "Churchill Mk VII",
        nation: "UK",
        year: 1944,
        battle_rating: 3.3,
        mass_kg: 31700.0,
        engine_kw: 254.0,
        top_speed_kph: 24.0,
        reverse_speed_kph: 12.0,
        crew: 5,
        armor_front_mm: 152.0,
        armor_side_mm: 63.0,
        armor_rear_mm: 38.0,
        front_slope_deg: 20.0,
        is_cast_hull: false,
        gun_name: "QF 6-pdr",
        gun_caliber_mm: 57.0,
        muzzle_velocity: 502.0,
        reload_seconds: 6.0,
        shell_penetration_0_mm: 84.0,
        shell_mass_kg: 3.2,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    VehicleDefinition {
        id: "cromwell",
        display_name: "Cromwell",
        nation: "UK",
        year: 1944,
        battle_rating: 3.7,
        mass_kg: 26700.0,
        engine_kw: 213.0,
        top_speed_kph: 64.0,
        reverse_speed_kph: 20.0,
        crew: 4,
        armor_front_mm: 76.0,
        armor_side_mm: 38.0,
        armor_rear_mm: 38.0,
        front_slope_deg: 30.0,
        is_cast_hull: false,
        gun_name: "QF 6-pdr",
        gun_caliber_mm: 57.0,
        muzzle_velocity: 502.0,
        reload_seconds: 6.0,
        shell_penetration_0_mm: 84.0,
        shell_mass_kg: 3.2,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    // ---- Japan (deliberately thin armour, hence low BR) ----
    VehicleDefinition {
        id: "type97-chiha",
        display_name: "Type 97 Chi-Ha",
        nation: "Japan",
        year: 1941,
        battle_rating: 2.0,
        mass_kg: 16900.0,
        engine_kw: 150.0,
        top_speed_kph: 42.0,
        reverse_speed_kph: 14.0,
        crew: 3,
        armor_front_mm: 25.0,
        armor_side_mm: 8.0,
        armor_rear_mm: 8.0,
        front_slope_deg: 15.0,
        is_cast_hull: false,
        gun_name: "Type 97 57mm",
        gun_caliber_mm: 57.0,
        muzzle_velocity: 430.0,
        reload_seconds: 5.5,
        shell_penetration_0_mm: 43.0,
        shell_mass_kg: 1.55,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    VehicleDefinition {
        id: "type95-ha-go",
        display_name: "Type 95 Ha-Go",
        nation: "Japan",
        year: 1937,
        battle_rating: 1.3,
        mass_kg: 12700.0,
        engine_kw: 86.0,
        top_speed_kph: 64.0,
        reverse_speed_kph: 22.0,
        crew: 3,
        armor_front_mm: 8.0,
        armor_side_mm: 6.0,
        armor_rear_mm: 6.0,
        front_slope_deg: 20.0,
        is_cast_hull: false,
        gun_name: "Type 95 37mm",
        gun_caliber_mm: 37.0,
        muzzle_velocity: 400.0,
        reload_seconds: 4.5,
        shell_penetration_0_mm: 28.0,
        shell_mass_kg: 0.9,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    // ---- France (free pre-1940, then armoured cars) ----
    VehicleDefinition {
        id: "somua-s35",
        display_name: "SOMUA S35",
        nation: "France",
        year: 1938,
        battle_rating: 1.7,
        mass_kg: 11700.0,
        engine_kw: 147.0,
        top_speed_kph: 72.0,
        reverse_speed_kph: 28.0,
        crew: 3,
        armor_front_mm: 40.0,
        armor_side_mm: 17.0,
        armor_rear_mm: 17.0,
        front_slope_deg: 60.0,
        is_cast_hull: false,
        gun_name: "37mm SA 38",
        gun_caliber_mm: 37.0,
        muzzle_velocity: 435.0,
        reload_seconds: 5.0,
        shell_penetration_0_mm: 46.0,
        shell_mass_kg: 0.9,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
    // ---- Italy ----
    VehicleDefinition {
        id: "m13-40",
        display_name: "M13/40",
        nation: "Italy",
        year: 1940,
        battle_rating: 1.7,
        mass_kg: 15300.0,
        engine_kw: 138.0,
        top_speed_kph: 32.0,
        reverse_speed_kph: 12.0,
        crew: 4,
        armor_front_mm: 50.0,
        armor_side_mm: 22.0,
        armor_rear_mm: 22.0,
        front_slope_deg: 30.0,
        is_cast_hull: false,
        gun_name: "47mm Mod 40",
        gun_caliber_mm: 47.0,
        muzzle_velocity: 430.0,
        reload_seconds: 5.5,
        shell_penetration_0_mm: 52.0,
        shell_mass_kg: 1.5,
        shell_type: ShellType::Aphebc,
        has_casemate_gun: false,
    },
];

/// The catalogue.
pub struct VehicleCatalog;

impl VehicleCatalog {
    /// Default battle-rating bracket half-width. A +/- 0.7 BR window is what keeps
    /// a T-34 from meeting a Tiger II.
    pub const DEFAULT_BR_WINDOW: f32 = 0.7;

    /// Every vehicle, in catalogue order.
    pub fn all() -> &'static [VehicleDefinition] {
        VEHICLES
    }

    /// Look a vehicle up by id, case-insensitively.
    ///
    /// The C# original used `StringComparer.OrdinalIgnoreCase`, which is ordinal
    /// case folding rather than culture-aware comparison, so
    /// `eq_ignore_ascii_case` is an exact match for the ASCII ids in this roster.
    ///
    /// Returns `None` for an unknown id; the C# `Get` threw a
    /// `KeyNotFoundException`, so callers that relied on that should `.expect(..)`.
    pub fn get(id: &str) -> Option<&'static VehicleDefinition> {
        VEHICLES.iter().find(|v| v.id.eq_ignore_ascii_case(id))
    }

    /// Vehicles playable at a given battle rating, bracket included.
    pub fn get_by_battle_rating(
        br: f32,
        window: Option<f32>,
    ) -> Vec<&'static VehicleDefinition> {
        let window = window.unwrap_or(Self::DEFAULT_BR_WINDOW);
        VEHICLES
            .iter()
            .filter(|v| mathf::abs(v.battle_rating - br) <= window)
            .collect()
    }

    /// Build the runtime shell for a vehicle's main gun.
    ///
    /// Honours [`VehicleDefinition::shell_type`]. Every current vehicle is APHEBC,
    /// so this is bit-identical to the C# original's unconditional
    /// `Shell.APHEBC(...)` -- but the catalogue can no longer disagree with itself.
    ///
    /// The two filler-bearing families have no charge weight in the catalogue, so
    /// it is derived from projectile mass using the same 2.5% rule APHEBC uses.
    /// No vehicle currently takes those branches; see
    /// `every_vehicle_still_carries_aphebc`.
    pub fn build_shell(vehicle: &VehicleDefinition) -> Shell {
        let caliber = vehicle.gun_caliber_mm;
        let mass = vehicle.shell_mass_kg;
        let v0 = vehicle.muzzle_velocity;
        let pen0 = vehicle.shell_penetration_0_mm;
        let filler = mass * 0.025;

        match vehicle.shell_type {
            ShellType::Aphebc => Shell::aphebc(caliber, mass, v0, pen0),
            ShellType::Aphe => Shell::aphe(caliber, mass, v0, pen0, filler, None),
            ShellType::He => Shell::he(caliber, mass, v0, filler, None),
            ShellType::Apcbc => Shell::apcbc(caliber, mass, v0, pen0, None),
            ShellType::Apc => Shell::apc(caliber, mass, v0, pen0, None),
            ShellType::Apcr => Shell::apcr(caliber, mass, v0, pen0, None),
            ShellType::Heat => Shell::heat(caliber, mass, v0, pen0, None),
        }
    }

    /// The three armour plates a vehicle presents, in the order the hit-resolution
    /// code expects to find them.
    ///
    /// **Bug fix.** The C# `BuildDamageModel` hardcoded `isCast: false` for the
    /// front plate, contradicting [`VehicleDefinition::is_cast_hull`]. `BattleSim`
    /// honoured the flag on its own path, so the two code paths disagreed on cast
    /// hull physics for the Panther and the Tiger. The flag is now honoured here
    /// too.
    pub fn build_plates(vehicle: &VehicleDefinition) -> [ArmourPlate; 3] {
        [
            if vehicle.is_cast_hull {
                ArmourPlate::cast("front", vehicle.armor_front_mm)
            } else {
                ArmourPlate::new("front", vehicle.armor_front_mm)
            },
            ArmourPlate::new("side", vehicle.armor_side_mm),
            ArmourPlate::new("rear", vehicle.armor_rear_mm),
        ]
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashSet;

    #[test]
    fn the_roster_has_nineteen_unique_vehicles() {
        assert_eq!(VEHICLES.len(), 19);
        let mut ids = HashSet::new();
        for v in VEHICLES {
            assert!(ids.insert(v.id), "duplicate id {}", v.id);
        }
    }

    #[test]
    fn lookup_is_case_insensitive() {
        // C# used StringComparer.OrdinalIgnoreCase, so "PzIV-H" must resolve.
        assert_eq!(VehicleCatalog::get("pziv-h").map(|v| v.id), Some("pziv-h"));
        assert_eq!(VehicleCatalog::get("PZIV-H").map(|v| v.id), Some("pziv-h"));
        assert_eq!(VehicleCatalog::get("T34-76").map(|v| v.id), Some("t34-76"));
        assert!(VehicleCatalog::get("no-such-tank").is_none());
    }

    #[test]
    fn catalogue_is_internally_consistent() {
        for v in VEHICLES {
            let where_ = v.id;
            assert!(v.armor_front_mm >= v.armor_side_mm, "{where_}: front armour below side");
            assert!(v.armor_side_mm >= v.armor_rear_mm, "{where_}: side armour below rear");
            assert!(v.shell_penetration_0_mm > 0.0, "{where_}: no penetration figure");
            assert!(v.gun_caliber_mm > 0.0, "{where_}: no calibre");
            assert!(v.muzzle_velocity > 0.0, "{where_}: no muzzle velocity");
            assert!(v.reload_seconds > 0.0, "{where_}: no reload time");
            assert!(v.battle_rating > 0.0 && v.battle_rating < 8.0, "{where_}: BR out of range");
            assert!(v.engine_kw > 0.0, "{where_}: no engine power");
            assert!(v.top_speed_kph > 0.0, "{where_}: no top speed");
            assert!(v.mass_kg > 0.0, "{where_}: no mass");
            assert!((0.0..=90.0).contains(&v.front_slope_deg), "{where_}: impossible slope");
            assert!(v.crew >= 1, "{where_}: no crew");
            assert!(!v.display_name.is_empty() && !v.gun_name.is_empty(), "{where_}: blank name");
        }
    }

    /// Proves the `shell_type` fix changed nothing. In the C# original the field
    /// was written for all 19 vehicles and ignored; now it is honoured, so this
    /// test is what guarantees behaviour parity.
    #[test]
    fn every_vehicle_still_carries_aphebc() {
        for v in VEHICLES {
            assert_eq!(
                v.shell_type,
                ShellType::Aphebc,
                "{} now carries {:?}; that changes gameplay and this assertion is where \
                 you find out, not in a match",
                v.id,
                v.shell_type
            );
        }
    }

    #[test]
    fn build_shell_reproduces_the_csharp_aphebc_call() {
        // The C# body was exactly:
        //   Shell.APHEBC(v.GunCaliberMm, v.ShellMassKg, v.MuzzleVelocity, v.ShellPenetration0Mm)
        for v in VEHICLES {
            let expected =
                Shell::aphebc(v.gun_caliber_mm, v.shell_mass_kg, v.muzzle_velocity, v.shell_penetration_0_mm);
            assert_eq!(VehicleCatalog::build_shell(v), expected, "{} shell mismatch", v.id);
        }
    }

    #[test]
    fn build_shell_actually_reacts_to_a_different_shell_type() {
        // Without this the fix is untested: build_shell could ignore the field
        // again and every other test would still pass.
        let mut he = *VehicleCatalog::get("t34-76").expect("t34-76 exists");
        he.shell_type = ShellType::He;
        let shell = VehicleCatalog::build_shell(&he);
        assert_eq!(shell.shell_type, ShellType::He);
        assert_eq!(shell.penetration_0_mm, 0.0);

        let mut heat = he;
        heat.shell_type = ShellType::Heat;
        assert!(VehicleCatalog::build_shell(&heat).ignores_slope());
    }

    /// The cast-hull fix. In C#, `BuildDamageModel` hardcoded `isCast: false`, so
    /// a Panther's front plate was modelled as rolled while `BattleSim` modelled
    /// the same hit as cast.
    #[test]
    fn build_plates_honours_the_cast_hull_flag() {
        let panther = VehicleCatalog::get("panther-g").expect("panther-g exists");
        assert!(panther.is_cast_hull);
        let plates = VehicleCatalog::build_plates(panther);
        assert_eq!(plates[0].name, "front");
        assert!(plates[0].is_cast, "the Panther's front plate is cast hull");
        assert!((plates[0].material_coefficient() - 1.1).abs() < 1e-6);

        let t34 = VehicleCatalog::get("t34-76").expect("t34-76 exists");
        assert!(!t34.is_cast_hull);
        let plates = VehicleCatalog::build_plates(t34);
        assert!(!plates[0].is_cast, "the T-34's glacis is rolled and welded");
        assert_eq!(plates[0].material_coefficient(), 1.0);
    }

    #[test]
    fn side_and_rear_plates_are_always_rolled() {
        for v in VEHICLES {
            let plates = VehicleCatalog::build_plates(v);
            assert_eq!(plates[1].thickness_mm, v.armor_side_mm);
            assert_eq!(plates[2].thickness_mm, v.armor_rear_mm);
            assert!(!plates[1].is_cast && !plates[2].is_cast, "{}", v.id);
        }
    }

    #[test]
    fn battle_rating_bracketing_keeps_a_t34_away_from_a_tiger_ii() {
        let t34 = VehicleCatalog::get("t34-76").expect("t34-76 exists");
        let tiger = VehicleCatalog::get("tiger2-e").expect("tiger2-e exists");
        // The King Tiger really is 3.0 BR above the T-34.
        assert!((tiger.battle_rating - t34.battle_rating) > 2.0);

        let brackets = VehicleCatalog::get_by_battle_rating(t34.battle_rating, None);
        assert!(brackets.iter().any(|v| v.id == "t34-76"));
        assert!(
            !brackets.iter().any(|v| v.id == "tiger2-e"),
            "a 3.7 BR tank must not bracket a 6.7 BR tank at the default +/-0.7 window"
        );

        // A zero-width window is exact and inclusive, and BR 3.7 is shared by the
        // T-34/76, the M4A2 Sherman and the Cromwell -- so it returns three, not one.
        let exact = VehicleCatalog::get_by_battle_rating(t34.battle_rating, Some(0.0));
        assert_eq!(exact.len(), 3, "three vehicles share BR 3.7");
        for vehicle in &exact {
            assert_eq!(vehicle.battle_rating, t34.battle_rating);
        }
    }

    #[test]
    fn every_nation_in_the_roster_is_represented_in_the_original_groupings() {
        for nation in ["Germany", "USSR", "USA", "UK", "Japan", "France", "Italy"] {
            assert!(
                VEHICLES.iter().any(|v| v.nation == nation),
                "no vehicle from {nation}"
            );
        }
    }
}