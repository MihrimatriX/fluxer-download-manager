// Media sniffing: which network responses are videos/audio worth offering. Pure (no chrome.*), so it runs
// under Vitest. The service worker feeds it response headers; the popup and the video button read the result.

export type MediaKind = 'file' | 'hls' | 'dash';

export interface MediaHit {
  url: string;
  kind: MediaKind;
  /** Lower-case extension or format label: mp4, webm, mp3, m3u8, mpd … */
  ext: string;
  name: string;
  size?: number;
  mime?: string;
  /** When it was first seen (ms). Lists are newest first and a re-fetch (live playlist, range read) keeps its place. */
  at: number;
  /** Frame that requested it (webRequest frameId; 0 = top frame). */
  frameId?: number;
  /** Fetched by script (fetch/XHR: a MediaSource player), not by a <video>/<audio> element. */
  xhr?: boolean;
  /** HLS master playlist: the tallest variant (1080 for 1920x1080) and how many variants it offers. */
  height?: number;
  variants?: number;
}

const FILE_EXTS = new Set([
  'mp4', 'm4v', 'webm', 'mkv', 'mov', 'avi', 'wmv', 'flv', '3gp', 'ogv',
  'mp3', 'm4a', 'aac', 'ogg', 'oga', 'opus', 'flac', 'wav', 'weba',
]);
/** Pieces of a stream, never useful on their own. */
const SEGMENT_EXTS = new Set(['ts', 'm4s', 'cmfv', 'cmfa', 'mp4a', 'vtt', 'webvtt', 'key', 'srt']);
/** Progressive files smaller than this are UI sounds, previews and ads. */
export const MIN_FILE_BYTES = 300 * 1024;
export const MAX_HITS = 40;

const extOf = (u: URL): string => {
  const last = u.pathname.split('/').pop() ?? '';
  const dot = last.lastIndexOf('.');
  return dot > 0 ? last.slice(dot + 1).toLowerCase() : '';
};

function nameOf(u: URL, ext: string): string {
  const last = u.pathname.split('/').pop() ?? '';
  let name = last;
  try { name = decodeURIComponent(last); } catch { /* keep raw */ }
  return name || `${u.hostname}.${ext}`;
}

/** Total size from Content-Length, or from Content-Range on a 206 ("bytes 0-1023/52428800"). */
export function totalSize(contentLength?: string, contentRange?: string): number | undefined {
  const m = contentRange ? /\/(\d+)\s*$/.exec(contentRange) : null;
  if (m) return Number(m[1]);
  const n = contentLength ? Number(contentLength) : NaN;
  return Number.isFinite(n) && n > 0 ? n : undefined;
}

/** Query parameters that only select a byte range of the same file. */
const RANGE_PARAMS = /^(range|bytestart|byteend|bytes)$/i;

/** Identity of a media resource: the URL without range selectors, so 50 range requests are one entry. */
export function mediaKey(url: string): string {
  try {
    const u = new URL(url);
    for (const k of [...u.searchParams.keys()]) if (RANGE_PARAMS.test(k)) u.searchParams.delete(k);
    u.hash = '';
    return u.href;
  } catch {
    return url;
  }
}

export interface ResponseInfo {
  url: string;
  status: number;
  contentType?: string;
  contentLength?: string;
  contentRange?: string;
  /** webRequest resource type: 'media', 'xmlhttprequest', … */
  type?: string;
  frameId?: number;
  now?: number;
}

/** A media hit for this response, or null when it is not something to offer. */
export function classifyMedia(r: ResponseInfo): MediaHit | null {
  if (r.status !== 200 && r.status !== 206) return null;
  let u: URL;
  try { u = new URL(r.url); } catch { return null; }
  if (u.protocol !== 'http:' && u.protocol !== 'https:') return null;
  // YouTube & co. stream through range requests on one endpoint; the page URL (yt-dlp) is the way there.
  if (/(^|\.)googlevideo\.com$/i.test(u.hostname) || u.pathname.endsWith('/videoplayback')) return null;

  const mime = r.contentType?.split(';')[0].trim().toLowerCase() || undefined;
  const ext = extOf(u);
  const at = r.now ?? Date.now();
  const where = { at, ...(r.frameId !== undefined && { frameId: r.frameId }) };
  if (SEGMENT_EXTS.has(ext) || mime === 'video/mp2t' || mime === 'video/iso.segment') return null;

  if (mime?.includes('mpegurl') || ext === 'm3u8')
    return { url: r.url, kind: 'hls', ext: 'm3u8', name: nameOf(u, 'm3u8'), mime, ...where };
  if (mime === 'application/dash+xml' || ext === 'mpd')
    return { url: r.url, kind: 'dash', ext: 'mpd', name: nameOf(u, 'mpd'), mime, ...where };

  const isAv = !!mime && (mime.startsWith('video/') || mime.startsWith('audio/'));
  // A known media extension served as octet-stream still counts; text/html never does.
  const byExt = FILE_EXTS.has(ext) && (!mime || mime === 'application/octet-stream' || mime === 'binary/octet-stream');
  if (!isAv && !byExt) return null;

  const size = totalSize(r.contentLength, r.contentRange);
  if (size !== undefined && size < MIN_FILE_BYTES) return null;
  const label = FILE_EXTS.has(ext) ? ext : (mime?.split('/')[1] ?? 'media');
  return {
    url: r.url, kind: 'file', ext: label, name: nameOf(u, label), size, mime, ...where,
    ...(r.type === 'xmlhttprequest' && { xhr: true }),
  };
}

/** What the service worker keeps per tab (storage.session, `media:<tabId>`). */
export interface TabMedia {
  pageUrl?: string;
  hits: MediaHit[];
  /** mediaKeys never listed again: variant and rendition playlists of a master that is listed. */
  hidden?: string[];
  /** segmentTemplate()s of script-fetched files that turned out to be pieces of a stream. */
  segments?: string[];
}
const MAX_HIDDEN = 400, MAX_SEGMENTS = 60;

/**
 * Shape of a URL with its numbers taken out: host, path with digit runs as '#', and the query's keys.
 * fMP4/CMAF players fetch "seg-1.mp4", "seg-2.mp4" … (or "media.mp4?segment=1") one by one; those share it.
 */
export function segmentTemplate(url: string): string | undefined {
  try {
    const u = new URL(url);
    const keys = [...new Set(u.searchParams.keys())].sort().join('&');
    const parts = u.pathname.split('/');
    const last = parts.pop() ?? '';
    const dot = last.lastIndexOf('.');
    // The extension keeps its digits (m4a, mp4).
    const [stem, ext] = dot > 0 ? [last.slice(0, dot), last.slice(dot)] : [last, ''];
    const path = [...parts, stem].join('/').replace(/\d+/g, '#') + ext;
    return `${u.host}${path}?${keys}`;
  } catch {
    return undefined;
  }
}

const pathOf = (url: string) => { try { return new URL(url).pathname; } catch { return url; } };
const capped = (list: string[], max: number) => list.slice(Math.max(0, list.length - max));

/**
 * Adds a response to a tab's list, or returns null when the list would not change (a live playlist
 * refreshed, the next range of a known file, a hidden variant, another segment), so callers skip the write.
 * - one entry per mediaKey: a known entry keeps its place and first-seen time and only learns a size;
 * - two script-fetched files of the same segmentTemplate are a stream's pieces: both go, the rest are ignored.
 */
export function addHit(m: TabMedia, hit: MediaHit, max = MAX_HITS): TabMedia | null {
  const key = mediaKey(hit.url);
  if (m.hidden?.includes(key)) return null;
  const i = m.hits.findIndex((h) => mediaKey(h.url) === key);
  if (i >= 0) {
    const old = m.hits[i];
    if (hit.size === undefined || hit.size === old.size) return null;
    const hits = [...m.hits];
    hits[i] = { ...old, size: hit.size };
    return { ...m, hits };
  }
  if (hit.kind === 'file' && hit.xhr) {
    const tpl = segmentTemplate(hit.url);
    if (tpl && m.segments?.includes(tpl)) return null;
    // The same file under a new token has the same path and size; segments differ in at least one.
    const sibling = tpl && m.hits.find((h) => h.kind === 'file' && h.xhr && segmentTemplate(h.url) === tpl
      && !(pathOf(h.url) === pathOf(hit.url) && h.size !== undefined && h.size === hit.size));
    if (sibling) {
      return { ...m, hits: m.hits.filter((h) => h !== sibling), segments: capped([...(m.segments ?? []), tpl], MAX_SEGMENTS) };
    }
  }
  return { ...m, hits: [hit, ...m.hits].slice(0, max) };
}

export interface HlsMaster {
  /** Variant playlists (EXT-X-STREAM-INF), absolute. */
  variants: string[];
  /** Audio/subtitle renditions (EXT-X-MEDIA) and I-frame playlists, absolute. */
  renditions: string[];
  /** Tallest RESOLUTION among the variants. */
  height?: number;
}

/** The variants of an HLS master playlist, or null for a media playlist (segments) or anything else. */
export function parseHlsMaster(text: string, base: string): HlsMaster | null {
  if (!text.trimStart().startsWith('#EXTM3U')) return null;
  const abs = (s: string) => { try { return new URL(s, base).href; } catch { return undefined; } };
  const variants: string[] = [], renditions: string[] = [];
  let height: number | undefined;
  let uriNext = false;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line) continue;
    if (line.startsWith('#EXT-X-STREAM-INF')) {
      uriNext = true;
      const res = /RESOLUTION=\d+x(\d+)/i.exec(line);
      if (res) height = Math.max(height ?? 0, Number(res[1]));
    } else if (line.startsWith('#EXT-X-MEDIA') || line.startsWith('#EXT-X-I-FRAME-STREAM-INF')) {
      const uri = /URI="([^"]+)"/.exec(line);
      const u = uri && abs(uri[1]);
      if (u) renditions.push(u);
    } else if (!line.startsWith('#') && uriNext) {
      const u = abs(line);
      if (u) variants.push(u);
      uriNext = false;
    }
  }
  return variants.length ? { variants, renditions, ...(height && { height }) } : null;
}

/** A master playlist was read: label it, drop its variants/renditions from the list and keep them out. */
export function applyMaster(m: TabMedia, masterUrl: string, master: HlsMaster): TabMedia {
  const masterKey = mediaKey(masterUrl);
  const children = new Set([...master.variants, ...master.renditions].map(mediaKey));
  children.delete(masterKey);
  const hidden = new Set(m.hidden);
  const labelled = (h: MediaHit) => h.variants === master.variants.length && h.height === master.height;
  const done = [...children].every((k) => hidden.has(k))
    && m.hits.every((h) => !children.has(mediaKey(h.url)) && (mediaKey(h.url) !== masterKey || labelled(h)));
  if (done) return m;
  const hits = m.hits
    .filter((h) => !children.has(mediaKey(h.url)))
    .map((h) => (mediaKey(h.url) === masterKey ? { ...h, variants: master.variants.length, height: master.height } : h));
  return { ...m, hits, hidden: capped([...new Set([...hidden, ...children])], MAX_HIDDEN) };
}

/**
 * What the in-page video button (or the context menu on a video) should send for a <video> whose source is `src`.
 * - a real file URL: that file; a manifest URL: that stream
 * - blob:/MediaSource players: what the same frame loaded, else what the tab loaded: the newest HLS master
 *   (then any HLS, then DASH), else the largest video file (ads are short), else the page for yt-dlp.
 */
export function pickForVideo(src: string | undefined, hits: readonly MediaHit[], frameId?: number):
  { type: 'file'; url: string } | { type: 'stream'; url: string } | { type: 'page' } {
  if (src && /^https?:/i.test(src)) {
    const known = hits.find((h) => mediaKey(h.url) === mediaKey(src));
    if (known) return known.kind === 'file' ? { type: 'file', url: src } : { type: 'stream', url: known.url };
    const direct = classifyMedia({ url: src, status: 200 });
    if (direct?.kind === 'file') return { type: 'file', url: src };
    if (direct) return { type: 'stream', url: src };
  }
  const own = frameId === undefined ? [] : hits.filter((h) => h.frameId === frameId);
  const pool = own.length ? own : hits;
  const stream = pool.find((h) => h.kind === 'hls' && h.variants)
    ?? pool.find((h) => h.kind === 'hls') ?? pool.find((h) => h.kind === 'dash');
  if (stream) return { type: 'stream', url: stream.url };
  const files = pool.filter((h) => h.kind === 'file' && h.mime?.startsWith('video/'));
  const file = files.reduce<MediaHit | undefined>((best, h) => (!best || (h.size ?? 0) > (best.size ?? 0) ? h : best), undefined);
  if (file) return { type: 'file', url: file.url };
  return { type: 'page' };
}
