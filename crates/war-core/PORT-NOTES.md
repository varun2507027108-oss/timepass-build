# PORT-NOTES.md — migrating `war-arena-unity` from C# to Rust

Every decision that is *not* recoverable by reading the code. Anything a reader
could derive from the source belongs in a comment there, not here.

---

## 0. Status

| Phase | Scope | State |
|---|---|---|
| **0** | Interop feasibility spike | **complete** |
| **1** | `math` + ballistics + catalogues + `DamageModule` | **complete** |
| **2** | `FlightModel` + `AirCombatSim` + parity harness | **complete** |
| 3 | `BattleSim` | not started |
| 4 | `VehicleDamageModel` + remaining bug fixes | not started |
| 5 | `WarSimRuntime` wired into `WarArenaBootstrap` | not started |
| 6 | Full WebGL verification with the real simulation driving the game | not started |

Phase 0 proves the *boundary* works. Phase 1 ports the engine-independent
systems. Phase 2 ports the flight model and air combat -- the highest-risk code
in the project -- and introduces the parity harness that makes "the port is
correct" a measured claim rather than an assertion.

**No gameplay behaviour has changed yet** — see §7.

| Check | Phase 1 | Phase 2 |
|---|---|---|
| `cargo test --workspace` | 108 | **153** |
| 15 `NormalisationAndRicochetTests` ported | all pass | all pass |
| 14 `FlightModelTests` ported | — | all pass |
| wasm interop probe | pass | pass |
| Unity EditMode | 49/49 | **57/57** (49 + 8 parity) |
| Unity PlayMode | 36/36 | 36/36 (one known flake, §6) |
| WebGL build + headless Chrome | verified | **verified** |
| shipped `.wasm` | 57.5 KB | **65.4 KB** |

The `.wasm` grew by 7.9 KB in Phase 2 because the flight model is now reachable
from an exported symbol. In Phase 1 it was compiled and tested but LTO-stripped,
since nothing in the `extern "C"` surface referenced it yet.

---

## 1. Architecture

Rust owns simulation state. Unity owns presentation. There is exactly one
authoritative copy of the simulation, and it is the Rust one.

```
Assets/Scripts/Interop/WarSimRuntime.cs   thin managed shell: P/Invoke + decode
Assets/Plugins/WebGL/WarCore.jslib        WebGL instantiation glue
Assets/Plugins/x86_64/war_core_ffi.dll    Editor + Windows player
Assets/StreamingAssets/war_core/*.wasm    the module, fetched at runtime
crates/war-core/                          the simulation (no engine, no rendering)
crates/war-core-ffi/                      the C ABI
crates/war-parity/                        trace differ for the Phase 4 port
```

Unity remains the renderer: procedural geometry, materials, the Panzer IV prefab,
the CC0 scan textures and the IMGUI HUD are all still C# and are still the
reference implementation for any future renderer work.

---

## 2. WebGL plugin strategy — the central finding

**Unity's native WebAssembly plugin path cannot be used for a Rust `cdylib`.**

Unity's own documentation (*Web native plug-ins for Emscripten*) states that its
Web plug-in importer accepts Emscripten-produced `.a` / `.bc` archives, and
warns that LLVM artifacts are not portable across compiler versions. The Unity 6
install here bundles **Emscripten 4.0.20** (per
`.../WebGLSupport/BuildTools/Emscripten/emscripten/emscripten-version.txt`),
while Rust uses its own LLVM. There is no supported path for producing a
compatible archive from Rust.

That leaves dynamically-linked side modules, which a Rust `cdylib` is also not.
Measured by `crates/tools/wasm-interop-probe.mjs` §0, which asserts all of this
so a toolchain change fails on the build machine rather than in a browser:

| Property | Measured | Consequence |
|---|---|---|
| Imports | **0** | `WebAssembly.instantiate(module, {})` needs no stubs |
| Exports `memory` | yes | **No shared heap with Unity.** Every buffer must live inside the Rust module's own memory and be copied out. This is why the state crosses as a blob rather than as shared memory |
| Exports `__indirect_function_table` | no | not a dynamic-link side module |
| Exports `__data_end` | no | not an Emscripten archive |

**Chosen route:** ship the `.wasm` as a StreamingAssets file and instantiate it
from a `.jslib`, which is plain JavaScript and unambiguously supported. The C
ABI is then reached through ordinary `[DllImport("__Internal")]`.

### `.jslib` semantics — two traps, both hit

1. **A `.jslib` is evaluated at BUILD time** by `emcc --symbols-only`, to collect
   symbol names. Code outside `mergeInto(...)` executes during that pass, *not*
   in the browser. A top-level IIFE fails the build with
   `ReferenceError: Module is not defined`. All runtime work must be triggered
   from an exported function that C# calls.

2. **A `$`-prefixed key inside the library object is silently dropped.**
   `mergeInto(LibraryManager.library, { $WarCore: {...} })` produces nothing, and
   reading `Module.$WarCore` at runtime throws
   `Cannot read properties of undefined (reading 'loadStarted')`.

   State is therefore created lazily on a plain `Module.__warCore` property from
   inside `war_core_begin_load`, the first function C# always calls. Every other
   export treats a missing state object as "not ready" and returns a harmless
   value rather than throwing, so a bad call order degrades instead of trapping
   the player.

### StreamingAssets location

Unity places WebGL StreamingAssets in a **sibling** of `Build/`, not inside it —
this build produced `StreamingAssets/war_core/war_core.wasm`. The docs do not
state this clearly and it has moved between versions, so `WarCore.jslib` probes
four candidate paths in order rather than hard-coding one.

### Target-feature flags

`crates/tools/build-wasm.ps1` compiles with
`-C target-feature=+simd128,+bulk-memory,+nontrapping-fptoint,+sign-ext` to match
Unity's WebAssembly 2023 build (visible in the emcc command line the build
emits). Mismatched feature sets between the two modules would fail at
instantiation.

---

## 3. Numerical parity: Unity's `Mathf` widens to `double`

`UnityEngine.Mathf.Sin` is literally `(float)Math.Sin(f)` where `Math.Sin` takes
a `double`. Every transcendental is computed in double precision and narrowed.
Calling Rust's `f32::sin()` computes in single precision and **differs**.

Measured over 2,000,000 inputs on x86_64 Windows
(`single_precision_transcendentals_would_have_diverged`):

| Function | Inputs where `f64`-widened ≠ `f32::` |
|---|---|
| `sin` | 2,665 (0.13%) |
| `cos` | 2,675 (0.13%) |
| **`asin`** | **126,897 (6.3%)** |

This matters because `FlightModel` takes an `Asin` on the flight-path angle of
**every step** and `Penetration` an `Acos` on **every shot**. A naive port would
have been wrong ~6% of the time per call, and over 20,000 integration steps that
is enough to flip a stall verdict.

`crates/war-core/src/math.rs` therefore widens every transcendental to `f64`,
computes, and narrows.

Two more Unity-specific details that are *not* recoverable from the Rust side:

- `Mathf.Deg2Rad` (`0.0174532924f`) and `Mathf.Rad2Deg` (`57.29578f`) are rounded
  literals and are **not** reciprocals of each other (the exact reciprocal is
  57.29577951…). Once narrowed to `f32` they happen to round equal, so an `f32`
  assertion cannot prove the distinction — the test checks it in `f64`.
- `Mathf.Sign(0.0)` returns `1.0`, not `0.0`. This matches `f >= 0 ? 1 : -1` in
  Unity, not IEEE signbit.

### Still to pin down in Phase 2 — RESOLVED

**`Quaternion.Euler` order.** Unity applies rotations in **ZXY** order — rotate
Z, then X, then Y — which makes **positive pitch nose-down**. Confirmed from
Unity's own API reference for `Quaternion.Euler` ("The rotation order is ZXY",
"Rotations are applied in ZXY order"), not from memory.

This was flagged as the single highest-risk unknown of the whole migration,
because `FlightModel`'s longitudinal stability term is sign-inverted against it
and `AircraftView.Apply` negates `RollDeg` to compensate. The implementation
composes `qy * qx * qz`.

It is now verified two ways:

1. `euler_order_is_zxy_not_xyz` in `math.rs` asserts the composition **and**
   asserts that the naive `qx * qy * qz` reading is a *different* rotation — so
   the test can actually tell the two apart rather than passing either way.
2. `positive_pitch_is_nose_down` pins the consequence the flight model relies on.

`AircraftView` already compensates by negating roll at the view layer
(`Quaternion.Euler(pitch, yaw, -roll)`). That compensation is correct *because*
the order is ZXY; had the port guessed XYZ, the view layer would have needed
negating on pitch too, and nothing would have said so until the aircraft flew
backwards.

## 3a. The parity harness — making "the port is correct" measurable

Phase 2 introduced the thing the migration plan promised and the project did not
have: a way to compare the Rust and C# simulations directly.

**The problem.** The flight model's correctness rests on sign conventions that
cannot be verified by reading either implementation:

- Unity's ZXY Euler order makes positive pitch nose-*down*.
- Angle of attack is `SignedAngle(forward, flightPath, right)`. Reverse the
  arguments and a nose-up attitude reports negative, so lift is computed
  downward and the aircraft is driven into the ground with no recovery.
- Angle of attack is measured **twice** per step, the second time against the
  attitude the aircraft will actually fly. Reusing the first measurement leaves a
  one-frame lag that shows as a slow steady sink.

A Rust unit test written against the Rust code passes no matter how wrong the
Rust code is, because both share the same wrong assumption. This is not
hypothetical: **four of my first Phase 2 test assertions were wrong**, and each one
looked entirely reasonable.

**The mechanism.** `wa_flight_trace` in the FFI runs a scripted scenario and
writes a per-tick state trace (12 `f32`s: position, velocity, three Euler angles,
throttle, two flags). `FlightModelParityTests` runs the *C# `FlightModel`* through
the identical scenario and compares every tick of both traces.

**Result — seven scenarios, every tick compared:**

| Scenario | What it polices |
|---|---|
| `HandsOffFullPower` | the phugoid; most sensitive to the Euler sign |
| `HandsOffHandsOff` | the surplus-thrust and speed-bias trim terms |
| `SustainedClimb` | pitch clamp, mid-step AoA re-measurement, stall branch |
| `SustainedRollAndRudder` | roll-to-yaw coupling, roll auto-level decay |
| `ADeepStallDivergence` | 2000 ticks of a tumbling deep stall |
| `TheJet` | turbojet thrust branch, transonic drag rise |
| `ThePropellerDrivenJet` | a jet airframe flying a piston engine |

**Measured worst deviation across all seven: 4.9e-4**, on a position value of
2445.31 — a relative error of about **2e-7**, i.e. `f32` round-off accumulating
over 1538 steps. The tolerance is set at 0.05, about 100x that, because the bugs
being policed diverge by orders of magnitude rather than percentages.

The deep-stall scenario is the one worth calling out. Probing showed the Rust
model recovers from a shallow stall and then **re-enters a far deeper one**, with
the angle of attack climbing past 100 degrees, the nose pinned at the -45 degree
clamp, and the aircraft descending to 400 m. That could only have been a faithful
property of this explicitly "conditionally stable" integrator or a porting error,
and reading the code cannot tell you which. Comparing 2000 ticks says the C# does
the same thing. It is **faithful**, and it is now recorded as a test so it stays
visible rather than being rediscovered as a "bug" later.

## 3b. Two more bugs found in Phase 2

**`IndicatedSpeedMs` had its ratio inverted.** The C# computed
`SpeedMs * sqrt(rho0 / rho)`, which at 3000 m turns 162 m/s of true airspeed into
193 m/s — a *faster* reading at altitude. Its own comment admitted as much:
*"Compression makes it read faster than TAS at altitude."* Indicated airspeed works
the other way: lower density means the pitot tube reads slower, because a given
dynamic pressure needs a higher true speed to produce it. Corrected to
`SpeedMs * sqrt(rho / rho0)`.

It has **zero consumers**, so the fix is inert today. Corrected rather than
dropped, because it is a genuinely useful HUD quantity and because a corrected
unused helper is less of a trap than a wrong one someone eventually wires up.

**`AirCombatSim.TracerPositions` handed out a shared mutable list.** The getter
cleared and refilled a private `List` on *every access* and returned it by
reference, so a caller's copy was invalidated by the next read and was mutable by
the caller. That is a borrow-checker violation in Rust and an aliasing bug in C#.
Now returns an owned `Vec<Vec3>`, and `tracer_positions_is_an_owned_snapshot`
asserts the caller really does own it.

## 3c. One latent issue recorded, not fixed

`AirCombatSim.EnemyRoundsLeft` is never decremented — the enemy fire branch sets
its cooldown but consumes no ammunition, so the AI has unlimited rounds. It has
**zero consumers**, so nothing is affected today. Preserved for parity: making it
finite would change no observable behaviour now while quietly changing gameplay
the moment someone wires it to the HUD. Worth deciding when ammo is given meaning.

---

## 4. ABI design decisions

**No structs by value, no arrays.** Every argument is a scalar or a pointer to a
caller-owned buffer. The aim point is three separate `f32`s rather than a
`float[3]`: IL2CPP array marshalling is a well-known source of silent
corruption, and three scalars cannot be marshalled wrongly. (This started as
`float[] aim` and was changed for exactly this reason.)

**State crosses as one flat `f32` blob**, `[magic, abi_version, ...fields]`. The
host checks magic and version on **every frame**, not once at startup, so a
buffer that is silently not being written is caught immediately rather than
manifesting as inexplicably drifting tanks.

**The layout is a named, public contract** (`war_core::spike::layout`), mirrored
in C# by name and asserted by `ManagedSlotConstantsAreUniqueAndFitTheModulesLayout`.
An earlier draft used bare integers in three languages; renumbering a slot then
corrupted everything quietly. `every_slot_is_unique_and_in_range` now prevents
duplicate indices structurally.

**Strings never cross.** Rust serialises the event feed into a length-prefixed
UTF-8 blob (`u32` LE length + bytes) that the host decodes with
`Encoding.UTF8.GetString`. No per-frame managed allocation, no cross-runtime
string marshalling.

**No `unsafe` in C#.** `ProjectSettings.asset` has `allowUnsafeCode: 0`, so
`fixed`/`unsafe` are unavailable. State is read with
`Marshal.Copy(IntPtr, float[], int, int)`, not by pinning a managed array.

**No panics cross the boundary.** `panic = "abort"` in the release profile is a
correctness requirement, not an optimisation: unwinding out of `extern "C"` is
undefined behaviour. Every entry point additionally tolerates a null handle and
returns a sentinel (`NaN`, `0`) rather than trapping, so a bad handle cannot kill
the host player. All of this is asserted by the probe's §8.

**`allowUnsafeCode: 0` also means the `#if UNITY_WEBGL` branch is never compiled
by the test suites.** A duplicate `DllImport` in that branch reached a green
EditMode run and only failed at WebGL build time. *Only the WebGL build validates
that code.* This is a standing verification gap, recorded here so it is not
rediscovered later.

---

## 5. Bugs found and fixed

Found by the ported and new tests, not by inspection.

### Phase 0

**Out-of-bounds heap write in the state serialiser.** `write_state` clamped the
slice to the caller's buffer length but then wrote at fixed slot offsets that
could exceed it — `index out of bounds: the len is 8 but the index is 8`. Against
a short caller buffer this would have corrupted the host player's memory rather
than failing loudly. Every write is now bounds-checked against the actual slice.

**A "still loading" guard that could never fire.** `TryFinalize` read the ABI
version into a `uint` and tested `abi < 0`, but the WebGL glue returns `-1` when
not ready — as a `uint` that is `4294967295`. The guard was dead code, so a
still-loading module would have been reported as an ABI mismatch. Now `int`.

### Phase 1

**`BuildDamageModel` hardcoded `isCast: false`, contradicting `IsCastHull`.**
The Panther and Tiger are cast hulls; `BattleSim` honoured that flag on its own
hit-resolution path while the damage-model path treated every vehicle as rolled.
The two code paths disagreed on cast-hull physics for exactly the vehicles where
it matters most. `VehicleCatalog::build_plates` now honours the flag, matching
`BattleSim`.

**`VehicleDefinition.ShellType` was written but never read.** All 19 vehicles
stored `APHEBC` and `BuildShell` ignored the field, unconditionally returning an
APHEBC round. A catalogue that records what a vehicle carries and then builds
something else is a trap. `build_shell` now dispatches on the field. Behaviour is
unchanged — every vehicle is still APHEBC — and `every_vehicle_still_carries_aphebc`
is the test that guarantees it, alongside `build_shell_reproduces_the_csharp_aphebc_call`
which pins the exact C# expression.

**`CrewMember.RecoveryTimer` could run deeply negative.** The C# was
`if (RecoveryTimer > 0f) RecoveryTimer -= dt;`, so one `tick(1000.0)` left it at
`-994`. Nothing read the magnitude (only `<= 0` was tested), so it was harmless,
but a timer that counts past zero is not what "6 seconds of recovery" means. Now
clamped at zero.

**`Shell.HE`'s fourth parameter meant something different from its six siblings.**
`HE(float caliber, float mass, float v0, float filler, float drag)` took
*filler* where every other factory took *penetration*, so calling it in the
obvious way built a shell with the charge weight in the penetration slot. The
port names the parameter `filler`, forces `penetration_0_mm` to 0, and
`he_takes_filler_not_penetration` pins it.

### Phase 2

**`IndicatedSpeedMs` had its density ratio inverted** — see §3b.

**`AirCombatSim.TracerPositions` returned a shared mutable list** — see §3b.

### Dead code dropped rather than translated

| Removed | Was |
|---|---|
| `ArmourPlate.StructuralIntegrity` | documented, defaulted to 1, never read anywhere |
| `ModuleKind.FuelTank` | never constructed or referenced |
| `ModuleKind.MachineGun` | never constructed or referenced |
| `Shell.penetration_at`'s `impactAngleDeg` | never read; its own docs say obliquity belongs to the plate |
| `DamageModule::apply_damage`'s `bool` return | collapsed "ignored / damaged / destroyed" into one flag that `VehicleDamageModel` branched on — now `DamageOutcome` |
| `FlightModel.StallSpeedBias` | declared `public const`, zero consumers |
| `FlightModel.CruiseAngleOfAttackDeg` | had a long doc comment, never read |
| `FlightModel`'s `pathCorrection` local | computed, then never used — the final `trimPitch` expression is what C# used |

Removing `FuelTank` and `MachineGun` renumbers `AmmoRack` 5→4, `Tracks` 6→5 and
`Optic` 7→6. Nothing serialises the enum, so this is behaviour-preserving; the
values are pinned by `discriminants_are_contiguous_after_dropping_the_dead_variants`.

## 5a. Two places where the C# comments are wrong

Found while porting the data; both were resolved in favour of **the data**, and
the tests assert the data.

**The Zero is not the dirgiest airframe.** The doc comment on
`AircraftDefinition.ParasiteDragCoefficient` says "the Zero's radial engine and
wire-covered wings make it the draggiest fighter here". Its own figures say
otherwise: the Spitfire IX is `0.0250` against the Zero's `0.0185`. The test
`the_spitfire_is_the_dirgiest_airframe_and_the_meteor_the_cleanest` asserts the
figures, with the discrepancy called out in the test.

**Jets are not uniformly faster than props in this roster.** The Meteor F.4 is
`201 m/s`, slower than both the Spitfire IX (`205`) and the P-51D (`220`). Only
the Me 262 (`244`) outruns the fastest prop. This is historically right — the
Meteor was a 1945 interceptor built around the Derwent 8 that won by turning, not
by running away — and it is the entire point of the 262/Meteor pairing. An
earlier version of the port test asserted "jets are faster", which was simply
false; it now asserts the actual, more interesting relationship.

---

## 6. Pre-existing test flakiness (not caused by this work)

`WarArena.Tests.TankControlsPlayModeTests.CameraPlacementMatchesMode` fails
intermittently on its own precondition:

```
Trim must actually have slewed the turret, otherwise this test proves nothing
  Expected: greater than 0.100000001f
  But was:  0.0716481283f
```

Attribution was measured, not assumed:

| Tree | Runs | Result |
|---|---|---|
| Baseline (`04dd12a`, changes stashed) | 3 | 36/36, **35/36**, 36/36 |
| With Phase 0 changes | 3 | 36/36, **35/36**, 36/36 |

Fails at the same rate on both, so it is pre-existing. The cause is that the test
drives `Time.deltaTime` through `[UnityTest]` coroutines, so the distance the
turret slews depends on frame timing; the assertion sits just above the typical
value. Not fixed here — fixing a PlayMode timing test is out of scope for the
migration and would muddy the signal. Worth a separate look.

---

## 7. What has deliberately *not* changed yet

- `WarArenaBootstrap` gained exactly one line: `WarSimRuntime.BeginLoad()` in
  `Awake`. It only starts a download and logs. Nothing reads from Rust, so the 36
  PlayMode tests are unaffected.
- **No C# simulation file has been deleted or edited.** The C# sim remains the
  parity oracle for Phases 2–4.
- Phases 1–4 touch no Unity files at all. Phase 1's Rust is compiled and tested,
  but is not yet reachable from the `extern "C"` surface, so the shipped `.wasm`
  is unchanged in size.
- Phase 1 applies the `BuildDamageModel` cast-hull fix to the **Rust** side only.
  The C# path still has the contradiction, because the C# file is being kept as
  the parity oracle until the Rust core replaces it. When the swap happens, the
  behaviour change lands.

---

## 8. Tooling

| Command | Purpose |
|---|---|
| `cargo test --manifest-path crates/Cargo.toml --workspace` | 27 Rust tests, no Unity needed |
| `crates/tools/build-native.ps1` | builds `war_core_ffi.dll`, checks exports, installs to `Assets/Plugins/x86_64` |
| `crates/tools/build-wasm.ps1` | builds the `.wasm`, **verifies it, and refuses to install on failure**, then installs to `Assets/StreamingAssets/war_core` |
| `node crates/tools/wasm-interop-probe.mjs <wasm>` | 12-section verification outside Unity, including the module-shape assertions that pin §2 |
| `node Tools/webgl-server.mjs` | WebGL dev server with the required COOP/COEP headers |
| `node Tools/verify-webgl-core.mjs` | boots the real player in headless Chrome over CDP and asserts the Rust core reports ready |

`Tools/webgl-server.mjs` also resolves a long-standing item: `PROJECT-DOCUMENTATION.md`
§15.4 noted the dev server "should be moved into `Tools/` so it is
version-controlled and reusable for deployment". It now is, and it prints the
headers it sets, which is the information you actually want when a build will not
boot.

### Unity batch-mode gotchas learned here

- **`-runTests` must NOT be combined with `-quit`.** With `-quit`, Unity exits
  after compiling and the tests never run — it reports success and writes no
  results XML. This is probably the cause of the "`-runTests` hangs" note in
  `PROJECT-DOCUMENTATION.md` §10: the run was never actually hanging, it was
  being killed.
- `-nographics` is fine for EditMode but PlayMode needs it omitted here.

---

## 9. Phase 4 bug-fix backlog

Fixed in Phase 1: the `BuildDamageModel` cast-hull contradiction, the dead
`ShellType` field, the negative `RecoveryTimer`, and `Shell.HE`'s parameter
tr (§5).

Still outstanding, by the phase that owns the file:

| # | Bug | Lands in |
|---|---|---|
| 1 | `BattleSim.ResolveHit` lateral test degenerates to `|incoming.z|`, so the plate is chosen from the shell's world-Z velocity regardless of target heading, and `FrontSlopeDeg` is computed into a dead local. The Panther's 55° glacis is never modelled | Phase 3 |
| 2 | `VehicleDamageModel` uses `UnityEngine.Random.value` — non-deterministic, contradicting its own server-replay claim | Phase 4 |
| 3 | `BattleSim._respawnCounter` is `static`, shared across instances | Phase 3 |
| 4 | `InFlightPositions` / `TracerPositions` hand out a shared mutable `List` that is cleared and refilled on every get | Phase 3 |
| 5 | Remaining dead locals: `FlightModel`'s `pathCorrection`, `BattleSim`'s `local`/`slope`, `VehicleDamageModel`'s `breech`, `AircraftView.WingRight` | Phases 2–3 |

**Tuning that must be preserved verbatim** (changing it moves balance):
`ShellType` declaration order, the `0.42×` muzzle-velocity shell scale, the
42/26 flat damage in `BattleSim`, the `0.8×` enemy speed factor, and `f32`
throughout rather than `f64`.

---

## 10. Open decisions

### Is crew wounding meant to be permanent?

**Found in Phase 1, deliberately not fixed. This needs a decision.**

`CrewMember.Wound()` sets `IsAlive = false` and starts a 6-second
`RecoveryTimer`. Nothing anywhere in the project ever sets `IsAlive` back to
`true`. So:

- `IsAvailable => IsAlive && RecoveryTimer <= 0` can never become true again for a
  wounded crew member.
- The recovery timer is dead: it counts down and is never read for a gameplay
  decision. `IsAvailable` has **zero consumers** — confirmed by searching the
  whole project.
- Every `RecoveryTimer` consumer is either the definition itself or
  `VehicleDamageModel.Tick`, which only decrements it.

The naming (`RecoveryTimer`, `IsAvailable`, six seconds) strongly implies the
intent was *temporary* incapacitation. The C# behaviour is reproduced exactly and
pinned by `recovery_never_revives_a_wounded_crew_member`, because reviving is
**not** a safe silent fix:

| Option | Effect |
|---|---|
| **Keep permanent** (current) | An ammo-rack hit wounds all five crew → `AliveCrewCount == 0` → `IsDestroyed`. The timer and `IsAvailable` are pointless; a follow-up should delete them |
| **Make it temporary** | A tank crewed by wounded-but-recovering men stops counting as destroyed and comes back after 6 s. This also weakens "ammo-rack hits are lethal", which `Tools/SimVerify` asserts as a core matchup |
| **Hybrid** | Distinguish *dead* from *incapacitated*: add a second state, have `AliveCrewCount` count only the dead, and let a tank with no able crew be immobile rather than destroyed |

My read is that the third is what the original author meant, but it is a balance
change to a matchup, so it is not a call to make silently mid-migration.

### Others

- **HUD.** The DOM/CSS overlay was agreed, but Unity remains the renderer in this
  architecture, so the IMGUI HUD is untouched and the DOM approach is *deferred*
  to any future renderer work. Recorded so it is not lost.
- **`VehicleDamageModel` has no runtime consumer** — `BattleSim` uses flat HP
  numbers and never calls it, and the only tests exercising it are the
  `Tools/SimVerify` checks, which have never been run on this machine and several
  of which contradict the current NUnit suite. It is ported in Phase 4 for
  completeness, but treat those 26 checks as unproven rather than as a
  specification.
- **Phase 6 needs a decision on the dead 33.8 MB of `.glb` files.**
  `panzer_iv.glb` (32.9 MB) and `panzer_iv_low.glb` (0.86 MB) both have
  `DefaultImporter` and can never load — they are 59% of the `Assets` tree and
  ship inside the build for nothing.