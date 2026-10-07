import type { TreeNode } from '../api/types'

export function flatten(nodes: TreeNode[]): TreeNode[] {
  return nodes.flatMap((node) => [node, ...flatten(node.children)])
}

export function findNode(nodes: TreeNode[], id: string | null): TreeNode | null {
  if (!id) return null
  return flatten(nodes).find((node) => node.id === id) ?? null
}

export function descendantIds(node: TreeNode): Set<string> {
  return new Set(flatten([node]).map((n) => n.id))
}
