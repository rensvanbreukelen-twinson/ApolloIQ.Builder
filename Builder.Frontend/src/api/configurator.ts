import { ApiError } from './client'
import type { ApiErrorBody, InterlockSummary, TreeNode } from './types'

export type Position = { x: number; y: number }

export type ConfigCm = {
  id: string
  name: string
  path: string
  blueprint: string
  description: string
  position: Position | null
  unitId: string | null
  role: string | null
  interlocks: InterlockSummary
  inputs: string[]
}

export type ConfigRole = { role: string; blueprint: string; controlModuleId: string | null }

export type ConfigUnit = {
  id: string
  name: string
  path: string
  blueprint: string
  description: string
  position: Position | null
  roles: ConfigRole[]
  problem: string | null
  equipmentModule: boolean
  unitId: string | null
  role: string | null
  interlocks?: InterlockSummary | null
}

export type ConfigView = {
  folderId: string | null
  folderPath: string
  folders: { id: string; name: string; path: string }[]
  controlModules: ConfigCm[]
  units: ConfigUnit[]
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
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

const base = (id: string) => `/api/projects/${id}`

export const configuratorApi = {
  view: (id: string, folderId: string | null) =>
    request<ConfigView>('GET', `${base(id)}/configurator${folderId ? `?folder=${folderId}` : ''}`),
  createUnit: (id: string, name: string, parentId: string | null, blueprint: string) =>
    request<TreeNode>('POST', `${base(id)}/units`, { name, parentId, blueprint }),
  setMember: (id: string, unitId: string, role: string, controlModuleId: string | null) =>
    request<void>('PUT', `${base(id)}/units/${unitId}/roles/${encodeURIComponent(role)}`, { controlModuleId }),
  layout: (id: string, positions: { id: string; x: number; y: number }[]) =>
    request<void>('PUT', `${base(id)}/layout`, { positions }),
}
