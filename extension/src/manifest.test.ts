/// <reference types="vite/client" />
import { describe, expect, it } from 'vitest';
import { FIREFOX_ID, firefoxManifest, storeManifest } from '../scripts/manifest.mjs';

type Manifest = Record<string, any>;
const raw = import.meta.glob<string>(['../public/manifest.json', '../../src/Fluxer.Ipc/Protocol.cs'], { eager: true, query: '?raw', import: 'default' });
const manifest: Manifest = JSON.parse(raw['../public/manifest.json']);

describe('storeManifest', () => {
  it('drops the dev key and keeps everything else', () => {
    const m = storeManifest(manifest);
    expect(m.key).toBeUndefined();
    expect(m.background).toEqual(manifest.background);
    expect(m.permissions).toEqual(manifest.permissions);
  });
});

describe('firefoxManifest', () => {
  const m = firefoxManifest(manifest) as Manifest & { browser_specific_settings: { gecko: { id: string } } };

  it('runs the background script as an event page', () => {
    expect(m.background).toEqual({ scripts: ['background.js'], type: 'module' });
  });

  it('carries the add-on ID the native host allows', () => {
    expect(m.browser_specific_settings.gecko.id).toBe(FIREFOX_ID);
    expect(raw['../../src/Fluxer.Ipc/Protocol.cs']).toContain(`AllowedFirefoxIds = ["${FIREFOX_ID}"]`);
  });

  it('drops Chromium-only keys and keeps permissions, content scripts and popup', () => {
    expect(m.key).toBeUndefined();
    expect(m.minimum_chrome_version).toBeUndefined();
    expect(m.permissions).toEqual(manifest.permissions);
    expect(m.host_permissions).toEqual(manifest.host_permissions);
    expect(m.content_scripts).toEqual(manifest.content_scripts);
    expect(m.action).toEqual(manifest.action);
    expect(m.version).toBe(manifest.version);
  });

  it('does not change the Chromium manifest', () => {
    expect(manifest.background.service_worker).toBe('background.js');
    expect(manifest.key).toBeDefined();
  });
});
