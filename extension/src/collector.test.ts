// @vitest-environment happy-dom
import { beforeEach, describe, expect, it } from 'vitest';
import { collectResources, largestFromSrcset, parseSrcset, truncateToFit } from './collector';

// The window's own document (a detached one has no defaultView for getComputedStyle).
function page(html: string): Document {
  (window as unknown as { happyDOM: { setURL(u: string): void } }).happyDOM.setURL('https://site.test/dir/page.html');
  document.body.innerHTML = html;
  return document;
}

describe('srcset', () => {
  it('parses width and density descriptors, including URLs with commas', () => {
    expect(parseSrcset('a.jpg 100w, b.jpg 300w,c.jpg 200w')).toEqual([
      { url: 'a.jpg', w: 100 }, { url: 'b.jpg', w: 300 }, { url: 'c.jpg', w: 200 },
    ]);
    expect(parseSrcset('/img/w_100,h_50/x.jpg 1x, /img/w_200,h_100/x.jpg 2x')).toEqual([
      { url: '/img/w_100,h_50/x.jpg', x: 1 }, { url: '/img/w_200,h_100/x.jpg', x: 2 },
    ]);
  });
  it('picks the largest candidate', () => {
    expect(largestFromSrcset('a.jpg 100w, b.jpg 300w, c.jpg 200w')).toBe('b.jpg');
    expect(largestFromSrcset('a.jpg, b.jpg 1.5x, c.jpg 3x')).toBe('c.jpg');
    expect(largestFromSrcset('a.jpg 2x, b.jpg')).toBe('a.jpg');
    expect(largestFromSrcset('')).toBeUndefined();
    expect(largestFromSrcset(null)).toBeUndefined();
  });
});

describe('collectResources', () => {
  let doc: Document;
  beforeEach(() => {
    doc = page(`
      <a href="files/a.zip">a</a>
      <a href="https://other.test/b.pdf#page=2">b</a>
      <a href="files/a.zip#again">dup</a>
      <a href="javascript:void(0)">js</a>
      <a href="mailto:x@y.z">mail</a>
      <img src="small.jpg" srcset="small.jpg 300w, big.jpg 1200w">
      <img src="data:image/gif;base64,R0lGOD" data-src="lazy1.jpg">
      <img data-lazy-src="lazy2.jpg" data-original="lazy3.jpg" data-srcset="l4-1.jpg 1x, l4-2.jpg 2x">
      <img src="blob:https://site.test/123">
      <picture><source srcset="pic-s.webp 400w, pic-l.webp 1600w"><img src="pic.jpg"></picture>
      <video src="/v/movie.mp4"><source src="/v/movie.webm"></video>
      <audio src="song.mp3"></audio>
      <div style="background-image: url('bg.png'), linear-gradient(red, blue)"></div>
      <div style="background-image: url(data:image/png;base64,AAA)"></div>
      <a href="big.jpg">links to an image</a>
    `);
  });

  it('resolves, filters to http(s) and dedupes', () => {
    const urls = collectResources(doc).map((r) => r.url);
    expect(new Set(urls).size).toBe(urls.length);
    for (const u of urls) expect(u).toMatch(/^https?:\/\//);
    expect(urls).toContain('https://site.test/dir/files/a.zip');
    expect(urls).toContain('https://other.test/b.pdf');
    expect(urls.filter((u) => u.includes('a.zip'))).toHaveLength(1);
  });

  it('classifies kinds', () => {
    const kind = Object.fromEntries(collectResources(doc).map((r) => [r.url, r.kind]));
    expect(kind['https://site.test/dir/files/a.zip']).toBe('link');
    expect(kind['https://site.test/v/movie.mp4']).toBe('media');
    expect(kind['https://site.test/v/movie.webm']).toBe('media');
    expect(kind['https://site.test/dir/song.mp3']).toBe('media');
    // A link to an image that is also shown as an image is reported as an image.
    expect(kind['https://site.test/dir/big.jpg']).toBe('image');
  });

  it('takes the largest srcset candidate instead of src', () => {
    const urls = collectResources(doc).map((r) => r.url);
    expect(urls).toContain('https://site.test/dir/big.jpg');
    expect(urls).not.toContain('https://site.test/dir/small.jpg');
    expect(urls).toContain('https://site.test/dir/pic-l.webp');
  });

  it('includes lazy-load attributes', () => {
    const urls = collectResources(doc).map((r) => r.url);
    for (const n of ['lazy1.jpg', 'lazy2.jpg', 'lazy3.jpg', 'l4-2.jpg']) expect(urls).toContain(`https://site.test/dir/${n}`);
  });

  it('reads CSS background images', () => {
    const r = collectResources(doc).find((x) => x.url === 'https://site.test/dir/bg.png');
    expect(r?.kind).toBe('image');
  });

  it('caps the number of elements scanned for background images', () => {
    expect(collectResources(doc, 0).some((x) => x.url.endsWith('bg.png'))).toBe(false);
  });

  it('attaches natural size to the image actually shown', () => {
    const d = page('<img src="shown.jpg">');
    const img = d.querySelector('img')!;
    Object.defineProperty(img, 'naturalWidth', { value: 640 });
    Object.defineProperty(img, 'naturalHeight', { value: 480 });
    expect(collectResources(d)).toEqual([{ url: 'https://site.test/dir/shown.jpg', kind: 'image', width: 640, height: 480 }]);
  });
});

describe('truncateToFit', () => {
  it('keeps the serialized message within the limit', () => {
    const items = Array.from({ length: 1000 }, (_, i) => ({ url: `https://x.test/${i}`, kind: 'link' as const }));
    const msg = { payload: { resources: [] as typeof items } };
    const empty = JSON.stringify(msg);
    const limit = 5000;
    msg.payload.resources = truncateToFit(items, empty, limit);
    const size = new TextEncoder().encode(JSON.stringify(msg)).length;
    expect(size).toBeLessThanOrEqual(limit);
    expect(msg.payload.resources.length).toBeGreaterThan(0);
    expect(msg.payload.resources.length).toBeLessThan(items.length);
    // One more item would not have fit.
    msg.payload.resources = items.slice(0, msg.payload.resources.length + 1);
    expect(new TextEncoder().encode(JSON.stringify(msg)).length).toBeGreaterThan(limit);
  });
  it('returns everything when it fits', () => {
    expect(truncateToFit([1, 2, 3], '{"a":[]}', 100)).toEqual([1, 2, 3]);
  });
});
