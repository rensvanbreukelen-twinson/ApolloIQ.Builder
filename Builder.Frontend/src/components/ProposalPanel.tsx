import { useCallback, useEffect, useMemo, useState } from 'react'
import { proposalApi, type Proposal, type ProposalList, type ReviewComment, type ReviewItem, type Validation } from '../api/proposals'
import { diffLines } from '../lib/diff'
import { toYaml } from '../lib/yaml'
import { Markdown } from './Markdown'

type Props = {
  projectId: string
  /** The project or the blueprints changed (an accept or an undo): refresh the tree and the library. */
  onChanged: () => void
}

const reviewerKey = 'apolloiq.builder.reviewer'

function rememberedReviewer(): string {
  try {
    return localStorage.getItem(reviewerKey) || 'engineer'
  } catch {
    return 'engineer'
  }
}

function rememberReviewer(name: string) {
  try {
    localStorage.setItem(reviewerKey, name)
  } catch {
    return
  }
}

const statusText: Record<string, string> = { Open: 'Open', PartlyAccepted: 'Partly accepted', Accepted: 'Accepted', Rejected: 'Rejected', Superseded: 'Superseded' }

const when = (at: string) => new Date(at).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' })

/** The proposals of the project (left) and the review of the selected one (right). */
export function ProposalPanel({ projectId, onChanged }: Props) {
  const [list, setList] = useState<ProposalList | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [reviewer, setReviewer] = useState(rememberedReviewer)
  const [reload, setReload] = useState(0)

  const refreshList = useCallback(async () => {
    try {
      const loaded = await proposalApi.list(projectId)
      setList(loaded)
      setSelectedId((current) => current ?? loaded.proposals.find((p) => p.status === 'Open' || p.status === 'PartlyAccepted')?.id ?? loaded.proposals[0]?.id ?? null)
      setError(null)
    } catch (reason) {
      setError((reason as Error).message)
    }
  }, [projectId])

  useEffect(() => {
    let cancelled = false
    const load = () => proposalApi.list(projectId)
      .then((loaded) => {
        if (cancelled) return
        setList(loaded)
        setSelectedId((current) => current ?? loaded.proposals.find((p) => p.status === 'Open' || p.status === 'PartlyAccepted')?.id ?? loaded.proposals[0]?.id ?? null)
      })
      .catch((reason: Error) => { if (!cancelled) setError(reason.message) })
    void load()
    const timer = window.setInterval(() => void load(), 15000)
    return () => { cancelled = true; window.clearInterval(timer) }
  }, [projectId])

  const undo = async () => {
    if (!list?.undo || !window.confirm(`Undo the last accept (${list.undo.items} items of "${list.undo.proposalTitle}")? The project and the blueprints go back to how they were before it.`)) return
    try {
      setList(await proposalApi.undo(projectId))
      setReload((r) => r + 1)
      onChanged()
    } catch (reason) {
      setError((reason as Error).message)
    }
  }

  return (
    <div className="bp-layout pr-layout">
      <aside className="bp-list pr-list">
        <div className="pr-list-head">
          <b>Proposals</b>
          <button className="button button-small" onClick={() => void refreshList()}>Refresh</button>
        </div>
        <label className="pr-reviewer">
          <span className="muted">Reviewer</span>
          <input className="input" value={reviewer} onChange={(event) => { setReviewer(event.target.value); rememberReviewer(event.target.value) }} />
        </label>
        {list?.undo && (
          <div className="pr-undo">
            <span className="muted">Last accept: {list.undo.items} item{list.undo.items === 1 ? '' : 's'} of “{list.undo.proposalTitle}”, {when(list.undo.at)}</span>
            <button className="button button-small" onClick={() => void undo()}>Undo last accept</button>
          </div>
        )}
        {error && <p className="form-error">{error}</p>}
        <ul>
          {list?.proposals.map((p) => (
            <li key={p.id}>
              <button className={`bp-list-item pr-list-item ${selectedId === p.id ? 'bp-list-item-active' : ''}`} onClick={() => setSelectedId(p.id)}>
                <span className="pr-list-title">{p.title}</span>
                <span className="pr-list-meta">
                  <span className={`pr-status pr-status-${p.status}`}>{statusText[p.status] ?? p.status}</span>
                  <span className="muted">{p.author} · v{p.version} · {when(p.updatedAt)}</span>
                </span>
                <span className="muted pr-list-count">{p.openItems} of {p.items} items open</span>
              </button>
            </li>
          ))}
        </ul>
        {list && list.proposals.length === 0 && (
          <p className="muted">No proposals yet. An AI agent opens them through the Builder MCP server (<code>create_proposal</code>).</p>
        )}
      </aside>
      {selectedId
        ? <ProposalReview key={`${selectedId}-${reload}`} projectId={projectId} proposalId={selectedId} reviewer={reviewer}
            onChanged={() => { onChanged(); void refreshList() }} />
        : <div className="bp-empty muted">Select a proposal.</div>}
    </div>
  )
}

type ReviewProps = { projectId: string; proposalId: string; reviewer: string; onChanged: () => void }

function ProposalReview({ projectId, proposalId, reviewer, onChanged }: ReviewProps) {
  const [proposal, setProposal] = useState<Proposal | null>(null)
  const [selection, setSelection] = useState<Set<string>>(new Set())
  const [focus, setFocus] = useState<string | null>(null)
  const [checked, setChecked] = useState<{ key: string; result: Validation } | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      setProposal(await proposalApi.get(projectId, proposalId))
      setError(null)
    } catch (reason) {
      setError((reason as Error).message)
    }
  }, [projectId, proposalId])

  useEffect(() => {
    let cancelled = false
    proposalApi.get(projectId, proposalId)
      .then((loaded) => { if (!cancelled) { setProposal(loaded); setFocus(loaded.items[0]?.id ?? null) } })
      .catch((reason: Error) => { if (!cancelled) setError(reason.message) })
    return () => { cancelled = true }
  }, [projectId, proposalId])

  const items = useMemo(() => new Map((proposal?.items ?? []).map((i) => [i.id, i])), [proposal])
  const selectable = useCallback((item: ReviewItem | undefined) =>
    Boolean(item && item.state !== 'Accepted' && !item.conflict?.startsWith('No longer') && proposal?.status !== 'Rejected'), [proposal])

  // Live validation of the selection (the result belongs to the selection it was made for).
  const selectionKey = [...selection].sort().join('|')
  useEffect(() => {
    if (!selectionKey) return
    let cancelled = false
    const timer = window.setTimeout(() => {
      proposalApi.validate(projectId, proposalId, selectionKey.split('|'))
        .then((result) => { if (!cancelled) setChecked({ key: selectionKey, result }) })
        .catch((reason: Error) => { if (!cancelled) setChecked({ key: selectionKey, result: { ok: false, errors: [reason.message], warnings: [] } }) })
    }, 250)
    return () => { cancelled = true; window.clearTimeout(timer) }
  }, [selectionKey, projectId, proposalId])
  const validation = checked && checked.key === selectionKey ? checked.result : null
  const checking = selection.size > 0 && validation === null
  const setValidation = (result: Validation) => setChecked({ key: selectionKey, result })

  const groups = useMemo(() => {
    const result = new Map<string, ReviewItem[]>()
    for (const item of proposal?.items ?? []) result.set(item.group, [...(result.get(item.group) ?? []), item])
    return [...result.entries()]
  }, [proposal])

  const withDependencies = (ids: string[]) => {
    const result = new Set(selection)
    const added: string[] = []
    const visit = (id: string) => {
      const item = items.get(id)
      if (!item || result.has(id) || !selectable(item)) return
      result.add(id)
      added.push(id)
      item.dependsOn.forEach(visit)
    }
    ids.forEach(visit)
    return { result, added }
  }

  const dependentsOf = (ids: Set<string>, within: Set<string>) => {
    const result = new Set<string>()
    let grew = true
    while (grew) {
      grew = false
      for (const id of within) {
        if (result.has(id) || ids.has(id)) continue
        if (items.get(id)?.dependsOn.some((d) => ids.has(d) || result.has(d))) {
          result.add(id)
          grew = true
        }
      }
    }
    return result
  }

  const select = (ids: string[], on: boolean) => {
    if (on) {
      const { result, added } = withDependencies(ids)
      const extra = added.filter((id) => !ids.includes(id))
      setNotice(extra.length ? `Also selected what this needs: ${extra.map((id) => items.get(id)?.summary).join('; ')}.` : null)
      setSelection(result)
      return
    }
    const removing = new Set(ids)
    const dependents = dependentsOf(removing, selection)
    const result = new Set([...selection].filter((id) => !removing.has(id) && !dependents.has(id)))
    setNotice(dependents.size ? `Also deselected, because they need it: ${[...dependents].map((id) => items.get(id)?.summary).join('; ')}.` : null)
    setSelection(result)
  }

  const openIds = (proposal?.items ?? []).filter((i) => i.state === 'Open' && selectable(i)).map((i) => i.id)

  const run = async (action: () => Promise<void>) => {
    setBusy(true)
    try {
      await action()
      setError(null)
    } catch (reason) {
      const failed = reason as Error & { validation?: Validation | null }
      if (failed.validation) setValidation(failed.validation)
      setError(failed.message)
    } finally {
      setBusy(false)
    }
  }

  const accept = () => run(async () => {
    const result = await proposalApi.accept(projectId, proposalId, [...selection], reviewer)
    setProposal(result.proposal)
    setSelection(new Set())
    setNotice(`Accepted ${selection.size} item${selection.size === 1 ? '' : 's'}. Undo it from the list on the left.`)
    onChanged()
  })

  const reject = () => run(async () => {
    setProposal(await proposalApi.reject(projectId, proposalId, [...selection], reviewer))
    setNotice(`Rejected ${selection.size} item${selection.size === 1 ? '' : 's'}.`)
    setSelection(new Set())
    onChanged()
  })

  const rejectProposal = () => run(async () => {
    if (!window.confirm('Reject the whole proposal? Its open items are rejected; accepted items stay.')) return
    setProposal(await proposalApi.rejectProposal(projectId, proposalId, reviewer))
    setSelection(new Set())
    onChanged()
  })

  const reopen = (id: string) => run(async () => {
    setProposal(await proposalApi.reopen(projectId, proposalId, [id]))
    onChanged()
  })

  const comment = async (text: string, itemId: string | null) => {
    await proposalApi.comment(projectId, proposalId, text, reviewer, itemId)
    await load()
  }

  if (!proposal) return <div className="bp-empty muted">{error ?? 'Loading…'}</div>

  const latest = proposal.versions[proposal.versions.length - 1]
  const focused = focus ? items.get(focus) ?? null : null
  const closed = proposal.status === 'Rejected' || proposal.status === 'Superseded'
  /** Comments on an item; for null, the general ones and those on items that are no longer in the proposal. */
  const commentsOf = (itemId: string | null) =>
    proposal.comments.filter((c) => (itemId === null ? c.itemId === null || !items.has(c.itemId) : c.itemId === itemId))

  return (
    <div className="bp-editor pr-review">
      <div className="toolbar">
        <span className="toolbar-title">{proposal.title}</span>
        <span className={`pr-status pr-status-${proposal.status}`}>{statusText[proposal.status] ?? proposal.status}</span>
        <span className="muted">by {proposal.author}, {when(proposal.createdAt)}</span>
        <span className="spacer" />
        <button className="button" disabled={closed || openIds.length === 0} onClick={() => select(openIds, true)}>Select all open</button>
        <button className="button button-primary" disabled={busy || closed || selection.size === 0 || checking || validation?.ok === false} onClick={() => void accept()}>
          Accept selected{selection.size ? ` (${selection.size})` : ''}
        </button>
        <button className="button" disabled={busy || closed || selection.size === 0} onClick={() => void reject()}>Reject selected</button>
        <button className="button button-danger" disabled={busy || closed || proposal.counts.open === 0} onClick={() => void rejectProposal()}>Reject proposal</button>
      </div>
      <div className="pr-version-bar">
        <span className="pr-version">Version {proposal.version}</span>
        {proposal.version > 1
          ? <span>{latest.changedItems + latest.newItems} item{latest.changedItems + latest.newItems === 1 ? '' : 's'} changed since version {proposal.version - 1}
            {latest.removedItems > 0 && <>, {latest.removedItems} dropped</>}{latest.note && <span className="muted"> — {latest.note}</span>}</span>
          : <span className="muted">First version</span>}
        {proposal.versions.length > 1 && <span className="muted pr-version-history">{proposal.versions.map((v) => `v${v.number} ${when(v.createdAt)}`).join(' · ')}</span>}
      </div>
      {error && <div className="banner banner-error">{error}</div>}
      {proposal.supersededBy && <div className="banner banner-info">Superseded by a newer proposal; nothing in this one can be accepted any more.</div>}
      {proposal.projectChanged && !closed && (
        <div className="banner banner-warning">The project changed since this version was made. The items are checked against the current project; conflicts are marked.</div>
      )}
      {proposal.problems.length > 0 && (
        <div className="banner banner-error"><b>The proposal has problems the agent must fix:</b><ul>{proposal.problems.map((p, i) => <li key={i}>{p}</li>)}</ul></div>
      )}
      {selection.size > 0 && (
        checking ? <div className="banner pr-checking">Checking the selection…</div>
          : validation && (validation.ok
            ? <div className="banner pr-valid">The selection is valid: {selection.size} item{selection.size === 1 ? '' : 's'} can be accepted as one step.
              {validation.warnings.length > 0 && <ul className="muted">{validation.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>}</div>
            : <div className="banner banner-error"><b>The selection does not validate:</b><ul>{validation.errors.map((e, i) => <li key={i}>{e}</li>)}</ul></div>)
      )}
      {notice && <div className="banner banner-info pr-notice">{notice}<button className="cfg-x" aria-label="Dismiss" onClick={() => setNotice(null)}>×</button></div>}
      <div className="pr-body">
        <section className="pr-main">
          {proposal.description && <div className="pr-card"><h3>Description</h3><Markdown text={proposal.description} /></div>}
          {proposal.questions.length > 0 && (
            <div className="pr-card">
              <h3>Questions ({proposal.questions.length})</h3>
              <ol className="pr-questions">
                {proposal.questions.map((q, i) => (
                  <li key={i}>
                    {q.about && <span className="mono pr-about">{q.about}</span>}
                    <div>{q.question}</div>
                    {q.assumed && <div className="muted">Assumed: {q.assumed}</div>}
                  </li>
                ))}
              </ol>
            </div>
          )}
          <div className="pr-card">
            <h3>
              Changes
              <span className="muted pr-counts">
                {proposal.counts.open} open · {proposal.counts.accepted} accepted · {proposal.counts.rejected} rejected
                {proposal.counts.conflicts > 0 && <> · <span className="pr-conflict-text">{proposal.counts.conflicts} conflict{proposal.counts.conflicts === 1 ? '' : 's'}</span></>}
              </span>
            </h3>
            {groups.length === 0 && <p className="muted">This proposal changes nothing: the project already matches it.</p>}
            {groups.map(([group, groupItems]) => {
              const choosable = groupItems.filter(selectable).filter((i) => i.state !== 'Rejected')
              const all = choosable.length > 0 && choosable.every((i) => selection.has(i.id))
              return (
                <div key={group} className="pr-group">
                  <label className="pr-group-head">
                    <input type="checkbox" disabled={closed || choosable.length === 0} checked={all}
                      onChange={(event) => select(choosable.map((i) => i.id), event.target.checked)} />
                    <span className="mono">{group}</span>
                    <span className="muted">{groupItems.length} item{groupItems.length === 1 ? '' : 's'}</span>
                  </label>
                  {groupItems.map((item) => (
                    <div key={item.id} className={`pr-item ${focus === item.id ? 'pr-item-focus' : ''} pr-item-${item.state}`} onClick={() => setFocus(item.id)}>
                      <input type="checkbox" aria-label={`Select ${item.summary}`} disabled={closed || !selectable(item)} checked={selection.has(item.id)}
                        onClick={(event) => event.stopPropagation()} onChange={(event) => select([item.id], event.target.checked)} />
                      <span className={`pr-kind pr-kind-${item.kind}`}>{item.kind}</span>
                      <span className="pr-summary">{item.summary}</span>
                      {item.state !== 'Open' && <span className={`pr-chip pr-chip-${item.state}`}>{item.state}</span>}
                      {item.conflict && <span className="pr-chip pr-chip-conflict" title={item.conflict}>conflict</span>}
                      {item.problems.length > 0 && <span className="pr-chip pr-chip-conflict" title={item.problems.join('\n')}>problem</span>}
                      {item.changedSincePreviousVersion && <span className="pr-chip pr-chip-changed">changed in v{proposal.version}</span>}
                      {item.newInVersion && <span className="pr-chip pr-chip-changed">new in v{proposal.version}</span>}
                      {commentsOf(item.id).length > 0 && <span className="pr-chip">{commentsOf(item.id).length} comment{commentsOf(item.id).length === 1 ? '' : 's'}</span>}
                    </div>
                  ))}
                </div>
              )
            })}
          </div>
          <div className="pr-card">
            <h3>Comments on the proposal</h3>
            <Comments comments={commentsOf(null)} onAdd={(text) => comment(text, null)} placeholder="A comment for the agent about the whole proposal…" showItem />
          </div>
        </section>
        <section className="pr-detail">
          {focused ? (
            <>
              <div className="pr-detail-head">
                <span className={`pr-kind pr-kind-${focused.kind}`}>{focused.kind}</span>
                <b>{focused.summary}</b>
              </div>
              <div className="muted mono pr-item-id">{focused.id}</div>
              {focused.state === 'Rejected' && !closed && (
                <p>Rejected. <button className="button button-small" onClick={() => void reopen(focused.id)}>Reopen</button></p>
              )}
              {focused.conflict && <div className="banner banner-warning pr-inline">{focused.conflict}</div>}
              {focused.problems.length > 0 && <div className="banner banner-error pr-inline"><ul>{focused.problems.map((p, i) => <li key={i}>{p}</li>)}</ul></div>}
              {focused.dependsOn.length > 0 && (
                <div className="pr-needs">
                  <span className="muted">Needs:</span>
                  {focused.dependsOn.map((id) => (
                    <button key={id} className="pr-link" onClick={() => setFocus(id)}>{items.get(id)?.summary ?? id}</button>
                  ))}
                </div>
              )}
              <Diff before={focused.before} after={focused.after} />
              <h4>Comments</h4>
              <Comments key={focused.id} comments={commentsOf(focused.id)} onAdd={(text) => comment(text, focused.id)} placeholder="A comment for the agent about this change…" />
            </>
          ) : <p className="muted">Select a change to see its diff.</p>}
        </section>
      </div>
    </div>
  )
}

function Diff({ before, after }: { before: unknown; after: unknown }) {
  const lines = useMemo(() => diffLines(toYaml(before), toYaml(after)), [before, after])
  const added = lines.filter((l) => l.kind === 'added').length
  const removed = lines.filter((l) => l.kind === 'removed').length
  return (
    <div className="pr-diff-wrap">
      <div className="pr-diff-legend muted">
        {before == null ? 'New' : after == null ? 'Removed' : 'Changed'}: <span className="pr-plus">+{added}</span> <span className="pr-minus">−{removed}</span>
      </div>
      <pre className="pr-diff">
        {lines.map((line, i) => (
          <div key={i} className={`pr-line pr-line-${line.kind}`}>
            <span className="pr-sign">{line.kind === 'added' ? '+' : line.kind === 'removed' ? '−' : ' '}</span><span className="pr-text">{line.text || ' '}</span>
          </div>
        ))}
      </pre>
    </div>
  )
}

function Comments({ comments, onAdd, placeholder, showItem }: { comments: ReviewComment[]; onAdd: (text: string) => Promise<void>; placeholder: string; showItem?: boolean }) {
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const add = async () => {
    setBusy(true)
    try {
      await onAdd(text)
      setText('')
      setError(null)
    } catch (reason) {
      setError((reason as Error).message)
    } finally {
      setBusy(false)
    }
  }
  return (
    <div className="pr-comments">
      {comments.map((c) => (
        <div key={c.id} className="pr-comment">
          <div className="muted pr-comment-head">
            <b>{c.author}</b> · {when(c.at)} · version {c.version}
            {c.itemId && showItem && <> · on <span className="mono">{c.itemId}</span> (not in this version)</>}
          </div>
          <div className="pr-comment-text">{c.text}</div>
        </div>
      ))}
      <textarea className="input pr-comment-input" rows={2} value={text} placeholder={placeholder} onChange={(event) => setText(event.target.value)} />
      <div className="pr-comment-actions">
        {error && <span className="form-error">{error}</span>}
        <button className="button button-small" disabled={busy || !text.trim()} onClick={() => void add()}>Add comment</button>
      </div>
    </div>
  )
}
