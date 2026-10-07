import { useId } from 'react'
import type { NodeKind, TreeNode } from '../api/types'
import { flatten } from '../lib/tree'

type Props = {
  label: string
  tree: TreeNode[]
  value: string | null
  onChange: (value: string | null) => void
  exclude?: Set<string>
  error?: string | null
  allow?: NodeKind[]
}

const suffix: Partial<Record<NodeKind, string>> = { unit: ' (Unit)', equipmentModule: ' (Equipment module)' }

export function ParentSelect({ label, tree, value, onChange, exclude, error, allow = ['folder'] }: Props) {
  const folders = flatten(tree).filter((node) => allow.includes(node.kind) && !exclude?.has(node.id))
  const id = useId()
  return (
    <div className="field">
      <label className="field-label" htmlFor={id}>{label}</label>
      <select
        id={id}
        className={error ? 'input input-error' : 'input'}
        value={value ?? ''}
        onChange={(event) => onChange(event.target.value || null)}
      >
        <option value="">(project root)</option>
        {folders.map((folder) => (
          <option key={folder.id} value={folder.id}>
            {folder.path}{suffix[folder.kind] ?? ''}
          </option>
        ))}
      </select>
      {error && <span className="field-hint field-hint-error">{error}</span>}
    </div>
  )
}
