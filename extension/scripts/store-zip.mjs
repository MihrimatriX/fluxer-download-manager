// dist/         → store/fluxer-extension-<version>.zip for the Chrome Web Store and Edge Add-ons.
// dist-firefox/ → store/fluxer-extension-firefox-<version>.zip for addons.mozilla.org.
// The Chromium stores assign their own extension IDs and reject a manifest that carries "key", so it is
// dropped here; the unpacked/dev build keeps it (fixed ID dckkomckipcaglacbgfmbgmapfckpkja).
//   npm run build && npm run store
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { storeManifest } from './manifest.mjs';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), '..');
if (!fs.existsSync(path.join(root, 'dist', 'manifest.json'))) { console.error('dist/ is missing: run npm run build first'); process.exit(1); }
writeZip(path.join(root, 'dist'), 'fluxer-extension', storeManifest);
if (fs.existsSync(path.join(root, 'dist-firefox', 'manifest.json'))) writeZip(path.join(root, 'dist-firefox'), 'fluxer-extension-firefox', (m) => m);

function writeZip(dist, prefix, transform) {
  const files = [];
  const walk = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) walk(p);
      else files.push(p);
    }
  };
  walk(dist);

  const manifest = transform(JSON.parse(fs.readFileSync(path.join(dist, 'manifest.json'), 'utf8')));

  // Minimal zip writer (deflate, no external dependency).
  const local = [], central = [];
  let offset = 0;
  for (const file of files) {
    const name = path.relative(dist, file).split(path.sep).join('/');
    const data = name === 'manifest.json' ? Buffer.from(JSON.stringify(manifest, null, 2)) : fs.readFileSync(file);
    const packed = zlib.deflateRawSync(data, { level: 9 });
    const crc = zlib.crc32(data);
    const nameBuf = Buffer.from(name, 'utf8');
    const head = Buffer.alloc(30);
    head.writeUInt32LE(0x04034b50, 0); head.writeUInt16LE(20, 4); head.writeUInt16LE(0x0800, 6); head.writeUInt16LE(8, 8);
    head.writeUInt32LE(0, 10); head.writeUInt32LE(crc, 14); head.writeUInt32LE(packed.length, 18); head.writeUInt32LE(data.length, 22);
    head.writeUInt16LE(nameBuf.length, 26); head.writeUInt16LE(0, 28);
    local.push(head, nameBuf, packed);
    const dir = Buffer.alloc(46);
    dir.writeUInt32LE(0x02014b50, 0); dir.writeUInt16LE(20, 4); dir.writeUInt16LE(20, 6); dir.writeUInt16LE(0x0800, 8); dir.writeUInt16LE(8, 10);
    dir.writeUInt32LE(0, 12); dir.writeUInt32LE(crc, 16); dir.writeUInt32LE(packed.length, 20); dir.writeUInt32LE(data.length, 24);
    dir.writeUInt16LE(nameBuf.length, 28); dir.writeUInt32LE(offset, 42);
    central.push(dir, nameBuf);
    offset += head.length + nameBuf.length + packed.length;
  }
  const centralSize = central.reduce((n, b) => n + b.length, 0);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(files.length, 8); end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(centralSize, 12); end.writeUInt32LE(offset, 16);

  const out = path.join(root, 'store', `${prefix}-${manifest.version}.zip`);
  fs.mkdirSync(path.dirname(out), { recursive: true });
  fs.writeFileSync(out, Buffer.concat([...local, ...central, end]));
  console.log(`${files.length} files → ${path.relative(root, out)}`);
}
