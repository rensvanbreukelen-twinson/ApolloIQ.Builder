import { useCallback, useEffect, useState } from 'react'
import { topologyApi, type Binding, type DeploymentItem, type Device, type DeviceRole, type Link, type LinkClass, type Topology } from '../api/topology'

type Props = { projectId: string; revision: number; onChanged: () => void }

const roles: { role: DeviceRole; text: string }[] = [
  { role: 'Plc', text: 'PLC (runs logic)' },
  { role: 'Scada', text: 'SCADA / HMI server' },
  { role: 'ThirdParty', text: 'Third-party device' },
]

export function TopologyPanel({ projectId, revision, onChanged }: Props) {
  const [topology, setTopology] = useState<Topology | null>(null)
  const [deployment, setDeployment] = useState<DeploymentItem[]>([])
  const [binding, setBinding] = useState<Binding | null>(null)
  const [dirty, setDirty] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [openInputs, setOpenInputs] = useState<string | null>(null)
  const [onlyCritical, setOnlyCritical] = useState(true)

  const reload = useCallback(async () => {
    const [t, d, b] = await Promise.all([topologyApi.topology(projectId), topologyApi.deployment(projectId), topologyApi.binding(projectId)])
    return { t, d, b }
  }, [projectId])

  useEffect(() => {
    let cancelled = false
    reload()
      .then(({ t, d, b }) => {
        if (cancelled) return
        setTopology(t)
        setDeployment(d)
        setBinding(b)
        setDirty(false)
      })
      .catch((reason: Error) => { if (!cancelled) setError(reason.message) })
    return () => { cancelled = true }
  }, [reload, revision])

  const refreshReport = async () => {
    const { d, b } = await reload()
    setDeployment(d)
    setBinding(b)
  }

  const act = async (action: () => Promise<unknown>) => {
    try {
      await action()
      setError(null)
      await refreshReport()
      onChanged()
    } catch (reason) {
      setError((reason as Error).message)
    }
  }

  if (!topology) return <section className="tag-list"><p className="empty">{error ?? 'Loading…'}</p></section>

  const devices = topology.devices
  const plcs = devices.filter((d) => d.role === 'Plc' && d.id)
  const updateDevice = (index: number, change: Partial<Device>) => {
    setTopology({ ...topology, devices: devices.map((d, i) => i === index ? { ...d, ...change } : d) })
    setDirty(true)
  }
  const updateLink = (index: number, change: Partial<Link>) => {
    setTopology({ ...topology, links: topology.links.map((l, i) => i === index ? { ...l, ...change } : l) })
    setDirty(true)
  }
  const deviceName = (reference: string) => devices.find((d) => d.id === reference || d.name === reference)?.name ?? reference

  const saveTopology = () => act(async () => {
    const links = topology.links.map((l) => ({ ...l, from: deviceName(l.from), to: deviceName(l.to) }))
    setTopology(await topologyApi.saveTopology(projectId, { devices, links }))
    setDirty(false)
  })

  return (
    <section className="tag-list topology">
      <div className="toolbar">
        <span className="toolbar-title">Topology and binding</span>
        {binding && (
          <span className={binding.errors > 0 ? 'sim-status scenario-failed' : 'sim-status sim-status-running'}>
            {binding.errors} errors · {binding.warnings} warnings
          </span>
        )}
        <span className="spacer" />
        <button className="button button-primary" disabled={!dirty} onClick={() => void saveTopology()}>Save devices and links</button>
      </div>
      {error && <div className="banner banner-error">{error}</div>}
      <div className="topology-body">
        <div className="topology-card">
          <div className="interlock-heading">Devices</div>
          <table className="grid wire-grid">
            <thead><tr><th>Name</th><th>Role</th><th>Description</th><th /></tr></thead>
            <tbody>
              {devices.map((device, index) => (
                <tr key={device.id ?? `new-${index}`}>
                  <td><input className="input mono" aria-label={`Name of device ${index}`} value={device.name} onChange={(e) => updateDevice(index, { name: e.target.value })} /></td>
                  <td>
                    <select className="input" aria-label={`Role of device ${index}`} value={device.role} onChange={(e) => updateDevice(index, { role: e.target.value as DeviceRole })}>
                      {roles.map((r) => <option key={r.role} value={r.role}>{r.text}</option>)}
                    </select>
                  </td>
                  <td><input className="input" aria-label={`Description of device ${index}`} value={device.description ?? ''} onChange={(e) => updateDevice(index, { description: e.target.value })} /></td>
                  <td><button className="button button-small button-danger" aria-label={`Remove device ${index}`}
                    onClick={() => { setTopology({ ...topology, devices: devices.filter((_, i) => i !== index), links: topology.links.filter((l) => l.from !== device.id && l.to !== device.id && l.from !== device.name && l.to !== device.name) }); setDirty(true) }}>✕</button></td>
                </tr>
              ))}
            </tbody>
          </table>
          <button className="button" onClick={() => { setTopology({ ...topology, devices: [...devices, { id: null, name: `PLC${devices.length + 1}`, role: 'Plc', description: '' }] }); setDirty(true) }}>Add device</button>
        </div>

        <div className="topology-card">
          <div className="interlock-heading">Links <span className="muted">· Control links may carry interlock and PLC-alarm inputs; monitoring links (for example through SCADA) may not (G-64).</span></div>
          <table className="grid wire-grid">
            <thead><tr><th>From</th><th>To</th><th>Protocol</th><th>Class</th><th /></tr></thead>
            <tbody>
              {topology.links.map((link, index) => (
                <tr key={link.id ?? `new-${index}`}>
                  {(['from', 'to'] as const).map((end) => (
                    <td key={end}>
                      <select className="input" aria-label={`${end} of link ${index}`} value={deviceName(link[end])} onChange={(e) => updateLink(index, { [end]: e.target.value })}>
                        {devices.map((d) => <option key={d.id ?? d.name} value={d.name}>{d.name}</option>)}
                      </select>
                    </td>
                  ))}
                  <td><input className="input" aria-label={`Protocol of link ${index}`} value={link.protocol} onChange={(e) => updateLink(index, { protocol: e.target.value })} /></td>
                  <td>
                    <select className="input" aria-label={`Class of link ${index}`} value={link.class} onChange={(e) => updateLink(index, { class: e.target.value as LinkClass })}>
                      <option value="Control">Control</option>
                      <option value="Monitoring">Monitoring</option>
                    </select>
                  </td>
                  <td><button className="button button-small button-danger" aria-label={`Remove link ${index}`}
                    onClick={() => { setTopology({ ...topology, links: topology.links.filter((_, i) => i !== index) }); setDirty(true) }}>✕</button></td>
                </tr>
              ))}
            </tbody>
          </table>
          <button className="button" disabled={devices.length < 2}
            onClick={() => { setTopology({ ...topology, links: [...topology.links, { id: null, from: devices[0].name, to: devices[1].name, protocol: 'OPC UA', class: 'Control' }] }); setDirty(true) }}>
            Add link
          </button>
        </div>

        <div className="topology-card">
          <div className="interlock-heading">Deployment <span className="muted">· where each CM runs, and where each FIN input comes from</span></div>
          {dirty && <p className="muted">Save the devices and links first.</p>}
          <table className="grid wire-grid">
            <thead><tr><th>Object</th><th>Runs on</th><th>Inputs</th></tr></thead>
            <tbody>
              {deployment.map((item) => (
                <tr key={item.id}>
                  <td><span className="mono">{item.path}</span> <span className="muted">{item.type}</span></td>
                  <td>
                    <select className="input" aria-label={`Device of ${item.path}`} value={item.deviceId ?? ''} disabled={dirty}
                      onChange={(e) => void act(() => topologyApi.setDevice(projectId, item.id, e.target.value || null))}>
                      <option value="">Not deployed</option>
                      {plcs.map((d) => <option key={d.id!} value={d.id!}>{d.name}</option>)}
                    </select>
                  </td>
                  <td>
                    {item.inputs.length === 0 ? <span className="muted">—</span> : (
                      <button className="button button-small" onClick={() => setOpenInputs(openInputs === item.id ? null : item.id)}>
                        {item.inputs.filter((i) => i.deviceId).length} of {item.inputs.length} with origin
                      </button>
                    )}
                    {openInputs === item.id && (
                      <table className="grid origin-grid">
                        <thead><tr><th>FIN tag</th><th>Origin device</th><th>Source</th><th>Address</th></tr></thead>
                        <tbody>
                          {item.inputs.map((input) => (
                            <OriginRow key={input.tagId} input={input} devices={devices} disabled={dirty}
                              onSave={(deviceId, source, address) => act(() => topologyApi.setOrigin(projectId, input.tagId, deviceId, source, address))} />
                          ))}
                        </tbody>
                      </table>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        {binding && (
          <div className="topology-card">
            <div className="interlock-heading">Validation</div>
            {binding.issues.length === 0 && <p className="muted">No issues.</p>}
            <ul className="binding-issues">
              {binding.issues.filter((i) => i.severity !== 'Info').concat(binding.issues.filter((i) => i.severity === 'Info')).map((issue, index) => (
                <li key={index} className={`binding-${issue.severity.toLowerCase()}`}>
                  <span className="binding-severity">{issue.severity}</span> {issue.message}
                </li>
              ))}
            </ul>
            <div className="interlock-heading">
              Access plan
              <label className="checkbox binding-filter">
                <input type="checkbox" checked={onlyCritical} onChange={(e) => setOnlyCritical(e.target.checked)} /> Only control inputs
              </label>
            </div>
            <table className="grid wire-grid">
              <thead><tr><th>Tag</th><th>Used by</th><th>On</th><th>Access</th><th>Path</th><th>Address</th></tr></thead>
              <tbody>
                {binding.accesses.filter((a) => !onlyCritical || a.critical).map((a, index) => (
                  <tr key={index} className={a.safetyViolation || a.kind === 'Unreachable' ? 'sim-bad' : undefined}>
                    <td className="mono">{a.tag}</td>
                    <td>{a.usedBy}</td>
                    <td>{a.consumer}</td>
                    <td>{a.kind}{a.safetyViolation ? ' · unsafe' : ''}</td>
                    <td>{a.path}</td>
                    <td className="mono">{a.address}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  )
}

type OriginRowProps = {
  input: DeploymentItem['inputs'][number]
  devices: Device[]
  disabled: boolean
  onSave: (deviceId: string | null, source: string, address: string) => Promise<void>
}

function OriginRow({ input, devices, disabled, onSave }: OriginRowProps) {
  const [deviceId, setDeviceId] = useState(input.deviceId ?? '')
  const [source, setSource] = useState(input.source ?? '')
  const [address, setAddress] = useState(input.address ?? '')
  const changed = deviceId !== (input.deviceId ?? '') || source !== (input.source ?? '') || address !== (input.address ?? '')
  return (
    <tr>
      <td className="mono" title={input.tagSource}>{input.tag.split('.').slice(-2).join('.')}</td>
      <td>
        <select className="input" aria-label={`Origin device of ${input.tag}`} value={deviceId} disabled={disabled} onChange={(e) => setDeviceId(e.target.value)}>
          <option value="">Local I/O of the CM's PLC</option>
          {devices.filter((d) => d.id).map((d) => <option key={d.id!} value={d.id!}>{d.name}</option>)}
        </select>
      </td>
      <td><input className="input" aria-label={`Source of ${input.tag}`} value={source} placeholder="Modbus TCP" disabled={disabled || !deviceId} onChange={(e) => setSource(e.target.value)} /></td>
      <td>
        <input className="input mono" aria-label={`Address of ${input.tag}`} value={address} placeholder="40021" disabled={disabled || !deviceId} onChange={(e) => setAddress(e.target.value)} />
        {changed && <button className="button button-small" onClick={() => void onSave(deviceId || null, source, address)}>Apply</button>}
      </td>
    </tr>
  )
}
