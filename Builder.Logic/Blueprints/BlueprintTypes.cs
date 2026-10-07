using System.Globalization;
using System.Text.Json.Nodes;
using ApolloIQ.Core.Alarms;
using ApolloIQ.Core.Blueprints;
using ApolloIQ.Core.Conventions;
using ApolloIQ.Core.Expressions;
using ApolloIQ.Core.Identity;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using TagDataType = Builder.Core.Tags.TagDataType;
using CoreDataType = ApolloIQ.Core.Blueprints.TagDataType;

namespace Builder.Logic.Blueprints;

/// <summary>Turns a blueprint into the runtime type: tags, generated logic (C-style expressions), alarms and interlocks.</summary>
public static class BlueprintTypes
{
    public const string PreviousState = "bp_prev_state";

    public const string UnitOverrideAlarm = "ManualByOverride";

    public const string EscalatedTransition = "escalated";

    public const string UnavailableTransition = "unavailable";

    public const string AvailableTransition = "available_again";

    private static readonly HashSet<string> GroupCodes = new(BlueprintCatalog.ReservedNames, StringComparer.Ordinal);

    public static CmType ToCmType(Blueprint blueprint)
    {
        BlueprintRules.AssignCodes(blueprint);
        var boolInputs = blueprint.Tags.Where(t => t.Group == "FIN" && t.DataType == "Bool").Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = new List<TagTemplate>();
        var unit = blueprint.Kind != BlueprintKind.CM;
        var roles = unit ? blueprint.Roles.Select(r => r.Name).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal) : [];
        string E(string expression) => roles.Count == 0 ? expression : RoleReferences(expression, roles);
        bool Member(string tag) => unit && tag.Split('.').Length > 2;
        string Write(string tag, string condition, string value, bool safeState = false) => Member(tag)
            ? safeState ? $"{condition} && ({E(value)})" : $"{condition} && [STS.auto] && ({E(value)})"
            : $"SEL({condition}, [{tag}], ({E(value)}))";
        string Target(string tag) => Member(tag) ? $"@{tag}" : tag;

        foreach (var tag in BlueprintRules.InterfaceTags(blueprint).Where(t => !BaseBehaviour.IsReserved(Group(t.Group), t.Name)))
        {
            var dataType = tag.DataType.ToString();
            var initialValue = tag.Initial is { } text ? Initial(text, dataType) : tag.Group == "SET" && dataType == "Bool" ? JsonValue.Create(false) : null;
            tags.Add(Template(tag.Name, tag.Group, dataType, tag.Description, null, initialValue, TagSource.Internal));
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
                tags.Add(Template(TimeoutParameter(state), "PAR", "Real", $"Timeout of state {state.Name}", "s", JsonValue.Create(seconds), TagSource.Internal));
                time = $"[PAR.{TimeoutParameter(state)}]";
            }
            else
                time = $"({E(time)})";
            times[state.Name] = time;
        }
        tags.Add(Template(PreviousState, "INT", "Int", "State at the end of the previous cycle (entry, run and exit actions)", null, JsonValue.Create(-1), TagSource.Internal));

        var transitions = new List<Transition>();
        var inputs = blueprint.Tags.Where(t => t.Group == "FIN" && t.DataType == "Bool").Select(t => $"GOOD([FIN.{t.Name}])").ToList();
        var healthy = inputs.Count == 0 ? "[STS.enabled]" : $"[STS.enabled] && {string.Join(" && ", inputs)}";
        var initial = blueprint.States.FirstOrDefault(s => s.Initial) ?? blueprint.States.FirstOrDefault();
        transitions.Add(new([BlueprintRules.AnyState], UniversalStates.CategoryNamed("Unavailable")!.Name, -1, $"!({healthy})", UnavailableTransition, "generated"));
        if (initial is not null)
            transitions.Add(new(["Unavailable"], initial.Name, 0, healthy, AvailableTransition, "generated"));
        if (unit && blueprint.States.FirstOrDefault(s => UniversalStates.IsFault(s.Category)) is { } safe)
            transitions.Add(new([BlueprintRules.AnyState], safe.Name, -1, "[INT.escalated]", EscalatedTransition, "generated"));
        foreach (var t in blueprint.Transitions)
            transitions.Add(new(t.From, t.To, t.Priority, E(t.Guard), t.Name, $"transitions.{t.Name}"));
        foreach (var state in blueprint.States.Where(s => !string.IsNullOrWhiteSpace(s.Timeout?.GoTo)))
        {
            var own = blueprint.Transitions.Where(t => t.From.Contains(state.Name, StringComparer.OrdinalIgnoreCase) || t.From.Contains(BlueprintRules.AnyState)).Select(t => t.Priority);
            transitions.Add(new([state.Name], state.Timeout!.GoTo!, own.DefaultIfEmpty(0).Max() + 1, $"STATE_TIME() > {times[state.Name]}",
                TimeoutTransition(state), $"states.{state.Name}.timeout"));
        }

        var after = new List<AssignStep>();
        foreach (var state in blueprint.States)
            foreach (var (a, i) in state.Exit.Select((a, i) => (a, i)))
                after.Add(new(Target(a.Tag), Write(a.Tag, $"[INT.{PreviousState}] == {state.Code} && [STS.state] != {state.Code}", a.Value, UniversalStates.IsFault(state.Category)), $"states.{state.Name}.exit[{i}]"));
        foreach (var state in blueprint.States)
            foreach (var (a, i) in state.Entry.Select((a, i) => (a, i)))
                after.Add(new(Target(a.Tag), Write(a.Tag, $"[STS.state] == {state.Code} && [INT.{PreviousState}] != {state.Code}", a.Value, UniversalStates.IsFault(state.Category)), $"states.{state.Name}.entry[{i}]"));
        foreach (var state in blueprint.States)
            foreach (var (a, i) in state.Run.Select((a, i) => (a, i)))
                after.Add(new(Target(a.Tag), Write(a.Tag, $"[STS.state] == {state.Code}", a.Value, UniversalStates.IsFault(state.Category)), $"states.{state.Name}.run[{i}]"));
        after.Add(new($"INT.{PreviousState}", "[STS.state]", "generated"));

        var before = new List<AssignStep>();
        foreach (var (a, i) in blueprint.Always.Select((a, i) => (a, i)).Where(x => !IsConditioning(x.a, boolInputs)))
            before.Add(Member(a.Tag) ? new(Target(a.Tag), $"[STS.auto] && ({E(a.Value)})", $"always[{i}]") : new(a.Tag, E(a.Value), $"always[{i}]"));
        var plant = blueprint.Plant.Select((a, i) => new AssignStep(a.Tag, a.Value, $"plant[{i}]")).ToList();

        var resettable = BlueprintRules.Has(blueprint, BlueprintCatalog.Resettable);
        var alarms = new List<CmAlarm>();
        foreach (var alarm in blueprint.Alarms)
        {
            var definition = alarm.Alarm.Clone();
            definition.Condition = E(definition.Condition);
            definition.Input = E(definition.Input);
            definition.HighCaution = E(definition.HighCaution);
            definition.HighWarning = E(definition.HighWarning);
            definition.HighAlarm = E(definition.HighAlarm);
            definition.LowCaution = E(definition.LowCaution);
            definition.LowWarning = E(definition.LowWarning);
            definition.LowAlarm = E(definition.LowAlarm);
            definition.Running = E(definition.Running);
            definition.TriggerExpr = E(definition.TriggerExpr);
            definition.Stop = E(definition.Stop);
            definition.Timeout = E(definition.Timeout);
            var onTransition = definition.PlcReactive && !string.IsNullOrWhiteSpace(alarm.OnTransition) ? alarm.OnTransition : null;
            if (onTransition is not null && definition.Trigger == AlarmTrigger.State && string.IsNullOrWhiteSpace(definition.Condition))
                definition.Condition = "TRUE";
            alarms.Add(new CmAlarm(definition, AlarmSource.Blueprint, definition.PlcReactive && alarm.Latched && resettable, onTransition));
        }
        foreach (var state in blueprint.States.Where(s => !string.IsNullOrWhiteSpace(s.Timeout?.Alarm)))
            alarms.Add(TimeoutAlarm(blueprint, state, times[state.Name], resettable));

        var interlocks = InterlockRule.WithAlarmNames(blueprint.Interlocks).Select(r =>
        {
            var copy = r.Clone();
            copy.Target = r.Target.Trim();
            copy.TargetId = null;
            copy.Condition = E(r.Condition);
            if (copy.Kind == InterlockKind.Trip && (copy.AlarmId is null || copy.AlarmId == Guid.Empty))
                copy.AlarmId = StableId.From("apolloiq.builder.trip-alarm", blueprint.Id.ToString("D"), copy.Alarm!);
            return copy;
        }).ToList();
        foreach (var trip in interlocks.Where(r => r.Kind == InterlockKind.Trip))
            alarms.Add(InterlockRule.TripAlarm(trip, string.IsNullOrWhiteSpace(trip.Target) ? $"{{instance_name}}: trip {trip.Alarm}" : $"{{instance_name}}: {trip.Target} tripped"));
        if (unit)
            alarms.Add(new CmAlarm(new AlarmDefinition
            {
                Id = StableId.From("apolloiq.builder.generated-alarm", blueprint.Id.ToString("D"), UnitOverrideAlarm),
                Name = UnitOverrideAlarm,
                Priority = AlarmPriority.Typical(AlarmLevel.Warning),
                Message = "{instance_name}: switched to manual by an override on a member",
                Trigger = AlarmTrigger.State,
                PlcReactive = true
            }, AlarmSource.UnitOverride));

        return new CmType
        {
            Id = blueprint.Id,
            Name = blueprint.Name,
            Version = blueprint.Version,
            Kind = blueprint.Kind,
            Description = blueprint.Description,
            Tags = tags,
            Aliases = new Dictionary<string, string>(blueprint.Aliases, StringComparer.OrdinalIgnoreCase),
            Alarms = alarms,
            Interlocks = interlocks,
            ObjectStates = blueprint.States.Select(s => s.ToDefinition()).ToList(),
            InitialState = initial?.Code ?? 0,
            DefaultCommandInputs = blueprint.CommandInputs is { Rows.Count: > 0 } defaults ? defaults : null,
            SingleCommands = SingleCommands(blueprint),
            Roles = blueprint.Roles.Where(r => r.Name.Length > 0).ToDictionary(r => r.Name, r => r.BlueprintId, StringComparer.Ordinal),
            Logic = new LogicModel(plant, before, transitions, after)
        };
    }

    /// <summary>
    /// The alarm of a state timeout (PLC reactive). With a "go to" state it is raised on the timeout transition and latches
    /// when the blueprint is resettable; without one it is a timeout alarm: active while the state lasts longer than the time.
    /// </summary>
    private static CmAlarm TimeoutAlarm(Blueprint blueprint, BlueprintState state, string time, bool resettable)
    {
        var timeout = state.Timeout!;
        var definition = new AlarmDefinition
        {
            Id = timeout.AlarmId ?? StableId.From("apolloiq.builder.timeout-alarm", blueprint.Id.ToString("D"), state.Name),
            Name = timeout.Alarm!.Trim(),
            Priority = timeout.Priority,
            Message = string.IsNullOrWhiteSpace(timeout.Message)
                ? $"{{instance_name}}: {(string.IsNullOrWhiteSpace(state.Text) ? state.Name : state.Text)} took too long"
                : timeout.Message.Trim(),
            PlcReactive = true
        };
        if (!string.IsNullOrWhiteSpace(timeout.GoTo))
        {
            definition.Trigger = AlarmTrigger.State;
            definition.Condition = $"[STS.state] == {state.Name} && STATE_TIME() > {time}";
            return new CmAlarm(definition, AlarmSource.StateTimeout, resettable, TimeoutTransition(state));
        }
        definition.Trigger = AlarmTrigger.Timeout;
        definition.TimeoutMode = TimeoutMode.Running;
        definition.Running = $"[STS.state] == {state.Name}";
        definition.Condition = "FALSE";
        definition.Timeout = time;
        return new CmAlarm(definition, AlarmSource.StateTimeout);
    }

    public static string TimeoutParameter(BlueprintState state) => $"{state.Name}_timeout";

    public static string TimeoutTransition(BlueprintState state) => $"timeout_{state.Name}";

    public static IReadOnlyList<string> SingleCommands(Blueprint blueprint) =>
        BlueprintRules.InterfaceTags(blueprint).Where(t => t.Group == "CMD").Select(t => t.Name)
            .Concat(blueprint.Tags.Where(t => t.Group == "CMD" && t.DataType == "Bool").Select(t => t.Name))
            .Where(n => n is not ("set_on" or "set_off")).Distinct().ToList();

    /// <summary>
    /// <c>[ROLE.SUB.GROUP.tag]</c> → <c>[{role:ROLE.SUB}.GROUP.tag]</c>: the segments before a tag group (or before the last one,
    /// an alias) are roles. The runtime replaces <c>{role:…}</c> with the member's path.
    /// </summary>
    public static string RoleReferences(string expression, IReadOnlySet<string> roles) =>
        Expression.RewriteReferences(expression, reference =>
        {
            var segments = reference.Split('.');
            if (segments.Length < 2 || !roles.Contains(segments[0]))
                return reference;
            var i = 1;
            while (i < segments.Length - 1 && !GroupCodes.Contains(segments[i]))
                i++;
            return $"{InterlockDisplay.RolePrefix}{string.Join('.', segments[..i])}}}.{string.Join('.', segments[i..])}";
        });

    private static bool IsConditioning(BlueprintAction action, HashSet<string> boolInputs)
    {
        if (!action.Tag.StartsWith("INT.", StringComparison.OrdinalIgnoreCase) || !boolInputs.Contains(action.Tag[4..]))
            return false;
        var value = action.Value.Trim();
        return string.Equals(value, $"[FIN.{action.Tag[4..]}]", StringComparison.OrdinalIgnoreCase);
    }

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
        return new TagTemplate(name, g, DataType(dataType), direction, g == TagGroup.Fin ? TagKind.External : TagKind.Internal, source, initial, null, unit, description);
    }

    /// <summary>Blueprint data type (Bool, Int, Real, String) → tag data type of the project.</summary>
    public static TagDataType DataType(string dataType) => dataType switch
    {
        nameof(CoreDataType.Bool) => TagDataType.Bool,
        nameof(CoreDataType.Int) => TagDataType.Int32,
        nameof(CoreDataType.String) => TagDataType.String,
        _ => TagDataType.Real
    };

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
