# Blueprints and the export to SCADA

## Blueprint file

`Library/blueprints/<Name>.blueprint.json`, schema `apolloiq.blueprint/1`. Examples: `Builder.Tests/Fixtures/blueprints`.

```jsonc
{
  "schema": "apolloiq.blueprint/1",
  "id": "0b1e0000-0000-4000-8000-000000000001",   // stable: assigned on the first save, kept on rename
  "kind": "CM",                                     // CM, EM or Unit
  "name": "Light",
  "version": "0.1.0",                               // release.major.minor; raise it before exporting a change
  "description": "…",
  "interfaces": [ "Base", "Switchable", "Resettable", "Interlocks" ],
  "tags": [
    { "id": "…", "group": "FIN", "name": "feedback", "dataType": "Bool", "description": "…", "source": "LocalIO" }
  ],
  "roles": [ { "name": "LAMP", "blueprintId": "…" } ],        // EM and Unit: members, by blueprint id
  "states": [
    { "name": "TurningOn", "text": "Turning on", "description": "", "category": 300, "code": 300,
      "run": [ { "tag": "OUT.lamp", "value": "TRUE" } ],
      "timeout": { "time": "[PAR.max_switch_time]", "goTo": "Broken",
                   "alarm": "DoesNotSwitchOn", "alarmId": "…", "priority": 20, "message": null } }
  ],
  "transitions": [ { "name": "switch_on", "from": [ "Off" ], "to": "TurningOn", "guard": "[CMD.set_on] && [LOK.can_on]", "priority": 10 } ],
  "always": [ { "tag": "STS.remote_ok", "value": "[STS.enabled]" } ],
  "alarms": [ /* see below */ ],
  "plant": [ { "tag": "FIN.feedback", "value": "[OUT.lamp]" } ],     // simulator only
  "interlocks": [
    { "target": "LAMP", "kind": "Trip", "condition": "![FEED.is_closed]", "text": "Feeder opened",
      "alarm": "FeederOpen", "alarmId": "…", "priority": 20, "escalate": "EM" }
  ],
  "aliases": { "is_closed": "is_running" },
  "commandInputs": { "rows": [ { "name": "HMI", "source": "Hmi", "kind": "Pulse", "on": 1, "off": 1, "commands": [ "reset" ] } ] }
}
```

- **Ids**: the blueprint, its tags, its alarms, its timeout alarms and its trip alarms have GUIDs, assigned once by the Builder and
  kept on rename. Instances (`blueprintId` + `blueprintVersion` in the CM / Unit files) and roles refer to the blueprint by id.
- **States**: `name` is an identifier used in expressions (`[STS.state] == TurningOn`); it may not be a category (Stopped,
  Running, …) or a standard alias (`is_running`, …). `text` is what the operator sees (the name when empty). The `code` follows from
  the category and the order of the states.
- All expressions use the ApolloIQ.Core syntax (see the README). Action targets are plain tag names.

### Alarms

An alarm is the shared ApolloIQ.Core `AlarmDefinition`, written flat, plus two Builder-only settings:

```jsonc
{
  "id": "…",
  "name": "LampFailure",
  "priority": 10,                         // 0–9 caution, 10–19 warning, 20–30 alarm
  "message": "{instance_name}: lamp failure",
  "trigger": "State",                     // State, Range or Timeout
  "plcReactive": false,
  "condition": "[STS.state] == On && ![INT.feedback]",   // State; Timeout: must become true
  "input": "", "highCaution": "", "highWarning": "", "highAlarm": "", "lowCaution": "", "lowWarning": "", "lowAlarm": "",   // Range
  "timeoutMode": "Running", "running": "", "triggerExpr": "", "stop": "", "timeout": "",                                   // Timeout
  "onDelaySeconds": 1,
  "latched": false,                       // Builder: PLC reactive only, stays active until CMD.reset (needs Resettable)
  "onTransition": null                    // Builder: PLC reactive only, raised in the cycle this transition is taken
}
```

- **PLC reactive** (`plcReactive: true`): the PLC logic evaluates the alarm and reacts to it. It gets the tags
  `ALM.<name>.active / .enabled / .raise_count`; transition guards may read `[ALM.<name>.active]`. SCADA reads it as a PLC
  alarm byte (`AlarmRules.ForScada` turns it into a `PlcByte` alarm, keeping the condition for information).
- **Not PLC reactive**: SCADA evaluates the alarm. It has no tags in the PLC logic and transition guards may not refer to it.
- Generated alarms are PLC reactive: the alarm of a state timeout (with a "go to" state it is raised on the timeout transition,
  latched when the blueprint is resettable; without one it is a Timeout alarm that is active while the state lasts too long), the
  alarm of a Trip interlock line, `ManualByOverride` of every EM and Unit, and `<input>_stuck` of a digital command input with a
  stuck time.
- The simulator evaluates **every** alarm with the shared `AlarmTriggerLogic` (as SCADA will) and lists them in the Simulator tab
  (`GET /api/projects/{id}/simulation/alarms`).
- A CM can override the priority of a blueprint alarm (Alarms dialog); the export carries the blueprint's priority and warns.

## Export to SCADA

`POST /api/projects/{id}/export/scada` downloads `<Project>.apolloiq.json` (ApolloIQ.Core `ExchangeFile`, schema
`apolloiq.exchange/1`). `GET …/export/scada/check` returns `{ errors[], warnings[], blueprints[{ name, version, changedSinceLastExport }], fileName }`.

Per blueprint used by the project:

| Field | Content |
|---|---|
| `id`, `name`, `kind`, `version`, `description` | from the blueprint |
| `interfaces` | the interfaces SCADA knows (`InScada`); Container is Builder-only |
| `tags` | the blueprint's own tags except OUT; the tags the PLC logic generates (state timeout parameters `PAR.<state>_timeout`, `SET.invert_<input>`, conditioned `INT.<input>`); the tags of Builder-only interfaces (`INT.member_tripped`, `INT.escalated`) as ordinary tags; the tags of the default command inputs (`CMD.HMI_on`, …). Never ALM or SCADA-interface tags. Generated tags get name-based ids (`StableId.From("apolloiq.builder.interface-tag" / "…generated-tag" / "…command-input-tag", blueprintId, "GROUP.name")`) |
| `states` | the named states: code, name, text, description |
| `commandLabels` | captions of the command-input CMD tags (`HMI_on` → "On") |
| `interlockTexts` | the text of each `LOK.can_on_status` / `can_off_status` bit for the blueprint's own lines |
| `alarms` | blueprint alarms, timeout alarms, trip alarms, `ManualByOverride`, stuck alarms, through `AlarmRules.ForScada` |
| `hash` | `ExchangeJson.Hash` of the above |

Per CM, EM and Unit (parents before children): `id` (the Builder object id), `name`, `blueprintId`, `parentId` (the containing EM
or Unit; folders are not instances), `device` (the execution device), `interlockTexts` when interlocks of containers or the
project add lines to the object (the complete list, numbered as the PLC logic numbers the bits), and `alarms` with the trip alarms
of the object's own project-level interlock lines.

**Version guard.** `exports.json` in the project folder records, per blueprint, the version and hash of the last export. An export
is refused when a blueprint's hash changed but its version did not ("<Blueprint> changed since the last export to SCADA; raise its
version"). The first export is not checked. Blueprint validation errors also block the export.

**Warnings** (not blocking): per-CM priority overrides, CMs whose command inputs differ from their blueprint's defaults,
instances made from another version than the one exported, SCADA alarms of an EM or Unit that refer to member roles.
