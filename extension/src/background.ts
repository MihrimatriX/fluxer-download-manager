// Service worker: download capture, context menus, native messaging.
import { categoryOfName, nameOf, toBatchLinks } from './batch';
import {
  DEFAULT_SETTINGS, RecentUrls, askAnswer, basename, bumpDaily, buildInterceptMessage, buildTorrentFileMessage, canAsk,
  decide, formatBytes, isAddable, isHttp, localDay, outcomeOf, rejectReason, sentCount, toCookieDto,
  type DailyCount, type Outcome, type Settings,
} from './capture';
import { truncateToFit } from './collector';
import { classifyMedia, mergeHit, pickForVideo, type MediaHit } from './media';
import {
  MAX_REQUEST_BYTES, envelope, sendNative,
  type CookieDto, type DownloadAdd, type DownloadBatch, type PageResources, type PageSend, type Reply, type Request, type Resource,
} from './protocol';

const t = (key: string) => chrome.i18n.getMessage(key);

// ---- state (in-memory; the worker may restart, which only drops these short-lived entries) ----
const postUrls = new RecentUrls(30_000);
const bypassUrls = new RecentUrls(30_000);

const loadSettings = () => chrome.storage.local.get(DEFAULT_SETTINGS) as Promise<Settings>;
let settings = loadSettings();
chrome.storage.onChanged.addListener((_changes, area) => {
  if (area === 'local') settings = loadSettings();
});

// Alt state comes from content scripts; mirrored to storage.session to survive worker restarts.
let altHeld: boolean | undefined;
async function isAltHeld(): Promise<boolean> {
  if (altHeld === undefined) {
    const s = await chrome.storage.session.get('altHeld');
    altHeld ??= Boolean(s.altHeld);
  }
  return altHeld;
}
chrome.runtime.onMessage.addListener((msg, sender) => {
  if (msg?.type === 'alt' && sender.id === chrome.runtime.id) {
    altHeld = Boolean(msg.down);
    void chrome.storage.session.set({ altHeld });
  }
});

// Non-blocking observer: remember navigations made with POST so their downloads stay in the browser.
// Only frame navigations: a POST that turns into a download is one of those, and this keeps the
// worker from waking for every subresource request.
chrome.webRequest.onBeforeRequest.addListener(
  (d) => { if (d.method === 'POST') postUrls.add(d.url); },
  { urls: ['<all_urls>'], types: ['main_frame', 'sub_frame'] },
);

// ---- helpers ----
async function trySend(msg: Request): Promise<Reply | undefined> {
  let reply: Reply | undefined;
  try {
    reply = await sendNative(msg);
  } catch (e) {
    console.warn('Fluxer: native messaging failed', e);
  }
  const n = sentCount(msg, reply);
  if (n) void countSent(n);
  return reply;
}

// "Sent to Fluxer today" for the popup: one read-modify-write at a time.
let sentWrites = Promise.resolve();
function countSent(n: number): Promise<void> {
  sentWrites = sentWrites.then(async () => {
    const { sentToday } = await chrome.storage.local.get('sentToday') as { sentToday?: DailyCount };
    await chrome.storage.local.set({ sentToday: bumpDaily(sentToday, localDay(new Date()), n) });
  }).catch((e) => console.warn('Fluxer: sent count', e));
  return sentWrites;
}

function notify(messageKey: string): void {
  chrome.notifications.create({
    type: 'basic',
    iconUrl: chrome.runtime.getURL('icons/icon128.png'),
    title: t('extName'),
    message: t(messageKey),
  });
}

/** prefix: 'fallback' (download handed back to the browser) or 'send' (context-menu actions). */
function notifyOutcome(outcome: Outcome, prefix: 'fallback' | 'send'): void {
  if (outcome === 'ok') return;
  notify(outcome === 'update' ? 'updateRequired' : prefix + (outcome === 'unreachable' ? 'Unreachable' : 'Failed'));
}

async function cookiesFor(urls: (string | undefined)[], tabId?: number): Promise<CookieDto[]> {
  // The tab's cookie store matters for incognito; without a tab we fall back to the default store.
  const storeId = tabId === undefined
    ? undefined
    : (await chrome.cookies.getAllCookieStores()).find((s) => s.tabIds.includes(tabId))?.id;
  const out = new Map<string, CookieDto>();
  for (const url of new Set(urls)) {
    if (!url || !isHttp(url)) continue;
    for (const c of await chrome.cookies.getAll(storeId ? { url, storeId } : { url })) {
      out.set(`${c.name}|${c.domain}|${c.path}`, toCookieDto(c));
    }
  }
  return [...out.values()];
}

// ponytail: DownloadItem has no tabId, so "the tab" is the active tab of the last focused window
// (matched on incognito). Wrong for downloads started from background tabs; good enough for tabUrl.
async function activeTab(incognito: boolean): Promise<chrome.tabs.Tab | undefined> {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  return tab?.incognito === incognito ? tab : undefined;
}

// ---- download capture ----
// The browser download is paused (not cancelled) while Fluxer probes the URL. If Fluxer declines —
// the server would hand it a web page (Cloudflare, one-time link, missing session) — the browser
// simply resumes its own request, so nothing is fetched twice.
chrome.downloads.onCreated.addListener((item) => { void onDownloadCreated(item); });
chrome.downloads.onChanged.addListener((delta) => {
  if (delta.state?.current === 'complete') void onDownloadComplete(delta.id);
});

/** Downloads the browser finishes itself and Fluxer then opens as torrents. Survives worker restarts. */
const pendingTorrents = {
  async add(id: number) { await chrome.storage.session.set({ [`torrent:${id}`]: true }); },
  async take(id: number) {
    const key = `torrent:${id}`;
    const hit = Boolean((await chrome.storage.session.get(key))[key]);
    if (hit) await chrome.storage.session.remove(key);
    return hit;
  },
};

async function onDownloadCreated(item: chrome.downloads.DownloadItem): Promise<void> {
  const decision = decide(item, {
    settings: await settings,
    altHeld: await isAltHeld(),
    bypassed: bypassUrls.take(item.url),
    postInitiated: postUrls.has(item.url) || postUrls.has(item.finalUrl),
    ownExtensionId: chrome.runtime.id,
  });
  if (decision === 'torrent') {
    await pendingTorrents.add(item.id);
    if (item.state === 'complete') await onDownloadComplete(item.id);
    return;
  }
  if (decision !== 'capture') return;

  try {
    await chrome.downloads.pause(item.id);
  } catch {
    return; // finished or failed already: leave it to the browser
  }
  const [paused] = await chrome.downloads.search({ id: item.id });
  if (!paused || paused.state !== 'in_progress') return;

  const tab = await activeTab(item.incognito);
  // The toast on the page: "Download with Fluxer" (or the countdown) carries on below, "Leave in browser" hands back.
  if (canAsk(paused, tab) && (await askOnPage(tab!.id!, paused)) === 'browser') return handBack(item, 'ok');

  const msg = buildInterceptMessage(paused, {
    cookies: await cookiesFor([item.url, item.finalUrl], tab?.id),
    userAgent: navigator.userAgent,
    tabUrl: tab?.url,
  });
  const reply = await trySend(msg);
  const outcome = outcomeOf(reply, 'download.add.result');
  if (outcome === 'ok') {
    await chrome.downloads.cancel(item.id).catch(() => {});
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
    return;
  }

  const reason = rejectReason(reply);
  if (reason === 'torrent') await pendingTorrents.add(item.id);
  // 'html'/'torrent' are normal hand-backs; only tell the user when Fluxer could not be reached.
  await handBack(item, outcome, reason === 'html' || reason === 'torrent');
}

/**
 * Lets the browser carry on with a paused download. `outcome` 'ok' means the user chose the browser (no notice);
 * otherwise Fluxer could not take it and the user is told, unless `quiet` (a normal hand-back).
 */
async function handBack(item: chrome.downloads.DownloadItem, outcome: Outcome, quiet = false): Promise<void> {
  try {
    await chrome.downloads.resume(item.id);
    if (!quiet) notifyOutcome(outcome, 'fallback');
    return;
  } catch {
    // The paused request died (server timeout): restart it below.
  }
  await chrome.downloads.cancel(item.id).catch(() => {});
  await chrome.downloads.erase({ id: item.id }).catch(() => {});

  if (item.incognito) {
    // chrome.downloads.download() would run in the regular profile and leak the URL into normal
    // history, so tell the user how to get it in the browser instead.
    if (outcome !== 'ok') notify(outcome === 'update' ? 'updateRequired' : 'fallbackIncognito');
    return;
  }
  bypassUrls.add(item.url); // before download(): its onCreated must not capture it again
  try {
    await chrome.downloads.download({ url: item.url });
  } catch (e) {
    console.warn('Fluxer: could not restart download in browser', e);
  }
  notifyOutcome(outcome, 'fallback');
}

const ASK_SECONDS = 3;
const sizeFormat = new Intl.NumberFormat(chrome.i18n.getUILanguage(), { maximumFractionDigits: 1 });

/**
 * Shows the capture toast in the tab's top frame and waits for the answer. No content script (page open since
 * before an update, restricted page) rejects at once, so the download goes to Fluxer right away as before.
 */
async function askOnPage(tabId: number, item: chrome.downloads.DownloadItem): Promise<'send' | 'browser'> {
  const name = basename(item.filename) || nameOf(item.finalUrl || item.url);
  const ask = {
    type: 'capture-ask',
    name,
    size: item.totalBytes > 0 ? formatBytes(item.totalBytes, sizeFormat) : '',
    category: categoryOfName(name),
    seconds: ASK_SECONDS,
  };
  // A page that never answers (frozen, throttled) must not hold the download.
  const timeout = new Promise((r) => setTimeout(r, (ASK_SECONDS + 10) * 1000));
  try {
    return askAnswer(await Promise.race([chrome.tabs.sendMessage(tabId, ask, { frameId: 0 }), timeout]));
  } catch {
    return 'send';
  }
}

async function onDownloadComplete(id: number): Promise<void> {
  if (!(await pendingTorrents.take(id))) return;
  const [item] = await chrome.downloads.search({ id });
  if (!item?.filename) return;
  const tab = await activeTab(item.incognito);
  const msg = buildTorrentFileMessage(item, { userAgent: navigator.userAgent, tabUrl: tab?.url });
  // On failure the .torrent simply stays in the browser's downloads, which is what the user would get anyway.
  const outcome = outcomeOf(await trySend(msg), 'download.add.result');
  if (outcome === 'unreachable' || outcome === 'update') notifyOutcome(outcome, 'send');
}

// ---- context menus ----
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: 'download', title: t('menuDownload'), contexts: ['link', 'image', 'video', 'audio'] });
    chrome.contextMenus.create({ id: 'download-night', title: t('menuNight'), contexts: ['link'] });
    chrome.contextMenus.create({ id: 'send-links', title: t('menuAllLinks'), contexts: ['link'] });
    chrome.contextMenus.create({ id: 'grab', title: t('menuGrab'), contexts: ['page'] });
    chrome.contextMenus.create({ id: 'send-page', title: t('menuSendPage'), contexts: ['page'] });
  });
});

chrome.contextMenus.onClicked.addListener((info, tab) => { void onMenu(info, tab); });

async function onMenu(info: chrome.contextMenus.OnClickData, tab?: chrome.tabs.Tab): Promise<void> {
  const ctx = { userAgent: navigator.userAgent, incognito: tab?.incognito ?? false };

  if (info.menuItemId === 'download' || info.menuItemId === 'download-night') {
    const url = info.linkUrl || info.srcUrl;
    // blob:/MediaSource players: the same logic as the in-page video button.
    if (tab && !info.linkUrl && (info.mediaType === 'video' || info.mediaType === 'audio') && !isHttp(url))
      return notifyOutcome(await sendVideo(tab, url), 'send');
    if (!isAddable(url)) return notify('unsupportedUrl');
    const queue = info.menuItemId === 'download-night' ? 'night' : undefined;
    return notifyOutcome(await addUrl(url, tab, info.frameUrl || info.pageUrl, queue), 'send');
  }

  if (info.menuItemId === 'send-links' && tab?.id !== undefined) {
    const resources = await collectLinks(tab.id);
    if (!resources) return notify('pageNotReady');
    const items = toBatchLinks(resources).map((l) => ({ url: l.url }));
    if (!items.length) return notify('nothingFound');
    return notifyOutcome(await sendBatch(tab, items), 'send');
  }

  if (info.menuItemId === 'send-page') {
    const pageUrl = info.pageUrl || tab?.url;
    if (!isHttp(pageUrl)) return notify('unsupportedUrl');
    const msg = envelope<PageSend>('page.send', {
      ...ctx,
      pageUrl: pageUrl!,
      title: tab?.title,
      referrer: pageUrl,
      cookies: await cookiesFor([pageUrl], tab?.id),
    });
    return notifyOutcome(outcomeOf(await trySend(msg)), 'send');
  }

  if (info.menuItemId === 'grab' && tab?.id !== undefined) {
    let page: { pageUrl: string; resources: Resource[] } | undefined;
    try {
      page = await chrome.tabs.sendMessage(tab.id, { type: 'collect' }, { frameId: info.frameId ?? 0 });
    } catch {
      // No content script: page was open before the extension was installed/updated.
    }
    if (!page) return notify('pageNotReady');
    if (!page.resources.length) return notify('nothingFound');
    const msg = envelope<PageResources>('page.resources', {
      ...ctx,
      pageUrl: page.pageUrl,
      referrer: page.pageUrl,
      cookies: await cookiesFor([page.pageUrl], tab.id),
      resources: [],
    });
    msg.payload.resources = truncateToFit(page.resources, JSON.stringify(msg), MAX_REQUEST_BYTES);
    return notifyOutcome(outcomeOf(await trySend(msg)), 'send');
  }
}

// ---- media sniffing: videos/audio and stream manifests seen on each tab ----
// Kept in storage.session (survives worker restarts, cleared with the browser session).
interface TabMedia { pageUrl?: string; hits: MediaHit[] }
const mediaKey = (tabId: number) => `media:${tabId}`;

async function tabMedia(tabId: number): Promise<TabMedia> {
  const key = mediaKey(tabId);
  return ((await chrome.storage.session.get(key))[key] as TabMedia | undefined) ?? { hits: [] };
}

// One write at a time per tab: responses arrive in bursts and each update reads the previous list.
const mediaWrites = new Map<number, Promise<void>>();
function updateTabMedia(tabId: number, change: (m: TabMedia) => TabMedia): Promise<void> {
  const next = (mediaWrites.get(tabId) ?? Promise.resolve()).then(async () => {
    const m = change(await tabMedia(tabId));
    await chrome.storage.session.set({ [mediaKey(tabId)]: m });
    await updateBadge(tabId, m.hits.length);
  }).catch((e) => console.warn('Fluxer: media list', e));
  mediaWrites.set(tabId, next);
  return next;
}

async function updateBadge(tabId: number, count: number): Promise<void> {
  const show = (await settings).showBadge;
  await chrome.action.setBadgeText({ tabId, text: show && count > 0 ? String(Math.min(count, 99)) : '' }).catch(() => {});
}
// Turning the badge setting on or off applies to open tabs right away.
chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== 'local' || !changes.showBadge) return;
  void chrome.tabs.query({}).then((tabs) => Promise.all(tabs.map(async (tab) => {
    if (tab.id !== undefined) await updateBadge(tab.id, (await tabMedia(tab.id)).hits.length);
  })));
});
void chrome.action.setBadgeBackgroundColor({ color: '#0A84FF' });
void chrome.action.setBadgeTextColor({ color: '#FFFFFF' });

const header = (h: chrome.webRequest.HttpHeader[] | undefined, name: string) =>
  h?.find((x) => x.name.toLowerCase() === name)?.value;

chrome.webRequest.onHeadersReceived.addListener(
  (d) => {
    if (d.tabId < 0) return;
    const hit = classifyMedia({
      url: d.url,
      status: d.statusCode,
      contentType: header(d.responseHeaders, 'content-type'),
      contentLength: header(d.responseHeaders, 'content-length'),
      contentRange: header(d.responseHeaders, 'content-range'),
    });
    if (hit) void updateTabMedia(d.tabId, (m) => ({ ...m, hits: mergeHit(m.hits, hit) }));
  },
  { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'other', 'object'] },
  ['responseHeaders'],
);

// A new page (not just a #hash change) starts with an empty list.
const pageOf = (url: string) => url.split('#')[0];
chrome.tabs.onUpdated.addListener((tabId, info) => {
  if (!info.url) return;
  const url = info.url;
  void updateTabMedia(tabId, (m) => (m.pageUrl && pageOf(m.pageUrl) === pageOf(url) ? m : { pageUrl: url, hits: [] }));
});
chrome.tabs.onRemoved.addListener((tabId) => {
  mediaWrites.delete(tabId);
  void chrome.storage.session.remove(mediaKey(tabId));
});

// ---- sending media / pages / batches (popup, video button, context menu) ----
const browserCtx = (tab: chrome.tabs.Tab) => ({ userAgent: navigator.userAgent, incognito: tab.incognito });

/** One URL to Fluxer (context menu, popup paste, captured media). `queue: 'night'` holds it for the night schedule. */
async function addUrl(url: string, tab: chrome.tabs.Tab | undefined, referrer?: string, queue?: 'night'): Promise<Outcome> {
  const msg = envelope<DownloadAdd>('download.add', {
    userAgent: navigator.userAgent,
    incognito: tab?.incognito ?? false,
    url,
    source: 'context-menu',
    referrer,
    tabUrl: tab?.url,
    cookies: await cookiesFor([url, referrer], tab?.id),
    ...(queue && { queue }),
  });
  return outcomeOf(await trySend(msg), 'download.add.result');
}

const sendFile = (tab: chrome.tabs.Tab, url: string) => addUrl(url, tab, tab.url);

/** A page for yt-dlp, or a stream manifest with the page it plays on as referrer. */
async function sendMediaPage(tab: chrome.tabs.Tab, url: string): Promise<Outcome> {
  const msg = envelope<PageSend>('page.send', {
    ...browserCtx(tab),
    pageUrl: url,
    title: tab.title,
    referrer: tab.url,
    cookies: await cookiesFor([url, tab.url], tab.id),
  });
  return outcomeOf(await trySend(msg));
}

async function sendVideo(tab: chrome.tabs.Tab, src: string | undefined): Promise<Outcome> {
  if (!isHttp(tab.url)) return 'failed';
  const pick = pickForVideo(src, tab.id === undefined ? [] : (await tabMedia(tab.id)).hits);
  return pick.type === 'file' ? sendFile(tab, pick.url)
    : pick.type === 'stream' ? sendMediaPage(tab, pick.url)
    : sendMediaPage(tab, tab.url!);
}

async function sendBatch(tab: chrome.tabs.Tab, items: { url: string; fileName?: string }[]): Promise<Outcome> {
  const http = items.filter((i) => isHttp(i.url)).slice(0, 1000);
  if (!http.length || !isHttp(tab.url)) return 'failed';
  // Cookies for the page and for each host the files live on (download hosts often differ).
  const origins = [...new Set(http.map((i) => new URL(i.url).origin))].slice(0, 30).map((o) => o + '/');
  const msg = envelope<DownloadBatch>('download.batch', {
    ...browserCtx(tab),
    pageUrl: tab.url!,
    referrer: tab.url,
    items: http,
    cookies: await cookiesFor([tab.url, ...origins], tab.id),
  });
  msg.payload.items = truncateToFit(http, JSON.stringify({ ...msg, payload: { ...msg.payload, items: [] } }), MAX_REQUEST_BYTES);
  return outcomeOf(await trySend(msg), 'download.add.result');
}

async function collectLinks(tabId: number): Promise<Resource[] | undefined> {
  try {
    const page = await chrome.tabs.sendMessage(tabId, { type: 'collect' }, { frameId: 0 });
    return page?.resources;
  } catch {
    return undefined; // no content script: the tab was open before the extension was installed/updated
  }
}

/** Messages from the popup (tabId given) and from the in-page video button (sender.tab). */
type PanelMessage =
  | { type: 'media-list'; tabId: number }
  | { type: 'media-download'; tabId: number; url: string; kind: MediaHit['kind'] }
  | { type: 'page-video'; tabId: number }
  | { type: 'collect-links'; tabId: number }
  | { type: 'batch'; tabId: number; items: { url: string; fileName?: string }[] }
  | { type: 'add-url'; url: string }
  | { type: 'video-button'; src?: string };

chrome.runtime.onMessage.addListener((msg: PanelMessage, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id || !msg || typeof msg !== 'object') return;
  const handled = handlePanel(msg, sender);
  if (!handled) return;
  handled.then(sendResponse, (e) => { console.warn('Fluxer:', e); sendResponse({ outcome: 'failed' }); });
  return true; // async response
});

function handlePanel(msg: PanelMessage, sender: chrome.runtime.MessageSender): Promise<unknown> | undefined {
  const withTab = async (tabId: number, run: (tab: chrome.tabs.Tab) => Promise<Outcome>) =>
    ({ outcome: await run(await chrome.tabs.get(tabId)) });
  switch (msg.type) {
    case 'media-list': return tabMedia(msg.tabId);
    case 'media-download':
      return withTab(msg.tabId, (tab) => (msg.kind === 'file' ? sendFile(tab, msg.url) : sendMediaPage(tab, msg.url)));
    case 'page-video':
      return withTab(msg.tabId, (tab) => (isHttp(tab.url) ? sendMediaPage(tab, tab.url!) : Promise.resolve<Outcome>('failed')));
    case 'collect-links': return collectLinks(msg.tabId).then((resources) => ({ resources }));
    case 'batch': return withTab(msg.tabId, (tab) => sendBatch(tab, msg.items));
    case 'add-url':
      return (isAddable(msg.url) ? addUrl(msg.url, undefined) : Promise.resolve<Outcome>('failed')).then((outcome) => ({ outcome }));
    case 'video-button':
      return sender.tab ? sendVideo(sender.tab, msg.src).then((outcome) => ({ outcome })) : undefined;
    default: return undefined;
  }
}
