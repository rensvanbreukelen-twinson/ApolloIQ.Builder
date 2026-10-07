namespace Builder.Core.Types;

/// <summary>The published blueprints as runtime types, by blueprint id.</summary>
public sealed class CmLibrary
{
    private readonly Dictionary<Guid, CmType> _types = new();

    public IReadOnlyCollection<CmType> Types => _types.Values;

    public CmType? Find(Guid id) => _types.GetValueOrDefault(id);

    public CmType? FindByName(string name) =>
        _types.Values.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Replace(CmType type) => _types[type.Id] = type;

    public void Remove(Guid id) => _types.Remove(id);
}
