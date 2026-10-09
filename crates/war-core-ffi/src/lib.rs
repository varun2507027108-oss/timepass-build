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
pub unsafe extern "C" fn wa_spike_read(
    handle: *mut SpikeSim,
    out: *mut f32,
    cap: u32,
) -> u32 {
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

    let mut model = war_core::air::FlightModel::new(spec, Vec3::new(0.0, altitude, 0.0), heading_deg);

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
        trace[base + 0] = p.x;
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
        assert!(layout::USED <= STATE_SLOTS);
    }

    #[test]
    fn null_handles_are_tolerated_everywhere() {
        let mut buf = [0.0f32; STATE_SLOTS];
        let mut bytes = [0u8; 64];
        // None of these may trap: a bad handle must not kill the host player.
        unsafe {
            assert!(wa_spike_echo(core::ptr::null_mut(), 1.0).is_nan());
            assert_eq!(
                wa_spike_advance(core::ptr::null_mut(), 0.016, 0.0, 0.0, 0.0, 0, 0.0, 0.0, 0.0),
                0
            );
            assert_eq!(
                wa_spike_read(core::ptr::null_mut(), buf.as_mut_ptr(), buf.len() as u32),
                0
            );
            assert_eq!(
                wa_spike_feed(core::ptr::null_mut(), bytes.as_mut_ptr(), bytes.len() as u32),
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