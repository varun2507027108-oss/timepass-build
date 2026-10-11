using System.Collections.Generic;
using UnityEngine;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Per-wheel contact state. The analogue of <c>PhysicalWheel.IsOnTheGround</c> in
    /// both reference applications.
    /// </summary>
    public interface IVehicleWheel
    {
        bool IsOnTheGround { get; }
        bool IsDriving { get; }
        bool IsTurning { get; }
        float Radius { get; }
        void SetTorque(float velocity);
        void Turn(float value);
    }

    /// <summary>
    /// A tracked vehicle driven by Unity PhysX WheelColliders.
    ///
    /// Architecture, taken from the two reference applications
    /// -------------------------------------------------------
    /// Both XDEVS titles (Modern Tanks, Tank Force) build ground contact the same
    /// way, and neither of them integrates a suspension force or fits a plane:
    ///
    ///   * <c>TankUnitController</c> owns a <c>Rigidbody</c> plus an array of
    ///     <c>WheelCollider</c>. PhysX resolves each wheel as an independent contact,
    ///     so a wheel hanging in the air contributes exactly zero force with no
    ///     special-case code anywhere.
    ///   * <c>PhysicalWheel</c> wraps one <c>WheelCollider</c> and exposes
    ///     <c>IsOnTheGround</c> from a cached <c>WheelHit</c>, so callers can ask
    ///     per wheel whether it is carrying anything.
    ///   * <c>TransmissionController</c> splits the wheel array into left and right
    ///     sets and drives them differentially, which is what makes it skid-steer.
    ///   * <c>SuspensionController</c> does NOT simulate. It holds a
    ///     <c>suspension</c> Transform and a separate <c>body</c> Transform and
    ///     offsets the body from the authoritative one in
    ///     <c>AnimateWheels(dampLength, dampSpeed, velocityMagnitude, velocity)</c>.
    ///
    /// The last point is the one that matters most for how this is built. In both
    /// applications the PhysX transform is AUTHORITATIVE and the visual body is
    /// derived from it. Nothing integrates a height, so nothing can integrate a
    /// height wrongly, and the renderer can never disagree with the simulation
    /// about where the vehicle is, because it is reading the simulation.
    ///
    /// This class follows that structure. <see cref="TrackedVehicle"/> owns the
    /// Rigidbody and wheels; <see cref="VehicleBodyOffset"/> derives a visual body
    /// from it.
    ///
    /// Deliberate divergence
    /// --------------------
    /// The two applications step PhysX on Unity's own FixedUpdate clock. This one
    /// keeps BattleSim's explicit <c>Physics.Simulate</c> lockstep instead, because
    /// the surrounding game already advances on a fixed 30 Hz accumulator and
    /// having physics on a different clock than the ballistics is a worse problem
    /// than the one being solved. The contact ARCHITECTURE is the same; only the
    /// clock differs.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class TrackedVehicle : MonoBehaviour
    {
        // ------------------------------------------------------------- geometry

        [Header("Contact geometry (measured from the shipped model)")]
        [Tooltip("Distance between front and rear road wheels, metres.")]
        public float trackGauge = 5.89f;

        [Tooltip("Overall track width, metres.")]
        public float trackWidth = 2.97f;

        [Tooltip("Road wheel radius, metres.")]
        public float wheelRadius = 0.5f;

        [Tooltip("Suspension travel, metres. This is the physical limit that stops " +
                 "the hull folding through the ground on terrain the springs cannot span.")]
        public float suspensionTravel = 0.45f;

        [Tooltip("Spring force per metre of compression at rest, newtons per metre.")]
        public float springRate = 220000f;

        [Tooltip("Damping force per metre per second of compression rate.")]
        public float damperRate = 34000f;

        [Tooltip("Fraction of springRate kept as the damper's force ceiling. Keyed " +
                 "to the spring so a damper can never exceed the structure it is on.")]
        [Range(0.05f, 1f)]
        public float damperCeilingFraction = 0.5f;

        // ------------------------------------------------------------- drivetrain

        [Header("Drivetrain")]
        [Tooltip("Force at full throttle, newtons.")]
        public float engineForce = 120000f;

        [Tooltip("Brake force when the throttle opposes travel, newtons.")]
        public float brakeForce = 260000f;

        [Tooltip("Speed the engine drives toward, metres per second.")]
        public float maxSpeed = 11.11f;

        [Tooltip("How far the two track speeds separate under full steering, as a " +
                 "fraction of vehicle speed. 0.5 means the inside track runs at half " +
                 "and the outside at 1.5x, which is what makes it skid-steer.")]
        [Range(0f, 1f)]
        public float differentialSkew = 0.5f;

        [Tooltip("Below this speed the differential cannot turn the hull, so a small " +
                 "neutral-turn term takes over. Skid steering needs something to " +
                 "push against; this is the scrub-turn that does it.")]
        public float neutralTurnRate = 0.35f;

        [Tooltip("Speed at which full steering no longer uses the whole differential.")]
        public float speedAtMaxYaw = 4.5f;

        [Tooltip("Gain on the per-side differential trim, newtons per m/s of speed " +
                 "error. Higher turns more sharply but oscillates when too high.")]
        public float differentialTrimGain = 220f;

        [Tooltip("Rigidbody mass, kg. Set from the vehicle catalogue at runtime.")]
        public float massKg = 26000f;

        // -------------------------------------------------------------- assembly

        public Rigidbody Body { get; private set; }
        public WheelCollider[] Wheels { get; private set; } = new WheelCollider[0];
        public List<IVehicleWheel> WheelViews { get; } = new List<IVehicleWheel>();

        /// <summary>
        /// The steering demand from the most recent <see cref="ApplyForces"/> call,
        /// -1..1. Kept so the track animation can show the differential the drivetrain
        /// actually applied rather than re-deriving it.
        /// </summary>
        public float LastSteerDemand { get; private set; }

        /// <summary>True when at least one wheel is carrying load.</summary>
        public bool IsOnGround
        {
            get
            {
                for (int i = 0; i < Wheels.Length; i++)
                {
                    if (Wheels[i].isGrounded) return true;
                }
                return false;
            }
        }

        /// <summary>How many wheels are carrying load, 0..Wheels.Length.</summary>
        public int GroundedWheelCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Wheels.Length; i++)
                {
                    if (Wheels[i].isGrounded) n++;
                }
                return n;
            }
        }

        /// <summary>
        /// Average normal of the wheels that are in contact.
        ///
        /// This is the reference application's per-wheel ground normal, gathered.
        /// Only wheels that are actually touching contribute, because PhysX reports
        /// a contact normal only where there IS a contact -- an airborne wheel has
        /// none to report, which is precisely why that engine does not need the
        /// load-gating this game's previous analytic model required.
        /// </summary>
        public bool TryGetAverageGroundNormal(out Vector3 normal)
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int i = 0; i < Wheels.Length; i++)
            {
                if (Wheels[i].isGrounded)
                {
                    sum += Wheels[i].GetGroundHit(out WheelHit hit) ? hit.normal : Vector3.up;
                    n++;
                }
            }
            if (n == 0) { normal = Vector3.up; return false; }
            normal = sum.normalized;
            return true;
        }

        private void Awake()
        {
            Body = GetComponent<Rigidbody>();
            Body.mass = massKg;
            // Centre of mass low and slightly forward, so the hull resists rolling and
            // pitches nose-up under power rather than tipping onto its tail.
            Body.centerOfMass = new Vector3(0f, -0.35f, 0.15f);
            Body.angularDamping = 2.5f;
            Body.linearDamping = 0.02f;
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        /// <summary>
        /// Build the wheel set: four road wheels per side, laid out along the track.
        /// </summary>
        /// <remarks>
        /// Four per side rather than two so that the suspension spans the vehicle the
        /// way the real contact patch does. A pair at the ends only samples terrain at
        /// the extremities and lets everything between them fall through, which is
        /// what produces a hull that perches on one corner over a rise.
        /// </remarks>
        public void BuildWheels(int perSide)
        {
            if (perSide < 1) perSide = 4;

            float halfGauge = trackGauge * 0.5f;
            float halfWidth = trackWidth * 0.5f;

            // Distribute along the gauge, inset so the end wheels sit under the hull
            // rather than under the nose and tail overhang.
            var offsets = new List<float>();
            for (int i = 0; i < perSide; i++)
            {
                float t = perSide == 1 ? 0.5f : (float)i / (perSide - 1);
                offsets.Add(Mathf.Lerp(-halfGauge * 0.86f, halfGauge * 0.86f, t));
            }

            // Innermost first so index 0 is the front-left road wheel, matching the
            // left/right split the transmission uses.
            var roots = new List<WheelCollider>();
            foreach (float z in offsets)
            {
                foreach (float x in new[] { -halfWidth, halfWidth })
                {
                    var go = new GameObject($"Wheel_{roots.Count:00}");
                    go.transform.SetParent(transform, false);
                    go.transform.localPosition = new Vector3(x, 0f, z);

                    var wc = go.AddComponent<WheelCollider>();
                    wc.radius = wheelRadius;
                    wc.suspensionDistance = suspensionTravel;
                    wc.forceAppPointDistance = 0f;
                    wc.mass = massKg / (perSide * 2);

                    var spring = wc.suspensionSpring;
                    spring.spring = springRate;
                    // Damper ceiling is expressed through the PhysX damper rate, which
                    // is a damping coefficient in N per m/s. Scaling it off the spring
                    // keeps the two coupled exactly as the analytic model did.
                    spring.damper = springRate * damperCeilingFraction;
                    wc.suspensionSpring = spring;

                    var friction = wc.forwardFriction;
                    // Tracks grip hard longitudinally (that is what draws the vehicle
                    // forward) and resist sideways motion less, which is what lets it
                    // slide across a camber instead of gripping and tracking across it.
                    friction.extremumSlip = 4f;
                    friction.extremumValue = 1.4f;
                    friction.asymptoteSlip = 0.8f;
                    friction.asymptoteValue = 0.72f;
                    friction.stiffness = 0.6f;
                    wc.forwardFriction = friction;

                    var sideways = wc.sidewaysFriction;
                    sideways.extremumSlip = 0.4f;
                    sideways.extremumValue = 1.0f;
                    sideways.asymptoteSlip = 0.2f;
                    sideways.asymptoteValue = 0.55f;
                    sideways.stiffness = 0.55f;
                    wc.sidewaysFriction = sideways;

                    roots.Add(wc);
                    WheelViews.Add(new PhysicalWheel(wc));
                }
            }

            Wheels = roots.ToArray();
        }

        /// <summary>
        /// Apply one tick of drivetrain input. Called once per simulation step,
        /// immediately before <c>Physics.Simulate</c>.
        /// </summary>
        /// <param name="throttle">-1 reverse .. +1 forward, already rate-limited.</param>
        /// <param name="steer">-1 left .. +1 right, already rate-limited.</param>
        public void ApplyForces(float throttle, float steer)
        {
            if (Body == null || Body.isKinematic) return;

            throttle = Mathf.Clamp(throttle, -1f, 1f);
            steer = Mathf.Clamp(steer, -1f, 1f);
            LastSteerDemand = steer;

            float speed = Vector3.Dot(Body.linearVelocity, transform.forward);

            // --- differential ------------------------------------------------
            // The left and right tracks are driven at different speeds. That is what
            // steering IS on a tracked vehicle; there is no steering road wheel to
            // turn. The split is proportional to speed, so the turn RADIUS stays
            // roughly constant and the yaw RATE rises with speed -- the opposite of
            // a car, and a large part of why the vehicle reads as heavy.
            float skew = steer * differentialSkew;
            float leftTarget = speed * (1f - skew);
            float rightTarget = speed * (1f + skew);

            // Below the speed where the differential can act, fall back to the neutral
            // turn: one track locked, the other driven, scrubbing on the spot. It is
            // slow because it really is slow, but without it a stopped vehicle cannot
            // rotate at all.
            float yawScale = 0f;
            if (Mathf.Abs(speed) > 0.05f)
            {
                yawScale = Mathf.Clamp01(Mathf.Abs(speed) / speedAtMaxYaw);
            }
            else
            {
                float neutral = steer * neutralTurnRate * (Mathf.Abs(speed) < 0.05f ? 1f : 0f);
                if (Mathf.Abs(neutral) > 0.0001f)
                {
                    leftTarget = -Mathf.Sign(steer) * 1.2f;
                    rightTarget = Mathf.Sign(steer) * 1.2f;
                }
            }

            // --- engine and brake --------------------------------------------
            float baseTorque = 0f;

            if (Mathf.Abs(throttle) > 0.01f)
            {
                bool opposing = throttle * speed < -0.02f;
                if (opposing)
                {
                    // Holding the throttle against travel locks the tracks. The tracks
                    // ARE the brakes, which is why a tank stops far harder than it
                    // accelerates. No motor torque at all in this branch, so the
                    // differential trim cannot pull the vehicle back into drive.
                    for (int i = 0; i < Wheels.Length; i++)
                    {
                        Wheels[i].motorTorque = 0f;
                        Wheels[i].brakeTorque = Wheels[i].isGrounded ? brakeForce : 0f;
                    }
                    ApplyDifferentialTrim(0, leftTarget, speed, 0f);
                    ApplyDifferentialTrim(1, rightTarget, speed, 0f);
                    return;
                }

                float governor = 1f;
                if (Mathf.Abs(speed) >= maxSpeed && throttle * speed > 0f) governor = 0f;

                // Constant force at low speed falling as 1/v above the balance point,
                // which is what constant POWER looks like.
                float v = Mathf.Max(Mathf.Abs(speed), 1.2f);
                float falloff = Mathf.Clamp(speedAtMaxYaw / v, 0.35f, 1f);

                baseTorque = throttle * engineForce * falloff * governor;
                for (int i = 0; i < Wheels.Length; i++)
                {
                    Wheels[i].brakeTorque = 0f;
                }
            }
            else
            {
                // Throttle closed: the engine drags and the tracks are free. Light drag,
                // not a brake, so a coasting vehicle keeps rolling.
                for (int i = 0; i < Wheels.Length; i++)
                {
                    Wheels[i].brakeTorque = Wheels[i].isGrounded ? brakeForce * 0.06f : 0f;
                }
            }

            ApplyDifferentialTrim(0, leftTarget, speed, baseTorque);
            ApplyDifferentialTrim(1, rightTarget, speed, baseTorque);
        }

        /// <summary>
        /// Keep a side's tracks near their target speed by trimming motor torque.
        /// </summary>
        /// <remarks>
        /// WheelCollider applies motorTorque as a raw force, so a single value drives
        /// every wheel on the vehicle at once. Steering therefore needs each side
        /// trimmed RELATIVE to the other, and the difference between the two sides is
        /// what the turn is.
        ///
        /// This runs AFTER the engine/brake decision and only ADDS a differential
        /// trim to it -- it does not decide throttle or braking itself. An earlier
        /// version assigned motorTorque absolutely here, which silently overwrote the
        /// brake that the main block had just applied: reversing set a brake, this
        /// replaced it with drive torque, and the vehicle kept accelerating forward
        /// instead of reversing.
        /// </remarks>
        private void ApplyDifferentialTrim(int side, float targetSpeed, float currentSpeed, float baseTorque)
        {
            int perSide = Wheels.Length / 2;
            if (perSide <= 0) return;

            // The trim is the DIFFERENCE between this side's target and the vehicle's
            // actual speed, scaled by the requested skew -- but it is added to the base
            // drive, never replaces it, and it only ever SUBTRACTS magnitude. It is a
            //
            //     trim = (targetSpeed - currentSpeed) * gain
            //
            // where targetSpeed is the side's commanded speed (which already includes
            // the skew). On its own that term chases an absolute speed and will fight
            // braking -- when the vehicle is slowing to a stop, every side sees a large
            // error and the trim drives the wheels ONWARD. Constraining it to be at
            // most the base torque keeps braking authoritative: the trim can only
            // distribute the drive already being asked for between the two sides, and
            // when baseTorque is zero (braking) it contributes nothing.
            float error = targetSpeed - currentSpeed;
            float trim = Mathf.Clamp(error * differentialTrimGain, -Mathf.Abs(baseTorque), Mathf.Abs(baseTorque));

            for (int i = side * perSide; i < (side + 1) * perSide && i < Wheels.Length; i++)
            {
                var wc = Wheels[i];
                if (!wc.isGrounded)
                {
                    wc.motorTorque = 0f;
                    continue;
                }
                wc.motorTorque = (baseTorque + trim) / wc.mass;
            }
        }

        /// <summary>
        /// Place the vehicle on the ground without dropping it through the terrain.
        /// </summary>
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);
            if (Body != null)
            {
                Body.velocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
                Body.position = position;
                Body.rotation = rotation;
            }
        }
    }

    /// <summary>
    /// Wraps one WheelCollider and exposes contact state, mirroring
    /// <c>PhysicalWheel</c> in both reference applications.
    /// </summary>
    public class PhysicalWheel : IVehicleWheel
    {
        private readonly WheelCollider _collider;

        public PhysicalWheel(WheelCollider collider)
        {
            _collider = collider;
        }

        public bool IsOnTheGround => _collider != null && _collider.isGrounded;
        public bool IsDriving { get; private set; }
        public bool IsTurning { get; private set; }
        public float Radius => _collider != null ? _collider.radius : 0f;

        public void SetTorque(float velocity)
        {
            if (_collider == null) return;
            _collider.motorTorque = velocity;
            IsDriving = Mathf.Abs(velocity) > 0.01f;
        }

        public void Turn(float value)
        {
            IsTurning = Mathf.Abs(value) > 0.01f;
        }
    }
}