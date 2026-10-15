// Pure capture logic: no chrome.* calls here, so it runs under Vitest.
import { envelope, type CookieDto, type DownloadAdd, type Reply } from './protocol';

export interface Settings {
  captureEnabled: boolean;
  /** Files smaller than this (when size is known) stay in the browser. 0 = capture everything. */
  minSizeMiB: number;
}
export const DEFAULT_SETTINGS: Settings = { captureEnabled: true, minSizeMiB: 1 };

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

export type Decision = 'capture' | 'disabled' | 'alt' | 'bypass' | 'scheme' | 'complete' | 'post' | 'small';

export const isHttp = (url: string | undefined): boolean => !!url && /^https?:\/\//i.test(url);

export function decide(item: DownloadItemLike, ctx: CaptureContext): Decision {
  if (!ctx.settings.captureEnabled) return 'disabled';
  if (ctx.altHeld) return 'alt';
  if (ctx.bypassed || (ctx.ownExtensionId && item.byExtensionId === ctx.ownExtensionId)) return 'bypass';
  if (!isHttp(item.url) || (item.finalUrl && !isHttp(item.finalUrl))) return 'scheme';
  if (item.state === 'complete') return 'complete';
  if (ctx.postInitiated) return 'post';
  if (item.totalBytes > 0 && item.totalBytes < ctx.settings.minSizeMiB * 1024 * 1024) return 'small';
  return 'capture';
}

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
    return reply.payload?.code === 'app-unavailable' ? 'unreachable' : 'failed';
  }
  if (expect && reply.type !== expect) return 'failed';
  if (reply.type === 'download.add.result') return reply.payload?.status === 'accepted' ? 'ok' : 'failed';
  return 'ok';
}
