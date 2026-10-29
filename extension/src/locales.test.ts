// Every message key the extension uses exists in every locale (a missing key renders as an empty string).
/// <reference types="vite/client" />
import { describe, expect, it } from 'vitest';
import { CATEGORIES } from './batch';

const files = import.meta.glob<{ default: Record<string, { message: string }> }>('../public/_locales/*/messages.json', { eager: true });
const messages = Object.fromEntries(Object.entries(files).map(([path, m]) => [path.split('/').at(-2)!, m.default]));
const locales = Object.keys(messages);

const raw = import.meta.glob<string>(
  ['./background.ts', './content.ts', './popup/popup.ts', './popup/index.html', '../public/manifest.json'],
  { eager: true, query: '?raw', import: 'default' },
);
const sources = Object.values(raw).join('\n');

const used = new Set<string>([
  ...[...sources.matchAll(/\bt\('([A-Za-z_]+)'/g)].map((m) => m[1]),
  ...[...sources.matchAll(/data-i18n(?:-[a-z]+)?="([A-Za-z_]+)"/g)].map((m) => m[1]),
  ...[...sources.matchAll(/__MSG_([A-Za-z_]+)__/g)].map((m) => m[1]),
  ...[...sources.matchAll(/notify(?:Outcome)?\('([A-Za-z_]+)'/g)].map((m) => m[1]),
  // picked by a condition inside t(…)
  'emptyDownloads', 'appNotRunning', 'updateRequired', 'sendUnreachable', 'sendFailed',
  'popupConnected', 'popupUpdateRequired', 'popupUnreachable', 'videoButtonSent', 'videoButtonFailed', 'videoButtonUnreachable',
  'batchEmpty', 'pageNotReady', 'siteOn', 'siteOff', 'skewExtensionOld', 'skewAppOld',
  'pauseAll', 'resumeAll', 'capturePause', 'capturePauseTip', 'captureResume', 'captureResumeTip',
  // built at runtime: t(`state_${…}`), t(`cat_${…}`)
  ...['downloading', 'seeding', 'queued', 'paused', 'completed', 'failed'].map((s) => `state_${s}`),
  ...['all', ...CATEGORIES].map((c) => `cat_${c}`),
]);

describe('locales', () => {
  it('finds the keys in the sources', () => {
    expect(used.size).toBeGreaterThan(60);
  });
  for (const locale of locales) {
    it(`${locale} has every used key`, () => {
      expect([...used].filter((k) => !messages[locale][k]?.message)).toEqual([]);
    });
  }
  it('locales have the same keys', () => {
    const [first, ...rest] = locales;
    for (const l of rest) expect(Object.keys(messages[l]).sort()).toEqual(Object.keys(messages[first]).sort());
  });
});
