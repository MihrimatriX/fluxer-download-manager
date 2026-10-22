// Message contracts — mirrors docs/ARCHITECTURE.md §5.2. Keep in sync with Fluxer.Ipc.
export const PROTOCOL_VERSION = 1;
export const NATIVE_HOST = 'com.fluxer.host';
/** Extension → host limit (ARCHITECTURE.md §5.1). */
export const MAX_REQUEST_BYTES = 4 * 1024 * 1024;

export interface Envelope<T extends string, P> {
  protocolVersion: number;
  id: string;          // crypto.randomUUID(); the reply carries the same id
  type: T;
  payload: P;
}

export interface CookieDto {
  name: string; value: string; domain: string; path: string;
  secure: boolean; httpOnly: boolean; expirationDate?: number;
}

export interface BrowserContext {
  referrer?: string;
  userAgent: string;
  cookies: CookieDto[];
  incognito: boolean;
}

// ---- extension → app ----
export type Ping = Envelope<'ping', {}>;

export type DownloadAdd = Envelope<'download.add', BrowserContext & {
  url: string;
  finalUrl?: string;
  fileName?: string;        // name suggested by the browser
  mime?: string;
  totalBytes?: number;      // only when DownloadItem.totalBytes > 0
  source: 'intercept' | 'context-menu' | 'torrent-file';
  torrentPath?: string;     // torrent-file: the .torrent the browser just saved (absolute path)
  tabUrl?: string;
  queue?: 'night';          // context menu "Add to night queue": the app holds it for its night schedule
}>;

export type StatusGet = Envelope<'status.get', {}>;

export type PageSend = Envelope<'page.send', BrowserContext & { pageUrl: string; title?: string }>;
export type PageResources = Envelope<'page.resources', BrowserContext & {
  pageUrl: string;
  resources: { url: string; kind: 'link' | 'image' | 'media'; width?: number; height?: number }[];
}>;

/** Batch download from the popup: opens Fluxer's add dialog with all of them. */
export type DownloadBatch = Envelope<'download.batch', BrowserContext & {
  pageUrl: string;
  items: { url: string; fileName?: string }[];   // http(s) only, 1..1000
}>;

export type CommandAction = 'pause' | 'resume' | 'remove' | 'open' | 'folder' | 'details' | 'pauseAll' | 'resumeAll' | 'show';
/** Popup buttons. `id` is required for the per-download actions. */
export type Command = Envelope<'command', { action: CommandAction; id?: number }>;

export type Request = Ping | DownloadAdd | StatusGet | PageSend | PageResources | DownloadBatch | Command;

// ---- app → extension ----
export type Pong = Envelope<'pong', { appVersion: string }>;
export type DownloadAddResult = Envelope<'download.add.result', {
  status: 'accepted' | 'rejected';   // rejected → extension hands the download back to the browser
  /** 'html': the server sends us a web page, 'torrent': let the browser fetch the .torrent, then send torrentPath. */
  reason?: 'html' | 'torrent' | 'unreachable' | string;
}>;
export type EntryState = 'downloading' | 'seeding' | 'queued' | 'paused' | 'completed' | 'failed';
export interface StatusEntry {
  id: number; name: string; state: EntryState; kind: 'http' | 'torrent' | 'media';
  progress: number;          // 0..1
  bytesPerSecond: number; doneBytes: number; totalBytes?: number;
}
export type Status = Envelope<'status', {
  active: number; queued: number; bytesPerSecond: number;
  top: { name: string; progress: number; bytesPerSecond: number }[]; // at most 5
  items?: StatusEntry[];     // the popup list (app 0.7+): unfinished first, then recent finished
}>;
export type ErrorReply = Envelope<'error', {
  code: 'protocol-mismatch' | 'invalid-message' | 'app-unavailable' | 'internal';
  minProtocolVersion?: number;   // on protocol-mismatch: tell the user to update
  message?: string;
}>;
export type Reply = Pong | DownloadAddResult | Status | ErrorReply;

export type Resource = PageResources['payload']['resources'][number];

export function envelope<R extends Request>(type: R['type'], payload: R['payload']): R {
  return { protocolVersion: PROTOCOL_VERSION, id: crypto.randomUUID(), type, payload } as R;
}

/** One host process per message (no persistent port). Rejects if the host is missing or crashes. */
export function sendNative(msg: Request): Promise<Reply> {
  return chrome.runtime.sendNativeMessage(NATIVE_HOST, msg) as Promise<Reply>;
}
