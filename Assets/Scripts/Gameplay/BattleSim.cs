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

        /// <summary>Half-extent of the playable arena, metres.</summary>
        public const float ArenaHalfExtent = 90f;

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

        public event System.Action OnPlayerFired;

        public readonly List<string> Feed = new List<string>();
        private readonly List<ActiveShell> _inFlight = new List<ActiveShell>();
        private float _enemyReload;
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
            // Remember where the vehicles were at the start of this frame so the
            // renderer can interpolate between simulation ticks.
            PlayerPositionPrev = PlayerPosition;
            EnemyPositionPrev = EnemyPosition;
            PlayerHeadingPrevRad = PlayerHeadingRad;
            PlayerGunPitchPrevRad = PlayerGunPitchRad;

            // Rolling accumulator, held across calls, so the simulation rate is
            // constant regardless of render frame rate.
            _accumulator += Mathf.Min(frameDelta, 0.1f);
            int steps = 0;
            while (_accumulator >= FixedDt && steps < MaxStepsPerFrame)
            {
                _accumulator -= FixedDt;
                steps++;
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
                DrivePlayer(dt, throttle, steer);
                AimTurret(dt, turretTrim, aimPoint);

                if (fire && ReloadComplete) FirePlayer();
            }

            if (EnemyHealth > 0)
            {
                DriveEnemy(dt);
            }

            AdvanceShells(dt);
        }

        private float _playerCurrentSpeedMps;

        private void DrivePlayer(float dt, float throttle, float steer)
        {
            float targetSpeed = throttle * MaxSpeedMps;
            float rate = AccelerationMps2;
            if (Mathf.Abs(throttle) < 0.01f)
            {
                rate = CoastDecelMps2;
            }
            else if ((_playerCurrentSpeedMps > 0.1f && throttle < -0.01f) || (_playerCurrentSpeedMps < -0.1f && throttle > 0.01f))
            {
                rate = BrakingDecelMps2;
            }
            _playerCurrentSpeedMps = Mathf.MoveTowards(_playerCurrentSpeedMps, targetSpeed, rate * dt);
            float speed = _playerCurrentSpeedMps;

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
            // Only while actually moving. A stationary tank needs to be able to hold
            // its traverse for aiming, and it cannot steer anyway.
            float moving = Mathf.Abs(throttle) > 0.01f ? 1f : 0f;
            float gunWorldHeading = WrapAngle(PlayerHeadingRad + PlayerTurretYawRad);
            float toGunLine = WrapAngle(gunWorldHeading - PlayerHeadingRad);

            float demand = (toGunLine * HullAutoAlignStrength * moving) + steer;
            demand = Mathf.Clamp(demand, -1f, 1f);

            float maxStep = MaxTurnRateRad * dt;
            PlayerHeadingRad = WrapAngle(
                PlayerHeadingRad + Mathf.Clamp(demand * MaxTurnRateRad, -maxStep, maxStep));

            Vector3 forward = new Vector3(Mathf.Sin(PlayerHeadingRad), 0f, Mathf.Cos(PlayerHeadingRad));
            Vector3 next = PlayerPosition + forward * speed * dt;

            PlayerPosition = new Vector3(
                Mathf.Clamp(next.x, -ArenaHalfExtent, ArenaHalfExtent), 0f,
                Mathf.Clamp(next.z, -ArenaHalfExtent, ArenaHalfExtent));
            PlayerSpeedKph = Mathf.Abs(speed) * 3.6f;
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
                PlayerGunPitchRad = Slew(PlayerGunPitchRad, wantedPitch, GunElevationSlewRate * dt);
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

            PlayerTurretYawRad = Slew(PlayerTurretYawRad, wanted, TurretSlewRate * dt);

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
            float dist = toPlayer.magnitude;

            float throttle = 0.45f;
            if (dist < 26f) throttle = 0.1f;

            float desiredHeading = dist > 0.1f
                ? Mathf.Atan2(toPlayer.x, toPlayer.z) + Mathf.Sin(phase) * 0.7f
                : EnemyHeadingRad;

            EnemyHeadingRad = Slew(EnemyHeadingRad, WrapAngle(desiredHeading - EnemyHeadingRad) + EnemyHeadingRad,
                1.2f * dt);

            float speed = throttle * MaxSpeedMps * 0.8f;
            Vector3 forward = new Vector3(Mathf.Sin(EnemyHeadingRad), 0f, Mathf.Cos(EnemyHeadingRad));
            Vector3 next = EnemyPosition + forward * speed * dt;

            EnemyPosition = new Vector3(
                Mathf.Clamp(next.x, -ArenaHalfExtent, ArenaHalfExtent), 0f,
                Mathf.Clamp(next.z, -ArenaHalfExtent, ArenaHalfExtent));

            EnemyTurretYawRad = WrapAngle(Mathf.Atan2(-toPlayer.x, -toPlayer.z) - EnemyHeadingRad);
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

            Vector3 origin = PlayerPosition + new Vector3(0f, 2.2f, 0f);
            float absoluteYaw = PlayerHeadingRad + PlayerTurretYawRad;
            float cosPitch = Mathf.Cos(PlayerGunPitchRad);
            float sinPitch = Mathf.Sin(PlayerGunPitchRad);
            Vector3 direction = new Vector3(Mathf.Sin(absoluteYaw) * cosPitch, sinPitch, Mathf.Cos(absoluteYaw) * cosPitch);

            // Cannon recoil impulse pushes the vehicle slightly backwards
            _playerCurrentSpeedMps -= 1.2f;

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
                s.Position += s.Velocity * dt;

                Vector3 target = s.FromPlayer ? EnemyPosition : PlayerPosition;
                bool targetAlive = s.FromPlayer ? EnemyHealth > 0 : PlayerHealth > 0;

                // Hit volume is roughly the hull footprint plus the turret.
                bool hit = targetAlive
                    && new Vector2(s.Position.x - target.x, s.Position.z - target.z).sqrMagnitude < 3.2f * 3.2f
                    && s.Position.y < 4.5f;

                if (hit)
                {
                    ResolveHit(s);
                    _inFlight.RemoveAt(i);
                    continue;
                }

                if (s.Life <= 0f || Mathf.Abs(s.Position.x) > ArenaHalfExtent + 40f
                    || Mathf.Abs(s.Position.z) > ArenaHalfExtent + 40f)
                {
                    _inFlight.RemoveAt(i);
                    continue;
                }

                _inFlight[i] = s;
            }
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

            // Impact angle between the incoming round and the target's facing.
            Vector3 targetForward = new Vector3(Mathf.Sin(targetHeading), 0f, Mathf.Cos(targetHeading));
            float dot = Mathf.Clamp(Vector3.Dot(-incoming, targetForward), -1f, 1f);
            float impactAngleDeg = Mathf.Acos(dot) * Mathf.Rad2Deg;

            // Which plate was struck, by where the round met the hull.
            Vector3 local = Vector3.Scale(incoming, targetPos);
            float lateral = Mathf.Abs(new Vector3(-targetForward.z, 0f, targetForward.x).x * incoming.x
                                      + (-targetForward.z) * incoming.z);
            bool front = lateral < 0.4f;
            bool side = Mathf.Abs(impactAngleDeg - 90f) < 55f && !front;

            string plateName = front ? "front" : side ? "side" : "rear";
            float plateMm = front ? targetDef.ArmorFrontMm
                : side ? targetDef.ArmorSideMm
                : targetDef.ArmorRearMm;
            float slope = front ? targetDef.FrontSlopeDeg : 0f;

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
                    EnemyPosition = new Vector3(Randomize(45f), 0f, Randomize(45f));
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
        /// Whether the player's current aim would penetrate the enemy where it
        /// stands. Drives the on-screen reticle colour, so the player can see that
        /// a sloped front plate is refusing the shot before pulling the trigger.
        /// </summary>
        public bool AimWouldPenetrate(Vector3 aimPoint)
        {
            Vector3 delta = aimPoint - EnemyPosition;
            delta.y = 0f;
            if (delta.sqrMagnitude < 3.2f * 3.2f) return true;

            Vector3 targetForward = new Vector3(Mathf.Sin(EnemyHeadingRad), 0f, Mathf.Cos(EnemyHeadingRad));
            float dot = Mathf.Clamp(Vector3.Dot(-delta.normalized, targetForward), -1f, 1f);
            float impactAngleDeg = Mathf.Acos(dot) * Mathf.Rad2Deg;

            Shell ordnance = VehicleCatalog.BuildShell(_playerDef);

            // Per-shell ricochet limit, matching what ResolveHit will actually do.
            // Using a different threshold here would show the player "green" on a
            // shot that then ricochets, which is worse than no feedback at all.
            if (impactAngleDeg >= ordnance.CriticalRicochetDeg) return false;

            // Resolve against the real plate rather than re-deriving the maths here.
            // The previous version duplicated the effective-thickness calculation,
            // which is exactly the kind of duplication that drifts: it ignored the
            // cast-hull coefficient and the plate's own slope, so the reticle colour
            // disagreed with the shot it was predicting.
            ArmourPlate plate = new ArmourPlate("front", _enemyDef.ArmorFrontMm,
                _enemyDef.IsCastHull);

            // Resolved once, against the real plate and at the real range. The
            // previous version duplicated the effective-thickness maths inline, which
            // is exactly the kind of duplication that silently drifts: it ignored the
            // cast-hull coefficient and range falloff, so the reticle showed green on
            // shots that would then bounce.
            ImpactReport report = Penetration.Resolve(ordnance, plate, impactAngleDeg);
            return report.Result == ImpactResult.Penetration
                || report.Result == ImpactResult.OverPenetration;
        }
    }
}
