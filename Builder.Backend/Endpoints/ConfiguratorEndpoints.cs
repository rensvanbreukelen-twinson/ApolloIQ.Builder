using Builder.Backend.Contracts;
using Builder.Backend.Services;
using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Logic.Blueprints;

namespace Builder.Backend.Endpoints;

public sealed record PositionDto(double X, double Y);

public sealed record ConfiguratorCmDto(Guid Id, string Name, string Path, Guid BlueprintId, string Blueprint, string Description, PositionDto? Position,
    Guid? UnitId, string? Role, InterlockSummaryDto Interlocks, IReadOnlyList<string> Inputs);

public sealed record UnitRoleDto(string Role, Guid BlueprintId, string Blueprint, Guid? ControlModuleId);

public sealed record ConfiguratorUnitDto(Guid Id, string Name, string Path, Guid BlueprintId, string Blueprint, string Description, PositionDto? Position,
    IReadOnlyList<UnitRoleDto> Roles, string? Problem, bool EquipmentModule = false, Guid? UnitId = null, string? Role = null,
    InterlockSummaryDto? Interlocks = null);

public sealed record ConfiguratorFolderDto(Guid Id, string Name, string Path);

public sealed record ConfiguratorDto(Guid? FolderId, string FolderPath, IReadOnlyList<ConfiguratorFolderDto> Folders,
    IReadOnlyList<ConfiguratorCmDto> ControlModules, IReadOnlyList<ConfiguratorUnitDto> Units);

/// <summary>How many interlocks the object defines and how many act on it (G-172).</summary>
public sealed record InterlockSummaryDto(bool HasInterlocks, int Defined, int SwitchOn, int SwitchOff, int Trips);

public sealed record CreateUnitRequest(string Name, Guid? ParentId, Guid BlueprintId);

public sealed record UnitMemberRequest(Guid? ControlModuleId);

public sealed record LayoutItem(Guid Id, double X, double Y);

public sealed record LayoutRequest(IReadOnlyList<LayoutItem> Positions);

public static class ConfiguratorEndpoints
{
    public static void MapConfiguratorApi(this WebApplication app)
    {
        var project = app.MapGroup("/api/projects/{projectId:guid}");

        project.MapGet("/configurator", (Guid projectId, Guid? folder, ProjectWorkspace workspace, CmLibrary library, BlueprintStore blueprints) =>
            workspace.Get(projectId).Read(p => Build(p, folder, library, blueprints)));

        project.MapPost("/units", (Guid projectId, CreateUnitRequest request, ProjectWorkspace workspace, BlueprintStore blueprints, CmLibrary library) =>
        {
            var blueprint = blueprints.Find(request.BlueprintId);
            if (blueprint is null || blueprint.Kind == ApolloIQ.Core.Blueprints.BlueprintKind.CM)
                throw new ProjectException(ProjectErrors.InvalidUnit, $"Blueprint {request.BlueprintId} is not a Unit or Equipment module blueprint.", "blueprint");
            var node = workspace.Get(projectId).Change(p =>
            {
                var unit = UnitSupport.Create(p, blueprint, request.Name ?? "", request.ParentId, library, blueprints);
                return Mapping.Node(p, library, unit);
            });
            return Results.Created($"/api/projects/{projectId}/units/{node.Id}", node);
        });

        project.MapPut("/units/{id:guid}/roles/{role}", (Guid projectId, Guid id, string role, UnitMemberRequest request,
            ProjectWorkspace workspace, BlueprintStore blueprints, CmLibrary library) =>
            workspace.Get(projectId).Change(p =>
            {
                UnitSupport.CheckRole(p, blueprints, p.Get<UnitInstance>(id), role, request.ControlModuleId);
                UnitSupport.SetMember(p, library, id, role, request.ControlModuleId);
                return Results.NoContent();
            }));

        project.MapPut("/layout", (Guid projectId, LayoutRequest request, ProjectWorkspace workspace) =>
            workspace.Get(projectId).Change(p =>
            {
                foreach (var item in request.Positions ?? [])
                    p.SetPosition(item.Id, item.X, item.Y);
                return Results.NoContent();
            }));
    }

    private static ConfiguratorDto Build(Project project, Guid? folderId, CmLibrary library, BlueprintStore blueprints)
    {
        var opened = folderId is { } id ? project.Get(id) : null;
        if (opened is not (null or Folder or UnitInstance))
            throw new ProjectException(ProjectErrors.InvalidParent, $"{opened.Name} is not a folder, Unit or Equipment module.");
        PositionDto? Position(Guid objectId) => project.Layout.TryGetValue(objectId, out var p) ? new PositionDto(p.X, p.Y) : null;
        var top = opened is UnitInstance container ? [container] : project.GetChildren(folderId).ToList();
        var children = new List<ProjectObject>();
        void Add(ProjectObject obj)
        {
            children.Add(obj);
            if (obj is UnitInstance)
                foreach (var child in project.GetChildren(obj.Id).Where(c => c is ControlModule or UnitInstance))
                    Add(child);
        }
        foreach (var obj in top)
            Add(obj);

        var cms = children.OfType<ControlModule>().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(cm =>
        {
            var unit = project.UnitOf(cm.Id);
            var type = library.Find(cm.BlueprintId);
            return new ConfiguratorCmDto(cm.Id, cm.Name, project.GetPath(cm.Id), cm.BlueprintId, type?.Name ?? "(missing blueprint)", type?.Description ?? "",
                Position(cm.Id), unit?.Id, unit?.RoleMembers.First(m => m.Value == cm.Id).Key, Summary(project, library, cm),
                (cm.CommandInputs?.Rows ?? []).Select(r => r.Name).ToList());
        }).ToList();

        var units = children.OfType<UnitInstance>().OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).Select(u =>
        {
            var blueprint = blueprints.Find(u.BlueprintId);
            var roles = (blueprint?.Roles ?? []).Select(r => new UnitRoleDto(r.Name, r.BlueprintId, blueprints.Find(r.BlueprintId)?.Name ?? "(missing blueprint)",
                u.RoleMembers.TryGetValue(r.Name, out var cm) ? cm : null)).ToList();
            var parent = project.UnitOf(u.Id);
            return new ConfiguratorUnitDto(u.Id, u.Name, project.GetPath(u.Id), u.BlueprintId, blueprint?.Name ?? "(missing blueprint)", blueprint?.Description ?? "", Position(u.Id), roles,
                blueprint is null ? $"Blueprint {u.BlueprintId} does not exist." : null, u.IsEquipmentModule, parent?.Id,
                parent?.RoleMembers.First(m => m.Value == u.Id).Key, Summary(project, library, u));
        }).ToList();

        var folders = top.OfType<Folder>().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new ConfiguratorFolderDto(f.Id, f.Name, project.GetPath(f.Id))).ToList();
        return new ConfiguratorDto(folderId, folderId is { } f2 ? project.GetPath(f2) : "", folders, cms, units);
    }

    private static InterlockSummaryDto Summary(Project project, CmLibrary library, ProjectObject obj)
    {
        var acting = InterlockSources.Targeting(project, library, obj.Id);
        return new InterlockSummaryDto(InterlockEndpoints.HasInterlocks(project, obj), InterlockSources.OwnRules(obj).Count,
            acting.Count(s => s.Rule.Kind == InterlockKind.SwitchOn), acting.Count(s => s.Rule.Kind == InterlockKind.SwitchOff),
            acting.Count(s => s.Rule.Kind == InterlockKind.Trip));
    }
}
