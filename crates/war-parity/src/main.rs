//! `war-parity` — drive the simulation through a scripted input sequence and
//! dump a state trace.
//!
//! This exists because compiling is not evidence. The only way to show the Rust
//! port is behaviourally equivalent to the C# original is to run both over the
//! same inputs and diff the outputs, tick by tick.
//!
//! Phase 0 exercises the ABI from a *non-Unity* consumer, which additionally
//! proves the C ABI is usable from a plain native host — the same way Unity will
//! use it on Windows.
//!
//! ```text
//! cargo run -p war-parity -- --ticks 200 --probe
//! ```

use std::process::ExitCode;

use war_core::spike::layout;
use war_core_ffi::{
    wa_spike_abi_version, wa_spike_advance, wa_spike_create, wa_spike_destroy, wa_spike_echo,
    wa_spike_echo_ids, wa_spike_feed, wa_spike_read, wa_spike_state_slots,
};

/// The last `f32` slots in the blob are unused padding; the spike's meaningful
/// fields end well before this. Kept explicit so the diff columns are stable as
/// the real schema lands.
const TRACKED_SLOTS: usize = 16;

fn main() -> ExitCode {
    let ticks: u32 = arg_value("--ticks").unwrap_or(200.0) as u32;
    let verbose = std::env::args().any(|a| a == "--probe");

    // --- ABI handshake, mirroring what WarSimRuntime.cs will assert ---------
    let abi = wa_spike_abi_version();
    let slots = wa_spike_state_slots() as usize;
    println!("abi_version={abi} state_slots={slots} ticks={ticks}");

    if slots != war_core::spike::STATE_SLOTS {
        eprintln!(
            "FAIL: host and module disagree on state slot count ({slots} vs {})",
            war_core::spike::STATE_SLOTS
        );
        return ExitCode::FAILURE;
    }

    // --- Scalar probe ------------------------------------------------------
    let handle = wa_spike_create();
    assert!(!handle.is_null(), "create returned NULL");

    // SAFETY: `handle` is a live handle; every buffer below matches its stated
    // length. All calls are single-threaded on this thread.
    unsafe {
        if verbose {
for x in [0.0f32, 1.0, -3.5, 1.0e-4, 12.25] {
                    let v = wa_spike_echo(handle, x);
                    println!("echo({x:>8}) = {v:.9}  (bits 0x{:08x})", v.to_bits());
                }
            let player = b"pziv-h";
            let enemy = b"t34-76";
            let mut ids = [0u8; 64];
            let n = wa_spike_echo_ids(
                handle,
                player.as_ptr(),
                player.len() as u32,
                enemy.as_ptr(),
                enemy.len() as u32,
                ids.as_mut_ptr(),
                ids.len() as u32,
            );
            println!(
                "echo_ids = {:?}",
                std::str::from_utf8(&ids[..n as usize]).expect("ids must be utf8")
            );
        }

        // --- Scripted run, emitting one line per tick ----------------------
        let mut buf = vec![0.0f32; slots];
        let mut feed_buf = vec![0u8; 4096];

        // Same fixed timestep BattleSim uses (30 Hz) so a future C# trace can
        // be compared tick for tick.
        const DT: f32 = 1.0 / 30.0;
        let aim = [12.0f32, 0.0, 40.0];

        println!("# tick player_x player_y player_z heading_rad speed_kph health");
        for tick in 0..ticks {
            // Deterministic, reproducible input script: full throttle, a
            // periodic steering weave, periodic fire. No RNG anywhere, so this
            // trace is byte-stable across runs and machines.
            let phase = tick as f32 * 0.05;
            let throttle = 1.0;
            let steer = phase.sin();
            let fire = if tick % 37 == 0 { 1u32 } else { 0u32 };

            wa_spike_advance(handle, DT, throttle, steer, 0.0, fire, aim[0], aim[1], aim[2]);
            let written = wa_spike_read(handle, buf.as_mut_ptr(), slots as u32);
            if written as usize != slots {
                eprintln!("FAIL: read wrote {written} slots, expected {slots}");
                return ExitCode::FAILURE;
            }

            // Column 0 is the magic and column 1 the ABI version; verify both on
            // every single tick rather than once, so a buffer that is silently
            // not being written is caught immediately.
            if buf[layout::MAGIC] != war_core::spike::STATE_MAGIC {
                eprintln!("FAIL: tick {tick} state magic mismatch ({})", buf[0]);
                return ExitCode::FAILURE;
            }
            if buf[layout::ABI_VERSION] as u32 != abi {
                eprintln!(
                    "FAIL: tick {tick} state abi mismatch ({} vs {abi})",
                    buf[1] as u32
                );
                return ExitCode::FAILURE;
            }

            println!(
                "{tick} {} {} {} {:.6} {:.4} {}",
                buf[layout::PLAYER_X],
                buf[layout::PLAYER_Y],
                buf[layout::PLAYER_Z],
                buf[layout::PLAYER_HEADING_RAD],
                buf[layout::PLAYER_SPEED_KPH],
                buf[layout::PLAYER_HEALTH] as i32
            );

            // Drain the feed so it cannot grow without bound, and prove the
            // blob is decodable exactly.
            let feed_len = wa_spike_feed(handle, feed_buf.as_mut_ptr(), feed_buf.len() as u32);
            decode_feed(&feed_buf[..feed_len as usize]);
        }

        let feed_len = wa_spike_feed(handle, feed_buf.as_mut_ptr(), feed_buf.len() as u32);
        let messages = decode_feed(&feed_buf[..feed_len as usize]);
        println!("# feed ({} messages)", messages.len());
        for m in messages {
            println!("#   {m}");
        }

        wa_spike_destroy(handle);
    }

    println!("# OK {} ticks, {} tracked slots", ticks, TRACKED_SLOTS);
    ExitCode::SUCCESS
}

/// Split the length-prefixed UTF-8 blob, panicking if the framing is corrupt.
/// A truncated blob is a real bug in the FFI, not something to tolerate.
fn decode_feed(bytes: &[u8]) -> Vec<String> {
    let mut out = Vec::new();
    let mut cursor = 0usize;
    while cursor < bytes.len() {
        assert!(cursor + 4 <= bytes.len(), "feed blob truncated at {cursor}");
        let len = u32::from_le_bytes(bytes[cursor..cursor + 4].try_into().unwrap()) as usize;
        cursor += 4;
        assert!(
            cursor + len <= bytes.len(),
            "feed blob length {len} overruns {} bytes",
            bytes.len() - cursor
        );
        let text = std::str::from_utf8(&bytes[cursor..cursor + len])
            .expect("feed blob is not valid utf8");
        out.push(text.to_string());
        cursor += len;
    }
    assert_eq!(cursor, bytes.len(), "feed blob not consumed exactly");
    out
}

/// Minimal `--flag value` / `--flag=value` reader; avoids a dependency for one
/// integer.
fn arg_value(flag: &str) -> Option<f32> {
    let args: Vec<String> = std::env::args().collect();
    for (i, a) in args.iter().enumerate() {
        if a == flag {
            return args.get(i + 1).and_then(|v| v.parse().ok());
        }
        if let Some(v) = a.strip_prefix(&format!("{flag}=")) {
            return v.parse().ok();
        }
    }
    None
}