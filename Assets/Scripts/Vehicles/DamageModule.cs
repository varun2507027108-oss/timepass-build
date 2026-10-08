using System;
using UnityEngine;

namespace WarArena.Vehicles
{
    /// <summary>
    /// A damageable subsystem. Modules are what actually kills a tank: armour
    /// only delays the outcome, and a shot that defeats the front plate still
    /// has to reach something that matters.
    /// </summary>
    public enum ModuleKind
    {
        Engine,
        Transmission,
        TurretDrive,
        GunBreech,
        FuelTank,
        AmmoRack,
        Tracks,
        Optic,
        MachineGun
    }

    [Serializable]
    public class DamageModule
    {
        public string Name;
        public ModuleKind Kind;
        public float MaxHp = 100f;
        public float Hp;

        /// <summary>Hazard geometry. Used by the spall cone test, in local metres.</summary>
        public Vector3 LocalPosition;

        /// <summary>Blast radius for explosive damage, m. Zero for non-hazard modules.</summary>
        public float HazardRadius;

        /// <summary>Chance of catastrophic failure once destroyed, 0..1.</summary>
        public float FireChanceOnDestroy;

        /// <summary>Fraction of full effect remaining at the edge of its radius.</summary>
        public float EdgeDamageFloor = 0.35f;

        public bool IsDestroyed => Hp <= 0f;

        public DamageModule(string name, ModuleKind kind, Vector3 localPos, float maxHp, float hazardRadius)
        {
            Name = name;
            Kind = kind;
            LocalPosition = localPos;
            MaxHp = maxHp;
            Hp = maxHp;
            HazardRadius = hazardRadius;
        }

        /// <summary>
        /// Applies damage with linear falloff across the hazard radius.
        /// Returns true when this hit destroyed the module.
        /// </summary>
        public bool ApplyDamage(float amount, float distanceM)
        {
            if (IsDestroyed || amount <= 0f) return false;

            float factor;
            if (HazardRadius <= 0.001f)
            {
                factor = 1f;
            }
            else if (distanceM >= HazardRadius)
            {
                return false;
            }
            else
            {
                float t = 1f - (distanceM / HazardRadius);
                factor = Mathf.Lerp(EdgeDamageFloor, 1f, t);
            }

            Hp -= amount * factor;
            if (Hp < 0f) Hp = 0f;
            return IsDestroyed;
        }
    }

    /// <summary>
    /// Multi-crew vehicle. Crew are individually vulnerable, which is why a
    /// penetrating round is often lethal even when it misses every module: the
    /// spall cone catches whoever is sitting in it.
    /// </summary>
    [Serializable]
    public class CrewMember
    {
        public string Name;
        public Vector3 LocalPosition;
        public bool IsAlive = true;

        /// <summary>Seconds before a wounded crew member is available again.</summary>
        public float RecoveryTimer;

        public CrewMember(string name, Vector3 localPos)
        {
            Name = name;
            LocalPosition = localPos;
        }

        public bool IsAvailable => IsAlive && RecoveryTimer <= 0f;

        public void Wound()
        {
            IsAlive = false;
            RecoveryTimer = 6f;
        }

        public void Tick(float dt)
        {
            if (RecoveryTimer > 0f) RecoveryTimer -= dt;
        }
    }
}