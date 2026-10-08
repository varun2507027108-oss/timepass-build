using System;
using Unity.Netcode;
using UnityEngine;
using WarArena.Ballistics;
using WarArena.Vehicles;

namespace WarArena.Net
{
    /// <summary>
    /// Server-authoritative vehicle. Clients send an input intent; the server owns
    /// physics, ballistics and damage. Clients only interpolate transforms.
    ///
    /// Rationale: a competitive vehicle game cannot trust client physics, because
    /// WheelCollider state is trivially manipulable and a client reporting its own
    /// hit results is a trivial cheat. Server authority costs some latency, which
    /// for 30-40 km/h vehicles at 600 m engagement ranges is entirely acceptable.
    /// </summary>
    public class NetworkVehicle : NetworkBehaviour
    {
        /// <summary>Compressed per-tick input. 4 bytes of intent per vehicle per tick.</summary>
        [Serializable]
        public struct VehicleInput
        {
            public short Seq;
            public sbyte Throttle;   // -100..100
            public sbyte Steer;      // -100..100
            public sbyte Elevate;    // -100..100
            public bool Fire;

            public static VehicleInput Empty => new VehicleInput { Seq = 0 };

            public float ThrottleF => Throttle / 100f;
            public float SteerF => Steer / 100f;
            public float ElevateF => Elevate / 100f;
        }

        [Header("Identity")]
        public string VehicleId = "t34-76";

        [Header("Weapon")]
        public float ReloadSeconds = 7.1f;
        public Shell LoadedShell;

        [Header("References")]
        public TankController Controller;
        public VehicleDamageModel Damage;
        public Transform ShellSpawn;

        // Replicated state. These are what the client is allowed to know.
        private readonly NetworkVariable<float> _networkSpeed = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<short> _networkHull = new NetworkVariable<short>(
            1000, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<bool> _networkOnFire = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<ulong> _networkKillId = new NetworkVariable<ulong>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private VehicleInput _pendingInput;
        private float _reloadTimer;
        private bool _hasPendingInput;

        public override void OnNetworkSpawn()
        {
            // Tick rate is NOT settable at runtime -- NetworkTickSystem.TickRate is
            // read-only in Netcode for GameObjects 2.x. It comes from the
            // NetworkManager's NetworkConfig (Project Settings > Multiplayer, or
            // NetworkManager.TickRate in a bootstrap script). 30 Hz is right for
            // vehicles: they are slow, so the extra bandwidth of 60 buys nothing
            // and the extra authority latency is still irrelevant at 600 m.
        }

        /// <summary>Called on the server from the player's input handler.</summary>
        public void SubmitInput(in VehicleInput input)
        {
            if (!IsServer) return;
            _pendingInput = input;
            _hasPendingInput = true;
        }

        /// <summary>
        /// Server-side simulation step. Kept separate from FixedUpdate so it runs on
        /// the network tick and stays deterministic for a given input stream.
        /// </summary>
        public void ServerTick(float dt)
        {
            if (!IsServer || Controller == null) return;

            float throttle = _hasPendingInput ? _pendingInput.ThrottleF : 0f;
            float steer = _hasPendingInput ? _pendingInput.SteerF : 0f;
            float elevate = _hasPendingInput ? _pendingInput.ElevateF : 0f;

            Controller.Drive(throttle, steer, elevate, dt);

            if (Damage != null)
            {
                Damage.Tick(dt);
                _networkOnFire.Value = Damage.IsOnFire;
            }

            _networkSpeed.Value = Controller.CurrentSpeedKph;

            if (_reloadTimer > 0f) _reloadTimer -= dt;

            if (_hasPendingInput && _pendingInput.Fire && _reloadTimer <= 0f && CanFire())
            {
                FireShellServer();
                _reloadTimer = ReloadSeconds;
            }

            _hasPendingInput = false;
        }

        private bool CanFire()
        {
            if (Damage != null && (Damage.IsDestroyed || !Damage.CanFireGun)) return false;
            return true;
        }

        /// <summary>
        /// Spawns the shell and resolves it server-side immediately in a straight
        /// line. For the full solution, shells need a swept collision test against
        /// terrain plus vehicles along the trajectory, and lag-compensated rewinding
        /// so the shooter is judged against where the target was when the shot was
        /// taken rather than where it is now.
        /// </summary>
        private void FireShellServer()
        {
            if (ShellSpawn == null || Damage == null) return;

            Vector3 origin = ShellSpawn.position;
            Vector3 dir = ShellSpawn.forward;

            // Sweep against vehicles in range. Terrain occlusion is the next step.
            NetworkVehicle[] all = FindObjectsByType<NetworkVehicle>(FindObjectsSortMode.None);
            NetworkVehicle target = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < all.Length; i++)
            {
                NetworkVehicle v = all[i];
                if (v == this || v.Damage == null || v.Damage.IsDestroyed) continue;

                float d = Vector3.Distance(origin, v.transform.position);
                if (d > 1200f || d >= bestDist) continue;

                Vector3 toTarget = (v.transform.position - origin).normalized;
                float alignment = Vector3.Dot(dir, toTarget);
                // ~6 m half-width at 1000 m is roughly the hull width at range.
                float angular = Mathf.Asin(Mathf.Clamp(1f - alignment, -1f, 1f));
                float spread = d * angular;
                if (spread < 3.0f)
                {
                    bestDist = d;
                    target = v;
                }
            }

            if (target == null)
            {
                ShellImpactManager.ServerSpawnShell(origin, dir, LoadedShell, NetworkObjectId);
                return;
            }

            Vector3 impactPoint = target.transform.position + new Vector3(0f, 1.0f, 0f);
            Vector3 travel = impactPoint - origin;

            // Angle between the incoming round and the target's facing. This is the
            // number that decides penetration, so it is computed from the server's
            // authoritative positions rather than anything the client sent.
            Vector3 targetForward = target.transform.forward;
            Vector3 incoming = travel.normalized;
            float dot = Mathf.Clamp(Vector3.Dot(-incoming, targetForward), -1f, 1f);
            float impactAngleDeg = Mathf.Acos(dot) * Mathf.Rad2Deg;

            ImpactReport report = target.Damage.ApplyShellImpact(
                LoadedShell,
                "front",
                impactAngleDeg,
                impactPoint,
                target.transform.InverseTransformDirection(incoming),
                target.transform);

            _networkHull.Value = (short)(1000f * Mathf.Clamp01(
                (target.Damage.AliveCrewCount / 4f)));
            if (target.Damage.IsDestroyed)
            {
                _networkKillId.Value = NetworkObjectId;
            }

            ShellImpactManager.ServerSpawnShell(origin, travel.normalized, LoadedShell, NetworkObjectId, report);
        }

        public float ReplicatedSpeedKph => _networkSpeed.Value;
        public bool ReplicatedOnFire => _networkOnFire.Value;
    }
}