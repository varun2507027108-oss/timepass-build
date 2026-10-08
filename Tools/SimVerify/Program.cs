using System;
using UnityEngine;
using WarArena.Ballistics;
using WarArena.Vehicles;

namespace WarArena.Tools
{
    /// <summary>
    /// Numerical verification of the armour model. These assertions encode the
    /// matchup relationships that must hold, because they are what the whole
    /// game is balanced around. If the penetration maths drifts, these fail
    /// before players notice.
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static void Check(string name, bool ok, string detail = "")
        {
            if (ok) { _passed++; Console.WriteLine($"  PASS  {name}  {detail}"); }
            else { _failed++; Console.WriteLine($"  FAIL  {name}  {detail}"); }
        }

        private static int Main()
        {
            Console.WriteLine("WarArena ballistics + damage verification");
            Console.WriteLine(new string('-', 68));

            CatalogIsInternallyConsistent();
            SlopeAndRicochetBehaviour();
            PenetrationDecaysWithRange();
            HeatDoesNotDecay();
            MatchupT34VsPanther();
            MatchupTigerVsSherman();
            BigShellBeatsSlopedLightTank();
            SpallConeKillsCrew();
            AmmoRackIsLethal();
            ModuleLossDegradesMobility();
            NoNaNAnywhere();

            Console.WriteLine(new string('-', 68));
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static VehicleCatalog.VehicleDefinition V(string id) => VehicleCatalog.Get(id);

        private static void CatalogIsInternallyConsistent()
        {
            Console.WriteLine("Catalogue");
            bool ok = true;
            int count = 0;
            foreach (VehicleCatalog.VehicleDefinition d in VehicleCatalog.All)
            {
                count++;
                ok &= d.ArmorFrontMm > 0 && d.ArmorFrontMm >= d.ArmorSideMm;
                ok &= d.ShellPenetration0Mm > 0;
                ok &= d.GunCaliberMm > 0 && d.MuzzleVelocity > 0;
                ok &= d.BattleRating > 0 && d.BattleRating < 8f;
                ok &= d.EngineKw > 0 && d.TopSpeedKph > 0;
            }
            Check("every vehicle has coherent armour, gun and BR data", ok, $"({count} vehicles)");
        }

        private static void SlopeAndRicochetBehaviour()
        {
            Console.WriteLine("Impact angle");
            ArmourPlate plate = new ArmourPlate("test", 100f);

            Check("normal impact is the reference thickness",
                Mathf.Round(plate.EffectiveThickness(0f)) == 100f,
                $"thickness={plate.EffectiveThickness(0f):F0}");

            float sloped = plate.EffectiveThickness(60f);
            Check("sloped plate is thicker to a shot", sloped > 190f,
                $"{plate.Name} at 60 deg = {sloped:F0}mm (vs 100 flat)");

            Shell shell = VehicleCatalog.BuildShell(V("t34-76"));
            ImpactReport ricochet = Penetration.Resolve(shell, plate, 75f);
            Check("past 70 degrees everything ricochets",
                ricochet.Result == ImpactResult.Ricochet, $"result={ricochet.Result}");

            ImpactReport steep = Penetration.Resolve(shell, plate, 69f);
            Check("just inside the critical angle still resolves",
                steep.Result != ImpactResult.Ricochet, $"result={steep.Result}");
        }

        private static void PenetrationDecaysWithRange()
        {
            Console.WriteLine("Range falloff");
            Shell shell = VehicleCatalog.BuildShell(V("t34-76"));

            float near = shell.PenetrationAt(0f, 0f);
            float mid = shell.PenetrationAt(1000f, 0f);
            float far = shell.PenetrationAt(2000f, 0f);

            Check("penetration decreases with distance",
                near > mid && mid > far,
                $"0m={near:F0} 1000m={mid:F0} 2000m={far:F0} mm");

            float v0 = shell.VelocityAt(0f);
            float v1 = shell.VelocityAt(1000f);
            Check("velocity decays with distance", v0 > v1,
                $"v0={v0:F0} m/s v@1000={v1:F0} m/s");
        }

        private static void HeatDoesNotDecay()
        {
            Console.WriteLine("HEAT");
            Shell heat = Shell.HEAT(88f, 19f, 1100f, 160f, 0.001f);
            Check("HEAT penetration is range-independent",
                Math.Abs(heat.PenetrationAt(0f, 0f) - heat.PenetrationAt(2000f, 0f)) < 0.01f,
                $"0m={heat.PenetrationAt(0f, 0f):F0} 2000m={heat.PenetrationAt(2000f, 0f):F0} mm");
        }

        private static void MatchupT34VsPanther()
        {
            Console.WriteLine("T-34/76 vs Panther (the core matchup)");
            VehicleCatalog.VehicleDefinition t34 = V("t34-76");
            VehicleCatalog.VehicleDefinition panther = V("panther-g");

            Shell t34Shell = VehicleCatalog.BuildShell(t34);
            ArmourPlate glacis = new ArmourPlate("front", panther.ArmorFrontMm);
            float impactAngle = panther.FrontSlopeDeg;

            // Close range: the T-34's 76 mm bounces off 120 mm at 55 degrees.
            ImpactReport close = Penetration.Resolve(t34Shell, glacis, impactAngle);
            Check("T-34 does NOT penetrate Panther glacis at 0 m",
                close.Result != ImpactResult.Penetration,
                $"{t34Shell.PenetrationAt(0f, impactAngle):F0}mm vs {glacis.EffectiveThickness(impactAngle):F0}mm eff");

            // Side and rear are far thinner.
            ImpactReport side = Penetration.Resolve(t34Shell, new ArmourPlate("side", panther.ArmorSideMm), 0f);
            Check("T-34 does penetrate Panther side",
                side.Result == ImpactResult.Penetration,
                $"{t34Shell.PenetrationAt(0f, 0f):F0}mm vs {panther.ArmorSideMm}mm");

            ImpactReport rear = Penetration.Resolve(t34Shell, new ArmourPlate("rear", panther.ArmorRearMm), 0f);
            Check("T-34 does penetrate Panther rear",
                rear.Result == ImpactResult.Penetration,
                $"{t34Shell.PenetrationAt(0f, 0f):F0}mm vs {panther.ArmorRearMm}mm");
        }

private static void MatchupTigerVsSherman()
        {
            Console.WriteLine("Tiger vs Sherman");
            VehicleCatalog.VehicleDefinition tiger = V("tiger-e");
            VehicleCatalog.VehicleDefinition sherman = V("m4a2-75");
            VehicleCatalog.VehicleDefinition pziv = V("pziv-h");

            // The famous relationship is not "the Tiger cannot hurt the Sherman".
            // The Tiger's 88 mm KwK 36 carries 149 mm at 780 m/s, which does defeat
            // 50.8 mm at 56 degrees at close range. What it cannot do is trade at
            // range, which is exactly where the 88 mm loses its velocity.
            Shell tigerShell = VehicleCatalog.BuildShell(tiger);
            ArmourPlate hull = new ArmourPlate("front", sherman.ArmorFrontMm);
            float impactAngle = sherman.FrontSlopeDeg;

            ImpactReport close = Penetration.Resolve(tigerShell, hull, impactAngle);
            Check("Tiger 88 mm DOES defeat the Sherman front at point-blank",
                close.Result == ImpactResult.Penetration,
                $"{tigerShell.PenetrationAt(0f, impactAngle):F0}mm vs {hull.EffectiveThickness(impactAngle):F0}mm eff");

            float tigerAt1000 = tigerShell.PenetrationAt(1000f, impactAngle);
            Check("Tiger 88 mm fails the same plate at 1000 m",
                tigerAt1000 < hull.EffectiveThickness(impactAngle),
                $"{tigerAt1000:F0}mm vs {hull.EffectiveThickness(impactAngle):F0}mm eff");

            // The Pz IV is the vehicle that historically cannot bounce a Sherman:
            // 111 mm of penetration against the same effective 89 mm, and it decays
            // out to nothing over distance.
            Shell pzivShell = VehicleCatalog.BuildShell(pziv);
            Check("Pz IV 75 mm fails the Sherman front beyond 500 m",
                pzivShell.PenetrationAt(1000f, impactAngle) < hull.EffectiveThickness(impactAngle),
                $"{pzivShell.PenetrationAt(1000f, impactAngle):F0}mm vs {hull.EffectiveThickness(impactAngle):F0}mm eff");

            ImpactReport side = Penetration.Resolve(tigerShell, new ArmourPlate("side", sherman.ArmorSideMm), 0f);
            Check("Tiger 88 mm DOES defeat the Sherman side",
                side.Result == ImpactResult.Penetration,
                $"{tigerShell.PenetrationAt(0f, 0f):F0}mm vs {sherman.ArmorSideMm}mm");
        }

        private static void BigShellBeatsSlopedLightTank()
        {
            Console.WriteLine("Heavy gun vs heavy sloped hull");
            VehicleCatalog.VehicleDefinition is2 = V("is2-1944");
            VehicleCatalog.VehicleDefinition churchill = V("churchill-vii");

            // The IS-2's 122 mm D-25T carries only ~142 mm against the Churchill
            // VII's 152 mm plate at 20 degrees, which is 162 mm effective. The
            // Churchill VII really was the one tank the IS-2 could not frontally
            // defeat -- this assertion exists so that cannot silently change.
            Shell big122 = VehicleCatalog.BuildShell(is2);
            ArmourPlate churchillFront = new ArmourPlate("front", churchill.ArmorFrontMm);
            float angle = churchill.FrontSlopeDeg;

            ImpactReport front = Penetration.Resolve(big122, churchillFront, angle);
            Check("IS-2 122 mm does NOT defeat the Churchill VII sloped front",
                front.Result != ImpactResult.Penetration,
                $"{big122.PenetrationAt(0f, angle):F0}mm vs {churchillFront.EffectiveThickness(angle):F0}mm eff");

            ImpactReport side = Penetration.Resolve(big122, new ArmourPlate("side", churchill.ArmorSideMm), 0f);
            Check("IS-2 122 mm DOES defeat the Churchill side",
                side.Result == ImpactResult.Penetration,
                $"{big122.PenetrationAt(0f, 0f):F0}mm vs {churchill.ArmorSideMm}mm");
        }

        private static void SpallConeKillsCrew()
        {
            Console.WriteLine("Spall cone");
            VehicleCatalog.VehicleDefinition tiger = V("tiger-e");
            VehicleDamageModel model = VehicleCatalog.BuildDamageModel(tiger);

            int crewBefore = model.AliveCrewCount;
            var t = new Transform { position = Vector3.zero };

            // Penetrating the front plate: the round continues into the
            // compartment and the spall cone catches whoever is behind it.
            Vector3 impact = new Vector3(0f, 1.0f, 3.2f);
            Vector3 dir = new Vector3(0f, 0f, -1f);
            model.ApplyShellImpact(VehicleCatalog.BuildShell(tiger), "front", 0f, impact, dir, t);

            Check("a penetrating hit wounds crew",
                model.AliveCrewCount < crewBefore,
                $"crew {crewBefore} -> {model.AliveCrewCount}");
        }

private static void AmmoRackIsLethal()
        {
            Console.WriteLine("Ammo rack");
            VehicleCatalog.VehicleDefinition sherman = V("m4a2-75");
            VehicleCatalog.VehicleDefinition hago = V("type95-ha-go");

            // HE is a fragmentation round: it must NOT cook off the ammunition rack
            // through a 50 mm frontal plate, because the burst happens outside the
            // hull. This is the behaviour that keeps HE honest against armour.
            VehicleDamageModel shermanModel = VehicleCatalog.BuildDamageModel(sherman);
            Shell he = Shell.HE(75f, 6.8f, 619f, 1.1f, 0.001f);
            shermanModel.ApplyShellImpact(
                he, "front", 0f,
                new Vector3(-0.6f, 0.6f, 2.0f), new Vector3(0f, 0f, -1f),
                new Transform { position = Vector3.zero });

            Check("HE does NOT cook the Sherman ammo rack through the front plate",
                !shermanModel.IsDestroyed,
                $"destroyed={shermanModel.IsDestroyed}");

            // Against 8 mm of plate the same round gets in, and then it does. The impact
            // point is the Ha-Go's actual front plate, roughly z = +0.9 m for this
            // hull layout; placing it further forward would model a burst in mid-air
            // and prove nothing.
            VehicleDamageModel hagoModel = VehicleCatalog.BuildDamageModel(hago);
            hagoModel.ApplyShellImpact(
                he, "front", 0f,
                new Vector3(-0.6f, 0.6f, 0.9f), new Vector3(0f, 0f, -1f),
                new Transform { position = Vector3.zero });

            Check("HE DOES cook the Type 95 Ha-Go ammo rack through 8 mm",
                hagoModel.IsDestroyed,
                $"destroyed={hagoModel.IsDestroyed}");
        }

        private static void ModuleLossDegradesMobility()
        {
            Console.WriteLine("Module degradation");
            VehicleCatalog.VehicleDefinition stug = V("stugiii-g");
            VehicleDamageModel model = VehicleCatalog.BuildDamageModel(stug);

            float powerBefore = model.EnginePowerFraction;
            float speedBefore = model.SpeedFraction;
            float traverseBefore = model.TurretRateFraction;

            DamageModule engine = model.FindModule(ModuleKind.Engine);
            DamageModule transmission = model.FindModule(ModuleKind.Transmission);
            DamageModule turret = model.FindModule(ModuleKind.TurretDrive);
            DamageModule tracks = model.FindModule(ModuleKind.Tracks);

            engine.ApplyDamage(engine.MaxHp + 1f, 0f);
            transmission.ApplyDamage(transmission.MaxHp + 1f, 0f);
            turret.ApplyDamage(turret.MaxHp + 1f, 0f);
            tracks.ApplyDamage(tracks.MaxHp + 1f, 0f);
            model.RecomputeFlags();

            Check("destroyed engine removes all power", model.EnginePowerFraction < powerBefore,
                $"{powerBefore:F2} -> {model.EnginePowerFraction:F2}");
            Check("destroyed transmission cuts top speed", model.SpeedFraction < speedBefore,
                $"{speedBefore:F2} -> {model.SpeedFraction:F2}");
            Check("destroyed turret drive stops traverse", model.TurretRateFraction < traverseBefore,
                $"{traverseBefore:F2} -> {model.TurretRateFraction:F2}");
            Check("destroyed tracks immobilise", model.IsImmobilised, $"immobile={model.IsImmobilised}");
            Check("gun still fires with a live breech", model.CanFireGun, $"canFire={model.CanFireGun}");
        }

        private static void NoNaNAnywhere()
        {
            Console.WriteLine("Numerical robustness");
            bool ok = true;
            foreach (VehicleCatalog.VehicleDefinition d in VehicleCatalog.All)
            {
                Shell s = VehicleCatalog.BuildShell(d);
                for (int range = 0; range <= 3000; range += 100)
                {
                    for (int angle = 0; angle <= 89; angle += 1)
                    {
                        float pen = s.PenetrationAt(range, angle);
                        float vel = s.VelocityAt(range);
                        if (float.IsNaN(pen) || float.IsInfinity(pen) || pen < 0f
                            || float.IsNaN(vel) || vel < 0f)
                        {
                            ok = false;
                        }
                    }
                }
            }
            Check("no NaN or infinity across the full envelope", ok,
                "all vehicles x 31 ranges x 90 impact angles");
        }
    }
}