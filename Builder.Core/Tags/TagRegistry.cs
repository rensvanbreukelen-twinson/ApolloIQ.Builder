using Builder.Core.Model;

namespace Builder.Core.Tags;

public sealed class TagRegistry(Project project)
{
    private Dictionary<string, Tag>? _byPath;
    private long _indexedRevision = -1;

    public Tag? FindById(Guid id) => project.Find(id) as Tag;

    public Tag? FindByPath(string path)
    {
        if (_byPath is null || _indexedRevision != project.Revision)
        {
            _byPath = project.Tags.ToDictionary(t => project.GetPath(t.Id), StringComparer.OrdinalIgnoreCase);
            _indexedRevision = project.Revision;
        }
        return _byPath.GetValueOrDefault(path);
    }

    public string PathOf(Tag tag) => project.GetPath(tag.Id);

    public IEnumerable<Tag> ForControlModule(Guid controlModuleId) =>
        project.GetChildren(controlModuleId).OfType<Tag>();

    public IEnumerable<Tag> ForGroup(Guid controlModuleId, TagGroup group) =>
        ForControlModule(controlModuleId).Where(t => t.Group == group);
}
