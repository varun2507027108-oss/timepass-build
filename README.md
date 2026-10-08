# War Arena — Unity 6 (playable)

WW1/WW2 combined-arms vehicle combat with real armour penetration. **Playable
now** — open `Assets/Scenes/Kursk.unity` and press Play.

**This is the Unity version of the earlier `war-arena` web prototype.** That one
still runs and still iterates in under a second, which is why both are kept.

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

Two vehicles: **T-34/76** (you) vs **Panzer IV Ausf. H**. 19 more are in the
catalogue.

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
- **Parasite drag** is per-airframe, so the Zero's radial engine and wire-covered
  wings make it the draggiest fighter and the swept 262 the cleanest.
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
Packages/manifest.json                    Unity 6 + NGO 2.7 + Input System 1.14
Assets/Scripts/
  Ballistics/
    Shell.cs            Ordnance types, de Marre penetration, velocity decay
    Penetration.cs      Plate resolution, ricochet, spall cones
  Air/
    AircraftCatalog.cs  6 historical airframes: mass, wing, thrust, drag
    FlightModel.cs      Forces, stability, trim, stall, integrator
  Gameplay/
    AirCombatSim.cs     Aircraft combat, cannon tracers, health, respawns
    AircraftView.cs     Procedural aircraft rendering
    BattlefieldBuilder.cs   Ground, ruins, tree line, lighting, camera
    VisualMaterials.cs  Shared material/texture/sky construction, both pipelines
    KeyboardAircraftInput.cs  Real aircraft controls
    WarArenaBootstrap.cs     Ground/air mode, runtime construction, HUD, cameras
  Vehicles/
    DamageModule.cs     Subsystems + crew as damageable entities
    VehicleDamageModel.cs  Authoritative hit resolution
    TankController.cs   WheelCollider tracked-vehicle physics + turret
    VehicleCatalog.cs   19 historical vehicles with BR brackets
  Net/
    NetworkVehicle.cs   Server-authoritative vehicle + input submission
    ShellImpactManager.cs  Server-side ballistics, replicated tracers
Assets/Tests/
  EditMode/             Flight model + ballistics (14 tests)
  PlayMode/             Game loop, ground, air, rendering (30 tests)
```

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
| Unity Editor compile (6000.6.4f1, Netcode 2.13.3) | clean, 0 errors |
| **Unity PlayMode tests (real game loop)** | **34 / 34 pass** |
| **Unity EditMode tests (flight model, ballistics, gunnery)** | **37 / 37 pass** |
| Headless `Tools/SimVerify` (.NET SDK) | **26 / 26 pass** |

The 34 PlayMode tests cover 10 ground behaviours (throttle, reverse, hull steering,
turret slew independence, edge-gated camera toggle, shells entering flight, reload
gating, arena clamping, enemy driven by the sim, 600-frame input soak), 7 air
behaviours, and 17 rendering assertions described below.

The 37 EditMode tests cover the flight model, the ballistics, and the gunnery. The
gunnery tests are deliberately written to be **free of sign conventions**: they
compare the barrel against the target and require a positive dot product, so they
are immune to the argument about whether positive yaw means left or right. That is
exactly the kind of thing that hides an inversion.

The ballistics tests are similarly written to assert the *direction* of an effect
rather than a hard-coded number wherever possible, because the first version of
them encoded a wrong assumption — that normalisation reduces the *round's*
penetration rather than the *plate's* required penetration — and would have passed
while the physics was wrong.

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

**26 assertions, all passing.** They encode matchup relationships that must hold,
so the maths cannot drift silently:

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
- Ricochet past 70°, penetration decays with range, HEAT does not decay
- No NaN or infinity anywhere in the envelope (19 vehicles × 31 ranges × 90 angles)

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
`WarArena/Build Playable Scene` menu item.

Movement in this scene is **kinematic**, not WheelCollider, so the demo runs with
zero art assets and zero prefab setup. `TankController` (real WheelCollider
physics) and `VehicleCatalogPhysics` remain for when prefabs exist; the view is
driven identically either way, so the art is not what would have to change.