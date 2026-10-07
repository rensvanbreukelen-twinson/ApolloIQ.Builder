import { useEffect, useId, useState } from 'react'
import { ApiError, api } from '../api/client'
import type { HmiAddressMode, HmiExportProfile } from '../api/types'
import { Modal } from './Modal'

type Props = {
  projectId: string
  projectName: string
  onClose: () => void
}

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

function download(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.click()
  URL.revokeObjectURL(url)
}

export function ExportDialog({ projectId, projectName, onClose }: Props) {
  const [profile, setProfile] = useState<HmiExportProfile | null>(null)
  const [connectionId, setConnectionId] = useState('')
  const [scanRate, setScanRate] = useState('1000')
  const [address, setAddress] = useState<HmiAddressMode>('Path')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  const ids = { connection: useId(), scanRate: useId(), address: useId() }

  useEffect(() => {
    api.hmiProfile(projectId).then((loaded) => {
      setProfile(loaded)
      setConnectionId(loaded.connectionId ?? '')
      setScanRate(String(loaded.scanRateMs))
      setAddress(loaded.address)
    }).catch((reason: Error) => setErrors({ form: reason.message }))
  }, [projectId])

  const trimmed = connectionId.trim()
  const localErrors: Record<string, string> = {}
  if (trimmed && !guid.test(trimmed)) localErrors.connectionId = 'Use the connection ID from the HMI project, for example c1d2e3f4-a5b6-7890-cdef-111122223333.'
  if (!/^\d+$/.test(scanRate)) localErrors.scanRateMs = 'Enter a whole number of milliseconds.'
  const shown = { ...errors, ...localErrors }
  const valid = profile !== null && Object.keys(localErrors).length === 0

  const exportTags = async (scada = false) => {
    setBusy(true)
    setErrors({})
    setDone(false)
    try {
      await api.saveHmiProfile(projectId, trimmed || null, Number(scanRate), address)
      if (scada) download(await api.downloadScada(projectId), 'apolloiq-scada.json')
      else download(await api.downloadHmiTags(projectId), 'tags.json')
      setDone(true)
    } catch (reason) {
      if (reason instanceof ApiError && reason.field) setErrors({ [reason.field]: reason.message })
      else setErrors({ form: reason instanceof Error ? reason.message : String(reason) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      title={`Export for the HMI · ${projectName}`}
      onClose={onClose}
      wide
      footer={
        <>
          <button className="button" onClick={onClose}>{done ? 'Close' : 'Cancel'}</button>
          <button className="button" disabled={!valid || busy} onClick={() => void exportTags()}>Download tags.json</button>
          <button className="button button-primary" disabled={!valid || busy} onClick={() => void exportTags(true)}>Download SCADA project</button>
        </>
      }
    >
      <p className="muted">
        <strong>SCADA project</strong> (<code>apolloiq-scada.json</code>): {profile ? profile.tagCount : '…'} tags plus the CMs, Equipment modules, Units, alarms and the interlock lines per object (Switch on, Switch off, Trip). Import it in the HMI engineer view (Tags › Import Builder project). <code>tags.json</code> contains the tags only.
      </p>
      <div className="field">
        <label className="field-label" htmlFor={ids.connection}>Connection ID</label>
        <input id={ids.connection} className={shown.connectionId ? 'input input-error mono' : 'input mono'} value={connectionId}
          spellCheck={false} placeholder="(none)" onChange={(event) => { setConnectionId(event.target.value); setDone(false) }} />
        <span className="field-hint">{shown.connectionId ?? 'Every tag is assigned to this connection from connections.json of the HMI project.'}</span>
      </div>
      <div className="field">
        <label className="field-label" htmlFor={ids.scanRate}>Scan rate (ms)</label>
        <input id={ids.scanRate} className={shown.scanRateMs ? 'input input-error' : 'input'} value={scanRate} inputMode="numeric"
          onChange={(event) => { setScanRate(event.target.value); setDone(false) }} />
        <span className="field-hint">{shown.scanRateMs ?? 'Between 50 and 3 600 000 ms.'}</span>
      </div>
      <div className="field">
        <label className="field-label" htmlFor={ids.address}>Address</label>
        <select id={ids.address} className="input" value={address}
          onChange={(event) => { setAddress(event.target.value as HmiAddressMode); setDone(false) }}>
          <option value="Path">Tag path, for example PMS.GEN1.STS.state</option>
          <option value="SymbolKey">Symbol key, for example T_3F9A2C0B11D4</option>
        </select>
        {shown.address && <span className="field-hint field-hint-error">{shown.address}</span>}
      </div>
      {shown.form && <p className="form-error">{shown.form}</p>}
      {done && <p className="form-success">tags.json downloaded. The settings are saved with the project.</p>}
    </Modal>
  )
}
