using System;
using System.Collections.Generic;
using UnityEngine;
using WarArena.Ballistics;

namespace WarArena.Vehicles
{
    /// <summary>
    /// Authoritative damage resolution for a vehicle. The server owns this and
    /// replicates only the resulting HP/fire flags, never the per-module values,
    /// so clients cannot reverse-engineer friendly armour layouts.
    /// </summary>
    public class VehicleDamageModel
    {
        public readonly List<DamageModule> Modules = new List<DamageModule>();
        public readonly List<CrewMember> Crew = new List<CrewMember>();

        /// <summary>Local-space armour geometry. Keyed by plate name.</summary>
        private readonly Dictionary<string, ArmourPlate> _plates =
            new Dictionary<string, ArmourPlate>(StringComparer.Ordinal);

        /// <summary>Set when any module is burning; drives smoke VFX and attrition.</summary>
        public bool IsOnFire;

        /// <summary>Set when the tracks are gone and the vehicle cannot move.</summary>
        public bool IsImmobilised;

        public bool IsDestroyed;

        /// <summary>Fraction of rated engine power still available, 0..1.</summary>
        public float EnginePowerFraction = 1f;

        /// <summary>Fraction of rated top speed still available, 0..1.</summary>
        public float SpeedFraction = 1f;

        /// <summary>Current turret traverse rate multiplier, 0..1.</summary>
        public float TurretRateFraction = 1f;

        /// <summary>Whether the main gun can currently be fired.</summary>
        public bool CanFireGun => !IsDestroyed && FindModule(ModuleKind.GunBreech) is { IsDestroyed: false };

        public void AddPlate(ArmourPlate plate)
        {
            _plates[plate.Name] = plate;
        }

        public ArmourPlate GetPlate(string name)
        {
            return _plates.TryGetValue(name, out ArmourPlate plate)
                ? plate
                : new ArmourPlate("unknown", 0f);
        }

        public bool HasPlate(string name) => _plates.ContainsKey(name);

        public DamageModule FindModule(ModuleKind kind)
        {
            for (int i = 0; i < Modules.Count; i++)
            {
                if (Modules[i].Kind == kind) return Modules[i];
            }
            return null;
        }

        public int AliveCrewCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Crew.Count; i++) if (Crew[i].IsAlive) n++;
                return n;
            }
        }

        /// <summary>
        /// Applies a shell impact. <paramref name="worldImpactPoint"/> is used to
        /// compute per-module distance; <paramref name="localImpactDirection"/> is
        /// the world-space direction the round was travelling, already converted
        /// into the vehicle's local frame so the spall cone can be tested properly.
        /// </summary>
        public ImpactReport ApplyShellImpact(
            Shell shell,
            string plateName,
            float impactAngleDeg,
            Vector3 worldImpactPoint,
            Vector3 localImpactDirection,
            Transform vehicleTransform)
        {
            ArmourPlate plate = GetPlate(plateName);
            ImpactReport report = Penetration.Resolve(shell, plate, impactAngleDeg);

            if (report.Result == ImpactResult.Ricochet)
            {
                return report;
            }

            Vector3 localPoint = vehicleTransform != null
                ? vehicleTransform.InverseTransformPoint(worldImpactPoint)
                : worldImpactPoint;

            if (report.Result == ImpactResult.NonPenetration || shell.Type == ShellType.HE)
            {
                // HE has no penetration figure, so it is handled separately: either it
                // breaches the plate and bursts inside the hull, or it bursts against
                // the outside and only fragments matter. Which of those happens is the
                // entire difference between a HE round that kills a light tank and one
                // that merely spalls paint.
                if (shell.Type == ShellType.HE && shell.BreachesPlate(report.EffectiveArmorMm))
                {
                    float heBurstDepth = Mathf.Max(0.3f, shell.CaliberMm * 0.008f);
                    Vector3 heBurstPoint = localPoint + localImpactDirection * heBurstDepth;
                    ApplyBlast(shell, heBurstPoint, localImpactDirection, vehicleTransform, report.KineticEnergyJ);
                }
                else
                {
                    ApplyBlast(shell, localPoint, localImpactDirection, vehicleTransform, report.KineticEnergyJ);
                }
                return report;
            }

            // Penetrated. A round that gets through does not detonate against the
            // outer plate -- it detonates INSIDE the fighting compartment, one fuse
            // depth further along its path. Getting this wrong is why penetrating
            // rounds fail to kill anyone, and crew survival is the whole point of
            // modelling spall and fragmentation separately.
            float fuseDepth = shell.FuseDepthM > 0f ? shell.FuseDepthM : 0.5f;
            Vector3 burstPoint = localPoint + localImpactDirection * fuseDepth;

            if (report.CreatedSpallCone)
            {
                ApplySpallCone(report.SpallConeDeg, burstPoint, localImpactDirection, shell);
            }

            float remainingEnergy = report.KineticEnergyJ;
            if (shell.ExplosiveMassKg > 0.01f)
            {
                // A shell that penetrates and then cooks off is the worst case.
                ApplyBlast(shell, burstPoint, localImpactDirection, vehicleTransform, remainingEnergy);
                remainingEnergy = 0f;
            }

            if (remainingEnergy > 0f)
            {
                ApplyLineDamage(shell, localPoint, localImpactDirection, vehicleTransform, remainingEnergy);
            }

            RecomputeFlags();
            return report;
        }

        /// <summary>Damage along the round's path. First hard module hit takes the energy.</summary>
        private void ApplyLineDamage(
            Shell shell, Vector3 localPoint, Vector3 dir, Transform t, float energy)
        {
            if (energy <= 0f) return;

            DamageModule best = null;
            float bestDist = float.MaxValue;
            Vector3 bestWorld = Vector3.zero;

            for (int i = 0; i < Modules.Count; i++)
            {
                DamageModule m = Modules[i];
                if (m.IsDestroyed) continue;
                Vector3 world = t != null ? t.TransformPoint(m.LocalPosition) : m.LocalPosition;
                float d = Vector3.Distance(world, t != null ? t.TransformPoint(localPoint) : localPoint);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = m;
                    bestWorld = world;
                }
            }

            if (best == null) return;

            // Energy scales into a flat damage number via a mass-normalised factor;
            // big shells simply carry more joules.
            float damage = energy / 800f;
            if (best.ApplyDamage(damage, bestDist))
            {
                OnModuleDestroyed(best);
            }
        }

        /// <summary>
        /// Fragmentation from an HE/APHE round. Scales with explosive filler, not
        /// muzzle energy, which is why a 75 mm HE is devastating to a 16 mm FT.
        /// </summary>
        private void ApplyBlast(
            Shell shell, Vector3 localPoint, Vector3 dir, Transform t, float kineticEnergy)
        {
            if (shell.ExplosiveMassKg <= 0f) return;

            // TNT-equivalent damage falls off with the cube of the calibre ratio.
            float blastRadius = Mathf.Clamp(shell.CaliberMm * 0.022f, 0.4f, 3.2f);
            float baseDamage = shell.ExplosiveMassKg * 95f + kineticEnergy / 500f;

            Vector3 worldPoint = t != null ? t.TransformPoint(localPoint) : localPoint;

            for (int i = 0; i < Modules.Count; i++)
            {
                DamageModule m = Modules[i];
                if (m.IsDestroyed) continue;
                Vector3 world = t != null ? t.TransformPoint(m.LocalPosition) : m.LocalPosition;
                float d = Vector3.Distance(world, worldPoint);
                float reach = Mathf.Max(blastRadius, m.HazardRadius);
                if (d >= reach) continue;

                float t01 = 1f - (d / reach);
                float damage = baseDamage * Mathf.Lerp(m.EdgeDamageFloor, 1f, t01);
                if (m.ApplyDamage(damage, d)) OnModuleDestroyed(m);
            }

            // Crew caught in the blast.
            for (int i = 0; i < Crew.Count; i++)
            {
                CrewMember c = Crew[i];
                if (!c.IsAlive) continue;
                Vector3 world = t != null ? t.TransformPoint(c.LocalPosition) : c.LocalPosition;
                if (Vector3.Distance(world, worldPoint) <= blastRadius) c.Wound();
            }

            // Armour rack: a direct hit cooks off ammunition with very high odds,
            // which is how the large majority of tanks die.
            DamageModule ammo = FindModule(ModuleKind.AmmoRack);
            if (ammo != null && !ammo.IsDestroyed)
            {
                Vector3 world = t != null ? t.TransformPoint(ammo.LocalPosition) : ammo.LocalPosition;
                if (Vector3.Distance(world, worldPoint) <= blastRadius)
                {
                    ammo.ApplyDamage(baseDamage * 3f, blastRadius);
                    OnModuleDestroyed(ammo);
                }
            }
        }

        /// <summary>
        /// Spall cone: fragments fan out inside the fighting compartment. This is
        /// what actually kills crew and light modules behind a defeated plate.
        /// </summary>
        private void ApplySpallCone(
            float coneHalfAngleDeg, Vector3 localPoint, Vector3 dir, Shell shell)
        {
            float coneCos = Mathf.Cos(coneHalfAngleDeg * Mathf.Deg2Rad);
            Vector3 worldPointOrigin = localPoint;

            for (int i = 0; i < Modules.Count; i++)
            {
                DamageModule m = Modules[i];
                if (m.IsDestroyed) continue;
                Vector3 toModule = m.LocalPosition - worldPointOrigin;
                float len = toModule.magnitude;
                if (len < 0.001f) continue;
                float alignment = Vector3.Dot(toModule / len, dir);
                if (alignment < coneCos) continue;

                float damage = shell.MassKg * 12f * alignment;
                if (m.ApplyDamage(damage, 0f)) OnModuleDestroyed(m);
            }

            for (int i = 0; i < Crew.Count; i++)
            {
                CrewMember c = Crew[i];
                if (!c.IsAlive) continue;
                Vector3 toCrew = c.LocalPosition - worldPointOrigin;
                float len = toCrew.magnitude;
                if (len < 0.001f) continue;
                if (Vector3.Dot(toCrew / len, dir) >= coneCos) c.Wound();
            }
        }

        private void OnModuleDestroyed(DamageModule m)
        {
            if (m.Kind == ModuleKind.AmmoRack)
            {
                // Detonation. Unsurvivable for the crew in almost every case.
                IsDestroyed = true;
                for (int i = 0; i < Crew.Count; i++) Crew[i].Wound();
                return;
            }

            if (m.Kind == ModuleKind.Engine && UnityEngine.Random.value < m.FireChanceOnDestroy)
            {
                IsOnFire = true;
            }
        }

        /// <summary>Recomputes the derived movement penalties from module state.</summary>
        public void RecomputeFlags()
        {
            DamageModule engine = FindModule(ModuleKind.Engine);
            DamageModule trans = FindModule(ModuleKind.Transmission);
            DamageModule turret = FindModule(ModuleKind.TurretDrive);
            DamageModule tracks = FindModule(ModuleKind.Tracks);
            DamageModule breech = FindModule(ModuleKind.GunBreech);

            EnginePowerFraction = engine != null && !engine.IsDestroyed ? engine.Hp / engine.MaxHp : 0f;
            SpeedFraction = trans != null && !trans.IsDestroyed ? trans.Hp / trans.MaxHp : 0f;
            TurretRateFraction = turret != null && !turret.IsDestroyed ? turret.Hp / turret.MaxHp : 0f;
            IsImmobilised = (tracks != null && tracks.IsDestroyed) || (trans != null && trans.IsDestroyed);

            if (AliveCrewCount == 0) IsDestroyed = true;

            // A destroyed breech only disables the gun, which is already handled by
            // CanFireGun. It does not by itself kill the vehicle -- a mobility kill
            // on the crew or the engine does that.
        }

        public void Tick(float dt)
        {
            for (int i = 0; i < Crew.Count; i++) Crew[i].Tick(dt);

            if (IsOnFire)
            {
                // Attrition: fire eventually cooks the vehicle off.
                for (int i = 0; i < Modules.Count; i++)
                {
                    DamageModule m = Modules[i];
                    if (!m.IsDestroyed) m.ApplyDamage(4f * dt, 0f);
                }
                if (EnginePowerFraction <= 0f && AliveCrewCount <= 0) IsDestroyed = true;
            }

            RecomputeFlags();
        }
    }
}