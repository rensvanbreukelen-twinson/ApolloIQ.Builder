using Builder.Core.Model;
using Builder.Core.Tags;

namespace Builder.Core.Types;

public sealed record DeletionSummary(int Folders, int ControlModules, int Tags);

public static class InstanceFactory
{
    public static ControlModule Create(Project project, CmLibrary library, string typeName, string name, Guid? parentId,
        IEnumerable<string>? optionalTags = null)
    {
        var type = library.Find(typeName)
                   ?? throw new ProjectException(ProjectErrors.NotFound, $"CM type '{typeName}' does not exist.");
        if (type.IsUnit)
            throw new ProjectException(ProjectErrors.InvalidUnit, $"{type.Name} is a Unit blueprint; add it as a Unit.");
        var selected = (optionalTags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var known = type.OptionalTags.Select(t => $"{t.Group.Code()}.{t.Name}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = selected.Where(s => !known.Contains(s)).ToList();
        if (unknown.Count > 0)
            throw new ProjectException(ProjectErrors.NotFound, $"'{string.Join("', '", unknown)}' is not an optional tag of {type.Name}.");

        project.CheckNewChild(name, parentId, ObjectKind.ControlModule);
        var canonical = type.OptionalTags.Select(t => $"{t.Group.Code()}.{t.Name}")
            .Where(key => selected.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList();
        var cm = project.AddControlModule(name, parentId, type.Name, type.Version, optionalTags: canonical);
        try
        {
            foreach (var template in type.ExpandTags(canonical))
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

    public static UnitInstance CreateUnit(Project project, CmLibrary library, string typeName, string name, Guid? parentId)
    {
        var type = library.Find(typeName)
                   ?? throw new ProjectException(ProjectErrors.NotFound, $"Blueprint '{typeName}' does not exist.");
        if (!type.IsUnit)
            throw new ProjectException(ProjectErrors.InvalidUnit, $"{type.Name} is a CM blueprint.");
        project.CheckNewChild(name, parentId, ObjectKind.Unit, type.IsEquipmentModule);
        var unit = project.AddUnit(name, parentId, type.Name, type.Version, equipmentModule: type.IsEquipmentModule);
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

    public static void Join(Project project, CmLibrary library, Guid memberId, string? role = null)
    {
        var member = project.Get(memberId);
        if (member.ParentId is not { } parentId || project.Find(parentId) is not UnitInstance unit)
            return;
        var memberType = member switch { ControlModule c => c.TypeName, UnitInstance u => u.BlueprintName, _ => "" };
        var roles = library.Find(unit.BlueprintName)?.Roles ?? new Dictionary<string, string>();
        role ??= roles.Where(r => string.Equals(r.Value, memberType, StringComparison.OrdinalIgnoreCase) && !unit.RoleMembers.ContainsKey(r.Key))
            .Select(r => r.Key).FirstOrDefault();
        if (role is null)
            return;
        project.SetUnitMember(unit.Id, role, memberId);
        CommandInputBehaviour.SetUnitRow(project, library, memberId, member: true, unit.RowName);
    }

    public static DeletionSummary Summarize(Project project, Guid id)
    {
        var objects = project.Descendants(id);
        return new DeletionSummary(
            objects.Count(o => o.Kind == ObjectKind.Folder),
            objects.Count(o => o.Kind == ObjectKind.ControlModule),
            objects.Count(o => o.Kind == ObjectKind.Tag));
    }
}
