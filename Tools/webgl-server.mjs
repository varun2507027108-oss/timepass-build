// Minimal zero-dependency static server for Unity WebGL builds.
//
// Exists because a WebGL build cannot be opened via file:// and needs specific
// response headers. Getting these wrong produces failures that look exactly
// like a graphics problem, which is the worst possible debugging experience:
//
//   Cross-Origin-Opener-Policy:   same-origin      <- required for SharedArrayBuffer
//   Cross-Origin-Embedder-Policy: require-corp      <- required for SharedArrayBuffer
//   Content-Type: application/wasm                   <- required for streaming compile
//   Cache-Control: no-store on the loader            <- so a rebuild is actually picked up
//
// Unity also ships its own WebGLSupport/BuildTools/SimpleWebServer.exe; this one
// exists because it prints exactly which headers it set, which is the
// information you actually want when a build will not boot.
//
// Usage:
//   node Tools/webgl-server.mjs [buildDir] [port]
//
// Defaults to Builds/WebGL on port 8099.

import { createServer } from 'node:http';
import { createReadStream, promises as fs } from 'node:fs';
import { extname, join, normalize, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import process from 'node:process';

const repoRoot = resolve(fileURLToPath(new URL('..', import.meta.url)));
const buildDir = resolve(process.argv[2] ?? join(repoRoot, 'Builds', 'WebGL'));
const port = Number(process.argv[3] ?? 8099);

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.wasm': 'application/wasm',
  '.data': 'application/octet-stream',
  '.symbols': 'application/octet-stream',
  '.mem': 'application/octet-stream',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.ico': 'image/x-icon',
  '.webp': 'image/webp',
  '.br': 'application/octet-stream',
  '.gz': 'application/gzip',
};

async function resolveTarget(urlPath) {
  // Strip the query string and decode, then reject anything that escapes the
  // build directory.
  const clean = decodeURIComponent(urlPath.split('?')[0].split('#')[0]);
  const candidate = resolve(join(buildDir, normalize(clean)));
  if (candidate !== buildDir && !candidate.startsWith(buildDir + sep)) {
    return null; // path traversal attempt
  }
  try {
    const stat = await fs.stat(candidate);
    if (stat.isDirectory()) {
      const index = join(candidate, 'index.html');
      await fs.stat(index);
      return index;
    }
    return candidate;
  } catch {
    return null;
  }
}

const server = createServer(async (req, res) => {
  const target = await resolveTarget(req.url ?? '/');
  const headers = {
    // Required for SharedArrayBuffer, which Unity's threaded builds use.
    'Cross-Origin-Opener-Policy': 'same-origin',
    'Cross-Origin-Embedder-Policy': 'require-corp',
    'Cross-Origin-Resource-Policy': 'same-origin',
    // The loader must not be cached, or a rebuilt player silently keeps running
    // the previous one -- which looks exactly like "my fix did nothing".
    'Cache-Control': 'no-store, must-revalidate',
  };

  if (target === null) {
    res.writeHead(404, { ...headers, 'Content-Type': 'text/plain; charset=utf-8' });
    res.end(`404 not found: ${req.url}\nserving ${buildDir}`);
    return;
  }

  const type = MIME[extname(target).toLowerCase()] ?? 'application/octet-stream';
  headers['Content-Type'] = type;

  try {
    const stat = await fs.stat(target);
    headers['Content-Length'] = String(stat.size);
    res.writeHead(200, headers);
    if (req.method === 'HEAD') {
      res.end();
      return;
    }
    createReadStream(target).pipe(res);
  } catch (err) {
    res.writeHead(500, { ...headers, 'Content-Type': 'text/plain; charset=utf-8' });
    res.end(`500 ${err.message}`);
  }
});

server.listen(port, '127.0.0.1', () => {
  console.log(`serving ${buildDir}`);
  console.log(`  http://127.0.0.1:${port}/`);
  console.log('  headers on every response:');
  console.log('    Cross-Origin-Opener-Policy:   same-origin');
  console.log('    Cross-Origin-Embedder-Policy: require-corp');
  console.log('    .wasm served as application/wasm');
});