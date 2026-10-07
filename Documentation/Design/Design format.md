# Design format `apolloiq.design/1` (draft)

The language between an AI agent (or a person) and the Builder. Readable YAML, **names and paths only, no ids**: the Builder
assigns ids and keeps them as long as names match. The same format is used to read a project (`read_design`) and to propose
changes (a proposal = one or more design fragments, step 2).

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
(`delete: true`, `renamedFrom: OLD_NAME`), so a rename never becomes delete + create.

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
| `name`, `kind` (CM / EM / Unit), `version` (release.major.minor), `description` | Version: raise it when a blueprint changes |
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
| `commandInputs` | default command input rows (HMI, local panel, Unit) |

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
