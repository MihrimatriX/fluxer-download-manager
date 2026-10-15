// Service worker: download capture, context menus, native messaging.
import {
  DEFAULT_SETTINGS, RecentUrls, buildInterceptMessage, buildTorrentFileMessage, decide, isHttp, outcomeOf,
  rejectReason, toCookieDto,
  type Outcome, type Settings,
} from './capture';
import { truncateToFit } from './collector';
import {
  MAX_REQUEST_BYTES, envelope, sendNative,
  type CookieDto, type DownloadAdd, type PageResources, type PageSend, type Reply, type Request, type Resource,
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
  try {
    return await sendNative(msg);
  } catch (e) {
    console.warn('Fluxer: native messaging failed', e);
    return undefined;
  }
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
  try {
    await chrome.downloads.resume(item.id);
    // 'html'/'torrent' are normal hand-backs; only tell the user when Fluxer could not be reached.
    if (reason !== 'html' && reason !== 'torrent') notifyOutcome(outcome, 'fallback');
    return;
  } catch {
    // The paused request died (server timeout): restart it below.
  }
  await chrome.downloads.cancel(item.id).catch(() => {});
  await chrome.downloads.erase({ id: item.id }).catch(() => {});

  if (item.incognito) {
    // chrome.downloads.download() would run in the regular profile and leak the URL into normal
    // history, so tell the user how to get it in the browser instead.
    notify(outcome === 'update' ? 'updateRequired' : 'fallbackIncognito');
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
    chrome.contextMenus.create({ id: 'grab', title: t('menuGrab'), contexts: ['page'] });
    chrome.contextMenus.create({ id: 'send-page', title: t('menuSendPage'), contexts: ['page'] });
  });
});

chrome.contextMenus.onClicked.addListener((info, tab) => { void onMenu(info, tab); });

async function onMenu(info: chrome.contextMenus.OnClickData, tab?: chrome.tabs.Tab): Promise<void> {
  const ctx = { userAgent: navigator.userAgent, incognito: tab?.incognito ?? false };

  if (info.menuItemId === 'download') {
    const url = info.linkUrl || info.srcUrl;
    if (!url || !/^(https?|magnet):/i.test(url)) return notify('unsupportedUrl');
    const msg = envelope<DownloadAdd>('download.add', {
      ...ctx,
      url,
      source: 'context-menu',
      referrer: info.frameUrl || info.pageUrl,
      tabUrl: tab?.url,
      cookies: await cookiesFor([url], tab?.id),
    });
    return notifyOutcome(outcomeOf(await trySend(msg), 'download.add.result'), 'send');
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
