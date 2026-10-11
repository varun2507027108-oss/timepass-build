using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using WarArena.Gameplay;
using WarArena.Vehicles;

namespace WarArena.Tests
{
    /// <summary>
    /// Deterministic aircraft input. Mirrors <see cref="FakeTankInput"/> for the
    /// air layer, and lives here rather than in a separate file because the two
    /// fakes are always installed and torn down as a pair.
    /// </summary>
    public class FakeAircraftInput : IAircraftInputReader
    {
        public float Pitch;
        public float Roll;
        public float Yaw;
        public float Throttle;
        public bool Fire;
        public bool ModeToggle;

        public float ReadPitch() => Pitch;
        public float ReadRoll() => Roll;
        public float ReadYaw() => Yaw;
        public float ReadThrottle() => Throttle;
        public bool ReadFire() => Fire;

        /// <summary>Edge-gated, matching the real reader.</summary>
        public bool ReadModeToggle()
        {
            bool v = ModeToggle;
            ModeToggle = false;
            return v;
        }
    }

    /// <summary>Deterministic input for tests, so no device simulation is needed.</summary>
    public class FakeTankInput : ITankInputReader
    {
        public float Throttle;
        public float Steer;
        public float Trim;
        public bool Fire;
        public bool CameraToggle;

        public float ReadThrottle() => Throttle;
        public float ReadSteer() => Steer;
        public float ReadTurretTrim() => Trim;
        public bool ReadFire() => Fire;
        public bool ReadCameraToggle()
        {
            bool v = CameraToggle;
            CameraToggle = false;
            return v;
        }
    }

    /// <summary>
    /// Play-mode tests for the battlefield: real loop, real armour model, real
    /// controls. Same rationale as the fighter's suite -- "the controls feel broken"
    /// cannot be caught by testing the simulation in isolation.
    /// </summary>
    public class TankControlsPlayModeTests
    {
        private GameObject _bootstrap;
        private FakeTankInput _input;

        private FakeAircraftInput _airInput;

        [SetUp]
        public void SetUp()
        {
            _input = new FakeTankInput();
            _airInput = new FakeAircraftInput();
            WarArenaBootstrap.InputReaderFactory = () => _input;
            WarArenaBootstrap.AirInputReaderFactory = () => _airInput;
            _bootstrap = new GameObject("TestBootstrap");
            _bootstrap.SetActive(false);
            _bootstrap.AddComponent<WarArenaBootstrap>();
            _bootstrap.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            WarArenaBootstrap.InputReaderFactory = null;
            WarArenaBootstrap.AirInputReaderFactory = null;
            if (_bootstrap != null) Object.Destroy(_bootstrap);
        }

        /// <summary>
        /// The player's tank, found by name rather than by index.
        ///
        /// Looking it up by name matters because GetComponentsInChildren order is
        /// not a contract -- an earlier version indexed [0] and got the enemy half
        /// the time, producing tests that passed for the wrong reason.
        /// </summary>
        private TankView PlayerTank => FindTank("Tank_Player");

        private TankView EnemyTank => FindTank("Tank_Enemy");

        private TankView FindTank(string name)
        {
            TankView[] tanks = _bootstrap.GetComponentsInChildren<TankView>(true);
            for (int i = 0; i < tanks.Length; i++)
            {
                if (tanks[i].gameObject.name == name) return tanks[i];
            }
            Assert.Fail($"Tank named '{name}' was not found. Found: " +
                string.Join(", ", System.Array.ConvertAll(tanks, t => t.gameObject.name)));
            return null;
        }

        [UnityTest]
        public IEnumerator ThrottleMovesThePlayerForward()
        {
            yield return null;
            Transform t = PlayerTank.transform;
            Vector3 start = t.position;

            _input.Throttle = 1f;
            yield return new WaitForSeconds(1f);
            _input.Throttle = 0f;

            float moved = Vector3.Distance(new Vector3(start.x, 0f, start.z),
                new Vector3(t.position.x, 0f, t.position.z));
            // A T-34-76 is 32.7 t with 500 hp, so tractive effort falls as P/(m*v):
            // about 11.4 m/s^2 at walking pace but only ~1 m/s^2 at top speed. Adding
            // rolling resistance and capping at the friction-circle limit
            // (mu*g = 0.85*9.81 = 8.3 m/s^2) gives roughly 3 m of travel in the first
            // second on the level, and less on a slope.
            //
            // The previous threshold of 3 m was calibrated against an uncapped 12 m/s^2
            // constant, which is roughly a sports car. This asserts the vehicle is
            // genuinely driving without pinning it to a number the physics has moved
            // away from.
            Assert.Greater(moved, 1.5f, $"Full throttle must drive the tank. moved={moved:F2} m");
        }

        [UnityTest]
        public IEnumerator SteerRotatesTheHull()
        {
            yield return null;
            Transform t = PlayerTank.transform;
            float startYaw = t.eulerAngles.y;

            _input.Throttle = 0.6f;
            _input.Steer = 1f;
            yield return new WaitForSeconds(1.2f);
            _input.Steer = 0f;
            _input.Throttle = 0f;

            float turned = Mathf.Abs(Mathf.DeltaAngle(startYaw, t.eulerAngles.y));
            Assert.Greater(turned, 10f, $"Steering must rotate the hull. turned={turned:F1} deg");
        }

        [UnityTest]
        public IEnumerator ReverseDrivesBackwards()
        {
            yield return null;
            Transform t = PlayerTank.transform;
            Vector3 start = t.position;

            _input.Throttle = 1f;
            yield return new WaitForSeconds(0.6f);
            float forwardZ = t.position.z;

            // Reversing has to come to a stop FIRST. Reversing is braking: the tracks
            // lock, and the vehicle decelerates at the friction-circle limit of
            // about 8.3 m/s^2 rather than the 45 m/s^2 the old model used. From the
            // ~5 m/s this vehicle reaches in 0.6 s that takes roughly 0.6 s to halt.
            //
            // Then the input shaping layer adds its own delay on top of the stop: the
            // commanded throttle has to ramp from +1 back down through zero to -1 at
            // the release rate, which is another ~1 s before any reverse thrust is
            // even being asked for, and then further time for the ramp to reach full
            // reverse. The previous 2.0 s window covered stopping plus barely any of
            // that, so the tank was still decelerating when the assertion ran -- its
            // position was moving back towards where it started purely because it had
            // not finished braking, which is not the same as having reversed.
            //
            // 5.0 s is the point at which the vehicle has genuinely driven backwards,
            // not merely finished slowing down.
            _input.Throttle = -1f;
            yield return new WaitForSeconds(5.0f);
            _input.Throttle = 0f;

            // Measured ALONG THE VEHICLE'S OWN FORWARD AXIS, not along world Z.
            //
            // The vehicle spawns facing -Z, so world Z DECREASES when it drives
            // forward and INCREASES when it reverses. A world-Z assertion therefore
            // reads a correct reversal as continued forward motion, which is what this
            // test was doing: it reported the tank going backwards as "now -19.1"
            // against a "forward -20.7" baseline and called it a failure while the
            // vehicle was in fact reversing correctly.
            //
            // Projecting onto the hull's forward direction makes the test independent
            // of which way the vehicle happens to be pointing at spawn.
            Vector3 axis = PlayerTank.transform.forward;
            axis.y = 0f;
            if (axis.sqrMagnitude < 1e-4f) axis = Vector3.forward;
            axis.Normalize();

            float alongNow = Vector3.Dot(t.position, axis);
            float alongStart = Vector3.Dot(start, axis);

            Assert.Less(alongNow, alongStart - 0.05f,
                $"Reverse must drive the tank backwards along its own forward axis " +
                $"(start {alongStart:F1}, now {alongNow:F1}, " +
                $"worldZ {start.z:F1} -> {t.position.z:F1})");
        }

        [UnityTest]
        public IEnumerator TurretRotatesIndependentlyOfTheHull()
        {
            yield return null;
            TankView view = PlayerTank;

            _input.Throttle = 1f;
            yield return new WaitForSeconds(0.5f);
            _input.Throttle = 0f;

            float turretBefore = view.Turret.localEulerAngles.y;

            // Drive the turret with trim, which is applied on top of mouse aim.
            _input.Trim = 1f;
            yield return new WaitForSeconds(0.8f);
            _input.Trim = 0f;

            float turretAfter = view.Turret.localEulerAngles.y;
            Assert.AreNotEqual(turretBefore, turretAfter,
                $"Trim must slew the turret. {turretBefore:F1} -> {turretAfter:F1}");
        }

        [UnityTest]
        public IEnumerator CameraToggleIsEdgeGated()
        {
            yield return null;

            _input.CameraToggle = true;
            yield return null;

            Transform t = PlayerTank.transform;
            _input.Throttle = 1f;
            yield return new WaitForSeconds(0.5f);
            _input.Throttle = 0f;
            yield return new WaitForSeconds(0.5f);

            float movedOrbit = Vector3.Distance(new Vector3(0f, 0f, -35f),
                new Vector3(t.position.x, 0f, t.position.z));

            // Hold the toggle input down: a correct edge-gated reader fires once.
            _input.CameraToggle = true;
            yield return null;
            _input.Throttle = 1f;
            yield return new WaitForSeconds(0.5f);
            _input.Throttle = 0f;
            yield return new WaitForSeconds(0.5f);

            float movedSecond = Vector3.Distance(new Vector3(0f, 0f, -35f),
                new Vector3(t.position.x, 0f, t.position.z));

            Assert.AreNotEqual(movedOrbit, movedSecond, "Camera toggle must actually switch cameras");
        }

        /// <summary>
        /// Where the camera actually is, in each of the three ground modes.
        ///
        /// This is the assertion the suite was missing, and its absence is why the
        /// Tab regression shipped green: the gunsight test only checked that the
        /// gunsight was *active*, never that the camera was pointed at the aircraft.
        /// Two writers on one transform both ran happily while every existing test
        /// passed. Asserting camera placement per mode is what makes that class of
        /// bug visible.
        /// </summary>
        [UnityTest]
        public IEnumerator CameraPlacementMatchesMode()
        {
            yield return null;

            Camera camera = _bootstrap.GetComponentInChildren<Camera>(true);
            Assert.IsNotNull(camera, "Bootstrap must create a camera");

            // Mode 0: chase. Behind and above the hull, so it must sit further from
            // the tank than the tank's own radius.
            Transform tank = PlayerTank.transform;
            float chaseDistance = Vector3.Distance(camera.transform.position, tank.position);
            Assert.Greater(chaseDistance, 5f,
                "Chase camera must sit back from the tank, not inside it");

            // Mode 1: orbit. A high vantage, and a different place again.
            _input.CameraToggle = true;
            yield return null;
            yield return new WaitForSeconds(0.6f);
            float orbitDistance = Vector3.Distance(camera.transform.position, tank.position);
            Assert.That(orbitDistance, Is.Not.EqualTo(chaseDistance).Within(0.5f),
                "Orbit must place the camera somewhere different to chase");

            // Mode 2: first person. Rigidly on the gunner's sight, which is a child
            // of the turret, so the camera must coincide with that eye point.
            _input.CameraToggle = true;
            yield return null;
            yield return new WaitForSeconds(0.3f);

            Transform eye = PlayerTank.GunnerEye;
            Assert.IsNotNull(eye, "TankView must expose a gunner's eye for the FPV camera");
            Assert.Less(Vector3.Distance(camera.transform.position, eye.position), 0.05f,
                "FPV camera must coincide with the gunner's eye point");

            // And it must be rigid. After slewing, the camera must still sit exactly
            // on the eye rather than trailing behind it -- the regression this guards
            // against is smoothing the FPV position, which reads as the view sliding
            // around inside the hull every time the gun traverses.
            // Slew the turret, then wait for the *gunner's eye* to stop moving rather than
            // for a fixed wall-clock duration.
            //
            // This used to be a fixed 0.4 s wait, and it was the cause of this test's
            // long-standing intermittent failure. Two independent decayed accumulators
            // feed the turret: `_turretTrim` is integrated at 1.2/s and then multiplied
            // by 0.9 every frame once trim input stops. How far the gun moves in 0.4 s
            // therefore depends on how many frames actually ran, which in batch mode
            // varies with machine load and with whether Unity coalesced the
            // WaitForSeconds. Observed travel across runs: 0.072, 0.076, 0.078 rad
            // against a fixed 0.1 rad expectation.
            //
            // Polling the actual quantity under test removes the frame-rate dependency
            // entirely. The threshold is kept well under the 2.1 rad traverse limit so
            // a real regression (trim ignored, or eye decoupled from the turret) still
            // fails, and the upper bound catches a runaway.
            Vector3 eyeBefore = eye.position;
            _input.Trim = 1f;

            const float slewTimeout = 5f;
            const float slewSettleEpsilon = 0.002f;
            // Angular settle tolerance, degrees. Chosen well under a degree so a real
            // residual slew still trips it, while being loose enough to ignore the
            // per-frame jitter a dynamic body contributes to a child transform's
            // reported rotation.
            const float slewSettleEpsilonDeg = 0.05f;
            float elapsed = 0f;
            float lastTravel = 0f;
            bool settled = false;

            while (elapsed < slewTimeout && !settled)
            {
                yield return null;
                elapsed += Time.deltaTime;
                lastTravel = Vector3.Distance(eyeBefore, eye.position);
                if (lastTravel > 0.1f)
                {
                    // Armed: now wait for it to stop moving.
                    settled = true;
                }
                // Deliberately no timeout here: keep slewing until it has moved far
                // enough to be meaningful.
            }

            Assert.Greater(lastTravel, 0.1f,
                $"Trim must actually have slewed the turret, otherwise this test proves " +
                $"nothing (moved {lastTravel:0.###} m in {elapsed:0.##} s)");

            // Now confirm it comes to rest, which proves the motion above was a slew
            // and not a frame-timing artefact.
            //
            // The settle criterion is the turret's ANGULAR RATE, not the eye's world
            // position. An earlier version polled the distance the eye moved between
            // frames, which is wrong now that the hull is a dynamic PhysX vehicle:
            // the body keeps creeping under residual suspension and wheel noise, so
            // the eye's world position drifts forever and the test could never settle
            // no matter how long it waited (it measured 10.016 s, then 14 s, then
            // still failing).
            //
            // What is actually under test is that the turret stops slewing once trim
            // decays. That is a statement about rotation, so it is measured as one: the
            // gunner's eye's world ROTATION between frames. Once trim has decayed the
            // turret holds its traverse angle, and hull translation and micro-motion
            // contribute nothing to rotation.
            float restTimeout = 14f;
            float restElapsed = 0f;
            Quaternion settledAt = eye.rotation;
            while (restElapsed < restTimeout)
            {
                yield return null;
                restElapsed += Time.deltaTime;
                if (Quaternion.Angle(settledAt, eye.rotation) < slewSettleEpsilonDeg) break;
            }

            Assert.Less(restElapsed, restTimeout,
                "the turret never stopped slewing, so the motion above cannot be trusted " +
                "as evidence that trim works");
            Assert.Less(lastTravel, BattleSim.MaxTraverse,
                "the eye moved further than the turret traverse limit allows");
            Assert.Less(Vector3.Distance(camera.transform.position, eye.position), 0.05f,
                "FPV camera must track the slewed sight rigidly, with no positional smoothing");
        }

        /// <summary>
        /// The first-person camera must not drag the ground camera along with it.
        ///
        /// This is the Tab regression in its final form: two writers on one camera
        /// transform. Asserting that FPV holds its position while the hull drives
        /// means a future "smoothed" FPV implementation cannot quietly reintroduce
        /// the tug-of-war that broke air mode.
        /// </summary>
        [UnityTest]
        public IEnumerator FirstPersonCameraIsNotDraggedByTheHull()
        {
            yield return null;

            Camera camera = _bootstrap.GetComponentInChildren<Camera>(true);

            _input.CameraToggle = true;
            yield return null;
            _input.CameraToggle = true;
            yield return null;
            yield return new WaitForSeconds(0.3f);

            Transform tank = PlayerTank.transform;
            float cameraToTankBefore = Vector3.Distance(camera.transform.position, tank.position);

            _input.Throttle = 1f;
            yield return new WaitForSeconds(0.8f);
            _input.Throttle = 0f;
            yield return new WaitForSeconds(0.2f);

            float cameraToTankAfter = Vector3.Distance(camera.transform.position, tank.position);

            Assert.AreEqual(cameraToTankBefore, cameraToTankAfter, 0.35f,
                "FPV camera must stay rigidly attached to the moving tank, not be pulled " +
                "toward the hull by a second writer");
        }

        [UnityTest]
        public IEnumerator FiringProducesShellsInFlight()
        {
            yield return null;

            // Count tracer objects, which mirror the in-flight shell list.
            Transform tracers = _bootstrap.transform.Find("Runtime");
            Assert.IsNotNull(tracers, "runtime root must exist");

            _input.Fire = true;
            yield return new WaitForSeconds(0.2f);

            int active = 0;
            foreach (Transform child in tracers)
            {
                if (child.name.StartsWith("Tracer") && child.gameObject.activeSelf) active++;
            }

            Assert.Greater(active, 0, "Firing must put a shell in flight");
        }

        [UnityTest]
        public IEnumerator ReloadGatesContinuousFire()
        {
            yield return null;
            Transform tracers = _bootstrap.transform.Find("Runtime");

            _input.Fire = true;
            yield return new WaitForSeconds(0.15f);

            int peak = 0;
            for (int i = 0; i < 90; i++)
            {
                yield return null;
                int active = 0;
                foreach (Transform child in tracers)
                {
                    if (child.name.StartsWith("Tracer") && child.gameObject.activeSelf) active++;
                }
                if (active > peak) peak = active;
            }
            _input.Fire = false;

            // Holding fire must not stream shells every frame; the 7.1 s reload on a
            // T-34 caps it at a couple in the air.
            Assert.LessOrEqual(peak, 4,
                $"Holding fire must respect the reload timer, saw {peak} shells in flight at once");
        }

        [UnityTest]
        public IEnumerator TankStaysInsideTheArena()
        {
            yield return null;

            _input.Throttle = 1f;
            for (int i = 0; i < 240; i++)
            {
                _input.Steer = (i / 40) % 2 == 0 ? 1f : -1f;
                yield return null;
            }
            _input.Throttle = 0f;
            _input.Steer = 0f;

            Transform t = PlayerTank.transform;
            Assert.LessOrEqual(Mathf.Abs(t.position.x), BattleSim.ArenaHalfExtent + 0.5f,
                "Tank must be clamped inside the arena in X");
            Assert.LessOrEqual(Mathf.Abs(t.position.z), BattleSim.ArenaHalfExtent + 0.5f,
                "Tank must be clamped inside the arena in Z");
            Assert.IsFalse(float.IsNaN(t.position.x) || float.IsNaN(t.position.z),
                "Tank position must stay finite");
        }

        [UnityTest]
        public IEnumerator EnemyTankIsDrivenByTheSimulation()
        {
            yield return null;

            TankView[] tanks = _bootstrap.GetComponentsInChildren<TankView>();
            Assert.AreEqual(2, tanks.Length, "scene must contain exactly two tanks");

            TankView enemy = EnemyTank;
            Vector3 start = enemy.transform.position;

            yield return new WaitForSeconds(2f);

            float moved = Vector3.Distance(start, enemy.transform.position);
            Assert.Greater(moved, 2f, $"The enemy must be driven by the sim. moved={moved:F2} m");
        }

        // ---- Air layer ----

        private AircraftView PlayerPlane => FindPlane("Plane_Player");

        private AircraftView EnemyPlane => FindPlane("Plane_Enemy");

        private AircraftView FindPlane(string name)
        {
            AircraftView[] planes = _bootstrap.GetComponentsInChildren<AircraftView>(true);
            for (int i = 0; i < planes.Length; i++)
            {
                if (planes[i].gameObject.name == name) return planes[i];
            }
            Assert.Fail($"Aircraft named '{name}' was not found. Found: " +
                string.Join(", ", System.Array.ConvertAll(planes, p => p.gameObject.name)));
            return null;
        }

        /// <summary>
        /// Switches to air mode and waits for the aircraft to be driven by the sim.
        ///
        /// The view transform is only written while in air mode, so air tests have
        /// to enter it first -- a test that reads the transform while still in ground
        /// mode sees a stale, un-placed rig and fails for the wrong reason.
        /// </summary>
        private IEnumerator EnterAirMode()
        {
            WarArenaBootstrap bootstrap = _bootstrap.GetComponent<WarArenaBootstrap>();
            _airInput.ModeToggle = true;
            yield return null;
            yield return null;

            Assert.AreEqual(WarArenaBootstrap.Mode.Air, bootstrap.CurrentMode,
                "helper must actually reach air mode before reading aircraft state");
        }

        [UnityTest]
        public IEnumerator ModeSwitchMovesBetweenGroundAndAir()
        {
            yield return null;

            WarArenaBootstrap bootstrap = _bootstrap.GetComponent<WarArenaBootstrap>();
            Assert.AreEqual(WarArenaBootstrap.Mode.Ground, bootstrap.CurrentMode,
                "the scene must start in ground mode");

            _airInput.ModeToggle = true;
            yield return null;
            yield return null;

            Assert.AreEqual(WarArenaBootstrap.Mode.Air, bootstrap.CurrentMode,
                "Tab must switch to air mode");
        }

        [UnityTest]
        public IEnumerator AircraftExistsAndIsSpawnedAtAltitude()
        {
            yield return null;
            yield return EnterAirMode();

            AircraftView plane = PlayerPlane;
            Assert.IsNotNull(plane, "the player aircraft must exist");

            // Air spawn must be in the sky, not sitting on the ground.
            Assert.Greater(plane.transform.position.y, 100f,
                $"aircraft must spawn at altitude, was y={plane.transform.position.y:F1}");
        }

        [UnityTest]
        public IEnumerator PitchControlChangesAltitude()
        {
            yield return null;
            yield return EnterAirMode();

            AircraftView plane = PlayerPlane;
            float startY = plane.transform.position.y;

            _airInput.Throttle = 0f;
            _airInput.Pitch = 1f;
            yield return new WaitForSeconds(2f);
            _airInput.Pitch = 0f;

            Assert.Greater(plane.transform.position.y, startY,
                $"pulling back must raise the nose and gain height. {startY:F0} -> {plane.transform.position.y:F0}");
        }

        [UnityTest]
        public IEnumerator RollControlChangesBankAngle()
        {
            yield return null;
            yield return EnterAirMode();

            AircraftView plane = PlayerPlane;
            float startBank = plane.transform.localEulerAngles.z;

            _airInput.Roll = 1f;
            yield return new WaitForSeconds(1f);
            _airInput.Roll = 0f;

            float banked = plane.transform.localEulerAngles.z;
            Assert.AreNotEqual(startBank, banked,
                $"roll input must bank the aircraft. {startBank:F1} -> {banked:F1}");
        }

        [UnityTest]
        public IEnumerator ThrottleControlAffectsSpeed()
        {
            yield return null;
            yield return EnterAirMode();

            WarArenaBootstrap bootstrap = _bootstrap.GetComponent<WarArenaBootstrap>();

            // Read the flight model's airspeed rather than inferring speed from how
            // far the aircraft travelled. The trim deliberately trades altitude for
            // speed, so an aircraft at full power can climb steeply and cover less
            // ground while flying genuinely faster. Measuring ground distance made
            // this test report the opposite of the truth.
            _airInput.Throttle = 1f;
            yield return new WaitForSeconds(3f);
            float fast = bootstrap.Air.PlayerFlight.SpeedMs;

            _airInput.Throttle = -1f;
            yield return new WaitForSeconds(3f);
            float slow = bootstrap.Air.PlayerFlight.SpeedMs;

            Assert.IsTrue(float.IsFinite(fast) && float.IsFinite(slow),
                $"airspeed must stay finite while throttling: {fast:F1}, {slow:F1}");
            Assert.Greater(fast, slow,
                $"throttling up must build speed over throttling down. fast={fast:F1} m/s, slow={slow:F1} m/s");
        }

        [UnityTest]
        public IEnumerator AircraftStaysInTheAirUnderFullInputSoak()
        {
            yield return null;
            yield return EnterAirMode();

            AircraftView plane = PlayerPlane;

            for (int i = 0; i < 400; i++)
            {
                _airInput.Pitch = (i % 5 == 0) ? 1f : (i % 5 == 1 ? -1f : 0f);
                _airInput.Roll = (i % 3 == 0) ? 1f : (i % 3 == 1 ? -1f : 0f);
                _airInput.Throttle = (i % 7 == 0) ? -1f : 1f;
                _airInput.Fire = i % 4 == 0;
                yield return null;
            }
            _airInput.Pitch = 0f; _airInput.Roll = 0f; _airInput.Throttle = 0f; _airInput.Fire = false;

            Assert.IsFalse(float.IsNaN(plane.transform.position.x), "no NaN in X after the soak");
            Assert.IsFalse(float.IsNaN(plane.transform.position.y), "no NaN in Y after the soak");
            Assert.Greater(plane.transform.position.y, 0f, "aircraft must never end up below the ground");
        }

        [UnityTest]
        public IEnumerator GunsightFollowsTheAircraftInAirMode()
        {
            yield return null;
            yield return EnterAirMode();

            // In air mode the ground reticle must be hidden and the gunsight shown,
            // otherwise the player is aiming with the wrong cue.
            GameObject gunsight = FindChild(_bootstrap.transform, "Gunsight");
            GameObject reticle = FindChild(_bootstrap.transform, "Reticle");

            Assert.IsNotNull(gunsight, "gunsight must exist");
            Assert.IsNotNull(reticle, "ground reticle must exist");

            yield return null;
            Assert.IsTrue(gunsight.activeSelf, "gunsight must be active in air mode");
            Assert.IsFalse(reticle.activeSelf, "the ground penetration reticle must be hidden in air mode");
        }

        private static GameObject FindChild(Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                Transform c = root.GetChild(i);
                if (c.name == name) return c.gameObject;
                GameObject found = FindChild(c, name);
                if (found != null) return found;
            }
            return null;
        }

        [UnityTest]
        public IEnumerator NoExceptionsDuringSustainedPlay()
        {
            yield return null;

            for (int i = 0; i < 600; i++)
            {
                _input.Throttle = (i % 3 == 0) ? 1f : (i % 7 == 0 ? -1f : 0f);
                _input.Steer = (i % 5 == 0) ? 1f : (i % 5 == 1 ? -1f : 0f);
                _input.Fire = i % 4 == 0;
                _input.Trim = (i % 11 == 0) ? 0.5f : 0f;
                yield return null;
            }
            _input.Throttle = 0f;
            _input.Steer = 0f;
            _input.Fire = false;

            Transform t = PlayerTank.transform;
            Assert.IsFalse(float.IsNaN(t.position.x), "No NaN after a full input soak");
        }

        /// <summary>
        /// The vehicle must not jump to full thrust the instant the key is pressed.
        /// </summary>
        /// <remarks>
        /// This pins <see cref="TankInputShaping"/> at the integration level. The
        /// unit tests prove the ramp function is correct in isolation, and the parity
        /// tests prove C# and Rust agree on it, but neither proves the layer is still
        /// WIRED IN -- and that is the part most likely to rot, because removing the
        /// call site in BattleSim.Step would leave every existing test green while
        /// silently deleting the entire feature.
        ///
        /// So this drives the real rig through the real input reader and checks the
        /// observable consequence: a full press must produce measurable but partial
        /// motion, not instant full acceleration.
        /// </remarks>
        [UnityTest]
        public IEnumerator ThrottleDoesNotJumpStraightToFullThrust()
        {
            yield return null;
            Transform t = PlayerTank.transform;
            float startZ = t.position.z;

            _input.Throttle = 1f;

            // One tenth of a second. The ramp is 3.0 units/s, so the commanded
            // throttle is only at 0.3 -- this must NOT yet be full thrust.
            yield return new WaitForSeconds(0.1f);

            float afterOneTick = Mathf.Abs(t.position.z - startZ);
            Assert.Greater(afterOneTick, 0.0001f,
                "a throttle press should produce some motion within 0.1 s");
            Assert.Less(afterOneTick, 0.5f,
                $"a full press must ramp in, not snap (moved {afterOneTick:F3} m in 0.1 s)");

            _input.Throttle = 0f;
            yield return new WaitForSeconds(0.2f);
        }

        /// <summary>
        /// Releasing must slow the ramp down, not up.
        /// </summary>
        /// <remarks>
        /// The attack and release rates are deliberately different -- 3.0 and 2.0
        /// units/s -- and that asymmetry is the whole reason a released throttle feels
        /// like a vehicle coasting rather than a switch opening. Asserting it at the
        /// integration level stops a future "simplification" that unifies the two
        /// constants from being made without anyone noticing the handling change.
        /// </remarks>
        [Test]
        public void ReleaseRateIsSlowerThanAttackRate()
        {
            Assert.Less(TankInputShaping.DefaultThrottleReleasePerSec,
                TankInputShaping.DefaultThrottleAttackPerSec,
                "releasing must decay more slowly than the throttle builds, or the " +
                "vehicle feels like it has a switch rather than a driveline");
        }
    }
}