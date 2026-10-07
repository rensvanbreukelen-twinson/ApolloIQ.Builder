# ApolloIQ Builder

Engineering tool for ApolloIQ: control modules, the master tag list and the exports to SCADA/HMI. Design: `ApolloIQ v2/7 Design/7.1 Milestone 0.md`.

| Project | Purpose |
|---|---|
| `Builder.Core` | Object model, tags, CM types. No dependencies. |
| `Builder.Persistence` | Project stored as one JSON file per object |
| `Builder.Backend` | ASP.NET Core API on http://localhost:5180 |
| `Builder.Frontend` | React + TypeScript + Vite on http://localhost:5173 |
| `Builder.Tests` | xUnit v3 tests (Microsoft Testing Platform, see `global.json`) |

## Run

```
dotnet run --project Builder.Backend
cd Builder.Frontend && npm install && npm run dev
```

## Test

```
dotnet test
dotnet test --filter-class "Builder.Tests.Core.TagTests"
```
