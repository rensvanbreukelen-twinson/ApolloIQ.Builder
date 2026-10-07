import { useState } from 'react'
import type { NodeKind, TreeNode } from '../api/types'
import { checkName, checkText } from '../lib/names'
import { Modal } from './Modal'
import { NameField } from './NameField'
import { ParentSelect } from './ParentSelect'
import { useSubmit } from './useSubmit'

type Props = {
  title: string
  confirmLabel: string
  initialName?: string
  maxLength: number
  tree?: TreeNode[]
  allow?: NodeKind[]
  initialParentId?: string | null
  onSubmit: (name: string, parentId: string | null) => Promise<void>
  onClose: () => void
  freeText?: boolean
}

export function NameDialog({ title, confirmLabel, initialName = '', maxLength, tree, allow, initialParentId = null, onSubmit, onClose, freeText }: Props) {
  const [name, setName] = useState(initialName)
  const [parentId, setParentId] = useState<string | null>(initialParentId)
  const { errors, busy, submit, clearErrors } = useSubmit()
  const valid = (freeText ? checkText : checkName)(name, maxLength) === null

  const confirm = () => {
    if (valid && !busy) void submit(() => onSubmit(name, parentId))
  }

  return (
    <Modal
      title={title}
      onClose={onClose}
      footer={
        <>
          <button className="button" onClick={onClose}>Cancel</button>
          <button className="button button-primary" disabled={!valid || busy} onClick={confirm}>{confirmLabel}</button>
        </>
      }
    >
      <form onSubmit={(event) => { event.preventDefault(); confirm() }}>
        <NameField label="Name" value={name} maxLength={maxLength} serverError={errors.name} autoFocus freeText={freeText}
          onChange={(value) => { setName(value); clearErrors() }} />
        {tree && (
          <ParentSelect label="Folder" tree={tree} value={parentId} error={errors.parentId} allow={allow}
            onChange={(value) => { setParentId(value); clearErrors() }} />
        )}
        {errors.form && <p className="form-error">{errors.form}</p>}
      </form>
    </Modal>
  )
}
