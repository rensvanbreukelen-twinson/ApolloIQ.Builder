using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;

namespace Builder.Persistence.Export;

public static class ScadaExporter
{
    public const string Schema = "apolloiq.scada/2";

    public const string FileName = "apolloiq-scada.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static ScadaFile Build(Project project, CmLibrary library, HmiExportProfile profile, string projectName)
    {
        var objects = new List<ScadaObject>();
        var registry = new TagRegistry(project);
        foreach (var obj in project.Objects.Where(o => o is ControlModule or UnitInstance).OrderBy(o => project.GetPath(o.Id), StringComparer.Ordinal))
        {
            var (typeName, inputs, kind) = obj switch
            {
                ControlModule c => (c.TypeName, c.CommandInputs, "controlModule"),
                UnitInstance { IsEquipmentModule: true } u => (u.BlueprintName, u.CommandInputs, "equipmentModule"),
                UnitInstance u => (u.BlueprintName, u.CommandInputs, "unit"),
                _ => throw new InvalidOperationException()
            };
            var type = library.Find(typeName);
            var parent = obj.ParentId is { } p && project.Find(p) is UnitInstance container ? project.GetPath(container.Id) : null;
            var severities = obj is ControlModule cm ? cm.AlarmSeverities : new Dictionary<string, int>();
            objects.Add(new ScadaObject(obj.Id, project.GetPath(obj.Id), obj.Name, kind, typeName, type?.Description ?? "", parent,
                project.RoleOf(obj.Id),
                (type?.States ?? UniversalStates.All).Select(s => new ScadaState(s.Code, s.Name)).ToList(),
                type?.Aliases.Count > 0 ? new SortedDictionary<string, string>(type.Aliases.ToDictionary(a => a.Key, a => a.Value), StringComparer.Ordinal) : null,
                Commands(project, obj, inputs),
                (type?.Alarms ?? []).Select(a => new ScadaAlarm(a.Name, severities.TryGetValue(a.Name, out var s) ? s : a.Severity,
                    AlarmDefinition.Band(severities.TryGetValue(a.Name, out var s2) ? s2 : a.Severity),
                    a.Message.TryGetValue("en", out var en) ? en : a.Message.Values.FirstOrDefault() ?? a.Name,
                    $"{typeName}.{a.Name}", a.Latch, a.Plc ? "PLC" : "SCADA", a.Plc ? null : a.Condition,
                    new SortedDictionary<string, string>(a.Message.ToDictionary(m => m.Key, m => m.Value), StringComparer.Ordinal)))
                    .Concat(InterlockSources.OwnRules(obj).Where(r => r.Kind == InterlockKind.Trip).Select(r =>
                    {
                        var message = string.IsNullOrWhiteSpace(r.Text) ? $"{r.Alarm}" : r.Text;
                        return new ScadaAlarm(r.Alarm!, r.Severity, AlarmDefinition.Band(r.Severity), message, $"{project.GetPath(obj.Id)}.{r.Alarm}", "TRUE", "PLC", null,
                            new SortedDictionary<string, string>(StringComparer.Ordinal) { ["en"] = message });
                    })).ToList(),
                HmiInterlocksExporter.Build(project, library, registry, obj),
                CommandLock(project, registry, obj, inputs)));
        }
        return new ScadaFile(Schema, projectName, HmiTagsExporter.Build(project, profile), objects);
    }

    private static SortedDictionary<string, string> Commands(Project project, ProjectObject obj, CommandInputConfig? inputs)
    {
        var tags = project.GetChildren(obj.Id).OfType<Tag>().Where(t => t.Group == TagGroup.Cmd && t.DataType == TagDataType.Bool)
            .Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var rows = inputs?.Rows ?? [];
        var generated = rows.SelectMany(r => new[] { $"{r.Name}_on", $"{r.Name}_off" }.Concat(r.SingleCommands.Select(c => $"{r.Name}_{c}")))
            .ToHashSet(StringComparer.Ordinal);
        var hmi = rows.FirstOrDefault(r => r.Source == CommandSource.Hmi);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var command in tags.Where(t => !generated.Contains(t)))
        {
            var routed = hmi is null ? null : command switch
            {
                "set_on" when hmi.GivesOn => $"{hmi.Name}_on",
                "set_off" when hmi.GivesOff => $"{hmi.Name}_off",
                _ when hmi.SingleCommands.Contains(command) => $"{hmi.Name}_{command}",
                _ => null
            };
            result[command] = routed is not null && tags.Contains(routed) ? $"CMD.{routed}"
                : rows.Count > 0 && command is "set_on" or "set_off" ? "" : $"CMD.{command}";
        }
        foreach (var key in result.Where(r => r.Value.Length == 0).Select(r => r.Key).ToList())
            result.Remove(key);
        return result;
    }

    /// <summary>
    /// When the HMI's commands only count in one mode (the HMI command input row is Ignore or Only in auto), the mode tag that
    /// decides it: the auto tag of the container, as the command inputs read it. The HMI shows the command-locked badge.
    /// </summary>
    private static ScadaCommandLock? CommandLock(Project project, TagRegistry registry, ProjectObject obj, CommandInputConfig? inputs)
    {
        var hmi = inputs?.Rows.FirstOrDefault(r => r.Source == CommandSource.Hmi);
        if (hmi is null || hmi.InAuto is not (InputInAuto.Ignore or InputInAuto.Only) || project.UnitOf(obj.Id) is not { } unit)
            return null;
        var path = $"{project.GetPath(unit.Id)}.STS.auto";
        return registry.FindByPath(path) is { } auto ? new ScadaCommandLock(new HmiTagRef(auto.Id, path), hmi.InAuto == InputInAuto.Ignore) : null;
    }

    public static byte[] SerializeUtf8(Project project, CmLibrary library, HmiExportProfile profile, string projectName) =>
        new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(Build(project, library, profile, projectName), Options) + "\n");
}

public sealed record ScadaFile(string Schema, string Project, IReadOnlyList<HmiTagRecord> Tags, IReadOnlyList<ScadaObject> Objects);

public sealed record ScadaObject(Guid Uid, string Path, string Name, string Kind, string Blueprint, string Description, string? Parent, string? Role,
    IReadOnlyList<ScadaState> States, IReadOnlyDictionary<string, string>? Aliases, IReadOnlyDictionary<string, string> Commands,
    IReadOnlyList<ScadaAlarm> Alarms, HmiInterlocks? Interlocks = null, ScadaCommandLock? CommandLock = null);

/// <summary>The HMI's commands are locked while <c>Auto</c> is TRUE (<c>LockedInAuto</c>) or while it is FALSE.</summary>
public sealed record ScadaCommandLock(HmiTagRef Auto, bool LockedInAuto);

public sealed record ScadaState(int Code, string Name);

public sealed record ScadaAlarm(string Name, int Severity, string Band, string Message, string MessageKey, string Latch, string RunsOn,
    string? Condition, IReadOnlyDictionary<string, string> Messages);
