import { useEffect, useState } from 'react'

export type StateCategory = {
  code: number
  name: string
  label: string
  description: string
  isOn: boolean
  headsOn: boolean
  moving: boolean
  ready: boolean
  fault: boolean
  reporting: boolean
  column: number
}

export type SeverityBand = { name: string; from: number; to: number }

export type Conventions = {
  categorySize: number
  unavailableCode: number
  states: StateCategory[]
  severityMin: number
  severityMax: number
  severityBands: SeverityBand[]
  priorityMin: number
  priorityMax: number
  maxDebounceSeconds: number
  defaultDebounceSeconds: number
  defaultSeverity: number
}

let cached: Promise<Conventions> | null = null

export function loadConventions(): Promise<Conventions> {
  cached ??= fetch('/api/conventions').then((r) => {
    if (!r.ok) throw new Error(`${r.status} ${r.statusText}`)
    return r.json() as Promise<Conventions>
  }).catch((reason) => { cached = null; throw reason })
  return cached
}

export function useConventions(): Conventions | null {
  const [value, setValue] = useState<Conventions | null>(null)
  useEffect(() => {
    let alive = true
    loadConventions().then((c) => { if (alive) setValue(c) }).catch(() => undefined)
    return () => { alive = false }
  }, [])
  return value
}

export function categoryOf(conventions: Conventions | null, code: number): StateCategory | undefined {
  if (!conventions || !Number.isFinite(code)) return undefined
  const base = code === conventions.unavailableCode ? code : Math.floor(code / conventions.categorySize) * conventions.categorySize
  return conventions.states.find((s) => s.code === base)
}

export function stateKind(category: StateCategory | undefined): string {
  if (!category) return 'unknown'
  if (!category.reporting) return 'unavailable'
  if (category.fault) return 'fault'
  if (category.moving) return 'moving'
  if (category.isOn) return 'on'
  return category.ready ? 'ready' : 'off'
}

export function stateClass(conventions: Conventions | null, code: number, prefix = 'sim-state'): string {
  return `${prefix} ${prefix}-${stateKind(categoryOf(conventions, code))}`
}

export function firstCategory(conventions: Conventions | null, match: (c: StateCategory) => boolean): StateCategory | undefined {
  return conventions?.states.find(match)
}
