// WebGL glue between Unity's WebGL player and the Rust simulation core.
//
// ============================================================================
// WHY THIS FILE EXISTS
// ============================================================================
// The obvious approach -- drop the Rust .wasm into Assets/Plugins/WebGL and let
// Unity's plugin linker merge it into the player -- is not available:
//
//   * Unity's WebGL plugin importer accepts Emscripten-produced archives
//     (.a / .bc) built with its own bundled toolchain, and modules that can be
//     dynamically linked as side modules.
//   * A Rust `cdylib` is neither. It *defines* its own linear memory, and a side
//     module must instead import the host's.
//     Verified by crates/tools/wasm-interop-probe.mjs, which asserts this.
//
// A `.jslib` file is plain JavaScript and is unambiguously supported, so the
// module is instantiated here and reached through the normal `__Internal`
// P/Invoke mechanism.
//
// ============================================================================
// MEASURED PROPERTIES THIS FILE DEPENDS ON
// ============================================================================
// Both asserted by crates/tools/wasm-interop-probe.mjs, so a toolchain change
// that breaks the integration fails on the build machine, not in a browser:
//
//   1. The module has ZERO imports, so `{}` is a complete import object and no
//      WASI stubs are needed.
//   2. It exports `memory`, so there is NO shared heap with Unity's player.
//      Every buffer must be allocated *inside* the Rust module's own linear
//      memory and copied out by the caller.
//
// ============================================================================
// HOW STATE IS HELD, AND WHY IT IS SHAPED THIS WAY
// ============================================================================
// emcc evaluates a `.jslib` at BUILD time in `--symbols-only` mode purely to
// collect symbol names. Two consequences, both learned the hard way:
//
//   * Code outside `mergeInto(...)` runs during that build-time pass, NOT in the
//     browser. A top-level IIFE here fails the build with
//     "ReferenceError: Module is not defined". All runtime work therefore has to
//     be triggered from an exported function that C# calls.
//   * A `$`-prefixed key inside the library object is NOT emitted as a
//     module-scope helper. `mergeInto(LibraryManager.library, { $WarCore: {...} })`
//     silently drops the object, and reading `Module.$WarCore` at runtime throws
//     "Cannot read properties of undefined".
//
// So the state object is created lazily, on a plain property of `Module`, from
// inside `war_core_begin_load` -- the first function C# always calls. Every other
// export treats its absence as "not ready" and returns a harmless value rather
// than throwing, so a bad call order degrades instead of trapping the player.
//
// ============================================================================
// LOADING MODEL
// ============================================================================
// Fetching is asynchronous and cannot be awaited from IL2CPP, so the load is
// kicked off once and C# polls `war_core_ready()` until it reports 1. Because
// Rust owns the simulation handle, C# never handles pointers or lifetimes: it
// asks for the state blob, gets a pointer to a stable region of the module's own
// memory, and copies out with Marshal.Copy.

mergeInto(LibraryManager.library, {

  // ---------------------------------------------------------------------------
  // Creates (or returns) the state object hung off `Module`.
  //
  // Written as an inline expression rather than a `$`-named helper because
  // helpers are dropped, as described above.
  // ---------------------------------------------------------------------------
  war_core_begin_load: function () {
    var M = Module;
    if (M.__warCore) return 1; // already initialised; nothing to do

    var S = {
      instance: null,
      memory: null,
      view: null, // Float32Array over memory.buffer, refreshed after heap growth
      handle: 0, // simulation handle, owned entirely by Rust
      ready: false,
      failed: false,

      // Scratch regions carved out of the top of the module's own linear memory.
      // Placed at the end so the growing Rust heap cannot collide with them, and
      // re-reserved if the heap ever grows enough to reach them.
      reserveFrom: 0,
      statePtr: 0,
      feedPtr: 0,
      feedBytes: 4096,

      stateSlots: 0,
      feedLength: 0,

      // Where to try loading the module, in order. Unity has placed WebGL
      // StreamingAssets both inside `Build/` and in a sibling `StreamingAssets/`
      // directory across versions, and the latter is what this project's build
      // actually produces. Rather than guess from documentation, probe the
      // plausible locations and use the first that responds.
      candidatePaths: [
        'StreamingAssets/war_core/war_core.wasm',
        'Build/war_core/war_core.wasm',
        'StreamingAssets/war_core.wasm',
        'war_core.wasm',
      ],
    };

    // WebAssembly memory growth DETACHES the previous ArrayBuffer, so a cached
    // view silently reads zeros afterwards. This is the classic "the state blob
    // is all zeroes after a few seconds" bug.
    S.refreshView = function () {
      var buffer = S.memory.buffer;
      if (!S.view || S.view.buffer !== buffer) {
        S.view = new Float32Array(buffer);
      }
      return S.view;
    };

    // Claim the last 64 KiB of the module's memory for our buffers.
    S.reserve = function () {
      var reserveSize = 64 * 1024;
      var base = S.memory.buffer.byteLength - reserveSize;
      base -= base % 16; // 16-byte align
      S.reserveFrom = base;
      S.statePtr = base;
      S.feedPtr = base + 16384; // state blob first (2 KiB used), then slack
      return S;
    };

    // If the Rust heap has grown past our reservation, move to the new top.
    S.ensureReservation = function () {
      if (S.statePtr === 0 || S.statePtr + 24576 > S.memory.buffer.byteLength) {
        S.reserve();
      }
      return S;
    };

    // Probe the candidate URLs in order, resolving the first that responds.
    S.fetchFirst = function () {
      var chain = Promise.reject(new Error('no candidate path responded'));
      S.candidatePaths.forEach(function (path) {
        chain = chain.catch(function () {
          return fetch(path).then(function (response) {
            if (!response.ok) throw new Error(path + ' -> HTTP ' + response.status);
            console.log('[WarCore] loading from ' + path);
            return response;
          });
        });
      });
      return chain;
    };

    S.instantiate = function () {
      if (S.instance) return Promise.resolve(S.instance);
      if (S.failed) return Promise.reject(new Error('load previously failed'));

      return S.fetchFirst().then(function (response) {
        // instantiateStreaming requires Content-Type: application/wasm. If the
        // host misconfigures it the failure looks like a graphics bug, so fall
        // back to the ArrayBuffer path, which has no MIME requirement.
        if (WebAssembly.instantiateStreaming) {
          return WebAssembly.instantiateStreaming(Promise.resolve(response), {})
            .catch(function () {
              console.warn('[WarCore] streaming instantiation failed; falling back ' +
                           'to ArrayBuffer. Check that the host serves .wasm as ' +
                           'application/wasm.');
              return response.arrayBuffer().then(function (bytes) {
                return WebAssembly.instantiate(bytes, {});
              });
            });
        }
        return response.arrayBuffer().then(function (bytes) {
          return WebAssembly.instantiate(bytes, {});
        });
      }).then(function (result) {
        S.instance = result.instance;
        S.memory = S.instance.exports.memory;
        S.reserve();
        S.refreshView();
        S.stateSlots = S.instance.exports.wa_spike_state_slots();
        S.handle = S.instance.exports.wa_spike_create();
        if (S.handle === 0) throw new Error('wa_spike_create returned NULL');
        S.ready = true;
        console.log('[WarCore] ready: abi=' +
                    S.instance.exports.wa_spike_abi_version() +
                    ' slots=' + S.stateSlots);
        return S.instance;
      }).catch(function (err) {
        S.failed = true;
        console.error('[WarCore] instantiation FAILED; the Rust simulation will ' +
                      'not run in this browser. Tried: ' +
                      S.candidatePaths.join(', '), err);
        throw err;
      });
    };

    M.__warCore = S;

    // Kick the load off now. Failures are logged with their real cause above;
    // swallowing here keeps an unhandled rejection from burying that message.
    S.instantiate().catch(function () {});

    return 1;
  },

  // ---------------------------------------------------------------------------
  // Introspection -- reached from C# via [DllImport("__Internal")]
  // ---------------------------------------------------------------------------

  // 0 while loading, 1 once ready, 2 on failure (bitwise-or'd). C# polls this.
  war_core_ready: function () {
    var S = Module.__warCore;
    if (!S) return 0;
    return (S.ready ? 1 : 0) | (S.failed ? 2 : 0);
  },

  // -1 until ready, so the "still loading" guard in C# is distinguishable from a
  // real value. C# asserts this against the version it was built with, which is
  // what catches a stale .wasm next to fresh C#.
  war_core_abi_version: function () {
    var S = Module.__warCore;
    if (!S || !S.ready) return -1;
    return S.instance.exports.wa_spike_abi_version();
  },

  war_core_state_slots: function () {
    var S = Module.__warCore;
    if (!S || !S.ready) return -1;
    return S.stateSlots;
  },

  // How many slots the module actually uses, fewer than the allocated count.
  // C# asserts its own highest slot index is below this, which is the check that
  // catches the two sides disagreeing about the layout.
  war_core_state_layout_slots: function () {
    var S = Module.__warCore;
    if (!S || !S.ready) return -1;
    return S.instance.exports.wa_spike_state_layout_slots();
  },

  // Restore the simulation to its just-constructed state. The handle is
  // session-long, so this is how a match restart gets a known starting point.
  war_core_reset: function () {
    var S = Module.__warCore;
    if (!S || !S.ready) return 0;
    return S.instance.exports.wa_spike_reset(S.handle);
  },

  // ---------------------------------------------------------------------------
  // Simulation
  // ---------------------------------------------------------------------------

  // Advance one frame. Scalars arrive as doubles and are narrowed by Rust's own
  // signature. The aim point is three separate scalars for the same reason it is
  // three scalars in the C ABI: no array marshalling for IL2CPP to get wrong.
  // Returns 1 on success, 0 if the module is not ready.
  war_core_advance: function (dt, throttle, steer, turretTrim, fire,
                              aimX, aimY, aimZ) {
    var S = Module.__warCore;
    if (!S || !S.ready) return 0;
    return S.instance.exports.wa_spike_advance(
      S.handle, dt, throttle, steer, turretTrim, fire, aimX, aimY, aimZ);
  },

  // Serialise the state blob into the module's own memory and return a pointer to
  // it. The pointer stays valid only until the next call, so C# must copy before
  // calling anything else. Returns 0 if not ready.
  war_core_state_ptr: function () {
    var S = Module.__warCore;
    if (!S || !S.ready) return 0;
    S.ensureReservation();
    S.instance.exports.wa_spike_read(S.handle, S.statePtr, S.stateSlots);
    return S.statePtr;
  },

  // Refresh the feed blob and return its length in bytes; the bytes are at
  // war_core_feed_ptr(). Returns 0 if not ready.
  war_core_feed_length: function () {
    var S = Module.__warCore;
    if (!S || !S.ready) return 0;
    S.ensureReservation();
    S.feedLength = S.instance.exports.wa_spike_feed(S.handle, S.feedPtr, S.feedBytes);
    return S.feedLength;
  },

  war_core_feed_ptr: function () {
    var S = Module.__warCore;
    return S ? S.feedPtr : 0;
  },

  // Read one scalar straight out of the state blob, without C# copying the whole
  // thing. Used for HUD values so the hot path avoids a 2 KiB copy.
  // Returns NaN if not ready or the slot is out of range.
  war_core_read_slot: function (slot) {
    var S = Module.__warCore;
    if (!S || !S.ready) return NaN;
    if (slot < 0 || slot >= S.stateSlots) return NaN;
    S.instance.exports.wa_spike_read(S.handle, S.statePtr, S.stateSlots);
    var v = S.refreshView();
    return v[(S.statePtr >> 2) + slot];
  },

  // Scalar probe, proving the handle round-trips across the boundary.
  war_core_echo: function (x) {
    var S = Module.__warCore;
    if (!S || !S.ready) return NaN;
    return S.instance.exports.wa_spike_echo(S.handle, x);
  },
});