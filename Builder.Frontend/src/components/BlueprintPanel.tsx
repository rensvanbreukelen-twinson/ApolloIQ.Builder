import { createContext, Fragment, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react'
import {
  blueprintApi, newBlueprint, type Blueprint, type BpAction, type BpCatalog, type BpInterlock, type BpIssue, type BpState, type BpSummary, type BlueprintKind,
} from '../api/blueprints'
import { useConventions } from '../api/conventions'
import { BlueprintDiagram } from './BlueprintDiagram'
import { CommandInputsEditor } from './CommandInputsEditor'
import { ExpressionInput } from './ExpressionInput'

const Suggestions = createContext<{ refs: string[]; writable: string[]; states?: (reference: string) => string[] }>({ refs: [], writable: [] })

type Tab = 'general' | 'tags' | 'roles' | 'states' | 'transitions' | 'alarms' | 'interlocks' | 'inputs' | 'simulation' | 'json'

const standardAliases = ['is_running', 'is_off', 'is_stopped', 'is_available', 'is_starting', 'is_stopping', 'is_shutdown']

const writableGroups = ['OUT', 'STS', 'INT', 'CMD', 'SET']

function clone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

function assignCodes(bp: Blueprint) {
  const next = new Map<number, number>()
  for (const s of bp.states) {
    const code = next.get(s.category) ?? s.category
    s.code = code
    next.set(s.category, code + 1)
  }
}

function uniqueName(base: string, taken: string[]) {
  let i = 1
  while (taken.includes(`${base}${i}`)) i++
  return `${base}${i}`
}

export function BlueprintPanel() {
  const conventions = useConventions()
  const [catalog, setCatalog] = useState<BpCatalog | null>(null)
  const [list, setList] = useState<BpSummary[]>([])
  const [savedName, setSavedName] = useState<string | null>(null)
  const [bp, setBp] = useState<Blueprint | null>(null)
  const [dirty, setDirty] = useState(false)
  const [issues, setIssues] = useState<BpIssue[]>([])
  const [tab, setTab] = useState<Tab>('general')
  const [selectedState, setSelectedState] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [members, setMembers] = useState<Record<string, Blueprint>>({})
  const validation = useRef(0)

  const reloadList = useCallback(() => blueprintApi.list().then(setList), [])

  useEffect(() => {
    Promise.all([blueprintApi.catalog(), blueprintApi.list()])
      .then(([c, l]) => { setCatalog(c); setList(l) })
      .catch((reason: Error) => setError(reason.message))
  }, [])

  useEffect(() => {
    if (!bp) return
    const id = ++validation.current
    const timer = window.setTimeout(() => {
      blueprintApi.validate(bp).then((result) => { if (id === validation.current) setIssues(result.issues) }).catch(() => undefined)
    }, 350)
    return () => window.clearTimeout(timer)
  }, [bp])

  const roleKey = bp && bp.kind !== 'CM' ? bp.roles.map((r) => r.blueprint).join(',') : ''
  useEffect(() => {
    let cancelled = false
    const load = (names: string[]) => Promise.all([...new Set(names)].map((n) => blueprintApi.get(n).then((r) => [n, r.blueprint] as const).catch(() => null)))
      .then((loaded) => loaded.filter((x): x is readonly [string, Blueprint] => x !== null))
    void (async () => {
      const first = await load(roleKey.split(',').filter(Boolean))
      const second = await load(first.filter(([, m]) => m.kind !== 'CM').flatMap(([, m]) => m.roles.map((r) => r.blueprint)))
      if (!cancelled) setMembers(Object.fromEntries([...first, ...second]))
    })()
    return () => { cancelled = true }
  }, [roleKey])

  const confirmDiscard = () => !dirty || window.confirm('Discard the unsaved changes?')

  const open = async (name: string) => {
    if (!confirmDiscard()) return
    try {
      const result = await blueprintApi.get(name)
      setBp(result.blueprint)
      setIssues(result.issues)
      setSavedName(name)
      setDirty(false)
      setSelectedState(result.blueprint.states[0]?.name ?? null)
      setError(null)
    } catch (reason) {
      setError((reason as Error).message)
    }
  }

  const create = (kind: BlueprintKind) => {
    if (!confirmDiscard()) return
    const initial = catalog?.categories.find((c) => c.ready && !c.isOn) ?? catalog?.categories[0]
    if (!initial) return
    const created = newBlueprint(kind, uniqueName(kind === 'Unit' ? 'NewUnit' : kind === 'EM' ? 'NewEquipmentModule' : 'NewBlueprint', list.map((b) => b.name)), initial.code)
    setBp(created)
    setSavedName(null)
    setDirty(true)
    setTab('general')
    setSelectedState('Off')
  }

  const update = (change: (draft: Blueprint) => void) => {
    setBp((current) => {
      if (!current) return current
      const draft = clone(current)
      change(draft)
      assignCodes(draft)
      return draft
    })
    setDirty(true)
  }

  const save = async () => {
    if (!bp) return
    try {
      const result = await blueprintApi.save(savedName ?? bp.name, bp)
      setBp(result.blueprint)
      setIssues(result.issues)
      setSavedName(result.blueprint.name)
      setDirty(false)
      setError(null)
      await reloadList()
    } catch (reason) {
      setError((reason as Error).message)
    }
  }

  const remove = async () => {
    if (!bp || !savedName || !window.confirm(`Delete blueprint ${savedName}?`)) return
    await blueprintApi.remove(savedName)
    setBp(null)
    setSavedName(null)
    setDirty(false)
    await reloadList()
  }

  const interfaceTags = useMemo(() => {
    if (!bp || !catalog) return []
    return catalog.interfaces
      .filter((i) => i.required || bp.interfaces.includes(i.name) || (i.name === 'AutoManual' && bp.kind !== 'CM'))
      .flatMap((i) => i.tags.map((t) => ({ ...t, from: i.name })))
  }, [bp, catalog])

  const references = useMemo(() => {
    if (!bp) return []
    const own = [...interfaceTags.map((t) => `${t.group}.${t.name}`), ...bp.tags.map((t) => `${t.group}.${t.name}`)]
    const alarms = [...bp.alarms.map((a) => a.name), ...bp.states.map((s) => s.timeout?.alarm).filter((a): a is string => !!a)]
    const memberRefs = (prefix: string, m: Blueprint): string[] => {
      if (!catalog) return []
      const tags = [
        ...catalog.interfaces.filter((i) => i.required || m.interfaces.includes(i.name)).flatMap((i) => i.tags),
        ...m.tags,
      ]
      return [...tags.map((t) => `${prefix}.${t.group}.${t.name}`), ...m.alarms.map((a) => `${prefix}.ALM.${a.name}.active`),
        ...[...standardAliases, ...Object.keys(m.aliases ?? {})].map((a) => `${prefix}.${a}`)]
    }
    const roles = bp.kind !== 'CM' ? bp.roles.flatMap((r) => {
      const m = members[r.blueprint]
      if (!m) return []
      const nested = m.kind !== 'CM' ? m.roles.flatMap((s) => members[s.blueprint] ? memberRefs(`${r.name}.${s.name}`, members[s.blueprint]) : []) : []
      return [...memberRefs(r.name, m), ...nested]
    }) : []
    return [...own, ...alarms.map((a) => `ALM.${a}.active`), ...roles]
  }, [bp, interfaceTags, members, catalog])

  const rolePaths = useMemo(() => {
    if (!bp || bp.kind === 'CM') return []
    return bp.roles.flatMap((r) => {
      const m = members[r.blueprint]
      const nested = m && m.kind !== 'CM' ? m.roles.map((s) => ({ path: `${r.name}.${s.name}`, blueprint: members[s.blueprint] })) : []
      return [{ path: r.name, blueprint: m }, ...nested]
    })
  }, [bp, members])

  const stateNames = useCallback((reference: string) => {
    if (!bp) return []
    const source = reference === '' ? bp : members[bp.roles.find((r) => r.name === reference)?.blueprint ?? '']
    return source ? [...new Set([...source.states.map((st) => st.name), 'Unavailable'])] : []
  }, [bp, members])

  const writable = references.filter((r) => {
    const parts = r.split('.')
    return parts.length === 3 ? parts[1] === 'CMD' : writableGroups.includes(parts[0]) && parts[0] !== 'ALM'
  })

  const errors = issues.filter((i) => i.severity === 'Error').length
  const warnings = issues.length - errors
  const state = bp?.states.find((s) => s.name === selectedState) ?? null
  const stateIndex = bp && state ? bp.states.indexOf(state) : -1

  const tabs: [Tab, string][] = [['general', 'General'], ['tags', 'Tags'], ...(bp && bp.kind !== 'CM' ? [['roles', 'Roles'] as [Tab, string]] : []),
    ['states', 'States'], ['transitions', 'Transitions'], ['alarms', 'Alarms'], ['interlocks', 'Interlocks'], ['inputs', 'Command inputs'], ...(bp?.kind === 'CM' ? [['simulation', 'Simulation'] as [Tab, string]] : []), ['json', 'JSON']]

  return (
    <section className="tag-list blueprints">
      <Suggestions.Provider value={{ refs: references, writable, states: stateNames }}>
      <div className="bp-layout">
        <aside className="bp-list">
          <div className="bp-list-actions">
            <button className="button button-primary" onClick={() => create('CM')}>New CM blueprint</button>
            <button className="button" onClick={() => create('EM')}>New Equipment module blueprint</button>
            <button className="button" onClick={() => create('Unit')}>New Unit blueprint</button>
          </div>
          <ul>
            {list.map((b) => (
              <li key={b.name}>
                <button className={`bp-list-item ${savedName === b.name ? 'bp-list-item-active' : ''}`} onClick={() => void open(b.name)}>
                  <span className="mono">{b.name}</span>
                  <span className="muted"> {b.kind} v{b.version}</span>
                  {b.errors > 0 && <span className="bp-count bp-count-error">{b.errors}</span>}
                  {b.warnings > 0 && <span className="bp-count bp-count-warning">{b.warnings}</span>}
                </button>
              </li>
            ))}
            {list.length === 0 && <li className="muted">No blueprints yet.</li>}
          </ul>
        </aside>

        {!bp ? (
          <div className="empty bp-empty">Open a blueprint, or create a new one.</div>
        ) : (
          <div className="bp-editor">
            <div className="toolbar">
              <span className="toolbar-title mono">{bp.name}</span>
              <span className="muted">{bp.kind} · v{bp.version}{dirty ? ' · unsaved' : ''}</span>
              <div className="tabs" role="tablist">
                {tabs.map(([id, label]) => (
                  <button key={id} role="tab" aria-selected={tab === id} className={`tab ${tab === id ? 'tab-active' : ''}`} onClick={() => setTab(id)}>{label}</button>
                ))}
              </div>
              <span className="spacer" />
              <span className={errors ? 'bp-count bp-count-error' : 'bp-ok'}>{errors ? `${errors} errors` : 'no errors'}</span>
              {warnings > 0 && <span className="bp-count bp-count-warning">{warnings} warnings</span>}
              <button className="button button-primary" disabled={!dirty} onClick={() => void save()}>Save</button>
              {savedName && <button className="button button-danger" onClick={() => void remove()}>Delete</button>}
            </div>
            {error && <div className="banner banner-error">{error}</div>}

            <div className="bp-body">
              {tab === 'general' && catalog && (
                <div className="bp-form">
                  <label>Name<input className="input" value={bp.name} onChange={(e) => update((d) => { d.name = e.target.value })} /></label>
                  <label>Kind<input className="input" value={bp.kind} disabled /></label>
                  <label>Version<input className="input" value={bp.version} onChange={(e) => update((d) => { d.version = e.target.value })} /></label>
                  <label className="bp-wide">Description<textarea className="input" rows={3} value={bp.description} onChange={(e) => update((d) => { d.description = e.target.value })} /></label>
                  <fieldset className="bp-wide">
                    <legend>Interfaces</legend>
                    {catalog.interfaces.map((i) => {
                      const forced = i.required || (i.name === 'AutoManual' && bp.kind !== 'CM')
                      return (
                        <label key={i.name} className="checkbox">
                          <input type="checkbox" disabled={forced} checked={forced || bp.interfaces.includes(i.name)}
                            onChange={(e) => update((d) => { d.interfaces = e.target.checked ? [...d.interfaces, i.name] : d.interfaces.filter((n) => n !== i.name) })} />
                          {i.name} <span className="muted mono">{i.tags.map((t) => `${t.group}.${t.name}`).join(', ')}</span>
                        </label>
                      )
                    })}
                  </fieldset>
                </div>
              )}

              {tab === 'tags' && catalog && (
                <div className="grid-wrap">
                  <table className="grid bp-grid">
                    <thead><tr><th>Group</th><th>Name</th><th>Type</th><th>Description</th><th>Unit</th><th>Min</th><th>Max</th><th>Initial</th><th>Source</th><th>Primary</th><th /></tr></thead>
                    <tbody>
                      {interfaceTags.map((t) => (
                        <tr key={`i-${t.group}.${t.name}`} className="bp-readonly">
                          <td><span className={`badge badge-${t.group.toLowerCase()}`}>{t.group}</span></td>
                          <td className="mono">{t.name}</td><td>{t.dataType}</td><td>{t.description}</td>
                          <td colSpan={7} className="muted">from interface {t.from}</td>
                        </tr>
                      ))}
                      {bp.tags.map((t, i) => (
                        <tr key={i}>
                          <td><select className="input" aria-label="Group" value={t.group} onChange={(e) => update((d) => { d.tags[i].group = e.target.value; if (e.target.value !== 'FIN') d.tags[i].source = null })}>
                            {catalog.groups.map((g) => <option key={g}>{g}</option>)}</select></td>
                          <td><input className="input mono" aria-label="Tag name" value={t.name} onChange={(e) => update((d) => { d.tags[i].name = e.target.value })} /></td>
                          <td><select className="input" aria-label="Data type" value={t.dataType} onChange={(e) => update((d) => { d.tags[i].dataType = e.target.value })}>
                            {catalog.dataTypes.map((g) => <option key={g}>{g}</option>)}</select></td>
                          <td><input className="input" value={t.description} onChange={(e) => update((d) => { d.tags[i].description = e.target.value })} /></td>
                          <td><input className="input bp-narrow" value={t.unit ?? ''} onChange={(e) => update((d) => { d.tags[i].unit = e.target.value || null })} /></td>
                          <td><input className="input bp-narrow" type="number" value={t.min ?? ''} onChange={(e) => update((d) => { d.tags[i].min = e.target.value === '' ? null : Number(e.target.value) })} /></td>
                          <td><input className="input bp-narrow" type="number" value={t.max ?? ''} onChange={(e) => update((d) => { d.tags[i].max = e.target.value === '' ? null : Number(e.target.value) })} /></td>
                          <td><input className="input bp-narrow" value={t.initial ?? ''} onChange={(e) => update((d) => { d.tags[i].initial = e.target.value || null })} /></td>
                          <td>{t.group === 'FIN' ? (
                            <select className="input" aria-label="Source" value={t.source ?? ''} onChange={(e) => update((d) => { d.tags[i].source = (e.target.value || null) as 'LocalIO' | 'External' | null })}>
                              <option value="">— choose —</option><option value="LocalIO">Local I/O</option><option value="External">External</option>
                            </select>) : <span className="muted">—</span>}</td>
                          <td><input type="checkbox" aria-label="Primary" checked={t.primary} onChange={(e) => update((d) => { d.tags.forEach((x, j) => { x.primary = j === i && e.target.checked }) })} /></td>
                          <td><button className="button button-small" onClick={() => update((d) => { d.tags.splice(i, 1) })}>Remove</button></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  <div className="bp-row-actions">
                    {catalog.groups.map((g) => (
                      <button key={g} className="button button-small" onClick={() => update((d) => {
                        d.tags.push({ group: g, name: uniqueName('tag', d.tags.map((x) => x.name)), dataType: 'Bool', description: '', primary: false, source: g === 'FIN' ? null : undefined })
                      })}>+ {g}</button>
                    ))}
                  </div>
                </div>
              )}

              {tab === 'roles' && (
                <div className="bp-section">
                  <p className="muted">{bp.kind === 'EM'
                    ? <>Roles are placeholders for the CMs of this Equipment module. Each instance fills them with real CMs, which then sit under it in the tree. Refer to member tags as <code>ROLE.GROUP.name</code>; an Equipment module may only write its members' commands.</>
                    : <>Roles are placeholders for the members of this Unit: Equipment modules or CMs. Each Unit instance fills them, and the members then sit under it in the tree. Refer to member tags as <code>ROLE.GROUP.name</code>; a Unit may only write its members' commands.</>}</p>
                  <table className="grid bp-grid">
                    <thead><tr><th>Role</th><th>Blueprint</th><th /></tr></thead>
                    <tbody>
                      {bp.roles.map((r, i) => (
                        <tr key={i}>
                          <td><input className="input mono" aria-label="Role name" value={r.name} onChange={(e) => update((d) => { d.roles[i].name = e.target.value })} /></td>
                          <td><select className="input" aria-label="Role blueprint" value={r.blueprint} onChange={(e) => update((d) => { d.roles[i].blueprint = e.target.value })}>
                            <option value="">— choose —</option>
                            {list.filter((b) => b.kind === 'CM' || (bp.kind === 'Unit' && b.kind === 'EM')).map((b) => <option key={b.name} value={b.name}>{b.name}{b.kind === 'EM' ? ' (EM)' : ''}</option>)}
                          </select></td>
                          <td><button className="button button-small" onClick={() => update((d) => { d.roles.splice(i, 1) })}>Remove</button></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  <button className="button button-small" onClick={() => update((d) => { d.roles.push({ name: uniqueName('MEMBER', d.roles.map((r) => r.name)), blueprint: '' }) })}>+ Role</button>
                </div>
              )}

              {tab === 'states' && catalog && (
                <div className="bp-states">
                  <div className="bp-diagram-wrap">
                    <BlueprintDiagram states={bp.states} transitions={bp.transitions} categories={catalog.categories} selected={selectedState} onSelect={setSelectedState} />
                  </div>
                  <div className="bp-state-side">
                    <div className="bp-row-actions">
                      <select className="input" aria-label="Selected state" value={selectedState ?? ''} onChange={(e) => setSelectedState(e.target.value)}>
                        {bp.states.map((s) => <option key={s.name} value={s.name}>{s.code} {s.name}</option>)}
                      </select>
                      <button className="button button-small" onClick={() => {
                        const name = uniqueName('State', bp.states.map((s) => s.name))
                        const category = catalog.categories.find((c) => c.isOn && !c.moving) ?? catalog.categories[0]
                        update((d) => { d.states.push({ name, category: category.code, code: category.code, initial: d.states.length === 0, entry: [], run: [], exit: [] }) })
                        setSelectedState(name)
                      }}>+ State</button>
                      {state && <button className="button button-small button-danger" onClick={() => {
                        update((d) => { d.states.splice(stateIndex, 1) })
                        setSelectedState(bp.states.find((s) => s !== state)?.name ?? null)
                      }}>Delete state</button>}
                    </div>
                    {state && (
                      <StateEditor key={stateIndex} state={state} states={bp.states.map((s) => s.name)} catalog={catalog}
                        onRename={(name) => { update((d) => renameState(d, state.name, name)); setSelectedState(name) }}
                        onChange={(change) => update((d) => { change(d.states[stateIndex]); if (d.states[stateIndex].initial) d.states.forEach((s, j) => { if (j !== stateIndex) s.initial = false }) })} />
                    )}
                  </div>
                </div>
              )}

              {tab === 'transitions' && (
                <div className="grid-wrap">
                  <table className="grid bp-grid">
                    <thead><tr><th>Name</th><th>From</th><th>To</th><th>Guard</th><th>Priority</th><th /></tr></thead>
                    <tbody>
                      {bp.transitions.map((t, i) => (
                        <tr key={i}>
                          <td><input className="input mono" aria-label="Transition name" value={t.name} onChange={(e) => update((d) => { d.transitions[i].name = e.target.value })} /></td>
                          <td>
                            <div className="bp-from">
                              <label className="checkbox"><input type="checkbox" checked={t.from.includes('*')} onChange={(e) => update((d) => { d.transitions[i].from = e.target.checked ? ['*'] : [] })} />any</label>
                              {!t.from.includes('*') && bp.states.map((s) => (
                                <label key={s.name} className="checkbox"><input type="checkbox" checked={t.from.includes(s.name)}
                                  onChange={(e) => update((d) => { const f = d.transitions[i].from; d.transitions[i].from = e.target.checked ? [...f, s.name] : f.filter((x) => x !== s.name) })} />{s.name}</label>
                              ))}
                            </div>
                          </td>
                          <td><select className="input" aria-label="To" value={t.to} onChange={(e) => update((d) => { d.transitions[i].to = e.target.value })}>
                            <option value="">—</option>{bp.states.map((s) => <option key={s.name}>{s.name}</option>)}</select></td>
                          <td><ExpressionInput className="input mono bp-expr" ariaLabel="Guard" value={t.guard} onChange={(v) => update((d) => { d.transitions[i].guard = v })} suggestions={references} states={stateNames} /></td>
                          <td><input className="input bp-narrow" type="number" value={t.priority} onChange={(e) => update((d) => { d.transitions[i].priority = Number(e.target.value) })} /></td>
                          <td><button className="button button-small" onClick={() => update((d) => { d.transitions.splice(i, 1) })}>Remove</button></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  <div className="bp-row-actions">
                    <button className="button button-small" onClick={() => update((d) => {
                      d.transitions.push({ name: uniqueName('t', d.transitions.map((t) => t.name)), from: d.states[0] ? [d.states[0].name] : [], to: d.states[1]?.name ?? '', guard: '', priority: 10 })
                    })}>+ Transition</button>
                  </div>
                  <h4>Always-running logic</h4>
                  <p className="muted">Runs every cycle in every state, before the transitions. Use it for conditioning, such as <code>INT.feedback := FIN.feedback</code>.</p>
                  <ActionList actions={bp.always} onChange={(change) => update((d) => change(d.always))} />
                </div>
              )}

              {tab === 'alarms' && (
                <div className="grid-wrap">
                  <p className="muted">Alarms run at the end of every cycle. <b>Reactive</b> alarms may be used in this blueprint's transitions; <b>non-reactive</b> alarms only inform the operator and other CMs. Timeout alarms are set on the state.</p>
                  <table className="grid bp-grid">
                    <thead><tr><th>Name</th><th>Kind</th><th>Condition</th><th>On transition</th><th>Latched</th><th>Severity</th><th>Message</th><th /></tr></thead>
                    <tbody>
                      {bp.alarms.map((a, i) => (
                        <tr key={i}>
                          <td><input className="input mono" aria-label="Alarm name" value={a.name} onChange={(e) => update((d) => { d.alarms[i].name = e.target.value })} /></td>
                          <td><select className="input" aria-label="Alarm kind" value={a.kind} onChange={(e) => update((d) => { d.alarms[i].kind = e.target.value as 'Reactive' | 'NonReactive' })}>
                            <option value="Reactive">Reactive</option><option value="NonReactive">Non-reactive</option></select></td>
                          <td><ExpressionInput className="input mono bp-expr" ariaLabel="Condition" value={a.condition} onChange={(v) => update((d) => { d.alarms[i].condition = v })} suggestions={references} states={stateNames} /></td>
                          <td><select className="input" aria-label="On transition" value={a.onTransition ?? ''} onChange={(e) => update((d) => { d.alarms[i].onTransition = e.target.value || null })}>
                            <option value="">— any time —</option>{bp.transitions.map((t) => <option key={t.name}>{t.name}</option>)}</select></td>
                          <td><input type="checkbox" aria-label="Latched" title="Stays active until CMD.reset" checked={!!a.latched} onChange={(e) => update((d) => { d.alarms[i].latched = e.target.checked })} /></td>
                          <td><input className="input bp-narrow" type="number" min={conventions?.severityMin} max={conventions?.severityMax} value={a.severity} onChange={(e) => update((d) => { d.alarms[i].severity = Number(e.target.value) })} /></td>
                          <td><input className="input" value={a.message} onChange={(e) => update((d) => { d.alarms[i].message = e.target.value })} /></td>
                          <td><button className="button button-small" onClick={() => update((d) => { d.alarms.splice(i, 1) })}>Remove</button></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  <div className="bp-row-actions">
                    <button className="button button-small" onClick={() => update((d) => {
                      d.alarms.push({ name: uniqueName('Alarm', d.alarms.map((a) => a.name)), kind: 'Reactive', condition: '', severity: 20, message: '' })
                    })}>+ Alarm</button>
                  </div>
                </div>
              )}

              {tab === 'interlocks' && (
                <InterlockList bp={bp} rolePaths={rolePaths} references={references} stateNames={stateNames}
                  onChange={(change) => update((d) => { d.interlocks ??= []; change(d.interlocks) })} />
              )}

              {tab === 'inputs' && (
                <div className="bp-section">
                  <p className="muted">Default command inputs for every instance of this blueprint. Each instance can change them in the Configurator or with <b>Command inputs</b> on the CM. Members of a Unit get a <b>UNIT</b> input automatically.</p>
                  <CommandInputsEditor value={bp.commandInputs ?? { rows: [], level: false }}
                    onChange={(value) => update((d) => { d.commandInputs = value.rows.length || value.level || value.selector ? value : null })}
                    singleCommands={[...interfaceTags.filter((t) => t.group === 'CMD').map((t) => t.name), ...bp.tags.filter((t) => t.group === 'CMD' && t.dataType === 'Bool').map((t) => t.name)]
                      .filter((n, i, all) => n !== 'set_on' && n !== 'set_off' && all.indexOf(n) === i)}
                    hasPair={interfaceTags.some((t) => t.group === 'CMD' && t.name === 'set_on') && interfaceTags.some((t) => t.group === 'CMD' && t.name === 'set_off')}
                    selectors={bp.tags.filter((t) => t.group === 'FIN' && t.dataType === 'Bool').map((t) => `FIN.${t.name}`)} />
                </div>
              )}

              {tab === 'simulation' && (
                <div className="bp-section">
                  <p className="muted">The plant model runs first in every simulator cycle and writes this blueprint's inputs (FIN tags), for example <code>FIN.feedback := OUT.lamp</code>. It is used only by the simulator, never in PLC code. Saved blueprints without errors appear as CM types in <b>New control module</b>.</p>
                  <ActionList actions={bp.plant ?? []} onChange={(change) => update((d) => { d.plant ??= []; change(d.plant) })} />
                </div>
              )}

              {tab === 'json' && <pre className="bp-json mono">{JSON.stringify(bp, null, 2)}</pre>}
            </div>

            <div className="bp-issues" aria-label="Problems">
              {issues.length === 0 ? <span className="bp-ok">No problems.</span> : issues.map((issue, i) => (
                <div key={i} className={`bp-issue bp-issue-${issue.severity.toLowerCase()}`}>
                  <b>{issue.severity}</b> <span className="muted">{issue.where}</span> — {issue.message}
                </div>
              ))}
            </div>
          </div>
        )}
      </div>
      </Suggestions.Provider>
    </section>
  )
}

function renameState(d: Blueprint, from: string, to: string) {
  for (const s of d.states) {
    if (s.name === from) s.name = to
    if (s.timeout?.goTo === from) s.timeout.goTo = to
  }
  for (const t of d.transitions) {
    t.from = t.from.map((f) => (f === from ? to : f))
    if (t.to === from) t.to = to
  }
}

type StateEditorProps = {
  state: BpState
  states: string[]
  catalog: BpCatalog
  onRename: (name: string) => void
  onChange: (change: (s: BpState) => void) => void
}

function StateEditor({ state, states, catalog, onRename, onChange }: StateEditorProps) {
  const conventions = useConventions()
  const suggest = useContext(Suggestions)
  const [name, setName] = useState(state.name)
  const commit = () => { if (name && name !== state.name) onRename(name) }
  return (
    <div className="bp-state-editor">
      <div className="bp-form">
        <label>Name<input className="input mono" aria-label="State name" value={name} onChange={(e) => setName(e.target.value)} onBlur={commit}
          onKeyDown={(e) => { if (e.key === 'Enter') commit() }} /></label>
        <label>Category<select className="input" aria-label="Category" value={state.category} onChange={(e) => onChange((s) => { s.category = Number(e.target.value) })}>
          {catalog.categories.map((c) => <option key={c.code} value={c.code}>{c.code} {c.label}</option>)}
        </select></label>
        <label className="checkbox"><input type="checkbox" checked={state.initial} onChange={(e) => onChange((s) => { s.initial = e.target.checked })} />Initial state</label>
      </div>
      {(['entry', 'run', 'exit'] as const).map((kind) => (
        <div key={kind} className="bp-action-block">
          <h4>{kind === 'entry' ? 'Entry — once, on entering' : kind === 'run' ? 'Run — every cycle while active' : 'Exit — once, on leaving'}</h4>
          <ActionList actions={state[kind]} onChange={(change) => onChange((s) => change(s[kind]))} />
        </div>
      ))}
      <div className="bp-action-block">
        <h4>
          <label className="checkbox"><input type="checkbox" checked={!!state.timeout}
            onChange={(e) => onChange((s) => { s.timeout = e.target.checked ? { time: '10', goTo: null, alarm: null, alarmKind: 'Reactive', severity: 20 } : null })} />Timeout</label>
        </h4>
        {state.timeout && (
          <div className="bp-form">
            <label>Time (s or PAR tag)<ExpressionInput className="input mono" ariaLabel="Timeout time" value={state.timeout.time} onChange={(v) => onChange((s) => { s.timeout!.time = v })} suggestions={suggest.refs} states={suggest.states} single /></label>
            <label>Go to<select className="input" aria-label="Timeout go to" value={state.timeout.goTo ?? ''} onChange={(e) => onChange((s) => { s.timeout!.goTo = e.target.value || null })}>
              <option value="">— stay —</option>{states.map((n) => <option key={n}>{n}</option>)}</select></label>
            <label>Alarm name<input className="input mono" aria-label="Timeout alarm" value={state.timeout.alarm ?? ''} placeholder="none" onChange={(e) => onChange((s) => { s.timeout!.alarm = e.target.value || null })} /></label>
            {state.timeout.alarm && (
              <>
                <label>Alarm kind<select className="input" value={state.timeout.alarmKind} onChange={(e) => onChange((s) => { s.timeout!.alarmKind = e.target.value as 'Reactive' | 'NonReactive' })}>
                  <option value="Reactive">Reactive</option><option value="NonReactive">Non-reactive</option></select></label>
                <label>Severity<input className="input bp-narrow" type="number" min={conventions?.severityMin} max={conventions?.severityMax} value={state.timeout.severity} onChange={(e) => onChange((s) => { s.timeout!.severity = Number(e.target.value) })} /></label>
              </>
            )}
            <p className="muted bp-wide">With a constant time the Builder generates <code>PAR.{state.name}_timeout</code> so it can be tuned per instance.</p>
          </div>
        )}
      </div>
    </div>
  )
}

function ActionList({ actions, onChange }: { actions: BpAction[]; onChange: (change: (list: BpAction[]) => void) => void }) {
  const suggest = useContext(Suggestions)
  return (
    <div className="bp-actions">
      {actions.map((a, i) => (
        <div key={i} className="bp-action">
          <span className="muted">set</span>
          <ExpressionInput className="input mono" ariaLabel="Action tag" placeholder="OUT.tag" value={a.tag} onChange={(v) => onChange((l) => { l[i].tag = v })} suggestions={suggest.writable} single />
          <span className="muted">:=</span>
          <ExpressionInput className="input mono bp-expr" ariaLabel="Action value" placeholder="expression" value={a.value} onChange={(v) => onChange((l) => { l[i].value = v })} suggestions={suggest.refs} states={suggest.states} />
          <button className="button button-small" onClick={() => onChange((l) => { l.splice(i, 1) })}>×</button>
        </div>
      ))}
      <button className="button button-small" onClick={() => onChange((l) => { l.push({ tag: '', value: '' }) })}>+ set</button>
    </div>
  )
}

type RolePath = { path: string; blueprint?: Blueprint }

const kindLabels: Record<BpInterlock['kind'], string> = { SwitchOn: 'Switch on', SwitchOff: 'Switch off', Trip: 'Trip' }

function InterlockList({ bp, rolePaths, references, stateNames, onChange }: {
  bp: Blueprint
  rolePaths: RolePath[]
  references: string[]
  stateNames: (reference: string) => string[]
  onChange: (change: (list: BpInterlock[]) => void) => void
}) {
  const conventions = useConventions()
  const list = bp.interlocks ?? []
  const hasInterface = (target: string) => target === ''
    ? bp.interfaces.includes('Interlocks')
    : rolePaths.find((r) => r.path === target)?.blueprint?.interfaces.includes('Interlocks') ?? true
  return (
    <div className="grid-wrap">
      <p className="muted">
        Conditions before a CM may switch on or off, and conditions that trip it. No interlock objects and no linking: each line names its <b>target</b>
        {bp.kind === 'CM' ? ' (this blueprint)' : <> (this blueprint, or a role path such as <code>BREAKER</code> or <code>GEN1.BREAKER</code>)</>}.
        The logic runs in the target CM. <b>Switch on</b> / <b>Switch off</b>: the command is blocked while the condition is false.
        <b> Trip</b>: while the target is switching on or on, a true condition switches it off at once and raises a latched alarm on this blueprint; a reset clears it once the condition is gone.
        <b> Escalate</b> sends the nearest EM or Unit above the target to its fault state; that state's actions are the safe state.
      </p>
      <table className="grid bp-grid il-grid">
        <thead><tr><th>Target</th><th>Kind</th><th>Condition</th><th>HMI text</th><th /></tr></thead>
        <tbody>
          {list.map((r, i) => (
            <Fragment key={i}>
            <tr>
              <td>
                {bp.kind === 'CM' ? <span className="muted">this blueprint</span> : (
                  <select className={`input ${hasInterface(r.target) ? '' : 'input-error'}`} aria-label="Target" value={r.target} onChange={(e) => onChange((l) => { l[i].target = e.target.value })}>
                    <option value="">this blueprint</option>
                    {rolePaths.map((p) => <option key={p.path} value={p.path}>{p.path}{p.blueprint ? ` (${p.blueprint.name})` : ''}</option>)}
                  </select>
                )}
              </td>
              <td>
                <select className="input" aria-label="Kind" value={r.kind} onChange={(e) => onChange((l) => { l[i].kind = e.target.value as BpInterlock['kind'] })}>
                  {(Object.keys(kindLabels) as BpInterlock['kind'][]).map((k) => <option key={k} value={k}>{kindLabels[k]}</option>)}
                </select>
              </td>
              <td><ExpressionInput className="input mono bp-expr" ariaLabel="Condition" value={r.condition}
                placeholder={r.kind === 'Trip' ? 'NOT ENGINE.is_running' : 'ENGINE.is_running'}
                onChange={(v) => onChange((l) => { l[i].condition = v })} suggestions={references} states={stateNames} /></td>
              <td><input className="input" aria-label="HMI text" placeholder="generated from the condition" value={r.text} onChange={(e) => onChange((l) => { l[i].text = e.target.value })} /></td>
              <td><button className="button button-small" onClick={() => onChange((l) => { l.splice(i, 1) })}>Remove</button></td>
            </tr>
            {r.kind === 'Trip' && (
              <tr className="il-trip-row">
                <td />
                <td colSpan={4}>
                  <label>Alarm <input className="input mono" aria-label="Trip alarm" placeholder={`Trip${list.slice(0, i + 1).filter((x) => x.kind === 'Trip').length}`} value={r.alarm ?? ''} onChange={(e) => onChange((l) => { l[i].alarm = e.target.value || null })} /></label>
                  <label>Severity <input className="input bp-narrow" type="number" aria-label="Severity" min={conventions?.severityMin} max={conventions?.severityMax} value={r.severity} onChange={(e) => onChange((l) => { l[i].severity = Number(e.target.value) })} /></label>
                  <label>Escalate <select className="input" aria-label="Escalate" value={r.escalate} onChange={(e) => onChange((l) => { l[i].escalate = e.target.value as BpInterlock['escalate'] })}>
                    <option value="None">None: only the target trips</option><option value="EM">EM: its Equipment module goes to Shutdown</option><option value="Unit">Unit: its Unit goes to Shutdown</option>
                  </select></label>
                </td>
              </tr>
            )}
            </Fragment>
          ))}
          {list.length === 0 && <tr><td colSpan={5} className="muted">No interlocks yet.</td></tr>}
        </tbody>
      </table>
      <div className="bp-row-actions">
        {(Object.keys(kindLabels) as BpInterlock['kind'][]).map((k) => (
          <button key={k} className="button button-small" onClick={() => onChange((l) => {
            l.push({ target: '', kind: k, condition: '', text: '', alarm: null, severity: conventions?.defaultSeverity ?? 20, escalate: 'None' })
          })}>+ {kindLabels[k]}</button>
        ))}
      </div>
    </div>
  )
}
