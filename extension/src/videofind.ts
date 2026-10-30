// Finding the <video> under the pointer for the in-page button. Pure over anything with elementsFromPoint
// (Document, ShadowRoot), so it runs under Vitest with fakes.

export interface PointRoot { elementsFromPoint(x: number, y: number): Element[] }
export type ShadowOf = (e: Element) => PointRoot | null | undefined;

/**
 * The topmost accepted <video> at (x, y), looking into shadow roots on the way: players built as web
 * components (Reddit's, many news sites') keep the <video> where document queries can't see it, and
 * overlays (controls, ads, a transparent div) sit on top of it. Breadth first, at most `maxRoots` roots.
 */
export function videoFromPoint(
  root: PointRoot, x: number, y: number, shadowOf: ShadowOf, accept: (v: HTMLVideoElement) => boolean, maxRoots = 12,
): HTMLVideoElement | undefined {
  const queue: PointRoot[] = [root];
  const seen = new Set<PointRoot>(queue);
  for (let i = 0; i < queue.length && i < maxRoots; i++) {
    for (const e of queue[i].elementsFromPoint(x, y)) {
      if (e.tagName === 'VIDEO' && accept(e as HTMLVideoElement)) return e as HTMLVideoElement;
      const sr = shadowOf(e);
      if (sr && !seen.has(sr)) { seen.add(sr); queue.push(sr); }
    }
  }
  return undefined;
}
