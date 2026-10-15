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
  /** Detection time (ms), newest first in lists. */
  at: number;
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
  if (SEGMENT_EXTS.has(ext) || mime === 'video/mp2t' || mime === 'video/iso.segment') return null;

  if (mime?.includes('mpegurl') || ext === 'm3u8')
    return { url: r.url, kind: 'hls', ext: 'm3u8', name: nameOf(u, 'm3u8'), mime, at };
  if (mime === 'application/dash+xml' || ext === 'mpd')
    return { url: r.url, kind: 'dash', ext: 'mpd', name: nameOf(u, 'mpd'), mime, at };

  const isAv = !!mime && (mime.startsWith('video/') || mime.startsWith('audio/'));
  // A known media extension served as octet-stream still counts; text/html never does.
  const byExt = FILE_EXTS.has(ext) && (!mime || mime === 'application/octet-stream' || mime === 'binary/octet-stream');
  if (!isAv && !byExt) return null;

  const size = totalSize(r.contentLength, r.contentRange);
  if (size !== undefined && size < MIN_FILE_BYTES) return null;
  const label = FILE_EXTS.has(ext) ? ext : (mime?.split('/')[1] ?? 'media');
  return { url: r.url, kind: 'file', ext: label, name: nameOf(u, label), size, mime, at };
}

/** Adds a hit to a tab's list: one entry per mediaKey (refreshed, size kept), newest first, capped. */
export function mergeHit(list: readonly MediaHit[], hit: MediaHit, max = MAX_HITS): MediaHit[] {
  const key = mediaKey(hit.url);
  const old = list.find((h) => mediaKey(h.url) === key);
  const merged: MediaHit = old ? { ...old, ...hit, size: hit.size ?? old.size } : hit;
  return [merged, ...list.filter((h) => h !== old)].slice(0, max);
}

/**
 * What the in-page video button should send for a <video> whose source is `src`.
 * - a real file URL: that file
 * - blob:/MediaSource players: the newest stream manifest seen on the tab (HLS before DASH), else
 *   the newest progressive file, else the page itself for yt-dlp to work out.
 */
export function pickForVideo(src: string | undefined, hits: readonly MediaHit[]):
  { type: 'file'; url: string } | { type: 'stream'; url: string } | { type: 'page' } {
  if (src && /^https?:/i.test(src)) {
    const direct = classifyMedia({ url: src, status: 200 });
    if (direct?.kind === 'file') return { type: 'file', url: src };
    if (direct) return { type: 'stream', url: src };
  }
  const stream = hits.find((h) => h.kind === 'hls') ?? hits.find((h) => h.kind === 'dash');
  if (stream) return { type: 'stream', url: stream.url };
  const file = hits.find((h) => h.kind === 'file' && h.mime?.startsWith('video/'));
  if (file) return { type: 'file', url: file.url };
  return { type: 'page' };
}
