//! # war-core-ffi
//!
//! The C ABI Unity binds to. Every symbol here is `extern "C"`, has a primitive
//! or pointer-only signature, and is safe to call from an IL2CPP-compiled
//! WebGL player or from the Windows player via P/Invoke.
//!
//! ## Design rules
//!
//! 1. **No panics cross the boundary.** `panic = "abort"` is set in the release
//!    profile, so a panic aborts the module rather than unwinding into a foreign
//!    runtime. Callers additionally treat a `0` handle as "not initialised".
//! 2. **Rust owns all state.** The host holds an opaque `*mut` handle and a
//!    fixed, caller-allocated `f32` out-buffer. Nothing on the Unity side is
//!    authoritative, so there is no possibility of the two sides disagreeing.
//! 3. **No structs by value across the boundary.** IL2CPP marshalling of
//!    non-blittable types is a source of silent corruption; every argument is a
//!    scalar or a raw pointer to a caller-owned buffer.
//! 4. **Strings are owned by Rust.** The feed is serialised to a length-prefixed
//!    UTF-8 blob the host decodes, so there is no per-frame string allocation
//!    and no cross-runtime string marshalling.
//!
//! ## Phase 0 status
//!
//! These symbols currently wrap the temporary [`war_core::spike`] harness. They
//! will be renamed to `wa_battle_*` / `wa_air_*` as the real systems land. The
//! *shape* of the ABI is final; only the bodies change.

use core::ffi::c_char;

use war_core::math::Vec3;
use war_core::spike::{layout, SpikeSim, STATE_ABI_VERSION, STATE_SLOTS};

// ---------------------------------------------------------------------------
// Version handshake
// ---------------------------------------------------------------------------

/// Schema version of this ABI. The host asserts this at startup.
///
/// Getting this wrong is the failure mode that otherwise surfaces as plausible
/// but wrong floats, so it is checked explicitly on both sides.
///
/// # Safety
/// Safe to call from any thread; touches no shared state.
#[no_mangle]
pub extern "C" fn wa_spike_abi_version() -> u32 {
    STATE_ABI_VERSION
}

/// Number of `f32` slots the host must allocate for the state blob.
///
/// Exposed so the C# side sizes its array from the module rather than from a
/// hard-coded constant that could drift.
///
/// # Safety
/// Safe to call from any thread.
#[no_mangle]
pub extern "C" fn wa_spike_state_slots() -> u32 {
    STATE_SLOTS as u32
}

/// Number of slots the module actually *uses*, which is fewer than
/// [`wa_spike_state_slots`]; the remainder is headroom so the schema can grow
/// without a host change.
///
/// The host asserts its own highest slot index is below this, which is the one
/// check that catches C# and Rust disagreeing about where a field lives.
///
/// # Safety
/// Safe to call from any thread.
#[no_mangle]
pub extern "C" fn wa_spike_state_layout_slots() -> u32 {
    layout::USED as u32
}

// ---------------------------------------------------------------------------
// Scalar round-trip
// ---------------------------------------------------------------------------

/// Scalar in, scalar out, via a live handle. Proves that a call which mutates
/// simulation state across the ABI boundary returns a consistent value.
///
/// # Safety
/// `handle` must be a pointer previously returned by [`wa_spike_create`] and not
/// yet passed to [`wa_spike_destroy`]. Must not be `NULL`.
#[no_mangle]
pub unsafe extern "C" fn wa_spike_echo(handle: *mut SpikeSim, x: f32) -> f32 {
    // SAFETY: caller guarantees `handle` is a live, non-null handle.
    match unsafe { handle.as_mut() } {
        Some(sim) => sim.echo(x),
        // Returning a sentinel rather than panicking keeps a bad handle from
        // killing the host player.
        None => f32::NAN,
    }
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

/// Allocate a simulation. The host must eventually call [`wa_spike_destroy`].
///
/// Returns `NULL` on allocation failure. Note the returned pointer is opaque to
/// the host and must not be dereferenced on the C# side.
///
/// # Safety
/// Safe to call from any thread.
#[no_mangle]
pub extern "C" fn wa_spike_create() -> *mut SpikeSim {
    Box::into_raw(Box::new(SpikeSim::new()))
}

/// Destroy a simulation created by [`wa_spike_create`]. Passing `NULL` is a
/// no-op, so the host can call this unconditionally during teardown.
///
/// # Safety
/// `handle` must be `NULL` or a pointer from [`wa_spike_create`] that has not
/// already been destroyed. Calling twice with the same handle is a double free.
#[no_mangle]
pub unsafe extern "C" fn wa_spike_destroy(handle: *mut SpikeSim) {
    if !handle.is_null() {
        // SAFETY: caller guarantees `handle` came from `Box::into_raw` in
        // `wa_spike_create` and has not been freed.
        drop(unsafe { Box::from_raw(handle) });
    }
}

// ---------------------------------------------------------------------------
// Simulation step
// ---------------------------------------------------------------------------

/// Advance the simulation by one frame.
///
/// `aim_x`, `aim_y` and `aim_z` are passed as three separate `f32`s rather than
/// a pointer to a `f32[3]`. A pointer would require marshalling an array across
/// the ABI, and IL2CPP array marshalling is a well-known source of silent
/// corruption; three scalars cannot be marshalled wrongly.
///
/// Returns `1` if the step ran, `0` if the handle was invalid.
///
/// # Safety
/// `handle` must be a live handle from [`wa_spike_create`]. Must not be called
/// concurrently on the same handle.
#[no_mangle]
pub unsafe extern "C" fn wa_spike_advance(
    handle: *mut SpikeSim,
    dt: f32,
    throttle: f32,
    steer: f32,
    turret_trim: f32,
    fire: u32,
    aim_x: f32,
    aim_y: f32,
    aim_z: f32,
) -> u32 {
    let Some(sim) = (unsafe { handle.as_mut() }) else {
        return 0;
    };
    sim.advance(
        dt,
        throttle,
        steer,
        Vec3::new(aim_x, aim_y, aim_z),
        fire != 0,
    );
    // `turret_trim` is accepted so the real signature is exercised end to end,
    // even though the spike does not use it.
    let _ = turret_trim;
    1
}

/// Restore a simulation to its just-constructed state without freeing it.
///
/// Needed because a handle is long-lived (one per session), so anything that
/// wants a known starting point -- the interop tests, or a match restart --
/// cannot rely on constructing a fresh handle.
///
/// # Safety
/// `handle` must be a live handle from [`wa_spike_create`]. Returns 0 if the
/// handle is invalid.
#[no_mangle]
pub unsafe extern "C" fn wa_spike_reset(handle: *mut SpikeSim) -> u32 {
    match unsafe { handle.as_mut() } {
        Some(sim) => {
            *sim = SpikeSim::new();
            1
        }
        None => 0,
    }
}

// ---------------------------------------------------------------------------
// State read-back
// ---------------------------------------------------------------------------

/// Write the full state snapshot into a host-provided `f32` buffer.
///
/// Returns the number of slots written, which is `min(cap, STATE_SLOTS)`. The
/// host must pre-fill the buffer and check `wa_spike_abi_version()` at startup.
///
/// # Safety
/// `handle` must be a live handle. `out` must be `NULL` or point to at least
/// `cap` writable `f32`s.
#[no_mangle]
pub unsafe extern "C" fn wa_spike_read(handle: *mut SpikeSim, out: *mut f32, cap: u32) -> u32 {
    let cap = cap as usize;
    let Some(sim) = (unsafe { handle.as_mut() }) else {
        return 0;
    };
    if out.is_null() {
        return 0;
    }
    // SAFETY: caller guarantees `out` is valid for `cap` writable f32s.
    unsafe { sim.write_state(out, cap) }
}

/// Serialise the event feed as a length-prefixed UTF-8 blob.
///
/// Format: repeated `u32` little-endian byte length followed by that many UTF-8
/// bytes. Returns bytes written, clamped to `cap`.
///
/// # Safety
/// `handle` must be a live handle. `out` must be `NULL` or point to at least
/// `cap` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn wa_spike_feed(handle: *mut SpikeSim, out: *mut u8, cap: u32) -> u32 {
    let cap = cap as usize;
    let Some(sim) = (unsafe { handle.as_ref() }) else {
        return 0;
    };
    if out.is_null() {
        return 0;
    }
    // SAFETY: caller guarantees `out` is valid for `cap` writable bytes.
    unsafe { sim.feed(out, cap) }
}

// ---------------------------------------------------------------------------
// String probe
// ---------------------------------------------------------------------------

/// Copy a NUL-terminated identifier out of Rust's memory into a host buffer.
///
/// `player_id` / `enemy_id` must point to readable bytes. This exists because the
/// real constructors take vehicle and aircraft ids, and it lets the spike prove
/// that byte-slice arguments survive the boundary before those constructors
/// exist.
///
/// # Safety
/// `handle` must be a live handle. `player_id` and `enemy_id` must each be
/// `NULL` or point to at least `player_id_len` / `enemy_id_len` readable bytes.
/// `out` must be `NULL` or point to at least `cap` writable bytes. The result is
/// **not** NUL-terminated; use the returned length.
#[no_mangle]
pub unsafe extern "C" fn wa_spike_echo_ids(
    handle: *mut SpikeSim,
    player_id: *const u8,
    player_id_len: u32,
    enemy_id: *const u8,
    enemy_id_len: u32,
    out: *mut u8,
    cap: u32,
) -> u32 {
    if handle.is_null() || out.is_null() {
        return 0;
    }
    let cap = cap as usize;
    // SAFETY: caller guarantees each pointer is valid for its stated length.
    let player = if player_id.is_null() {
        &[][..]
    } else {
        unsafe { core::slice::from_raw_parts(player_id, player_id_len as usize) }
    };
    // SAFETY: as above.
    let enemy = if enemy_id.is_null() {
        &[][..]
    } else {
        unsafe { core::slice::from_raw_parts(enemy_id, enemy_id_len as usize) }
    };

    let mut combined = Vec::with_capacity(player.len() + 1 + enemy.len());
    combined.extend_from_slice(player);
    combined.push(b'|');
    combined.extend_from_slice(enemy);

    let n = core::cmp::min(cap, combined.len());
    // SAFETY: `n <= cap` and the caller guarantees `out` is valid for `cap`.
    unsafe { core::ptr::copy_nonoverlapping(combined.as_ptr(), out, n) };
    n as u32
}

/// Placeholder so the `c_char` import is exercised; removed with the real
/// string-returning entry points.
#[allow(dead_code)]
fn _unused(_: *const c_char) {}

// ---------------------------------------------------------------------------
// Flight model parity trace
// ---------------------------------------------------------------------------

/// Number of `f32` values written per simulation tick by
/// [`wa_flight_trace`]. Mirrored by `FlightModelParityTests.TraceStride` on the C#
/// side; if either changes the other must, which is exactly what the parity test
/// is there to catch.
pub const FLIGHT_TRACE_STRIDE: u32 = 12;

/// Run a scripted flight scenario and write a per-tick state trace.
///
/// This exists because the flight model's correctness rests on sign conventions
/// that cannot be verified by reading either implementation. Unity's ZXY Euler
/// order makes positive pitch nose-*down*; `FlightModel` depends on that for its
/// entire longitudinal stability term; and the angle-of-attack measurement has a
/// specific argument order whose reversal drives the aircraft into the ground with
/// no way to recover. A unit test written against the Rust code would agree with
/// the Rust code no matter how wrong it was.
///
/// So the C# `FlightModel` is run through the identical scenario in the Unity
/// Editor and every tick of both traces is compared. See
/// `Assets/Tests/EditMode/FlightModelParityTests.cs`.
///
/// `aircraft_id` must point to `aircraft_id_len` readable ASCII bytes. `out` must
/// be `NULL` or point to at least `cap` writable `f32`s.
///
/// Returns the number of `f32`s written, or 0 on error.
///
/// # Safety
/// All pointer arguments must satisfy the above. Must not race another call on the
/// same buffer.
#[no_mangle]
pub unsafe extern "C" fn wa_flight_trace(
    aircraft_id: *const u8,
    aircraft_id_len: u32,
    altitude: f32,
    heading_deg: f32,
    ticks: u32,
    dt: f32,
    pitch_in: f32,
    roll_in: f32,
    yaw_in: f32,
    throttle_in: f32,
    out: *mut f32,
    cap: u32,
) -> u32 {
    if out.is_null() || aircraft_id.is_null() {
        return 0;
    }
    // SAFETY: caller guarantees `aircraft_id` is readable for `aircraft_id_len`.
    let id_bytes = unsafe { core::slice::from_raw_parts(aircraft_id, aircraft_id_len as usize) };
    let Ok(id) = core::str::from_utf8(id_bytes) else {
        return 0;
    };
    let Some(spec) = war_core::air::AircraftCatalog::get(id) else {
        return 0;
    };

    let mut model =
        war_core::air::FlightModel::new(spec, Vec3::new(0.0, altitude, 0.0), heading_deg);

    let stride = FLIGHT_TRACE_STRIDE as usize;
    let capacity = cap as usize / stride;
    let ticks_to_run = core::cmp::min(ticks as usize, capacity);

    // SAFETY: caller guarantees `out` is valid for `cap` writable f32s, and every
    // write below is bounded by `ticks_to_run * stride <= cap`.
    let trace = unsafe { core::slice::from_raw_parts_mut(out, cap as usize) };

    for tick in 0..ticks_to_run {
        model.step(dt, pitch_in, roll_in, yaw_in, throttle_in);

        let base = tick * stride;
        let p = model.position();
        let v = model.velocity();
        trace[base] = p.x;
        trace[base + 1] = p.y;
        trace[base + 2] = p.z;
        trace[base + 3] = v.x;
        trace[base + 4] = v.y;
        trace[base + 5] = v.z;
        trace[base + 6] = model.pitch_deg();
        trace[base + 7] = model.yaw_deg();
        trace[base + 8] = model.roll_deg();
        trace[base + 9] = model.throttle();
        trace[base + 10] = if model.is_stalled() { 1.0 } else { 0.0 };
        trace[base + 11] = if model.is_overstressed() { 1.0 } else { 0.0 };
    }

    (ticks_to_run * stride) as u32
}

// ---------------------------------------------------------------------------
// Battle sim parity trace
// ---------------------------------------------------------------------------

/// Number of `f32` values written per simulation tick by [`wa_battle_trace`].
/// Mirrored by `BattleSimParityTests.TraceStride` on the C# side.
pub const BATTLE_TRACE_STRIDE: u32 = 17;

/// Trace field indices, in write order. Kept explicit because the C# side indexes
/// the same layout positionally and a silent reorder would compare nonsense.
pub mod battle_trace_field {
    pub const PLAYER_X: usize = 0;
    pub const PLAYER_Z: usize = 1;
    pub const ENEMY_X: usize = 2;
    pub const ENEMY_Z: usize = 3;
    pub const PLAYER_HEADING: usize = 4;
    pub const PLAYER_TURRET_YAW: usize = 5;
    pub const PLAYER_GUN_PITCH: usize = 6;
    pub const ENEMY_HEADING: usize = 7;
    pub const ENEMY_TURRET_YAW: usize = 8;
    pub const PLAYER_SPEED: usize = 9;
    pub const PLAYER_HEALTH: usize = 10;
    pub const ENEMY_HEALTH: usize = 11;
    pub const KILLS: usize = 12;
    pub const RELOAD_REMAINING: usize = 13;
    /// Hull pitch, degrees, nose-up positive.
    ///
    /// Carried so the parity harness actually exercises the suspension. Without
    /// these three the two implementations could disagree completely about the
    /// vehicle's attitude and every trace would still match, because attitude used
    /// to be a pure function of the terrain slope and therefore redundant.
    pub const HULL_PITCH: usize = 14;
    /// Hull roll, degrees, right-side-up positive.
    pub const HULL_ROLL: usize = 15;
    /// Suspension ride height of the contact plane, metres.
    pub const HULL_HEIGHT: usize = 16;
}

/// Radius of the scripted aim circle used by aim mode 0.
const SWEEP_RADIUS_M: f32 = 60.0;
/// Bearing at tick 0 for aim mode 0.
const SWEEP_BASE_DEG: f32 = 25.0;
/// Bearing advance per tick for aim mode 0.
///
/// **3 deg/tick is load-bearing, and was arrived at empirically.** The turret slews
/// at TURRET_SLEW_RATE = 2.6 rad/s, which is about 5 deg/tick. For the scenario to
/// exercise the traverse clamp the aim must move SLOWER than that, so the turret
/// can actually track it all the way round and press against a stop. A faster sweep
/// does the opposite of what it looks like: at 10 deg/tick the turret is outrun,
/// chases a target that wraps past +/-pi, and oscillates within +/-1.5 rad of centre
/// without ever reaching either stop — the coverage guard caught exactly this.
///
/// Two earlier values failed the same way for different reasons. 0.75 deg/tick let
/// the hull follow the aim so closely that the turret spanned only 0.47 rad against
/// a 2.1 rad limit. 6 deg/tick saturated only while the hull steering bug pinned
/// the hull at maximum turn rate; once steering became proportional to input, the
/// hull stopped chasing and the sweep had to be slowed instead.
///
/// MUST match `SweepRateDeg` in
/// Assets/Tests/EditMode/BattleSimParityTests.cs. They are the same schedule
/// expressed twice, and the parity comparison is meaningless if they drift: a
/// mismatch shows up as a turret-yaw divergence rather than as an obvious
/// configuration error.
const SWEEP_RATE_DEG: f32 = 3.0;

/// Height of the aim point for aim mode 0, metres.
///
/// Above the muzzle, so the gun must depress to reach it and the upper pitch clamp
/// saturates. Kept separate from `SWEEP_RADIUS_M` so the elevation clamps are
/// genuinely exercised rather than approximated by a long, flat trajectory.
const SWEEP_HEIGHT_M: f32 = 30.0;

/// Aim point for a scripted battle scenario.
///
/// **Both implementations must compute this identically**, because it is the only
/// coupling between the two traces: every other input is constant for the run.
///
/// Mode 0 is deliberately a pure function of `tick` and *not* of the simulation
/// state. If the aim depended on the vehicle's position, then any tiny
/// floating-point difference in position would feed back into the aim, then into
/// the turret angle, then into where the vehicle ends up -- a divergence amplifier
/// that would make a 900-tick trace impossible to compare for reasons that have
/// nothing to do with porting fidelity. A state-independent schedule keeps the
/// comparison honest: any difference that appears is a real difference.
fn scripted_aim(mode: u32, tick: u32, enemy_position: Vec3) -> Vec3 {
    match mode {
0 => {
            let bearing = war_core::math::mathf::DEG2RAD
                * (SWEEP_BASE_DEG + tick as f32 * SWEEP_RATE_DEG);
            Vec3::new(
                SWEEP_RADIUS_M * war_core::math::mathf::cos(bearing),
                SWEEP_HEIGHT_M,
                SWEEP_RADIUS_M * war_core::math::mathf::sin(bearing),
            )
        }
        // Aim at the enemy, at muzzle height. State-coupled by design: this is what
        // exercises turret tracking and the fire solution, and the C# parity test
        // only compares the ticks before the first damage lands.
        _ => Vec3::new(enemy_position.x, 1.6, enemy_position.z),
    }
}

/// Run a scripted ground engagement and write a per-tick state trace.
///
/// This is the ground-combat counterpart to [`wa_flight_trace`], and it exists for
/// the same reason: the model is full of sign conventions, clamp orders and
/// thresholds that cannot be verified by reading either implementation, and a Rust
/// unit test only proves the Rust agrees with itself. Running the C# `BattleSim`
/// and the Rust `BattleSim` through one identical scenario and comparing every
/// tick is the only check that catches a ported-but-wrong constant. See
/// `Assets/Tests/EditMode/BattleSimParityTests.cs`.
///
/// Only fields present on *both* sides are traced, so the layout is identical:
/// the C# exposes `OnPlayerFired` as an event and has no enemy gun pitch, so
/// neither is compared. Player-fired is observable through the ammo counter in
/// either implementation.
///
/// # Safety
/// `player_vehicle_id` must point to `player_vehicle_id_len` readable ASCII bytes,
/// likewise for the enemy id. `out` must be `NULL` or point to at least `cap`
/// writable `f32`s. Must not race another call on the same buffer.
#[no_mangle]
pub unsafe extern "C" fn wa_battle_trace(
    player_vehicle_id: *const u8,
    player_vehicle_id_len: u32,
    enemy_vehicle_id: *const u8,
    enemy_vehicle_id_len: u32,
    ticks: u32,
    dt: f32,
    throttle: f32,
    steer: f32,
    turret_trim: f32,
    fire: u32,
    aim_mode: u32,
    out: *mut f32,
    cap: u32,
) -> u32 {
    if out.is_null() || player_vehicle_id.is_null() || enemy_vehicle_id.is_null() {
        return 0;
    }
    // SAFETY: caller guarantees both id pointers are readable for their lengths.
    let player_bytes =
        unsafe { core::slice::from_raw_parts(player_vehicle_id, player_vehicle_id_len as usize) };
    // SAFETY: as above.
    let enemy_bytes =
        unsafe { core::slice::from_raw_parts(enemy_vehicle_id, enemy_vehicle_id_len as usize) };
    let (Ok(player_id), Ok(enemy_id)) = (
        core::str::from_utf8(player_bytes),
        core::str::from_utf8(enemy_bytes),
    ) else {
        return 0;
    };
    if war_core::vehicles::VehicleCatalog::get(player_id).is_none()
        || war_core::vehicles::VehicleCatalog::get(enemy_id).is_none()
    {
        return 0;
    }

    let mut sim = war_core::gameplay::BattleSim::new(player_id, enemy_id);

    let stride = BATTLE_TRACE_STRIDE as usize;
    let capacity = cap as usize / stride;
    let ticks_to_run = core::cmp::min(ticks as usize, capacity);

    // SAFETY: caller guarantees `out` is valid for `cap` writable f32s, and every
    // write below is bounded by `ticks_to_run * stride <= cap`.
    let trace = unsafe { core::slice::from_raw_parts_mut(out, cap as usize) };

    for tick in 0..ticks_to_run {
        let aim = scripted_aim(aim_mode, tick as u32, sim.enemy_position());
        sim.advance(dt, throttle, steer, turret_trim, fire != 0, aim);

        let base = tick * stride;
        use battle_trace_field::*;
        let p = sim.player_position();
        let e = sim.enemy_position();
        trace[base + PLAYER_X] = p.x;
        trace[base + PLAYER_Z] = p.z;
        trace[base + ENEMY_X] = e.x;
        trace[base + ENEMY_Z] = e.z;
        trace[base + PLAYER_HEADING] = sim.player_heading_rad();
        trace[base + PLAYER_TURRET_YAW] = sim.player_turret_yaw_rad();
        trace[base + PLAYER_GUN_PITCH] = sim.player_gun_pitch_rad();
        trace[base + ENEMY_HEADING] = sim.enemy_heading_rad();
        trace[base + ENEMY_TURRET_YAW] = sim.enemy_turret_yaw_rad();
        trace[base + PLAYER_SPEED] = sim.player_speed_kph();
        trace[base + PLAYER_HEALTH] = sim.player_health() as f32;
        trace[base + ENEMY_HEALTH] = sim.enemy_health() as f32;
        trace[base + KILLS] = sim.kills() as f32;
        trace[base + RELOAD_REMAINING] = sim.reload_remaining();
        trace[base + HULL_PITCH] = sim.hull_pitch_deg();
        trace[base + HULL_ROLL] = sim.hull_roll_deg();
        trace[base + HULL_HEIGHT] = sim.player_position().y;
    }

    (ticks_to_run * stride) as u32
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn abi_version_and_slot_count_are_exposed() {
        assert_eq!(wa_spike_abi_version(), STATE_ABI_VERSION);
        assert_eq!(wa_spike_state_slots() as usize, STATE_SLOTS);
        assert_eq!(
            wa_spike_state_layout_slots() as usize,
            layout::USED,
            "the host must be able to check its highest slot against this"
        );
        // Both sides are compile-time constants, so a plain ssert! folds away and
        // clippy correctly reports it as a constant value. lack_box keeps the
        // invariant checked at runtime without weakening it.
        assert!(core::hint::black_box(layout::USED) <= STATE_SLOTS);
    }

    #[test]
    fn null_handles_are_tolerated_everywhere() {
        let mut buf = [0.0f32; STATE_SLOTS];
        let mut bytes = [0u8; 64];
        // None of these may trap: a bad handle must not kill the host player.
        unsafe {
            assert!(wa_spike_echo(core::ptr::null_mut(), 1.0).is_nan());
            assert_eq!(
                wa_spike_advance(
                    core::ptr::null_mut(),
                    0.016,
                    0.0,
                    0.0,
                    0.0,
                    0,
                    0.0,
                    0.0,
                    0.0
                ),
                0
            );
            assert_eq!(
                wa_spike_read(core::ptr::null_mut(), buf.as_mut_ptr(), buf.len() as u32),
                0
            );
            assert_eq!(
                wa_spike_feed(
                    core::ptr::null_mut(),
                    bytes.as_mut_ptr(),
                    bytes.len() as u32
                ),
                0
            );
            assert_eq!(
                wa_spike_echo_ids(
                    core::ptr::null_mut(),
                    core::ptr::null(),
                    0,
                    core::ptr::null(),
                    0,
                    bytes.as_mut_ptr(),
                    bytes.len() as u32,
                ),
                0
            );
            // Destroying NULL is explicitly a no-op.
            wa_spike_destroy(core::ptr::null_mut());
        }
    }

    #[test]
    fn battle_trace_rejects_bad_arguments_instead_of_trapping() {
        let mut buf = [0.0f32; BATTLE_TRACE_STRIDE as usize * 4];
        let p = b"t34-76";
        let e = b"panther-g";
        unsafe {
            assert_eq!(
                wa_battle_trace(
                    core::ptr::null(),
                    6,
                    e.as_ptr(),
                    e.len() as u32,
                    4,
                    0.033,
                    1.0,
                    0.0,
                    0.0,
                    0,
                    1,
                    buf.as_mut_ptr(),
                    buf.len() as u32,
                ),
                0
            );
            assert_eq!(
                wa_battle_trace(
                    p.as_ptr(),
                    p.len() as u32,
                    e.as_ptr(),
                    e.len() as u32,
                    4,
                    0.033,
                    1.0,
                    0.0,
                    0.0,
                    0,
                    1,
                    core::ptr::null_mut(),
                    buf.len() as u32,
                ),
                0
            );
            // An unknown vehicle id must be reported, not substituted with a
            // default vehicle: a silent substitution would produce a trace that
            // looks plausible and compares against the wrong tank.
            assert_eq!(
                wa_battle_trace(
                    b"not-a-tank".as_ptr(),
                    10,
                    e.as_ptr(),
                    e.len() as u32,
                    4,
                    0.033,
                    1.0,
                    0.0,
                    0.0,
                    0,
                    1,
                    buf.as_mut_ptr(),
                    buf.len() as u32,
                ),
                0
            );
        }
    }

    #[test]
    fn battle_trace_fills_a_full_stride_per_tick_and_stops_at_capacity() {
        let stride = BATTLE_TRACE_STRIDE as usize;
        // Room for exactly 4 ticks, but 6 requested: capacity must win, and the
        // return value must describe what was actually written so the host can tell.
        let mut buf = vec![0.0f32; stride * 4];

        let written = run_battle_trace(&mut buf, "t34-76", "panther-g", 6, 1.0, 0.0, 0.0, 0, 1);
        assert_eq!(written, stride * 4);

        // The player must have moved under full throttle, and the tail of the buffer
        // must be untouched.
        use battle_trace_field::*;
        assert!(
            buf[PLAYER_Z] > -35.0,
            "full throttle did not move the player"
        );
        assert!(buf[PLAYER_HEALTH] > 0.0 && buf[ENEMY_HEALTH] > 0.0);
    }

    #[test]
    fn battle_trace_is_deterministic_for_the_state_independent_aim_mode() {
        let stride = BATTLE_TRACE_STRIDE as usize;
        let mut a = vec![0.0f32; stride * 60];
        let mut b = vec![0.0f32; stride * 60];
        assert_eq!(
            run_battle_trace(&mut a, "t34-76", "panther-g", 60, 0.5, 0.3, 0.0, 0, 0),
            stride * 60
        );
        assert_eq!(
            run_battle_trace(&mut b, "t34-76", "panther-g", 60, 0.5, 0.3, 0.0, 0, 0),
            stride * 60
        );
        // The C# parity test relies on this: identical inputs must produce an
        // identical trace, or the tick-by-tick comparison measures noise.
        assert_eq!(a, b);
    }

    /// Drive [`wa_battle_trace`] with length-prefixed ids, so a test can never
    /// accidentally pass a length that includes a NUL terminator and silently get
    /// back a "0 means unknown vehicle" rejection.
    ///
    /// Argument count mirrors the export it wraps, which is what keeps the two in
    /// step; collapsing them into a struct would hide exactly the correspondence the
    /// test is meant to check.
    #[allow(clippy::too_many_arguments)]
    fn run_battle_trace(
        buf: &mut [f32],
        player: &str,
        enemy: &str,
        ticks: usize,
        throttle: f32,
        steer: f32,
        turret_trim: f32,
        fire: u32,
        aim_mode: u32,
    ) -> usize {
        unsafe {
            wa_battle_trace(
                player.as_ptr(),
                player.len() as u32,
                enemy.as_ptr(),
                enemy.len() as u32,
                ticks as u32,
                1.0 / 30.0,
                throttle,
                steer,
                turret_trim,
                fire,
                aim_mode,
                buf.as_mut_ptr(),
                buf.len() as u32,
            ) as usize
        }
    }

    #[test]
    fn full_lifecycle_through_the_c_abi() {
        let handle = wa_spike_create();
        assert!(!handle.is_null());

        let mut buf = [0.0f32; STATE_SLOTS];
        let mut bytes = [0u8; 256];
        let aim = [10.0f32, 0.0, 40.0];

        unsafe {
            assert!(!wa_spike_echo(handle, 0.5).is_nan());
            assert_eq!(
                wa_spike_advance(handle, 1.0 / 30.0, 1.0, 0.0, 0.0, 1, aim[0], aim[1], aim[2]),
                1
            );

            let written = wa_spike_read(handle, buf.as_mut_ptr(), buf.len() as u32);
            assert_eq!(written as usize, STATE_SLOTS);

            let feed_written = wa_spike_feed(handle, bytes.as_mut_ptr(), bytes.len() as u32);
            assert!(feed_written > 0, "firing must have produced feed messages");

            wa_spike_destroy(handle);
        }
    }

    #[test]
    fn advance_takes_the_aim_point_as_three_scalars() {
        // The aim point is deliberately not a pointer or an array: see the
        // signature's docs. Verify all three components actually reach the sim,
        // since a transposed pair would be invisible in the resulting position.
        let handle = wa_spike_create();
        let mut buf = [0.0f32; STATE_SLOTS];
        let mut feed = [0u8; 512];
        // SAFETY: live handle, correctly sized buffers.
        let feed_written = unsafe {
            wa_spike_advance(handle, 1.0 / 30.0, 0.0, 0.0, 0.0, 1, 30.0, 0.0, 5.0);
            wa_spike_read(handle, buf.as_mut_ptr(), buf.len() as u32);
            wa_spike_feed(handle, feed.as_mut_ptr(), feed.len() as u32)
        } as usize;
        // Firing reports the range to the aim point. The player spawns at
        // (0, 0, -35), so an aim point of (30, 0, 5) is a 3-4-5 triangle away:
        // dx = 30, dz = 40, range = 50. That exercises all three components and
        // their signs -- a transposed or swapped pair would give a different
        // number and fail here.
        let text = std::str::from_utf8(&feed[..feed_written]).unwrap();
        assert!(
            text.contains("50m"),
            "aim (30, 0, 5) is 50m from the spawn point (0, 0, -35); got {text:?}"
        );

        // SAFETY: handle created above, destroyed exactly once.
        unsafe { wa_spike_destroy(handle) };
    }

    #[test]
    fn id_bytes_round_trip_through_the_boundary() {
        let handle = wa_spike_create();
        let player = b"pziv-h";
        let enemy = b"t34-76";
        let mut out = [0u8; 64];

        // SAFETY: all buffers match their stated lengths.
        let n = unsafe {
            wa_spike_echo_ids(
                handle,
                player.as_ptr(),
                player.len() as u32,
                enemy.as_ptr(),
                enemy.len() as u32,
                out.as_mut_ptr(),
                out.len() as u32,
            )
        };
        assert_eq!(&out[..n as usize], b"pziv-h|t34-76");

        // SAFETY: handle created above, destroyed exactly once.
        unsafe { wa_spike_destroy(handle) };
    }

    #[test]
    fn state_blob_is_written_fresh_each_read() {
        let handle = wa_spike_create();
        let mut buf = [0.0f32; STATE_SLOTS];
        // SAFETY: live handle and a correctly sized buffer.
        unsafe {
            wa_spike_advance(handle, 1.0 / 30.0, 1.0, 0.0, 0.0, 0, 0.0, 0.0, 0.0);
            wa_spike_read(handle, buf.as_mut_ptr(), buf.len() as u32);
            let first = buf[layout::WRITE_SENTINEL];
            wa_spike_read(handle, buf.as_mut_ptr(), buf.len() as u32);
            let second = buf[layout::WRITE_SENTINEL];
            assert_eq!(second - first, 1.0, "sentinel must advance per read");
        }
        // SAFETY: handle created above, destroyed exactly once.
        unsafe { wa_spike_destroy(handle) };
    }
}
