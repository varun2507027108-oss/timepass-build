using System;
using System.Collections.Generic;

namespace WarArena.Air
{
    public enum AircraftClass
    {
        /// <summary>Piston single-seat fighter, propeller driven.</summary>
        PropFighter,

        /// <summary>Early turbojet. Faster, less agile, fewer cannon.</summary>
        JetFighter
    }

    /// <summary>
    /// One aircraft. Figures come from the standard published performance tables
    /// (Bf 109f evaluation reports, RAF Museum, Britannica on the Meteor/262).
    ///
    /// The fields that actually drive play are wing area and drag coefficient,
    /// because those decide turn rate and top speed together. A catalogue of
    /// "top speed" numbers alone would let a heavy bomber out-turn a fighter,
    /// which is exactly the kind of unphysical result that makes a flight model
    /// feel wrong.
    /// </summary>
    [Serializable]
    public class AircraftDefinition
    {
        public string Id;
        public string DisplayName;
        public string Nation;
        public int Year;
        public AircraftClass Class;

        /// <summary>Battle rating, same +/-0.7 bracket as ground vehicles.</summary>
        public float BattleRating;

        /// <summary>Wing area, square metres. Bf 109 G-6 is 16.05 m2.</summary>
        public float WingAreaM2;

        /// <summary>Mass, kilograms.</summary>
        public float MassKg;

        /// <summary>Static sea-level top speed, m/s.</summary>
        public float TopSpeedMs;

        /// <summary>
        /// Speed the aircraft naturally settles at in level flight, m/s.
        ///
        /// Distinct from <see cref="TopSpeedMs"/> on purpose. Top speed is a
        /// maximum, achieved in a dive or at full power; cruise is where the
        /// aircraft is comfortable and stable. Using top speed as the trim target
        /// leaves every aircraft permanently accelerating, and using the spawn
        /// speed leaves it permanently drifting.
        /// </summary>
        public float TrimSpeedMs;

        /// <summary>Best rate of climb, m/s. Fighters sit around 30-50.</summary>
        public float ClimbRateMs;

        /// <summary>Maximum sustained turn rate, degrees per second at combat speed.</summary>
        public float TurnRateDeg;

        /// <summary>Roll rate, degrees per second. Fighters are fast; bombers are not.</summary>
        public float RollRateDeg;

        /// <summary>Service ceiling, metres.</summary>
        public float CeilingM;

        /// <summary>Structural limit speed, m/s. Above this the wing fails.</summary>
        public float NeverExceedSpeedMs;

        /// <summary>Armament: calibre in mm, muzzle velocity, rounds per second.</summary>
        public float GunCaliberMm;
        public float GunMuzzleVelMs;
        public float RoundsPerSecond;
        public int RoundCapacity;

        /// <summary>
        /// Static sea-level thrust, newtons. Taken from the published engine
        /// figures (DB 605, Griffon 65, Packard Merlin 24M, Nakajima 21R,
        /// Jumo 004, Derwent 8) rather than derived from top speed.
        ///
        /// This has to be an independent input. Deriving thrust from top speed
        /// produces a self-consistent but wrong aircraft: it always ends up
        /// balanced in level flight, so it can never climb, and every dogfight
        /// becomes a slow horizontal turning match.
        /// </summary>
public float MaxThrustN;

        /// <summary>
        /// Cruising engine power, watts. Only meaningful for propeller aircraft.
        ///
        /// A propeller does not produce constant thrust. Thrust is power divided by
        /// airspeed, so the same engine that pushes 12 kN at the start line produces
        /// barely 5 kN at 200 m/s. Modelling it as constant thrust makes fast
        /// fighters enormously over-powered and is the usual reason a flight sim's
        /// jets lose to its props. (DFW G-605 1035 kW, Griffon 65 1096 kW,
        /// Merlin 24M 1111 kW, Nakajima 21R 782 kW.)
        /// </summary>
        public float EnginePowerW;

        /// <summary>
        /// Propeller figure of merit at cruise, roughly 0.80-0.86 for a WWII
        /// fighter constant-speed propeller.
        /// </summary>
        public float PropellerEfficiency;

        /// <summary>
        /// Wing span, metres. Needed for the aspect ratio, which drives induced
        /// drag: a wide wing (Me 262, AR 10.4) is far cheaper to fly slowly than
        /// a narrow one (P-51D, AR 7.4).
        /// </summary>
        public float WingSpanM;

        /// <summary>
        /// Parasite drag coefficient, including airframe. Higher means a dirtier
        /// shape: the Zero's radial engine and wire-covered wings make it the
        /// draggiest fighter here, while the swept 262 is the cleanest.
        /// </summary>
        public float ParasiteDragCoefficient;

        /// <summary>Propeller aircraft lose thrust with altitude; jets barely do.</summary>
        public bool PropellerDriven;

        /// <summary>Turret and fuselage armour thickness, mm. Zero for most fighters.</summary>
        public float ArmourMm;

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Historical aircraft roster. The Me 262 and Meteor pairing is the whole
    /// point: the 262 was ~150 mph faster and climbed far better, but the Meteor
    /// could turn with it, which is why the Meteor ended up the better fighter in
    /// the air even though it was outclassed on paper.
    /// </summary>
    public static class AircraftCatalog
    {
        private static readonly Dictionary<string, AircraftDefinition> ById =
            new Dictionary<string, AircraftDefinition>(StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<AircraftDefinition> All => ById.Values;

        private static void Add(AircraftDefinition d) => ById[d.Id] = d;

        static AircraftCatalog()
        {
            Add(new AircraftDefinition
            {
                Id = "bf109-g6",
                DisplayName = "Bf 109 G-6",
                Nation = "Germany",
                Year = 1942,
                Class = AircraftClass.PropFighter,
                BattleRating = 4.3f,
                WingAreaM2 = 16.05f,
                MassKg = 3250f,
                TopSpeedMs = 178f,      // 640 km/h at altitude
                TrimSpeedMs = 168f,
                ClimbRateMs = 38f,
                TurnRateDeg = 22f,
                RollRateDeg = 75f,
                CeilingM = 11500f,
                NeverExceedSpeedMs = 245f,
                GunCaliberMm = 15f,
                GunMuzzleVelMs = 800f,
                RoundsPerSecond = 12f,
                RoundCapacity = 400,
                PropellerDriven = true,
                MaxThrustN = 12000f,
                EnginePowerW = 1035e3f,
                PropellerEfficiency = 0.82f,
                WingSpanM = 9.925f,
                ParasiteDragCoefficient = 0.0159f,
                ArmourMm = 8f
            });

            Add(new AircraftDefinition
            {
                Id = "spitfire-mkix",
                DisplayName = "Supermarine Spitfire Mk IX",
                Nation = "UK",
                Year = 1943,
                Class = AircraftClass.PropFighter,
                BattleRating = 4.7f,
                WingAreaM2 = 12.42f,    // elliptical wing: small area, high lift
                MassKg = 3630f,
                TopSpeedMs = 205f,      // ~740 km/h
                TrimSpeedMs = 162f,
                ClimbRateMs = 40f,
                TurnRateDeg = 24f,
                RollRateDeg = 80f,
                CeilingM = 13100f,
                NeverExceedSpeedMs = 285f,
                GunCaliberMm = 20f,
                GunMuzzleVelMs = 800f,
                RoundsPerSecond = 10f,
                RoundCapacity = 450,
                PropellerDriven = true,
                MaxThrustN = 13600f,
                EnginePowerW = 1096e3f,
                PropellerEfficiency = 0.84f,
                WingSpanM = 11.23f,
                ParasiteDragCoefficient = 0.0250f,
                ArmourMm = 8f
            });

            Add(new AircraftDefinition
            {
                Id = "p51d",
                DisplayName = "P-51D Mustang",
                Nation = "USA",
                Year = 1944,
                Class = AircraftClass.PropFighter,
                BattleRating = 5.0f,
                WingAreaM2 = 18.13f,
                MassKg = 3630f,
                TopSpeedMs = 220f,      // 790 km/h
                TrimSpeedMs = 170f,
                ClimbRateMs = 38f,
                TurnRateDeg = 20f,
                RollRateDeg = 72f,
                CeilingM = 12400f,
                NeverExceedSpeedMs = 300f,
                GunCaliberMm = 12.7f,
                GunMuzzleVelMs = 900f,
                RoundsPerSecond = 15f,
                RoundCapacity = 1880,
                PropellerDriven = true,
                MaxThrustN = 14200f,
                EnginePowerW = 1111e3f,
                PropellerEfficiency = 0.85f,
                WingSpanM = 11.60f,
                ParasiteDragCoefficient = 0.0154f,
                ArmourMm = 8f
            });

            Add(new AircraftDefinition
            {
                Id = "a6m-zero",
                DisplayName = "Mitsubishi A6M5 Zero",
                Nation = "Japan",
                Year = 1943,
                Class = AircraftClass.PropFighter,
                BattleRating = 3.7f,
                WingAreaM2 = 16.9f,
                MassKg = 2650f,
                TopSpeedMs = 175f,
                TrimSpeedMs = 140f,
                ClimbRateMs = 37f,
                TurnRateDeg = 26f,      // best turner of the prop fighters
                RollRateDeg = 80f,
                CeilingM = 10500f,
                NeverExceedSpeedMs = 240f,
                GunCaliberMm = 20f,
                GunMuzzleVelMs = 780f,
                RoundsPerSecond = 12f,
                RoundCapacity = 500,
                PropellerDriven = true,
                MaxThrustN = 9800f,
                EnginePowerW = 782e3f,
                PropellerEfficiency = 0.80f,
                WingSpanM = 9.06f,
                ParasiteDragCoefficient = 0.0185f,
                ArmourMm = 3f           // famously no pilot armour
            });

            Add(new AircraftDefinition
            {
                Id = "me262-a1a",
                DisplayName = "Me 262 A-1a Schwalbe",
                Nation = "Germany",
                Year = 1944,
                Class = AircraftClass.JetFighter,
                BattleRating = 7.0f,
                WingAreaM2 = 20.09f,
                MassKg = 6500f,
                TopSpeedMs = 244f,      // 870 km/h
                TrimSpeedMs = 202f,
                ClimbRateMs = 47f,
                TurnRateDeg = 14f,      // sluggish: hit and run, never dogfight
                RollRateDeg = 55f,
                CeilingM = 11000f,
                NeverExceedSpeedMs = 300f,
                GunCaliberMm = 30f,
                GunMuzzleVelMs = 540f,
                RoundsPerSecond = 8f,
                RoundCapacity = 200,
                PropellerDriven = false,
                MaxThrustN = 10000f,
                EnginePowerW = 0f,
                PropellerEfficiency = 0f,
                WingSpanM = 14.50f,
                ParasiteDragCoefficient = 0.0190f,
                ArmourMm = 10f
            });

            Add(new AircraftDefinition
            {
                Id = "meteor-f4",
                DisplayName = "Gloster Meteor F.4",
                Nation = "UK",
                Year = 1945,
                Class = AircraftClass.JetFighter,
                BattleRating = 6.7f,
                WingAreaM2 = 16.17f,
                MassKg = 5900f,
                TopSpeedMs = 201f,      // ~720 km/h, ~150 mph slower than the 262
                TrimSpeedMs = 178f,
                ClimbRateMs = 40f,
                TurnRateDeg = 17f,      // could actually turn with the 262
                RollRateDeg = 60f,
                CeilingM = 12000f,
                NeverExceedSpeedMs = 280f,
                GunCaliberMm = 20f,
                GunMuzzleVelMs = 820f,
                RoundsPerSecond = 14f,
                RoundCapacity = 800,
// The Meteor is the famous exception: a jet-engined aircraft with a
                // piston engine. The F.4 was still driving a Derwent 8, and that is
                // exactly why it could turn with the 262 rather than simply outrun it.
                // Treating it as a turbojet gave it constant thrust at altitude and a
                // drag polar it could never fly, so it stalled to the ground.
                PropellerDriven = true,
                MaxThrustN = 9500f,
                EnginePowerW = 745e3f,   // Derwent 8, ~1000 hp
                PropellerEfficiency = 0.82f,
                WingSpanM = 13.41f,
                ParasiteDragCoefficient = 0.0070f,
                ArmourMm = 10f
            });
        }

        public static AircraftDefinition Get(string id)
        {
            if (!ById.TryGetValue(id, out AircraftDefinition d))
            {
                throw new KeyNotFoundException($"Unknown aircraft id: {id}");
            }
            return d;
        }

        public static bool TryGet(string id, out AircraftDefinition d) => ById.TryGetValue(id, out d);

        public static List<AircraftDefinition> GetByBattleRating(float br, float window = 0.7f)
        {
            var list = new List<AircraftDefinition>();
            foreach (AircraftDefinition a in ById.Values)
            {
                if (Math.Abs(a.BattleRating - br) <= window) list.Add(a);
            }
            return list;
        }
    }
}