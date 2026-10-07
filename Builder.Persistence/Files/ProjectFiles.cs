using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ApolloIQ.Core.Conventions;
using ApolloIQ.Core.Versioning;
using Builder.Core.Model;

namespace Builder.Persistence.Files;

public sealed class ProjectFile
{
    [JsonPropertyOrder(0)] public string Schema { get; set; } = ProjectStore.Schema;
    [JsonPropertyOrder(1)] public Guid Id { get; set; }
    [JsonPropertyOrder(2)] public string Name { get; set; } = "";
    [JsonPropertyOrder(3)] public int MaxNameLength { get; set; }
}

public sealed class FolderFile
{
    [JsonPropertyOrder(0)] public string Schema { get; set; } = ProjectStore.FolderSchema;
    [JsonPropertyOrder(1)] public Guid Id { get; set; }
    [JsonPropertyOrder(2)] public string Name { get; set; } = "";
    [JsonPropertyOrder(3)] public Guid? ParentId { get; set; }
    [JsonPropertyOrder(4)] public string? Description { get; set; }
}

public sealed class ControlModuleFile
{
    [JsonPropertyOrder(0)] public string Schema { get; set; } = ProjectStore.ControlModuleSchema;
    [JsonPropertyOrder(1)] public Guid Id { get; set; }
    [JsonPropertyOrder(2)] public string Name { get; set; } = "";
    [JsonPropertyOrder(3)] public Guid? ParentId { get; set; }
    [JsonPropertyOrder(4)] public Guid BlueprintId { get; set; }
    [JsonPropertyOrder(5)] public BlueprintVersion BlueprintVersion { get; set; } = BlueprintVersion.Initial;
    [JsonPropertyOrder(6)] public string? Description { get; set; }
    [JsonPropertyOrder(7)] public List<TagEntry> Tags { get; set; } = [];
    [JsonPropertyOrder(8)] public List<InterlockEntry>? Interlocks { get; set; }
    [JsonPropertyOrder(9)] public SortedDictionary<string, int>? AlarmPriority { get; set; }
    [JsonPropertyOrder(10)] public List<WireEntry>? Wires { get; set; }
    [JsonPropertyOrder(12)] public Guid? ExecutionDeviceId { get; set; }
    [JsonPropertyOrder(13)] public CommandInputConfig? CommandInputs { get; set; }
}

public sealed class UnitFile
{
    [JsonPropertyOrder(0)] public string Schema { get; set; } = ProjectStore.UnitSchema;
    [JsonPropertyOrder(1)] public Guid Id { get; set; }
    [JsonPropertyOrder(2)] public string Name { get; set; } = "";
    [JsonPropertyOrder(3)] public Guid? ParentId { get; set; }
    [JsonPropertyOrder(4)] public Guid BlueprintId { get; set; }
    [JsonPropertyOrder(5)] public BlueprintVersion BlueprintVersion { get; set; } = BlueprintVersion.Initial;
    [JsonPropertyOrder(5)][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool EquipmentModule { get; set; }
    [JsonPropertyOrder(6)] public string? Description { get; set; }
    [JsonPropertyOrder(6)] public SortedDictionary<string, Guid> Members { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyOrder(7)] public List<TagEntry> Tags { get; set; } = [];
    [JsonPropertyOrder(8)] public CommandInputConfig? CommandInputs { get; set; }
    [JsonPropertyOrder(9)] public List<InterlockEntry>? Interlocks { get; set; }
}

public sealed class LayoutFile
{
    [JsonPropertyOrder(0)] public string Schema { get; set; } = ProjectStore.LayoutSchema;
    [JsonPropertyOrder(1)] public SortedDictionary<Guid, LayoutEntry> Positions { get; set; } = [];
}

public sealed class LayoutEntry
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>A project-level interlock (G-172). The condition stores tag references as IDs.</summary>
public sealed class InterlockEntry
{
    [JsonPropertyOrder(0)] public Guid? TargetId { get; set; }
    [JsonPropertyOrder(1)] public string Kind { get; set; } = "SwitchOn";
    [JsonPropertyOrder(2)] public string Condition { get; set; } = "";
    [JsonPropertyOrder(3)] public string Text { get; set; } = "";
    [JsonPropertyOrder(4)] public string? Alarm { get; set; }
    [JsonPropertyOrder(5)] public Guid? AlarmId { get; set; }
    [JsonPropertyOrder(6)] public int? Priority { get; set; }
    [JsonPropertyOrder(7)] public string? Escalate { get; set; }

    public static List<InterlockEntry>? From(IReadOnlyList<InterlockRule> rules) => rules.Count == 0 ? null : rules.Select(r => new InterlockEntry
    {
        TargetId = r.TargetId,
        Kind = r.Kind.ToString(),
        Condition = r.Condition,
        Text = r.Text,
        Alarm = r.Kind == InterlockKind.Trip ? r.Alarm : null,
        AlarmId = r.Kind == InterlockKind.Trip ? r.AlarmId : null,
        Priority = r.Kind == InterlockKind.Trip ? r.Priority : null,
        Escalate = r.Kind == InterlockKind.Trip && r.Escalate != TripEscalation.None ? r.Escalate.ToString() : null
    }).ToList();

    public InterlockRule ToRule() => new()
    {
        TargetId = TargetId,
        Kind = Enum.TryParse<InterlockKind>(Kind, true, out var kind) && Enum.IsDefined(kind)
            ? kind : throw new ProjectException(ProjectErrors.InvalidInterlock, $"Unknown interlock kind '{Kind}'."),
        Condition = Condition,
        Text = Text,
        Alarm = Alarm,
        AlarmId = AlarmId,
        Priority = Priority ?? ApolloIQ.Core.Conventions.AlarmPriority.Default,
        Escalate = Escalate is null ? TripEscalation.None
            : Enum.TryParse<TripEscalation>(Escalate, true, out var e) && Enum.IsDefined(e) ? e : throw new ProjectException(ProjectErrors.InvalidInterlock, $"Unknown escalation '{Escalate}'.")
    };
}

public sealed class TagEntry
{
    [JsonPropertyOrder(0)] public Guid Id { get; set; }
    [JsonPropertyOrder(1)] public string Group { get; set; } = "";
    [JsonPropertyOrder(2)] public string Name { get; set; } = "";
    [JsonPropertyOrder(3)] public string DataType { get; set; } = "";
    [JsonPropertyOrder(4)] public string Direction { get; set; } = "";
    [JsonPropertyOrder(5)] public string Kind { get; set; } = "";
    [JsonPropertyOrder(6)] public string SymbolKey { get; set; } = "";
    [JsonPropertyOrder(7)] public JsonNode? Initial { get; set; }
    [JsonPropertyOrder(8)] public string? Enum { get; set; }
    [JsonPropertyOrder(9)] public string? Unit { get; set; }
    [JsonPropertyOrder(10)] public string Description { get; set; } = "";
    [JsonPropertyOrder(11)] public OriginEntry? Origin { get; set; }
}

public sealed class OriginEntry
{
    [JsonPropertyOrder(0)] public Guid Device { get; set; }
    [JsonPropertyOrder(1)] public string Source { get; set; } = "";
    [JsonPropertyOrder(2)] public string Address { get; set; } = "";
}

public sealed class TopologyFile
{
    [JsonPropertyOrder(0)] public string Schema { get; set; } = ProjectStore.TopologySchema;
    [JsonPropertyOrder(1)] public List<DeviceEntry> Devices { get; set; } = [];
    [JsonPropertyOrder(2)] public List<LinkEntry> Links { get; set; } = [];
}

public sealed class DeviceEntry
{
    [JsonPropertyOrder(0)] public Guid Id { get; set; }
    [JsonPropertyOrder(1)] public string Name { get; set; } = "";
    [JsonPropertyOrder(2)] public string Role { get; set; } = "";
    [JsonPropertyOrder(3)] public string Description { get; set; } = "";
}

public sealed class LinkEntry
{
    [JsonPropertyOrder(0)] public Guid Id { get; set; }
    [JsonPropertyOrder(1)] public Guid From { get; set; }
    [JsonPropertyOrder(2)] public Guid To { get; set; }
    [JsonPropertyOrder(3)] public string Protocol { get; set; } = "";
    [JsonPropertyOrder(4)] public string Class { get; set; } = "";
}

public sealed class WireEntry
{
    [JsonPropertyOrder(0)] public Guid Source { get; set; }
    [JsonPropertyOrder(1)] public string Mode { get; set; } = "";
    [JsonPropertyOrder(2)] public string? Command { get; set; }
}
