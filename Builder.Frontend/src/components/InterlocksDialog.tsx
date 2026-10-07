import { Fragment, useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import { levelOf, useConventions } from '../api/conventions'
import type { InterlockKind, InterlockRule, ObjectInterlocks, TripEscalation } from '../api/types'
import { ExpressionInput } from './ExpressionInput'
import { Modal } from './Modal'

type Props = {
  projectId: string
  objectId: string
  title: string
  onClose: () => void
  onChanged: () => void
}

type Draft = {
  targetId: string | null
  kind: InterlockKind
  condition: string
  text: string
  alarm: string
  priority: number
  escalate: TripEscalation
  generatedText: string
  error: string | null
}

const kindLabels: Record<InterlockKind, string> = {
  SwitchOn: 'Switch on',
  SwitchOff: 'Switch off',
  Trip: 'Trip',
}

const kindHelp: Record<InterlockKind, string> = {
  SwitchOn: 'may switch on when the condition is true',
  SwitchOff: 'may switch off when the condition is true',
  Trip: 'switches off at once and raises a latched alarm when the condition is true',
}

function toDraft(rule: InterlockRule): Draft {
  return {
    targetId: rule.targetId,
    kind: rule.kind,
    condition: rule.condition,
    text: rule.text,
    alarm: rule.alarm ?? '',
    priority: rule.priority,
    escalate: rule.escalate,
    generatedText: rule.generatedText,
    error: rule.error,
  }
}

/** Project-level interlocks of a CM, EM or Unit (G-172): lists of conditions, no interlock objects. */
export function InterlocksDialog({ projectId, objectId, title, onClose, onChanged }: Props) {
  const conventions = useConventions()
  const [data, setData] = useState<ObjectInterlocks | null>(null)
  const [drafts, setDrafts] = useState<Draft[]>([])
  const [dirty, setDirty] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [tags, setTags] = useState<string[]>([])
  const [states, setStates] = useState<Record<string, string[]>>({})

  useEffect(() => {
    let cancelled = false
    Promise.all([api.objectInterlocks(projectId, objectId), api.tags(projectId, {}), api.states(projectId)])
      .then(([loaded, allTags, objectStates]) => {
        if (cancelled) return
        setStates(objectStates)
        setData(loaded)
        setDrafts(loaded.interlocks.map(toDraft))
        setTags(allTags.map((t) => t.path))
      })
      .catch((reason: Error) => { if (!cancelled) setError(reason.message) })
    return () => { cancelled = true }
  }, [projectId, objectId])

  const suggestions = useMemo(() => {
    const owners = [...new Set(tags.map((t) => t.split('.').slice(0, -2).join('.')))]
    const aliases = owners.flatMap((o) => ['is_running', 'is_off', 'is_stopped', 'is_available', 'is_starting', 'is_stopping', 'is_shutdown'].map((a) => `${o}.${a}`))
    return [...tags, ...aliases]
  }, [tags])

  const change = (index: number, patch: Partial<Draft>) => {
    setDrafts((list) => list.map((d, i) => (i === index ? { ...d, ...patch } : d)))
    setDirty(true)
  }

  const validate = async (index: number, condition: string) => {
    if (!condition.trim()) return
    try {
      const result = await api.validateInterlock(projectId, objectId, condition)
      setDrafts((list) => list.map((d, i) => (i === index ? { ...d, error: result.error } : d)))
    } catch {
      /* the save reports it */
    }
  }

  const save = async () => {
    setBusy(true)
    try {
      const result = await api.setObjectInterlocks(projectId, objectId, drafts.map((d) => ({
        targetId: d.targetId,
        kind: d.kind,
        condition: d.condition,
        text: d.text.trim() || null,
        alarm: d.kind === 'Trip' ? d.alarm.trim() || null : null,
        priority: d.kind === 'Trip' ? d.priority : null,
        escalate: d.kind === 'Trip' ? d.escalate : null,
      })))
      setData(result)
      setDrafts(result.interlocks.map(toDraft))
      setDirty(false)
      setError(null)
      onChanged()
    } catch (reason) {
      setError((reason as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const close = () => {
    if (dirty && !window.confirm('Discard the unsaved interlocks?')) return
    onClose()
  }

  const targets = data?.targets ?? []

  return (
    <Modal title={`Interlocks of ${title}`} onClose={close} extraWide
      footer={<>
        <button className="button" onClick={close}>Close</button>
        <button className="button button-primary" disabled={!dirty || busy} onClick={() => void save()}>Save</button>
      </>}>
      {error && <p className="form-error">{error}</p>}
      <p className="muted">
        Lists of conditions, no interlock objects. <b>Switch on</b> and <b>Switch off</b> lines block the command (and grey out the HMI button) while one is false.
        A <b>Trip</b> switches the target off at once while it is switching on or on, raises a latched alarm on this object, and needs a reset.
        The logic always runs in the target CM. Use paths such as <code>[PMS.SHORE_CB.is_closed]</code>, or this object's own tags such as <code>STS.auto</code>.
      </p>

      {data && !data.hasInterlocks && targets.every((t) => !t.hasInterlocks) && (
        <div className="banner banner-warning">Neither this object nor anything below it has the Interlocks interface.</div>
      )}

      <div className="grid-wrap">
        <table className="grid bp-grid il-grid">
          <thead><tr><th>Target</th><th>Kind</th><th>Condition</th><th>HMI text</th><th /></tr></thead>
          <tbody>
            {drafts.map((d, i) => (
              <Fragment key={i}>
              <tr>
                <td>
                  <select className="input" aria-label="Target" value={d.targetId ?? ''} onChange={(e) => change(i, { targetId: e.target.value || null })}>
                    {targets.map((t) => (
                      <option key={t.id} value={t.id === objectId ? '' : t.id} disabled={!t.hasInterlocks}>
                        {t.id === objectId ? `${t.path} (this)` : t.path}{t.hasInterlocks ? '' : ' — no Interlocks interface'}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <select className="input" aria-label="Kind" title={kindHelp[d.kind]} value={d.kind} onChange={(e) => change(i, { kind: e.target.value as InterlockKind })}>
                    {(Object.keys(kindLabels) as InterlockKind[]).map((k) => <option key={k} value={k}>{kindLabels[k]}</option>)}
                  </select>
                </td>
                <td>
                  <ExpressionInput className={`input mono bp-expr ${d.error ? 'input-error' : ''}`} ariaLabel="Condition" bracketed value={d.condition}
                    placeholder={d.kind === 'Trip' ? '![PMS.GEN1.is_running]' : '[PMS.GEN1.is_running] && [PMS.CB1.STS.state] != Tripped'}
                    onChange={(v) => { change(i, { condition: v, error: null }); void validate(i, v) }} suggestions={suggestions} states={(reference) => states[reference] ?? []} />
                  {d.error && <div className="form-error">{d.error}</div>}
                </td>
                <td><input className="input" aria-label="HMI text" placeholder={d.generatedText || 'generated from the condition'} value={d.text} onChange={(e) => change(i, { text: e.target.value })} /></td>
                <td><button className="button button-small" onClick={() => { setDrafts((list) => list.filter((_, j) => j !== i)); setDirty(true) }}>Remove</button></td>
              </tr>
              {d.kind === 'Trip' && (
                <tr className="il-trip-row">
                  <td />
                  <td colSpan={4}>
                    <label>Alarm <input className="input mono" aria-label="Trip alarm" placeholder={`Trip${drafts.slice(0, i + 1).filter((x) => x.kind === 'Trip').length}`} value={d.alarm} onChange={(e) => change(i, { alarm: e.target.value })} /></label>
                    <label>Priority <input className="input bp-narrow" aria-label="Priority" type="number" min={conventions?.alarmPriorityMin} max={conventions?.alarmPriorityMax} value={d.priority} onChange={(e) => change(i, { priority: Number(e.target.value) })} /> <span className="muted">{levelOf(conventions, d.priority)}</span></label>
                    <label>Escalate <select className="input" aria-label="Escalate" value={d.escalate} onChange={(e) => change(i, { escalate: e.target.value as TripEscalation })}>
                      <option value="None">None: only the target trips</option><option value="EM">EM: its Equipment module goes to Shutdown</option><option value="Unit">Unit: its Unit goes to Shutdown</option>
                    </select></label>
                  </td>
                </tr>
              )}
              </Fragment>
            ))}
            {drafts.length === 0 && <tr><td colSpan={5} className="muted">No project-level interlocks on this object.</td></tr>}
          </tbody>
        </table>
        <div className="bp-row-actions">
          {(Object.keys(kindLabels) as InterlockKind[]).map((k) => (
            <button key={k} className="button button-small" onClick={() => {
              const first = targets.find((t) => t.hasInterlocks)
              const targetId = !first || first.id === objectId ? null : first.id
              setDrafts((list) => [...list, { targetId, kind: k, condition: '', text: '', alarm: '', priority: conventions?.defaultAlarmPriority ?? 25, escalate: 'None', generatedText: '', error: null }])
              setDirty(true)
            }}>+ {kindLabels[k]}</button>
          ))}
        </div>
      </div>

      {data && data.actingOnThis.length > 0 && (
        <>
          <h4>Acting on {data.path} from its blueprint and containers</h4>
          <table className="grid il-grid">
            <thead><tr><th>Kind</th><th>Text</th><th>Condition</th><th>Defined by</th></tr></thead>
            <tbody>
              {data.actingOnThis.map((a, i) => (
                <tr key={i} className="bp-readonly">
                  <td>{kindLabels[a.kind]}{a.kind === 'Trip' && a.escalate !== 'None' ? ` → ${a.escalate}` : ''}</td>
                  <td>{a.text}</td>
                  <td className="mono">{a.condition}</td>
                  <td className="mono">{a.definedBy} <span className="muted">({a.origin}{a.alarm ? `, alarm ${a.alarm}` : ''})</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </Modal>
  )
}
