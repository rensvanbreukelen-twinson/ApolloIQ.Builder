using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Logic.Blueprints;

namespace Builder.Backend.Services;

public static class UnitSupport
{
    public static UnitInstance Create(Project project, Blueprint blueprint, string name, Guid? parentId, CmLibrary? library = null, BlueprintStore? blueprints = null)
    {
        var equipmentModule = blueprint.Kind == BlueprintKind.EM;
        project.CheckNewChild(name, parentId, ObjectKind.Unit, equipmentModule);
        var unit = project.AddUnit(name, parentId, blueprint.Name, blueprint.Version, equipmentModule: equipmentModule);
        var type = BlueprintTypes.ToCmType(blueprint);
        foreach (var template in type.ExpandTags())
            project.AddTag(unit.Id, template.ToDefinition());
        if (type.DefaultCommandInputs is { } inputs)
        {
            foreach (var tag in CommandInputBehaviour.Tags(inputs))
                project.AddTag(unit.Id, tag);
            project.SetUnitCommandInputs(unit.Id, inputs);
        }
        if (library is not null && blueprints is not null)
            Entered(project, library, blueprints, unit.Id);
        return unit;
    }

    public static string TypeOf(ProjectObject obj) => obj switch
    {
        ControlModule c => c.TypeName,
        UnitInstance u => u.BlueprintName,
        _ => ""
    };

    public static void CheckRole(Project project, BlueprintStore blueprints, UnitInstance unit, string role, Guid? memberId)
    {
        var definition = blueprints.Find(unit.BlueprintName)?.Roles.FirstOrDefault(r => r.Name == role)
            ?? throw new ProjectException(ProjectErrors.InvalidUnit, $"{unit.Name} has no role '{role}'.", "role");
        if (memberId is { } id && project.Get(id) is var member && !string.Equals(TypeOf(member), definition.Blueprint, StringComparison.OrdinalIgnoreCase))
            throw new ProjectException(ProjectErrors.InvalidUnit,
                $"Role {role} needs a {definition.Blueprint}; {member.Name} is a {TypeOf(member)}.", "controlModuleId");
    }

    public static void SetMember(Project project, CmLibrary library, Guid unitId, string role, Guid? memberId)
    {
        var unit = project.Get<UnitInstance>(unitId);
        var previous = unit.RoleMembers.TryGetValue(role, out var old) ? old : (Guid?)null;
        if (memberId is { } id && CommandInputBehaviour.InputsOf(project.Get(id))?.Rows.Any(r => r.Kind == InputKind.Switch) == true)
            throw new ProjectException(ProjectErrors.InvalidUnit,
                $"{project.Get(id).Name} is controlled by a maintained switch and cannot be a member (G-143).", "controlModuleId");
        project.SetUnitMember(unitId, role, memberId);
        if (previous is { } p && p != memberId && project.UnitOf(p) is null)
            CommandInputBehaviour.SetUnitRow(project, library, p, member: false);
        if (memberId is { } added)
            CommandInputBehaviour.SetUnitRow(project, library, added, member: true, unit.RowName);
    }

    public static void Move(Project project, CmLibrary library, BlueprintStore blueprints, Guid id, Guid? parentId)
    {
        var wasMember = project.UnitOf(id) is not null;
        project.Move(id, parentId);
        if (wasMember && project.UnitOf(id) is null && project.Get(id) is ControlModule or UnitInstance)
            CommandInputBehaviour.SetUnitRow(project, library, id, member: false);
        Entered(project, library, blueprints, id);
    }

    public static void Entered(Project project, CmLibrary library, BlueprintStore blueprints, Guid id)
    {
        var member = project.Get(id);
        if (member is not (ControlModule or UnitInstance) || member.ParentId is not { } parent || project.Find(parent) is not UnitInstance unit)
            return;
        if (unit.RoleMembers.Values.Contains(id))
            return;
        var role = blueprints.Find(unit.BlueprintName)?.Roles.FirstOrDefault(r =>
            string.Equals(r.Blueprint, TypeOf(member), StringComparison.OrdinalIgnoreCase) && !unit.RoleMembers.ContainsKey(r.Name));
        if (role is not null)
            SetMember(project, library, unit.Id, role.Name, id);
    }
}
