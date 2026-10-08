// Phase 0 WebAssembly interop probe.
//
// This performs the *same call sequence* the Unity `WarCore.jslib` glue will,
// but outside Unity. If it passes, the WebGL path is proven: the module is a
// valid standalone WebAssembly binary, its exports are reachable from plain
// JavaScript, and both the scalar and byte-slice calling conventions behave.
//
// It also asserts the module's *structural* properties, because those decide
// whether Unity can link it at all. A toolchain change that breaks the Unity
// integration fails here, on the build machine, rather than inside a browser.
//
// Usage:  node crates/tools/wasm-interop-probe.mjs <path-to-war_core.wasm>

import { readFile } from 'node:fs/promises';
import process from 'node:process';

const wasmPath = process.argv[2];
if (!wasmPath) {
  console.error('usage: node wasm-interop-probe.mjs <path-to.wasm>');
  process.exit(2);
}

const failures = [];
function check(name, condition, detail = '') {
  console.log(`  ${condition ? 'PASS' : 'FAIL'}  ${name}${condition ? '' : `  ${detail}`}`);
  if (!condition) failures.push(name);
}

const bytes = await readFile(wasmPath);
console.log(`module: ${wasmPath}`);
console.log(`size:   ${bytes.length} bytes (${(bytes.length / 1024).toFixed(1)} KiB)`);

const module = await WebAssembly.compile(bytes);
const imports = WebAssembly.Module.imports(module);
const exports = WebAssembly.Module.exports(module);

console.log(`\nexports (${exports.length}):`);
for (const e of exports) console.log(`  ${e.name}: ${e.kind}`);

// ===========================================================================
// [0] Module shape -- pins the WebGL integration strategy.
// ===========================================================================
console.log('\n[0] module shape (pins the Unity integration strategy)');
const exportNames = exports.map((e) => e.name);

// Rust's cdylib links its own linear memory and needs no WASI/emscripten imports
// once the code performs no I/O, which is what lets the .jslib instantiate it
// with an empty import object.
check('has zero imports', imports.length === 0,
  `found: ${imports.map((i) => `${i.module}.${i.name}`).join(', ')}`);

// Conversely, defining and exporting `memory` makes this module INELIGIBLE as a
// dynamic-link side module -- side modules must import the host's memory. So
// Unity's native WebAssembly plugin linker cannot consume this file, and the
// .jslib standalone-instantiation route is required. See PORT-NOTES.md.
check('exports its own memory', exportNames.includes('memory'));
check('is NOT a side module (no table export)',
  !exportNames.includes('__indirect_function_table'));
check('is NOT an emscripten archive (no __data_end)',
  !exportNames.includes('__data_end'));

// ===========================================================================
// Instantiate.
// ===========================================================================
// Rust std links WASI-ish symbols even when never called, and they would still
// appear in the import list -- but with zero imports nothing is needed. Assert
// that explicitly rather than assuming it, since it is what makes the .jslib
// import object trivially `{}`.
const instance = await WebAssembly.instantiate(module, {});
const ex = instance.exports;
const memory = ex.memory;

console.log('\n[1] version handshake');
const required = [
  'wa_spike_abi_version',
  'wa_spike_state_slots',
  'wa_spike_create',
  'wa_spike_destroy',
  'wa_spike_echo',
  'wa_spike_advance',
  'wa_spike_read',
  'wa_spike_feed',
  'wa_spike_echo_ids',
];
for (const name of required) {
  check(`export ${name} present`, typeof ex[name] === 'function');
}
if (failures.length > 0) {
  console.error('\nmissing exports; cannot continue');
  process.exit(1);
}

const abi = ex.wa_spike_abi_version();
const SLOTS = ex.wa_spike_state_slots();
console.log(`  abi_version=${abi} state_slots=${SLOTS}`);
check('abi_version is 1', abi === 1, `got ${abi}`);
check('state_slots is 512', SLOTS === 512, `got ${SLOTS}`);
check('memory is exported and non-trivial', memory instanceof WebAssembly.Memory
  && memory.buffer.byteLength > 1024, `${memory.buffer.byteLength} bytes`);

// ===========================================================================
// Scratch buffers, carved out of the top of the module's own memory exactly as
// WarCore.jslib does. Because the module owns its memory, there is no shared
// heap with the host -- every buffer must live in here.
// ===========================================================================
const view = () => new Float32Array(memory.buffer);
const bytesView = () => new Uint8Array(memory.buffer);

const RESERVE = 16 * 1024;
const base = memory.buffer.byteLength - RESERVE;
const statePtr = base;
const feedPtr = base + 4096;
const idPtr = base + 12288;
const idOutPtr = idPtr + 64;
console.log(`\n[2] scratch regions (carved from the module's own memory)`);
for (const [name, ptr, size] of [
  ['state', statePtr, SLOTS * 4],
  ['feed', feedPtr, 4096],
  ['ids', idPtr, 128],
]) {
  check(`${name} region fits and is ${size === SLOTS * 4 ? '4' : '1'}-byte aligned`,
    ptr + size <= memory.buffer.byteLength && ptr % (size === SLOTS * 4 ? 4 : 1) === 0,
    `ptr=${ptr} end=${ptr + size} limit=${memory.buffer.byteLength}`);
}

// ===========================================================================
// [3] Lifecycle + scalar round-trip.
// ===========================================================================
console.log('\n[3] scalar round-trip through a live handle');
const handle = ex.wa_spike_create();
check('create returns a non-null pointer', handle !== 0 && handle !== undefined);
check('create returns a pointer inside the module heap',
  handle > 0 && handle < memory.buffer.byteLength, `${handle}`);

const echoValue = ex.wa_spike_echo(handle, 1.0);
check('echo(1.0) is finite', Number.isFinite(echoValue), `got ${echoValue}`);
check('echo is deterministic', echoValue === ex.wa_spike_echo(handle, 1.0));
check('echo handles a negative input', Number.isFinite(ex.wa_spike_echo(handle, -3.5)));
check('echo handles a tiny input', Number.isFinite(ex.wa_spike_echo(handle, 1e-4)));

// ===========================================================================
// [4] Scalar arguments into Rust, including the three-scalar aim point.
// ===========================================================================
console.log('\n[4] scalar arguments into Rust');
check('advance with scalars returns 1',
  ex.wa_spike_advance(handle, 1 / 30, 1.0, 0.0, 0.0, 1, 30.0, 0.0, 5.0) === 1);
check('advance with fire=0 returns 1',
  ex.wa_spike_advance(handle, 1 / 30, 1.0, 0.0, 0.0, 0, 0.0, 0.0, 0.0) === 1);

// All three aim components must arrive, in order. The player spawns at
// (0, 0, -35), so an aim of (30, 0, 5) is 50m away (3-4-5). A transposed or
// sign-flipped pair changes the reported range and fails here.
{
  const n = ex.wa_spike_feed(handle, feedPtr, 4096);
  const raw = bytesView().slice(feedPtr, feedPtr + n);
  const text = new TextDecoder().decode(raw);
  check('all three aim scalars arrived, in order (expected "50m")',
    text.includes('50m'), `got ${JSON.stringify(text)}`);
}

// ===========================================================================
// [5] Byte-slice arguments -- the other pointer-shaped input the real vehicle
//     and aircraft constructors will need.
// ===========================================================================
console.log('\n[5] byte-slice arguments into Rust');
{
  const player = new TextEncoder().encode('pziv-h');
  const enemy = new TextEncoder().encode('t34-76');
  bytesView().set(player, idPtr);
  bytesView().set(enemy, idPtr + 32);
  const n = ex.wa_spike_echo_ids(
    handle, idPtr, player.length, idPtr + 32, enemy.length, idOutPtr, 32);
  const got = new TextDecoder().decode(bytesView().slice(idOutPtr, idOutPtr + n));
  check('ids round-trip', got === 'pziv-h|t34-76', `got ${JSON.stringify(got)}`);
}

// ===========================================================================
// [6] The state blob.
// ===========================================================================
console.log('\n[6] state blob');
check('read reports 512 slots', ex.wa_spike_read(handle, statePtr, SLOTS) === 512);

// Compare against an f32-rounded literal: a JS double cannot hold the f32 nearest
// to 1.7014117e-5 exactly, so a direct === would fail spuriously.
const MAGIC = new Float32Array([1.7014117e-5])[0];
const blob = view().subarray(statePtr / 4, statePtr / 4 + SLOTS);
check('magic present', blob[0] === MAGIC, `got ${blob[0]} want ${MAGIC}`);
check('abi version echoed into the blob', blob[1] === abi, `got ${blob[1]}`);
check('sentinel was written', blob[15] > 1_000_000, `got ${blob[15]}`);
check('health is sane', blob[10] >= 0 && blob[10] <= 100, `got ${blob[10]}`);

check('player advanced from the spawn point', blob[4] > -35,
  `z=${blob[4]} (spawn is -35)`);
check('enemy has not moved (it is driven, not static)', blob[7] === 35,
  `z=${blob[7]}`);

// The blob must be rewritten on every read, not left stale.
const sentinelOnce = blob[15];
ex.wa_spike_read(handle, statePtr, SLOTS);
const sentinelTwice = view()[statePtr / 4 + 15];
check('blob is rewritten on every read', sentinelTwice - sentinelOnce === 1,
  `${sentinelOnce} -> ${sentinelTwice}`);

// A short buffer must be truncated, never overrun.
{
  const shortPtr = idOutPtr + 128;
  const n = ex.wa_spike_read(handle, shortPtr, 8);
  check('short buffer reports the clamped count', n === 8, `got ${n}`);
}

// ===========================================================================
// [7] Feed blob.
// ===========================================================================
console.log('\n[7] UTF-8 feed blob');
{
  const n = ex.wa_spike_feed(handle, feedPtr, 4096);
  check('feed produced bytes', n > 0, `got ${n}`);

  const raw = bytesView().slice(feedPtr, feedPtr + n);
  let cursor = 0;
  const messages = [];
  let malformed = false;
  while (cursor < raw.length) {
    if (cursor + 4 > raw.length) { malformed = true; break; }
    const len = new DataView(raw.buffer, raw.byteOffset + cursor, 4).getUint32(0, true);
    cursor += 4;
    if (cursor + len > raw.length) { malformed = true; break; }
    messages.push(new TextDecoder().decode(raw.subarray(cursor, cursor + len)));
    cursor += len;
  }
  check('feed framing is well formed', !malformed);
  check('feed blob consumed exactly', cursor === raw.length, `${cursor} vs ${raw.length}`);
  console.log(`  messages: ${JSON.stringify(messages)}`);

  // Too small for even one message: must emit nothing rather than a partial.
  const tiny = ex.wa_spike_feed(handle, feedPtr, 4);
  check('undersized feed buffer emits nothing', tiny === 0, `got ${tiny}`);
}

// ===========================================================================
// [8] Null handles must never trap -- a bad handle kills the host player if it
//     unwinds.
// ===========================================================================
console.log('\n[8] null handles must not trap');
check('echo(NULL) is NaN', Number.isNaN(ex.wa_spike_echo(0, 1.0)));
check('advance(NULL) returns 0',
  ex.wa_spike_advance(0, 0.016, 0, 0, 0, 0, 0, 0, 0) === 0);
check('read(NULL) returns 0', ex.wa_spike_read(0, statePtr, 512) === 0);
check('feed(NULL) returns 0', ex.wa_spike_feed(0, feedPtr, 4096) === 0);
check('echo_ids(NULL) returns 0',
  ex.wa_spike_echo_ids(0, idPtr, 5, idPtr + 32, 5, idOutPtr, 32) === 0);
ex.wa_spike_destroy(0); // explicitly a no-op, must not trap

// ===========================================================================
// [9] Sustained run -- heap growth would invalidate cached views, which is the
//     classic "state blob goes to zeroes after a few seconds" failure.
// ===========================================================================
console.log('\n[9] sustained run (500 steps)');
{
  let ok = true;
  let lastSentinel = 0;
  for (let i = 0; i < 500; i++) {
    const steer = Math.sin(i * 0.05);
    if (ex.wa_spike_advance(handle, 1 / 30, 1.0, steer, 0.0, i % 37 === 0 ? 1 : 0,
      30.0, 0.0, 5.0) !== 1) {
      ok = false;
      break;
    }
    if (ex.wa_spike_read(handle, statePtr, SLOTS) !== SLOTS) { ok = false; break; }
    // Re-create the view each step: this is what a cached view would fail to do.
    const b = new Float32Array(memory.buffer, statePtr, SLOTS);
    if (b[0] !== MAGIC || !Number.isFinite(b[2]) || !Number.isFinite(b[4])) { ok = false; break; }
    if (b[10] < 0 || b[10] > 100) { ok = false; break; }
    lastSentinel = b[15];
  }
  check('500 steps completed with a valid blob every frame', ok);
  console.log(`  final sentinel: ${lastSentinel}`);
}

// ===========================================================================
console.log('\n[10] teardown');
ex.wa_spike_destroy(handle);
check('destroy did not trap', true);

console.log(`\n${failures.length === 0
  ? 'ALL CHECKS PASSED'
  : `${failures.length} CHECK(S) FAILED: ${failures.join(', ')}`}`);
process.exit(failures.length === 0 ? 0 : 1);