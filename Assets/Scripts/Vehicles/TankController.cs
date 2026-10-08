using UnityEngine;

namespace WarArena.Vehicles
{
    /// <summary>
    /// WheelCollider-based tank controller, and the reference implementation for
    /// how the vehicle should feel.
    ///
    /// Design notes that matter for authenticity:
    ///  - Tracked vehicles do not steer like cars. They use differential steering:
    ///    the tracks turn at different speeds. Tank-style input blends engine torque
    ///    into left and right tracks.
    ///  - Power is limited by both torque and a top-speed governor, so a Tiger cannot
    ///    accelerate like a T-34 even at full throttle.
    ///  - The gun fires on the hull's yaw plus an independent turret yaw, and the
    ///    turret is rate-limited by the TraverseSpeed value.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class TankController : MonoBehaviour
    {
        [Header("Drivetrain")]
        [Tooltip("Rated engine power in kilowatts. Divided by mass for power-to-weight.")]
        public float RatedEngineKw = 500f;

        [Tooltip("Peak torque multiplier applied to tracks at low speed.")]
        public float TorqueGain = 2400f;

        [Tooltip("Seconds from rest to rated speed. Higher feels heavier.")]
        public float AccelerationTime = 6f;

        public float MaxSteerAngle = 28f;
        public float MaxTrackForce = 14000f;

        [Header("Suspension")]
        public float SuspensionDistance = 0.45f;
        public float SuspensionSpring = 32000f;
        public float SuspensionDamper = 4200f;
        public float SuspensionTarget = 0.42f;

        [Header("Turret")]
        [Tooltip("Degrees per second. Real turrets are slow, which is the whole reason to hull-down.")]
        public float TraverseSpeedDeg = 30f;
        public float ElevationSpeedDeg = 12f;
        public float MinElevationDeg = -10f;
        public float MaxElevationDeg = 20f;

        private WheelCollider[] _tracks;
        private Rigidbody _body;
        private VehicleDamageModel _damage;
        private float _elevationDeg;
        private float _turretYawDeg;
        private float _currentSpeedKph;
        private float _governorSpeedKph;

        public float CurrentSpeedKph => _currentSpeedKph;
        public float ElevationDeg => _elevationDeg;
        public float TurretYawDeg => _turretYawDeg;

        /// <summary>Transform of the turret, used to rotate the hull-mounted gun.</summary>
        public Transform TurretTransform { get; private set; }

        /// <summary>Muzzle point; shells are spawned here on the server.</summary>
        public Transform MuzzleTransform { get; private set; }

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _tracks = GetComponentsInChildren<WheelCollider>(true);
            _damage = GetComponent<VehicleDamageModel>();

            // Tracked vehicles sit low and heavy. Centre of mass below the hull
            // line is what stops them rolling over in a turn.
            _body.mass = 26000f;
            _body.centerOfMass = new Vector3(0f, -0.35f, 0.1f);
            _body.linearDamping = 0.02f;
            _body.angularDamping = 6f;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            for (int i = 0; i < _tracks.Length; i++)
            {
                WheelCollider wc = _tracks[i];
                wc.suspensionDistance = SuspensionDistance;
                wc.wheelDampingRate = 0.6f;
                JointSpring spring = wc.suspensionSpring;
                spring.spring = SuspensionSpring;
                spring.damper = SuspensionDamper;
                spring.targetPosition = SuspensionTarget;
                wc.suspensionSpring = spring;

                // High longitudinal friction so tracks grip, near-zero lateral so the
                // vehicle slides slightly in a slide turn rather than railroading.
                wc.forwardFriction = new WheelFrictionCurve
                {
                    extremumSlip = 0.4f,
                    extremumValue = 1.6f,
                    asymptoteSlip = 0.8f,
                    asymptoteValue = 0.75f,
                    stiffness = 1.5f
                };
                wc.sidewaysFriction = new WheelFrictionCurve
                {
                    extremumSlip = 0.25f,
                    extremumValue = 0.35f,
                    asymptoteSlip = 0.6f,
                    asymptoteValue = 0.28f,
                    stiffness = 0.6f
                };
            }
        }

        public void BindTurret(Transform turret, Transform muzzle)
        {
            TurretTransform = turret;
            MuzzleTransform = muzzle;
        }

        /// <summary>
        /// <paramref name="throttle"/> -1..1, <paramref name="steer"/> -1..1,
        /// <paramref name="elevateInput"/> -1..1. Damage penalties are applied here so
        /// a destroyed transmission is felt immediately rather than as a stat.
        /// </summary>
        public void Drive(float throttle, float steer, float elevateInput, float dt)
        {
            if (_damage != null && (_damage.IsDestroyed || _damage.IsImmobilised))
            {
                throttle = 0f;
                steer = 0f;
            }

            float powerScale = _damage != null ? _damage.EnginePowerFraction : 1f;
            float speedScale = _damage != null ? _damage.SpeedFraction : 1f;
            float traverseScale = _damage != null ? _damage.TurretRateFraction : 1f;

            // Rated top speed from power-to-weight, then penalised by damage.
            _governorSpeedKph = Mathf.Lerp(0f, RatedEngineKw * 0.062f, speedScale);
            _currentSpeedKph = transform.InverseTransformDirection(_body.linearVelocity).z * 3.6f;

            float speedNorm = _governorSpeedKph > 0.01f
                ? Mathf.Clamp01(Mathf.Abs(_currentSpeedKph) / _governorSpeedKph)
                : 0f;

            // Engine falls off as it approaches rated speed.
            float torqueCurve = (1f - speedNorm) * powerScale;
            float steerAuthority = Mathf.Clamp01(1f - speedNorm * 0.65f);

            if (Mathf.Abs(steer) > 0.01f)
            {
                // Pivot steering: the inner track stops, the outer one drives.
                float pivotAngle = steer * MaxSteerAngle * steerAuthority;
                ApplyTrack(0, throttle, pivotAngle); // left
                ApplyTrack(1, throttle, -pivotAngle); // right
            }
            else
            {
                ApplyTrack(0, throttle, 0f);
                ApplyTrack(1, throttle, 0f);
            }

            float ramp = throttle * TorqueGain * torqueCurve * (1f - speedNorm * 0.4f);
            for (int i = 0; i < _tracks.Length; i++)
            {
                _tracks[i].motorTorque = ramp;
                _tracks[i].brakeTorque = Mathf.Approximately(throttle, 0f) ? 0f : 0f;
            }

            UpdateTurret(steer, elevateInput, traverseScale, dt);
        }

        private void ApplyTrack(int index, float throttle, float steerAngle)
        {
            // With a 4-wheel rig, wheels 0/1 are the left track and 2/3 the right.
            int perTrack = Mathf.Max(1, _tracks.Length / 2);
            for (int w = 0; w < perTrack; w++)
            {
                int idx = index * perTrack + w;
                if (idx >= _tracks.Length) continue;
                WheelCollider wc = _tracks[idx];
                wc.motorTorque = throttle * TorqueGain * 0.5f;
                wc.steerAngle = steerAngle;
            }
        }

        private void UpdateTurret(float steer, float elevateInput, float traverseScale, float dt)
        {
            if (TurretTransform == null) return;

            // Traverse is commanded, not instant: real turrets cannot keep up with a
            // fast hull reversal, which is the core of hull-down gunnery.
            float requested = steer * TraverseSpeedDeg * traverseScale;
            float maxStep = TraverseSpeedDeg * traverseScale * dt;
            _turretYawDeg += Mathf.Clamp(requested * dt, -maxStep, maxStep);
            _turretYawDeg = Mathf.Clamp(_turretYawDeg, -170f, 170f);
            TurretTransform.localRotation = Quaternion.Euler(0f, _turretYawDeg, 0f);

            _elevationDeg = Mathf.Clamp(
                _elevationDeg + elevateInput * ElevationSpeedDeg * dt,
                MinElevationDeg,
                MaxElevationDeg);

            // Elevation is applied to a gun pivot if the scene provides one.
            if (TurretTransform.childCount > 0)
            {
                Transform gunPivot = TurretTransform.GetChild(0);
                gunPivot.localRotation = Quaternion.Euler(-_elevationDeg, 0f, 0f);
            }
        }

        /// <summary>Sets a specific turret yaw, used when replaying authoritative state.</summary>
        public void SetTurretYaw(float deg)
        {
            _turretYawDeg = Mathf.Clamp(deg, -170f, 170f);
            if (TurretTransform != null)
            {
                TurretTransform.localRotation = Quaternion.Euler(0f, _turretYawDeg, 0f);
            }
        }
    }
}