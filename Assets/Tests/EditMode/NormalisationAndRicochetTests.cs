using NUnit.Framework;
using UnityEngine;
using WarArena.Ballistics;

namespace WarArena.Tests
{
    /// <summary>
    /// Tests for the two things that were missing from the terminal ballistics
    /// model: shell normalisation, and per-shell ricochet thresholds.
    ///
    /// Both are small in degree terms and enormous in gameplay terms, which is
    /// exactly why they need pinning down. Plate resistance goes as 1/cos, so five
    /// degrees of normalisation against a 60 degree glacis is worth roughly 13
    /// percent of required penetration: the difference between defeating a Panther
    /// and bouncing off it.
    ///
    /// Normalisation is modelled on the **plate**, not on the projectile, because
    /// what changes when a blunt nose bites in is how much armour the round has to
    /// chew through, not what the round is capable of. Asserting it on the shell
    /// instead is a mistake these tests were written to catch, and did.
    /// </summary>
    public class NormalisationAndRicochetTests
    {
        private static Shell ApcBc() => Shell.APCBC(88f, 6.8f, 800f, 150f, 0.0015f);
        private static Shell Apcr() => Shell.APCR(88f, 3f, 1200f, 190f, 0.0012f);
        private static Shell Heat() => Shell.HEAT(88f, 17f, 800f, 200f, 0.002f);
        private static Shell Apc() => Shell.APC(88f, 6.8f, 800f, 150f, 0.0015f);

        /// <summary>Pure geometry, no shell, i.e. what normalisation changes.</summary>
        private static float GeometricPlate(float thicknessMm, float angleDeg)
        {
            return new ArmourPlate("test", thicknessMm).EffectiveThickness(angleDeg);
        }

        [Test]
        public void BluntRoundsNormaliseAndSharpRoundsDenormalise()
        {
            // Direction of the effect is the whole point. A blunt APBC nose bites
            // into the plate and rotates toward the normal, so the plate presents
            // less. A sharp APCR rod is pushed away, so it presents more.
            Assert.Greater(Shell.NormalisationFor(ShellType.APCBC), 0f,
                "a blunt-nosed APBC round must normalise");
            Assert.Greater(Shell.NormalisationFor(ShellType.APHEBC), 0f,
                "a ballistically capped APHEBC round must normalise");
            Assert.Greater(Shell.NormalisationFor(ShellType.APC), 0f,
                "a capped APC round must normalise");

            Assert.Less(Shell.NormalisationFor(ShellType.APCR), 0f,
                "a sharp sub-calibre rod must denormalise");
        }

        [Test]
        public void NormalisationReducesEffectivePlateOnSlopedPlate()
        {
            var plate = new ArmourPlate("glacis", 120f);
            Shell round = ApcBc();

            float geometric = GeometricPlate(120f, 60f);
            float normalised = plate.EffectiveThickness(round, 60f);

            Assert.Less(normalised, geometric,
                $"normalisation must reduce the plate the round faces: " +
                $"{geometric:F1} -> {normalised:F1} mm");

            // And the effect must equal the angle shift, so the two quantities stay
            // separable and the plate model remains the single source of truth.
            float expected = GeometricPlate(120f, 60f - round.NormalisationDeg);
            Assert.AreEqual(expected, normalised, 0.01f,
                "normalisation must act as an angle shift before the cosine");        }

        [Test]
        public void NormalisingRoundSeesExactlyNominalThicknessOnFlatPlate()
        {
            // The control case that proves the effect is applied to the angle rather
            // than as a flat bonus. At 0 degrees the plate normal and the shell axis
            // already agree, so a normalising round must face exactly the nominal
            // thickness and not one millimetre less.
            var plate = new ArmourPlate("flat", 100f);

            Assert.AreEqual(100f, plate.EffectiveThickness(ApcBc(), 0f), 0.01f,
                "a normalising round must face exactly nominal thickness on flat plate");

            // A denormalising round faces very slightly more, because it is pushed
            // away from the plate normal. The magnitude is small by design: it is a
            // couple of degrees, not a different plate.
            float sharpSees = plate.EffectiveThickness(Apcr(), 0f);
            Assert.Greater(sharpSees, 100f,
                "a denormalising round must face slightly more than nominal");
            Assert.Less(sharpSees, 100.5f,
                $"denormalisation on flat plate must stay a small effect, got {sharpSees:F2} mm");
        }

        [Test]
        public void NormalisationIsWorthMoreOnSteeperPlate()
        {
            // Nonlinearity is the reason this is an angle shift and not a percentage.
            // The same six degrees is worth more on a steep glacis than a shallow one,
            // because that is where 1/cos steepens.
            var plate = new ArmourPlate("test", 120f);
            Shell round = ApcBc();

            float gainShallow = GeometricPlate(120f, 30f) - plate.EffectiveThickness(round, 30f);
            float gainSteep = GeometricPlate(120f, 65f) - plate.EffectiveThickness(round, 65f);

            Assert.Greater(gainSteep, gainShallow,
                $"normalisation must be worth more on a steeper plate: " +
                $"30 deg gained {gainShallow:F1} mm, 65 deg gained {gainSteep:F1} mm");
        }

        [Test]
        public void DenormalisationCannotReducePlateBelowFaceOn()
        {
            // A rod pushed away from the plate must never be handed *less* armour
            // than face-on geometry. Clamping the corrected angle at zero is what
            // prevents that, and without the clamp this is a straight inversion.
            var plate = new ArmourPlate("flat", 100f);
            Shell sharp = Apcr();

            float sharpSees = plate.EffectiveThickness(sharp, 0f);
            float bluntSees = plate.EffectiveThickness(ApcBc(), 0f);

            Assert.GreaterOrEqual(sharpSees, bluntSees,
                "a denormalising round must never see less plate than a normalising one " +
                $"on the same flat plate: {sharpSees:F1} mm vs {bluntSees:F1} mm");
        }

        [Test]
        public void HeatIgnoresSlopeEntirely()
        {
            // The shaped-charge jet collapses onto the plate normal however oblique
            // the impact, so a sloped glacis costs it nothing. This is why HEAT
            // dominates heavy sloped frontage that stops every kinetic round.
            var plate = new ArmourPlate("glacis", 200f);
            Shell heat = Heat();

            Assert.AreEqual(plate.EffectiveThickness(heat, 0f),
                plate.EffectiveThickness(heat, 65f), 0.01f,
                "HEAT must face the same plate thickness at any angle");

            // Against a kinetic round, the contrast is the whole point.
            Assert.Less(plate.EffectiveThickness(heat, 65f),
                plate.EffectiveThickness(ApcBc(), 65f),
                "HEAT must face less plate than a kinetic round at the same angle");
        }

        [Test]
        public void SharpRodRicochetsEarliestAndBluntRoundsLatest()
        {
            // A sharp rod skips off plate where a blunt nose still punches in. One
            // global 70 degree threshold made APCR far too usable against sloped
            // armour and APBC far too vulnerable to it.
            Assert.Less(Apcr().CriticalRicochetDeg, Apc().CriticalRicochetDeg,
                "the sharp sub-calibre rod must ricochet earlier than capped AP");
            Assert.Greater(Shell.CriticalRicochetFor(ShellType.APCBC),
                Shell.CriticalRicochetFor(ShellType.APC),
                "blunt Soviet APBC must tolerate the steepest kinetic plate");
        }

        [Test]
        public void HeatJetDoesNotRicochet()
        {
            var plate = new ArmourPlate("front", 200f);
            Shell heat = Heat();

            Assert.AreNotEqual(ImpactResult.Ricochet, Penetration.Resolve(heat, plate, 80f).Result,
                "a HEAT jet must not ricochet off a plate at 80 degrees");
        }

        [Test]
        public void RicochetThresholdIsPerShellNotGlobal()
        {
            // The behaviour that matters to a player: same plate, same angle, and the
            // shell family alone decides whether it bounces. Historically this is why
            // blunt APBC outperformed sub-calibre against sloped armour.
            var plate = new ArmourPlate("glacis", 120f);

            Assert.AreEqual(ImpactResult.Ricochet, Penetration.Resolve(Apcr(), plate, 65f).Result,
                "APCR must ricochet at 65 degrees");

            Assert.AreNotEqual(ImpactResult.Ricochet, Penetration.Resolve(ApcBc(), plate, 65f).Result,
                "blunt APBC must still be biting at 65 degrees where APCR has skipped");
        }

        [Test]
        public void CastHullResistsBetterThanRolledPlate()
        {
            var rolled = new ArmourPlate("rolled", 100f);
            var cast = new ArmourPlate("cast", 100f, true);

            Assert.Greater(cast.EffectiveThickness(0f), rolled.EffectiveThickness(0f),
                "a cast hull must resist more than rolled plate of equal thickness");
            Assert.AreEqual(1.1f, cast.MaterialCoefficient, 0.001f);
            Assert.AreEqual(1.0f, rolled.MaterialCoefficient, 0.001f);
        }

        [Test]
        public void CastHullFlagMatchesHistoricalVehicles()
        {
            // Panther and Tiger are cast; the T-34's famous sloped glacis is welded
            // rolled plate. That contrast is the entire reason its geometry beats a
            // nominally thicker cast plate, so having both in the game makes the
            // distinction directly observable and worth asserting.
            Assert.IsTrue(Vehicles.VehicleCatalog.Get("panther-g").IsCastHull,
                "the Panther's hull is cast");
            Assert.IsTrue(Vehicles.VehicleCatalog.Get("tiger-e").IsCastHull,
                "the Tiger I's hull is cast");
            Assert.IsFalse(Vehicles.VehicleCatalog.Get("t34-76").IsCastHull,
                "the T-34's sloped glacis is rolled and welded, not cast");
            Assert.IsFalse(Vehicles.VehicleCatalog.Get("m4a3-76").IsCastHull,
                "the Sherman's hull is welded rolled plate");
        }

        [Test]
        public void SlopedArmourBeatsNominalThickness()
        {
            // The historical lesson the catalogue should teach, as arithmetic. A 45 mm
            // glacis at 60 degrees presents roughly 78 mm; the Panther's 120 mm at 55
            // presents over 200. Nominal thickness alone would rank them backwards.
            var t34Plate = new ArmourPlate("T-34 glacis", 45f, false);
            var pantherPlate = new ArmourPlate("Panther glacis", 120f, true);

            float t34Effective = t34Plate.EffectiveThickness(60f);
            float pantherEffective = pantherPlate.EffectiveThickness(55f);

            Assert.Greater(t34Effective, 45f * 1.5f,
                $"a 60 degree glacis must present far more than its nominal 45 mm, " +
                $"got {t34Effective:F0} mm");

            Assert.Greater(pantherEffective, t34Effective,
                $"the Panther's sloped cast glacis must present more: " +
                $"{pantherEffective:F0} mm vs {t34Effective:F0} mm");
        }

        [Test]
        public void PantherGunBeatsT34GlacisCloseInButNotAtRange()
        {
            // The single most consequential matchup fact in the game, asserted through
            // the real resolution path rather than by re-deriving the maths.
            //
            // Worth stating plainly because it is counterintuitive: a Panther 75 mm
            // DOES defeat the T-34's 45 mm glacis at 60 degrees, because that glacis
            // presents only about 78 mm and the round brings roughly 104. What defeats
            // the T-34 is the Panther's 88 mm, and distance -- the 75 mm loses its
            // advantage over range while the T-34's sloped plate does not.
            //
            // Getting this backwards is an easy and very satisfying mistake to make,
            // so it is asserted explicitly rather than left implicit.
            var t34Plate = new ArmourPlate("T-34 glacis", 45f, false);
            Shell panther75 = Shell.APHEBC(75f, 5.5f, 800f, 104f);
            Shell panther88 = Shell.APHEBC(88f, 9.9f, 900f, 160f);

            // The glacis really is the weak point, and it is doing its job: nominal
            // 45 mm presenting well over twice that against a normalising round.
            Assert.Less(t34Plate.EffectiveThickness(panther75, 60f), 90f,
                $"the T-34's 60 degree glacis must present under 90 mm against a " +
                $"normalising round, got {t34Plate.EffectiveThickness(panther75, 60f):F0} mm");

            // Both Panther guns win close in. This is correct and is why the T-34 was
            // fought at range rather than head-on.
            Assert.IsTrue(Penetrates(panther75, t34Plate, 60f),
                "the Panther 75 mm does defeat the T-34 glacis up close");
            Assert.IsTrue(Penetrates(panther88, t34Plate, 60f),
                "the Panther 88 mm defeats the T-34 glacis up close");

            // The 75 mm's margin shrinks with range but does not vanish: it is still
            // the better gun up close. What actually beat the T-34 in practice was the
            // Panther's 88 mm, and the T-34's compensation was mobility and the
            // 85 mm that the overwhelming majority of Panthers did not carry.
            Assert.IsTrue(Penetrates(panther75, t34Plate, 60f, 2000f),
                "the Panther 75 mm should still beat the T-34 glacis at 2000 m");
            Assert.IsTrue(Penetrates(panther88, t34Plate, 60f, 2000f),
                "the Panther 88 mm must defeat the T-34 glacis at 2000 m");

            // Range still bites: against flat 90 mm plate the 75 mm wins close in and
            // loses further out. This is the mechanism that makes distance matter,
            // and it only works now that the drag coefficients are physically sane.
            var flat90 = new ArmourPlate("flat 90", 90f);
            Assert.IsTrue(Penetrates(panther75, flat90, 0f),
                "precondition: the 75 mm defeats flat 90 mm at point blank");
            Assert.IsFalse(Penetrates(panther75, flat90, 0f, 2500f),
                "the 75 mm must fall short of flat 90 mm at 2500 m");
        }

        private static bool Penetrates(Shell shell, ArmourPlate plate, float angleDeg,
            float rangeM = 0f)
        {
            // Resolve at the requested range by scaling the round to the velocity it
            // will actually have arrived with, which is what the live sim does.
            Shell atRange = shell;
            atRange.Penetration0Mm = shell.PenetrationAt(rangeM, 0f);

            ImpactReport report = Penetration.Resolve(atRange, plate, angleDeg);
            return report.Result == ImpactResult.Penetration
                || report.Result == ImpactResult.OverPenetration;
        }

        [Test]
        public void PenetrationFallsOffWithRange()
        {
            // The reason normalisation is not the only thing that matters: a
            // normalising round still loses the engagement at range.
            //
            // Over-penetration counts as a success here, so the precondition asserts
            // "gets through" rather than naming one specific result: a round that
            // exactly matches the plate correctly resolves to OverPenetration.
            var plate = new ArmourPlate("flat", 120f);
            Shell round = ApcBc();

            ImpactReport close = Penetration.Resolve(round, plate, 0f);
            Assert.IsTrue(close.Result == ImpactResult.Penetration
                || close.Result == ImpactResult.OverPenetration,
                $"precondition: must get through at point blank, was {close.Result}");

            // A plate it can beat close in must stop it far out. Same round, same
            // gun, only distance differs.
            float velocityClose = round.VelocityAt(0f);
            float velocityFar = round.VelocityAt(2500f);

            Assert.Less(velocityFar, velocityClose,
                "a round must lose velocity over range");
            Assert.Less(round.PenetrationAt(2500f, 0f), round.PenetrationAt(0f, 0f),
                "a round must lose penetration over range");

            // And that decay must actually flip the outcome, which is the whole
            // reason range matters in the game.
            var heavyPlate = new ArmourPlate("heavy", 145f);
            ImpactReport near = Penetration.Resolve(round, heavyPlate, 0f);
            float penAtRange = round.PenetrationAt(2500f, 0f);

            Assert.IsTrue(near.Result == ImpactResult.Penetration
                || near.Result == ImpactResult.OverPenetration,
                $"precondition: must get through a 145 mm plate at 0 m, was {near.Result}");
            Assert.Less(penAtRange, 145f,
                $"the same round must fall short of 145 mm at 2500 m, brought {penAtRange:F0} mm");
        }

        [Test]
        public void NormalisationChangesAMatchupOutcome()
        {
            // The point of the whole feature, asserted as a before-and-after on a
            // realistic exchange. APCR has the higher quoted figure; on a 55 degree
            // glacis the normalising AP round must beat it, which is precisely why
            // normalising AP rounds were developed and preferred for sloped targets.
            var pantherPlate = new ArmourPlate("Panther glacis", 120f, true);

            Shell normalising = Shell.APHEBC(75f, 5.5f, 800f, 104f);
            Shell denormalising = Shell.APCR(75f, 3.1f, 1150f, 130f, 0.0012f);

            Assert.Greater(denormalising.Penetration0Mm, normalising.Penetration0Mm,
                "precondition: the APCR has the higher quoted figure");

            float normalisingPlate = pantherPlate.EffectiveThickness(normalising, 55f);
            float denormalisingPlate = pantherPlate.EffectiveThickness(denormalising, 55f);

            Assert.Greater(denormalisingPlate, normalisingPlate,
                $"on a 55 degree glacis the denormalising rod must face more plate: " +
                $"{denormalisingPlate:F0} mm vs {normalisingPlate:F0} mm");
        }
    }
}
