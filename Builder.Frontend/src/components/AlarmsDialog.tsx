import { useEffect, useState } from 'react'
import { api } from '../api/client'
import { levelOf, useConventions } from '../api/conventions'
import type { AlarmDefinition, TreeNode } from '../api/types'
import { Modal } from './Modal'

type Props = {
  projectId: string
  node: TreeNode
  onClose: () => void
}

function levelClass(level: string | null) {
  return `alarm-band alarm-band-${(level ?? 'caution').toLowerCase()}`
}

const sourceLabels: Record<AlarmDefinition['source'], string> = {
  Blueprint: 'blueprint',
  StateTimeout: 'state timeout',
  Trip: 'trip',
  UnitOverride: 'override',
  StuckInput: 'command input',
}

export function AlarmsDialog({ projectId, node, onClose }: Props) {
  const [alarms, setAlarms] = useState<AlarmDefinition[]>([])
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const conventions = useConventions()

  useEffect(() => {
    let cancelled = false
    api.alarms(projectId, node.id)
      .then((loaded) => { if (!cancelled) setAlarms(loaded) })
      .catch((reason: Error) => { if (!cancelled) setError(reason.message) })
    return () => { cancelled = true }
  }, [projectId, node.id])

  const save = async (alarm: AlarmDefinition, text: string) => {
    const trimmed = text.trim()
    const priority = trimmed === '' ? null : Number(trimmed)
    const min = conventions?.alarmPriorityMin ?? 0
    const max = conventions?.alarmPriorityMax ?? 30
    if (priority !== null && (!Number.isInteger(priority) || priority < min || priority > max)) {
      setError(`The priority must be a whole number from ${min} to ${max}.`)
      return
    }
    try {
      setAlarms(await api.setPriority(projectId, node.id, alarm.name, priority === alarm.defaultPriority ? null : priority))
      setDrafts((current) => { const next = { ...current }; delete next[alarm.name]; return next })
      setError(null)
    } catch (reason) {
      setError((reason as Error).message)
    }
  }

  return (
    <Modal title={`Alarms of ${node.path}`} onClose={onClose} extraWide
      footer={<button className="button button-primary" onClick={onClose}>Done</button>}>
      {error && <p className="form-error">{error}</p>}
      <p className="muted">
        Priority 0–9 caution, 10–19 warning, 20–30 alarm. Leave the field empty to use the blueprint's priority. <b>PLC reactive</b> alarms are
        evaluated by the PLC logic (ALM tags); the others are evaluated by SCADA. A priority changed here is not carried over to SCADA.
      </p>
      <table className="grid alarm-grid">
        <thead><tr><th>Alarm</th><th>Priority</th><th>Trigger</th><th>Evaluated by</th><th>Latch</th></tr></thead>
        <tbody>
          {alarms.map((alarm) => {
            const draft = drafts[alarm.name]
            const custom = alarm.priority !== alarm.defaultPriority
            const shown = draft ?? String(alarm.priority)
            return (
              <tr key={alarm.name} title={alarm.message}>
                <td>
                  <div>{alarm.message}</div>
                  <div className="mono muted">{alarm.name} · {sourceLabels[alarm.source]}</div>
                </td>
                <td className="alarm-severity">
                  <input className="input" aria-label={`Priority of ${alarm.name}`} inputMode="numeric" value={shown}
                    onChange={(e) => setDrafts((current) => ({ ...current, [alarm.name]: e.target.value }))}
                    onBlur={(e) => { if (draft !== undefined) void save(alarm, e.target.value) }}
                    onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }} />
                  <span className={levelClass(levelOf(conventions, Number(shown)) ?? alarm.level)}>{levelOf(conventions, Number(shown)) ?? alarm.level}</span>
                  {custom && <span className="muted"> blueprint {alarm.defaultPriority}</span>}
                </td>
                <td className="mono alarm-condition">
                  <div className="muted">{alarm.trigger}</div>
                  {alarm.onTransition && <div>on transition “{alarm.onTransition}”</div>}
                  {alarm.condition && alarm.condition !== 'TRUE' && <div>{alarm.condition}</div>}
                </td>
                <td>{alarm.plcReactive ? <>PLC{alarm.activeTag && <div className="mono muted">{alarm.activeTag}</div>}</> : 'SCADA'}</td>
                <td>{alarm.latched ? 'until reset' : '—'}</td>
              </tr>
            )
          })}
          {alarms.length === 0 && !error && <tr><td colSpan={5} className="empty">This blueprint has no alarms.</td></tr>}
        </tbody>
      </table>
    </Modal>
  )
}
