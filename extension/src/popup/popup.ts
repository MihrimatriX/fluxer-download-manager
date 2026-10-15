import { DEFAULT_SETTINGS, outcomeOf, type Settings } from '../capture';
import { envelope, sendNative, type Reply, type StatusGet } from '../protocol';

const t = (key: string) => chrome.i18n.getMessage(key);
const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const locale = chrome.i18n.getUILanguage();
document.documentElement.lang = locale;
document.title = t('extName');
for (const el of document.querySelectorAll<HTMLElement>('[data-i18n]')) el.textContent = t(el.dataset.i18n!);

const num = new Intl.NumberFormat(locale, { maximumFractionDigits: 1 });
const pct = new Intl.NumberFormat(locale, { style: 'percent' });

function speed(bps: number): string {
  const units = ['B', 'KB', 'MB', 'GB'];
  let v = bps, i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${num.format(v)} ${units[i]}${t('perSecond')}`;
}

// ---- status (polled while the popup is open; each call is one short-lived host process) ----
async function refresh(): Promise<void> {
  let reply: Reply | undefined;
  try {
    reply = await sendNative(envelope<StatusGet>('status.get', {}));
  } catch {
    reply = undefined;
  }
  const outcome = outcomeOf(reply, 'status');
  const status = $('status');
  status.dataset.state = outcome;
  status.textContent = t(outcome === 'ok' ? 'popupConnected' : outcome === 'update' ? 'popupUpdateRequired' : 'popupUnreachable');

  const s = reply?.type === 'status' ? reply.payload : undefined;
  $('active').textContent = s ? num.format(s.active) : '–';
  $('queued').textContent = s ? num.format(s.queued) : '–';
  $('speed').textContent = s ? speed(s.bytesPerSecond) : '–';
  const items = s?.top.slice(0, 5) ?? [];
  $('top').replaceChildren(...items.map((it) => {
    const li = document.createElement('li');
    const row = document.createElement('div');
    row.className = 'name';
    const name = document.createElement('span');
    name.textContent = it.name;
    name.title = it.name;
    const info = document.createElement('span');
    info.textContent = `${pct.format(it.progress)} · ${speed(it.bytesPerSecond)}`;
    row.append(name, info);
    const bar = document.createElement('progress');
    bar.max = 1;
    bar.value = it.progress; // progress is a 0..1 fraction
    li.append(row, bar);
    return li;
  }));
  $('empty').hidden = !s || items.length > 0;
}

(async function poll() {
  await refresh();
  setTimeout(poll, 1000);
})();

// ---- quick settings ----
const capture = $<HTMLInputElement>('captureEnabled');
const minSize = $<HTMLInputElement>('minSizeMiB');
void (chrome.storage.local.get(DEFAULT_SETTINGS) as Promise<Settings>).then((s) => {
  capture.checked = s.captureEnabled;
  minSize.value = String(s.minSizeMiB);
});
capture.addEventListener('change', () => void chrome.storage.local.set({ captureEnabled: capture.checked }));
minSize.addEventListener('change', () => {
  const v = Math.max(0, Number(minSize.value) || 0);
  minSize.value = String(v);
  void chrome.storage.local.set({ minSizeMiB: v });
});
