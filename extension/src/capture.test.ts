import { describe, expect, it } from 'vitest';
import {
  DEFAULT_SETTINGS, RecentUrls, basename, buildInterceptMessage, decide, outcomeOf, toCookieDto,
  type CaptureContext, type DownloadItemLike,
} from './capture';
import { PROTOCOL_VERSION, type Reply } from './protocol';

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
    expect(decide(item({ totalBytes: 3 * MiB }), ctx({ settings: { captureEnabled: true, minSizeMiB: 5 } }))).toBe('small');
    expect(decide(item({ totalBytes: 10 }), ctx({ settings: { captureEnabled: true, minSizeMiB: 0 } }))).toBe('capture');
  });
  it('leaves everything when capture is disabled', () => {
    expect(decide(item(), ctx({ settings: { ...DEFAULT_SETTINGS, captureEnabled: false } }))).toBe('disabled');
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

describe('RecentUrls', () => {
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
