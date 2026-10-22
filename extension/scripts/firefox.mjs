// dist/ → dist-firefox/: the same build with the Firefox manifest (see manifest.mjs).
//   npm run build && npm run build:firefox
// Load it in Firefox: about:debugging → This Firefox → Load Temporary Add-on… → dist-firefox/manifest.json.
import fs from 'node:fs';
import path from 'node:path';
import { firefoxManifest } from './manifest.mjs';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), '..');
const dist = path.join(root, 'dist');
const out = path.join(root, 'dist-firefox');
if (!fs.existsSync(path.join(dist, 'manifest.json'))) { console.error('dist/ is missing: run npm run build first'); process.exit(1); }

fs.rmSync(out, { recursive: true, force: true });
fs.cpSync(dist, out, { recursive: true });
const manifest = JSON.parse(fs.readFileSync(path.join(dist, 'manifest.json'), 'utf8'));
fs.writeFileSync(path.join(out, 'manifest.json'), JSON.stringify(firefoxManifest(manifest), null, 2));
console.log(`dist-firefox/ (${manifest.version})`);
