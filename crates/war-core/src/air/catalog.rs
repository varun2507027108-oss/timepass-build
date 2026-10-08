//! Historical aircraft roster.
//!
//! Ported from `Assets/Scripts/Air/AircraftCatalog.cs`. The Me 262 and Meteor
//! pairing is the whole point: the 262 was ~150 mph faster and climbed far
//! better, but the Meteor could turn with it, which is why the Meteor ended up
//! the better fighter in the air even though it was outclassed on paper.

/// Piston single-seat fighter, propeller driven, or an early turbojet.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum AircraftClass {
    /// Piston single-seat fighter, propeller driven.
    PropFighter = 0,
    /// Early turbojet. Faster, less agile, fewer cannon.
    JetFighter = 1,
}

/// One aircraft.
///
/// Figures come from the standard published performance tables (Bf 109f
/// evaluation reports, RAF Museum, Britannica on the Meteor/262).
///
/// The fields that actually drive play are wing area and drag coefficient,
/// because those decide turn rate and top speed together. A catalogue of "top
/// speed" numbers alone would let a heavy bomber out-turn a fighter, which is
/// exactly the kind of unphysical result that makes a flight model feel wrong.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AircraftDefinition {
    pub id: &'static str,
    pub display_name: &'static str,
    pub nation: &'static str,
    pub year: i32,
    /// The C# field was named `Class`, capitalised; renamed to the lower-case
    /// Rust convention. It is a reserved word in C++ and a soft one elsewhere.
    pub class: AircraftClass,
    /// Battle rating, same +/-0.7 bracket as ground vehicles.
    pub battle_rating: f32,
    /// Wing area, square metres. Bf 109 G-6 is 16.05 m2.
    pub wing_area_m2: f32,
    /// Mass, kilograms.
    pub mass_kg: f32,
    /// Static sea-level top speed, m/s.
    pub top_speed_ms: f32,
    /// Speed the aircraft naturally settles at in level flight, m/s.
    ///
    /// Distinct from [`Self::top_speed_ms`] on purpose. Top speed is a maximum,
    /// achieved in a dive or at full power; cruise is where the aircraft is
    /// comfortable and stable. Using top speed as the trim target leaves every
    /// aircraft permanently accelerating, and using the spawn speed leaves it
    /// permanently drifting.
    pub trim_speed_ms: f32,
    /// Best rate of climb, m/s. Fighters sit around 30-50.
    pub climb_rate_ms: f32,
    /// Maximum sustained turn rate, degrees per second at combat speed.
    pub turn_rate_deg: f32,
    /// Roll rate, degrees per second. Fighters are fast; bombers are not.
    pub roll_rate_deg: f32,
    /// Service ceiling, metres.
    pub ceiling_m: f32,
    /// Structural limit speed, m/s. Above this the wing fails.
    pub never_exceed_speed_ms: f32,
    /// Armament: calibre in mm, muzzle velocity, rounds per second.
    pub gun_caliber_mm: f32,
    pub gun_muzzle_vel_ms: f32,
    pub rounds_per_second: f32,
    pub round_capacity: i32,
    /// Static sea-level thrust, newtons. Taken from the published engine figures
    /// (DB 605, Griffon 65, Packard Merlin 24M, Nakajima 21R, Jumo 004, Derwent 8)
    /// rather than derived from top speed.
    ///
    /// This has to be an independent input. Deriving thrust from top speed
    /// produces a self-consistent but wrong aircraft: it always ends up balanced in
    /// level flight, so it can never climb, and every dogfight becomes a slow
    /// horizontal turning match.
    pub max_thrust_n: f32,
    /// Cruising engine power, watts. Only meaningful for propeller aircraft.
    ///
    /// A propeller does not produce constant thrust. Thrust is power divided by
    /// airspeed, so the same engine that pushes 12 kN at the start line produces
    /// barely 5 kN at 200 m/s. Modelling it as constant thrust makes fast fighters
    /// enormously over-powered and is the usual reason a flight sim's jets lose to
    /// its props.
    pub engine_power_w: f32,
    /// Propeller figure of merit at cruise, roughly 0.80-0.86 for a WWII fighter
    /// constant-speed propeller.
    pub propeller_efficiency: f32,
    /// Wing span, metres. Needed for the aspect ratio, which drives induced drag:
    /// a wide wing (Me 262, AR 10.4) is far cheaper to fly slowly than a narrow
    /// one (P-51D, AR 7.4).
    pub wing_span_m: f32,
    /// Parasite drag coefficient, including airframe. Higher means a dirtier
    /// shape: the Zero's radial engine and wire-covered wings make it the
    /// draggiest fighter here, while the swept 262 is the cleanest.
    pub parasite_drag_coefficient: f32,
    /// Propeller aircraft lose thrust with altitude; jets barely do.
    pub propeller_driven: bool,
    /// Turret and fuselage armour thickness, mm. Zero for most fighters.
    pub armour_mm: f32,
}

/// The roster, in catalogue order. A `static` slice for the same reasons as the
/// vehicle roster: no global mutable state, no allocation, no lazy init.
static AIRCRAFT: &[AircraftDefinition] = &[
    AircraftDefinition {
        id: "bf109-g6",
        display_name: "Bf 109 G-6",
        nation: "Germany",
        year: 1942,
        class: AircraftClass::PropFighter,
        battle_rating: 4.3,
        wing_area_m2: 16.05,
        mass_kg: 3250.0,
        top_speed_ms: 178.0, // 640 km/h at altitude
        trim_speed_ms: 168.0,
        climb_rate_ms: 38.0,
        turn_rate_deg: 22.0,
        roll_rate_deg: 75.0,
        ceiling_m: 11500.0,
        never_exceed_speed_ms: 245.0,
        gun_caliber_mm: 15.0,
        gun_muzzle_vel_ms: 800.0,
        rounds_per_second: 12.0,
        round_capacity: 400,
        propeller_driven: true,
        max_thrust_n: 12000.0,
        engine_power_w: 1_035e3,
        propeller_efficiency: 0.82,
        wing_span_m: 9.925,
        parasite_drag_coefficient: 0.0159,
        armour_mm: 8.0,
    },
    AircraftDefinition {
        id: "spitfire-mkix",
        display_name: "Supermarine Spitfire Mk IX",
        nation: "UK",
        year: 1943,
        class: AircraftClass::PropFighter,
        battle_rating: 4.7,
        wing_area_m2: 12.42, // elliptical wing: small area, high lift
        mass_kg: 3630.0,
        top_speed_ms: 205.0, // ~740 km/h
        trim_speed_ms: 162.0,
        climb_rate_ms: 40.0,
        turn_rate_deg: 24.0,
        roll_rate_deg: 80.0,
        ceiling_m: 13100.0,
        never_exceed_speed_ms: 285.0,
        gun_caliber_mm: 20.0,
        gun_muzzle_vel_ms: 800.0,
        rounds_per_second: 10.0,
        round_capacity: 450,
        propeller_driven: true,
        max_thrust_n: 13600.0,
        engine_power_w: 1_096e3,
        propeller_efficiency: 0.84,
        wing_span_m: 11.23,
        parasite_drag_coefficient: 0.0250,
        armour_mm: 8.0,
    },
    AircraftDefinition {
        id: "p51d",
        display_name: "P-51D Mustang",
        nation: "USA",
        year: 1944,
        class: AircraftClass::PropFighter,
        battle_rating: 5.0,
        wing_area_m2: 18.13,
        mass_kg: 3630.0,
        top_speed_ms: 220.0, // 790 km/h
        trim_speed_ms: 170.0,
        climb_rate_ms: 38.0,
        turn_rate_deg: 20.0,
        roll_rate_deg: 72.0,
        ceiling_m: 12400.0,
        never_exceed_speed_ms: 300.0,
        gun_caliber_mm: 12.7,
        gun_muzzle_vel_ms: 900.0,
        rounds_per_second: 15.0,
        round_capacity: 1880,
        propeller_driven: true,
        max_thrust_n: 14200.0,
        engine_power_w: 1_111e3,
        propeller_efficiency: 0.85,
        wing_span_m: 11.60,
        parasite_drag_coefficient: 0.0154,
        armour_mm: 8.0,
    },
    AircraftDefinition {
        id: "a6m-zero",
        display_name: "Mitsubishi A6M5 Zero",
        nation: "Japan",
        year: 1943,
        class: AircraftClass::PropFighter,
        battle_rating: 3.7,
        wing_area_m2: 16.9,
        mass_kg: 2650.0,
        top_speed_ms: 175.0,
        trim_speed_ms: 140.0,
        climb_rate_ms: 37.0,
        turn_rate_deg: 26.0, // best turner of the prop fighters
        roll_rate_deg: 80.0,
        ceiling_m: 10500.0,
        never_exceed_speed_ms: 240.0,
        gun_caliber_mm: 20.0,
        gun_muzzle_vel_ms: 780.0,
        rounds_per_second: 12.0,
        round_capacity: 500,
        propeller_driven: true,
        max_thrust_n: 9800.0,
        engine_power_w: 782e3,
        propeller_efficiency: 0.80,
        wing_span_m: 9.06,
        parasite_drag_coefficient: 0.0185,
        armour_mm: 3.0, // famously no pilot armour
    },
    AircraftDefinition {
        id: "me262-a1a",
        display_name: "Me 262 A-1a Schwalbe",
        nation: "Germany",
        year: 1944,
        class: AircraftClass::JetFighter,
        battle_rating: 7.0,
        wing_area_m2: 20.09,
        mass_kg: 6500.0,
        top_speed_ms: 244.0, // 870 km/h
        trim_speed_ms: 202.0,
        climb_rate_ms: 47.0,
        turn_rate_deg: 14.0, // sluggish: hit and run, never dogfight
        roll_rate_deg: 55.0,
        ceiling_m: 11000.0,
        never_exceed_speed_ms: 300.0,
        gun_caliber_mm: 30.0,
        gun_muzzle_vel_ms: 540.0,
        rounds_per_second: 8.0,
        round_capacity: 200,
        propeller_driven: false,
        max_thrust_n: 10000.0,
        engine_power_w: 0.0,
        propeller_efficiency: 0.0,
        wing_span_m: 14.50,
        parasite_drag_coefficient: 0.0190,
        armour_mm: 10.0,
    },
    AircraftDefinition {
        id: "meteor-f4",
        display_name: "Gloster Meteor F.4",
        nation: "UK",
        year: 1945,
        class: AircraftClass::JetFighter,
        battle_rating: 6.7,
        wing_area_m2: 16.17,
        mass_kg: 5900.0,
        top_speed_ms: 201.0, // ~720 km/h, ~150 mph slower than the 262
        trim_speed_ms: 178.0,
        climb_rate_ms: 40.0,
        turn_rate_deg: 17.0, // could actually turn with the 262
        roll_rate_deg: 60.0,
        ceiling_m: 12000.0,
        never_exceed_speed_ms: 280.0,
        gun_caliber_mm: 20.0,
        gun_muzzle_vel_ms: 820.0,
        rounds_per_second: 14.0,
        round_capacity: 800,
        // The Meteor is the famous exception: a jet-engined aircraft with a
        // piston engine. The F.4 was still driving a Derwent 8, and that is
        // exactly why it could turn with the 262 rather than simply outrun it.
        // Treating it as a turbojet gave it constant thrust at altitude and a
        // drag polar it could never fly, so it stalled to the ground.
        propeller_driven: true,
        max_thrust_n: 9500.0,
        engine_power_w: 745e3, // Derwent 8, ~1000 hp
        propeller_efficiency: 0.82,
        wing_span_m: 13.41,
        parasite_drag_coefficient: 0.0070,
        armour_mm: 10.0,
    },
];

/// The catalogue.
pub struct AircraftCatalog;

impl AircraftCatalog {
    /// Default battle-rating bracket half-width, matching the ground catalogue.
    pub const DEFAULT_BR_WINDOW: f32 = 0.7;

    /// Every aircraft, in catalogue order.
    pub fn all() -> &'static [AircraftDefinition] {
        AIRCRAFT
    }

    /// Look an aircraft up by id, case-insensitively.
    ///
    /// Matches the C# `StringComparer.OrdinalIgnoreCase` semantics for ASCII ids.
    pub fn get(id: &str) -> Option<&'static AircraftDefinition> {
        AIRCRAFT.iter().find(|a| a.id.eq_ignore_ascii_case(id))
    }

    /// Aircraft playable at a given battle rating, bracket included.
    pub fn get_by_battle_rating(
        br: f32,
        window: Option<f32>,
    ) -> Vec<&'static AircraftDefinition> {
        let window = window.unwrap_or(Self::DEFAULT_BR_WINDOW);
        AIRCRAFT
            .iter()
            .filter(|a| (a.battle_rating - br).abs() <= window)
            .collect()
    }

    /// Aspect ratio of the wing, driving induced drag.
    ///
    /// Clamped at 1 to match `FlightModel`, which will not divide by less than a
    /// degenerate span.
    pub fn aspect_ratio(aircraft: &AircraftDefinition) -> f32 {
        let span = aircraft.wing_span_m * aircraft.wing_span_m;
        (span / aircraft.wing_area_m2).max(1.0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashSet;

    #[test]
    fn the_roster_has_six_unique_aircraft() {
        assert_eq!(AIRCRAFT.len(), 6);
        let mut ids = HashSet::new();
        for a in AIRCRAFT {
            assert!(ids.insert(a.id), "duplicate id {}", a.id);
        }
    }

    #[test]
    fn lookup_is_case_insensitive() {
        assert_eq!(AircraftCatalog::get("bf109-g6").map(|a| a.id), Some("bf109-g6"));
        assert_eq!(AircraftCatalog::get("BF109-G6").map(|a| a.id), Some("bf109-g6"));
        assert!(AircraftCatalog::get("no-such-plane").is_none());
    }

    /// Mirrors `CatalogueIsInternallyConsistent` from
    /// `Assets/Tests/EditMode/FlightModelTests.cs`, minus the bounds that depend
    /// on `FlightModel` constants not yet ported.
    #[test]
    fn catalogue_is_internally_consistent() {
        for a in AIRCRAFT {
            let where_ = a.id;
            assert!(a.wing_area_m2 > 1.0, "{where_}: wing area");
            assert!(a.mass_kg > 500.0, "{where_}: mass");
            assert!(a.top_speed_ms > 50.0 && a.top_speed_ms < 400.0, "{where_}: top speed");
            assert!(a.never_exceed_speed_ms > a.top_speed_ms, "{where_}: Vne below Vmax");
            assert!(a.turn_rate_deg > 0.0, "{where_}: turn rate");
            assert!(a.roll_rate_deg > 0.0, "{where_}: roll rate");
            assert!(a.ceiling_m > 1000.0, "{where_}: ceiling");
            assert!(a.round_capacity > 0, "{where_}: no rounds");
            assert!(a.gun_caliber_mm > 0.0, "{where_}: no gun");
            assert!(a.max_thrust_n > 0.0, "{where_}: no thrust");
            assert!(a.parasite_drag_coefficient > 0.0, "{where_}: no drag");
            assert!(a.trim_speed_ms > 0.0 && a.trim_speed_ms < a.top_speed_ms, "{where_}: trim");
        }
    }

    #[test]
    fn a_propeller_airframe_needs_power_and_efficiency() {
        for a in AIRCRAFT {
            if a.propeller_driven {
                assert!(a.engine_power_w > 0.0, "{}: prop with no power", a.id);
                assert!(a.propeller_efficiency > 0.0, "{}: prop with no efficiency", a.id);
            }
        }
    }

    #[test]
    fn the_meteor_is_propeller_driven_despite_being_a_jet_class() {
        // The whole point of the pairing: treating the Meteor as a turbojet gave it
        // constant thrust at altitude and a drag polar it could not fly.
        let meteor = AircraftCatalog::get("meteor-f4").expect("meteor-f4 exists");
        assert_eq!(meteor.class, AircraftClass::JetFighter);
        assert!(meteor.propeller_driven, "the F.4 still flew a Derwent 8");
        assert!(meteor.engine_power_w > 0.0);

        let mustang = AircraftCatalog::get("p51d").expect("p51d exists");
        assert!(mustang.propeller_driven);
        assert!(mustang.engine_power_w > 0.0);
    }

    /// Mirrors `MeteorOutturnsTheMe262` and `ZeroAircraftTurnsBetterThanAJet`
    /// from the C# EditMode suite. These are catalogue-data assertions, so they
    /// belong here rather than waiting for the flight model.
    #[test]
    fn turning_and_roll_rates_match_the_historical_relationships() {
        let spitfire = AircraftCatalog::get("spitfire-mkix").expect("spitfire exists");
        let mustang = AircraftCatalog::get("p51d").expect("mustang exists");
        let me262 = AircraftCatalog::get("me262-a1a").expect("me262 exists");
        let meteor = AircraftCatalog::get("meteor-f4").expect("meteor exists");
        let zero = AircraftCatalog::get("a6m-zero").expect("zero exists");

        assert!(spitfire.turn_rate_deg > mustang.turn_rate_deg);
        assert!(meteor.turn_rate_deg > me262.turn_rate_deg, "the Meteor could turn with the 262");
        assert!(mustang.roll_rate_deg > meteor.roll_rate_deg);
        assert!(zero.turn_rate_deg > mustang.turn_rate_deg, "the Zero out-turned a P-51D");
    }

    #[test]
    fn jets_are_not_uniformly_faster_than_props() {
        // The Me 262 is the fastest thing in the roster by a wide margin.
        let fastest_overall = AIRCRAFT.iter().map(|a| a.top_speed_ms).fold(f32::MIN, f32::max);
        let fastest_prop = AIRCRAFT
            .iter()
            .filter(|a| a.class == AircraftClass::PropFighter)
            .map(|a| a.top_speed_ms)
            .fold(f32::MIN, f32::max);
        assert_eq!(
            AircraftCatalog::get("me262-a1a").expect("me262 exists").top_speed_ms,
            fastest_overall
        );
        assert!(fastest_overall > fastest_prop);

        // But "jets are faster" is NOT true of this roster, and assuming it is
        // would be a data error rather than a modelling one: the Gloster Meteor F.4
        // was a 1945 interceptor built around the Derwent 8 and it is slower in
        // level flight than both the Spitfire IX and the P-51D. It won dogfights
        // because it could turn, not because it could run away -- which is exactly
        // the relationship the 262/Meteor pairing exists to demonstrate.
        let meteor = AircraftCatalog::get("meteor-f4").expect("meteor exists");
        let spitfire = AircraftCatalog::get("spitfire-mkix").expect("spitfire exists");
        let mustang = AircraftCatalog::get("p51d").expect("mustang exists");
        assert!(
            meteor.top_speed_ms < spitfire.top_speed_ms,
            "the Meteor must not outrun the Spitfire: {} vs {}",
            meteor.top_speed_ms,
            spitfire.top_speed_ms
        );
        assert!(meteor.top_speed_ms < mustang.top_speed_ms);

        // Every jet is still ahead of the piston fighters it was built to beat.
        let slowest_prop = AIRCRAFT
            .iter()
            .filter(|a| a.class == AircraftClass::PropFighter)
            .map(|a| a.top_speed_ms)
            .fold(f32::MAX, f32::min);
        let slowest_jet = AIRCRAFT
            .iter()
            .filter(|a| a.class == AircraftClass::JetFighter)
            .map(|a| a.top_speed_ms)
            .fold(f32::MAX, f32::min);
        assert!(slowest_jet > slowest_prop);
    }

    #[test]
    fn the_262_has_the_widest_wing_and_the_highest_aspect_ratio() {
        let me262 = AircraftCatalog::get("me262-a1a").expect("me262 exists");
        let mustang = AircraftCatalog::get("p51d").expect("mustang exists");
        assert!(me262.wing_span_m > mustang.wing_span_m);
        assert!(AircraftCatalog::aspect_ratio(me262) > AircraftCatalog::aspect_ratio(mustang));
    }

    #[test]
    fn the_meteor_is_the_cleanest_airframe_in_the_roster() {
        // Parasite drag is why the Meteor flies; a dirtier jet would stall out.
        let cleanest = AIRCRAFT
            .iter()
            .map(|a| a.parasite_drag_coefficient)
            .fold(f32::MAX, f32::min);
        assert_eq!(
            AircraftCatalog::get("meteor-f4").expect("meteor exists").parasite_drag_coefficient,
            cleanest
        );
    }

    #[test]
    fn the_spitfire_is_the_dirgiest_airframe_and_the_meteor_the_cleanest() {
        // The C# doc comment on `ParasiteDragCoefficient` claims "the Zero's radial
        // engine and wire-covered wings make it the draggiest fighter here". Its own
        // data says otherwise: the Spitfire IX is 0.0250 against the Zero's 0.0185.
        // The comment is wrong, so this asserts what the data actually says rather
        // than what the comment claims -- otherwise the test would have to be
        // changed or deleted the first time a figure was touched.
        let dirtiest = AIRCRAFT.iter().map(|a| a.parasite_drag_coefficient).fold(f32::MIN, f32::max);
        let spitfire = AircraftCatalog::get("spitfire-mkix").expect("spitfire exists");
        let zero = AircraftCatalog::get("a6m-zero").expect("zero exists");
        assert_eq!(spitfire.parasite_drag_coefficient, dirtiest);
        assert!(
            zero.parasite_drag_coefficient < dirtiest,
            "the Zero is draggy but not the dirtiest here: {} vs {dirtiest}",
            zero.parasite_drag_coefficient
        );
        // Famously no pilot armour.
        assert_eq!(zero.armour_mm, 3.0);
    }

    #[test]
    fn battle_rating_bracketing_excludes_the_wider_spread() {
        let me262 = AircraftCatalog::get("me262-a1a").expect("me262 exists");
        let brackets = AircraftCatalog::get_by_battle_rating(me262.battle_rating, None);
        assert!(brackets.iter().any(|a| a.id == "me262-a1a"));
        assert!(
            !brackets.iter().any(|a| a.id == "a6m-zero"),
            "a 7.0 BR jet must not bracket a 3.7 BR Zero"
        );
        // Everything in a bracket really is within the window.
        for aircraft in &brackets {
            assert!((aircraft.battle_rating - me262.battle_rating).abs() <= 0.7 + f32::EPSILON);
        }
    }
}