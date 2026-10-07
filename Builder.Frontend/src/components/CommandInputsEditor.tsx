import { kindsFor, newRow, type CommandInput, type CommandInputConfig, type CommandSource } from '../api/commandInputs'
import { useConventions } from '../api/conventions'

type Props = {
  value: CommandInputConfig
  onChange: (value: CommandInputConfig) => void
  singleCommands: string[]
  hasPair: boolean
  selectors: string[]
  allowUnitRow?: boolean
}

const sourceText: Record<CommandSource, string> = { Hmi: 'HMI', DigitalInput: 'Digital input', Unit: 'Unit' }

export function CommandInputsEditor({ value, onChange, singleCommands, hasPair, selectors, allowUnitRow = false }: Props) {
  const rows = value.rows
  const set = (index: number, change: Partial<CommandInput>) =>
    onChange({ ...value, rows: rows.map((r, i) => (i === index ? { ...r, ...change } : r)) })
  const remove = (index: number) => onChange({ ...value, rows: rows.filter((_, i) => i !== index) })
  const move = (index: number, delta: number) => {
    const next = [...rows]
    const [row] = next.splice(index, 1)
    next.splice(index + delta, 0, row)
    onChange({ ...value, rows: next })
  }
  const conventions = useConventions()
  const priorityMin = conventions?.priorityMin ?? 1
  const priorityMax = conventions?.priorityMax ?? priorityMin
  const add = (source: CommandSource) => { if (conventions) onChange({ ...value, rows: [...rows, newRow(source, rows.map((r) => r.name), priorityMin, conventions.defaultDebounceSeconds)] }) }
  const hasSwitch = rows.some((r) => r.kind === 'Switch')
  const number = (text: string) => (text.trim() === '' ? null : Number(text))

  return (
    <div className="ci-editor">
      <div className="ci-options">
        <label className="checkbox">
          <input type="checkbox" checked={value.level} onChange={(e) => onChange({ ...value, level: e.target.checked })} />
          Hold-to-run: keep the command on while the winning input is held (otherwise one pulse per win)
        </label>
        <label>
          Local/remote selector
          <select className="input" aria-label="Local/remote selector" value={value.selector ?? ''}
            onChange={(e) => onChange({ ...value, selector: e.target.value || null, rows: e.target.value ? rows : rows.map((r) => ({ ...r, location: 'Any' })) })}>
            <option value="">None</option>
            {selectors.map((s) => <option key={s} value={s}>{s} (TRUE = remote)</option>)}
          </select>
        </label>
      </div>
      {hasSwitch && <div className="banner banner-warning">A maintained switch must be the only command input; the CM then has no HMI or Unit control (G-143).</div>}
      <div className="grid-wrap">
        <table className="grid ci-grid">
          <thead>
            <tr>
              <th>#</th><th>Name</th><th>Source</th><th>Kind</th><th>Drives</th>
              {hasPair && <><th title="Priority for On, 1 = highest, empty = cannot">On</th><th title="Priority for Off">Off</th></>}
              <th>Other commands</th><th>In auto</th>{value.selector && <th>Location</th>}<th>Debounce (s)</th><th>Stuck after (s)</th><th />
            </tr>
          </thead>
          <tbody>
            {rows.map((row, i) => {
              const physical = row.source === 'DigitalInput'
              const unit = row.source === 'Unit'
              return (
                <tr key={i}>
                  <td className="muted">{i + 1}</td>
                  <td><input className="input mono ci-name" aria-label={`Name of input ${i + 1}`} value={row.name} disabled={unit}
                    onChange={(e) => set(i, { name: e.target.value })} /></td>
                  <td>{sourceText[row.source]}</td>
                  <td><select className="input" aria-label={`Kind of ${row.name}`} value={row.kind}
                    onChange={(e) => {
                      const kind = e.target.value as CommandInput['kind']
                      set(i, { kind, drives: kind === 'Switch' ? 'OnOff' : row.drives })
                    }}>
                    {kindsFor[row.source].map((k) => <option key={k}>{k}</option>)}
                  </select></td>
                  <td><select className="input" aria-label={`Drives of ${row.name}`} value={row.drives} disabled={row.kind === 'Switch' || !hasPair}
                    onChange={(e) => set(i, { drives: e.target.value as CommandInput['drives'] })}>
                    <option value="OnOff">{physical && row.kind !== 'Switch' ? 'On/Off (two contacts)' : 'On/Off'}</option>
                    <option value="On">On only</option>
                    <option value="Off">Off only</option>
                    {physical && row.kind === 'Button' && <option value="Toggle">Toggle</option>}
                  </select></td>
                  {hasPair && (
                    <>
                      <td><input className="input ci-prio" type="number" min={priorityMin} max={priorityMax} aria-label={`On priority of ${row.name}`}
                        value={row.on ?? ''} disabled={row.drives === 'Off'} onChange={(e) => set(i, { on: number(e.target.value) })} /></td>
                      <td><input className="input ci-prio" type="number" min={priorityMin} max={priorityMax} aria-label={`Off priority of ${row.name}`}
                        value={row.off ?? ''} disabled={row.drives === 'On'} onChange={(e) => set(i, { off: number(e.target.value) })} /></td>
                    </>
                  )}
                  <td><div className="ci-commands">
                    {singleCommands.length === 0 && <span className="muted">—</span>}
                    {singleCommands.map((c) => (
                      <label key={c} className="checkbox">
                        <input type="checkbox" checked={(row.commands ?? []).includes(c)}
                          onChange={(e) => set(i, { commands: e.target.checked ? [...(row.commands ?? []), c] : (row.commands ?? []).filter((x) => x !== c) })} />
                        {c}
                      </label>
                    ))}
                  </div></td>
                  <td><select className="input" aria-label={`In auto of ${row.name}`} value={row.inAuto} disabled={unit}
                    onChange={(e) => set(i, { inAuto: e.target.value as CommandInput['inAuto'] })}>
                    <option value="Always">Always</option>
                    <option value="Ignore">Ignore in auto</option>
                    <option value="Override">Override (Unit to manual)</option>
                    {unit && <option value="Only">Only in auto</option>}
                  </select></td>
                  {value.selector && (
                    <td><select className="input" aria-label={`Location of ${row.name}`} value={row.location}
                      onChange={(e) => set(i, { location: e.target.value as CommandInput['location'] })}>
                      <option value="Any">Any</option><option value="Local">Local</option><option value="Remote">Remote</option>
                    </select></td>
                  )}
                  <td>{physical ? (
                    <span className="ci-optional">
                      <input type="checkbox" aria-label={`Debounce ${row.name}`} checked={row.debounce != null}
                        onChange={(e) => set(i, { debounce: e.target.checked ? (conventions?.defaultDebounceSeconds ?? null) : null })} />
                      {row.debounce != null && <input className="input ci-prio" type="number" step={0.01} min={0.01} value={row.debounce}
                        aria-label={`Debounce time of ${row.name}`} onChange={(e) => set(i, { debounce: number(e.target.value) })} />}
                    </span>) : <span className="muted">—</span>}</td>
                  <td>{physical ? (
                    <span className="ci-optional">
                      <input type="checkbox" aria-label={`Stuck alarm ${row.name}`} checked={row.stuckTime != null}
                        onChange={(e) => set(i, { stuckTime: e.target.checked ? 30 : null })} />
                      {row.stuckTime != null && <input className="input ci-prio" type="number" min={0.1} value={row.stuckTime}
                        aria-label={`Stuck time of ${row.name}`} onChange={(e) => set(i, { stuckTime: number(e.target.value) })} />}
                    </span>) : <span className="muted">—</span>}</td>
                  <td className="ci-actions">
                    <button className="button button-small" disabled={i === 0} aria-label="Move up" onClick={() => move(i, -1)}>↑</button>
                    <button className="button button-small" disabled={i === rows.length - 1} aria-label="Move down" onClick={() => move(i, 1)}>↓</button>
                    {!unit && <button className="button button-small" aria-label={`Remove ${row.name}`} onClick={() => remove(i)}>✕</button>}
                  </td>
                </tr>
              )
            })}
            {rows.length === 0 && <tr><td colSpan={12} className="empty">No command inputs: the commands are written directly (no arbitration).</td></tr>}
          </tbody>
        </table>
      </div>
      <div className="bp-row-actions">
        <button className="button button-small" onClick={() => add('Hmi')}>+ HMI input</button>
        <button className="button button-small" onClick={() => add('DigitalInput')}>+ Digital input</button>
        {allowUnitRow && !rows.some((r) => r.source === 'Unit') && <button className="button button-small" onClick={() => add('Unit')}>+ Unit input</button>}
      </div>
      <p className="muted ci-help">
        The lowest number wins; the same number in opposite directions gives no command; a held input that lost must be released and pressed again.
        HMI inputs write <code>CMD.&lt;name&gt;_on</code> once and the PLC resets them; HMI hold inputs are refreshed every 200 ms.
        Digital inputs get <code>FIN.&lt;name&gt;</code> and <code>SET.invert_&lt;name&gt;</code>; a Button with On/Off has two contacts (<code>_on</code>, <code>_off</code>).
        Debounce and stuck alarms are optional; without them no timers are generated.
      </p>
    </div>
  )
}
