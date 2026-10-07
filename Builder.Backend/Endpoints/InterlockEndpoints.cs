using Builder.Backend.Contracts;
using Builder.Backend.Services;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Runtime;

namespace Builder.Backend.Endpoints;

public static class InterlockEndpoints
{
    public static void MapInterlockApi(this WebApplication app)
    {
        var project = app.MapGroup("/api/projects/{projectId:guid}");

        project.MapGet("/objects/{id:guid}/interlocks", (Guid projectId, Guid id, ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Read(p => Interlocks(p, library, p.Get(id))));

        project.MapPut("/objects/{id:guid}/interlocks", (Guid projectId, Guid id, SetInterlocksRequest request, ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Change(p =>
            {
                var rules = request.Interlocks.Select((r, i) => new InterlockRule
                {
                    TargetId = r.TargetId,
                    Kind = Parse<InterlockKind>(r.Kind, $"interlocks[{i}].kind"),
                    Condition = ExpressionReferences.ToStored(p, r.Condition ?? ""),
                    Text = r.Text ?? "",
                    Alarm = string.IsNullOrWhiteSpace(r.Alarm) ? null : r.Alarm.Trim(),
                    Severity = r.Severity ?? SeverityBands.DefaultSeverity,
                    Escalate = string.IsNullOrWhiteSpace(r.Escalate) ? TripEscalation.None : Parse<TripEscalation>(r.Escalate, $"interlocks[{i}].escalate")
                }).ToList();
                p.SetInterlocks(id, rules);
                return Interlocks(p, library, p.Get(id));
            }));

        project.MapPost("/objects/{id:guid}/interlocks/validate", (Guid projectId, Guid id, ValidateConditionRequest request, ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Read(p => new ValidateConditionResponse(LogicProgram.CheckInterlockCondition(p, library, id, request.Expression))));

        project.MapGet("/control-modules/{id:guid}/pic", (Guid projectId, Guid id, ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Read(p => Pic(p, library, p.Get<ControlModule>(id))));

        project.MapPut("/control-modules/{id:guid}/pic", (Guid projectId, Guid id, PicRequest request, ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Change(p =>
            {
                var cm = p.Get<ControlModule>(id);
                if (library.Find(cm.TypeName)?.IsPriorityInputControl != true)
                    throw new ProjectException(ProjectErrors.InvalidPic, $"{p.GetPath(cm.Id)} is not a PriorityInputControl.");
                var rows = request.Rows.Select((r, i) => new PicRow(r.Name?.Trim() ?? "",
                    Parse<PicSource>(r.Source, $"rows[{i}].source"), Parse<PicInputKind>(r.Kind, $"rows[{i}].kind"), r.On, r.Off,
                    string.IsNullOrWhiteSpace(r.InAuto) ? PicInAuto.Normal : Parse<PicInAuto>(r.InAuto, $"rows[{i}].inAuto"))).ToList();
                PicBehaviour.Configure(p, id, new PicConfiguration(request.OnLabel?.Trim() ?? "", request.OffLabel?.Trim() ?? "", rows));
                return Pic(p, library, cm);
            }));

        project.MapGet("/control-modules/{id:guid}/wires", (Guid projectId, Guid id, ProjectWorkspace workspace) =>
            workspace.Get(projectId).Read(p => Wires(p, p.Get<ControlModule>(id))));

        project.MapPut("/control-modules/{id:guid}/wires", (Guid projectId, Guid id, SetWiresRequest request, ProjectWorkspace workspace) =>
            workspace.Get(projectId).Change(p =>
            {
                var registry = new TagRegistry(p);
                var wires = request.Wires.Select((w, i) =>
                {
                    var source = Guid.TryParse(w.Source, out var sourceId) ? p.Find(sourceId) as Tag : registry.FindByPath(w.Source.Trim());
                    if (source is null)
                        throw new ProjectException(ProjectErrors.InvalidWire, $"Wire {i}: unknown source tag '{w.Source}'.", $"wires[{i}].source");
                    if (!Enum.TryParse<WireMode>(w.Mode, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
                        throw new ProjectException(ProjectErrors.InvalidWire, $"Wire {i}: unknown mode '{w.Mode}'. Use On, Off, Toggle, Maintained or Direct.", $"wires[{i}].mode");
                    return new CommandWire(source.Id, mode, string.IsNullOrWhiteSpace(w.Command) ? null : w.Command.Trim());
                }).ToList();
                p.SetCommandWires(id, wires);
                return Wires(p, p.Get<ControlModule>(id));
            }));

        project.MapGet("/control-modules/{id:guid}/alarms", (Guid projectId, Guid id, ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Read(p => Alarms(p, library, p.Get<ControlModule>(id))));

        project.MapPut("/control-modules/{id:guid}/alarms/{alarm}", (Guid projectId, Guid id, string alarm, SeverityRequest request,
            ProjectWorkspace workspace, CmLibrary library) =>
            workspace.Get(projectId).Change(p =>
            {
                var cm = p.Get<ControlModule>(id);
                if (library.Find(cm.TypeName)?.Alarms.Any(a => a.Name == alarm) != true)
                    throw new ProjectException(ProjectErrors.NotFound, $"{p.GetPath(cm.Id)} has no alarm '{alarm}'.");
                p.SetAlarmSeverity(id, alarm, request.Severity);
                return Alarms(p, library, cm);
            }));
    }

    private static T Parse<T>(string? text, string field) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value
            : throw new ProjectException(typeof(T) == typeof(InterlockKind) || typeof(T) == typeof(TripEscalation) ? ProjectErrors.InvalidInterlock : ProjectErrors.InvalidPic, $"Unknown value '{text}'. Use {string.Join(", ", Enum.GetNames<T>())}.", field);

    private static PicDto Pic(Project project, CmLibrary library, ControlModule cm)
    {
        if (library.Find(cm.TypeName)?.IsPriorityInputControl != true)
            throw new ProjectException(ProjectErrors.InvalidPic, $"{project.GetPath(cm.Id)} is not a PriorityInputControl.");
        var config = cm.Pic ?? PicConfiguration.Default;
        var path = project.GetPath(cm.Id);
        return new PicDto(config.OnLabel, config.OffLabel,
            config.Rows.Select(r => new PicRowDto(r.Name, r.Source.ToString(), r.Kind.ToString(), r.On, r.Off, r.InAuto.ToString(),
                r.On is null ? null : $"{path}.CMD.{r.OnCommand}", r.Off is null ? null : $"{path}.CMD.{r.OffCommand}")).ToList(),
            PicConfiguration.LabelPairs.Select(l => $"{l.On}/{l.Off}").ToList());
    }

    private static List<WireDto> Wires(Project project, ControlModule cm) =>
        cm.CommandWires.Select(w => new WireDto(w.SourceTagId, project.GetPath(w.SourceTagId), w.Mode.ToString(), w.Command)).ToList();

    private static List<AlarmDto> Alarms(Project project, CmLibrary library, ControlModule cm)
    {
        var type = library.Find(cm.TypeName);
        if (type is null)
            return [];
        var path = project.GetPath(cm.Id);
        return type.AlarmsFor(cm.OptionalTags).Select(a =>
        {
            var severity = cm.AlarmSeverities.TryGetValue(a.Name, out var custom) ? custom : a.Severity;
            return new AlarmDto(a.Name, severity, a.Severity, AlarmDefinition.Band(severity), a.Message, a.Condition, a.Latch,
                a.Plc ? "PLC" : "SCADA", a.OnTransition, a.Plc ? $"{path}.ALM.{a.Name}.active" : null);
        }).ToList();
    }

    public static bool HasInterlocks(Project project, ProjectObject obj) =>
        project.GetChildren(obj.Id).OfType<Tag>().Any(t => t.Group == TagGroup.Lok && t.Name == "can_on");

    internal static ObjectInterlocksDto Interlocks(Project project, CmLibrary library, ProjectObject obj)
    {
        if (obj is not (ControlModule or UnitInstance))
            throw new ProjectException(ProjectErrors.InvalidInterlock, $"{obj.Name} is not a control module, Equipment module or Unit.");
        var own = InterlockSources.OwnRules(obj).Select(r =>
        {
            var source = new InterlockSource(obj, r, false);
            var display = InterlockDisplay.Condition(project, source);
            var targetId = r.TargetId ?? obj.Id;
            return new InterlockRuleDto(r.TargetId, project.GetPath(targetId), r.Kind.ToString(), ExpressionReferences.ToDisplay(project, r.Condition), r.Text,
                ConditionText.Generate(display), r.Alarm, r.Severity, r.Escalate.ToString(),
                LogicProgram.CheckInterlockCondition(project, library, obj.Id, ExpressionReferences.ToDisplay(project, r.Condition)));
        }).ToList();
        var acting = InterlockSources.Targeting(project, library, obj.Id).Where(s => s.FromBlueprint || s.Owner.Id != obj.Id)
            .Select(s => new InheritedInterlockDto(s.Rule.Kind.ToString(), InterlockDisplay.Text(project, s), InterlockDisplay.Condition(project, s),
                project.GetPath(s.Owner.Id), s.FromBlueprint ? "blueprint" : "project", s.Rule.Alarm, s.Rule.Escalate.ToString())).ToList();
        var targets = new[] { obj }.Concat(project.Descendants(obj.Id).Where(o => o.Id != obj.Id && o is ControlModule or UnitInstance))
            .Select(o => new InterlockTargetDto(o.Id, project.GetPath(o.Id), HasInterlocks(project, o)))
            .ToList();
        return new ObjectInterlocksDto(obj.Id, project.GetPath(obj.Id), HasInterlocks(project, obj), own, acting, targets);
    }
}
