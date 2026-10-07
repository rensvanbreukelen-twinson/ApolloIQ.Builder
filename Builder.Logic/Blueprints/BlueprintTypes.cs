using System.Globalization;
using System.Text.Json.Nodes;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;

namespace Builder.Logic.Blueprints;

public static class BlueprintTypes
{
    public const string PreviousState = "bp_prev_state";

    public const string UnitOverrideAlarm = "ManualByOverride";

    public static CmType ToCmType(Blueprint blueprint)
    {
        BlueprintCatalog.AssignCodes(blueprint);
        var boolInputs = blueprint.Tags.Where(t => t.Group == "FIN" && t.DataType == "Bool").Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = new List<TagTemplate>();
        var unit = blueprint.Kind != BlueprintKind.CM;
        var roles = unit ? blueprint.Roles.Select(r => r.Name).Where(n => n.Length > 0).OrderByDescending(n => n.Length).ToList() : [];
        var rolePattern = roles.Count == 0 ? null
            : new System.Text.RegularExpressions.Regex(@"(?<![\w.\[\]])(" + string.Join("|", roles.Select(System.Text.RegularExpressions.Regex.Escape)) + @")\.([A-Za-z_]\w*(?:\.[A-Za-z_]\w*){0,6})");
        string E(string expression) => rolePattern is null ? expression : rolePattern.Replace(expression, m => RoleReference(m.Groups[1].Value, m.Groups[2].Value));
        bool Member(string tag) => unit && tag.Split('.').Length > 2;
        string Write(string tag, string condition, string value, string keep, bool safeState = false) => Member(tag)
            ? safeState ? $"{condition} AND ({E(value)})" : $"{condition} AND STS.auto AND ({E(value)})"
            : $"SEL({condition}, {keep}, {E(value)})";
        string Target(string tag) => Member(tag) ? $"@{tag}" : tag;

        foreach (var tag in BlueprintCatalog.InterfaceTags(blueprint).Where(t => !BaseBehaviour.IsReserved(Group(t.Group), t.Name)))
        {
            var initialValue = tag.Initial is { } text ? Initial(text, tag.DataType) : tag.Group == "SET" && tag.DataType == "Bool" ? JsonValue.Create(false) : null;
            tags.Add(Template(tag.Name, tag.Group, tag.DataType, tag.Description, null, initialValue, TagSource.Internal));
        }
        foreach (var tag in blueprint.Tags)
        {
            if (tag.Group == "INT" && boolInputs.Contains(tag.Name))
                continue;
            var source = tag.Group != "FIN" ? TagSource.Internal : tag.Source == InputSource.External ? TagSource.Controller : TagSource.Hardwired;
            tags.Add(Template(tag.Name, tag.Group, tag.DataType, tag.Description, tag.Unit, Initial(tag.Initial, tag.DataType), source));
        }

        var times = new Dictionary<string, string>();
        foreach (var state in blueprint.States.Where(s => s.Timeout is not null))
        {
            var time = state.Timeout!.Time.Trim();
            if (double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                tags.Add(Template($"{state.Name}_timeout", "PAR", "Real", $"Timeout of state {state.Name}", "s", JsonValue.Create(seconds), TagSource.Internal));
                time = $"PAR.{state.Name}_timeout";
            }
            times[state.Name] = time;
        }
        tags.Add(Template(PreviousState, "INT", "Int", "State at the end of the previous cycle (entry, run and exit actions)", null, JsonValue.Create(-1), TagSource.Internal));

        var transitions = new JsonArray();
        var inputs = blueprint.Tags.Where(t => t.Group == "FIN" && t.DataType == "Bool").Select(t => $"GOOD(FIN.{t.Name})").ToList();
        var healthy = inputs.Count == 0 ? "STS.enabled" : $"STS.enabled AND {string.Join(" AND ", inputs)}";
        var initial = blueprint.States.FirstOrDefault(s => s.Initial) ?? blueprint.States.FirstOrDefault();
        transitions.Add(Transition("unavailable", ["*"], "Unavailable", -1, $"NOT ({healthy})"));
        if (initial is not null)
            transitions.Add(Transition("available_again", ["Unavailable"], initial.Name, 0, healthy));
        if (unit && blueprint.States.FirstOrDefault(s => UniversalStates.IsFault(s.Category)) is { } safe)
            transitions.Add(Transition(EscalatedTransition, ["*"], safe.Name, -1, "INT.escalated"));
        foreach (var t in blueprint.Transitions)
            transitions.Add(Transition(t.Name, t.From, t.To, t.Priority, E(t.Guard)));
        foreach (var state in blueprint.States.Where(s => !string.IsNullOrWhiteSpace(s.Timeout?.GoTo)))
        {
            var own = blueprint.Transitions.Where(t => t.From.Contains(state.Name, StringComparer.OrdinalIgnoreCase) || t.From.Contains(BlueprintCatalog.AnyState)).Select(t => t.Priority);
            transitions.Add(Transition(TimeoutName(state), [state.Name], state.Timeout!.GoTo!, own.DefaultIfEmpty(0).Max() + 1, $"STATE_TIME() > {times[state.Name]}"));
        }

        var after = new JsonArray();
        foreach (var state in blueprint.States)
            foreach (var a in state.Exit)
                after.Add(Set(Target(a.Tag), Write(a.Tag, $"INT.{PreviousState} = {state.Code} AND STS.state <> {state.Code}", a.Value, a.Tag, UniversalStates.IsFault(state.Category))));
        foreach (var state in blueprint.States)
            foreach (var a in state.Entry)
                after.Add(Set(Target(a.Tag), Write(a.Tag, $"STS.state = {state.Code} AND INT.{PreviousState} <> {state.Code}", a.Value, a.Tag, UniversalStates.IsFault(state.Category))));
        foreach (var state in blueprint.States)
            foreach (var a in state.Run)
                after.Add(Set(Target(a.Tag), Write(a.Tag, $"STS.state = {state.Code}", a.Value, a.Tag, UniversalStates.IsFault(state.Category))));
        after.Add(Set($"INT.{PreviousState}", "STS.state"));

        var before = new JsonArray();
        foreach (var a in blueprint.Always.Where(a => !IsConditioning(a, boolInputs)))
            before.Add(Member(a.Tag) ? Set(Target(a.Tag), $"STS.auto AND ({E(a.Value)})") : Set(a.Tag, E(a.Value)));
        var plant = new JsonArray();
        foreach (var a in blueprint.Plant)
            plant.Add(Set(a.Tag, a.Value));

        var resettable = blueprint.Interfaces.Contains("Resettable");
        var alarms = blueprint.Alarms.Select(a => Alarm(a.Name, a.Severity, a.Message,
            string.IsNullOrWhiteSpace(a.Condition) ? "TRUE" : E(a.Condition), a.Latched && resettable ? "TRUE" : "FALSE",
            string.IsNullOrWhiteSpace(a.OnTransition) ? null : a.OnTransition)).ToList();
        foreach (var state in blueprint.States.Where(s => !string.IsNullOrWhiteSpace(s.Timeout?.Alarm)))
        {
            var timeout = state.Timeout!;
            alarms.Add(string.IsNullOrWhiteSpace(timeout.GoTo)
                ? Alarm(timeout.Alarm!, timeout.Severity, "", $"STS.state = {state.Code} AND STATE_TIME() > {times[state.Name]}", "FALSE", null)
                : Alarm(timeout.Alarm!, timeout.Severity, "", "TRUE", resettable ? "TRUE" : "FALSE", TimeoutName(state)));
        }

        var interlocks = InterlockRule.WithAlarmNames(blueprint.Interlocks).Select(r =>
        {
            var copy = r.Clone();
            copy.Target = r.Target.Trim();
            copy.TargetId = null;
            copy.Condition = E(r.Condition);
            return copy;
        }).ToList();
        foreach (var trip in interlocks.Where(r => r.Kind == InterlockKind.Trip))
            alarms.Add(InterlockRule.TripAlarm(trip, string.IsNullOrWhiteSpace(trip.Target) ? $"Trip: {trip.Alarm}" : $"{trip.Target} tripped"));

        if (unit)
            tags.AddRange(BaseBehaviour.AlarmTags(new AlarmDefinition(UnitOverrideAlarm, 20,
                new Dictionary<string, string> { ["en"] = "Switched to manual by an override on a member" }, "FALSE", "FALSE", true, null, [])));
        var states = blueprint.States.Select(s => new StateDefinition(s.Code, s.Name)).Append(new StateDefinition(UniversalStates.UnavailableCode, "Unavailable")).OrderBy(s => s.Code).ToList();
        return new CmType
        {
            Name = blueprint.Name,
            Version = blueprint.Version,
            Description = blueprint.Description,
            Tags = tags,
            Aliases = new Dictionary<string, string>(blueprint.Aliases, StringComparer.OrdinalIgnoreCase),
            Alarms = alarms,
            Interlocks = interlocks,
            StateList = states,
            ExactStates = true,
            InitialState = initial?.Code ?? 0,
            Blueprint = Blueprint.SchemaId,
            DefaultCommandInputs = blueprint.CommandInputs is { Rows.Count: > 0 } defaults ? defaults : null,
            SingleCommands = SingleCommands(blueprint),
            IsUnit = unit,
            IsEquipmentModule = blueprint.Kind == BlueprintKind.EM,
            Roles = blueprint.Roles.Where(r => r.Name.Length > 0).ToDictionary(r => r.Name, r => r.Blueprint, StringComparer.Ordinal),
            Logic = new JsonObject
            {
                ["plant"] = plant,
                ["before"] = before,
                ["stateMachine"] = new JsonObject { ["transitions"] = transitions },
                ["after"] = after
            }
        };
    }

    public static IReadOnlyList<string> SingleCommands(Blueprint blueprint) =>
        BlueprintCatalog.InterfaceTags(blueprint).Where(t => t.Group == "CMD").Select(t => t.Name)
            .Concat(blueprint.Tags.Where(t => t.Group == "CMD" && t.DataType == "Bool").Select(t => t.Name))
            .Where(n => n is not ("set_on" or "set_off")).Distinct().ToList();

    public const string EscalatedTransition = "escalated";

    private static readonly HashSet<string> GroupCodes = new(StringComparer.Ordinal) { "FIN", "CMD", "OUT", "LOK", "PAR", "SET", "PMT", "STS", "INT", "ALM" };

    /// <summary>ROLE.SUB.GROUP.tag → [{role:ROLE.SUB}.GROUP.tag]: segments before a tag group (or before the last, an alias) are sub-roles.</summary>
    private static string RoleReference(string role, string rest)
    {
        var segments = rest.Split('.');
        var path = role;
        var i = 0;
        while (i < segments.Length - 1 && !GroupCodes.Contains(segments[i]))
            path += "." + segments[i++];
        return $"[{{role:{path}}}.{string.Join('.', segments[i..])}]";
    }

    private static string TimeoutName(BlueprintState state) => $"timeout_{state.Name}";

    private static bool IsConditioning(BlueprintAction action, HashSet<string> boolInputs) =>
        action.Tag.StartsWith("INT.", StringComparison.OrdinalIgnoreCase) && boolInputs.Contains(action.Tag[4..])
        && string.Equals(action.Value.Trim(), $"FIN.{action.Tag[4..]}", StringComparison.OrdinalIgnoreCase);

    private static JsonObject Set(string tag, string expression) => new() { ["set"] = tag, ["expr"] = expression };

    private static JsonObject Transition(string name, IEnumerable<string> from, string to, int priority, string guard) => new()
    {
        ["name"] = name,
        ["from"] = new JsonArray(from.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
        ["to"] = to,
        ["priority"] = priority,
        ["guard"] = guard
    };

    private static AlarmDefinition Alarm(string name, int severity, string message, string condition, string latch, string? onTransition) =>
        new(name, severity, new Dictionary<string, string> { ["en"] = string.IsNullOrWhiteSpace(message) ? name : message }, condition, latch, true, null, [], onTransition);

    private static TagGroup Group(string group) => Enum.Parse<TagGroup>(group, ignoreCase: true);

    private static TagTemplate Template(string name, string group, string dataType, string description, string? unit, JsonNode? initial, TagSource source)
    {
        var g = Group(group);
        var direction = g switch
        {
            TagGroup.Fin or TagGroup.Cmd or TagGroup.Lok => TagDirection.In,
            TagGroup.Par or TagGroup.Set => TagDirection.InOut,
            _ => TagDirection.Out
        };
        var type = dataType switch { "Bool" => TagDataType.Bool, "Int" => TagDataType.Int32, "String" => TagDataType.String, _ => TagDataType.Real };
        return new TagTemplate(name, g, type, direction, g == TagGroup.Fin ? TagKind.External : TagKind.Internal, source, false, initial, null, unit, description);
    }

    private static JsonNode? Initial(string? text, string dataType)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return dataType switch
        {
            "Bool" => JsonValue.Create(text.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase) || text.Trim() == "1"),
            "String" => JsonValue.Create(text),
            _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? dataType == "Int" ? JsonValue.Create((long)Math.Round(v)) : JsonValue.Create(v)
                : null
        };
    }
}
