using Builder.Backend.Contracts;
using Builder.Backend.Services;
using Builder.Core.Model;
using Builder.Core.Tags;

namespace Builder.Backend.Endpoints;

public sealed record DeviceDto(Guid? Id, string Name, string Role, string? Description);

public sealed record LinkDto(Guid? Id, string From, string To, string Protocol, string Class);

public sealed record TopologyDto(IReadOnlyList<DeviceDto> Devices, IReadOnlyList<LinkDto> Links);

public sealed record DeviceRequest(Guid? DeviceId);

public sealed record OriginRequest(Guid? DeviceId, string? Source, string? Address);

public sealed record OriginDto(Guid TagId, string Tag, string TagSource, Guid? DeviceId, string? Source, string? Address);

public sealed record DeploymentItemDto(Guid Id, string Path, string Kind, string? Type, Guid? DeviceId, IReadOnlyList<OriginDto> Inputs);

public sealed record AccessDto(string Tag, string? Owner, string Consumer, string UsedBy, string Kind, string Path, bool Critical, bool SafetyViolation, string Address);

public sealed record BindingDto(IReadOnlyList<BindingIssue> Issues, IReadOnlyList<AccessDto> Accesses, int Errors, int Warnings);

public static class TopologyEndpoints
{
    public static void MapTopologyApi(this WebApplication app)
    {
        var project = app.MapGroup("/api/projects/{projectId:guid}");

        project.MapGet("/topology", (Guid projectId, ProjectWorkspace workspace) => workspace.Get(projectId).Read(ToDto));

        project.MapPut("/topology", (Guid projectId, TopologyDto request, ProjectWorkspace workspace) =>
            workspace.Get(projectId).Change(p =>
            {
                var devices = request.Devices.Select((d, i) => new Device(d.Id ?? Guid.NewGuid(), d.Name?.Trim() ?? "",
                    Parse<DeviceRole>(d.Role, $"devices[{i}].role"), d.Description ?? "")).ToList();
                Guid Resolve(string reference, string field)
                {
                    if (Guid.TryParse(reference, out var id) && devices.Any(d => d.Id == id))
                        return id;
                    return devices.FirstOrDefault(d => string.Equals(d.Name, reference, StringComparison.OrdinalIgnoreCase))?.Id
                           ?? throw new ProjectException(ProjectErrors.InvalidTopology, $"Unknown device '{reference}'.", field);
                }
                var links = request.Links.Select((l, i) => new Link(l.Id ?? Guid.NewGuid(), Resolve(l.From, $"links[{i}].from"),
                    Resolve(l.To, $"links[{i}].to"), l.Protocol ?? "", Parse<LinkClass>(l.Class, $"links[{i}].class"))).ToList();
                p.SetTopology(devices, links);
                return ToDto(p);
            }));

        project.MapGet("/deployment", (Guid projectId, ProjectWorkspace workspace, Builder.Core.Types.CmLibrary library) =>
            workspace.Get(projectId).Read(p => p.Objects
                .Where(o => o is ControlModule)
                .OrderBy(o => p.GetPath(o.Id), StringComparer.OrdinalIgnoreCase)
                .Select(o => new DeploymentItemDto(o.Id, p.GetPath(o.Id), "controlModule",
                    (o is ControlModule tc ? library.Find(tc.BlueprintId)?.Name : null), (o as ControlModule)?.ExecutionDeviceId,
                    p.GetChildren(o.Id).OfType<Tag>().Where(t => t.Group == TagGroup.Fin).OrderBy(t => t.Name, StringComparer.Ordinal)
                        .Select(t => new OriginDto(t.Id, p.GetPath(t.Id), t.Description, t.Origin?.DeviceId, t.Origin?.Source, t.Origin?.Address)).ToList()))
                .ToList()));

        project.MapPut("/objects/{id:guid}/device", (Guid projectId, Guid id, DeviceRequest request, ProjectWorkspace workspace) =>
        {
            workspace.Get(projectId).Change(p =>
            {
                p.SetExecutionDevice(id, request.DeviceId);
                return 0;
            });
            return Results.NoContent();
        });

        project.MapPut("/tags/{id:guid}/origin", (Guid projectId, Guid id, OriginRequest request, ProjectWorkspace workspace) =>
        {
            workspace.Get(projectId).Change(p =>
            {
                p.SetOrigin(id, request.DeviceId is { } device ? new TagOrigin(device, request.Source?.Trim() ?? "", request.Address?.Trim() ?? "") : null);
                return 0;
            });
            return Results.NoContent();
        });

        project.MapGet("/binding", (Guid projectId, bool? all, ProjectWorkspace workspace) =>
            workspace.Get(projectId).Read(p =>
            {
                var report = BindingResolver.Resolve(p);
                string Name(Guid? id) => id is { } d ? p.Topology.Device(d)?.Name ?? "?" : "";
                var accesses = report.Accesses.Where(a => all == true || a.Critical || a.Kind is not (AccessKind.Local or AccessKind.Direct))
                    .Select(a => new AccessDto(a.Tag, a.OwnerDeviceId is null ? null : Name(a.OwnerDeviceId), Name(a.ConsumerDeviceId), a.UsedBy,
                        a.Kind.ToString(), string.Join(" → ", a.Path.Select(d => Name(d))), a.Critical, a.SafetyViolation, a.Address))
                    .ToList();
                return new BindingDto(report.Issues, accesses, report.Errors, report.Warnings);
            }));
    }

    private static TopologyDto ToDto(Project project) => new(
        project.Topology.Devices.Select(d => new DeviceDto(d.Id, d.Name, d.Role.ToString(), d.Description)).ToList(),
        project.Topology.Links.Select(l => new LinkDto(l.Id, l.From.ToString(), l.To.ToString(), l.Protocol, l.Class.ToString())).ToList());

    private static T Parse<T>(string? text, string field) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value
            : throw new ProjectException(ProjectErrors.InvalidTopology, $"Unknown value '{text}'. Use {string.Join(", ", Enum.GetNames<T>())}.", field);
}
