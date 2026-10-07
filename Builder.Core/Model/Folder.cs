namespace Builder.Core.Model;

public sealed class Folder(Guid id, string name, Guid? parentId) : ProjectObject(id, name, parentId)
{
    public override ObjectKind Kind => ObjectKind.Folder;

    public string Description { get; internal set; } = "";
}
