//! Phase 0 interop spike.
//!
//! This is a throwaway *harness*, not a game system. Its only job is to prove
//! that every data-movement pattern the real port depends on survives the trip
//! between Rust and a Unity player:
//!
//! | Pattern | Exercised by |
//! |---|---|
//! | scalar in, scalar out | [`SpikeSim::echo`] via `wa_spike_echo_scalar` |
//! | opaque handle, created and destroyed by the host | `wa_spike_create` / `wa_spike_destroy` |
//! | pointer argument *into* Rust (`&[f32]` aim point) | `wa_spike_advance` |
//! | 512-float out-blob with a versioned header | [`write_state`] via `wa_spike_read` |
//! | UTF-8 byte blob the host decodes | [`SpikeSim::feed`] via `wa_spike_feed` |
//!
//! It deliberately runs a small fixed-step integrator so the state blob has
//! realistic, continuously-changing values to round-trip rather than a constant.

use crate::math::{mathf, Vec3};

/// Number of `f32` slots in the state blob the host provides.
///
/// The real blob will be larger; 512 is chosen so the spike can also prove that
/// a generous, fixed, caller-allocated buffer works, and so the host never has
/// to grow memory at runtime.
pub const STATE_SLOTS: usize = 512;

/// First slot: identifies the buffer as ours so a schema mismatch is loud
/// rather than a silent misread of floats.
pub const STATE_MAGIC: f32 = 1.701_411_7e-5;

/// Second slot: schema version, bumped whenever the slot layout below changes.
pub const STATE_ABI_VERSION: u32 = 1;

/// `wa_spike_abi_version` â€” lets the host assert it linked the module it thinks
/// it did, which is the failure mode that otherwise shows up as garbage floats.
pub const SPIKE_ABI_VERSION: u32 = STATE_ABI_VERSION;

/// Slot offsets into the state blob, in `f32` units.
/// Slot offsets into the state blob, in `f32` units.
///
/// This is the ABI contract between Rust and the Unity host. It is deliberately
/// public and named, because the alternative -- bare integers scattered across
/// three languages (Rust, C#, JavaScript) -- silently corrupts everything when a
/// field is inserted. `Assets/Scripts/Interop/WarSimRuntime.cs` mirrors these
/// names, and `crates/tools/wasm-interop-probe.mjs` asserts the layout too.
pub mod layout {
    /// Identifies the buffer, so a schema mismatch is loud rather than a silent
    /// misread of floats.
    pub const MAGIC: usize = 0;
    /// Schema version, bumped whenever any slot below moves.
    pub const ABI_VERSION: usize = 1;

    pub const PLAYER_X: usize = 2;
    pub const PLAYER_Y: usize = 3;
    pub const PLAYER_Z: usize = 4;
    pub const ENEMY_X: usize = 5;
    pub const ENEMY_Y: usize = 6;
    pub const ENEMY_Z: usize = 7;
    pub const PLAYER_HEADING_RAD: usize = 8;
    pub const PLAYER_SPEED_KPH: usize = 9;
    pub const PLAYER_HEALTH: usize = 10;
    pub const ENEMY_HEALTH: usize = 11;
    pub const KILLS: usize = 12;
    pub const DEATHS: usize = 13;
    pub const TICKS: usize = 14;

    /// Incremented on every write, so the host can prove the blob is freshly
    /// written each frame rather than left stale.
    pub const WRITE_SENTINEL: usize = 15;

    /// Number of slots actually in use. The blob is still allocated at
    /// [`super::STATE_SLOTS`] so there is room to grow without a host change.
    pub const USED: usize = 16;

    #[cfg(test)]
    mod tests {
        use super::*;

        /// No two fields may share a slot, and nothing may sit past USED. A
        /// duplicate index would make one field silently overwrite another.
        #[test]
        fn every_slot_is_unique_and_in_range() {
            let all = [
                MAGIC, ABI_VERSION, PLAYER_X, PLAYER_Y, PLAYER_Z, ENEMY_X, ENEMY_Y,
                ENEMY_Z, PLAYER_HEADING_RAD, PLAYER_SPEED_KPH, PLAYER_HEALTH,
                ENEMY_HEALTH, KILLS, DEATHS, TICKS, WRITE_SENTINEL,
            ];
            assert_eq!(all.len(), USED, "USED must match the field count");
            let mut sorted = all;
            sorted.sort_unstable();
            for (i, slot) in sorted.iter().enumerate() {
                assert_eq!(*slot, i, "slots must be contiguous and unique");
            }
            assert!(*sorted.last().unwrap() < super::super::STATE_SLOTS);
        }
    }
}

/// Incremented every time [`SpikeSim::write_state`] is called. Written into the
/// blob so the host can verify it observes a fresh snapshot each frame.
pub const WRITE_SENTINEL_BASE: f32 = 1_000_000.0;

/// State owned by the host across frames. The real port replaces this with
/// `BattleSim` / `AirCombatSim`.
pub struct SpikeSim {
    pub player_position: Vec3,
    pub player_heading_rad: f32,
    pub player_speed_mps: f32,
    pub player_health: i32,
    pub enemy_position: Vec3,
    pub enemy_health: i32,
    pub kills: i32,
    pub deaths: i32,
    pub ticks: u32,
    /// Newest-first, capped â€” mirrors `BattleSim.Feed`'s `Insert(0, ..)` +
    /// `RemoveAt(Count - 1)` behaviour.
    feed: Vec<String>,
    writes: u32,
}

impl Default for SpikeSim {
    fn default() -> Self {
        Self::new()
    }
}

impl SpikeSim {
    pub fn new() -> Self {
        SpikeSim {
            player_position: Vec3::new(0.0, 0.0, -35.0),
            player_heading_rad: 0.0,
            player_speed_mps: 0.0,
            player_health: 100,
            enemy_position: Vec3::new(0.0, 0.0, 35.0),
            enemy_health: 100,
            kills: 0,
            deaths: 0,
            ticks: 0,
            feed: Vec::new(),
            writes: 0,
        }
    }

    /// Scalar round-trip, with a deliberately lossy operation so a bit-exact
    /// match at the far side actually means something.
    pub fn echo(&mut self, x: f32) -> f32 {
        mathf::atan2(x, 1.75) * mathf::RAD2DEG
    }

    /// One fixed-step tick. Mirrors `BattleSim.Advance`'s shape: a clamped
    /// accumulator, a bounded number of steps, and a pointer-provided aim point.
    pub fn advance(&mut self, dt: f32, throttle: f32, steer: f32, aim: Vec3, fire: bool) {
        // Deliberately reduced: 10 m/s top speed rather than 12, to keep the
        // arena bounded inside the few hundred ticks the spike runs.
        const MAX_SPEED_MPS: f32 = 10.0;
        const ACCELERATION: f32 = 12.0;
        const MAX_TURN_RAD: f32 = 1.8;
        const ARENA_HALF_EXTENT: f32 = 90.0;

        self.ticks += 1;

        let target_speed = mathf::clamp(throttle, -1.0, 1.0) * MAX_SPEED_MPS;
        self.player_speed_mps = mathf::move_towards(
            self.player_speed_mps,
            target_speed,
            ACCELERATION * dt,
        );

        let demand = mathf::clamp(steer, -1.0, 1.0);
        // MAX_TURN_RAD is a rate in rad/s, so the per-step delta needs the dt factor;
        // without it the clamp saturates on any input and this is not a rate at all.
        let max_step = MAX_TURN_RAD * dt;
        self.player_heading_rad += mathf::clamp(demand * MAX_TURN_RAD * dt, -max_step, max_step);
        if self.player_heading_rad > mathf::PI {
            self.player_heading_rad -= 2.0 * mathf::PI;
        } else if self.player_heading_rad < -mathf::PI {
            self.player_heading_rad += 2.0 * mathf::PI;
        }

        let forward = Vec3::new(
            mathf::sin(self.player_heading_rad),
            0.0,
            mathf::cos(self.player_heading_rad),
        );
        self.player_position += forward * (self.player_speed_mps * dt);
        self.player_position.x = mathf::clamp(
            self.player_position.x,
            -ARENA_HALF_EXTENT,
            ARENA_HALF_EXTENT,
        );
        self.player_position.z = mathf::clamp(
            self.player_position.z,
            -ARENA_HALF_EXTENT,
            ARENA_HALF_EXTENT,
        );

        if fire {
            self.player_health = mathf::max(0.0, self.player_health as f32 - 34.0) as i32;
            self.push(format!(
                "PENETRATION @ {:.0}m aim",
                Vec3::distance(self.player_position, aim)
            ));
        }
    }

    fn push(&mut self, message: String) {
        self.feed.insert(0, message);
        if self.feed.len() > 4 {
            self.feed.pop();
        }
    }

    /// Fill `out` with the full state snapshot. Returns the number of `f32`
    /// slots written.
    ///
    /// Callers should pass a buffer of at least [`STATE_SLOTS`], in which case
    /// every field is written. A shorter buffer is **truncated, not overrun**:
    /// writes whose slot lies past `min(cap, STATE_SLOTS)` are dropped, and the
    /// returned count tells the caller how many slots are meaningful.
    ///
    /// # Safety
    /// `out` must point to at least `cap` writable `f32`s.
    pub unsafe fn write_state(&mut self, out: *mut f32, cap: usize) -> u32 {
        self.writes += 1;
        let n = core::cmp::min(cap, STATE_SLOTS);
        // SAFETY: the caller guarantees `out` is valid for `cap` writes.
        unsafe {
            let s = &mut *std::slice::from_raw_parts_mut(out, n);
            // Bounds-check every write against the *actual* slice length. A
            // fixed-offset write into a short caller buffer is an out-of-bounds
            // heap write, which would corrupt the host player's memory rather
            // than fail loudly.
            let mut put = |i: usize, v: f32| {
                if i < n {
                    s[i] = v;
                }
            };
            put(layout::MAGIC, STATE_MAGIC);
            put(layout::ABI_VERSION, STATE_ABI_VERSION as f32);
            put(layout::PLAYER_X, self.player_position.x);
            put(layout::PLAYER_Y, self.player_position.y);
            put(layout::PLAYER_Z, self.player_position.z);
            put(layout::ENEMY_X, self.enemy_position.x);
            put(layout::ENEMY_Y, self.enemy_position.y);
            put(layout::ENEMY_Z, self.enemy_position.z);
            put(layout::PLAYER_HEADING_RAD, self.player_heading_rad);
            put(layout::PLAYER_SPEED_KPH, self.player_speed_mps * 3.6);
            put(layout::PLAYER_HEALTH, self.player_health as f32);
            put(layout::ENEMY_HEALTH, self.enemy_health as f32);
            put(layout::KILLS, self.kills as f32);
            put(layout::DEATHS, self.deaths as f32);
            put(layout::TICKS, self.ticks as f32);
            put(layout::WRITE_SENTINEL, WRITE_SENTINEL_BASE + self.writes as f32);
        }
        n as u32
    }

    /// Serialise the feed as a length-prefixed UTF-8 blob: repeated
    /// `u32 little-endian length` followed by that many bytes. The host splits on
    /// the prefixes and decodes each message, so Rust owns all string data and
    /// the host performs no allocation per frame.
    ///
    /// # Safety
    /// `out` must point to at least `cap` writable bytes.
    pub unsafe fn feed(&self, out: *mut u8, cap: usize) -> u32 {
        let mut written = 0usize;
        for message in &self.feed {
            let bytes = message.as_bytes();
            let need = 4 + bytes.len();
            if written + need > cap {
                break;
            }
            // SAFETY: bounds were just checked; `written + 4 <= cap` before the
            // length write and `written + need <= cap` for the payload.
            unsafe {
                let dst = std::slice::from_raw_parts_mut(out.add(written), need);
                dst[0..4].copy_from_slice(&(bytes.len() as u32).to_le_bytes());
                dst[4..need].copy_from_slice(bytes);
            }
            written += need;
        }
        written as u32
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn state_blob_has_a_magic_and_version_header() {
        let mut sim = SpikeSim::new();
        let mut buf = [0.0f32; STATE_SLOTS];
        let n = unsafe { sim.write_state(buf.as_mut_ptr(), STATE_SLOTS) };
        assert_eq!(n as usize, STATE_SLOTS);
        assert_eq!(buf[layout::MAGIC], STATE_MAGIC);
        assert_eq!(buf[layout::ABI_VERSION], STATE_ABI_VERSION as f32);
    }

    #[test]
    fn state_blob_write_count_advances_every_call() {
        let mut sim = SpikeSim::new();
        let mut buf = [0.0f32; STATE_SLOTS];
        let mut seen = Vec::new();
        for _ in 0..5 {
            unsafe { sim.write_state(buf.as_mut_ptr(), STATE_SLOTS) };
            seen.push(buf[layout::WRITE_SENTINEL]);
        }
        for w in seen.windows(2) {
            assert_eq!(w[1] - w[0], 1.0, "sentinel must increment by 1 per write");
        }
    }

    #[test]
    fn state_blob_respects_a_short_buffer() {
        let mut sim = SpikeSim::new();
        let mut buf = [0.0f32; 8];
        let n = unsafe { sim.write_state(buf.as_mut_ptr(), buf.len()) };
        assert_eq!(n as usize, 8, "must report the clamped slot count");
        // Slots past the short buffer must be untouched.
        assert_eq!(buf[layout::PLAYER_X], 0.0);
    }

    #[test]
    fn feed_blob_is_length_prefixed_utf8() {
        let mut sim = SpikeSim::new();
        sim.push("hÃ©llo".to_string());
        sim.push("second".to_string());

        let mut buf = vec![0u8; 256];
        let n = unsafe { sim.feed(buf.as_mut_ptr(), buf.len()) } as usize;
        assert!(n > 0);

        let mut cursor = 0usize;
        let mut decoded = Vec::new();
        while cursor + 4 <= n {
            let len = u32::from_le_bytes(buf[cursor..cursor + 4].try_into().unwrap()) as usize;
            cursor += 4;
            let text = std::str::from_utf8(&buf[cursor..cursor + len]).unwrap();
            decoded.push(text.to_string());
            cursor += len;
        }
        assert_eq!(cursor, n, "length prefixes must consume the blob exactly");
        // Newest first.
        assert_eq!(decoded, vec!["second".to_string(), "hÃ©llo".to_string()]);
    }

    /// A buffer too small for even one message must yield nothing rather than a
    /// partial record, and one that fits exactly one must yield exactly one.
    #[test]
    fn feed_blob_stops_cleanly_when_the_buffer_is_short() {
        let mut sim = SpikeSim::new();
        for i in 0..4 {
            sim.push(format!("message number {i}")); // 15 bytes each
        }
        let newest = "message number 3";
        let one_message = 4 + newest.len();

        // Too small for even the first message: nothing written, no panic.
        let mut tiny = vec![0u8; one_message - 1];
        let n = unsafe { sim.feed(tiny.as_mut_ptr(), tiny.len()) };
        assert_eq!(n, 0, "must not emit a truncated record");

        // Exactly one message: exactly one record, blob fully consumed.
        let mut exact = vec![0u8; one_message];
        let n = unsafe { sim.feed(exact.as_mut_ptr(), exact.len()) } as usize;
        assert_eq!(n, one_message);
        // The record is a `u32` LE length followed by the payload; decode it
        // rather than string-comparing the raw blob, which starts with the prefix.
        let prefix = u32::from_le_bytes(exact[..4].try_into().unwrap()) as usize;
        assert_eq!(prefix, newest.len(), "length prefix must match the payload");
        assert_eq!(&exact[4..n], newest.as_bytes(), "newest message first");

        // One byte too few for two messages: exactly one emitted.
        let mut almost = vec![0u8; one_message * 2 - 1];
        let n = unsafe { sim.feed(almost.as_mut_ptr(), almost.len()) } as usize;
        assert_eq!(n, one_message);
        assert!(std::str::from_utf8(&almost[..n]).is_ok());
    }

    #[test]
    fn feed_keeps_only_the_newest_four() {
        let mut sim = SpikeSim::new();
        for i in 0..10 {
            sim.push(format!("m{i}"));
        }
        let mut buf = vec![0u8; 1024];
        let n = unsafe { sim.feed(buf.as_mut_ptr(), buf.len()) } as usize;
        let text = std::str::from_utf8(&buf[..n]).unwrap();
        assert!(text.contains("m9"), "newest survives");
        assert!(!text.contains("m0"), "oldest is evicted");
    }

    #[test]
    fn advance_stays_inside_the_arena() {
        let mut sim = SpikeSim::new();
        for i in 0..600 {
            sim.advance(1.0 / 30.0, 1.0, if i % 7 == 0 { 1.0 } else { -1.0 }, Vec3::ZERO, false);
            assert!(sim.player_position.x.abs() <= 90.0 + 1e-3);
            assert!(sim.player_position.z.abs() <= 90.0 + 1e-3);
        }
        assert!(sim.player_position.x.is_finite());
        assert!(sim.player_position.z.is_finite());
    }

    #[test]
    fn echo_is_bit_reproducible() {
        let mut sim = SpikeSim::new();
        for x in [0.0f32, 1.0, -3.5, 1.0e-4, 12.25] {
            let a = sim.echo(x);
            let b = sim.echo(x);
            assert_eq!(a.to_bits(), b.to_bits(), "echo must be deterministic");
        }
    }
}