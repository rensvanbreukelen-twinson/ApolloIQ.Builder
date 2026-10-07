import { useEffect, useMemo, useState } from 'react'
import { isWritable, simulationApi, useSimulation, type SimAlarm, type SimLink, type SimTag, type SimValue, type TcpLink } from '../api/simulation'
import type { TreeNode } from '../api/types'
import { stateClass, useConventions } from '../api/conventions'

type Props = {
  projectId: string
  scope: TreeNode | null
  revision: number
}

const groups = ['FIN', 'CMD', 'OUT', 'LOK', 'PAR', 'SET', 'PMT', 'STS', 'INT', 'ALM']
const speeds = [0.5, 1, 2, 5, 10, 50]

function formatValue(tag: SimTag): string {
  if (tag.value === null || tag.value === undefined) return '—'
  if (typeof tag.value === 'boolean') return tag.value ? 'TRUE' : 'FALSE'
  if (typeof tag.value === 'number' && tag.dataType === 'Real') return String(Math.round(tag.value * 1000) / 1000)
  return String(tag.value)
}

function parseInput(tag: SimTag, text: string): SimValue {
  if (tag.dataType === 'Bool') return ['1', 'true', 'on'].includes(text.trim().toLowerCase())
  if (tag.dataType === 'String' || tag.dataType === 'DateTime') return text
  return text.trim()
}

export function SimulatorPanel({ projectId, scope, revision }: Props) {
  const sim = useSimulation(projectId, revision)
  const conventions = useConventions()
  const [group, setGroup] = useState('')
  const [search, setSearch] = useState('')
  const [editing, setEditing] = useState<{ id: string; text: string } | null>(null)
  const [onlyForced, setOnlyForced] = useState(false)
  const [tcp, setTcp] = useState<TcpLink | null>(null)
  const [links, setLinks] = useState<SimLink[]>([])
  const [alarms, setAlarms] = useState<SimAlarm[]>([])

  useEffect(() => {
    let cancelled = false
    const poll = () => simulationApi.alarms(projectId).then((list) => { if (!cancelled) setAlarms(list) }).catch(() => undefined)
    void poll()
    const timer = window.setInterval(() => void poll(), 500)
    return () => { cancelled = true; window.clearInterval(timer) }
  }, [projectId, revision])

  useEffect(() => {
    let cancelled = false
    simulationApi.links(projectId).then((loaded) => { if (!cancelled) setLinks(loaded) }).catch(() => undefined)
    return () => { cancelled = true }
  }, [projectId, revision])

  useEffect(() => {
    let cancelled = false
    const poll = () => simulationApi.tcp().then((link) => { if (!cancelled) setTcp(link) }).catch(() => undefined)
    void poll()
    const timer = window.setInterval(poll, 3000)
    return () => { cancelled = true; window.clearInterval(timer) }
  }, [])

  const prefix = scope ? `${scope.path}.` : ''
  const visibleTags = useMemo(() => sim.tags.filter((tag) =>
    (!prefix || tag.path.startsWith(prefix))
    && (!group || tag.group === group)
    && (!onlyForced || tag.forced || !tag.good)
    && (!search.trim() || tag.path.toLowerCase().includes(search.trim().toLowerCase()))), [sim.tags, prefix, group, onlyForced, search])

  const visibleCms = useMemo(() => (sim.state?.controlModules ?? [])
    .filter((cm) => !scope || cm.path === scope.path || cm.path.startsWith(prefix))
    .sort((a, b) => a.path.localeCompare(b.path)), [sim.state, scope, prefix])

  if (!sim.state) {
    return <section className="tag-list"><p className="empty">{sim.error ?? 'Loading the simulator…'}</p></section>
  }

  const activeAlarms = alarms.filter((a) => a.active && (!prefix || a.object === scope?.path || a.object.startsWith(prefix)))
    .sort((a, b) => b.priority - a.priority)
  const { status } = sim.state
  const running = status.status === 'Running'
  const forcedCount = sim.tags.filter((t) => t.forced || !t.good).length

  const submit = (tag: SimTag, text: string, force: boolean) => {
    const value = parseInput(tag, text)
    setEditing(null)
    return sim.tagAction(() => force ? simulationApi.force(projectId, tag.path, value) : simulationApi.write(projectId, tag.path, value))
  }

  const toggle = (tag: SimTag) => {
    const next = !(tag.value as boolean)
    return sim.tagAction(() => isWritable(tag.group, tag.path) && !tag.forced
      ? simulationApi.write(projectId, tag.path, next)
      : simulationApi.force(projectId, tag.path, next))
  }

  return (
    <section className="tag-list simulator">
      <div className="toolbar">
        <span className="toolbar-title">Simulator</span>
        <span className={`sim-status ${running ? 'sim-status-running' : ''}`}>{status.status}</span>
        <button className="button button-primary" aria-label={running ? 'Pause' : 'Run'}
          onClick={() => void sim.control(() => running ? simulationApi.pause(projectId) : simulationApi.start(projectId))}>
          {running ? 'Pause' : 'Run'}
        </button>
        <button className="button" disabled={running} onClick={() => void sim.control(() => simulationApi.step(projectId, 1))}>Step</button>
        <button className="button" disabled={running}
          onClick={() => void sim.control(() => simulationApi.step(projectId, Math.round(1 / status.cycleSeconds)))}>+1 s</button>
        <button className="button" onClick={() => void sim.control(() => simulationApi.reset(projectId)).then(() => sim.load())}>Reset</button>
        <label className="sim-speed">
          Speed
          <select className="input" value={status.speed}
            onChange={(e) => void sim.control(() => simulationApi.speed(projectId, Number(e.target.value)))}>
            {speeds.map((s) => <option key={s} value={s}>{s}×</option>)}
          </select>
        </label>
        <span className="mono sim-clock">cycle {status.cycle} · {status.timeSeconds.toFixed(2)} s</span>
        {tcp?.port != null && (tcp.projectId === projectId
          ? <span className="sim-tcp" title="SCADA TcpDriver connection">HMI link on port {tcp.port} · {tcp.clients} connected</span>
          : <button className="button button-small" onClick={() => void simulationApi.serve(projectId).then(setTcp)}>Serve to HMI (port {tcp.port})</button>)}
        <span className={`sim-link ${sim.connected ? '' : 'sim-link-down'}`}>{sim.connected ? 'live' : 'not live'}</span>
      </div>

      {sim.error && <div className="banner banner-error">{sim.error}</div>}
      {sim.state.errors.length > 0 && (
        <div className="banner banner-warning">
          Logic errors: these parts of the logic are not executed.
          <ul>{sim.state.errors.map((e) => <li key={e} className="mono">{e}</li>)}</ul>
        </div>
      )}

      {activeAlarms.length > 0 && (
        <div className="sim-alarms" aria-label="Active alarms">
          <span className="sim-alarms-title">Active alarms</span>
          {activeAlarms.map((alarm) => (
            <span key={`${alarm.object}.${alarm.name}`} className={`sim-alarm alarm-band-${(alarm.rangeLevel ?? alarm.level).toLowerCase()}`}
              title={`${alarm.object} · ${alarm.name} · priority ${alarm.priority} · ${alarm.plcReactive ? 'PLC reactive (PLC logic)' : 'SCADA alarm (evaluated as SCADA will)'}`}>
              {alarm.message} <span className="mono muted">{alarm.plcReactive ? 'PLC' : 'SCADA'}</span>
            </span>
          ))}
        </div>
      )}

      {links.length > 0 && (
        <div className="sim-links" aria-label="Links">
          <span className="sim-alarms-title sim-links-title">Links</span>
          {links.map((link) => (
            <button key={link.id} className={`sim-link-toggle ${link.down ? 'sim-link-toggle-down' : ''}`}
              title={`${link.protocol}, ${link.class} link, ${link.tags} control values cross it. Click to ${link.down ? 'restore' : 'cut'} it.`}
              onClick={() => void sim.run(() => simulationApi.setLink(projectId, link.id, !link.down)).then((result) => { if (result) setLinks(result) })}>
              {link.from} ↔ {link.to} {link.down ? '· cut' : ''}
            </button>
          ))}
        </div>
      )}

      <div className="sim-cms">
        {visibleCms.map((cm) => (
          <div key={cm.id} className="sim-cm">
            <div className="mono sim-cm-path">{cm.path}</div>
            <div className="muted">{cm.type}</div>
            <div className={stateClass(conventions, cm.state)}>{cm.state} {cm.stateText && cm.stateText !== cm.stateName ? `${cm.stateName} · ${cm.stateText}` : cm.stateName}</div>
            <div className="muted">{(((status.cycle - (cm.enteredCycle ?? status.cycle)) * status.cycleSeconds)).toFixed(1)} s in state</div>
          </div>
        ))}
      </div>

      <div className="toolbar">
        <input className="input input-search" placeholder="Search path" value={search} onChange={(e) => setSearch(e.target.value)} />
        <select className="input" aria-label="Group" value={group} onChange={(e) => setGroup(e.target.value)}>
          <option value="">All groups</option>
          {groups.map((g) => <option key={g} value={g}>{g}</option>)}
        </select>
        <label className="checkbox">
          <input type="checkbox" checked={onlyForced} onChange={(e) => setOnlyForced(e.target.checked)} />
          Only forced or bad ({forcedCount})
        </label>
        <button className="button" disabled={forcedCount === 0}
          onClick={() => void sim.run(() => simulationApi.unforceAll(projectId))}>
          Release all
        </button>
        <span className="toolbar-count">{visibleTags.length} tags</span>
      </div>

      <div className="sim-body">
        <div className="grid-wrap">
          <table className="grid sim-grid">
            <thead>
              <tr><th>Path</th><th>Group</th><th>Type</th><th>Value</th><th>Actions</th></tr>
            </thead>
            <tbody>
              {visibleTags.map((tag) => {
                const writable = isWritable(tag.group, tag.path)
                const isEditing = editing?.id === tag.id
                return (
                  <tr key={tag.id} className={tag.forced ? 'sim-forced' : !tag.good ? 'sim-bad' : undefined}>
                    <td className="mono">{tag.path}</td>
                    <td><span className={`badge badge-${tag.group.toLowerCase()}`}>{tag.group}</span></td>
                    <td>{tag.dataType}</td>
                    <td className="mono sim-value">
                      {isEditing ? (
                        <form onSubmit={(e) => { e.preventDefault(); void submit(tag, editing.text, !writable || tag.forced) }}>
                          <input className="input sim-input" autoFocus value={editing.text} aria-label={`Value of ${tag.path}`}
                            onChange={(e) => setEditing({ id: tag.id, text: e.target.value })}
                            onKeyDown={(e) => { if (e.key === 'Escape') setEditing(null) }} />
                        </form>
                      ) : tag.dataType === 'Bool' ? (
                        <button className={`sim-bool ${tag.value ? 'sim-bool-on' : ''}`} title={writable ? 'Toggle' : 'Force the opposite value'}
                          onClick={() => void toggle(tag)}>{formatValue(tag)}</button>
                      ) : (
                        <button className="sim-number" title={writable ? 'Edit' : 'Force a value'}
                          onClick={() => setEditing({ id: tag.id, text: tag.value === null ? '' : String(tag.value) })}>{formatValue(tag)}</button>
                      )}
                      {tag.forced && <span className="sim-flag">forced</span>}
                      {!tag.good && <span className="sim-flag sim-flag-bad">bad</span>}
                    </td>
                    <td className="sim-actions">
                      {tag.forced
                        ? <button className="button button-small" onClick={() => void sim.tagAction(() => simulationApi.unforce(projectId, tag.path))}>Release</button>
                        : <button className="button button-small" onClick={() => void sim.tagAction(() => simulationApi.force(projectId, tag.path, tag.value))}>Force</button>}
                      <button className="button button-small" onClick={() => void sim.tagAction(() => simulationApi.quality(projectId, tag.path, tag.good))}>
                        {tag.good ? 'Bad quality' : 'Good quality'}
                      </button>
                    </td>
                  </tr>
                )
              })}
              {visibleTags.length === 0 && <tr><td colSpan={5} className="empty">No tags match.</td></tr>}
            </tbody>
          </table>
        </div>
        <aside className="sim-events">
          <div className="sim-events-title">
            Events
            <button className="button button-small" onClick={sim.clearEvents}>Clear</button>
          </div>
          {sim.events.length === 0 && <p className="muted">State changes and diagnostics appear here.</p>}
          <ul>
            {sim.events.map((event) => (
              <li key={event.key} className={event.kind === 'diagnostic' ? 'sim-event-diagnostic' : undefined}>
                <span className="mono muted">{event.cycle}</span> {event.text}
              </li>
            ))}
          </ul>
        </aside>
      </div>
    </section>
  )
}
