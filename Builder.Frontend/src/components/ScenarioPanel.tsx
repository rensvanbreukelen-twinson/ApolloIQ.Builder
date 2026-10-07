import { useState } from 'react'

type Failure = { cycle: number; location: string; message: string }

type Result = {
  file: string
  type: string
  name: string
  passed: boolean
  cycles: number
  failures: Failure[]
  logicErrors: string[]
}

type TraceEntry = { cycle: number; states: Record<string, number>; changes: Record<string, unknown> }

type TransitionHit = { controlModule: string; index: number; name: string; from: number; to: number }

type TracedResult = Result & { trace: TraceEntry[]; transitions: TransitionHit[] }

type TransitionCoverage = { index: number; name: string; from: string; to: string; hits: number }

type Coverage = { type: string; covered: number; total: number; transitions: TransitionCoverage[] }

type Report = { results: Result[]; coverage: Coverage[]; errors: string[]; passed: number; failed: number }

async function post<T>(url: string, body: unknown): Promise<T> {
  const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`)
  return response.json() as Promise<T>
}

function formatChange(value: unknown): string {
  if (typeof value === 'boolean') return value ? 'TRUE' : 'FALSE'
  if (typeof value === 'number') return String(Math.round(value * 1000) / 1000)
  return String(value)
}

export function ScenarioPanel() {
  const [report, setReport] = useState<Report | null>(null)
  const [running, setRunning] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [trace, setTrace] = useState<TracedResult | null>(null)

  const run = async () => {
    setRunning(true)
    try {
      setReport(await post<Report>('/api/scenarios/run', {}))
      setTrace(null)
      setError(null)
    } catch (reason) {
      setError((reason as Error).message)
    } finally {
      setRunning(false)
    }
  }

  const open = async (result: Result) => {
    try {
      setTrace(await post<TracedResult>('/api/scenarios/trace', { file: result.file, name: result.name }))
    } catch (reason) {
      setError((reason as Error).message)
    }
  }

  return (
    <section className="tag-list simulator">
      <div className="toolbar">
        <span className="toolbar-title">Scenarios</span>
        <button className="button button-primary" disabled={running} onClick={() => void run()}>{running ? 'Running…' : 'Run all scenarios'}</button>
        {report && (
          <span className={report.failed === 0 ? 'sim-status sim-status-running' : 'sim-status scenario-failed'}>
            {report.passed} passed · {report.failed} failed
          </span>
        )}
        <span className="toolbar-count muted">Library/scenarios/*.scenarios.json, run on the reference interpreter</span>
      </div>
      {error && <div className="banner banner-error">{error}</div>}
      {report && report.errors.length > 0 && (
        <div className="banner banner-warning">
          Scenario files with errors:
          <ul>{report.errors.map((e) => <li key={e} className="mono">{e}</li>)}</ul>
        </div>
      )}
      {!report && <p className="empty">Run the scenarios to test the CM types in the library and see which transitions they cover.</p>}
      {report && (
        <div className="sim-body">
          <div className="grid-wrap">
            <div className="sim-cms">
              {report.coverage.map((c) => (
                <div key={c.type} className="sim-cm scenario-coverage">
                  <div className="sim-cm-path">{c.type}</div>
                  <div className={c.covered === c.total ? 'sim-state scn-pass' : 'sim-state scn-partial'}>
                    {c.covered} / {c.total} transitions covered
                  </div>
                  {c.transitions.filter((t) => t.hits === 0).map((t) => (
                    <div key={t.index} className="muted">Not covered: [{t.index}] {t.name} ({t.from} → {t.to})</div>
                  ))}
                </div>
              ))}
            </div>
            <table className="grid sim-grid">
              <thead><tr><th>Type</th><th>Scenario</th><th>Result</th><th>Cycles</th><th>Details</th></tr></thead>
              <tbody>
                {report.results.map((r) => (
                  <tr key={`${r.file}/${r.name}`} onClick={() => void open(r)} className={trace?.name === r.name && trace.file === r.file ? 'scenario-selected' : undefined}>
                    <td>{r.type}</td>
                    <td>{r.name}</td>
                    <td><span className={r.passed ? 'sim-state scn-pass' : 'sim-state scn-fail'}>{r.passed ? 'Passed' : 'Failed'}</span></td>
                    <td className="mono">{r.cycles}</td>
                    <td className="scenario-message">{r.failures.map((f) => `${f.location}, cycle ${f.cycle}: ${f.message}`).join(' ')}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <aside className="sim-events">
            <div className="sim-events-title">{trace ? trace.name : 'Trace'}</div>
            {!trace && <p className="muted">Select a scenario to see its trace: state changes and output changes per cycle.</p>}
            {trace && (
              <ul>
                {trace.trace.filter((t) => Object.keys(t.changes).length > 0).map((t) => (
                  <li key={t.cycle}>
                    <span className="mono muted">{t.cycle}</span>{' '}
                    {Object.entries(t.changes).map(([tag, value]) => `${tag} = ${formatChange(value)}`).join(', ')}
                  </li>
                ))}
              </ul>
            )}
          </aside>
        </div>
      )}
    </section>
  )
}
