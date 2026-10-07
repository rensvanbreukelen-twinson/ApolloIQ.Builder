import { useEffect, useState } from 'react'
import { commandInputsApi, type CommandInputConfig, type CommandInputsDto } from '../api/commandInputs'
import { CommandInputsEditor } from './CommandInputsEditor'
import { Modal } from './Modal'

type Props = {
  projectId: string
  controlModuleId: string
  title: string
  onClose: () => void
  onChanged: () => void
}

export function CommandInputsDialog({ projectId, controlModuleId, title, onClose, onChanged }: Props) {
  const [dto, setDto] = useState<CommandInputsDto | null>(null)
  const [config, setConfig] = useState<CommandInputConfig | null>(null)
  const [dirty, setDirty] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    commandInputsApi.get(projectId, controlModuleId).then((d) => { setDto(d); setConfig(d.config) }).catch((reason: Error) => setError(reason.message))
  }, [projectId, controlModuleId])

  const apply = async (action: () => Promise<CommandInputsDto>) => {
    try {
      const d = await action()
      setDto(d)
      setConfig(d.config)
      setDirty(false)
      setError(null)
      onChanged()
    } catch (reason) {
      setError((reason as Error).message)
    }
  }

  return (
    <Modal title={`Command inputs of ${title}`} onClose={onClose} extraWide
      footer={
        <>
          <button className="button" disabled={!dto?.defaults} onClick={() => void apply(() => commandInputsApi.reset(projectId, controlModuleId))}>Reset to blueprint defaults</button>
          <span className="spacer" />
          <button className="button" onClick={onClose}>{dirty ? 'Cancel' : 'Close'}</button>
          <button className="button button-primary" disabled={!dirty || !config} onClick={() => void apply(() => commandInputsApi.save(projectId, controlModuleId, config!))}>Save</button>
        </>
      }>
      {error && <div className="banner banner-error">{error}</div>}
      {dto?.problems.map((p) => <div key={p} className="banner banner-warning">{p}</div>)}
      {dto?.inUnit && <p className="muted">This CM is a member of a Unit, so it has a <b>UNIT</b> input that counts only while the Unit is in auto.</p>}
      {dto && config ? (
        <CommandInputsEditor value={config} onChange={(c) => { setConfig(c); setDirty(true) }} singleCommands={dto.singleCommands}
          hasPair={dto.hasPair} selectors={dto.selectors} />
      ) : !error && <p className="muted">Loading…</p>}
    </Modal>
  )
}
