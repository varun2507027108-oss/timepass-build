using NUnit.Framework;
using UnityEngine;
using WarArena.Gameplay;

namespace WarArena.Tests.EditMode
{
    public class SlopeMovementTests
    {
        [Test]
        public void ClampToArena_WithinRadius_ReturnsSameCoordinates()
        {
            Vector2 pos = new Vector2(100f, 200f);
            Vector2 clamped = BattleSim.ClampToArena(pos.x, pos.y);
            Assert.AreEqual(pos.x, clamped.x, 1e-4f);
            Assert.AreEqual(pos.y, clamped.y, 1e-4f);
        }

        [Test]
        public void ClampToArena_OutsideRadius_ClampsToExactRadius()
        {
            Vector2 pos = new Vector2(500f, 500f);
            Vector2 clamped = BattleSim.ClampToArena(pos.x, pos.y);
            float r = clamped.magnitude;
            Assert.AreEqual(BattleSim.ArenaRadius, r, 0.01f);
        }

        [Test]
        public void SlopeSpeedFactor_FlatGround_ReturnsOne()
        {
            // Inside r <= 85m flat inner area
            float factor = BattleSim.SlopeSpeedFactor(0f, 0f, Vector2.up);
            Assert.AreEqual(1f, factor, 1e-4f);
        }

        [Test]
        public void SlopeSpeedFactor_ReversingDownhill_ReturnsOne()
        {
            // At a point where terrain slopes upward toward +Z:
            // Moving in -Z direction (reversing downhill) should have factor 1.0 (no penalty)
            float factor = BattleSim.SlopeSpeedFactor(0f, 200f, -Vector2.up);
            Assert.AreEqual(1f, factor, 1e-4f);
        }
    }
}
