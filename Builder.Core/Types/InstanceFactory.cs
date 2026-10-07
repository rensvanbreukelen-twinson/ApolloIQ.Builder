using Builder.Core.Model;
using Builder.Core.Tags;

namespace Builder.Core.Types;

public sealed record DeletionSummary(int Folders, int ControlModules, int Tags);

public static class InstanceFactory
{
    public static ControlModule Create(Project project, CmLibrary library, Guid blueprintId, string name, Guid? parentId)
    {
        var type = library.Find(blueprintId)
                   ?? throw new ProjectException(ProjectErrors.NotFound, $"Blueprint {blueprintId} does not exist or has errors.");
        if (type.IsUnit)
            throw new ProjectException(ProjectErrors.InvalidUnit, $"{type.Name} is a Unit or Equipment module blueprint; add it as a Unit.");
        project.CheckNewChild(name, parentId, ObjectKind.ControlModule);
        var cm = project.AddControlModule(name, parentId, type.Id, type.Version);
        try
        {
            foreach (var template in type.ExpandTags())
                project.AddTag(cm.Id, template.ToDefinition());
            if (type.DefaultCommandInputs is { } inputs)
                CommandInputBehaviour.Configure(project, library, cm.Id, inputs);
        }
        catch
        {
            project.Delete(cm.Id);
            throw;
        }
        return cm;
    }

    public static UnitInstance CreateUnit(Project project, CmLibrary library, Guid blueprintId, string name, Guid? parentId)
    {
        var type = library.Find(blueprintId)
                   ?? throw new ProjectException(ProjectErrors.NotFound, $"Blueprint {blueprintId} does not exist or has errors.");
        if (!type.IsUnit)
            throw new ProjectException(ProjectErrors.InvalidUnit, $"{type.Name} is a CM blueprint.");
        project.CheckNewChild(name, parentId, ObjectKind.Unit, type.IsEquipmentModule);
        var unit = project.AddUnit(name, parentId, type.Id, type.Version, equipmentModule: type.IsEquipmentModule);
        foreach (var template in type.ExpandTags())
            project.AddTag(unit.Id, template.ToDefinition());
        if (type.DefaultCommandInputs is { } inputs)
        {
            foreach (var tag in CommandInputBehaviour.Tags(inputs))
                project.AddTag(unit.Id, tag);
            project.SetUnitCommandInputs(unit.Id, inputs);
        }
        return unit;
    }

    /// <summary>Puts a member that sits under a Unit or EM into a role: the given one, or the first free role for its blueprint.</summary>
    public static void Join(Project project, CmLibrary library, Guid memberId, string? role = null)
    {
        var member = project.Get(memberId);
        if (member.ParentId is not { } parentId || project.Find(parentId) is not UnitInstance unit)
            return;
        var memberType = BlueprintIdOf(member);
        var roles = library.Find(unit.BlueprintId)?.Roles ?? new Dictionary<string, Guid>();
        role ??= roles.Where(r => r.Value == memberType && !unit.RoleMembers.ContainsKey(r.Key)).Select(r => r.Key).FirstOrDefault();
        if (role is null)
            return;
        project.SetUnitMember(unit.Id, role, memberId);
        CommandInputBehaviour.SetUnitRow(project, library, memberId, member: true, unit.RowName);
    }

    public static Guid BlueprintIdOf(ProjectObject obj) => obj switch
    {
        ControlModule c => c.BlueprintId,
        UnitInstance u => u.BlueprintId,
        _ => Guid.Empty
    };

    public static DeletionSummary Summarize(Project project, Guid id)
    {
        var objects = project.Descendants(id);
        return new DeletionSummary(
            objects.Count(o => o.Kind == ObjectKind.Folder),
            objects.Count(o => o.Kind == ObjectKind.ControlModule),
            objects.Count(o => o.Kind == ObjectKind.Tag));
    }
}
