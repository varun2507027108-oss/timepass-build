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
| **3** | `BattleSim` + parity harness | **complete** |
| 4 | `VehicleDamageModel` + remaining bug fixes | not started |
| 5 | `WarSimRuntime` wired into `WarArenaBootstrap` | not started |
| 6 | Full WebGL verification with the real simulation driving the game | not started |

Phase 0 proves the *boundary* works. Phase 1 ports the engine-independent
systems. Phase 2 ports the flight model and air combat -- the highest-risk code
in the project -- and introduces the parity harness that makes "the port is
correct" a measured claim rather than an assertion. Phase 3 does the same for
ground combat, and is where the harness was itself found to be unsound; see §3d.

**Gameplay behaviour has now changed, in Rust only** -- see §7. Phase 3 fixes
four defects in the hit model, one of which made the game unwinnable.

| Check | Phase 1 | Phase 2 | Phase 3 |
|---|---|---|---|
| `cargo test --workspace` | 108 | 153 | **177** |
| 15 `NormalisationAndRicochetTests` ported | all pass | all pass | all pass |
| 14 `FlightModelTests` ported | — | all pass | all pass |
| 8 `TurretAimingTests` ported | — | — | all pass |
| wasm interop probe | pass | pass | **pass** |
| Unity EditMode | 49/49 | 57/57 | **76/76** (57 + 13 parity + 6 damage-model) |
| Unity PlayMode | 36/36 | 36/36 (one known flake, §6) | **36/36 ×3** (flake now fixed, §6) |
| WebGL build + headless Chrome | verified | verified | **verified** |
| shipped `.wasm` | 57.5 KB | 65.4 KB | **78.4 KB** |
| `cargo clippy --workspace --all-targets` | not run | not run | 0 errors, 2 pre-existing warnings |

Phase 3 added 24 Rust tests (13 `turret_aiming` for the fixes, 3 `wa_battle_trace`
ABI, the 8 original `TurretAimingTests`) and 13 C# parity tests. The worst measured
deviation from the C# original across all eight parity scenarios is **1.7e-5 rad**,
on the hard-turn case over 600 ticks.

The `.wasm` grew 13 KB because `BattleSim` is now reachable from an exported
symbol for the first time.

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

## 3d. The parity harness was itself unsound, and how it was caught

The ground parity fixture passed on its first run. It was proving nothing.

**How it was caught.** After the first green run, the Rust `TURRET_SLEW_RATE` was
mutated from 2.6 to 2.65 — a 2% error — and the suite was rerun. **Still 68/68
green.** A parity test that cannot detect a 2% error in a turret slew rate is not a
parity test. Every mutation below is now verified to fail the suite:

| Mutated constant | Error | Result |
|---|---|---|
| `TURRET_SLEW_RATE` | 2.6 → 2.65 (2%) | caught, 1 test |
| `MAX_TRAVERSE` | 2.1 → 2.2 (5%) | caught, 8 tests |
| `MAX_SPEED_MPS` | 12.0 → 12.5 (4%) | caught, 8 tests |
| `MAX_GUN_PITCH_RAD` | 20.0° → 20.4° (2%) | **initially not caught** — see below |

**Why it was blind.** Two independent reasons, and both had to be fixed.

*The scenarios never reached the clamps.* The aim sweep ran at 0.75 deg/tick. But the
hull is pulled onto the gun's line while driving (`HULL_AUTO_ALIGN_STRENGTH = 0.35`),
so a slow sweep is simply *followed* by the hull. Measured over that scenario: turret
yaw spanned **0.47 rad against a 2.1 rad traverse limit**, and gun pitch spanned
**0.0019 rad against a 0.52 rad range**. No clamp was ever reached, so no wrong clamp
could be observed. The fix was to sweep at **6 deg/tick** and put the aim point 30 m
*up*, so the hull cannot keep up and the clamps saturate. `ScenariosActuallyReachTheClamps`
now asserts the coverage rather than assuming it, so a future retune that weakens the
schedule fails loudly instead of silently proving nothing again.

*The tolerance was too loose for clamps.* `Tolerance = 0.05` is right for integrated
fields but useless for a clamp, because a wrong clamp is a one-shot offset, not an
accumulating one: 20.0° → 20.4° is only **0.007 rad** at the limit, seven times inside
the bound. Tightening the general bound is not the fix — the ground sim's AI genuinely
compounds error, and a bound tight enough to catch this would be tight enough to fail
on an unrelated compiler change. So clamps are now checked **separately and exactly**,
by `ClampedFieldsMustMatchExactly`, which asserts on the ceiling the gun settles at
rather than on the trace.

An intermediate attempt at this was itself wrong and was caught the same way: it
compared *every* gun-pitch sample for exact equality. Gun pitch is an integrated value
that merely gets clamped, so away from the limit the two sides differ by an ulp or two
(measured 0.0229406115 vs 0.0229406133) and 8 tests failed on float noise. Comparing
the ceiling isolates the clamp from the integration.

**Generalisable lesson.** A green parity suite is evidence only to the extent that it
can go red. Every parity harness needs mutation testing to establish its own
sensitivity, and the scenarios need assertions about their own coverage rather than
trust in the reader's intent.

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

### Phase 3

Four defects, all in the ground hit model. The first is the most serious bug found
in the entire migration.

**1. Plate selection used an expression containing no heading term.** The C# wrote

```csharp
float lateral = Mathf.Abs((-f.z) * in.x + (-f.z) * in.z);
```

where `f` is the target's forward vector and `in` the incoming round. The third term
should be `f.x`, so this reduces to `|f.z| * |in.x + in.z|` — a quantity with no
geometric meaning, which ignores which way the tank is pointing entirely. Which plate
was struck therefore depended on the round's *world* Z velocity.

Even corrected, the intended expression `|dot(right, incoming)| < 0.4` is wrong,
because it measures distance from the **fore/aft axis**, which cannot tell head-on
from dead astern: both give a small value. A round arriving from directly behind was
classified as a **front** hit.

**2. Consequently, rear armour was impenetrable.** Impact angle was computed from the
hull's *facing* for every plate, so that rear hit measured 180°, past every shell's
ricochet limit. Nothing could be hit from the side or the rear, ever. Since the player
tops 12 m/s against the AI's ~4.3, overtaking the enemy was a one-way trip: you
could drive past it and then never damage it again. This is what made the port's first
end-to-end test fail — every shot ricocheted at 136–147° and no kill ever occurred.

The Rust port classifies by which way the round is arriving relative to the hull
(`FRONT_PLATE_CONE_DEG = 45°`), then measures the impact angle against the normal of
the plate actually struck: `hull + slope` for the front, `|hull - 90|` for the side,
`|180 - hull|` for the rear. Ricochet is now a front-plate-only test.

**3. `FrontSlopeDeg` was read and never used.** The C# assigned `slope` and then
compared `impactAngleDeg` against it without ever adding it in, so a Panther's 55°
glacis behaved as flat plate — the exact thing sloped armour exists to defeat.

**4. The respawn offset counter was `private static`.** Process-global mutable state
shared by every `BattleSim` in existence, so an engagement could respawn to different
positions depending on what else happened to be running. Replays were impossible.
Now per-instance; `two_engagements_do_not_share_state` advances two identical sims
interleaved and compares 13 fields after every one of 1200 steps.

**Also fixed:** `InFlightPositions` returned a shared list the next access cleared
(the Phase 2 tracer bug, repeated), and the managed `OnPlayerFired` event became an
`player_fired_count` edge counter, since Rust has no events to subscribe to and the
count is observable from either side.

#### A sixth bug, self-inflicted and caught immediately

The aiming reticle (`AimWouldPenetrate`) assumed a *front* plate while the shot
resolution classified properly. Once #1 and #2 were fixed the two disagreed: the shot
correctly struck the thin rear plate, while the reticle still measured ~180° against
the sloped glacis and reported "no penetration" — on precisely the shot a flanking
gunner most wants to take. A reticle that lies is worse than no reticle.

Both now call one private `struck_plate(def, heading, from_shooter)` helper, so the
classification cannot drift again. `struck_plate` takes the heading explicitly rather
than deriving it, because a `VehicleDefinition` carries no heading.

Note the AI always turns to face the player, so a live flank shot is unreachable
through `BattleSim`'s public API. That is why the reticle test sweeps the aim point
around the enemy rather than trying to set up a rear engagement.

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

**Fixed, in the test rather than in the game.** `CameraPlacementMatchesMode` failed
intermittently on its own precondition:

```
Trim must actually have slewed the turret, otherwise this test proves nothing
  Expected: greater than 0.100000001f
  But was:  0.0761422515f
```

Observed four times across the migration — 0.0716, 0.0761, 0.0780, 0.0762 rad —
always in the same narrow band, always just short of the 0.1 rad threshold. That
pattern pointed at the test rather than the game, and it was: the test held trim for a
fixed `WaitForSeconds(0.4f)` and then demanded a specific distance had been covered.

Two decayed accumulators feed the turret (`WarArenaBootstrap`: `_turretTrim` integrates
at `1.2/s` and is multiplied by `0.9` every frame once trim input stops), so distance
covered in 0.4 s depends on how many frames actually ran. In batch mode that varies
with machine load and with frame coalescing.

The fix polls the quantity under test — the gunner's eye position — until it has moved
far enough, then until it stops, instead of waiting a fixed duration and hoping.

The second half of the fix needed a second correction. The original settle timeout was
3 s, and that failed too, by 0.0001 s: `_turretTrim` decays by 0.9 **per frame**, so
the number of frames required to fall below the epsilon scales inversely with framerate,
and batch-mode framerates vary. The check is "does it stop", not "does it stop quickly",
so the timeout is now 10 s. After that, four consecutive PlayMode runs passed 36/36.

This is the same lesson as §3d from a different direction: a test whose precondition
depends on frame timing will pass most of the time and fail in exactly the situations
you cannot debug, which is why the fix polls a real quantity rather than tuning a
constant until it stops flaking.

Attribution was measured before the fix, not assumed:

| Tree | Runs | Result |
|---|---|---|
| Baseline (`04dd12a`, changes stashed) | 3 | 36/36, **35/36**, 36/36 |
| With Phase 0 changes | 3 | 36/36, **35/36**, 36/36 |

Failing at the same rate on both confirmed it was pre-existing rather than caused by
the migration, which is why it was left alone for two phases. Fixing it required
touching `Assets/`, so it was deferred until the Rust work was finished and the
signal was no longer muddied.

After the fix: 4 consecutive runs, 36/36 each.

---

## 7. What has deliberately *not* changed yet

- `WarArenaBootstrap` gained exactly one line: `WarSimRuntime.BeginLoad()` in
  `Awake`. It only starts a download and logs. Nothing reads from Rust, so the 36
  PlayMode tests are unaffected.
- **No C# *simulation* file has been deleted or edited.** `BattleSim`, `AirCombatSim`
  and `FlightModel` are byte-identical to their pre-migration state and remain the
  parity oracle. What *was* changed in C# is listed below, and none of it is in a file
  the parity harness compares against.
- Phases 1–4 touch no Unity files at all. Phase 1's Rust is compiled and tested,
  but is not yet reachable from the `extern "C"` surface, so the shipped `.wasm`
  is unchanged in size.
- Phase 1 applies the `BuildDamageModel` cast-hull fix to the **Rust** side only.
  The C# path still has the contradiction, because the C# file is being kept as
  the parity oracle until the Rust core replaces it. When the swap happens, the
  behaviour change lands.
- Phase 3 extends this to all four hit-model fixes. They exist in Rust only, so
  the game as currently played still has them. This is the first phase where the
  Rust and C# implementations are **intentionally** not interchangeable.

### The consequence for the parity test

Because the two implementations diverge on hit *resolution* by design,
`BattleSimParityTests` compares only up to the first tick on which either vehicle
takes damage. That is the strongest honest claim available: everything the two
share — driving, steering, hull rotation, turret slew and traverse, gun elevation,
the AI, shell ballistics in flight, and hit *detection* — must agree exactly, and
the comparison stops precisely where the deliberate divergence begins. See §3a.

## 7a. Bugs fixed in C# that are outside the parity harness

The hit-model fixes above exist only in Rust, because the C# has to stay the oracle.
These four are different: they are in `VehicleDamageModel` and `NetworkVehicle`, which
no parity test compares, so fixing them in C# cannot desynchronise anything. They were
left alone for two phases on the grounds that touching `Assets/` would muddy the parity
signal, then fixed once the Rust work was finished.

All four are covered by `Assets/Tests/EditMode/VehicleDamageModelBugTests.cs`. Each was
verified by **mutation**: the original code was reinstated and the suite re-run to prove
the test actually goes red. This mattered, because three of the six tests as first
written passed against the buggy code:

| Mutation | Caught by |
|---|---|
| `GetPlate` zero-thickness fallback restored | `AMissingPlateMustNotSilentlyBecomeZeroThickness` |
| fire condition restored to `&& AliveCrewCount <= 0` | `FireMustCookTheVehicleOffEvenWithCrewAlive` |
| fire condition given the crew term *and* module term | `FireMustCookTheVehicleOffEvenWithCrewAlive` |
| `HullStrengthPermille` switched back to `/4` | `ReplicatedHullStrengthMustNotAssumeFourCrew` |

The third row is the one worth keeping. Two separate reinstatements of the fire
condition both had to be caught, because they break it in different directions.

Three tests as first written were **discarded and rewritten** after they proved
incapable of failing:

- Two ammo tests fired a shell at the rack's position and compared final HP. The module
  loop destroyed the rack before the cook-off block ran, and once it is at 0 HP the two
  paths are indistinguishable. Reinstating the bug left the suite green. Rewritten to
  pin `ApplyDamage`'s reachability semantics directly, which is where the fault was.
- `TheCookOffMustLandOnlyWhileTheRackStillStands` asserts an ordering guarantee that is
  only visible from inside `ApplyBlast`. It is kept because the guarantee is now real,
  but it is a weaker test than the other five and is labelled as such.

**1. `GetPlate` returned zero thickness for an unknown plate.** The fallback was
`new ArmourPlate("unknown", 0f)`, and a zero-thickness plate stops nothing. Not
hypothetical: `NetworkVehicle` resolves impacts with a hard-coded `"front"` plate name,
so any vehicle definition that fails to register a front plate has *every* round
penetrate cleanly and presents as "this tank takes no damage", with nothing logged.
The fallback is now 200 mm — armour should fail toward the target surviving.

**2. The ammo cook-off was unconditional, and double-counted damage.** `ApplyBlast` did:

```csharp
ammo.ApplyDamage(baseDamage * 3f, blastRadius);
OnModuleDestroyed(ammo);   // return value discarded
```

Two distinct defects in three lines:

- `ApplyDamage` returns `false` *without applying anything* when
  `distance >= HazardRadius`, and the call passed `blastRadius` as the distance. The
  rack's hazard radius is 1.8 m and `blastRadius` is `CaliberMm * 0.022`, so for **any
  shell over ~82 mm** the blast radius exceeded the rack's own, nothing was applied, and
  the cook-off fired anyway — producing a tank reporting `IsDestroyed` with an
  *untouched* ammo module.
- The blast loop above already applies falloff-scaled damage to every module including
  the rack, so a single direct hit cost the rack 4x the blast it was credited with.

Now gated on the rack actually crossing zero, with the redundant radius check removed
by passing the real distance to `ApplyDamage`.

**3. Fire could never cook a tank off.** `Tick` destroyed the vehicle on
`EnginePowerFraction <= 0f && AliveCrewCount <= 0`. Fire attrition only ever damages
*modules* — the crew loop merely decrements recovery timers — so the crew half of the
AND can never become true through fire. Every burning tank consumed all of its modules
and then sat at `IsDestroyed == false` permanently: never removed, never recoverable.
Now destroys when the crew are wiped (which `RecomputeFlags` already handles) **or** when
no module survives.

**4. Replicated hull strength assumed exactly four crew.** `NetworkVehicle` computed
`AliveCrewCount / 4f`. The divisor was never derived from the roster, so a five-crew
vehicle reporting `5/4 = 1.25` was silently flattened by `Mathf.Clamp01` — and the moment
it lost a single crew member it still reported `1.0`. A 20% loss was invisible to clients
until a second casualty, then it jumped. `VehicleDamageModel.CrewHealthFraction` now owns
the division.

Also removed: `RecomputeFlags` looked the gun breech up into a local and never read it.
The behaviour was correct — `CanFireGun` queries the module directly, and a gun kill
should not destroy the vehicle — but a dead local that looks like an unfinished check is
a trap for the next reader, so the reasoning is now recorded as a comment.

**Not fixed:** `VehicleDamageModel`'s dependence on `UnityEngine.Random.value` for engine
fire. It is the last known bug, and the only remaining one that needs a decision rather
than a fix — see §9.

One structural note on bug 4. The fix moved the arithmetic out of `NetworkVehicle` into
`VehicleDamageModel.HullStrengthPermille`, because a `NetworkBehaviour` cannot be
constructed in an EditMode test — which is precisely why the original inline
`AliveCrewCount / 4f` went unnoticed. Mutating `NetworkVehicle` directly was confirmed
to leave the suite green; mutating the new property was caught. The number that crosses
the wire now has exactly one definition, and it is tested.

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

Fixed in Phase 3: items 1, 3, 4 and 5 below, plus the glacis slope and the
rear-armour bug they combine to cause. See §5 Phase 3 and §3d.

| # | Bug | State |
|---|---|---|
| 1 | `BattleSim.ResolveHit` lateral test degenerates to `|incoming.z|`, so the plate is chosen from the shell's world-Z velocity regardless of target heading, and `FrontSlopeDeg` is computed into a dead local. The Panther's 55° glacis is never modelled | **fixed, Phase 3** |
| 2 | `VehicleDamageModel` uses `UnityEngine.Random.value` — non-deterministic, contradicting its own server-replay claim | Phase 4 |
| 3 | `BattleSim._respawnCounter` is `static`, shared across instances | **fixed, Phase 3** |
| 4 | `InFlightPositions` / `TracerPositions` hand out a shared mutable `List` that is cleared and refilled on every get | **fixed, Phases 2 and 3** |
| 5 | Remaining dead locals: `FlightModel`'s `pathCorrection`, `BattleSim`'s `local`/`slope`, `VehicleDamageModel`'s `breech`, `AircraftView.WingRight` | **all fixed** (Phases 2–3, plus §7a for `breech`) |

### Bugs found after this table was written

These were not in the original backlog. All four are documented in §7a with the
reasoning; they are listed here so the backlog stays the single place to look for
outstanding work.

| Bug | State |
|---|---|
| `GetPlate` returns a zero-thickness plate for an unknown name, and `NetworkVehicle` hard-codes `"front"` | **fixed** |
| Ammo cook-off fires unconditionally and double-counts blast damage against the rack | **fixed** |
| Fire attrition can never destroy a vehicle (`&& AliveCrewCount <= 0` is unsatisfiable via fire) | **fixed** |
| Replicated hull strength divides by a hard-coded 4 rather than the roster size | **fixed** |
| `CameraPlacementMatchesMode` depends on frame timing to satisfy its own precondition | **fixed in the test, §6** |

Item 2 above is the only remaining known bug.

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
---

