using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Builder.Simulator.Scenarios;

public static partial class ScenarioLoader
{
    public const string Schema = "apolloiq.scenarios/1";
    public const string FileSuffix = ".scenarios.json";

    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly HashSet<string> RootProperties = ["schema", "type", "scenarios"];
    private static readonly HashSet<string> ScenarioProperties = ["name", "description", "instances", "steps"];
    private static readonly HashSet<string> InstanceProperties = ["name", "type", "optionalTags", "wires", "pic", "commandInputs", "parent", "role"];
    private static readonly HashSet<string> StepKinds = ["set", "force", "release", "bad", "good", "run", "until", "within", "expect", "note"];

    public static (IReadOnlyList<ScenarioFile> Files, IReadOnlyList<ScenarioError> Errors) LoadDirectory(string directory)
    {
        var files = new List<ScenarioFile>();
        var errors = new List<ScenarioError>();
        if (!Directory.Exists(directory))
            return (files, errors);
        foreach (var path in Directory.GetFiles(directory, $"*{FileSuffix}").Order(StringComparer.Ordinal))
        {
            try
            {
                files.Add(Parse(File.ReadAllText(path), Path.GetFileName(path)));
            }
            catch (ScenarioException ex)
            {
                errors.AddRange(ex.Errors);
            }
        }
        return (files, errors);
    }

    public static ScenarioFile Parse(string json, string fileName)
    {
        var errors = new List<ScenarioError>();
        void Error(string location, string message) => errors.Add(new ScenarioError(fileName, location, message));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ScenarioException([new ScenarioError(fileName, $"line {(ex.LineNumber ?? 0) + 1}", ex.Message)]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ScenarioException([new ScenarioError(fileName, "", "The file must contain an object.")]);
            Unknown(root, RootProperties, "", Error);
            if (Text(root, "schema") != Schema)
                Error("schema", $"'schema' must be \"{Schema}\".");
            var type = Text(root, "type");
            if (string.IsNullOrWhiteSpace(type))
                Error("type", "'type' is required: the CM type these scenarios test.");

            var scenarios = new List<Scenario>();
            if (!root.TryGetProperty("scenarios", out var list) || list.ValueKind != JsonValueKind.Array)
                Error("scenarios", "'scenarios' must be a list.");
            else
            {
                var index = 0;
                foreach (var item in list.EnumerateArray())
                {
                    var at = $"scenarios[{index++}]";
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        Error(at, "A scenario must be an object.");
                        continue;
                    }
                    Unknown(item, ScenarioProperties, at, Error);
                    var name = Text(item, "name");
                    if (string.IsNullOrWhiteSpace(name))
                        Error($"{at}.name", "'name' is required.");
                    else if (scenarios.Any(s => s.Name == name))
                        Error($"{at}.name", $"Duplicate scenario name '{name}'.");
                    var instances = Instances(item, type ?? "", at, Error);
                    var steps = Steps(item, at, Error);
                    scenarios.Add(new Scenario(name ?? at, Text(item, "description") ?? "", instances, steps));
                }
            }
            if (errors.Count > 0)
                throw new ScenarioException(errors);
            return new ScenarioFile(fileName, type!, scenarios);
        }
    }

    public static Duration ParseDuration(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var cycles) && cycles >= 0)
            return new Duration(cycles, null);
        if (value.ValueKind == JsonValueKind.String && DurationPattern().Match(value.GetString()!) is { Success: true } match)
        {
            var number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return match.Groups[2].Value switch
            {
                "ms" => new Duration(null, number / 1000),
                "s" => new Duration(null, number),
                "min" => new Duration(null, number * 60),
                _ => new Duration((int)number, null)
            };
        }
        throw new FormatException("A duration is a number of cycles, or a text such as \"2 s\", \"500 ms\", \"3 min\" or \"10 cycles\".");
    }

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*(ms|s|min|cycles?)\s*$")]
    private static partial Regex DurationPattern();

    private static List<ScenarioInstance> Instances(JsonElement scenario, string type, string at, Action<string, string> error)
    {
        if (!scenario.TryGetProperty("instances", out var list))
            return [new ScenarioInstance("CM", type, [])];
        var result = new List<ScenarioInstance>();
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            error($"{at}.instances", "'instances' must be a non-empty list.");
            return result;
        }
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var location = $"{at}.instances[{index++}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                error(location, "An instance must be an object with name and type.");
                continue;
            }
            Unknown(item, InstanceProperties, location, error);
            var name = Text(item, "name");
            var instanceType = Text(item, "type") ?? type;
            if (string.IsNullOrWhiteSpace(name))
                error($"{location}.name", "'name' is required.");
            var optional = item.TryGetProperty("optionalTags", out var o) && o.ValueKind == JsonValueKind.Array
                ? o.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : [];
            var wires = new List<ScenarioWire>();
            if (item.TryGetProperty("wires", out var wireList))
            {
                if (wireList.ValueKind != JsonValueKind.Array)
                    error($"{location}.wires", "'wires' must be a list of { source, mode, command }.");
                else
                {
                    var w = 0;
                    foreach (var wire in wireList.EnumerateArray())
                    {
                        var wireAt = $"{location}.wires[{w++}]";
                        var source = wire.ValueKind == JsonValueKind.Object ? Text(wire, "source") : null;
                        var mode = wire.ValueKind == JsonValueKind.Object ? Text(wire, "mode") : null;
                        if (source is null || mode is null)
                            error(wireAt, "A wire needs 'source' (a Bool tag, for example BTN.INT.pressed) and 'mode'.");
                        else
                            wires.Add(new ScenarioWire(source, mode, Text(wire, "command")));
                    }
                }
            }
            JsonElement? pic = item.TryGetProperty("pic", out var picElement) ? picElement.Clone() : null;
            JsonElement? inputs = item.TryGetProperty("commandInputs", out var inputElement) ? inputElement.Clone() : null;
            result.Add(new ScenarioInstance(name ?? location, instanceType, optional, wires, pic, inputs, Text(item, "parent"), Text(item, "role")));
        }
        return result;
    }

    private static List<ScenarioStep> Steps(JsonElement scenario, string at, Action<string, string> error)
    {
        var steps = new List<ScenarioStep>();
        if (!scenario.TryGetProperty("steps", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            error($"{at}.steps", "'steps' must be a list.");
            return steps;
        }
        var index = 0;
        foreach (var step in list.EnumerateArray())
        {
            var location = $"{at}.steps[{index++}]";
            if (step.ValueKind != JsonValueKind.Object)
            {
                error(location, "A step must be an object.");
                continue;
            }
            var kinds = step.EnumerateObject().Select(p => p.Name).ToList();
            foreach (var unknown in kinds.Where(k => !StepKinds.Contains(k)))
                error($"{location}.{unknown}", $"Unknown step '{unknown}'. Use {string.Join(", ", StepKinds)}.");
            var main = kinds.Where(k => k is not ("note" or "within")).ToList();
            if (main.Count != 1)
            {
                error(location, "A step has exactly one of: set, force, release, bad, good, run, until, expect.");
                continue;
            }
            var value = step.GetProperty(main[0]);
            switch (main[0])
            {
                case "set" or "force":
                    if (value.ValueKind != JsonValueKind.Object)
                        error($"{location}.{main[0]}", "Must be an object of tag → value.");
                    else
                        steps.Add(new SetStep(value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()), main[0] == "force", location));
                    break;
                case "release" or "bad" or "good":
                    if (TagList(value) is { } tags)
                        steps.Add(main[0] == "release" ? new ReleaseStep(tags, location) : new QualityStep(tags, main[0] == "bad", location));
                    else
                        error($"{location}.{main[0]}", "Must be a tag or a list of tags.");
                    break;
                case "run":
                    if (ReadDuration(value, location, "run", error) is { } cycles)
                        steps.Add(new RunStep(cycles, location));
                    break;
                case "until":
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        error($"{location}.until", "Must be a condition.");
                        break;
                    }
                    if (!step.TryGetProperty("within", out var within))
                    {
                        error($"{location}.within", "'until' needs 'within', for example \"10 s\".");
                        break;
                    }
                    if (ReadDuration(within, location, "within", error) is { } max)
                        steps.Add(new UntilStep(value.GetString()!, max, location));
                    break;
                case "expect":
                    if (TagList(value) is { } conditions)
                        steps.Add(new ExpectStep(conditions, location));
                    else
                        error($"{location}.expect", "Must be a condition or a list of conditions.");
                    break;
            }
        }
        return steps;
    }

    private static Duration? ReadDuration(JsonElement value, string location, string key, Action<string, string> error)
    {
        try
        {
            return ParseDuration(value);
        }
        catch (FormatException ex)
        {
            error($"{location}.{key}", ex.Message);
            return null;
        }
    }

    private static List<string>? TagList(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Array when value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String) =>
            value.EnumerateArray().Select(e => e.GetString()!).ToList(),
        _ => null
    };

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Unknown(JsonElement element, HashSet<string> allowed, string at, Action<string, string> error)
    {
        foreach (var property in element.EnumerateObject().Where(p => !allowed.Contains(p.Name)))
            error(at.Length == 0 ? property.Name : $"{at}.{property.Name}", $"Unknown property '{property.Name}'.");
    }
}
