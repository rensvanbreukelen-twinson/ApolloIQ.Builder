import type { CommandInputConfig } from './commandInputs'
import type { StateCategory } from './conventions'
import { ApiError } from './client'
import type { ApiErrorBody } from './types'

export type BlueprintKind = 'CM' | 'Unit' | 'EM'
export type AlarmReaction = 'Reactive' | 'NonReactive'

export type BpTag = {
  group: string
  name: string
  dataType: string
  description: string
  unit?: string | null
  min?: number | null
  max?: number | null
  initial?: string | null
  source?: 'LocalIO' | 'External' | null
  primary: boolean
}

export type BpAction = { tag: string; value: string }

export type BpTimeout = { time: string; goTo?: string | null; alarm?: string | null; alarmKind: AlarmReaction; severity: number }

export type BpState = {
  name: string
  category: number
  code: number
  initial: boolean
  entry: BpAction[]
  run: BpAction[]
  exit: BpAction[]
  timeout?: BpTimeout | null
}

export type BpTransition = { name: string; from: string[]; to: string; guard: string; priority: number }

export type BpAlarm = { name: string; kind: AlarmReaction; condition: string; severity: number; message: string; onTransition?: string | null; latched?: boolean }

export type BpRole = { name: string; blueprint: string }

/** One line in a blueprint's interlock list (G-172). Target: '' = this blueprint, else a role path such as BREAKER or GEN1.BREAKER. */
export type BpInterlock = {
  target: string
  kind: 'SwitchOn' | 'SwitchOff' | 'Trip'
  condition: string
  text: string
  alarm?: string | null
  severity: number
  escalate: 'None' | 'EM' | 'Unit'
}

export type Blueprint = {
  schema: string
  kind: BlueprintKind
  name: string
  version: string
  description: string
  interfaces: string[]
  tags: BpTag[]
  roles: BpRole[]
  states: BpState[]
  transitions: BpTransition[]
  always: BpAction[]
  alarms: BpAlarm[]
  plant: BpAction[]
  interlocks?: BpInterlock[]
  aliases?: Record<string, string>
  commandInputs?: CommandInputConfig | null
}

export type BpIssue = { severity: 'Error' | 'Warning'; where: string; message: string }

export type BpSummary = { name: string; kind: BlueprintKind; version: string; description: string; errors: number; warnings: number }

export type BpInterface = { name: string; required: boolean; tags: { group: string; name: string; dataType: string; description: string; initial?: string | null }[] }

export type BpCatalog = { interfaces: BpInterface[]; categories: StateCategory[]; groups: string[]; dataTypes: string[] }

export type BpResult = { blueprint: Blueprint; issues: BpIssue[] }

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const response = await fetch(url, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!response.ok) {
    const parsed = (await response.json().catch(() => null)) as ApiErrorBody | null
    throw new ApiError(response.status, parsed ?? { code: 'http_error', message: `${response.status} ${response.statusText}` })
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export const blueprintApi = {
  catalog: () => request<BpCatalog>('GET', '/api/blueprints/catalog'),
  list: () => request<BpSummary[]>('GET', '/api/blueprints'),
  get: (name: string) => request<BpResult>('GET', `/api/blueprints/${encodeURIComponent(name)}`),
  validate: (blueprint: Blueprint) => request<BpResult>('POST', '/api/blueprints/validate', blueprint),
  save: (name: string, blueprint: Blueprint) => request<BpResult>('PUT', `/api/blueprints/${encodeURIComponent(name)}`, blueprint),
  remove: (name: string) => request<void>('DELETE', `/api/blueprints/${encodeURIComponent(name)}`),
}

export function newBlueprint(kind: BlueprintKind, name: string, initialCategory: number): Blueprint {
  return {
    schema: 'apolloiq.blueprint/1',
    kind,
    name,
    version: '0.1.0',
    description: '',
    interfaces: kind === 'Unit' ? ['Base', 'AutoManual', 'Resettable'] : kind === 'EM' ? ['Base', 'Switchable', 'Resettable', 'Interlocks', 'AutoManual'] : ['Base', 'Switchable', 'Resettable', 'Interlocks'],
    tags: [],
    roles: [],
    states: [{ name: 'Off', category: initialCategory, code: initialCategory, initial: true, entry: [], run: [], exit: [] }],
    transitions: [],
    always: [],
    alarms: [],
    plant: [],
    interlocks: [],
  }
}
