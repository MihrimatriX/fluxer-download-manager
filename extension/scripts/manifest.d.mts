type Manifest = Record<string, any>;
export declare const FIREFOX_ID: string;
export declare const FIREFOX_MIN_VERSION: string;
export declare function storeManifest(manifest: Manifest): Manifest;
export declare function firefoxManifest(manifest: Manifest): Manifest;
