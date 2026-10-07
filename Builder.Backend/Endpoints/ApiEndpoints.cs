using Builder.Backend.Contracts;
using Builder.Backend.Services;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;

namespace Builder.Backend.Endpoints;

public static class ApiEndpoints
{
    public static void MapBuilderApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { status = "ok", application = "ApolloIQ.Builder" }));

        api.MapGet("/library/types", (CmLibrary library) =>
            library.Types.Where(t => !t.IsUnit).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => t.ToDto()).ToList());

        api.MapGet("/projects", (ProjectWorkspace workspace) => workspace.List());

        api.MapGet("/conventions", () => ApolloIQ.Core.Conventions.Conventions.Current);

        api.MapPost("/projects", (CreateProjectRequest request, ProjectWorkspace workspace) =>
        {
            var session = workspace.Create(request.Name);
            return Results.Created($"/api/projects/{session.Id}", ToDto(session));
        });

        var project = api.MapGroup("/projects/{projectId:guid}");

        project.MapGet("", (Guid projectId, ProjectWorkspace workspace) => ToDto(workspace.Get(projectId)));

        project.MapGet("/tree", (Guid projectId, ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Read(p => Mapping.Tree(p, library, null)));

        project.MapPost("/folders", (Guid projectId, CreateFolderRequest request, ProjectWorkspace workspace, CmLibrary library) =>
        {
            var node = workspace.Get(projectId).Change(p => Mapping.Node(p, library, p.AddFolder(request.Name, request.ParentId)));
            return Results.Created($"/api/projects/{projectId}/objects/{node.Id}", node);
        });

        project.MapPost("/control-modules", (Guid projectId, CreateControlModuleRequest request, ProjectWorkspace workspace, CmLibrary library, BlueprintStore blueprints) =>
        {
            var node = workspace.Get(projectId).Change(p =>
            {
                var cm = InstanceFactory.Create(p, library, request.BlueprintId, request.Name, request.ParentId);
                UnitSupport.Entered(p, library, blueprints, cm.Id);
                return Mapping.Node(p, library, cm);
            });
            return Results.Created($"/api/projects/{projectId}/objects/{node.Id}", node);
        });

        project.MapGet("/objects/{id:guid}", (Guid projectId, Guid id, ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Read(p => Mapping.Node(p, library, Container(p, id))));

        project.MapPatch("/objects/{id:guid}", (Guid projectId, Guid id, RenameRequest request, ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Change(p =>
            {
                p.Rename(Container(p, id).Id, request.Name);
                return Mapping.Node(p, library, p.Get(id));
            }));

        project.MapPost("/objects/{id:guid}/move", (Guid projectId, Guid id, MoveRequest request, ProjectWorkspace workspace, CmLibrary library, BlueprintStore blueprints) =>
            workspace.Get(projectId).Change(p =>
            {
                UnitSupport.Move(p, library, blueprints, Container(p, id).Id, request.ParentId);
                return Mapping.Node(p, library, p.Get(id));
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
                foreach (var obj in p.Objects.Where(o => o is ControlModule or UnitInstance))
                    if (library.Find(InstanceFactory.BlueprintIdOf(obj)) is { } type)
                        result[p.GetPath(obj.Id)] = type.States.Select(s => s.Name).ToArray();
                return result;
            }));

        project.MapGet("/tags", (Guid projectId, Guid? scope, string? group, string? direction, string? kind, string? search,
            ProjectWorkspace workspace) =>
            workspace.Get(projectId).Read(p => QueryTags(p, scope, group, direction, kind, search)));
    }

    internal static ProjectDto ToDto(ProjectSession session) =>
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
