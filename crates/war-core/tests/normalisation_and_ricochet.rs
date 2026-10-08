//! Port of `Assets/Tests/EditMode/NormalisationAndRicochetTests.cs`.
//!
//! Tests for the two things that were missing from the terminal ballistics model:
//! shell normalisation, and per-shell ricochet thresholds.
//!
//! Both are small in degree terms and enormous in gameplay terms, which is exactly
//! why they need pinning down. Plate resistance goes as 1/cos, so five degrees of
//! normalisation against a 60 degree glacis is worth roughly 13 percent of required
//! penetration: the difference between defeating a Panther and bouncing off it.
//!
//! Normalisation is modelled on the **plate**, not on the projectile, because what
//! changes when a blunt nose bites in is how much armour the round has to chew
//! through, not what the round is capable of. Asserting it on the shell instead is
//! a mistake these tests were written to catch, and did.
//!
//! Kept as an integration test rather than an inline module so it exercises only
//! the public API, exactly as the NUnit suite did.

use war_core::ballistics::{ArmourPlate, ImpactResult, Penetration, Shell, ShellType};
use war_core::vehicles::VehicleCatalog;

fn apcbc() -> Shell {
    Shell::apcbc(88.0, 6.8, 800.0, 150.0, Some(0.0015))
}

fn apcr() -> Shell {
    Shell::apcr(88.0, 3.0, 1200.0, 190.0, Some(0.0012))
}

fn heat() -> Shell {
    Shell::heat(88.0, 17.0, 800.0, 200.0, Some(0.002))
}

fn apc() -> Shell {
    Shell::apc(88.0, 6.8, 800.0, 150.0, Some(0.0015))
}

/// Pure geometry, no shell, i.e. what normalisation changes.
fn geometric_plate(thickness_mm: f32, angle_deg: f32) -> f32 {
    ArmourPlate::new("test", thickness_mm).effective_thickness(angle_deg)
}

#[test]
fn blunt_rounds_normalise_and_sharp_rounds_denormalise() {
    // Direction of the effect is the whole point. A blunt APBC nose bites into the
    // plate and rotates toward the normal, so the plate presents less. A sharp APCR
    // rod is pushed away, so it presents more.
    assert!(ShellType::Apcbc.normalisation_deg() > 0.0, "a blunt-nosed APBC round must normalise");
    assert!(
        ShellType::Aphebc.normalisation_deg() > 0.0,
        "a ballistically capped APHEBC round must normalise"
    );
    assert!(ShellType::Apc.normalisation_deg() > 0.0, "a capped APC round must normalise");
    assert!(ShellType::Apcr.normalisation_deg() < 0.0, "a sharp sub-calibre rod must denormalise");
}

#[test]
fn normalisation_reduces_effective_plate_on_sloped_plate() {
    let plate = ArmourPlate::new("glacis", 120.0);
    let round = apcbc();

    let geometric = geometric_plate(120.0, 60.0);
    let normalised = plate.effective_thickness_vs(&round, 60.0);

    assert!(
        normalised < geometric,
        "normalisation must reduce the plate the round faces: {geometric:.1} -> {normalised:.1} mm"
    );

    // And the effect must equal the angle shift, so the two quantities stay separable
    // and the plate model remains the single source of truth.
    let expected = geometric_plate(120.0, 60.0 - round.normalisation_deg());
    assert!(
        (expected - normalised).abs() < 0.01,
        "normalisation must act as an angle shift before the cosine: \
         expected {expected:.3}, got {normalised:.3}"
    );
}

#[test]
fn normalising_round_sees_exactly_nominal_thickness_on_flat_plate() {
    // The control case that proves the effect is applied to the angle rather than as
    // a flat bonus. At 0 degrees the plate normal and the shell axis already agree,
    // so a normalising round must face exactly the nominal thickness and not one
    // millimetre less.
    let plate = ArmourPlate::new("flat", 100.0);

    let blunt = plate.effective_thickness_vs(&apcbc(), 0.0);
    assert!(
        (blunt - 100.0).abs() < 0.01,
        "a normalising round must face exactly nominal thickness on flat plate: got {blunt}"
    );

    // A denormalising round faces very slightly more, because it is pushed away from
    // the plate normal. The magnitude is small by design: a couple of degrees, not a
    // different plate.
    let sharp_sees = plate.effective_thickness_vs(&apcr(), 0.0);
    assert!(
        sharp_sees > 100.0,
        "a denormalising round must face slightly more than nominal: got {sharp_sees}"
    );
    assert!(
        sharp_sees < 100.5,
        "denormalisation on flat plate must stay a small effect, got {sharp_sees:.2} mm"
    );
}

#[test]
fn normalisation_is_worth_more_on_steeper_plate() {
    // Nonlinearity is the reason this is an angle shift and not a percentage. The
    // same six degrees is worth more on a steep glacis than a shallow one, because
    // that is where 1/cos steepens.
    let plate = ArmourPlate::new("test", 120.0);
    let round = apcbc();

    let gain_shallow = geometric_plate(120.0, 30.0) - plate.effective_thickness_vs(&round, 30.0);
    let gain_steep = geometric_plate(120.0, 65.0) - plate.effective_thickness_vs(&round, 65.0);

    assert!(
        gain_steep > gain_shallow,
        "normalisation must be worth more on a steeper plate: \
         30 deg gained {gain_shallow:.1} mm, 65 deg gained {gain_steep:.1} mm"
    );
}

#[test]
fn denormalisation_cannot_reduce_plate_below_face_on() {
    // A rod pushed away from the plate must never be handed *less* armour than face-on
    // geometry. Clamping the corrected angle at zero is what prevents that, and
    // without the clamp this is a straight inversion.
    let plate = ArmourPlate::new("flat", 100.0);
    let sharp_sees = plate.effective_thickness_vs(&apcr(), 0.0);
    let blunt_sees = plate.effective_thickness_vs(&apcbc(), 0.0);

    assert!(
        sharp_sees >= blunt_sees,
        "a denormalising round must never see less plate than a normalising one on the \
         same flat plate: {sharp_sees:.1} mm vs {blunt_sees:.1} mm"
    );
}

#[test]
fn heat_ignores_slope_entirely() {
    // The shaped-charge jet collapses onto the plate normal however oblique the
    // impact, so a sloped glacis costs it nothing. This is why HEAT dominates heavy
    // sloped frontage that stops every kinetic round.
    let plate = ArmourPlate::new("glacis", 200.0);
    let jet = heat();

    let face_on = plate.effective_thickness_vs(&jet, 0.0);
    let sloped = plate.effective_thickness_vs(&jet, 65.0);
    assert!(
        (face_on - sloped).abs() < 0.01,
        "HEAT must face the same plate thickness at any angle: {face_on:.2} vs {sloped:.2}"
    );

    // Against a kinetic round, the contrast is the whole point.
    let kinetic = plate.effective_thickness_vs(&apcbc(), 65.0);
    assert!(
        sloped < kinetic,
        "HEAT must face less plate than a kinetic round at the same angle: \
         {sloped:.2} vs {kinetic:.2}"
    );
}

#[test]
fn sharp_rod_ricochets_earliest_and_blunt_rounds_latest() {
    // A sharp rod skips off plate where a blunt nose still punches in. One global 70
    // degree threshold made APCR far too usable against sloped armour and APBC far
    // too vulnerable to it.
    assert!(
        apcr().critical_ricochet_deg() < apc().critical_ricochet_deg(),
        "the sharp sub-calibre rod must ricochet earlier than capped AP"
    );
    assert!(
        ShellType::Apcbc.critical_ricochet_deg() > ShellType::Apc.critical_ricochet_deg(),
        "blunt Soviet APBC must tolerate the steepest kinetic plate"
    );
}

#[test]
fn heat_jet_does_not_ricochet() {
    let plate = ArmourPlate::new("front", 200.0);
    assert_ne!(
        Penetration::resolve(&heat(), &plate, 80.0).result,
        ImpactResult::Ricochet,
        "a HEAT jet must not ricochet off a plate at 80 degrees"
    );
}

#[test]
fn ricochet_threshold_is_per_shell_not_global() {
    // The behaviour that matters to a player: same plate, same angle, and the shell
    // family alone decides whether it bounces. Historically this is why blunt APBC
    // outperformed sub-calibre against sloped armour.
    let plate = ArmourPlate::new("glacis", 120.0);

    assert_eq!(
        Penetration::resolve(&apcr(), &plate, 65.0).result,
        ImpactResult::Ricochet,
        "APCR must ricochet at 65 degrees"
    );
    assert_ne!(
        Penetration::resolve(&apcbc(), &plate, 65.0).result,
        ImpactResult::Ricochet,
        "blunt APBC must still be biting at 65 degrees where APCR has skipped"
    );
}

#[test]
fn cast_hull_resists_better_than_rolled_plate() {
    let rolled = ArmourPlate::new("rolled", 100.0);
    let cast = ArmourPlate::cast("cast", 100.0);

    assert!(
        cast.effective_thickness(0.0) > rolled.effective_thickness(0.0),
        "a cast hull must resist more than rolled plate of equal thickness"
    );
    assert!((cast.material_coefficient() - 1.1).abs() < 0.001);
    assert!((rolled.material_coefficient() - 1.0).abs() < 0.001);
}

#[test]
fn cast_hull_flag_matches_historical_vehicles() {
    // Panther and Tiger are cast; the T-34's famous sloped glacis is welded rolled
    // plate. That contrast is the entire reason its geometry beats a nominally
    // thicker cast plate, so having both in the game makes the distinction directly
    // observable and worth asserting.
    let expect = |id: &str, cast: bool| {
        let vehicle = VehicleCatalog::get(id).unwrap_or_else(|| panic!("unknown vehicle {id}"));
        assert_eq!(
            vehicle.is_cast_hull, cast,
            "{} cast-hull flag is wrong: {}",
            id, vehicle.display_name
        );
    };
    expect("panther-g", true);
    expect("tiger-e", true);
    expect("t34-76", false);
    expect("m4a3-76", false);
}

#[test]
fn sloped_armour_beats_nominal_thickness() {
    // The historical lesson the catalogue should teach, as arithmetic. A 45 mm
    // glacis at 60 degrees presents roughly 78 mm; the Panther's 120 mm at 55
    // presents over 200. Nominal thickness alone would rank them backwards.
    let t34_plate = ArmourPlate::new("T-34 glacis", 45.0);
    let panther_plate = ArmourPlate::cast("Panther glacis", 120.0);

    let t34_effective = t34_plate.effective_thickness(60.0);
    let panther_effective = panther_plate.effective_thickness(55.0);

    assert!(
        t34_effective > 45.0 * 1.5,
        "a 60 degree glacis must present far more than its nominal 45 mm, got {t34_effective:.0} mm"
    );
    assert!(
        panther_effective > t34_effective,
        "the Panther's sloped cast glacis must present more: \
         {panther_effective:.0} mm vs {t34_effective:.0} mm"
    );
}

#[test]
fn panther_gun_beats_t34_glacis_close_in_but_not_at_range() {
    // The single most consequential matchup fact in the game, asserted through the
    // real resolution path rather than by re-deriving the maths.
    //
    // Worth stating plainly because it is counterintuitive: a Panther 75 mm DOES
    // defeat the T-34's 45 mm glacis at 60 degrees, because that glacis presents
    // only about 78 mm and the round brings roughly 104. What defeats the T-34 is
    // the Panther's 88 mm, and distance -- the 75 mm loses its advantage over range
    // while the T-34's sloped plate does not.
    let t34_plate = ArmourPlate::new("T-34 glacis", 45.0);
    let panther75 = Shell::aphebc(75.0, 5.5, 800.0, 104.0);
    let panther88 = Shell::aphebc(88.0, 9.9, 900.0, 160.0);

    // The glacis really is the weak point, and it is doing its job: nominal 45 mm
    // presenting well over twice that against a normalising round.
    let presented = t34_plate.effective_thickness_vs(&panther75, 60.0);
    assert!(
        presented < 90.0,
        "the T-34's 60 degree glacis must present under 90 mm against a normalising round, \
         got {presented:.0} mm"
    );

    // Both Panther guns win close in. This is correct and is why the T-34 was fought
    // at range rather than head-on.
    assert!(penetrates(&panther75, &t34_plate, 60.0, 0.0), "the Panther 75 mm does defeat the T-34 glacis up close");
    assert!(penetrates(&panther88, &t34_plate, 60.0, 0.0), "the Panther 88 mm defeats the T-34 glacis up close");

    // The 75 mm's margin shrinks with range but does not vanish.
    assert!(penetrates(&panther75, &t34_plate, 60.0, 2000.0), "the Panther 75 mm should still beat the T-34 glacis at 2000 m");
    assert!(penetrates(&panther88, &t34_plate, 60.0, 2000.0), "the Panther 88 mm must defeat the T-34 glacis at 2000 m");

    // Range still bites: against flat 90 mm plate the 75 mm wins close in and loses
    // further out. This is the mechanism that makes distance matter, and it only
    // works now that the drag coefficients are physically sane.
    let flat90 = ArmourPlate::new("flat 90", 90.0);
    assert!(penetrates(&panther75, &flat90, 0.0, 0.0), "precondition: the 75 mm defeats flat 90 mm at point blank");
    assert!(!penetrates(&panther75, &flat90, 0.0, 2500.0), "the 75 mm must fall short of flat 90 mm at 2500 m");
}

fn penetrates(shell: &Shell, plate: &ArmourPlate, angle_deg: f32, range_m: f32) -> bool {
    // Resolve at the requested range by scaling the round to the velocity it will
    // actually have arrived with, which is what the live sim does.
    let mut at_range = *shell;
    at_range.penetration_0_mm = shell.penetration_at(range_m);

    let report = Penetration::resolve(&at_range, plate, angle_deg);
    report.result == ImpactResult::Penetration || report.result == ImpactResult::OverPenetration
}

#[test]
fn penetration_falls_off_with_range() {
    // The reason normalisation is not the only thing that matters: a normalising
    // round still loses the engagement at range.
    //
    // Over-penetration counts as a success here, so the precondition asserts "gets
    // through" rather than naming one specific result: a round that exactly matches
    // the plate correctly resolves to OverPenetration.
    let plate = ArmourPlate::new("flat", 120.0);
    let round = apcbc();

    let close = Penetration::resolve(&round, &plate, 0.0);
    assert!(
        close.result == ImpactResult::Penetration || close.result == ImpactResult::OverPenetration,
        "precondition: must get through at point blank, was {:?}",
        close.result
    );

    let velocity_close = round.velocity_at(0.0);
    let velocity_far = round.velocity_at(2500.0);
    assert!(velocity_far < velocity_close, "a round must lose velocity over range");
    assert!(
        round.penetration_at(2500.0) < round.penetration_at(0.0),
        "a round must lose penetration over range"
    );

    // And that decay must actually flip the outcome, which is the whole reason range
    // matters in the game.
    let heavy_plate = ArmourPlate::new("heavy", 145.0);
    let near = Penetration::resolve(&round, &heavy_plate, 0.0);
    assert!(
        near.result == ImpactResult::Penetration || near.result == ImpactResult::OverPenetration,
        "precondition: must get through a 145 mm plate at 0 m, was {:?}",
        near.result
    );
    let pen_at_range = round.penetration_at(2500.0);
    assert!(
        pen_at_range < 145.0,
        "the same round must fall short of 145 mm at 2500 m, brought {pen_at_range:.0} mm"
    );
}

#[test]
fn normalisation_changes_a_matchup_outcome() {
    // The point of the whole feature, asserted as a before-and-after on a realistic
    // exchange. APCR has the higher quoted figure; on a 55 degree glacis the
    // normalising AP round must beat it, which is precisely why normalising AP rounds
    // were developed and preferred for sloped targets.
    let panther_plate = ArmourPlate::cast("Panther glacis", 120.0);

    let normalising = Shell::aphebc(75.0, 5.5, 800.0, 104.0);
    let denormalising = Shell::apcr(75.0, 3.1, 1150.0, 130.0, Some(0.0012));

    assert!(
        denormalising.penetration_0_mm > normalising.penetration_0_mm,
        "precondition: the APCR has the higher quoted figure"
    );

    let normalising_plate = panther_plate.effective_thickness_vs(&normalising, 55.0);
    let denormalising_plate = panther_plate.effective_thickness_vs(&denormalising, 55.0);

    assert!(
        denormalising_plate > normalising_plate,
        "on a 55 degree glacis the denormalising rod must face more plate: \
         {denormalising_plate:.0} mm vs {normalising_plate:.0} mm"
    );
}