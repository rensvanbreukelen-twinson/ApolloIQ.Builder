using ApolloIQ.Core.Versioning;

namespace Builder.Core.Model;

/// <summary>A control module: an instance of a CM blueprint, referenced by the blueprint's id and the version it was made from.</summary>
public sealed class ControlModule(
    Guid id,
    string name,
    Guid? parentId,
    Guid blueprintId,
    BlueprintVersion blueprintVersion)
    : ProjectObject(id, name, parentId)
{
    public override ObjectKind Kind => ObjectKind.ControlModule;

    public Guid BlueprintId { get; } = blueprintId;

    public BlueprintVersion BlueprintVersion { get; internal set; } = blueprintVersion;

    internal List<InterlockRule> Rules { get; private init; } = [];

    internal Dictionary<string, int> Priorities { get; } = new(StringComparer.Ordinal);

    internal List<CommandWire> Wires { get; } = [];

    public IReadOnlyList<CommandWire> CommandWires => Wires;

    public CommandInputConfig? CommandInputs { get; internal set; }

    /// <summary>A Unit or EM seen as a control module by the runtime (it runs a state machine too).</summary>
    public static ControlModule ForUnit(UnitInstance unit) =>
        new(unit.Id, unit.Name, unit.ParentId, unit.BlueprintId, unit.BlueprintVersion) { CommandInputs = unit.CommandInputs, Rules = unit.Rules };

    public Guid? ExecutionDeviceId { get; internal set; }

    /// <summary>What this instance is, in the engineer's words ("Dirty water transfer pump").</summary>
    public string Description { get; internal set; } = "";

    /// <summary>Alarm priorities that differ from the blueprint, per alarm name.</summary>
    public IReadOnlyDictionary<string, int> AlarmPriorities => Priorities;

    /// <summary>Project-level interlocks defined on this instance (G-172).</summary>
    public IReadOnlyList<InterlockRule> Interlocks => Rules;
}
