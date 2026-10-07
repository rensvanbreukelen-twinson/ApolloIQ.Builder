import type { BpState, BpTransition } from '../api/blueprints'
import { stateKind, type StateCategory } from '../api/conventions'

type Props = {
  states: BpState[]
  transitions: BpTransition[]
  categories: StateCategory[]
  selected: string | null
  onSelect: (name: string) => void
}

const W = 128
const H = 44
const GX = 200
const GY = 96
const PAD = 24

type Box = { x: number; y: number; state: BpState }

type Edge = { from: string; to: string; label: string; dashed: boolean }

function border(from: Box, to: Box) {
  const cx = from.x + W / 2
  const cy = from.y + H / 2
  const dx = to.x + W / 2 - cx
  const dy = to.y + H / 2 - cy
  const scale = 1 / Math.max(Math.abs(dx) / (W / 2) || 1e-9, Math.abs(dy) / (H / 2) || 1e-9)
  return { x: cx + dx * scale, y: cy + dy * scale }
}

export function BlueprintDiagram({ states, transitions, categories, selected, onSelect }: Props) {
  const boxes = new Map<string, Box>()
  const columns = [...categories].sort((a, b) => a.column - b.column).map((c) => c.code)
  const used = columns.filter((c) => states.some((s) => s.category === c))
  used.forEach((category, col) => {
    states.filter((s) => s.category === category).forEach((state, row) => {
      boxes.set(state.name, { x: PAD + col * GX, y: PAD + 20 + row * GY, state })
    })
  })
  const edges: Edge[] = []
  for (const t of transitions) {
    const froms = t.from.includes('*') ? states.map((s) => s.name).filter((n) => n !== t.to) : t.from
    for (const from of froms) edges.push({ from, to: t.to, label: t.from.includes('*') ? `${t.name} (any)` : t.name, dashed: false })
  }
  for (const s of states) if (s.timeout?.goTo) edges.push({ from: s.name, to: s.timeout.goTo, label: '⏱ timeout', dashed: true })

  const rows = Math.max(1, ...used.map((c) => states.filter((s) => s.category === c).length))
  const width = PAD * 2 + Math.max(1, used.length) * GX - (GX - W)
  const height = PAD * 2 + 20 + rows * GY - (GY - H) + 40 + Math.max(1, used.length) * 22

  return (
    <svg className="bp-diagram" viewBox={`0 0 ${width} ${height}`} width="100%" style={{ maxWidth: width }} role="img" aria-label="State diagram">
      <defs>
        <marker id="bp-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M 0 0 L 10 5 L 0 10 z" fill="#5f6b7a" />
        </marker>
      </defs>
      {used.map((category, col) => (
        <text key={category} x={PAD + col * GX + W / 2} y={PAD} textAnchor="middle" className="bp-col-label">{category}</text>
      ))}
      {edges.map((edge, i) => {
        const a = boxes.get(edge.from)
        const b = boxes.get(edge.to)
        if (!a || !b) return null
        if (a === b) {
          const x = a.x + W - 20
          const y = a.y
          return (
            <g key={i}>
              <path d={`M ${x} ${y} C ${x + 10} ${y - 34}, ${x + 40} ${y - 10}, ${a.x + W} ${y + 14}`} className="bp-edge" markerEnd="url(#bp-arrow)" />
              <text x={x + 26} y={y - 18} className="bp-edge-label">{edge.label}</text>
            </g>
          )
        }
        const p = border(a, b)
        const q = border(b, a)
        const back = b.x < a.x || (b.x === a.x && b.y < a.y)
        const mx = (p.x + q.x) / 2
        const my = (p.y + q.y) / 2
        const len = Math.hypot(q.x - p.x, q.y - p.y) || 1
        const bend = back ? 24 + len * 0.22 : 10 + len * 0.06
        const cx = mx + ((q.y - p.y) / len) * bend
        const cy = my - ((q.x - p.x) / len) * bend
        return (
          <g key={i}>
            <path d={`M ${p.x} ${p.y} Q ${cx} ${cy} ${q.x} ${q.y}`} className={`bp-edge ${edge.dashed ? 'bp-edge-timeout' : ''}`} markerEnd="url(#bp-arrow)" />
            <text x={(p.x + 2 * cx + q.x) / 4} y={(p.y + 2 * cy + q.y) / 4 + (back ? 12 : -4)} textAnchor="middle" className="bp-edge-label">{edge.label}</text>
          </g>
        )
      })}
      {[...boxes.values()].map(({ x, y, state }) => (
        <g key={state.name} className={`bp-node bp-node-${stateKind(categories.find((c) => c.code === state.category))} ${selected === state.name ? 'bp-node-selected' : ''}`}
          onClick={() => onSelect(state.name)} role="button" aria-label={`State ${state.name}`}>
          <rect x={x} y={y} width={W} height={H} rx={8} />
          {state.initial && <circle cx={x - 8} cy={y + H / 2} r={4} className="bp-initial" />}
          <text x={x + W / 2} y={y + 18} textAnchor="middle" className="bp-node-name">{state.name}</text>
          <text x={x + W / 2} y={y + 34} textAnchor="middle" className="bp-node-code">{state.code}{state.timeout ? ' · ⏱' : ''}</text>
        </g>
      ))}
    </svg>
  )
}
