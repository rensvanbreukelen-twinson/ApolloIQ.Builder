// The Builder REST API (Builder.Backend). Base URL from APOLLOIQ_BUILDER_URL, default http://localhost:5180.
export const baseUrl = (process.env.APOLLOIQ_BUILDER_URL ?? 'http://localhost:5180').replace(/\/+$/, '')

export class BuilderError extends Error {
  constructor(status, body) {
    super(body?.message ?? `HTTP ${status}`)
    this.status = status
    this.body = body
  }
}

export async function call(method, path, body) {
  let response
  try {
    response = await fetch(`${baseUrl}${path}`, {
      method,
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch (error) {
    throw new Error(`The Builder is not reachable at ${baseUrl} (${error.cause?.code ?? error.message}). Start it with: dotnet run --project Builder.Backend`)
  }
  const text = await response.text()
  const json = text ? JSON.parse(text) : null
  if (!response.ok) throw new BuilderError(response.status, json)
  return json
}

/** A project by id or by name (case-insensitive). */
export async function resolveProject(reference) {
  const projects = await call('GET', '/api/projects')
  const wanted = String(reference).trim().toLowerCase()
  const project = projects.find((p) => p.id === wanted || p.name.toLowerCase() === wanted)
  if (!project) throw new Error(`No project '${reference}'. Projects: ${projects.map((p) => `${p.name} (${p.id})`).join(', ') || 'none'}`)
  return project
}
