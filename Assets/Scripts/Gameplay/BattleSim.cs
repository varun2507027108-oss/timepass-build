using System.Collections.Generic;
using UnityEngine;
using WarArena.Ballistics;
using WarArena.Vehicles;

namespace WarArena.Gameplay
{
    /// <summary>
    /// The battle: two vehicles driving a fixed-step arena, shells as travelling
    /// projectiles, and hit resolution through the real armour model.
    ///
    /// This mirrors the shape the web prototype used so that behaviour stays
    /// comparable between the two, but the numbers come from
    /// <see cref="VehicleCatalog"/> rather than a hand-written JSON file.
    /// </summary>
    public class BattleSim
    {
        /// <summary>Simulation rate. 30 Hz is plenty for a 40 km/h vehicle.</summary>
        public const float TickHz = 30f;
        public const float FixedDt = 1f / TickHz;

        /// <summary>Radius of the circular playable arena, metres.</summary>
        public const float ArenaRadius = 440f;
        /// <summary>Compatibility alias for legacy tests and scripts.</summary>
        public const float ArenaHalfExtent = ArenaRadius;

        /// <summary>Hit radius in the horizontal plane: hull footprint plus turret.</summary>
        public const float HitRadiusM = 3.2f;
        /// <summary>
        /// How far above a vehicle's own ground contact a shell may pass and still
        /// strike the hull, metres. RELATIVE to the target — never an absolute world
        /// height, which makes every shot on the hills (0..74 m) a guaranteed miss.
        /// Must match HIT_CEILING_M in crates/war-core/src/gameplay/battle_sim.rs.
        /// </summary>
        public const float HitCeilingM = 4.5f;
        /// <summary>
        /// How far below a vehicle's own ground contact a shell may pass and still
        /// strike the hull, metres.
        /// Must match HIT_FLOOR_M in crates/war-core/src/gameplay/battle_sim.rs.
        /// </summary>
        public const float HitFloorM = 1.0f;

        public static Vector2 ClampToArena(float x, float z)
        {
            float r = Mathf.Sqrt(x * x + z * z);
            if (r <= ArenaRadius) return new Vector2(x, z);
            if (r < 1e-4f) return Vector2.zero;
            float k = ArenaRadius / r;
            return new Vector2(x * k, z * k);
        }

        /// <summary>
        /// Half-width of a vehicle's collision footprint, metres. This is the
        /// contact half-width the suspension already uses, so the body that collides
        /// is the same size as the body that rides.
        /// </summary>
        public const float VehicleCollisionRadiusM = 1.5f;

        /// <summary>Resolved hit against an obstacle this step, for feedback and tests.</summary>
        public bool DidCollideWithObstacle { get; private set; }

        /// <summary>
        /// Push a vehicle out of any solid obstacle it has entered.
        /// </summary>
        /// <remarks>
        /// The vehicle is treated as a circle and the obstacle as a vertical cylinder;
        /// overlap is resolved by pushing the vehicle out along the contact normal and
        /// cancelling the velocity component driving it further in, which scrubs speed
        /// along the wall instead of stopping the vehicle dead.
        ///
        /// Iterated a few times because two obstacles can overlap and one pass leaves
        /// the vehicle inside the second. Cheap: the loop exits as soon as nothing is
        /// touching, which is the overwhelmingly common case.
        /// </remarks>
        private static bool ResolveObstacles(ref Vector2 position, float vehicleRadius)
        {
            var obstacles = BattlefieldObstacles.All;
            if (obstacles.Count == 0) return false;

            bool touchedAnywhere = false;

            // Resolve the DEEPEST overlap each pass, one obstacle at a time.
            //
            // Sweeping every obstacle in order looks equivalent but is not. Two adjacent
            // trees have exclusion discs that do not overlap, yet pushing a vehicle out
            // of one TOWARD its neighbour lands it inside that neighbour, and the next
            // sweep pushes it back. Sequential resolution oscillates on any pair of
            // nearby obstacles and runs out of passes still inside one of them. Taking
            // the single deepest violation first converges.
            for (int pass = 0; pass < 24; pass++)
            {
                int bestIndex = -1;
                float bestDepth = 0f;

                for (int i = 0; i < obstacles.Count; i++)
                {
                    var o = obstacles[i];
                    float dx = position.x - o.X;
                    float dz = position.y - o.Z;
                    float distSq = dx * dx + dz * dz;
                    float reach = o.Radius + vehicleRadius;

                    if (distSq >= reach * reach) continue;

                    float depth = reach - Mathf.Sqrt(distSq);
                    if (depth > bestDepth)
                    {
                        bestDepth = depth;
                        bestIndex = i;
                    }
                }

                if (bestIndex < 0) break;

                var target = obstacles[bestIndex];
                float tdx = position.x - target.X;
                float tdz = position.y - target.Z;
                float dist = Mathf.Sqrt(tdx * tdx + tdz * tdz);

                float nx, nz;
                if (dist < 1e-4f)
                {
                    // Dead centre: no direction to resolve along, so pick one
                    // deterministically rather than dividing by zero. The DISTANCE is
                    // left at its true value of zero -- overwriting it here pushed the
                    // vehicle one metre short of the boundary.
                    nx = 1f; nz = 0f;
                }
                else
                {
                    nx = tdx / dist; nz = tdz / dist;
                }

                // A hair of separation beyond contact. Resolving to exactly the reach
                // distance leaves the vehicle resting on the boundary, where float
                // rounding can place it a fraction of a millimetre INSIDE, which then
                // reads as a collision that never actually happened.
                const float Separation = 1e-3f;
                position.x += nx * (bestDepth + Separation);
                position.y += nz * (bestDepth + Separation);
                touchedAnywhere = true;
            }

            return touchedAnywhere;
        }

        public static float SlopeSpeedFactor(float x, float z, Vector2 moveDir)
        {
            const float d = 1.5f;
            float ahead  = BattlefieldBuilder.GetTerrainHeight(x + moveDir.x * d, z + moveDir.y * d);
            float behind = BattlefieldBuilder.GetTerrainHeight(x - moveDir.x * d, z - moveDir.y * d);
            float deg = Mathf.Atan2(ahead - behind, 2f * d) * Mathf.Rad2Deg; // positive = climbing

            if (deg <= 0f)  return 1f;                                // flat or downhill: no penalty
            if (deg >= 32f) return 0f;                                // too steep to climb
            if (deg <= 20f) return Mathf.Lerp(1f, 0.6f, deg / 20f);
            return Mathf.Lerp(0.6f, 0.25f, (deg - 20f) / 12f);
        }

        /// <summary>Hard ceiling on catch-up ticks so a stalled tab cannot teleport units.</summary>
        private const int MaxStepsPerFrame = 4;

        /// <summary>Turret traverse rate, radians per second.</summary>
        public const float TurretSlewRate = 2.6f;

        /// <summary>Maximum turret travel relative to the hull, radians.</summary>
        public const float MaxTraverse = 2.1f;

        /// <summary>Gun elevation limits and slew rate in radians.</summary>
        public const float MinGunPitchRad = -10f * Mathf.Deg2Rad;
        public const float MaxGunPitchRad = 20f * Mathf.Deg2Rad;
        public const float GunElevationSlewRate = 1.8f;

        private const float MaxSpeedMps = 12f;
        private const float MaxTurnRateRad = 1.8f;
        private const float AccelerationMps2 = 12.0f;
        private const float CoastDecelMps2 = 8.0f;
        private const float BrakingDecelMps2 = 45.0f;

        // --- tracked-vehicle contact geometry ---------------------------------
        // Defined once in TrackedVehicleDynamics and shared with the visual
        // ground-contact solve, so the slope the player feels and the tilt they see
        // are computed from the same four terrain samples.
        private const float TrackGaugeM = TrackedVehicleDynamics.TrackGaugeM;
        private const float TrackWidthM = TrackedVehicleDynamics.TrackWidthM;

        // --- drivetrain / resistance ------------------------------------------
        /// <summary>
        /// Speed above which available acceleration becomes power-limited and starts
        /// to fall off as 1/v, m/s. Real traction effort is roughly constant below
        /// this and constant power above it.
        /// </summary>
        private const float PowerLimitedSpeedMps = 6f;
        /// <summary>
        /// Coefficient of rolling resistance on firm ground. The documented band for
        /// tracked vehicles is 0.03-0.09; 0.055 is mid-range for a tank on packed
        /// earth.
        /// </summary>
        private const float RollingResistanceCoeff = 0.055f;
        /// <summary>
        /// Friction coefficient resisting sideways motion. Much higher than rolling
        /// resistance: tracks do not skid sideways easily, which is why a tank holds a
        /// line across a camber.
        /// </summary>
        private const float LateralFrictionCoeff = 0.85f;
        /// <summary>Total friction budget available from the tracks, m/s^2.</summary>
        private const float TrackedGripAccelMps2 = 9.81f * 0.85f;
        /// <summary>Lateral slip stiffness, see TrackedVehicleDynamics.</summary>
        private const float LateralSlipStiffness = TrackedVehicleDynamics.LateralSlipStiffnessPerMps;
        /// <summary>Deceleration when the throttle opposes the direction of travel.</summary>
        private const float BrakeAccelMps2 = 12.0f;
        /// <summary>
        /// Impulse of gun recoil as a change in forward speed, m/s.
        ///
        /// Applied as a CAP rather than a subtraction. A 120 mm gun firing on a
        /// stationary tank used to drive it backwards at 1.2 m/s, where it then rolled
        /// away on its own forever; a tank's tracks and the ground hold it.
        /// </summary>
        private const float RecoilDeltaSpeedMps = -1.2f;
        /// <summary>
        /// Height of the muzzle above the hull's ground-contact datum, metres.
        ///
        /// The turret ring sits 2.1 m up and the trunnion a little above that. This
        /// used to be a hand-fitted 2.2 m, which happens to look right in a
        /// screenshot and is wrong by a metre, so every commanded gun elevation was
        /// depressed by roughly atan2(error, range) -- about a degree at 100 m and
        /// four at 28 m.
        /// </summary>
        public const float MuzzleHeightM = 2.25f;
        /// <summary>
        /// Speed at which full steering input still produces the full turn rate,
        /// m/s. Above this the differential has to push harder for the same radius.
        /// </summary>
        private const float SpeedAtMaxYawMps = 4.5f;
        /// <summary>
        /// How far the two track speeds separate under full steering input, as a
        /// fraction of the vehicle's own speed. A skid-steered vehicle turns by
        /// driving one track faster than the other, so this is the physical meaning
        /// of the steering axis: at full lock the inside track runs at
        /// (1 - DifferentialSkew) of the vehicle speed and the outside at
        /// (1 + DifferentialSkew).
        /// </summary>
        public const float DifferentialSkew = 0.5f;

        /// <summary>
        /// How strongly the hull is pulled onto the gun's line while driving.
        ///
        /// 1.0 would snap the hull directly at the gun every frame and make the
        /// turret's local angle collapse to zero, which would throw away manual
        /// traverse entirely. At 0.35 the vehicle lines up over a couple of seconds
        /// of driving, so the hull visibly follows the turret without the turret
        /// feeling welded to the centreline.
        /// </summary>
        private const float HullAutoAlignStrength = 0.35f;

        private readonly VehicleCatalog.VehicleDefinition _playerDef;
        private readonly VehicleCatalog.VehicleDefinition _enemyDef;

        public Vector3 PlayerPosition { get; private set; }
        public float PlayerHeadingRad { get; private set; }
        public float PlayerTurretYawRad { get; private set; }
        public float PlayerGunPitchRad { get; private set; }
        public float PlayerSpeedKph { get; private set; }

        public Vector3 EnemyPosition { get; private set; }
        public float EnemyHeadingRad { get; private set; }
        public float EnemyTurretYawRad { get; private set; }

        public int PlayerHealth { get; private set; }
        public int EnemyHealth { get; private set; }
        public int Kills { get; private set; }
        public int Deaths { get; private set; }

        public float ReloadRemaining { get; private set; }
        public bool ReloadComplete => ReloadRemaining <= 0f;

        private bool _usePhysicsMovement = false;

        /// <summary>
        /// When enabled, player movement is simulated using PhysX TrackedVehiclePhysics
        /// instead of the kinematic BattleSim traction integration.
        /// Defaults to false to preserve deterministic parity tests.
        /// </summary>
        public bool UsePhysicsMovement
        {
            get => _usePhysicsMovement;
            set
            {
                _usePhysicsMovement = value;
                if (PlayerPhysicsVehicle != null)
                {
                    PlayerPhysicsVehicle.SetActive(value);
                }
                Physics.simulationMode = value ? SimulationMode.Script : SimulationMode.FixedUpdate;
            }
        }

        public TrackedVehiclePhysics PlayerPhysicsVehicle;

        public event System.Action OnPlayerFired;

        public readonly List<string> Feed = new List<string>();
        private readonly List<ActiveShell> _inFlight = new List<ActiveShell>();
        private float _enemyReload;

        /// <summary>
        /// Enemy forward and sideways velocity in its hull frame, m/s. Held as state
        /// so the AI obeys the same terrain forces, rolling resistance and friction
        /// circle as the player instead of teleporting along a fixed ground speed.
        /// </summary>
        private float _enemySpeedMps;
        private float _enemyLatSpeedMps;
        private float _respawnTimer;
        private int _tick;

        private struct ActiveShell
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public float Life;
            public bool FromPlayer;
            public Ballistics.Shell Ordnance;
        }

        public BattleSim(string playerVehicleId, string enemyVehicleId)
        {
            _playerDef = VehicleCatalog.Get(playerVehicleId);
            _enemyDef = VehicleCatalog.Get(enemyVehicleId);

            PlayerPosition = new Vector3(0f, 0f, -35f);
            EnemyPosition = new Vector3(0f, 0f, 35f);
            PlayerHeadingRad = 0f;
            EnemyHeadingRad = Mathf.PI;

            PlayerHealth = 100;
            EnemyHealth = 100;

            // Seed the interpolation history so the very first frame does not lerp
            // from the origin and teleport the tank across the map.
            PlayerPositionPrev = PlayerPosition;
            EnemyPositionPrev = EnemyPosition;
            PlayerHeadingPrevRad = PlayerHeadingRad;
            PlayerGunPitchPrevRad = PlayerGunPitchRad = 0f;
            HullPitchPrevRad = 0f;
            HullRollPrevRad = 0f;
            InterpolationAlpha = 1f;
        }

        public VehicleCatalog.VehicleDefinition PlayerSpec => _playerDef;
        public VehicleCatalog.VehicleDefinition EnemySpec => _enemyDef;

        /// <summary>Shells currently in flight, for the renderer to draw tracers.</summary>
        public IReadOnlyList<Vector3> InFlightPositions
        {
            get
            {
                _positions.Clear();
                for (int i = 0; i < _inFlight.Count; i++) _positions.Add(_inFlight[i].Position);
                return _positions;
            }
        }

        private readonly List<Vector3> _positions = new List<Vector3>();

        /// <summary>
        /// Advances the battle. <paramref name="aimPoint"/> is the world point the
        /// player is aiming at, normally the mouse's ground intersection.
        /// </summary>
        public void Advance(float frameDelta, float throttle, float steer, float turretTrim,
            bool fire, Vector3 aimPoint)
        {
            // Rolling accumulator, held across calls, so the simulation rate is
            // constant regardless of render frame rate.
            _accumulator += Mathf.Min(frameDelta, 0.1f);
            int steps = 0;
            while (_accumulator >= FixedDt && steps < MaxStepsPerFrame)
            {
                _accumulator -= FixedDt;
                steps++;

                // Snapshot the state IMMEDIATELY before each step, not once before the
                // loop.
                //
                // Capturing before the loop made Prev the state before ALL steps this
                // frame. When a frame runs two or more ticks -- which happens whenever
                // the frame rate dips below 30 Hz, exactly when the player is most
                // likely to be watching -- the interpolation then spanned the whole
                // multi-step jump instead of the last tick, and the drawn vehicle
                // lagged the simulation by up to MaxStepsPerFrame ticks.
                PlayerPositionPrev = PlayerPosition;
                EnemyPositionPrev = EnemyPosition;
                PlayerHeadingPrevRad = PlayerHeadingRad;
                PlayerGunPitchPrevRad = PlayerGunPitchRad;
                HullPitchPrevRad = HullPitchRad;
                HullRollPrevRad = HullRollRad;

                Step(FixedDt, throttle, steer, turretTrim, fire, aimPoint);
            }
            if (_accumulator > FixedDt * MaxStepsPerFrame) _accumulator = 0f;

            InterpolationAlpha = steps == 0
                ? 1f
                : Mathf.Clamp01(_accumulator / FixedDt);
        }

        /// <summary>
        /// Position at the start of the current render frame, for interpolation.
        /// </summary>
        public Vector3 PlayerPositionPrev { get; private set; }

        public Vector3 EnemyPositionPrev { get; private set; }

        public float PlayerHeadingPrevRad { get; private set; }

        public float PlayerGunPitchPrevRad { get; private set; }

        /// <summary>Suspension attitude at the previous simulation tick, radians.</summary>
        public float HullPitchPrevRad { get; private set; }

        /// <inheritdoc cref="HullPitchPrevRad"/>
        public float HullRollPrevRad { get; private set; }

        /// <summary>
        /// Fraction of the way from the previous simulation state to the current one,
        /// for rendering between ticks.
        ///
        /// Without this the vehicles visibly step: the simulation runs at a fixed rate
        /// while the camera moves smoothly, so the tank judders against a steady
        /// background. Interpolating the drawn transform removes the stepping entirely
        /// and costs nothing.
        /// </summary>
        public float InterpolationAlpha { get; private set; } = 1f;

        /// <summary>Interpolated player position for drawing this frame.</summary>
        public Vector3 PlayerRenderPosition =>
            Vector3.Lerp(PlayerPositionPrev, PlayerPosition, InterpolationAlpha);

        /// <summary>Interpolated enemy position for drawing this frame.</summary>
        public Vector3 EnemyRenderPosition =>
            Vector3.Lerp(EnemyPositionPrev, EnemyPosition, InterpolationAlpha);

        /// <summary>
        /// Interpolated player hull heading for drawing this frame.
        ///
        /// Angle-aware, because heading is wrapped to plus or minus pi and a plain
        /// lerp across the wrap would spin the tank the long way round.
        /// </summary>
        public float PlayerRenderHeadingRad
        {
            get
            {
                float delta = Mathf.DeltaAngle(
                    PlayerHeadingPrevRad * Mathf.Rad2Deg,
                    PlayerHeadingRad * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                return PlayerHeadingPrevRad + delta * InterpolationAlpha;
            }
        }

        /// <summary>Interpolated player gun pitch for drawing this frame.</summary>
        public float PlayerRenderGunPitchRad =>
            Mathf.Lerp(PlayerGunPitchPrevRad, PlayerGunPitchRad, InterpolationAlpha);

        /// <summary>
        /// Hull attitude interpolated between simulation ticks, radians.
        /// </summary>
        /// <remarks>
        /// The attitude used to be read straight off the simulation while position, heading
        /// and gun elevation all went through <see cref="InterpolationAlpha">. The view
        /// then smoothed it with its own exponential lag, which is a different filter at a
        /// different time constant driven by render frame time rather than tick time. The
        /// result was two disagreeing smoothing paths: the hull leaned into and out of
        /// terrain a beat behind or ahead of where the body was actually travelling.
        ///
        /// <see cref="Mathf.LerpAngle"/> rather than <c>Lerp</c>, so the interpolation is
        /// correct across the wrap even though the attitude is clamped well inside +/-45
        /// degrees.
        /// </remarks>
        public float PlayerRenderHullPitchRad =>
            Mathf.LerpAngle(HullPitchPrevRad, HullPitchRad, InterpolationAlpha);

        /// <inheritdoc cref="PlayerRenderHullPitchRad"/>
        public float PlayerRenderHullRollRad =>
            Mathf.LerpAngle(HullRollPrevRad, HullRollRad, InterpolationAlpha);

        private float _accumulator;

        private void Step(float dt, float throttle, float steer, float turretTrim,
            bool fire, Vector3 aimPoint)
        {
            _tick++;
            if (ReloadRemaining > 0f) ReloadRemaining -= dt;
            if (_enemyReload > 0f) _enemyReload -= dt;
            if (_respawnTimer > 0f)
            {
                _respawnTimer -= dt;
                if (_respawnTimer <= 0f)
                {
                    if (PlayerHealth <= 0) PlayerHealth = 100;
                    if (EnemyHealth <= 0) EnemyHealth = 100;
                }
            }

            if (PlayerHealth > 0)
            {
                if (UsePhysicsMovement && PlayerPhysicsVehicle != null && PlayerPhysicsVehicle.rb != null)
                {
                    DrivePlayerPhysics(dt, throttle, steer);
                }
                else
                {
                    DrivePlayer(dt, throttle, steer);
                }
                AimTurret(dt, turretTrim, aimPoint);

                if (fire && ReloadComplete) FirePlayer();
            }

            if (EnemyHealth > 0)
            {
                DriveEnemy(dt);
            }

            AdvanceShells(dt);
        }

        private void DrivePlayerPhysics(float dt, float throttle, float steer)
        {
            float moving = Mathf.Abs(throttle) > 0.01f ? 1f : 0f;
            float gunWorldHeading = WrapAngle(PlayerHeadingRad + PlayerTurretYawRad);
            float toGunLine = WrapAngle(gunWorldHeading - PlayerHeadingRad);

            float demand = (toGunLine * HullAutoAlignStrength * moving) + steer;
            demand = Mathf.Clamp(demand, -1f, 1f);

            PlayerPhysicsVehicle.throttle = throttle;
            PlayerPhysicsVehicle.steer = demand;
            PlayerPhysicsVehicle.maxSpeed = MaxSpeedMps;

            PlayerPhysicsVehicle.ApplyForces();
            Physics.simulationMode = SimulationMode.Script;
            Physics.Simulate(dt);

            Vector3 rbPos = PlayerPhysicsVehicle.rb.position;
            Vector2 clamped = ClampToArena(rbPos.x, rbPos.z);
            if (!Mathf.Approximately(clamped.x, rbPos.x) || !Mathf.Approximately(clamped.y, rbPos.z))
            {
                rbPos.x = clamped.x;
                rbPos.z = clamped.y;
                PlayerPhysicsVehicle.rb.position = rbPos;
            }

            PlayerPosition = rbPos;
            PlayerHeadingRad = WrapAngle(PlayerPhysicsVehicle.rb.rotation.eulerAngles.y * Mathf.Deg2Rad);
            PlayerSpeedKph = PlayerPhysicsVehicle.rb.linearVelocity.magnitude * 3.6f;
        }

        private float _playerCurrentSpeedMps;

        /// <summary>
        /// Sideways velocity in the hull's frame, m/s. Non-zero only when the
        /// tracks are sliding — over a side slope, or while skidding out of a turn.
        /// This is what makes a vehicle on a camber slide toward the fall line
        /// instead of driving wherever it happens to be pointed.
        /// </summary>
        private float _playerLatSpeedMps;

        /// <summary>
        /// Per-contact hull suspension. Owns pitch, roll and ride height as real
        /// degrees of freedom driven by four spring-dampers, so weight transfer and
        /// cross-slope settling are emergent rather than cosmetic.
        /// </summary>
        private HullSuspension _playerSuspension;

        /// <summary>
        /// Longitudinal acceleration last computed in <see cref="DrivePlayer"/>,
        /// m/s^2. Fed to the suspension to produce weight transfer.
        /// </summary>
        private float _playerAccelLong;

        /// <inheritdoc cref="_playerAccelLong"/>
        private float _playerAccelLat;

        /// <summary>
        /// Ground speed of each track, m/s, positive forwards. This is what the track
        /// animation is driven from, so the wheels are a readout of the simulation
        /// instead of a decoration replaying the input.
        /// </summary>
        public float PlayerLeftTrackMps { get; private set; }
        /// <inheritdoc cref="PlayerLeftTrackMps"/>
        public float PlayerRightTrackMps { get; private set; }
        /// <inheritdoc cref="PlayerLeftTrackMps"/>
        public float EnemyLeftTrackMps { get; private set; }
        /// <inheritdoc cref="PlayerLeftTrackMps"/>
        public float EnemyRightTrackMps { get; private set; }

        /// <summary>
        /// Hull pitch and roll, radians, from the suspension. Positive pitch is nose
        /// up; positive roll is right side up. These are real degrees of freedom, not
        /// a cosmetic fit to the terrain.
        /// </summary>
        public float HullPitchRad { get; private set; }
        /// <inheritdoc cref="HullPitchRad"/>
        public float HullRollRad { get; private set; }

        /// <summary>
        /// Slope of the terrain under the hull, in the hull's frame, degrees.
        /// Positive longitudinal means climbing; positive lateral means the ground
        /// falls away to the right.
        /// </summary>
        public float SlopeForwardDeg { get; private set; }
        /// <inheritdoc cref="SlopeForwardDeg"/>
        public float SlopeLateralDeg { get; private set; }

        private void DrivePlayer(float dt, float throttle, float steer)
        {
            throttle = Mathf.Clamp(throttle, -1f, 1f);
            steer = Mathf.Clamp(steer, -1f, 1f);

            Vector3 fwd3 = new Vector3(Mathf.Sin(PlayerHeadingRad), 0f, Mathf.Cos(PlayerHeadingRad));
            Vector2 fwd = new Vector2(fwd3.x, fwd3.z);
            Vector2 right = new Vector2(fwd3.z, -fwd3.x);

            // --- terrain frame -------------------------------------------------
            // Half-dimensions: these are the fore/aft and lateral probe offsets from
            // the centre of the hull, not the overall wheelbase and track width.
            float slopeFwdDeg, slopeLatDeg;
            TrackedVehicleDynamics.SampleSlopes(
                PlayerPosition.x, PlayerPosition.z, fwd, right,
                TrackedVehicleDynamics.HalfGaugeM, TrackedVehicleDynamics.HalfTrackWidthM,
                out slopeFwdDeg, out slopeLatDeg);

            float normalLoadG;
            Vector2 gravity = TrackedVehicleDynamics.GravityOnGroundPlane(
                fwd, right, slopeFwdDeg, slopeLatDeg, out normalLoadG);

            // --- longitudinal forces ------------------------------------------
            float aEngine = TrackedVehicleDynamics.EngineAcceleration(
                throttle, _playerCurrentSpeedMps, AccelerationMps2, PowerLimitedSpeedMps, MaxSpeedMps);
            float aRoll = TrackedVehicleDynamics.RollingResistance(
                _playerCurrentSpeedMps, normalLoadG, RollingResistanceCoeff);

            float desiredLong = aEngine + gravity.x + aRoll;

            // Braking. A tank's tracks are its brakes: holding the throttle against
            // the direction of travel locks them, which is a far larger deceleration
            // than the engine can ever produce in reverse.
            if (throttle * _playerCurrentSpeedMps < -0.02f)
            {
                desiredLong = -Mathf.Sign(_playerCurrentSpeedMps) * BrakeAccelMps2;
            }

            // --- lateral: tracks resist sideways motion strongly -------------
            float aLatGrip = TrackedVehicleDynamics.LateralGripAccel(
                _playerLatSpeedMps, normalLoadG, LateralFrictionCoeff, LateralSlipStiffness);

            // Friction circle: whatever the tracks are already spending sideways is
            // unavailable for acceleration. Without this, traction is infinite in any
            // direction at once.
            float aLong = TrackedVehicleDynamics.ApplyFrictionCircle(
                desiredLong, aLatGrip, TrackedGripAccelMps2);

            _playerCurrentSpeedMps += aLong * dt;
            _playerLatSpeedMps += aLatGrip * dt;

            // Scrub loss while skidding to turn.
            _playerCurrentSpeedMps += TrackedVehicleDynamics.SkidScrubSpeedLoss(steer, _playerCurrentSpeedMps) * dt;

            // Coasting drag: a tank with the throttle closed still slows, because the
            // engine is dragging and the transmission is in neutral.
            if (Mathf.Abs(throttle) < 0.01f)
            {
                _playerCurrentSpeedMps = Mathf.MoveTowards(_playerCurrentSpeedMps, 0f, CoastDecelMps2 * dt);
            }

            // Numerical floor: on a slope the gravity term can very nearly cancel the
            // engine. Let it settle rather than jitter.
            if (Mathf.Abs(_playerCurrentSpeedMps) < 1e-3f) _playerCurrentSpeedMps = 0f;
            if (Mathf.Abs(_playerLatSpeedMps) < 1e-3f) _playerLatSpeedMps = 0f;

            // --- steering ------------------------------------------------------
            // The hull steers onto the gun's line while driving.
            //
            // Without this the tank and its turret always disagree: the gun tracks
            // the mouse in world space while the hull keeps whatever heading the
            // driver chose, so the turret visibly swings the opposite way to the tank
            // every time the hull turns. It reads as an inverted control even though
            // the gun is in fact pointing at the target. Real tracked vehicles do
            // exactly this -- the hull is what rotates, and driving straight lines the
            // vehicle up with where the gun is pointing.
            //
            // `toGunLine` is an ANGLE in radians, so it is scaled into the same
            // normalised [-1,1] domain as the steering input. The previous code added
            // raw radians to a normalised demand, which meant the "0.35 strength" was
            // really 73% of full lock at full traverse.
            float moving = Mathf.Abs(throttle) > 0.01f ? 1f : 0f;
            float gunWorldHeading = WrapAngle(PlayerHeadingRad + PlayerTurretYawRad);
            float toGunLine = WrapAngle(gunWorldHeading - PlayerHeadingRad);

            float align = Mathf.Clamp(toGunLine / MaxTraverse, -1f, 1f) * HullAutoAlignStrength * moving;
            float demand = Mathf.Clamp(align + steer, -1f, 1f);

            float yawRate = TrackedVehicleDynamics.SkidSteerYawRate(
                _playerCurrentSpeedMps, demand, TrackGaugeM, MaxTurnRateRad, SpeedAtMaxYawMps);

            // A side slope takes some of the steering authority away before the
            // differential is even used, because the tracks are already sliding.
            yawRate *= TrackedVehicleDynamics.LateralSlopeSteeringScale(slopeLatDeg);

            PlayerHeadingRad = WrapAngle(PlayerHeadingRad + yawRate * dt);

            // --- integrate position --------------------------------------------
            // The velocity components live in the HULL frame, and the hull has just
            // rotated, so carry them round with it. Rebuilding the components from
            // the new axes without re-projecting would silently re-aim any sideways
            // slide by the amount the hull turned.
            float c = Mathf.Cos(yawRate * dt);
            float s = Mathf.Sin(yawRate * dt);
            float vLongNew = _playerCurrentSpeedMps * c + _playerLatSpeedMps * s;
            float vLatNew = -_playerCurrentSpeedMps * s + _playerLatSpeedMps * c;
            _playerCurrentSpeedMps = vLongNew;
            _playerLatSpeedMps = vLatNew;

            // Motion follows the velocity vector, not the hull axis, so a tank
            // sliding sideways down a camber genuinely moves sideways.
            Vector3 fwdNow = new Vector3(Mathf.Sin(PlayerHeadingRad), 0f, Mathf.Cos(PlayerHeadingRad));
            Vector2 rightNow = new Vector2(fwdNow.z, -fwdNow.x);
            // NOTE: build the horizontal forward vector explicitly. Unity's implicit
            // Vector3 -> Vector2 conversion keeps (x, y) and DISCARDS z, so passing a
            // Vector3 here silently yields (sin h, 0) and every projection against it
            // collapses to zero.
            Vector2 fwdNow2D = new Vector2(fwdNow.x, fwdNow.z);
            Vector2 velocity = fwdNow2D * _playerCurrentSpeedMps + rightNow * _playerLatSpeedMps;

            // NOTE: the reciprocal is computed once and multiplied through.
            // Dividing by dt is a different floating-point expression and diverges
            // from the Rust mirror by more than the parity tolerance once the force
            // model is in play.
            float invDt = 1f / Mathf.Max(dt, 1e-5f);
            Vector2 from = new Vector2(PlayerPosition.x, PlayerPosition.z);
            Vector2 next = from + velocity * dt;

            Vector2 clamped = ClampToArena(next.x, next.y);

            // Solid obstacles. Terrain height and obstacle collision are different
            // problems: the suspension keeps the hull at the right height while the
            // vehicle still drives straight through a tree trunk.
            DidCollideWithObstacle = ResolveObstacles(ref clamped, VehicleCollisionRadiusM);
            float posY = BattlefieldBuilder.GetTerrainHeight(clamped.x, clamped.y);
            PlayerPosition = new Vector3(clamped.x, posY, clamped.y);

            // Weight transfer: the acceleration the hull is actually experiencing in
            // its own frame. Fed to the suspension, which turns it into pitch and
            // roll moments because the centre of gravity sits above the track plane.
            _playerAccelLong = aLong;
            _playerAccelLat = aLatGrip;

            StepSuspension(dt);

            // Cancel the velocity component driven into the boundary, so the vehicle
            // slides along the edge instead of pressing into it and snapping back.
            Vector2 actual = clamped - from;
            float along = Vector2.Dot(actual, fwdNow2D);
            _playerCurrentSpeedMps = along * invDt;
            _playerLatSpeedMps = Vector2.Dot(actual, rightNow) * invDt;

            // Ground speed is what the driver feels and what the HUD should show.
            // Using |longitudinal| alone would report full speed while the vehicle is
            // barely moving because it is sliding sideways on a 30 degree slope.
            PlayerSpeedKph = actual.magnitude * invDt * 3.6f;

            // Differential steering: the tracks run at different speeds, and that
            // difference is what actually turns the vehicle. Exposing it means the
            // track animation shows a real skid turn -- the outside track visibly
            // outrunning the inside one -- rather than two wheels turning together.
            // The RIGHT track is the outside of a right-hand turn, so it runs faster.
            // Positive yaw rate turns toward +X, which is the local right axis.
            float half = _playerCurrentSpeedMps * DifferentialSkew;
            PlayerRightTrackMps = _playerCurrentSpeedMps + half;
            PlayerLeftTrackMps = _playerCurrentSpeedMps - half;

            SlopeForwardDeg = slopeFwdDeg;
            SlopeLateralDeg = slopeLatDeg;
        }

        /// <summary>
        private void StepSuspension(float dt)
        {
            if (_playerSuspension == null)
            {
                float massKg = _playerDef != null ? Mathf.Max(_playerDef.MassKg, 1000f) : 25000f;

                // Footprint measured from the RENDERED vehicle, at the same scale
                // the bootstrap applies to the rig. Using the generic track
                // constants here instead put the contacts at +/-1.7 m on a 9 m
                // tank, so the rear third of the tracks drove on no physics at all.
                _playerSuspension = new HullSuspension(massKg,
                    TrackedVehicleDynamics.ContactHalfLengthM(massKg),
                    TrackedVehicleDynamics.ContactHalfWidthM(massKg),
                    TrackedVehicleDynamics.ContactCentreZM(massKg));
                _playerSuspension.Reset(PlayerPosition.x, PlayerPosition.z, PlayerHeadingRad);
            }

            _playerSuspension.Step(dt, PlayerPosition.x, PlayerPosition.z,
                PlayerHeadingRad, _playerAccelLong, _playerAccelLat);

            // The hull's Y now comes from its own ride height rather than from the
            // terrain height under the centre. Those differ whenever the hull is
            // tilted or bridging a dip, and taking the centre value alone was one of
            // the reasons it looked like it was skating over the ground.
            PlayerPosition = new Vector3(
                PlayerPosition.x,
                _playerSuspension.height,
                PlayerPosition.z);

            HullPitchRad = _playerSuspension.pitchRad;
            HullRollRad = _playerSuspension.rollRad;
            SlopeForwardDeg = Mathf.Asin(Mathf.Clamp(
                Mathf.Sin(_playerSuspension.pitchRad), -1f, 1f)) * Mathf.Rad2Deg;
            SlopeLateralDeg = Mathf.Asin(Mathf.Clamp(
                Mathf.Sin(_playerSuspension.rollRad), -1f, 1f)) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Turret follows the aim point, clamped to a realistic traverse arc and
        /// slewed at a finite rate.
        /// </summary>
        private void AimTurret(float dt, float trim, Vector3 aimPoint)
        {
            // Traverse about the turret ring, not the hull centre. A real turret
            // rotates around the ring it is mounted on, and aiming from the hull
            // centre makes the barrel appear to lag the cursor by the length of the
            // hull whenever the tank is sideways to the target.
            Vector3 turretPivot = PlayerPosition + TurretPivotOffset(PlayerHeadingRad);
            Vector3 delta = aimPoint - turretPivot;

            float horizontalDist = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);
            if (horizontalDist > 0.1f)
            {
                float dy = delta.y - 0.15f;
                float wantedPitch = Mathf.Atan2(dy, horizontalDist);
                wantedPitch = Mathf.Clamp(wantedPitch, MinGunPitchRad, MaxGunPitchRad);
                PlayerGunPitchRad = SlewBounded(PlayerGunPitchRad, wantedPitch, GunElevationSlewRate * dt);
                PlayerGunPitchRad = Mathf.Clamp(PlayerGunPitchRad, MinGunPitchRad, MaxGunPitchRad);
            }

            delta.y = 0f;

            if (delta.sqrMagnitude < 0.01f) return;

            float wanted = WrapAngle(Mathf.Atan2(delta.x, delta.z) - PlayerHeadingRad);
            wanted += trim;

            // Clamp AFTER the trim is added. Clamping before it meant the trim could
            // push the requested angle straight back out through the traverse limit,
            // which is how the turret ended up past MaxTraverse and swinging the wrong
            // way before it caught itself.
            wanted = Mathf.Clamp(wanted, -MaxTraverse, MaxTraverse);

            PlayerTurretYawRad = SlewBounded(PlayerTurretYawRad, wanted, TurretSlewRate * dt);

            // Belt and braces: the state is clamped too, so no combination of inputs
            // can ever leave the barrel outside its arc. An out-of-arc turret looks
            // exactly like an inverted one to the player, because the gun is pointing
            // at the opposite side of the tank from the target.
            PlayerTurretYawRad = Mathf.Clamp(PlayerTurretYawRad, -MaxTraverse, MaxTraverse);
        }

        /// <summary>
        /// Where the turret ring sits relative to the hull origin.
        ///
        /// Matches the rig built in <c>TankView</c>, which parents the turret at
        /// local z = -0.3, slightly behind the hull centre.
        /// </summary>
        private static Vector3 TurretPivotOffset(float headingRad)
        {
            const float behind = 0.3f;
            return new Vector3(-Mathf.Sin(headingRad) * behind, 0f, -Mathf.Cos(headingRad) * behind);
        }

        private void DriveEnemy(float dt)
        {
            // Orbit behaviour: close the distance, then circle. Deterministic,
            // driven only by the tick counter, so replays are identical.
            float phase = _tick * 0.02f;
            Vector3 toPlayer = PlayerPosition - EnemyPosition;

            // Horizontal distance only. Using the 3D magnitude made a target on a hill
            // read as kilometres away, so the AI would refuse to fire at or close on
            // anything that was not on the same contour.
            toPlayer.y = 0f;
            float dist = toPlayer.magnitude;

            float throttle = 0.45f;
            if (dist < 26f) throttle = 0.1f;

            float desiredHeading = dist > 0.1f
                ? Mathf.Atan2(toPlayer.x, toPlayer.z) + Mathf.Sin(phase) * 0.7f
                : EnemyHeadingRad;

            EnemyHeadingRad = Slew(EnemyHeadingRad, WrapAngle(desiredHeading - EnemyHeadingRad) + EnemyHeadingRad,
                1.2f * dt);

            // The AI drives through the same terrain-aware force model as the player.
            // It previously moved at a constant `throttle * MaxSpeedMps * 0.8` with no
            // inertia, no braking and no slope response at all, so it climbed hills at
            // full speed and could not be out-driven.
            Vector3 fwd3 = new Vector3(Mathf.Sin(EnemyHeadingRad), 0f, Mathf.Cos(EnemyHeadingRad));
            Vector2 fwd = new Vector2(fwd3.x, fwd3.z);
            Vector2 right = new Vector2(fwd3.z, -fwd3.x);

            float slopeFwdDeg, slopeLatDeg;
            TrackedVehicleDynamics.SampleSlopes(
                EnemyPosition.x, EnemyPosition.z, fwd, right,
                TrackedVehicleDynamics.HalfGaugeM, TrackedVehicleDynamics.HalfTrackWidthM,
                out slopeFwdDeg, out slopeLatDeg);

            float normalLoadG;
            Vector2 gravity = TrackedVehicleDynamics.GravityOnGroundPlane(
                fwd, right, slopeFwdDeg, slopeLatDeg, out normalLoadG);

            float aEngine = TrackedVehicleDynamics.EngineAcceleration(
                throttle, _enemySpeedMps, AccelerationMps2 * 0.8f, PowerLimitedSpeedMps, MaxSpeedMps);
            float aRoll = TrackedVehicleDynamics.RollingResistance(
                _enemySpeedMps, normalLoadG, RollingResistanceCoeff);

            float aLatGrip = TrackedVehicleDynamics.LateralGripAccel(
                _enemyLatSpeedMps, normalLoadG, LateralFrictionCoeff, LateralSlipStiffness);
            float aLong = TrackedVehicleDynamics.ApplyFrictionCircle(
                aEngine + gravity.x + aRoll, aLatGrip, TrackedGripAccelMps2);

            _enemySpeedMps += aLong * dt;
            _enemyLatSpeedMps += aLatGrip * dt;
            if (Mathf.Abs(_enemySpeedMps) < 1e-3f) _enemySpeedMps = 0f;
            if (Mathf.Abs(_enemyLatSpeedMps) < 1e-3f) _enemyLatSpeedMps = 0f;

            Vector2 velocity = fwd * _enemySpeedMps + right * _enemyLatSpeedMps;

            Vector2 from = new Vector2(EnemyPosition.x, EnemyPosition.z);
            Vector2 next = from + velocity * dt;
            Vector2 clamped = ClampToArena(next.x, next.y);

            // Solid obstacles. Terrain height and obstacle collision are different
            // problems: the suspension keeps the hull at the right height while the
            // vehicle still drives straight through a tree trunk.
            DidCollideWithObstacle = ResolveObstacles(ref clamped, VehicleCollisionRadiusM);
            float enemyY = BattlefieldBuilder.GetTerrainHeight(clamped.x, clamped.y);
            EnemyPosition = new Vector3(clamped.x, enemyY, clamped.y);
            Vector2 actual = clamped - from;
            float eInvDt = 1f / Mathf.Max(dt, 1e-5f);
            _enemySpeedMps = Vector2.Dot(actual, fwd) * eInvDt;
            _enemyLatSpeedMps = Vector2.Dot(actual, right) * eInvDt;

            float eHalf = _enemySpeedMps * DifferentialSkew * 0.5f;
            EnemyRightTrackMps = _enemySpeedMps + eHalf;
            EnemyLeftTrackMps = _enemySpeedMps - eHalf;

            // The AI turret slews at a finite rate like the player's, rather than
            // snapping, so leading a target on a turn is a real skill.
            float wantedYaw = WrapAngle(Mathf.Atan2(-toPlayer.x, -toPlayer.z) - EnemyHeadingRad);
            wantedYaw = Mathf.Clamp(wantedYaw, -MaxTraverse, MaxTraverse);
            EnemyTurretYawRad = SlewBounded(EnemyTurretYawRad, wantedYaw, TurretSlewRate * 0.7f * dt);
            EnemyTurretYawRad = Mathf.Clamp(EnemyTurretYawRad, -MaxTraverse, MaxTraverse);

            if (_enemyReload <= 0f && dist < 60f)
            {
                _enemyReload = _enemyDef.ReloadSeconds + 1.5f;
                SpawnShell(EnemyPosition + new Vector3(0f, 1.6f, 0f),
                    Vector3.Normalize(PlayerPosition + new Vector3(0f, 1.6f, 0f) - (EnemyPosition + new Vector3(0f, 1.6f, 0f))),
                    false, VehicleCatalog.BuildShell(_enemyDef));
            }
        }

        private void FirePlayer()
        {
            ReloadRemaining = _playerDef.ReloadSeconds;

            Vector3 origin = PlayerPosition + new Vector3(0f, MuzzleHeightM, 0f);
            float absoluteYaw = PlayerHeadingRad + PlayerTurretYawRad;
            float cosPitch = Mathf.Cos(PlayerGunPitchRad);
            float sinPitch = Mathf.Sin(PlayerGunPitchRad);

            // The barrel's world direction, including the hull's own pitch and roll.
            //
            // This used to be computed as if the hull were level, while the renderer
            // rotated the whole vehicle onto the terrain. On a 20 degree nose-up
            // slope the muzzle flash appeared 20 degrees above the tracer, so the
            // player watched the shell leave somewhere the gun was not pointing. The
            // aim solve and the visual tilt now share one attitude.
            Vector3 hullUp = new Vector3(0f, 1f, 0f);
            Quaternion hullTilt = Quaternion.Euler(-HullPitchRad * Mathf.Rad2Deg, 0f, HullRollRad * Mathf.Rad2Deg);
            Vector3 upTilted = hullTilt * hullUp;

            Vector3 levelDir = new Vector3(Mathf.Sin(absoluteYaw) * cosPitch, sinPitch, Mathf.Cos(absoluteYaw) * cosPitch);
            Vector3 direction = Quaternion.FromToRotation(Vector3.up, upTilted) * levelDir;
            direction = direction.normalized;

            // Cannon recoil impulse pushes the vehicle slightly backwards
            // Cannon recoil impulse pushes the vehicle slightly backwards. A real gun also
            // shoves the hull a little sideways as it settles on its trunnions, which
            // matters on a slope where the vehicle is already sliding.
            _playerCurrentSpeedMps = Mathf.Min(_playerCurrentSpeedMps, RecoilDeltaSpeedMps);

            SpawnShell(origin, direction, true, VehicleCatalog.BuildShell(_playerDef));
            OnPlayerFired?.Invoke();
        }

        private void SpawnShell(Vector3 origin, Vector3 direction, bool fromPlayer, Ballistics.Shell ordnance)
        {
            _inFlight.Add(new ActiveShell
            {
                Position = origin,
                Velocity = direction.normalized * ordnance.MuzzleVelocity * 0.42f,
                Life = 4f,
                FromPlayer = fromPlayer,
                Ordnance = ordnance
            });
        }

        private void AdvanceShells(float dt)
        {
            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                ActiveShell s = _inFlight[i];
                s.Life -= dt;
                Vector3 previous = s.Position;
                s.Position += s.Velocity * dt;

                Vector3 target = s.FromPlayer ? EnemyPosition : PlayerPosition;
                bool targetAlive = s.FromPlayer ? EnemyHealth > 0 : PlayerHealth > 0;

                // Hit volume is the hull footprint plus the turret, measured RELATIVE
                // to the target's own ground contact — see HitCeilingM. The test is
                // SWEPT along the segment travelled this tick: a round leaves the gun
                // at ~0.42 * 800 m/s = 336 m/s, which at the 30 Hz tick rate is
                // 11.2 m of travel against a cylinder only 6.4 m across. Sampling
                // the endpoint alone missed roughly 43% of rounds that flew straight
                // through the target. Must mirror segment_hits_target in
                // crates/war-core/src/gameplay/battle_sim.rs.
                bool hit = targetAlive && SegmentHitsTarget(previous, s.Position, target);

                if (hit)
                {
                    ResolveHit(s);
                    _inFlight.RemoveAt(i);
                    continue;
                }

                float rSq = s.Position.x * s.Position.x + s.Position.z * s.Position.z;
                float maxShellR = ArenaRadius + 60f;
                if (s.Life <= 0f || rSq > maxShellR * maxShellR)
                {
                    _inFlight.RemoveAt(i);
                    continue;
                }

                _inFlight[i] = s;
            }
        }

        /// <summary>
        /// Whether the segment travelled by a shell this tick passes through the
        /// target's hit volume: a vertical cylinder of <see cref="HitRadiusM"/> about
        /// the target's ground contact, spanning -<see cref="HitFloorM"/> to
        /// <see cref="HitCeilingM"/> relative to it.
        ///
        /// Solves for the closest approach of the SEGMENT rather than sampling its end
        /// point, so a round moving further than the cylinder is wide in one tick
        /// cannot pass through the target unseen.
        ///
        /// Must stay in step with <c>segment_hits_target</c> in
        /// crates/war-core/src/gameplay/battle_sim.rs — the parity harness compares
        /// the two implementations tick for tick.
        /// </summary>
        internal static bool SegmentHitsTarget(Vector3 from, Vector3 to, Vector3 target)
        {
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            float lenSq = dx * dx + dz * dz;

            // Parameter along the segment at which the horizontal distance to the
            // target is smallest. A shell fired straight up (lenSq ~ 0) keeps t at
            // its start value, which is the correct limit.
            float t = 0f;
            if (lenSq > 1e-12f)
            {
                float px = from.x - target.x;
                float pz = from.z - target.z;
                t = Mathf.Clamp01(-(px * dx + pz * dz) / lenSq);
            }

            float cx = from.x + dx * t;
            float cz = from.z + dz * t;
            float ox = cx - target.x;
            float oz = cz - target.z;
            if (ox * ox + oz * oz >= HitRadiusM * HitRadiusM)
            {
                return false;
            }

            // Height is interpolated at the same parameter, so a descending round is
            // judged where it actually crosses the target.
            float dy = (from.y + (to.y - from.y) * t) - target.y;
            return dy < HitCeilingM && dy > -HitFloorM;
        }

        /// <summary>
        /// Resolves a shell hit through the real armour model, so the outcome
        /// depends on calibre, velocity, plate slope and impact angle rather than
        /// a flat damage number.
        /// </summary>
        private void ResolveHit(in ActiveShell s)
        {
            VehicleCatalog.VehicleDefinition targetDef = s.FromPlayer ? _enemyDef : _playerDef;
            Vector3 targetPos = s.FromPlayer ? EnemyPosition : PlayerPosition;
            float targetHeading = s.FromPlayer ? EnemyHeadingRad : PlayerHeadingRad;

            Vector3 incoming = s.Velocity.normalized;
            Vector3 shotDir = -incoming; // Incident vector reversed, pointing toward shooter

            Vector3 targetForward = new Vector3(Mathf.Sin(targetHeading), 0f, Mathf.Cos(targetHeading)).normalized;
            Vector3 targetRight = new Vector3(targetForward.z, 0f, -targetForward.x);

            float forwardDot = Vector3.Dot(shotDir, targetForward);
            float rightDot = Vector3.Dot(shotDir, targetRight);

            string plateName;
            float plateMm;
            Vector3 plateNormal;

            if (Mathf.Abs(forwardDot) >= Mathf.Abs(rightDot))
            {
                if (forwardDot >= 0f)
                {
                    plateName = "front";
                    plateMm = targetDef.ArmorFrontMm;
                    plateNormal = targetDef.FrontSlopeDeg > 0f
                        ? Quaternion.AngleAxis(-targetDef.FrontSlopeDeg, targetRight) * targetForward
                        : targetForward;
                }
                else
                {
                    plateName = "rear";
                    plateMm = targetDef.ArmorRearMm;
                    plateNormal = -targetForward;
                }
            }
            else
            {
                plateName = "side";
                plateMm = targetDef.ArmorSideMm;
                plateNormal = rightDot >= 0f ? targetRight : -targetRight;
            }

            // Obliquity angle measured against the plate normal (0 deg = head-on, 90 deg = grazing)
            float impactAngleDeg = Mathf.Clamp(Vector3.Angle(shotDir, plateNormal), 0f, 89.9f);

            float travelled = new Vector3(s.Position.x - targetPos.x, 0f, s.Position.z - targetPos.z).magnitude;
            float penAtImpact = s.Ordnance.PenetrationAt(travelled, impactAngleDeg);

            // Cast hulls resist better than rolled plate of the same nominal
            // thickness. Panther and Tiger are cast; the T-34 is not.
            ArmourPlate plate = new ArmourPlate(plateName, plateMm, targetDef.IsCastHull);
            float effective = plate.EffectiveThickness(s.Ordnance, impactAngleDeg);

            if (impactAngleDeg >= s.Ordnance.CriticalRicochetDeg)
            {
                Push($"RICOCHET @ {impactAngleDeg:F0}deg - angle too steep " +
                    $"for {s.Ordnance.Type} (limit {s.Ordnance.CriticalRicochetDeg:F0}deg)");
                return;
            }

            bool penetrates = s.Ordnance.Type == ShellType.HEAT || penAtImpact >= effective;

            if (!penetrates)
            {
                Push($"BOUNCE - {penAtImpact:F0}mm vs {effective:F0}mm effective ({plateName})");
                return;
            }

            if (s.FromPlayer)
            {
                EnemyHealth = Mathf.Max(0, EnemyHealth - 34);
                Push($"PENETRATION - {penAtImpact:F0}mm vs {effective:F0}mm @ {impactAngleDeg:F0}deg");
                if (EnemyHealth <= 0)
                {
                    Kills++;
                    _respawnTimer = 2f;
                    Push($"DESTROYED - {targetDef.DisplayName}");
                    float rx = Randomize(45f);
                    float rz = Randomize(45f);
                    EnemyPosition = new Vector3(rx, BattlefieldBuilder.GetTerrainHeight(rx, rz), rz);
                }
            }
            else
            {
                PlayerHealth = Mathf.Max(0, PlayerHealth - 26);
                Push($"HIT TAKEN - penetrated {plateName} @ {impactAngleDeg:F0}deg");
                if (PlayerHealth <= 0)
                {
                    Deaths++;
                    _respawnTimer = 2f;
                    Push("KNOCKED OUT - respawning");
                }
            }
        }

        /// <summary>
        /// Offsets a respawn without UnityEngine.Random, which is not safe to use
        /// in simulation code that may need to replay identically.
        /// </summary>
        private static float Randomize(float limit)
        {
            float n = Mathf.Sin(_respawnCounter++ * 12.9898f) * 43758.5453f;
            n -= Mathf.Floor(n);
            return (n * 2f - 1f) * limit;
        }

        private static int _respawnCounter;

        private void Push(string message)
        {
            Feed.Insert(0, message);
            if (Feed.Count > 4) Feed.RemoveAt(Feed.Count - 1);
        }

        private static float WrapAngle(float a)
        {
            while (a > Mathf.PI) a -= Mathf.PI * 2f;
            while (a < -Mathf.PI) a += Mathf.PI * 2f;
            return a;
        }

        private static float Slew(float current, float target, float maxStep)
        {
            float delta = WrapAngle(target - current);
            if (Mathf.Abs(delta) <= maxStep) return WrapAngle(target);
            return WrapAngle(current + Mathf.Sign(delta) * maxStep);
        }

        /// <summary>
        /// Slew toward a target confined to the same bounded interval, WITHOUT wrapping.
        ///
        /// <see cref="Slew"/> takes the short way round an unbounded circle and is only
        /// correct for a genuinely unbounded quantity — the AI's hull heading. For a
        /// quantity confined to an arc it deadlocks: the two traverse stops are 2 *
        /// MaxTraverse = 4.2 rad apart, wider than pi, so a demand on the far side
        /// wraps *through the back* of the arc and the clamp pins the turret at its
        /// stop forever.
        ///
        /// Must stay in step with `slew_bounded` in
        /// crates/war-core/src/gameplay/battle_sim.rs.
        /// </summary>
        private static float SlewBounded(float current, float target, float maxStep)
        {
            float delta = target - current;
            if (Mathf.Abs(delta) <= maxStep) return target;
            return current + Mathf.Sign(delta) * maxStep;
        }

        /// <summary>
        /// Whether the player's current aim would penetrate the enemy where it
        /// stands. Drives the on-screen reticle colour, so the player can see that
        /// a sloped front plate is refusing the shot before pulling the trigger.
        /// </summary>
        public bool AimWouldPenetrate(Vector3 aimPoint)
        {
            Vector3 toPlayer = PlayerPosition - EnemyPosition;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.01f) return true;
            Vector3 shotDir = toPlayer.normalized; // Points from enemy back to player

            Vector3 targetForward = new Vector3(Mathf.Sin(EnemyHeadingRad), 0f, Mathf.Cos(EnemyHeadingRad)).normalized;
            Vector3 targetRight = new Vector3(targetForward.z, 0f, -targetForward.x);

            float forwardDot = Vector3.Dot(shotDir, targetForward);
            float rightDot = Vector3.Dot(shotDir, targetRight);

            string plateName;
            float plateMm;
            Vector3 plateNormal;

            if (Mathf.Abs(forwardDot) >= Mathf.Abs(rightDot))
            {
                if (forwardDot >= 0f)
                {
                    plateName = "front";
                    plateMm = _enemyDef.ArmorFrontMm;
                    plateNormal = _enemyDef.FrontSlopeDeg > 0f
                        ? Quaternion.AngleAxis(-_enemyDef.FrontSlopeDeg, targetRight) * targetForward
                        : targetForward;
                }
                else
                {
                    plateName = "rear";
                    plateMm = _enemyDef.ArmorRearMm;
                    plateNormal = -targetForward;
                }
            }
            else
            {
                plateName = "side";
                plateMm = _enemyDef.ArmorSideMm;
                plateNormal = rightDot >= 0f ? targetRight : -targetRight;
            }

            float impactAngleDeg = Mathf.Clamp(Vector3.Angle(shotDir, plateNormal), 0f, 89.9f);
            Shell ordnance = VehicleCatalog.BuildShell(_playerDef);

            // Per-shell ricochet limit, matching what ResolveHit will actually do.
            if (impactAngleDeg >= ordnance.CriticalRicochetDeg) return false;

            ArmourPlate plate = new ArmourPlate(plateName, plateMm, _enemyDef.IsCastHull);
            float travelled = toPlayer.magnitude;
            float penAtImpact = ordnance.PenetrationAt(travelled, impactAngleDeg);
            float effective = plate.EffectiveThickness(ordnance, impactAngleDeg);

            ImpactReport report = Penetration.Resolve(ordnance, plate, impactAngleDeg);
            return report.Result == ImpactResult.Penetration
                || report.Result == ImpactResult.OverPenetration;
        }
    }
}
