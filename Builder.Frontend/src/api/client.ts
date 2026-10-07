import type {
  AlarmDefinition,
  CommandWire,
  ApiErrorBody,
  CmType,
  DeletionSummary,
  ExportCheck,
  InterlockRuleInput,
  ObjectInterlocks,
  Project,
  ProjectSummary,
  Tag,
  TagFilter,
  TreeNode,
} from './types'

export class ApiError extends Error {
  readonly code: string
  readonly field: string | null
  readonly status: number

  constructor(status: number, body: ApiErrorBody) {
    super(body.message)
    this.status = status
    this.code = body.code
    this.field = body.field ?? null
  }
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const response = await fetch(url, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!response.ok) {
    const fallback: ApiErrorBody = { code: 'http_error', message: `${response.status} ${response.statusText}` }
    const body = (await response.json().catch(() => null)) as ApiErrorBody | null
    throw new ApiError(response.status, body ?? fallback)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

const project = (id: string) => `/api/projects/${id}`

export const api = {
  types: () => request<CmType[]>('GET', '/api/library/types'),
  projects: () => request<ProjectSummary[]>('GET', '/api/projects'),
  createProject: (name: string) => request<Project>('POST', '/api/projects', { name }),
  project: (id: string) => request<Project>('GET', project(id)),
  tree: (id: string) => request<TreeNode[]>('GET', `${project(id)}/tree`),
  createFolder: (id: string, name: string, parentId: string | null) =>
    request<TreeNode>('POST', `${project(id)}/folders`, { name, parentId }),
  createControlModule: (id: string, blueprintId: string, name: string, parentId: string | null) =>
    request<TreeNode>('POST', `${project(id)}/control-modules`, { blueprintId, name, parentId }),
  rename: (id: string, objectId: string, name: string) =>
    request<TreeNode>('PATCH', `${project(id)}/objects/${objectId}`, { name }),
  move: (id: string, objectId: string, parentId: string | null) =>
    request<TreeNode>('POST', `${project(id)}/objects/${objectId}/move`, { parentId }),
  deletionSummary: (id: string, objectId: string) =>
    request<DeletionSummary>('GET', `${project(id)}/objects/${objectId}/deletion-summary`),
  remove: (id: string, objectId: string) => request<void>('DELETE', `${project(id)}/objects/${objectId}`),
  exportCheck: (id: string) => request<ExportCheck>('GET', `${project(id)}/export/scada/check`),
  /** Downloads the exchange file; a refused export throws an ApiError with the reasons. */
  exportToScada: async (id: string) => {
    const response = await fetch(`${project(id)}/export/scada`, { method: 'POST' })
    if (!response.ok) {
      const body = (await response.json().catch(() => null)) as { error?: string; errors?: string[] } | null
      throw new ApiError(response.status, { code: 'export_refused', message: body?.error ?? `${response.status} ${response.statusText}` })
    }
    return response.blob()
  },
  objectInterlocks: (id: string, objectId: string) => request<ObjectInterlocks>('GET', `${project(id)}/objects/${objectId}/interlocks`),
  setObjectInterlocks: (id: string, objectId: string, interlocks: InterlockRuleInput[]) =>
    request<ObjectInterlocks>('PUT', `${project(id)}/objects/${objectId}/interlocks`, { interlocks }),
  validateInterlock: (id: string, objectId: string, expression: string) =>
    request<{ error: string | null }>('POST', `${project(id)}/objects/${objectId}/interlocks/validate`, { expression }),
  wires: (id: string, cmId: string) => request<CommandWire[]>('GET', `${project(id)}/control-modules/${cmId}/wires`),
  setWires: (id: string, cmId: string, wires: CommandWire[]) =>
    request<CommandWire[]>('PUT', `${project(id)}/control-modules/${cmId}/wires`,
      { wires: wires.map((w) => ({ source: w.source, mode: w.mode, command: w.mode === 'Direct' ? w.command : null })) }),
  alarms: (id: string, cmId: string) => request<AlarmDefinition[]>('GET', `${project(id)}/control-modules/${cmId}/alarms`),
  setPriority: (id: string, cmId: string, alarm: string, priority: number | null) =>
    request<AlarmDefinition[]>('PUT', `${project(id)}/control-modules/${cmId}/alarms/${alarm}`, { priority }),
  states: (id: string) => request<Record<string, string[]>>('GET', `${project(id)}/states`),
  tags: (id: string, filter: TagFilter) => {
    const query = new URLSearchParams()
    for (const [key, value] of Object.entries(filter)) if (value) query.set(key, value)
    const suffix = query.toString()
    return request<Tag[]>('GET', `${project(id)}/tags${suffix ? `?${suffix}` : ''}`)
  },
}
