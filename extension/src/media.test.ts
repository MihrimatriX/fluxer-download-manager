import { describe, expect, it } from 'vitest';
import {
  MIN_FILE_BYTES, addHit, applyMaster, classifyMedia, mediaKey, parseHlsMaster, pickForVideo, segmentTemplate, totalSize,
  type MediaHit, type TabMedia,
} from './media';

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

describe('mediaKey / addHit', () => {
  const hit = (url: string, at: number, size?: number, o: Partial<MediaHit> = {}): MediaHit =>
    ({ url, kind: 'file', ext: 'mp4', name: 'v.mp4', at, size, ...o });
  const add = (m: TabMedia, h: MediaHit) => addHit(m, h) ?? m;

  it('treats range selectors as the same file', () => {
    expect(mediaKey('https://x.com/v.mp4?bytestart=0&byteend=99&token=a')).toBe(mediaKey('https://x.com/v.mp4?bytestart=100&byteend=199&token=a'));
    expect(mediaKey('https://x.com/v.mp4?token=a')).not.toBe(mediaKey('https://x.com/v.mp4?token=b'));
  });
  it('keeps one entry per file in first-seen order and learns a size', () => {
    let m: TabMedia = { hits: [] };
    m = add(m, hit('https://x.com/a.mp4', 1));
    m = add(m, hit('https://x.com/b.mp4', 2));
    m = add(m, hit('https://x.com/a.mp4?range=1-2', 3, 500));
    expect(m.hits.map((h) => [h.at, h.size])).toEqual([[2, undefined], [1, 500]]);
  });
  it('returns null when nothing changes, so the caller writes nothing', () => {
    const m = add({ hits: [] }, hit('https://x.com/live.m3u8', 1, undefined, { kind: 'hls' }));
    expect(addHit(m, hit('https://x.com/live.m3u8', 2, undefined, { kind: 'hls' }))).toBeNull();
    const f = add({ hits: [] }, hit('https://x.com/a.mp4', 1, 900));
    expect(addHit(f, hit('https://x.com/a.mp4?bytestart=5', 2, 900))).toBeNull();
  });
  it('caps the list', () => {
    let m: TabMedia = { hits: [] };
    for (let i = 0; i < 10; i++) m = addHit(m, hit(`https://x.com/${i}.mp4`, i), 4) ?? m;
    expect(m.hits.map((h) => h.at)).toEqual([9, 8, 7, 6]);
  });
  it('drops script-fetched segment series (CMAF .mp4 pieces) and ignores the rest', () => {
    const seg = (n: number) => hit(`https://cdn.x.com/v/720/seg-${n}.mp4?t=abc`, n, 400_000 + n, { xhr: true });
    let m: TabMedia = { hits: [] };
    m = add(m, seg(1));
    expect(m.hits).toHaveLength(1);
    m = add(m, seg(2));
    expect(m.hits).toHaveLength(0);
    expect(addHit(m, seg(3))).toBeNull();
    const q = (n: number) => hit(`https://cdn.x.com/media.mp4?segment=${n}`, n, 1000 + n, { xhr: true });
    expect(add(add({ hits: [] }, q(1)), q(2)).hits).toHaveLength(0);
  });
  it('keeps files a <video> loaded, and the same file under a new token', () => {
    const el = (n: number) => hit(`https://x.com/clip${n}.mp4`, n, 5_000_000);
    expect(add(add({ hits: [] }, el(1)), el(2)).hits).toHaveLength(2);
    const tok = (t: string) => hit(`https://x.com/movie.mp4?token=${t}1`, 1, 9_000_000, { xhr: true });
    expect(add(add({ hits: [] }, tok('a')), tok('b')).hits).toHaveLength(2);
  });
  it('segmentTemplate ignores numbers but keeps names and query keys', () => {
    expect(segmentTemplate('https://a.com/p/12/seg-003.m4a?x=1')).toBe('a.com/p/#/seg-#.m4a?x');
    expect(segmentTemplate('https://a.com/video_a.mp4')).not.toBe(segmentTemplate('https://a.com/video_b.mp4'));
  });
});

describe('HLS master playlists', () => {
  const master = [
    '#EXTM3U',
    '#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="en",URI="audio/en.m3u8"',
    '#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,AUDIO="aac"',
    '360/index.m3u8',
    '#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,AUDIO="aac"',
    'https://cdn2.x.com/1080/index.m3u8?sig=1',
    '#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=90000,URI="iframes.m3u8"',
  ].join('\r\n');
  const base = 'https://x.com/v/master.m3u8';

  it('reads variants, renditions and the best resolution', () => {
    expect(parseHlsMaster(master, base)).toEqual({
      variants: ['https://x.com/v/360/index.m3u8', 'https://cdn2.x.com/1080/index.m3u8?sig=1'],
      renditions: ['https://x.com/v/audio/en.m3u8', 'https://x.com/v/iframes.m3u8'],
      height: 1080,
    });
  });
  it('is null for media playlists and non-playlists', () => {
    expect(parseHlsMaster('#EXTM3U\n#EXTINF:6,\nseg1.ts\n', base)).toBeNull();
    expect(parseHlsMaster('<html>', base)).toBeNull();
  });
  it('hides the variants of a listed master and labels it', () => {
    const h = (url: string, at: number): MediaHit => ({ url, kind: 'hls', ext: 'm3u8', name: 'x', at });
    let m: TabMedia = { hits: [h('https://x.com/v/audio/en.m3u8', 3), h('https://x.com/v/360/index.m3u8', 2), h(base, 1)] };
    m = applyMaster(m, base, parseHlsMaster(master, base)!);
    expect(m.hits).toEqual([{ ...h(base, 1), variants: 2, height: 1080 }]);
    expect(addHit(m, h('https://cdn2.x.com/1080/index.m3u8?sig=1', 4))).toBeNull();
    expect(applyMaster(m, base, parseHlsMaster(master, base)!)).toBe(m);
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
  it('prefers a master playlist and the largest video file (ads are short)', () => {
    const variant: MediaHit = { ...hls, url: 'https://x.com/360.m3u8', at: 5 };
    const master: MediaHit = { ...hls, variants: 3, at: 4 };
    expect(pickForVideo('blob:x', [variant, master])).toEqual({ type: 'stream', url: master.url });
    const ad: MediaHit = { ...file, url: 'https://ads.x.com/ad.mp4', size: 2_000_000, at: 9 };
    const main: MediaHit = { ...file, size: 80_000_000 };
    expect(pickForVideo('blob:x', [ad, main])).toEqual({ type: 'file', url: main.url });
  });
  it('looks at what the video\'s own frame loaded first', () => {
    const top: MediaHit = { ...hls, url: 'https://x.com/top.m3u8', frameId: 0, at: 9 };
    const embed: MediaHit = { ...dash, frameId: 7 };
    expect(pickForVideo('blob:x', [top, embed], 7)).toEqual({ type: 'stream', url: embed.url });
    expect(pickForVideo('blob:x', [top, embed], 3)).toEqual({ type: 'stream', url: top.url });
  });
  it('uses what the tab saw for an http source without a media extension', () => {
    const s: MediaHit = { ...hls, url: 'https://x.com/play?id=1' };
    expect(pickForVideo('https://x.com/play?id=1', [s])).toEqual({ type: 'stream', url: s.url });
  });
});

describe('segmentTemplate', () => {
  it('numbers anywhere in the path, but not in the extension', () => {
    expect(segmentTemplate('https://a.com/v1.2/chunk/123')).toBe('a.com/v#.#/chunk/#?');
    expect(segmentTemplate('https://a.com/s/9.m4s')).toBe('a.com/s/#.m4s?');
  });
});
