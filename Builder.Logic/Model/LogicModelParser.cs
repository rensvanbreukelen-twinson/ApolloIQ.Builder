using System.Text.Json.Nodes;
using Builder.Logic.Blocks;

namespace Builder.Logic.Model;

public static class LogicModelParser
{
    private static readonly HashSet<string> RootProperties = ["plant", "before", "stateMachine", "after"];
    private static readonly HashSet<string> MachineProperties = ["transitions", "outputs"];
    private static readonly HashSet<string> TransitionProperties = ["from", "to", "priority", "guard", "name"];
    private static readonly HashSet<string> AssignProperties = ["set", "expr"];
    private static readonly HashSet<string> BlockProperties = ["block", "id", "in", "out"];
    private static readonly HashSet<string> OutputProperties = ["states", "when"];

    public static LogicModel Parse(JsonNode? node)
    {
        if (node is null)
            return LogicModel.Empty;
        var errors = new List<LogicError>();
        if (node is not JsonObject root)
            throw new LogicException([new LogicError("logic", "'logic' must be an object.")]);
        Unknown(root, RootProperties, "logic", errors);

        var plant = Steps(root["plant"], "logic.plant", errors);
        var before = Steps(root["before"], "logic.before", errors);
        var after = Steps(root["after"], "logic.after", errors);
        var transitions = new List<Transition>();
        var outputs = new List<OutputRule>();

        if (root["stateMachine"] is { } machineNode)
        {
            if (machineNode is not JsonObject machine)
                errors.Add(new LogicError("logic.stateMachine", "'stateMachine' must be an object."));
            else
            {
                Unknown(machine, MachineProperties, "logic.stateMachine", errors);
                if (machine["transitions"] is JsonArray list)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        var location = $"logic.stateMachine.transitions[{i}]";
                        if (list[i] is not JsonObject t)
                        {
                            errors.Add(new LogicError(location, "A transition must be an object."));
                            continue;
                        }
                        Unknown(t, TransitionProperties, location, errors);
                        var from = t["from"] switch
                        {
                            JsonArray a => a.Select(x => x?.GetValue<string>() ?? "").ToList(),
                            JsonValue v when v.TryGetValue<string>(out var s) => [s],
                            _ => null
                        };
                        var to = Text(t, "to", location, errors);
                        var guard = Text(t, "guard", location, errors);
                        var priority = t["priority"] is JsonValue p && p.TryGetValue<int>(out var pr) ? pr : 10;
                        if (from is null || from.Count == 0)
                            errors.Add(new LogicError($"{location}.from", "'from' must be a state name, a list of state names, or \"*\"."));
                        if (from is null || to is null || guard is null)
                            continue;
                        var name = t["name"] is JsonValue n && n.TryGetValue<string>(out var nm) ? nm : $"{string.Join("|", from)} → {to}";
                        transitions.Add(new Transition(from, to, priority, guard, name, location));
                    }
                }
                else if (machine["transitions"] is not null)
                    errors.Add(new LogicError("logic.stateMachine.transitions", "'transitions' must be a list."));

                if (machine["outputs"] is JsonObject outputObject)
                {
                    foreach (var (target, value) in outputObject)
                    {
                        var location = $"logic.stateMachine.outputs.{target}";
                        switch (value)
                        {
                            case JsonArray states:
                                outputs.Add(new OutputRule(target, states.Select(x => x?.GetValue<string>() ?? "").ToList(), null, location));
                                break;
                            case JsonValue v when v.TryGetValue<string>(out var expression):
                                outputs.Add(new OutputRule(target, null, expression, location));
                                break;
                            case JsonObject o:
                                Unknown(o, OutputProperties, location, errors);
                                var states2 = o["states"] is JsonArray sa ? sa.Select(x => x?.GetValue<string>() ?? "").ToList() : null;
                                var when = o["when"] is JsonValue w && w.TryGetValue<string>(out var ws) ? ws : null;
                                if (states2 is null && when is null)
                                    errors.Add(new LogicError(location, "An output needs 'states', 'when' or both."));
                                else
                                    outputs.Add(new OutputRule(target, states2, when, location));
                                break;
                            default:
                                errors.Add(new LogicError(location, "An output is a list of states, an expression, or { states, when }."));
                                break;
                        }
                    }
                }
                else if (machine["outputs"] is not null)
                    errors.Add(new LogicError("logic.stateMachine.outputs", "'outputs' must be an object."));
            }
        }

        if (errors.Count > 0)
            throw new LogicException(errors);
        return new LogicModel(plant, before, transitions, outputs, after);
    }

    private static List<LogicStep> Steps(JsonNode? node, string location, List<LogicError> errors)
    {
        var result = new List<LogicStep>();
        if (node is null)
            return result;
        if (node is not JsonArray list)
        {
            errors.Add(new LogicError(location, "Must be a list of steps."));
            return result;
        }
        for (var i = 0; i < list.Count; i++)
        {
            var at = $"{location}[{i}]";
            if (list[i] is not JsonObject step)
            {
                errors.Add(new LogicError(at, "A step must be an object."));
                continue;
            }
            if (step.ContainsKey("set"))
            {
                Unknown(step, AssignProperties, at, errors);
                var target = Text(step, "set", at, errors);
                var expr = Text(step, "expr", at, errors);
                if (target is not null && expr is not null)
                    result.Add(new AssignStep(target, expr, at));
            }
            else if (step.ContainsKey("block"))
            {
                Unknown(step, BlockProperties, at, errors);
                var block = Text(step, "block", at, errors);
                if (block is not null && !BlockLibrary.Exists(block))
                    errors.Add(new LogicError($"{at}.block", $"Unknown block '{block}'. Use {string.Join(", ", BlockLibrary.Names)}."));
                var id = step["id"] is JsonValue idv && idv.TryGetValue<string>(out var ids) ? ids : $"{block}_{i}";
                var inputs = Map(step["in"], $"{at}.in", errors);
                var outputs = Map(step["out"], $"{at}.out", errors);
                if (block is not null)
                    result.Add(new BlockStep(block.ToUpperInvariant(), id, inputs, outputs, at));
            }
            else
                errors.Add(new LogicError(at, "A step needs 'set' (assignment) or 'block'."));
        }
        return result;
    }

    private static Dictionary<string, string> Map(JsonNode? node, string location, List<LogicError> errors)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is null)
            return result;
        if (node is not JsonObject obj)
        {
            errors.Add(new LogicError(location, "Must be an object of pin → expression or tag."));
            return result;
        }
        foreach (var (key, value) in obj)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var s))
                result[key] = s;
            else if (value is JsonValue n && (n.TryGetValue<double>(out var d)))
                result[key] = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            else if (value is JsonValue b && b.TryGetValue<bool>(out var bv))
                result[key] = bv ? "TRUE" : "FALSE";
            else
                errors.Add(new LogicError($"{location}.{key}", "Must be an expression or a tag."));
        }
        return result;
    }

    private static string? Text(JsonObject obj, string key, string location, List<LogicError> errors)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0)
            return s;
        errors.Add(new LogicError($"{location}.{key}", $"'{key}' is required."));
        return null;
    }

    private static void Unknown(JsonObject obj, HashSet<string> allowed, string location, List<LogicError> errors)
    {
        foreach (var (key, _) in obj)
        {
            if (!allowed.Contains(key))
                errors.Add(new LogicError($"{location}.{key}", $"Unknown property '{key}'."));
        }
    }
}
