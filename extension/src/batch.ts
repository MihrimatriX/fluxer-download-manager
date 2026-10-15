// Batch download: turn the page's collected resources into a list of downloadable files the popup can
// filter and select. Pure, so it runs under Vitest.
import type { Resource } from './protocol';

export type LinkCategory = 'video' | 'audio' | 'archive' | 'document' | 'program' | 'image' | 'torrent' | 'other';
export const CATEGORIES: LinkCategory[] = ['video', 'audio', 'archive', 'document', 'program', 'image', 'torrent', 'other'];

const BY_EXT: Record<string, LinkCategory> = {};
const add = (cat: LinkCategory, exts: string) => { for (const e of exts.split(' ')) BY_EXT[e] = cat; };
add('video', 'mp4 m4v webm mkv mov avi wmv flv 3gp mpg mpeg ogv ts m3u8');
add('audio', 'mp3 m4a aac ogg oga opus flac wav wma weba');
add('archive', 'zip rar 7z tar gz tgz bz2 xz zst iso img cab');
add('document', 'pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt csv epub mobi djvu');
add('program', 'exe msi msix appx apk dmg pkg deb rpm appimage jar bat');
add('image', 'jpg jpeg png gif webp avif bmp svg tif tiff heic ico');
add('torrent', 'torrent');

/** Extensions that are web pages, not files: never offered as links. */
const PAGE_EXTS = new Set(['html', 'htm', 'shtml', 'xhtml', 'php', 'asp', 'aspx', 'jsp', 'cgi', 'pl', 'cfm']);

export interface BatchLink {
  url: string;
  name: string;
  ext: string;
  category: LinkCategory;
}

export function extOf(url: string): string {
  try {
    const last = new URL(url).pathname.split('/').pop() ?? '';
    const dot = last.lastIndexOf('.');
    return dot > 0 ? last.slice(dot + 1).toLowerCase() : '';
  } catch {
    return '';
  }
}

export const categoryOf = (url: string): LinkCategory => BY_EXT[extOf(url)] ?? 'other';

/** Category of a bare file name ("setup.exe"). */
export function categoryOfName(name: string): LinkCategory {
  const dot = name.lastIndexOf('.');
  return (dot > 0 && BY_EXT[name.slice(dot + 1).toLowerCase()]) || 'other';
}

/** Last path segment of a URL, decoded (the host when the path is empty). */
export function nameOf(url: string): string {
  try {
    const u = new URL(url);
    const last = u.pathname.split('/').filter(Boolean).pop() ?? u.hostname;
    try { return decodeURIComponent(last); } catch { return last; }
  } catch {
    return url;
  }
}

/**
 * Files among the page's resources. Links only count when their path names a file (an extension that is
 * not a web page); media and images always count. Deduplicated, ordered by category then name.
 * `includePages`: also offer extension-less links (for sites whose downloads look like /get?id=1).
 */
export function toBatchLinks(resources: readonly Resource[], includePages = false): BatchLink[] {
  const seen = new Set<string>();
  const out: BatchLink[] = [];
  for (const r of resources) {
    if (!/^https?:/i.test(r.url) || seen.has(r.url)) continue;
    const ext = extOf(r.url);
    if (r.kind === 'link') {
      if (PAGE_EXTS.has(ext)) continue;
      if (!ext && !includePages) continue;
    }
    seen.add(r.url);
    const category = BY_EXT[ext] ?? (r.kind === 'image' ? 'image' : r.kind === 'media' ? 'video' : 'other');
    out.push({ url: r.url, name: nameOf(r.url), ext, category });
  }
  const order = (c: LinkCategory) => CATEGORIES.indexOf(c);
  return out.sort((a, b) => order(a.category) - order(b.category) || a.name.localeCompare(b.name));
}

/** Popup filter: one category (or all) and a case-insensitive text match on name or URL. */
export function filterLinks(links: readonly BatchLink[], category: LinkCategory | 'all', query: string): BatchLink[] {
  const q = query.trim().toLowerCase();
  return links.filter((l) => (category === 'all' || l.category === category)
    && (!q || l.name.toLowerCase().includes(q) || l.url.toLowerCase().includes(q)));
}

export function countByCategory(links: readonly BatchLink[]): Partial<Record<LinkCategory, number>> {
  const out: Partial<Record<LinkCategory, number>> = {};
  for (const l of links) out[l.category] = (out[l.category] ?? 0) + 1;
  return out;
}
