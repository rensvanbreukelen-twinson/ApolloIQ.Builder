using System.Text.Json.Nodes;
using Builder.Core.Model;
using ApolloIQ.Core.Alarms;
using ApolloIQ.Core.Conventions;
using ApolloIQ.Core.Identity;
using Builder.Core.Tags;
using NameRules = Builder.Core.Model.NameRules;

namespace Builder.Core.Types;

public static class CommandInputBehaviour
{
    public const double HmiHoldTimeoutSeconds = 0.5;

    public static readonly IReadOnlyList<string> PairCommands = ["set_on", "set_off"];

    public static IReadOnlyList<string> Problems(CommandInputConfig config, IReadOnlyCollection<string> singleCommands, bool hasPair,
        Func<string, bool>? tagExists = null)
    {
        var problems = new List<string>();
        if (config.Rows.Count > CommandInputConfig.MaxRows)
            problems.Add($"At most {CommandInputConfig.MaxRows} command inputs.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in config.Rows)
        {
            var at = $"Input {row.Name}";
            if (NameRules.Check(row.Name, 20) is { } error)
                problems.Add($"{at}: {error}");
            else if (!names.Add(row.Name))
                problems.Add($"{at}: the name is used twice.");
            var kindFits = row.Source switch
            {
                CommandSource.Hmi => row.Kind is InputKind.Pulse or InputKind.Hold,
                CommandSource.Unit => row.Kind is InputKind.Pulse or InputKind.Hold,
                _ => row.Kind is InputKind.Button or InputKind.Switch or InputKind.Sensor
            };
            if (!kindFits)
                problems.Add($"{at}: kind {row.Kind} does not fit source {row.Source}.");
            if (row.Drives == InputDrives.Toggle && (row.Source != CommandSource.DigitalInput || row.Kind != InputKind.Button))
                problems.Add($"{at}: only a Button can toggle.");
            if (row.Kind == InputKind.Switch && row.Drives != InputDrives.OnOff)
                problems.Add($"{at}: a switch drives On/Off (its position is the command).");
            var drivesPair = row.Drives switch
            {
                InputDrives.On => row.On is not null,
                InputDrives.Off => row.Off is not null,
                _ => row.On is not null || row.Off is not null
            };
            if (drivesPair && !hasPair)
                problems.Add($"{at}: this blueprint has no On/Off commands (Switchable interface).");
            if (!drivesPair && row.SingleCommands.Count == 0)
                problems.Add($"{at}: give a priority for On or Off, or select a command such as reset.");
            if (!CommandInputLimits.IsPriority(row.On) || !CommandInputLimits.IsPriority(row.Off))
                problems.Add($"{at}: {CommandInputLimits.PriorityText}");
            foreach (var command in row.SingleCommands.Where(c => !singleCommands.Contains(c, StringComparer.Ordinal)))
                problems.Add($"{at}: '{command}' is not a command of this blueprint.");
            if (row.Debounce is { } d && (!row.IsPhysical || d <= 0 || d > CommandInputLimits.MaxDebounceSeconds))
                problems.Add($"{at}: debounce is for digital inputs, 0 to {CommandInputLimits.MaxDebounceSeconds} s.");
            if (row.StuckTime is { } s && (!row.IsPhysical || s <= 0))
                problems.Add($"{at}: the stuck time is for digital inputs and must be more than 0 s.");
            if (row.Source == CommandSource.Unit && row.InAuto != InputInAuto.Only)
                problems.Add($"{at}: the Unit input counts only in auto.");
            if (row.Location != InputLocation.Any && config.Selector is null)
                problems.Add($"{at}: a location needs a local/remote selector.");
        }
        if (config.Rows.Any(r => r.Kind == InputKind.Switch) && config.Rows.Count > 1)
            problems.Add("A maintained switch must be the only command input (G-143).");
        if (config.Selector is { } selector && tagExists is not null && !tagExists(selector))
            problems.Add($"Selector {selector} is not a Bool input of this CM.");
        return problems;
    }

    public static IReadOnlyList<TagDefinition> Tags(CommandInputConfig config)
    {
        var tags = new List<TagDefinition>();
        foreach (var row in config.Rows)
        {
            if (row.IsPhysical)
            {
                var contacts = row.TwoContacts ? new[] { $"{row.Name}_on", $"{row.Name}_off" } : [row.Name];
                foreach (var contact in contacts)
                {
                    tags.Add(new TagDefinition(contact, TagGroup.Fin, TagDataType.Bool, TagDirection.In, TagKind.External, null, null,
                        $"Command input {row.Name} ({row.Kind})"));
                    tags.Add(new TagDefinition($"invert_{contact}", TagGroup.Set, TagDataType.Bool, TagDirection.InOut, TagKind.Internal,
                        JsonValue.Create(false), null, $"Invert {contact} (normally closed contact)"));
                }
                if (row.Debounce is { } debounce)
                    tags.Add(new TagDefinition($"{row.Name}_debounce", TagGroup.Par, TagDataType.Real, TagDirection.InOut, TagKind.Internal,
                        JsonValue.Create(debounce), null, $"Debounce of {row.Name}", "s"));
                if (row.StuckTime is { } stuck)
                {
                    tags.Add(new TagDefinition($"{row.Name}_stuck_time", TagGroup.Par, TagDataType.Real, TagDirection.InOut, TagKind.Internal,
                        JsonValue.Create(stuck), null, $"Stuck alarm when {row.Name} stays active longer", "s"));
                    tags.Add(new TagDefinition($"{row.Name}_stuck.active", TagGroup.Alm, TagDataType.Bool, TagDirection.Out));
                    tags.Add(new TagDefinition($"{row.Name}_stuck.enabled", TagGroup.Alm, TagDataType.Bool, TagDirection.InOut, TagKind.Internal, JsonValue.Create(true)));
                    tags.Add(new TagDefinition($"{row.Name}_stuck.raise_count", TagGroup.Alm, TagDataType.Int32, TagDirection.Out));
                }
            }
            else
            {
                if (row.GivesOn)
                    tags.Add(new TagDefinition($"{row.Name}_on", TagGroup.Cmd, TagDataType.Bool, TagDirection.In, TagKind.Internal, null, null,
                        $"On request from {row.Name} ({row.Source}, {row.Kind})"));
                if (row.GivesOff)
                    tags.Add(new TagDefinition($"{row.Name}_off", TagGroup.Cmd, TagDataType.Bool, TagDirection.In, TagKind.Internal, null, null,
                        $"Off request from {row.Name} ({row.Source}, {row.Kind})"));
            }
            foreach (var command in row.SingleCommands.Where(_ => !row.IsPhysical))
                tags.Add(new TagDefinition($"{row.Name}_{command}", TagGroup.Cmd, TagDataType.Bool, TagDirection.In, TagKind.Internal, null, null,
                    $"{command} request from {row.Name}"));
        }
        if (config.Rows.Count > 0)
        {
            tags.Add(new TagDefinition("active_input", TagGroup.Sts, TagDataType.Int32, TagDirection.Out, TagKind.Internal, null, null,
                "Command input that last won (row number, 0 = none)"));
            tags.Add(new TagDefinition("override", TagGroup.Sts, TagDataType.Bool, TagDirection.Out, TagKind.Internal, null, null,
                "An override input acted while the Unit was in auto"));
        }
        return tags;
    }

    public static void Configure(Project project, CmLibrary library, Guid controlModuleId, CommandInputConfig? config)
    {
        var cm = project.Get(controlModuleId);
        var type = library.Find(InstanceFactory.BlueprintIdOf(cm));
        var own = project.GetChildren(cm.Id).OfType<Tag>().ToList();
        if (config is not null)
        {
            var hasPair = own.Any(t => t.Group == TagGroup.Cmd && t.Name == "set_on") && own.Any(t => t.Group == TagGroup.Cmd && t.Name == "set_off");
            var problems = Problems(config, type?.SingleCommands ?? [], hasPair,
                selector => own.Any(t => $"{t.Group.Code()}.{t.Name}".Equals(selector, StringComparison.OrdinalIgnoreCase) && t.DataType == TagDataType.Bool && t.Group == TagGroup.Fin));
            if (problems.Count > 0)
                throw new ProjectException(ProjectErrors.InvalidInputs, problems[0], "rows");
            if (config.Rows.Any(r => r.Kind == InputKind.Switch) && project.UnitOf(cm.Id) is { } unit)
                throw new ProjectException(ProjectErrors.InvalidInputs, $"{cm.Name} is a member of Unit {unit.Name}; a CM controlled by a maintained switch cannot be in a Unit (G-143).", "rows");
        }
        var before = InputsOf(cm) is { } old ? Tags(old).Select(Key).ToHashSet(StringComparer.Ordinal) : [];
        var wanted = config is null ? [] : Tags(config);
        var wantedKeys = wanted.Select(Key).ToHashSet(StringComparer.Ordinal);
        foreach (var tag in own.Where(t => before.Contains($"{t.Group.Code()}.{t.Name}") && !wantedKeys.Contains($"{t.Group.Code()}.{t.Name}")))
            project.Delete(tag.Id);
        foreach (var definition in wanted)
        {
            var existing = own.FirstOrDefault(t => $"{t.Group.Code()}.{t.Name}" == Key(definition));
            if (existing is null)
                project.AddTag(cm.Id, definition);
            else if (definition.Group == TagGroup.Par && !JsonNode.DeepEquals(existing.InitialValue, definition.InitialValue))
                project.SetInitialValue(existing.Id, definition.InitialValue);
        }
        if (cm is UnitInstance)
            project.SetUnitCommandInputs(cm.Id, config);
        else
            project.SetCommandInputs(cm.Id, config);
    }

    public static CommandInputConfig? InputsOf(ProjectObject obj) => obj switch
    {
        ControlModule c => c.CommandInputs,
        UnitInstance u => u.CommandInputs,
        _ => null
    };

    public static void SetUnitRow(Project project, CmLibrary library, Guid controlModuleId, bool member, string rowName = CommandInputConfig.UnitRow)
    {
        var cm = project.Get(controlModuleId);
        var config = InputsOf(cm) ?? CommandInputConfig.Empty;
        var current = config.Rows.FirstOrDefault(r => r.Source == CommandSource.Unit);
        if (member ? current?.Name == rowName : current is null)
            return;
        if (current is not null)
            config = config with { Rows = config.Rows.Where(r => r.Source != CommandSource.Unit).ToList() };
        if (member && config.Rows.Any(r => r.Kind == InputKind.Switch))
            throw new ProjectException(ProjectErrors.InvalidInputs, $"{cm.Name} is controlled by a maintained switch and cannot be in a Unit (G-143).", "controlModuleId");
        var type = library.Find(InstanceFactory.BlueprintIdOf(cm));
        var hasPair = project.GetChildren(cm.Id).OfType<Tag>().Count(t => t.Group == TagGroup.Cmd && (t.Name == "set_on" || t.Name == "set_off")) == 2;
        var commands = (type?.SingleCommands ?? []).Where(c => c == "reset").ToList();
        if (member && !hasPair && commands.Count == 0)
        {
            if (current is not null)
                Configure(project, library, cm.Id, config);
            return;
        }
        var rows = member
            ? config.Rows.Append(new CommandInput(rowName, CommandSource.Unit, InputKind.Pulse, InputDrives.OnOff,
                hasPair ? 3 : null, hasPair ? 3 : null, InputInAuto.Only,
                Commands: (type?.SingleCommands ?? []).Where(c => c == "reset").ToList())).ToList()
            : config.Rows.Where(r => r.Source != CommandSource.Unit).ToList();
        Configure(project, library, cm.Id, config with { Rows = rows });
    }

    /// <summary>The stuck alarm of a digital input row (PLC reactive; the command input logic raises it).</summary>
    public static CmAlarm StuckAlarm(CommandInput row, Guid blueprintId) => new(new AlarmDefinition
    {
        Id = StableId.From("apolloiq.builder.stuck-alarm", blueprintId.ToString("D"), row.Name),
        Name = $"{row.Name}_stuck",
        Priority = AlarmPriority.Typical(AlarmLevel.Warning),
        Message = $"{{instance_name}}: command input {row.Name} is stuck",
        Trigger = AlarmTrigger.State,
        PlcReactive = true
    }, AlarmSource.StuckInput);

    private static string Key(TagDefinition definition) => $"{definition.Group.Code()}.{definition.Name}";
}
