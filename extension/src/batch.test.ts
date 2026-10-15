import { describe, expect, it } from 'vitest';
import { categoryOf, categoryOfName, countByCategory, filterLinks, toBatchLinks } from './batch';
import type { Resource } from './protocol';

const link = (url: string): Resource => ({ url, kind: 'link' });

describe('categoryOf', () => {
  it('maps extensions, case-insensitively, ignoring the query', () => {
    expect(categoryOf('https://x.com/a/Setup.EXE?x=1')).toBe('program');
    expect(categoryOf('https://x.com/a.tar.gz')).toBe('archive');
    expect(categoryOf('https://x.com/doc.pdf#page=2')).toBe('document');
    expect(categoryOf('https://x.com/a.torrent')).toBe('torrent');
    expect(categoryOf('https://x.com/a.unknownext')).toBe('other');
    expect(categoryOf('not a url')).toBe('other');
  });
});

describe('toBatchLinks', () => {
  it('keeps file links, drops pages and extension-less links', () => {
    const out = toBatchLinks([
      link('https://x.com/files/report.pdf'),
      link('https://x.com/about.html'),
      link('https://x.com/index.php?id=3'),
      link('https://x.com/download?id=3'),
      link('https://x.com/'),
      link('mailto:a@b.c'),
    ]);
    expect(out.map((l) => l.name)).toEqual(['report.pdf']);
  });
  it('offers extension-less links when asked', () => {
    expect(toBatchLinks([link('https://x.com/download?id=3')], true)).toHaveLength(1);
  });
  it('keeps images and media even without an extension', () => {
    const out = toBatchLinks([{ url: 'https://img.x.com/p/123', kind: 'image' }, { url: 'https://x.com/stream/9', kind: 'media' }]);
    expect(out.map((l) => l.category).sort()).toEqual(['image', 'video']);
  });
  it('deduplicates and orders by category then name', () => {
    const out = toBatchLinks([
      link('https://x.com/z.zip'), link('https://x.com/b.mp4'), link('https://x.com/a.zip'), link('https://x.com/z.zip'),
    ]);
    expect(out.map((l) => l.name)).toEqual(['b.mp4', 'a.zip', 'z.zip']);
  });
  it('decodes names', () => {
    expect(toBatchLinks([link('https://x.com/Yeni%20Belge.pdf')])[0].name).toBe('Yeni Belge.pdf');
  });
});

describe('filterLinks / countByCategory', () => {
  const links = toBatchLinks([
    link('https://x.com/a.mp4'), link('https://x.com/b.mp4'), link('https://x.com/Report.pdf'), link('https://cdn.y.org/c.zip'),
  ]);
  it('filters by category and text (name or URL)', () => {
    expect(filterLinks(links, 'video', '')).toHaveLength(2);
    expect(filterLinks(links, 'all', 'report')).toHaveLength(1);
    expect(filterLinks(links, 'all', 'y.org')).toHaveLength(1);
    expect(filterLinks(links, 'document', 'mp4')).toHaveLength(0);
  });
  it('counts per category', () => {
    expect(countByCategory(links)).toEqual({ video: 2, document: 1, archive: 1 });
  });
});

describe('categoryOfName', () => {
  it('maps bare file names', () => {
    expect(categoryOfName('devtool-2.3.0-x64.EXE')).toBe('program');
    expect(categoryOfName('a.tar.gz')).toBe('archive');
    expect(categoryOfName('zip')).toBe('other');
    expect(categoryOfName('README')).toBe('other');
  });
});
