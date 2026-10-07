import type { CommandInputConfig } from './commandInputs'
import type { StateCategory } from './conventions'
import { ApiError } from './client'
import type { ApiErrorBody } from './types'

export type BlueprintKind = 'CM' | 'EM' | 'Unit'

export type BpTag = {
  /** Stable id, assigned by the server when the blueprint is saved. */
  id?: string
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

/** A state timeout; its alarm is PLC reactive. The time is seconds or a numeric expression such as [PAR.max_time]. */
export type BpTimeout = { time: string; goTo?: string | null; alarm?: string | null; alarmId?: string | null; priority: number; message?: string | null }

export type BpState = {
  /** Identifier used in expressions: [STS.state] == ReadyForConnection. */
  name: string
  /** What the operator sees, for example "Ready for connection"; the name when empty. */
  text?: string | null
  description?: string
  category: number
  code: number
  initial: boolean
  entry: BpAction[]
  run: BpAction[]
  exit: BpAction[]
  timeout?: BpTimeout | null
}

export type BpTransition = { name: string; from: string[]; to: string; guard: string; priority: number }

export type AlarmTrigger = 'State' | 'Range' | 'Timeout'

/** A blueprint alarm: the shared ApolloIQ.Core alarm definition plus the Builder-only latched and onTransition. */
export type BpAlarm = {
  id?: string
  name: string
  priority: number
  message: string
  trigger: AlarmTrigger
  /** Evaluated in the PLC and the logic reacts to it; otherwise SCADA evaluates it. */
  plcReactive: boolean
  condition: string
  input: string
  highCaution: string
  highWarning: string
  highAlarm: string
  lowCaution: string
  lowWarning: string
  lowAlarm: string
  timeoutMode: 'Running' | 'TriggerStop'
  running: string
  triggerExpr: string
  stop: string
  timeout: string
  onDelaySeconds: number
  latched: boolean
  onTransition?: string | null
}

export type BpRole = { name: string; blueprintId: string }

/** One line in a blueprint's interlock list (G-172). Target: '' = this blueprint, else a role path such as BREAKER or GEN1.BREAKER. */
export type BpInterlock = {
  target: string
  kind: 'SwitchOn' | 'SwitchOff' | 'Trip'
  condition: string
  text: string
  alarm?: string | null
  alarmId?: string | null
  priority: number
  escalate: 'None' | 'EM' | 'Unit'
}

export type Blueprint = {
  schema: string
  /** Stable id; absent until the blueprint is first saved. */
  id?: string
  kind: BlueprintKind
  name: string
  /** release.major.minor */
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

export type BpSummary = { id: string; name: string; kind: BlueprintKind; version: string; description: string; errors: number; warnings: number }

export type BpInterface = {
  name: string
  description: string
  requiredFor: BlueprintKind[]
  inScada: boolean
  tags: { group: string; name: string; dataType: string; description: string; initial?: string | null }[]
}

export type BpCatalog = { interfaces: BpInterface[]; categories: StateCategory[]; groups: string[]; dataTypes: string[]; standardAliases: string[] }

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
  get: (id: string) => request<BpResult>('GET', `/api/blueprints/${id}`),
  validate: (blueprint: Blueprint) => request<BpResult>('POST', '/api/blueprints/validate', blueprint),
  /** Creates the blueprint (the server assigns its ids) or saves it under its id. */
  save: (blueprint: Blueprint) => blueprint.id
    ? request<BpResult>('PUT', `/api/blueprints/${blueprint.id}`, blueprint)
    : request<BpResult>('POST', '/api/blueprints', blueprint),
  remove: (id: string) => request<void>('DELETE', `/api/blueprints/${id}`),
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

/** release.major.minor → the next release, major or minor version. */
export function nextVersion(version: string, part: 'release' | 'major' | 'minor'): string {
  const [release, major, minor] = version.split('.').map((n) => Number(n) || 0)
  if (part === 'release') return `${release + 1}.0.0`
  if (part === 'major') return `${release}.${major + 1}.0`
  return `${release}.${major}.${minor + 1}`
}

/** A new alarm with every field of the shared definition. */
export function newAlarm(name: string, priority: number): BpAlarm {
  return {
    name, priority, message: `{instance_name}: ${name}`, trigger: 'State', plcReactive: false, condition: '', input: '',
    highCaution: '', highWarning: '', highAlarm: '', lowCaution: '', lowWarning: '', lowAlarm: '',
    timeoutMode: 'Running', running: '', triggerExpr: '', stop: '', timeout: '', onDelaySeconds: 0, latched: false, onTransition: null,
  }
}
