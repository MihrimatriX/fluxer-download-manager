// Content script (built as a classic IIFE script). Tracks the Alt key for the capture bypass
// (the service worker can't see keyboard state), collects page resources on request, shows a
// "Download with Fluxer" button over videos and the capture toast (top frame only, on request).
import { collectResources } from './collector';
import { videoFromPoint, type PointRoot } from './videofind';

let alt = false;
function setAlt(down: boolean): void {
  if (down === alt) return;
  alt = down;
  chrome.runtime.sendMessage({ type: 'alt', down }).catch(() => {});
}

addEventListener('keydown', (e) => { if (e.key === 'Alt') setAlt(true); }, true);
addEventListener('keyup', (e) => { if (e.key === 'Alt') setAlt(false); }, true);
// Clicks carry the modifier state too: covers Alt+click in a frame other than the focused one.
addEventListener('mousedown', (e) => setAlt(e.altKey), true);
addEventListener('click', (e) => setAlt(e.altKey), true);
// Window blur only (not element blur): Alt released while another window/app has focus.
addEventListener('blur', (e) => { if (e.target === window) setAlt(false); });

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg?.type === 'collect') {
    sendResponse({ pageUrl: location.href, resources: collectResources(document) });
  }
  if (msg?.type === 'capture-ask' && sender.id === chrome.runtime.id) {
    askToast(msg, sendResponse);
    return true; // answered when the user picks or the countdown ends
  }
});

// ---- video button ----
// Lives in a closed shadow root (page CSS can't touch it), styled through a constructed sheet (not
// subject to the page's CSP) and built without innerHTML (Trusted Types pages).

const MIN_W = 200, MIN_H = 110;
const t = (key: string, ...subs: string[]) => chrome.i18n.getMessage(key, subs);
const SVG_NS = 'http://www.w3.org/2000/svg';

function svgIcon(d: string, viewBox = '0 0 24 24'): SVGSVGElement {
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('viewBox', viewBox);
  svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS(SVG_NS, 'path');
  path.setAttribute('d', d);
  svg.append(path);
  return svg;
}

/** Fullscreen elements live in the top layer: our hosts have to be inside them to show. */
function overlayParent(): Element {
  // A <video> that is itself fullscreen renders no children, so then nothing can follow it.
  const fs = document.fullscreenElement;
  return fs && fs.tagName !== 'VIDEO' ? fs : document.documentElement;
}

let enabled = true;
void chrome.storage.local.get({ showVideoButton: true }).then((s) => { enabled = s.showVideoButton !== false; });
chrome.storage.onChanged.addListener((c, area) => {
  if (area !== 'local' || !c.showVideoButton) return;
  enabled = c.showVideoButton.newValue !== false;
  if (!enabled) hide();
});

const CSS = `
  button {
    all: initial; box-sizing: border-box; display: flex; align-items: center; gap: 6px;
    padding: 7px 13px 7px 10px; border-radius: 999px; cursor: pointer;
    background: rgba(37, 99, 235, .94); color: #fff;
    font: 600 12.5px/1 system-ui, "Segoe UI", sans-serif; white-space: nowrap;
    box-shadow: 0 4px 18px rgba(0, 0, 0, .35); backdrop-filter: blur(8px);
    transition: transform .12s ease, background-color .15s ease, opacity .15s ease;
  }
  button:hover { background: #2563eb; transform: translateY(-1px); }
  button:active { transform: scale(.96); }
  button:focus-visible { outline: 2px solid #fff; outline-offset: 2px; }
  button[data-state="busy"] { opacity: .8; cursor: progress; }
  button[data-state="ok"] { background: #16875a; }
  button[data-state="fail"] { background: #d33a3a; }
  svg { width: 16px; height: 16px; fill: none; stroke: #fff; stroke-width: 2.2; stroke-linecap: round; stroke-linejoin: round; }
`;

let host: HTMLElement | undefined;
let button: HTMLButtonElement;
let label: HTMLSpanElement;
let current: HTMLVideoElement | undefined;
let hideTimer: ReturnType<typeof setTimeout> | undefined;
let busy = false;

function build(): void {
  host = document.createElement('fluxer-video-button');
  host.style.cssText = 'all: initial; position: fixed; z-index: 2147483647; top: 0; left: 0; display: none;';
  const root = host.attachShadow({ mode: 'closed' });
  const sheet = new CSSStyleSheet();
  sheet.replaceSync(CSS);
  root.adoptedStyleSheets = [sheet];

  const svg = svgIcon('M12 4v11m0 0-4.5-4.5M12 15l4.5-4.5M5 20h14');

  button = document.createElement('button');
  button.type = 'button';
  label = document.createElement('span');
  label.textContent = t('videoButton');
  button.append(svg, label);
  button.title = t('videoButtonTip');
  button.addEventListener('click', (e) => { e.preventDefault(); e.stopPropagation(); void send(); });
  button.addEventListener('mouseenter', () => clearTimeout(hideTimer));
  button.addEventListener('mouseleave', () => scheduleHide());
  root.append(button);
  attach();
}

/** Fullscreen players live in the top layer: the button has to be inside the fullscreen element. */
function attach(): void {
  if (!host) return;
  const parent = overlayParent();
  if (parent && host.parentNode !== parent) parent.append(host);
}
document.addEventListener('fullscreenchange', () => { attach(); place(); });

const bigEnough = (v: HTMLVideoElement) => {
  const r = v.getBoundingClientRect();
  return r.width >= MIN_W && r.height >= MIN_H && getComputedStyle(v).visibility !== 'hidden';
};

/** Closed shadow roots too: chrome.dom in Chromium, element.openOrClosedShadowRoot in Firefox content scripts. */
const shadowOf = (e: Element): PointRoot | null | undefined =>
  e.shadowRoot
  ?? (typeof chrome.dom?.openOrClosedShadowRoot === 'function' && e instanceof HTMLElement
    ? chrome.dom.openOrClosedShadowRoot(e) : undefined)
  ?? (e as Element & { openOrClosedShadowRoot?: ShadowRoot | null }).openOrClosedShadowRoot;

function videoAt(x: number, y: number): HTMLVideoElement | undefined {
  const hit = videoFromPoint(document, x, y, shadowOf, bigEnough);
  if (hit) return hit;
  // elementsFromPoint skips pointer-events: none, which some players set on the <video> itself.
  const videos = document.getElementsByTagName('video');
  for (let i = 0; i < videos.length && i < 60; i++) {
    const v = videos[i];
    const r = v.getBoundingClientRect();
    if (x < r.left || x > r.right || y < r.top || y > r.bottom) continue;
    if (bigEnough(v)) return v;
  }
  return undefined;
}

function place(): void {
  if (!host || !current || host.style.display === 'none') return;
  const r = current.getBoundingClientRect();
  if (r.width < MIN_W || r.height < MIN_H || r.bottom < 0 || r.top > innerHeight) return hide();
  const w = host.offsetWidth || 150;
  host.style.left = `${Math.max(r.left + 10, Math.min(r.right, innerWidth) - w - 12)}px`;
  host.style.top = `${Math.max(r.top, 0) + 12}px`;
}

function show(v: HTMLVideoElement): void {
  if (!host) build();
  clearTimeout(hideTimer);
  current = v;
  attach();
  host!.style.display = 'block';
  place();
}

function hide(): void {
  if (busy || !host) return;
  host.style.display = 'none';
  current = undefined;
}

function scheduleHide(ms = 900): void {
  clearTimeout(hideTimer);
  hideTimer = setTimeout(hide, ms);
}

let pending = false;
addEventListener('mousemove', (e) => {
  if (!enabled || pending) return;
  pending = true;
  const { clientX: x, clientY: y } = e;
  requestAnimationFrame(() => {
    pending = false;
    const v = videoAt(x, y);
    if (v) show(v);
    else if (host?.style.display === 'block') scheduleHide();
  });
}, { capture: true, passive: true });
addEventListener('scroll', place, { capture: true, passive: true });
addEventListener('resize', place, { passive: true });

async function send(): Promise<void> {
  if (busy || !current) return;
  busy = true;
  button.dataset.state = 'busy';
  label.textContent = t('videoButtonSending');
  let outcome = 'failed';
  try {
    const reply = await chrome.runtime.sendMessage({ type: 'video-button', src: current.currentSrc || current.src });
    outcome = reply?.outcome ?? 'failed';
  } catch {
    // The extension was reloaded: this page's content script is orphaned until the page reloads.
  }
  button.dataset.state = outcome === 'ok' ? 'ok' : 'fail';
  label.textContent = t(outcome === 'ok' ? 'videoButtonSent' : outcome === 'unreachable' ? 'videoButtonUnreachable' : 'videoButtonFailed');
  setTimeout(() => {
    busy = false;
    delete button.dataset.state;
    label.textContent = t('videoButton');
    scheduleHide(1500);
  }, 2200);
}

// ---- capture toast ----
// The service worker paused a browser download and asks here before handing it to Fluxer: "Download with
// Fluxer" or the countdown answers 'send', "Leave in browser" or × answers 'browser'. Same rules as the button.

const TOAST_CSS = `
  :host { all: initial; }
  .card {
    box-sizing: border-box; width: 340px; border-radius: 10px; overflow: hidden;
    background: rgba(25, 28, 33, .96); backdrop-filter: blur(24px); border: 1px solid rgba(255, 255, 255, .1);
    box-shadow: 0 14px 36px rgba(0, 0, 0, .35); color: #E6E8EC;
    font: 13px/1.35 'Segoe UI Variable Text', 'Segoe UI', system-ui, sans-serif; font-variant-numeric: tabular-nums;
    animation: in .18s ease;
  }
  @keyframes in { from { opacity: 0; transform: translateY(8px); } }
  .body { padding: 10px 10px 10px 12px; display: flex; flex-direction: column; gap: 8px; }
  .row { display: flex; align-items: center; gap: 6px; }
  .logo { width: 16px; height: 16px; border-radius: 4px; background: #2563EB; display: grid; place-items: center; flex: none; }
  .logo svg { width: 10px; height: 10px; stroke: #fff; stroke-width: 3; }
  .title { flex: 1; font-size: 11.5px; color: #8E95A2; }
  button {
    all: initial; box-sizing: border-box; cursor: pointer; font: inherit; color: inherit;
    display: grid; place-items: center; border-radius: 5px; transition: background-color .15s ease;
  }
  button:focus-visible { outline: 2px solid #8CC2FA; outline-offset: 1px; }
  .close, .hold { width: 24px; height: 24px; }
  .close svg { width: 10px; height: 10px; stroke: #C2C7D0; stroke-width: 1.5; }
  .hold svg { width: 12px; height: 12px; stroke: #C2C7D0; stroke-width: 2; }
  .close:hover, .hold:hover, .keep:hover { background: rgba(255, 255, 255, .08); }
  .file { display: flex; align-items: center; gap: 10px; }
  .tile { width: 30px; height: 30px; border-radius: 7px; background: rgba(255, 255, 255, .06); display: grid; place-items: center; flex: none; }
  .tile svg { width: 16px; height: 16px; stroke: #C2C7D0; stroke-width: 1.7; }
  .info { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 1px; }
  .name { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .sub { font-size: 11.5px; color: #8E95A2; }
  .acts { display: flex; gap: 6px; }
  .send { flex: 1; height: 30px; background: #2563EB; color: #fff; font-weight: 600; }
  .send:hover { background: #1D4ED8; }
  .keep { height: 30px; padding: 0 10px; border: 1px solid rgba(255, 255, 255, .12); background: rgba(255, 255, 255, .04); }
  .hint { font-size: 11px; color: #6A717E; }
  .track { height: 2px; background: rgba(255, 255, 255, .08); }
  .bar { height: 2px; background: #2563EB; transform-origin: left; }
  [hidden] { display: none !important; }
  svg { fill: none; stroke-linecap: round; stroke-linejoin: round; }
`;
let toastSheet: CSSStyleSheet | undefined;
/** A paused card waits this long for a choice (background.ts waits a little longer). */
const HOLD_MS = 4 * 60 * 1000;
let closeToast: ((answer: 'send' | 'browser') => void) | undefined;

function el<K extends keyof HTMLElementTagNameMap>(tag: K, cls: string, text?: string): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

function askToast(
  msg: { name: string; size: string; category: string; seconds: number },
  reply: (answer: 'send' | 'browser') => void,
): void {
  // ponytail: one toast at a time; a second capture sends the first one now (its countdown default).
  closeToast?.('send');

  const host = document.createElement('fluxer-capture-toast');
  host.style.cssText = 'all: initial; position: fixed; z-index: 2147483647; right: 20px; bottom: 20px;';
  const root = host.attachShadow({ mode: 'closed' });
  if (!toastSheet) {
    toastSheet = new CSSStyleSheet();
    toastSheet.replaceSync(TOAST_CSS);
  }
  root.adoptedStyleSheets = [toastSheet];

  const card = el('div', 'card');
  card.setAttribute('role', 'dialog');
  card.ariaLabel = t('toastTitle');

  const logo = el('span', 'logo');
  logo.append(svgIcon('M12 4v11M7 10l5 5 5-5M6 20h12'));
  const close = el('button', 'close');
  close.type = 'button';
  close.title = close.ariaLabel = t('toastClose');
  close.append(svgIcon('M2 2l8 8M10 2l-8 8', '0 0 12 12'));
  // Stops the countdown for good: the card then waits for a choice.
  const hold = el('button', 'hold');
  hold.type = 'button';
  hold.title = hold.ariaLabel = t('toastHold');
  hold.append(svgIcon('M9 5v14M15 5v14'));
  const head = el('div', 'row');
  head.append(logo, el('span', 'title', t('toastTitle')), hold, close);

  const tile = el('span', 'tile');
  tile.append(svgIcon('M3 7l9-4 9 4v10l-9 4-9-4zM3 7l9 4 9-4M12 11v10'));
  const info = el('span', 'info');
  const name = el('span', 'name', msg.name);
  name.title = msg.name;
  info.append(name, el('span', 'sub', [msg.size, t(`cat_${msg.category}`)].filter(Boolean).join(' · ')));
  const file = el('div', 'file');
  file.append(tile, info);

  const send = el('button', 'send', t('toastSend'));
  send.type = 'button';
  const keep = el('button', 'keep', t('toastKeep'));
  keep.type = 'button';
  const acts = el('div', 'acts');
  acts.append(send, keep);

  const hint = el('div', 'hint', t('toastHint', String(msg.seconds)));
  const body = el('div', 'body');
  body.append(head, file, acts, hint);
  const bar = el('div', 'bar');
  const track = el('div', 'track');
  track.append(bar);
  card.append(body, track);
  root.append(card);
  overlayParent().append(host);

  // The countdown runs while the pointer is away; hovering pauses it, the pause button stops it.
  const countdown = bar.animate([{ transform: 'scaleX(1)' }, { transform: 'scaleX(0)' }], { duration: msg.seconds * 1000, easing: 'linear', fill: 'forwards' });
  let left = msg.seconds * 1000;
  let startedAt = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let held = false;
  let deadline: ReturnType<typeof setTimeout> | undefined;
  // The service worker gives up waiting after a few seconds unless told the card is paused. Nobody picks for
  // ever, though: a forgotten card still ends the way the countdown would have.
  const tellHeld = () => {
    if (deadline !== undefined) return;
    deadline = setTimeout(() => finish('send'), HOLD_MS);
    chrome.runtime.sendMessage({ type: 'capture-held' }).catch(() => {});
  };
  const run = () => {
    if (held || timer !== undefined) return;
    startedAt = performance.now();
    timer = setTimeout(() => finish('send'), left);
    countdown.play();
  };
  const stop = () => {
    if (timer === undefined) return;
    clearTimeout(timer);
    timer = undefined;
    left = Math.max(0, left - (performance.now() - startedAt));
    countdown.pause();
    tellHeld();
  };
  run();
  card.addEventListener('mouseenter', stop);
  card.addEventListener('mouseleave', run);
  hold.addEventListener('click', (e) => {
    e.preventDefault();
    e.stopPropagation();
    stop();
    held = true;
    hold.hidden = true;
    track.hidden = true;
    hint.textContent = t('toastHeld');
  });
  function finish(answer: 'send' | 'browser'): void {
    if (closeToast !== finish) return;
    closeToast = undefined;
    clearTimeout(timer);
    clearTimeout(deadline);
    host.remove();
    reply(answer);
  }
  closeToast = finish;
  for (const [b, answer] of [[send, 'send'], [keep, 'browser'], [close, 'browser']] as const) {
    b.addEventListener('click', (e) => { e.preventDefault(); e.stopPropagation(); finish(answer); });
  }
}
