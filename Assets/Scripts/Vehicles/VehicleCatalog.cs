using System;
using System.Collections.Generic;
using UnityEngine;
using WarArena.Ballistics;

namespace WarArena.Vehicles
{
    /// <summary>
    /// Authoritative vehicle roster. Historical specs for the WWII lineup; battle
    /// rating brackets vehicles for matchmaking the way War Thunder does.
    ///
    /// Figures are from Bovington Tank Museum spec sheets, ww2db, and the
    /// Britannica armour-history article. Armor values are frontal at the
    /// driver's plate; production figures exclude schürzen and applique.
    /// </summary>
    public static class VehicleCatalog
    {
        [Serializable]
        public class VehicleDefinition
        {
            public string Id;
            public string DisplayName;
            public string Nation;
            public int Year;

            /// <summary>Battle rating, 1.0 to 8.0. Matchmaker brackets to +/- 0.7.</summary>
            public float BattleRating;

            public float MassKg;
            public float EngineKw;
            public float TopSpeedKph;
            public float ReverseSpeedKph;
            public int Crew;

            public float ArmorFrontMm;
            public float ArmorSideMm;
            public float ArmorRearMm;

            /// <summary>Frontal plate slope in degrees. Drives the ricochet model.</summary>
            public float FrontSlopeDeg;

            /// <summary>
            /// True for a cast hull, false for welded rolled plate.
            ///
            /// Cast armour is harder and resists perforation better than rolled plate
            /// of the same nominal thickness, which is why the Panther and Tiger are
            /// cast and the T-34 and Sherman are not. It is a small effect next to
            /// slope, but it is the reason a sloped rolled glacis defeats nearly
            /// everything that a flat cast hull stops, and both vehicles being in the
            /// same game makes the distinction directly observable.
            ///
            /// The Panther and Tiger were cast nose-to-tail; the T-34's famous
            /// sloped glacis is rolled and welded, which is precisely why its
            /// geometry beats a nominally thicker cast plate.
            /// </summary>
            public bool IsCastHull;

            public string GunName;
            public float GunCaliberMm;
            public float MuzzleVelocity;
            public float ReloadSeconds;
            public float ShellPenetration0Mm;
            public float ShellMassKg;
            public Ballistics.ShellType ShellType;

            /// <summary>True for vehicles with a rear-facing casemate gun.</summary>
            public bool HasCasemateGun;
        }

        private static readonly Dictionary<string, VehicleDefinition> _byId =
            new Dictionary<string, VehicleDefinition>(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyCollection<VehicleDefinition> All => _byId.Values;

        private static void Add(VehicleDefinition d) => _byId[d.Id] = d;

        static VehicleCatalog()
        {
            // ---- Germany, 1939-45 ----
            Add(new VehicleDefinition
            {
                Id = "pziv-h", DisplayName = "Panzer IV Ausf. H", Nation = "Germany", Year = 1943,
                BattleRating = 4.0f, MassKg = 25000, EngineKw = 220, TopSpeedKph = 40, ReverseSpeedKph = 16,
                Crew = 5, ArmorFrontMm = 80, ArmorSideMm = 30, ArmorRearMm = 20, FrontSlopeDeg = 12,
                GunName = "75mm KwK 40 L/48", GunCaliberMm = 75, MuzzleVelocity = 750,
                ReloadSeconds = 5.9f, ShellPenetration0Mm = 111, ShellMassKg = 6.8f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            Add(new VehicleDefinition
            {
                Id = "panther-g", DisplayName = "Panther Ausf. G", Nation = "Germany", Year = 1944,
                BattleRating = 5.3f, MassKg = 43000, EngineKw = 519, TopSpeedKph = 55, ReverseSpeedKph = 22,
                Crew = 5, ArmorFrontMm = 120, ArmorSideMm = 50, ArmorRearMm = 40, FrontSlopeDeg = 55,
                IsCastHull = true,
                GunName = "75mm KwK 42 L/70", GunCaliberMm = 75, MuzzleVelocity = 936,
                ReloadSeconds = 6.5f, ShellPenetration0Mm = 172, ShellMassKg = 6.8f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC, HasCasemateGun = true
            });

            Add(new VehicleDefinition
            {
                Id = "tiger-e", DisplayName = "Tiger I Ausf. E", Nation = "Germany", Year = 1942,
                BattleRating = 5.3f, MassKg = 57000, EngineKw = 519, TopSpeedKph = 38, ReverseSpeedKph = 10,
                Crew = 5, ArmorFrontMm = 100, ArmorSideMm = 80, ArmorRearMm = 80, FrontSlopeDeg = 0,
                IsCastHull = true,
                GunName = "88mm KwK 36 L/56", GunCaliberMm = 88, MuzzleVelocity = 780,
                ReloadSeconds = 8.7f, ShellPenetration0Mm = 149, ShellMassKg = 10.2f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            Add(new VehicleDefinition
            {
                Id = "tiger2-e", DisplayName = "Tiger II (King Tiger)", Nation = "Germany", Year = 1944,
                BattleRating = 6.7f, MassKg = 68000, EngineKw = 619, TopSpeedKph = 45, ReverseSpeedKph = 12,
                Crew = 5, ArmorFrontMm = 180, ArmorSideMm = 80, ArmorRearMm = 80, FrontSlopeDeg = 0,
                IsCastHull = true,
                GunName = "88mm KwK 43 L/71", GunCaliberMm = 88, MuzzleVelocity = 1000,
                ReloadSeconds = 12.0f, ShellPenetration0Mm = 202, ShellMassKg = 10.5f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            Add(new VehicleDefinition
            {
                Id = "stugiii-g", DisplayName = "StuG III Ausf. G", Nation = "Germany", Year = 1942,
                BattleRating = 4.3f, MassKg = 23900, EngineKw = 132, TopSpeedKph = 40, ReverseSpeedKph = 15,
                Crew = 4, ArmorFrontMm = 80, ArmorSideMm = 30, ArmorRearMm = 20, FrontSlopeDeg = 9,
                GunName = "75mm StuK 40 L/43", GunCaliberMm = 75, MuzzleVelocity = 750,
                ReloadSeconds = 5.9f, ShellPenetration0Mm = 111, ShellMassKg = 6.8f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            // ---- USSR ----
            Add(new VehicleDefinition
            {
                Id = "t34-76", DisplayName = "T-34/76 Model 1942", Nation = "USSR", Year = 1942,
                BattleRating = 3.7f, MassKg = 26500, EngineKw = 370, TopSpeedKph = 53, ReverseSpeedKph = 20,
                Crew = 4, ArmorFrontMm = 45, ArmorSideMm = 40, ArmorRearMm = 40, FrontSlopeDeg = 60,
                GunName = "76.2mm F-34", GunCaliberMm = 76.2f, MuzzleVelocity = 662,
                ReloadSeconds = 7.1f, ShellPenetration0Mm = 108, ShellMassKg = 6.3f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            Add(new VehicleDefinition
            {
                Id = "t34-85", DisplayName = "T-34/85 Model 1944", Nation = "USSR", Year = 1944,
                BattleRating = 4.7f, MassKg = 32000, EngineKw = 370, TopSpeedKph = 51, ReverseSpeedKph = 20,
                Crew = 5, ArmorFrontMm = 90, ArmorSideMm = 45, ArmorRearMm = 45, FrontSlopeDeg = 47,
                GunName = "85mm ZiS-S-53", GunCaliberMm = 85, MuzzleVelocity = 792,
                ReloadSeconds = 7.6f, ShellPenetration0Mm = 128, ShellMassKg = 9.1f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            Add(new VehicleDefinition
            {
                Id = "kv1-1941", DisplayName = "KV-1 Model 1941", Nation = "USSR", Year = 1941,
                BattleRating = 4.0f, MassKg = 33400, EngineKw = 366, TopSpeedKph = 35, ReverseSpeedKph = 12,
                Crew = 5, ArmorFrontMm = 90, ArmorSideMm = 75, ArmorRearMm = 75, FrontSlopeDeg = 0,
                GunName = "76.2mm F-32", GunCaliberMm = 76.2f, MuzzleVelocity = 581,
                ReloadSeconds = 8.5f, ShellPenetration0Mm = 88, ShellMassKg = 6.3f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            Add(new VehicleDefinition
            {
                Id = "is2-1944", DisplayName = "IS-2 Model 1944", Nation = "USSR", Year = 1944,
                BattleRating = 6.0f, MassKg = 46000, EngineKw = 447, TopSpeedKph = 45, ReverseSpeedKph = 15,
                Crew = 4, ArmorFrontMm = 120, ArmorSideMm = 90, ArmorRearMm = 90, FrontSlopeDeg = 20,
                GunName = "122mm D-25T", GunCaliberMm = 122, MuzzleVelocity = 781,
                ReloadSeconds = 15.0f, ShellPenetration0Mm = 142, ShellMassKg = 21.4f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            // ---- USA ----
            Add(new VehicleDefinition
            {
                Id = "m4a2-75", DisplayName = "M4A2 Sherman (75mm)", Nation = "USA", Year = 1943,
                BattleRating = 3.7f, MassKg = 30300, EngineKw = 335, TopSpeedKph = 39, ReverseSpeedKph = 16,
                Crew = 5, ArmorFrontMm = 50, ArmorSideMm = 38, ArmorRearMm = 38, FrontSlopeDeg = 56,
                GunName = "75mm M3", GunCaliberMm = 75, MuzzleVelocity = 619,
                ReloadSeconds = 6.5f, ShellPenetration0Mm = 88, ShellMassKg = 6.8f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            Add(new VehicleDefinition
            {
                Id = "m4a3-76", DisplayName = "M4A3 Sherman (76mm W)", Nation = "USA", Year = 1944,
                BattleRating = 4.0f, MassKg = 31700, EngineKw = 335, TopSpeedKph = 40, ReverseSpeedKph = 16,
                Crew = 5, ArmorFrontMm = 63, ArmorSideMm = 38, ArmorRearMm = 38, FrontSlopeDeg = 47,
                GunName = "76mm M1", GunCaliberMm = 76.2f, MuzzleVelocity = 793,
                ReloadSeconds = 6.5f, ShellPenetration0Mm = 131, ShellMassKg = 9.1f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            Add(new VehicleDefinition
            {
                Id = "sherman-firefly", DisplayName = "Sherman VC Firefly", Nation = "UK", Year = 1944,
                BattleRating = 5.0f, MassKg = 34300, EngineKw = 335, TopSpeedKph = 32, ReverseSpeedKph = 14,
                Crew = 4, ArmorFrontMm = 63, ArmorSideMm = 38, ArmorRearMm = 38, FrontSlopeDeg = 47,
                GunName = "QF 17-pdr", GunCaliberMm = 76.2f, MuzzleVelocity = 823,
                ReloadSeconds = 6.2f, ShellPenetration0Mm = 149, ShellMassKg = 9.1f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            Add(new VehicleDefinition
            {
                Id = "m26-pershing", DisplayName = "M26 Pershing", Nation = "USA", Year = 1945,
                BattleRating = 6.0f, MassKg = 46700, EngineKw = 335, TopSpeedKph = 40, ReverseSpeedKph = 13,
                Crew = 5, ArmorFrontMm = 102, ArmorSideMm = 50, ArmorRearMm = 50, FrontSlopeDeg = 47,
                GunName = "90mm M3", GunCaliberMm = 90, MuzzleVelocity = 838,
                ReloadSeconds = 8.0f, ShellPenetration0Mm = 152, ShellMassKg = 10.9f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            // ---- Britain ----
            Add(new VehicleDefinition
            {
                Id = "churchill-vii", DisplayName = "Churchill Mk VII", Nation = "UK", Year = 1944,
                BattleRating = 3.3f, MassKg = 31700, EngineKw = 254, TopSpeedKph = 24, ReverseSpeedKph = 12,
                Crew = 5, ArmorFrontMm = 152, ArmorSideMm = 63, ArmorRearMm = 38, FrontSlopeDeg = 20,
                GunName = "QF 6-pdr", GunCaliberMm = 57, MuzzleVelocity = 502,
                ReloadSeconds = 6.0f, ShellPenetration0Mm = 84, ShellMassKg = 3.2f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            Add(new VehicleDefinition
            {
                Id = "cromwell", DisplayName = "Cromwell", Nation = "UK", Year = 1944,
                BattleRating = 3.7f, MassKg = 26700, EngineKw = 213, TopSpeedKph = 64, ReverseSpeedKph = 20,
                Crew = 4, ArmorFrontMm = 76, ArmorSideMm = 38, ArmorRearMm = 38, FrontSlopeDeg = 30,
                GunName = "QF 6-pdr", GunCaliberMm = 57, MuzzleVelocity = 502,
                ReloadSeconds = 6.0f, ShellPenetration0Mm = 84, ShellMassKg = 3.2f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            // ---- Japan (deliberately thin armour, hence low BR) ----
            Add(new VehicleDefinition
            {
                Id = "type97-chiha", DisplayName = "Type 97 Chi-Ha", Nation = "Japan", Year = 1941,
                BattleRating = 2.0f, MassKg = 16900, EngineKw = 150, TopSpeedKph = 42, ReverseSpeedKph = 14,
                Crew = 3, ArmorFrontMm = 25, ArmorSideMm = 8, ArmorRearMm = 8, FrontSlopeDeg = 15,
                GunName = "Type 97 57mm", GunCaliberMm = 57, MuzzleVelocity = 430,
                ReloadSeconds = 5.5f, ShellPenetration0Mm = 43, ShellMassKg = 1.55f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            Add(new VehicleDefinition
            {
                Id = "type95-ha-go", DisplayName = "Type 95 Ha-Go", Nation = "Japan", Year = 1937,
                BattleRating = 1.3f, MassKg = 12700, EngineKw = 86, TopSpeedKph = 64, ReverseSpeedKph = 22,
                Crew = 3, ArmorFrontMm = 8, ArmorSideMm = 6, ArmorRearMm = 6, FrontSlopeDeg = 20,
                GunName = "Type 95 37mm", GunCaliberMm = 37, MuzzleVelocity = 400,
                ReloadSeconds = 4.5f, ShellPenetration0Mm = 28, ShellMassKg = 0.9f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            // ---- France (free pre-1940, then armoured cars) ----
            Add(new VehicleDefinition
            {
                Id = "somua-s35", DisplayName = "SOMUA S35", Nation = "France", Year = 1938,
                BattleRating = 1.7f, MassKg = 11700, EngineKw = 147, TopSpeedKph = 72, ReverseSpeedKph = 28,
                Crew = 3, ArmorFrontMm = 40, ArmorSideMm = 17, ArmorRearMm = 17, FrontSlopeDeg = 60,
                GunName = "37mm SA 38", GunCaliberMm = 37, MuzzleVelocity = 435,
                ReloadSeconds = 5.0f, ShellPenetration0Mm = 46, ShellMassKg = 0.9f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });

            // ---- Italy ----
            Add(new VehicleDefinition
            {
                Id = "m13-40", DisplayName = "M13/40", Nation = "Italy", Year = 1940,
                BattleRating = 1.7f, MassKg = 15300, EngineKw = 138, TopSpeedKph = 32, ReverseSpeedKph = 12,
                Crew = 4, ArmorFrontMm = 50, ArmorSideMm = 22, ArmorRearMm = 22, FrontSlopeDeg = 30,
                GunName = "47mm Mod 40", GunCaliberMm = 47, MuzzleVelocity = 430,
                ReloadSeconds = 5.5f, ShellPenetration0Mm = 52, ShellMassKg = 1.5f,
                ShellType = WarArena.Ballistics.ShellType.APHEBC
            });
        }

        public static VehicleDefinition Get(string id)
        {
            if (!_byId.TryGetValue(id, out VehicleDefinition d))
            {
                throw new KeyNotFoundException($"Unknown vehicle id: {id}");
            }
            return d;
        }

        public static bool TryGet(string id, out VehicleDefinition d) => _byId.TryGetValue(id, out d);

        /// <summary>
        /// Vehicles playable at a given battle rating, bracket included.
        /// A +/- 0.7 BR window is what keeps a T-34 from meeting a Tiger II.
        /// </summary>
        public static List<VehicleDefinition> GetByBattleRating(float br, float window = 0.7f)
        {
            var list = new List<VehicleDefinition>();
            foreach (VehicleDefinition v in _byId.Values)
            {
                if (Mathf.Abs(v.BattleRating - br) <= window) list.Add(v);
            }
            return list;
        }

        /// <summary>Builds the runtime shell for a vehicle's main gun.</summary>
        public static Shell BuildShell(VehicleDefinition v)
        {
            return Shell.APHEBC(v.GunCaliberMm, v.ShellMassKg, v.MuzzleVelocity, v.ShellPenetration0Mm);
        }

        /// <summary>
        /// Builds a full damage model from a catalogue entry: plates, modules and
        /// crew laid out in a plausible hull, with hazard radii that produce the
        /// module-coupling behaviour the damage model relies on.
        /// </summary>
        public static VehicleDamageModel BuildDamageModel(VehicleDefinition v)
        {
            var model = new VehicleDamageModel();

            model.AddPlate(new ArmourPlate("front", v.ArmorFrontMm, isCast: false));
            model.AddPlate(new ArmourPlate("side", v.ArmorSideMm));
            model.AddPlate(new ArmourPlate("rear", v.ArmorRearMm));

            // Modules in metres, local space: x right, y up, z forward.
            model.Modules.Add(new DamageModule("Engine", ModuleKind.Engine,
                new Vector3(0f, -0.1f, -2.1f), 120f, 1.6f) { FireChanceOnDestroy = 0.85f });
            model.Modules.Add(new DamageModule("Transmission", ModuleKind.Transmission,
                new Vector3(0f, -0.1f, -0.4f), 100f, 1.2f));
            model.Modules.Add(new DamageModule("TurretDrive", ModuleKind.TurretDrive,
                new Vector3(0.7f, 0.4f, 0.2f), 80f, 0.9f));
            model.Modules.Add(new DamageModule("GunBreech", ModuleKind.GunBreech,
                new Vector3(0f, 0.45f, 0.85f), 90f, 0.7f));
            model.Modules.Add(new DamageModule("AmmoRack", ModuleKind.AmmoRack,
                new Vector3(-0.6f, -0.1f, -0.9f), 70f, 1.8f) { FireChanceOnDestroy = 1f });
            model.Modules.Add(new DamageModule("Tracks", ModuleKind.Tracks,
                new Vector3(0f, -0.65f, 0f), 90f, 1.1f));
            model.Modules.Add(new DamageModule("Optic", ModuleKind.Optic,
                new Vector3(0.35f, 0.85f, 0.35f), 40f, 0.5f));

            model.Crew.Add(new CrewMember("Commander", new Vector3(0.45f, 0.7f, -0.2f)));
            model.Crew.Add(new CrewMember("Gunner", new Vector3(-0.35f, 0.6f, 0.55f)));
            model.Crew.Add(new CrewMember("Loader", new Vector3(-0.4f, 0.5f, 0.9f)));
            model.Crew.Add(new CrewMember("Driver", new Vector3(-0.5f, 0f, 1.6f)));
            model.Crew.Add(new CrewMember("RadioOp", new Vector3(0.5f, 0.1f, 0.1f)));

            return model;
        }
    }
}
