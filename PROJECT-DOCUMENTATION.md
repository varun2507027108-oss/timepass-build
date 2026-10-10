# War Arena — Unity — Complete Project Documentation

Comprehensive reference for the `war-arena-unity` project: engine, architecture,
file structure, every system, the **WebGL browser build pipeline**, the
**Rust simulation core**, test status, known defects, and licensing.

> **Last updated:** after the first fix pass over the Rust port and the terrain
> rework. Every file in the repository has been read; see
> [§11a Known defects](#11a-known-defects--full-inventory).

---

## 0. Read this first

The simulation is **mid-migration from C# to Rust**.

- The **playable game does not use the Rust core.** It runs the C# `BattleSim`.
  The Rust module loads and is exercised by 223 tests, but `BeginLoad()` at
  `WarArenaBootstrap.cs:183` is the only production line that touches it.
- The simulation is **duplicated in both languages.** As of this pass they agree
  on driving, steering, turret traverse, gun elevation and shell hit detection;
  they still disagree on everything after the first hit.
- **One CRITICAL defect remains open:** every aircraft settles into a steady
  3–6 m/s descent and the AI flies into the ground after ~8 minutes.

**Current state: Rust 223/223 · Unity EditMode 98/98 · PlayMode 36/36 · all
green.** Eight CRITICAL defects were found and fixed in the last pass, each with a
regression test verified to fail against the old code.

---

## 1. Overview

A **browser-first** WWII armored combat simulator with two playable domains in one
build:

- **Ground** — tank vs tank combat with realistic armour penetration
- **Air** — six fighter aircraft from prop fighters through early jets

Players open a link in Chrome or Edge and play with no download and no install. A
Windows standalone build is retained as a backup.

The scene is built at runtime by `WarArenaBootstrap` + `BattlefieldBuilder`, over a
baked 1000 m radial terrain. There are no scene-authored prefabs beyond the
terrain, and the playable scene contains exactly one GameObject.

| Property | Value |
| --- | --- |
| Engine | **Unity 6** (6000.6.4f1, revision `12bfff696524`) |
| Render pipeline | **Built-in** (URP/HDRP **not** installed) |
| Colour space | **Gamma** (`m_ActiveColorSpace: 0`) |
| Build targets | Windows Standalone (backup) + **WebGL (primary, verified running)** |
| **Rust core** | **20 `.rs` files, 9,029 lines, zero external dependencies** |
| **C# runtime** | 24 files, 7,989 lines |
| **C# tests** | 11 files, 3,653 lines |
| **C# editor tools** | 7 files, 1,007 lines |
| Assets | 154 files + 190 `.meta` (0 orphans, 0 missing) |
| Largest asset | `panzer_iv.glb` — 32.9 MB |
| Vehicles in catalogue | 19 |
| Aircraft in catalogue | 6 |
| **Rust tests** | **223 pass** (167 unit + 15 + 32 integration + 9 FFI) |
| **Unity EditMode** | **98 / 98 pass** |
| **Unity PlayMode** | **36 / 36 pass** |
| `cargo fmt --check` | **fails, 108 hunks** — no `rustfmt.toml` |
| `cargo clippy` | 0 errors, 2 intentional warnings |
| WebGL build size | **42.2 MB**, 17 files, ~4 min build |
| Third-party assets | 6 CC0 textures (19 MB), duplicated to ~25 MB |
| CI | **none** — no `.github/`, no `rust-toolchain.toml`, no `.cargo/config.toml` |

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

**This is now in the repo** at `Tools/webgl-server.mjs` (previously a script in
`%TEMP%` outside version control). It binds `127.0.0.1` only, and its path
traversal guard is correct (`resolve(join(buildDir, normalize(clean)))` plus a
`startsWith(buildDir + sep)` prefix check).

```powershell
node Tools/webgl-server.mjs Builds/WebGL 8099
```

Then open **http://127.0.0.1:8099/**.

> **Known defect:** `decodeURIComponent` at `webgl-server.mjs:52` is outside any
> try/catch. A single `GET /%zz` from any page the browser loads kills the
> process — the rejection is unhandled, and Node ≥15 treats that as fatal. The
> traversal guard one line below is sound but unreachable for malformed input.

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
bug cannot recur. See §11.6 — it is now listed as the highest-value missing test.

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
target audience and this has not been measured — see §16.

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
│   │   ├── CREDITS.md                 Third-party licences, what to avoid
│   │   ├── Models/PanzerIV/panzer_iv.glb       32.9 MB — the player's tank
│   │   ├── SourceModels/PanzerIV/      55 .obj parts + manifest.json
│   │   ├── SourceModels/T34/           FBX + 8 stub textures
│   │   └── Environment/                FBX/OBJ ruins, rocks, wreck + .mat files
│   ├── Editor/                        7 files, 1,007 lines — no asmdef, so
│   │   │                                these compile into Assembly-CSharp-Editor
│   │   ├── WebBuildSetup.cs       247   [executeMethod] WebGL build entry point
│   │   ├── EnvironmentPrefabBuilder.cs 274  builds 7 props + 6 materials
│   │   ├── ModelPrefabBuilder.cs   218   Panzer IV prefab — SOURCE PATH MISSING
│   │   ├── BakeTerrain.cs          110   1025² heightmap from the analytic fn
│   │   ├── ModelDiagnostics.cs      79   unreachable, probes wrong paths
│   │   ├── SceneBuilder.cs          49   [MenuItem] playable-scene generator
│   │   └── SlopeReport.cs            30   terrain slope sampling, log only
│   ├── Plugins/
│   │   ├── x86_64/war_core_ffi.dll         164 KB — GITIGNORED, build it
│   │   └── WebGL/WarCore.jslib       13 KB   Emscripten glue for the same ABI
│   ├── StreamingAssets/
│   │   └── war_core/war_core.wasm     80 KB — GITIGNORED, build it
│   ├── Resources/
│   │   ├── BattlefieldTerrain.prefab   2.9 KB
│   │   ├── BattlefieldTerrainData.asset  34.1 MB  baked heightmap
│   │   ├── BattlefieldGroundLayer.terrainlayer
│   │   └── Artwork/                    CC0 scans + PanzerIV low-poly prefab
│   │       ├── Ground/  Road/  Rubble/ 6 × 2K jpg, 19 MB
│   │       ├── Environment/            7 built prop prefabs
│   │       ├── Textures/               3 png DUPLICATED from Artwork/ (~6 MB)
│   │       └── Models/PanzerIV/        panzer_iv_low.glb + PanzerIV.prefab
│   ├── Scenes/
│   │   └── Kursk.unity           1 GameObject: WarArenaBootstrap
│   ├── Scripts/                       24 files, 7,989 lines
│   │   ├── WarArena.Scripts.asmdef
│   │   ├── Air/
│   │   │   ├── AircraftCatalog.cs     355   6 airframes          (C# copy)
│   │   │   └── FlightModel.cs         706   forces, trim, stall  (C# copy)
│   │   ├── Ballistics/
│   │   │   ├── Shell.cs               365   ordnance, de Marre   (C# copy)
│   │   │   └── Penetration.cs         258   plate, ricochet, spall(C# copy)
│   │   ├── Gameplay/
│   │   │   ├── WarArenaBootstrap.cs   815   root orchestrator, HUD, cameras
│   │   │   ├── BattleSim.cs           745   ground simulation     (C# copy)
│   │   │   ├── BattlefieldBuilder.cs  927   ground, ruins, lighting, camera
│   │   │   ├── TrackedVehicleDynamics.cs  1.4k  slope, friction circle, skid steer
│   │   │   ├── HullSuspension.cs      354   4-contact heave/pitch/roll  (C# copy)
│   │   │   ├── TrackedVehiclePhysics.cs 137 PhysX model — UNTRACKED, disabled
│   │   │   ├── VisualMaterials.cs     449   material/texture/sky, both pipelines
│   │   │   ├── TankView.cs            420   procedural + imported tank rig
│   │   │   ├── AirCombatSim.cs        274   air combat            (C# copy)
│   │   │   ├── AircraftView.cs        252   procedural aircraft rig
│   │   │   ├── TankModels.cs           64   vehicle id → prefab path
│   │   │   ├── ITankInputReader.cs     60   input contracts
│   │   │   ├── KeyboardTankInput.cs    80   real tank controls
│   │   │   └── KeyboardAircraftInput.cs 87  real aircraft controls
│   │   ├── Interop/
│   │   │   └── WarSimRuntime.cs       513   the ONLY P/Invoke surface
│   │   ├── Vehicles/
│   │   │   ├── VehicleCatalog.cs      349   19 vehicles           (C# copy)
│   │   │   ├── VehicleDamageModel.cs  400   module + crew resolution — DEAD
│   │   │   ├── TankController.cs      219   WheelCollider ref impl — DEAD
│   │   │   ├── DamageModule.cs        119   damageable subsystems  (C# copy)
│   │   │   └── VehicleCatalogPhysics.cs 49  catalogue → controller — DEAD
│   │   └── Net/                            both unreachable — no NGO in the scene
│   │       ├── NetworkVehicle.cs      203   server-authoritative vehicle
│   │       └── ShellImpactManager.cs  143   server ballistics replication
│   ├── link.xml                        IL2CPP collider preservation
│   └── DefaultNetworkPrefabs.asset     NGO prefab list (empty)
├── Assets/Tests/                       11 files, 3,653 lines
│   ├── EditMode/                      10 files — 89 tests
│   │   ├── BattleSimParityTests.cs            643   13  Rust↔C# battle trace
│   │   ├── NormalisationAndRicochetTests.cs   309   15
│   │   ├── FlightModelTests.cs                293   14
│   │   ├── FlightModelParityTests.cs          270    8  Rust↔C# flight trace
│   │   ├── VehicleDamageModelBugTests.cs      267    6
│   │   ├── TurretAimingTests.cs               244    8
│   │   ├── RustCoreInteropTests.cs            208   12
│   │   ├── SlopeMovementTests.cs               40    4
│   │   └── TerrainMatchCheckTests.cs           38    1
│   └── PlayMode/                       2 files — 36 tests
│       ├── TankControlsPlayModeTests.cs  562   19
│       └── RenderSetupTests.cs          368   17
├── crates/                            20 .rs, 9,029 lines, ZERO deps
│   ├── Cargo.toml                     resolver 2, panic="abort", opt-level="z"
│   ├── Cargo.lock                     zero third-party crates
│   ├── war-core/                      pure simulation, no Unity, no unsafe
│   │   ├── PORT-NOTES.md       635   the port plan and its rationale
│   │   └── src/
│   │       ├── lib.rs                   25   module wiring
│   │       ├── math.rs                 864   Mathf/Vector3/Quaternion port
│   │       ├── spike.rs                 416   Phase-0 ABI harness
│   │       ├── ballistics/                    shell, shell_type, penetration
│   │       ├── vehicles/                      catalog, modules
│   │       ├── air/                           catalog, flight_model
│   │       └── gameplay/                      battle_sim (30 Hz), air_combat_sim
│   │   └── tests/                    1,022   turret_aiming, normalisation
│   ├── war-core-ffi/          804   the C ABI — all the unsafe lives here
│   ├── war-parity/            163   CLI trace dumper — no driver script
│   └── tools/
│       ├── build-native.ps1     71   → Assets/Plugins/x86_64/war_core_ffi.dll
│       ├── build-wasm.ps1       85   → Assets/StreamingAssets/.../war_core.wasm
│       └── wasm-interop-probe.mjs 257  fails the build on an ABI mismatch
├── Tools/
│   ├── SimVerify/                  headless .NET harness — SDK NOT INSTALLED
│   ├── webgl-server.mjs            COOP/COEP dev server — crashes on /%zz
│   └── verify-webgl-core.mjs       headless Chrome check of the built player
├── Builds/WebGL/                    42.2 MB browser build output
├── Packages/manifest.json           Input System 1.20.1, NGO 2.13.3
├── ProjectSettings/                 25 files
├── PROJECT-DOCUMENTATION.md        this file
└── README.md
```

**42 C# files / 12,649 lines · 20 `.rs` files / 9,029 lines · 154 assets / 190
`.meta` (0 orphans, 0 missing).**

`.meta` pairing is clean but note: `war_core_ffi.dll.meta` is 2 lines with **no
`PluginImporter` block**. It works today by folder-name convention only.

### Assembly definitions

| Assembly | References |
| --- | --- |
| `WarArena.Scripts` | Unity.InputSystem, Unity.Netcode.Runtime, Unity.Networking.Transport |
| `WarArena.EditTests` | WarArena.Scripts, InputSystem, TestRunner, nunit.framework.dll |
| `WarArena.PlayTests` | WarArena.Scripts, InputSystem, TestRunner, nunit.framework.dll |

Both test assemblies set `overrideReferences: true`, so a missing entry is a hard
compile error rather than a warning.

### Working notes

- **`Library/` was deleted** during troubleshooting (see §11.8). It is a regenerable
  import cache — no source is affected. The next Editor open does a full reimport,
  ~1 min.
- The **active build target is now WebGL**. Tests still run in the Editor, but you must
  switch back to Windows under `File → Build Profiles` to produce a `.exe`.
- **The two Rust artefacts are gitignored.** `Assets/Plugins/x86_64/war_core_ffi.dll`
  and `Assets/StreamingAssets/war_core/war_core.wasm` must be produced by
  `crates/tools/build-{native,wasm}.ps1` before Unity will find them (§10.6). A
  fresh clone that skips this step fails every interop and parity test.

---

## 6. Ground Systems

### BattleSim — the tank simulation

Pure, deterministic, Unity-free apart from `Mathf`/`Vector3`. 30 Hz fixed timestep
with a rolling accumulator held across frames. 30 Hz ground, 60 Hz air.

```csharp
public const float TickHz = 30f;
public const float FixedDt = 1f / TickHz;
public const float ArenaRadius = 440f;     // CIRCLE — was a 180 m square
public const float TurretSlewRate = 2.6f;   // rad/s
public const float MaxTraverse = 2.1f;      // 120 deg each way
private const float MaxSpeedMps = 12f;      // 43 kph — a single global constant,
                                            // so every vehicle drives at 43 kph
private const float AccelerationMps2 = 12.0f;
private const float MaxTurnRateRad = 1.8f;
private const float HullAutoAlignStrength = 0.35f;
private const float ShellVelocityScale = 0.42f;
```

> **CRITICAL — this is where the terrain rework broke the game.** `ArenaRadius`
> went from 90 m per side to a 440 m circle and vehicles now take their `y` from
> `GetTerrainHeight`, but the shell hit test still compares against an *absolute*
> `HIT_CEILING_M = 4.5` while the muzzle sits at `y + 2.2`. Across the arena,
> 520 801 of 608 189 sampled cells sit above 4.5 m — **85.6 % of the map cannot be
> shot at.** See §11a.

### The terrain

`BattlefieldBuilder.GetTerrainHeight(x, z)` is an analytic sum-of-sinusoids
height function — flat basin inside r ≤ 85 m, hills rising to ~73.7 m by the
rim. It is **duplicated verbatim** in `battle_sim.rs`, and it is called from
`BattleSim.DrivePlayer`, `DriveEnemy`, `SlopeSpeedFactor` and `TankView.Place`.

That duplication is the single biggest structural obstacle to finishing the Rust
migration: the simulation reaches into a **rendering/scene-construction class**
for its ground truth. `battle_sim.rs` duplicates the function body to break the
dependency, which means it can drift.

- **Baked**: `BakeTerrain.cs` renders the same function into a 1025² `TerrainData`
  (34 MB). `TerrainMatchCheckTests` samples both at 2000 points and requires
  agreement within 0.35 m (measured 0.039 m).
- **`SlopeSpeedFactor`** projects the analytic gradient onto the direction of
  travel, lerps from 1.0 to 0.5 as the angle reaches 32°, and zeroes past that.
  The factor is **flipped when the vehicle reverses** (`BattleSim.cs:375`), so
  driving downhill is not penalised.
- The bake's own verification block (`BakeTerrain.cs:91-101`) only
  `Debug.Log`s, and is character-for-character the same code as the test — so
  the bake verifies itself with the thing that could be wrong.

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

> **The "drop the backlog" branch is unreachable.** Both sims reset the
> accumulator only when it exceeds `FIXED_DT × MAX_STEPS`, which can never happen
> given the 0.1 s per-frame clamp. Real behaviour below 10 fps is silent
> slow-motion, not a stall.

**Hull follows the gun line.** Driving pulls the hull toward the gun's heading, which
is what real tracked vehicles and every tank game does:

```csharp
float moving = Mathf.Abs(throttle) > 0.01f ? 1f : 0f;
float gunWorldHeading = WrapAngle(PlayerHeadingRad + PlayerTurretYawRad);
float toGunLine = WrapAngle(gunWorldHeading - PlayerHeadingRad);
float demand = (toGunLine * HullAutoAlignStrength * moving) + steer;
```

> This block is **copy-pasted verbatim** into `DrivePlayerPhysics` six lines
> away, and the physics path then drops `SlopeSpeedFactor`. Terrain handling
> currently has three divergent answers.

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

> **CRITICAL — this deadlocks.** `Slew` wraps the short way around an
> *unbounded* angle, but the state is clamped to ±2.1 rad. The limits are 4.2 rad
> apart, wider than π, so the wrap can pick a path through the back — outside the
> arc — and the clamp pins the turret forever. Reproduced: parked at 1.9945 rad
> demanding −1.9 rad, the turret is still at 2.1000 rad after 600 frames.

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

## 10. The Rust Core

`crates/` is a Cargo workspace of three crates with **zero external
dependencies** — `Cargo.lock` contains only the three path dependencies, so
there is no Rust supply-chain surface at all.

| Crate | Lines | Role |
| --- | --- | --- |
| `war-core` | 7,641 | Pure simulation. No Unity, no `unsafe`, no panics that unwind. |
| `war-core-ffi` | 804 | The C ABI. **All the hand-written pointer arithmetic lives here.** |
| `war-parity` | 163 | CLI that dumps a scenario trace for the C# parity tests |

### 10.1 Why the boundary is shaped this way

`crates/Cargo.toml` sets `panic = "abort"` in the release profile. That is a
**correctness requirement, not a size optimisation**: unwinding out of
`extern "C"` is undefined behaviour, and Unity's native plugin host has nowhere
to put a landing pad. `war-core-ffi` is the only crate whose manifest allows
`unsafe_op_in_unsafe_fn`.

### 10.2 The ABI — nine symbols, all scalars

```rust
wa_spike_abi_version()  wa_spike_state_slots()  wa_spike_state_layout_slots()
wa_spike_create()  wa_spike_destroy()  wa_spike_reset()
wa_spike_advance(handle, f32*4, u32, f32*3)  wa_spike_read(handle, f32*, cap)
wa_spike_feed(handle, u8*, cap)  wa_spike_echo(handle, f32)
wa_battle_trace(...)  wa_flight_trace(...)     // parity harnesses only
```

**There are no structs by value anywhere in the ABI** — every argument is a
scalar or a raw pointer. That is the right design and it is correctly implemented
on both sides, which is why there is no packing or marshalling bug to find. The
managed side is `Assets/Scripts/Interop/WarSimRuntime.cs` (513 lines), which uses
only `[DllImport]` on static methods, blittable `[Out]` arrays and
`Marshal.Copy` — **no reflection, no `ICustomMarshaler`, no `StructLayout`**, so
nothing in the interop path is strippable and `link.xml` correctly says nothing
about it.

### 10.3 What the ABI can and cannot reach

Reachable from `extern "C"`: the spike harness, `BattleSim` (through
`wa_battle_trace`), `FlightModel` (through `wa_flight_trace`), and the two
catalogues.

**Not reachable** — the compiler agrees, verified by a deliberate compile
failure:

- The **entire ballistics module** (`Shell`, `Penetration`, `ArmourPlate`)
- The **entire damage model** (`DamageModule`, `CrewMember`, `ModuleKind`)
- **`AirCombatSim`** — the whole air gameplay type
- **`BattleSim::aim_would_penetrate`**, `::feed`, `::in_flight_positions`, the
  `player_render_*` accessors
- **`get_terrain_height`, `slope_speed_factor`, `clamp_to_arena`, `ARENA_RADIUS`**
  — all `pub` inside `battle_sim.rs`, but `mod battle_sim;` is private in
  `gameplay/mod.rs`, so they are crate-private. The first nine constants are
  mirrored as associated consts on `BattleSim`; the three terrain functions are
  **not**, so the C# renderer cannot ask the simulation where the ground is.
  **This blocks the in-flight terrain work at the ABI boundary.**
- 18 `pub const` tuning values in `flight_model.rs`, for the same reason

### 10.4 Numerical parity

`math.rs` is a `Mathf`/`Vector3`/`Quaternion` port targeting **bit-level parity
with Unity**, which is why every transcendental widens to `f64` and narrows:

```rust
pub fn sqrt(x: f32) -> f32 { (x as f64).sqrt() as f32 }
```

The `sqrt` shortcut is provably safe (IEEE guarantees `(float)sqrt((double)x) ==
sqrtf(x)`). `DEG2RAD` is `0.017453292` rather than Unity's `0.0174532924`, but
both round to the identical `f32` (`0x3c8efa35`) — verified, and pinned by a
test.

`mathf::smooth_step` was added recently and is the **first deliberate
reassociation** in a file whose stated rule is bit parity:

```rust
let t = -2.0 * t * t * t + 3.0 * t * t;   // Rust
let t = (-2.0f * t + 3.0f) * t * t;        // Unity
```

1 ULP apart at `t = 0.7`. Harmless (it drives terrain height, below the 0.05
parity tolerance) but it was introduced without a note or a test.

**Determinism is clean**: `f32` throughout state, no `HashMap`/`HashSet` in any
simulation path, `Vec` iterated in index order, fixed-step accumulators,
`Vec3`/`Quat` are `#[repr(C)]`, and the respawn counter is per-instance (proven
by `two_engagements_do_not_share_state`).

### 10.5 The parity claim has one unquantified hole

All parity testing happens on **Windows x64**, where Rust and .NET both reach
ucrt. The shipped artifact is **Emscripten + IL2CPP**, where Rust uses musl and
.NET uses `SystemNative_*`. `mathf::exp` (called every step for air density and
every shot for velocity) and `pow` are widened to `f64` and handed to the
platform libm — and **neither is correctly rounded**. Nothing in `PORT-NOTES.md`
establishes that the two agree on the shipping target.

### 10.6 Build scripts

```powershell
pwsh crates/tools/build-native.ps1   # → Assets/Plugins/x86_64/war_core_ffi.dll
pwsh crates/tools/build-wasm.ps1     # → Assets/StreamingAssets/war_core/war_core.wasm
```

Both use `--locked`, both assert artefact existence before installing, and both
correctly relax `$ErrorActionPreference` around `cargo` (cargo writes progress to
stderr, and a strict preference turns it into error records). The WASM build runs
`wasm-interop-probe.mjs` afterwards and refuses to install on failure.

There is **no emscripten involved** — it is plain
`cargo build --target wasm32-unknown-unknown` via rust-lld. The `RUSTFLAGS` are
a hand-chosen approximation of Unity's WebAssembly-2023 baseline, and **nothing
on either side pins or asserts the other**.

Both `.dll` and `.wasm` are **gitignored**; their `.meta` files are tracked.
That is correct — the binary is never the source of truth — but it means a fresh
clone must run the scripts.

---

## 11. Test Suite

| Suite | Result |
| --- | --- |
| `cargo test --workspace` | **223 / 223 pass** |
| `cargo clippy --workspace --all-targets` | 0 errors, 2 pre-existing intentional warnings |
| Unity EditMode | **98 / 98 pass** |
| Unity PlayMode | **36 / 36 pass** |
| Headless `Tools/SimVerify` | **cannot run — no .NET SDK** |

### 11.1 Rust - 196
shoot, the turret could deadlock, any steering input full-locked the tank, and the
aircraft flew into the ground. Every one of those is now fixed, and each fix is pinned by a
regression test that was verified to fail against the old code.
| In-module `#[cfg(test)]` | 132 | constants, sign conventions, catalogue relationships, `Mathf` parity |
| `tests/normalisation_and_ricochet.rs` | 15 | ports of the C# suite — **self-comparisons, not cross-language** |
| `tests/turret_aiming.rs` | 21 | same |
| `war-core-ffi` unit tests | 9 | ABI framing, null handles, `panic = "abort"` discipline |

The Rust tests are strong on **unit-level pin-down** — they lock constants and
sign conventions that are genuinely easy to invert. They are weak on **scenarios**:
no test runs long enough or at a location varied enough to see emergent
behaviour. That is why all 177 passed against a build where 85 % of the map could
not shoot, the turret could deadlock, any steering input full-locked the tank, and the
aircraft flew into the ground. Every one of those is now fixed, and each fix is pinned by a
regression test that was verified to fail against the old code.

Four of them **cannot fail at all**:

- `turret_aiming.rs:281` — `let flank_aim = enemy + Vec3::new(0,0,0); let _ = flank_aim;`
  is dead code under a comment claiming it aims at the flank.
- `turret_aiming.rs:288` — asserts the plate name ∈ {front, side, rear} when
  `struck_plate` can only return those three literals. The `panic!` arm is
  unreachable; it cannot detect the C# bug it is named after.
- `turret_aiming.rs:206` — `render_interpolation_removes_simulation_stepping`
  asserts `biggest_jump < 0.5`. At exactly 2× the sim rate the jump is 0.4 m
  **with or without** the feature, so it passes if interpolation is deleted.
- `air_combat_sim.rs:568` — asserts `sim.deaths() + sim.kills() < i32::MAX`.

### 11.2 The stale-plugin failure — resolved, but not detected

This was the one failing EditMode test, and it was never a code defect:

```
BattleSimParityTests.ReversingMatchesTheCSharpOriginal
  worst deviation 55.57 at tick 299 field PlayerPosition.z
    rust   : -90          ← the loaded DLL clamps at the OLD 90 m arena
    csharp : -145.568     ← source says 440 m
```

`war_core_ffi.dll` predated commit `6f355cf`. The ABI handshake
(`abi == 1 && slots == 512`) is version-stable, so **nothing detected that the
plugin's physics had drifted from its source** — which is why the suite sat red
for several runs without anyone noticing. Rebuilding the plugin fixed it.

`RustCoreInteropTests` was also corrected: it asserted `|x| <= 90`, hard-coded to
the obsolete square arena, and passed *only because the DLL was stale*. It now
reads `BattleSim.ArenaRadius` and checks the radius rather than a square bound —
which is what `clamp_to_arena` actually enforces.

**The detection gap is still open.** The handshake compares a compile-time
constant against a value the module reports about itself, so a semantically stale
plugin still passes. See §16 step 14.

### 11.3 Unity EditMode — 89

| Fixture | Count | What it does |
| --- | --- | --- |
| `BattleSimParityTests` | 13 | Rust `BattleSim` vs C# `BattleSim`, 14-field trace, 300–900 ticks |
| `FlightModelParityTests` | 8 | Rust `FlightModel` vs C# `FlightModel`, 12-field trace |
| `NormalisationAndRicochetTests` | 15 | effective thickness, ricochet, cast hulls, matchups |
| `FlightModelTests` | 14 | flyability relationships |
| `RustCoreInteropTests` | 12 | ABI framing, slot layout, blob sweep under abuse |
| `TurretAimingTests` | 8 | barrel direction, traverse limits, hull alignment |
| `VehicleDamageModelBugTests` | 6 | documented regressions, each with its mechanism |
| `SlopeMovementTests` | 4 | arena clamp, slope factor |
| `TerrainMatchCheckTests` | 1 | analytic vs baked heightmap |

### 11.4 What the parity harness actually proves

It is genuinely the strongest thing in the repo: an independent C# implementation
and an independent Rust implementation driven through identical scripted
scenarios, compared tick by tick over hundreds of ticks. **Worst measured
deviation `2.0e-5` against a `0.05` tolerance** — 100× headroom, which is a
reasonable engineering choice.

**It proves:** driving, steering, hull rotation, turret slew and clamping, gun
elevation, AI driving, shell hit *detection*.

**It does not prove:**

- **Everything after the first hit.** The comparison deliberately truncates at
  the first damage tick, because the Rust port carries four intentional fixes. So
  plate classification, ricochet scope, damage application, kills and respawns are
  entirely unverified across the boundary — and those are exactly where the two
  implementations now disagree.
- **NaN and Infinity are invisible.** Both loops use `if (delta > worst)`, and
  `NaN > worst` is `false`. A field that has blown up on one side is silently
  skipped. **This is the single most serious defect in the harness.**
- **Clamp constants under 0.05 in their own unit.** Gun pitch is pinned exactly
  via `ClampedFieldsMustMatchExactly`. `MAX_TRAVERSE` has no equivalent — a Rust
  value of 2.12 instead of 2.1 gives a 0.02 rad deviation and the traverse tests
  stay green.
- **3 of 19 vehicles.** Scenarios use `t34-76`, `panther-g`, `tiger2-e`. The
  player's actual vehicle, `pziv-h`, appears in no scenario.
- **Nothing after the first frame at 10 fps.** `MaxDeviationUntilFirstDamage`
  returns a `worst` value that all eight callers discard.

`ScenariosActuallyReachTheClamps` is the best test in the suite — it makes the
scenario's *coverage* a first-class assertion, so a retuned schedule cannot
silently stop testing anything. It should be the model for the rest.

`TraceStrideMatchesTheRustModule` exists in both parity fixtures and is
**tautological** — it compares a C# `const` to itself, so it cannot detect a Rust
stride change, which is precisely what its failure message claims it prevents.

### 11.5 Unity PlayMode — 36

`TankControlsPlayModeTests` (19) — ground: throttle, reverse, hull steering,
turret, camera, shells in flight, reload gating, arena clamp, enemy driven by
the sim, input soak. Air: mode switch, altitude spawn, pitch/roll/throttle,
soak, gunsight swap.

`RenderSetupTests` (17) — glossiness actually arrives, transparency configured,
sun angled and registered, skybox rendered, CC0 artwork loads, bump mapping,
textures tile, procedural fallback, fog, MSAA, seamlessness, steel vs paint,
canopy transparency, HUD unlit, global state restored.

These are good tests of the *rendering* contract — each asserts the value that
was actually wrong, which is the discipline that catches silent no-ops.

### 11.6 Known test gaps

| Gap | Note |
| --- | --- |
| `TrackedVehiclePhysics` — **zero tests** | The whole file, `DrivePlayerPhysics`, and the `UsePhysicsMovement` flag that mutates global `Physics.simulationMode` |
| `BattleSim.AimWouldPenetrate` — **zero C# tests** | The on-screen reticle. Rust has two dedicated tests for the equivalent. |
| `AirCombatSim` — effectively zero | No EditMode fixture at all. The air gunsight lead computation is uncovered. |
| `TankController`, `VehicleCatalogPhysics` | Zero tests, zero references |
| `NetworkVehicle`, `ShellImpactManager` | Zero tests; cannot be constructed in EditMode |
| `TankModels` | Zero tests, despite its own comment claiming a test uses it |
| `Keyboard{Tank,Aircraft}Input` | Zero tests — the real shipped input path is never executed |
| `BattlefieldBuilder` | Only `GetTerrainHeight`, indirectly |
| **No test asserts where the camera is** | Precisely why the Tab regression (§3) shipped green |
| **No test covers the WebGL build** | Shader inclusion, collider preservation and platform switching are all manual |

### 11.7 Test-harness defects

- **A cross-fixture static leak.** `TankControlsPlayModeTests.cs:88` sets
  `WarArenaBootstrap.InputReaderFactory = null` and never restores it.
  `RenderSetupTests` builds a bootstrap without restoring it, so it NREs in
  `Awake()`. Currently masked **purely by alphabetical ordering** — rename a
  fixture or use `-testFilter` and 17 tests break for no visible reason.
- **Three `Assert.Pass` exits** in `RenderSetupTests` report green while asserting
  nothing, when artwork is absent.
- **`CameraToggleIsEdgeGated` does not test edge gating** — it compares two noisy
  distance samples and would pass with edge-gating entirely removed.
- **A `Penetrates()` helper aliases and mutates its caller's `Shell`**, so
  successive calls compound the range decay. The Rust mirror takes a copy. The
  headline range assertion in that C# test passes for the wrong reason.
- **Three tests re-assert their own precondition** and are arithmetically
  incapable of failing.
- **Global state never restored after PlayMode**: `Application.targetFrameRate`,
  `QualitySettings.vSyncCount`, `Physics.simulationMode`.
- **`Random.InitState(42)`** in `TerrainMatchCheckTests` mutates Unity's global
  RNG with no `[TearDown]`.
- **`Poll()` is a no-op outside WebGL** yet is called by every `RequireReady()`,
  implying polling that never happens.
- **No CI.** Every gate is manual, which is why the suite went stale-red unnoticed.

### 11.8 Historical blocker — `-runTests` hangs

Recorded because it was real and cost time:

```
[Indexing] Starting Initial Indexing for Assets
```

then one core pinned at 100 % indefinitely. Ruled out: compile error, `-nographics`,
corrupt `Library` (deleted entirely, still hangs), active target, project data,
batchmode itself, `-executeMethod`. It is specifically the **Test Framework's
initialisation deadlocking against the asset-index subsystem** in this install.

**Workaround:** run from the Editor's Test Runner window
(`Window → General → Test Runner`). Close the Editor first when using batchmode —
an open editor holds the project lock and exits with code 1 and no output, which
looks identical to a failure.

**Current status:** `editmode-run.log` and `playmode-run.log` show completed runs
producing `editmode-results.xml` and `playmode-results.xml`, so batch mode works
now.

---

## 11a. Known Defects — full inventory

Everything here was found by reading every file. **Items marked ✅ were fixed in
the last pass**, each with a regression test verified to fail against the old
code.

### Simulation — CRITICAL

**1. ✅ 85.6 % of the map could not be shot at.** The terrain rework raised the
arena to a 440 m circle and put vehicle `y` on the hills, but the hit test
compared the shell's height against an *absolute* 4.5 m. 520 801 of 608 189
sampled cells sit above that.

> **Fixed** — the ceiling is now relative to the target's own ground contact, and
> the test is swept along the shell's path. Pinned by
> `the_hit_volume_follows_the_target_up_the_hills` across y = 0 … 73.7 m.

**2. ✅ Turret traverse deadlocked permanently.** `slew` wrapped an unbounded
angle while the state was clamped to ±2.1 rad; the stops are 4.2 rad apart,
wider than π, so the wrap pointed out through the back.

> **Fixed** — added `slew_bounded` for arc-confined quantities, kept `slew` for
> the AI hull heading. Pinned by `the_turret_can_recover_from_its_traverse_stop`,
> which reproduces *"parked -2.100 … after demanding the far shoulder it is
> -2.100"* against the old code.

**3. ✅ Hull steering 30× too sensitive, full-locked at 3.3 % input.** Missing
`* dt` at `battle_sim.rs` and `spike.rs`.

> **Fixed** in Rust and C#. Verified linear: 1 % → 0.018 rad/s, 10 % → 0.180,
> 100 % → 1.800. Pinned by
> `steering_is_proportional_to_the_input_not_saturated_by_it`, which reports
> *"a 1 % steer input turned at 0.5400 rad/s, expected ~0.018"* against the old code.

**4. ❌ No aircraft can hold altitude — STILL OPEN.** Steady descent, not
equilibrium. AI reaches the terrain floor at ~8 min. Sink: Bf 109 −2.9, Me 262
−3.5, **Meteor −5.9 m/s**. The trim test allows 200 m drift over 20 s against a
measured 58–118 m, so it passes with 1.7× margin on a permanent sink.

### Simulation — MAJOR

**✅ Rounds tunnelled through their targets — FIXED.** A round covers 11.2 m per
tick through a 6.4 m cylinder and the test sampled only the endpoint, so ~43 % of
rounds that flew straight through a target were never observed. Replaced with
`segment_hits_target` / `BattleSim.SegmentHitsTarget`, which solve for the
segment's closest approach. Pinned by
`a_round_faster_than_the_hit_cylinder_still_connects`.

- **Drag/range never affects a hit.** `travelled` is measured *from the target*,
  bounded to ≤ 3.2 m by the hit test, so `pen_at_impact ≈ pen₀ × 0.9997`. The
  whole drag calibration is dead in the hit path, and `ActiveShell` carries no
  origin, so it cannot be fixed locally.
- **Two penetration decision paths that disagree.** `resolve_hit` lets HEAT
  through *any* thickness; `Penetration::resolve` returns `NonPenetration` for
  HEAT vs 400 mm. Ricochet is front-plate-only in one, every-plate in the other.
- **`Shell::breaches_plate` has zero non-test callers** — HE can never breach.
- **Energy, spall and over-penetration are computed and discarded.**
- **Exact-match round → `OverPenetration` → zero energy.** `residual = max(0,
  pen − consumed)`, so 0 means "overkill". Pinned by a test, and wrong.
- **Zero-calibre shell → `NaN`.** `inf/inf`, and the guard checks `> 0.0`.
- **Two contradictory plate constructions.** `build_plates` says side/rear are
  rolled; `struck_plate` applies the cast coefficient. Only the second is live,
  and a test pins the first.
- **`AirCombatSim`: enemy never respawns.** The timer is decremented only in the
  `player_health > 0` branch. `AIR_ARENA_HALF_EXTENT` applies only to tracers —
  aircraft measured 99 km from spawn after 600 s.
- **Backlog-drop branch unreachable** in both sims → silent slow-motion at 10 fps.
- **Flight model is timestep-dependent.** Per-*step* `lerp(…, 0.35)` and a
  per-*step* velocity cap that ignores `dt`.
- **`max_thrust_n` is meaningless for all five propeller aircraft** — the cap never
  binds (measured 2.2–2.8× above the uncapped value at trim). `top_speed_ms` is
  decorative: modelled level speeds are 0.63–0.85× of it, and 0.34× for the Meteor.
  `turn_rate_deg` is documented as turn rate but used only as an elevator
  authority cap; the actual turn rate is a single global `roll × 0.18`.
- **`AIR_ARENA_HALF_EXTENT`, `TICK_HZ`, `FIXED_DT`** unreachable from the FFI.
- **`get_terrain_height` / `slope_speed_factor` unreachable from the FFI** — the
  `pub` functions are crate-private because `mod battle_sim;` is private.

### Interop — CRITICAL ✅ FIXED

**5. ✅ WebGL never finalised the Rust module; a missing `.wasm` was silent.**
`Poll()` was the only caller of `TryFinalize` and was never called from game code.

> **Fixed** — `WarArenaBootstrap.Update` now calls `WarSimRuntime.Poll()`.

**6. ✅ A stale DLL threw out of `Awake()` → empty scene.**
`wa_spike_state_layout_slots()` was outside the try/catch.

> **Fixed** — moved inside, with a specific message for a missing export.

**7. ✅ `NativeHandle` poisoned the type on a missing DLL.** It was a static field
initialiser (despite a comment claiming `LazyInitializer`), with no NULL check, so
one missing DLL made every later access throw `TypeInitializationException` for
the life of the process.

> **Fixed** — catches at initialisation, checks for NULL, and `TryFinalize` refuses
> to set `IsReady` while the handle is NULL. The wrong comment is corrected.

**8. ❌ WebGL scratch reserved from the top of WASM memory — STILL OPEN.** The
reservation and its provably-dead guard are unchanged.

### Interop — MAJOR

- **Three ground-physics implementations, zero wired to the FFI.**
- **`TrackedVehiclePhysics` is untracked → a clean clone does not compile.**
- It has **no suspension and leaves `useGravity` at its default `true`**, so the
  vehicle free-falls through the world. It accumulates and normalises the ground
  normal at `:97` and then never reads it.
- `if (!IsGrounded) return;` sits above *all three* force applications — no drag,
  no steering, no braking off a crest.
- It **conflicts with `TankController`** on six Rigidbody fields with
  nondeterministic `Awake` ordering (mass 28000 vs 26000, damping 0.05/0.02,
  angular 4 vs 6, `isKinematic` vs `Interpolate`/`ContinuousDynamic`).
- It **conflicts with `TankView.Place`** on transform ownership, and loses: `Place`
  clamps Y against the *analytic* terrain.
- `Physics.simulationMode` is set globally from a property setter and is **never
  restored** by `OnDestroy`.
- **Per-call `new Vector3[4]` on the 30 Hz hot path.**
- `engineForce` is 7.5× weaker than the kinematic model; `brakeForce` is never used
  as a brake; `turnTorque` about `transform.up`, not world up; `mass` hardcoded to
  a T-34 while the player drives a Panzer IV.
- **`TankController._damage` is always `null`** — `GetComponent<VehicleDamageModel>()`
  on a plain C# class. The entire damage-gating block is inert.
- **`TankController` differential torque is discarded** by a loop that overwrites
  every wheel identically; `brakeTorque` is `0f` on both ternary branches.
- **`NetworkVehicle` writes the target's hull and kill into the *shooter's*
  `NetworkVariable`s** (`NetworkVehicle.cs:191`).
- **`ShellImpactManager` leaks a GameObject and two Materials per shell**;
  `SpawnImpactSpark` places the spark at a fixed 1500 m and calls `LookAt` with a
  zero-length vector; `_instance` is never cleared on despawn.
- **`BattleSim.AimWouldPenetrate` ignores its `aimPoint` parameter entirely**
  (the Rust version does not), and has two dead locals.
- **The reticle resolves at zero range** — dead `travelled`/`penAtImpact`/
  `effective`, and `Penetration.Resolve` internally uses `penetration_at(0)`.
- **`BattleSim.Randomize` is `static`** — process-global mutable state shared by
  every instance. The Rust port fixed this; C# did not.
- **Shell flight has constant velocity and a point-sampled hit test** — a 278 m/s
  round advances 9.3 m per tick against a 6.4 m cylinder. Tunnelling is the
  default.
- **`InFlightPositions`/`TracerPositions` return the live list** as
  `IReadOnlyList<T>` after clearing and refilling it — an aliasing bug.
- `wa_spike_advance`/`wa_spike_reset` return values discarded; `ReadSlot` has no
  bounds check and different semantics per platform.

### Renderer — MAJOR

- **Fallback terrain is 180 m but the arena is 880 m across.** `BuildOuterTerrain`
  was deleted. If the terrain prefab fails to load, the tank drives off a 180 m
  island at ~90 m while trees are still placed out to 220 m in mid-air.
- **≈250 `Material` allocations per scene build**, all inside loops, none
  destroyed. 17 test bootstraps ≈ 4,250 leaked materials.
- **8 `Texture2D` HUD styles** created in `OnGUI`, never destroyed.
- **Architecture vanishes silently when Artwork is absent** — only the houses
  have a fallback; church, watchtowers, wrecks and rocks are all gated on
  `Resources.Load` with no fallback.
- **Roads do not follow the terrain** beyond r ≈ 85 m — buried or floating.
- Terrain prefab transform position hardcoded twice (bake + runtime).
- `TankView._leftTrack`/`_rightTrack`, `AircraftView.WingRight` (never assigned),
  `_wasStalled` (branch body is a no-op, comment claims smoke trails) — dead.
- Six of seven `Shell` factories, `NormalisationFor`, `CriticalRicochetFor`,
  `NonPenetrationMultiplier` and `IgnoresSlope` all collapse to one value at
  runtime because **every one of the 19 catalogue entries is `APHEBC`**.
  The entire per-shell-family armour model is dead.
- `BattlefieldBuilder.GetTerrainHeight` — a rendering class — is the simulation's
  ground truth, called from four places and duplicated into Rust.

### Build and editor tooling — MAJOR

- **`build-wasm.ps1` installs a broken `.wasm` if `node` is missing.**
  `$LASTEXITCODE` stays at `0` from the preceding `cargo build`, so the probe
  passes without running. Verified empirically.
- **`build-native.ps1` skips `wa_spike_reset`** (which C# calls), checks two
  symbols nothing imports, and does a byte-substring scan rather than a PE
  export-table parse.
- **`build-native.ps1` never passes `--target`**, so a MinGW host produces a
  MinGW DLL under an MSVC label.
- **Unshipped `VCRUNTIME140.dll` dependency** — Unity does not bundle the MSVC
  redistributable for third-party plugins, and nothing documents or checks it.
- **The interop probe verifies a 16 KiB scratch layout; the `.jslib` uses 64 KiB.**
- **`webgl-server.mjs` dies on `GET /%zz`** — `decodeURIComponent` outside any
  try/catch, and Node ≥15 treats the rejection as fatal. Verified.
- **`ModelPrefabBuilder.SourceFbx` does not exist** → fails on every WebGL build.
- **`SceneBuilder` destroys the open scene** with no prompt and no save.
- **`BakeTerrain` deletes any terrain in the open scene**, no Undo.
- **`EditorApplication.Exit()` inside the build method**, ungated on `isBatchMode`.
- **`Directory.CreateDirectory("Assets/...")` is CWD-relative** in three files.
- `EnvironmentPrefabBuilder` reports success while shipping fewer props, and
  mutates checked-in texture importer settings (`isReadable = true`) every build.
- `ModelDiagnostics` unreachable, probes two paths the game does not use, reports
  `AssetDatabase.IsValidFolder` as `isReadable`.
- 20 log files, `scratch/` (25 files, binaries included) and `__pycache__` are
  **tracked**. `.gitignore` misses seven untracked logs. `test-run.log` is
  tracked, modified, and contains a real stack trace.

### Hygiene — MINOR

- `cargo fmt` fails on 108 hunks; `modules.rs` and `flight_model.rs` have doc
  comments unindented to column 0 inside `impl` blocks.
- **Mojibake in `spike.rs`** (UTF-8 round-tripped through Latin-1) — including
  `"hÃ©llo"` in the one test whose job is proving UTF-8 round-trips. Characters
  have been *dropped* in `war-core-ffi/src/lib.rs` ("a plain **ssert**").
- Mixed LF/CRLF line endings, some files internally mixed.
- `WarArena.Scripts.asmdef` references `Unity.Networking.Transport`, unused.
- Both test asmdefs reference `Unity.InputSystem`, unused by any test.
- `FakeAircraftInput` is duplicated across two test assemblies under the same
  fully-qualified name.
- `link.xml` has **zero NGO coverage**, masked only by WebGL stripping at `Low`.
- `WarSimRuntime.cs:395` says "Asserted by the interop PlayMode tests"; it is an
  EditMode test.
- `BattleSimParityTests.cs:129` says positive pitch is nose-down; in `BattleSim`
  positive pitch is **elevation**. A tuner following that comment inverts the clamp.
- `BattleSimParityTests.cs:387` claims worst deviation `1.7e-5`; the log says
  `2.003e-5`.
- `PORT-NOTES.md` claims "27 Rust tests" in §8 and "177" in §0, in the same file.

### Dead code, consolidated

Never read in production, excluding their own tests: the entire damage model
(`DamageModule`, `CrewMember`, `ModuleKind`); `AirCombatSim`; the whole
ballistics module; `Shell::breaches_plate`; `VehicleCatalog::build_plates`;
`get_by_battle_rating` (both catalogues); `VehicleCatalog::all`;
`NetworkVehicle`; `ShellImpactManager`; `TankController`; `VehicleCatalogPhysics`;
`VehicleDamageModel`; `FlightModel::indicated_speed_ms`;
`AirCombatSim::SelectNextAircraft`; `AirCombatSim.EnemyRoundsLeft`;
`TankModels.HasModel`; `ArmourPlate.StructuralIntegrity`; `DamageModuleKind.FuelTank`
and `.MachineGun`; `CrewMember.IsAvailable`; `DamageModule.FireChanceOnDestroy`;
`AircraftCatalog.ClimbRateMs` and `.ArmourMm`; `VehicleDefinition.{TopSpeedKph,
ReverseSpeedKph, Crew, GunName, HasCasemateGun, MassKg, EngineKw, BattleRating}`;
`mathf::{atan, tan, log, lerp_unclamped, floor_to_int, repeat}`;
`Vec3::{scale, lerp_unclamped, is_finite}`; six of seven `Shell` factories;
`WarSimRuntime.SlotTicks`; `WarArenaBootstrap.{_reticleOk, _reticleBad, _lastLeadDeg}`.

That is roughly **1,800 lines of C# and 3,000 lines of Rust that no shipped code
path executes** — most of it a complete damage model, a complete air-combat
simulator, and a complete multiplayer layer, all built and none reachable.

---

## 12. Multiplayer

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

**Not implemented, and less alive than the diagram suggests.** No playable
multiplayer mode exists. `DefaultNetworkPrefabs.asset` has an empty list and
`Kursk.unity` contains no NGO components at all, so neither class is ever
instantiated.

Both classes have defects that will surface the moment they *are* wired up:

- `NetworkVehicle.cs:191` writes the **target's** hull strength and kill ID into
  the **shooter's own** `NetworkVariable`s — so a shot landing on the enemy
  overwrites the shooter's hull and credits the shooter with its own
  `NetworkObjectId` for a kill it inflicted elsewhere.
- `NetworkVehicle.cs:150` — the hit cone is ~20× too tight. `Asin(1 − alignment)`
  is not an angle; for small deviations `1 − alignment ≈ θ²/2`, so requiring
  `spread < 3 m` at 1000 m needs θ < **0.24°**. The comment claims ~6 m at 1000 m.
- `NetworkVehicle.cs:46` declares `public VehicleDamageModel Damage;` — an
  `Inspector`-serialised reference to a **plain C# class**, which can never be
  assigned.
- `ShellImpactManager` leaks a GameObject and two Materials per shell, never
  clears `_instance` on despawn, places impact sparks at a fixed 1500 m, and
  calls `LookAt` with a zero-length vector.
- `link.xml` has no NGO coverage (§11a).
- `TankController`, which the diagram puts in the server tick, **has never been
  instantiated** and its damage model can never be resolved (§11a).

---

## 13. Headless Harness (broken)

`Tools/SimVerify` compiles the **real** ballistics and vehicle sources against a
98-line `UnityShim` (`Mathf`, `Vector3`, `Quaternion`, `Transform`, `Object`,
`Component`, `MonoBehaviour`, `Random`). Its purpose is to hold the simulation
layer Unity-free so it stays testable — and that design constraint is genuinely
valuable.

**Currently cannot run — no .NET SDK installed.** Nothing invokes it. And the
project's own `PORT-NOTES.md:802-807` records that its assertions *"have never
been run on this machine and several of which contradict the current NUnit suite —
treat those 26 checks as unproven rather than as a specification."* Any "26/26
pass" figure is unsupported.

Two specific problems beyond the missing SDK:

- Three of the 26 assertions exercise `VehicleDamageModel`, which **the shipped
  game never calls** — it applies a flat `−34` / `−26`. They can green a build
  that has no path to the code they test.
- `UnityShim.cs:93` substitutes a fixed-seed `System.Random(12345)` for
  `UnityEngine.Random`, which the live code uses for the ammo-rack cook-off roll.
  The harness is therefore **deterministic where the game is not**, and can
  green a case the shipped build fails.

There is also a `Tools/verify-webgl-core.mjs` that drives the built player in
headless Chrome with a fresh temp profile and `--disable-background-networking`,
and kills the browser on `shutdown`. That one works and is worth keeping.

---

## 14. Licensing

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

### Vehicle models — one shipped

**This section was materially out of date.** There is now a real Panzer IV in the
project:

| Asset | Size | Notes |
| --- | --- | --- |
| `Assets/Artwork/Models/PanzerIV/panzer_iv.glb` | 32.9 MB | Source model, 55 `.obj` parts in `SourceModels/PanzerIV/` |
| `Assets/Resources/Artwork/Models/PanzerIV/panzer_iv_low.glb` | 902 KB | In-game, loaded by `TankModels.cs:32` |
| `Assets/Resources/Artwork/Models/PanzerIV/PanzerIV.prefab` | 162 KB | Built by `ModelPrefabBuilder` |
| `Assets/Artwork/SourceModels/T34/T-34-85.fbx` | 1 MB | Referenced by `ModelPrefabBuilder:7` — **the path does not exist**, so that builder always fails |

The player tank is `pziv-h`, so `TankView.BuildFromModel` loads the Panzer IV and
every tank and aircraft that is **not** the player falls back to Unity primitives
(`TankModels.cs`). The T-34, and all six aircraft, are still primitive-built.

**Licensing is unresolved.** `Assets/Artwork/CREDITS.md` is the record of where the
Panzer IV came from and under what terms. Check it before shipping anything —
this is the one asset in the project whose provenance is not a clean CC0 scan.

Beyond that, **there are no CC0 WW2 tanks or aircraft anywhere** — verified across
Sketchfab, itch.io, Poly Haven, Quaternius, Kenney, ambientCG.

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

## 15. Quick Reference

### Playing it in a browser

```powershell
# 1. build the Rust core for the web (REQUIRED — the .wasm is gitignored)
pwsh crates/tools/build-wasm.ps1

# 2. start the server (needs COOP/COEP + application/wasm headers)
node Tools/webgl-server.mjs Builds/WebGL 8099

# 3. open http://127.0.0.1:8099/  in Chrome or Edge
```

Cannot be opened by double-clicking `index.html` — the headers are required.

> The WebGL server **crashes on any malformed request** (`GET /%zz`) because
> `decodeURIComponent` is unguarded. Restart it if the page kills it.

### Building the Rust core

```powershell
pwsh crates/tools/build-native.ps1   # → Assets/Plugins/x86_64/war_core_ffi.dll
pwsh crates/tools/build-wasm.ps1     # → Assets/StreamingAssets/war_core/war_core.wasm
```

Both artefacts are **gitignored**. Run at least `build-native.ps1` before opening
Unity, or every interop and parity test fails. Both scripts need `pwsh` (PowerShell
7); **it is not installed on this machine** — `powershell -File` works, since both
scripts are 5.1-compatible.

> `build-wasm.ps1` will silently install a broken `.wasm` if `node` is not on
> `PATH`. See §11a.

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

> `BuildWeb` chains in `EnvironmentPrefabBuilder.BuildAll()`,
> `ModelPrefabBuilder.Build()` (which **always fails** — missing source) and
> `BakeTerrain.Bake()` (which **deletes any terrain in the open scene**). Running
> it overwrites three tracked assets with no confirmation.

### Opening the project

```powershell
& "C:\Users\varun\Unity\6000.6.4f1\Editor\Unity.exe" `
  -projectPath "C:\Users\varun\Projects\browser-games\war-arena-unity"
```

Open `Assets/Scenes/Kursk.unity`, press Play. Regenerate via menu
**WarArena → Build Playable Scene** — **which destroys your open scene without a
prompt.** First open after a `Library` wipe reimports for about a minute.

### Running tests

```powershell
# Rust — fast, no Unity needed
cd crates
cargo test --workspace              # expect 223 passed
cargo clippy --workspace --all-targets
cargo fmt --all                      # expect 108 hunks to change

# Unity
$u = "C:\Users\varun\Unity\6000.6.4f1\Editor\Unity.exe"
& $u -batchmode -quit -nographics -runTests -projectPath . `
  -testPlatform EditMode -testResults "$env:TEMP\edit.xml"
& $u -batchmode -quit -nographics -runTests -projectPath . `
  -testPlatform PlayMode -testResults "$env:TEMP\play.xml"
```

Expect **89/89 EditMode** and **36/36 PlayMode**.


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

## 16. Recommended Next Steps

### Blockers

> ~~**Done in the last pass:** the shell hit test, the turret deadlock, the
> steering `* dt`, the tunnelling, the four interop CRITICALs, the native plugin
> rebuild, and the stale 90 m arena assertion.~~

1. **Fix the flight-model trim** so it reaches equilibrium rather than a 3–6 m/s
   descent, or give the AI an altitude hold (§11a #4). This is the last open
   CRITICAL. The existing tests will not catch it — tighten the 200 m / 20 s
   tolerance and lengthen the air-combat scenario well past 10 s.
2. **Commit or delete `TrackedVehiclePhysics.cs`.** It is **untracked, so a clean
   clone does not compile** — `WarArenaBootstrap.cs:210` references the type.
3. **Add `float.IsFinite` guards** to both parity comparison loops. `NaN > worst`
   is `false`, so a field that has blown up on one side is silently skipped.
4. **Add a native-plugin freshness probe.** The handshake compares a compile-time
   constant against a value the module reports about itself, so a semantically
   stale plugin passes — which is how the suite sat red unnoticed (§11.2).

### Make the migration finishable

8. **Pick one ground-physics implementation and delete the other two.** The Rust
   `battle_sim.rs` is the intended destination; `BattleSim.cs` is what ships;
   `TrackedVehiclePhysics.cs` is neither and is incomplete.
9. **Break `GetTerrainHeight` out of `BattlefieldBuilder`** into a
   simulation-owned module, so the sim stops reaching into a rendering class for
   its ground truth and the Rust copy can stop being a hand-duplicated body (§11a).
10. **Expose `get_terrain_height` / `slope_speed_factor` through the ABI.**
    They are `pub` inside a private module, so the C# renderer cannot ask the
    simulation where the ground is. This blocks the in-flight terrain work.
11. **Re-export the terrain constants** the way `BattleSim`'s other nine are
    mirrored, or add `wa_battle_*` handle exports — there is currently **no way
    for Unity to drive the actual game simulation through the ABI at all**, only
    to run scripted parity traces.
12. ~~Call `Poll()`~~ and ~~move `wa_spike_state_layout_slots()` inside the
    `try`/`catch`~~ — **both done** (§11a #5, #6).
13. **Fix `WarCore.jslib`'s scratch reservation.** Reserving from the top of WASM
    linear memory is the one place the Rust allocator will hand out first, and
    the re-reserve guard is provably dead (§11a #8).

### Restore trust in the tooling

14. **Add CI.** `.github/workflows/` running `build-native.ps1` →
    `cargo test --workspace` → EditMode → PlayMode, plus `rust-toolchain.toml`
    and `.cargo/config.toml`. There is currently **no automation of any kind**,
    which is how a stale plugin turned the suite red unnoticed.
15. ~~Add `Get-Command` preflights~~ **Done** for the wasm script — a missing `node`
    is now fatal instead of leaving `$LASTEXITCODE` at `0` from the preceding
    `cargo build` and installing an unverified `.wasm` (§11a).
16. **Finish the native export gate.** `wa_spike_reset` is now checked, `--target`
    is pinned, and a missing `VCRUNTIME140.dll` is reported. Still open: it is a
    byte-substring scan rather than a PE export-directory parse, so a symbol name
    appearing in a panic message would satisfy it.
17. **Align the interop probe's scratch layout** with `WarCore.jslib` (16 KiB vs
    64 KiB).
18. **Guard the editor tools.** `SceneBuilder` and `BakeTerrain` destroy scene
    content with no prompt; `EditorApplication.Exit` is ungated on `isBatchMode`;
    three files use CWD-relative `Assets/` paths; `ModelPrefabBuilder`'s source
    path does not exist.
19. **Fix the `webgl-server.mjs` crash**, **untrack `scratch/` and `__pycache__`**,
    broaden `.gitignore` to `*.log`, and delete the four throwaway `inspect_*.py`
    scripts.
20. **Run `cargo fmt`** — 108 hunks, no `rustfmt.toml`. And fix the mojibake in
    `spike.rs` and `war-core-ffi/src/lib.rs`.
21. **Decide on `Tools/SimVerify`** — install the SDK and reconcile it, or delete
    it and delete the "26/26 pass" claim. A green number no machine has ever
    produced is worse than no number.
22. **Add NGO entries to `link.xml`** before anyone raises WebGL managed stripping
    above `Low`.

### Product

23. **Gun elevation** — the gun is currently flat, so you cannot engage anything
    above or below the horizon. This is the largest remaining gameplay gap.
24. **Aircraft-to-ground combat** — aircraft currently only fight aircraft, so
    this is two games rather than combined arms.
25. **Press `Tab` in the browser** and confirm the camera stays behind the
    aircraft. This is the one §3 fix still unverified.
26. **Fix `ShellImpactManager`'s missing shader fallback** before any multiplayer
    work, or impact tracers will silently vanish (§3.1).
27. **Enable Brotli** once the deployment host is confirmed to send
    `Content-Encoding: br`. Should take 42.2 MB down substantially (§2.6).
28. **Measure on integrated graphics.** Integrated-GPU laptops are the target
    audience and nothing has been profiled there. Expect a WebGL quality ladder.
29. **Decide on vehicle models** — CC-BY with attribution is the only realistic
    route; get written permission from any artist whose pack lists your exact
    vehicles. This is also the single biggest visual upgrade available.
30. **Consider URP** for the rendering ceiling (PBR, real-time shadows,
    post-processing, eventually WebGPU) — but only after the built-in pipeline is
    stable, not just for browser support.
