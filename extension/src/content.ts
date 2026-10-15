// Content script (built as a classic IIFE script). Tracks the Alt key for the capture bypass
// (the service worker can't see keyboard state), collects page resources on request, and shows a
// "Download with Fluxer" button over videos.
import { collectResources } from './collector';

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

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type === 'collect') {
    sendResponse({ pageUrl: location.href, resources: collectResources(document) });
  }
});

// ---- video button ----
// Lives in a closed shadow root (page CSS can't touch it), styled through a constructed sheet (not
// subject to the page's CSP) and built without innerHTML (Trusted Types pages).

const MIN_W = 200, MIN_H = 110;
const t = (key: string) => chrome.i18n.getMessage(key);

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
    background: rgba(10, 132, 255, .92); color: #fff;
    font: 600 12.5px/1 system-ui, "Segoe UI", sans-serif; white-space: nowrap;
    box-shadow: 0 4px 18px rgba(0, 0, 0, .35); backdrop-filter: blur(8px);
    transition: transform .12s ease, background-color .15s ease, opacity .15s ease;
  }
  button:hover { background: #0a84ff; transform: translateY(-1px); }
  button:active { transform: scale(.96); }
  button:focus-visible { outline: 2px solid #fff; outline-offset: 2px; }
  button[data-state="busy"] { opacity: .8; cursor: progress; }
  button[data-state="ok"] { background: #30a46c; }
  button[data-state="fail"] { background: #e5484d; }
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

  const svgNs = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(svgNs, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  const path = document.createElementNS(svgNs, 'path');
  path.setAttribute('d', 'M12 4v11m0 0-4.5-4.5M12 15l4.5-4.5M5 20h14');
  svg.append(path);

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
  // A <video> that is itself fullscreen renders no children, so then the button can't follow it.
  const fs = document.fullscreenElement;
  const parent = fs && fs.tagName !== 'VIDEO' ? fs : document.documentElement;
  if (parent && host.parentNode !== parent) parent.append(host);
}
document.addEventListener('fullscreenchange', () => { attach(); place(); });

function videoAt(x: number, y: number): HTMLVideoElement | undefined {
  const videos = document.getElementsByTagName('video');
  for (let i = 0; i < videos.length && i < 60; i++) {
    const v = videos[i];
    const r = v.getBoundingClientRect();
    if (r.width < MIN_W || r.height < MIN_H) continue;
    if (x < r.left || x > r.right || y < r.top || y > r.bottom) continue;
    if (getComputedStyle(v).visibility === 'hidden') continue;
    return v;
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
