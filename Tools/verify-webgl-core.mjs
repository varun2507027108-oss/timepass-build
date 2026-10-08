// End-to-end verification that the Rust simulation core loads inside a real
// Unity WebGL player running in a real browser.
//
// This is the last Phase 0 gate. Everything else can be green while the WebGL
// integration is still broken in ways only a browser would show: a wrong
// StreamingAssets path, a missing COOP/COEP header, a MIME type that defeats
// streaming compilation, or a .jslib symbol that never made it into the runtime.
//
// How it works, with no dependencies:
//   1. node Tools/webgl-server.mjs           (started separately, or here)
//   2. launch Chrome headless with --remote-debugging-port
//   3. speak the DevTools Protocol over the WebSocket built into Node >= 22
//   4. collect console output and page errors, then assert on them
//
// Usage:
//   node Tools/verify-webgl-core.mjs [--url http://127.0.0.1:8099/] [--timeout 180]
//
// Exits 0 if the Rust core reported ready in the browser console, 1 otherwise.

import { spawn } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import process from 'node:process';

const args = process.argv.slice(2);
function arg(name, fallback) {
  const i = args.indexOf(`--${name}`);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
}
const url = arg('url', 'http://127.0.0.1:8099/');
const timeoutMs = Number(arg('timeout', '180')) * 1000;
const keepOpen = args.includes('--keep-open');

const CHROME_CANDIDATES = [
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
];

const failures = [];
const consoleLines = [];
const pageErrors = [];

function check(name, ok, detail = '') {
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : `  ${detail}`}`);
  if (!ok) failures.push(name);
}

async function findChrome() {
  const { existsSync } = await import('node:fs');
  for (const p of CHROME_CANDIDATES) {
    if (existsSync(p)) return p;
  }
  throw new Error(`no Chrome or Edge found; looked in:\n  ${CHROME_CANDIDATES.join('\n  ')}`);
}

async function waitForEndpoint(port, deadline) {
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/json/version`);
      if (res.ok) return await res.json();
    } catch {
      // not up yet
    }
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error(`DevTools endpoint did not come up on port ${port}`);
}

const chromePath = await findChrome();
const port = 9222 + Math.floor(Math.random() * 400);
const profile = await mkdtemp(join(tmpdir(), 'warcore-cdp-'));

console.log(`chrome:  ${chromePath}`);
console.log(`url:     ${url}`);
console.log(`profile: ${profile}\n`);

const chrome = spawn(chromePath, [
  '--headless=new',
  `--remote-debugging-port=${port}`,
  `--user-data-dir=${profile}`,
  '--no-first-run',
  '--no-default-browser-check',
  '--disable-extensions',
  '--disable-background-networking',
  // Unity needs a GPU context; without these, WebGL is unavailable and the player
  // never reaches the point where it would load our module.
  '--enable-unsafe-swiftshader',
  '--use-gl=angle',
  '--use-angle=swiftshader',
  '--window-size=1280,720',
  'about:blank',
], { stdio: ['ignore', 'pipe', 'pipe'] });

let chromeStderr = '';
chrome.stderr.on('data', (d) => { chromeStderr += d.toString(); });

function shutdown(code) {
  try { chrome.kill(); } catch { /* already gone */ }
  if (!keepOpen) rm(profile, { recursive: true, force: true }).catch(() => {});
  process.exit(code);
}

try {
  await waitForEndpoint(port, Date.now() + 30000);

  // Open a tab on the build and attach to it.
  const target = await (await fetch(
    `http://127.0.0.1:${port}/json/new?${encodeURIComponent(url)}`,
    { method: 'PUT' },
  )).json();

  const ws = new WebSocket(target.webSocketDebuggerUrl);
  let nextId = 1;
  const pending = new Map();

  const send = (method, params = {}) =>
    new Promise((resolve, reject) => {
      const id = nextId++;
      pending.set(id, { resolve, reject });
      ws.send(JSON.stringify({ id, method, params }));
    });

  await new Promise((resolve, reject) => {
    ws.addEventListener('open', resolve, { once: true });
    ws.addEventListener('error', reject, { once: true });
  });

  ws.addEventListener('message', (event) => {
    const msg = JSON.parse(event.data);

    if (msg.id !== undefined && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) reject(new Error(JSON.stringify(msg.error)));
      else resolve(msg.result);
      return;
    }

    // Collect everything the page prints, so we can assert on it.
    if (msg.method === 'Runtime.consoleAPICalled') {
      const text = (msg.params.args ?? [])
        .map((a) => a.value ?? a.description ?? a.unserializableValue ?? '')
        .join(' ');
      consoleLines.push(`[${msg.params.type}] ${text}`);
    } else if (msg.method === 'Log.entryAdded') {
      const e = msg.params.entry;
      consoleLines.push(`[log:${e.level}] ${e.text}`);
      if (e.level === 'error') pageErrors.push(e.text);
    } else if (msg.method === 'Runtime.exceptionThrown') {
      const d = msg.params.exceptionDetails;
      pageErrors.push(d.exception?.description ?? d.text ?? 'unknown exception');
    } else if (msg.method === 'Network.responseReceived') {
      const r = msg.params.response;
      if (r.url.endsWith('.wasm')) {
        consoleLines.push(
          `[net] ${r.status} ${r.mimeType} ${r.url}`,
        );
      }
    }
  });

  await send('Runtime.enable');
  await send('Log.enable');
  await send('Network.enable');
  await send('Page.enable');
  await send('Page.navigate', { url });

  // Unity's boot plus a WebAssembly instantiate; poll rather than guess.
  const deadline = Date.now() + timeoutMs;
  let ready = false;
  while (Date.now() < deadline && !ready) {
    await new Promise((r) => setTimeout(r, 1000));
    ready = consoleLines.some((l) => l.includes('[WarCore] ready'));
  }

  console.log('\n--- page console ---');
  // Print everything, not a filtered subset: an EntryPointNotFoundException for a
  // missing __Internal symbol does not contain the word "error", so a filter
  // tuned to look for "error" happily hides the one line that explains the
  // failure.
  for (const line of consoleLines.slice(0, 80)) console.log(`  ${line}`);
  if (consoleLines.length === 0) console.log('  (no console output captured at all)');

  if (pageErrors.length > 0) {
    console.log('\n--- page errors / exceptions ---');
    for (const e of pageErrors.slice(0, 20)) {
      console.log(`  ${e.split('\n').slice(0, 6).join('\n  ')}`);
    }
  }

  console.log('\n--- assertions ---');
  check('Rust core reported ready in the browser', ready,
    consoleLines.length === 0 ? '(no console output captured at all)' : '');

  const readyLine = consoleLines.find((l) => l.includes('[WarCore] ready')) ?? '';
  check('reported ABI version is 1', /abi=1\b/.test(readyLine), `got: ${readyLine}`);
  check('reported 512 state slots', /slots=512\b/.test(readyLine), `got: ${readyLine}`);

  const wasmResponses = consoleLines.filter((l) => l.startsWith('[net]') && l.includes('.wasm'));
  const rustWasm = wasmResponses.find((l) => l.includes('war_core'));
  check('war_core.wasm was fetched over HTTP', Boolean(rustWasm),
    wasmResponses.length ? wasmResponses.join('; ') : '(no .wasm response seen)');
  if (rustWasm) {
    check('war_core.wasm served as application/wasm',
      rustWasm.includes('application/wasm'), `got: ${rustWasm}`);
    check('war_core.wasm returned 200', /\[net\] 200\b/.test(rustWasm), `got: ${rustWasm}`);
  }

  const warCoreErrors = pageErrors.filter((e) => /WarCore/i.test(e));
  check('no WarCore errors reported by the page', warCoreErrors.length === 0,
    warCoreErrors.join(' | '));

  console.log(`\n${failures.length === 0
    ? 'WEBGL RUST CORE VERIFIED'
    : `${failures.length} CHECK(S) FAILED: ${failures.join(', ')}`}`);
  ws.close();
  shutdown(failures.length === 0 ? 0 : 1);
} catch (err) {
  console.error('\nverification harness failed:', err.message);
  if (consoleLines.length === 0) {
    console.error('chrome stderr:\n', chromeStderr.slice(0, 2000));
  } else {
    console.error('captured console:\n', consoleLines.slice(0, 40).join('\n'));
  }
  shutdown(1);
}