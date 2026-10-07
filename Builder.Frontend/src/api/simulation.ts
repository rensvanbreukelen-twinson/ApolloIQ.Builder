import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError } from './client'
import type { ApiErrorBody } from './types'

export type SimValue = boolean | number | string | null

export type SimStatus = {
  status: 'Paused' | 'Running'
  cycle: number
  timeSeconds: number
  cycleSeconds: number
  speed: number
  errors: number
}

export type SimControlModule = {
  id: string
  path: string
  type: string
  state: number
  stateName: string
  /** The state's display text (the object's own state text, else the category). */
  stateText?: string
  stateSeconds: number
  enteredCycle?: number
}

export type SimulationState = {
  status: SimStatus
  errors: string[]
  controlModules: SimControlModule[]
}

export type SimTag = {
  id: string
  path: string
  symbolKey: string
  group: string
  dataType: string
  enumType: string | null
  value: SimValue
  good: boolean
  forced: boolean
}

export type SimTagValue = { id: string; path: string; value: SimValue; good: boolean; forced: boolean }

export type SimChangeBatch = { cycle: number; timeSeconds: number; values: SimTagValue[] }

export type SimEvent = { key: number; cycle: number; text: string; kind: 'state' | 'diagnostic' }

type StateChangedMessage = { cycle: number; controlModule: string; from: number; to: number; transition: string; toName: string | null }

type DiagnosticMessage = { cycle: number; controlModule: string; message: string }

/** An alarm in the simulation: PLC reactive ones from the PLC logic, the others evaluated as SCADA will. */
export type SimAlarm = {
  object: string
  name: string
  priority: number
  level: 'Caution' | 'Warning' | 'Alarm'
  message: string
  plcReactive: boolean
  source: string
  active: boolean
  rangeLevel: 'Caution' | 'Warning' | 'Alarm' | null
}

export type SimLink = { id: string; from: string; to: string; protocol: string; class: string; down: boolean; tags: number }

export type TcpLink = { port: number | null; clients: number; projectId: string | null }

export const writableGroups = new Set(['CMD', 'PAR', 'SET', 'LOK', 'FIN'])

export const isWritable = (group: string, path: string) => writableGroups.has(group) || (group === 'ALM' && path.endsWith('.enabled'))

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

const base = (projectId: string) => `/api/projects/${projectId}/simulation`

export const simulationApi = {
  state: (projectId: string) => request<SimulationState>('GET', base(projectId)),
  tags: (projectId: string) => request<SimTag[]>('GET', `${base(projectId)}/tags`),
  alarms: (projectId: string) => request<SimAlarm[]>('GET', `${base(projectId)}/alarms`),
  start: (projectId: string) => request<SimulationState>('POST', `${base(projectId)}/start`),
  pause: (projectId: string) => request<SimulationState>('POST', `${base(projectId)}/pause`),
  step: (projectId: string, cycles: number) => request<SimulationState>('POST', `${base(projectId)}/step`, { cycles }),
  reset: (projectId: string) => request<SimulationState>('POST', `${base(projectId)}/reset`),
  speed: (projectId: string, speed: number) => request<SimulationState>('PUT', `${base(projectId)}/speed`, { speed }),
  write: (projectId: string, tag: string, value: SimValue) => request<SimTagValue>('POST', `${base(projectId)}/write`, { tag, value }),
  force: (projectId: string, tag: string, value: SimValue) => request<SimTagValue>('POST', `${base(projectId)}/force`, { tag, value }),
  unforce: (projectId: string, tag: string) => request<SimTagValue>('POST', `${base(projectId)}/unforce`, { tag }),
  unforceAll: (projectId: string) => request<void>('POST', `${base(projectId)}/unforce-all`),
  tcp: () => request<TcpLink>('GET', '/api/simulator/tcp'),
  links: (projectId: string) => request<SimLink[]>('GET', `${base(projectId)}/links`),
  setLink: (projectId: string, linkId: string, down: boolean) => request<SimLink[]>('POST', `${base(projectId)}/links/${linkId}`, { down }),
  serve: (projectId: string) => request<TcpLink>('POST', `${base(projectId)}/serve`),
  quality: (projectId: string, tag: string, bad: boolean) => request<SimTagValue>('POST', `${base(projectId)}/quality`, { tag, bad }),
}

function withEnteredCycles(state: SimulationState): SimulationState {
  const { cycle, cycleSeconds } = state.status
  return {
    ...state,
    controlModules: state.controlModules.map((cm) => ({ ...cm, enteredCycle: cycle - Math.round(cm.stateSeconds / cycleSeconds) })),
  }
}

export function useSimulation(projectId: string, revision: number) {
  const [state, setState] = useState<SimulationState | null>(null)
  const [tags, setTags] = useState<SimTag[]>([])
  const [events, setEvents] = useState<SimEvent[]>([])
  const [connected, setConnected] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const eventKey = useRef(0)

  const applyValues = useCallback((values: SimTagValue[]) => {
    const byId = new Map(values.map((v) => [v.id, v]))
    setTags((current) => current.map((tag) => {
      const update = byId.get(tag.id)
      return update ? { ...tag, path: update.path, value: update.value, good: update.good, forced: update.forced } : tag
    }))
  }, [])

  const addEvent = useCallback((cycle: number, text: string, kind: SimEvent['kind']) => {
    eventKey.current += 1
    const key = eventKey.current
    setEvents((current) => [{ key, cycle, text, kind }, ...current].slice(0, 100))
  }, [])

  const load = useCallback(async () => {
    const [loadedState, loadedTags] = await Promise.all([simulationApi.state(projectId), simulationApi.tags(projectId)])
    setState(withEnteredCycles(loadedState))
    setTags(loadedTags)
  }, [projectId])

  useEffect(() => {
    let cancelled = false
    Promise.all([simulationApi.state(projectId), simulationApi.tags(projectId)])
      .then(([loadedState, loadedTags]) => {
        if (cancelled) return
        setState(withEnteredCycles(loadedState))
        setTags(loadedTags)
        setError(null)
      })
      .catch((reason: Error) => { if (!cancelled) setError(reason.message) })
    return () => { cancelled = true }
  }, [projectId, revision])

  useEffect(() => {
    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl('/hubs/simulation')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()
    let active = true

    connection.on('values', (batch: SimChangeBatch) => {
      applyValues(batch.values)
      setState((current) => current && { ...current, status: { ...current.status, cycle: batch.cycle, timeSeconds: batch.timeSeconds } })
    })
    connection.on('status', (status: SimStatus) => setState((current) => current && { ...current, status }))
    connection.on('stateChanged', (change: StateChangedMessage) => {
      setState((current) => current && {
        ...current,
        controlModules: current.controlModules.map((cm) => cm.path === change.controlModule
          ? { ...cm, state: change.to, stateName: change.toName ?? String(change.to), stateText: undefined, stateSeconds: 0, enteredCycle: change.cycle + 1 }
          : cm),
      })
      addEvent(change.cycle, `${change.controlModule}: ${change.from} → ${change.to} ${change.toName ?? ''} (${change.transition})`, 'state')
    })
    connection.on('diagnostic', (diagnostic: DiagnosticMessage) =>
      addEvent(diagnostic.cycle, `${diagnostic.controlModule}: ${diagnostic.message}`, 'diagnostic'))
    connection.onreconnected(() => {
      setConnected(true)
      void connection.invoke('Join', projectId)
      void load()
    })
    connection.onreconnecting(() => setConnected(false))
    connection.onclose(() => setConnected(false))

    connection.start()
      .then(() => connection.invoke('Join', projectId))
      .then(() => { if (active) setConnected(true) })
      .catch((reason: Error) => { if (active) setError(`Live connection failed: ${reason.message}`) })

    return () => {
      active = false
      void connection.stop()
    }
  }, [projectId, applyValues, addEvent, load])

  const run = useCallback(async <T,>(action: () => Promise<T>) => {
    try {
      const result = await action()
      setError(null)
      return result
    } catch (reason) {
      setError((reason as Error).message)
      return undefined
    }
  }, [])

  const control = useCallback(async (action: () => Promise<SimulationState>) => {
    const result = await run(action)
    if (result) setState(withEnteredCycles(result))
    return result
  }, [run])

  const tagAction = useCallback(async (action: () => Promise<SimTagValue>) => {
    const result = await run(action)
    if (result) applyValues([result])
  }, [run, applyValues])

  return { state, tags, events, connected, error, load, run, control, tagAction, clearEvents: () => setEvents([]) }
}
