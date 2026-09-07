import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

function source(relative: string) {
  return readFileSync(resolve(__dirname, relative), 'utf8')
}

describe('condensed responsive reachability', () => {
  it('prevents route focus from scrolling content under the sticky header', () => {
    const layout = source('../components/Layout.tsx')
    expect(layout).toContain('window.scrollTo({ top: 0, left: 0 })')
    expect(layout).toContain('focus({ preventScroll: true })')
  })

  it('keeps shell and tab controls reachable instead of root-clipped', () => {
    const header = source('../components/Header.tsx')
    const tabs = source('../components/TabContainer.tsx')
    const contentTabs = source('../components/ContentTabs.tsx')
    expect(header).toMatch(/hidden[^"']*sm:flex/)
    expect(tabs).toMatch(/overflow-x-auto/)
    expect(tabs).toMatch(/flex-nowrap/)
    expect(contentTabs).toMatch(/overflow-x-auto/)
    expect(contentTabs).toMatch(/min-w-full w-max|w-max min-w-full/)
  })

  it('stacks detail actions and constrains chat/dialog overlays on mobile', () => {
    const detail = source('../pages/KnowledgeDetailPage.tsx')
    expect(detail).toMatch(/flex-col[^"']*sm:flex-row/)
    expect(detail).toContain('inset-x-4 sm:left-auto sm:right-6')
    expect(detail).toContain('max-h-[calc(100dvh-2rem)]')
    expect(detail).toContain('z-[60]')
  })
})
