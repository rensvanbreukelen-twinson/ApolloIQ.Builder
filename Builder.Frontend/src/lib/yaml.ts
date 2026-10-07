import { Document, isMap, isSeq, parse, visit, type visitorFn } from 'yaml'

const flowLimit = 120

type Collection = { items: unknown[]; flow?: boolean; toJSON: () => unknown }

const valueOf = (node: Collection, item: unknown) => (isMap(node) ? (item as { value: unknown }).value : item)

const scalarsOnly = (node: Collection) => node.items.every((item) => !isMap(valueOf(node, item)) && !isSeq(valueOf(node, item)))

/** A short collection is written on one line: `{ type: Bool, source: LocalIO }`, `[Base, Switchable]`. */
const flowWhenShort: visitorFn<unknown> = (_, node, path) => {
  if (path.length < 3) return
  const collection = node as unknown as Collection
  const items = collection.items.map((item) => valueOf(collection, item))
  if (items.some((item) => isMap(item) || (isSeq(item) && !scalarsOnly(item as unknown as Collection)))) return
  if (JSON.stringify(collection.toJSON()).length <= flowLimit) collection.flow = true
}

function withoutNulls(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(withoutNulls)
  if (value && typeof value === 'object')
    return Object.fromEntries(Object.entries(value).filter(([, v]) => v !== null && v !== undefined).map(([k, v]) => [k, withoutNulls(v)]))
  return value
}

/** A design fragment (JSON from the API) as the YAML the engineer and the agent read. */
export function toYaml(value: unknown): string {
  if (value === null || value === undefined) return ''
  const document = new Document(withoutNulls(value))
  visit(document, { Map: flowWhenShort, Seq: flowWhenShort })
  return document.toString({ lineWidth: 140, flowCollectionPadding: true })
}

export function fromYaml(text: string): unknown {
  return parse(text)
}
