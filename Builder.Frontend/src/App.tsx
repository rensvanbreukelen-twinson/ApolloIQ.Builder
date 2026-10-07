import { useCallback, useEffect, useState } from 'react'
import { api } from './api/client'
import type { CmType, Project, ProjectSummary, TreeNode } from './api/types'
import { AlarmsDialog } from './components/AlarmsDialog'
import { BlueprintPanel } from './components/BlueprintPanel'
import { CmWizard } from './components/CmWizard'
import { CommandInputsDialog } from './components/CommandInputsDialog'
import { ConfiguratorPanel } from './components/ConfiguratorPanel'
import { DeleteDialog } from './components/DeleteDialog'
import { ExportDialog } from './components/ExportDialog'
import { InterlocksDialog } from './components/InterlocksDialog'
import { MoveDialog } from './components/MoveDialog'
import { NameDialog } from './components/NameDialog'
import { ProjectTree } from './components/ProjectTree'
import { ScenarioPanel } from './components/ScenarioPanel'
import { SimulatorPanel } from './components/SimulatorPanel'
import { TagList } from './components/TagList'
import { TopologyPanel } from './components/TopologyPanel'
import { findNode } from './lib/tree'

type Dialog =
  | { kind: 'newProject' }
  | { kind: 'newFolder' }
  | { kind: 'newCm' }
  | { kind: 'interlocks'; node: TreeNode }
  | { kind: 'alarms'; node: TreeNode }
  | { kind: 'inputs'; node: TreeNode }
  | { kind: 'rename'; node: TreeNode }
  | { kind: 'move'; node: TreeNode }
  | { kind: 'delete'; node: TreeNode }
  | { kind: 'export' }
  | null

const projectKey = 'apolloiq.builder.project'

function rememberedProject(): string | null {
  try {
    return localStorage.getItem(projectKey)
  } catch {
    return null
  }
}

function rememberProject(id: string) {
  try {
    localStorage.setItem(projectKey, id)
  } catch {
    return
  }
}

export default function App() {
  const [projects, setProjects] = useState<ProjectSummary[]>([])
  const [project, setProject] = useState<Project | null>(null)
  const [tree, setTree] = useState<TreeNode[]>([])
  const [types, setTypes] = useState<CmType[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [dialog, setDialog] = useState<Dialog>(null)
  const [revision, setRevision] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [view, setView] = useState<'tags' | 'configurator' | 'simulator' | 'scenarios' | 'topology' | 'blueprints'>('tags')

  const openProject = useCallback(async (id: string) => {
    const opened = await api.project(id)
    setProject(opened)
    setTree(await api.tree(id))
    setSelectedId(null)
    rememberProject(id)
  }, [])

  useEffect(() => {
    Promise.all([api.projects(), api.types()])
      .then(async ([list, loadedTypes]) => {
        setProjects(list)
        setTypes(loadedTypes)
        const remembered = rememberedProject()
        const initial = list.find((p) => p.id === remembered) ?? list[0]
        if (initial) await openProject(initial.id)
      })
      .catch((reason: Error) => setError(`Backend not reachable: ${reason.message}`))
  }, [openProject])

  const refresh = useCallback(async () => {
    if (!project) return
    setTree(await api.tree(project.id))
    setRevision((r) => r + 1)
  }, [project])

  const selected = findNode(tree, selectedId)
  const targetFolderId = selected ? (selected.kind === 'folder' || selected.kind === 'unit' || selected.kind === 'equipmentModule' ? selected.id : selected.parentId) : null
  const close = () => setDialog(null)

  return (
    <div className="app">
      <header className="topbar">
        <span className="brand">ApolloIQ Builder</span>
        <select className="input" aria-label="Project" value={project?.id ?? ''}
          onChange={(event) => event.target.value && void openProject(event.target.value)}>
          {!project && <option value="">No project</option>}
          {projects.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
        <button className="button" onClick={() => setDialog({ kind: 'newProject' })}>New project</button>
        {(
          <div className="tabs" role="tablist">
            <button role="tab" aria-selected={view === 'blueprints'} className={`tab ${view === 'blueprints' ? 'tab-active' : ''}`} onClick={() => setView('blueprints')}>Blueprints</button>
            {project && <>
            <button role="tab" aria-selected={view === 'tags'} className={`tab ${view === 'tags' ? 'tab-active' : ''}`} onClick={() => setView('tags')}>Tags</button>
            <button role="tab" aria-selected={view === 'configurator'} className={`tab ${view === 'configurator' ? 'tab-active' : ''}`} onClick={() => { void api.types().then(setTypes); setView('configurator') }}>Configurator</button>
            <button role="tab" aria-selected={view === 'simulator'} className={`tab ${view === 'simulator' ? 'tab-active' : ''}`} onClick={() => setView('simulator')}>Simulator</button>
            <button role="tab" aria-selected={view === 'topology'} className={`tab ${view === 'topology' ? 'tab-active' : ''}`} onClick={() => setView('topology')}>Topology</button>
            <button role="tab" aria-selected={view === 'scenarios'} className={`tab ${view === 'scenarios' ? 'tab-active' : ''}`} onClick={() => setView('scenarios')}>Scenarios</button>
            </>}
          </div>
        )}
        <span className="spacer" />
        {project && <button className="button" onClick={() => setDialog({ kind: 'export' })}>Export to SCADA…</button>}
      </header>

      {error && <div className="banner banner-error">{error}</div>}
      {view === 'blueprints' ? (
        <main className="workspace bp-workspace"><BlueprintPanel /></main>
      ) : project ? (
        <main className="workspace">
          <aside className="sidebar">
            <div className="sidebar-actions">
              <button className="button" onClick={() => setDialog({ kind: 'newFolder' })}>New folder</button>
              <button className="button button-primary" onClick={() => { void api.types().then(setTypes); setDialog({ kind: 'newCm' }) }}>New control module</button>
            </div>
            <ProjectTree nodes={tree} selectedId={selectedId} onSelect={setSelectedId} />
            {selected && (
              <div className="selection">
                <div className="selection-title">
                  <span className="mono">{selected.path}</span>
                  {selected.typeName && <span className="muted"> · {selected.typeName} v{selected.typeVersion}</span>}
                </div>
                <div className="selection-actions">
                  {selected.kind === 'controlModule' && (
                    <>
                      <button className="button" onClick={() => setDialog({ kind: 'interlocks', node: selected })}>Interlocks</button>
                      <button className="button" onClick={() => setDialog({ kind: 'alarms', node: selected })}>Alarms</button>
                      <button className="button" onClick={() => setDialog({ kind: 'inputs', node: selected })}>Command inputs</button>
                    </>
                  )}
                  {(selected.kind === 'unit' || selected.kind === 'equipmentModule') && (
                    <>
                      <button className="button" onClick={() => setDialog({ kind: 'interlocks', node: selected })}>Interlocks</button>
                      <button className="button" onClick={() => setDialog({ kind: 'inputs', node: selected })}>Command inputs</button>
                    </>
                  )}
                  <button className="button" onClick={() => setDialog({ kind: 'rename', node: selected })}>Rename</button>
                  <button className="button" onClick={() => setDialog({ kind: 'move', node: selected })}>Move</button>
                  <button className="button button-danger" onClick={() => setDialog({ kind: 'delete', node: selected })}>Delete</button>
                </div>
              </div>
            )}
          </aside>
          {view === 'tags' && <TagList projectId={project.id} scope={selected} revision={revision} />}
          {view === 'configurator' && (
            <ConfiguratorPanel key={project.id} projectId={project.id} folderId={targetFolderId} types={types} revision={revision}
              onChanged={() => void refresh()} onSelect={setSelectedId} />
          )}
          {view === 'simulator' && <SimulatorPanel key={project.id} projectId={project.id} scope={selected} revision={revision} />}
          {view === 'scenarios' && <ScenarioPanel />}
          {view === 'topology' && <TopologyPanel key={project.id} projectId={project.id} revision={revision} onChanged={() => void refresh()} />}
        </main>
      ) : (
        !error && (
          <main className="welcome">
            <p>Create a project to start.</p>
            <button className="button button-primary" onClick={() => setDialog({ kind: 'newProject' })}>New project</button>
          </main>
        )
      )}

      {dialog?.kind === 'newProject' && (
        <NameDialog title="New project" confirmLabel="Create" maxLength={64} freeText onClose={close}
          onSubmit={async (name) => {
            const created = await api.createProject(name)
            setProjects(await api.projects())
            await openProject(created.id)
            close()
          }} />
      )}
      {project && dialog?.kind === 'newFolder' && (
        <NameDialog title="New folder" confirmLabel="Create" maxLength={project.maxNameLength} tree={tree}
          initialParentId={selected?.kind === 'folder' ? selected.id : null} onClose={close}
          onSubmit={async (name, parentId) => {
            const node = await api.createFolder(project.id, name, parentId)
            await refresh()
            setSelectedId(node.id)
            close()
          }} />
      )}
      {project && dialog?.kind === 'newCm' && (
        <CmWizard types={types} tree={tree} maxLength={project.maxNameLength} initialParentId={targetFolderId} onClose={close}
          onSubmit={async (blueprintId, name, parentId) => {
            const node = await api.createControlModule(project.id, blueprintId, name, parentId)
            await refresh()
            setSelectedId(node.id)
            close()
          }} />
      )}
      {project && dialog?.kind === 'interlocks' && (
        <InterlocksDialog projectId={project.id} objectId={dialog.node.id} title={dialog.node.path} onClose={close} onChanged={() => void refresh()} />
      )}
      {project && dialog?.kind === 'inputs' && (
        <CommandInputsDialog projectId={project.id} controlModuleId={dialog.node.id} title={dialog.node.path} onClose={close} onChanged={() => void refresh()} />
      )}
      {project && dialog?.kind === 'alarms' && (
        <AlarmsDialog projectId={project.id} node={dialog.node} onClose={close} />
      )}
      {project && dialog?.kind === 'rename' && (
        <NameDialog title={`Rename ${dialog.node.path}`} confirmLabel="Rename" initialName={dialog.node.name}
          maxLength={project.maxNameLength} onClose={close}
          onSubmit={async (name) => {
            await api.rename(project.id, dialog.node.id, name)
            await refresh()
            close()
          }} />
      )}
      {project && dialog?.kind === 'move' && (
        <MoveDialog node={dialog.node} tree={tree} onClose={close}
          onSubmit={async (parentId) => {
            await api.move(project.id, dialog.node.id, parentId)
            await refresh()
            close()
          }} />
      )}
      {project && dialog?.kind === 'export' && (
        <ExportDialog projectId={project.id} projectName={project.name} onClose={close} />
      )}
      {project && dialog?.kind === 'delete' && (
        <DeleteDialog projectId={project.id} node={dialog.node} onClose={close}
          onDeleted={async () => {
            setSelectedId(null)
            await refresh()
            close()
          }} />
      )}
    </div>
  )
}
