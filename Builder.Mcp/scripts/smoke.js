#!/usr/bin/env node
// Smoke test against a running Builder (APOLLOIQ_BUILDER_URL, default http://localhost:5180), through the real MCP server on stdio:
// create a project (HTTP; no tool may change a project) → validate_design(dirty water) → create_proposal → read_proposal.
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { Client } from '@modelcontextprotocol/sdk/client/index.js'
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js'
import { baseUrl, call } from '../src/builder.js'

const here = path.dirname(fileURLToPath(import.meta.url))
const design = readFileSync(process.argv[2] ?? path.resolve(here, '../../Documentation/Design/dirty-water.design.yaml'), 'utf8')

const client = new Client({ name: 'apolloiq-smoke', version: '0.1.0' })
await client.connect(new StdioClientTransport({ command: process.execPath, args: [path.resolve(here, '../src/server.js')], env: { ...process.env, APOLLOIQ_BUILDER_URL: baseUrl } }))

let failed = false
async function use(name, args = {}) {
  const result = await client.callTool({ name, arguments: args })
  const text = result.content.map((c) => c.text).join('\n')
  console.log(`\n=== ${name} ${result.isError ? '(ERROR)' : ''}\n${text.length > 3000 ? `${text.slice(0, 3000)}\n… (${text.length} characters)` : text}`)
  if (result.isError) failed = true
  return text
}

const { tools } = await client.listTools()
console.log(`Tools: ${tools.map((t) => t.name).join(', ')}`)

const name = `Smoke ${new Date().toISOString().replace(/[:.]/g, '-')}`
const project = await call('POST', '/api/projects', { name })
console.log(`Created project ${name} (${project.id}) over HTTP`)

await use('list_projects')
const rules = await use('read_rules')
if (!rules.includes('apolloiq.design/1')) failed = true
await use('validate_design', { project: name, design })
const created = await use('create_proposal', {
  project: name,
  title: 'Dirty water tank',
  description: 'Builds the dirty water tank with its transfer pump from the functional description.\n\nSee the questions: four assumptions to confirm.',
  design,
})
const id = /proposal: ([0-9a-f-]{36})/.exec(created)?.[1]
if (!id) throw new Error('No proposal id in the create_proposal result')
await use('list_proposals', { project: name })
await use('read_proposal', { project: name, proposal: id })
await use('read_review_comments', { project: name, proposal: id })
await use('read_design', { project: name })

await client.close()
console.log(failed ? '\nSMOKE FAILED' : `\nSMOKE OK — project "${name}", proposal ${id}`)
process.exit(failed ? 1 : 0)
