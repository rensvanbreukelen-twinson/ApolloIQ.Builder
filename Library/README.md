# Library

The Builder's blueprint library and its scenarios. Both folders start empty; new blueprints are made in the Blueprint editor.

| Folder | Files | Used by |
|---|---|---|
| `blueprints/` | `<Name>.blueprint.json` (schema `apolloiq.blueprint/1`) | `BlueprintStore` (`Builder:BlueprintPath`); every blueprint without errors is published as a type for new CMs, EMs and Units |
| `scenarios/` | `<Blueprint>.scenarios.json` (schema `apolloiq.scenarios/1`) | the Scenarios tab and `POST /api/scenarios/run` (`Builder:ScenarioPath`) |

The file formats are described in [../Documentation/Blueprints.md](../Documentation/Blueprints.md). Examples of both are the test
fixtures in `Builder.Tests/Fixtures/`.
