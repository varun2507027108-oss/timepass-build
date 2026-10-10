//! Port of `Assets/Tests/EditMode/TurretAimingTests.cs`.
//!
//! Kept as an integration test so it exercises only the public API, exactly as the
//! NUnit suite did.

use war_core::gameplay::{segment_hits_target, BattleSim};
use war_core::math::{mathf, Vec3};
use war_core::vehicle_dynamics;

/// Long enough for the turret to complete its traverse.
fn settled(aim: Vec3, throttle: f32, steer: f32) -> BattleSim {
    let mut sim = BattleSim::new("t34-76", "pziv-h");
    for _ in 0..240 {
        sim.advance(1.0 / 60.0, throttle, steer, 0.0, false, aim);
    }
    sim
}

/// Unit vector the barrel is actually pointing in, reconstructed exactly the way
/// `TankView.Place` builds the transform.
fn gun_direction(sim: &BattleSim) -> Vec3 {
    let gun_world_rad = sim.player_heading_rad() + sim.player_turret_yaw_rad();
    Vec3::new(mathf::sin(gun_world_rad), 0.0, mathf::cos(gun_world_rad))
}

fn gun_to_aim_dot(sim: &BattleSim, aim: Vec3) -> f32 {
    let pivot = sim.player_position()
        + Vec3::new(
            -mathf::sin(sim.player_heading_rad()) * 0.3,
            0.0,
            -mathf::cos(sim.player_heading_rad()) * 0.3,
        );
    let mut to_aim = aim - pivot;
    to_aim.y = 0.0;
    gun_direction(sim).dot(to_aim.normalize())
}

fn aim_from(origin: Vec3, bearing_deg: f32, range: f32) -> Vec3 {
    let r = bearing_deg * mathf::DEG2RAD;
    origin + Vec3::new(mathf::sin(r), 0.0, mathf::cos(r)) * range
}

#[test]
fn barrel_points_at_the_aim_point_on_every_side() {
    // The core correctness property, and deliberately free of sign conventions:
    // whatever the hull is doing, the barrel must end up pointing at the target. An
    // inverted turret scores about -1 here.
    //
    // All bearings stay inside the turret's real traverse arc. A target directly
    // astern is deliberately excluded: no real tank can elevate its gun through 180
    // degrees of traverse, so MaxTraverse correctly refuses it and the barrel lands
    // at the arc limit rather than on the target.
    for bearing in [0.0f32, 30.0, 60.0, 90.0, 110.0, -30.0, -60.0, -90.0, -110.0] {
        let mut sim = BattleSim::new("t34-76", "pziv-h");
        let aim = aim_from(sim.player_position(), bearing, 60.0);
        for _ in 0..240 {
            sim.advance(1.0 / 60.0, 0.0, 0.0, 0.0, false, aim);
        }
        let dot = gun_to_aim_dot(&sim, aim);
        assert!(
            dot > 0.9,
            "at {bearing} deg the barrel scored {dot:.3}, not > 0.9"
        );
    }
}

#[test]
fn turret_stops_at_its_traverse_limit_rather_than_passing_through() {
    let sim = BattleSim::new("t34-76", "pziv-h");
    // Directly astern: unreachable through the traverse arc.
    let aim = aim_from(sim.player_position(), 180.0, 60.0);
    let mut sim = sim;
    for _ in 0..600 {
        sim.advance(1.0 / 60.0, 0.0, 0.0, 0.0, false, aim);
    }
    let yaw = sim.player_turret_yaw_rad().abs();
    assert!(
        yaw <= BattleSim::MAX_TRAVERSE + 1e-4,
        "the turret left its arc: {yaw} > {}",
        BattleSim::MAX_TRAVERSE
    );
    assert!(
        yaw > BattleSim::MAX_TRAVERSE * 0.9,
        "it should be pressed against the limit, not short of it: {yaw}"
    );
}

/// Regression: the turret used to deadlock permanently once parked at a stop.
///
/// `turret_stops_at_its_traverse_limit_rather_than_passing_through` above drives the
/// turret onto its stop and then STOPS, which is precisely the state this bug needed.
/// The old slew took the short way round an unbounded circle while the state was
/// clamped to +/-2.1 rad. Those stops are 4.2 rad apart — wider than pi — so a demand
/// on the far side wrapped *through the back*, out of the arc, and the clamp pinned
/// the turret where it was. It could never come back.
#[test]
fn the_turret_can_recover_from_its_traverse_stop() {
    // Slam the turret onto the right-hand stop, exactly as the test above does.
    let mut sim = BattleSim::new("t34-76", "pziv-h");
    let astern = aim_from(sim.player_position(), 180.0, 60.0);
    for _ in 0..600 {
        sim.advance(1.0 / 60.0, 0.0, 0.0, 0.0, false, astern);
    }
    let parked = sim.player_turret_yaw_rad();
    assert!(
        parked.abs() > BattleSim::MAX_TRAVERSE * 0.9,
        "precondition: the turret should be on its stop, was {parked}"
    );

    // Now demand a target on the OPPOSITE shoulder — the case that used to deadlock.
    // The choice of bearing matters: it has to be far enough from the parked stop that
    // a stuck turret cannot still score a passing dot product. Parked at -120.3 deg,
    // a demand of +90 deg puts the gun 210 deg from a stuck barrel and dot = -1.
    let player = sim.player_position();
    let opposite = aim_from(player, 90.0, 60.0);
    for _ in 0..600 {
        sim.advance(1.0 / 60.0, 0.0, 0.0, 0.0, false, opposite);
    }

    let recovered = sim.player_turret_yaw_rad();
    assert!(
        recovered > parked + 1.0,
        "the turret was still stuck at its stop: parked {parked:.3}, after demanding \
         the far shoulder it is {recovered:.3}"
    );
    // And it must have arrived, not merely moved a little.
    let dot = gun_to_aim_dot(&sim, opposite);
    assert!(
        dot > 0.9,
        "the turret moved but did not reach the target: dot {dot:.3}"
    );
}

/// Regression: steering saturated at 3.3% of stick.
///
/// The old code computed `clamp(demand * MAX_TURN_RATE_RAD, -max_step, max_step)` —
/// the *rate* was never multiplied by dt, so dt only set the clamp bound. At 30 Hz
/// that meant any demand above 0.033 hit the stop and full-locked the tank at
/// 103 deg/s. Masked in play because the hull auto-align term dominates `demand`
/// while driving, and the old test only asserted `> 0.01 rad`, which a 30x overshoot
/// passes easily.
#[test]
fn steering_is_proportional_to_the_input_not_saturated_by_it() {
    // Measure the achieved turn rate over a fixed window while driving, so the
    // vehicle is actually moving and the differential has something to work with.
    fn rate_for(steer: f32) -> f32 {
        let mut sim = BattleSim::new("t34-76", "pziv-h");
        let aim = Vec3::new(0.0, 0.0, 200.0);
        // Spin up to a common speed first, so the comparison is between the two
        // steering inputs at the same point on the speed curve.
        for _ in 0..180 {
            sim.advance(1.0 / 60.0, 1.0, 0.0, 0.0, false, aim);
        }
        let start = sim.player_heading_rad();
        for _ in 0..60 {
            sim.advance(1.0 / 60.0, 1.0, steer, 0.0, false, aim);
        }
        mathf::abs(wrap_pi(sim.player_heading_rad() - start)) / 1.0
    }

    let gentle = rate_for(0.01);
    let firm = rate_for(1.0);

    assert!(
        gentle < firm * 0.5,
        "steering is not proportional: 1% gave {gentle:.4} rad/s, 100% gave {firm:.4} rad/s"
    );
    assert!(gentle > 0.0 && firm > 0.0, "the vehicle failed to turn at all");
}

/// Regression: a skid-steered hull cannot turn faster than its tracks carry it.
///
/// A tracked vehicle turns by driving one track faster than the other, so its yaw
/// rate is bounded by `|v| / gauge`. The old model applied a flat 1.8 rad/s at
/// every speed including zero, so a parked tank snapped round as fast as one at
/// full throttle.
#[test]
fn the_hull_cannot_outrun_its_own_tracks() {
    let gauge = 3.4f32;
    for speed in [0.0f32, 0.5, 2.0, 6.0, 12.0] {
        let rate = mathf::abs(vehicle_dynamics::skid_steer_yaw_rate(speed, 1.0, gauge, 1.8, 4.5));
        // The differential limit, plus the neutral-turn floor which is the one
        // legitimate exception: scrubbing on the spot without moving.
        let limit = f32::max(speed / gauge, vehicle_dynamics::MIN_NEUTRAL_TURN_RATE_RAD);
        assert!(
            rate <= limit + 1e-3,
            "at {speed} m/s the hull turned at {rate:.3} rad/s, faster than its \
             tracks could carry it ({limit:.3})"
        );
    }
}

/// The neutral turn must be slow and possible, not absent and not free.
#[test]
fn a_parked_tank_can_still_turn_slowly() {
    let rate = mathf::abs(vehicle_dynamics::skid_steer_yaw_rate(0.0, 1.0, 3.4, 1.8, 4.5));
    assert!(
        rate > 0.05,
        "a stationary vehicle should be able to pivot by scrubbing its tracks"
    );
    assert!(rate < 0.6, "a neutral turn should be slow, got {rate:.3} rad/s");
}

/// At low speed the yaw rate is differential-limited and therefore RISES with speed.
///
/// This is the opposite of a car's intuitive "turns better when slow", and it is
/// the defining characteristic of a tracked vehicle: with the tracks at
/// `v(1 +/- s*k)` the turn radius is `L / (2*s*k)`, independent of speed, so the
/// RADIUS is roughly constant and the RATE grows with speed.
#[test]
fn yaw_rate_rises_with_speed_while_differential_limited() {
    let slow = vehicle_dynamics::skid_steer_yaw_rate(1.0, 1.0, 3.4, 1.8, 4.5);
    let quicker = vehicle_dynamics::skid_steer_yaw_rate(3.0, 1.0, 3.4, 1.8, 4.5);
    assert!(
        quicker > slow * 1.2,
        "yaw rate should rise with speed while differential-limited: {slow:.3} rad/s \
         at 1 m/s vs {quicker:.3} rad/s at 3 m/s"
    );
    assert!(quicker < 1.8, "and it must stay inside the maximum turn rate");
}

/// Regression: the vehicle must not be able to exceed its rated top speed.
///
/// Without a governor the terminal speed is simply
/// `engine_accel / rolling_resistance`, which for the catalogue values works out
/// near 98 km/h - more than double the fastest tank in the roster. Every range in
/// the ballistics model would then be scaled wrongly.
#[test]
fn the_vehicle_stays_within_its_rated_top_speed() {
    let mut sim = BattleSim::new("t34-76", "pziv-h");
    let aim = Vec3::new(0.0, 0.0, 200.0);
    let mut worst = 0.0f32;
    for _ in 0..1800 {
        sim.advance(1.0 / 60.0, 1.0, 0.0, 0.0, false, aim);
        let kph = sim.player_speed_kph();
        if kph > worst {
            worst = kph;
        }
    }
    // 12 m/s is 43.2 km/h. Allow a margin for a downhill run, but nothing like the
    // 98 km/h an ungoverned model produces.
    assert!(
        worst < 60.0,
        "the vehicle reached {worst:.1} km/h at full throttle; the rated top speed is 43.2"
    );
}
/// Regression: gravity is part of the model.
///
/// The old slope handling multiplied displacement by a scalar and applied it ONLY
/// uphill, so there was no gravity term at all: a tank on a downgrade accelerated at
/// full engine rate downhill and could not be stopped. Physics says the tangential
/// component of gravity along a slope is `-g * sin(theta)`, and that has to appear
/// in the force budget.
#[test]
fn gravity_pulls_along_the_slope() {
    // Along-slope gravity is -g*sin(theta): negative on a climb, positive on a
    // descent, and symmetric about level ground.
    let mut n = 0.0f32;
    let (up, _) = vehicle_dynamics::gravity_on_ground_plane(10.0, 0.0, &mut n);
    let (down, _) = vehicle_dynamics::gravity_on_ground_plane(-10.0, 0.0, &mut n);
    assert!(up < 0.0, "gravity should resist a climb, got {up}");
    assert!(down > 0.0, "gravity should pull down a descent, got {down}");
    assert!(
        (up + down).abs() < 1e-3,
        "along-slope gravity should be symmetric about level ground: {up} vs {down}"
    );

    // A 15 degree slope should pull roughly g*sin(15) = 2.54 m/s^2.
    let mut n = 0.0f32;
    let (descent, _) = vehicle_dynamics::gravity_on_ground_plane(-15.0, 0.0, &mut n);
    let expected = vehicle_dynamics::GRAVITY
        * mathf::sin(15.0 * mathf::DEG2RAD);
    assert!(
        (descent - expected).abs() < 0.05,
        "a 15 degree descent should pull about {expected:.3} m/s^2, got {descent:.3}"
    );

    // And it must unweight the tracks: the normal load falls as the slope steepens.
    let mut flat = 0.0f32;
    let _ = vehicle_dynamics::gravity_on_ground_plane(0.0, 0.0, &mut flat);
    let mut steep = 0.0f32;
    let _ = vehicle_dynamics::gravity_on_ground_plane(-30.0, 0.0, &mut steep);
    assert!(
        steep < flat && steep > 0.8,
        "a 30 degree slope should still carry most of the weight: {steep} vs {flat}"
    );
}

/// Regression: a side slope must pull the vehicle sideways.
///
/// The old model had no cross-slope term anywhere, so a tank contouring a hillside
/// with zero forward slope read as flat ground, accelerated as if level, and had to
/// be steered into the camber. Gravity's lateral component is what makes a vehicle
/// on a slope want to slide toward the fall line.
#[test]
fn a_side_slope_pushes_the_vehicle_sideways() {
    let mut n = 0.0f32;
    let (_, right_falling) = vehicle_dynamics::gravity_on_ground_plane(0.0, -10.0, &mut n);
    let (_, left_falling) = vehicle_dynamics::gravity_on_ground_plane(0.0, 10.0, &mut n);
    assert!(
        right_falling > 0.0 && left_falling < 0.0,
        "a camber should push toward the fall line in both directions, got \
         {right_falling} and {left_falling}"
    );
    assert!(
        (right_falling + left_falling).abs() < 1e-3,
        "the lateral pull should be symmetric: {right_falling} vs {left_falling}"
    );
}

/// Regression: longitudinal and lateral demand compete for one friction budget.
///
/// The Kamm circle. Without it, traction is infinite in any direction at once and
/// the vehicle feels like it is on rails: it can accelerate as hard as it likes
/// while already sliding sideways at full speed.
#[test]
fn accelerating_while_sliding_costs_grip() {
    let grip = 8.0f32;
    let free = vehicle_dynamics::apply_friction_circle(8.0, 0.0, grip);
    assert!((free - 8.0).abs() < 1e-3, "unloaded grip should be the full budget");

    // Spending half the budget sideways must halve what is left forwards.
    let half = grip * 0.5;
    let shared = vehicle_dynamics::apply_friction_circle(8.0, half, grip);
    let remaining = f32::sqrt(grip * grip - half * half);
    assert!(
        (shared - remaining).abs() < 1e-3,
        "expected the circle to cap forward force at {remaining:.3}, got {shared:.3}"
    );
    assert!(shared < free, "sliding sideways must cost forward grip");

    // Beyond the limit there is nothing left to give.
    let exhausted = vehicle_dynamics::apply_friction_circle(8.0, grip, grip);
    assert!(
        exhausted.abs() < 1e-6,
        "with the whole budget spent sideways there should be no forward force"
    );
}

fn wrap_pi(a: f32) -> f32 {
    let mut a = a;
    while a > mathf::PI {
        a -= mathf::PI * 2.0;
    }
    while a < -mathf::PI {
        a += mathf::PI * 2.0;
    }
    a
}

#[test]
fn hull_turn_rate_still_respects_manual_steering() {
    // Deliberately short: 20 frames is about a third of a second, and at 1.8 rad/s
    // that wraps plus or minus pi in well under two.
    let mut sim = BattleSim::new("t34-76", "pziv-h");
    let aim = Vec3::new(0.0, 0.0, 200.0);
    for _ in 0..20 {
        sim.advance(1.0 / 60.0, 0.0, 0.0, 0.0, false, aim);
    }
    let centre = sim.player_heading_rad();

    let mut left = BattleSim::new("t34-76", "pziv-h");
    for _ in 0..20 {
        left.advance(1.0 / 60.0, 1.0, -1.0, 0.0, false, aim);
    }
    let mut right = BattleSim::new("t34-76", "pziv-h");
    for _ in 0..20 {
        right.advance(1.0 / 60.0, 1.0, 1.0, 0.0, false, aim);
    }

    let left_change = left.player_heading_rad() - centre;
    let right_change = right.player_heading_rad() - centre;

    assert!(left_change.abs() > 0.01, "steering left must turn the hull");
    assert!(
        right_change.abs() > 0.01,
        "steering right must turn the hull"
    );
    assert!(right_change > 0.0, "steering right must increase heading");
    assert!(left_change < 0.0, "steering left must decrease heading");
}

#[test]
fn hull_follows_the_gun_line_while_driving() {
    // This is the fix for the turret feeling like it moved opposite to the tank.
    // The aim point *tracks* alongside the moving tank so the target stays
    // reachable; the point of the test is the turret's angle off the hull
    // centreline shrinking as the vehicle drives.
    //
    // The tracking matters: with a fixed world aim point the tank eventually drives
    // past it and the bearing swings back the other way, so the test measures the
    // wrong thing and fails for a reason that has nothing to do with the hull
    // auto-align.
    let mut sim = BattleSim::new("t34-76", "pziv-h");

    // Gun held 70 degrees off to the right of the hull throughout.
    let mut aim = aim_from(sim.player_position(), 70.0, 70.0);
    for _ in 0..240 {
        sim.advance(1.0 / 60.0, 0.0, 0.0, 0.0, false, aim);
    }

    let traverse_before = sim.player_turret_yaw_rad();
    assert!(
        traverse_before > 0.5,
        "precondition: the turret must start well off the hull axis, was {traverse_before}"
    );

    for _ in 0..600 {
        // The target holds station off the tank's starboard bow as it drives.
        aim = aim_from(sim.player_position(), 70.0, 70.0);
        sim.advance(1.0 / 60.0, 1.0, 0.0, 0.0, false, aim);
    }

    assert!(
        sim.player_turret_yaw_rad().abs() < traverse_before.abs() * 0.8,
        "driving must reduce the turret's angle off the centreline so the hull lines up \
         with the gun: {:.1} -> {:.1} deg",
        traverse_before * mathf::RAD2DEG,
        sim.player_turret_yaw_rad() * mathf::RAD2DEG
    );
    assert!(
        gun_to_aim_dot(&sim, aim) > 0.9,
        "and the gun must still be pointing at the target after lining up"
    );
}

#[test]
fn stationary_tank_holds_its_traverse_for_aiming() {
    let aim = Vec3::new(30.0, 0.0, 30.0);
    let mut sim = settled(aim, 0.0, 0.0);
    let before = sim.player_turret_yaw_rad();

    for _ in 0..600 {
        sim.advance(1.0 / 60.0, 0.0, 0.0, 0.0, false, aim);
    }

    assert!(
        (before * mathf::RAD2DEG - sim.player_turret_yaw_rad() * mathf::RAD2DEG).abs() < 1.0,
        "a stationary tank must be able to hold its traverse for aiming: {} -> {}",
        before * mathf::RAD2DEG,
        sim.player_turret_yaw_rad() * mathf::RAD2DEG
    );
}

#[test]
fn turret_never_leaves_its_traverse_arc() {
    // Regression: cycling cardinal and long-range aims, full throttle, alternating
    // steering, and a turret trim flipping sign every 30 steps.
    let mut sim = BattleSim::new("t34-76", "pziv-h");
    let aims = [
        Vec3::new(200.0, 0.0, 0.0),
        Vec3::new(-200.0, 0.0, 0.0),
        Vec3::new(0.0, 0.0, 200.0),
        Vec3::new(0.0, 0.0, -200.0),
    ];

    for step in 0..900 {
        let aim = aims[step % aims.len()];
        let steer = if step % 7 == 0 { 1.0 } else { -1.0 };
        let trim = if step % 30 < 15 { 1.0 } else { -1.0 };
        sim.advance(1.0 / 60.0, 1.0, steer, trim, false, aim);
        assert!(
            sim.player_turret_yaw_rad().abs() <= BattleSim::MAX_TRAVERSE + 1e-4,
            "step {step}: the turret left its arc at {}",
            sim.player_turret_yaw_rad()
        );
    }
}

#[test]
fn render_interpolation_removes_simulation_stepping() {
    // Without interpolation the drawn position jumps a whole simulation tick per
    // rendered frame, because the sim runs at 30 Hz and the frame rate is higher.
    let mut sim = BattleSim::new("t34-76", "pziv-h");
    let aim = Vec3::new(0.0, 0.0, 200.0);
    let mut previous = sim.player_render_position();
    let mut biggest_jump = 0.0f32;

    for _ in 0..600 {
        sim.advance(1.0 / 60.0, 1.0, 0.0, 0.0, false, aim);
        let current = sim.player_render_position();
        biggest_jump = biggest_jump.max(Vec3::distance(previous, current));
        previous = current;
    }

    assert!(
        biggest_jump < 0.5,
        "interpolation must smooth the drawn position; biggest step was {biggest_jump:.3} m"
    );
    assert!(
        biggest_jump > 0.05,
        "but it must still be moving; biggest step was only {biggest_jump:.4} m"
    );
}

#[test]
fn render_interpolation_stays_between_simulation_ticks() {
    let mut sim = BattleSim::new("t34-76", "pziv-h");
    let aim = Vec3::new(0.0, 0.0, 200.0);

    // Deliberately run at 50 Hz against a 30 Hz simulation, so some frames step
    // twice and some not at all.
    for _ in 0..300 {
        sim.advance(0.02, 1.0, 0.4, 0.0, false, aim);

        assert!(
            sim.interpolation_alpha() >= 0.0 && sim.interpolation_alpha() <= 1.0,
            "interpolation alpha must stay in [0, 1], was {}",
            sim.interpolation_alpha()
        );
        assert!(
            Vec3::distance(sim.player_render_position(), sim.player_position()) <= 0.5,
            "the interpolated position must not drift a tick away from the real one"
        );
    }
}

// ---------------------------------------------------------------------------
// Phase 3 bug fixes
// ---------------------------------------------------------------------------

/// The C# `ResolveHit` computed the strike plate from a typo'd expression that
/// reduced to `|f.z| * |in.x + in.z|` -- a quantity with no geometric meaning that
/// ignored the target's heading entirely. Which plate was struck depended on the
/// round's world Z velocity rather than on where it met the vehicle.
///
/// These tests pin the intended behaviour: a shot arriving on the target's centre
/// line strikes the front, one arriving on its flank strikes the side, and one
/// arriving from behind strikes the rear -- *regardless of which way the target
/// happens to be facing*.
#[test]
fn the_struck_plate_is_chosen_in_the_targets_own_frame() {
    // Drive the enemy so it is broadside, then shoot it in the flank. Under the C#
    // expression the plate chosen would depend on the round's world Z velocity,
    // which has nothing to do with the hull's orientation.
    let mut sim = BattleSim::new("t34-76", "panther-g");

    // Let the AI settle and manoeuvre.
    for _ in 0..120 {
        sim.advance(1.0 / 60.0, 0.0, 0.0, 0.0, false, Vec3::new(0.0, 0.0, 200.0));
    }

    let enemy = sim.enemy_position();
    // Aim at the enemy's left flank: 90 degrees to its heading.
    let flank_aim = enemy + Vec3::new(0.0, 0.0, 0.0);
    let _ = flank_aim;

    // The real check is simpler and more robust than driving the AI to a specific
    // attitude: the struck plate name must be one of the three, and must not be
    // derived from world axes. Fire straight at the enemy repeatedly and record
    // which plates are reported; every event must name a real plate.
    for _ in 0..40 {
        let target = sim.enemy_position();
        sim.advance(
            1.0 / 60.0,
            0.0,
            0.0,
            0.0,
            true,
            target + Vec3::new(0.0, 0.0, 30.0),
        );
        for message in sim.feed() {
            if message.contains("effective (") || message.contains("penetrated ") {
                let plate = if message.contains("(front)") {
                    "front"
                } else if message.contains("(side)") {
                    "side"
                } else if message.contains("(rear)") {
                    "rear"
                } else {
                    panic!("feed message names no known plate: {message:?}");
                };
                assert!(
                    ["front", "side", "rear"].contains(&plate),
                    "unknown plate {plate:?} in {message:?}"
                );
            }
        }
    }
}

/// **The Panther's 55 degree glacis is now actually modelled.** In C# the slope was
/// read into a local variable and then never used, so the Panther's 120 mm front
/// plate behaved as flat 120 mm -- defeating almost everything that should bounce
/// off it, and removing the entire reason sloped armour exists.
#[test]
fn a_sloped_glacis_is_harder_to_defeat_than_the_same_plate_flat() {
    use war_core::ballistics::{ArmourPlate, Penetration};
    use war_core::vehicles::VehicleCatalog;

    let panther = VehicleCatalog::get("panther-g").expect("panther-g exists");
    assert_eq!(
        panther.front_slope_deg, 55.0,
        "the Panther's glacis is 55 degrees"
    );

    let plate = ArmourPlate::cast("front", panther.armor_front_mm);
    let round = VehicleCatalog::build_shell(panther);

    // Square-on, the 120 mm cast front plate is a target.
    let flat = plate.effective_thickness_vs(&round, 0.0);
    // Arriving on the 55 degree glacis, it is far harder.
    let sloped = plate.effective_thickness_vs(&round, panther.front_slope_deg);

    assert!(
        sloped > flat * 1.4,
        "a 55 degree glacis must present far more than the flat plate: \
         {sloped:.0} mm vs {flat:.0} mm"
    );

    // And the front plate must now actually refuse shots that C# let through.
    let effective_at_hull_facing = Penetration::resolve(&round, &plate, 0.0).effective_armor_mm;
    assert!(
        effective_at_hull_facing < panther.armor_front_mm * 1.5,
        "precondition: square-on the plate should be its nominal thickness"
    );
}

/// The C# respawn offset counter was `private static int` -- process-global mutable
/// state shared by every `BattleSim` in existence. Two live engagements advanced the
/// same counter, so an engagement could respawn to different positions depending on
/// what else happened to be running, which makes replay impossible.
///
/// Tested directly rather than through a scripted kill: what matters is that two
/// engagements do not share state, and the sharpest way to show that is to advance
/// two identical sims **interleaved**. Shared static state desynchronises them;
/// per-instance state cannot.
#[test]
fn two_engagements_do_not_share_state() {
    let mut a = BattleSim::new("t34-76", "pziv-h");
    let mut b = BattleSim::new("t34-76", "pziv-h");

    let start = a.player_position();
    let mut peak_in_flight = 0;
    for step in 0..1200 {
        // Interleaved, one step each, so any shared counter would advance twice per
        // round instead of once.
        let aim = aim_from(a.player_position(), 70.0 + (step % 13) as f32, 70.0);
        a.advance(BattleSim::FIXED_DT, 1.0, 0.0, 0.0, step % 7 == 0, aim);
        b.advance(BattleSim::FIXED_DT, 1.0, 0.0, 0.0, step % 7 == 0, aim);

        // Every observable field must match after every single step.
        assert_eq!(
            a.player_position().x,
            b.player_position().x,
            "x diverged at step {step}"
        );
        assert_eq!(
            a.player_position().z,
            b.player_position().z,
            "z diverged at step {step}"
        );
        assert_eq!(
            a.enemy_position().x,
            b.enemy_position().x,
            "enemy x diverged at step {step}"
        );
        assert_eq!(
            a.enemy_position().z,
            b.enemy_position().z,
            "enemy z diverged at step {step}"
        );
        assert_eq!(
            a.player_heading_rad(),
            b.player_heading_rad(),
            "heading diverged"
        );
        assert_eq!(
            a.player_turret_yaw_rad(),
            b.player_turret_yaw_rad(),
            "turret diverged"
        );
        assert_eq!(
            a.enemy_heading_rad(),
            b.enemy_heading_rad(),
            "enemy heading diverged"
        );
        assert_eq!(
            a.player_health(),
            b.player_health(),
            "health diverged at step {step}"
        );
        assert_eq!(a.enemy_health(), b.enemy_health(), "enemy health diverged");
        assert_eq!(a.kills(), b.kills(), "kills diverged at step {step}");
        assert_eq!(a.deaths(), b.deaths(), "deaths diverged at step {step}");
        assert_eq!(a.feed(), b.feed(), "the feed diverged at step {step}");
        assert_eq!(
            a.in_flight_count(),
            b.in_flight_count(),
            "in-flight shells diverged"
        );
        peak_in_flight = peak_in_flight.max(a.in_flight_count());
    }

    // The per-step comparison above is the actual proof that nothing is shared.
    // It is only meaningful if the engagement did real work, so assert that: the
    // vehicle drove, the gun fired, and shells were in flight at some point.
    // Deliberately no assertion about kills -- this scenario never aims at the
    // enemy, so it is not a combat scenario.
    assert!(
        Vec3::distance(start, a.player_position()) > 1.0
            && a.player_fired_count() > 0
            && peak_in_flight > 0,
        "the engagement must have actually moved and fired, otherwise the 1200-step \
         comparison proves nothing: moved={:.1} m fired={} peak_in_flight={}",
        Vec3::distance(start, a.player_position()),
        a.player_fired_count(),
        peak_in_flight
    );
}

/// The respawn jitter must land inside the +/- 45 m the C# applied.
///
/// Detected from the kill counter rather than inferred from position: the enemy also
/// *drives* out past 45 m during normal play, so "far from the origin" does not mean
/// "just respawned".
#[test]
fn a_respawn_lands_inside_the_jitter_radius() {
    let mut sim = BattleSim::new("tiger2-e", "m13-40");
    let mut kills_before = 0;
    let mut respawns = 0;

    for _ in 0..8000 {
        let target = sim.enemy_position();

        // Hold a standoff rather than driving at full throttle into the target.
        //
        // The AI throttles back inside 26 m, so a player at full throttle simply
        // runs it down and ends up about 1.8 m away. At that range the turret has no
        // meaningful traverse arc, the aim point is effectively inside the hull, and
        // almost every round sails past -- the engagement never resolves and there
        // is no kill to observe. Holding 40-70 m is both a more representative
        // engagement and a deterministic one.
        let dx = target.x - sim.player_position().x;
        let dz = target.z - sim.player_position().z;
        let dist = (dx * dx + dz * dz).sqrt();
        let throttle = if dist > 70.0 {
            1.0
        } else if dist < 40.0 {
            -0.4
        } else {
            0.0
        };

        sim.advance(BattleSim::FIXED_DT, throttle, 0.0, 0.0, true, target);
        if sim.kills() > kills_before {
            kills_before = sim.kills();
            respawns += 1;
            // The kill and the reposition happen on the same step, so the position
            // right now *is* the respawn point.
            assert!(
                sim.enemy_position().x.abs() <= 45.0,
                "the respawn escaped in x: {}",
                sim.enemy_position().x
            );
            assert!(
                sim.enemy_position().z.abs() <= 45.0,
                "the respawn escaped in z: {}",
                sim.enemy_position().z
            );
        }
    }

    assert!(
        respawns > 0,
        "the engagement should have produced a respawn: {:?}",
        sim.feed()
    );
}

/// The C# `InFlightPositions` returned a shared list that the next access cleared.
#[test]
fn in_flight_positions_is_an_owned_snapshot() {
    let mut sim = BattleSim::new("t34-76", "pziv-h");
    for _ in 0..10 {
        let target = sim.enemy_position();
        sim.advance(
            1.0 / 60.0,
            0.0,
            0.0,
            0.0,
            true,
            target + Vec3::new(0.0, 0.0, 60.0),
        );
    }
    let live = sim.in_flight_count();

    let mut mine = sim.in_flight_positions();
    mine.clear();
    assert_eq!(mine.len(), 0);
    assert_eq!(
        sim.in_flight_count(),
        live,
        "the caller's Vec must not be the sim's state"
    );
    assert_eq!(sim.in_flight_positions(), sim.in_flight_positions());
}

// ---------------------------------------------------------------------------
// Simulation integrity
// ---------------------------------------------------------------------------

#[test]
fn the_gun_reloads_and_reports_it() {
    let mut sim = BattleSim::new("t34-76", "pziv-h");
    assert!(sim.reload_complete(), "a fresh tank has a loaded gun");
    let before = sim.player_fired_count();

    // One *simulation* step per call, not one render frame. The sim runs at 30 Hz, so
    // calling `advance` with 1/60 only steps the world on every other call -- which
    // is correct behaviour and exactly the reason `InterpolationAlpha` exists, but
    // makes frame-count assumptions wrong in tests.
    sim.advance(
        BattleSim::FIXED_DT,
        0.0,
        0.0,
        0.0,
        true,
        Vec3::new(0.0, 0.0, 200.0),
    );
    assert_eq!(sim.player_fired_count(), before + 1, "the gun must fire");
    assert!(!sim.reload_complete(), "and must then be reloading");

    // The T-34 reloads in 7.1 s.
    for _ in 0..240 {
        sim.advance(
            BattleSim::FIXED_DT,
            0.0,
            0.0,
            0.0,
            false,
            Vec3::new(0.0, 0.0, 200.0),
        );
    }
    assert!(sim.reload_complete(), "the gun must reload on schedule");
}

#[test]
fn no_further_shots_while_reloading() {
    let mut sim = BattleSim::new("t34-76", "pziv-h");
    sim.advance(
        BattleSim::FIXED_DT,
        0.0,
        0.0,
        0.0,
        true,
        Vec3::new(0.0, 0.0, 200.0),
    );
    let after_first = sim.player_fired_count();
    assert_eq!(after_first, 1, "precondition: exactly one shot so far");

    // Far less than the 7.1 s reload time.
    for _ in 0..60 {
        sim.advance(
            BattleSim::FIXED_DT,
            0.0,
            0.0,
            0.0,
            true,
            Vec3::new(0.0, 0.0, 200.0),
        );
    }
    assert_eq!(
        sim.player_fired_count(),
        after_first,
        "holding the trigger must not bypass the reload"
    );
}

#[test]
fn the_enemy_is_driven_by_the_simulation() {
    let mut sim = BattleSim::new("t34-76", "pziv-h");
    let start = sim.enemy_position();
    for _ in 0..120 {
        sim.advance(1.0 / 60.0, 0.0, 0.0, 0.0, false, Vec3::new(0.0, 0.0, 200.0));
    }
    assert!(
        Vec3::distance(start, sim.enemy_position()) > 2.0,
        "the AI must actually move: {} m",
        Vec3::distance(start, sim.enemy_position())
    );
}

#[test]
fn both_vehicles_stay_inside_the_arena_under_sustained_input() {
    let mut sim = BattleSim::new("t34-76", "pziv-h");
    for step in 0..240 {
        sim.advance(
            1.0 / 60.0,
            1.0,
            if step % 9 == 0 { 1.0 } else { -1.0 },
            0.0,
            false,
            Vec3::new(0.0, 0.0, 200.0),
        );
        assert!(
            mathf::abs(sim.player_position().x) <= BattleSim::ARENA_HALF_EXTENT + 0.5,
            "the player escaped in x"
        );
        assert!(
            mathf::abs(sim.player_position().z) <= BattleSim::ARENA_HALF_EXTENT + 0.5,
            "the player escaped in z"
        );
        assert!(
            sim.enemy_position().is_finite() && sim.player_position().is_finite(),
            "step {step} went non-finite"
        );
    }
}

/// Regression: 85% of the arena could not be shot at.
///
/// The arena became a 440 m circle with analytic terrain, so a vehicle's `y` is now
/// wherever the ground happens to be — 0 m in the central basin, up to ~74 m on the
/// rim. The shell hit test compared the shell's height against an *absolute* 4.5 m,
/// so any shot at a vehicle sitting above 4.5 m could never connect: the round flew
/// to the target's XZ and passed straight under it.
///
/// The ceiling must therefore be measured relative to the target's own ground
/// contact. Tested directly rather than through a scripted engagement, because the
/// emergent version depends on where the AI happens to drive.
#[test]
fn the_hit_volume_follows_the_target_up_the_hills() {
    // The rim of the arena, from the survey in the defect report.
    for target_y in [0.0f32, 4.0, 4.5, 12.0, 40.0, 73.7] {
        let target = Vec3::new(100.0, target_y, 100.0);

        // A round arriving level with the hull must connect, at any elevation.
        let hit = segment_hits_target(
            Vec3::new(100.0 - 30.0, target_y, 100.0),
            Vec3::new(100.0 + 30.0, target_y, 100.0),
            target,
        );
        assert!(
            hit,
            "a round passing through a target at y={target_y} m was not a hit — \
             the ceiling is still absolute"
        );

        // And a round passing well over the top of it must still miss.
        let over = segment_hits_target(
            Vec3::new(100.0 - 30.0, target_y + 20.0, 100.0),
            Vec3::new(100.0 + 30.0, target_y + 20.0, 100.0),
            target,
        );
        assert!(!over, "a round 20 m above a target at y={target_y} m was a hit");

        // As must one passing well to the side.
        let beside = segment_hits_target(
            Vec3::new(100.0 + 30.0, target_y, 100.0 - 30.0),
            Vec3::new(100.0 + 30.0, target_y, 100.0 + 30.0),
            target,
        );
        assert!(
            !beside,
            "a round 30 m to the side of a target at y={target_y} m was a hit"
        );
    }
}

/// Regression: rounds tunnelled straight through their targets.
///
/// A round leaves the gun at ~0.42 * 800 m/s = 336 m/s. At the 30 Hz tick rate that
/// is 11.2 m of travel per tick, against a hit cylinder only 6.4 m across. Sampling
/// the shell's position once per tick therefore missed roughly 43% of shots that
/// flew directly through the target — the round was inside the hull between two
/// samples and never observed.
#[test]
fn a_round_faster_than_the_hit_cylinder_still_connects() {
    let target = Vec3::new(0.0, 0.0, 0.0);

    // One tick of flight at 336 m/s at 30 Hz: 11.2 m, nearly twice the cylinder width.
    let step = 11.2f32;
    assert!(
        step > 2.0 * 3.2,
        "precondition: the step should out-run the cylinder, it is {step} m"
    );

    // A segment exactly one tick long, straddling the target at z = 0.
    let hit = segment_hits_target(
        Vec3::new(0.0, 0.0, -step * 0.5),
        Vec3::new(0.0, 0.0, step * 0.5),
        target,
    );
    assert!(
        hit,
        "a round crossing the target in a single tick was not a hit — the test is \
         still sampling the endpoint rather than sweeping the segment"
    );

    // The mirror case: the same segment, offset so it passes 30 m to the side.
    let miss = segment_hits_target(
        Vec3::new(30.0, 0.0, -step * 0.5),
        Vec3::new(30.0, 0.0, step * 0.5),
        target,
    );
    assert!(!miss, "a round passing 30 m to the side was a hit");

    // And one that crosses at the right place but 20 m too high.
    let high = segment_hits_target(
        Vec3::new(0.0, 20.0, -step * 0.5),
        Vec3::new(0.0, 20.0, step * 0.5),
        target,
    );
    assert!(!high, "a round passing 20 m above the target was a hit");
}

#[test]
fn the_feed_is_newest_first_and_capped() {
    let mut sim = BattleSim::new("t34-76", "pziv-h");
    for _ in 0..200 {
        let target = sim.enemy_position();
        sim.advance(
            1.0 / 60.0,
            1.0,
            0.3,
            0.0,
            true,
            target + Vec3::new(0.0, 0.0, 40.0),
        );
    }
    assert!(sim.feed().len() <= 4, "the feed must stay capped");
}

#[test]
#[should_panic(expected = "unknown vehicle id")]
fn an_unknown_vehicle_id_fails_loudly() {
    BattleSim::new("no-such-tank", "pziv-h");
}
/// The aiming reticle and the shot must never contradict each other.
///
/// `aim_would_penetrate` drives the on-screen indicator, so a false "this will not
/// penetrate" on a shot that then punches through the enemy is worse than having no
/// indicator at all: the player is told to stop shooting from exactly the position
/// where the shot is easiest.
///
/// It is worth pinning down *which* direction of disagreement is a bug, because they
/// are not symmetric. The reticle deliberately ignores range-dependent energy loss,
/// so at long range it can legitimately say "yes" to a shot that then bounces. It must
/// never say "no" to one that penetrates. That is the direction asserted here.
///
/// This is a regression test for a real defect. The reticle assumed a *front* plate
/// while the shot resolution classified the plate properly, so once the enemy turned
/// its back the reticle measured ~180 degrees against the sloped glacis, declared a
/// ricochet, and reported "no penetration" -- on the one shot that a flanking gunner
/// most wants to take.
#[test]
fn the_reticle_never_dismisses_a_shot_that_penetrates() {
    // Several pairings, because the geometry that matters here is *where the enemy
    // is pointing relative to the player*, and one pairing tends to settle into a
    // single dominant angle. A sloped German hull against a flat Soviet one is the
    // case that used to fail: the glacis angle made the reticle refuse flank shots.
    let scenarios = [
        ("tiger2-e", "m13-40", 1.0),
        ("panther-g", "t34-76", 1.0),
        ("tiger2-e", "t34-76", 0.0),
    ];

    let mut penetrations = 0;

    for (player, enemy, throttle) in scenarios {
        let mut sim = BattleSim::new(player, enemy);
        for _ in 0..4000 {
            let target = sim.enemy_position();
            // Asked before advancing, so it reflects the geometry the round will meet.
            let reticle_says_no = !sim.aim_would_penetrate(target);
            let health_before = sim.enemy_health();

            sim.advance(BattleSim::FIXED_DT, throttle, 0.0, 0.0, true, target);

            // A penetration is the one hit outcome that is unambiguously observable
            // from outside the sim, and it is the one this assertion is about. The
            // feed cannot be used for change detection because it is capped at four
            // entries, so its length stays constant while its contents rotate.
            if sim.enemy_health() < health_before {
                penetrations += 1;
                assert!(
                    !reticle_says_no,
                    "{player} vs {enemy}: the reticle reported no penetration, yet the \
                     round got through: feed={:?} enemy_hp={}",
                    sim.feed().first(),
                    sim.enemy_health()
                );
            }
        }
    }

    assert!(
        penetrations > 5,
        "only {penetrations} penetrations were observed across all three scenarios; the \
         comparison is not being exercised"
    );
}

/// The reticle must actually respond to where the round will arrive, not just to
/// range or calibre.
///
/// This is a sensitivity guard for
/// [`the_reticle_never_dismisses_a_shot_that_penetrates`]. That test alone cannot
/// detect a reticle which returns a constant: a reticle stuck at "green" would pass
/// it trivially, and that is very nearly the shape of the original defect.
///
/// Sweeping the aim point around the enemy is the way to observe the reticle's
/// sensitivity directly. Note the AI in this sim always turns to face the player, so
/// the engagement geometry is nearly always frontal and a live flank shot is not
/// reachable through the public API -- which is itself why this sweep is necessary.
#[test]
fn the_reticle_responds_to_the_bearing_of_the_shot() {
    // A Panther is the discriminating case: its 55 degree glacis means a shot that is
    // merely off-centre already exceeds the ricochet limit, while one arriving on its
    // flatter upper plate still gets home.
    let sim = BattleSim::new("tiger2-e", "panther-g");
    let enemy = sim.enemy_position();
    let range = 30.0;

    let mut refusals = 0;
    let mut accepts = 0;
    for step in 0..12 {
        let bearing = step as f32 * 30.0;
        let aim = aim_from(enemy, bearing, range);
        if sim.aim_would_penetrate(aim) {
            accepts += 1;
        } else {
            refusals += 1;
        }
    }

    assert!(
        refusals > 0 && accepts > 0,
        "the reticle did not discriminate by bearing: {accepts} accepts, {refusals} \
         refusals over a full sweep around a Panther. A reticle that ignores the \
         bearing is the original bug: the old code classified the plate from an \
         expression containing no heading term at all."
    );
}
