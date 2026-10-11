using NUnit.Framework;
using UnityEngine;
using WarArena.Gameplay;

namespace WarArena.Tests
{
    /// <summary>
    /// Diagnostic harness. Dumps the full suspension state at the two locations that
    /// matter: the slope the existing suite settles on, and the worst position the
    /// arena sweep finds.
    ///
    /// This exists to answer a question the assertions cannot: whether a contact that
    /// the model calls "not carrying load" genuinely is off the ground, or whether the
    /// load test is misfiring and the hull is being levitated by contacts that are in
    /// fact touching. Both look identical from the outside -- the hull sits too high
    /// and one side hangs -- so the only way to tell them apart is to read the
    /// per-contact numbers.
    /// </summary>
    [TestFixture]
    public class HullSuspensionStateProbe
    {
        private const float Step = 1f / 30f;
        private const float PlayerMassKg = 26000f;

        private static HullSuspension NewHull()
        {
            return new HullSuspension(PlayerMassKg,
                TrackedVehicleDynamics.ContactHalfLengthM(PlayerMassKg),
                TrackedVehicleDynamics.ContactHalfWidthM(PlayerMassKg),
                TrackedVehicleDynamics.ContactCentreZM(PlayerMassKg));
        }

        private static void Probe(string label, float x, float z, float headingDeg)
        {
            float heading = headingDeg * Mathf.Deg2Rad;
            HullSuspension s = NewHull();
            s.Reset(x, z, heading);
            for (int i = 0; i < 240; i++) s.Step(Step, x, z, heading, 0f, 0f);

            Debug.Log($"--- {label} @ ({x:F0},{z:F0}) hdg {headingDeg:F0} ---");
            Debug.Log($"    height={s.height:F3} pitch={(s.pitchRad * Mathf.Rad2Deg):F2} " +
                      $"roll={(s.rollRad * Mathf.Rad2Deg):F2} vertV={s.verticalVelocity:F3}");

            float h = TrackedVehicleDynamics.ContactHalfLengthM(PlayerMassKg);
            float w = TrackedVehicleDynamics.ContactHalfWidthM(PlayerMassKg);
            float cz = TrackedVehicleDynamics.ContactCentreZM(PlayerMassKg);
            float third = h / 3f;
            float[] zOff = { -h, -third, third, h };
            float[] xOff = { -w, w };

            for (int track = 0; track < 2; track++)
            {
                for (int k = 0; k < 4; k++)
                {
                    int i = track * 4 + k;
                    float lz = zOff[k] + cz;
                    float lx = xOff[track];

                    float cy = Mathf.Cos(heading);
                    float sy = Mathf.Sin(heading);
                    float wx = x + lx * cy + lz * sy;
                    float wz = z - lx * sy + lz * cy;

                    float ground = BattlefieldBuilder.GetTerrainHeight(wx, wz);
                    float localY = lx * Mathf.Sin(s.rollRad) * Mathf.Cos(s.pitchRad)
                                 + lz * Mathf.Sin(s.pitchRad);
                    float hullPlane = s.height + localY;

                    // Gap from the hull contact plane down to the ground. NEGATIVE
                    // means the plane is BELOW the terrain, i.e. sunk in.
                    float gap = hullPlane - ground;

                    Debug.Log($"    contact{i} (track {track}, slot {k}): " +
                              $"ground={ground:F3} hullPlane={hullPlane:F3} gap={gap:+0.000;-0.000}");
                }
            }
        }

        [Test]
        public void DumpStateAtLocationsOfInterest()
        {
            Probe("worst baseline position", 320f, -48f, 90f);
            Probe("worst baseline position hdg0", 320f, -48f, 0f);
            Assert.Pass("diagnostic dump");
        }
    }
}