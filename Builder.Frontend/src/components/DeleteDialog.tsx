import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { DeletionSummary, TreeNode } from '../api/types'
import { Modal } from './Modal'
import { useSubmit } from './useSubmit'

type Props = {
  projectId: string
  node: TreeNode
  onDeleted: () => void
  onClose: () => void
}

function plural(count: number, word: string) {
  return `${count} ${word}${count === 1 ? '' : 's'}`
}

export function DeleteDialog({ projectId, node, onDeleted, onClose }: Props) {
  const [summary, setSummary] = useState<DeletionSummary | null>(null)
  const { errors, busy, submit } = useSubmit()

  useEffect(() => {
    api.deletionSummary(projectId, node.id).then(setSummary).catch(() => setSummary(null))
  }, [projectId, node.id])

  const parts = summary
    ? [
        summary.folders > 0 ? plural(summary.folders, 'folder') : null,
        summary.controlModules > 0 ? plural(summary.controlModules, 'control module') : null,
        plural(summary.tags, 'tag'),
      ].filter(Boolean)
    : []

  return (
    <Modal
      title={`Delete ${node.path}`}
      onClose={onClose}
      footer={
        <>
          <button className="button" onClick={onClose}>Cancel</button>
          <button className="button button-danger" disabled={busy || !summary}
            onClick={() => void submit(async () => { await api.remove(projectId, node.id); onDeleted() })}>Delete</button>
        </>
      }
    >
      <p>{summary ? `This deletes ${parts.join(', ')}. This cannot be undone.` : 'Counting what will be deleted…'}</p>
      {errors.form && <p className="form-error">{errors.form}</p>}
    </Modal>
  )
}
