import { useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { CSSProperties } from 'react'
import { createPortal } from 'react-dom'

type Props = {
  value: string
  onChange: (value: string) => void
  suggestions: string[]
  bracketed?: boolean
  className?: string
  placeholder?: string
  ariaLabel?: string
  single?: boolean
  onAccept?: (value: string) => void
  states?: (reference: string) => string[]
}


const groupRank: Record<string, number> = { STS: 1, FIN: 2, CMD: 3, OUT: 4, LOK: 5, INT: 6, PAR: 7, SET: 8, PMT: 9, ALM: 10 }

function rank(path: string) {
  const parts = path.split('.')
  if (parts[parts.length - 1].startsWith('is_')) return 0
  const group = parts.find((p) => p in groupRank)
  return group ? groupRank[group] : 5
}

function ordered(list: string[]) {
  return [...list].sort((a, b) => rank(a) - rank(b) || a.length - b.length || a.localeCompare(b))
}

const stateContext = /\[?(?:([A-Za-z0-9_.:{}]+?)\.)?STS\.state\]?\s*(?:=|<>)\s*([A-Za-z_]\w*)?$/

function stateToken(text: string, caret: number) {
  const match = stateContext.exec(text.slice(0, caret))
  if (!match) return null
  const token = match[2] ?? ''
  return { reference: match[1] ?? '', start: caret - token.length, token }
}

type Item = { label: string; value: string; branch: boolean }

function leaf(value: string): Item {
  return { label: value, value, branch: false }
}

function search(suggestions: string[], token: string): Item[] {
  const needle = token.trim().toLowerCase()
  if (!needle) return []
  const starts = suggestions.filter((s) => s.toLowerCase().startsWith(needle))
  const parts = suggestions.filter((s) => !s.toLowerCase().startsWith(needle) && s.toLowerCase().split('.').some((p) => p.startsWith(needle)))
  const contains = suggestions.filter((s) => !starts.includes(s) && !parts.includes(s) && s.toLowerCase().includes(needle))
  return [...ordered(starts), ...ordered(parts), ...ordered(contains)].filter((s) => s.toLowerCase() !== needle).map(leaf)
}

function branches(suggestions: string[], token: string): Item[] {
  const cut = token.lastIndexOf('.') + 1
  const prefix = token.slice(0, cut).toLowerCase()
  const partial = token.slice(cut).toLowerCase()
  const found = new Map<string, Item>()
  for (const s of suggestions) {
    if (!s.toLowerCase().startsWith(prefix)) continue
    const rest = s.slice(cut)
    const dot = rest.indexOf('.')
    const segment = dot < 0 ? rest : rest.slice(0, dot)
    if (!segment || !segment.toLowerCase().startsWith(partial)) continue
    const branch = dot >= 0
    if (!branch && segment.toLowerCase() === partial) continue
    const key = `${segment}|${branch}`
    if (!found.has(key)) found.set(key, { label: segment, value: s.slice(0, cut) + segment, branch })
  }
  return [...found.values()].sort((a, b) => rank(a.value) - rank(b.value) || Number(a.branch) - Number(b.branch) || a.label.localeCompare(b.label))
}

function currentToken(text: string, caret: number, bracketed: boolean) {
  const before = text.slice(0, caret)
  if (bracketed) {
    const open = before.lastIndexOf('[')
    if (open >= 0 && before.lastIndexOf(']') < open) return { start: open, token: before.slice(open + 1) }
  }
  const match = /[A-Za-z0-9_.]*$/.exec(before)
  const token = match ? match[0] : ''
  return { start: caret - token.length, token }
}

export function ExpressionInput({ value, onChange, suggestions, bracketed = false, className, placeholder, ariaLabel, single = false, onAccept, states }: Props) {
  const input = useRef<HTMLInputElement>(null)
  const [caret, setCaret] = useState(0)
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)

  const state = !single && states ? stateToken(value, caret) : null
  const stateReference = state?.reference ?? null
  const stateNames = useMemo(() => (stateReference !== null && states ? states(stateReference) : []), [stateReference, states])
  const { start, token } = state ?? (single ? { start: 0, token: value } : currentToken(value, caret, bracketed))
  const matches = useMemo<Item[]>(() => {
    if (stateReference !== null && stateNames.length > 0) {
      const needle = token.toLowerCase()
      return stateNames.filter((n) => n.toLowerCase().startsWith(needle) && n.toLowerCase() !== needle).map(leaf)
    }
    if (single) return search(suggestions, token)
    const tree = branches(suggestions, token)
    if (tree.length > 0 || token.includes('.')) return tree
    return token.trim() ? search(suggestions, token) : []
  }, [suggestions, token, stateReference, stateNames, single])

  const accept = (choice: Item) => {
    const end = single ? value.length : caret
    const isState = state !== null && stateNames.length > 0
    const bracket = bracketed && !isState ? '[' : ''
    const after = choice.branch ? value.slice(end).replace(/^[A-Za-z0-9_.]*/, '') : value.slice(end).replace(isState ? /^\w*/ : /^[A-Za-z0-9_.]*\]?/, '')
    const inserted = choice.branch ? `${bracket}${choice.value}.` : bracketed && !isState ? `[${choice.value}]` : choice.value
    const next = value.slice(0, start) + inserted + after
    onChange(next)
    setOpen(choice.branch)
    setActive(0)
    if (!choice.branch) onAccept?.(next)
    const position = start + inserted.length
    requestAnimationFrame(() => {
      input.current?.focus()
      input.current?.setSelectionRange(position, position)
      setCaret(position)
    })
  }

  const shown = open && matches.length > 0
  const [place, setPlace] = useState<CSSProperties | null>(null)

  useLayoutEffect(() => {
    if (!shown) return
    const position = () => {
      const rect = input.current?.getBoundingClientRect()
      if (!rect) return
      const below = window.innerHeight - rect.bottom - 8
      const above = rect.top - 8
      const up = below < 160 && above > below
      const room = Math.max(80, Math.min(300, up ? above : below))
      setPlace({ left: rect.left, width: Math.max(rect.width, 220), maxHeight: room,
        ...(up ? { bottom: window.innerHeight - rect.top + 2 } : { top: rect.bottom + 2 }) })
    }
    position()
    window.addEventListener('scroll', position, true)
    window.addEventListener('resize', position)
    return () => { window.removeEventListener('scroll', position, true); window.removeEventListener('resize', position) }
  }, [shown])

  return (
    <div className="expr-input">
      <input ref={input} className={className} value={value} placeholder={placeholder} aria-label={ariaLabel} autoComplete="off"
        role="combobox" aria-expanded={shown} aria-autocomplete="list"
        onChange={(e) => { onChange(e.target.value); setCaret(e.target.selectionStart ?? e.target.value.length); setOpen(true); setActive(0) }}
        onClick={(e) => setCaret(e.currentTarget.selectionStart ?? 0)}
        onKeyUp={(e) => { if (!['ArrowDown', 'ArrowUp', 'Enter', 'Tab', 'Escape'].includes(e.key)) setCaret(e.currentTarget.selectionStart ?? 0) }}
        onBlur={() => window.setTimeout(() => setOpen(false), 150)}
        onKeyDown={(e) => {
          if (!shown) return
          if (e.key === 'ArrowDown') { e.preventDefault(); setActive((a) => (a + 1) % matches.length) }
          else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((a) => (a - 1 + matches.length) % matches.length) }
          else if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); accept(matches[active] ?? matches[0]) }
          else if (e.key === 'Escape') setOpen(false)
        }} />
      {shown && place && createPortal(
        <ul className="expr-suggestions" role="listbox" style={place} onMouseDown={(e) => e.preventDefault()}>
          {matches.map((m, i) => (
            <li key={`${m.value}|${m.branch}`} role="option" aria-selected={i === active} ref={i === active ? (el) => el?.scrollIntoView({ block: 'nearest' }) : undefined} className={i === active ? 'expr-active' : undefined}
              onMouseDown={(e) => { e.preventDefault(); accept(m) }}>{m.label}{m.branch && <span className="expr-branch">.</span>}</li>
          ))}
        </ul>, document.body
      )}
    </div>
  )
}
