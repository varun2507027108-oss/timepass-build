using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using WarArena.Ballistics;
using WarArena.Gameplay;

namespace WarArena.Net
{
    /// <summary>
    /// Owns shell spawning and ballistics on the server, and replicates a cheap
    /// tracer to clients. Shells are server-only simulation: the client never
    /// decides whether it hit anything.
    /// </summary>
    public class ShellImpactManager : NetworkBehaviour
    {
        private struct ActiveShell
        {
            public GameObject Visual;
            public Vector3 Origin;
            public Vector3 Direction;
            public float DistanceTravelled;
            public float Range;
            public Shell Shell;
            public ulong OwnerId;
        }

        private readonly List<ActiveShell> _shells = new List<ActiveShell>();

        /// <summary>Spawned shells per second, server-side. Guards against spam.</summary>
        [SerializeField] private int maxActiveShells = 64;

        private static ShellImpactManager _instance;

        public override void OnNetworkSpawn()
        {
            _instance = this;
        }

        public static ShellImpactManager Instance => _instance;

        /// <summary>Server-only spawn. <paramref name="report"/> carries the resolved impact.</summary>
        public static void ServerSpawnShell(
            Vector3 origin, Vector3 direction, Shell shell, ulong ownerId, ImpactReport? report = null)
        {
            if (_instance == null || !_instance.IsServer) return;
            _instance.Spawn(origin, direction, shell, ownerId, report);
        }

        private void Spawn(Vector3 origin, Vector3 direction, Shell shell, ulong ownerId, ImpactReport? report)
        {
            if (_shells.Count >= maxActiveShells) return;

            GameObject visual = CreateTracer(shell);
            Vector3 dir = direction.normalized;

            _shells.Add(new ActiveShell
            {
                Visual = visual,
                Origin = origin,
                Direction = dir,
                DistanceTravelled = 0f,
                // ~1200 m is where tank duel ranges effectively end.
                Range = 1500f,
                Shell = shell,
                OwnerId = ownerId
            });

            if (report.HasValue)
            {
                // The authoritative damage was already applied by the shooter.
                // This is purely so late-joining clients can see the hit spark,
                // placed at the range the shell was about to reach.
                const float SparkRange = 1500f;
                SpawnImpactSpark(origin + dir * SparkRange * 0.999f, report.Value);
            }
        }

        private GameObject CreateTracer(Shell shell)
        {
            GameObject go = new GameObject("shell-tracer");
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = "tracer";
            Object.Destroy(visual.GetComponent<Collider>());
            visual.transform.SetParent(go.transform, false);
            visual.transform.localScale = Vector3.one * 0.35f;

            Renderer r = visual.GetComponent<Renderer>();
            r.material = shell.Type == ShellType.HEAT
                ? new Material(VisualMaterials.UnlitShader)
                { color = new Color(1f, 0.75f, 0.4f) }
                : new Material(VisualMaterials.UnlitShader)
                { color = new Color(1f, 0.85f, 0.5f) };

            go.transform.position = Vector3.zero;
            return go;
        }

        private void SpawnImpactSpark(Vector3 position, ImpactReport report)
        {
            GameObject spark = GameObject.CreatePrimitive(PrimitiveType.Quad);
            spark.name = "impact-spark";
            spark.transform.position = position;
            spark.transform.localScale = Vector3.one * 2.5f;
            spark.transform.LookAt(position + Vector3.up);

            Color c = report.Result == ImpactResult.Penetration
                ? new Color(1f, 0.6f, 0.25f)
                : report.Result == ImpactResult.Ricochet
                    ? new Color(0.8f, 0.85f, 1f)
                    : new Color(0.7f, 0.7f, 0.7f);

            spark.GetComponent<Renderer>().material =
                new Material(VisualMaterials.UnlitShader) { color = c };

            Destroy(spark, 0.35f);
        }

        private void Update()
        {
            if (!IsServer) return;

            float dt = Time.deltaTime;
            for (int i = _shells.Count - 1; i >= 0; i--)
            {
                ActiveShell s = _shells[i];
                // Velocity decays with distance, so flight time is not linear.
                float speed = s.Shell.VelocityAt(s.DistanceTravelled);
                float step = speed * dt;
                s.DistanceTravelled += step;
                s.Origin = s.Origin + s.Direction * step;

                Transform t = s.Visual != null ? s.Visual.transform : null;
                if (t != null) t.position = s.Origin;

                if (s.DistanceTravelled >= s.Range)
                {
                    if (s.Visual != null) Destroy(s.Visual);
                    _shells.RemoveAt(i);
                }
            }
        }
    }
}