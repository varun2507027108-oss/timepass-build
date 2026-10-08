# PORT-NOTES.md — migrating `war-arena-unity` from C# to Rust

Every decision that is *not* recoverable by reading the code. Anything a reader
could derive from the source belongs in a comment there, not here.

---

## 0. Status

| Phase | Scope | State |
|---|---|---|
| **0** | Interop feasibility spike | **complete** |
| 1 | `math` + ballistics + catalogues + `DamageModule` | not started |
| 2 | `FlightModel` + `AirCombatSim` | not started |
| 3 | `BattleSim` | not started |
| 4 | `VehicleDamageModel` + bug fixes | not started |
| 5 | `WarSimRuntime` wired into `WarArenaBootstrap` | not started |
| 6 | Full WebGL verification with real simulation | not started |

Phase 0 proves the *boundary* works. It deliberately contains a throwaway
simulation (`war_core::spike`) so the ABI can be validated before 3,100 lines of
game code depend on it. **No gameplay behaviour has changed yet** — see §7.

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

### Still to pin down in Phase 2

**`Quaternion.Euler` order is unverified.** Unity applies Z, then X, then Y.
`FlightModel` and `AircraftView` are calibrated against that, and the C# source
comment explicitly notes that positive X pitch is nose-*down* in Unity while
`AircraftView.Apply` negates `RollDeg`. A naive XYZ-order port inverts pitch and
rolls the wrong way. This will be pinned by test against the C# original during
Phase 2, not assumed.

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

## 5. Bugs found and fixed during Phase 0

Both were found by the new tests, not by inspection.

**Out-of-bounds heap write in the state serialiser.** `write_state` clamped the
slice to the caller's buffer length but then wrote at fixed slot offsets that
could exceed it — `index out of bounds: the len is 8 but the index is 8`. Against
a short caller buffer this would have corrupted the host player's memory rather
than failing loudly. Every write is now bounds-checked against the actual slice.

**A "still loading" guard that could never fire.** `TryFinalize` read the ABI
version into a `uint` and tested `abi < 0`, but the WebGL glue returns `-1` when
not ready — as a `uint` that is `4294967295`. The guard was dead code, so a
still-loading module would have been reported as an ABI mismatch. Now `int`.

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
- No C# simulation file has been deleted or edited. The C# sim remains the
  parity oracle for Phases 1–4.
- Phases 1–4 touch no Unity files at all.

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

## 9. Phase 4 bug-fix backlog (agreed, not yet applied)

1. `BattleSim.ResolveHit` lateral test degenerates to `|incoming.z|`, so the plate
   is chosen from the shell's world-Z velocity regardless of target heading, and
   `FrontSlopeDeg` is computed into a dead local. The Panther's 55° glacis is
   never modelled.
2. `VehicleCatalog.BuildDamageModel` hardcodes `isCast: false`, contradicting
   `VehicleDefinition.IsCastHull`; `BattleSim` honours the flag, this path does not.
3. `VehicleDamageModel` uses `UnityEngine.Random.value` — non-deterministic,
   contradicting its own server-replay claim.
4. `BattleSim._respawnCounter` is `static`, shared across instances.
5. `InFlightPositions` / `TracerPositions` hand out a shared mutable `List`.
6. Dead code to drop, not translate: `FlightModel.StallSpeedBias`,
   `CruiseAngleOfAttackDeg`, `ArmourPlate.StructuralIntegrity`,
   `ModuleKind.FuelTank`/`MachineGun`, `VehicleDefinition.ShellType` (never read),
   `AircraftView.WingRight`, the dead `pathCorrection` / `local` / `slope` /
   `breech` locals.
7. `Shell.HE(caliber, mass, v0, filler, drag)` — its 4th parameter means *filler*
   while all six sibling factories take *penetration*. Rename on port.

**Tuning that must be preserved verbatim** (changing it moves balance):
`ShellType` declaration order, the `0.42×` muzzle-velocity shell scale, the
42/26 flat damage in `BattleSim`, the `0.8×` enemy speed factor, and `f32`
throughout rather than `f64`.

---

## 10. Open decisions

- **HUD.** The DOM/CSS overlay was agreed, but Unity remains the renderer in this
  architecture, so the IMGUI HUD is untouched and the DOM approach is *deferred*
  to any future renderer work. Recorded so it is not lost.
- **`VehicleDamageModel` has no runtime consumer** — `BattleSim` uses flat HP
  numbers and never calls it, and the only tests exercising it are the
  `Tools/SimVerify` checks, which have never been run on this machine and several
  of which contradict the current NUnit suite. Port it for completeness, but treat
  the 26 SimVerify checks as unproven rather than as a specification.
- **Phase 6 needs a decision on the dead 33.8 MB of `.glb` files.** `panzer_iv.glb`
  (32.9 MB) and `panzer_iv_low.glb` (0.86 MB) both have `DefaultImporter` and can
  never load — they are 59% of the `Assets` tree and ship inside the build for
  nothing.