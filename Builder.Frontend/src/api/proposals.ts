import { ApiError } from './client'

export type ItemState = 'Open' | 'Accepted' | 'Rejected'

export type ProposalStatus = 'Open' | 'PartlyAccepted' | 'Accepted' | 'Rejected' | 'Closed'

/** A change item of a proposal as the review shows it. Before and after are design fragments (JSON of the YAML format). */
export type ReviewItem = {
  id: string
  kind: 'Create' | 'Update' | 'Delete' | 'Rename' | 'Move'
  area: string
  target: string
  group: string
  section: string | null
  summary: string
  before: unknown
  after: unknown
  dependsOn: string[]
  problems: string[]
  state: ItemState
  conflict: string | null
  changedSincePreviousVersion: boolean
  newInVersion: boolean
  version: number
}

export type DesignQuestion = { about?: string | null; question?: string | null; assumed?: string | null }

export type ReviewComment = { id: string; author: string; text: string; at: string; version: number; itemId: string | null }

export type VersionSummary = { number: number; createdAt: string; author: string; note: string | null; items: number; changedItems: number; newItems: number; removedItems: number }

export type Proposal = {
  id: string
  title: string
  description: string
  author: string
  status: ProposalStatus
  createdAt: string
  updatedAt: string
  projectChanged: boolean
  version: number
  versions: VersionSummary[]
  questions: DesignQuestion[]
  problems: string[]
  warnings: string[]
  items: ReviewItem[]
  comments: ReviewComment[]
  counts: { open: number; accepted: number; rejected: number; conflicts: number }
}

export type ProposalSummary = { id: string; title: string; author: string; status: ProposalStatus; createdAt: string; updatedAt: string; version: number; openItems: number; items: number }

export type ProposalList = { proposals: ProposalSummary[]; undo: { proposalId: string; proposalTitle: string; items: number; at: string; by: string } | null }

export type Validation = { ok: boolean; errors: string[]; warnings: string[] }

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const response = await fetch(url, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  const json = (await response.json().catch(() => null)) as (T & { code?: string; message?: string; validation?: Validation }) | null
  if (!response.ok) {
    const error = new ApiError(response.status, { code: json?.code ?? 'http_error', message: json?.message ?? `${response.status} ${response.statusText}` })
    throw Object.assign(error, { validation: json?.validation ?? null })
  }
  return json as T
}

const base = (projectId: string) => `/api/projects/${projectId}/proposals`

export const proposalApi = {
  list: (projectId: string) => request<ProposalList>('GET', base(projectId)),
  get: (projectId: string, id: string) => request<Proposal>('GET', `${base(projectId)}/${id}`),
  validate: (projectId: string, id: string, itemIds: string[]) => request<Validation>('POST', `${base(projectId)}/${id}/validate`, { itemIds }),
  accept: (projectId: string, id: string, itemIds: string[], author: string) =>
    request<{ proposal: Proposal; validation: Validation }>('POST', `${base(projectId)}/${id}/accept`, { itemIds, author }),
  reject: (projectId: string, id: string, itemIds: string[], author: string) => request<Proposal>('POST', `${base(projectId)}/${id}/reject`, { itemIds, author }),
  reopen: (projectId: string, id: string, itemIds: string[]) => request<Proposal>('POST', `${base(projectId)}/${id}/reopen`, { itemIds }),
  rejectProposal: (projectId: string, id: string, author: string) => request<Proposal>('POST', `${base(projectId)}/${id}/reject-proposal`, { author }),
  comment: (projectId: string, id: string, text: string, author: string, itemId: string | null) =>
    request<ReviewComment>('POST', `${base(projectId)}/${id}/comments`, { text, author, itemId }),
  undo: (projectId: string) => request<ProposalList>('POST', `${base(projectId)}/undo`, {}),
}
