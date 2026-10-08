using UnityEngine;
using WarArena.Air;

namespace WarArena.Air
{
    /// <summary>
    /// Six-degree-of-freedom flight model: forces and moments, not a speed
    /// controller.
    ///
    /// The model is deliberately simple but physically grounded. It does not cheat
    /// with "if input up, go faster", because that produces aircraft that turn
    /// like rockets and climb like nothing, which is exactly the failure mode that
    /// makes browser flight games feel wrong. Instead it integrates lift, induced
    /// drag, parasitic drag, thrust and gravity, so a plane's behaviour emerges from
    /// its wing area and mass.
    ///
    /// Consequence worth knowing: a light fighter with a small wing area turns
    /// faster than a heavy one, and a jet with poor lift-to-drag cannot out-turn a
    /// propeller even at twice the speed. That is how the Me 262 ends up fast and
    /// the Meteor ends up better.
    ///
    /// Units are SI throughout: metres, seconds, kilograms, newtons, radians.
    /// </summary>
    public class FlightModel
    {
        private const float AirDensitySeaLevel = 1.225f;

        /// <summary>Scale height of the standard atmosphere, metres.</summary>
        private const float ScaleHeight = 8500f;

        /// <summary>Gravity, m/s2.</summary>
        public const float Gravity = 9.81f;

        /// <summary>Speed of sound at sea level, m/s. Sets the Mach number.</summary>
        private const float SpeedOfSound = 340f;

        /// <summary>Drag rise starts here. Below it, compressibility is negligible.</summary>
        private const float TransonicMach = 0.85f;

        /// <summary>How steeply drag climbs past the transonic threshold.</summary>
        private const float CompressibilityDragRise = 3.2f;

        /// <summary>
        /// Peak lift coefficient, reached just before the stall. Thin-aerofoil theory
        /// over-predicts past about 12 degrees, so the curve is capped here at a
        /// believable CLmax of roughly 1.3 for a clean WWII wing.
        /// </summary>
        private const float MaxLiftCoefficient = 1.3f;

        /// <summary>
        /// Oswald span efficiency. Around 0.8 is typical for a fighter wing; it
        /// converts aspect ratio into induced drag.
        /// </summary>
        private const float OswaldEfficiency = 0.80f;

        /// <summary>Angle of attack at which the wing stalls, degrees.</summary>
        private const float StallAngleDeg = 16f;

        /// <summary>
        /// Ceiling on the velocity change a single integration step may apply.
        ///
        /// Guards the explicit integrator rather than the aircraft. At the 120 Hz
        /// step this simulation runs at, no real manoeuvre changes velocity by more
        /// than a couple of m/s per step, so a limit of 8 m/s never engages in normal
        /// flight and only catches the numerically divergent cases.
        /// </summary>
        private const float MaxVelocityChangePerStepMs = 8f;

        /// <summary>Below this altitude the wing stalls. Real, and gameplay-relevant.</summary>
        public const float StallSpeedBias = 0.62f;

        /// <summary>
        /// How hard the tailplane pulls the nose back toward the trimmed angle of
        /// attack, in degrees of pitch per second per degree of error.
        /// </summary>
        private const float PitchStabilityGain = 3.2f;

        /// <summary>
        /// Pitch rate damping. Real aircraft have this because the tailplane and
        /// elevator both resist being moved at speed; without it the stability term
        /// behaves as an undamped spring and the nose oscillates indefinitely.
        /// </summary>
        private const float PitchDampingGain = 0.35f;

        /// <summary>
        /// Nose response to flight-path angle, degrees per second per degree of climb
        /// or dive.
        ///
        /// Deliberately unscaled by airspeed. This term is what holds altitude, and
        /// an altitude hold should work at any speed including a stall, so scaling it
        /// by speedFactor lets it fade out precisely when the aircraft needs it most
        /// and leaves a slow permanent sink in its place.
        /// </summary>
        private const float FlightPathDampingGain = 3.0f;

        /// <summary>
        /// Nose response to surplus thrust, degrees per second per newton.
        ///
        /// Small because thrust is measured in thousands of newtons. At 0.001, an
        /// aircraft with 1000 N to spare raises its nose one degree per second, which
        /// is a gentle settle. This term is what lets surplus thrust become altitude
        /// instead of raw speed, so it must stay positive-biased: more power means
        /// climbing away, which is both correct and the whole point of an excess
        /// power figure.
        /// </summary>
        private const float SurplusThrustPitchGain = 0.001f;

        /// <summary>
        /// Nose response to speed error, degrees per second per m/s. Weak relative to
        /// the surplus-thrust term: it is only the fine trim that stops the climb, not
        /// the thing that sets the attitude.
        /// </summary>
        /// <summary>
        /// Degrees of commanded flight-path change per m/s of speed error.
        ///
        /// Small, so it biases the target path gently rather than overriding it. At
        /// 0.03, an aircraft 60 m/s below its trim speed asks for a 1.8 degree nose-down
        /// path, enough to accelerate without diving into the ground.
        /// </summary>
        private const float SpeedToPathGain = 0.03f;

        /// <summary>
        /// Wing loading the surplus-thrust gain is calibrated against, N/m2. Roughly
        /// that of a 1944 fighter, so the gain is unity for such an aircraft and is
        /// scaled up for lighter, more agile types.
        /// </summary>
        private const float ReferenceWingLoading = 2800f;

        /// <summary>
        /// Largest climb angle the trim will ask for, in degrees, as a cap on the
        /// surplus-thrust pitch demand.
        ///
        /// Around 6-8 degrees is a brisk sustained climb. Anything steeper trades so
        /// much speed for altitude that the aircraft ends up slower than it started
        /// and below its own best climb speed, which is a losing trade rather than a
        /// climb.
        /// </summary>
        private const float MaxTrimClimbAngleDeg = 4f;

        /// <summary>
        /// Steepest shallow descent the trim will command, degrees. Shallower than the
        /// climb limit because a dive is easy to sustain and, unlike a climb, costs
        /// nothing: the aircraft trades altitude for speed and arrives faster.
        /// </summary>
        private const float MaxTrimDiveAngleDeg = 8f;



        

        /// <summary>
        /// Cruise angle of attack the aircraft trims itself toward, in degrees.
        ///
        /// Real fighters cruise nose-high at roughly 3-5 degrees because that is the
        /// angle at which lift equals weight most efficiently at flight speeds. Hard
        /// input has to be able to leave this value, otherwise the aircraft would
        /// simply refuse to climb, so the gain is scaled by the elevator's authority.
        /// </summary>
        private const float CruiseAngleOfAttackDeg = 3.5f;

        /// <summary>
        /// The angle of attack at which lift exactly balances weight for the current
        /// speed and dynamic pressure.
        ///
        /// This is the real trimmed condition, and deriving it is what lets the same
        /// wing-area and mass numbers produce a slow-turning A6M and a fast-turning
        /// Bf 109 without hand-tuned per-aircraft fudge factors.
        /// </summary>
        private float TrimAngleOfAttackFor(float dynamicPressure, float wingArea)
        {
            // Lift needed to hold the aircraft up equals its weight.
            float weight = _spec.MassKg * Gravity;
            float requiredCoefficient = weight / Mathf.Max(1e-3f, dynamicPressure * wingArea);

            // Invert CL = 2*pi*alpha (radians). If the wing genuinely cannot
            // generate this much lift at this speed, the aircraft is below its stall
            // speed: clamp rather than letting the trim chase an impossible value.
            float radians = requiredCoefficient / (2f * Mathf.PI);

            // The ceiling on the trim target must sit below the stall angle. Letting
            // it reach or exceed the stall means the trim system actively commands
            // the wing to stall whenever an aircraft slows down, which turns a routine
            // deceleration into an unrecoverable one. The Me 262 stalled itself to
            // destruction on its trim target alone at 3000 m.
            //
            // This is also the real aerodynamic limit: an aircraft below its stall
            // speed is not trimmed anywhere, it is falling, and no amount of elevator
            // will make that wing carry the weight.
            float maxTrimAngle = StallAngleDeg - 3f;
            return Mathf.Min(radians * Mathf.Rad2Deg, maxTrimAngle);
        }

        /// <summary>
        /// Maximum thrust available right now, in newtons.
        ///
        /// The two engine classes need genuinely different treatment. A propeller is
        /// a momentum device: thrust is power over airspeed, so it is enormous on the
        /// takeoff roll and small at 200 m/s. A turbojet produces nearly the same
        /// thrust at any subsonic speed, which is precisely why it outruns a piston
        /// fighter in level flight.
        ///
        /// Getting this wrong is not cosmetic. With constant thrust everywhere, a
        /// well-powered prop out-accelerates a 262 and the whole jet-versus-prop
        /// distinction in the catalogue is meaningless.
        /// </summary>
        private float ThrustAt(float density, float speed)
        {
            if (!_spec.PropellerDriven)
            {
                // Turbojets lose thrust with altitude as the compressor sees less
                // air, but far less than a piston engine, and they keep it at speed.
                float altitudeFactor = Mathf.Lerp(1f, density / AirDensitySeaLevel, 0.35f);
                return _spec.MaxThrustN * altitudeFactor;
            }

            // Piston engines are altitude-limited: roughly half the power is gone
            // by 6 km, which is why prop fighters want height and jets do not.
            float power = _spec.EnginePowerW * (density / AirDensitySeaLevel);

            // Thrust = power * efficiency / airspeed, capped by the propeller's
            // static thrust. The floor on airspeed keeps this finite at a standstill.
            float velocityThrust = power * _spec.PropellerEfficiency / Mathf.Max(speed, 25f);
            return Mathf.Min(velocityThrust, _spec.MaxThrustN);
        }


        /// <summary>
        /// Total drag at the given speed and density, for an aircraft holding itself
        /// level.
        ///
        /// Shares its constants with the main aerodynamics on purpose. If the two
        /// drifted apart, the trim would aim at a speed the aircraft could never
        /// actually sustain, and the aircraft would sit there slowly accelerating or
        /// decelerating forever.
        /// </summary>
        private float DragAt(float density, float speed, float wingArea)
        {
            float dynamicPressure = Mathf.Max(1e-3f, 0.5f * density * speed * speed);
            float requiredLift = _spec.MassKg * Gravity;

            float liftCoefficient = Mathf.Min(
                requiredLift / (dynamicPressure * wingArea), MaxLiftCoefficient);
            float lift = liftCoefficient * dynamicPressure * wingArea;

            float aspectRatio = Mathf.Max(1f, (_spec.WingSpanM * _spec.WingSpanM) / wingArea);
            float inducedDrag = (lift * lift)
                / (dynamicPressure * wingArea * Mathf.PI * OswaldEfficiency * aspectRatio);

            float parasiteDrag = dynamicPressure * wingArea * _spec.ParasiteDragCoefficient;

            float mach = speed / SpeedOfSound;
            float compressibilityDrag = 0f;
            if (mach > TransonicMach)
            {
                compressibilityDrag = parasiteDrag * (mach - TransonicMach)
                    * CompressibilityDragRise;
            }

            return inducedDrag + parasiteDrag + compressibilityDrag;
        }

        private readonly AircraftDefinition _spec;

        // --- State ---
        public Vector3 Position;
        public Vector3 Velocity;

        /// <summary>Body orientation, as Euler angles in degrees for readability.</summary>
        public float PitchDeg;

        /// <summary>Pitch rate, degrees per second. Feeds the damping term.</summary>
        public float PitchDegRate;
        public float YawDeg;
        public float RollDeg;

        public float Throttle;

        /// <summary>Propeller RPM-ish state used only for the visual.</summary>
        public float PropellerAngle;

        /// <summary>True when the wing has stalled this frame.</summary>
        public bool IsStalled { get; private set; }

        /// <summary>True when the airframe is over never-exceed speed and is breaking up.</summary>
        public bool IsOverstressed { get; private set; }

        public float Altitude => Position.y;
        public float SpeedMs => Velocity.magnitude;

        /// <summary>Indicated airspeed. Compression makes it read faster than TAS at altitude.</summary>
        public float IndicatedSpeedMs
        {
            get
            {
                float density = AirDensity(Position.y);
                float ratio = AirDensitySeaLevel / Mathf.Max(density, 0.05f);
                return SpeedMs * Mathf.Sqrt(ratio);
            }
        }

        public FlightModel(AircraftDefinition spec, Vector3 spawnPosition, float headingDeg)
        {
            _spec = spec;
            Position = spawnPosition;
            YawDeg = headingDeg;
            PitchDeg = 2f;
            RollDeg = 0f;
            Throttle = 0.8f;

            // Spawn at the aircraft's actual trim speed, already established in the
            // catalogue rather than guessed as a fraction of top speed.
            //
            // This matters more than it looks. A fraction of top speed is a
            // different number for every aircraft, so some spawn well below their
            // stall speed and cannot generate enough lift to fly at all. The Me 262
            // spawned at 134 m/s, decelerated, and was still below its 75 m/s stall
            // speed several minutes later with the nose level and full power on.
            // Starting at the trim speed means the aircraft begins in equilibrium,
            // which is also what makes it controllable from the first frame.
            Velocity = Forward() * spec.TrimSpeedMs;
        }

        public AircraftDefinition Spec => _spec;

        /// <summary>Standard-atmosphere density at altitude.</summary>
        public static float AirDensity(float altitudeM)
        {
            return AirDensitySeaLevel * Mathf.Exp(-Mathf.Max(0f, altitudeM) / ScaleHeight);
        }

        /// <summary>
        /// Body axes, derived from a single rotation.
        ///
        /// These must come from one shared quaternion. Computing each independently
        /// as Quaternion.Euler(pitch, yaw, roll) applied to a different unit vector
        /// produces three axes that are not mutually perpendicular, because Euler
        /// order means the rotations do not commute. The symptom is subtle and was
        /// expensive to find: lift gets applied in a direction that disagrees with the
        /// nose, so an aircraft sitting level and unloaded still sinks a few metres
        /// per second and no amount of attitude trim will hold it, because the
        /// position it is converging to is not level flight.
        ///
        /// Deriving all three from the same rotation keeps the basis orthonormal, and
        /// makes Forward, Right and Up consistent with each other by construction.
        /// </summary>
        private Quaternion Rotation =>
            Quaternion.Euler(PitchDeg, YawDeg, RollDeg);

        public Vector3 Forward() => Rotation * Vector3.forward;

        public Vector3 Right() => Rotation * Vector3.right;

        public Vector3 Up() => Rotation * Vector3.up;

        /// <summary>
        /// Advances the aircraft by one fixed step.
        /// </summary>
        /// <param name="pitchInput">-1 nose down .. +1 nose up</param>
        /// <param name="rollInput">-1 roll left .. +1 roll right</param>
        /// <param name="yawInput">Rudder, -1..1</param>
        /// <param name="throttleInput">-1..1, adjusts the throttle setting</param>
        public void Step(float dt, float pitchInput, float rollInput, float yawInput, float throttleInput)
        {
            Throttle = Mathf.Clamp01(Throttle + throttleInput * dt);

            // --- Kinematics, sampled before control ---
            // Stability has to be judged against the airflow as it is now, so the
            // angle of attack is measured up front. Reading it after the control
            // update would make the aerodynamics chase the elevator by one frame.
            Vector3 velocity = Velocity;
            float speed = velocity.magnitude;
            float density = AirDensity(Position.y);
            float wingArea = _spec.WingAreaM2;

            // Zero airspeed produces no dynamic pressure, hence no lift, which is
            // how an aircraft ends up falling. The small floor keeps the stall
            // maths well-conditioned instead of dividing by zero at the top of a
            // vertical climb.
            float dynamicPressure = Mathf.Max(0.01f, 0.5f * density * speed * speed);

            // Angle of attack: the angle between the nose and the direction the
            // aircraft is actually travelling through the air.
            //
            // The argument order matters and is easy to get backwards. Measured as
            // SignedAngle(flightPath, nose, right) a nose-up attitude reports a
            // NEGATIVE angle of attack, so lift is computed downward and the
            // aircraft is driven into the ground with no way to recover. Measuring
            // nose against flight path gives the conventional sign: nose above the
            // airflow is positive, and therefore lift-up.
            float angleOfAttack = 0f;
            if (speed > 1f)
            {
                Vector3 flightPath = Vector3.ProjectOnPlane(velocity, Right()).normalized;
                angleOfAttack = Vector3.SignedAngle(Forward(), flightPath, Right());
            }
            angleOfAttack = Mathf.Clamp(angleOfAttack, -20f, 24f);

            // --- Control authority ---
            // Roll is the most agile axis and is scaled by speed: a stalled plane
            // cannot roll, which is the single most important thing a player learns
            // in a flight sim.
            float speedFactor = Mathf.Clamp01(SpeedMs / Mathf.Max(20f, _spec.TopSpeedMs * 0.5f));

            RollDeg += rollInput * _spec.RollRateDeg * speedFactor * dt;
            RollDeg = Mathf.Clamp(RollDeg, -80f, 80f);

            // Pitch and roll induce turn. Bank angle is what actually turns a
            // fighter: pull back and roll, and the lift vector tilts.
            float bankTurn = -RollDeg * 0.18f;
            YawDeg += (bankTurn + yawInput * 8f) * speedFactor * dt;

            // --- Elevator, and static longitudinal stability ---
            // A tailplane pulls the nose back toward the angle of attack the wing is
            // trimmed for. Without this term the model has no stable point at all:
            // any speed change moves the airflow, the angle of attack drifts, lift
            // stops matching weight, and the aircraft sinks from cruise forever
            // because no elevator input is present to correct it.
            float trimAngleOfAttack = TrimAngleOfAttackFor(dynamicPressure, wingArea);

            // Sign matters here, and getting it wrong makes the aircraft diverge
            // instead of fly. Unity's Euler order puts a *positive* X pitch
            // nose-down, so a nose-up correction is a negative pitch change. With
            // the signs the other way round the stability term pushes the nose
            // further down every frame and the aircraft leaves the atmosphere.
            float stabilityPitch = -(trimAngleOfAttack - angleOfAttack) * PitchStabilityGain;

            // Speed stability: the aircraft is trimmed for the speed its power and
            // drag can actually sustain at this altitude, not merely for the speed
            // it happens to be doing when it spawns.
            //
            // This is what removes the phugoid. Matching lift to weight alone leaves
            // the speed free to wander, so the aircraft trades height for speed and
            // back for minutes on end; the Meteor oscillated down to 72 m/s at full
            // throttle. Referencing thrust against the *steady-state* drag for the
            // current speed and density gives a genuine fixed point: at the trim
            // speed, excess thrust is zero, so both altitude and speed stop moving.
            // Real aircraft sit on exactly this point, which is what cruise trim is.
            // Level-flight trim: a cascade of two error terms.
            // Hands-off trim.
            //
            // Three ideas, each of which was wrong on its own before being combined
            // correctly. Understanding why they are needed is the difference between
            // an aircraft that flies and one that porpoises into the ground.
            //
            // First, the airflow has to be judged against the attitude the aircraft
            // will actually hold this step, not the one it had on entry.
            //
            // Second, surplus thrust buys a *commanded climb angle*, not a raw pitch
            // rate. Commanding pitch rate directly has no fixed point: spare power
            // keeps asking for nose-up, the angle of attack rises with it, induced
            // drag climbs, speed bleeds away, and the aircraft ends up stalled at the
            // ceiling. That is not hypothetical, it is what every jet in this roster
            // did before the climb angle was bounded.
            //
            // Third, surplus thrust alone still does not converge. Climbing costs
            // speed, slower flight needs a higher angle of attack, and more induced
            // drag, so the aircraft keeps sinking slowly instead of settling. Correcting
            // toward the catalogue trim speed removes the drift: the fixed point is
            // the speed at which surplus thrust is zero, which is what trim speed means.
            float trimPitch = 0f;
            if (Mathf.Abs(pitchInput) < 0.05f && speed > 10f)
            {
                float flightPathAngle = Mathf.Asin(Mathf.Clamp(velocity.y / speed, -1f, 1f))
                    * Mathf.Rad2Deg;

                float availableThrust = ThrustAt(density, speed) * Throttle;
                float surplusThrust = availableThrust - DragAt(density, speed, wingArea);
                float speedError = speed - _spec.TrimSpeedMs;

                // Scaled by wing loading, because that is what makes the response
                // comparable across very different airframes. Wing loading ranges from
                // about 1900 N/m2 on the A6M Zero to 4600 N/m2 on the P-51D, so a
                // fixed gain made the heavy jets pitch violently for a surplus a light
                // prop handled calmly.
                float wingLoading = _spec.MassKg * Gravity / Mathf.Max(1f, wingArea);
                float surplusGain = SurplusThrustPitchGain * ReferenceWingLoading / wingLoading;

                float commandedClimb = Mathf.Clamp(
                    -surplusThrust * surplusGain,
                    -MaxTrimDiveAngleDeg, MaxTrimClimbAngleDeg);

// Work the actual path onto the commanded path rather than merely
                // damping it. Note the sign: positive Euler pitch is nose-down, so a
                // path steeper than commanded gets a nose-down correction.
                float pathCorrection = (flightPathAngle - commandedClimb) * FlightPathDampingGain;

                // Speed error is a *bias* on the path, not a rate term of its own.
                //
                // Adding it straight into the pitch rate looks reasonable and is
                // catastrophic. A 60 m/s error then demands about 3 deg/s of nose
                // down, which swamps the path correction entirely: the aircraft
                // flies itself into the ground at full power with the nose pinned
                // down, because thrust is being pointed at the floor instead of along
                // the flight path. The Meteor did precisely this and stalled out
                // every time.
                //
                // What is wanted is a target flight path, not a target speed. When
                // slow, the target path tilts down to let the aircraft trade height
                // for the speed it needs; the path term then does the work, and the
                // attitude follows the physics instead of fighting it.
                float speedBiasAngle = Mathf.Clamp(
                    speedError * SpeedToPathGain, -MaxTrimDiveAngleDeg, MaxTrimDiveAngleDeg);

                trimPitch = (flightPathAngle - commandedClimb - speedBiasAngle)
                    * FlightPathDampingGain;
            }

            float elevatorPitch = -pitchInput * _spec.TurnRateDeg * 0.5f * speedFactor;

            // Damped, or the tailplane overshoots and the aircraft porpoises.
            // Rate damping is what turns a bare spring into something that settles.
            float pitchRate = (elevatorPitch + stabilityPitch + trimPitch)
                - PitchDegRate * PitchDampingGain;

            // Cap the total pitch rate at the elevator's authority.
            //
            // The surplus-thrust term does not know or care about the angle of
            // attack, so an aircraft with plenty of power to spare keeps commanding
            // "nose up" indefinitely and flies straight into a stall. Real aircraft
            // do not have this problem because a steep climb needs an angle of
            // attack the wing cannot provide, so excess power buys a climb *angle*
            // only up to the stall limit. Bounding the rate by the same authority
            // the elevator has reproduces that: the aircraft settles at the steepest
            // climb its wing can actually fly.
            float maxPitchRate = _spec.TurnRateDeg * 0.5f;
            pitchRate = Mathf.Clamp(pitchRate, -maxPitchRate, maxPitchRate);

            PitchDegRate = Mathf.Lerp(PitchDegRate, pitchRate, 0.35f);

            PitchDeg += PitchDegRate * dt;
            PitchDeg = Mathf.Clamp(PitchDeg, -45f, 40f);

            // Re-measure the airflow against the attitude the aircraft will actually
            // fly this step. Reusing the pre-control angle leaves a one-frame lag,
            // which shows up as a slow steady sink in otherwise level flight.
            if (speed > 1f)
            {
                Vector3 settled = Vector3.ProjectOnPlane(velocity, Right()).normalized;
                angleOfAttack = Vector3.SignedAngle(Forward(), settled, Right());
                angleOfAttack = Mathf.Clamp(angleOfAttack, -20f, 24f);
            }

            // Roll naturally levels out, which is how real aircraft behave and
            // stops the plane from spiralling uncontrollably.
            if (Mathf.Abs(rollInput) < 0.01f)
            {
                RollDeg *= 1f - 1.6f * dt;
            }

            Vector3 forward = Forward();

            // --- Lift ---
            // Lift coefficient. Thin-aerofoil theory gives CL = 2*pi*alpha for small
            // angles, which is the relation that makes wing area meaningful: a small
            // wing (Spitfire, 12.4 m2) needs a higher coefficient and therefore a
            // lower speed to fly than a big wing (P-51D, 18.1 m2) of the same mass.
            //
            // Past the stall angle the coefficient collapses. Modelling that collapse
            // is what makes a stall recoverable rather than an unrecoverable fall.
            float absAoa = Mathf.Abs(angleOfAttack);
            float liftCoefficient;

            if (absAoa > StallAngleDeg)
            {
                IsStalled = true;
                // Lose roughly 1.2 CL per 10 degrees past the stall, floored so the
                // wing keeps some authority instead of becoming a brick.
                float over = (absAoa - StallAngleDeg) * 0.12f;
                liftCoefficient = 1.2f - Mathf.Min(over, 0.9f);
                liftCoefficient = Mathf.Max(liftCoefficient, 0.3f);
            }
            else
            {
                IsStalled = false;
                liftCoefficient = 2f * Mathf.PI * angleOfAttack * Mathf.Deg2Rad;
                // Thin-aerofoil theory over-predicts near the stall; taper it so the
                // peak lands at a believable CLmax.
                liftCoefficient = Mathf.Min(liftCoefficient, MaxLiftCoefficient);
            }

            Vector3 lift = Up() * (liftCoefficient * dynamicPressure * wingArea);

            // --- Drag ---
            // Induced drag grows with lift, parasite drag with speed. Propellers
            // lose thrust with altitude; jets hold it.
            float liftMagnitude = lift.magnitude;

            // Induced drag follows D = L^2 / (q * S * pi * e * AR). The aspect ratio is
            // what separates a wide, efficient wing from a narrow one: it is why
            // the Meteor can dogfight the 262 despite the 262's speed advantage.
            float aspectRatio = Mathf.Max(1f, (_spec.WingSpanM * _spec.WingSpanM) / wingArea);
            float inducedDrag = (liftMagnitude * liftMagnitude)
                / (dynamicPressure * wingArea * Mathf.PI * OswaldEfficiency * aspectRatio);

            float parasiteDrag = dynamicPressure * wingArea * _spec.ParasiteDragCoefficient;
            float compressibilityDrag = 0f;

            // Transonic drag rise. Below Mach 0.85 it is negligible; past it the
            // drag climbs steeply, which is why the 262 needs altitude to go fast.
            float mach = speed / SpeedOfSound;
            if (mach > TransonicMach)
            {
                compressibilityDrag = parasiteDrag * (mach - TransonicMach) * CompressibilityDragRise;
            }

            Vector3 dragForce = -velocity.normalized * (inducedDrag + parasiteDrag + compressibilityDrag);

            // --- Thrust ---
            float maxThrust = ThrustAt(density, speed);
            Vector3 thrust = forward * (maxThrust * Throttle);

            // --- Gravity ---
            Vector3 gravity = Vector3.down * (_spec.MassKg * Gravity);

            // --- Integrate ---
            // --- Integrate ---
            //
            // Explicit Euler on lift is only conditionally stable. Dynamic pressure
            // scales with the square of speed, so the lift term grows faster than the
            // integration can absorb it, and once an aircraft is pushed past a few
            // hundred m/s the lift for one step is enough to throw it to the ceiling
            // and far beyond. This is a genuine solver limitation, not a tuning
            // problem: the Me 262 reached 3720 m/s at the ceiling before this was
            // caught.
            //
            // The fix is to cap the change in velocity a single step may make, which
            // is standard practice for explicit integrators. The cap is generous
            // enough to be inert during ordinary flight and only ever engages when
            // the step is no longer physically solvable.
            Vector3 acceleration = (lift + dragForce + thrust + gravity) / _spec.MassKg;

            Vector3 velocityChange = acceleration * dt;
            float maxChange = MaxVelocityChangePerStepMs * _spec.MassKg;
            if (velocityChange.sqrMagnitude > maxChange * maxChange)
            {
                velocityChange = velocityChange.normalized * maxChange;
            }

            velocity += velocityChange;

            // Hard speed ceiling.
            //
            // The per-step clamp above is not sufficient on its own, because lift
            // grows with the square of speed: an aircraft past a certain speed needs
            // so much lift that even a bounded step accelerates it further, and the
            // error compounds. Left unbounded, the Me 262 and the Meteor both reached
            // over 3000 m/s at the service ceiling while stalled.
            //
            // This is a numerical backstop rather than a flight rule. It sits above
            // every aircraft's never-exceed speed, so it can never engage during
            // normal flight or even in a full-power dive.
            float speedAfterStep = velocity.magnitude;
            float speedLimit = _spec.NeverExceedSpeedMs * 1.5f;
            if (speedAfterStep > speedLimit)
            {
                velocity = velocity.normalized * speedLimit;
            }

            Position += velocity * dt;

            // Never let the aircraft go below the terrain.
            if (Position.y < 1.5f)
            {
                Position.y = 1.5f;
                if (velocity.y < 0f) velocity.y = 0f;
            }

            // Ceiling: thrust stops being able to overcome drag.
            if (Position.y > _spec.CeilingM)
            {
                Position.y = _spec.CeilingM;
            }

            Velocity = velocity;

            IsOverstressed = SpeedMs > _spec.NeverExceedSpeedMs;
            PropellerAngle += Throttle * 1800f * dt;
        }

        /// <summary>
        /// Whether the aircraft is pointed close enough to <paramref name="target"/>
        /// to be putting rounds on it. Used to gate the gun so the HUD can show
        /// convergence rather than letting players spray.
        /// </summary>
        public bool IsAimedAt(Vector3 target, float maxAngleDeg)
        {
            Vector3 toTarget = target - Position;
            if (toTarget.sqrMagnitude < 1f) return true;
            return Vector3.Angle(Forward(), toTarget) <= maxAngleDeg;
        }

        /// <summary>
        /// Angular velocity of the gunsight line for a target at <paramref name="range"/>.
        /// This is the lead the player has to compute, so the HUD can show it.
        /// </summary>
        public float LeadAngleDeg(Vector3 targetVelocity, Vector3 target, float range)
        {
            if (range < 1f) return 0f;
            float timeOfFlight = range / Mathf.Max(1f, _spec.GunMuzzleVelMs);
            Vector3 lead = target + targetVelocity * timeOfFlight;
            Vector3 toLead = lead - Position;
            if (toLead.sqrMagnitude < 1f) return 0f;
            return Vector3.SignedAngle(Forward(), toLead, Up());
        }
    }
}
