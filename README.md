# War Arena — Unity 6 (playable)

WW1/WW2 combined-arms vehicle combat with real armour penetration. **Playable
now** — open `Assets/Scenes/Kursk.unity` and press Play.

**This is the Unity version of the earlier `war-arena` web prototype.** That one
still runs and still iterates in under a second, which is why both are kept.

> ## ⚠ Read this first: the simulation is mid-migration
>
> The simulation core is being ported from C# to **Rust** (`crates/war-core`),
> bridged to Unity through a C ABI. The port is **incomplete**:
>
> - **The playable game still runs the C# `BattleSim`.** The Rust module loads, is
>   exercised by 216 tests, and is not yet stepped by game code.
> - **The simulation is duplicated in both languages.** The copies now agree on
>   the fixes made so far, but each language still carries work the other has not
>   taken on.
> - **There are known open defects**, including one where every aircraft slowly
>   sinks into the ground and one where drag never affects a hit. See
>   [Known defects](#known-defects).
>
> **Current state: Rust 216/216, Unity EditMode 93/93, PlayMode 36/36, all
> green.** All four CRITICAL simulation defects and four CRITICAL interop
> defects were found and fixed in the last pass, each with a regression test
> verified to fail against the old code.
>
> Nothing above blocks *opening the project and playing it*. See
> [`crates/war-core/PORT-NOTES.md`](crates/war-core/PORT-NOTES.md) for the port
> plan and [`PROJECT-DOCUMENTATION.md`](PROJECT-DOCUMENTATION.md) for the
> full reference.

## Controls

### Ground

| Input | Action |
| --- | --- |
| `W` / `S` | Throttle / reverse |
| `A` / `D` | Hull traverse |
| Mouse | Aim — the reticle turns **red** when your shot would penetrate |
| `Q` / `E` | Turret fine trim on top of mouse aim |
| `Space` or LMB | Fire (gated by the gun's real reload time) |
| `C` | Toggle chase / overhead camera |
| `Tab` | Switch between ground and air |

### Air

| Input | Action |
| --- | --- |
| `W` / `S` | Pitch (nose down / nose up) |
| `A` / `D` | Roll |
| `Q` / `E` | Rudder |
| `Shift` / `Ctrl` | Throttle up / down |
| `Space` | Fire cannons |
| `Tab` | Switch between air and ground |

The ground penetration reticle is hidden in air mode and the gunsight shown, so the
aiming cue always matches the vehicle you are flying.

Two vehicles by default: **Panzer IV Ausf. H** (you) vs **T-34/76**. 17 more are
in the catalogue. The Panzer IV is also the only vehicle with a real imported
model — everything else falls back to Unity primitives.

---

## Aircraft

Six airframes, spanning the whole prop-to-jet range, and deliberately chosen so
the interesting matchups are the ones that actually existed:

| | | |
| --- | --- | --- |
| Bf 109 G-6 | Spitfire Mk IX | A6M5 Zero |
| P-51D | Me 262 A-1a | Meteor F.4 |

The pairing is the point. The **Me 262** was faster than anything else in the game
by a wide margin, but the **Meteor could turn with it** and won the real engagement
because it was built to be flown, not to go fast. Both relationships are asserted
in the test suite so they cannot silently change.

#---

## Rendering

The scene is built entirely from untextured primitives in code, on Unity's
**built-in** render pipeline. Two things follow from that, and both caused real
bugs worth knowing about.

### One material factory, both pipelines

`VisualMaterials` is the single place materials, textures and the sky are built.
It writes **both** the URP and the built-in property names — `_Smoothness` *and*
`_Glossiness`, `_Surface` *and* `_Mode` — because setting a property a shader does
not have is a silent no-op by design. That makes writing both the safe and the
correct thing to do, and means the file keeps working unchanged if the project ever
moves to URP.

The alternative, guarding each setter with `HasProperty`, hides the mistake rather
than fixing it: the call succeeds and does nothing, which is exactly how the
original code ended up with opaque canopies and uniform gloss.

Transparency is the fiddliest part. Built-in `Standard` hides it behind a `_Mode`
switch where 3 means transparent, *and* requires the `_ALPHABLEND_ON` keyword —
setting the blend state without the keyword is silently ignored.

### Lighting

| | |
| --- | --- |
| Sun | Warm (`1.0, 0.90, 0.74`) at **27° elevation** — late afternoon |
| Shadows | Soft, with tuned bias and normal bias |
| Ambient | Trilight, cool sky against warm ground bounce |
| Fog | Exponential squared, tinted to the horizon |
| Sky | Procedural, with a sun disc matched to the directional light |
| Anti-aliasing | 4× MSAA |

The sun elevation is the single highest-impact number in the scene. A sun straight
overhead is the worst possible angle for a ground-level battlefield: no surface has
a lit side and a shaded side, forms have no volume, and shadows pool underneath
their objects instead of reading as cast.

The fog colour is deliberately matched to the sky just above the horizon so the
tree line dissolves into the background. With no textures and no landmarks, fog is
the only thing giving the 140 m ground plane any sense of scale.

### Ground detail

The ground, road and ruined walls use **CC0 scan textures from Poly Haven** —
real photographed surfaces rather than flat colour. Diffuse and normal maps at 2K,
under `Assets/Resources/Artwork/`.

Roughness and AO maps were deliberately not shipped: mud, gravel and rubble are
dielectrics, so metallic is zero and gloss is a constant per material. That is
physically right and saved about 8 MB.

Poly Haven is CC0 — public domain, no attribution required, commercial use fine.
Full details, plus a survey of the vehicle-model situation and which licences to
avoid, are in [`Assets/Artwork/CREDITS.md`](Assets/Artwork/CREDITS.md).

**Normal maps are OpenGL-style (+Y up)**, which is exactly what Unity's Standard
shader expects natively, so no channel flip is applied at import.

If the artwork folder is removed the scene still builds: `VisualMaterials.Textured`
falls back to a generated seamless value-noise texture, so the simulation and tests
work without a 19 MB folder present. `VisualMaterials.ArtworkPresent` reports which
path is active. The noise is **tileable** — every octave wraps its lattice lookup by
its own period, removing the grid seam that plain Perlin noise produces — and there
is a test that samples opposite edges of the tile to catch it.

### Materials that differ on purpose

| Surface | Treatment |
| --- | --- |
| Hull, walls, foliage | Matte, low gloss |
| Tracks, road wheels, gun barrels | Metallic, glossier — they catch the low sun |
| Aircraft skin | Matte aluminium with a soft sky sheen |
| Canopy glazing | Genuinely transparent, high gloss |
| Tracers, jet exhaust | Emissive, so they glow regardless of sun angle |
| Reticle, gunsight | Unlit, so the sun can never dim the aiming cue |

The last one matters for play specifically: a lit reticle dims when the tank turns
away from the sun, which is exactly when the player most needs to read the
penetration indicator.

---

## The flight model

Forces and moments, not a speed controller — so behaviour emerges from each
airframe's numbers rather than being scripted:

- **Lift** follows thin-aerofoil theory, `CL = 2πα`, capped at `CLmax` of 1.3. This
  is what makes wing area mean something: the Spitfire's small 12.4 m² wing needs a
  higher coefficient and flies slower than the P-51D's 18.1 m² wing of similar mass.
- **Induced drag** uses `D = L²/(q·S·π·e·AR)` with aspect ratio from the real span,
  so a wide wing is cheap to fly slowly. That is why the Meteor can dogfight.
- **Parasite drag** is per-airframe, so drag ranking comes from the data rather
  than from a script. (Note: the Zero is *not* the draggiest — its coefficient is
  0.0185 against the Spitfire's 0.0250, and a comment in `air/catalog.rs` still
  claims the opposite. `PORT-NOTES` records the dispute as resolved in favour of
  the data; the comment was never updated.)
- **Propeller thrust is power ÷ airspeed**, not constant thrust. A propeller makes
  ~12 kN on the takeoff roll and ~3 kN at 200 m/s. Treating it as constant makes a
  piston fighter out-accelerate a jet and destroys the entire prop-versus-jet
  distinction. Jets hold thrust with speed; props lose power with altitude.
- **Static stability** comes from a tailplane pulling the nose toward the trimmed
  angle of attack, with rate damping so it settles instead of porpoising.
- **Stall** is modelled with lift collapse past 16°, and the trim target is capped
  below it so the aircraft can never be trimmed into a stall.
- **Transonic drag rise** past Mach 0.85, which is why the 262 needs altitude to go
  fast.

### What the tests enforce

Beyond the performance relationships, the suite asserts the properties that make an
aircraft flyable rather than merely exist:

- Every airframe **holds altitude and steady speed hands-off**, with no input
- None of them **stalls in trimmed flight**
- Every airframe settles **well clear of its own stall speed**
- Full-input abuse (rapid pitch, roll, throttle and trigger) stays **finite and
  bounded**
- Climbing **costs speed**, and cutting the throttle **bleeds it**
- The 262 out-runs a P-51D; the Meteor out-turns it; the Zero turns better than
  either prop

---

## Why Unity for this one

Real research, not vibes:

| Need | Unity 6 verdict |
| --- | --- |
| Vehicle physics | `WheelCollider` + custom torque model is the proven approach for tracked vehicles. Existing MIT references: `benmcinnes/ArcadeVehiclePhysics` (202★), `Jermesa-Studio/JRS_Vehicle_Physics_Controller` (221★), `JustInvoke/Randomation-Vehicle-Physics` (936★) |
| Rendering budget | Currently on the **built-in** pipeline, not URP. URP would be worth it for HDR, post-processing (bloom, AO, tone mapping) and better shadow cascades — but it is a real change, not a config flip: the asmdef needs a `Unity.RenderPipelines.Universal.Runtime` reference and every material property name changes |
| Multiplayer | `com.unity.netcode.gameobjects` 2.13.3 is Unity's supported answer; supports **WebGL since NGO 1.2 + Unity Transport 2.0** |
| Cost | Free below the Unity revenue threshold; Pro seats are a licence cost, not a runtime cost |

**Honest trade-off:** Unity WebGL builds are roughly 20–40 MB versus the
browser prototype's 266 KB gzipped. If your goal is "click a link and play",
stay web-native. If your goal is "War Thunder-class fidelity", Unity is the
correct tool and the payload is irrelevant because the game installs.

---

## Layout

```
Assets/
  Plugins/
    x86_64/war_core_ffi.dll        Rust core, native   (gitignored — build it)
    WebGL/WarCore.jslib            Emscripten glue for the same ABI
  StreamingAssets/
    war_core/war_core.wasm         Rust core, WebGL    (gitignored — build it)
  Resources/
    BattlefieldTerrain.prefab      1000 m radial terrain, 1025² heightmap
    BattlefieldTerrainData.asset   34 MB baked heightmap
    Artwork/                       CC0 scan textures + Panzer IV prefab
  Scenes/Kursk.unity               1 GameObject: WarArenaBootstrap
  Scripts/            24 files · 7,989 lines
    Ballistics/
      Shell.cs                    Ordnance, de Marre, normalisation  (C# copy)
      Penetration.cs              Plate resolution, ricochet, spall   (C# copy)
    Air/
      AircraftCatalog.cs          6 airframes                         (C# copy)
      FlightModel.cs              Forces, stability, trim, integrator (C# copy)
    Gameplay/
      WarArenaBootstrap.cs        Root orchestrator, HUD, cameras
      BattleSim.cs                Ground simulation                  (C# copy)
      BattlefieldBuilder.cs       Ground, ruins, trees, lighting, camera
      TrackedVehiclePhysics.cs    PhysX tracked-vehicle model — UNTRACKED, disabled
      VisualMaterials.cs          Material/texture/sky factory, both pipelines
      TankView.cs                 Procedural tank + imported Panzer IV rig
      AirCombatSim.cs             Air combat, tracers, health        (C# copy)
      AircraftView.cs             Procedural aircraft geometry
      TankModels.cs               Vehicle id → prefab path map
      ITankInputReader.cs / Keyboard{Tank,Aircraft}Input.cs
    Interop/
      WarSimRuntime.cs            The only P/Invoke surface into the Rust core
    Vehicles/
      VehicleCatalog.cs           19 historical vehicles             (C# copy)
      VehicleDamageModel.cs       Module + crew damage resolution
      TankController.cs           WheelCollider reference implementation
      DamageModule.cs             Damageable subsystems + crew
      VehicleCatalogPhysics.cs    Catalogue → controller bridge
    Net/
      NetworkVehicle.cs           Server-authoritative vehicle  (never instantiated)
      ShellImpactManager.cs       Server ballistics replication   (never instantiated)

Assets/Tests/
  EditMode/           10 files · 2,708 lines · 91 tests
  PlayMode/            2 files ·   945 lines — 36 tests

Assets/Editor/         7 files · 1,007 lines
  WebBuildSetup.cs, EnvironmentPrefabBuilder.cs, ModelPrefabBuilder.cs,
  BakeTerrain.cs, SceneBuilder.cs, ModelDiagnostics.cs, SlopeReport.cs

crates/                20 .rs files · 9,029 lines — zero external dependencies
  war-core/            Pure simulation. No Unity, no deps, panics cannot unwind out.
    src/math.rs        Mathf / Vector3 / Quaternion port, bit-parity with Unity
    src/spike.rs       Phase-0 ABI harness: proves the Rust↔Unity boundary works
    src/ballistics/    shell, shell_type, penetration
    src/vehicles/      19-vehicle catalogue, damage modules, crew
    src/air/           6-aircraft catalogue, flight model
    src/gameplay/      battle_sim (30 Hz ground), air_combat_sim (60 Hz air)
    tests/             turret_aiming.rs, normalisation_and_ricochet.rs
  war-core-ffi/        The C ABI: 9 `extern "C"` symbols, all the unsafe code
  war-parity/          CLI that dumps a scenario trace for the C# parity tests
  tools/
    build-native.ps1   → Assets/Plugins/x86_64/war_core_ffi.dll
    build-wasm.ps1     → Assets/StreamingAssets/war_core/war_core.wasm
    wasm-interop-probe.mjs   Fails the build if the .wasm ABI is wrong

Tools/
  SimVerify/           Headless .NET parity harness — SDK NOT installed (§below)
  webgl-server.mjs     COOP/COEP dev server
  verify-webgl-core.mjs  Headless Chrome check of the shipped build
```

Line counts are exact as of the working tree. The `.dll` and `.wasm` are
gitignored but their `.meta` files are tracked — **a fresh clone must run the
build scripts** before Unity will find the plugin.

### The three ground-vehicle models

There are currently **three** implementations of ground movement, which is the
main obstacle to finishing the migration:

| Where | Model | Status |
| --- | --- | --- |
| `Assets/Scripts/Gameplay/BattleSim.cs` | Kinematic, tractive | **What the game runs** |
| `crates/war-core/.../battle_sim.rs` | Kinematic, Rust port | **What the tests police** |
| `Assets/Scripts/Gameplay/TrackedVehiclePhysics.cs` | PhysX Rigidbody | **Untracked, disabled, incomplete** |

The PhysX model has no suspension and leaves gravity enabled, conflicts with
`TankController` over six Rigidbody fields, and loses the ground-height write to
`TankView.Place`. It is currently `UsePhysicsMovement = false` and that flag is
never set to `true` anywhere.

---

## The damage model

Modelled on Gaijin's published Ground Forces diaries and the de Marre /
Lanz-Odermatt equations War Thunder switched to in 2019.

**Armour** is a set of plates with real thickness and slope. A Panther's 55°
glacis is 120 mm of plate presented at an angle, not 120 mm face-on, so the
effective thickness is `thickness / cos(impactAngle)` and ricochets past 70°.

**Penetration** decays with distance because velocity decays: `v = v₀·e^(-kd)`,
and penetration scales with the cube root of retained velocity. This is why a
long-range shot at a sloped plate fails where the same round up close punches
through. HEAT is the deliberate exception and does not decay.

**Kills come from modules and crew, never from a health bar.** A shell that
defeats the front plate still has to reach the engine, transmission, turret
drive, gun breech, ammo rack, tracks, or a crew member:

- Hitstun, block chip, knockback, and hitstop are per-move.
- Penetrating AP rounds throw a **spall cone** — fragments fan out inside the
  fighting compartment and kill crew and light modules. This is the actual
  mechanism of death in the genre, not a damage multiplier.
- APHE/APHEBC rounds that penetrate then cook off, which is why ballistic-cap
  AP is more lethal than APC once it gets through.
- The ammo rack cooks off on a direct hit and is unsurvivable. Most tanks in
  this game will die that way, exactly as they did historically.
- Destroyed engine reduces power, transmission reduces top speed, turret drive
  slows traverse, tracks immobilise, gun breech prevents firing.

Implemented in `VehicleDamageModel` and `Penetration`. All of it is pure
static functions over value types, so it is unit-testable and safe to run
server-side for hit-regret correction.

### Normalisation and ricochet

Plate resistances are published for a round arriving **square** to the plate. In
reality the nose does not stay aligned with its velocity vector at impact. A blunt
or capped nose bites in and rotates *toward* the surface normal, so the plate
presents less depth than geometry alone suggests; a sharp uncapped nose is pushed
the other way.

| Family | Normalisation | Ricochet limit |
| --- | --- | --- |
| APBC (blunt Soviet) | +6° | 73° |
| APHEBC | +5° | 71° |
| APC | +3° | 68° |
| APCR (sharp rod) | −2° | 60° |
| HEAT | — (ignores slope) | never bounces |

This matters because resistance goes as `1/cos(θ)`. **Five degrees of
normalisation against a 60° glacis is worth ~13% of required penetration** — the
difference between defeating a Panther and bouncing off it.

Two things this gets right that a single global threshold could not:

- **Normalisation belongs to the plate, not the projectile.** What changes when a
  nose bites in is how much armour the round must chew through, not what the round
  is capable of. Keeping them separate means one plate resolves correctly against
  the whole roster with no per-shell special case at any call site.
- **A HEAT jet forms on the plate normal** however oblique the impact, so slope
  costs it nothing. That is why HEAT dominates heavy sloped frontage that stops
  every kinetic round in the game.

Cast hulls also resist ~10% more than rolled plate of equal thickness. The Panther
and Tigers are cast; the T-34's famous sloped glacis is welded rolled plate.

### Range falloff, and the bug that broke it

Velocity decays exponentially (`v = v₀·e^(−kd)`) and penetration follows from
velocity through the de Marre relationship, so range matters — a round that
defeats a plate up close can fail at 2 km against the same one.

The drag coefficients were originally ~15× too high. At `k = 0.0019` an 88 mm round
was doing **134 m/s at 1000 m**, where the historical figure is about **800 m/s**.
Every shell in the game was effectively dead within a kilometre, which flattened
the entire engagement model. The values are now set so residual velocity matches
the documented figures:

| Round | v₀ | v @ 1000 m | Penetration loss |
| --- | --- | --- | --- |
| 88 mm APHEBC | 900 m/s | ~800 m/s | 160 → ~137 mm at 2 km |
| 85 mm APHEBC | 800 m/s | ~690 m/s | 128 → ~105 mm at 2 km |
| 75 mm APHEBC | 800 m/s | ~700 m/s | 104 → ~87 mm at 2 km |

**Known incomplete:** `PEN_CALIBRATION_K`-style constants are calibrated by hand
against firing tables, not solved from them. Drag coefficients are now calibrated
against documented residual velocities, which is closer but still an approximation
of a real drag curve rather than a solution to one.

> **Known broken:** none of the above reaches a hit. `BattleSim` measures the
> shell's distance from the *target* — which the hit test has already bounded to
> ≤ 3.2 m — so `pen_at_impact` is always `pen₀ × 0.9997`. The drag model, the
> calibration table above, and the documented penetration-loss-per-km figures are
> all computed correctly and then discarded. See [Known defects](#known-defects).

---

## Multiplayer

Server-authoritative, because WheelCollider state is trivially manipulable and a
client reporting its own hits is a trivial cheat. For 30–40 km/h vehicles at
600 m ranges, the added latency is irrelevant.

```
Client                        Server (host)
  │ input intent (4 bytes) ────▶ NetworkVehicle.SubmitInput
  │                                ServerTick @ 30 Hz
  │                                  TankController.Drive (physics)
  │                                  DamageModel.ApplyShellImpact
  │                                ShellImpactManager.ServerSpawnShell
  │ ◀─── NetworkVariable sync ───── speed / hull / fire / kill
```

`NetworkVehicle` replicates only derived values — speed, hull integrity, fire
flag, kill attribution. Per-module HP stays server-side so clients cannot map
an enemy armour layout from network traffic.

**Next steps in order:**
1. Swept collision for shells along their trajectory, including terrain.
2. Lag compensation: rewind targets to where they were when the shot was fired.
3. Spatial partitioning for collision checks as player counts rise.
4. Aircraft-to-ground combat: the flight model and air combat sim exist, but
   aircraft currently only fight aircraft. Wiring the cannons to hit ground
   targets is what makes this a combined-arms game rather than two games.
5. SPAA guns and unguided rockets.

---

## Open in Unity

Unity is installed at `C:\Users\varun\Unity\6000.6.4f1` (see note below). Open
`war-arena-unity`, or:

```powershell
& "C:\Users\varun\Unity\6000.6.4f1\Editor\Unity.exe" `
  -projectPath "C:\Users\varun\Projects\browser-games\war-arena-unity"
```

1. Build `Assets/Scenes/Kursk.unity` from a vehicle prefab: `TankController` +
   `VehicleDamageModel` + `NetworkVehicle`, with a turret child and a muzzle
   transform wired up.
2. Set the network tick rate to **30 Hz** in Project Settings → Multiplayer →
   Tick Rate. It cannot be assigned from script — `NetworkTickSystem.TickRate`
   is read-only in Netcode for GameObjects 2.x.
3. Bootstrap:
   ```csharp
   var def = VehicleCatalog.Get("t34-76");
   VehicleCatalogPhysics.ApplyToController(tankController, def);
   tankController.LoadedShell = VehicleCatalog.BuildShell(def);
   var model = VehicleCatalog.BuildDamageModel(def);
   ```

> **About the Unity install.** The original install under
> `C:\Program Files\Unity\Hub\Editor\6000.6.4f1` died partway because the disk ran
> out of space, leaving `Data/Managed` missing and the Editor dead on launch with
> *"Unity.dll failed to load"*. It could not be deleted without admin rights, so a
> working copy was installed to `C:\Users\varun\Unity\6000.6.4f1` instead. The
> broken folder can be removed from an elevated prompt once you no longer need it.

## Verification status

| Check | Result |
| --- | --- |
| `cargo test --workspace` | **216 / 216 pass** (160 unit + 15 + 32 integration + 9 FFI) |
| `cargo clippy --workspace --all-targets` | 0 errors, 2 pre-existing intentional warnings |
| **Unity EditMode tests** | **91 / 91 pass** (81 + 10 hull suspension/footprint) |
| **Unity PlayMode tests** | **36 / 36 pass** |
| Unity Editor compile | clean, 0 `error CS` |
| Headless `Tools/SimVerify` | **cannot run — no .NET SDK.** Any "26/26" claim is unverified. |
| WebGL build + wasm interop probe | verified, all checks pass |

### What was fixed in the last pass

| Defect | Fix |
| --- | --- |
| **85 % of the map could not be shot at** | Hit ceiling is now relative to the target's own ground contact, and swept along the shell's path |
| **Rounds tunnelled through targets** | Endpoint sampling replaced with a swept segment-vs-cylinder test — a round covers 11.2 m per tick through a 6.4 m cylinder, so ~43 % of rounds that flew straight through were never observed |
| **Turret deadlocked permanently** | Added `slew_bounded`; `slew` wraps an unbounded circle, which points *out through the back* of a ±2.1 rad arc |
| **Steering 30× too sensitive** | Missing `* dt` on the per-step turn delta; any input ≥ 3.3 % full-locked the tank |
| **Stale native plugin turned the suite red** | `build-native.ps1` now pins `--target`, checks `wa_spike_reset`, and warns about the VC++ runtime dependency |
| **EditMode asserted the obsolete 90 m arena** | Reads `BattleSim.ArenaRadius`, and checks the radius rather than a square bound |
| **WebGL never finalised the Rust module** | `WarSimRuntime.Poll()` is now called from `Update` |
| **A stale DLL threw out of `Awake()` → empty scene** | `wa_spike_state_layout_slots()` moved inside the try/catch |
| **A missing DLL poisoned the type forever** | `NativeHandle` catches at initialisation and checks for NULL |
| **`build-wasm.ps1` installed unverified `.wasm`** | Explicit `node` preflight; a missing `node` is fatal instead of leaving `$LASTEXITCODE` at `0` |

Each fix has a regression test that was **verified to fail against the old code**
before being accepted — see `crates/war-core/tests/turret_aiming.rs`.

### The stale-plugin failure is gone

It was never a code defect:

```
BattleSimParityTests.ReversingMatchesTheCSharpOriginal
  worst deviation 55.57 at tick 299 field PlayerPosition.z
    rust   : -90          ← the loaded DLL clamped at the OLD 90 m arena
    csharp : -145.568     ← source says 440 m
```

`war_core_ffi.dll` predated the commit that raised the arena. The ABI handshake
(`abi == 1 && slots == 512`) is version-stable, so **nothing detected that the
plugin's physics had drifted from its source** — which is why the suite sat red
for several runs without anyone noticing. Rebuilding the plugin fixed the test;
`RustCoreInteropTests` was also corrected to read the arena radius from
production rather than restating the obsolete constant.

**The underlying detection gap is still open**: the handshake cannot distinguish a
stale plugin from a current one, so this can recur silently.

### What the parity suite does and does not prove

The parity harness is the strongest thing in the repo: it runs a Rust
`BattleSim` and an independent C# `BattleSim` through identical scripted
scenarios and compares a 14-field trace tick by tick over hundreds of ticks.
Worst measured deviation is `2.0e-5` against a `0.05` tolerance. That is real.

What it proves: **driving, steering, hull rotation, turret slew and clamping,
gun elevation, AI driving, and shell hit *detection*.**

What it does not prove, and this matters:

- **Everything after the first hit.** The comparison deliberately truncates at
  the first damage tick, because the Rust port contains four intentional fixes.
  So plate classification, ricochet scope, damage application, kills and respawns
  are entirely unverified across the boundary — and those are exactly where the
  two implementations now disagree.
- **NaN and Infinity are invisible.** Both comparison loops use
  `if (delta > worst)`, and `NaN > worst` is `false`. A field that has blown up
  on one side is silently skipped. This is the single most serious defect in the
  harness.
- **Clamp constants under 0.05 in their own unit.** Gun pitch is pinned exactly,
  but `MAX_TRAVERSE` has no equivalent — a Rust value of 2.12 instead of 2.1
  produces a 0.02 rad deviation and the traverse tests stay green.
- **3 of 19 vehicles.** Scenarios use `t34-76`, `panther-g` and `tiger2-e`. The
  player's actual vehicle, `pziv-h`, appears in no scenario.
- **Clamping under tolerance.** `ScenariosActuallyReachTheClamps` is the best
  test in the suite — it makes the scenario's *coverage* a first-class assertion
  so a retuned schedule cannot silently stop testing anything. It should be the
  model for the rest.

### Test inventory

**Rust — 177 tests.** 132 unit tests inside the modules (constants, sign
conventions, catalogue relationships, math parity against hard-coded `Mathf`
values), 15 `normalisation_and_ricochet.rs`, 21 `turret_aiming.rs`, 9 FFI
boundary tests.

**Unity EditMode — 91 tests.** 13 battle-sim parity · 8 flight-model parity ·
12 Rust core interop · 14 flight model · 15 normalisation and ricochet ·
8 turret aiming · 6 vehicle damage model · 4 slope movement · 1 terrain match ·
8 hull suspension.

**Unity PlayMode — 36 tests.** 19 gameplay (ground, air, camera, soak) ·
17 rendering.

### Where the coverage has holes

- **`TrackedVehiclePhysics.cs` has zero tests.** The whole file, plus
  `BattleSim.DrivePlayerPhysics`, plus the `UsePhysicsMovement` flag that mutates
  the global `Physics.simulationMode`.
- **`BattleSim.AimWouldPenetrate` has zero C# tests** — the on-screen penetration
  reticle, a player-facing correctness feature. Rust has two dedicated tests for
  the equivalent.
- **The entire modules/crew/spall/HE damage model is unreachable from the game.**
  `VehicleDamageModel` is only called from one test. The game applies a flat
  `−34` / `−26`.
- **`AirCombatSim` is effectively untested** — no EditMode fixture at all, so the
  air gunsight lead computation and air hit resolution are uncovered.
- **No test asserts where the camera is** — which is precisely why the Tab camera
  regression shipped green.

Four Rust tests cannot fail at all — see [Known defects](#known-defects).

### `Tools/SimVerify`

> **The "26 / 26 pass" claim previously in this file is not supported.**
> `dotnet` is not installed on this machine, nothing invokes the harness, and the
> project's own `PORT-NOTES.md:802-807` says *"have never been run on this machine
> and several of which contradict the current NUnit suite — treat those 26 checks
> as unproven rather than as a specification."* It is left here documented but
> unverified.

The design is sound and worth keeping: it compiles the **real** game sources —
not copies — against a ~120-line `UnityShim`. If `Ballistics/` or `Vehicles/`
ever grows a Unity dependency that is not in the shim, it stops compiling. That
is the point: the simulation layer is held to being Unity-free so it stays
testable. But the three damage-model assertions that depend on the unreachable
`VehicleDamageModel` cannot green a shipped build, because the shipped build
never calls it.

### Bugs this harness caught

Worth recording, because none of them would have shown up in playtesting:

1. **Penetrating rounds burst against the outer plate.** APHE/APHEBC that got
   through were detonating *outside* the hull, so they wounded nobody. The burst
   now happens one fuse depth *inside*. This is the entire mechanism by which a
   pen-and-bounce round kills a crew.
2. **Spall cones on explosive rounds.** APHEBC was throwing a solid-projectile
   spall cone, which made high-penetration rounds lethal at absurd range. Cones are
   now restricted to genuinely solid projectiles.
3. **HE could never breach anything.** Modelled as having no penetration at all, so
   HE could not kill a Type 95 Ha-Go through 8 mm. `Shell.BreachesPlate()` now
   decides inside-versus-outside burst.
4. **~60 missing `f` suffixes** on float literals across `VehicleCatalog`, which
   would have failed to compile in Unity.

### Turret and hull behaviour

The turret is the control most sensitive to a player, and the most subtle, so the
behaviour is pinned down explicitly:

- **The barrel points at the aim point on every bearing** within the traverse arc.
  Asserted by dot product against the target rather than by a sign, so an inverted
  turret scores about −1 and cannot hide behind a convention.
- **A target astern stops at the arc limit** rather than passing through. No real
  tank elevates its gun through 180° of traverse.
- **The turret never leaves its arc**, even with the trim slammed back and forth —
  that was how the old clamp was being defeated.
- **Driving lines the hull up with the gun**, so tank and turret move together. This
  is the fix for the turret feeling like it moved opposite to the tank.
- **A stationary tank holds its traverse**, so a player can set up a specific angle
  before moving.
- **Manual steering still works**, and left and right turn opposite ways.
- **Render interpolation** neither steps nor runs ahead of the simulation.

The air EditMode tests are worth calling out because they prove the aircraft are
actually flyable rather than merely present in the catalogue.

```powershell
$u = "C:\Users\varun\Unity\6000.6.4f1\Editor\Unity.exe"
& $u -batchmode -nographics -runTests -projectPath . -testPlatform PlayMode -testResults "$env:TEMP\r.xml"
```

`Tools/SimVerify` compiles the **real** game sources — not copies — against a
~120-line `UnityShim`. If `Ballistics/` or `Vehicles/` ever grows a Unity
dependency that is not in the shim, this stops compiling. That is the point: the
simulation layer is held to being Unity-free so it stays testable.

> **Its 26 assertions are unverified.** `dotnet` is not installed on this machine,
> nothing invokes the harness, and `PORT-NOTES.md:802-807` records that they
> *"have never been run on this machine and several of which contradict the
> current NUnit suite — treat those 26 checks as unproven rather than as a
> specification."* The three that exercise `VehicleDamageModel` cannot green a
> shipped build, because the shipped build never calls it. They are listed below
> as a record of what was *intended* to be pinned, not as passing results.

They encode matchup relationships that must hold, so the maths cannot drift
silently:

- T-34/76 does **not** defeat the Panther's 55° glacis at 0 m (188 mm vs 209 mm
  effective) but does take the side and rear
- Tiger 88 mm **does** defeat the Sherman front at point-blank (266 mm vs 89 mm
  effective) and **fails** the same plate at 1000 m (75 mm) — it trades, and range
  is the whole story
- Pz IV 75 mm fails the Sherman front beyond 500 m
- IS-2 122 mm does **not** defeat the Churchill VII's 20° front (151 mm vs 162 mm
  effective). The Churchill VII was genuinely the one tank the IS-2 could not
  frontally beat, and that assertion exists so it cannot silently change
- HE does not cook a Sherman's ammo rack through 50 mm, but does through the
  Ha-Go's 8 mm
- Ricochet past 70%, penetration decays with range, HEAT does not decay
- No NaN or infinity anywhere in the envelope (19 vehicles × 31 ranges × 90 angles)

### Bugs the Editor compile caught

These only surfaced once a real Editor and the real packages were available:

5. `NetworkVehicle.cs` was missing `using WarArena.Vehicles;`, so neither
   `TankController` nor `VehicleDamageModel` resolved.
6. `com.unity.modules.vehicles` was absent from the manifest, so `WheelCollider`
   did not exist at all.
7. `NetworkManager.NetworkTickSystem.TickRate` is **read-only** in Netcode for
   GameObjects 2.x — tick rate comes from `NetworkConfig`, not runtime code.
8. `ShellImpactManager` referenced a bare `Range` where the active-shell field
   was in scope.

### Bugs the flight model tests caught

The aircraft were the deepest debugging of this project. Every one of these
produced an unflyable or wrong-feeling aircraft, and none would have been obvious
in playtesting — the plane would just have felt bad.

1. **The angle of attack had the wrong sign.** `Vector3.SignedAngle` was called
   with its arguments reversed, so a nose-up attitude reported a *negative* angle
   of attack. Lift was therefore computed downward, and the aircraft was driven
   into the ground with no way to recover.
2. **No pitch stability at all.** Pitch only ever changed from player input, so
   once the velocity aligned with the nose the angle of attack collapsed, lift
   vanished, and the aircraft fell from cruise forever. Real aircraft are
   statically stable; this one had no equilibrium to sit in.
3. **The body axes were not mutually perpendicular.** `Forward`, `Right` and `Up`
   were each built from independent Euler rotations, and because Euler order means
   those rotations do not commute, the three axes disagreed with each other. Lift
   was applied in a direction inconsistent with the nose, so an aircraft sitting
   level and unloaded still sank a few metres per second, and no amount of
   attitude trim could hold it, because the state it was converging to was not
   level flight.
4. **Propeller thrust was constant.** Thrust was applied as a fixed value at any
   speed, which massively over-powered fast aircraft. The Me 262 *lost* to a
   P-51D in level flight, inverting the entire historical point of the matchup.
5. **Induced drag ignored aspect ratio.** Every aircraft used the same effective
   span, so wing shape did not matter and no aircraft could out-turn another for
   the right reason.
6. **The trim system commanded stalls.** The trimmed angle of attack was clamped
   above the stall angle, so any aircraft that slowed down was actively trimmed
   into a stall.
7. **The integrator was unstable.** Explicit Euler on lift is only conditionally
   stable, because lift grows with the square of speed. The Me 262 and the Meteor
   both reached **over 3000 m/s** at the service ceiling. Now bounded by a
   per-step change limit and a speed ceiling well above never-exceed speed.
8. **The Meteor F.4 was modelled as a jet.** It is famously the exception: a
   jet-engined aircraft with a *piston* engine, because the F.4 still drove a
   Derwent 8. Modelling it as a turbojet gave it constant thrust at altitude and a
   drag polar it could not fly, so it stalled to the ground every time.
9. **Speed error was added straight into the pitch rate.** A 60 m/s error then
   demanded about 3°/s of nose-down, swamping the path correction. Aircraft flew
   themselves into the ground at full power with thrust pointed at the floor.
   Speed error is now a bias on the *target flight path*, not a raw rate.
10. **Surplus thrust bought unbounded pitch.** Spare power kept asking for nose-up,
    the angle of attack rose with it, induced drag climbed, speed bled away, and
    every jet stalled at the ceiling. Excess power now buys a *bounded* climb
    angle, which is what it physically does.

The first version of the trim used three contradictory corrections at once
(wings-level attitude, zero surplus thrust, and path damping). Together they
produced nose-down rates that exactly cancelled the path damping, freezing each
aircraft in a permanent 3 m/s descent. Two correct corrections beat three
plausible ones.

### Bugs the rendering tests caught

These are worth listing separately, because **every single one compiled cleanly,
threw nothing, and rendered a plausible-looking frame.** An exception-based test
would have passed all of them. Each is now asserted on the value that was actually
wrong.

1. **Every material was written against the wrong render pipeline.** The project
   runs the **built-in** pipeline — no URP package is installed — but all three
   material factories were written against URP property names and fell back to
   `Standard`. So `_Smoothness` was silently discarded (the built-in shader calls
   it `_Glossiness`), and every surface in the game rendered at the same default
   gloss regardless of what value was requested.
2. **Transparency never worked at all.** `MakeTransparent` set six URP blend-mode
   properties that do not exist on `Standard` or `Unlit/Color`. The aircraft
   canopy, propeller disc, jet exhaust and gunsight were all explicitly configured
   to be see-through and all rendered **fully opaque**. The canopy drew as a solid
   pale blue box.
3. **The sun pointed straight down.** A directional light's *rotation* is its beam
   direction, and the rig set only a position. Every surface was therefore lit
   identically from directly above: nothing had a lit side and a shaded side, forms
   had no volume, and every shadow pooled directly under its object like a dark
   stain rather than reading as a cast shadow.
4. **The sky was never drawn.** The camera cleared to a flat navy fill colour, so
   the top half of every frame was one constant colour and there was no horizon.
5. **Fog and ambient were unrelated.** The fog colour was a dark blue-grey while
   the tree line sat against a flat background, so distance had no cue at all in a
   scene with nothing else to judge it against.
6. **The ground was one flat green plane** — 140 m of untextured primitive, which
   is most of why the battlefield read as a physics test rather than a place.
7. **No shadow bias was tuned**, on a scene made of long thin primitives (barrels,
   tree trunks, wall edges), which is the classic case for shadow acne.
8. **Tracks and barrels were painted, not metal**, so the tanks had no material
   variation at all and read as coloured boxes.
9. **RenderSettings leaked between tests.** The settings are global and the
   PlayMode suite builds a full bootstrap 34 times, so the last test to run left
   the editor lit by a sun that no longer existed. Now captured and restored.

### Bugs found by player reports

These came from actually playing it, which is the point of the exercise.

10. **The turret felt inverted.** It was mathematically correct and felt completely
    backwards, which is the hardest kind of bug to see from the code. Measuring it
    instead of reasoning about sign conventions showed the real cause: the gun
    tracked the mouse in **world** space while the hull kept whatever heading the
    driver chose. So the turret swung the opposite way to the tank every time the
    hull turned, and read as inverted. Fixed by steering the hull onto the gun's
    line while driving, which is what real tracked vehicles do and what every tank
    game does.
11. **The turret could leave its traverse arc.** The trim was added *after* the arc
    clamp, so it could push the requested angle straight back out through the limit.
    A turret past its stop points on the wrong side of the tank, which a player
    cannot distinguish from an inverted one. Clamped after the trim now, and the
    state is clamped too.
12. **The turret aimed from the hull centre** rather than from the ring it is
    mounted on, so the barrel lagged the cursor by the length of the hull whenever
    the tank was sideways to its target.
13. **Everything juddered.** Two separate causes, both of which had to be fixed:
    the vehicles were drawn at the raw simulation position while the camera moved
    smoothly, so the tank visibly stepped against a steady background; and the
    camera moved in `Update`, leaving it half a frame behind the tank. That second
    one mattered more than it looks, because the camera is also what the mouse aim
    ray is cast from — the lag fed back into the aim point, so the turret chased a
    target that was moving *because* of the camera. Fixed with render
    interpolation between fixed ticks, and by moving the camera to `LateUpdate`.
14. **Every shell in the game was dead within a kilometre.** Drag coefficients were
    ~15× too high: an 88 mm round was modelled at 134 m/s after 1000 m, where the
    historical figure is about 800 m/s. No round could ever reach long range, so
    the whole engagement model was flat. Recalibrated against documented residual
    velocities. This was the single most consequential bug found in this pass, and
    it was found by a *test asserting the wrong thing*, not by playtesting.
15. **The penetration reticle could lie.** It duplicated the effective-thickness
    maths inline and had drifted from the real hit resolution — it ignored the
    cast-hull coefficient and range falloff, so it showed green on shots that then
    bounced. Now calls the same `Penetration.Resolve` the simulation uses.
16. **One global ricochet angle for every shell.** Made APCR far too usable against
    sloped armour and HEAT wrongly afraid of it. Now per family.
17. **Normalisation was missing entirely**, so every round behaved as if it had a
    perfectly stable nose. That is the difference between sloped armour being
    beatable and not.

---

## Known defects

Found by reading every file in the repository. **The four CRITICAL simulation
defects and four CRITICAL interop defects are now fixed** — see
[Verification status](#verification-status) for what changed. Everything below
that is not marked ✅ is still open.

### Simulation — CRITICAL ✅ FIXED

**1. ~~85 % of the map cannot shoot.~~** Commit `6f355cf` raised the arena to a
440 m circle and put vehicle `y` on the hills, but the hit test still compared
against an *absolute* `HIT_CEILING_M = 4.5` while the muzzle sits at `y + 2.2`.
520 801 of 608 189 sampled cells sit above 4.5 m.

> **Fixed.** The ceiling is now relative to the target's own ground contact
> (`dy < HIT_CEILING_M && dy > -HIT_FLOOR_M`), pinned by
> `the_hit_volume_follows_the_target_up_the_hills` across y = 0 … 73.7 m.

**2. ~~Turret traverse deadlocks permanently.~~** `slew` took the short way round
an *unbounded* angle while the state was clamped to ±2.1 rad. The stops are
4.2 rad apart — wider than π — so a demand on the far side wrapped through the
back and the clamp pinned the turret forever.

> **Fixed** by adding `slew_bounded`, used for both the turret and the gun pitch.
> `slew` is retained for the AI hull heading, which genuinely is a full circle.
> Pinned by `the_turret_can_recover_from_its_traverse_stop`, which reproduces the
> original stuck-at-2.100 result against the old code.

**3. ~~Hull steering is 30× too sensitive.~~** A missing `* dt`: the desired rate
was never scaled, so `dt` only set the clamp bound.

> **Fixed** in `battle_sim.rs` and `spike.rs`, mirrored in C#. Verified linear:
> 1 % steer → 0.018 rad/s, 10 % → 0.180, 100 % → 1.800. Pinned by
> `steering_is_proportional_to_the_input_not_saturated_by_it`, which reports
> *"a 1 % steer input turned at 0.5400 rad/s, expected ~0.018"* against the old code.

**4. ~~No aircraft can hold altitude.~~** Still open — see MAJOR below.

### Interop — CRITICAL ✅ FIXED

**5. ~~WebGL never finalises the Rust module.~~** `Poll()` was the only caller of
`TryFinalize` and was never called from game code.

> **Fixed** — `WarArenaBootstrap.Update` now calls `WarSimRuntime.Poll()`.

**6. ~~A stale DLL throws out of `Awake()` → empty scene.~~**

> **Fixed** — `wa_spike_state_layout_slots()` is inside the try/catch, and a
> missing export is reported as a status message rather than escaping `Awake`.

**7. ~~`NativeHandle` poisons the type on a missing DLL.~~**

> **Fixed** — it catches `DllNotFoundException` / `EntryPointNotFoundException`
> at initialisation, checks the returned handle for NULL, and `TryFinalize` now
> refuses to set `IsReady` while it is NULL. The doc comment claiming it was a
> `LazyInitializer` is corrected.

**8. ~~WebGL scratch reserved from the top of WASM memory.~~** Still open — the
reservation and its provably-dead guard are unchanged.

### Simulation — MAJOR ✅ PARTLY FIXED

**Rounds tunnelled through their targets — FIXED.** A round leaves the gun at
~336 m/s, which is 11.2 m of travel per 30 Hz tick against a hit cylinder only
6.4 m across, and the test sampled only the endpoint. Roughly 43 % of rounds that
flew straight through a target were never observed inside it. Replaced with
`segment_hits_target`, which solves for the segment's closest approach. Pinned by
`a_round_faster_than_the_hit_cylinder_still_connects` in both Rust and C#
(`BattleSim.SegmentHitsTarget`).

### Simulation — MAJOR

**Aircraft cannot hold altitude.** The trim reaches a steady *descent*, not
equilibrium. Hands off, at the throttle the AI flies:

```
 1 min: player 1108 m | AI 1068 m
 5 min: player  735 m | AI  357 m
 8 min: player  471 m | AI    1.5 m   ← AI on the terrain floor
10 min: player  302 m | AI    1.5 m   (skimming at 181 m/s, never fires again)
```

Sink rates over 20 s: Bf 109 −2.9 m/s, Me 262 −3.5 m/s, **Meteor −5.9 m/s**.
`is_stalled()` returns `false` throughout, so there is no warning.

Two tests appear to police this and do not: the trim test allows **200 m of
drift over 20 s** when the measured drift is 58–118 m — a 3–6 m/s permanent sink
passes with 1.7× margin — and the air-combat test runs **600 frames = 10 s**, a
fiftieth of the time to impact. **This is the one CRITICAL defect still open.**

- **Drag and range never affect a hit.** Penetration is evaluated using distance
  *from the target*, which the hit test has already bounded to ≤ 3.2 m. So
  `pen_at_impact ≈ pen₀ × 0.9997` at any range. The entire drag calibration — and
  the `0.42×` muzzle velocity scale documented as "what makes long-range matchups
  depend on drag" — has no effect in the live hit path. `ActiveShell` carries no
  origin, so this cannot be fixed locally.
- **Two penetration decision paths that disagree.** `BattleSim::resolve_hit` lets
  HEAT through *any* thickness and applies flat 26/34 damage. `Penetration::resolve`
  compares HEAT's 150 mm against a 400 mm plate and returns `NonPenetration`.
  Ricochet is front-plate-only in one and every-plate in the other.
- **`Shell::breaches_plate` has zero non-test callers** — so HE can never breach
  anything, which was supposedly bug #3 fixed.
- **Kinetic energy, spall cones and over-penetration are computed and discarded.**
  `Penetration::resolve` produces them; its one production caller throws away
  everything but `.result`.
- **An exactly-matching round is classified `OverPenetration` and deals zero
  energy** — `residual = max(0, pen − consumed)`, so residual 0 means "overkill".
  Deliberate, pinned by a test, and wrong.
- **Zero-calibre shell produces `NaN`** — `inf/inf` in the de Marre ratio, and the
  guard checks `> 0.0`, which `inf` satisfies.
- **Two contradictory sources of plate construction.** `VehicleCatalog::build_plates`
  hard-codes side and rear as rolled; `struck_plate` applies the cast coefficient
  to all three plates. Only the second is live — and a test pins the first.
- **`AirCombatSim`: the enemy never respawns.** The respawn timer is decremented
  only inside the `player_health > 0` branch, so killing the enemy ends the match
  permanently. `AIR_ARENA_HALF_EXTENT` is applied only to tracers; nothing bounds
  the aircraft — measured 99 km from spawn after 600 s.
- **The "drop the backlog" branch is unreachable.** Both sims reset the
  accumulator only when it exceeds `FIXED_DT × MAX_STEPS`, which cannot happen.
  Real behaviour at 10 fps is silent slow-motion: the sim runs at 4/6 of real time.
- **The flight model is timestep-dependent.** `lerp(pitch_rate, target, 0.35)` is
  a per-*step* constant, and the velocity cap is `8 m/s · mass` regardless of
  `dt`. A player at 30 fps gets a different aircraft than one at 144 fps.
- **Frame-rate parity is unverified on the shipping target.** All parity testing
  happens on Windows x64, where Rust and .NET both reach ucrt. The shipped
  artifact is Emscripten + IL2CPP, where Rust uses musl and .NET uses
  `SystemNative_*`. `exp` and `pow` are not correctly-rounded. This is the largest
  unquantified hole in the "bit parity" claim.

### Interop — MAJOR

- **The WebGL scratch region is reserved from the top of WASM linear memory**,
  which is exactly where Rust's allocator grows upward. The re-reservation guard
  is provably dead code: `statePtr + 24576` is *always* less than `byteLength`.
  When the heap grows past the watermark the state blob is silently corrupted and
  reported as a "magic mismatch" — pointing at the wrong subsystem.

### Tests and tooling — MAJOR

- **Four Rust tests cannot fail.** One has a dead `let _ =` under a comment
  claiming it aims at the flank; one asserts a plate name ∈ {front, side, rear}
  when only those three literals exist; one passes identically with interpolation
  deleted; one asserts `deaths() + kills() < i32::MAX`.
- **A cross-fixture static leak.** `TankControlsPlayModeTests` nulls
  `WarArenaBootstrap.InputReaderFactory` and never restores it.
  `RenderSetupTests` builds a bootstrap without restoring it, so it NREs in
  `Awake()`. Currently masked purely by alphabetical ordering — rename a fixture
  or use `-testFilter` and 17 tests break for no visible reason.
- **Global state never restored after PlayMode**: `Application.targetFrameRate`,
  `QualitySettings.vSyncCount`, and `Physics.simulationMode`.
- **`Random.InitState(42)`** in `TerrainMatchCheckTests` mutates Unity's global
  RNG with no `[TearDown]`.
- **Three `Assert.Pass` exits** in `RenderSetupTests` report green while asserting
  nothing, when artwork is absent.
- **`CameraToggleIsEdgeGated` does not test edge gating** — it compares two noisy
  distance samples and would pass with edge-gating entirely removed.
- **A `Penetrates()` helper aliases and mutates its caller's `Shell`**, so
  successive calls compound the range decay. The Rust mirror takes a copy. The
  headline range assertion in that C# test passes for the wrong reason.
- **Three tests re-assert their own precondition** and are arithmetically
  incapable of failing.
- **No CI.** No `.github/`, no `rust-toolchain.toml`, no `.cargo/config.toml`. Every
  gate is manual, which is why the suite went stale-red without anyone noticing.

### Build tooling — MAJOR

- **`build-wasm.ps1` silently installs a broken `.wasm` if `node` is not on
  PATH.** PowerShell leaves `$LASTEXITCODE` at its previous value — which is `0`
  from the successful `cargo build` two statements earlier — so the interop probe
  passes without ever running. This defeats the script's stated purpose of
  *"verifying it, and refusing to install on failure."* Verified empirically.
- **`build-native.ps1` checks two symbols nothing imports and skips
  `wa_spike_reset`, which `WarSimRuntime.cs` calls.** The check is also a raw byte
  substring scan, not a PE export-table parse.
- **`build-native.ps1` announces `x86_64-pc-windows-msvc` and never passes
  `--target`.** On a MinGW host it would build the wrong DLL, print the wrong
  triple, and install it.
- **The DLL has an unshipped hard dependency on the MSVC runtime** — it imports
  `VCRUNTIME140.dll`, and Unity does not bundle the redistributable for
  third-party plugins. Neither script checks for it or documents it.
- **The interop probe verifies a different memory layout than the `.jslib`
  uses** — 16 KiB reserve with a third region, versus 64 KiB with two. The probe
  passes on builds where the browser corrupts state.
- **`Tools/webgl-server.mjs` crashes on one malformed request.**
  `decodeURIComponent` is outside any try/catch; a single `GET /%zz` from any
  loaded page kills the server. Verified.
- **20 log files, `scratch/` (25 files including binaries) and `__pycache__` are
  tracked.** `.gitignore` misses seven already-untracked logs. `test-run.log` is
  tracked and modified, and contains a real stack trace.

### Editor tooling — MAJOR

- **`ModelPrefabBuilder` always fails.** Its source path
  `Assets/Artwork/SourceModels/T34/T-34-85.fbx` does not exist — there is no `T34`
  directory under `SourceModels`. It logs an error and returns, on **every WebGL
  build**, and the build proceeds with a stale prefab.
- **`EnvironmentPrefabBuilder` reports success while silently shipping fewer
  props.** Missing sources are warnings; `BuildRocks` `continue`s silently; then
  it logs *"All environment prefabs built successfully!"* unconditionally.
- **`SceneBuilder` destroys your open scene with no prompt and no save.**
  `EditorSceneManager.NewScene(EmptyScene, Single)` with unsaved work loses it.
  This is the most dangerous interaction in the Editor folder.
- **`BakeTerrain` deletes any terrain in the open scene**, silently, no Undo.
- **`EditorApplication.Exit()` sits inside `WebBuildSetup`'s build method**,
  ungated on `Application.isBatchMode`.
- **`Directory.CreateDirectory("Assets/...")` is process-CWD-relative**, not
  project-relative — three files do this.
- **`ModelDiagnostics` is entirely unreachable** (no menu item, no caller), probes
  two `Resources` paths the game does not use, and reports `AssetDatabase.IsValidFolder`
  as `isReadable` — which is always `false` for a file.

### `link.xml`

Correct and complete **for the interop layer**, which needs nothing: it uses only
`[DllImport]` on static methods, blittable array marshalling and `Marshal.Copy`,
with no reflection. The collider preservation is the textbook fix for the
documented stripping failure.

Incomplete in two ways: it has **zero coverage for Netcode for GameObjects**
(masked only because WebGL managed stripping is set to `Low` — raise it to Medium
and NGO breaks), and its own comment contradicts the `WheelCollider` entry
directly beneath it.

---

## Historical data sources

- Bovington Tank Museum spec sheets — the canonical mass/armour/speed figures
- ww2db.com — per-vehicle production totals and crew counts
- Britannica, *Tank: Armament, Armor, Mobility* — the APDS/HEAT ammunition
  history used for post-war ammo modelling
- Gaijin's Developers' Diaries Part 1 and 2 — the damage model this mirrors
- `forum.warthunder.com` and `community.gaijin.net` — community disputes about
  specific penetrations, which is where the DeMarr complaints originate
### Bugs the PlayMode tests caught

5. **The tanks were never parented**, so the whole scene graph was orphaned at
   scene level: destroying the bootstrap leaked every object, and nothing could
   find the player tank. Fixed by parenting the runtime root and the vehicles.
6. **`TankView` returned `GameObject` where a `Transform` was expected**,
   which broke track rolling.
7. **`Shell` inside `BattleSim` collided with `Ballistics.Shell`** -- a
   struct cannot contain itself. Renamed to `ActiveShell`.

---

## Playable scene

`Assets/Scenes/Kursk.unity` holds exactly one GameObject, `WarArenaBootstrap`.
The battlefield, both tanks, tracers, reticle, lighting and camera are all built
in code, so there are no serialized references to break. Regenerate with the
`WarArena/Build Playable Scene` menu item — **which destroys your open scene
without a prompt or a save.**

Movement in this scene is **kinematic**, not WheelCollider, so the demo runs with
zero art assets and zero prefab setup. `TankController` (real WheelCollider
physics) and `VehicleCatalogPhysics` remain for when prefabs exist; the view is
driven identically either way, so the art is not what would have to change.

### The arena

The playfield is a **440 m circle**, not a square — `BattleSim.ArenaRadius`.
It was 90 m per side before commit `6f355cf`.

Terrain is analytic: `BattlefieldBuilder.GetTerrainHeight(x, z)` is a
sum-of-sinusoids height function duplicated verbatim in `battle_sim.rs`, with a
flat basin inside r ≤ 85 m and hills rising to ~73.7 m. A 1025² baked
`TerrainData` (34 MB) mirrors it for rendering; `TerrainMatchCheckTests` samples
both and requires agreement within 0.35 m (measured 0.039 m).

`SlopeSpeedFactor` slows a vehicle by the slope gradient projected onto its
direction of travel, and zeroes it past 32° — and the factor is flipped when the
vehicle reverses, so driving downhill is not penalised.

> **Known breakage:** the terrain change is what broke the shell hit test — see
> [Known defects #1](#known-defects). The arena is 440 m; the hit test still
> assumes a 4.5 m ceiling above sea level.

### Artwork

- `Assets/Artwork/Models/PanzerIV/panzer_iv.glb` — 32.9 MB, the player's tank
- `Assets/Resources/Artwork/Models/PanzerIV/panzer_iv_low.glb` — 902 KB, in-game
- `Assets/Artwork/Environment/Props/` — FBX/OBJ ruins, rocks, a T-26 wreck
- `Assets/Resources/Artwork/{Ground,Road,Rubble}/` — 6 CC0 Poly Haven scans, 19 MB
- ~6 MB of textures are **duplicated** between `Artwork/` and
  `Resources/Artwork/Textures/`

`VisualMaterials.ArtworkPresent` reports whether the CC0 scans loaded, and
everything falls back to procedural noise without them. But `BuildArchitecture`
gates the church, watchtowers, wrecks and rocks on `Resources.Load` returning
non-null with **no fallback** — remove the folder and that architecture vanishes
silently, with no warning.