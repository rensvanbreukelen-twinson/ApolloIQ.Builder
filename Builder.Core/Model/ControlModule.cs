namespace Builder.Core.Model;

public sealed class ControlModule(
    Guid id,
    string name,
    Guid? parentId,
    string typeName,
    string typeVersion,
    IEnumerable<string>? optionalTags = null)
    : ProjectObject(id, name, parentId)
{
    public override ObjectKind Kind => ObjectKind.ControlModule;

    public string TypeName { get; } = typeName;

    public string TypeVersion { get; internal set; } = typeVersion;

    public IReadOnlyList<string> OptionalTags { get; } = (optionalTags ?? []).Order(StringComparer.Ordinal).ToList();

    internal List<InterlockRule> Rules { get; private init; } = [];

    internal Dictionary<string, int> Severities { get; } = new(StringComparer.Ordinal);

    internal List<CommandWire> Wires { get; } = [];

    public IReadOnlyList<CommandWire> CommandWires => Wires;

    public PicConfiguration? Pic { get; internal set; }

    public CommandInputConfig? CommandInputs { get; internal set; }

    public static ControlModule ForUnit(UnitInstance unit) =>
        new(unit.Id, unit.Name, unit.ParentId, unit.BlueprintName, unit.BlueprintVersion) { CommandInputs = unit.CommandInputs, Rules = unit.Rules };

    public Guid? ExecutionDeviceId { get; internal set; }

    public IReadOnlyDictionary<string, int> AlarmSeverities => Severities;

    /// <summary>Project-level interlocks defined on this instance (G-172).</summary>
    public IReadOnlyList<InterlockRule> Interlocks => Rules;
}
