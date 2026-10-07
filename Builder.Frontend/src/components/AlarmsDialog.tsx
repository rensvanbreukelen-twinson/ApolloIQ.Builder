import { useEffect, useState } from 'react'
import { api } from '../api/client'
import { useConventions } from '../api/conventions'
import type { AlarmDefinition, TreeNode } from '../api/types'
import { Modal } from './Modal'

type Props = {
  projectId: string
  node: TreeNode
  onClose: () => void
}

function bandClass(band: string) {
  return `alarm-band alarm-band-${band.toLowerCase()}`
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
    const severity = trimmed === '' ? null : Number(trimmed)
    const min = conventions?.severityMin ?? 0
    const max = conventions?.severityMax ?? 0
    if (severity !== null && (!conventions || !Number.isInteger(severity) || severity < min || severity > max)) {
      setError(`Severity must be a whole number from ${min} to ${max}.`)
      return
    }
    try {
      setAlarms(await api.setSeverity(projectId, node.id, alarm.name, severity === alarm.defaultSeverity ? null : severity))
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
      <p className="muted">Severity 0–9 caution, 10–19 warning, 20–30 alarm. Leave the field empty to use the type's default.</p>
      <table className="grid alarm-grid">
        <thead><tr><th>Alarm</th><th>Severity</th><th>Condition</th><th>Latch</th><th>Runs on</th></tr></thead>
        <tbody>
          {alarms.map((alarm) => {
            const draft = drafts[alarm.name]
            const custom = alarm.severity !== alarm.defaultSeverity
            return (
              <tr key={alarm.name} title={alarm.message.en ?? alarm.name}>
                <td>
                  <div>{alarm.message.en ?? alarm.name}</div>
                  <div className="mono muted">{alarm.name}</div>
                </td>
                <td className="alarm-severity">
                  <input className="input" aria-label={`Severity of ${alarm.name}`} inputMode="numeric"
                    value={draft ?? String(alarm.severity)}
                    onChange={(e) => setDrafts((current) => ({ ...current, [alarm.name]: e.target.value }))}
                    onBlur={(e) => { if (draft !== undefined) void save(alarm, e.target.value) }}
                    onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }} />
                  <span className={bandClass(alarm.band)}>{alarm.band}</span>
                  {custom && <span className="muted"> default {alarm.defaultSeverity}</span>}
                </td>
                <td className="mono alarm-condition">
                  {alarm.onTransition && <div>on transition “{alarm.onTransition}”</div>}
                  {alarm.condition !== 'TRUE' && <div>{alarm.condition}</div>}
                </td>
                <td className="mono">{alarm.latch === 'FALSE' ? '—' : alarm.latch === 'TRUE' ? 'until reset' : alarm.latch}</td>
                <td>{alarm.runsOn}</td>
              </tr>
            )
          })}
          {alarms.length === 0 && !error && <tr><td colSpan={5} className="empty">This CM type has no alarms.</td></tr>}
        </tbody>
      </table>
    </Modal>
  )
}
