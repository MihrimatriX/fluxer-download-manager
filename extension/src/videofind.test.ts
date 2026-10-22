import { describe, expect, it } from 'vitest';
import { videoFromPoint, type PointRoot } from './videofind';

const node = (tagName: string, shadow?: PointRoot) => ({ tagName, shadow }) as unknown as Element & { shadow?: PointRoot };
const root = (...els: Element[]): PointRoot => ({ elementsFromPoint: () => els });
const shadowOf = (e: Element) => (e as Element & { shadow?: PointRoot }).shadow;
const any = () => true;

describe('videoFromPoint', () => {
  it('finds a video under overlays', () => {
    const v = node('VIDEO');
    expect(videoFromPoint(root(node('DIV'), v, node('BODY')), 0, 0, shadowOf, any)).toBe(v);
  });
  it('looks into (nested) shadow roots of web-component players', () => {
    const v = node('VIDEO');
    const inner = root(node('DIV'), v);
    const page = root(node('DIV'), node('SHREDDIT-PLAYER', root(node('MEDIA-CHROME', inner))), node('BODY'));
    expect(videoFromPoint(page, 0, 0, shadowOf, any)).toBe(v);
  });
  it('skips videos the caller rejects (too small) and survives cycles', () => {
    const small = node('VIDEO');
    const self: PointRoot = { elementsFromPoint: () => [small, host] };
    const host = node('X-LOOP', self);
    expect(videoFromPoint(root(host), 0, 0, shadowOf, (v) => v !== small)).toBeUndefined();
  });
});
