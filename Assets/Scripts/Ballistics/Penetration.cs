using System;
using UnityEngine;

namespace WarArena.Ballistics
{
    public enum ImpactResult
    {
        /// <summary>Round passed through the plate and continues with reduced energy.</summary>
        Penetration,

        /// <summary>Plate held. A bouncing round detonates here instead of passing on.</summary>
        NonPenetration,

        /// <summary>Impact too oblique, over the critical angle. Round skips away.</summary>
        Ricochet,

        /// <summary>Round passed a plate that could not stop it; overkill.</summary>
        OverPenetration
    }

    /// <summary>
    /// One armour plate in the hit path. Thickness is at the plate normal, so the
    /// sloped glacis of a Panther is modelled as thick plate presented at an angle
    /// rather than as a thin plate plus a fudge factor.
    /// </summary>
    [Serializable]
    public struct ArmourPlate
    {
        public string Name;

        /// <summary>Plate thickness perpendicular to its own normal, mm of RHA.</summary>
        public float ThicknessMm;

        /// <summary>Structural integrity before the plate itself fails, per hit.</summary>
        public float StructuralIntegrity;

        /// <summary>True for cast hulls, which spall differently from rolled plate.</summary>
        public bool IsCast;

        /// <summary>Spall cone half-angle in degrees. 76 mm AP throws roughly 15.</summary>
        public float SpallConeDeg;

        public ArmourPlate(string name, float thicknessMm, bool isCast = false)
        {
            Name = name;
            ThicknessMm = thicknessMm;
            StructuralIntegrity = 1f;
            IsCast = isCast;
            SpallConeDeg = 15f;
        }

        /// <summary>
        /// Relative resistance of the plate's material against rolled homogeneous
        /// armour, which is the unit all published thicknesses are quoted in.
        ///
        /// Cast homogeneous armour is harder and resists perforation better than
        /// rolled plate of the same nominal thickness, so it is modelled as slightly
        /// thicker. This is a modest effect next to slope, but it is the reason a
        /// Panther's cast hull and its rolled front are not interchangeable, and why
        /// sloped rolled plate defeats nearly everything a flat cast plate stops.
        /// </summary>
        public float MaterialCoefficient => IsCast ? 1.10f : 1.0f;

        /// <summary>
        /// Effective thickness presented to an incoming round at
        /// <paramref name="impactAngleDeg"/> away from the plate normal, against pure
        /// geometry with no shell involved.
        ///
        /// Use this only when you genuinely have no ordnance in hand. Every gameplay
        /// path should call the overload that takes a shell, because normalisation
        /// and the shaped-charge exception both belong here.
        /// </summary>
        public float EffectiveThickness(float impactAngleDeg)
        {
            return ThicknessMm * MaterialCoefficient * Shell.SlopeMultiplier(impactAngleDeg);
        }

        /// <summary>
        /// Effective thickness this plate presents to a specific round.
        ///
        /// Normalisation belongs on the plate, not on the projectile, and that is the
        /// subtle part. The round's capability does not change when its nose bites in;
        /// what changes is how much plate the round has to chew through. A blunt
        /// APBC nose rotating toward the normal means the plate is met at a less
        /// oblique angle than the firing line suggested, so it presents less.
        ///
        /// Applying it here rather than in <see cref="Shell.PenetrationAt"/> also
        /// means a single plate resolves correctly against the whole roster without
        /// any per-shell special case at the call sites, and it keeps the two
        /// quantities separable: one is what the shell can do, the other is what the
        /// armour makes it do.
        /// </summary>
        public float EffectiveThickness(Shell shell, float impactAngleDeg)
        {
            // A shaped-charge jet collapses onto the plate normal however oblique the
            // impact, so slope costs it nothing at all.
            if (shell.IgnoresSlope) return ThicknessMm * MaterialCoefficient;

            float angle = Mathf.Abs(impactAngleDeg);

            // Clamped at zero so a denormalising round cannot be handed *less*
            // effective thickness than face-on geometry, which would be a straight
            // inversion of the physics.
            float effectiveAngle = Mathf.Max(0f, angle - shell.NormalisationDeg);

            return ThicknessMm * MaterialCoefficient * Shell.SlopeMultiplier(effectiveAngle);
        }
    }

    public struct ImpactReport
    {
        public ImpactResult Result;
        public float ImpactAngleDeg;
        public float EffectiveArmorMm;
        public float ShellPenetrationMm;

        /// <summary>Residual penetration after eating the plate. Drives over-penetration.</summary>
        public float ResidualPenetrationMm;

        /// <summary>Kinetic energy delivered to modules behind the plate, joules.</summary>
        public float KineticEnergyJ;

        /// <summary>True when the round went through and a spall cone is created.</summary>
        public bool CreatedSpallCone;

        /// <summary>Spall cone half-angle, degrees. 0 when no cone.</summary>
        public float SpallConeDeg;
    }

    /// <summary>
    /// Resolves one plate hit. Stateless and side-effect free so it is safe to run
    /// on the server and replay identically for hit-regret corrections.
    /// </summary>
    public static class Penetration
    {
        /// <summary>
        /// Fallback critical ricochet angle in degrees, used only by callers that
        /// have no shell to hand.
        ///
        /// <see cref="Shell.CriticalRicochetDeg"/> is the real answer and every
        /// gameplay path should use it. This constant remains as a conservative
        /// default so that a shell-less call site cannot accidentally inherit the
        /// most permissive threshold in the roster.
        /// </summary>
        public const float CriticalRicochetAngleDeg = 65f;

        /// <summary>
        /// Damage multiplier applied when a round fails to penetrate. A brittle APCBC
        /// breaks into fragments inside the plate and is nearly harmless; an APHE
        /// round still cooks off, which is why capped AP is dangerous on light tanks.
        /// </summary>
        private static float NonPenetrationMultiplier(ShellType type)
        {
            switch (type)
            {
                case ShellType.APCBC: return 0.15f;
                case ShellType.APC: return 0.25f;
                case ShellType.APHE: return 0.85f;
                case ShellType.APHEBC: return 0.80f;
                case ShellType.APCR: return 0.10f;
                case ShellType.HEAT: return 1.0f;
                case ShellType.HE: return 1.0f;
                default: return 0.5f;
            }
        }

        public static ImpactReport Resolve(Shell shell, ArmourPlate plate, float impactAngleDeg)
        {
            float angle = Mathf.Abs(impactAngleDeg);

            // Per-shell threshold, not one global angle. A sharp APCR rod skips off
            // plate at 60 degrees where a blunt APCBC still punches in at 73, and a
            // HEAT jet does not bounce at all. A single global value made the sharp
            // rounds usable against sloped armour and made HEAT wrongly afraid of it.
            float criticalRicochet = shell.CriticalRicochetDeg;

            if (angle >= criticalRicochet)
            {
                return new ImpactReport
                {
                    Result = ImpactResult.Ricochet,
                    ImpactAngleDeg = angle,
                    EffectiveArmorMm = plate.EffectiveThickness(shell, angle),
                    ShellPenetrationMm = shell.PenetrationAt(0f, angle),
                    ResidualPenetrationMm = 0f,
                    KineticEnergyJ = 0f,
                    CreatedSpallCone = false,
                    SpallConeDeg = 0f
                };
            }

            float effective = plate.EffectiveThickness(shell, angle);
            float penetration = shell.PenetrationAt(0f, angle);

            // HE rounds never "penetrate" in this sense: they fragment and blast.
            if (shell.Type == ShellType.HE)
            {
                return new ImpactReport
                {
                    Result = ImpactResult.NonPenetration,
                    ImpactAngleDeg = angle,
                    EffectiveArmorMm = effective,
                    ShellPenetrationMm = 0f,
                    ResidualPenetrationMm = 0f,
                    KineticEnergyJ = 0f,
                    CreatedSpallCone = false,
                    SpallConeDeg = 0f
                };
            }

            if (penetration < effective)
            {
                return new ImpactReport
                {
                    Result = ImpactResult.NonPenetration,
                    ImpactAngleDeg = angle,
                    EffectiveArmorMm = effective,
                    ShellPenetrationMm = penetration,
                    ResidualPenetrationMm = 0f,
                    KineticEnergyJ = KineticEnergyJ(shell) * NonPenetrationMultiplier(shell.Type),
                    CreatedSpallCone = false,
                    SpallConeDeg = 0f
                };
            }

            // Penetration: the round loses energy proportional to plate thickness,
            // exactly as it does on real armour, so it may still fail on the plate
            // behind this one.
            float consumed = effective;
            float residual = Mathf.Max(0f, penetration - consumed);
            float energy = KineticEnergyJ(shell) * Mathf.Clamp01(residual / Mathf.Max(1f, penetration));

            // A spall cone is a feature of SOLID projectiles only. An APHE or
            // APHEBC round fragments the plate and then cooks off inside the hull;
            // it does not throw a coherent cone. Modelling it as one would make
            // high-penetration rounds lethal at absurd range.
            bool solidProjectile = shell.ExplosiveMassKg <= 0.01f;

            return new ImpactReport
            {
                Result = residual > 0f ? ImpactResult.Penetration : ImpactResult.OverPenetration,
                ImpactAngleDeg = angle,
                EffectiveArmorMm = effective,
                ShellPenetrationMm = penetration,
                ResidualPenetrationMm = residual,
                KineticEnergyJ = energy,
                CreatedSpallCone = solidProjectile,
                SpallConeDeg = solidProjectile ? plate.SpallConeDeg : 0f
            };
        }

        /// <summary>Kinetic energy in joules: 0.5 * m * v^2.</summary>
        public static float KineticEnergyJ(Shell shell)
        {
            return 0.5f * shell.MassKg * shell.MuzzleVelocity * shell.MuzzleVelocity;
        }
    }
}