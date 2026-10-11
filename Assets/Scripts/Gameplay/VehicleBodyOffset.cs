using UnityEngine;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Derives a visual hull transform from an authoritative PhysX transform.
    ///
    /// This is the counterpart to <c>SuspensionController</c> in both reference
    /// applications, and it exists for a specific reason worth stating plainly.
    ///
    /// In both XDEVS titles the PhysX transform is authoritative and the visual body
    /// is a SEPARATE transform whose offset from it is computed each frame.
    /// <c>SuspensionController</c> holds a <c>suspension</c> Transform and a
    /// <c>body</c> Transform, keeps <c>dampValue</c>, <c>dampSpeed</c> and
    /// <c>pullSpeed</c>, and updates them in
    /// <c>AnimateWheels(dampLength, dampSpeed, velocityMagnitude, velocity)</c>.
    ///
    /// The consequence is that the renderer cannot disagree with the simulation about
    /// where the vehicle is, because it is not making its own decision -- it is
    /// reading the authoritative transform and applying a bounded cosmetic offset on
    /// top. That separation is what this project's earlier suspension lacked: it
    /// integrated a height that the renderer then displayed directly, so every
    /// integrator error became a visible placement error.
    ///
    /// The offset applied here is deliberately SMALL and CLAMPED. It is a
    /// cosmetic weight-transfer lean, not a simulation term. It cannot push the
    /// hull into terrain because it is bounded by <see cref="maxOffsetM"/>, and it
    /// cannot lift the hull clear of the ground to fake a ride height, because it
    /// only ever moves the body DOWN from where PhysX put it plus a little.
    /// </summary>
    [RequireComponent(typeof(TrackedVehicle))]
    public class VehicleBodyOffset : MonoBehaviour
    {
        [Header("Cosmetic body response")]
        [Tooltip("How strongly the body leans under acceleration. Expressed as a " +
                 "target offset in metres at full lateral load.")]
        public float maxOffsetM = 0.12f;

        [Tooltip("Smoothing time constant for the lean, seconds. Short enough to " +
                 "read as weight transfer, long enough not to buzz.")]
        public float smoothingSeconds = 0.15f;

        [Tooltip("Vertical drop applied when the vehicle lands, metres at full " +
                 "impact. Pinned to the PhysX transform so it can never sink.")]
        public float maxSquashM = 0.08f;

        private TrackedVehicle _vehicle;
        private Transform _body;
        private Vector3 _offset;
        private float _squash;
        private float _lastVerticalVelocity;

        /// <summary>
        /// Attach the cosmetic body transform. This is the transform that gets the
        /// lean; the vehicle's own transform stays authoritative and untouched.
        /// </summary>
        public void Bind(Transform body)
        {
            _body = body;
            _vehicle = GetComponent<TrackedVehicle>();
        }

        /// <summary>
        /// Update the cosmetic offset. Called once per RENDER frame, after the
        /// simulation step, and never writes to the Rigidbody.
        /// </summary>
        public void UpdateVisual(float dt, float lateralAccel, float longitudinalAccel)
        {
            if (_vehicle == null || _vehicle.Body == null) return;

            Rigidbody rb = _vehicle.Body;

            // Landing squash, from the change in vertical velocity frame to frame.
            // Uses the Rigidbody's own vertical velocity rather than anything derived
            // here, so the source of truth is still PhysX.
            float vertical = rb.linearVelocity.y;
            float dv = vertical - _lastVerticalVelocity;
            _lastVerticalVelocity = vertical;

            float targetSquash = Mathf.Clamp(dv * 0.02f, -maxSquashM, maxSquashM);

            // Lean into the load. Lateral acceleration rolls the body outward,
            // longitudinal pitches it: nose-up under power, nose-down under braking.
            Vector3 target = new Vector3(
                Mathf.Clamp(-lateralAccel * 0.004f, -maxOffsetM, maxOffsetM),
                0f,
                Mathf.Clamp(-longitudinalAccel * 0.004f, -maxOffsetM, maxOffsetM));

            float k = smoothingSeconds <= 1e-4f ? 1f : 1f - Mathf.Exp(-dt / smoothingSeconds);
            _offset = Vector3.Lerp(_offset, target, k);
            _squash += (targetSquash - _squash) * k;

            if (_body == null) return;

            // The visual body copies the authoritative transform and then has this
            // bounded offset applied. It never computes a position of its own, which
            // is the entire point.
            _body.SetPositionAndRotation(
                transform.position + _offset - Vector3.up * _squash,
                transform.rotation);
        }
    }
}