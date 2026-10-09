# Design kit

Everything needed to go from a functional description to a Builder project with an AI agent.

| File | For |
|---|---|
| [Functional description template.md](Functional%20description%20template.md) | The engineer: what a description must contain |
| [Rules for AI.md](Rules%20for%20AI.md) | The AI agent: how to turn a description into a Builder design |
| [Example - Dirty water tank.md](Example%20-%20Dirty%20water%20tank.md) | A worked example description |

## Workflow

1. **Describe.** The engineer writes the functional description in plain text from the template.
2. **Translate.** The engineer gives it to the AI agent (Claude, with the Builder tools connected). The agent reads the
   rules, asks questions in the chat until the design is complete, and then puts the design in the Builder as a
   proposal.
3. **Review.** The engineer opens the Builder, **Proposals** tab, and accepts the proposal (all of it, or part of it). From
   then on everything is visible in the Builder's normal views: blueprints, states, transitions, alarms, interlocks,
   and the simulator.
4. **Change.** The engineer asks the agent for changes, in the chat or as comments on the proposal. The agent makes a new
   proposal; the engineer accepts it. Repeat until the design is right. Small fixes can also be made directly in the
   Builder.
5. **Export.** **Export to SCADA…** in the Builder makes the file that SCADA imports (Blueprints tab, **Import Builder
   project…**).

## Setup (once)

1. Start the Builder: `dotnet run --project Builder.Backend` and, in `Builder.Frontend`, `npm install` then `npm run dev`
   (http://localhost:5174).
2. In `Builder.Mcp`, run `npm install`.
3. Register the Builder tools in Claude (see [Builder.Mcp/README.md](../Builder.Mcp/README.md)): for Claude Desktop add
   `apolloiq-builder` to `claude_desktop_config.json` and restart Claude.
4. Create an empty project in the Builder.

## Starting a design

Open a new chat in Claude and write, for example:

> Read the rules with `read_rules`. Then design the system described below in the Builder project "Dirty water demo".
> Ask me your questions first.
>
> *(paste the functional description, or attach the file)*
