import { useState } from 'react'
import type { TreeNode } from '../api/types'

type Props = {
  nodes: TreeNode[]
  selectedId: string | null
  onSelect: (id: string | null) => void
}

export function ProjectTree({ nodes, selectedId, onSelect }: Props) {
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())

  const toggle = (id: string) =>
    setCollapsed((current) => {
      const next = new Set(current)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  const render = (node: TreeNode, depth: number) => {
    const isFolder = node.kind === 'folder'
    const isContainer = isFolder || node.kind === 'unit' || node.kind === 'equipmentModule'
    const open = !collapsed.has(node.id)
    return (
      <li key={node.id}>
        <div
          className={node.id === selectedId ? 'tree-row tree-row-selected' : 'tree-row'}
          style={{ paddingLeft: 8 + depth * 16 }}
          onClick={() => onSelect(node.id)}
          title={node.path}
        >
          {isContainer && node.children.length > 0 ? (
            <button className="tree-toggle" aria-label={open ? 'Collapse' : 'Expand'}
              onClick={(event) => { event.stopPropagation(); toggle(node.id) }}>
              {open ? '▾' : '▸'}
            </button>
          ) : (
            <span className="tree-toggle" />
          )}
          <span className={`tree-icon tree-icon-${node.kind === 'controlModule' ? 'cm' : node.kind}`}>
            {isFolder ? '▢' : node.kind === 'unit' ? '▣' : node.kind === 'equipmentModule' ? '▤' : '◆'}
          </span>
          <span className="tree-name">{node.name}</span>
          {!isFolder && <span className="tree-type">{node.typeName}</span>}
        </div>
        {isContainer && open && node.children.length > 0 && <ul>{node.children.map((child) => render(child, depth + 1))}</ul>}
      </li>
    )
  }

  if (nodes.length === 0) return <p className="empty">No folders or control modules yet.</p>

  return (
    <ul className="tree" onClick={(event) => { if (event.target === event.currentTarget) onSelect(null) }}>
      {nodes.map((node) => render(node, 0))}
    </ul>
  )
}
