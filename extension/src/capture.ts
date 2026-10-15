// Pure capture logic: no chrome.* calls here, so it runs under Vitest.
import { envelope, type CookieDto, type DownloadAdd, type Reply, type Request } from './protocol';

export interface Settings {
  captureEnabled: boolean;
  /** Files smaller than this (when size is known) stay in the browser. 0 = capture everything. */
  minSizeMiB: number;
  /** "Download with Fluxer" button over videos on web pages. */
  showVideoButton: boolean;
  /** Count of videos found on the tab, on the toolbar icon. */
  showBadge: boolean;
  /** Hosts whose downloads stay in the browser ("Turn off on this site" in the popup). */
  disabledSites: string[];
}
export const DEFAULT_SETTINGS: Settings = { captureEnabled: true, minSizeMiB: 1, showVideoButton: true, showBadge: true, disabledSites: [] };

export type DownloadItemLike = Pick<
  chrome.downloads.DownloadItem,
  'url' | 'finalUrl' | 'state' | 'totalBytes' | 'filename' | 'mime' | 'referrer' | 'incognito' | 'byExtensionId'
>;

export interface CaptureContext {
  settings: Settings;
  altHeld: boolean;
  /** We re-started this URL in the browser ourselves. */
  bypassed: boolean;
  /** The URL was requested with POST in the last ~30 s. */
  postInitiated: boolean;
  ownExtensionId?: string;
}

export type Decision = 'capture' | 'torrent' | 'disabled' | 'site' | 'alt' | 'bypass' | 'scheme' | 'complete' | 'post' | 'small';

const TORRENT_RE = /\.torrent(?:[?#]|$)/i;

/** Browser-reported mime or a .torrent name/URL. */
export const isTorrentLike = (item: Pick<DownloadItemLike, 'mime' | 'url' | 'finalUrl' | 'filename'>): boolean =>
  item.mime === 'application/x-bittorrent' || [item.url, item.finalUrl, item.filename].some((s) => !!s && TORRENT_RE.test(s));

export const isHttp = (url: string | undefined): boolean => !!url && /^https?:\/\//i.test(url);

/** URLs Fluxer can download from a context menu or a pasted link. */
export const isAddable = (url: string | undefined): url is string => !!url && /^(https?|magnet):/i.test(url);

/** Lower-case host of an http(s) URL. */
export function hostOf(url: string | undefined): string | undefined {
  if (!isHttp(url)) return undefined;
  try { return new URL(url!).hostname.toLowerCase() || undefined; } catch { return undefined; }
}

// ponytail: the site is the page that linked the download (referrer), else the download's own host. A page
// with a no-referrer policy is matched by its file host; pass the tab host to decide() if that matters.
export const siteOf = (item: Pick<DownloadItemLike, 'referrer' | 'url'>): string | undefined =>
  hostOf(item.referrer) ?? hostOf(item.url);

/** Adds or removes `host` from the per-site exclusion list. */
export const toggleSite = (sites: readonly string[], host: string): string[] =>
  sites.includes(host) ? sites.filter((h) => h !== host) : [...sites, host];

export function decide(item: DownloadItemLike, ctx: CaptureContext): Decision {
  if (!ctx.settings.captureEnabled) return 'disabled';
  const site = siteOf(item);
  if (site && ctx.settings.disabledSites?.includes(site)) return 'site';
  if (ctx.altHeld) return 'alt';
  if (ctx.bypassed || (ctx.ownExtensionId && item.byExtensionId === ctx.ownExtensionId)) return 'bypass';
  if (!isHttp(item.url) || (item.finalUrl && !isHttp(item.finalUrl))) return 'scheme';
  // .torrent files are tiny and often behind Cloudflare or one-time links: the browser fetches them, Fluxer reads the file.
  if (isTorrentLike(item)) return 'torrent';
  if (item.state === 'complete') return 'complete';
  if (ctx.postInitiated) return 'post';
  if (item.totalBytes > 0 && item.totalBytes < ctx.settings.minSizeMiB * 1024 * 1024) return 'small';
  return 'capture';
}

/**
 * Ask on the page (the bottom-right toast) before handing a captured download to Fluxer? Only when it came
 * from the http(s) tab we would show the toast in: a referrer from another origin means another tab or window.
 */
export function canAsk(item: Pick<DownloadItemLike, 'referrer'>, tab: { id?: number; url?: string } | undefined): boolean {
  if (tab?.id === undefined || !isHttp(tab.url)) return false;
  if (!item.referrer) return true;
  try { return new URL(item.referrer).origin === new URL(tab.url!).origin; } catch { return false; }
}

/** The toast's answer: anything but an explicit "leave it in the browser" (countdown, closed page, no reply) sends. */
export const askAnswer = (reply: unknown): 'send' | 'browser' => (reply === 'browser' ? 'browser' : 'send');

/** URL set whose entries expire after `ttlMs`. Used for POST URLs and our own re-started downloads. */
export class RecentUrls {
  private readonly expiry = new Map<string, number>();
  constructor(private readonly ttlMs: number, private readonly now: () => number = Date.now) {}

  add(url: string): void {
    const t = this.now();
    for (const [k, exp] of this.expiry) if (exp <= t) this.expiry.delete(k);
    this.expiry.set(url, t + this.ttlMs);
  }
  has(url: string | undefined): boolean {
    const exp = url === undefined ? undefined : this.expiry.get(url);
    return exp !== undefined && exp > this.now();
  }
  /** has() + delete: each entry is consumed by one download. */
  take(url: string | undefined): boolean {
    const hit = this.has(url);
    if (hit) this.expiry.delete(url!);
    return hit;
  }
}

export const basename = (path: string | undefined): string | undefined =>
  path?.split(/[\\/]/).pop() || undefined;

export function toCookieDto(c: chrome.cookies.Cookie): CookieDto {
  const dto: CookieDto = {
    name: c.name, value: c.value, domain: c.domain, path: c.path,
    secure: c.secure, httpOnly: c.httpOnly,
  };
  if (c.expirationDate !== undefined) dto.expirationDate = c.expirationDate;
  return dto;
}

export function buildInterceptMessage(
  item: DownloadItemLike,
  ctx: { cookies: CookieDto[]; userAgent: string; tabUrl?: string },
): DownloadAdd {
  return envelope<DownloadAdd>('download.add', {
    url: item.url,
    finalUrl: item.finalUrl || undefined,
    fileName: basename(item.filename),
    mime: item.mime || undefined,
    totalBytes: item.totalBytes > 0 ? item.totalBytes : undefined,
    source: 'intercept',
    tabUrl: ctx.tabUrl,
    referrer: item.referrer || undefined,
    userAgent: ctx.userAgent,
    cookies: ctx.cookies,
    incognito: item.incognito,
  });
}

export function buildTorrentFileMessage(
  item: DownloadItemLike,
  ctx: { userAgent: string; tabUrl?: string },
): DownloadAdd {
  return envelope<DownloadAdd>('download.add', {
    url: item.finalUrl || item.url,
    fileName: basename(item.filename),
    torrentPath: item.filename,
    source: 'torrent-file',
    tabUrl: ctx.tabUrl,
    referrer: item.referrer || undefined,
    userAgent: ctx.userAgent,
    cookies: [],
    incognito: item.incognito,
  });
}

/** Why the app declined a download (undefined when it did not decline). */
export const rejectReason = (reply: Reply | undefined): string | undefined =>
  reply?.type === 'download.add.result' && reply.payload?.status === 'rejected' ? reply.payload.reason : undefined;

/** What to do with a reply. `undefined` means sendNativeMessage failed (host missing, crashed, …). */
export type Outcome = 'ok' | 'unreachable' | 'failed' | 'update';

/**
 * `expect`: the reply type the request must get (e.g. 'download.add.result'); anything else is 'failed'.
 * Without it (page.* — no reply type is defined for them yet) any non-error reply counts as 'ok'.
 */
export function outcomeOf(reply: Reply | undefined, expect?: Reply['type']): Outcome {
  if (!reply || typeof reply !== 'object') return 'unreachable';
  if (reply.type === 'error') {
    if (reply.payload?.code === 'protocol-mismatch') return 'update';
    // An older app that does not know a newer message type yet.
    if (reply.payload?.code === 'invalid-message' && reply.payload.message === 'unknown type') return 'update';
    return reply.payload?.code === 'app-unavailable' ? 'unreachable' : 'failed';
  }
  if (expect && reply.type !== expect) return 'failed';
  if (reply.type === 'download.add.result') return reply.payload?.status === 'accepted' ? 'ok' : 'failed';
  return 'ok';
}

/** Files a request handed to Fluxer, for the popup's "sent today" line (0 when the app did not take them). */
export function sentCount(msg: Request, reply: Reply | undefined): number {
  switch (msg.type) {
    case 'download.add': return outcomeOf(reply, 'download.add.result') === 'ok' ? 1 : 0;
    case 'download.batch': return outcomeOf(reply, 'download.add.result') === 'ok' ? msg.payload.items.length : 0;
    case 'page.send': return outcomeOf(reply) === 'ok' ? 1 : 0;
    default: return 0;
  }
}

export interface DailyCount { day: string; count: number }
/** Local calendar day, e.g. "2026-10-16". */
export const localDay = (d: Date): string =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
/** Adds `n` to today's count; a count from an earlier day starts over. */
export const bumpDaily = (prev: DailyCount | undefined, day: string, n: number): DailyCount =>
  ({ day, count: (prev?.day === day ? prev.count : 0) + n });
/** Today's count (0 when the stored one is from an earlier day). */
export const countToday = (stored: DailyCount | undefined, day: string): number => (stored?.day === day ? stored.count : 0);

/** "8.4 MB" in the given number format (1024-based, like the app). */
export function formatBytes(n: number | undefined, num: Intl.NumberFormat): string {
  if (n === undefined || n < 0) return '';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = n, i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${num.format(v)} ${units[i]}`;
}
