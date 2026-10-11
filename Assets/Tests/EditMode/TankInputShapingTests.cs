using NUnit.Framework;
using UnityEngine;
using WarArena.Gameplay;

namespace WarArena.Tests
{
    /// <summary>
    /// Behavioural tests for <see cref="TankInputShaping"/>.
    ///
    /// These pin the SHAPING, not the feel. "Feels good" is not a measurable
    /// property, but the things that produce it are: a full-throttle press must
    /// take a known fraction of a second, releasing must be slower than building,
    /// a reversal must cross zero rather than jump it, and the steer filter must
    /// stay bounded no matter how long the tick is.
    ///
    /// The last group matters more than it looks. The naive exponential filter
    /// <c>t * dt</c> silently overshoots once dt approaches or exceeds the time
    /// constant, and the simulation only runs at 30 Hz today, so a regression there
    /// would not show up in any driving test -- it would appear the first time
    /// somebody changed the tick rate.
    /// </summary>
    [TestFixture]
    public class TankInputShapingTests
    {
        private const float Step = 1f / 30f;

        private static float Attack => TankInputShaping.DefaultThrottleAttackPerSec;
        private static float Release => TankInputShaping.DefaultThrottleReleasePerSec;

        // ------------------------------------------------------------- throttle

        [Test]
        public void FullThrottleTakesTheReciprocalOfTheRateToBuild()
        {
            // 3.0 units/s means a third of a second from rest to full thrust.
            float t = 0f;
            float v = 0f;
            while (v < 0.999f && t < 5f)
            {
                v = TankInputShaping.Ramp(v, 1f, Step, Attack, Release);
                t += Step;
            }

            Assert.That(t, Is.EqualTo(1f / Attack).Within(Step),
                "full throttle should take exactly 1/rate seconds to build");
        }

        [Test]
        public void RampNeverOvershootsTheCommand()
        {
            float v = 0f;
            for (int i = 0; i < 200; i++)
            {
                v = TankInputShaping.Ramp(v, 0.4f, Step, Attack, Release);
                Assert.That(v, Is.LessThanOrEqualTo(0.4f + 1e-5f),
                    $"tick {i} overshot a 0.4 command (v={v})");
            }
            Assert.That(v, Is.EqualTo(0.4f).Within(1e-4f));
        }

        [Test]
        public void CommandIsClampedToTheAxisRange()
        {
            float v = TankInputShaping.Ramp(0f, 9f, Step, Attack, Release);
            Assert.That(v, Is.LessThanOrEqualTo(1f));
            v = TankInputShaping.Ramp(0f, -9f, Step, Attack, Release);
            Assert.That(v, Is.GreaterThanOrEqualTo(-1f));
        }

        [Test]
        public void ReleaseIsSlowerThanAttack()
        {
            // Compare the rate OF CHANGE. The two start from opposite states, so
            // comparing levels would say nothing about which is slower.
            float buildStep = TankInputShaping.Ramp(0f, 1f, Step, Attack, Release);
            float releaseStep = 1f - TankInputShaping.Ramp(1f, 0f, Step, Attack, Release);

            Assert.That(buildStep, Is.GreaterThan(releaseStep),
                "spooling up should be quicker than spooling down");
            Assert.That(buildStep, Is.EqualTo(Attack * Step).Within(1e-5f));
            Assert.That(releaseStep, Is.EqualTo(Release * Step).Within(1e-5f));
        }

        [Test]
        public void ReversalCrossesZeroRatherThanTeleporting()
        {
            float v = TankInputShaping.Ramp(1f, -1f, Step, Attack, Release);
            Assert.That(v, Is.GreaterThan(0.5f),
                "a single tick must not jump straight across the axis to reverse");
            Assert.That(v, Is.LessThan(1f), "and it must actually have moved");
        }

        [Test]
        public void AsymmetricRateIsChosenByDirectionOfTravelNotByWhichHalfOfTheAxis()
        {
            // Moving away from zero is a BUILD at the attack rate whichever side of
            // the axis it happens on; moving towards zero is a RELEASE at the slower
            // rate. This is the design contract that makes release feel different from
            // attack, and it must not depend on the sign of the command.
            float buildFwd = TankInputShaping.Ramp(0.5f, 1f, Step, Attack, Release) - 0.5f;
            // Moving away from zero on the negative side means the signed value goes
            // DOWN, so the raw difference is negative. The magnitude is the thing under
            // test; taking Abs on both sides keeps the comparison sign-agnostic.
            float buildBack = Mathf.Abs(TankInputShaping.Ramp(-0.5f, -1f, Step, Attack, Release) + 0.5f);
            Assert.That(buildFwd, Is.EqualTo(Attack * Step).Within(1e-5f));
            Assert.That(buildBack, Is.EqualTo(Attack * Step).Within(1e-5f));

            float releaseFwd = 0.5f - TankInputShaping.Ramp(0.5f, 0f, Step, Attack, Release);
            // On the negative side the axis moves UP towards zero, so the signed
            // difference is negative and the magnitude has to be taken. Writing it as
            // `ramp(...) + 0.5f` avoids a subtraction of two negatives, which is easy
            // to get backwards and silently compares a negative against a positive.
            float releaseBack = TankInputShaping.Ramp(-0.5f, 0f, Step, Attack, Release) + 0.5f;
            Assert.That(releaseFwd, Is.EqualTo(Release * Step).Within(1e-5f));
            Assert.That(releaseBack, Is.EqualTo(Release * Step).Within(1e-5f));
        }

        [Test]
        public void AFullReversalPassesThroughZeroAtTheReleaseRate()
        {
            // A full-ahead to full-astern command is a decrease in magnitude first,
            // so it decays at the release rate rather than getting a shortcut. It
            // must take several ticks to cross and then reach full reverse.
            float v = 1f;
            float ticksToZero = 0f;
            while (v > 0f && ticksToZero < 5f)
            {
                v = TankInputShaping.Ramp(v, -1f, Step, Attack, Release);
                ticksToZero += Step;
            }

            // 1.0 of axis at 2.0 units/s is half a second, i.e. fifteen ticks at 30 Hz.
            Assert.That(ticksToZero, Is.EqualTo(1f / Release).Within(Step * 2f));

            float back = TankInputShaping.Ramp(v, -1f, Step, Attack, Release);
            for (int i = 0; i < 60; i++)
            {
                back = TankInputShaping.Ramp(back, -1f, Step, Attack, Release);
            }
            Assert.That(back, Is.EqualTo(-1f).Within(1e-3f), "should settle at full reverse");
        }

        // -------------------------------------------------------------- steering

        [Test]
        public void SteerFilterHalvesTheErrorOverOneTimeConstant()
        {
            float tau = TankInputShaping.DefaultSteerLerpSeconds;
            float v = TankInputShaping.LerpAxis(0f, 1f, tau, tau);
            Assert.That(v, Is.EqualTo(1f - Mathf.Exp(-1f)).Within(0.01f));
        }

        [Test]
        public void SteerFilterStaysBoundedAtLongTickLengths()
        {
            // The common `t * dt` approximation returns 2.0 here and overshoots.
            // The exact 1-exp(-dt/tau) form cannot.
            float v = TankInputShaping.LerpAxis(0f, 1f, 10f, 1f);
            Assert.That(v, Is.EqualTo(1f).Within(1e-4f), "long tick overshot");
            Assert.That(v, Is.LessThanOrEqualTo(1f));
        }

        [Test]
        public void ZeroTimeConstantIsAPassthrough()
        {
            Assert.That(TankInputShaping.LerpAxis(0f, 0.75f, Step, 0f), Is.EqualTo(0.75f).Within(1e-6f));
        }

        [Test]
        public void SteerBlendIsWiderThroughNeutral()
        {
            float steady = TankInputShaping.SteerBlend(1f, 1f,
                TankInputShaping.DefaultSteerLerpSeconds,
                TankInputShaping.DefaultDirectionChangeSeconds);
            float flip = TankInputShaping.SteerBlend(1f, -1f,
                TankInputShaping.DefaultSteerLerpSeconds,
                TankInputShaping.DefaultDirectionChangeSeconds);

            Assert.That(steady, Is.EqualTo(TankInputShaping.DefaultSteerLerpSeconds));
            Assert.That(flip, Is.GreaterThan(steady),
                "traversing through neutral should be the slowest transition");
        }

        [Test]
        public void SteerBlendIsWidenedByAnySignDisagreementNotOnlyFullReversal()
        {
            // A reversal is defined as a sign disagreement, not as |target| == 1.
            // Asking for less lock on the OTHER side of centre is still crossing the
            // hull through neutral, and still needs the longer time constant.
            float tau = TankInputShaping.DefaultSteerLerpSeconds;
            float dir = TankInputShaping.DefaultDirectionChangeSeconds;

            float steady = TankInputShaping.SteerBlend(0.5f, 0.9f, tau, dir);
            float acrossCentre = TankInputShaping.SteerBlend(0.5f, -0.2f, tau, dir);

            Assert.That(steady, Is.EqualTo(tau));
            Assert.That(acrossCentre, Is.GreaterThan(tau),
                "moving to the other side of centre should widen the constant");
        }

        [Test]
        public void SteerBlendIgnoresReversalWhenThereIsNoSignDisagreement()
        {
            float tau = TankInputShaping.DefaultSteerLerpSeconds;
            float dir = TankInputShaping.DefaultDirectionChangeSeconds;

            // Same side, and the degenerate centre case (whose product is exactly 0,
            // which must not be read as "less than zero" and so must not trip the
            // reversal branch).
            Assert.That(TankInputShaping.SteerBlend(0.2f, 0.6f, tau, dir), Is.EqualTo(tau));
            Assert.That(TankInputShaping.SteerBlend(0f, 0f, tau, dir), Is.EqualTo(tau));
            Assert.That(TankInputShaping.SteerBlend(0.4f, 0f, tau, dir), Is.EqualTo(tau));
        }

        [Test]
        public void SteeringSettlesExactlyOnTheCommand()
        {
            float v = 0f;
            for (int i = 0; i < 300; i++)
            {
                v = TankInputShaping.LerpAxis(v, -0.6f, Step, TankInputShaping.DefaultSteerLerpSeconds);
            }
            Assert.That(v, Is.EqualTo(-0.6f).Within(1e-3f));
        }

        // ----------------------------------------------------------------- climb

        [Test]
        public void FlatGroundCostsNoCapability()
        {
            Assert.That(TankInputShaping.ClimbCapability(0f), Is.EqualTo(1f));
            Assert.That(TankInputShaping.ClimbCapability(6f), Is.EqualTo(1f));
            Assert.That(TankInputShaping.ClimbCapability(-6f), Is.EqualTo(1f),
                "a downhill is not a climb and must not be penalised");
        }

        [Test]
        public void ClimbDecaysToTheFloorAtTheGradeabilityLimit()
        {
            Assert.That(TankInputShaping.ClimbCapability(TankInputShaping.ClimbLimitDeg),
                Is.EqualTo(TankInputShaping.ClimbMinCapability).Within(1e-6f));
            Assert.That(TankInputShaping.ClimbCapability(60f),
                Is.EqualTo(TankInputShaping.ClimbMinCapability).Within(1e-6f),
                "capability must clamp rather than keep falling past the limit");
        }

        [Test]
        public void ClimbCapabilityIsMonotonicAndSymmetric()
        {
            float previous = TankInputShaping.ClimbCapability(0f);
            for (float s = 0f; s <= 40f; s += 0.5f)
            {
                float c = TankInputShaping.ClimbCapability(s);
                Assert.That(c, Is.LessThanOrEqualTo(previous + 1e-6f),
                    $"capability increased at {s} degrees");
                previous = c;

                Assert.That(TankInputShaping.ClimbCapability(-s), Is.EqualTo(c).Within(1e-6f),
                    $"sign must not matter at {s} degrees");
            }
        }

        [Test]
        public void ClimbLimitSitsAtRealGradeability()
        {
            // TOP 2-2-610 puts a main battle tank's gradeability around 29-30
            // degrees on a firm dry surface. The shaping must not start penalising
            // below that and must reach its floor at it, or the vehicle would feel
            // sluggish on ground it can actually climb.
            Assert.That(TankInputShaping.ClimbFlatDeg, Is.LessThan(20f));
            Assert.That(TankInputShaping.ClimbLimitDeg,
                Is.InRange(26f, 31f), "floor should sit near the real limit");
        }
    }
}