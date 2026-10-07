using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Persistence;

namespace Builder.Backend.Services;

public sealed class ProjectSession(Guid id, string name, string directory, Project project)
{
    private readonly Lock _lock = new();

    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public string Directory { get; } = directory;

    public Project Project { get; } = project;

    public TagRegistry Tags { get; } = new(project);

    public T Read<T>(Func<Project, T> read)
    {
        lock (_lock)
            return read(Project);
    }

    public event Action<ProjectSession>? Changed;

    public T Change<T>(Func<Project, T> change)
    {
        T result;
        lock (_lock)
        {
            result = change(Project);
            ProjectStore.Save(Directory, Id, Name, Project);
        }
        Changed?.Invoke(this);
        return result;
    }
}
