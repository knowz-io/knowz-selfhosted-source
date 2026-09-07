/**
 * Simple markdown-to-HTML formatter.
 * Handles headings, bold, italic, inline code, code blocks, lists, and line breaks.
 * No external dependencies required.
 */

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}

export function formatMarkdown(text: string): string {
  const lines = text.split('\n')
  const result: string[] = []
  let inCodeBlock = false
  let listType: 'ul' | 'ol' | null = null

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]

    // Fenced code blocks
    if (line.trimStart().startsWith('```')) {
      if (inCodeBlock) {
        result.push('</code></pre>')
        inCodeBlock = false
      } else {
        if (listType) {
          result.push(`</${listType}>`)
          listType = null
        }
        result.push('<pre><code>')
        inCodeBlock = true
      }
      continue
    }

    if (inCodeBlock) {
      result.push(escapeHtml(line))
      continue
    }

    // Empty line
    if (line.trim() === '') {
      if (listType) {
        result.push(`</${listType}>`)
        listType = null
      }
      result.push('<br>')
      continue
    }

    // Headings
    const headingMatch = line.match(/^(#{1,6})\s+(.+)$/)
    if (headingMatch) {
      if (listType) {
        result.push(`</${listType}>`)
        listType = null
      }
      const level = headingMatch[1].length
      result.push(`<h${level}>${formatInline(headingMatch[2])}</h${level}>`)
      continue
    }

    // Unordered list items
    const listMatch = line.match(/^(\s*[-*+])\s+(.+)$/)
    if (listMatch) {
      if (listType === 'ol') {
        result.push('</ol>')
        listType = null
      }
      if (!listType) {
        result.push('<ul>')
        listType = 'ul'
      }
      result.push(`<li>${formatInline(listMatch[2])}</li>`)
      continue
    }

    // Ordered list items
    const orderedMatch = line.match(/^(\s*\d+\.)\s+(.+)$/)
    if (orderedMatch) {
      if (listType === 'ul') {
        result.push('</ul>')
        listType = null
      }
      if (!listType) {
        result.push('<ol>')
        listType = 'ol'
      }
      result.push(`<li>${formatInline(orderedMatch[2])}</li>`)
      continue
    }

    // Regular paragraph line
    if (listType) {
      result.push(`</${listType}>`)
      listType = null
    }
    result.push(`<p>${formatInline(line)}</p>`)
  }

  if (inCodeBlock) {
    result.push('</code></pre>')
  }
  if (listType) {
    result.push(`</${listType}>`)
  }

  return result.join('\n')
}

function formatInline(text: string): string {
  let html = escapeHtml(text)

  // Inline code (must come before bold/italic to avoid conflicts)
  html = html.replace(/`([^`]+)`/g, '<code>$1</code>')

  // Bold
  html = html.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
  html = html.replace(/__(.+?)__/g, '<strong>$1</strong>')

  // Italic
  html = html.replace(/\*(.+?)\*/g, '<em>$1</em>')
  html = html.replace(/_(.+?)_/g, '<em>$1</em>')

  // Strikethrough
  html = html.replace(/~~(.+?)~~/g, '<del>$1</del>')

  return html
}
