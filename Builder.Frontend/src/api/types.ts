export type ApiErrorBody = { code: string; message: string; field?: string | null }

/** A published CM blueprint, as the New control module dialog lists it. */
export type CmType = {
  id: string
  name: string
  version: string
  description: string
  tagCount: number
}

export type ProjectSummary = { id: string; name: string }

export type Project = { id: string; name: string; maxNameLength: number }

export type NodeKind = 'folder' | 'controlModule' | 'unit' | 'equipmentModule'

export type TreeNode = {
  id: string
  name: string
  kind: NodeKind
  path: string
  parentId: string | null
  blueprintId: string | null
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
  priority: number
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
  priority: number | null
  escalate: TripEscalation | null
}

export type InterlockSummary = { hasInterlocks: boolean; defined: number; switchOn: number; switchOff: number; trips: number }

/** An alarm of a CM; the priority can be overridden per CM. */
export type AlarmDefinition = {
  id: string
  name: string
  priority: number
  defaultPriority: number
  level: 'Caution' | 'Warning' | 'Alarm'
  message: string
  trigger: 'State' | 'Range' | 'Timeout' | 'PlcByte'
  condition: string
  plcReactive: boolean
  latched: boolean
  onTransition: string | null
  source: 'Blueprint' | 'StateTimeout' | 'Trip' | 'UnitOverride' | 'StuckInput'
  activeTag: string | null
}

export type WireMode = 'On' | 'Off' | 'Toggle' | 'Maintained' | 'Direct'

export type CommandWire = { sourceId?: string; source: string; mode: WireMode; command: string | null }

/** The check before an export to SCADA. Errors block the download, warnings name what does not reach SCADA. */
export type ExportCheck = {
  errors: string[]
  warnings: string[]
  blueprints: { id: string; name: string; version: string; changedSinceLastExport: boolean; lastExportedVersion: string | null }[]
  fileName: string
}
