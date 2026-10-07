namespace Builder.Core.Model;

public abstract class ProjectObject
{
    protected ProjectObject(Guid id, string name, Guid? parentId)
    {
        Id = id;
        Name = name;
        ParentId = parentId;
    }

    public Guid Id { get; }

    public string Name { get; internal set; }

    public Guid? ParentId { get; internal set; }

    public abstract ObjectKind Kind { get; }

    public virtual string PathSegment => Name;
}
