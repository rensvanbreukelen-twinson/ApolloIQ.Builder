import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { ExportCheck } from '../api/types'
import { Modal } from './Modal'

type Props = {
  projectId: string
  projectName: string
  onClose: () => void
}

/**
 * Export to SCADA: checks the project, lists the blueprints with their versions (a blueprint that changed since the last
 * export without a new version blocks the export), shows errors and warnings, and downloads <project>.apolloiq.json.
 */
export function ExportDialog({ projectId, projectName, onClose }: Props) {
  const [check, setCheck] = useState<ExportCheck | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState<string | null>(null)

  const runCheck = () => api.exportCheck(projectId).then((c) => { setCheck(c); setError(null) }).catch((reason: Error) => setError(reason.message))

  useEffect(() => {
    let cancelled = false
    api.exportCheck(projectId).then((c) => { if (!cancelled) setCheck(c) }).catch((reason: Error) => { if (!cancelled) setError(reason.message) })
    return () => { cancelled = true }
  }, [projectId])

  const download = async () => {
    setBusy(true)
    try {
      const blob = await api.exportToScada(projectId)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = check?.fileName ?? `${projectName}.apolloiq.json`
      link.click()
      URL.revokeObjectURL(url)
      setDone(link.download)
      await runCheck()
    } catch (reason) {
      setError((reason as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const blocked = !check || check.errors.length > 0

  return (
    <Modal title={`Export ${projectName} to SCADA`} onClose={onClose} wide
      footer={<>
        <button className="button" onClick={() => void runCheck()}>Check again</button>
        <span className="spacer" />
        <button className="button" onClick={onClose}>Close</button>
        <button className="button button-primary" disabled={blocked || busy} onClick={() => void download()}>Download for SCADA</button>
      </>}>
      <p className="muted">
        SCADA imports the file <code>{check?.fileName ?? `${projectName}.apolloiq.json`}</code>: the blueprints the project uses (tags, states, alarms,
        interlock texts) and every CM, Equipment module and Unit. A blueprint that changed since the last export needs a new version first.
      </p>
      {error && <p className="form-error">{error}</p>}
      {done && <div className="banner banner-info">Downloaded {done}. The versions are recorded for the next export.</div>}
      {!check && !error && <p className="muted">Checking…</p>}
      {check && (
        <>
          <table className="grid">
            <thead><tr><th>Blueprint</th><th>Version</th><th>Last export</th><th /></tr></thead>
            <tbody>
              {check.blueprints.map((b) => {
                const unbumped = b.changedSinceLastExport && b.version === b.lastExportedVersion
                return (
                  <tr key={b.id} className={unbumped ? 'export-unbumped' : undefined}>
                    <td className="mono">{b.name}</td>
                    <td className="mono">{b.version}</td>
                    <td className="mono muted">{b.lastExportedVersion ?? '—'}</td>
                    <td>{unbumped ? <b className="form-error">changed — raise the version</b> : b.changedSinceLastExport ? 'changed, new version' : b.lastExportedVersion ? 'unchanged' : 'first export'}</td>
                  </tr>
                )
              })}
              {check.blueprints.length === 0 && <tr><td colSpan={4} className="empty">The project has no CMs, Equipment modules or Units yet.</td></tr>}
            </tbody>
          </table>
          {check.errors.length > 0 && (
            <div className="banner banner-error">
              <b>The export is blocked:</b>
              <ul>{check.errors.map((e, i) => <li key={i}>{e}</li>)}</ul>
            </div>
          )}
          {check.warnings.length > 0 && (
            <div className="banner banner-warning">
              <b>Not carried over to SCADA:</b>
              <ul>{check.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>
            </div>
          )}
          {check.errors.length === 0 && check.warnings.length === 0 && <p className="muted">No problems found.</p>}
        </>
      )}
    </Modal>
  )
}
