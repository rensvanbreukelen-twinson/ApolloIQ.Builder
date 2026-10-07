using System.Text.Json;
using ApolloIQ.Core.Alarms;
using ApolloIQ.Core.Blueprints;
using ApolloIQ.Core.Exchange;
using ApolloIQ.Core.Expressions;
using ApolloIQ.Core.Identity;
using ApolloIQ.Core.Versioning;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Blueprints;
using CoreDataType = ApolloIQ.Core.Blueprints.TagDataType;
using TagDataType = Builder.Core.Tags.TagDataType;

namespace Builder.Persistence.Export;

/// <summary>One blueprint of an export and whether it changed since the last export.</summary>
public sealed record ExportBlueprintInfo(Guid Id, string Name, string Version, bool ChangedSinceLastExport, string? LastExportedVersion);

/// <summary>Errors block the export; warnings name what does not reach SCADA.</summary>
public sealed record ExportCheck(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<ExportBlueprintInfo> Blueprints)
{
    public bool Ok => Errors.Count == 0;
}

public sealed record ExportResult(ExchangeFile File, ExportCheck Check);

/// <summary>
/// Builds the Builder → SCADA exchange file (<see cref="ExchangeFile"/>, <c>apolloiq.exchange/1</c>) from a project and its blueprints.
/// <para>Per blueprint: its own tags (no OUT, no ALM, no interface tags), the tags of interfaces SCADA does not know (as ordinary
/// tags), the tags the PLC logic generates for it (state timeouts, input inversion, conditioned inputs) and the tags of its default
/// command inputs (the CMD tags the HMI writes, with labels); its named states; its alarms as SCADA runs them
/// (<see cref="AlarmRules.ForScada"/>); the interlock texts per status bit.</para>
/// <para>Per instance (parents first): id, blueprint, containing EM/Unit, device, and its own interlock texts and trip alarms when
/// project-level interlocks act on it.</para>
/// </summary>
public static class ExchangeExporter
{
    public const string Generator = "ApolloIQ.Builder";

    public const string InterfaceTagSpace = "apolloiq.builder.interface-tag";
    public const string GeneratedTagSpace = "apolloiq.builder.generated-tag";
    public const string CommandInputTagSpace = "apolloiq.builder.command-input-tag";

    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static string FileName(string projectName) =>
        string.Concat(projectName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ExchangeFile.FileSuffix;

    public static ExportResult Build(Guid projectId, string projectName, Project project, CmLibrary library, Func<Guid, Blueprint?> blueprints,
        ExportRecord? last = null, DateTimeOffset? now = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var file = new ExchangeFile
        {
            Source = new ExchangeSource { ProjectId = projectId, ProjectName = projectName, ExportedAt = now ?? DateTimeOffset.UtcNow, Generator = Generator }
        };

        var instances = Instances(project);
        var used = new List<(Blueprint Blueprint, CmType Type)>();
        foreach (var id in instances.Select(InstanceFactory.BlueprintIdOf).Distinct())
        {
            var blueprint = blueprints(id);
            var users = string.Join(", ", instances.Where(i => InstanceFactory.BlueprintIdOf(i) == id).Select(i => project.GetPath(i.Id)).Take(3));
            if (blueprint is null)
            {
                errors.Add($"Blueprint {id} (used by {users}) does not exist.");
                continue;
            }
            var issues = BlueprintValidator.Validate(blueprint, blueprints).Where(i => i.Severity == "Error").ToList();
            if (issues.Count > 0)
            {
                errors.AddRange(issues.Select(i => $"Blueprint {blueprint.Name}: {i.Where} — {i.Message}"));
                continue;
            }
            used.Add((blueprint, library.Find(id) ?? BlueprintTypes.ToCmType(blueprint.Clone())));
        }

        var infos = new List<ExportBlueprintInfo>();
        foreach (var (blueprint, type) in used.OrderBy(u => u.Blueprint.Name, StringComparer.OrdinalIgnoreCase))
        {
            var exchange = ToExchange(blueprint, type, warnings);
            exchange.Hash = ExchangeJson.Hash(exchange);
            file.Blueprints.Add(exchange);
            var previous = last?.Blueprints.GetValueOrDefault(blueprint.Id);
            var changed = previous is not null && previous.Hash != exchange.Hash;
            if (changed && previous!.Version == blueprint.Version)
                errors.Add($"{blueprint.Name} changed since the last export to SCADA; raise its version (now {blueprint.Version}).");
            if (previous is not null && blueprint.Version < previous.Version)
                warnings.Add($"{blueprint.Name}: version {blueprint.Version} is lower than the last exported version {previous.Version}.");
            infos.Add(new ExportBlueprintInfo(blueprint.Id, blueprint.Name, blueprint.Version.ToString(), changed, previous?.Version.ToString()));
        }

        var types = used.ToDictionary(u => u.Blueprint.Id, u => u);
        var paths = ScadaPaths(project, instances);
        foreach (var obj in instances)
        {
            if (!types.TryGetValue(InstanceFactory.BlueprintIdOf(obj), out var entry))
                continue;
            file.Instances.Add(ToInstance(project, library, obj, entry.Blueprint, entry.Type, paths, warnings));
        }
        return new ExportResult(file, new ExportCheck(errors, warnings, infos));
    }

    /// <summary>The CMs, EMs and Units of the project, parents before children (folders are not instances).</summary>
    public static IReadOnlyList<ProjectObject> Instances(Project project)
    {
        var result = new List<ProjectObject>();
        void Walk(Guid? parent)
        {
            foreach (var child in project.GetChildren(parent).Where(c => c is not Tag).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (child is ControlModule or UnitInstance)
                    result.Add(child);
                Walk(child.Id);
            }
        }
        Walk(null);
        return result;
    }

    private static ExchangeBlueprint ToExchange(Blueprint blueprint, CmType type, List<string> warnings)
    {
        var interfaces = BlueprintRules.Interfaces(blueprint);
        var exchange = new ExchangeBlueprint
        {
            Id = blueprint.Id,
            Name = blueprint.Name,
            Kind = blueprint.Kind,
            Version = blueprint.Version,
            Description = blueprint.Description,
            Interfaces = interfaces.Where(i => i.InScada).Select(i => i.Name).ToList()
        };

        var own = blueprint.Tags.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
        var scadaInterfaceTags = interfaces.Where(i => i.InScada).SelectMany(i => i.Tags).Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var otherInterfaceTags = interfaces.Where(i => !i.InScada).SelectMany(i => i.Tags).ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(ExchangeTag tag)
        {
            if (added.Add($"{tag.Group}.{tag.Name}"))
                exchange.Tags.Add(tag);
        }

        foreach (var tag in blueprint.Tags.Where(t => t.Group != "OUT"))
            Add(new ExchangeTag { Id = tag.Id, Group = tag.Group, Name = tag.Name, DataType = Enum.Parse<CoreDataType>(tag.DataType), Description = tag.Description, Unit = tag.Unit });
        foreach (var template in type.ExpandTags())
        {
            var key = template.Key;
            if (template.Group is TagGroup.Out or TagGroup.Alm || scadaInterfaceTags.Contains(key) || own.ContainsKey(key)
                || key == $"INT.{BlueprintTypes.PreviousState}")
                continue;
            var id = otherInterfaceTags.ContainsKey(key)
                ? StableId.From(InterfaceTagSpace, blueprint.Id.ToString("D"), key)
                : StableId.From(GeneratedTagSpace, blueprint.Id.ToString("D"), key);
            Add(new ExchangeTag { Id = id, Group = template.Group.Code(), Name = template.Name, DataType = DataType(template.DataType), Description = template.Description, Unit = template.Unit });
        }

        var inputs = blueprint.CommandInputs ?? CommandInputConfig.Empty;
        foreach (var tag in CommandInputBehaviour.Tags(inputs).Where(t => t.Group is not (TagGroup.Out or TagGroup.Alm)))
        {
            var key = $"{tag.Group.Code()}.{tag.Name}";
            Add(new ExchangeTag { Id = StableId.From(CommandInputTagSpace, blueprint.Id.ToString("D"), key), Group = tag.Group.Code(), Name = tag.Name, DataType = DataType(tag.DataType), Description = tag.Description, Unit = tag.Unit });
        }
        foreach (var (command, label) in CommandLabels(inputs))
            exchange.CommandLabels[command] = label;

        BlueprintRules.AssignCodes(blueprint);
        exchange.States = blueprint.States.OrderBy(s => s.Code)
            .Select(s => new ExchangeState { Code = s.Code, Name = s.Name, Text = string.IsNullOrWhiteSpace(s.Text) ? null : s.Text.Trim(), Description = s.Description })
            .ToList();

        var own2 = InterlockRule.WithAlarmNames(blueprint.Interlocks).Where(r => r.Target.Trim().Length == 0).ToList();
        exchange.InterlockTexts = Texts(own2.Select(r => (r.Kind, string.IsNullOrWhiteSpace(r.Text) ? ConditionText.Generate(r.Condition) : r.Text)));

        var roles = blueprint.Roles.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var alarm in blueprint.Alarms)
        {
            if (!alarm.Alarm.PlcReactive && roles.Count > 0 && AlarmExpressions(alarm.Alarm).SelectMany(Expression.References).Any(r => roles.Contains(r.Split('.')[0])))
                warnings.Add($"{blueprint.Name}: SCADA alarm {alarm.Alarm.Name} refers to member roles; SCADA resolves names relative to the instance, so make it PLC reactive or use the member's own alarm.");
            exchange.Alarms.Add(AlarmRules.ForScada(alarm.Alarm));
        }
        foreach (var alarm in type.Alarms.Where(a => a.Source is AlarmSource.StateTimeout or AlarmSource.UnitOverride))
            exchange.Alarms.Add(AlarmRules.ForScada(alarm.Definition));
        foreach (var rule in InterlockRule.WithAlarmNames(blueprint.Interlocks).Where(r => r.Kind == InterlockKind.Trip))
        {
            var alarm = InterlockRule.TripAlarm(rule, string.IsNullOrWhiteSpace(rule.Target) ? $"{{instance_name}}: trip {rule.Alarm}" : $"{{instance_name}}: {rule.Target} tripped");
            if (alarm.Definition.Id == Guid.Empty)
                alarm.Definition.Id = StableId.From("apolloiq.builder.trip-alarm", blueprint.Id.ToString("D"), rule.Alarm!);
            exchange.Alarms.Add(AlarmRules.ForScada(alarm.Definition));
        }
        foreach (var row in inputs.Rows.Where(r => r.IsPhysical && r.StuckTime is not null))
            exchange.Alarms.Add(AlarmRules.ForScada(CommandInputBehaviour.StuckAlarm(row, blueprint.Id).Definition));
        return exchange;
    }

    /// <summary>
    /// Builder path → SCADA path of every instance. Builder paths include folders (<c>PMS.MAIN</c>); SCADA paths follow the
    /// containment tree only (<c>MAIN</c>, <c>PLANT.GROUP1.LAMP1</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ScadaPaths(Project project, IReadOnlyList<ProjectObject> instances)
    {
        var ids = instances.Select(i => i.Id).ToHashSet();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in instances)
        {
            var segments = new Stack<string>();
            for (ProjectObject? o = obj; o is not null; o = o.ParentId is { } p && project.Find(p) is { } parent && ids.Contains(parent.Id) ? parent : null)
                segments.Push(o.Name);
            result[project.GetPath(obj.Id)] = string.Join('.', segments);
        }
        return result;
    }

    /// <summary>Rewrites absolute references from Builder paths to SCADA paths (longest matching object path wins).</summary>
    public static string ToScadaReferences(string expression, IReadOnlyDictionary<string, string> paths) =>
        expression.Length == 0 ? expression : Expression.RewriteReferences(expression, reference =>
        {
            var best = paths.Keys
                .Where(p => reference.Equals(p, StringComparison.OrdinalIgnoreCase) || reference.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase))
                .MaxBy(p => p.Length);
            return best is null ? reference : paths[best] + reference[best.Length..];
        });

    private static ExchangeInstance ToInstance(Project project, CmLibrary library, ProjectObject obj, Blueprint blueprint, CmType type,
        IReadOnlyDictionary<string, string> paths, List<string> warnings)
    {
        var path = project.GetPath(obj.Id);
        var instance = new ExchangeInstance
        {
            Id = obj.Id,
            Name = obj.Name,
            BlueprintId = blueprint.Id,
            ParentId = obj.ParentId is { } parent && project.Find(parent) is UnitInstance ? parent : null,
            Device = obj is ControlModule { ExecutionDeviceId: { } device } ? project.Topology.Device(device)?.Name : null
        };

        var version = obj switch { ControlModule c => c.BlueprintVersion, UnitInstance u => u.BlueprintVersion, _ => blueprint.Version };
        if (version != blueprint.Version)
            warnings.Add($"{path} was made from {blueprint.Name} {version}; the export carries {blueprint.Version}.");

        var sources = InterlockSources.Targeting(project, library, obj.Id);
        if (sources.Any(s => !(s.FromBlueprint && s.Owner.Id == obj.Id)))
            instance.InterlockTexts = Texts(sources.Select(s => (s.Rule.Kind, string.IsNullOrWhiteSpace(s.Rule.Text)
                ? ConditionText.Generate(ToScadaReferences(InterlockDisplay.Condition(project, s), paths))
                : s.Rule.Text)));

        foreach (var rule in InterlockRule.WithAlarmNames(InterlockSources.OwnRules(obj)).Where(r => r.Kind == InterlockKind.Trip))
        {
            var alarm = InterlockRule.TripAlarm(rule, $"{{instance_name}}: trip {rule.Alarm}").Definition;
            alarm.Condition = ToScadaReferences(InterlockDisplay.Condition(project, new InterlockSource(obj, rule, false)), paths);
            if (alarm.Id == Guid.Empty)
                alarm.Id = StableId.From("apolloiq.builder.trip-alarm", obj.Id.ToString("D"), rule.Alarm!);
            instance.Alarms.Add(AlarmRules.ForScada(alarm));
        }

        if (obj is ControlModule cm)
        {
            foreach (var (alarm, priority) in cm.AlarmPriorities)
                warnings.Add($"{path}: alarm {alarm} has priority {priority} on this CM; the export carries the blueprint's priority " +
                             $"({type.Alarms.FirstOrDefault(a => a.Name == alarm)?.Definition.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}).");
        }
        var inputs = CommandInputBehaviour.InputsOf(obj) ?? CommandInputConfig.Empty;
        var own = inputs with { Rows = inputs.Rows.Where(r => r.Source != CommandSource.Unit).ToList() };
        var defaults = blueprint.CommandInputs ?? CommandInputConfig.Empty;
        if (JsonSerializer.Serialize(own, InputJson) != JsonSerializer.Serialize(defaults with { Rows = defaults.Rows.ToList() }, InputJson))
            warnings.Add($"{path}: its command inputs differ from the defaults of {blueprint.Name}; SCADA gets the blueprint's command tags and labels.");
        return instance;
    }

    private static ExchangeInterlockTexts Texts(IEnumerable<(InterlockKind Kind, string Text)> lines)
    {
        var texts = new ExchangeInterlockTexts();
        foreach (var (kind, text) in lines)
        {
            var list = kind switch
            {
                InterlockKind.SwitchOn => texts.SwitchOn,
                InterlockKind.SwitchOff => texts.SwitchOff,
                _ => null
            };
            if (list is not null && list.Count < InterlockRule.MaxPerKind)
                list.Add(new ExchangeInterlockText { Bit = list.Count, Text = text });
        }
        return texts;
    }

    /// <summary>HMI captions of the commands the default command inputs generate (CMD.HMI_on → "On").</summary>
    public static IReadOnlyDictionary<string, string> CommandLabels(CommandInputConfig inputs)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = inputs.Rows.Where(r => !r.IsPhysical).ToList();
        foreach (var row in rows)
        {
            var suffix = rows.Count(r => r.Source == row.Source) > 1 || row.Source != CommandSource.Hmi ? $" ({row.Name})" : "";
            if (row.GivesOn)
                labels[$"{row.Name}_on"] = "On" + suffix;
            if (row.GivesOff)
                labels[$"{row.Name}_off"] = "Off" + suffix;
            foreach (var command in row.SingleCommands)
                labels[$"{row.Name}_{command}"] = char.ToUpperInvariant(command[0]) + command[1..].Replace('_', ' ') + suffix;
        }
        return labels;
    }

    private static IEnumerable<string> AlarmExpressions(AlarmDefinition alarm) =>
        [alarm.Condition, alarm.Input, .. AlarmRules.RangeThresholds(alarm), alarm.Running, alarm.TriggerExpr, alarm.Stop, alarm.Timeout];

    private static CoreDataType DataType(TagDataType type) => type switch
    {
        TagDataType.Bool => CoreDataType.Bool,
        TagDataType.Int16 or TagDataType.Int32 or TagDataType.Enum => CoreDataType.Int,
        TagDataType.Real or TagDataType.LReal => CoreDataType.Real,
        _ => CoreDataType.String
    };
}
