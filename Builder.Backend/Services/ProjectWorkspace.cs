using System.Collections.Concurrent;
using Builder.Core.Model;
using Builder.Persistence;

namespace Builder.Backend.Services;

public sealed record ProjectSummary(Guid Id, string Name);

public sealed class ProjectWorkspace(string root)
{
    private readonly ConcurrentDictionary<Guid, ProjectSession> _sessions = new();
    private readonly Lock _createLock = new();

    public string Root { get; } = root;

    public IReadOnlyList<ProjectSummary> List()
    {
        if (!Directory.Exists(Root))
            return [];
        return Directory.EnumerateDirectories(Root)
            .Where(d => File.Exists(Path.Combine(d, ProjectStore.ProjectFileName)))
            .Select(d => TryOpen(d))
            .OfType<ProjectSession>()
            .Select(s => new ProjectSummary(s.Id, s.Name))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ProjectSession Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ProjectException(ProjectErrors.InvalidName, "A project name is required.");
        lock (_createLock)
        {
            if (List().Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new ProjectException(ProjectErrors.DuplicateName, $"A project named '{name.Trim()}' already exists.");
            var id = Guid.NewGuid();
            var directory = Path.Combine(Root, id.ToString("D"));
            var session = new ProjectSession(id, name.Trim(), directory, new Project());
            session.Change(_ => 0);
            _sessions[id] = session;
            return session;
        }
    }

    public ProjectSession Get(Guid id)
    {
        if (_sessions.TryGetValue(id, out var session))
            return session;
        var directory = Path.Combine(Root, id.ToString("D"));
        if (!File.Exists(Path.Combine(directory, ProjectStore.ProjectFileName)))
            throw new ProjectException(ProjectErrors.NotFound, $"Project {id} does not exist.");
        return _sessions.GetOrAdd(id, _ => Open(directory));
    }

    private ProjectSession? TryOpen(string directory)
    {
        var name = Path.GetFileName(directory);
        if (Guid.TryParse(name, out var id) && _sessions.TryGetValue(id, out var cached))
            return cached;
        try
        {
            var session = Open(directory);
            return _sessions.GetOrAdd(session.Id, session);
        }
        catch (ProjectLoadException)
        {
            return null;
        }
    }

    private static ProjectSession Open(string directory)
    {
        var loaded = ProjectStore.Load(directory);
        return new ProjectSession(loaded.Id, loaded.Name, directory, loaded.Project);
    }
}
