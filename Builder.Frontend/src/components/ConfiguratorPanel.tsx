import { useCallback, useEffect, useRef, useState, type CSSProperties, type PointerEvent as ReactPointerEvent } from 'react'
import { blueprintApi, type BpSummary } from '../api/blueprints'
import { api } from '../api/client'
import { configuratorApi, type ConfigCm, type ConfigUnit, type ConfigView, type Position } from '../api/configurator'
import type { CmType, InterlockSummary } from '../api/types'
import { CommandInputsDialog } from './CommandInputsDialog'
import { InterlocksDialog } from './InterlocksDialog'

type Props = {
  projectId: string
  folderId: string | null
  types: CmType[]
  revision: number
  onChanged: () => void
  onSelect: (id: string) => void
}

type Drag = { kind: 'cm' | 'unit'; id: string; dx: number; dy: number; x: number; y: number; moved: boolean }

const cmWidth = 230
const unitGap = 20

function autoPositions(view: ConfigView): Map<string, Position> {
  const result = new Map<string, Position>()
  let x = 20
  for (const unit of view.units.filter((u) => !u.unitId || !view.units.some((p) => p.id === u.unitId))) {
    if (!unit.position) result.set(unit.id, { x, y: 20 })
    x += Math.max(2, unit.roles.length) * (cmWidth + unitGap) + 40
  }
  const top = view.units.length > 0 ? 360 : 20
  view.controlModules.filter((c) => !c.unitId && !c.position).forEach((cm, i) => {
    result.set(cm.id, { x: 20 + (i % 4) * (cmWidth + 30), y: top + Math.floor(i / 4) * 200 })
  })
  return result
}

export function ConfiguratorPanel({ projectId, folderId, types, revision, onChanged, onSelect }: Props) {
  const [view, setView] = useState<ConfigView | null>(null)
  const [unitBlueprints, setUnitBlueprints] = useState<BpSummary[]>([])
  const [error, setError] = useState<string | null>(null)
  const [drag, setDrag] = useState<Drag | null>(null)
  const [interlocksFor, setInterlocksFor] = useState<{ id: string; path: string } | null>(null)
  const [inputsFor, setInputsFor] = useState<ConfigCm | null>(null)
  const [adding, setAdding] = useState<{ kind: 'cm' | 'unit'; blueprint: string; name: string } | null>(null)
  const canvas = useRef<HTMLDivElement>(null)

  const load = useCallback(() => configuratorApi.view(projectId, folderId).then((v) => { setView(v); setError(null) })
    .catch((reason: Error) => setError(reason.message)), [projectId, folderId])

  useEffect(() => { void load() }, [load, revision])
  useEffect(() => { blueprintApi.list().then((l) => setUnitBlueprints(l.filter((b) => (b.kind === 'Unit' || b.kind === 'EM') && b.errors === 0))).catch(() => undefined) }, [])

  const run = async (action: () => Promise<unknown>) => {
    try {
      await action()
      await load()
      onChanged()
    } catch (reason) {
      setError((reason as Error).message)
    }
  }

  const toCanvas = (clientX: number, clientY: number) => {
    const rect = canvas.current!.getBoundingClientRect()
    return { x: clientX - rect.left + canvas.current!.scrollLeft, y: clientY - rect.top + canvas.current!.scrollTop }
  }

  const startDrag = (e: ReactPointerEvent, kind: 'cm' | 'unit', id: string) => {
    if (e.button !== 0) return
    const block = (e.currentTarget as HTMLElement).closest('[data-block]') as HTMLElement
    const rect = block.getBoundingClientRect()
    const at = toCanvas(rect.left, rect.top)
    e.preventDefault()
    setDrag({ kind, id, dx: e.clientX - rect.left, dy: e.clientY - rect.top, x: at.x, y: at.y, moved: false })
  }

  useEffect(() => {
    if (!drag) return
    const move = (e: PointerEvent) => {
      const p = toCanvas(e.clientX, e.clientY)
      setDrag((d) => d && { ...d, x: Math.max(0, p.x - d.dx), y: Math.max(0, p.y - d.dy), moved: true })
    }
    const up = (e: PointerEvent) => {
      const current = drag
      setDrag(null)
      if (!current.moved) {
        if (current.kind === 'cm') onSelect(current.id)
        return
      }
      const p = toCanvas(e.clientX, e.clientY)
      const x = Math.max(0, p.x - current.dx)
      const y = Math.max(0, p.y - current.dy)
      const cm = current.kind === 'cm' ? view?.controlModules.find((c) => c.id === current.id) : view?.units.find((u) => u.id === current.id)
      const target = document.elementsFromPoint(e.clientX, e.clientY)
        .map((el) => (el as HTMLElement).closest?.('[data-drop-role]') as HTMLElement | null).find((el) => el)
      if (target) {
        const unitId = target.dataset.unit!
        const role = target.dataset.dropRole!
        if (unitId === current.id) return
        if (cm && (cm.unitId !== unitId || cm.role !== role))
          void run(() => configuratorApi.setMember(projectId, unitId, role, current.id))
        return
      }
      void run(async () => {
        if (cm?.unitId && cm.role) await configuratorApi.setMember(projectId, cm.unitId, cm.role, null)
        await configuratorApi.layout(projectId, [{ id: current.id, x, y }])
      })
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
    return () => { window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up) }
  })

  if (!view) return <section className="tag-list"><p className="empty">{error ?? 'Loading…'}</p></section>

  const auto = autoPositions(view)
  const place = (id: string, saved: Position | null) => drag?.id === id ? { x: drag.x, y: drag.y } : saved ?? auto.get(id) ?? { x: 20, y: 20 }
  const free = view.controlModules.filter((c) => !c.unitId || !view.units.some((u) => u.id === c.unitId))
  const cmById = new Map(view.controlModules.map((c) => [c.id, c]))
  const unitById = new Map(view.units.map((u) => [u.id, u]))
  const topUnits = view.units.filter((u) => !u.unitId || !unitById.has(u.unitId))
  const extent = [...topUnits.map((u) => place(u.id, u.position)), ...free.map((c) => place(c.id, c.position))]
  const width = Math.max(1200, ...extent.map((p) => p.x + 900))
  const height = Math.max(700, ...extent.map((p) => p.y + 500))

  const interlockSection = (owner: { id: string; name: string; path: string; interlocks?: InterlockSummary | null }) => {
    const summary = owner.interlocks
    if (!summary || (!summary.hasInterlocks && summary.defined === 0)) return null
    const acting = summary.switchOn + summary.switchOff + summary.trips
    return (
      <div className="cfg-slots">
        <button className={`cfg-slot ${acting || summary.defined ? 'cfg-slot-filled' : ''}`} onPointerDown={(e) => e.stopPropagation()}
          aria-label={`Interlocks of ${owner.name}`}
          title={`Acting on ${owner.name}: ${summary.switchOn} Switch on, ${summary.switchOff} Switch off, ${summary.trips} Trip · defined here: ${summary.defined}`}
          onClick={() => setInterlocksFor({ id: owner.id, path: owner.path })}>
          <span className="cfg-slot-dot">{acting || ''}</span>
          Interlocks{summary.defined ? ` · ${summary.defined} defined here` : ''}
        </button>
        {summary.trips > 0 && <span className="cfg-slot cfg-slot-warning" title="Trips acting on this object">{summary.trips} trip{summary.trips === 1 ? '' : 's'}</span>}
      </div>
    )
  }

  const cmBlock = (cm: ConfigCm, style?: CSSProperties) => (
    <div key={cm.id} data-block className={`cfg-cm ${drag?.id === cm.id ? 'cfg-dragging' : ''}`} style={style}>
      <div className="cfg-cm-head" onPointerDown={(e) => startDrag(e, 'cm', cm.id)} title="Drag to move; drop on a Unit role to add it; click to select">
        <span className="cfg-cm-name mono">{cm.name}</span>
        {cm.role && <span className="cfg-role-tag">{cm.role}</span>}
        <button className="cfg-inputs" onPointerDown={(e) => e.stopPropagation()} onClick={() => setInputsFor(cm)}
          aria-label={`Command inputs of ${cm.name}`} title={cm.inputs.length ? `Command inputs: ${cm.inputs.join(', ')}` : 'No command inputs'}>
          ⇥ {cm.inputs.length}
        </button>
      </div>
      <div className="cfg-cm-type mono">{cm.blueprint}</div>
      {cm.description && <div className="cfg-cm-desc">{cm.description}</div>}
      {interlockSection(cm)}
    </div>
  )

  const unitBlock = (unit: ConfigUnit, nested = false) => {
    const p = place(unit.id, unit.position)
    return (
      <div key={unit.id} data-block className={`cfg-unit ${unit.equipmentModule ? 'cfg-em' : ''} ${nested ? 'cfg-nested' : ''} ${drag?.id === unit.id ? 'cfg-dragging' : ''}`}
        style={nested ? undefined : { left: p.x, top: p.y }}>
        <div className="cfg-unit-head" onPointerDown={(e) => startDrag(e, 'unit', unit.id)}
          title={unit.equipmentModule ? 'Drag to move; drop on a Unit role to add it' : 'Drag to move'}>
          <span className="cfg-cm-name mono">{unit.name}</span>
          <span className="cfg-cm-type mono">{unit.equipmentModule ? 'Equipment module' : 'Unit'} · {unit.blueprint}</span>
        </div>
        {unit.description && <div className="cfg-cm-desc">{unit.description}</div>}
        {interlockSection(unit)}
        {unit.problem && <div className="banner banner-warning">{unit.problem}</div>}
        <div className="cfg-roles">
          {unit.roles.map((role) => {
            const member = role.controlModuleId ? cmById.get(role.controlModuleId) : undefined
            const memberUnit = role.controlModuleId ? unitById.get(role.controlModuleId) : undefined
            return (
              <div key={role.role} className="cfg-role" data-drop-role={role.role} data-unit={unit.id}>
                <div className="cfg-role-head">
                  <span className="mono">{role.role}</span> <span className="muted">{role.blueprint}</span>
                  {role.controlModuleId && (
                    <button className="cfg-x" aria-label={`Remove ${member?.name ?? 'member'} from ${role.role}`}
                      onClick={() => void run(() => configuratorApi.setMember(projectId, unit.id, role.role, null))}>✕</button>
                  )}
                </div>
                {member ? cmBlock(member) : memberUnit ? unitBlock(memberUnit, true) : role.controlModuleId
                  ? <div className="cfg-role-empty">Member in another folder</div>
                  : <div className="cfg-role-empty">Drop a {role.blueprint} here</div>}
              </div>
            )
          })}
          {unit.roles.length === 0 && <p className="muted">This blueprint has no roles.</p>}
        </div>
      </div>
    )
  }

  const addChoices = adding?.kind === 'unit' ? unitBlueprints.map((b) => b.name) : types.map((t) => t.name)

  return (
    <section className="tag-list configurator">
      <div className="toolbar">
        <span className="toolbar-title">Configurator</span>
        <span className="mono muted">{view.folderPath || 'Project root'}</span>
        {view.folders.length > 0 && <span className="muted">· select a folder in the tree to open it ({view.folders.map((f) => f.name).join(', ')})</span>}
        <span className="spacer" />
        {adding ? (
          <form className="cfg-add" onSubmit={(e) => {
            e.preventDefault()
            const { kind, blueprint, name } = adding
            void run(async () => {
              if (kind === 'cm') await api.createControlModule(projectId, blueprint, name.trim(), folderId, [])
              else await configuratorApi.createUnit(projectId, name.trim(), folderId, blueprint)
            }).then(() => setAdding(null))
          }}>
            <select className="input" aria-label="Blueprint" value={adding.blueprint} onChange={(e) => setAdding({ ...adding, blueprint: e.target.value })}>
              <option value="">— {adding.kind === 'unit' ? 'Unit or Equipment module' : 'CM'} blueprint —</option>
              {addChoices.map((n) => <option key={n} value={n}>{n}{unitBlueprints.find((b) => b.name === n)?.kind === 'EM' ? ' (EM)' : ''}</option>)}
            </select>
            <input className="input mono" aria-label="Instance name" placeholder="Instance name" autoFocus value={adding.name}
              onChange={(e) => setAdding({ ...adding, name: e.target.value })} />
            <button className="button button-primary" type="submit" disabled={!adding.blueprint || !adding.name.trim()}>Add</button>
            <button className="button" type="button" onClick={() => setAdding(null)}>Cancel</button>
          </form>
        ) : (
          <>
            <button className="button button-primary" onClick={() => setAdding({ kind: 'cm', blueprint: '', name: '' })}>Add CM</button>
            <button className="button" disabled={unitBlueprints.length === 0} title={unitBlueprints.length ? '' : 'No Unit or Equipment module blueprints without errors'}
              onClick={() => setAdding({ kind: 'unit', blueprint: '', name: '' })}>Add Unit / EM</button>
          </>
        )}
      </div>
      {interlocksFor && (
        <InterlocksDialog projectId={projectId} objectId={interlocksFor.id} title={interlocksFor.path} onClose={() => setInterlocksFor(null)}
          onChanged={() => { void load(); onChanged() }} />
      )}
      {inputsFor && (
        <CommandInputsDialog projectId={projectId} controlModuleId={inputsFor.id} title={inputsFor.path} onClose={() => setInputsFor(null)}
          onChanged={() => { void load(); onChanged() }} />
      )}
      {error && <div className="banner banner-error">{error} <button className="button button-small" onClick={() => setError(null)}>Dismiss</button></div>}
      <div className="cfg-canvas-wrap" ref={canvas}>
        <div className="cfg-canvas" style={{ width, height }}>
          {topUnits.map((u) => unitBlock(u))}
          {free.map((cm) => {
            const p = place(cm.id, cm.position)
            return cmBlock(cm, { position: 'absolute', left: p.x, top: p.y })
          })}
          {drag && (drag.kind === 'cm' ? cmById.get(drag.id)?.unitId : unitById.get(drag.id)?.unitId) && (() => {
            const cm = drag.kind === 'cm' ? cmById.get(drag.id)! : unitById.get(drag.id)!
            return <div className="cfg-ghost" style={{ left: drag.x, top: drag.y }}>{cm.name}</div>
          })()}
          {view.units.length === 0 && view.controlModules.length === 0 && (
            <p className="empty cfg-empty">Nothing in this folder yet. Select a folder in the tree, then use Add CM or Add Unit.</p>
          )}
        </div>
      </div>
    </section>
  )
}
