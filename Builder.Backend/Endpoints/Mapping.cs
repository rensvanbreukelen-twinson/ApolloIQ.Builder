using Builder.Backend.Contracts;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;

namespace Builder.Backend.Endpoints;

public static class Mapping
{
    public static CmTypeDto ToDto(this CmType type) => new(type.Id, type.Name, type.Version.ToString(), type.Description, type.ExpandTags().Count);

    public static IReadOnlyList<TreeNodeDto> Tree(Project project, CmLibrary library, Guid? parentId) =>
        project.GetChildren(parentId)
            .Where(o => o.Kind != ObjectKind.Tag)
            .OrderBy(o => o.Kind switch { ObjectKind.Folder => 0, ObjectKind.Unit => 1, ObjectKind.ControlModule => 2, _ => 3 })
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Select(o => Node(project, library, o))
            .ToList();

    public static TreeNodeDto Node(Project project, CmLibrary library, ProjectObject obj)
    {
        var blueprintId = obj is Folder ? (Guid?)null : InstanceFactory.BlueprintIdOf(obj);
        var version = obj switch { ControlModule c => c.BlueprintVersion.ToString(), UnitInstance u => u.BlueprintVersion.ToString(), _ => null };
        return new TreeNodeDto(
            obj.Id,
            obj.Name,
            obj switch { Folder => "folder", UnitInstance { IsEquipmentModule: true } => "equipmentModule", UnitInstance => "unit", _ => "controlModule" },
            project.GetPath(obj.Id),
            obj.ParentId,
            blueprintId,
            blueprintId is { } id ? library.Find(id)?.Name ?? "(missing blueprint)" : null,
            version,
            obj is Folder ? 0 : project.GetChildren(obj.Id).Count(c => c.Kind == ObjectKind.Tag),
            obj is Folder or UnitInstance ? Tree(project, library, obj.Id) : []);
    }

    public static TagDto ToDto(this Tag tag, Project project) => new(
        tag.Id,
        project.GetPath(tag.Id),
        tag.Name,
        tag.Group.Code(),
        tag.DataType.ToString(),
        tag.Direction.ToString(),
        tag.TagKind.ToString(),
        tag.SymbolKey,
        tag.ParentId!.Value,
        project.GetPath(tag.ParentId.Value),
        tag.Unit,
        tag.EnumType,
        tag.InitialValue?.DeepClone(),
        tag.Description);
}
