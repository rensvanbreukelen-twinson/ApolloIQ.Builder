# ApolloIQ design rules

Rules for an AI agent (or an engineer) that turns a functional description into a Builder project. Read together with
the [Design format](../Documentation/Design/Design%20format.md) and the example [dirty-water.design.yaml](../Documentation/Design/dirty-water.design.yaml).

## 1. Workflow

You work with the engineer in a chat, with the Builder tools (MCP server `apolloiq-builder`) connected.

1. **Read.** `read_rules`, the functional description the engineer gives you, the project (`read_design`) and the
   blueprint library (`list_blueprints`).
2. **Ask.** Before building anything, ask the engineer every question the description leaves open, in the chat, in
   one numbered list per round (what you need, why, and what you would assume). Repeat until nothing essential is
   open. Small details may be assumed instead of asked: list them in `questions`.
3. **Build.** Reuse library blueprints where they fit; make a new blueprint only for equipment that behaves
   differently. Write one design (YAML, see [Design format](../Documentation/Design/Design%20format.md)) and check it with
   `validate_design` until there are no errors.
4. **Hand over.** `create_proposal` with a short title, what you built, and the remaining assumptions. Tell the engineer
   to open the Builder, **Proposals** tab, and accept it; after that the states, transitions, alarms and interlocks are
   visible in the Builder's normal views.
5. **Change.** The engineer asks for changes in the chat or as comments on the proposal (`read_review_comments`). Make a
   new design for the changes and open a new proposal (or `update_proposal` while the old one is still open).
   Never change the project in another way.

## 2. Never guess

- Ask about everything that changes the behaviour: what starts and stops equipment, what happens on a fault,
  manual/auto, contact types (normally open or closed), alarm reactions.
- Values you may assume without asking (and list in `questions`): times, limits, priorities, delays, descriptions.
- Do not invent equipment, signals or alarms that the description does not mention, except the standard supervision
  of section 8 (feedback timeouts, overload, local/remote), which you add and list as an assumption.

## 3. Names

- Full words, no abbreviations; only EM and CM are abbreviated. Tag group codes (FIN, CMD, OUT, LOK, PAR, SET, PMT,
  STS, INT, ALM) are fixed keywords.
- Objects, blueprints, roles, states and alarms: `PascalCase` (`TransferPump`, `DirtyWaterTank`, `NotEmptiedInTime`).
- Tags and transitions: `snake_case` (`start_level`, `maximum_start_time`, `stopped_unexpectedly`).
- Names may not be a tag group code, a state category (Stopped, Stopping, Available, Starting, Running, Shutdown,
  Unavailable) or a standard alias (`is_running`, `is_off`, …).
- Use the names of the description for objects (tank, pump, generator); never the cabinet or drawing codes.

## 4. Structure

| Level | Use for | Example |
|---|---|---|
| CM | One piece of equipment with its own I/O and behaviour | Pump, circuit breaker, valve |
| EM | A process function that coordinates CMs and owns plain inputs that only it uses | Dirty water tank with its level floats |
| Unit | A system that coordinates EMs and CMs | Power management |
| Folder | Grouping for people; no logic | Bilge, Engine room |

- A plain signal that only matters to its parent (a float switch, a pressure switch) is a FIN tag on the EM or Unit,
  not a CM.
- Members are roles of the EM/Unit blueprint; the EM/Unit commands them through their CMD tags (`TransferPump.CMD.set_on`).
- Every EM and Unit has auto/manual. In manual it does not command its members.
- Every object runs on one device (`device`); members inherit it.

## 5. Tags

| Group | Content |
|---|---|
| FIN | Field inputs. Hard-wired: `source: LocalIO` (the Builder adds `INT.<name>` and `SET.invert_<name>`). Use the conditioned `INT.<name>` in logic. |
| CMD | Commands to the object (`set_on`, `set_off`, `reset`, own commands). Written by command inputs. |
| OUT | Field outputs. Written in states (`run`) only. |
| LOK | Interlock results (interface Interlocks). Never written by your logic. |
| PAR | Values that define alarms and timing: times, limits. Always with unit, minimum, maximum, initial value. |
| SET | Settings that change behaviour: inversion, options. |
| PMT | Predictive maintenance counters (SCADA). |
| STS | Status: `state`, `enabled`, `remote_ok` (Base), `auto` (EM/Unit), own status values for the HMI. |
| INT | Internal values. |

- Normally-closed contacts: `values: { SET.invert_<name>: "TRUE" }` on the object, and a question to confirm.
- Times are in seconds, in PAR tags, never fixed numbers in expressions (except on-delays of alarms).

## 6. States

- Every blueprint has a state machine. Each state has a category: Stopped 0 (off, not ready), Stopping 100,
  Available 200 (off, ready), Starting 300, Running 400, Shutdown 500 (fault, needs reset). Unavailable 999 is set by
  the Builder.
- State names describe the equipment (`Pumping`, `PumpingOut`, `Failed`); `text` is what the operator reads.
- Exactly one initial state. A Shutdown state needs the Resettable interface and a `reset` transition.
- Transitions: lower `priority` number wins. Fault transitions get priority 1.
- A state that waits for feedback gets a `timeout` with `goTo` a failure state and an alarm.
- `[STS.state] == Running` matches all 4xx states; an own state name matches only itself.

## 7. Expressions

- C-style: `&&`, `||`, `!`, `^`, `==`, `!=`, `<`, `+ - * /`. Tags always in brackets, relative to the object:
  `[INT.start_level]`, member tags `[TransferPump.STS.state]`, other objects by full path in project-level interlocks.
- Functions: `ABS`, `MIN`, `MAX`, `LIMIT`, `SEL`, `GOOD`, `STATE_TIME()` (time in the current state), `TIME(condition)`.
- Action targets are plain tag names (`OUT.run`, `TransferPump.CMD.set_on`).

## 8. Alarms

- Priority 0–30: 0–9 caution, 10–19 warning, 20–30 alarm. Priority orders the alarm list only.
  Guide: equipment trip or overfull 25; failure to start/stop, not done in time 20; discrepancies 10; local control,
  information 5.
- Triggers: State (condition), Range (input against thresholds), Timeout (condition must become true in time).
- **PLC reactive** when the PLC logic must react to the alarm (a transition, a stop, a start). Otherwise the alarm is
  evaluated by SCADA and the logic may not read it.
- Messages start with `{instance_name}:` and say what is wrong in operator words.
- Add an on-delay against sloshing or contact bounce where the description mentions it; otherwise ask.
- Standard supervision for switched equipment (add, and list as assumptions): failed to start, failed to stop,
  stopped unexpectedly, overload, in local.
- A process alarm (tank overfull) belongs to the EM/Unit that knows the meaning, not to the sensor.

## 9. Interlocks

- SwitchOn: must be true to switch on. SwitchOff: must be true to switch off. Trip: switches the object off and raises
  an alarm (PLC reactive), optionally escalated to the EM/Unit.
- Interlocks inside one blueprint (on a role) go in the blueprint; interlocks between objects that are not in the same
  EM/Unit go on the object (`interlocks` in `objects`), with full paths.
- Every line has an operator text ("Suction valve open").

## 10. Command inputs

- The blueprint's default rows: HMI for every Switchable or Resettable object. Local panels and buttons only when the
  description mentions them. Members of an EM/Unit get their row automatically.

## 11. Versions

- New blueprint: `0.1.0`. A change that keeps instances compatible: raise minor; new or removed tags or states: raise
  major. The Builder refuses an export of a changed blueprint with an unchanged version.

## 12. Before opening the proposal

- `validate_design` gives no errors.
- Every signal, alarm and interlock of the description is somewhere in the design.
- Every state machine can reach every state and leave every non-final state.
- Every assumption is in `questions`.

The engineer writes the description from [Functional description template.md](Functional%20description%20template.md).
