using UnityEngine;

namespace WarArena.Gameplay
{
    /// <summary>
    /// PhysX-driven tracked vehicle movement component.
    ///
    /// Rather than ticking on Unity's standard FixedUpdate timer, simulation is explicitly
    /// triggered in lockstep with BattleSim via ApplyForces() followed by Physics.Simulate().
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class TrackedVehiclePhysics : MonoBehaviour
    {
        [Header("Inputs")]
        [Range(-1f, 1f)] public float throttle;
        [Range(-1f, 1f)] public float steer;

        [Header("Drivetrain")]
        public float maxSpeed = 11.11f; // ~40 km/h (MaxSpeedMps)
        public float engineForce = 45000f;
        public float brakeForce = 65000f;
        public float turnTorque = 150000f;
        public float lateralFrictionCoeff = 18000f;

        [Header("Suspension / Ground Contact")]
        public float raycastLength = 1.2f;
        public float trackWidth = 2.4f;
        public float trackLength = 4.2f;
        public LayerMask groundLayer = ~0;

        public Rigidbody rb { get; private set; }
        public bool IsGrounded { get; private set; }

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.mass = 28000f; // ~28 tonnes T-34-76
                rb.centerOfMass = new Vector3(0f, -0.4f, 0f);
                rb.linearDamping = 0.05f;
                rb.angularDamping = 4f;
                rb.isKinematic = true; // Kept kinematic while UsePhysicsMovement is false
            }
        }

        /// <summary>
        /// Enable or disable dynamic PhysX simulation for this vehicle.
        /// </summary>
        public void SetActive(bool active)
        {
            if (rb != null)
            {
                rb.isKinematic = !active;
            }
        }

        /// <summary>
        /// Explicit force calculation called by BattleSim immediately before Physics.Simulate().
        /// </summary>
        public void ApplyForces()
        {
            if (rb == null) return;

            // Sample ground contact across 4 track corners
            Vector3 center = rb.position;
            Vector3 fwd = transform.forward;
            Vector3 right = transform.right;
            Vector3 up = transform.up;

            float halfW = trackWidth * 0.5f;
            float halfL = trackLength * 0.5f;

            Vector3[] rayOrigins = new Vector3[]
            {
                center + fwd * halfL - right * halfW + up * 0.5f,
                center + fwd * halfL + right * halfW + up * 0.5f,
                center - fwd * halfL - right * halfW + up * 0.5f,
                center - fwd * halfL + right * halfW + up * 0.5f,
            };

            int groundContacts = 0;
            Vector3 avgNormal = Vector3.up;

            for (int i = 0; i < rayOrigins.Length; i++)
            {
                if (Physics.Raycast(rayOrigins[i], -up, out RaycastHit hit, raycastLength, groundLayer))
                {
                    groundContacts++;
                    avgNormal += hit.normal;
                }
            }

            IsGrounded = groundContacts > 0;
            if (!IsGrounded) return;

            avgNormal.Normalize();

            // Longitudinal velocity
            float forwardSpeed = Vector3.Dot(rb.linearVelocity, fwd);
            float targetSpeed = throttle * maxSpeed;

            if (Mathf.Abs(throttle) > 0.01f)
            {
                // Drive acceleration up to max speed
                if ((throttle > 0f && forwardSpeed < targetSpeed) || (throttle < 0f && forwardSpeed > targetSpeed))
                {
                    float forceMag = throttle * engineForce;
                    rb.AddForce(fwd * forceMag, ForceMode.Force);
                }
            }
            else
            {
                // Engine / coast braking when no throttle
                if (Mathf.Abs(forwardSpeed) > 0.1f)
                {
                    float brakeDir = -Mathf.Sign(forwardSpeed);
                    rb.AddForce(fwd * (brakeDir * brakeForce * 0.4f), ForceMode.Force);
                }
            }

            // Lateral skid friction: resist sideways slipping
            float lateralSpeed = Vector3.Dot(rb.linearVelocity, right);
            if (Mathf.Abs(lateralSpeed) > 0.01f)
            {
                float skidForce = -lateralSpeed * lateralFrictionCoeff;
                rb.AddForce(right * skidForce, ForceMode.Force);
            }

            // Differential steering torque
            if (Mathf.Abs(steer) > 0.01f)
            {
                rb.AddTorque(up * (steer * turnTorque), ForceMode.Force);
            }
        }
    }
}
