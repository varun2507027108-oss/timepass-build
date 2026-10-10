using UnityEngine;
using WarArena.Vehicles;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Single entry point for the battlefield scene. Builds everything in code and
    /// owns the battle loop, camera and HUD.
    /// </summary>
    public class WarArenaBootstrap : MonoBehaviour
    {
        /// <summary>
        /// Input source factory, overridable so play-mode tests can inject a
        /// deterministic fake. The shipped game uses the real keyboard and mouse.
        /// </summary>
        public static System.Func<ITankInputReader> InputReaderFactory =
            () => new KeyboardTankInput();

        public static System.Func<IAircraftInputReader> AirInputReaderFactory =
            () => new KeyboardAircraftInput();

    public const string DefaultPlayerVehicle = "pziv-h";
    public const string DefaultEnemyVehicle = "t34-76";
        public const string DefaultPlayerAircraft = "bf109-g6";
        public const string DefaultEnemyAircraft = "spitfire-mkix";

        /// <summary>Which mode the player is currently controlling.</summary>
        public enum Mode
        {
            Ground,
            Air
        }

        public Mode CurrentMode { get; private set; } = Mode.Ground;

        /// <summary>
        /// The air combat simulation, exposed so tests can read the flight models
        /// directly.
        ///
        /// Measuring an aircraft by watching its transform move is unreliable: the
        /// trim trades altitude for speed, so an aircraft at full throttle can cover
        /// less ground while genuinely flying faster. Tests need the actual airspeed
        /// the model is holding, not a by-product of where it went.
        /// </summary>
        public AirCombatSim Air => _air;

        private BattleSim _battle;
        private AirCombatSim _air;
        private TankView _playerView;
        private TankView _enemyView;
        private AircraftView _playerPlane;
        private AircraftView _enemyPlane;
        private Transform _cameraTransform;
        private Camera _camera;
        private ITankInputReader _input;
        private IAircraftInputReader _airInput;

        private readonly System.Collections.Generic.List<Transform> _tracers =
            new System.Collections.Generic.List<Transform>();

        private readonly System.Collections.Generic.List<Transform> _airTracers =
            new System.Collections.Generic.List<Transform>();

        private Material _tracerMaterial;
        private Material _airTracerMaterial;
        private Material _reticleMaterial;
        private GameObject _reticle;
        private GameObject _gunsight;
        private GameObject _muzzleFlash;
        private float _muzzleFlashTimer;

        /// <summary>
        /// Ground camera modes, cycled with the camera toggle key.
        ///
        /// This is an enum rather than the pair of booleans it replaced because two
        /// booleans describing three states is how a "which one is authoritative"
        /// bug starts.
        /// </summary>
        private enum CameraMode
        {
            /// <summary>Behind and above the hull, spring-followed.</summary>
            Chase,

            /// <summary>High fixed vantage, for reading the whole arena.</summary>
            Orbit,

            /// <summary>Gunner's sight, rigidly attached to the turret.</summary>
            FirstPerson,
        }

        private CameraMode _cameraMode = CameraMode.Chase;
        private float _turretTrim;
        private Vector3 _aimPoint;

        private GUIStyle _hudStyle;
        private GUIStyle _helpStyle;
        private GUIStyle _outlineStyle;
        private Texture2D _barBack;
        private Texture2D _barFill;
        private Texture2D _barHealthPlayer;
        private Texture2D _barHealthEnemy;
        private Texture2D _barReloadReady;
        private Texture2D _barReloading;
        private Texture2D _reticleOk;
        private Texture2D _reticleBad;

        /// <summary>
        /// Global render state captured before this bootstrap changes it, so it can
        /// be handed back on destroy.
        /// </summary>
        private struct RenderSettingsSnapshot
        {
            public UnityEngine.Rendering.AmbientMode AmbientMode;
            public Color AmbientSky;
            public Color AmbientEquator;
            public Color AmbientGround;
            public float AmbientIntensity;
            public bool Fog;
            public FogMode FogMode;
            public float FogDensity;
            public Color FogColor;
            public Material Skybox;
            public Light Sun;
        }

        private RenderSettingsSnapshot _previousRenderSettings;
        private int _previousAntiAliasing;

        private static RenderSettingsSnapshot CaptureRenderSettings()
        {
            return new RenderSettingsSnapshot
            {
                AmbientMode = RenderSettings.ambientMode,
                AmbientSky = RenderSettings.ambientSkyColor,
                AmbientEquator = RenderSettings.ambientEquatorColor,
                AmbientGround = RenderSettings.ambientGroundColor,
                AmbientIntensity = RenderSettings.ambientIntensity,
                Fog = RenderSettings.fog,
                FogMode = RenderSettings.fogMode,
                FogDensity = RenderSettings.fogDensity,
                FogColor = RenderSettings.fogColor,
                Skybox = RenderSettings.skybox,
                Sun = RenderSettings.sun
            };
        }

        private void OnDestroy()
        {
            // Restore only if something was captured, so a component that was never
            // started cannot stomp the editor's settings with defaults.
            if (_renderSettingsCaptured)
            {
                RenderSettings.ambientMode = _previousRenderSettings.AmbientMode;
                RenderSettings.ambientSkyColor = _previousRenderSettings.AmbientSky;
                RenderSettings.ambientEquatorColor = _previousRenderSettings.AmbientEquator;
                RenderSettings.ambientGroundColor = _previousRenderSettings.AmbientGround;
                RenderSettings.ambientIntensity = _previousRenderSettings.AmbientIntensity;
                RenderSettings.fog = _previousRenderSettings.Fog;
                RenderSettings.fogMode = _previousRenderSettings.FogMode;
                RenderSettings.fogDensity = _previousRenderSettings.FogDensity;
                RenderSettings.fogColor = _previousRenderSettings.FogColor;
                RenderSettings.skybox = _previousRenderSettings.Skybox;
                RenderSettings.sun = _previousRenderSettings.Sun;
                QualitySettings.antiAliasing = _previousAntiAliasing;
                _renderSettingsCaptured = false;
            }
        }

        private bool _renderSettingsCaptured;

        private void Awake()
        {
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 1;

            // Start fetching the Rust simulation core. Purely additive today --
            // nothing reads from it yet, so gameplay is unchanged -- but it means
            // the WebAssembly module is downloaded in parallel with the rest of
            // the boot instead of costing a round trip on the first frame, and it
            // means every single launch exercises the interop glue. A broken
            // integration shows up as a missing "[WarCore] ready" line in the
            // browser console rather than as a silently frozen simulation.
            WarArena.Interop.WarSimRuntime.BeginLoad();

            // RenderSettings and QualitySettings are global, not per-scene, and the
            // PlayMode suite builds a full bootstrap seventeen times. Without saving
            // the previous values here, the last test to run leaves the editor
            // permanently lit by a sun that no longer exists.
            _previousRenderSettings = CaptureRenderSettings();
            _previousAntiAliasing = QualitySettings.antiAliasing;
            _renderSettingsCaptured = true;

            var root = new GameObject("Runtime");
            root.transform.SetParent(transform, false);

            BattlefieldBuilder.Build(root.transform);
            BattlefieldBuilder.BuildLighting(root.transform);

            GameObject cameraGo = BattlefieldBuilder.BuildCamera(root.transform);
            _camera = cameraGo.GetComponent<Camera>();
            _cameraTransform = cameraGo.transform;

            VehicleCatalog.VehicleDefinition playerDef = VehicleCatalog.Get(DefaultPlayerVehicle);
            VehicleCatalog.VehicleDefinition enemyDef = VehicleCatalog.Get(DefaultEnemyVehicle);

            _playerView = BuildTank(root.transform, "Player", playerDef, new Color(0.30f, 0.36f, 0.20f));
            _enemyView = BuildTank(root.transform, "Enemy", enemyDef, new Color(0.40f, 0.32f, 0.22f));

            _battle = new BattleSim(DefaultPlayerVehicle, DefaultEnemyVehicle);
            _battle.OnPlayerFired += HandlePlayerFired;
            _input = InputReaderFactory();

            _air = new AirCombatSim(DefaultPlayerAircraft, DefaultEnemyAircraft);
            _airInput = AirInputReaderFactory();

            _playerPlane = BuildPlane(root.transform, "Plane_Player", _air.PlayerSpec,
                new Color(0.55f, 0.58f, 0.52f), new Color(0.9f, 0.35f, 0.2f));
            _enemyPlane = BuildPlane(root.transform, "Plane_Enemy", _air.EnemySpec,
                new Color(0.30f, 0.42f, 0.28f), new Color(0.2f, 0.4f, 0.9f));

            BuildEffects(root.transform);
        }

        /// <summary>
        /// Tanks are parented to the runtime root so the bootstrap owns the whole
        /// scene graph: tests can find them with GetComponentsInChildren, and
        /// destroying the bootstrap tears everything down instead of leaking
        /// orphaned tanks at scene root.
        /// </summary>
        private TankView BuildTank(Transform parent, string name, VehicleCatalog.VehicleDefinition def, Color body)
        {
            GameObject go = new GameObject("Tank_" + name);
            go.transform.SetParent(parent, false);
            var view = go.AddComponent<TankView>();

            float scale = Mathf.Clamp(def.MassKg / 26000f, 0.7f, 1.35f);

            // Prefer an imported model when one is supplied for this vehicle, and fall
            // back to the procedural rig when it is not. The fallback is not a
            // placeholder: the whole simulation and all 71 tests are built against the
            // primitive rig, so it must keep working for the other 18 vehicles.
            GameObject model = TankModels.ForVehicle(def.Id);
            if (model != null)
            {
                view.BuildFromModel(model, body, new Color(0.12f, 0.11f, 0.10f), scale);
            }
            else
            {
                // Scale roughly with the vehicle's mass so a Tiger reads as a bigger
                // object than an FT, as the catalogue intends.
                view.Build(body, new Color(0.12f, 0.11f, 0.10f), scale);
            }
            return view;
        }

        /// <summary>
        /// Aircraft are parented for the same reason the tanks are: so the bootstrap
        /// owns the whole graph and tests can find them by name.
        /// </summary>
        private AircraftView BuildPlane(Transform parent, string name, WarArena.Air.AircraftDefinition spec,
            Color body, Color accent)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var view = go.AddComponent<AircraftView>();
            view.Build(spec, body, accent);
            return view;
        }

        private void BuildEffects(Transform parent)
        {
            // Tank tracers: hot, so they self-illuminate rather than depending on
            // where the sun happens to be relative to the camera.
            _tracerMaterial = VisualMaterials.Emissive(
                new Color(0.25f, 0.18f, 0.10f), new Color(1f, 0.55f, 0.15f), 3.2f);

            for (int i = 0; i < 24; i++)
            {
                GameObject tracer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                tracer.name = "Tracer" + i;
                Object.Destroy(tracer.GetComponent<Collider>());
                tracer.transform.SetParent(parent, false);
                tracer.transform.localScale = Vector3.one * 0.4f;
                tracer.GetComponent<Renderer>().sharedMaterial = _tracerMaterial;
                tracer.SetActive(false);
                _tracers.Add(tracer.transform);
            }

            // Air tracers: smaller and brighter than tank shells, so the two are
            // distinguishable at a glance in the same frame.
            // Air tracers: brighter and more yellow than the tank shells, so the two
            // are distinguishable in the same frame.
            _airTracerMaterial = VisualMaterials.Emissive(
                new Color(1f, 0.85f, 0.4f), new Color(1f, 0.82f, 0.35f), 4f);

            for (int i = 0; i < 48; i++)
            {
                GameObject tracer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                tracer.name = "AirTracer" + i;
                Object.Destroy(tracer.GetComponent<Collider>());
                tracer.transform.SetParent(parent, false);
                tracer.transform.localScale = Vector3.one * 0.9f;
                tracer.GetComponent<Renderer>().sharedMaterial = _airTracerMaterial;
                tracer.SetActive(false);
                _airTracers.Add(tracer.transform);
            }

            // Gunsight ring, parented to the camera so it sits in view.
            // Built from a Quad primitive rather than an empty GameObject: a bare
            // GameObject has no Renderer, and one is required to draw the sight.
            _gunsight = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _gunsight.name = "Gunsight";
            Object.Destroy(_gunsight.GetComponent<Collider>());
            _gunsight.transform.SetParent(_cameraTransform, false);
            _gunsight.transform.localPosition = new Vector3(0f, 0f, 6f);
            _gunsight.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            _gunsight.transform.localScale = Vector3.one * 1.4f;
            // Unlit: a HUD element must not be shaded by the sun. A lit sight ring
            // would dim whenever the aircraft banked away from the light, which is
            // precisely when the player most needs to read it.
            Material sightMaterial = VisualMaterials.Unlit(new Color(1f, 0.35f, 0.3f, 0.85f));
            VisualMaterials.MakeTransparent(sightMaterial);
            _gunsight.GetComponent<Renderer>().sharedMaterial = sightMaterial;
            _gunsight.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            _reticle = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _reticle.name = "Reticle";
            Object.Destroy(_reticle.GetComponent<Collider>());
            _reticle.transform.SetParent(parent, false);
            _reticle.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            // Penetration reticle: unlit, and drawn after the world so it is never
            // hidden behind terrain. It turns GREEN when the shot will penetrate
            // (standard tank gunnery cue in War Thunder / World of Tanks) and RED when
            // deflected by slope or thick armor.
            _reticleMaterial = VisualMaterials.Unlit(new Color(0.2f, 1.0f, 0.25f, 0.95f));
            _reticle.GetComponent<Renderer>().sharedMaterial = _reticleMaterial;
            _reticle.GetComponent<Renderer>().shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;

            _muzzleFlash = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _muzzleFlash.name = "MuzzleFlash";
            Object.Destroy(_muzzleFlash.GetComponent<Collider>());
            _muzzleFlash.transform.SetParent(parent, false);
            Material flashMat = VisualMaterials.Emissive(
                new Color(1f, 0.9f, 0.6f), new Color(1f, 0.75f, 0.2f), 5.5f);
            VisualMaterials.MakeTransparent(flashMat);
            _muzzleFlash.GetComponent<Renderer>().sharedMaterial = flashMat;
            _muzzleFlash.GetComponent<Renderer>().shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            _muzzleFlash.SetActive(false);
        }

        private void Update()
        {
            if (_battle == null || _air == null) return;

            float dt = Time.deltaTime;

            if (_airInput.ReadModeToggle())
            {
                CurrentMode = CurrentMode == Mode.Ground ? Mode.Air : Mode.Ground;
            }

            if (CurrentMode == Mode.Air)
            {
                UpdateAir(dt);
                return;
            }

            if (_input.ReadCameraToggle())
            {
                _cameraMode = (CameraMode)(((int)_cameraMode + 1) % 3);

                // A first-person eye sits centimetres from the turret interior, so the
                // default 0.2 m near plane would clip straight through it and show the
                // sky where the hull should be.
                _camera.nearClipPlane = _cameraMode == CameraMode.FirstPerson ? 0.05f : 0.2f;
            }

            _turretTrim = Mathf.Clamp(_turretTrim + _input.ReadTurretTrim() * 1.2f * dt, -1.2f, 1.2f);
            if (Mathf.Abs(_input.ReadTurretTrim()) < 0.01f) _turretTrim *= 0.9f;

            _aimPoint = GroundPointUnderMouse();

            float throttle = _input.ReadThrottle();
            float steer = _input.ReadSteer();

            _battle.Advance(dt, throttle, steer, _turretTrim,
                _input.ReadFire(), _aimPoint);

            // Interpolated render positions. Using the raw simulation positions made the
            // vehicles step at the simulation rate while the camera moved smoothly,
            // which reads as a distinct judder rather than as a subtle lack of
            // smoothness.
            _playerView.Place(_battle.PlayerRenderPosition, _battle.PlayerRenderHeadingRad,
                _battle.PlayerTurretYawRad, _battle.PlayerRenderGunPitchRad);
            _playerView.SpinTracks((throttle - steer * 0.5f) * 12f, (throttle + steer * 0.5f) * 12f, dt);

            if (_cameraMode == CameraMode.FirstPerson)
            {
                _cameraTransform.SetPositionAndRotation(
                    _playerView.GunnerEyeWorldPosition,
                    _playerView.GunnerEyeWorldRotation);
            }

            _enemyView.Place(_battle.EnemyRenderPosition, _battle.EnemyHeadingRad,
                _battle.EnemyTurretYawRad);
            _enemyView.SpinTracks(6f, 6f, dt);

            if (_muzzleFlashTimer > 0f)
            {
                _muzzleFlashTimer -= dt;
                if (_muzzleFlashTimer <= 0f)
                {
                    _muzzleFlash.SetActive(false);
                }
                else
                {
                    _muzzleFlash.transform.position = _playerView.MuzzleWorldPosition;
                    float flashScale = Mathf.Lerp(0.4f, 1.6f, _muzzleFlashTimer / 0.08f);
                    _muzzleFlash.transform.localScale = Vector3.one * flashScale;
                }
            }

            UpdateTracers();
            UpdateReticle();
            _gunsight.SetActive(false);
        }

        /// <summary>
        /// Camera moves here, after the simulation and every view have been placed
        /// for this frame.
        ///
        /// Doing it in <c>Update</c> left the camera half a frame behind the tank it
        /// was chasing. That matters more than it looks, because the camera is also
        /// what the mouse aim ray is cast from: the lag fed straight back into the
        /// aim point, so the turret chased a target that was itself moving because of
        /// the camera, and the whole loop juddered.
        /// </summary>
        private void LateUpdate()
        {
            if (_battle == null || _camera == null) return;

            // Ground mode only. Air mode places the chase camera itself at the end of
            // UpdateAir, and calling UpdateCamera as well left two writers on one
            // transform in the same frame. The result was the camera drifting toward
            // the tank's ground chase position while the aircraft flew away from it,
            // which reads as the view zooming out to nothing. It also dragged the
            // gunsight, which is parented to the camera, across the screen as a blank
            // quad.
            if (CurrentMode != Mode.Ground) return;

            UpdateCamera(Time.deltaTime);
        }

        /// <summary>Air mode: fly, aim, shoot, and swap aircraft.</summary>
        private void UpdateAir(float dt)
        {
            _air.Advance(dt,
                _airInput.ReadPitch(),
                _airInput.ReadRoll(),
                _airInput.ReadYaw(),
                _airInput.ReadThrottle(),
                _airInput.ReadFire());

            _playerPlane.Apply(_air.PlayerFlight);
            _enemyPlane.Apply(_air.EnemyFlight);

            UpdateAirTracers();

            _reticle.SetActive(false);

            // Gunsight follows the camera and turns green when the nose is on.
            float lead = _air.LeadAngleDeg(out bool onTarget);
            _gunsight.SetActive(true);
            var sightMat = (Material)_gunsight.GetComponent<Renderer>().sharedMaterial;
            sightMat.color = onTarget
                ? new Color(0.3f, 1f, 0.4f, 0.9f)
                : new Color(1f, 0.85f, 0.3f, 0.75f);

            // Chase camera behind and slightly above the aircraft, looking down the
            // nose line so the gunsight sits where the player expects it.
            WarArena.Air.FlightModel f = _air.PlayerFlight;
            Vector3 back = -f.Forward();
            Vector3 target = f.Position + back * 38f + Vector3.up * 9f;
            float k = 1f - Mathf.Exp(-5f * dt);
            _cameraTransform.position = Vector3.Lerp(_cameraTransform.position, target, k);

            Vector3 lookAt = f.Position + f.Forward() * 220f;
            _cameraTransform.rotation = Quaternion.Slerp(
                _cameraTransform.rotation,
                Quaternion.LookRotation(lookAt - _cameraTransform.position, Vector3.up),
                k);

            _lastLeadDeg = lead;
        }

        private float _lastLeadDeg;

        private void UpdateAirTracers()
        {
            var positions = _air.TracerPositions;
            int count = Mathf.Min(positions.Count, _airTracers.Count);
            for (int i = 0; i < _airTracers.Count; i++)
            {
                if (i < count)
                {
                    _airTracers[i].gameObject.SetActive(true);
                    _airTracers[i].position = positions[i];
                }
                else
                {
                    _airTracers[i].gameObject.SetActive(false);
                }
            }
        }

        /// <summary>
        /// Projects the mouse onto the ground plane. Falls back to a point in front
        /// of the tank when the ray does not hit it, so aiming never breaks.
        /// </summary>
        private Vector3 GroundPointUnderMouse()
        {
            Ray ray = _camera.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit, 500f))
            {
                return new Vector3(
                    Mathf.Clamp(hit.point.x, -BattleSim.ArenaHalfExtent, BattleSim.ArenaHalfExtent),
                    hit.point.y,
                    Mathf.Clamp(hit.point.z, -BattleSim.ArenaHalfExtent, BattleSim.ArenaHalfExtent));
            }
            if (Mathf.Abs(ray.direction.y) < 1e-4f)
            {
                return _battle.PlayerPosition + Vector3.forward * 40f;
            }
            float distance = -ray.origin.y / ray.direction.y;
            if (distance <= 0f || float.IsInfinity(distance))
            {
                return _battle.PlayerPosition + Vector3.forward * 40f;
            }
            Vector3 point = ray.origin + ray.direction * distance;
            return new Vector3(
                Mathf.Clamp(point.x, -BattleSim.ArenaHalfExtent, BattleSim.ArenaHalfExtent), 0f,
                Mathf.Clamp(point.z, -BattleSim.ArenaHalfExtent, BattleSim.ArenaHalfExtent));
        }

        private void HandlePlayerFired()
        {
            if (_muzzleFlash != null && _playerView != null)
            {
                _muzzleFlashTimer = 0.08f;
                _muzzleFlash.transform.position = _playerView.MuzzleWorldPosition;
                _muzzleFlash.transform.rotation = _cameraTransform != null ? _cameraTransform.rotation : Quaternion.identity;
                _muzzleFlash.transform.localScale = Vector3.one * 1.6f;
                _muzzleFlash.SetActive(true);
            }
        }

        private void UpdateTracers()
        {
            var positions = _battle.InFlightPositions;
            int count = Mathf.Min(positions.Count, _tracers.Count);

            for (int i = 0; i < _tracers.Count; i++)
            {
                if (i < count)
                {
                    _tracers[i].gameObject.SetActive(true);
                    _tracers[i].position = positions[i];
                }
                else
                {
                    _tracers[i].gameObject.SetActive(false);
                }
            }
        }

        private void UpdateReticle()
        {
            _reticle.transform.position = _aimPoint + Vector3.up * 0.05f;
            _reticle.transform.localScale = Vector3.one * 2.4f;
            _reticleMaterial.color = _battle.AimWouldPenetrate(_aimPoint)
                ? new Color(0.2f, 1.0f, 0.25f, 0.95f)
                : new Color(1.0f, 0.2f, 0.2f, 0.95f);
        }

        private void UpdateCamera(float dt)
        {
            // First person returns early, rigid, and deliberately bypasses the
            // smoothing below. A gunner's sight is bolted to the turret: if this
            // position were lerped toward the eye, the whole view would swim inside
            // the hull every time the gun traversed, which is precisely the artefact
            // a sight exists to avoid. Being rigid also means this method stays the
            // only writer of the camera transform in ground mode.
            if (_cameraMode == CameraMode.FirstPerson)
            {
                _cameraTransform.SetPositionAndRotation(
                    _playerView.GunnerEyeWorldPosition,
                    _playerView.GunnerEyeWorldRotation);
                return;
            }

            Vector3 target;
            if (_cameraMode == CameraMode.Chase)
            {
                Vector3 forward = new Vector3(
                    Mathf.Sin(_battle.PlayerRenderHeadingRad), 0f,
                    Mathf.Cos(_battle.PlayerRenderHeadingRad));
                target = _battle.PlayerRenderPosition - forward * 17f + Vector3.up * 8f;
            }
            else
            {
                target = _battle.PlayerRenderPosition + new Vector3(0f, 34f, -26f);
            }

            // Frame-rate independent smoothing. Lerping by a fixed fraction per frame
            // would move faster at high frame rates, which is its own kind of jitter.
            float k = 1f - Mathf.Exp(-6f * dt);
            _cameraTransform.position = Vector3.Lerp(_cameraTransform.position, target, k);

            // Look at the interpolated position too, so the tank holds still in frame
            // rather than sliding around inside it.
            _cameraTransform.LookAt(_battle.PlayerRenderPosition + Vector3.up * 2f);
        }

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (_hudStyle == null)
            {
                _hudStyle = new GUIStyle(GUI.skin.box);
                _helpStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12, alignment = TextAnchor.LowerCenter
                };
                _helpStyle.normal.textColor = new Color(0.95f, 0.97f, 1f);

                // The HUD sits directly on the world with no panel behind it, so every
                // label has to stay readable against a bright sky at one moment and
                // dark ground at the next. A near-white fill on a one-pixel black
                // outline holds up on both; the previous plain label colour vanished
                // into the sky.
                _outlineStyle = new GUIStyle(_helpStyle);
                _outlineStyle.normal.textColor = Color.black;
                _barBack = Solid(new Color(0.08f, 0.10f, 0.12f, 0.92f));
                _barFill = Solid(new Color(0.85f, 0.30f, 0.25f));
                _barHealthPlayer = Solid(new Color(0.22f, 0.80f, 0.35f, 0.95f));
                _barHealthEnemy = Solid(new Color(0.88f, 0.24f, 0.22f, 0.95f));
                _barReloadReady = Solid(new Color(0.22f, 0.88f, 0.35f, 0.95f));
                _barReloading = Solid(new Color(0.96f, 0.65f, 0.15f, 0.95f));
                _reticleOk = Solid(new Color(0.2f, 1.0f, 0.25f, 0.95f));
                _reticleBad = Solid(new Color(1.0f, 0.2f, 0.2f, 0.95f));
            }

            if (_battle == null || _air == null) return;

            if (CurrentMode == Mode.Air)
            {
                DrawAirHud();
                Label(new Rect(0f, Screen.height - 76f, Screen.width, 70f),
                    "W/S pitch    A/D roll    Q/E rudder    Shift/Ctrl throttle    SPACE fire    TAB ground/air\n" +
                    "Green gunsight = nose on target. You must LEAD a crossing aircraft.");
                return;
            }

            DrawGroundHud();
            Label(new Rect(0f, Screen.height - 76f, Screen.width, 70f),
                "W/S throttle   A/D steer   Q/E turret trim   SPACE / LMB fire   C camera view   TAB ground/air\n" +
                "Reticle: GREEN = Guaranteed Penetration | RED = Armor Too Thick / High Obliquity (Deflection)");
        }

        /// <summary>Ground-mode HUD: armour, reload, range, and the damage feed.</summary>
        private void DrawGroundHud()
        {
            float barWidth = Mathf.Min(320f, Screen.width * 0.26f);
            const float barHeight = 16f;
            const float top = 22f;

            // Player health bar & numerical HP
            GUI.DrawTexture(new Rect(30f, top, barWidth, barHeight), _barBack);
            GUI.DrawTexture(new Rect(30f, top, barWidth * Mathf.Clamp01(_battle.PlayerHealth / 100f), barHeight), _barHealthPlayer);

            // Enemy health bar & numerical HP
            float enemyX = Screen.width - 30f - barWidth;
            GUI.DrawTexture(new Rect(enemyX, top, barWidth, barHeight), _barBack);
            GUI.DrawTexture(new Rect(enemyX, top, barWidth * Mathf.Clamp01(_battle.EnemyHealth / 100f), barHeight), _barHealthEnemy);

            // Health labels with exact numerical HP
            Label(new Rect(30f, top - 20f, barWidth, 20f),
                $"{_battle.PlayerSpec.DisplayName}   HP {_battle.PlayerHealth:F0}/100   [K {_battle.Kills} / D {_battle.Deaths}]");
            Label(new Rect(enemyX, top - 20f, barWidth, 20f),
                $"{_battle.EnemySpec.DisplayName}   HP {_battle.EnemyHealth:F0}/100   [HOSTILE]");

            // Reload bar & status
            float reload = _battle.ReloadComplete
                ? 1f
                : 1f - _battle.ReloadRemaining / _battle.PlayerSpec.ReloadSeconds;
            GUI.DrawTexture(new Rect(30f, top + barHeight + 6f, barWidth, 6f), _barBack);
            GUI.DrawTexture(new Rect(30f, top + barHeight + 6f, barWidth * Mathf.Clamp01(reload), 6f),
                _battle.ReloadComplete ? _barReloadReady : _barReloading);

            string cannonStatus = _battle.ReloadComplete
                ? "CANNON READY [SPACE / LMB]"
                : $"RELOADING {_battle.ReloadRemaining:F1}s";

            // Vehicle dynamics & fire control telemetry
            float spd = _battle.PlayerSpeedKph;
            string gear = Mathf.Abs(spd) < 0.8f ? "NEUT" : (spd > 0.8f ? "FWD" : "REV");
            float range = Vector3.Distance(_battle.PlayerPosition, _battle.EnemyPosition);
            bool wouldPen = _battle.AimWouldPenetrate(_aimPoint);
            string aimText = wouldPen ? "PENETRATION LIKELY (GREEN)" : "DEFLECT / RICOCHET (RED)";

            Label(new Rect(30f, top + barHeight + 16f, 600f, 20f),
                $"{cannonStatus}   |   GEAR [{gear}]  SPD {spd:F0} km/h   |   DIST {range:F0}m");
            Label(new Rect(30f, top + barHeight + 36f, 600f, 20f),
                $"AIM: {aimText}   |   CAM {CameraModeName(_cameraMode).ToUpper()}");

            for (int i = 0; i < _battle.Feed.Count; i++)
            {
                Label(new Rect(Screen.width - 470f, 20f + i * 20f, 460f, 20f), _battle.Feed[i]);
            }
        }

        /// <summary>
        /// Air-mode HUD: the flight instruments a pilot actually needs, plus the
        /// two warnings that matter (stall and overstress).
        /// </summary>
        private void DrawAirHud()
        {
            WarArena.Air.FlightModel f = _air.PlayerFlight;
            float barWidth = Mathf.Min(300f, Screen.width * 0.24f);
            const float barHeight = 14f;
            const float top = 20f;

            GUI.DrawTexture(new Rect(30f, top, barWidth, barHeight), _barBack);
            GUI.DrawTexture(new Rect(30f, top, barWidth * (_air.PlayerHealth / 100f), barHeight), _barFill);

            float enemyX = Screen.width - 30f - barWidth;
            GUI.DrawTexture(new Rect(enemyX, top, barWidth, barHeight), _barBack);
            GUI.DrawTexture(new Rect(enemyX, top, barWidth * (_air.EnemyHealth / 100f), barHeight), _barFill);

            Label(new Rect(30f, top - 18f, 280f, 18f),
                _air.PlayerSpec.DisplayName + "   K " + _air.Kills + " / D " + _air.Deaths);
            Label(new Rect(enemyX - 60f, top - 18f, 300f, 18f), _air.EnemySpec.DisplayName);

            // Throttle bar, so the player can see the setting it cannot see.
            GUI.DrawTexture(new Rect(30f, top + barHeight + 6f, barWidth, 6f), _barBack);
            GUI.DrawTexture(new Rect(30f, top + barHeight + 6f, barWidth * f.Throttle, 6f), _barFill);

            float range = Vector3.Distance(f.Position, _air.EnemyFlight.Position);
            Label(new Rect(30f, top + barHeight + 18f, 520f, 20f),
                $"ALT {f.Altitude:F0} m   TAS {f.SpeedMs * 3.6f:F0} km/h   " +
                $"MACH {f.SpeedMs / 340f:F2}   RANGE {range / 1000f:F2} km");

            if (f.IsStalled)
            {
                Label(new Rect(Screen.width * 0.5f - 90f, Screen.height * 0.62f, 180f, 30f), "STALL");
            }
            if (f.IsOverstressed)
            {
                Label(new Rect(Screen.width * 0.5f - 90f, Screen.height * 0.66f, 180f, 30f), "OVERSTRESS");
            }

            for (int i = 0; i < _air.Feed.Count; i++)
            {
                Label(new Rect(Screen.width - 470f, 20f + i * 20f, 460f, 20f), _air.Feed[i]);
            }
        }

        /// <summary>Short HUD label for the active ground camera mode.</summary>
        private static string CameraModeName(CameraMode mode)
        {
            switch (mode)
            {
                case CameraMode.Orbit: return "orbit";
                case CameraMode.FirstPerson: return "FPV";
                default: return "chase";
            }
        }

        /// <summary>
        /// Draws a HUD label with a one-pixel black outline behind it.
        ///
        /// IMGUI has no outline on <see cref="GUIStyle"/>, so the same rect is drawn
        /// four times in black at the cardinal offsets before the real text is drawn
        /// on top. The labels float directly over the scene with no panel behind them,
        /// and the background behind them is a bright sky at the horizon and dark
        /// ground below it, so neither a light nor a dark text colour works on its own.
        /// </summary>
        private void Label(Rect rect, string text)
        {
            if (_outlineStyle == null) return;

            GUI.Label(new Rect(rect.x - 1f, rect.y - 1f, rect.width, rect.height), text, _outlineStyle);
            GUI.Label(new Rect(rect.x + 1f, rect.y - 1f, rect.width, rect.height), text, _outlineStyle);
            GUI.Label(new Rect(rect.x - 1f, rect.y + 1f, rect.width, rect.height), text, _outlineStyle);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, _outlineStyle);
            GUI.Label(rect, text, _helpStyle);
        }

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }
    }
}