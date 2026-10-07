// YAML in and out for the design format: the Builder API speaks JSON with the same structure.
import { Document, isMap, isSeq, parse, visit } from 'yaml'

const flowLimit = 120

const scalarsOnly = (node) => node.items.every((item) => !isMap(isMap(node) ? item.value : item) && !isSeq(isMap(node) ? item.value : item))

/**
 * A short collection is written on one line (flow style): `{ type: Bool, source: LocalIO }`, `[Base, Switchable]`,
 * `{ from: [Ready], to: Running, guard: ... }`. Maps inside it are not; short lists of scalars are.
 */
function flowWhenShort(_, node, path) {
  if (path.length < 3) return
  const items = node.items.map((item) => (isMap(node) ? item.value : item))
  if (items.some((item) => isMap(item) || (isSeq(item) && !scalarsOnly(item)))) return
  if (JSON.stringify(node.toJSON()).length <= flowLimit) node.flow = true
}

/** Drops null and undefined values (the API writes some optional fields as null). */
export function withoutNulls(value) {
  if (Array.isArray(value)) return value.map(withoutNulls)
  if (value && typeof value === 'object')
    return Object.fromEntries(Object.entries(value).filter(([, v]) => v !== null && v !== undefined).map(([k, v]) => [k, withoutNulls(v)]))
  return value
}

export function toYaml(value) {
  const document = new Document(withoutNulls(value))
  visit(document, { Map: flowWhenShort, Seq: flowWhenShort })
  return document.toString({ lineWidth: 160, flowCollectionPadding: true })
}

/** Parses YAML (or JSON) text; a syntax error becomes a readable message. */
export function fromYaml(text, what = 'design') {
  try {
    const value = parse(text)
    if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new Error('the top level must be a map (schema, devices, blueprints, objects, questions)')
    return value
  } catch (error) {
    throw new Error(`The ${what} YAML does not parse: ${error.message}`)
  }
}
