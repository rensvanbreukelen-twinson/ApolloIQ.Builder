using Builder.Backend.Contracts;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;

namespace Builder.Backend.Endpoints;

public static class Mapping
{
    public static CmTypeDto ToDto(this CmType type) => new(
        type.Name,
        type.Version,
        type.Description,
        type.ExpandTags().Count,
        type.OptionalTags.Select(t => new OptionalTagDto(Key(t), t.Description, type.ExpandTags([Key(t)]).Count - type.ExpandTags().Count)).ToList());

    public static string Key(TagTemplate tag) => $"{tag.Group.Code()}.{tag.Name}";

    public static IReadOnlyList<TreeNodeDto> Tree(Project project, Guid? parentId) =>
        project.GetChildren(parentId)
            .Where(o => o.Kind != ObjectKind.Tag)
            .OrderBy(o => o.Kind switch { ObjectKind.Folder => 0, ObjectKind.Unit => 1, ObjectKind.ControlModule => 2, _ => 3 })
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Select(o => Node(project, o))
            .ToList();

    public static TreeNodeDto Node(Project project, ProjectObject obj)
    {
        var cm = obj as ControlModule;
        return new TreeNodeDto(
            obj.Id,
            obj.Name,
            obj switch { Folder => "folder", UnitInstance { IsEquipmentModule: true } => "equipmentModule", UnitInstance => "unit", _ => "controlModule" },
            project.GetPath(obj.Id),
            obj.ParentId,
            cm?.TypeName ?? (obj as UnitInstance)?.BlueprintName,
            cm?.TypeVersion ?? (obj as UnitInstance)?.BlueprintVersion,
            obj is Folder ? 0 : project.GetChildren(obj.Id).Count(c => c.Kind == ObjectKind.Tag),
            obj is Folder or UnitInstance ? Tree(project, obj.Id) : []);
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
