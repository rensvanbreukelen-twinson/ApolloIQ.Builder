# ApolloIQ Builder

Engineering tool for ApolloIQ: blueprints (state machines, alarms, interlocks), CM / Equipment module / Unit instances, the
simulator and the export to ApolloIQ.SCADA. The shared contract with SCADA (expression engine, conventions, alarm definition,
versions, the exchange file) is **ApolloIQ.Core**, included as the git submodule `Core/`.

| Project | Purpose |
|---|---|
| `Core/ApolloIQ.Core` | Shared with SCADA: expressions, conventions, alarms, blueprint versions, stable ids, exchange file |
| `Builder.Core` | Object model: project, CMs, Units, tags, interlocks, command inputs, runtime types |
| `Builder.Logic` | Blueprints, blueprint validator, blueprint → runtime type, the PLC logic interpreter |
| `Builder.Simulator` | Simulation sessions, scenario runner, TCP server for the SCADA TcpDriver |
| `Builder.Persistence` | Project stored as one JSON file per object; the export to SCADA (`Export/ExchangeExporter.cs`) |
| `Builder.Design` | The design format `apolloiq.design/1`: read a project as a design, turn a fragment into change items, apply a selection |
| `Builder.Backend` | ASP.NET Core API on http://localhost:5180 |
| `Builder.Frontend` | React + TypeScript + Vite on http://localhost:5174 |
| `Builder.Mcp` | MCP server (Node, stdio) for AI agents: read designs, open and revise proposals ([README](Builder.Mcp/README.md)) |
| `Builder.Tests` | xUnit v3 tests (Microsoft Testing Platform, see `global.json`); fixtures in `Builder.Tests/Fixtures` |
| `Library` | The blueprint library (`Library/blueprints`) and scenarios (`Library/scenarios`), empty to start with |

## Get the code

```
git clone <url of ApolloIQ.Builder>
cd ApolloIQ.Builder
git submodule update --init
```

After pulling, run `git submodule update --init` again when `Core` moved.

## Run

```
dotnet run --project Builder.Backend            # API on http://localhost:5180
cd Builder.Frontend && npm install && npm run dev   # UI on http://localhost:5174
```

SCADA's frontend uses port 5173, so both can run side by side. The simulator also listens on TCP port 5190 for the SCADA TcpDriver.

## Test

```
dotnet test
dotnet test -- --filter-class "Builder.Tests.Export.ExchangeExporterTests"
cd Builder.Frontend && npm run lint && npm run build
```

## Expressions

All logic uses the ApolloIQ.Core expression engine: C-style operators and tags in brackets.

```
[CMD.set_on] && [LOK.can_on]
[ENGINE.STS.state] == Running || ![FEED.is_closed]
SEL([SET.bistable], TRUE, STATE_TIME() < [PAR.pulse_time]) ^ [SET.invert_output]
```

- `&&` `||` `!` `^`, `==` `!=` `<` `<=` `>` `>=`, `+ - * / %`; functions `ABS SQRT ROUND FLOOR CEIL TRUNC POW MIN MAX LIMIT SEL GOOD TIME STATE_TIME`; times as `2 s` or `500 ms`.
- In a blueprint names are relative (`[FIN.feedback]`, `[ALM.Tripped.active]`, `[is_running]`) or start with a role (`[ENGINE.STS.state]`).
  Project-level interlocks use paths (`[PMS.GEN1.is_running]`).
- A state comparison takes a state name without brackets: a category (`Running`) matches its whole range (400–499), an object
  state (`ReadyToConnect`) only its own code.
- Action targets are plain tag names: `OUT.coil_on := [CMD.set_on] && ...`.

## Export to SCADA

**Export to SCADA…** (top right) checks the project and downloads `<Project>.apolloiq.json` (schema `apolloiq.exchange/1`),
which SCADA imports. See [Documentation/Blueprints.md](Documentation/Blueprints.md#export-to-scada).

API: `GET /api/projects/{id}/export/scada/check`, `POST /api/projects/{id}/export/scada`.

## AI proposals

An AI agent builds a project from a functional description through the MCP server in [Builder.Mcp](Builder.Mcp/README.md). It
never changes the project: it opens a **proposal** (a design fragment in [apolloiq.design/1](Documentation/Design/Design%20format.md),
example: [dirty-water.design.yaml](Documentation/Design/dirty-water.design.yaml)). The engineer reviews it in the **Proposals** tab:
the description and the open questions, the changes per target with a YAML diff, live validation of the selection (dependencies are
selected with it), accept all or part as one undoable step, reject, and comments per change or on the whole proposal. The agent reads
the comments and makes a new version; changed items are marked. See [Design format § Proposals](Documentation/Design/Design%20format.md#proposals).
