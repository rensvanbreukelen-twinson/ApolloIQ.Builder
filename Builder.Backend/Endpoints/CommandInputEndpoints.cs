using Builder.Backend.Services;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;

namespace Builder.Backend.Endpoints;

public sealed record CommandInputsDto(Guid ControlModuleId, string Path, CommandInputConfig Config, CommandInputConfig? Defaults, bool HasPair,
    IReadOnlyList<string> SingleCommands, IReadOnlyList<string> Selectors, bool InUnit, IReadOnlyList<string> Problems);

public static class CommandInputEndpoints
{
    public static void MapCommandInputApi(this WebApplication app)
    {
        var project = app.MapGroup("/api/projects/{projectId:guid}/control-modules/{id:guid}/command-inputs");

        project.MapGet("", (Guid projectId, Guid id, ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Read(p => Dto(p, library, id)));

        project.MapPut("", (Guid projectId, Guid id, CommandInputConfig request, ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Change(p =>
            {
                CommandInputBehaviour.Configure(p, library, id, request);
                return Dto(p, library, id);
            }));

        project.MapPost("/reset", (Guid projectId, Guid id, ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Change(p =>
            {
                var cm = p.Get(id);
                var defaults = library.Find(InstanceFactory.BlueprintIdOf(cm))?.DefaultCommandInputs ?? CommandInputConfig.Empty;
                CommandInputBehaviour.Configure(p, library, id, defaults);
                if (p.UnitOf(id) is { } parent)
                    CommandInputBehaviour.SetUnitRow(p, library, id, member: true, parent.RowName);
                return Dto(p, library, id);
            }));
    }

    private static CommandInputsDto Dto(Project project, CmLibrary library, Guid id)
    {
        var cm = project.Get(id);
        var type = library.Find(InstanceFactory.BlueprintIdOf(cm));
        var tags = project.GetChildren(cm.Id).OfType<Tag>().ToList();
        var hasPair = tags.Any(t => t.Group == TagGroup.Cmd && t.Name == "set_on") && tags.Any(t => t.Group == TagGroup.Cmd && t.Name == "set_off");
        var config = CommandInputBehaviour.InputsOf(cm) ?? CommandInputConfig.Empty;
        var generated = CommandInputBehaviour.Tags(config).Select(t => $"{t.Group.Code()}.{t.Name}").ToHashSet();
        var selectors = tags.Where(t => t.Group == TagGroup.Fin && t.DataType == TagDataType.Bool && !generated.Contains($"FIN.{t.Name}"))
            .Select(t => $"FIN.{t.Name}").Order(StringComparer.Ordinal).ToList();
        var problems = CommandInputBehaviour.Problems(config, type?.SingleCommands ?? [], hasPair, selectors.Contains);
        return new CommandInputsDto(cm.Id, project.GetPath(cm.Id), config, type?.DefaultCommandInputs, hasPair, type?.SingleCommands ?? [],
            selectors, project.UnitOf(cm.Id) is not null, problems);
    }
}
