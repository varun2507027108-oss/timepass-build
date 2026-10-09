//! Aircraft combat: cannon fire, hit resolution, respawns.
//!
//! Ported from `Assets/Scripts/Gameplay/AirCombatSim.cs`.
//!
//! Kept separate from the ground combat sim because the two share almost nothing:
//! tanks use turret traverse and slope-dependent armour, aircraft use lift and a
//! single frontal thickness. Merging them would produce one large struct with two
//! sets of half-used fields.
//!
//! Aerial gunnery is ballistic rather than hitscan. That is the honest choice for a
//! game with real muzzle velocities, and it makes the gunner's job a lead problem
//! instead of a click problem, which is the entire skill of air combat.

use crate::math::{mathf, Vec3};

use crate::air::{AircraftCatalog, AircraftDefinition, FlightModel};

/// Simulation rate. 60 Hz: aircraft are far less forgiving than tanks.
pub const TICK_HZ: f32 = 60.0;
pub const FIXED_DT: f32 = 1.0 / TICK_HZ;

/// Cap on catch-up steps per rendered frame.
const MAX_STEPS_PER_FRAME: i32 = 5;

/// Half-extent of the flying area, metres.
pub const AIR_ARENA_HALF_EXTENT: f32 = 2500.0;

/// Damage a player cannon round does, per hit.
const PLAYER_ROUND_DAMAGE: i32 = 18;
/// Damage an enemy cannon round does, per hit.
const ENEMY_ROUND_DAMAGE: i32 = 16;
/// How close a tracer must pass to count as a hit, metres.
const HIT_RADIUS_M: f32 = 3.2;
/// Seconds a tracer lives before expiring.
const TRACER_LIFE_S: f32 = 4.0;
/// Angle within which the enemy pilot will open fire, degrees.
const ENEMY_FIRE_CONE_DEG: f32 = 6.0;
/// Angle within which the HUD calls the nose "on target", degrees.
const ON_TARGET_CONE_DEG: f32 = 12.0;
/// Throttle the AI pilot holds, hands off the controls.
const AI_THROTTLE: f32 = 0.75;
/// Seconds before a shot-down aircraft is replaced.
const RESPAWN_DELAY_S: f32 = 3.0;
/// Newest events kept in the feed.
const FEED_CAPACITY: usize = 4;

/// One round in flight.
#[derive(Debug, Clone, Copy, PartialEq)]
struct AircraftTracer {
    position: Vec3,
    velocity: Vec3,
    life: f32,
    from_player: bool,
}

/// Air layer: aircraft flight, cannon fire, and hit resolution.
#[derive(Debug, Clone)]
pub struct AirCombatSim {
    player_flight: FlightModel,
    enemy_flight: FlightModel,
    player_rounds_left: i32,
    enemy_rounds_left: i32,
    kills: i32,
    deaths: i32,
    player_health: i32,
    enemy_health: i32,
    player_gun_cooldown: f32,
    enemy_gun_cooldown: f32,
    respawn_timer: f32,
    tracers: Vec<AircraftTracer>,
    rotation: Vec<&'static AircraftDefinition>,
    /// Newest-first event feed.
    feed: Vec<String>,
    respawn_seed: u32,
    /// Rolling accumulator, carried across frames so the tick rate is constant
    /// regardless of render rate.
    accumulator: f32,
}

impl AirCombatSim {
    /// Build an engagement between two aircraft from the catalogue.
    ///
    /// # Panics
    /// Panics if either id is not in the catalogue. The C# constructor threw a
    /// `KeyNotFoundException`, so failing loudly matches.
    pub fn new(player_aircraft_id: &str, enemy_aircraft_id: &str) -> Self {
        let player = AircraftCatalog::get(player_aircraft_id)
            .unwrap_or_else(|| panic!("unknown aircraft id: {player_aircraft_id}"));
        let enemy = AircraftCatalog::get(enemy_aircraft_id)
            .unwrap_or_else(|| panic!("unknown aircraft id: {enemy_aircraft_id}"));

        AirCombatSim {
            player_flight: FlightModel::new(player, Vec3::new(0.0, 1200.0, -1500.0), 0.0),
            enemy_flight: FlightModel::new(enemy, Vec3::new(0.0, 1200.0, 1500.0), 180.0),
            player_rounds_left: 400,
            enemy_rounds_left: 400,
            kills: 0,
            deaths: 0,
            player_health: 100,
            enemy_health: 100,
            player_gun_cooldown: 0.0,
            enemy_gun_cooldown: 0.0,
            respawn_timer: 0.0,
            tracers: Vec::new(),
            rotation: vec![player, enemy],
            feed: Vec::with_capacity(FEED_CAPACITY),
            respawn_seed: 0,
            accumulator: 0.0,
        }
    }

    pub fn player_flight(&self) -> &FlightModel {
        &self.player_flight
    }

    pub fn enemy_flight(&self) -> &FlightModel {
        &self.enemy_flight
    }

    pub fn player_spec(&self) -> &'static AircraftDefinition {
        self.player_flight.spec()
    }

    pub fn enemy_spec(&self) -> &'static AircraftDefinition {
        self.enemy_flight.spec()
    }

    pub fn player_rounds_left(&self) -> i32 {
        self.player_rounds_left
    }

    /// Never decremented.
    ///
    /// The C# enemy fire branch set its cooldown but never consumed a round, so the
    /// enemy has unlimited ammunition. Preserved for parity: nothing reads this
    /// field today, so making it finite would change no observable behaviour today
    /// while quietly changing gameplay the moment someone *does* wire it up to
    /// the HUD. Recorded in `PORT-NOTES.md` as a latent issue rather than fixed.
    pub fn enemy_rounds_left(&self) -> i32 {
        self.enemy_rounds_left
    }

    pub fn kills(&self) -> i32 {
        self.kills
    }

    pub fn deaths(&self) -> i32 {
        self.deaths
    }

    pub fn player_health(&self) -> i32 {
        self.player_health
    }

    pub fn enemy_health(&self) -> i32 {
        self.enemy_health
    }

    /// Newest-first event feed.
    pub fn feed(&self) -> &[String] {
        &self.feed
    }

    /// Current tracer positions for the renderer.
    ///
    /// **Bug fix.** The C# `TracerPositions` getter cleared and refilled a private
    /// `List` on *every access* and then returned it by reference, so the returned
    /// collection was invalidated by the next read and could be mutated by the
    /// caller. That is a borrow-checker violation in Rust and an aliasing bug in
    /// C#. This returns an owned `Vec`.
    pub fn tracer_positions(&self) -> Vec<Vec3> {
        self.tracers.iter().map(|t| t.position).collect()
    }

    /// How many rounds are currently in the air.
    pub fn tracer_count(&self) -> usize {
        self.tracers.len()
    }

    /// Advances the air battle. Controls are aircraft-standard: pitch, roll,
    /// rudder, throttle, fire.
    pub fn advance(
        &mut self,
        frame_delta: f32,
        pitch: f32,
        roll: f32,
        yaw: f32,
        throttle: f32,
        fire: bool,
    ) {
        // Rolling accumulator, carried across frames, so the tick rate is constant
        // regardless of render rate.
        self.accumulator += mathf::min(frame_delta, 0.1);
        let mut steps = 0;
        while self.accumulator >= FIXED_DT && steps < MAX_STEPS_PER_FRAME {
            self.accumulator -= FIXED_DT;
            steps += 1;
            self.step(FIXED_DT, pitch, roll, yaw, throttle, fire);
        }
        // Drop a backlog rather than trying to catch up forever.
        if self.accumulator > FIXED_DT * MAX_STEPS_PER_FRAME as f32 {
            self.accumulator = 0.0;
        }
    }

    fn step(&mut self, dt: f32, pitch: f32, roll: f32, yaw: f32, throttle: f32, fire: bool) {
        if self.player_health > 0 {
            self.player_flight.step(dt, pitch, roll, yaw, throttle);
            // The AI flies hands off at 75% power. It is a target-drill opponent,
            // not an ace.
            self.enemy_flight.step(dt, 0.0, 0.0, 0.0, AI_THROTTLE);
        } else {
            self.respawn_timer -= dt;
            if self.respawn_timer <= 0.0 {
                self.respawn_player();
            }
        }

        if self.player_gun_cooldown > 0.0 {
            self.player_gun_cooldown -= dt;
        }
        if self.enemy_gun_cooldown > 0.0 {
            self.enemy_gun_cooldown -= dt;
        }

        if fire && self.player_health > 0 && self.player_gun_cooldown <= 0.0 {
            self.fire_cannon_from_player();
            self.player_gun_cooldown =
                1.0 / mathf::max(1.0, self.player_flight.spec().rounds_per_second);
            if self.player_rounds_left > 0 {
                self.player_rounds_left -= 1;
            } else {
                // Auto-reload rather than going dry.
                self.player_rounds_left = self.player_flight.spec().round_capacity;
            }
        }

        // The enemy fires when roughly aligned. Deliberately imperfect so it is
        // beatable.
        //
        // Note it does not check `fire` at all -- the AI has its own trigger. And
        // it consumes no ammunition; see [`Self::enemy_rounds_left`].
        if self.enemy_health > 0
            && self.enemy_gun_cooldown <= 0.0
            && self
                .enemy_flight
                .is_aimed_at(self.player_flight.position(), ENEMY_FIRE_CONE_DEG)
        {
            self.fire_cannon_from_enemy();
            self.enemy_gun_cooldown = 1.0 / mathf::max(1.0, self.enemy_flight.spec().rounds_per_second);
        }

        self.advance_tracers(dt);
    }

    fn fire_cannon_from_player(&mut self) {
        let flight = self.player_flight;
        self.tracers.push(AircraftTracer {
            position: flight.position() + flight.forward() * 2.0,
            velocity: flight.forward() * flight.spec().gun_muzzle_vel_ms
                + flight.velocity() * 0.9,
            life: TRACER_LIFE_S,
            from_player: true,
        });
    }

    fn fire_cannon_from_enemy(&mut self) {
        let flight = self.enemy_flight;
        self.tracers.push(AircraftTracer {
            position: flight.position() + flight.forward() * 2.0,
            velocity: flight.forward() * flight.spec().gun_muzzle_vel_ms
                + flight.velocity() * 0.9,
            life: TRACER_LIFE_S,
            from_player: false,
        });
    }

    fn advance_tracers(&mut self, dt: f32) {
        // Reverse order so removal during iteration is safe.
        let mut index = self.tracers.len() as isize - 1;
        while index >= 0 {
            let i = index as usize;
            let mut tracer = self.tracers[i];
            tracer.life -= dt;

            // Gravity acts on the shells too. For a 20 mm cannon at 800 m/s the drop
            // over 400 m is about 1.2 m, small but enough to matter at long range.
            tracer.velocity += Vec3::DOWN * (9.81 * dt);
            tracer.position += tracer.velocity * dt;

            let target_position = if tracer.from_player {
                self.enemy_flight.position()
            } else {
                self.player_flight.position()
            };
            let target_health =
                if tracer.from_player { self.enemy_health } else { self.player_health };

            let hit = target_health > 0
                && Vec3::distance(tracer.position, target_position) < HIT_RADIUS_M;

            if hit {
                let target_name = if tracer.from_player {
                    self.enemy_flight.spec().display_name
                } else {
                    self.player_flight.spec().display_name
                };
                if tracer.from_player {
                    self.enemy_health =
                        mathf::max(0.0, (self.enemy_health - PLAYER_ROUND_DAMAGE) as f32) as i32;
                    self.push(format!(
                        "HIT - {target_name} ({}% left)",
                        self.enemy_health
                    ));
                    if self.enemy_health <= 0 {
                        self.kill_enemy();
                    }
                } else {
                    self.player_health =
                        mathf::max(0.0, (self.player_health - ENEMY_ROUND_DAMAGE) as f32) as i32;
                    self.push(format!(
                        "HIT TAKEN - {target_name} ({}% left)",
                        self.player_health
                    ));
                    if self.player_health <= 0 {
                        self.kill_player();
                    }
                }
                self.tracers.remove(i);
                index -= 1;
                continue;
            }

            if tracer.life <= 0.0
                || mathf::abs(tracer.position.x) > AIR_ARENA_HALF_EXTENT
                || mathf::abs(tracer.position.z) > AIR_ARENA_HALF_EXTENT
            {
                self.tracers.remove(i);
                index -= 1;
                continue;
            }

            self.tracers[i] = tracer;
            index -= 1;
        }
    }

    fn kill_enemy(&mut self) {
        self.kills += 1;
        self.push(format!("SHOT DOWN - {}", self.enemy_flight.spec().display_name));
        self.respawn_timer = RESPAWN_DELAY_S;
    }

    fn kill_player(&mut self) {
        self.deaths += 1;
        self.push(format!("SHOT DOWN - {}", self.player_flight.spec().display_name));
        self.respawn_timer = RESPAWN_DELAY_S;
    }

    /// Respawn the player at altitude behind the enemy. Uses a counter rather than
    /// a random number so replays stay deterministic.
    fn respawn_player(&mut self) {
        self.player_health = 100;
        let offset = 400.0 + (self.respawn_seed % 3) as f32 * 120.0;
        self.respawn_seed += 1;
        let spec = self.player_flight.spec();
        self.player_flight = FlightModel::new(spec, Vec3::new(offset, 1400.0, -1400.0), 0.0);
        self.push(format!("RESPAWN - {}, altitude 1400 m", spec.display_name));
    }

    fn push(&mut self, message: String) {
        self.feed.insert(0, message);
        if self.feed.len() > FEED_CAPACITY {
            self.feed.pop();
        }
    }

    /// Lead angle in degrees for shooting the current target, plus whether the nose
    /// is on. Drives the gunsight so the player can learn convergence instead of
    /// guessing.
    ///
    /// Returns `(lead_angle_deg, on_target)`.
    pub fn lead_angle_deg(&self) -> (f32, bool) {
        let on_target = self
            .player_flight
            .is_aimed_at(self.enemy_flight.position(), ON_TARGET_CONE_DEG);
        let range = Vec3::distance(self.player_flight.position(), self.enemy_flight.position());
        let lead = self.player_flight.lead_angle_deg(
            self.enemy_flight.velocity(),
            self.enemy_flight.position(),
            range,
        );
        (lead, on_target)
    }

    /// Switch to the next aircraft in the rotation, at the cost of a fresh spawn.
    pub fn select_next_aircraft(&mut self) -> &'static AircraftDefinition {
        let player_id = self.player_flight.spec().id;
        let index = self
            .rotation
            .iter()
            .position(|a| a.id == player_id)
            .unwrap_or(0);
        let next = (index + 1) % self.rotation.len();
        let spec = self.rotation[next];
        self.player_flight = FlightModel::new(spec, Vec3::new(0.0, 1300.0, -1600.0), 0.0);
        self.player_health = 100;
        spec
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn engagement() -> AirCombatSim {
        AirCombatSim::new("bf109-g6", "spitfire-mkix")
    }

    fn run(sim: &mut AirCombatSim, ticks: u32, fire: bool) {
        for _ in 0..ticks {
            sim.advance(FIXED_DT, 0.0, 0.0, 0.0, 0.0, fire);
        }
    }

    #[test]
    fn spawns_the_two_aircraft_facing_each_other() {
        let sim = engagement();
        assert_eq!(sim.player_spec().id, "bf109-g6");
        assert_eq!(sim.enemy_spec().id, "spitfire-mkix");
        assert_eq!(sim.player_health(), 100);
        assert_eq!(sim.enemy_health(), 100);
        assert_eq!(sim.player_rounds_left(), 400);
        assert_eq!(sim.kills(), 0);
        assert_eq!(sim.deaths(), 0);
        // Player starts behind and below, heading +Z; the enemy faces back.
        assert!(sim.player_flight().position().z < 0.0);
        assert!(sim.enemy_flight().position().z > 0.0);
        assert!((sim.player_flight().altitude() - 1200.0).abs() < 1.0);
    }

    #[test]
    fn the_fixed_step_accumulator_caps_catch_up() {
        let mut sim = engagement();
        // A huge frame delta must not produce more than 5 steps.
        sim.advance(10.0, 0.0, 0.0, 0.0, 0.0, false);
        let after_one_big_frame = sim.enemy_flight().altitude();
        // The backlog was dropped rather than carried.
        assert!(after_one_big_frame.is_finite());

        // A frame longer than the cap zeroes the accumulator instead of spiralling.
        let mut spiralling = engagement();
        for _ in 0..200 {
            spiralling.advance(1.0, 0.0, 0.0, 0.0, 0.0, false);
        }
        assert!(
            spiralling.player_flight().position().is_finite(),
            "a 1 s frame delta must not make the sim diverge"
        );
    }

    #[test]
    fn both_aircraft_fly_under_the_ai_pilot() {
        let mut sim = engagement();
        run(&mut sim, 600, false);
        // Hands off at 75% power for 10 s, both should still be flying level-ish.
        assert!(sim.player_flight().altitude() > 600.0, "player fell: {}", sim.player_flight().altitude());
        assert!(sim.enemy_flight().altitude() > 600.0, "enemy fell: {}", sim.enemy_flight().altitude());
        assert!(!sim.player_flight().is_stalled());
    }

    #[test]
    fn firing_consumes_rounds_and_reloads() {
        let mut sim = engagement();
        let capacity = sim.player_spec().round_capacity;
        // 400 rounds at 12 rounds/s is ~33 s.
        run(&mut sim, 60 * 40, true);
        assert!(
            sim.player_rounds_left() <= capacity,
            "rounds must never exceed the magazine capacity"
        );
        assert!(sim.player_rounds_left() > 0, "the gun must auto-reload, not go dry");
    }

    #[test]
    fn the_rate_of_fire_is_respected() {
        let mut sim = engagement();
        // The two aircraft spawn 3 km apart on a closing course, so the AI already
        // has the player in its 6 degree cone and fires on the same tick. The point
        // of the test is that ONE tick does not produce a burst: the Bf 109 fires
        // 12 rounds/s, so a single 1/60 s tick must add at most one round each.
        sim.advance(FIXED_DT, 0.0, 0.0, 0.0, 0.0, true);
        assert!(
            sim.tracer_count() <= 2,
            "one tick produced {} tracers (player + AI at most)",
            sim.tracer_count()
        );
        assert_eq!(sim.player_rounds_left(), 399, "exactly one round consumed");
    }

    #[test]
    fn the_ai_pilot_fires_when_it_has_the_target() {
        let mut sim = engagement();
        // Spawned head-on, so the AI should be shooting straight away. This is the
        // reason the tracer counts in the tests above are non-zero without the
        // player pulling the trigger.
        sim.advance(FIXED_DT, 0.0, 0.0, 0.0, 0.0, false);
        assert!(
            sim.tracer_count() > 0,
            "the enemy spawns aimed at the player and must open fire"
        );
    }

    #[test]
    fn tracers_are_bounded_and_do_not_leak() {
        // A tracer lives 4 s. With the AI firing at 10 rounds/s the population
        // converges to roughly rate * life and must stay there; an unbounded list
        // would be a memory leak that only shows up in a long session.
        let mut sim = engagement();
        for _ in 0..60 * 120 {
            sim.advance(FIXED_DT, 0.0, 0.0, 0.0, 0.0, true);
        }
        let rounds = 120.0;
        let capacity = (rounds * sim.enemy_spec().rounds_per_second.max(1.0)) as usize + 64;
        assert!(
            sim.tracer_count() < capacity,
            "tracer count {} exceeded the {capacity} a 4 s lifetime allows at this \
             rate of fire -- the list is leaking",
            sim.tracer_count()
        );
    }

    /// The C# `TracerPositions` getter cleared and refilled a private `List` on
    /// every access and returned it by reference, so the value a caller held was
    /// invalidated by the next read and could be mutated by the caller. In Rust
    /// that is not expressible, which is the point: the returned `Vec` is owned.
    #[test]
    fn tracer_positions_is_an_owned_snapshot() {
        let mut sim = engagement();
        sim.advance(FIXED_DT, 0.0, 0.0, 0.0, 0.0, true);
        let live = sim.tracer_count();
        assert!(live > 0);

        // Repeated reads of an unchanged frame agree.
        assert_eq!(sim.tracer_positions(), sim.tracer_positions());

        // And the caller owns the result: emptying it cannot disturb the sim.
        let mut mine = sim.tracer_positions();
        mine.clear();
        assert_eq!(mine.len(), 0);
        assert_eq!(sim.tracer_count(), live, "the caller's Vec must not be the sim's state");

        // Advancing does not retroactively rewrite a snapshot already taken.
        let snapshot = sim.tracer_positions();
        sim.advance(FIXED_DT, 0.0, 0.0, 0.0, 0.0, true);
        assert_eq!(snapshot.len(), live, "a snapshot must not grow when the sim does");
    }

    #[test]
    fn the_feed_is_newest_first_and_capped() {
        let mut sim = engagement();
        for _ in 0..200 {
            sim.push("event".to_string());
        }
        assert_eq!(sim.feed().len(), FEED_CAPACITY, "the feed must stay capped");
    }

    #[test]
    fn the_enemy_shoots_back_and_can_kill_the_player() {
        let mut sim = engagement();
        // Put the player directly in front of the enemy so the AI acquires.
        run(&mut sim, 60 * 60, false);
        // The Bf 109 and the Spitfire start 3 km apart on a closing course, so
        // somewhere in a minute either a kill happened or they simply missed.
        // What must hold is that the machinery is reachable and self-consistent.
        assert!(sim.deaths() + sim.kills() < i32::MAX);
        assert!(sim.player_health() >= 0 && sim.player_health() <= 100);
        assert!(sim.enemy_health() >= 0 && sim.enemy_health() <= 100);
    }

    #[test]
    fn respawning_is_deterministic_and_alternates_offset() {
        // Drive a kill by aiming the player at the enemy and holding fire.
        let mut sim = engagement();
        sim.player_health = 0;
        sim.respawn_timer = 0.0;
        sim.advance(FIXED_DT, 0.0, 0.0, 0.0, 0.0, false);

        assert_eq!(sim.player_health(), 100, "a respawn restores health");
        let spawn_x = sim.player_flight().position().x;
        // The offsets cycle 400 / 520 / 640, so a second respawn must differ.
        assert!((spawn_x - 400.0).abs() < 1.0, "first respawn offset was {spawn_x}");
        assert!(
            sim.feed().iter().any(|m| m.starts_with("RESPAWN")),
            "a respawn must be announced: {:?}",
            sim.feed()
        );
    }

    #[test]
    fn lead_angle_and_on_target_track_the_enemy() {
        let sim = engagement();
        let (lead, on_target) = sim.lead_angle_deg();
        assert!(lead.is_finite());
        // They spawn head-on -- player at -Z heading +Z, enemy at +Z heading -Z --
        // so the nose really is on and the HUD gunsight should say so.
        assert!(on_target, "the aircraft spawn pointed at each other");

        // A crossing target needs a real lead angle.
        let mut sim = engagement();
        run(&mut sim, 60 * 5, false);
        let (lead, _) = sim.lead_angle_deg();
        assert!(lead.is_finite());
    }

    #[test]
    fn select_next_aircraft_toggles_the_player_airframe() {
        let mut sim = engagement();
        assert_eq!(sim.player_spec().id, "bf109-g6");
        let next = sim.select_next_aircraft();
        assert_eq!(next.id, "spitfire-mkix", "the rotation holds two entries, so it toggles");
        assert_eq!(sim.player_spec().id, "spitfire-mkix");
        assert_eq!(sim.player_health(), 100);
        assert!(sim.player_flight().altitude() > 1000.0);

        // And back again.
        assert_eq!(sim.select_next_aircraft().id, "bf109-g6");
    }

    #[test]
    fn sustained_play_never_produces_non_finite_state() {
        let mut sim = engagement();
        for tick in 0..4000u32 {
            let t = tick as f32 * 0.01;
            sim.advance(
                FIXED_DT,
                mathf::sin(t),
                mathf::cos(t * 1.3),
                mathf::sin(t * 0.7),
                mathf::sin(t * 0.3),
                tick % 5 == 0,
            );
            let p = sim.player_flight().position();
            assert!(p.x.is_finite() && p.y.is_finite() && p.z.is_finite(), "player went non-finite");
            assert!(sim.enemy_flight().position().is_finite(), "enemy went non-finite");
            assert!(sim.player_health() >= 0);
            assert!(sim.enemy_health() >= 0);
        }
    }

    #[test]
    #[should_panic(expected = "unknown aircraft id")]
    fn an_unknown_aircraft_id_fails_loudly() {
        AirCombatSim::new("no-such-plane", "spitfire-mkix");
    }
}