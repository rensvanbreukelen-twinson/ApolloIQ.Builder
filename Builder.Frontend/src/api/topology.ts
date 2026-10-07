import { ApiError } from './client'
import type { ApiErrorBody } from './types'

export type DeviceRole = 'Plc' | 'Scada' | 'ThirdParty'
export type LinkClass = 'Control' | 'Monitoring'

export type Device = { id: string | null; name: string; role: DeviceRole; description: string | null }
export type Link = { id: string | null; from: string; to: string; protocol: string; class: LinkClass }
export type Topology = { devices: Device[]; links: Link[] }

export type Origin = { tagId: string; tag: string; tagSource: string; deviceId: string | null; source: string | null; address: string | null }
export type DeploymentItem = { id: string; path: string; kind: 'controlModule'; type: string | null; deviceId: string | null; inputs: Origin[] }

export type BindingIssue = { severity: 'Error' | 'Warning' | 'Info'; code: string; subject: string; message: string }
export type Access = {
  tag: string
  owner: string | null
  consumer: string
  usedBy: string
  kind: string
  path: string
  critical: boolean
  safetyViolation: boolean
  address: string
}
export type Binding = { issues: BindingIssue[]; accesses: Access[]; errors: number; warnings: number }

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

const base = (projectId: string) => `/api/projects/${projectId}`

export const topologyApi = {
  topology: (projectId: string) => request<Topology>('GET', `${base(projectId)}/topology`),
  saveTopology: (projectId: string, topology: Topology) => request<Topology>('PUT', `${base(projectId)}/topology`, topology),
  deployment: (projectId: string) => request<DeploymentItem[]>('GET', `${base(projectId)}/deployment`),
  setDevice: (projectId: string, objectId: string, deviceId: string | null) =>
    request<void>('PUT', `${base(projectId)}/objects/${objectId}/device`, { deviceId }),
  setOrigin: (projectId: string, tagId: string, deviceId: string | null, source: string, address: string) =>
    request<void>('PUT', `${base(projectId)}/tags/${tagId}/origin`, { deviceId, source, address }),
  binding: (projectId: string) => request<Binding>('GET', `${base(projectId)}/binding`),
}
