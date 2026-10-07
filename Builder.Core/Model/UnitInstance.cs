using ApolloIQ.Core.Versioning;

namespace Builder.Core.Model;

/// <summary>An instance of a Unit or Equipment module blueprint.</summary>
public sealed class UnitInstance(Guid id, string name, Guid? parentId, Guid blueprintId, BlueprintVersion blueprintVersion, bool isEquipmentModule = false)
    : ProjectObject(id, name, parentId)
{
    public override ObjectKind Kind => ObjectKind.Unit;

    public Guid BlueprintId { get; } = blueprintId;

    public bool IsEquipmentModule { get; } = isEquipmentModule;

    public string RowName => IsEquipmentModule ? CommandInputConfig.EmRow : CommandInputConfig.UnitRow;

    public BlueprintVersion BlueprintVersion { get; } = blueprintVersion;

    internal Dictionary<string, Guid> Members { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, Guid> RoleMembers => Members;

    public CommandInputConfig? CommandInputs { get; internal set; }

    internal List<InterlockRule> Rules { get; } = [];

    public IReadOnlyList<InterlockRule> Interlocks => Rules;
}
