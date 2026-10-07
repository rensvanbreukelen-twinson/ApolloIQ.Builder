using System.Text.Json.Nodes;
using ApolloIQ.Core.Alarms;
using ApolloIQ.Core.Blueprints;
using ApolloIQ.Core.Conventions;
using ApolloIQ.Core.Versioning;
using Builder.Core.Model;
using Builder.Logic.Blueprints;

namespace Builder.Design;

/// <summary>
/// A blueprint in the design format and back, one section at a time. A section that a fragment gives replaces that section of the
/// blueprint; ids (blueprint, tags, alarms, timeout and trip alarms) are kept by name, or by <c>renamedFrom</c>.
/// </summary>
public static class BlueprintDesign
{
    public const string General = "general";

    /// <summary>The sections of a blueprint, in the order they are applied (general first: kind and interfaces).</summary>
    public static readonly IReadOnlyList<string> Sections =
        [General, "roles", "tags", "states", "transitions", "always", "alarms", "interlocks", "aliases", "commandInputs", "plant"];

    public static string Title(string section) => section switch
    {
        General => "general fields and interfaces",
        "commandInputs" => "command inputs",
        "always" => "always actions",
        "plant" => "plant actions",
        _ => section
    };

    public static DesignBlueprint ToDesign(Blueprint blueprint, Func<Guid, string?> nameOf) => new()
    {
        Name = blueprint.Name,
        Kind = blueprint.Kind.ToString(),
        Version = blueprint.Version.ToString(),
        Description = Text(blueprint.Description),
        Interfaces = [.. blueprint.Interfaces],
        Roles = Map(blueprint.Roles.ToDictionary(r => r.Name, r => nameOf(r.BlueprintId) ?? r.BlueprintId.ToString())),
        Tags = Map(blueprint.Tags.Select(t => (t.Key, new DesignTag
        {
            Type = t.DataType,
            Description = Text(t.Description),
            Unit = Text(t.Unit),
            Min = t.Min,
            Max = t.Max,
            Initial = Text(t.Initial),
            Source = t.Source?.ToString(),
            Primary = t.Primary ? true : null
        }))),
        States = Map(blueprint.States.Select(s => (s.Name, new DesignState
        {
            Category = s.Category,
            Text = Text(s.Text),
            Description = Text(s.Description),
            Initial = s.Initial ? true : null,
            Entry = Actions(s.Entry),
            Run = Actions(s.Run),
            Exit = Actions(s.Exit),
            Timeout = s.Timeout is { } t
                ? new DesignTimeout
                {
                    Time = t.Time,
                    GoTo = Text(t.GoTo),
                    Alarm = Text(t.Alarm),
                    Priority = string.IsNullOrWhiteSpace(t.Alarm) || t.Priority == AlarmPriority.Default ? null : t.Priority,
                    Message = Text(t.Message)
                }
                : null
        }))),
        Transitions = Map(blueprint.Transitions.Select(t => (t.Name, new DesignTransition
        {
            From = [.. t.From],
            To = t.To,
            Guard = Text(t.Guard),
            Priority = t.Priority == 10 ? null : t.Priority
        }))),
        Always = Actions(blueprint.Always),
        Alarms = Map(blueprint.Alarms.Select(a => (a.Alarm.Name, ToDesign(a)))),
        Interlocks = blueprint.Interlocks.Count == 0 ? null : blueprint.Interlocks.Select(ToDesign).ToList(),
        Aliases = Map(blueprint.Aliases.Select(a => (a.Key, a.Value))),
        CommandInputs = CommandInputDesign.ToDesign(blueprint.CommandInputs),
        Plant = Actions(blueprint.Plant)
    };

    /// <summary>Only the name and one section of a design blueprint (null when the section is not given).</summary>
    public static DesignBlueprint? Section(DesignBlueprint design, string section)
    {
        var result = new DesignBlueprint { Name = design.Name };
        switch (section)
        {
            case General:
                if (design.Kind is null && design.Version is null && design.Description is null && design.Interfaces is null)
                    return null;
                result.Kind = design.Kind;
                result.Version = design.Version;
                result.Description = design.Description;
                result.Interfaces = design.Interfaces;
                return result;
            case "roles": return design.Roles is null ? null : Set(() => result.Roles = design.Roles);
            case "tags": return design.Tags is null ? null : Set(() => result.Tags = design.Tags);
            case "states": return design.States is null ? null : Set(() => result.States = design.States);
            case "transitions": return design.Transitions is null ? null : Set(() => result.Transitions = design.Transitions);
            case "always": return design.Always is null ? null : Set(() => result.Always = design.Always);
            case "alarms": return design.Alarms is null ? null : Set(() => result.Alarms = design.Alarms);
            case "interlocks": return design.Interlocks is null ? null : Set(() => result.Interlocks = design.Interlocks);
            case "aliases": return design.Aliases is null ? null : Set(() => result.Aliases = design.Aliases);
            case "commandInputs": return design.CommandInputs is null ? null : Set(() => result.CommandInputs = design.CommandInputs);
            case "plant": return design.Plant is null ? null : Set(() => result.Plant = design.Plant);
            default: throw new ArgumentException($"Unknown section {section}.");
        }

        DesignBlueprint Set(Action assign)
        {
            assign();
            return result;
        }
    }

    /// <summary>The section as it reads in a full design (empty sections included, so before and after compare).</summary>
    public static JsonNode SectionJson(DesignBlueprint full, string section)
    {
        var node = new JsonObject { ["name"] = full.Name };
        var json = DesignDocument.ToJson(full)!.AsObject();
        IEnumerable<string> keys = section == General ? ["kind", "version", "description", "interfaces"] : [section];
        foreach (var key in keys)
            node[key] = json[key]?.DeepClone() ?? (section == General ? null : Empty(section));
        if (section == General)
            foreach (var key in keys.Where(k => node[k] is null).ToList())
                node.Remove(key);
        return node;
    }

    private static JsonNode Empty(string section) => section is "interlocks" or "commandInputs" ? new JsonArray() : new JsonObject();

    /// <summary>A new blueprint made from a design (every section that is given).</summary>
    public static Blueprint Create(DesignBlueprint design, Guid id, Func<string, Guid?> idOf)
    {
        var blueprint = new Blueprint { Id = id, Name = design.Name, Interfaces = [BlueprintCatalog.Base] };
        foreach (var section in Sections)
            if (Section(design, section) is not null)
                Apply(blueprint, design, section, idOf);
        return blueprint;
    }

    /// <summary>Replaces one section of <paramref name="blueprint"/> by the design's. Throws <see cref="DesignException"/> on a bad value.</summary>
    public static void Apply(Blueprint blueprint, DesignBlueprint design, string section, Func<string, Guid?> idOf)
    {
        var where = $"Blueprint {design.Name}";
        switch (section)
        {
            case General:
                if (design.Kind is { } kind)
                    blueprint.Kind = Parse<BlueprintKind>(kind, $"{where}: kind");
                if (design.Version is { } version)
                    blueprint.Version = BlueprintVersion.TryParse(version, out var parsed)
                        ? parsed
                        : throw new DesignException($"{where}: version '{version}' is not release.major.minor, for example 0.1.0.");
                if (design.Description is { } description)
                    blueprint.Description = description.Trim();
                if (design.Interfaces is { } interfaces)
                    blueprint.Interfaces = [.. interfaces];
                break;
            case "roles":
                blueprint.Roles = design.Roles!.Select(r => new BlueprintRole
                {
                    Name = r.Key,
                    BlueprintId = idOf(r.Value) ?? throw new DesignException($"{where}: role {r.Key} needs blueprint '{r.Value}', which does not exist.")
                }).ToList();
                break;
            case "tags":
                blueprint.Tags = design.Tags!.Select(t => ToTag(blueprint, t.Key, t.Value, where)).ToList();
                break;
            case "states":
                blueprint.States = design.States!.Select(s => ToState(blueprint, s.Key, s.Value, where)).ToList();
                BlueprintRules.AssignCodes(blueprint);
                break;
            case "transitions":
                blueprint.Transitions = design.Transitions!.Select(t => new BlueprintTransition
                {
                    Name = t.Key,
                    From = t.Value.From ?? [],
                    To = t.Value.To ?? "",
                    Guard = t.Value.Guard ?? "",
                    Priority = t.Value.Priority ?? 10
                }).ToList();
                break;
            case "always":
                blueprint.Always = ToActions(design.Always);
                break;
            case "plant":
                blueprint.Plant = ToActions(design.Plant);
                break;
            case "alarms":
                blueprint.Alarms = design.Alarms!.Select(a => ToAlarm(blueprint, a.Key, a.Value, where)).ToList();
                break;
            case "interlocks":
                blueprint.Interlocks = design.Interlocks!.Select((line, i) =>
                {
                    var rule = ToRule(line, $"{where}: interlock {i + 1}");
                    rule.Target = line.Target?.Trim() ?? "";
                    rule.AlarmId = rule.Kind == InterlockKind.Trip
                        ? blueprint.Interlocks.FirstOrDefault(r => r.Kind == InterlockKind.Trip && string.Equals(r.Alarm, rule.Alarm, StringComparison.OrdinalIgnoreCase))?.AlarmId
                        : null;
                    return rule;
                }).ToList();
                break;
            case "aliases":
                blueprint.Aliases = new Dictionary<string, string>(design.Aliases!);
                break;
            case "commandInputs":
                blueprint.CommandInputs = CommandInputDesign.FromDesign(design.CommandInputs, where);
                break;
            default:
                throw new ArgumentException($"Unknown section {section}.");
        }
    }

    public static DesignInterlock ToDesign(InterlockRule rule) => new()
    {
        Target = Text(rule.Target),
        Kind = rule.Kind.ToString(),
        Condition = rule.Condition,
        Text = Text(rule.Text),
        Alarm = rule.Kind == InterlockKind.Trip ? Text(rule.Alarm) : null,
        Priority = rule.Kind == InterlockKind.Trip && rule.Priority != AlarmPriority.Default ? rule.Priority : null,
        Escalate = rule.Kind == InterlockKind.Trip && rule.Escalate != TripEscalation.None ? rule.Escalate.ToString() : null
    };

    /// <summary>An interlock line of the design as a rule (target and ids are left to the caller).</summary>
    public static InterlockRule ToRule(DesignInterlock line, string where)
    {
        var kind = Parse<InterlockKind>(line.Kind ?? "", $"{where}: kind");
        if (string.IsNullOrWhiteSpace(line.Condition))
            throw new DesignException($"{where} has no condition.");
        return new InterlockRule
        {
            Kind = kind,
            Condition = line.Condition.Trim(),
            Text = line.Text?.Trim() ?? "",
            Alarm = kind == InterlockKind.Trip && !string.IsNullOrWhiteSpace(line.Alarm) ? line.Alarm.Trim() : null,
            Priority = line.Priority ?? AlarmPriority.Default,
            Escalate = kind == InterlockKind.Trip && !string.IsNullOrWhiteSpace(line.Escalate) ? Parse<TripEscalation>(line.Escalate, $"{where}: escalate") : TripEscalation.None
        };
    }

    private static DesignAlarm ToDesign(BlueprintAlarm alarm)
    {
        var a = alarm.Alarm;
        return new DesignAlarm
        {
            Priority = a.Priority,
            Message = Text(a.Message),
            Trigger = a.Trigger.ToString(),
            PlcReactive = a.PlcReactive ? true : null,
            Condition = Text(a.Condition),
            Input = Text(a.Input),
            HighCaution = Text(a.HighCaution),
            HighWarning = Text(a.HighWarning),
            HighAlarm = Text(a.HighAlarm),
            LowCaution = Text(a.LowCaution),
            LowWarning = Text(a.LowWarning),
            LowAlarm = Text(a.LowAlarm),
            TimeoutMode = a.TimeoutMode == TimeoutMode.Running ? null : a.TimeoutMode.ToString(),
            Running = Text(a.Running),
            TriggerExpr = Text(a.TriggerExpr),
            Stop = Text(a.Stop),
            Timeout = Text(a.Timeout),
            OnDelay = a.OnDelaySeconds > 0 ? a.OnDelaySeconds : null,
            Latched = alarm.Latched ? true : null,
            OnTransition = Text(alarm.OnTransition)
        };
    }

    private static BlueprintAlarm ToAlarm(Blueprint blueprint, string name, DesignAlarm design, string where)
    {
        var at = $"{where}: alarm {name}";
        var previous = blueprint.Alarms.FirstOrDefault(a => a.Alarm.Name == name)
                       ?? (design.RenamedFrom is { } old ? blueprint.Alarms.FirstOrDefault(a => a.Alarm.Name == old) : null);
        return new BlueprintAlarm
        {
            Alarm = new AlarmDefinition
            {
                Id = previous?.Alarm.Id ?? Guid.Empty,
                Name = name,
                Priority = design.Priority ?? AlarmPriority.Default,
                Message = design.Message ?? "",
                Trigger = design.Trigger is { } trigger ? Parse<AlarmTrigger>(trigger, $"{at}: trigger") : AlarmTrigger.State,
                PlcReactive = design.PlcReactive ?? false,
                Condition = design.Condition ?? "",
                Input = design.Input ?? "",
                HighCaution = design.HighCaution ?? "",
                HighWarning = design.HighWarning ?? "",
                HighAlarm = design.HighAlarm ?? "",
                LowCaution = design.LowCaution ?? "",
                LowWarning = design.LowWarning ?? "",
                LowAlarm = design.LowAlarm ?? "",
                TimeoutMode = design.TimeoutMode is { } mode ? Parse<TimeoutMode>(mode, $"{at}: timeoutMode") : TimeoutMode.Running,
                Running = design.Running ?? "",
                TriggerExpr = design.TriggerExpr ?? "",
                Stop = design.Stop ?? "",
                Timeout = design.Timeout ?? "",
                OnDelaySeconds = design.OnDelay ?? 0
            },
            Latched = design.Latched ?? false,
            OnTransition = Text(design.OnTransition)
        };
    }

    private static BlueprintTag ToTag(Blueprint blueprint, string key, DesignTag design, string where)
    {
        var dot = key.IndexOf('.');
        if (dot <= 0 || dot == key.Length - 1)
            throw new DesignException($"{where}: tag '{key}' must be GROUP.name, for example FIN.running.");
        var group = key[..dot].ToUpperInvariant();
        var previous = blueprint.Tags.FirstOrDefault(t => string.Equals(t.Key, $"{group}.{key[(dot + 1)..]}", StringComparison.Ordinal))
                       ?? (design.RenamedFrom is { } old ? blueprint.Tags.FirstOrDefault(t => string.Equals(t.Key, old, StringComparison.OrdinalIgnoreCase)) : null);
        var type = design.Type ?? "Bool";
        var known = BlueprintRules.DataTypes.FirstOrDefault(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));
        return new BlueprintTag
        {
            Id = previous?.Id ?? Guid.Empty,
            Group = group,
            Name = key[(dot + 1)..],
            DataType = known ?? type,
            Description = design.Description?.Trim() ?? "",
            Unit = Text(design.Unit),
            Min = design.Min,
            Max = design.Max,
            Initial = Text(design.Initial),
            Source = design.Source is { } source ? Parse<InputSource>(source, $"{where}: tag {key}: source") : null,
            Primary = design.Primary ?? false
        };
    }

    private static BlueprintState ToState(Blueprint blueprint, string name, DesignState design, string where)
    {
        var previous = blueprint.States.FirstOrDefault(s => s.Name == name);
        var category = design.Category ?? previous?.Category
                       ?? throw new DesignException($"{where}: state {name} needs a category (Stopped 0, Stopping 100, Available 200, Starting 300, Running 400, Shutdown 500).");
        BlueprintTimeout? timeout = null;
        if (design.Timeout is { } t)
        {
            var alarm = Text(t.Alarm);
            timeout = new BlueprintTimeout
            {
                Time = t.Time ?? "10",
                GoTo = Text(t.GoTo),
                Alarm = alarm,
                AlarmId = alarm is not null && previous?.Timeout is { } old && string.Equals(old.Alarm, alarm, StringComparison.Ordinal) ? old.AlarmId : null,
                Priority = t.Priority ?? AlarmPriority.Default,
                Message = Text(t.Message)
            };
        }
        return new BlueprintState
        {
            Name = name,
            Category = category,
            Text = Text(design.Text),
            Description = design.Description?.Trim() ?? "",
            Initial = design.Initial ?? false,
            Entry = ToActions(design.Entry),
            Run = ToActions(design.Run),
            Exit = ToActions(design.Exit),
            Timeout = timeout
        };
    }

    private static List<BlueprintAction> ToActions(Dictionary<string, string>? actions) =>
        (actions ?? []).Select(a => new BlueprintAction { Tag = a.Key.Trim(), Value = a.Value }).ToList();

    private static Dictionary<string, string>? Actions(List<BlueprintAction> actions)
    {
        if (actions.Count == 0)
            return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var action in actions)
            result[action.Tag] = action.Value;
        return result;
    }

    private static Dictionary<string, T>? Map<T>(IEnumerable<(string Key, T Value)> items)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
            result[key] = value;
        return result.Count == 0 ? null : result;
    }

    private static Dictionary<string, T>? Map<T>(Dictionary<string, T> items) => items.Count == 0 ? null : items;

    internal static string? Text(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    internal static T Parse<T>(string text, string where) where T : struct, Enum =>
        Enum.TryParse<T>(text.Trim(), ignoreCase: true, out var value) && Enum.IsDefined(value) && !int.TryParse(text, out _)
            ? value
            : throw new DesignException($"{where}: unknown value '{text}'. Use {string.Join(", ", Enum.GetNames<T>())}.");
}

/// <summary>Command inputs in the design format: the rows, with defaults left out.</summary>
public static class CommandInputDesign
{
    public static DesignCommandInputs? ToDesign(CommandInputConfig? config)
    {
        if (config is null || (config.Rows.Count == 0 && !config.Level && config.Selector is null))
            return null;
        return new DesignCommandInputs
        {
            Rows = config.Rows.Select(r => new DesignCommandInput
            {
                Name = r.Name,
                Source = r.Source.ToString(),
                Kind = r.Kind.ToString(),
                Drives = r.Drives == InputDrives.OnOff ? null : r.Drives.ToString(),
                On = r.On,
                Off = r.Off,
                InAuto = r.InAuto == InputInAuto.Always ? null : r.InAuto.ToString(),
                Location = r.Location == InputLocation.Any ? null : r.Location.ToString(),
                Commands = r.SingleCommands.Count == 0 ? null : [.. r.SingleCommands],
                Debounce = r.Debounce,
                StuckTime = r.StuckTime
            }).ToList(),
            Level = config.Level ? true : null,
            Selector = config.Selector
        };
    }

    public static CommandInputConfig? FromDesign(DesignCommandInputs? design, string where)
    {
        if (design is null || (design.Rows.Count == 0 && design.Level != true && design.Selector is null))
            return null;
        return new CommandInputConfig(design.Rows.Select(r =>
        {
            var at = $"{where}: command input {r.Name}";
            return new CommandInput(r.Name,
                BlueprintDesign.Parse<CommandSource>(r.Source ?? nameof(CommandSource.Hmi), $"{at}: source"),
                BlueprintDesign.Parse<InputKind>(r.Kind ?? nameof(InputKind.Pulse), $"{at}: kind"),
                r.Drives is { } drives ? BlueprintDesign.Parse<InputDrives>(drives, $"{at}: drives") : InputDrives.OnOff,
                r.On, r.Off,
                r.InAuto is { } inAuto ? BlueprintDesign.Parse<InputInAuto>(inAuto, $"{at}: inAuto") : InputInAuto.Always,
                r.Location is { } location ? BlueprintDesign.Parse<InputLocation>(location, $"{at}: location") : InputLocation.Any,
                r.Commands is { Count: > 0 } commands ? [.. commands] : null,
                r.Debounce, r.StuckTime);
        }).ToList(), design.Level ?? false, BlueprintDesign.Text(design.Selector));
    }
}
