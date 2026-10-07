# Design format `apolloiq.design/1`

The language between an AI agent (or a person) and the Builder. Readable YAML, **names and paths only, no ids**: the Builder
assigns ids and keeps them as long as names match. The same format is used to read a project (`read_design`) and to propose
changes: a proposal holds a design fragment per version (see [Proposals](#proposals)). The Builder API speaks JSON with exactly
the same structure; the MCP server (`Builder.Mcp`) converts YAML to JSON and back.

Example: [dirty-water.design.yaml](dirty-water.design.yaml).

## Top level

```yaml
schema: apolloiq.design/1
project: <name>            # informational in a fragment
devices: [...]             # topology: PLCs and other devices
blueprints: [...]          # new or changed blueprints; library blueprints are referred to by name
objects: [...]             # the object tree: folders, Units, EMs, CMs
questions: [...]           # open points for the engineer; never guessed silently
```

A fragment only contains what it creates or changes. Anything not mentioned stays as it is. Deleting and renaming are explicit
(`delete: true`, `renamedFrom: OLD_NAME`), so a rename never becomes delete + create; a rename keeps the ids. An object moves to
another place in the tree with `movedFrom: Old.Parent.Path.Name` (it may get a new name at the same time).

The format is strict: an unknown field, a wrong `schema`, a tag key that is not `GROUP.name`, an unknown enum value (state
category, interlock kind, alarm trigger, device role) or a reference to something that does not exist is an error, never ignored.

## Devices

```yaml
devices:
  - name: MainPlc
    role: Plc               # Plc, Scada, ThirdParty, Hmi
    description: Bachmann M1, engine room
```

## Blueprints

Same content as a blueprint file, without ids. Only non-empty fields are written.

| Field | Notes |
|---|---|
| `name`, `kind` (CM / EM / Unit), `version` (release.major.minor), `description` | Version: raise it when a blueprint changes. `renamedFrom` / `delete: true` as for objects |
| `interfaces` | Base (always), Switchable, Resettable, Interlocks, AutoManual (always for EM/Unit) |
| `tags` | `GROUP.name: { type, description, unit, min, max, initial, source }` — groups FIN, CMD, OUT, LOK, PAR, SET, PMT, STS, INT; types Bool, Int, Real, String; `source: LocalIO` for hard-wired inputs (the Builder adds the conditioned `INT.` copy and `SET.invert_`) |
| `roles` | EM/Unit members: `ROLE: Blueprint` |
| `states` | `Name: { category, text, description, initial, entry, run, exit, timeout }`. Name = identifier used in expressions; `text` = what the operator sees. Category: Stopped 0, Stopping 100, Available 200, Starting 300, Running 400, Shutdown 500; codes follow from the order |
| actions (`entry`, `run`, `exit`, `always`, `plant`) | `TARGET.TAG: expression`. Target is a plain tag name (also member tags: `TransferPump.CMD.set_on`); `plant` = simulator only |
| `timeout` | `{ time, goTo, alarm, priority, message }` |
| `transitions` | `name: { from: [..] or "*", to, guard, priority }` (lower priority number wins) |
| `alarms` | `Name: { priority, message, trigger, plcReactive, condition, … , onDelay }` — as in SCADA; `{instance_name}` in messages |
| `interlocks` | `- { target, kind: SwitchOn/SwitchOff/Trip, condition, text, alarm, priority, escalate }` — target empty = the object itself, or a role |
| `aliases` | `is_closed: is_running` |
| tag `renamedFrom`, alarm `renamedFrom` | Rename a tag or an alarm inside a blueprint and keep its id; `primary: true` marks the primary feedback |
| `commandInputs` | default command input rows (HMI, local panel, Unit) |

A changed blueprint is given whole for the sections it mentions: a section that is present (`roles`, `tags`, `states` with
their `entry`/`run`/`exit`/`timeout`, `transitions`, `always`, `alarms`, `interlocks`, `aliases`, `commandInputs`, `plant`)
replaces that section; a section that is left out stays as it is. `kind`, `version`, `description` and `interfaces` form the
section "general". Each section that differs is one change item (`blueprint:<name>:<section>`); a rename is its own item. For a
new blueprint `kind` and the states are required.

Expressions: ApolloIQ.Core syntax, tags in brackets, relative to the object: `[STS.auto] && [INT.start_level]`,
`[TransferPump.STS.state] == Failed`, `STATE_TIME() > [PAR.run_on_time]`.

## Objects

A tree. Each node is one of `folder`, `unit`, `em`, `cm`:

```yaml
objects:
  - folder: Bilge
    children:
      - em: DirtyWaterTank          # name = path segment
        blueprint: DirtyWaterTank
        description: Dirty water tank
        device: MainPlc             # execution device (members inherit it)
        members:                    # EM/Unit: one entry per role; the member's blueprint follows from the role
          TransferPump: { cm: TransferPump, description: Dirty water transfer pump }
        interlocks: [...]           # project-level lines on this object; conditions use full paths
        alarmPriorities: { Overfull: 25 }    # per-object override
        values: { SET.invert_overfull_level: "TRUE" }   # per-object initial values of PAR / SET tags
        commandInputs: [...]        # per-object command inputs, when they differ from the blueprint
```

Full paths follow the tree including folders: `Bilge.DirtyWaterTank.TransferPump.STS.state`.

- An object that exists only needs its name to be a step in the path; fields that are given change it, fields left out stay.
- The blueprint of an existing object cannot change (delete it and create it again); a member's blueprint follows from the role.
- `interlocks` on an object is its whole list of project-level lines: lines left out are removed. A line is matched by kind,
  target and condition (a trip by its alarm), so changing a condition removes the old line and adds a new one.
- `values` only take PAR and SET tags. `alarmPriorities`, `values` and `commandInputs` are overrides: one change item each.

## Naming

Write names in full: `TransferPump`, `start_level`, `maximum_start_time`; no abbreviations other than EM and CM. The tag group
codes (FIN, CMD, OUT, LOK, PAR, SET, PMT, STS, INT, ALM) are fixed keywords of the format.

## Questions

```yaml
questions:
  - about: Bilge.DirtyWaterTank
    question: Should the pump also run on the overfull float in manual?
    assumed: No; in manual the operator decides. The overfull alarm is raised in both modes.
```

Every assumption the agent makes appears here, with what it assumed, so the engineer can confirm or correct it in the review.

## Proposals

A proposal is a pull request against the project. It is stored as `proposals/<id>.json` in the project folder (schema
`apolloiq.proposal/1`): title, description (markdown, with the open questions), author, created / updated, status, the base revision,
the versions (each: the design fragment, the change items computed from it, a note, which items changed since the previous version),
the state of each item (Open, Accepted, Rejected) and the review comments (per item or general, with the version they were made on).

**Change items.** The Builder turns a fragment into items matched by name and path against the current project: one per device,
per object (create, update, delete, rename, move of a folder, Unit, EM or CM), per override (`values`, `alarmPriorities`,
`commandInputs`), per project-level interlock line, and for an existing blueprint one per section (see [Blueprints](#blueprints));
a new blueprint is one item. Each item has an id (for example
`blueprint:Pump:alarms`, `object:Bilge.DirtyWaterTank`), kind (Create / Update / Delete / Rename / Move), target, summary, before and
after (design JSON) and the items it needs. A fragment that matches the project makes zero items. `questions` are shown, not applied.

**Accept.** A selection must include what it needs. It is applied to a copy of the project and the blueprint library, then
validated (blueprint validator, expressions, interlocks, project checks); only when that passes is the project replaced, as one
step that **Undo last accept** reverts. The Builder assigns all ids.

**Status.** Open → PartlyAccepted → Accepted; Rejected when the engineer rejects the proposal (or every item); Superseded when a
new proposal names this one in `supersedes`. When the project changed since a version was made, the items are computed again
against the current project; an item whose target changed since that version is marked as a conflict.

**API** (`/api/projects/{id}`): `GET design?path=`, `POST design/validate`, `GET|POST proposals`, `GET|PUT proposals/{p}`
(PUT = new version), `POST proposals/{p}/validate|accept|reject|reopen|reject-proposal`, `GET|POST proposals/{p}/comments`,
`POST proposals/undo`; the library as a design: `GET /api/blueprints/design?name=`.
