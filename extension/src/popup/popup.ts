import { CATEGORIES, countByCategory, filterLinks, toBatchLinks, type BatchLink, type LinkCategory } from '../batch';
import {
  DEFAULT_SETTINGS, countToday, formatBytes, hostOf, isAddable, isHttp, localDay, outcomeOf, parseTypes, toggleSite, versionSkew,
  type DailyCount, type Outcome, type Settings,
} from '../capture';
import type { MediaHit } from '../media';
import {
  envelope, sendNative,
  type Command, type CommandAction, type Ping, type Reply, type Resource, type StatusEntry, type StatusGet,
} from '../protocol';

const t = (key: string, ...subs: string[]) => chrome.i18n.getMessage(key, subs);
const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;

const locale = chrome.i18n.getUILanguage();
document.documentElement.lang = locale;
document.title = t('extName');
for (const el of document.querySelectorAll<HTMLElement>('[data-i18n]')) el.textContent = t(el.dataset.i18n!);
for (const el of document.querySelectorAll<HTMLElement>('[data-i18n-title]')) el.title = el.ariaLabel = t(el.dataset.i18nTitle!);
for (const el of document.querySelectorAll<HTMLInputElement>('[data-i18n-placeholder]')) el.placeholder = t(el.dataset.i18nPlaceholder!);

const num = new Intl.NumberFormat(locale, { maximumFractionDigits: 1 });
const pct = new Intl.NumberFormat(locale, { style: 'percent', maximumFractionDigits: 0 });
const bytes = (n: number | undefined) => formatBytes(n, num);
const speed = (bps: number) => `${bytes(bps)}${t('perSecond')}`;

/** "4 dk kaldı": seconds, minutes or hours left. */
function eta(seconds: number): string {
  if (seconds < 60) return t('etaSec', String(Math.max(1, Math.round(seconds))));
  if (seconds < 3600) return t('etaMin', String(Math.round(seconds / 60)));
  return t('etaHour', num.format(Math.round(seconds / 360) / 10));
}

// ---- small DOM helpers ----
const SVG_NS = 'http://www.w3.org/2000/svg';
function icon(d: string): SVGSVGElement {
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  const p = document.createElementNS(SVG_NS, 'path');
  p.setAttribute('d', d);
  svg.append(p);
  return svg;
}
const ICONS = {
  pause: 'M9 5v14M15 5v14',
  play: 'M7 4.5l12 7.5-12 7.5z',
  folder: 'M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z',
  open: 'M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5',
  remove: 'M6 6l12 12M18 6 6 18',
};
function iconButton(d: string, label: string, onClick: () => void, cls = 'icon-btn'): HTMLButtonElement {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = cls;
  b.title = b.ariaLabel = label;
  b.append(icon(d));
  b.addEventListener('click', (e) => { e.stopPropagation(); onClick(); });
  return b;
}
function el<K extends keyof HTMLElementTagNameMap>(tag: K, cls?: string, text?: string): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

let toastTimer: ReturnType<typeof setTimeout> | undefined;
function toast(text: string, kind: 'ok' | 'bad' | 'info' = 'info'): void {
  const box = $('toast');
  box.textContent = text;
  box.dataset.kind = kind;
  box.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { box.hidden = true; }, 2600);
}
function toastOutcome(outcome: Outcome, okText: string): void {
  if (outcome === 'ok') toast(okText, 'ok');
  else toast(t(outcome === 'update' ? 'updateRequired' : outcome === 'unreachable' ? 'sendUnreachable' : 'sendFailed'), 'bad');
}

// ---- views: two tabs, plus settings behind the header button ----
type TabName = 'downloads' | 'page' | 'settings';
let view: TabName = 'downloads';
let lastTab: Exclude<TabName, 'settings'> = 'downloads';
const tabButtons = [...document.querySelectorAll<HTMLButtonElement>('.tabs [role="tab"]')];
function selectTab(name: TabName): void {
  view = name;
  if (name !== 'settings') lastTab = name;
  for (const b of tabButtons) b.setAttribute('aria-selected', String(b.dataset.tab === name));
  for (const n of ['downloads', 'page', 'settings'] as TabName[]) $(`tab-${n}`).hidden = n !== name;
  $('home').hidden = name === 'settings';
  $('openSettings').setAttribute('aria-pressed', String(name === 'settings'));
  $('paste').hidden = name !== 'downloads';
  $('batchGo').hidden = name !== 'page';
  void chrome.storage.local.set({ popupTab: name });
}
for (const b of tabButtons) b.addEventListener('click', () => selectTab(b.dataset.tab as TabName));
$('openSettings').addEventListener('click', () => selectTab(view === 'settings' ? lastTab : 'settings'));
$('back').addEventListener('click', () => selectTab(lastTab));

// ---- native commands ----
async function native(msg: Parameters<typeof sendNative>[0]): Promise<Reply | undefined> {
  try { return await sendNative(msg); } catch { return undefined; }
}
async function command(action: CommandAction, id?: number): Promise<void> {
  const outcome = outcomeOf(await native(envelope<Command>('command', { action, id })), 'download.add.result');
  if (outcome !== 'ok') toastOutcome(outcome, '');
  void refresh();
}
$('openApp').addEventListener('click', () => void command('show'));
$('pauseAll').addEventListener('click', () => void command('pauseAll'));
$('resumeAll').addEventListener('click', () => void command('resumeAll'));

// App and extension update separately; say which one is behind (checked once per popup open).
void native(envelope<Ping>('ping', {})).then((r) => {
  if (r?.type !== 'pong') return;
  const skew = versionSkew(chrome.runtime.getManifest().version, r.payload.appVersion);
  if (skew === 'ok') return;
  $('skew').textContent = t(skew === 'extension-old' ? 'skewExtensionOld' : 'skewAppOld');
  $('skew').hidden = false;
});

// "Paste link": the clipboard's URL as a download. Clipboard access is optional and asked for here,
// on the click, so installing the extension does not request it up front.
$('paste').addEventListener('click', async () => {
  let text = '';
  try {
    if (!(await chrome.permissions.request({ permissions: ['clipboardRead'] }))) return toast(t('clipboardFailed'), 'bad');
    text = (await navigator.clipboard.readText()).trim();
  } catch {
    return toast(t('clipboardFailed'), 'bad');
  }
  if (!isAddable(text)) return toast(t('clipboardNoLink'), 'bad');
  const r = await chrome.runtime.sendMessage({ type: 'add-url', url: text }).catch(() => undefined);
  toastOutcome(r?.outcome ?? 'failed', t('sentOk'));
});

// ---- downloads tab (polled; rows are reused by id so hover/focus survive the refresh) ----
const rows = new Map<number, { li: HTMLLIElement; key: string }>();
const kindLabel = (k: StatusEntry['kind']) => (k === 'torrent' ? 'Torrent' : k === 'media' ? t('kindMedia') : 'HTTP');

/** "Torrent · 8,4 MB/s · 4 dk kaldı" while running; the state (and size when done) otherwise. */
function metaOf(e: StatusEntry): string {
  const parts: string[] = [];
  if (e.state === 'downloading') {
    parts.push(kindLabel(e.kind));
    if (e.bytesPerSecond > 0) parts.push(speed(e.bytesPerSecond));
    if (e.totalBytes && e.bytesPerSecond > 0) parts.push(eta((e.totalBytes - e.doneBytes) / e.bytesPerSecond));
    else parts.push(e.totalBytes ? `${bytes(e.doneBytes)} / ${bytes(e.totalBytes)}` : bytes(e.doneBytes));
  } else {
    parts.push(t(`state_${e.state}`));
    if (e.state === 'seeding' && e.bytesPerSecond > 0) parts.push(speed(e.bytesPerSecond));
    if (e.state === 'completed') parts.push(bytes(e.totalBytes ?? e.doneBytes));
  }
  return parts.filter(Boolean).join(' · ');
}

function renderEntry(e: StatusEntry): HTMLLIElement {
  const li = el('li', 'dl');
  li.dataset.state = e.state;
  const known = e.state === 'completed' || !!e.totalBytes;

  const name = el('span', 'name', e.name);
  name.title = `${e.name}\n${t('actDetails')}`;
  name.addEventListener('click', () => void command('details', e.id));
  const line = el('div', 'line');
  line.append(name, el('span', 'pct', known ? pct.format(e.progress) : ''));

  const bar = el('div', 'bar');
  const fill = el('div');
  if (e.state === 'downloading' && !known) fill.className = 'ind'; // size unknown: pulsing full bar
  else fill.style.width = `${Math.round(e.progress * 1000) / 10}%`;
  bar.append(fill);

  const body = el('div', 'body');
  body.append(line, bar, el('div', 'meta', metaOf(e)));

  // Main action (design): pause / resume / show in folder. Open and remove show on hover or focus.
  const acts = el('div', 'acts');
  if (e.state === 'completed') acts.append(iconButton(ICONS.open, t('actOpen'), () => void command('open', e.id), 'icon-btn more'));
  acts.append(iconButton(ICONS.remove, t('actRemove'), () => void command('remove', e.id), 'icon-btn more'));
  if (e.state === 'downloading' || e.state === 'queued' || e.state === 'seeding')
    acts.append(iconButton(ICONS.pause, t('actPause'), () => void command('pause', e.id)));
  else if (e.state === 'paused' || e.state === 'failed')
    acts.append(iconButton(ICONS.play, t('actResume'), () => void command('resume', e.id)));
  else acts.append(iconButton(ICONS.folder, t('actFolder'), () => void command('folder', e.id)));

  li.append(body, acts);
  return li;
}

function renderList(items: StatusEntry[]): void {
  const list = $('list');
  const keep = new Set<number>();
  let prev: Element | null = null;
  for (const e of items) {
    keep.add(e.id);
    const key = JSON.stringify(e);
    let row = rows.get(e.id);
    if (!row || row.key !== key) {
      const li = renderEntry(e);
      if (row) row.li.replaceWith(li);
      row = { li, key };
      rows.set(e.id, row);
    }
    // Keep the app's order (running first) without re-inserting rows that are already in place.
    const want: Element | null = prev ? prev.nextElementSibling : list.firstElementChild;
    if (want !== row.li) list.insertBefore(row.li, want);
    prev = row.li;
  }
  for (const [id, row] of rows) if (!keep.has(id)) { row.li.remove(); rows.delete(id); }
}

async function refresh(): Promise<void> {
  const reply = await native(envelope<StatusGet>('status.get', {}));
  const outcome = outcomeOf(reply, 'status');
  $('status').dataset.state = outcome;
  $('statusText').textContent = t(outcome === 'ok' ? 'popupConnected' : outcome === 'update' ? 'popupUpdateRequired' : 'popupUnreachable');

  const s = reply?.type === 'status' ? reply.payload : undefined;
  $('summaryRow').hidden = !s;
  if (s) $('summary').textContent = t('summary', num.format(s.active), num.format(s.queued), speed(s.bytesPerSecond));
  // Apps before 0.7 only send `top`: show those as running rows without actions.
  const items: StatusEntry[] = s?.items ?? s?.top.map((x, i) => ({
    id: -1 - i, name: x.name, state: 'downloading', kind: 'http', progress: x.progress,
    bytesPerSecond: x.bytesPerSecond, doneBytes: 0,
  })) ?? [];
  renderList(items);
  $('dlCount').textContent = s ? String(items.length) : '';
  // Not reachable: say how to get going instead of showing an empty list.
  $('empty').textContent = t(s ? 'emptyDownloads' : outcome === 'update' ? 'updateRequired' : 'appNotRunning');
  $('empty').hidden = !!s && items.length > 0;
  for (const b of [$('pauseAll'), $('resumeAll')] as HTMLButtonElement[]) b.disabled = !s;
}

(async function poll() {
  await refresh();
  setTimeout(poll, 1000);
})();

// "Sent to Fluxer from this browser today" (counted by the service worker).
function renderSentToday(stored: DailyCount | undefined): void {
  $('sentToday').textContent = t('sentToday', num.format(countToday(stored, localDay(new Date()))));
}
void chrome.storage.local.get('sentToday').then((r) => renderSentToday(r.sentToday as DailyCount | undefined));

// ---- this page: captured media + batch links (loaded at open: the tab shows their count) ----
let currentTab: chrome.tabs.Tab | undefined;
let links: BatchLink[] = [];
let mediaCount = 0;
let category: LinkCategory | 'all' = 'all';
const selected = new Set<string>();

async function activeTab(): Promise<chrome.tabs.Tab | undefined> {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  return tab;
}

async function loadPage(): Promise<void> {
  currentTab = await activeTab();
  const ok = !!currentTab?.id && isHttp(currentTab.url);
  $('pageUnsupported').hidden = ok;
  $('pageContent').hidden = !ok;
  renderSite();
  if (!ok) return;
  await Promise.all([loadMedia(), loadLinks()]);
}

const updatePageCount = () => { $('pageCount').textContent = mediaCount + links.length ? String(mediaCount + links.length) : ''; };

async function loadMedia(): Promise<void> {
  const res = await chrome.runtime.sendMessage({ type: 'media-list', tabId: currentTab!.id }).catch(() => undefined);
  const hits: MediaHit[] = res?.hits ?? [];
  mediaCount = hits.length;
  updatePageCount();
  $('mediaCount').textContent = hits.length ? String(hits.length) : '';
  $('mediaEmpty').hidden = hits.length > 0;
  $('media').replaceChildren(...hits.map((h) => {
    const li = el('li', 'media');
    const kind = el('span', 'kind', h.kind === 'file' ? h.ext : h.kind);
    kind.dataset.kind = h.kind;
    const info = el('div', 'info');
    const name = el('div', undefined, h.name);
    name.title = h.url;
    const quality = h.height ? `${h.height}p` : '';
    const sub = el('small', undefined, [h.kind === 'file' ? t('mediaFile') : t('mediaStream'), quality, bytes(h.size)].filter(Boolean).join(' · '));
    info.append(name, sub);
    const go = el('button', 'ghost', t('mediaDownload'));
    go.type = 'button';
    go.addEventListener('click', async () => {
      go.disabled = true;
      const r = await chrome.runtime.sendMessage({ type: 'media-download', tabId: currentTab!.id, url: h.url, kind: h.kind }).catch(() => undefined);
      toastOutcome(r?.outcome ?? 'failed', t('sentOk'));
      go.disabled = false;
    });
    li.append(kind, info, go);
    return li;
  }));
}

$('pageVideo').addEventListener('click', async () => {
  const b = $<HTMLButtonElement>('pageVideo');
  b.disabled = true;
  const r = await chrome.runtime.sendMessage({ type: 'page-video', tabId: currentTab!.id }).catch(() => undefined);
  toastOutcome(r?.outcome ?? 'failed', t('sentOk'));
  b.disabled = false;
});

async function loadLinks(): Promise<void> {
  const res = await chrome.runtime.sendMessage({ type: 'collect-links', tabId: currentTab!.id }).catch(() => undefined);
  const resources: Resource[] | undefined = res?.resources;
  links = resources ? toBatchLinks(resources) : [];
  $('linksEmpty').textContent = t(resources ? 'batchEmpty' : 'pageNotReady');
  updatePageCount();
  renderLinks();
}

function renderLinks(): void {
  const counts = countByCategory(links);
  $('linkCount').textContent = links.length ? String(links.length) : '';
  const chips = (['all', ...CATEGORIES] as const)
    .filter((c) => c === 'all' || counts[c])
    .map((c) => {
      const b = el('button', 'chip', t(`cat_${c}`));
      b.type = 'button';
      b.setAttribute('aria-pressed', String(c === category));
      b.append(el('small', undefined, String(c === 'all' ? links.length : counts[c])));
      b.addEventListener('click', () => { category = c; renderLinks(); });
      return b;
    });
  $('cats').replaceChildren(...chips);
  $('cats').hidden = !links.length;
  (document.querySelector('.search-row') as HTMLElement).hidden = !links.length;

  const shown = filterLinks(links, category, $<HTMLInputElement>('query').value);
  $('links').replaceChildren(...shown.map((l) => {
    const li = el('li', 'link');
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = selected.has(l.url);
    box.ariaLabel = l.name;
    const toggle = () => {
      if (selected.has(l.url)) selected.delete(l.url); else selected.add(l.url);
      box.checked = selected.has(l.url);
      updateSelection(shown);
    };
    box.addEventListener('click', (e) => { e.stopPropagation(); toggle(); });
    li.addEventListener('click', toggle);
    const info = el('div', 'info');
    info.append(el('div', undefined, l.name), el('small', undefined, t(`cat_${l.category}`)));
    info.title = l.url;
    li.append(box, info, el('span', 'ext', l.ext));
    return li;
  }));
  $('linksEmpty').hidden = shown.length > 0;
  updateSelection(shown);
}

function updateSelection(shown: BatchLink[]): void {
  const all = $<HTMLInputElement>('selectAll');
  const inView = shown.filter((l) => selected.has(l.url)).length;
  all.checked = shown.length > 0 && inView === shown.length;
  all.indeterminate = inView > 0 && inView < shown.length;
  $('selCount').textContent = selected.size ? t('batchDownloadN', String(selected.size)) : t('batchPick');
  $<HTMLButtonElement>('batchGo').disabled = selected.size === 0;
}

$('query').addEventListener('input', renderLinks);
$('selectAll').addEventListener('change', () => {
  const shown = filterLinks(links, category, $<HTMLInputElement>('query').value);
  const on = $<HTMLInputElement>('selectAll').checked;
  for (const l of shown) if (on) selected.add(l.url); else selected.delete(l.url);
  renderLinks();
});
$('batchGo').addEventListener('click', async () => {
  const items = links.filter((l) => selected.has(l.url)).map((l) => ({ url: l.url }));
  const b = $<HTMLButtonElement>('batchGo');
  b.disabled = true;
  const r = await chrome.runtime.sendMessage({ type: 'batch', tabId: currentTab!.id, items }).catch(() => undefined);
  toastOutcome(r?.outcome ?? 'failed', t('sentBatch', String(items.length)));
  if (r?.outcome === 'ok') { selected.clear(); renderLinks(); }
  b.disabled = selected.size === 0;
});

// ---- capture card + settings (one storage read; kept live through storage.onChanged) ----
let settings: Settings = DEFAULT_SETTINGS;

function renderSite(): void {
  const host = hostOf(currentTab?.url);
  $('siteRow').hidden = !host;
  if (!host) return;
  $('siteHost').textContent = $('siteHost').title = host;
  $('siteToggle').textContent = t(settings.disabledSites.includes(host) ? 'siteOn' : 'siteOff');
}

function renderSettings(): void {
  const host = hostOf(currentTab?.url);
  $<HTMLInputElement>('captureEnabled').checked = settings.captureEnabled;
  $('captureHint').textContent = !settings.captureEnabled ? t('captureHintOff')
    : host && settings.disabledSites.includes(host) ? t('captureHintSite')
    : settings.minSizeMiB > 0 ? t('captureHintOn', num.format(settings.minSizeMiB)) : t('captureHintAll');
  renderSite();
  const size = $<HTMLInputElement>('minSizeMiB');
  if (document.activeElement !== size) size.value = String(settings.minSizeMiB);
  $<HTMLInputElement>('showVideoButton').checked = settings.showVideoButton;
  $<HTMLInputElement>('showBadge').checked = settings.showBadge;
  const types = $<HTMLInputElement>('skipTypes');
  if (document.activeElement !== types) types.value = settings.skipTypes.join(', ');
}

chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== 'local') return;
  if (changes.sentToday) renderSentToday(changes.sentToday.newValue as DailyCount | undefined);
  for (const key of Object.keys(DEFAULT_SETTINGS) as (keyof Settings)[]) {
    if (changes[key]) settings = { ...settings, [key]: changes[key].newValue ?? DEFAULT_SETTINGS[key] };
  }
  renderSettings();
});

void (chrome.storage.local.get({ ...DEFAULT_SETTINGS, popupTab: 'downloads' }) as Promise<Settings & { popupTab: TabName }>).then((s) => {
  const { popupTab, ...rest } = s;
  settings = rest;
  renderSettings();
  selectTab(popupTab);
});
void loadPage().then(renderSettings);

for (const key of ['captureEnabled', 'showVideoButton', 'showBadge'] as const) {
  $<HTMLInputElement>(key).addEventListener('change', (e) => void chrome.storage.local.set({ [key]: (e.target as HTMLInputElement).checked }));
}
$('minSizeMiB').addEventListener('change', () => {
  const input = $<HTMLInputElement>('minSizeMiB');
  const v = Math.max(0, Number(input.value) || 0);
  input.value = String(v);
  void chrome.storage.local.set({ minSizeMiB: v });
});
$('skipTypes').addEventListener('change', () => {
  const input = $<HTMLInputElement>('skipTypes');
  const types = parseTypes(input.value);
  input.value = types.join(', ');
  void chrome.storage.local.set({ skipTypes: types });
});
$('siteToggle').addEventListener('click', () => {
  const host = hostOf(currentTab?.url);
  if (host) void chrome.storage.local.set({ disabledSites: toggleSite(settings.disabledSites, host) });
});
