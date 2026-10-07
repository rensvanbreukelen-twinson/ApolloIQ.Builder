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

    /// <summary>The project. Replaced as a whole when a proposal is accepted or an accept is undone.</summary>
    public Project Project { get; private set; } = project;

    public TagRegistry Tags => new(Project);

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

    /// <summary>
    /// Builds a new project from the current one (under the session lock) and, when <c>Next</c> is not null, puts it in place of the
    /// current one and saves it.
    /// </summary>
    public T Replace<T>(Func<Project, (Project? Next, T Result)> build)
    {
        (Project? Next, T Result) outcome;
        lock (_lock)
        {
            outcome = build(Project);
            if (outcome.Next is null)
                return outcome.Result;
            Project = outcome.Next;
            ProjectStore.Save(Directory, Id, Name, Project);
        }
        Changed?.Invoke(this);
        return outcome.Result;
    }
}
