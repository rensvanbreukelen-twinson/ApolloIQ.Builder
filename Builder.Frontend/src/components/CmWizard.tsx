import { useState } from 'react'
import type { CmType, TreeNode } from '../api/types'
import { checkName } from '../lib/names'
import { Modal } from './Modal'
import { NameField } from './NameField'
import { ParentSelect } from './ParentSelect'
import { useSubmit } from './useSubmit'

type Props = {
  types: CmType[]
  tree: TreeNode[]
  maxLength: number
  initialParentId: string | null
  onSubmit: (type: string, name: string, parentId: string | null, optionalTags: string[]) => Promise<void>
  onClose: () => void
}

export function CmWizard({ types, tree, maxLength, initialParentId, onSubmit, onClose }: Props) {
  const [type, setType] = useState<CmType | null>(null)
  const [name, setName] = useState('')
  const [parentId, setParentId] = useState<string | null>(initialParentId)
  const [optional, setOptional] = useState<Set<string>>(new Set())
  const { errors, busy, submit, clearErrors } = useSubmit()
  const valid = type !== null && checkName(name, maxLength) === null

  const toggle = (key: string) =>
    setOptional((current) => {
      const next = new Set(current)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })

  const create = () => {
    if (valid && !busy) void submit(() => onSubmit(type.name, name, parentId, [...optional]))
  }

  if (type === null) {
    return (
      <Modal title="New control module · 1 of 2: type" onClose={onClose} wide
        footer={<button className="button" onClick={onClose}>Cancel</button>}>
        {types.length === 0 && <p>No CM types are loaded. Check the library folder.</p>}
        <ul className="type-list">
          {types.map((t) => (
            <li key={t.name}>
              <button className="type-card" onClick={() => setType(t)}>
                <span className="type-name">{t.name}</span>
                <span className="type-version">v{t.version}</span>
                <span className="type-description">{t.description}</span>
                <span className="type-meta">
                  {t.tagCount} tags{t.optionalTags.length > 0 ? ` + ${t.optionalTags.length} optional` : ''}
                </span>
              </button>
            </li>
          ))}
        </ul>
      </Modal>
    )
  }

  return (
    <Modal
      title={`New control module · 2 of 2: ${type.name}`}
      onClose={onClose}
      wide
      footer={
        <>
          <button className="button" onClick={() => setType(null)}>Back</button>
          <span className="spacer" />
          <button className="button" onClick={onClose}>Cancel</button>
          <button className="button button-primary" disabled={!valid || busy} onClick={create}>Create</button>
        </>
      }
    >
      <form onSubmit={(event) => { event.preventDefault(); create() }}>
        <NameField label="Name" value={name} maxLength={maxLength} serverError={errors.name} autoFocus
          onChange={(value) => { setName(value); clearErrors() }} />
        <ParentSelect label="Place" tree={tree} value={parentId} error={errors.parentId} allow={['folder', 'unit', 'equipmentModule']}
          onChange={(value) => { setParentId(value); clearErrors() }} />
        {type.optionalTags.length > 0 && (
          <fieldset className="field">
            <legend className="field-label">Optional tags</legend>
            <div className="checkbox-list">
              {type.optionalTags.map((tag) => (
                <label key={tag.key} className="checkbox">
                  <input type="checkbox" checked={optional.has(tag.key)} onChange={() => toggle(tag.key)} />
                  <code>{tag.key}</code>
                  <span className="muted">{tag.description}</span>
                </label>
              ))}
            </div>
          </fieldset>
        )}
        <p className="muted">
          Creates {type.tagCount + type.optionalTags.filter((t) => optional.has(t.key)).reduce((sum, t) => sum + t.addsTags, 0)} tags
          {name && !checkName(name, maxLength) ? ` under ${[parentPath(tree, parentId), name].filter(Boolean).join('.')}` : ''}.
        </p>
        {errors.form && <p className="form-error">{errors.form}</p>}
      </form>
    </Modal>
  )
}

function parentPath(tree: TreeNode[], parentId: string | null): string {
  const stack = [...tree]
  while (stack.length > 0) {
    const node = stack.pop()!
    if (node.id === parentId) return node.path
    stack.push(...node.children)
  }
  return ''
}
