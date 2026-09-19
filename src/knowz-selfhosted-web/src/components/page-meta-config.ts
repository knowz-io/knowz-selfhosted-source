export interface PageMeta {
  section: string
  title: string
  description: string
}

interface PageRegistryEntry {
  match: (pathname: string) => boolean
  meta: PageMeta
}

export const DEFAULT_PAGE_META: PageMeta = {
  section: 'Workspace',
  title: 'Knowz',
  description: 'Capture, search, and organise knowledge on this instance.',
}

export const pageRegistry: ReadonlyArray<PageRegistryEntry> = Object.freeze<PageRegistryEntry[]>([
  {
    match: (p) => p === '/' || p === '/dashboard',
    meta: {
      section: 'Library',
      title: 'Knowledge',
      description: 'Browse, refine, and manage your self-hosted knowledge base.',
    },
  },
  {
    match: (p) => p.startsWith('/knowledge'),
    meta: {
      section: 'Library',
      title: 'Knowledge',
      description: 'Browse, refine, and manage your self-hosted knowledge base.',
    },
  },
  {
    match: (p) =>
      p === '/sync' || p === '/destinations' || p.startsWith('/admin/platform-sync'),
    meta: {
      section: 'Control',
      title: 'Destinations',
      description: 'Connect a Knowz Cloud destination and run manual pull, push, or full syncs.',
    },
  },
  {
    match: (p) => p.startsWith('/search') || p.startsWith('/ask'),
    meta: {
      section: 'Discover',
      title: 'Search',
      description: 'Search, filter, and ask questions across your self-hosted workspace.',
    },
  },
  {
    match: (p) => p.startsWith('/chat'),
    meta: {
      section: 'Workspace',
      title: 'Chat',
      description: 'Talk directly to your vaults with traceable, self-hosted context.',
    },
  },
  {
    match: (p) => p.startsWith('/vaults'),
    meta: {
      section: 'Library',
      title: 'Vaults',
      description: 'Organize collections and shape how knowledge is grouped.',
    },
  },
  {
    match: (p) => p.startsWith('/settings') || p.startsWith('/account'),
    meta: {
      section: 'Control',
      title: 'Settings',
      description: 'Configure connections, account settings, and self-hosted capabilities.',
    },
  },
  {
    match: (p) => p.startsWith('/files'),
    meta: {
      section: 'Assets',
      title: 'Files',
      description: 'Inspect uploaded files and manage attachment-heavy knowledge workflows.',
    },
  },
  {
    match: (p) => p.startsWith('/inbox'),
    meta: {
      section: 'Capture',
      title: 'Inbox',
      description: 'Review staged content before it becomes durable knowledge.',
    },
  },
  {
    match: (p) => p.startsWith('/organize'),
    meta: {
      section: 'Structure',
      title: 'Organize',
      description: 'Navigate tags, topics, and entities across this instance.',
    },
  },
  {
    match: (p) => p.startsWith('/admin'),
    meta: {
      section: 'Administration',
      title: 'Admin',
      description: 'Manage tenants, users, audit history, and self-hosted operations.',
    },
  },
])

export function getPageMeta(pathname: string): PageMeta {
  return pageRegistry.find((entry) => entry.match(pathname))?.meta ?? DEFAULT_PAGE_META
}

/** SH_InstanceCopyHygiene R6/R9 — the product name every route title ends with. */
export const PRODUCT_NAME = 'Knowz Self-Hosted'

/**
 * SH_InstanceCopyHygiene R3 — routes whose page renders its own <h1>. The
 * page-meta strip is suppressed here so a heading is never doubled. The list is
 * enumerated once, centrally, rather than suppressed case-by-case inside pages.
 */
const PAGE_META_SUPPRESSED_MATCHERS: ReadonlyArray<(p: string) => boolean> = Object.freeze([
  (p: string) => p.startsWith('/chat'),
  (p: string) =>
    p === '/sync' || p === '/destinations' || p.startsWith('/admin/platform-sync'),
  (p: string) => p.startsWith('/admin'),
])

export function isPageMetaSuppressed(pathname: string): boolean {
  return PAGE_META_SUPPRESSED_MATCHERS.some((m) => m(pathname))
}

/** SH_InstanceCopyHygiene R9 — `Chat \u00b7 Knowz Self-Hosted`. */
export function getDocumentTitle(pathname: string): string {
  return `${getPageMeta(pathname).title} \u00b7 ${PRODUCT_NAME}`
}
