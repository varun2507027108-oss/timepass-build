# War Arena — Unity — Complete Project Documentation

Comprehensive reference for the `war-arena-unity` project: engine, architecture,
file structure, every system, the **WebGL browser build pipeline**, test status,
known defects, and licensing.

> **Last updated:** after the first successful WebGL browser build and a batch of
> four fixes. See §2 (WebGL pipeline) and §3 (Tab regression, now fixed).

---

## 1. Overview

A **browser-first** WWII armored combat simulator with two playable domains in one
build:

- **Ground** — tank vs tank combat with realistic armour penetration
- **Air** — six fighter aircraft from prop fighters through early jets

Players open a link in Chrome or Edge and play with no download and no install. A
Windows standalone build is retained as a backup.

All geometry, lighting, sky and materials are **generated in code at runtime**. There
are no scene-authored prefabs, no imported vehicle models, and no hand-authored `.mat`
assets. The playable scene contains exactly one GameObject.

| Property | Value |
| --- | --- |
| Engine | **Unity 6** (6000.6.4f1, revision `12bfff696524`) |
| Render pipeline | **Built-in** (URP/HDRP **not** installed) |
| Colour space | **Gamma** (`m_ActiveColorSpace: 0`) |
| Build targets | Windows Standalone (backup) + **WebGL (primary, verified running)** |
| C# files | **28** |
| Lines of C# | **6,953** (5,116 scripts · 1,592 tests · 245 editor) |
| `.meta` files | 58 |
| Vehicles in catalogue | 19 |
| Aircraft in catalogue | 6 |
| Tests | 37 EditMode + 34 PlayMode = **71** — last green run predates today's changes |
| WebGL build size | **42.2 MB**, 17 files, ~4 min build, 0 errors |
| Third-party assets | 6 CC0 textures, 19.2 MB |

---

## 2. WebGL Build Pipeline (NEW)

This is the primary delivery target. Everything here was set up and verified working
in a real browser.

### 2.1 The two Unity installs — and why it mattered

There are **two** installs of the *same* version on this machine. Getting this wrong
cost real time, so it is recorded here:

| Install | Path | State |
| --- | --- | --- |
| **Working** | `C:\Users\varun\Unity\6000.6.4f1\Editor\Unity.exe` | **Use this.** Complete: `Data\Managed` and `Data\il2cpp` both present |
| Hub | `C:\Program Files\Unity\Hub\Editor\6000.6.4f1` | **Corrupt.** `Editor\Data\Managed` is missing *entirely*, so the Editor dies on launch with *"The code execution cannot proceed because Unity.dll failed to load"* |

The Hub install died mid-way (originally a disk-space failure) but it *did* retain a
complete **5.7 GB WebGL module**, which the working install lacked.

**Resolution:** copied the module folder from the corrupt install into the working one:

```powershell
robocopy `
  "C:\Program Files\Unity\Hub\Editor\6000.6.4f1\Editor\Data\PlaybackEngines\WebGLSupport" `
  "C:\Users\varun\Unity\6000.6.4f1\Editor\Data\PlaybackEngines\WebGLSupport" /E
```

25,228 files / 5,748 MB, ~36 s. Unity detects installed modules by **folder presence**
— there is no `modules.asset` registry file in either install — so the copy was
sufficient. Verified afterwards with `BuildPipeline.IsBuildTargetSupported`.

> **Note:** the corrupt Hub install is still on disk. It can be deleted from an
> elevated prompt if desired, but do so **after** confirming you no longer need the
> WebGL module payload from it.

### 2.2 Build tooling — `Assets/Editor/WebBuildSetup.cs`

Idempotent tooling so the browser build is reproducible from the CLI. Two entry points:

| Method | Purpose |
| --- | --- |
| `WebBuildSetup.Run()` | Registers the scene, guarantees runtime shaders are included, reports module status |
| `WebBuildSetup.BuildWeb()` | Applies player settings, switches target, builds, prints a report |

Both are callable headless via `-executeMethod`.

### 2.3 Scene registration

`Kursk.unity` **was not in Build Settings** (`m_Scenes: []`), so any player build
contained nothing. `Run()` registers it at index 0:

```yaml
m_Scenes:
- enabled: 1
  path: Assets/Scenes/Kursk.unity
  guid: 6dc22574157c2ea4fbbcf9ed5197f0c8
```

### 2.4 Shader stripping — the fix that made it visible at all

**Every material in this project is constructed at runtime** via `Shader.Find`
(`VisualMaterials.cs:64-79`, `ShellImpactManager.cs:87-112`). The build strips any
shader not statically referenced by a scene or asset. The scene holds only
`WarArenaBootstrap`, so nothing pulled them in.

```
Editor:   Shader.Find("Standard")  → found, material renders correctly
WebGL:    Shader.Find("Standard")  → null, every tank/tracer/skybox magenta
```

`m_AlwaysIncludedShaders` now contains, in addition to Unity's 7 defaults:

| Shader | fileID | Used by |
| --- | --- | --- |
| `Standard` | **46** | `VisualMaterials.Lit` — every opaque surface |
| `Unlit/Color` | **10755** | `VisualMaterials.Unlit` — tracers, exhaust, HUD |
| `Skybox/Procedural` | **106** | `VisualMaterials.BuildSkybox` |

These IDs were **resolved by Unity through the asset database**, not written by hand —
builtin shader fileIDs differ per editor version and a stale ID is silently ignored
rather than reported. The tool skips any shader already present, so re-running is safe.

### 2.5 Collider stripping — `Assets/link.xml`

IL2CPP managed stripping removed the 3D collider types, because the code only touches
them generically through `GameObject.CreatePrimitive` and `GetComponent<Collider>()`
and never names a concrete type.

```
Can't add component because class 'SphereCollider' doesn't exist!
Can't add component because class 'MeshCollider' doesn't exist!
```

This fired **once per spawned primitive**. It was easy to miss because the mesh still
renders and hit detection here is analytic — it never queries a collider. Anything that
does depend on them, notably `WheelCollider`, was simply unavailable in the browser.

`Assets/link.xml` preserves, under `UnityEngine.PhysicsModule`:
`BoxCollider`, `SphereCollider`, `CapsuleCollider`, `MeshCollider`, `WheelCollider`,
`TerrainCollider`, `CharacterController`, `Rigidbody`.

**Verified:** after the rebuild the console contains zero `'class does not exist'`
errors.

### 2.6 Player settings applied

| Setting | Value | Why |
| --- | --- | --- |
| `compressionFormat` | **Disabled** | Deliberate for this first build — see below |
| `dataCaching` | `true` | IndexedDB cache for repeat visits |
| `debugSymbolMode` | `Off` | Symbols are megabytes nobody ships |
| `powerPreference` | `HighPerformance` | Discrete GPU where present |
| `ManagedStrippingLevel` | **Low** | Safe with NGO's reflection-heavy ILPP |

**Why compression is off.** A Brotli build is only servable by a host that returns
`Content-Encoding: br`. A page that fails to load under Brotli looks identical to a
genuine graphics failure, which makes it a terrible first debugging step. Validate
uncompressed first, then enable Brotli once the deployment host is known to set the
header. **This is still outstanding for deployment.**

### 2.7 Build output

`Builds/WebGL` — 42.2 MB across 17 files, ~4 min:

| File | Size | Notes |
| --- | --- | --- |
| `index.html` | 6.4 KB | Loader; references `Build/` paths |
| `Build/WebGL.wasm` | 21.68 MB | `application/wasm` |
| `Build/WebGL.data` | 19.99 MB | Scene, scripts, textures |
| `Build/WebGL.framework.js` + `loader.js` | small | Runtime + loader |
| `TemplateData/` | small | Styles, favicon, icons |

### 2.8 Serving it — the headers matter

A WebGL build **cannot** be opened via `file://` or by double-clicking `index.html`.
It must be served over HTTP with:

- `Cross-Origin-Opener-Policy: same-origin`
- `Cross-Origin-Embedder-Policy: require-corp`
- `Content-Type: application/wasm` for `.wasm`

A minimal Python server implementing this is at
`%TEMP%\opencode\unity_web_server.py`. **It currently lives outside the repo** — it
should be moved to `Tools/` so it is version-controlled and reusable for deployment.

```powershell
& "C:\Users\varun\AppData\Local\Programs\Python\Python313\python.exe" `
  "$env:TEMP\opencode\unity_web_server.py" `
  "C:\Users\varun\Projects\browser-games\war-arena-unity\Builds\WebGL" 8099
```

Then open **http://127.0.0.1:8099/**.

Unity also ships its own: `WebGLSupport\BuildTools\SimpleWebServer.exe`.

### 2.9 Verified working in a real browser

Confirmed in Chrome with a real GPU (not headless):

- Procedural skybox, sun, atmospheric gradient — the shader fix held
- Textured ground and road, ruins, craters, cast shadows
- Panzer IV with hull, turret, barrel, tracks
- HUD live: `SPD · RANGE · CAM`, and a hit feed showing
  `HIT TAKEN — penetrated rear @ 60deg`, so ballistics work too

**False alarm worth recording:** a headless Chrome + SwiftShader run produced a flat
grey canvas and looked like a total failure. It was a software-rasteriser artifact. Real
GPU rendering was correct all along.

---

## 3. Recently Fixed — Tab Camera Regression

**Status: FIXED in code. Not yet runtime-verified — please press `Tab` in the browser.**

This was previously the top known defect. While fixing view judder, the camera update
was moved from `Update()` into a new `LateUpdate()`:

```csharp
// Assets/Scripts/Gameplay/WarArenaBootstrap.cs
private void LateUpdate()
{
    if (_battle == null || _camera == null) return;
    if (CurrentMode != Mode.Ground) return;   // <-- the fix
    UpdateCamera(Time.deltaTime);
}
```

**Root cause — two writers on one transform.** `UpdateCamera()` is the *ground*
camera and snaps to the tank. `UpdateAir()` sets the *air* camera in the same frame.
With no mode guard, both wrote `_cameraTransform` every frame; the tug-of-war read as
the camera "zooming out". The gunsight is a Quad **parented to the camera**, so it
stayed welded on while the world swung behind it — that was the blank square.

**Why no test caught it.** `GunsightFollowsTheAircraftInAirMode` asserts
`gunsight.activeSelf` is true and the reticle hidden. It passes, because the gunsight
*is* activated. Nothing asserted *where the camera is*.

**Still outstanding:** add a test asserting camera position per mode, so this class of
bug cannot recur. Blocked by §10.

### 3.1 Known latent bug — impact tracers request a shader that does not exist

`Assets/Scripts/Net/ShellImpactManager.cs:87-91`:

```csharp
r.material = shell.Type == ShellType.HEAT
    ? new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = ... }
    : new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = ... };
```

Both ternary branches are identical, and both request a **URP** shader with no
`Standard`/`Unlit/Color` fallback. URP is not installed, so `Shader.Find` returns null
and `new Material(null)` is constructed. `SpawnImpactSpark` (line 112) has the same
problem.

**Latent, not active:** `ShellImpactManager` is a `NetworkBehaviour` reached only via
`NetworkVehicle`, and `Kursk.unity` contains **no NGO components at all**. It only
matters once multiplayer is wired up — which is exactly when it would be hardest to
diagnose. `VisualMaterials` does this correctly, with a fallback; these call sites
bypass it.

---

## 4. Engine and Environment

### Unity install

| | |
| --- | --- |
| Version | 6000.6.4f1 (`12bfff696524`) |
| **Working path** | `C:\Users\varun\Unity\6000.6.4f1\Editor\Unity.exe` |
| WebGL module | Installed at `…\6000.6.4f1\Editor\Data\PlaybackEngines\WebGLSupport` |
| Hub path | `C:\Program Files\Unity\Hub\Editor\6000.6.4f1` — corrupt, see §2.1 |

### Packages

```json
"com.unity.inputsystem": "1.20.1",
"com.unity.netcode.gameobjects": "2.13.3",
"com.unity.modules.animation", "audio", "physics", "ui", "uielelements",
"vehicles", "unitywebrequest", "unitywebrequestwww"  — all 1.0.0
```

**Not installed:** URP, HDRP, Addressables, terrain, particlesystems, any
post-processing package.

### Quality settings

Active level is index 5 = **"Ultra"**: 4 shadow cascades, 150 m shadow distance,
**2× MSAA** (overridden to 4× at runtime), vSync 1. Target frame rate 60.

WebGL selects level 3 = "High": 2 cascades, 40 m shadow distance, no MSAA.

**Not yet done:** a WebGL-specific quality ladder. Integrated-GPU laptops are the
target audience and this has not been measured — see §15.

### Critical pipeline fact

The project runs **built-in** rendering, but material code was historically written
against **URP property names**. Every legitimate call site has a fallback:

```csharp
Shader shader = Shader.Find("Universal Render Pipeline/Lit");
if (shader == null) shader = Shader.Find("Standard");
```

So `Standard` is what actually renders. This caused several silent bugs, all now
fixed — see §9. `ShellImpactManager` is the one place that skipped the fallback (§3.1).

---

## 5. File Structure

```
war-arena-unity/
├── Assets/
│   ├── Artwork/
│   │   └── CREDITS.md                 Third-party licences, what to avoid
│   ├── Editor/
│   │   ├── SceneBuilder.cs       42   [MenuItem] playable-scene generator
│   │   └── WebBuildSetup.cs     203   NEW — WebGL scene/shader/build tool
│   ├── link.xml                        NEW — IL2CPP collider preservation
│   ├── DefaultNetworkPrefabs.asset     NGO prefab list (currently empty)
│   ├── Resources/
│   │   └── Artwork/                     CC0 scan textures (Resources folder so the
│   │       ├── Ground/                  code-built scene can load them by path)
│   │       │   ├── brown_mud_02_diff_2k.jpg
│   │       │   └── brown_mud_02_norm_2k.jpg
│   │       ├── Road/
│   │       │   ├── gravel_road_diff_2k.jpg
│   │       │   └── gravel_road_norm_2k.jpg
│   │       └── Rubble/
│   │           ├── rubble_diff_2k.jpg
│   │           └── rubble_norm_2k.jpg
│   ├── Scenes/
│   │   └── Kursk.unity           1 GameObject: WarArenaBootstrap
│   ├── Scripts/
│   │   ├── WarArena.Scripts.asmdef
│   │   ├── Air/
│   │   │   ├── AircraftCatalog.cs     321   6 airframes, all performance data
│   │   │   └── FlightModel.cs         607   forces, stability, trim, integrator
│   │   ├── Ballistics/
│   │   │   ├── Shell.cs               321   ordnance, de Marre, normalisation
│   │   │   └── Penetration.cs         225   plate resolution, ricochet, spall
│   │   ├── Gameplay/
│   │   │   ├── WarArenaBootstrap.cs   558   root orchestrator, HUD, cameras
│   │   │   ├── BattleSim.cs           468   ground simulation (tanks)
│   │   │   ├── AirCombatSim.cs        234   air combat, tracers, health
│   │   │   ├── BattlefieldBuilder.cs  220   ground, ruins, trees, light, camera
│   │   │   ├── VisualMaterials.cs     397   shared material/texture/sky factory
│   │   │   ├── AircraftView.cs        217   procedural aircraft geometry
│   │   │   ├── TankView.cs            138   procedural tank geometry
│   │   │   ├── ITankInputReader.cs     49   input contracts (tank + aircraft)
│   │   │   ├── KeyboardTankInput.cs    72   real tank controls
│   │   │   └── KeyboardAircraftInput.cs 78 real aircraft controls
│   │   ├── Vehicles/
│   │   │   ├── VehicleCatalog.cs      310   19 historical vehicles
│   │   │   ├── VehicleDamageModel.cs  280   module + crew damage resolution
│   │   │   ├── TankController.cs      190   WheelCollider physics (for prefabs)
│   │   │   ├── DamageModule.cs        102   damageable subsystems
│   │   │   └── VehicleCatalogPhysics.cs 44  catalogue → controller bridge
│   │   └── Net/
│   │       ├── NetworkVehicle.cs      165   server-authoritative vehicle
│   │       └── ShellImpactManager.cs  120   server ballistics replication
│   └── Tests/
│       ├── EditMode/
│       │   ├── WarArena.EditTests.asmdef
│       │   ├── FlightModelTests.cs            293   14 tests
│       │   ├── TurretAimingTests.cs           244    8 tests
│       │   └── NormalisationAndRicochetTests.cs 309  15 tests
│       └── PlayMode/
│           ├── WarArena.PlayTests.asmdef
│           ├── TankControlsPlayModeTests.cs  426   17 tests
│           └── RenderSetupTests.cs           320   17 tests
├── Builds/
│   └── WebGL/                    NEW — 42.2 MB browser build output
├── Tools/
│   └── SimVerify/                headless .NET harness (see §12 — SDK missing)
├── Packages/manifest.json
├── ProjectSettings/
├── PROJECT-DOCUMENTATION.md      this file
└── README.md
```

**28 C# files, 6,953 lines, 58 `.meta` files.**

### Assembly definitions

| Assembly | References |
| --- | --- |
| `WarArena.Scripts` | Unity.InputSystem, Unity.Netcode.Runtime, Unity.Networking.Transport |
| `WarArena.EditTests` | WarArena.Scripts, InputSystem, TestRunner, nunit.framework.dll |
| `WarArena.PlayTests` | WarArena.Scripts, InputSystem, TestRunner, nunit.framework.dll |

Both test assemblies set `overrideReferences: true`, so a missing entry is a hard
compile error rather than a warning.

### Working notes

- **`Library/` was deleted** during troubleshooting (§10). It is a regenerable import
  cache — no source is affected. **The next Editor open does a full reimport, ~1 min.**
- The **active build target is now WebGL**. Tests still run in the Editor, but you must
  switch back to Windows under `File → Build Profiles` to produce a `.exe`.

---

## 6. Ground Systems

### BattleSim — the tank simulation

Pure, deterministic, Unity-free apart from `Mathf`/`Vector3`. 30 Hz fixed timestep
with a rolling accumulator held across frames.

```csharp
public const float TickHz = 30f;
public const float FixedDt = 1f / TickHz;
public const float ArenaHalfExtent = 90f;      // 180 x 180 m arena
public const float TurretSlewRate = 2.6f;     // rad/s
public const float MaxTraverse = 2.1f;         // 120 deg each way
private const float MaxSpeedMps = 12f;         // 43 kph
private const float MaxTurnRateRad = 1.8f;
private const float HullAutoAlignStrength = 0.35f;
```

**Render interpolation** — added to fix judder. Vehicles step at the fixed rate while
the camera moves smoothly, so the drawn position is interpolated:

```csharp
public void Advance(float frameDelta, ...)
{
    PlayerPositionPrev = PlayerPosition;
    EnemyPositionPrev = EnemyPosition;
    PlayerHeadingPrevRad = PlayerHeadingRad;

    _accumulator += Mathf.Min(frameDelta, 0.1f);
    while (_accumulator >= FixedDt && steps < MaxStepsPerFrame) { Step(...); }

    InterpolationAlpha = steps == 0 ? 1f : Mathf.Clamp01(_accumulator / FixedDt);
}

public Vector3 PlayerRenderPosition =>
    Vector3.Lerp(PlayerPositionPrev, PlayerPosition, InterpolationAlpha);
```

Heading interpolation is angle-aware via `Mathf.DeltaAngle`, because heading is
wrapped to ±π and a plain lerp would spin the tank the long way round.

**Hull follows the gun line.** Driving pulls the hull toward the gun's heading, which
is what real tracked vehicles and every tank game does:

```csharp
float moving = Mathf.Abs(throttle) > 0.01f ? 1f : 0f;
float gunWorldHeading = WrapAngle(PlayerHeadingRad + PlayerTurretYawRad);
float toGunLine = WrapAngle(gunWorldHeading - PlayerHeadingRad);
float demand = (toGunLine * HullAutoAlignStrength * moving) + steer;
```

**Turret aiming** — from the turret ring, not the hull centre; clamped *after* trim:

```csharp
Vector3 turretPivot = PlayerPosition + TurretPivotOffset(PlayerHeadingRad);
Vector3 delta = aimPoint - turretPivot;
float wanted = WrapAngle(Mathf.Atan2(delta.x, delta.z) - PlayerHeadingRad);
wanted += trim;
wanted = Mathf.Clamp(wanted, -MaxTraverse, MaxTraverse);   // clamp AFTER trim
PlayerTurretYawRad = Slew(PlayerTurretYawRad, wanted, TurretSlewRate * dt);
PlayerTurretYawRad = Mathf.Clamp(PlayerTurretYawRad, -MaxTraverse, MaxTraverse);
```

### Shell / Penetration

**Jacob de Marre** governs penetration decay:

```csharp
public float PenetrationAt(float distanceM, float impactAngleDeg)
{
    float v = VelocityAt(distanceM);
    float caliberCubed = Cube(CaliberMm);
    float raw = MassKg * v * v / caliberCubed;
    float zeroReference = MassKg * MuzzleVelocity * MuzzleVelocity / caliberCubed;
    float ratio = Mathf.Pow(raw / zeroReference, 1f / 3f);
    return Penetration0Mm * ratio;   // obliquity handled by the plate
}
```

**Normalisation belongs to the plate, not the projectile.** This is the subtle part:

```csharp
public float EffectiveThickness(Shell shell, float impactAngleDeg)
{
    // A shaped-charge jet collapses onto the plate normal however oblique.
    if (shell.IgnoresSlope) return ThicknessMm * MaterialCoefficient;

    float angle = Mathf.Abs(impactAngleDeg);
    float effectiveAngle = Mathf.Max(0f, angle - shell.NormalisationDeg);
    return ThicknessMm * MaterialCoefficient * Shell.SlopeMultiplier(effectiveAngle);
}
```

| Family | Normalisation | Ricochet limit |
| --- | --- | --- |
| APBC (blunt Soviet) | +6° | 73° |
| APHEBC | +5° | 71° |
| APHE | +4° | 70° |
| APC | +3° | 68° |
| APCR (sharp rod) | −2° | 60° |
| HEAT | 0 (ignores slope) | never bounces |
| HE | 0 | never bounces |

**Cast hull resistance:** `MaterialCoefficient` is 1.10 for cast, 1.00 for rolled.
Panther and both Tigers are `IsCastHull = true`; T-34 and Sherman are false — the
T-34's famous sloped glacis is welded rolled plate.

**Drag coefficients** (recently corrected — see §9):

```csharp
public const float TypicalTankRoundDrag = 0.00013f;
public const float SubCalibreDrag       = 0.00008f;
public const float SolidShotDrag        = 0.00016f;
```

---

## 7. Air Systems

### Aircraft roster

| Aircraft | BR | Class | Top speed | Trim | Stall @3000m |
| --- | --- | --- | --- | --- | --- |
| Bf 109 G-6 | 5.7 | Prop | 178 m/s | 150 | 59.6 m/s |
| Spitfire Mk IX | 6.0 | Prop | 205 m/s | 145 | 71.6 m/s |
| P-51D | 6.3 | Prop | 220 m/s | 155 | 59.3 m/s |
| A6M5 Zero | 4.3 | Prop | 175 m/s | 130 | 52.4 m/s |
| Me 262 A-1a | 6.7 | Jet | 244 m/s | 191 | 75.3 m/s |
| Meteor F.4 | 6.7 | **Prop** | 201 m/s | 149 | 80.0 m/s |

The Meteor F.4 is deliberately `PropellerDriven = true` — famously the exception: a
jet-engined aircraft with a *piston* engine (Derwent 8), which is exactly why it could
turn with the 262 rather than merely outrun it.

### FlightModel — forces, not a speed controller

- **Lift:** `CL = 2πα`, capped at `CLmax` 1.3
- **Induced drag:** `D = L²/(q·S·π·e·AR)`, aspect ratio from real span
- **Parasite drag:** per-airframe calibrated coefficient
- **Prop thrust:** `power × efficiency ÷ airspeed`, capped at static thrust. A
  propeller makes ~12 kN on the takeoff roll and ~3 kN at 200 m/s; treating it as
  constant made the Me 262 *lose* to a P-51D
- **Jet thrust:** constant with speed, only mildly altitude-dependent
- **Stall:** lift collapse past 16°, with the trim target clamped below it

**Trim as a bounded commanded climb angle** — surplus thrust buys an *angle*, not a
raw pitch rate:

```csharp
float surplusThrust = ThrustAt(density, speed) * Throttle
                     - DragAt(density, speed, wingArea);

float commandedClimb = Mathf.Clamp(
    -surplusThrust * surplusGain,
    -MaxTrimDiveAngleDeg, MaxTrimClimbAngleDeg);   // 8 deg down, 4 deg up

trimPitch = (flightPathAngle - commandedClimb) * FlightPathDampingGain;
```

Two corrections that matter:

```csharp
// Sign convention: Unity's Euler order makes a POSITIVE pitch nose-DOWN.
float stabilityPitch = -(trimAngleOfAttack - angleOfAttack) * PitchStabilityGain;

// AoA: measure nose against flight path. The reverse order reports a nose-up
// attitude as negative, which drives lift downward.
angleOfAttack = Vector3.SignedAngle(Forward(), flightPath, Right());
```

**Integrator stability** — explicit Euler on lift is only conditionally stable, since
lift grows with v². Both clamps sit far above any real flight condition:

```csharp
private const float MaxVelocityChangePerStepMs = 8f;   // per-step guard
// ... and a hard ceiling at NeverExceedSpeedMs * 1.5f
```

---

## 8. Rendering

### Lighting

| | |
| --- | --- |
| Sun | `Color(1.0, 0.90, 0.74)`, **27° elevation**, 142° azimuth |
| Shadows | Soft, `shadowBias 0.02`, `shadowNormalBias 0.35`, strength 0.78 |
| Ambient | Trilight — sky `(0.36,0.43,0.56)`, equator `(0.30,0.30,0.28)`, ground `(0.16,0.15,0.11)` |
| Fog | Exponential squared, density `0.0055`, colour `(0.52,0.55,0.60)` |
| Sky | `Skybox/Procedural`, sun disc matched to the directional light |
| Anti-aliasing | 4× MSAA (overridden at runtime) |

The sun angle is the single highest-impact number. Previously rotation was left at
identity, pointing **straight down**: no lit/shaded sides, no volume, shadows pooling
under objects.

### VisualMaterials — one factory, both pipelines

Writes **both** URP and built-in property names because setting an absent property is
a silent no-op:

```csharp
material.SetFloat(SmoothnessId, smoothness);   // URP
material.SetFloat(GlossinessId, smoothness);   // built-in Standard
```

Transparency needed the most care — built-in `Standard` hides it behind `_Mode`:

```csharp
material.SetFloat("_Mode", 3f);                                    // transparent
material.EnableKeyword("_ALPHABLEND_ON");   // required or blend state is ignored
material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
material.SetInt("_ZWrite", 0);
```

### Materials by intent

| Surface | Treatment |
| --- | --- |
| Hull, walls, foliage | Matte, low gloss |
| Tracks, road wheels, barrels | Metallic 0.8, glossier — catch the low sun |
| Aircraft skin | Matte aluminium, soft sky sheen |
| Canopy glazing | Genuinely transparent, gloss 0.95 |
| Tracers, jet exhaust | Emissive |
| Reticle, gunsight | Unlit — the sun cannot dim the aiming cue |

### HUD legibility

IMGUI labels float directly over the scene with no panel behind them, and the
background behind them is a bright sky at the horizon and dark ground below it.
Neither a light nor a dark text colour works on its own.

`WarArenaBootstrap.Label()` draws the same rect four times in black at the cardinal
one-pixel offsets, then the real text on top:

```csharp
_helpStyle.normal.textColor = new Color(0.95f, 0.97f, 1f);   // near-white fill
_outlineStyle = new GUIStyle(_helpStyle);
_outlineStyle.normal.textColor = Color.black;

GUI.Label(new Rect(r.x - 1f, r.y - 1f, r.width, r.height), text, _outlineStyle);
GUI.Label(new Rect(r.x + 1f, r.y - 1f, r.width, r.height), text, _outlineStyle);
GUI.Label(new Rect(r.x - 1f, r.y + 1f, r.width, r.height), text, _outlineStyle);
GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, _outlineStyle);
GUI.Label(r, text, _helpStyle);
```

Styles are built once in `OnGUI` (`WarArenaBootstrap.cs:504-516`). All **12** label
call sites route through `Label()` — ground HUD, air HUD, damage feeds, stall and
overstress warnings, both control legends.

### Procedural fallback

CC0 textures load from `Resources/Artwork/` by path. If absent, `VisualMaterials.Textured`
falls back to a generated **seamless** value-noise texture (every octave wraps its
lattice by its own period, removing the grid seam). `VisualMaterials.ArtworkPresent`
reports which path is live — so the simulation still builds and tests without art.

### Global state restoration

`RenderSettings` and `QualitySettings` are global, not per-scene, and the PlayMode
suite builds a full bootstrap 34 times. Captured in `Awake`, restored in `OnDestroy`.

---

## 9. Bugs Found and Fixed

Every one of these compiled cleanly, threw nothing, and rendered a plausible frame.
An exception-based test would have passed all of them.

### Rendering (silent)

1. **All materials written against the wrong pipeline.** URP names, built-in
   pipeline. `_Smoothness` discarded (Standard calls it `_Glossiness`) — every surface
   at default gloss.
2. **Transparency never worked at all.** Six URP blend properties that don't exist on
   `Standard`. Canopy, prop disc, exhaust, gunsight all configured transparent and all
   rendering **opaque**.
3. **Sun pointed straight down.** A directional light's *rotation* is its beam
   direction; only a position was set.
4. **Sky never drawn.** Camera cleared to flat navy.
5. **No shadow bias tuned** on long thin primitives.
6. **Tracks and barrels painted, not metal.**
7. **`RenderSettings` leaked** between the 34 test bootstraps.

### Flight model (silent)

8. **Angle of attack had inverted sign.** `Vector3.SignedAngle` arguments reversed, so
   nose-up read as negative and lift was computed *downward*.
9. **No pitch stability.** Pitch only changed from input, so lift vanished at cruise.
10. **Body axes not perpendicular.** Independent Euler rotations don't commute; lift
    disagreed with the nose.
11. **Prop thrust constant.** Me 262 lost to a P-51D — inverting the historical point.
12. **Induced drag ignored aspect ratio.**
13. **Trim commanded stalls** — the clamp sat *above* the stall angle.
14. **Integrator unstable** — jets reached 3165 m/s at the ceiling.
15. **Meteor modelled as a jet** when it had a piston engine.
16. **Surplus thrust bought unbounded pitch** — every jet spiralled to a stall.
17. **Turret trim applied after the arc clamp**, letting it exceed `MaxTraverse`.

### Ballistics (silent)

18. **Every shell was dead within a kilometre.** Drag coefficients ~15× too high: an
    88 mm round modelled at **134 m/s at 1000 m** where history says ~**800 m/s**. The
    entire engagement model was flat. Found by a test asserting the wrong premise,
    not by playtesting.
19. **The penetration reticle could lie.** It duplicated the effective-thickness maths
    inline and had drifted from real hit resolution, ignoring cast hulls and range
    falloff — so it showed green on shots that bounced.
20. **One global ricochet angle** for all shells.
21. **Normalisation missing entirely.**

### Controls (player-reported)

22. **Turret felt inverted** — mathematically correct, felt backwards. The gun tracked
    the mouse in *world* space while the hull kept its own heading, so the turret
    swung opposite the tank every time you turned. Fixed by steering the hull onto the
    gun's line.
23. **Everything juddered** — vehicles drawn at raw sim position while camera moved
    smoothly, plus camera in `Update` leaving it half a frame behind (and the aim ray
    cast from the camera, so the lag fed back into the aim point). Fixed with render
    interpolation + `LateUpdate`.
24. **Tab camera regression** — *introduced* by fix 23, then fixed by the mode guard in
    §3.

### WebGL build (only visible in a player)

25. **Empty build scene list.** `m_Scenes: []` meant a player build contained nothing.
26. **Runtime shaders stripped.** Every material is built through `Shader.Find`, so the
    player lost `Standard`, `Unlit/Color` and `Skybox/Procedural` — the browser build
    would have been entirely magenta. Fixed via `m_AlwaysIncludedShaders` (§2.4).
27. **3D collider types stripped by IL2CPP.** `SphereCollider` and `MeshCollider`
    vanished, logging *"Can't add component because class 'SphereCollider' doesn't
    exist!"* once per primitive. Invisible in the Editor, because stripping only
    happens at build time. Fixed via `Assets/link.xml` (§2.5).
28. **HUD text unreadable against the sky.** Dark orange labels vanished at the
    horizon. Fixed with outlined text (§8).
29. **Trees rendered as bare poles.** Crowns were 1.7 m wide and 6 m tall, so at
    tree-line distance the silhouette collapsed into a vertical bar. Now squashed
    spheres, 2.4 × 1.9 m, at the same draw-call cost. *Improved, but still slightly
    lollipop-like.*

---

## 10. Test Suite

**Last green run: 37 EditMode + 34 PlayMode = 71 tests, all passing**, verified four
consecutive times. That run **predates today's changes**, so the HUD and camera edits
are currently unverified by automation.

### Current blocker — `-runTests` hangs

```
[Indexing] Starting Initial Indexing for Assets
```

then one core pinned at 100% indefinitely. Reproduced three times. What was ruled out:

| Hypothesis | Result |
| --- | --- |
| Compile error in the new code | **No** — assemblies load clean, no `error CS` |
| `-nographics` blocking texture import | **No** — still hangs with a real graphics device |
| Corrupt `Library` | **No** — deleted it entirely, 0.9 GB, still hangs |
| Active target = WebGL | **No** — `Library` deletion reset it to the default, still hangs |
| Project data problem | **No** — asset imports complete cleanly, all workers report ready |
| Batchmode itself broken | **No** — `-batchmode -quit` exits cleanly in 20 s |
| `-executeMethod` broken | **No** — both WebGL builds ran through it successfully |

So it is specifically the **Test Framework's initialisation deadlocking against the
asset-index subsystem** in this editor install. No accelerator is running, and no
`modules.asset` / preference anomaly was found.

**Workaround that should work:** run the suites from the Editor's Test Runner window
(`Window → General → Test Runner`). GUI mode very likely does not take the batchmode
indexing path. Close the Editor first when using batchmode — an open editor holds the
project lock and exits with code 1 and no output, which looks identical to a failure.

### EditMode — 37

**FlightModelTests (14)** — catalogue consistency; jets outrun props; Meteor out-turns
the 262; Zero turns best; prop thrust falls off with altitude but jets don't; stall;
climbing trades speed for height; all six aircraft hold altitude hands-off and settle
well clear of stall; terrain/ceiling respected; finite under abuse; roll bounded.

**TurretAimingTests (8)** — barrel points at the aim point on every bearing *via dot
product, so it cannot be fooled by a sign convention*; stops at its arc limit; hull
follows the gun line; stationary tank holds traverse; manual steering intact;
interpolation neither steps nor runs ahead.

**NormalisationAndRicochetTests (15)** — normalisation direction; it reduces the
*plate*; worthless on flat plate; worth more when steeper; denormalisation can't invert
it; HEAT ignores slope; per-shell ricochet; cast hulls; historical matchups.

### PlayMode — 34

**TankControlsPlayModeTests (17)** — 10 ground behaviours (throttle, reverse, hull
steering, turret slew independence, camera toggle, shells in flight, reload gating,
arena clamping, enemy driven by sim, 600-frame soak) + 7 air behaviours (mode switch,
altitude spawn, pitch/roll/throttle response, soak, gunsight swap).

**RenderSetupTests (17)** — glossiness actually arrives; transparency configured; sun
angled; sun warm and registered; skybox rendered; CC0 artwork loads; bump mapping;
textures tile; procedural fallback; fog; MSAA; texture seamlessness; steel vs paint;
canopy transparency; HUD unlit; global state restored.

### Known test gaps

- **No test asserts where the camera is.** This is precisely why the Tab regression
  (§3) shipped green. Highest-value test to add.
- No test asserts gunsight *position*, only visibility.
- **No test covers the WebGL build.** Shader inclusion, collider preservation and
  platform switching are all verified by hand, not automatically.
- `dotnet` SDK is **not installed**, so `Tools/SimVerify` cannot run. The README's
  "26/26 pass" for that harness is **stale and unverified**.

---

## 11. Multiplayer

Server-authoritative, because WheelCollider state is manipulable and client-reported
hits are a trivial cheat.

```
Client                      Server (host)
  │ input intent ──────────▶ NetworkVehicle.SubmitInput
  │                            ServerTick @ 30 Hz
  │                              TankController.Drive
  │                              DamageModel.ApplyShellImpact
  │                            ShellImpactManager.ServerSpawnShell
  │ ◀── NetworkVariable sync ─── speed / hull / fire / kill
```

Only derived values replicate; per-module HP stays server-side so clients cannot map
an enemy armour layout from traffic.

**Not implemented.** No playable multiplayer mode exists — the NGO classes are
scaffolding. `DefaultNetworkPrefabs.asset` has an empty list, and `Kursk.unity` contains
no NGO components at all, so `ShellImpactManager` never activates (§3.1).

---

## 12. Headless Harness (broken)

`Tools/SimVerify` compiles the **real** ballistics and vehicle sources against a
98-line `UnityShim` (only `Mathf`, `Vector3`, `Quaternion`, `Transform`, `Object`,
`Component`, `MonoBehaviour`, `Random`). Its purpose is to hold the simulation layer
Unity-free so it stays testable.

**Currently cannot run — no .NET SDK installed.** Its README claims 26/26; treat that
as unverified.

---

## 13. Licensing

### Shipped assets — CC0, public domain

Six Poly Haven scan textures (19.2 MB): ground `brown_mud_02`, road `gravel_road`,
rubble `rubble` — diffuse + normal, 2K. Poly Haven: *"CC0… effectively Public Domain.
You can use our assets for any purpose, including commercial work. You do not need to
give credit."*

Roughness/AO deliberately **not** shipped: mud, gravel and rubble are dielectrics, so
metallic is zero and gloss is constant per material. Physically correct, saved ~8 MB.

Normal maps are OpenGL-style (+Y up) — what Unity's Standard shader expects natively,
so no channel flip.

Full detail in `Assets/Artwork/CREDITS.md`.

### Vehicle models — NONE shipped

Every tank and aircraft is still built from Unity primitives. **There are no CC0
WW2 tanks or aircraft anywhere** — verified across Sketchfab, itch.io, Poly Haven,
Quaternius, Kenney, ambientCG.

| Source | Licence | Notes |
| --- | --- | --- |
| Newc42 low-poly German tanks | **CC0** verified | Tiger + Panzer III only. **No T-34, no Panzer IV** |
| Newc42 nature pack | **CC0** verified | 40+ trees/rocks/clouds |
| Sketchfab (various) | **CC-BY** attribution required | Best counts: Spitfire 3.4k, Me 262 2.3k tris |
| ambientCG | **CC0** verified | Terrain/road materials |
| Kenney Nature Kit | **CC0** verified | Trees/rocks, no vehicles |

**Avoid:**

- **Free3D** "free" = personal-use-only licence
- **TurboSquid** free models frequently labelled *Editorial Use* — forbids commercial
- **CC-BY-SA** — viral on derivatives (one A6M5 Zero is CC-BY-SA)
- **CC-BY-NC** — forbids commercial use
- **Quaternius is no longer CC0.** Custom QAL since 28 Aug 2026: commercial games
  fine, redistributing assets not. Older packs still show CC0 per-page.
- **Ripped game assets** — War Thunder / WoT / WoWP rips are prevalent on Sketchfab
  and likely infringing whatever badge the uploader picked

Sketchfab's licence filter in their API is broken: `cc0` returns nothing, `cc-by`
errors. Check per model.

### Reference material — do not build on it

`GTAmodding/re3` / `reVC` (reverse-engineered GTA III/Vice City) and the DOS Zone
`reVCDOS` WebAssembly port were studied while investigating browser ports of full
games. Note the licence tension: re3 states *"We don't feel like we're in a position
to give this code a license… educational, documentation and modding purposes"*, while
the port's README claims MIT. Those are not obviously compatible. **Reference-reading
only — do not incorporate code.** This project needs none of it: Unity has a first-party
WebGL export path, so there is no engine to reverse-engineer or hand-port.

---

## 14. Quick Reference

### Playing it in a browser

```powershell
# 1. start the server (needs COOP/COEP + application/wasm headers)
& "C:\Users\varun\AppData\Local\Programs\Python\Python313\python.exe" `
  "$env:TEMP\opencode\unity_web_server.py" `
  "C:\Users\varun\Projects\browser-games\war-arena-unity\Builds\WebGL" 8099

# 2. open http://127.0.0.1:8099/  in Chrome or Edge
```

Cannot be opened by double-clicking `index.html` — the headers are required.

### Building the WebGL player

```powershell
$u = "C:\Users\varun\Unity\6000.6.4f1\Editor\Unity.exe"
& $u -batchmode -quit -nographics `
  -projectPath "C:\Users\varun\Projects\browser-games\war-arena-unity" `
  -executeMethod WebBuildSetup.BuildWeb `
  -logFile "$env:TEMP\webglbuild.log"
```

~4 min, 42.2 MB output in `Builds\WebGL`. To only re-register the scene and shaders
without building, use `-executeMethod WebBuildSetup.Run`.

### Opening the project

```powershell
& "C:\Users\varun\Unity\6000.6.4f1\Editor\Unity.exe" `
  -projectPath "C:\Users\varun\Projects\browser-games\war-arena-unity"
```

Open `Assets/Scenes/Kursk.unity`, press Play. Regenerate via menu
**WarArena → Build Playable Scene**. First open after a `Library` wipe reimports for
about a minute.

### Running tests

**Batchmode currently hangs** — see §10. Use the Editor instead:

1. `Window` → `General` → `Test Runner`
2. **EditMode** tab → *Run All* → expect **37 passed**
3. **PlayMode** tab → *Run All* → expect **34 passed**

### Controls

| Ground | |
| --- | --- |
| `W`/`S` | Throttle / reverse |
| `A`/`D` | Hull traverse |
| Mouse | Aim — reticle turns red when the shot would not penetrate |
| `Q`/`E` | Turret fine trim |
| `Space`/LMB | Fire (gated by real reload time) |
| `C` | Chase / overhead camera |
| `Tab` | Ground ⇄ air — **fixed, please verify (§3)** |

| Air | |
| --- | --- |
| `W`/`S` | Pitch |
| `A`/`D` | Roll |
| `Q`/`E` | Rudder |
| `Shift`/`Ctrl` | Throttle |
| `Space` | Fire |
| `Tab` | Air ⇄ ground |

### Sibling projects

| Project | Engine |
| --- | --- |
| `war-arena` | Babylon.js / Vite web prototype |
| `war-arena-unity` | **This project** — Unity 6, browser-first |
| `kombat-clone` | Three.js / Vite fighter |
| `kombat-clone-unity` | Unity 6 fighter, playable |

---

## 15. Recommended Next Steps

1. **Press `Tab` in the browser** and confirm the camera stays behind the aircraft.
   This is the one fix I could not verify (§3).
2. **Run the test suites** from the Editor's Test Runner, and add a test asserting
   camera position per mode so the regression cannot return (§10).
3. **Enable Brotli** once the deployment host is confirmed to send
   `Content-Encoding: br`. Should take 42.2 MB down substantially (§2.6).
4. **Move the dev server into `Tools/`** and pick a deployment target — static host
   with correct headers, or a CDN (§2.8).
5. **Fix `ShellImpactManager`'s missing shader fallback** before any multiplayer work,
   or impact tracers will silently vanish (§3.1).
6. **Measure on integrated graphics.** Integrated-GPU laptops are the target audience
   and nothing has been profiled there. Expect to add a WebGL quality ladder.
7. **Gun elevation** — the gun is currently flat, so you cannot engage anything above
   or below the horizon. This is the largest remaining gameplay gap.
8. **Aircraft-to-ground combat** — aircraft currently only fight aircraft, so this is
   two games rather than combined arms.
9. **Decide on vehicle models** — CC-BY with attribution is the only realistic route;
   get written permission from any artist whose pack lists your exact vehicles. This is
   also the single biggest visual upgrade available.
10. **Consider URP** for the rendering ceiling (PBR, real-time shadows, post-processing,
    and eventually WebGPU) — but only after the built-in pipeline is stable, not just
    for browser support.
