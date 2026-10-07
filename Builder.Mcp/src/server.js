#!/usr/bin/env node
// ApolloIQ Builder MCP server (stdio). Reads designs and opens / revises proposals. There is no tool that changes a project
// directly: every change goes through a proposal that the engineer reviews and accepts in the Builder.
import { readFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js'
import { z } from 'zod'
import { BuilderError, baseUrl, call, resolveProject } from './builder.js'
import { fromYaml, toYaml } from './yaml.js'

const here = path.dirname(fileURLToPath(import.meta.url))
const designDocumentation = process.env.APOLLOIQ_DESIGN_DOCUMENTATION ?? path.resolve(here, '../../Documentation/Design')

const server = new McpServer({ name: 'apolloiq-builder', version: '0.1.0' }, {
  instructions:
    'The ApolloIQ Builder holds the automation project (blueprints, CMs, EMs, Units, interlocks). Read read_rules first. ' +
    'Read the current state with read_design / read_blueprint, iterate with validate_design until it has no errors, then open a ' +
    'proposal with create_proposal. Put every open question and assumption in the design\'s questions and in the description. ' +
    'Read the engineer\'s review with read_proposal / read_review_comments and answer it with update_proposal. You never change ' +
    'the project directly; the engineer accepts proposals in the Builder.',
})

const text = (value) => ({ content: [{ type: 'text', text: typeof value === 'string' ? value : toYaml(value) }] })

function tool(name, description, inputSchema, run) {
  server.registerTool(name, { description, inputSchema }, async (args) => {
    try {
      return text(await run(args))
    } catch (error) {
      const detail = error instanceof BuilderError && error.body?.validation ? `\n${toYaml({ validation: error.body.validation })}` : ''
      return { isError: true, content: [{ type: 'text', text: `${error.message}${detail}` }] }
    }
  })
}

const project = z.string().describe('Project name or id (see list_projects)')
const design = z.string().describe('A design fragment in YAML, schema apolloiq.design/1 (see read_rules)')
const proposalId = z.string().describe('Proposal id (see list_proposals)')

const itemLine = (i) => ({
  id: i.id,
  ...(i.state && i.state !== 'Open' ? { state: i.state } : {}),
  summary: i.summary,
  ...(i.dependsOn?.length ? { needs: i.dependsOn } : {}),
  ...(i.problems?.length ? { problems: i.problems } : {}),
  ...(i.conflict ? { conflict: i.conflict } : {}),
  ...(i.changedSincePreviousVersion ? { changedSincePreviousVersion: true } : {}),
})

const validationView = (v) => (v.ok ? { ok: true, ...(v.warnings.length ? { warnings: v.warnings } : {}) } : { ok: false, errors: v.errors, warnings: v.warnings })

tool('list_projects', 'The Builder projects (name and id).', {}, async () => {
  const projects = await call('GET', '/api/projects')
  return projects.length ? projects : 'No projects yet. The engineer creates one in the Builder.'
})

tool('read_design', 'The project as a design (YAML): devices, the blueprints it uses, the object tree. Optional path for one subtree (e.g. Bilge.DirtyWaterTank).',
  { project, path: z.string().optional().describe('Object path, for one subtree') },
  async ({ project: reference, path: objectPath }) => {
    const p = await resolveProject(reference)
    return call('GET', `/api/projects/${p.id}/design${objectPath ? `?path=${encodeURIComponent(objectPath)}` : ''}`)
  })

tool('list_blueprints', 'The blueprint library: name, kind, version, description and how many errors / warnings each has.', {}, async () => {
  const list = await call('GET', '/api/blueprints')
  return list.length ? list.map(({ name, kind, version, description, errors, warnings }) => ({ name, kind, version, description, errors, warnings })) : 'The library is empty.'
})

tool('read_blueprint', 'One library blueprint as a design (YAML).', { name: z.string().describe('Blueprint name') },
  async ({ name }) => call('GET', `/api/blueprints/design?name=${encodeURIComponent(name)}`))

tool('read_rules', 'The rules for designing (Documentation/Design/Rules.md) and the design format (Design format.md). Read this first.', {}, async () => {
  const read = async (file) => {
    try {
      return await readFile(path.join(designDocumentation, file), 'utf8')
    } catch {
      return null
    }
  }
  const rules = await read('Rules.md')
  const format = await read('Design format.md')
  return [
    '# Rules', rules ?? '(Rules.md is not written yet: follow the design format and the naming rules in it, and ask in questions when in doubt.)',
    '', '# Design format', format ?? `(Design format.md not found in ${designDocumentation})`,
  ].join('\n')
})

tool('validate_design', 'Checks a design fragment against the project WITHOUT opening a proposal: the change items it makes and the validation of accepting all of them. Use it to iterate.',
  { project, design }, async ({ project: reference, design: yaml }) => {
    const p = await resolveProject(reference)
    const check = await call('POST', `/api/projects/${p.id}/design/validate`, { design: fromYaml(yaml) })
    return {
      items: check.items.map(itemLine),
      ...(check.problems.length ? { problems: check.problems } : {}),
      ...(check.warnings.length ? { warnings: check.warnings } : {}),
      questions: check.questions.length,
      validation: validationView(check.validation),
    }
  })

tool('create_proposal', 'Opens a proposal (like a pull request) from a design fragment. The engineer reviews and accepts it in the Builder. Put open questions and assumptions in the description (markdown) and in the design\'s questions.',
  { project, title: z.string(), description: z.string().describe('Markdown: what the proposal does, assumptions, open questions'), design },
  async ({ project: reference, title, description, design: yaml }) => {
    const p = await resolveProject(reference)
    const result = await call('POST', `/api/projects/${p.id}/proposals`, { title, description, author: 'AI', design: fromYaml(yaml) })
    return {
      proposal: result.proposal.id,
      title: result.proposal.title,
      status: result.proposal.status,
      items: result.proposal.items.map(itemLine),
      ...(result.proposal.problems.length ? { problems: result.proposal.problems } : {}),
      questions: result.proposal.questions.length,
      acceptEverything: validationView(result.validation),
    }
  })

tool('update_proposal', 'Revises a proposal: a new version from a new design fragment (the whole fragment, not a delta). Comments are kept; changed items are marked for the engineer.',
  { project, proposal: proposalId, design, note: z.string().describe('What changed in this version, in answer to the review'),
    description: z.string().optional().describe('New description (markdown), when it changes') },
  async ({ project: reference, proposal, design: yaml, note, description }) => {
    const p = await resolveProject(reference)
    const result = await call('PUT', `/api/projects/${p.id}/proposals/${proposal}`, { design: fromYaml(yaml), note, author: 'AI', description })
    const latest = result.proposal.versions.at(-1)
    return {
      proposal: result.proposal.id,
      version: latest.number,
      changedItems: latest.changedItems,
      newItems: latest.newItems,
      removedItems: latest.removedItems,
      items: result.proposal.items.map(itemLine),
      acceptEverythingOpen: validationView(result.validation),
    }
  })

tool('list_proposals', 'The proposals of a project: title, author, status, version, open items.', { project }, async ({ project: reference }) => {
  const p = await resolveProject(reference)
  const list = await call('GET', `/api/projects/${p.id}/proposals`)
  return list.proposals.length ? list.proposals : 'No proposals yet.'
})

tool('read_proposal', 'A proposal as the engineer reviews it: description, questions, items with their state (Open, Accepted, Rejected), conflicts with the current project, versions and comments.',
  { project, proposal: proposalId, diffs: z.boolean().optional().describe('Include the before / after of every item') },
  async ({ project: reference, proposal, diffs }) => {
    const p = await resolveProject(reference)
    const view = await call('GET', `/api/projects/${p.id}/proposals/${proposal}`)
    return {
      id: view.id, title: view.title, status: view.status, version: view.version, author: view.author,
      projectChangedSinceThisVersion: view.projectChanged,
      counts: view.counts,
      description: view.description,
      questions: view.questions,
      items: view.items.map((i) => ({ ...itemLine(i), state: i.state, ...(diffs ? { before: i.before, after: i.after } : {}) })),
      comments: view.comments.map(({ author, text, at, version, itemId }) => ({ author, at, version, ...(itemId ? { item: itemId } : {}), text })),
      versions: view.versions,
    }
  })

tool('read_review_comments', 'The engineer\'s review comments on a proposal (per item or general), with the item\'s summary and state.',
  { project, proposal: proposalId }, async ({ project: reference, proposal }) => {
    const p = await resolveProject(reference)
    const view = await call('GET', `/api/projects/${p.id}/proposals/${proposal}`)
    const items = new Map(view.items.map((i) => [i.id, i]))
    if (!view.comments.length) return 'No comments yet.'
    return view.comments.map((c) => ({
      author: c.author, at: c.at, version: c.version,
      ...(c.itemId ? { item: c.itemId, summary: items.get(c.itemId)?.summary, state: items.get(c.itemId)?.state } : { about: 'the whole proposal' }),
      text: c.text,
    }))
  })

const transport = new StdioServerTransport()
await server.connect(transport)
console.error(`apolloiq-builder MCP server on stdio, Builder at ${baseUrl}`)
