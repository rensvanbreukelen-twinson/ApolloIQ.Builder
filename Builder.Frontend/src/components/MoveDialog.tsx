import { useState } from 'react'
import type { NodeKind, TreeNode } from '../api/types'
import { descendantIds } from '../lib/tree'
import { Modal } from './Modal'
import { ParentSelect } from './ParentSelect'
import { useSubmit } from './useSubmit'

type Props = {
  node: TreeNode
  tree: TreeNode[]
  onSubmit: (parentId: string | null) => Promise<void>
  onClose: () => void
}

export function MoveDialog({ node, tree, onSubmit, onClose }: Props) {
  const [parentId, setParentId] = useState<string | null>(node.parentId)
  const { errors, busy, submit } = useSubmit()
  const exclude = node.children.length > 0 ? descendantIds(node) : undefined
  const allow: NodeKind[] = node.kind === 'controlModule' ? ['folder', 'unit', 'equipmentModule']
    : node.kind === 'equipmentModule' ? ['folder', 'unit'] : ['folder']

  return (
    <Modal
      title={`Move ${node.path}`}
      onClose={onClose}
      footer={
        <>
          <button className="button" onClick={onClose}>Cancel</button>
          <button className="button button-primary" disabled={busy || parentId === node.parentId}
            onClick={() => void submit(() => onSubmit(parentId))}>Move</button>
        </>
      }
    >
      <ParentSelect label="New place" tree={tree} value={parentId} onChange={setParentId} exclude={exclude} allow={allow}
        error={errors.parentId ?? errors.name} />
      {errors.form && <p className="form-error">{errors.form}</p>}
    </Modal>
  )
}
