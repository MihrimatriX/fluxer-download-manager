// tools/strings.tsv (key \t English \t Turkish) → src/Fluxer.App/Strings.resx + Strings.tr.resx.
// Keeps only keys referenced as Strings.X in src/Fluxer.App (*.cs, *.axaml); fails on missing keys.
//   node tools/gen-resx.mjs
import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), '..');
const app = path.join(root, 'src', 'Fluxer.App');

const used = new Set();
const walk = (dir) => {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) { if (e.name !== 'bin' && e.name !== 'obj') walk(p); continue; }
    if (!/\.(cs|axaml)$/.test(e.name)) continue;
    for (const m of fs.readFileSync(p, 'utf8').matchAll(/Strings\.([A-Z][A-Za-z0-9]+)/g)) if (m[1] !== 'Culture') used.add(m[1]);
  }
};
walk(app);

const rows = fs.readFileSync(path.join(root, 'tools', 'strings.tsv'), 'utf8')
  .split(/\r?\n/).filter(Boolean).map((l) => l.split('\t')).filter((r) => used.has(r[0]));
const missing = [...used].filter((u) => !rows.some((r) => r[0] === u));
if (missing.length) { console.error('missing in strings.tsv: ' + missing.join(' ')); process.exit(1); }

const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const head = '<?xml version="1.0" encoding="utf-8"?>\n<root>\n  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>\n  <resheader name="version"><value>2.0</value></resheader>\n  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\n  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\n';
for (const [file, col] of [['Strings.resx', 1], ['Strings.tr.resx', 2]]) {
  let x = head;
  for (const r of rows) x += `  <data name="${r[0]}" xml:space="preserve"><value>${esc(r[col])}</value></data>\n`;
  fs.writeFileSync(path.join(app, file), x + '</root>\n');
}
console.log(`${rows.length} keys → Strings.resx, Strings.tr.resx (${used.size} used)`);
