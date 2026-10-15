// Content script (built as a classic IIFE script). Tracks the Alt key for the capture bypass
// (the service worker can't see keyboard state) and collects page resources on request.
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
