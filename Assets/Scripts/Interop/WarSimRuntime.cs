using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace WarArena.Interop
{
    /// <summary>
    /// Managed wrapper over the Rust simulation core (<c>war-core-ffi</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity owns presentation; Rust owns simulation state. This class is the
    /// only place the two meet. There is deliberately no managed simulation state
    /// here -- the handle, the state blob and the event feed all live inside the
    /// Rust module's own linear memory, so the two sides cannot disagree.
    /// </para>
    /// <para>
    /// <b>Why there are no <c>unsafe</c> blocks.</b> This project sets
    /// <c>allowUnsafeCode: 0</c> in ProjectSettings.asset, so <c>fixed</c> and
    /// <c>unsafe</c> are unavailable. State therefore crosses via
    /// <see cref="Marshal.Copy(IntPtr, float[], int, int)"/> from a pointer into
    /// the Rust module, not by pinning a managed array.
    /// </para>
    /// <para>
    /// <b>Platform strategy.</b> Two backends, selected at compile time. Windows
    /// players and the Editor use P/Invoke into <c>war_core_ffi.dll</c>. WebGL
    /// players route through <c>Assets/Plugins/WebGL/WarCore.jslib</c>, because
    /// Unity's WebGL plugin linker cannot consume a Rust <c>cdylib</c> -- see
    /// PORT-NOTES.md "WebGL plugin strategy".
    /// </para>
    /// </remarks>
    public static class WarSimRuntime
    {
        /// <summary>
        /// Schema version of the C ABI this C# was compiled against. Asserted
        /// against the loaded module at startup so a stale <c>.wasm</c> or
        /// <c>.dll</c> fails immediately with a clear message instead of
        /// producing plausible-but-wrong floats.
        /// </summary>
        public const uint ExpectedAbiVersion = 1u;

        /// <summary>
        /// Must match the module's <c>wa_spike_state_slots</c>. Kept as a constant
        /// only so the managed buffer can be allocated before the module has
        /// finished loading; the real value is read back and checked.
        /// </summary>
        public const int StateSlotCount = 512;

        // ---------------------------------------------------------------------
        // Platform imports
        // ---------------------------------------------------------------------

#if UNITY_WEBGL && !UNITY_EDITOR
        // Resolved by mergeInto(LibraryManager.library, ...) in WarCore.jslib.
        [DllImport("__Internal")] private static extern int war_core_ready();
        [DllImport("__Internal")] private static extern int war_core_begin_load();
        [DllImport("__Internal")] private static extern int war_core_abi_version();
        [DllImport("__Internal")] private static extern int war_core_state_slots();
        [DllImport("__Internal")] private static extern int war_core_advance(
            double dt, double throttle, double steer, double turretTrim, int fire,
            double aimX, double aimY, double aimZ);
        [DllImport("__Internal")] private static extern int war_core_state_ptr();
        [DllImport("__Internal")] private static extern int war_core_feed_length();
        [DllImport("__Internal")] private static extern int war_core_feed_ptr();
        [DllImport("__Internal")] private static extern double war_core_read_slot(int slot);
        [DllImport("__Internal")] private static extern double war_core_echo(double x);
        [DllImport("__Internal")] private static extern int war_core_state_layout_slots();
        [DllImport("__Internal")] private static extern int war_core_reset();
#else
        // Resolved from war_core_ffi.dll, produced by crates/tools/build-native.ps1
        // into Assets/Plugins/x86_64. The name must match the cdylib artefact
        // exactly -- cargo names it after the crate, and renaming it in the build
        // script would just create a second thing to keep in sync.
        [DllImport("war_core_ffi")] private static extern uint wa_spike_abi_version();
        [DllImport("war_core_ffi")] private static extern uint wa_spike_state_slots();
        [DllImport("war_core_ffi")] private static extern uint wa_spike_state_layout_slots();
        [DllImport("war_core_ffi")] private static extern int wa_spike_reset(IntPtr handle);
        [DllImport("war_core_ffi")] private static extern IntPtr wa_spike_create();
        [DllImport("war_core_ffi")] private static extern void wa_spike_destroy(IntPtr handle);
        [DllImport("war_core_ffi")] private static extern int wa_spike_advance(
            IntPtr handle, float dt, float throttle, float steer, float turretTrim,
            uint fire, float aimX, float aimY, float aimZ);
        [DllImport("war_core_ffi")] private static extern uint wa_spike_read(
            IntPtr handle, [Out] float[] dest, uint cap);
        [DllImport("war_core_ffi")] private static extern uint wa_spike_feed(
            IntPtr handle, [Out] byte[] dest, uint cap);
        [DllImport("war_core_ffi")] private static extern float wa_spike_echo(IntPtr handle, float x);
#endif

        // ---------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------

        /// <summary>State blob slots, mirroring the module's layout.</summary>
        private const int SlotMagic = 0;
        private const int SlotAbiVersion = 1;
        private const int SlotPlayerX = 2;
        private const int SlotPlayerY = 3;
        private const int SlotPlayerZ = 4;
        private const int SlotEnemyX = 5;
        private const int SlotEnemyY = 6;
        private const int SlotEnemyZ = 7;
        private const int SlotPlayerHeadingRad = 8;
        private const int SlotPlayerSpeedKph = 9;
        private const int SlotPlayerHealth = 10;
        private const int SlotEnemyHealth = 11;
        private const int SlotKills = 12;
        private const int SlotDeaths = 13;
        private const int SlotTicks = 14;

        /// <summary>
        /// Sentinel the module writes into slot 15, incremented once per read. The
        /// host compares it across frames to prove the blob is genuinely being
        /// rewritten rather than left stale.
        /// </summary>
        private const int SlotWriteSentinel = 15;

        private const float ExpectedMagic = 1.7014117e-5f;

        private static readonly float[] StateBuffer = new float[StateSlotCount];
        private static readonly byte[] FeedBuffer = new byte[8192];

        private static bool _initialised;
        private static bool _reportedFailure;
        private static float _lastSentinel;
        private static bool _sentinelAdvanced;
        private static List<string> _feed = new List<string>(4);

        /// <summary>True once the Rust module is loaded and its ABI matches.</summary>
        public static bool IsReady { get; private set; }

        /// <summary>Human-readable status for the HUD and for diagnosing builds.</summary>
        public static string Status { get; private set; } = "not started";

        /// <summary>Newest-first event feed, mirroring <c>BattleSim.Feed</c>.</summary>
        public static IReadOnlyList<string> Feed => _feed;

        // Player transform, for the ground camera and TankView.Place.
        public static Vector3 PlayerPosition => new Vector3(
            StateBuffer[SlotPlayerX], StateBuffer[SlotPlayerY], StateBuffer[SlotPlayerZ]);
        public static Vector3 EnemyPosition => new Vector3(
            StateBuffer[SlotEnemyX], StateBuffer[SlotEnemyY], StateBuffer[SlotEnemyZ]);
        public static float PlayerHeadingRad => StateBuffer[SlotPlayerHeadingRad];
        public static float PlayerSpeedKph => StateBuffer[SlotPlayerSpeedKph];
        public static int PlayerHealth => (int)StateBuffer[SlotPlayerHealth];
        public static int EnemyHealth => (int)StateBuffer[SlotEnemyHealth];
        public static int Kills => (int)StateBuffer[SlotKills];
        public static int Deaths => (int)StateBuffer[SlotDeaths];

        // ---------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------

        /// <summary>
        /// Start loading the module if needed. Call once at startup. Safe to call
        /// every frame.
        /// </summary>
        public static void BeginLoad()
        {
            if (_initialised)
            {
                return;
            }
            _initialised = true;

#if UNITY_WEBGL && !UNITY_EDITOR
            // Fire-and-forget: the module resolves asynchronously and
            // TryFinalize reports when it lands.
            war_core_begin_load();
#else
            TryFinalize();
#endif
        }

        /// <summary>
        /// Call once per frame until <see cref="IsReady"/>. Cheap after the first
        /// successful probe.
        /// </summary>
        public static void Poll()
        {
            if (IsReady || _reportedFailure)
            {
                return;
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            int state = war_core_ready();
            // 1 = ready, 2 = failed, bitwise-or'd.
            if ((state & 1) != 0)
            {
                TryFinalize();
            }
            else if ((state & 2) != 0)
            {
                Fail("the Rust module failed to load (see the browser console for " +
                     "the WebGL plugin error; check that .wasm is served as " +
                     "application/wasm and that the COOP/COEP headers are set)");
            }
#endif
        }

        private static void TryFinalize()
        {
            // `int`, not `uint`: the WebGL glue returns -1 to mean "still
            // loading", and as a uint that would silently become 4294967295 and
            // turn the guard below into dead code that reports a bogus mismatch.
            int abi;
            int slots;
            try
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                abi = war_core_abi_version();
                slots = war_core_state_slots();
#else
                abi = (int)wa_spike_abi_version();
                slots = (int)wa_spike_state_slots();
#endif
            }
            catch (DllNotFoundException e)
            {
                Fail("war_core_ffi.dll was not found. Build it with " +
                     "crates/tools/build-native.ps1, then restart the Editor. " + e.Message);
                return;
            }
            catch (EntryPointNotFoundException e)
            {
                Fail("war_core_ffi.dll is present but does not export the expected C " +
                     "ABI. It is probably stale -- rebuild it. " + e.Message);
                return;
            }

            if (abi < 0)
            {
                return; // still loading
            }

            if (abi != (int)ExpectedAbiVersion)
            {
                Fail($"Rust module ABI is {abi} but this C# was built against " +
                     $"{ExpectedAbiVersion}. The .wasm/.dll is stale; rebuild with " +
                     "crates/tools/build-wasm.ps1 (or build-native.ps1) and rebuild Unity.");
                return;
            }

            if (slots != StateSlotCount)
            {
                Fail($"Rust module reports {slots} state slots but this C# allocates " +
                     $"{StateSlotCount}. Rebuild both sides together.");
                return;
            }

            // The highest slot this file reads must lie inside the layout the
            // module says it uses. This is the single check that catches C# and
            // Rust disagreeing about where a field lives -- the failure mode
            // where everything looks plausible but the tanks read the wrong
            // numbers.
            uint layoutSlots;
#if UNITY_WEBGL && !UNITY_EDITOR
            layoutSlots = (uint)war_core_state_layout_slots();
#else
            layoutSlots = wa_spike_state_layout_slots();
#endif
            if (layoutSlots == 0 || SlotWriteSentinel >= (int)layoutSlots)
            {
                Fail($"this C# reads slot {SlotWriteSentinel} but the Rust module only " +
                     $"uses {layoutSlots}. The state layout is out of sync; rebuild both.");
                return;
            }

            IsReady = true;
            Status = $"ready (abi {abi}, {slots} slots)";
            Debug.Log($"[WarSimRuntime] Rust core {Status} via " + DescribeBackend());
        }

        private static void Fail(string message)
        {
            _reportedFailure = true;
            Status = "FAILED: " + message;
            Debug.LogError("[WarSimRuntime] " + Status);
        }

        private static string DescribeBackend()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return "WarCore.jslib WebAssembly module";
#else
            return "war_core_ffi.dll native plugin";
#endif
        }

        // ---------------------------------------------------------------------
        // Per-frame interface
        // ---------------------------------------------------------------------

        /// <summary>
        /// Advance the ground simulation by one frame and refresh
        /// <see cref="StateBuffer"/>. No-op until the module is ready.
        /// </summary>
        public static void AdvanceGround(
            float dt, float throttle, float steer, float turretTrim, bool fire, Vector3 aim)
        {
if (!IsReady)
        {
            return;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
            war_core_advance(dt, throttle, steer, turretTrim, fire ? 1 : 0,
                             aim.x, aim.y, aim.z);
            CopyStateFromModule();
#else
            IntPtr handle = NativeHandle.Value;
            wa_spike_advance(handle, dt, throttle, steer, turretTrim, fire ? 1u : 0u,
                             aim.x, aim.y, aim.z);
            int written = (int)wa_spike_read(handle, StateBuffer, StateSlotCount);
            if (written != StateSlotCount)
            {
                Fail($"read returned {written} slots, expected {StateSlotCount}");
                IsReady = false;
                return;
            }
#endif
            ValidateBlob();
            RefreshFeed();
        }

        #if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>
        /// Copy the state blob out of the module's own linear memory.
        /// </summary>
        /// <remarks>
        /// WebGL only. The native backend writes straight into the managed array
        /// via <c>[Out] float[]</c> marshalling, so it needs no copy.
        /// <para>
        /// The returned pointer is only valid until the next call into the module,
        /// so this must happen before anything else touches it -- hence calling it
        /// immediately after <c>war_core_advance</c> with nothing in between.
        /// </para>
        /// </remarks>
        private static void CopyStateFromModule()
        {
            int ptr = war_core_state_ptr();
            if (ptr == 0)
            {
                // The module owns this buffer; a null here means it is gone or the
                // heap moved unexpectedly. Stop stepping rather than read garbage.
                Fail("war_core_state_ptr returned 0; the Rust module's scratch region " +
                     "is unavailable");
                IsReady = false;
                return;
            }
            Marshal.Copy(new IntPtr(ptr), StateBuffer, 0, StateSlotCount);
        }
#endif

        /// <summary>
        /// Cheap integrity check on every frame. A blob that fails here means the
        /// Rust side and the C# side disagree about the layout, which is worth
        /// catching on frame 1 rather than as inexplicably drifting tanks.
        /// </summary>
        private static void ValidateBlob()
        {
            if (StateBuffer[SlotMagic] != ExpectedMagic)
            {
                Fail($"state blob magic mismatch ({StateBuffer[SlotMagic]}); " +
                     "the Rust module and this C# disagree about the state layout");
                IsReady = false;
                return;
            }

            if ((uint)StateBuffer[SlotAbiVersion] != ExpectedAbiVersion)
            {
                Fail($"state blob ABI mismatch ({StateBuffer[SlotAbiVersion]}); " +
                     "the Rust module and this C# disagree about the state layout");
                IsReady = false;
                return;
            }

            // Prove the blob is rewritten every frame rather than left stale.
            float sentinel = StateBuffer[SlotWriteSentinel];
            if (_lastSentinel == 0f)
            {
                _lastSentinel = sentinel;
            }
            else if (sentinel != _lastSentinel)
            {
                _lastSentinel = sentinel;
                _sentinelAdvanced = true;
            }
        }

        /// <summary>
        /// True once the state blob has been observed changing between frames.
        /// Asserted by the interop PlayMode tests.
        /// </summary>
        public static bool ObservedFreshStateWrite => _sentinelAdvanced;

        /// <summary>
        /// Restore the simulation to its just-constructed state. The handle is
        /// session-long, so this is how a match restart -- or an isolated test --
        /// gets a known starting point.
        /// </summary>
        public static void Reset()
        {
            if (!IsReady)
            {
                return;
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            war_core_reset();
#else
            wa_spike_reset(NativeHandle.Value);
#endif
            _lastSentinel = 0f;
            _sentinelAdvanced = false;
            _feed.Clear();
        }

        /// <summary>
        /// How many slots the Rust module says it uses. The managed side asserts
        /// its own highest slot index is below this, which is what catches the two
        /// sides disagreeing about the layout.
        /// </summary>
        public static int StateLayoutSlots
        {
            get
            {
                if (!IsReady)
                {
                    return 0;
                }
#if UNITY_WEBGL && !UNITY_EDITOR
                return war_core_state_layout_slots();
#else
                return (int)wa_spike_state_layout_slots();
#endif
            }
        }

        /// <summary>Decode the length-prefixed UTF-8 feed into <see cref="Feed"/>.</summary>
        private static void RefreshFeed()
        {
            int length;
#if UNITY_WEBGL && !UNITY_EDITOR
            length = war_core_feed_length();
            if (length > 0)
            {
                Marshal.Copy(new IntPtr(war_core_feed_ptr()), FeedBuffer, 0, length);
            }
#else
            length = (int)wa_spike_feed(NativeHandle.Value, FeedBuffer, (uint)FeedBuffer.Length);
#endif

            if (length <= 0)
            {
                _feed.Clear();
                return;
            }

            _feed.Clear();
            int cursor = 0;
            while (cursor + 4 <= length)
            {
                int messageLength = FeedBuffer[cursor]
                    | (FeedBuffer[cursor + 1] << 8)
                    | (FeedBuffer[cursor + 2] << 16)
                    | (FeedBuffer[cursor + 3] << 24);
                cursor += 4;
                if (messageLength < 0 || cursor + messageLength > length)
                {
                    Debug.LogError("[WarSimRuntime] feed blob is malformed; " +
                                   "the frame feed has been dropped this frame.");
                    break;
                }
                _feed.Add(Encoding.UTF8.GetString(FeedBuffer, cursor, messageLength));
                cursor += messageLength;
            }
        }

        /// <summary>Read one scalar straight from the module, without a full copy.</summary>
        public static double ReadSlot(int slot)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return war_core_read_slot(slot);
#else
            return StateBuffer[slot];
#endif
        }

        /// <summary>Scalar probe used by the interop tests.</summary>
        public static double Echo(double x)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return war_core_echo(x);
#else
            return wa_spike_echo(NativeHandle.Value, (float)x);
#endif
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        /// <summary>
        /// The native handle, created on first use. A <see cref="LazyInitializer"/>
        /// rather than a static field initialiser because the DLL must be present
        /// before this type is first touched.
        /// </summary>
        private static class NativeHandle
        {
            internal static readonly IntPtr Value = wa_spike_create();
        }
#endif
    }
}