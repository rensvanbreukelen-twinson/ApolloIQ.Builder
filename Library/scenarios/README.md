# Scenario files

One file per blueprint: `<Type>.scenarios.json`, schema `apolloiq.scenarios/1`. The Builder runs them on the reference interpreter (Scenarios tab, `POST /api/scenarios/run`, and the `ScenarioTests` unit test) and reports failures, a trace and transition coverage.

```jsonc
{
  "schema": "apolloiq.scenarios/1",
  "type": "CircuitBreaker",
  "scenarios": [
    {
      "name": "Close and open on command",
      "instances": [ { "name": "CM", "optionalTags": ["FIN.spring_charged"] } ],   // optional; default: one instance "CM"
      "steps": [
        { "run": 2 },                                         // cycles, or "2 s", "500 ms", "1 min", "10 cycles"
        { "set": { "CMD.set_on": true } },                    // write CMD, PAR, SET, LOK or FIN tags
        { "force": { "FIN.feedback": false } },               // force any tag
        { "release": "FIN.feedback" },                        // or a list
        { "bad": "FIN.feedback" }, { "good": "FIN.feedback" },// quality
        { "until": "is_closed", "within": "1 s" },            // fails when not true in time
        { "expect": ["STS.state = Running", "OUT.coil_on"] }  // fails when false or bad quality
      ]
    }
  ]
}
```

- Tags are relative to the first instance, or start with an instance name (`B.CMD.set_on`).
- An instance can have command wires and, for a PIC, its matrix:

```jsonc
"instances": [
  { "name": "CB", "type": "CircuitBreaker", "wires": [ { "source": "BTN.INT.pressed", "mode": "Toggle" } ] },
  { "name": "BTN", "type": "PushButton" },
  { "name": "PIC", "type": "PriorityInputControl", "pic": { "onLabel": "Up", "offLabel": "Down",
      "rows": [ { "name": "WH", "source": "Hardwired", "kind": "Hold", "on": 1, "off": 1 } ] } }
]
```
- Conditions use the expression language (no timers). Prefix `B:` to evaluate in instance `B`, or use `[SIM.B.is_closed]`.
- Instances live in folder `SIM`, so their paths are `SIM.<name>`.
- **Equipment modules and Units:** give a member `"parent": "<instance>"` (listed after its parent) and optionally `"role"`; without a role it takes the first free role with a matching blueprint. Members sit under their parent, so `PS.GEN.FIN.running` means `SIM.PS.GEN.FIN.running`, and `GEN: STS.state = Running` evaluates in the member.

```jsonc
"instances": [
  { "name": "PS", "type": "GenSetPowerSource" },
  { "name": "GEN", "type": "GenSet", "parent": "PS" },
  { "name": "CB", "type": "CircuitBreaker", "parent": "PS" },
  { "name": "PM", "type": "PowerMeter", "parent": "PS" }
]
```
- An EM or Unit only commands its members while it is in auto: start with `{ "set": { "CMD.set_auto": true } }`.
- Coverage of an EM or Unit blueprint is counted on an instance with all its roles filled.

## Command inputs

- An instance gets the command inputs of its blueprint. A scenario can replace them with `"commandInputs": { "level": false, "selector": null, "rows": [ … ] }` on the instance.
- HMI inputs are written as `CMD.<input>_on` / `_off` / `_<command>`, for example `{ "set": { "CMD.HMI_on": true } }`. Writing `CMD.set_on` directly has no effect on a CM with command inputs: arbitration writes it every cycle.
- Digital inputs are `FIN.<input>` (or `FIN.<input>_on` / `_off` for a two-contact button); force them.
