export type ApiErrorBody = { code: string; message: string; field?: string | null }

export type OptionalTag = { key: string; description: string; addsTags: number }

export type CmType = {
  name: string
  version: string
  description: string
  tagCount: number
  optionalTags: OptionalTag[]
}

export type LibraryError = { file: string; path: string; message: string; line?: number | null }

export type ProjectSummary = { id: string; name: string }

export type Project = { id: string; name: string; maxNameLength: number }

export type NodeKind = 'folder' | 'controlModule' | 'unit' | 'equipmentModule'

export type TreeNode = {
  id: string
  name: string
  kind: NodeKind
  path: string
  parentId: string | null
  typeName: string | null
  typeVersion: string | null
  tagCount: number
  children: TreeNode[]
}

export type DeletionSummary = { folders: number; controlModules: number; tags: number }

export type Tag = {
  id: string
  path: string
  name: string
  group: string
  dataType: string
  direction: string
  kind: string
  symbolKey: string
  controlModuleId: string
  controlModulePath: string
  unit: string | null
  enumType: string | null
  initial: unknown
  description: string
}

export type TagFilter = {
  scope?: string
  group?: string
  direction?: string
  kind?: string
  search?: string
}

export type HmiAddressMode = 'Path' | 'SymbolKey'

export type HmiExportProfile = {
  connectionId: string | null
  scanRateMs: number
  address: HmiAddressMode
  tagCount: number
}

export type InterlockKind = 'SwitchOn' | 'SwitchOff' | 'Trip'

export type TripEscalation = 'None' | 'EM' | 'Unit'

/** A project-level interlock line on a CM, EM or Unit (G-172). The condition uses paths. */
export type InterlockRule = {
  targetId: string | null
  targetPath: string
  kind: InterlockKind
  condition: string
  text: string
  generatedText: string
  alarm: string | null
  severity: number
  escalate: TripEscalation
  error: string | null
}

/** An interlock that acts on the object but is defined by its blueprint or a container. */
export type InheritedInterlock = { kind: InterlockKind; text: string; condition: string; definedBy: string; origin: 'blueprint' | 'project'; alarm: string | null; escalate: TripEscalation }

export type ObjectInterlocks = {
  id: string
  path: string
  hasInterlocks: boolean
  interlocks: InterlockRule[]
  actingOnThis: InheritedInterlock[]
  targets: { id: string; path: string; hasInterlocks: boolean }[]
}

export type InterlockRuleInput = {
  targetId: string | null
  kind: InterlockKind
  condition: string
  text: string | null
  alarm: string | null
  severity: number | null
  escalate: TripEscalation | null
}

export type InterlockSummary = { hasInterlocks: boolean; defined: number; switchOn: number; switchOff: number; trips: number }

export type AlarmDefinition = {
  name: string
  severity: number
  defaultSeverity: number
  band: string
  message: Record<string, string>
  condition: string
  latch: string
  runsOn: string
  onTransition: string | null
  activeTag: string | null
}

export type WireMode = 'On' | 'Off' | 'Toggle' | 'Maintained' | 'Direct'

export type CommandWire = { sourceId?: string; source: string; mode: WireMode; command: string | null }

export type PicRow = {
  name: string
  source: 'Hardwired' | 'Hmi' | 'Auto'
  kind: 'Hold' | 'Pulse'
  on: number | null
  off: number | null
  inAuto: 'Normal' | 'Only' | 'Ignore' | 'Override'
  onTag?: string | null
  offTag?: string | null
}

export type PicSettings = { onLabel: string; offLabel: string; rows: PicRow[]; labelPairs?: string[] }
