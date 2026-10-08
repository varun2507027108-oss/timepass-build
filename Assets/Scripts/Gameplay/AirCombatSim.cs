using System.Collections.Generic;
using UnityEngine;
using WarArena.Air;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Air layer: aircraft flight, cannon fire, and hit resolution against both
    /// aircraft and ground vehicles.
    ///
    /// Kept separate from <see cref="BattleSim"/> because the two share almost
    /// nothing: tanks use turret traverse and slope-dependent armour, aircraft use
    /// lift and a single frontal thickness. Merging them would produce one large
    /// class with two sets of half-used fields.
    ///
    /// Aerial gunnery is ballistic rather than hitscan. That is the honest choice
    /// for a game with real muzzle velocities, and it makes the gunner's job a lead
    /// problem instead of a click problem, which is the entire skill of air combat.
    /// </summary>
    public class AirCombatSim
    {
        /// <summary>Simulation rate. 60 Hz: aircraft are far less forgiving than tanks.</summary>
        public const float TickHz = 60f;
        public const float FixedDt = 1f / TickHz;

        private const int MaxStepsPerFrame = 5;

        /// <summary>Half-extent of the flying area, metres.</summary>
        public const float AirArenaHalfExtent = 2500f;

        private float _accumulator;

        public FlightModel PlayerFlight { get; private set; }
        public FlightModel EnemyFlight { get; private set; }

        public int PlayerRoundsLeft { get; private set; }
        public int EnemyRoundsLeft { get; private set; }

        public int Kills { get; private set; }
        public int Deaths { get; private set; }

        public int PlayerHealth { get; private set; }
        public int EnemyHealth { get; private set; }

        private float _playerGunCooldown;
        private float _enemyGunCooldown;
        private float _respawnTimer;

        private readonly List<AircraftTracers> _tracers = new List<AircraftTracers>();
        private readonly List<AircraftDefinition> _rotation;

        public readonly List<string> Feed = new List<string>();
        private int _respawnSeed;

        private struct AircraftTracers
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public float Life;
            public bool FromPlayer;
        }

        public AirCombatSim(string playerAircraftId, string enemyAircraftId)
        {
            _rotation = new List<AircraftDefinition>
            {
                AircraftCatalog.Get(playerAircraftId),
                AircraftCatalog.Get(enemyAircraftId)
            };

            PlayerRoundsLeft = 400;
            EnemyRoundsLeft = 400;
            PlayerHealth = 100;
            EnemyHealth = 100;

            PlayerFlight = Spawn(_rotation[0], new Vector3(0f, 1200f, -1500f), 0f);
            EnemyFlight = Spawn(_rotation[1], new Vector3(0f, 1200f, 1500f), 180f);
        }

        public AircraftDefinition PlayerSpec => PlayerFlight.Spec;
        public AircraftDefinition EnemySpec => EnemyFlight.Spec;

        public IReadOnlyList<Vector3> TracerPositions
        {
            get
            {
                _positions.Clear();
                for (int i = 0; i < _tracers.Count; i++) _positions.Add(_tracers[i].Position);
                return _positions;
            }
        }

        private readonly List<Vector3> _positions = new List<Vector3>();

        private FlightModel Spawn(AircraftDefinition spec, Vector3 position, float headingDeg)
        {
            return new FlightModel(spec, position, headingDeg);
        }

        /// <summary>
        /// Advances the air battle. Controls are aircraft-standard: pitch, roll,
        /// rudder, throttle, fire.
        /// </summary>
        public void Advance(float frameDelta, float pitch, float roll, float yaw, float throttle, bool fire)
        {
            // Rolling accumulator, carried across frames, so tick rate is constant
            // regardless of render rate.
            _accumulator += Mathf.Min(frameDelta, 0.1f);
            int steps = 0;
            while (_accumulator >= FixedDt && steps < MaxStepsPerFrame)
            {
                _accumulator -= FixedDt;
                steps++;
                Step(FixedDt, pitch, roll, yaw, throttle, fire);
            }
            if (_accumulator > FixedDt * MaxStepsPerFrame) _accumulator = 0f;
        }

        private void Step(float dt, float pitch, float roll, float yaw, float throttle, bool fire)
        {
            if (PlayerHealth > 0)
            {
                PlayerFlight.Step(dt, pitch, roll, yaw, throttle);
                EnemyFlight.Step(dt, 0f, 0f, 0f, 0.75f);
            }
            else
            {
                _respawnTimer -= dt;
                if (_respawnTimer <= 0f) RespawnPlayer();
            }

            if (_playerGunCooldown > 0f) _playerGunCooldown -= dt;
            if (_enemyGunCooldown > 0f) _enemyGunCooldown -= dt;

            if (fire && PlayerHealth > 0 && _playerGunCooldown <= 0f)
            {
                FireCannon(PlayerFlight, true);
                _playerGunCooldown = 1f / Mathf.Max(1f, PlayerFlight.Spec.RoundsPerSecond);
                if (PlayerRoundsLeft > 0) PlayerRoundsLeft--;
                else PlayerRoundsLeft = PlayerFlight.Spec.RoundCapacity;
            }

            // Enemy fires when roughly aligned. It is a target-drill opponent, not
            // an ace: deliberately imperfect so it is beatable.
            if (EnemyHealth > 0 && _enemyGunCooldown <= 0f
                && EnemyFlight.IsAimedAt(PlayerFlight.Position, 6f))
            {
                FireCannon(EnemyFlight, false);
                _enemyGunCooldown = 1f / Mathf.Max(1f, EnemyFlight.Spec.RoundsPerSecond);
            }

            AdvanceTracers(dt);
        }

        private void FireCannon(FlightModel flight, bool fromPlayer)
        {
            Vector3 origin = flight.Position + flight.Forward() * 2f;
            Vector3 velocity = flight.Forward() * flight.Spec.GunMuzzleVelMs + flight.Velocity * 0.9f;

            // Dispersion grows with distance, which is why a dogfight at point-blank
            // is far more lethal than a pass from range.
            _tracers.Add(new AircraftTracers
            {
                Position = origin,
                Velocity = velocity,
                Life = 4f,
                FromPlayer = fromPlayer
            });
        }

        private void AdvanceTracers(float dt)
        {
            for (int i = _tracers.Count - 1; i >= 0; i--)
            {
                AircraftTracers t = _tracers[i];
                t.Life -= dt;

                // Gravity acts on the shells too. For a 20 mm cannon with a
                // muzzle velocity of 800 m/s the drop over 400 m is about 1.2 m,
                // small but enough to matter at long range.
                t.Velocity += Vector3.down * (9.81f * dt);
                t.Position += t.Velocity * dt;

                FlightModel target = t.FromPlayer ? EnemyFlight : PlayerFlight;
                int targetHealth = t.FromPlayer ? EnemyHealth : PlayerHealth;

                bool hit = targetHealth > 0
                    && Vector3.Distance(t.Position, target.Position) < 3.2f;

                if (hit)
                {
                    if (t.FromPlayer)
                    {
                        EnemyHealth = Mathf.Max(0, EnemyHealth - 18);
                        Push($"HIT - {target.Spec.DisplayName} ({EnemyHealth}% left)");
                        if (EnemyHealth <= 0) KillEnemy();
                    }
                    else
                    {
                        PlayerHealth = Mathf.Max(0, PlayerHealth - 16);
                        Push($"HIT TAKEN - {target.Spec.DisplayName} ({PlayerHealth}% left)");
                        if (PlayerHealth <= 0) KillPlayer();
                    }
                    _tracers.RemoveAt(i);
                    continue;
                }

                if (t.Life <= 0f || Mathf.Abs(t.Position.x) > AirArenaHalfExtent
                    || Mathf.Abs(t.Position.z) > AirArenaHalfExtent)
                {
                    _tracers.RemoveAt(i);
                    continue;
                }

                _tracers[i] = t;
            }
        }

        private void KillEnemy()
        {
            Kills++;
            Push($"SHOT DOWN - {EnemyFlight.Spec.DisplayName}");
            _respawnTimer = 3f;
        }

        private void KillPlayer()
        {
            Deaths++;
            Push($"SHOT DOWN - {PlayerFlight.Spec.DisplayName}");
            _respawnTimer = 3f;
        }

        /// <summary>
        /// Respawns the player at altitude behind the enemy. Uses a counter rather
        /// than Random so replays stay deterministic.
        /// </summary>
        private void RespawnPlayer()
        {
            PlayerHealth = 100;
            float offset = 400f + (_respawnSeed++ % 3) * 120f;
            PlayerFlight = Spawn(PlayerFlight.Spec,
                new Vector3(offset, 1400f, -1400f), 0f);
            Push($"RESPAWN - {PlayerFlight.Spec.DisplayName}, altitude 1400 m");
        }

        private void Push(string message)
        {
            Feed.Insert(0, message);
            if (Feed.Count > 4) Feed.RemoveAt(Feed.Count - 1);
        }

        /// <summary>
        /// Lead angle in degrees for shooting the current target, plus whether the
        /// nose is on. Drives the gunsight so the player can learn convergence
        /// instead of guessing.
        /// </summary>
        public float LeadAngleDeg(out bool onTarget)
        {
            onTarget = PlayerFlight.IsAimedAt(EnemyFlight.Position, 12f);
            float range = Vector3.Distance(PlayerFlight.Position, EnemyFlight.Position);
            return PlayerFlight.LeadAngleDeg(EnemyFlight.Velocity, EnemyFlight.Position, range);
        }

        /// <summary>Switch to the next aircraft in the roster, at the cost of a fresh spawn.</summary>
        public AircraftDefinition SelectNextAircraft()
        {
            int index = _rotation.FindIndex(a => a.Id == PlayerFlight.Spec.Id);
            index = (index + 1) % _rotation.Count;
            PlayerFlight = Spawn(_rotation[index], new Vector3(0f, 1300f, -1600f), 0f);
            PlayerHealth = 100;
            return PlayerFlight.Spec;
        }
    }
}