# ApolloIQ Builder MCP server

An MCP server (stdio) that lets an AI agent read a Builder project and open **proposals**, like pull requests. The agent never
changes a project: the engineer reviews a proposal in the Builder (**Proposals** tab), accepts all or part of it, comments, and the
agent revises it. Designs go in and out as YAML in the format `apolloiq.design/1`
([Design format](../Documentation/Design/Design%20format.md)); the server converts to and from the JSON the Builder API speaks.

## Run

The Builder backend must run (`dotnet run --project Builder.Backend`, http://localhost:5180).

```
cd Builder.Mcp
npm install
node src/server.js                          # stdio; normally started by Claude, see below
APOLLOIQ_BUILDER_URL=http://localhost:5180 npm run smoke
```

| Environment | Default | |
|---|---|---|
| `APOLLOIQ_BUILDER_URL` | `http://localhost:5180` | The Builder API |
| `APOLLOIQ_DESIGN_DOCUMENTATION` | `../Documentation/Design` | Where `read_rules` finds `Rules.md` and `Design format.md` |

`npm run smoke` creates a project over HTTP, then through the MCP server runs `validate_design` on
[dirty-water.design.yaml](../Documentation/Design/dirty-water.design.yaml), `create_proposal`, `read_proposal` and a few read tools.
Open the Builder afterwards to review the proposal it made.

## Register in Claude

**Claude Code**

```
claude mcp add apolloiq-builder -e APOLLOIQ_BUILDER_URL=http://localhost:5180 -- node /absolute/path/to/ApolloIQ.Builder/Builder.Mcp/src/server.js
```

**Claude Desktop / Cowork** — `claude_desktop_config.json` (macOS `~/Library/Application Support/Claude/`, Windows `%APPDATA%\Claude\`):

```json
{
  "mcpServers": {
    "apolloiq-builder": {
      "command": "node",
      "args": ["/absolute/path/to/ApolloIQ.Builder/Builder.Mcp/src/server.js"],
      "env": { "APOLLOIQ_BUILDER_URL": "http://localhost:5180" }
    }
  }
}
```

Restart Claude after changing the file. Node 20 or later.

## Tools

| Tool | |
|---|---|
| `read_rules` | `Documentation/Design/Rules.md` (when present) and the design format. Read first. |
| `list_projects` | Projects by name and id. Tools take a project by name or id. |
| `read_design` | The project as a design (YAML); `path` for one subtree, e.g. `Bilge.DirtyWaterTank`. |
| `list_blueprints`, `read_blueprint` | The blueprint library; one blueprint as a design. |
| `validate_design` | The change items a fragment makes and the validation of accepting them all, **without** a proposal. Iterate with it. |
| `create_proposal` | Opens a proposal: title, description (markdown; assumptions and open questions), design fragment. Returns the items and the validation of accepting everything. |
| `update_proposal` | A new version from a new (whole) fragment with a note; comments stay, changed items are marked. |
| `list_proposals`, `read_proposal` | Status, items (Open / Accepted / Rejected), conflicts with the current project, versions, comments. `diffs: true` adds before / after. |
| `read_review_comments` | The engineer's comments, per item or on the whole proposal. |

There is no tool that changes a project directly, and no `run_scenarios`: the scenario runner (`POST /api/scenarios/run`) runs the
library's scenario files on the library's blueprints, not on a project or a proposal, so it would not test what the agent proposes.

## How a proposal works

1. The agent writes a fragment: only what it creates or changes (`renamedFrom`, `movedFrom`, `delete: true` are explicit).
2. The Builder turns it into **change items** matched by name and path: one per device, object, override (values, alarm
   priorities, command inputs), project-level interlock line, and per blueprint section (or one "create blueprint" item). Each item
   has a before / after and the items it depends on.
3. The engineer selects items; selecting one selects what it needs. The selection is validated live on a copy of the project
   (blueprint validation, expressions, interlocks). **Accept selected** applies it as one step, which can be undone.
4. Comments go to the agent through `read_review_comments`; `update_proposal` makes version 2. Items that changed are marked.
5. When the project changes after a version was made, the items are computed again; an item whose target changed is a conflict.
