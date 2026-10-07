import type { ReactNode } from 'react'

/** Inline markdown: **bold**, `code`. */
function inline(text: string): ReactNode[] {
  const parts: ReactNode[] = []
  const pattern = /(\*\*[^*]+\*\*|`[^`]+`)/g
  let last = 0
  for (const match of text.matchAll(pattern)) {
    if (match.index > last) parts.push(text.slice(last, match.index))
    const token = match[0]
    parts.push(token.startsWith('**') ? <b key={match.index}>{token.slice(2, -2)}</b> : <code key={match.index}>{token.slice(1, -1)}</code>)
    last = match.index + token.length
  }
  if (last < text.length) parts.push(text.slice(last))
  return parts
}

/** A small, safe markdown view for proposal descriptions: headings, lists, paragraphs, bold and code. */
export function Markdown({ text }: { text: string }) {
  const blocks: ReactNode[] = []
  let list: { ordered: boolean; items: string[] } | null = null
  let paragraph: string[] = []
  const flush = () => {
    if (paragraph.length) blocks.push(<p key={blocks.length}>{inline(paragraph.join(' '))}</p>)
    paragraph = []
    if (list) {
      const items = list.items.map((item, i) => <li key={i}>{inline(item)}</li>)
      blocks.push(list.ordered ? <ol key={blocks.length}>{items}</ol> : <ul key={blocks.length}>{items}</ul>)
    }
    list = null
  }
  for (const raw of text.split('\n')) {
    const line = raw.trimEnd()
    const heading = /^(#{1,4})\s+(.*)$/.exec(line)
    const bullet = /^\s*[-*]\s+(.*)$/.exec(line)
    const numbered = /^\s*\d+[.)]\s+(.*)$/.exec(line)
    if (heading) {
      flush()
      blocks.push(<h4 key={blocks.length}>{inline(heading[2])}</h4>)
    } else if (bullet || numbered) {
      if (paragraph.length || (list && list.ordered !== Boolean(numbered))) flush()
      list ??= { ordered: Boolean(numbered), items: [] }
      list.items.push((bullet ?? numbered)![1])
    } else if (!line.trim()) flush()
    else if (list && /^\s+/.test(raw)) list.items[list.items.length - 1] += ` ${line.trim()}`
    else {
      if (list) flush()
      paragraph.push(line)
    }
  }
  flush()
  return <div className="markdown">{blocks}</div>
}
