// Manifest variants derived from public/manifest.json (the Chromium dev build, which keeps "key").
//   storeManifest:   Chrome Web Store / Edge Add-ons; the stores assign their own IDs and reject "key".
//   firefoxManifest: Firefox (MV3). No service workers there, so the same background.js runs as an
//                    event page; the add-on ID must match Protocol.AllowedFirefoxIds on the app side.

export const FIREFOX_ID = 'fluxer@fluxer-download-manager';

// 128 is the ESR baseline; from 127 on, host_permissions are granted at install like in Chromium.
export const FIREFOX_MIN_VERSION = '128.0';

export function storeManifest(manifest) {
  const { key: _key, ...rest } = manifest;
  return rest;
}

export function firefoxManifest(manifest) {
  const { key: _key, minimum_chrome_version: _min, background, ...rest } = manifest;
  return {
    ...rest,
    background: { scripts: [background.service_worker], type: background.type },
    browser_specific_settings: {
      gecko: {
        id: FIREFOX_ID,
        strict_min_version: FIREFOX_MIN_VERSION,
        // Required by AMO for new add-ons: Fluxer sends nothing anywhere except the local app.
        data_collection_permissions: { required: ['none'] },
      },
    },
  };
}
