import { useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type { Tag, TreeNode } from '../api/types'

type Props = {
  projectId: string
  scope: TreeNode | null
  revision: number
}

type SortKey = 'path' | 'group' | 'dataType' | 'direction' | 'kind' | 'id'

const groups = ['FIN', 'CMD', 'OUT', 'LOK', 'PAR', 'SET', 'PMT', 'STS', 'INT']
const columns: { key: SortKey; label: string }[] = [
  { key: 'path', label: 'Path' },
  { key: 'group', label: 'Group' },
  { key: 'dataType', label: 'Type' },
  { key: 'direction', label: 'Direction' },
  { key: 'kind', label: 'Internal / external' },
  { key: 'id', label: 'ID' },
]

export function TagList({ projectId, scope, revision }: Props) {
  const [tags, setTags] = useState<Tag[]>([])
  const [error, setError] = useState<string | null>(null)
  const [group, setGroup] = useState('')
  const [direction, setDirection] = useState('')
  const [kind, setKind] = useState('')
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState<{ key: SortKey; ascending: boolean }>({ key: 'path', ascending: true })

  useEffect(() => {
    let cancelled = false
    api
      .tags(projectId, { scope: scope?.id, group, direction, kind, search: search.trim() })
      .then((result) => { if (!cancelled) { setTags(result); setError(null) } })
      .catch((reason: Error) => { if (!cancelled) setError(reason.message) })
    return () => { cancelled = true }
  }, [projectId, scope?.id, group, direction, kind, search, revision])

  const sorted = useMemo(() => {
    const factor = sort.ascending ? 1 : -1
    return [...tags].sort((a, b) => factor * a[sort.key].localeCompare(b[sort.key], undefined, { sensitivity: 'base' }))
  }, [tags, sort])

  const onSort = (key: SortKey) =>
    setSort((current) => ({ key, ascending: current.key === key ? !current.ascending : true }))

  const reset = () => { setGroup(''); setDirection(''); setKind(''); setSearch('') }
  const filtered = group || direction || kind || search

  return (
    <section className="tag-list">
      <div className="toolbar">
        <span className="toolbar-title">{scope ? `Tags in ${scope.path}` : 'All tags'}</span>
        <input className="input input-search" placeholder="Search path" value={search} onChange={(e) => setSearch(e.target.value)} />
        <select className="input" aria-label="Group" value={group} onChange={(e) => setGroup(e.target.value)}>
          <option value="">All groups</option>
          {groups.map((g) => <option key={g} value={g}>{g}</option>)}
        </select>
        <select className="input" aria-label="Direction" value={direction} onChange={(e) => setDirection(e.target.value)}>
          <option value="">All directions</option>
          <option value="In">In</option>
          <option value="Out">Out</option>
          <option value="InOut">InOut</option>
        </select>
        <select className="input" aria-label="Internal or external" value={kind} onChange={(e) => setKind(e.target.value)}>
          <option value="">Internal and external</option>
          <option value="Internal">Internal</option>
          <option value="External">External</option>
        </select>
        {filtered && <button className="button" onClick={reset}>Clear filters</button>}
        <span className="toolbar-count">{sorted.length} tags</span>
      </div>
      {error && <p className="form-error">{error}</p>}
      <div className="grid-wrap">
        <table className="grid">
          <thead>
            <tr>
              {columns.map((column) => (
                <th key={column.key} onClick={() => onSort(column.key)} aria-sort={sort.key === column.key ? (sort.ascending ? 'ascending' : 'descending') : 'none'}>
                  {column.label}
                  {sort.key === column.key && <span className="sort">{sort.ascending ? ' ▲' : ' ▼'}</span>}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {sorted.map((tag) => (
              <tr key={tag.id} title={tag.description}>
                <td className="mono">{tag.path}</td>
                <td><span className={`badge badge-${tag.group.toLowerCase()}`}>{tag.group}</span></td>
                <td>{tag.dataType}{tag.unit ? <span className="muted"> · {tag.unit}</span> : null}</td>
                <td>{tag.direction}</td>
                <td>{tag.kind}</td>
                <td className="mono muted">{tag.id}</td>
              </tr>
            ))}
            {sorted.length === 0 && !error && (
              <tr><td colSpan={columns.length} className="empty">No tags match.</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </section>
  )
}
