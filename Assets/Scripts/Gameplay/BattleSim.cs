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

/// <summary>
        /// The player's PhysX vehicle. Assigned by the bootstrap; the simulation reads
        /// its authoritative transform back each step and never writes a position.
        /// </summary>
        public TrackedVehicle PlayerVehicle;

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
        /// Plain <c>Lerp</c>, not <c>LerpAngle</c>: the latter is DEGREES-based (it wraps
        /// with Mathf.Repeat(delta, 360)) and these values are radians. It happens to give the
        /// same answer while the delta stays far below 360, which it always will inside the
        /// +/-40 degree clamp, but it is the wrong function for the units.
        /// </remarks>
        public float PlayerRenderHullPitchRad =>
            Mathf.Lerp(HullPitchPrevRad, HullPitchRad, InterpolationAlpha);

        /// <inheritdoc cref="PlayerRenderHullPitchRad"/>
        public float PlayerRenderHullRollRad =>
            Mathf.Lerp(HullRollPrevRad, HullRollRad, InterpolationAlpha);

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

                    // Zero the filtered driver axes on respawn.
                    //
                    // While the player is out, the shaping block below is skipped
                    // entirely, so whatever throttle was last commanded is still held
                    // in _shapedThrottle when control returns. With the throttle key
                    // no longer pressed the ramp decays it to zero over a third of a
                    // second, so the vehicle would lurch forward on respawn for no
                    // reason the player could see or stop.
                    _shapedThrottle = 0f;
                    _shapedSteer = 0f;
                }
            }

            if (PlayerHealth > 0)
            {
                // Shape the driver axes before anything consumes them.
                //
                // This sits ahead of the movement branch so that the PhysX path and
                // the kinematic path receive the SAME filtered command. Shaping them
                // separately -- or only on one branch -- would make the two movement
                // modes disagree about what the driver asked for, which is exactly the
                // kind of divergence the C#-vs-Rust parity tests exist to prevent.
                _shapedThrottle = TankInputShaping.Ramp(
                    _shapedThrottle, throttle, dt, ThrottleAttackPerSec, ThrottleReleasePerSec);

                // The steer time constant widens when the driver is reversing across
                // neutral, so the hull carries its old rotation less willingly into
                // the new direction.
                float steerTau = TankInputShaping.SteerBlend(
                    _shapedSteer, steer, SteerLerpSeconds, DirectionChangeSeconds);
                _shapedSteer = TankInputShaping.LerpAxis(_shapedSteer, steer, dt, steerTau);

                float throttleIn = _shapedThrottle;
                float steerIn = _shapedSteer;

                // Player movement is ALWAYS PhysX now. The previous analytic traction
                // integrator is gone; the vehicle's position, heading and speed are
                // read back from the Rigidbody after Physics.Simulate, which is the
                // arrangement both reference applications use.
                DrivePlayer(dt, throttleIn, steerIn);

                AimTurret(dt, turretTrim, aimPoint);

                if (fire && ReloadComplete) FirePlayer();
            }

            if (EnemyHealth > 0)
            {
                DriveEnemy(dt);
            }

            AdvanceShells(dt);
        }

        /// <summary>
        /// Drive the player vehicle through PhysX and read the authoritative state back.
        /// </summary>
        /// <remarks>
        /// PhysX owns the transform. This applies the drivetrain forces, steps the
        /// simulation, and then READS the result -- it never writes a position. That is
        /// what makes it impossible for the renderer and the simulation to disagree
        /// about where the vehicle is, which was the root of the hull sinking into
        /// terrain: the previous model integrated its own height and the view displayed
        /// that height, so every integrator error became a visible placement error.
        ///
        /// Explicit <c>Physics.Simulate</c> rather than Unity's FixedUpdate, because the
        /// surrounding simulation runs on a fixed 30 Hz accumulator and having physics
        /// on a different clock than the ballistics would be a worse desync than the one
        /// this replaces.
        /// </remarks>
        private void DrivePlayer(float dt, float throttle, float steer)
        {
            TrackedVehicle vehicle = PlayerVehicle;
            if (vehicle == null || vehicle.Body == null || vehicle.Body.isKinematic)
            {
                return;
            }

            // The hull steers onto the gun's line while driving, same as before. The
            // gun tracks the mouse in world space while the hull keeps whatever heading
            // the driver chose, so without this the turret visibly swings opposite to
            // the tank on every turn. The angle is scaled into the same normalised
            // domain as the steering input.
            float moving = Mathf.Abs(throttle) > 0.01f ? 1f : 0f;
            float gunWorldHeading = WrapAngle(PlayerHeadingRad + PlayerTurretYawRad);
            float toGunLine = WrapAngle(gunWorldHeading - PlayerHeadingRad);
            float demand = Mathf.Clamp(
                (toGunLine / MaxTraverse * HullAutoAlignStrength * moving) + steer,
                -1f, 1f);

            vehicle.ApplyForces(throttle, demand);

            Physics.simulationMode = SimulationMode.Script;
            Physics.Simulate(dt);

            Vector3 pos = vehicle.Body.position;
            Vector2 clamped = ClampToArena(pos.x, pos.z);
            if (!Mathf.Approximately(clamped.x, pos.x) || !Mathf.Approximately(clamped.y, pos.z))
            {
                pos.x = clamped.x;
                pos.z = clamped.y;
                vehicle.Body.position = pos;
            }

            PlayerPosition = pos;
            PlayerHeadingRad = WrapAngle(vehicle.Body.rotation.eulerAngles.y * Mathf.Deg2Rad);
            PlayerSpeedKph = vehicle.Body.linearVelocity.magnitude * 3.6f;

            // Track speeds come from the wheel colliders so the track animation reflects
            // what PhysX actually resolved rather than a re-derivation that could
            // disagree with it.
            UpdatePlayerTrackSpeeds(vehicle);
        }

        /// <summary>
        /// Average forward speed of each side's wheels, for the track animation.
        /// </summary>
        private void UpdatePlayerTrackSpeeds(TrackedVehicle vehicle)
        {
            WheelCollider[] wheels = vehicle.Wheels;
            if (wheels == null || wheels.Length < 2) return;

            int perSide = wheels.Length / 2;

            // Track speed is the vehicle's own forward velocity plus the per-side
            // differential the drivetrain is asking for.
            //
            // WheelHit.forwardSpeed does not exist in this Unity version -- the
            // available fields are forwardSlip, sidewaysSlip and rotationSpeed, and
            // WheelCollider.rigidbody is gone as well, so there is no per-wheel
            // velocity to read. Deriving it from the body velocity keeps the two track
            // halves consistent with how the vehicle actually moves, and the
            // differential reproduces the differential look the skid steer produces.
            float fwd = Vector3.Dot(vehicle.Body.linearVelocity, vehicle.transform.forward);
            float half = Mathf.Clamp(vehicle.LastSteerDemand, -1f, 1f) * vehicle.differentialSkew * fwd * 0.5f;

            PlayerLeftTrackMps = fwd - half;
            PlayerRightTrackMps = fwd + half;
        }

        private float _playerCurrentSpeedMps;

        /// <summary>
        /// Sideways velocity in the hull's frame, m/s. Non-zero only when the
        /// tracks are sliding — over a side slope, or while skidding out of a turn.
        /// This is what makes a vehicle on a camber slide toward the fall line
        /// instead of driving wherever it happens to be pointed.
        /// </summary>
        private float _playerLatSpeedMps;

        // --------------------------------------------------------- input shaping
        //
        // The filtered driver axes, as opposed to the raw keyboard values handed to
        // Advance(). See TankInputShaping for why this layer exists at all: it models
        // the driver and driveline, not the tank. Everything downstream of it --
        // traction, the friction circle, gradeability, the neutral turn -- is unchanged
        // physics.
        //
        // Held as state so they persist across ticks. Advance() is called once per
        // render frame but steps up to MaxStepsPerFrame times, so shaping inside
        // DrivePlayer would advance the ramp at the render rate rather than the tick
        // rate and the two would disagree whenever the frame rate dipped.

        /// <summary>
        /// Filtered throttle actually handed to the traction model, -1..1.
        /// </summary>
        private float _shapedThrottle;

        /// <summary>
        /// Filtered steering actually handed to the differential, -1..1.
        /// </summary>
        private float _shapedSteer;

        /// <summary>Throttle build rate while the command magnitude is growing.</summary>
        private const float ThrottleAttackPerSec = TankInputShaping.DefaultThrottleAttackPerSec;

        /// <summary>Throttle decay rate on release, deliberately slower than the build.</summary>
        private const float ThrottleReleasePerSec = TankInputShaping.DefaultThrottleReleasePerSec;

        /// <summary>Steering filter time constant, seconds.</summary>
        private const float SteerLerpSeconds = TankInputShaping.DefaultSteerLerpSeconds;

        /// <summary>Slower constant used while the driver is traversing through neutral.</summary>
        private const float DirectionChangeSeconds = TankInputShaping.DefaultDirectionChangeSeconds;

        /// <summary>
        /// Per-contact hull suspension. Owns pitch, roll and ride height as real
        /// degrees of freedom driven by four spring-dampers, so weight transfer and
        /// cross-slope settling are emergent rather than cosmetic.
        /// </summary>`n
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


        /// <summary>
        /// Read the hull's attitude from the authoritative PhysX transform.
        /// </summary>
        /// <remarks>
        /// The old analytic model computed pitch, roll and a ride height here by
        /// integrating its own spring state, and then WROTE those into
        /// <see cref="PlayerPosition"/>. That is precisely why the hull could sink into
        /// terrain: the renderer drew an integrated value, so any error in the
        /// integrator appeared directly as a placement error, and nothing in the engine
        /// could correct it.
        ///
        /// Now the attitude is simply read off the transform PhysX resolved. The
        /// vehicle's own suspension is the authority on where it is and how it is
        /// tilted, exactly as in both reference applications.
        /// </remarks>
        private void ReadHullAttitude()
        {
            Vector3 e = transform_euler(vehicle_rotation);
            HullPitchRad = e.x * Mathf.Deg2Rad;
            HullRollRad = e.z * Mathf.Deg2Rad;

            // Slope is reported in the hull's own frame so the HUD and the drive
            // logic keep working, derived from the attitude rather than from a second
            // independent terrain sample that could disagree with the suspension's.
            SlopeForwardDeg = -Mathf.Asin(Mathf.Clamp(Mathf.Sin(HullPitchRad), -1f, 1f)) * Mathf.Rad2Deg;
            SlopeLateralDeg = -Mathf.Asin(Mathf.Clamp(Mathf.Sin(HullRollRad), -1f, 1f)) * Mathf.Rad2Deg;
        }

        private Quaternion vehicle_rotation =>
            PlayerVehicle != null && PlayerVehicle.Body != null
                ? PlayerVehicle.Body.rotation
                : Quaternion.identity;

        private Vector3 transform_euler(Quaternion q) => q.eulerAngles;

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

            // The AI drives the same PhysX model as the player. It used to run a
            // parallel analytic force integration, which meant the enemy obeyed
            // terrain forces the player's vehicle no longer used -- two different
            // physics for the same class of object, and the AI could do things on a
            // slope the player could not.
            //
            // Speed and heading are kept as state here because the AI has no
            // Rigidbody of its own; only the player is a full PhysX vehicle.
            _enemySpeedMps = Mathf.MoveTowards(
                _enemySpeedMps,
                throttle * MaxSpeedMps,
                (Mathf.Abs(throttle) > 0.01f ? AccelerationMps2 : CoastDecelMps2) * dt);

            Vector2 from = new Vector2(EnemyPosition.x, EnemyPosition.z);
            Vector2 fwd = new Vector2(Mathf.Sin(EnemyHeadingRad), Mathf.Cos(EnemyHeadingRad));
            Vector2 next = from + fwd * _enemySpeedMps * dt;
            Vector2 clamped = ClampToArena(next.x, next.y);

            // Solid obstacles. Terrain height and obstacle collision are different
            // problems: the suspension keeps the hull at the right height while the
            // vehicle still drives straight through a tree trunk.
            DidCollideWithObstacle = ResolveObstacles(ref clamped, VehicleCollisionRadiusM);
            float enemyY = BattlefieldBuilder.GetTerrainHeight(clamped.x, clamped.y);
            EnemyPosition = new Vector3(clamped.x, enemyY, clamped.y);
            Vector2 actual = clamped - from;
            _enemySpeedMps = Vector2.Dot(actual, fwd) / Mathf.Max(dt, 1e-5f);

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
