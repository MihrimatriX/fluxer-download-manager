import { CATEGORIES, countByCategory, filterLinks, toBatchLinks, type BatchLink, type LinkCategory } from '../batch';
import { DEFAULT_SETTINGS, isHttp, outcomeOf, type Outcome, type Settings } from '../capture';
import type { MediaHit } from '../media';
import {
  envelope, sendNative,
  type Command, type CommandAction, type Reply, type Resource, type StatusEntry, type StatusGet,
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

function bytes(n: number | undefined): string {
  if (n === undefined || n < 0) return '';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = n, i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${num.format(v)} ${units[i]}`;
}
const speed = (bps: number) => `${bytes(bps)}${t('perSecond')}`;

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
  pause: 'M8 5v14M16 5v14',
  play: 'M7 4.5v15l12-7.5z',
  folder: 'M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z',
  open: 'M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5',
  remove: 'M6 6l12 12M18 6 6 18',
  file: 'M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8zM14 3v5h5',
  video: 'M3 7a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2zM16 10l5-3v10l-5-3',
  torrent: 'M12 3v10m0 0-4-4m4 4 4-4M5 15v2a4 4 0 0 0 4 4h6a4 4 0 0 0 4-4v-2',
};
function iconButton(d: string, label: string, onClick: () => void): HTMLButtonElement {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = 'icon-btn';
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

// ---- tabs ----
type TabName = 'downloads' | 'page' | 'settings';
const tabButtons = [...document.querySelectorAll<HTMLButtonElement>('.tabs [role="tab"]')];
function selectTab(name: TabName): void {
  for (const b of tabButtons) b.setAttribute('aria-selected', String(b.dataset.tab === name));
  for (const n of ['downloads', 'page', 'settings'] as TabName[]) $(`tab-${n}`).hidden = n !== name;
  $('batchBar').hidden = name !== 'page' || !links.length;
  void chrome.storage.local.set({ popupTab: name });
  if (name === 'page') void loadPage();
}
for (const b of tabButtons) b.addEventListener('click', () => selectTab(b.dataset.tab as TabName));

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

// ---- downloads tab (polled; rows are reused by id so hover/focus survive the refresh) ----
const STATE_COLOR: Record<StatusEntry['state'], string> = {
  downloading: 'var(--accent)', seeding: '#8e4ec6', queued: 'var(--warn)', paused: 'var(--muted)',
  completed: 'var(--ok)', failed: 'var(--bad)',
};
const rows = new Map<number, { li: HTMLLIElement; key: string }>();

function renderEntry(e: StatusEntry): HTMLLIElement {
  const li = el('li', 'dl');
  li.dataset.state = e.state;
  const tile = el('span', 'tile');
  const color = STATE_COLOR[e.state];
  tile.style.background = `color-mix(in srgb, ${color} 16%, transparent)`;
  tile.style.color = color;
  tile.append(icon(e.kind === 'torrent' ? ICONS.torrent : e.kind === 'media' ? ICONS.video : ICONS.file));

  const name = el('div', 'name', e.name);
  name.title = `${e.name}\n${t('actDetails')}`;
  name.addEventListener('click', () => void command('details', e.id));

  const acts = el('div', 'acts');
  if (e.state === 'downloading' || e.state === 'queued' || e.state === 'seeding')
    acts.append(iconButton(ICONS.pause, t('actPause'), () => void command('pause', e.id)));
  if (e.state === 'paused' || e.state === 'failed')
    acts.append(iconButton(ICONS.play, t('actResume'), () => void command('resume', e.id)));
  if (e.state === 'completed') {
    acts.append(iconButton(ICONS.open, t('actOpen'), () => void command('open', e.id)));
    acts.append(iconButton(ICONS.folder, t('actFolder'), () => void command('folder', e.id)));
  }
  acts.append(iconButton(ICONS.remove, t('actRemove'), () => void command('remove', e.id)));

  const meta = el('div', 'meta');
  const pill = el('span', 'state-pill', t(`state_${e.state}`));
  pill.style.background = `color-mix(in srgb, ${color} 16%, transparent)`;
  pill.style.color = color;
  meta.append(pill);
  const parts: string[] = [];
  if (e.state === 'downloading' && e.totalBytes) parts.push(pct.format(e.progress));
  if (e.state === 'downloading' && e.bytesPerSecond > 0) parts.push(speed(e.bytesPerSecond));
  parts.push(e.state === 'completed' || !e.totalBytes ? bytes(e.totalBytes ?? e.doneBytes) : `${bytes(e.doneBytes)} / ${bytes(e.totalBytes)}`);
  meta.append(el('span', undefined, parts.filter(Boolean).join(' · ')));

  const bar = document.createElement('progress');
  bar.max = 1;
  if (e.state !== 'downloading' || e.totalBytes) bar.value = e.progress; // no value = indeterminate

  li.append(tile, name, acts, meta, bar);
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
  const status = $('status');
  status.dataset.state = outcome;
  status.textContent = t(outcome === 'ok' ? 'popupConnected' : outcome === 'update' ? 'popupUpdateRequired' : 'popupUnreachable');

  const s = reply?.type === 'status' ? reply.payload : undefined;
  $('active').textContent = s ? num.format(s.active) : '–';
  $('queued').textContent = s ? num.format(s.queued) : '–';
  $('speed').textContent = s ? speed(s.bytesPerSecond) : '–';
  // Apps before 0.7 only send `top`: show those as running rows without actions.
  const items: StatusEntry[] = s?.items ?? s?.top.map((x, i) => ({
    id: -1 - i, name: x.name, state: 'downloading', kind: 'http', progress: x.progress,
    bytesPerSecond: x.bytesPerSecond, doneBytes: 0,
  })) ?? [];
  renderList(items);
  // Not reachable: say how to get going instead of showing an empty list.
  $('empty').textContent = t(s ? 'emptyDownloads' : outcome === 'update' ? 'updateRequired' : 'appNotRunning');
  $('empty').hidden = !!s && items.length > 0;
  for (const b of [$('pauseAll'), $('resumeAll')] as HTMLButtonElement[]) b.disabled = !s;
}

(async function poll() {
  await refresh();
  setTimeout(poll, 1000);
})();

// ---- this page: captured media + batch links ----
let currentTab: chrome.tabs.Tab | undefined;
let links: BatchLink[] = [];
let category: LinkCategory | 'all' = 'all';
const selected = new Set<string>();
let pageLoaded = false;

async function activeTab(): Promise<chrome.tabs.Tab | undefined> {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  return tab;
}

async function loadPage(): Promise<void> {
  if (pageLoaded) return;
  pageLoaded = true;
  currentTab = await activeTab();
  const ok = !!currentTab?.id && isHttp(currentTab.url);
  $('pageUnsupported').hidden = ok;
  $('pageContent').hidden = !ok;
  if (!ok) return;
  await Promise.all([loadMedia(), loadLinks()]);
}

async function loadMedia(): Promise<void> {
  const res = await chrome.runtime.sendMessage({ type: 'media-list', tabId: currentTab!.id }).catch(() => undefined);
  const hits: MediaHit[] = res?.hits ?? [];
  $('mediaCount').textContent = hits.length ? String(hits.length) : '';
  $('mediaEmpty').hidden = hits.length > 0;
  $('media').replaceChildren(...hits.map((h) => {
    const li = el('li', 'media');
    const kind = el('span', 'kind', h.kind === 'file' ? h.ext : h.kind);
    kind.dataset.kind = h.kind;
    const info = el('div', 'info');
    const name = el('div', undefined, h.name);
    name.title = h.url;
    const sub = el('small', undefined, [h.kind === 'file' ? t('mediaFile') : t('mediaStream'), bytes(h.size)].filter(Boolean).join(' · '));
    info.append(name, sub);
    const go = el('button', 'primary', t('mediaDownload'));
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
  if (!resources) {
    links = [];
    $('linksEmpty').textContent = t('pageNotReady');
    renderLinks();
    return;
  }
  links = toBatchLinks(resources);
  $('linksEmpty').textContent = t('batchEmpty');
  renderLinks();
}

const CAT_COLOR: Record<LinkCategory, string> = {
  video: '#8b5cf6', audio: '#ec4899', archive: '#f59e0b', document: '#3b82f6',
  program: '#10b981', image: '#06b6d4', torrent: '#14b8a6', other: '#64748b',
};

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
    const ext = el('span', 'ext', l.ext || '—');
    ext.style.background = `color-mix(in srgb, ${CAT_COLOR[l.category]} 16%, transparent)`;
    ext.style.color = CAT_COLOR[l.category];
    const info = el('div', 'info');
    info.append(el('div', undefined, l.name), el('small', undefined, l.url));
    info.title = l.url;
    li.append(box, ext, info);
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
  $('selCount').textContent = t('selectedCount', String(selected.size));
  $<HTMLButtonElement>('batchGo').disabled = selected.size === 0;
  $('batchBar').hidden = $('tab-page').hidden || !links.length;
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

// Badge on the "This page" tab: captured media count, visible from any tab.
void activeTab().then(async (tab) => {
  if (!tab?.id || !isHttp(tab.url)) return;
  const res = await chrome.runtime.sendMessage({ type: 'media-list', tabId: tab.id }).catch(() => undefined);
  const n = res?.hits?.length ?? 0;
  $('pageBadge').hidden = n === 0;
  $('pageBadge').textContent = String(n);
});

// ---- settings ----
void (chrome.storage.local.get({ ...DEFAULT_SETTINGS, popupTab: 'downloads' }) as Promise<Settings & { popupTab: TabName }>).then((s) => {
  $<HTMLInputElement>('captureEnabled').checked = s.captureEnabled;
  $<HTMLInputElement>('minSizeMiB').value = String(s.minSizeMiB);
  $<HTMLInputElement>('showVideoButton').checked = s.showVideoButton;
  $<HTMLInputElement>('showBadge').checked = s.showBadge;
  selectTab(s.popupTab);
});
for (const key of ['captureEnabled', 'showVideoButton', 'showBadge'] as const) {
  $<HTMLInputElement>(key).addEventListener('change', (e) => void chrome.storage.local.set({ [key]: (e.target as HTMLInputElement).checked }));
}
$('minSizeMiB').addEventListener('change', () => {
  const input = $<HTMLInputElement>('minSizeMiB');
  const v = Math.max(0, Number(input.value) || 0);
  input.value = String(v);
  void chrome.storage.local.set({ minSizeMiB: v });
});
