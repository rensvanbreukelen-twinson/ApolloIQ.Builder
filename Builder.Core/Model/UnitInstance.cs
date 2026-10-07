namespace Builder.Core.Model;

public sealed class UnitInstance(Guid id, string name, Guid? parentId, string blueprintName, string blueprintVersion, bool isEquipmentModule = false)
    : ProjectObject(id, name, parentId)
{
    public override ObjectKind Kind => ObjectKind.Unit;

    public string BlueprintName { get; } = blueprintName;

    public bool IsEquipmentModule { get; } = isEquipmentModule;

    public string RowName => IsEquipmentModule ? CommandInputConfig.EmRow : CommandInputConfig.UnitRow;

    public string BlueprintVersion { get; } = blueprintVersion;

    internal Dictionary<string, Guid> Members { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, Guid> RoleMembers => Members;

    public CommandInputConfig? CommandInputs { get; internal set; }

    internal List<InterlockRule> Rules { get; } = [];

    public IReadOnlyList<InterlockRule> Interlocks => Rules;
}
