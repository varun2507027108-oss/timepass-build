using System;
using UnityEngine;

namespace WarArena.Ballistics
{
    /// <summary>
    /// Shot types. Armour behaviour is the meaningful difference, not damage numbers:
    /// HEAT and HEAT rounds ignore distance falloff (they punch a hole, then blast),
    /// APBC detonates if it fails to penetrate, APCBC carries a spall cone.
    /// </summary>
    public enum ShellType
    {
        /// <summary>Armour-piercing, capped. Fragile if it fails to penetrate: breaks up inside.</summary>
        APCBC,

        /// <summary>Armour-piercing, high-explosive filler. Detonates 0.5-1.5 m after failing to penetrate.</summary>
        APHE,

        /// <summary>
        /// Armour-piercing, capped and ballistically capped. The WWII workhorse: it
        /// penetrates on the velocity of a solid round, then cooks off inside, which
        /// makes it far more lethal than APC once it gets through.
        /// </summary>
        APHEBC,

        /// <summary>Armour-piercing, composite, no explosive. Post-penetration energy keeps killing.</summary>
        APC,

        /// <summary>Armour-piercing, capped, sub-calibre core. High penetration, tiny spall.</summary>
        APCR,

        /// <summary>Shaped-charge. Penetration is constant with range; jet effect burns crew.</summary>
        HEAT,

        /// <summary>High explosive. Fragments; only useful against thin plate and crew.</summary>
        HE
    }

    /// <summary>
    /// A single ordnance round. Immutable after construction; the server owns
    /// the authoritative copy and replicates position/impact only.
    /// </summary>
    [Serializable]
    public struct Shell
    {
        public ShellType Type;
        public float CaliberMm;
        public float MassKg;

        /// <summary>Initial muzzle velocity, m/s.</summary>
        public float MuzzleVelocity;

        /// <summary>Armour penetration at 0 m and 90 degrees of impact, in mm of RHA.</summary>
        public float Penetration0Mm;

        /// <summary>Explosive filler mass, kg. Drives post-penetration damage and HE radius.</summary>
        public float ExplosiveMassKg;

        /// <summary>Drag coefficient. Higher loses velocity faster over distance.</summary>
        public float DragCoefficient;

        /// <summary>Fuse depth past the penetration point before a bouncing round detonates, m.</summary>
        public float FuseDepthM;

        public static Shell APCBC(float caliber, float mass, float v0, float pen0, float drag = 0f)
        {
            return new Shell
            {
                Type = ShellType.APCBC,
                CaliberMm = caliber,
                MassKg = mass,
                MuzzleVelocity = v0,
                Penetration0Mm = pen0,
                ExplosiveMassKg = 0f,
                DragCoefficient = drag > 0f ? drag : SolidShotDrag,
                FuseDepthM = 0.05f
            };
        }

        public static Shell APHE(float caliber, float mass, float v0, float pen0, float filler, float drag = 0f)
        {
            return new Shell
            {
                Type = ShellType.APHE,
                CaliberMm = caliber,
                MassKg = mass,
                MuzzleVelocity = v0,
                Penetration0Mm = pen0,
                ExplosiveMassKg = filler,
                DragCoefficient = drag > 0f ? drag : TypicalTankRoundDrag,
                FuseDepthM = 1.0f
            };
        }

        public static Shell APC(float caliber, float mass, float v0, float pen0, float drag = 0f)
        {
            return new Shell
            {
                Type = ShellType.APC,
                CaliberMm = caliber,
                MassKg = mass,
                MuzzleVelocity = v0,
                Penetration0Mm = pen0,
                ExplosiveMassKg = 0f,
                DragCoefficient = drag > 0f ? drag : TypicalTankRoundDrag,
                FuseDepthM = 0.05f
            };
        }

        /// <summary>Ballistically-capped HE filler. The default WWII tank round.</summary>
        public static Shell APHEBC(float caliber, float mass, float v0, float pen0)
        {
            // Filler mass is roughly 2.5% of the projectile mass for APHEBC.
            return new Shell
            {
                Type = ShellType.APHEBC,
                CaliberMm = caliber,
                MassKg = mass,
                MuzzleVelocity = v0,
                Penetration0Mm = pen0,
                ExplosiveMassKg = mass * 0.025f,
                DragCoefficient = TypicalTankRoundDrag,
                FuseDepthM = 0.9f
            };
        }

        /// <summary>
        /// Drag coefficient for a typical WWII tank round, chosen so residual velocity
        /// matches the documented figures: an 88 mm round doing about 800 m/s at
        /// 1000 m, an 85 mm about 690 m/s.
        /// </summary>
        public const float TypicalTankRoundDrag = 0.00013f;

        /// <summary>
        /// Sub-calibre rounds are long and slim, so they hold velocity better than a
        /// full-calibre shell of the same size, which is a large part of why they
        /// dominate at long range despite their small spall signature.
        /// </summary>
        public const float SubCalibreDrag = 0.00008f;

        /// <summary>
        /// Solid shot is shorter and blunter, so it sheds a little more velocity than
        /// a capped round of the same calibre.
        /// </summary>
        public const float SolidShotDrag = 0.00016f;

        public static Shell APCR(float caliber, float mass, float v0, float pen0, float drag = 0f)
        {
            return new Shell
            {
                Type = ShellType.APCR,
                CaliberMm = caliber,
                MassKg = mass,
                MuzzleVelocity = v0,
                Penetration0Mm = pen0,
                ExplosiveMassKg = 0f,
                DragCoefficient = drag > 0f ? drag : SubCalibreDrag,
                FuseDepthM = 0.05f
            };
        }

        public static Shell HEAT(float caliber, float mass, float v0, float pen0, float drag = 0f)
        {
            return new Shell
            {
                Type = ShellType.HEAT,
                CaliberMm = caliber,
                MassKg = mass,
                MuzzleVelocity = v0,
                Penetration0Mm = pen0,
                ExplosiveMassKg = caliber * caliber / 250f,
                DragCoefficient = drag > 0f ? drag : TypicalTankRoundDrag,
                FuseDepthM = 0f
            };
        }

        public static Shell HE(float caliber, float mass, float v0, float filler, float drag = 0f)
        {
            return new Shell
            {
                Type = ShellType.HE,
                CaliberMm = caliber,
                MassKg = mass,
                MuzzleVelocity = v0,
                Penetration0Mm = 0f,
                ExplosiveMassKg = filler,
                DragCoefficient = drag > 0f ? drag : TypicalTankRoundDrag,
                FuseDepthM = 0f
            };
        }

        /// <summary>
        /// Whether an HE burst can breach a plate of the given effective thickness
        /// and detonate INSIDE the hull rather than against the outside.
        ///
        /// HE has no meaningful penetration figure, so it cannot use the de Marre
        /// path. Modelling it as never breaching would be wrong in the opposite
        /// direction: a 75 mm HE round absolutely does defeat 8 mm of rolled plate,
        /// which is why HE was devastating against light tanks and Type 95 Ha-Gos.
        ///
        /// The threshold is an approximation of the observed relationship between
        /// charge weight and plate it can defeat, and is the piece to re-tune
        /// against firing data. It is deliberately explicit rather than hidden in a
        /// damage multiplier.
        /// </summary>
        public bool BreachesPlate(float effectiveThicknessMm)
        {
            if (Type != ShellType.HE) return false;
            if (effectiveThicknessMm <= 0f) return true;
            return effectiveThicknessMm <= CaliberMm * 0.12f;
        }

        /// <summary>
        /// Impact-angle multiplier for the plate, from geometry alone: pure
        /// line-of-sight depth, thickness divided by cosine.
        ///
        /// This is deliberately free of any shell-specific behaviour. It is the
        /// geometry, and callers that want normalisation or a shaped-charge
        /// exemption ask for it explicitly.
        /// </summary>
        public static float SlopeMultiplier(float impactAngleDeg)
        {
            float a = Mathf.Clamp(Mathf.Abs(impactAngleDeg), 0f, 89f);
            float rad = a * Mathf.Deg2Rad;
            return 1f / Mathf.Cos(rad);
        }

        /// <summary>
        /// Normalisation angle for this shell family, in degrees. Positive values
        /// normalise, i.e. the round bites in and gains.
        ///
        /// Plate resistances are published for a round arriving square to the plate.
        /// In reality the nose does not stay aligned with the velocity vector at
        /// impact: a blunt or capped nose bites into the plate and rotates *toward*
        /// the surface normal, so the plate presents less effective depth than its
        /// geometry alone suggests. A sharp uncapped nose is pushed the other way.
        ///
        /// The effect is small in degrees and decisive in millimetres, because
        /// resistance is 1/cos(theta). Five degrees of normalisation against a 60
        /// degree glacis moves the effective angle to 55 degrees, worth about 13
        /// percent of required penetration: the difference between defeating a
        /// Panther's glacis and bouncing off it.
        ///
        /// Applied to the plate rather than to the round's penetration figure,
        /// because normalisation is a property of how the nose meets the plate. The
        /// round's own capability does not change; what changes is the depth it has
        /// to chew through.
        /// </summary>
        public float NormalisationDeg => NormalisationFor(Type);

        public static float NormalisationFor(ShellType type)
        {
            switch (type)
            {
                // Soviet blunt-nosed APBC, notable for biting hard.
                case ShellType.APCBC: return 6f;

                case ShellType.APHEBC: return 5f;
                case ShellType.APHE: return 4f;

                // Plain capped AP: small cap, modest bite.
                case ShellType.APC: return 3f;

                // APCR discards its carrier and presents a sharp long rod, which is
                // pushed away from the plate. This is why sub-calibre rounds are so
                // unforgiving of sloped armour despite their high quoted figure.
                case ShellType.APCR: return -2f;

                // A shaped-charge jet is not a projectile with a nose at all: it
                // forms on the plate normal however oblique the impact, so it
                // neither normalises nor denormalises.
                case ShellType.HEAT: return 0f;
                case ShellType.HE: return 0f;

                default: return 0f;
            }
        }

        /// <summary>
        /// Critical ricochet angle for this shell family, degrees of impact.
        ///
        /// A single global threshold is wrong for most of the roster. A sharp
        /// sub-calibre rod skips off plate at angles where a blunt projectile still
        /// punches in, and a shaped-charge jet does not bounce at all because it has
        /// already collapsed onto the plate normal. Collapsing these into one number
        /// quietly made APCR far too usable against sloped armour and HEAT far too
        /// vulnerable to it.
        /// </summary>
        public float CriticalRicochetDeg => CriticalRicochetFor(Type);

        public static float CriticalRicochetFor(ShellType type)
        {
            switch (type)
            {
                // Sharp rod: skips earliest.
                case ShellType.APCR: return 60f;

                // Blunt APBC bites, so it tolerates the steepest kinetic plate.
                case ShellType.APCBC: return 73f;

                case ShellType.APHEBC: return 71f;
                case ShellType.APHE: return 70f;
                case ShellType.APC: return 68f;

                // The jet has collapsed onto the normal, so it does not bounce.
                case ShellType.HEAT: return 89f;
                case ShellType.HE: return 89f;

                default: return 70f;
            }
        }

        /// <summary>
        /// True when the round forms its effect on the plate normal and therefore
        /// ignores slope entirely.
        ///
        /// A shaped-charge jet collapses onto the target surface normal however
        /// oblique the firing angle, so sloped armour buys it nothing. That is why
        /// HEAT dominates heavy sloped frontage that stops every kinetic round in
        /// the roster, and it is a real tactical distinction rather than a
        /// convenience for the model.
        /// </summary>
        public bool IgnoresSlope => Type == ShellType.HEAT || Type == ShellType.HE;

        /// <summary>
        /// Armour penetration after travelling <paramref name="distanceM"/>.
        ///
        /// Uses the Jacob de Marre ballistic relationship: penetration falls off with
        /// the velocity the round has lost to drag, which is why a long-range shot at
        /// a sloped plate that would have punched through up close simply fails.
        ///
        /// This is the round's capability and nothing else. Obliquity is applied by
        /// the plate, because that is where normalisation and the shaped-charge
        /// exception physically belong. HEAT is the exception that does not decay
        /// with range.
        /// </summary>
        public float PenetrationAt(float distanceM, float impactAngleDeg)
        {
            float v = VelocityAt(distanceM);

            // de Marre: pen ~ (m * v^2 / d^3)^(1/3), normalised to the 0 m value.
            float caliberCubed = Cube(CaliberMm);
            float raw = MassKg * v * v / caliberCubed;
            float zeroReference = MassKg * MuzzleVelocity * MuzzleVelocity / caliberCubed;
            float ratio = zeroReference > 0f ? Mathf.Pow(raw / zeroReference, 1f / 3f) : 0f;

            return Penetration0Mm * ratio;
        }

        /// <summary>
        /// Velocity after <paramref name="distanceM"/>. Exponential decay model:
        /// v = v0 * exp(-k * d), which matches the shape of published velocity-vs-range
        /// tables far better than linear falloff at tank engagement ranges.
        /// </summary>
        public float VelocityAt(float distanceM)
        {
            return MuzzleVelocity * Mathf.Exp(-DragCoefficient * distanceM);
        }

        private static float Cube(float v)
        {
            return v * v * v;
        }
    }
}