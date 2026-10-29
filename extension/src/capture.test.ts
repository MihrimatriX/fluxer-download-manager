import { describe, expect, it } from 'vitest';
import {
  DEFAULT_SETTINGS, PAUSE_MS, RecentUrls, askAnswer, basename, bumpDaily, buildInterceptMessage, buildTorrentFileMessage, canAsk, countToday,
  decide, extOfPath, formatBytes, hostOf, isPaused, isTorrentLike, localDay, outcomeOf, parseTypes, rejectReason, sentCount, toCookieDto, toggleSite,
  typesOf, versionSkew,
  type CaptureContext, type DownloadItemLike,
} from './capture';
import { PROTOCOL_VERSION, type Reply, type Request } from './protocol';

const MiB = 1024 * 1024;
const item = (o: Partial<DownloadItemLike> = {}): DownloadItemLike => ({
  url: 'https://example.com/file.zip',
  finalUrl: 'https://cdn.example.com/file.zip',
  state: 'in_progress',
  totalBytes: 50 * MiB,
  filename: 'C:\\Users\\me\\Downloads\\file.zip',
  mime: 'application/zip',
  referrer: 'https://example.com/',
  incognito: false,
  ...o,
});
const ctx = (o: Partial<CaptureContext> = {}): CaptureContext => ({
  settings: DEFAULT_SETTINGS, altHeld: false, bypassed: false, postInitiated: false, ownExtensionId: 'self', ...o,
});

describe('decide', () => {
  it('captures a normal http(s) download', () => {
    expect(decide(item(), ctx())).toBe('capture');
    expect(decide(item({ url: 'http://x.test/a', finalUrl: '' }), ctx())).toBe('capture');
  });
  it('captures when size is unknown', () => {
    expect(decide(item({ totalBytes: 0 }), ctx())).toBe('capture');
    expect(decide(item({ totalBytes: -1 }), ctx())).toBe('capture');
  });

  it.each(['blob:https://example.com/uuid', 'data:text/plain,hi', 'file:///C:/a.txt', 'ftp://x/y', 'filesystem:https://x/y'])(
    'leaves non-http scheme %s', (url) => {
      expect(decide(item({ url, finalUrl: url }), ctx())).toBe('scheme');
    });
  it('leaves when the final URL is not http(s)', () => {
    expect(decide(item({ finalUrl: 'data:,x' }), ctx())).toBe('scheme');
  });
  it('leaves completed items', () => {
    expect(decide(item({ state: 'complete' }), ctx())).toBe('complete');
  });
  it('leaves POST-initiated downloads', () => {
    expect(decide(item(), ctx({ postInitiated: true }))).toBe('post');
  });
  it('leaves files below the threshold when size is known', () => {
    expect(decide(item({ totalBytes: MiB - 1 }), ctx())).toBe('small');
    expect(decide(item({ totalBytes: MiB }), ctx())).toBe('capture');
    expect(decide(item({ totalBytes: 3 * MiB }), ctx({ settings: { ...DEFAULT_SETTINGS, minSizeMiB: 5 } }))).toBe('small');
    expect(decide(item({ totalBytes: 10 }), ctx({ settings: { ...DEFAULT_SETTINGS, minSizeMiB: 0 } }))).toBe('capture');
  });
  it('leaves everything when capture is disabled', () => {
    expect(decide(item(), ctx({ settings: { ...DEFAULT_SETTINGS, captureEnabled: false } }))).toBe('disabled');
  });
  it('leaves everything while capture is paused, and captures again once the pause ends', () => {
    const now = 1_700_000_000_000;
    const paused = { ...DEFAULT_SETTINGS, pausedUntil: now + PAUSE_MS };
    expect(decide(item(), ctx({ settings: paused, now }))).toBe('paused');
    expect(decide(item(), ctx({ settings: paused, now: now + PAUSE_MS }))).toBe('capture');
    expect(isPaused(paused, now + PAUSE_MS - 1)).toBe(true);
    expect(isPaused(DEFAULT_SETTINGS, now)).toBe(false);
    // the off switch still wins: it says why the download stayed
    expect(decide(item(), ctx({ settings: { ...paused, captureEnabled: false }, now }))).toBe('disabled');
  });
  it('leaves while Alt is held', () => {
    expect(decide(item(), ctx({ altHeld: true }))).toBe('alt');
  });
  it('leaves our own re-started downloads', () => {
    expect(decide(item(), ctx({ bypassed: true }))).toBe('bypass');
    expect(decide(item({ byExtensionId: 'self' }), ctx())).toBe('bypass');
    expect(decide(item({ byExtensionId: 'other' }), ctx())).toBe('capture');
  });
});

describe('decide: pages and file types', () => {
  const skip = (...types: string[]) => ctx({ settings: { ...DEFAULT_SETTINGS, skipTypes: types } });
  it('leaves web pages in the browser (Fluxer would hand them back after a probe)', () => {
    expect(decide(item({ mime: 'text/html; charset=utf-8' }), ctx())).toBe('page');
    expect(decide(item({ mime: 'application/xhtml+xml' }), ctx())).toBe('page');
    expect(decide(item({ mime: 'text/html-sandboxed' }), ctx())).toBe('capture');
  });
  it('leaves the types the user listed, known by name, URL or mime', () => {
    expect(decide(item(), skip('zip'))).toBe('type');
    expect(decide(item({ filename: '', url: 'https://x.com/get?id=1', finalUrl: '', mime: 'application/pdf' }), skip('pdf'))).toBe('type');
    expect(decide(item({ filename: 'C:\\d\\photo.JPEG', mime: '' }), skip('jpg'))).toBe('type');
    expect(decide(item(), skip('pdf', 'exe'))).toBe('capture');
  });
  it('typesOf / extOfPath / parseTypes', () => {
    expect(typesOf(item())).toEqual(['zip']);
    expect(extOfPath('https://x.com/a.tar.gz?x=1.exe')).toBe('gz');
    expect(extOfPath('https://x.com/v1.2/file')).toBe('');
    expect(parseTypes(' PDF, .jpg;*.png  jpeg  bad!  ')).toEqual(['pdf', 'jpg', 'png']);
  });
});

describe('RecentUrls', () => {
  it('keeps a value per URL', () => {
    let now = 0;
    const tabs = new RecentUrls<number>(1000, () => now);
    tabs.add('https://a/', 7);
    expect(tabs.get('https://a/')).toBe(7);
    now = 1000;
    expect(tabs.get('https://a/')).toBeUndefined();
  });
  it('expires entries and take() consumes them', () => {
    let now = 0;
    const s = new RecentUrls(30_000, () => now);
    s.add('https://a/');
    expect(s.has('https://a/')).toBe(true);
    expect(s.has(undefined)).toBe(false);
    now = 29_999;
    expect(s.take('https://a/')).toBe(true);
    expect(s.take('https://a/')).toBe(false);
    s.add('https://b/');
    now += 30_000;
    expect(s.has('https://b/')).toBe(false);
  });
});

describe('message construction', () => {
  it('builds download.add from a DownloadItem', () => {
    const cookies = [{ name: 'sid', value: '1', domain: '.example.com', path: '/', secure: true, httpOnly: true }];
    const msg = buildInterceptMessage(item(), { cookies, userAgent: 'UA', tabUrl: 'https://example.com/page' });
    expect(msg.protocolVersion).toBe(PROTOCOL_VERSION);
    expect(msg.type).toBe('download.add');
    expect(msg.id).toMatch(/^[0-9a-f-]{36}$/);
    expect(msg.payload).toEqual({
      url: 'https://example.com/file.zip',
      finalUrl: 'https://cdn.example.com/file.zip',
      fileName: 'file.zip',
      mime: 'application/zip',
      totalBytes: 50 * MiB,
      source: 'intercept',
      tabUrl: 'https://example.com/page',
      referrer: 'https://example.com/',
      userAgent: 'UA',
      cookies,
      incognito: false,
    });
  });
  it('omits unknown size, empty filename/mime/referrer', () => {
    const msg = buildInterceptMessage(item({ totalBytes: -1, filename: '', mime: '', referrer: '', incognito: true }), { cookies: [], userAgent: 'UA' });
    const json = JSON.parse(JSON.stringify(msg));
    expect(json.payload).not.toHaveProperty('totalBytes');
    expect(json.payload).not.toHaveProperty('fileName');
    expect(json.payload).not.toHaveProperty('mime');
    expect(json.payload).not.toHaveProperty('referrer');
    expect(json.payload.incognito).toBe(true);
  });
  it('basename handles both separators', () => {
    expect(basename('/home/u/Downloads/a.iso')).toBe('a.iso');
    expect(basename('C:\\x\\y\\b.zip')).toBe('b.zip');
    expect(basename('c.txt')).toBe('c.txt');
    expect(basename(undefined)).toBeUndefined();
  });
  it('maps cookies, dropping expirationDate for session cookies', () => {
    const base = { name: 'a', value: 'b', domain: 'x.com', path: '/', secure: false, httpOnly: false, hostOnly: true, session: true, storeId: '0', sameSite: 'lax' } as chrome.cookies.Cookie;
    expect(toCookieDto(base)).toEqual({ name: 'a', value: 'b', domain: 'x.com', path: '/', secure: false, httpOnly: false });
    expect(toCookieDto({ ...base, session: false, expirationDate: 123 }).expirationDate).toBe(123);
  });
});

describe('outcomeOf', () => {
  const env = (type: string, payload: object) => ({ protocolVersion: 1, id: 'x', type, payload }) as Reply;
  it('classifies replies', () => {
    expect(outcomeOf(undefined)).toBe('unreachable');
    expect(outcomeOf(env('download.add.result', { status: 'accepted' }), 'download.add.result')).toBe('ok');
    expect(outcomeOf(env('download.add.result', { status: 'rejected' }), 'download.add.result')).toBe('failed');
    expect(outcomeOf(env('error', { code: 'protocol-mismatch', minProtocolVersion: 2 }))).toBe('update');
    expect(outcomeOf(env('error', { code: 'app-unavailable' }))).toBe('unreachable');
    expect(outcomeOf(env('error', { code: 'internal' }))).toBe('failed');
    expect(outcomeOf(env('pong', { appVersion: '1' }), 'download.add.result')).toBe('failed');
    expect(outcomeOf(env('pong', { appVersion: '1' }))).toBe('ok');
  });
});

describe('torrents', () => {
  it('recognises torrents by mime or by .torrent in url/filename', () => {
    expect(isTorrentLike(item({ mime: 'application/x-bittorrent', url: 'https://t.test/download/f3a8', finalUrl: '', filename: '' }))).toBe(true);
    expect(isTorrentLike(item({ mime: '', url: 'https://t.test/a.torrent?key=1', finalUrl: '' }))).toBe(true);
    expect(isTorrentLike(item({ mime: '', filename: 'C:\\d\\x.TORRENT' }))).toBe(true);
    expect(isTorrentLike(item({ url: 'https://t.test/xtorrent', finalUrl: '', filename: '' }))).toBe(false);
    expect(isTorrentLike(item())).toBe(false);
  });
  it('lets the browser fetch small, POST-initiated or completed .torrent files', () => {
    const t = { mime: 'application/x-bittorrent', totalBytes: 40_000 };
    expect(decide(item(t), ctx())).toBe('torrent');
    expect(decide(item(t), ctx({ postInitiated: true }))).toBe('torrent');
    expect(decide(item({ ...t, state: 'complete' }), ctx())).toBe('torrent');
    expect(decide(item(t), ctx({ altHeld: true }))).toBe('alt');
  });
  it('builds the torrent-file message with the saved path', () => {
    const m = buildTorrentFileMessage(item({ filename: 'C:\\D\\ubuntu.torrent' }), { userAgent: 'UA' });
    expect(m.type).toBe('download.add');
    expect(m.payload.source).toBe('torrent-file');
    expect(m.payload.torrentPath).toBe('C:\\D\\ubuntu.torrent');
    expect(m.payload.fileName).toBe('ubuntu.torrent');
  });
  it('reads the reject reason', () => {
    const r = (status: 'accepted' | 'rejected', reason?: string) =>
      ({ protocolVersion: PROTOCOL_VERSION, id: '1', type: 'download.add.result', payload: { status, reason } }) as Reply;
    expect(rejectReason(r('rejected', 'html'))).toBe('html');
    expect(rejectReason(r('accepted'))).toBeUndefined();
    expect(rejectReason(undefined)).toBeUndefined();
  });
});

describe('per-site exclusion', () => {
  const off = (...disabledSites: string[]) => ctx({ settings: { ...DEFAULT_SETTINGS, disabledSites } });
  it('leaves downloads from an excluded site (the referring page, else the file host)', () => {
    expect(decide(item(), off('example.com'))).toBe('site');
    expect(decide(item({ referrer: '' }), off('example.com'))).toBe('site');
    expect(decide(item({ referrer: 'https://Releases.Example.dev/x' }), off('releases.example.dev'))).toBe('site');
    expect(decide(item(), off('cdn.example.com', 'other.org'))).toBe('capture');
    expect(decide(item({ referrer: '' }), off())).toBe('capture');
  });
  it('toggles a host in the list', () => {
    expect(toggleSite([], 'a.com')).toEqual(['a.com']);
    expect(toggleSite(['a.com', 'b.com'], 'a.com')).toEqual(['b.com']);
  });
  it('reads hosts of http(s) URLs only', () => {
    expect(hostOf('https://WWW.x.com:8080/a')).toBe('www.x.com');
    expect(hostOf('chrome://extensions')).toBeUndefined();
    expect(hostOf(undefined)).toBeUndefined();
  });
});

describe('capture toast', () => {
  const tab = { id: 7, url: 'https://releases.example.dev/devtool/2.3' };
  it('asks only on the http(s) page tab the download came from', () => {
    expect(canAsk({ referrer: 'https://releases.example.dev/devtool/2.3' }, tab)).toBe(true);
    expect(canAsk({ referrer: 'https://releases.example.dev/' }, tab)).toBe(true);
    expect(canAsk({ referrer: '' }, tab)).toBe(true);
    expect(canAsk({ referrer: 'https://elsewhere.test/' }, tab)).toBe(false);
    expect(canAsk({ referrer: '' }, { id: 7, url: 'chrome://newtab/' })).toBe(false);
    expect(canAsk({ referrer: '' }, { url: tab.url })).toBe(false);
    expect(canAsk({ referrer: '' }, undefined)).toBe(false);
  });
  it('sends unless the user chose the browser', () => {
    expect(askAnswer('browser')).toBe('browser');
    expect(askAnswer('send')).toBe('send');
    expect(askAnswer(undefined)).toBe('send');
  });
});

describe('sent today', () => {
  const env = (type: string, payload: object) => ({ protocolVersion: 1, id: 'x', type, payload }) as Reply;
  const ok = env('download.add.result', { status: 'accepted' });
  it('counts files the app accepted', () => {
    const add = buildInterceptMessage(item(), { cookies: [], userAgent: 'UA' });
    expect(sentCount(add, ok)).toBe(1);
    expect(sentCount(add, env('download.add.result', { status: 'rejected' }))).toBe(0);
    expect(sentCount(add, undefined)).toBe(0);
    const batch = { protocolVersion: 1, id: 'b', type: 'download.batch', payload: { items: [{ url: 'a' }, { url: 'b' }] } } as Request;
    expect(sentCount(batch, ok)).toBe(2);
    expect(sentCount({ protocolVersion: 1, id: 'p', type: 'ping', payload: {} }, env('pong', {}))).toBe(0);
  });
  it('starts over on a new local day', () => {
    expect(localDay(new Date(2026, 0, 5, 23, 59))).toBe('2026-01-05');
    const a = bumpDaily(undefined, '2026-10-16', 1);
    const b = bumpDaily(a, '2026-10-16', 3);
    expect(b).toEqual({ day: '2026-10-16', count: 4 });
    expect(bumpDaily(b, '2026-10-17', 1)).toEqual({ day: '2026-10-17', count: 1 });
    expect(countToday(b, '2026-10-16')).toBe(4);
    expect(countToday(b, '2026-10-17')).toBe(0);
    expect(countToday(undefined, '2026-10-17')).toBe(0);
  });
  it('formats sizes', () => {
    const num = new Intl.NumberFormat('en', { maximumFractionDigits: 1 });
    expect(formatBytes(91 * MiB, num)).toBe('91 MB');
    expect(formatBytes(1536, num)).toBe('1.5 KB');
    expect(formatBytes(undefined, num)).toBe('');
  });
});

describe('versionSkew', () => {
  it('ignores patch differences and unknown versions', () => {
    expect(versionSkew('0.8.1', '0.8.4')).toBe('ok');
    expect(versionSkew('0.8.1', undefined)).toBe('ok');
    expect(versionSkew('0.8.1', 'dev')).toBe('ok');
  });
  it('names the side that is behind', () => {
    expect(versionSkew('0.8.1', '0.9.0')).toBe('extension-old');
    expect(versionSkew('0.10.0', '0.9.3')).toBe('app-old');
    expect(versionSkew('1.0.0', '0.12.0')).toBe('app-old');
  });
});
