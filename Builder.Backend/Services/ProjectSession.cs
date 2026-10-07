using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Persistence;
using Builder.Persistence.Export;

namespace Builder.Backend.Services;

public sealed class ProjectSession(Guid id, string name, string directory, Project project, HmiExportProfile? hmiExport = null)
{
    private readonly Lock _lock = new();

    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public string Directory { get; } = directory;

    public Project Project { get; } = project;

    public TagRegistry Tags { get; } = new(project);

    public HmiExportProfile HmiExport { get; private set; } = hmiExport ?? HmiExportProfile.Default;

    public HmiExportProfile SetHmiExport(HmiExportProfile profile)
    {
        HmiTagsExporter.Validate(profile);
        lock (_lock)
        {
            HmiExport = profile;
            ProjectStore.Save(Directory, Id, Name, Project, HmiExport);
            return HmiExport;
        }
    }

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
            ProjectStore.Save(Directory, Id, Name, Project, HmiExport);
        }
        Changed?.Invoke(this);
        return result;
    }
}
