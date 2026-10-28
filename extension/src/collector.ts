// Generic page resource collector. Pure over a Document (testable with happy-dom).
// No site-specific code — store policy.
import type { Resource } from './protocol';

export const MAX_STYLE_SCAN = 5000;

interface SrcsetCandidate { url: string; w?: number; x?: number }

/** Parses a srcset attribute (HTML spec: URL = run of non-whitespace, so URLs may contain commas). */
export function parseSrcset(srcset: string): SrcsetCandidate[] {
  const out: SrcsetCandidate[] = [];
  const s = srcset;
  let i = 0;
  while (i < s.length) {
    while (i < s.length && (s[i] === ',' || /\s/.test(s[i]))) i++;
    let start = i;
    while (i < s.length && !/\s/.test(s[i])) i++;
    let url = s.slice(start, i);
    let desc = '';
    if (url.endsWith(',')) {
      url = url.replace(/,+$/, '');
    } else {
      start = i;
      while (i < s.length && s[i] !== ',') i++;
      desc = s.slice(start, i);
      i++;
    }
    if (!url) continue;
    const c: SrcsetCandidate = { url };
    for (const d of desc.trim().split(/\s+/)) {
      const m = /^(\d*\.?\d+)([wx])$/i.exec(d);
      if (m) c[m[2].toLowerCase() as 'w' | 'x'] = parseFloat(m[1]);
    }
    out.push(c);
  }
  return out;
}

/** Largest candidate: by width descriptor if any candidate has one, else by density (no descriptor = 1x). */
export function largestFromSrcset(srcset: string | null | undefined): string | undefined {
  const cands = srcset ? parseSrcset(srcset) : [];
  if (!cands.length) return undefined;
  const byW = cands.some((c) => c.w !== undefined);
  const score = (c: SrcsetCandidate) => (byW ? c.w ?? 0 : c.x ?? 1);
  return cands.reduce((best, c) => (score(c) > score(best) ? c : best)).url;
}

function absolute(raw: string | null | undefined, base: string): string | undefined {
  if (!raw || !raw.trim()) return undefined;
  try {
    const u = new URL(raw.trim(), base);
    if (u.protocol !== 'http:' && u.protocol !== 'https:') return undefined;
    u.hash = '';
    return u.href;
  } catch {
    return undefined;
  }
}

export function cssUrls(value: string): string[] {
  return [...value.matchAll(/url\(\s*(['"]?)(.*?)\1\s*\)/g)].map((m) => m[2]);
}

export function collectResources(doc: Document, maxStyleScan = MAX_STYLE_SCAN): Resource[] {
  const base = doc.baseURI;
  const found = new Map<string, Resource>(); // first kind wins: media, image, link
  const add = (raw: string | null | undefined, kind: Resource['kind']) => {
    const url = absolute(raw, base);
    if (url && !found.has(url)) found.set(url, { url, kind });
    return url;
  };

  for (const el of doc.querySelectorAll<HTMLMediaElement>('video, audio')) add(el.getAttribute('src'), 'media');
  for (const el of doc.querySelectorAll('source')) {
    if (el.parentElement?.tagName === 'PICTURE') {
      add(largestFromSrcset(el.getAttribute('srcset')), 'image');
    } else {
      add(el.getAttribute('src'), 'media');
    }
  }

  for (const img of doc.querySelectorAll('img')) {
    const primary = largestFromSrcset(img.getAttribute('srcset')) ?? img.getAttribute('src');
    for (const raw of [
      primary,
      largestFromSrcset(img.getAttribute('data-srcset')),
      img.getAttribute('data-src'),
      img.getAttribute('data-lazy-src'),
      img.getAttribute('data-original'),
    ]) add(raw, 'image');
    // Natural size belongs to the image actually shown (currentSrc), so only attach it there.
    const shown = absolute(img.currentSrc || img.getAttribute('src'), base);
    const r = shown ? found.get(shown) : undefined;
    if (r && r.kind === 'image' && img.naturalWidth > 0 && img.naturalHeight > 0) {
      r.width = img.naturalWidth;
      r.height = img.naturalHeight;
    }
  }

  const view = doc.defaultView;
  if (view) {
    const all = doc.querySelectorAll('*');
    for (let i = 0; i < all.length && i < maxStyleScan; i++) {
      const bg = view.getComputedStyle(all[i]).backgroundImage;
      if (bg && bg !== 'none') for (const u of cssUrls(bg)) add(u, 'image');
    }
  }

  for (const a of doc.querySelectorAll('a[href]')) add(a.getAttribute('href'), 'link');

  return [...found.values()];
}

const utf8Length = (s: string) => new TextEncoder().encode(s).length;

/**
 * Drops trailing items so that a message serialized as `emptyJson` (with an empty items array)
 * stays at or under `limit` UTF-8 bytes once the items are put into that array.
 */
export function truncateToFit<T>(items: T[], emptyJson: string, limit: number): T[] {
  let size = utf8Length(emptyJson);
  for (let i = 0; i < items.length; i++) {
    size += utf8Length(JSON.stringify(items[i])) + (i > 0 ? 1 : 0);
    if (size > limit) return items.slice(0, i);
  }
  return items;
}
