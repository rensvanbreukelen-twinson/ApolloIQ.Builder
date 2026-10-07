using Builder.Backend.Contracts;
using Builder.Backend.Services;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Persistence.Export;

namespace Builder.Backend.Endpoints;

public static class ApiEndpoints
{
    public static void MapBuilderApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { status = "ok", application = "ApolloIQ.Builder" }));

        api.MapGet("/library/types", (CmLibrary library) =>
            library.Types.Where(t => !t.IsUnit).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => t.ToDto()).ToList());

        api.MapGet("/library/errors", (CmLibrary library) =>
            library.Errors.Select(e => new LibraryErrorDto(e.File, e.Path, e.Message, e.Line)).ToList());

        api.MapGet("/projects", (ProjectWorkspace workspace) => workspace.List());

        api.MapGet("/conventions", () => Conventions.Current);

        api.MapGet("/examples", (ExampleProjects examples) => examples.List());

        api.MapPost("/examples/{file}", (string file, ExampleProjects examples, ProjectWorkspace workspace, CmLibrary library, BlueprintStore blueprints) =>
        {
            var example = examples.Find(file);
            var names = workspace.List().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = example.Name;
            for (var i = 2; names.Contains(name); i++)
                name = $"{example.Name} ({i})";
            var session = workspace.Create(name);
            session.Change(p =>
            {
                ExampleProjects.Apply(p, library, blueprints, example);
                return 0;
            });
            return Results.Created($"/api/projects/{session.Id}", ToDto(session));
        });

        api.MapPost("/projects", (CreateProjectRequest request, ProjectWorkspace workspace) =>
        {
            var session = workspace.Create(request.Name);
            return Results.Created($"/api/projects/{session.Id}", ToDto(session));
        });

        var project = api.MapGroup("/projects/{projectId:guid}");

        project.MapGet("", (Guid projectId, ProjectWorkspace workspace) => ToDto(workspace.Get(projectId)));

        project.MapGet("/tree", (Guid projectId, ProjectWorkspace workspace) =>
            workspace.Get(projectId).Read(p => Mapping.Tree(p, null)));

        project.MapPost("/folders", (Guid projectId, CreateFolderRequest request, ProjectWorkspace workspace) =>
        {
            var node = workspace.Get(projectId).Change(p => Mapping.Node(p, p.AddFolder(request.Name, request.ParentId)));
            return Results.Created($"/api/projects/{projectId}/objects/{node.Id}", node);
        });

        project.MapPost("/control-modules", (Guid projectId, CreateControlModuleRequest request, ProjectWorkspace workspace, CmLibrary library, BlueprintStore blueprints) =>
        {
            var node = workspace.Get(projectId).Change(p =>
            {
                var cm = InstanceFactory.Create(p, library, request.Type, request.Name, request.ParentId, request.OptionalTags);
                UnitSupport.Entered(p, library, blueprints, cm.Id);
                return Mapping.Node(p, cm);
            });
            return Results.Created($"/api/projects/{projectId}/objects/{node.Id}", node);
        });

        project.MapGet("/objects/{id:guid}", (Guid projectId, Guid id, ProjectWorkspace workspace) =>
            workspace.Get(projectId).Read(p => Mapping.Node(p, Container(p, id))));

        project.MapPatch("/objects/{id:guid}", (Guid projectId, Guid id, RenameRequest request, ProjectWorkspace workspace) =>
            workspace.Get(projectId).Change(p =>
            {
                p.Rename(Container(p, id).Id, request.Name);
                return Mapping.Node(p, p.Get(id));
            }));

        project.MapPost("/objects/{id:guid}/move", (Guid projectId, Guid id, MoveRequest request, ProjectWorkspace workspace, CmLibrary library, BlueprintStore blueprints) =>
            workspace.Get(projectId).Change(p =>
            {
                UnitSupport.Move(p, library, blueprints, Container(p, id).Id, request.ParentId);
                return Mapping.Node(p, p.Get(id));
            }));

        project.MapGet("/objects/{id:guid}/deletion-summary", (Guid projectId, Guid id, ProjectWorkspace workspace) =>
            workspace.Get(projectId).Read(p =>
            {
                var summary = InstanceFactory.Summarize(p, Container(p, id).Id);
                return new DeletionSummaryDto(summary.Folders, summary.ControlModules, summary.Tags);
            }));

        project.MapDelete("/objects/{id:guid}", (Guid projectId, Guid id, ProjectWorkspace workspace) =>
        {
            workspace.Get(projectId).Change(p => p.Delete(Container(p, id).Id));
            return Results.NoContent();
        });

        project.MapGet("/states", (Guid projectId, ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Read(p =>
            {
                var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var cm in p.Objects.OfType<ControlModule>())
                    if (library.Find(cm.TypeName) is { } type) result[p.GetPath(cm.Id)] = type.States.Select(s => s.Name).ToArray();
                foreach (var unit in p.Objects.OfType<UnitInstance>())
                    if (library.Find(unit.BlueprintName) is { } type) result[p.GetPath(unit.Id)] = type.States.Select(s => s.Name).ToArray();
                return result;
            }));

        project.MapGet("/export/hmi-profile", (Guid projectId, ProjectWorkspace workspace) =>
            ProfileDto(workspace.Get(projectId)));

        project.MapPut("/export/hmi-profile", (Guid projectId, UpdateHmiExportProfileRequest request, ProjectWorkspace workspace) =>
        {
            if (!Enum.TryParse<HmiAddressMode>(request.Address, ignoreCase: true, out var address) || !Enum.IsDefined(address))
                throw new ProjectException(ProjectErrors.InvalidProfile, $"Unknown address mode '{request.Address}'. Use Path or SymbolKey.", "address");
            var session = workspace.Get(projectId);
            session.SetHmiExport(new HmiExportProfile(request.ConnectionId, request.ScanRateMs, address));
            return ProfileDto(session);
        });

        project.MapGet("/export/scada", (Guid projectId, ProjectWorkspace workspace, CmLibrary library) =>
        {
            var session = workspace.Get(projectId);
            var bytes = session.Read(p => ScadaExporter.SerializeUtf8(p, library, session.HmiExport, session.Name));
            return Results.File(bytes, "application/json", ScadaExporter.FileName);
        });

        project.MapGet("/export/hmi-tags", (Guid projectId, ProjectWorkspace workspace) =>
        {
            var session = workspace.Get(projectId);
            var bytes = session.Read(p => HmiTagsExporter.SerializeUtf8(p, session.HmiExport));
            return Results.File(bytes, "application/json", HmiTagsExporter.FileName);
        });

        project.MapGet("/tags", (Guid projectId, Guid? scope, string? group, string? direction, string? kind, string? search,
            ProjectWorkspace workspace) =>
            workspace.Get(projectId).Read(p => QueryTags(p, scope, group, direction, kind, search)));
    }

    private static HmiExportProfileDto ProfileDto(ProjectSession session) =>
        session.Read(p => new HmiExportProfileDto(session.HmiExport.ConnectionId, session.HmiExport.ScanRateMs,
            session.HmiExport.Address.ToString(), p.Tags.Count()));

    private static ProjectDto ToDto(ProjectSession session) =>
        new(session.Id, session.Name, session.Project.Settings.MaxNameLength);

    private static ProjectObject Container(Project project, Guid id)
    {
        var obj = project.Get(id);
        if (obj.Kind == ObjectKind.Tag)
            throw new ProjectException(ProjectErrors.NotFound, $"Object {id} is a tag; tags are managed through their CM.");
        return obj;
    }

    private static List<TagDto> QueryTags(Project project, Guid? scope, string? group, string? direction, string? kind, string? search)
    {
        IEnumerable<Tag> tags = scope is { } scopeId
            ? project.Descendants(Container(project, scopeId).Id).OfType<Tag>()
            : project.Tags;

        if (!string.IsNullOrEmpty(group))
        {
            if (!TagGroupNames.TryParse(group, out var g))
                throw new ProjectException(ProjectErrors.InvalidFilter, $"Unknown group '{group}'.");
            tags = tags.Where(t => t.Group == g);
        }
        if (!string.IsNullOrEmpty(direction))
        {
            if (!Enum.TryParse<TagDirection>(direction, true, out var d) || !Enum.IsDefined(d))
                throw new ProjectException(ProjectErrors.InvalidFilter, $"Unknown direction '{direction}'.");
            tags = tags.Where(t => t.Direction == d);
        }
        if (!string.IsNullOrEmpty(kind))
        {
            if (!Enum.TryParse<TagKind>(kind, true, out var k) || !Enum.IsDefined(k))
                throw new ProjectException(ProjectErrors.InvalidFilter, $"Unknown kind '{kind}'.");
            tags = tags.Where(t => t.TagKind == k);
        }

        var result = tags.Select(t => t.ToDto(project));
        if (!string.IsNullOrWhiteSpace(search))
            result = result.Where(t => t.Path.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
        return result.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
