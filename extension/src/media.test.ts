import { describe, expect, it } from 'vitest';
import { MIN_FILE_BYTES, classifyMedia, mediaKey, mergeHit, pickForVideo, totalSize, type MediaHit } from './media';

const MB = 1024 * 1024;
const res = (url: string, contentType?: string, extra: Partial<Parameters<typeof classifyMedia>[0]> = {}) =>
  classifyMedia({ url, status: 200, contentType, now: 1, ...extra });

describe('classifyMedia', () => {
  it('finds progressive video and audio files', () => {
    expect(res('https://cdn.example.com/v/clip.mp4', 'video/mp4', { contentLength: String(20 * MB) }))
      .toMatchObject({ kind: 'file', ext: 'mp4', name: 'clip.mp4', size: 20 * MB });
    expect(res('https://cdn.example.com/a/song.mp3', 'audio/mpeg', { contentLength: String(5 * MB) }))
      .toMatchObject({ kind: 'file', ext: 'mp3' });
  });
  it('accepts a known extension served as octet-stream, but never as html', () => {
    expect(res('https://x.com/movie.mkv', 'application/octet-stream')).toMatchObject({ kind: 'file', ext: 'mkv' });
    expect(res('https://x.com/movie.mkv', 'text/html')).toBeNull();
  });
  it('labels extension-less media by mime', () => {
    expect(res('https://x.com/stream?id=1', 'video/webm')).toMatchObject({ kind: 'file', ext: 'webm' });
  });
  it('finds HLS and DASH manifests by mime or extension', () => {
    expect(res('https://x.com/master.m3u8', 'application/vnd.apple.mpegurl')).toMatchObject({ kind: 'hls', ext: 'm3u8' });
    expect(res('https://x.com/p?x=1', 'application/x-mpegURL')).toMatchObject({ kind: 'hls' });
    expect(res('https://x.com/index.m3u8', 'text/plain')).toMatchObject({ kind: 'hls' });
    expect(res('https://x.com/manifest.mpd', 'application/dash+xml')).toMatchObject({ kind: 'dash', ext: 'mpd' });
  });
  it('ignores stream segments, small files, failures and non-http', () => {
    expect(res('https://x.com/seg-12.ts', 'video/mp2t')).toBeNull();
    expect(res('https://x.com/chunk-3.m4s', 'video/iso.segment')).toBeNull();
    expect(res('https://x.com/init.m4s', 'video/mp4')).toBeNull();
    expect(res('https://x.com/click.mp3', 'audio/mpeg', { contentLength: String(MIN_FILE_BYTES - 1) })).toBeNull();
    expect(res('https://x.com/a.mp4', 'video/mp4', { status: 404 })).toBeNull();
    expect(res('blob:https://x.com/abc', 'video/mp4')).toBeNull();
    expect(res('https://x.com/page', 'text/html')).toBeNull();
  });
  it('skips YouTube range requests (the page URL is the way there)', () => {
    expect(res('https://rr3---sn-abc.googlevideo.com/videoplayback?itag=22', 'video/mp4')).toBeNull();
  });
  it('reads the full size of a 206 from Content-Range', () => {
    expect(res('https://x.com/v.mp4', 'video/mp4', { status: 206, contentLength: '2', contentRange: 'bytes 0-1/52428800' }))
      .toMatchObject({ size: 52428800 });
  });
  it('decodes file names', () => {
    expect(res('https://x.com/my%20video.mp4', 'video/mp4')?.name).toBe('my video.mp4');
  });
});

describe('totalSize', () => {
  it('prefers the range total, falls back to length, ignores junk', () => {
    expect(totalSize('10', 'bytes 0-9/100')).toBe(100);
    expect(totalSize('10')).toBe(10);
    expect(totalSize('abc')).toBeUndefined();
    expect(totalSize(undefined, 'bytes */*')).toBeUndefined();
  });
});

describe('mediaKey / mergeHit', () => {
  const hit = (url: string, at: number, size?: number): MediaHit => ({ url, kind: 'file', ext: 'mp4', name: 'v.mp4', at, size });

  it('treats range selectors as the same file', () => {
    expect(mediaKey('https://x.com/v.mp4?bytestart=0&byteend=99&token=a')).toBe(mediaKey('https://x.com/v.mp4?bytestart=100&byteend=199&token=a'));
    expect(mediaKey('https://x.com/v.mp4?token=a')).not.toBe(mediaKey('https://x.com/v.mp4?token=b'));
  });
  it('keeps one entry per file, newest first, and remembers a known size', () => {
    let list: MediaHit[] = [];
    list = mergeHit(list, hit('https://x.com/a.mp4', 1, 500));
    list = mergeHit(list, hit('https://x.com/b.mp4', 2));
    list = mergeHit(list, hit('https://x.com/a.mp4?range=1-2', 3));
    expect(list.map((h) => h.at)).toEqual([3, 2]);
    expect(list[0].size).toBe(500);
  });
  it('caps the list', () => {
    let list: MediaHit[] = [];
    for (let i = 0; i < 10; i++) list = mergeHit(list, hit(`https://x.com/${i}.mp4`, i), 4);
    expect(list.map((h) => h.at)).toEqual([9, 8, 7, 6]);
  });
});

describe('pickForVideo', () => {
  const hls: MediaHit = { url: 'https://x.com/master.m3u8', kind: 'hls', ext: 'm3u8', name: 'master.m3u8', at: 2 };
  const dash: MediaHit = { url: 'https://x.com/m.mpd', kind: 'dash', ext: 'mpd', name: 'm.mpd', at: 3 };
  const file: MediaHit = { url: 'https://x.com/v.mp4', kind: 'file', ext: 'mp4', name: 'v.mp4', mime: 'video/mp4', at: 1 };

  it('sends a real file source as a file', () => {
    expect(pickForVideo('https://x.com/clip.webm', [hls])).toEqual({ type: 'file', url: 'https://x.com/clip.webm' });
  });
  it('sends a manifest source as a stream', () => {
    expect(pickForVideo('https://x.com/a.m3u8', [])).toEqual({ type: 'stream', url: 'https://x.com/a.m3u8' });
  });
  it('for blob players prefers HLS, then DASH, then a video file, then the page', () => {
    expect(pickForVideo('blob:https://x.com/1', [dash, hls, file])).toEqual({ type: 'stream', url: hls.url });
    expect(pickForVideo('blob:https://x.com/1', [dash, file])).toEqual({ type: 'stream', url: dash.url });
    expect(pickForVideo('blob:https://x.com/1', [file])).toEqual({ type: 'file', url: file.url });
    expect(pickForVideo('blob:https://x.com/1', [])).toEqual({ type: 'page' });
    expect(pickForVideo(undefined, [])).toEqual({ type: 'page' });
  });
});
