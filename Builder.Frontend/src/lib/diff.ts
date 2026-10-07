export type DiffLine = { kind: 'same' | 'added' | 'removed'; text: string }

/** A line diff (longest common subsequence): enough for the short YAML fragments of a change item. */
export function diffLines(before: string, after: string): DiffLine[] {
  const a = before ? before.replace(/\n$/, '').split('\n') : []
  const b = after ? after.replace(/\n$/, '').split('\n') : []
  const lengths = Array.from({ length: a.length + 1 }, () => new Array<number>(b.length + 1).fill(0))
  for (let i = a.length - 1; i >= 0; i--)
    for (let j = b.length - 1; j >= 0; j--)
      lengths[i][j] = a[i] === b[j] ? lengths[i + 1][j + 1] + 1 : Math.max(lengths[i + 1][j], lengths[i][j + 1])
  const result: DiffLine[] = []
  let i = 0
  let j = 0
  while (i < a.length && j < b.length) {
    if (a[i] === b[j]) {
      result.push({ kind: 'same', text: a[i] })
      i++
      j++
    } else if (lengths[i + 1][j] >= lengths[i][j + 1]) result.push({ kind: 'removed', text: a[i++] })
    else result.push({ kind: 'added', text: b[j++] })
  }
  while (i < a.length) result.push({ kind: 'removed', text: a[i++] })
  while (j < b.length) result.push({ kind: 'added', text: b[j++] })
  return result
}
