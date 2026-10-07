import { ApiError } from './client'
import type { ApiErrorBody } from './types'

export type CommandSource = 'Hmi' | 'DigitalInput' | 'Unit'
export type InputKind = 'Pulse' | 'Hold' | 'Button' | 'Switch' | 'Sensor'
export type InputDrives = 'OnOff' | 'On' | 'Off' | 'Toggle'
export type InputInAuto = 'Always' | 'Only' | 'Ignore' | 'Override'
export type InputLocation = 'Any' | 'Local' | 'Remote'

export type CommandInput = {
  name: string
  source: CommandSource
  kind: InputKind
  drives: InputDrives
  on: number | null
  off: number | null
  inAuto: InputInAuto
  location: InputLocation
  commands?: string[] | null
  debounce?: number | null
  stuckTime?: number | null
}

export type CommandInputConfig = { rows: CommandInput[]; level: boolean; selector?: string | null }

export type CommandInputsDto = {
  controlModuleId: string
  path: string
  config: CommandInputConfig
  defaults: CommandInputConfig | null
  hasPair: boolean
  singleCommands: string[]
  selectors: string[]
  inUnit: boolean
  problems: string[]
}

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
  return response.json() as Promise<T>
}

const url = (projectId: string, cmId: string) => `/api/projects/${projectId}/control-modules/${cmId}/command-inputs`

export const commandInputsApi = {
  get: (projectId: string, cmId: string) => request<CommandInputsDto>('GET', url(projectId, cmId)),
  save: (projectId: string, cmId: string, config: CommandInputConfig) => request<CommandInputsDto>('PUT', url(projectId, cmId), config),
  reset: (projectId: string, cmId: string) => request<CommandInputsDto>('POST', `${url(projectId, cmId)}/reset`, {}),
}

export const kindsFor: Record<CommandSource, InputKind[]> = {
  Hmi: ['Pulse', 'Hold'],
  Unit: ['Pulse', 'Hold'],
  DigitalInput: ['Button', 'Switch', 'Sensor'],
}

export function newRow(source: CommandSource, existing: string[], priority: number, debounce: number): CommandInput {
  const base = source === 'Hmi' ? 'HMI' : source === 'Unit' ? 'UNIT' : 'DI'
  let name = base
  for (let i = 2; existing.includes(name); i++) name = `${base}${i}`
  return {
    name, source, kind: kindsFor[source][0], drives: source === 'DigitalInput' ? 'Toggle' : 'OnOff', on: priority, off: priority,
    inAuto: source === 'Unit' ? 'Only' : 'Always', location: 'Any', commands: [], debounce: source === 'DigitalInput' ? debounce : null, stuckTime: null,
  }
}
