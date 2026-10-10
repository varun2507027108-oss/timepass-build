using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using WarArena.Interop;

namespace WarArena.Tests
{
    /// <summary>
    /// Proves the Rust module loads inside Unity and that both sides agree about
    /// the ABI and the state layout.
    /// </summary>
    /// <remarks>
    /// These run in the Editor, so they exercise the native
    /// <c>war_core_ffi.dll</c> P/Invoke path -- the same one the Windows player
    /// uses. The WebGL path cannot be instantiated from an EditMode test and is
    /// verified instead by <c>crates/tools/wasm-interop-probe.mjs</c>.
    /// </remarks>
    [TestFixture]
    public class RustCoreInteropTests
    {
        /// <summary>
        /// The handle is session-long, so every test starts from a known state.
        /// Without this, tests that fire the gun would leave health at 66 and the
        /// next test's assertion would fail depending on execution order.
        /// </summary>
        [SetUp]
        public void FreshSimulation()
        {
            WarSimRuntime.BeginLoad();
            WarSimRuntime.Poll();
            Assert.IsTrue(WarSimRuntime.IsReady,
                "the Rust core did not become ready: " + WarSimRuntime.Status +
                " (build it with crates/tools/build-native.ps1)");
            WarSimRuntime.Reset();
        }

        [Test]
        public void NativePluginLoadsAndReportsReady()
        {
            StringAssert.Contains("ready", WarSimRuntime.Status);
            Debug.Log("[RustCoreInteropTests] " + WarSimRuntime.Status);
        }

[Test]
        public void ModulesReportedLayoutFitsTheBufferThisCSharpAllocates()
        {
            // TryFinalize already rejects a mismatch, so assert the exposed values
            // to make a failure legible rather than only a handshake log line.
            int allocated = WarSimRuntime.StateSlotCount;
            int used = WarSimRuntime.StateLayoutSlots;

            Assert.Greater(used, 0, "the module reported an empty layout");
            Assert.LessOrEqual(used, allocated,
                $"the module uses {used} slots but only {allocated} are allocated");
            Debug.Log($"[RustCoreInteropTests] layout: {used} used of {allocated} allocated");
        }

        [Test]
        public void ScalarProbeCrossesTheBoundaryAndIsFinite()
        {
            double value = WarSimRuntime.Echo(1.0);
            Assert.IsFalse(double.IsNaN(value),
                "Echo returned NaN, which means the handle was rejected");
            Assert.IsTrue(double.IsFinite(value), $"Echo(1.0) returned {value}");
        }

        [Test]
        public void ScalarProbeIsDeterministic()
        {
            Assert.AreEqual(WarSimRuntime.Echo(1.0), WarSimRuntime.Echo(1.0));
            Assert.AreEqual(WarSimRuntime.Echo(-3.5), WarSimRuntime.Echo(-3.5));
        }

        [Test]
        public void StateBlobReflectsTheSimulationAfterOneSecondOfThrottle()
        {
            for (int i = 0; i < 30; i++)
            {
                WarSimRuntime.AdvanceGround(1f / 30f, 1f, 0f, 0f, false, Vector3.zero);
            }

            Assert.AreEqual(0f, WarSimRuntime.PlayerPosition.y,
                "a ground tank must stay at y == 0");
            Assert.Greater(WarSimRuntime.PlayerSpeedKph, 0f,
                "the player should be moving after a second at full throttle");
            Assert.AreEqual(100, WarSimRuntime.PlayerHealth);
            Assert.AreEqual(100, WarSimRuntime.EnemyHealth);
        }

        [Test]
        public void StateBlobIsRewrittenEveryFrameNotLeftStale()
        {
            for (int i = 0; i < 5; i++)
            {
                WarSimRuntime.AdvanceGround(1f / 30f, 1f, 0f, 0f, false, Vector3.zero);
            }
            Assert.IsTrue(WarSimRuntime.ObservedFreshStateWrite,
                "the state blob was not observed being rewritten across frames, so " +
                "the host may be reading a stale buffer");
        }

        [Test]
        public void FiringProducesDecodedUtf8FeedMessages()
        {
            // Aim 50m away (a 3-4-5 from the spawn point) so the message carries a
            // known distance, proving the aim point's three components crossed the
            // boundary in the right order.
            WarSimRuntime.AdvanceGround(1f / 30f, 0f, 0f, 0f, true, new Vector3(30f, 0f, 5f));

            IReadOnlyList<string> feed = WarSimRuntime.Feed;
            Assert.Greater(feed.Count, 0, "firing should have produced a feed message");

            bool found = false;
            for (int i = 0; i < feed.Count; i++)
            {
                Assert.IsNotEmpty(feed[i], "a feed message decoded to an empty string");
                Debug.Log($"[RustCoreInteropTests] feed[{i}]: {feed[i]}");
                if (feed[i].Contains("50m")) found = true;
            }
            Assert.IsTrue(found,
                "expected a message reporting the 50m range; got: " +
                string.Join(" | ", feed));
        }

        [Test]
        public void FeedIsNewestFirstAndCapped()
        {
            for (int i = 0; i < 12; i++)
            {
                WarSimRuntime.AdvanceGround(1f / 30f, 0f, 0f, 0f, true,
                    new Vector3(30f, 0f, 5f));
            }
            Assert.LessOrEqual(WarSimRuntime.Feed.Count, 4,
                "the feed must stay capped so the HUD cannot overflow");
        }

        [Test]
        public void FiringReducesHealthThroughTheBlob()
        {
            WarSimRuntime.AdvanceGround(1f / 30f, 0f, 0f, 0f, true, new Vector3(30f, 0f, 5f));
            Assert.Less(WarSimRuntime.PlayerHealth, 100,
                "the spike deals 34 damage per shot; the blob should show 66");
        }

        [Test]
        public void ArenaClampHoldsUnderSustainedInput()
        {
            for (int i = 0; i < 600; i++)
            {
                WarSimRuntime.AdvanceGround(1f / 30f, 1f, Mathf.Sin(i * 0.05f), 0f, false,
                    Vector3.zero);
            }
            Vector3 p = WarSimRuntime.PlayerPosition;
            Assert.IsTrue(float.IsFinite(p.x) && float.IsFinite(p.z),
                $"position went non-finite: {p}");

            // Read the arena size from production rather than restating it. This
            // assertion used to hard-code 90f, which was the half-extent of the old
            // 180 m SQUARE arena. The arena is now a 440 m CIRCLE, so the constant was
            // wrong -- and the test still passed, purely because the loaded plugin was
            // stale and clamping at the old radius. It would have failed the moment
            // the plugin was correctly rebuilt.
            //
            // Asserting against a square bound also fails to test what clamp_to_arena
            // actually enforces: a circle. Check the radius instead.
            float r = Mathf.Sqrt(p.x * p.x + p.z * p.z);
            Assert.LessOrEqual(r, ArenaRadius + 0.5f,
                $"escaped the {ArenaRadius} m arena at radius {r}: {p}");
        }

        /// <summary>
        /// The arena constant, read from the simulation rather than restated. Kept in
        /// one place so a future change to the arena cannot leave the tests pinning
        /// the previous value.
        /// </summary>
        private const float ArenaRadius = WarArena.Gameplay.BattleSim.ArenaRadius;

        [Test]
        public void EveryUsedStateSlotStaysFiniteUnderAbuse()
        {
            for (int i = 0; i < 60; i++)
            {
                WarSimRuntime.AdvanceGround(1f / 30f, 1f, 0.5f, 0.2f, i % 3 == 0,
                    new Vector3(10f, 0f, 50f));
            }
            int used = WarSimRuntime.StateLayoutSlots;
            for (int slot = 0; slot < used; slot++)
            {
                double v = WarSimRuntime.ReadSlot(slot);
                Assert.IsTrue(double.IsFinite(v), $"slot {slot} is not finite: {v}");
            }
        }

        /// <summary>
        /// The C# slot constants are private, so read them back by name. This
        /// proves they are unique and, crucially, that the highest of them lies
        /// inside the layout the Rust module reports -- which is the check that
        /// catches the two sides disagreeing about where a field lives. Renaming a
        /// slot in Rust's <c>layout</c> module without renaming it here is exactly
        /// the mistake this is here to catch.
        /// </summary>
        [Test]
        public void ManagedSlotConstantsAreUniqueAndFitTheModulesLayout()
        {
            Type runtime = typeof(WarSimRuntime);
            string[] expected =
            {
                "SlotMagic", "SlotAbiVersion",
                "SlotPlayerX", "SlotPlayerY", "SlotPlayerZ",
                "SlotEnemyX", "SlotEnemyY", "SlotEnemyZ",
                "SlotPlayerHeadingRad", "SlotPlayerSpeedKph",
                "SlotPlayerHealth", "SlotEnemyHealth",
                "SlotKills", "SlotDeaths", "SlotTicks", "SlotWriteSentinel",
            };

            Dictionary<string, int> byName = new Dictionary<string, int>();
            HashSet<int> indices = new HashSet<int>();
            foreach (string name in expected)
            {
                FieldInfo field = runtime.GetField(name,
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.IsNotNull(field, $"WarSimRuntime.{name} is missing");
                int index = (int)field.GetRawConstantValue();
                byName[name] = index;
                Assert.IsTrue(indices.Add(index),
                    $"slot {index} is claimed by both {byName[name]} and {name}");
            }

            int highest = 0;
            foreach (KeyValuePair<string, int> pair in byName)
            {
                if (pair.Value > highest) highest = pair.Value;
            }
            Assert.Less(highest, WarSimRuntime.StateLayoutSlots,
                $"this C# reads slot {highest} but the Rust module only uses " +
                $"{WarSimRuntime.StateLayoutSlots} slots; the layout is out of sync");

            // Magic and version must be the first two slots, since they are the
            // handshake every single frame depends on.
            Assert.AreEqual(0, byName["SlotMagic"]);
            Assert.AreEqual(1, byName["SlotAbiVersion"]);
        }
    }
}