// Writes public/icons/icon{16,32,48,128}.png: white down arrow on a rounded accent square.
// No image deps: rasterize with 4x4 supersampling, encode PNG with zlib.
import { deflateSync } from 'node:zlib';
import { mkdirSync, writeFileSync } from 'node:fs';

const ACCENT = [37, 99, 235]; // #2563eb
const SS = 4;

// Shapes in unit coordinates (0..1).
function inRoundedSquare(x, y) {
  const r = 0.22;
  const cx = Math.min(Math.max(x, r), 1 - r);
  const cy = Math.min(Math.max(y, r), 1 - r);
  return (x - cx) ** 2 + (y - cy) ** 2 <= r * r;
}
function inArrow(x, y) {
  const shaft = x >= 0.42 && x <= 0.58 && y >= 0.18 && y <= 0.56;
  // Head: triangle with apex at (0.5, 0.74), base y=0.48 from x=0.24..0.76.
  const head = y >= 0.48 && y <= 0.74 && Math.abs(x - 0.5) <= (0.74 - y);
  const base = x >= 0.24 && x <= 0.76 && y >= 0.8 && y <= 0.86;
  return shaft || head || base;
}

function crc32(buf) {
  let c, crc = ~0;
  for (const b of buf) {
    c = (crc ^ b) & 0xff;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    crc = (crc >>> 8) ^ c;
  }
  return ~crc >>> 0;
}
function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(td));
  return Buffer.concat([len, td, crc]);
}

function png(size) {
  const raw = Buffer.alloc(size * (size * 4 + 1));
  for (let py = 0; py < size; py++) {
    raw[py * (size * 4 + 1)] = 0; // filter: none
    for (let px = 0; px < size; px++) {
      let bg = 0, fg = 0;
      for (let sy = 0; sy < SS; sy++) {
        for (let sx = 0; sx < SS; sx++) {
          const x = (px + (sx + 0.5) / SS) / size;
          const y = (py + (sy + 0.5) / SS) / size;
          if (inRoundedSquare(x, y)) inArrow(x, y) ? fg++ : bg++;
        }
      }
      const n = SS * SS, a = bg + fg;
      const o = py * (size * 4 + 1) + 1 + px * 4;
      if (a === 0) continue;
      for (let i = 0; i < 3; i++) raw[o + i] = Math.round((ACCENT[i] * bg + 255 * fg) / a);
      raw[o + 3] = Math.round((255 * a) / n);
    }
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

const dir = new URL('../public/icons/', import.meta.url);
mkdirSync(dir, { recursive: true });
for (const s of [16, 32, 48, 128]) writeFileSync(new URL(`icon${s}.png`, dir), png(s));
