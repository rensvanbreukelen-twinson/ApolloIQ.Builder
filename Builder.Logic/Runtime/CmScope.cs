using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Expressions;

namespace Builder.Logic.Runtime;

internal sealed class CmScope(
    Project project,
    TagRegistry registry,
    CmLibrary library,
    TagMemory memory,
    ControlModule? cm,
    CmType? type,
    Action<Guid> dependsOn) : ISymbolScope
{
    private readonly string _path = cm is null ? "" : project.GetPath(cm.Id);

    public TagSymbol? ResolveTag(string reference, bool bracketed)
    {
        if (cm is not null && LooksLocal(reference))
        {
            if (registry.FindByPath($"{_path}.{reference}") is { } local)
                return Symbol(local, cm, type);
            if (Absent(reference) is { } absent)
                return absent;
            if (!bracketed)
                return null;
        }
        if (!bracketed)
            return null;
        if (registry.FindByPath(reference) is not { } tag)
            return null;
        switch (project.Find(tag.ParentId!.Value))
        {
            case ControlModule owner:
                if (owner.Id != cm?.Id)
                    dependsOn(owner.Id);
                return Symbol(tag, owner, library.Find(owner.TypeName));
            case UnitInstance unit:
                if (unit.Id != cm?.Id)
                    dependsOn(unit.Id);
                return Symbol(tag, ControlModule.ForUnit(unit), library.Find(unit.BlueprintName));
            default:
                return null;
        }
    }

    public AliasSymbol? ResolveAlias(string reference, bool bracketed)
    {
        if (cm is null && !(bracketed && reference.Contains('.')))
            return null;
        ControlModule target = cm!;
        CmType? targetType = type;
        var alias = reference;
        if (bracketed && reference.Contains('.'))
        {
            var dot = reference.LastIndexOf('.');
            var ownerPath = reference[..dot];
            alias = reference[(dot + 1)..];
            if (!string.Equals(ownerPath, _path, StringComparison.OrdinalIgnoreCase))
            {
                var owner = project.Objects.OfType<ControlModule>()
                    .FirstOrDefault(c => string.Equals(project.GetPath(c.Id), ownerPath, StringComparison.OrdinalIgnoreCase))
                    ?? project.Objects.OfType<UnitInstance>()
                        .Where(u => string.Equals(project.GetPath(u.Id), ownerPath, StringComparison.OrdinalIgnoreCase))
                        .Select(ControlModule.ForUnit).FirstOrDefault();
                if (owner is null)
                    return null;
                target = owner;
                targetType = library.Find(owner.TypeName);
            }
        }
        else if (reference.Contains('.'))
            return null;

        var ranges = AliasRanges(alias, targetType);
        if (ranges is null)
            return null;
        var stateTag = registry.FindByPath($"{project.GetPath(target.Id)}.STS.state");
        if (stateTag is null)
            return null;
        if (target.Id != cm?.Id)
            dependsOn(target.Id);
        return new AliasSymbol(Symbol(stateTag, target, targetType)!, ranges);
    }

    private static IReadOnlyList<(int, int)>? AliasRanges(string alias, CmType? type)
    {
        if (UniversalStates.StandardAliases.TryGetValue(alias, out var codes))
            return codes.Select(UniversalStates.Range).ToList();
        if (type is null || !type.Aliases.TryGetValue(alias, out var target))
            return null;
        if (UniversalStates.StandardAliases.TryGetValue(target, out var targetCodes))
            return targetCodes.Select(UniversalStates.Range).ToList();
        var state = type.States.FirstOrDefault(s => string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase));
        if (state is null)
            return null;
        return UniversalStates.IsUniversalCode(state.Code)
            ? [UniversalStates.Range(state.Code)]
            : [(state.Code, state.Code + 1)];
    }

    private TagSymbol? Absent(string reference)
    {
        var dot = reference.IndexOf('.');
        if (dot <= 0 || !TagGroupNames.TryParse(reference[..dot], out var group))
            return null;
        var name = reference[(dot + 1)..];
        if (type is null)
            return null;
        var template = type.Tags.FirstOrDefault(t => t.Group == group && t.Optional && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (template is null && group == TagGroup.Int)
            template = OptionalBoolInput(name);
        if (template is null && group == TagGroup.Set && name.StartsWith("invert_", StringComparison.OrdinalIgnoreCase)
            && OptionalBoolInput(name["invert_".Length..]) is { Source: TagSource.Hardwired })
            return new TagSymbol(-1, Expressions.ValueType.Bool, $"{_path}.{reference}", Constant: Value.False);
        if (template is null || !TagMemory.IsLogicType(template.DataType))
            return null;
        var value = InitialValues.From(template.AbsentValue ?? template.InitialValue, template.DataType, template.EnumType, type);
        return new TagSymbol(-1, TagMemory.LogicType(template.DataType), $"{_path}.{reference}", Constant: value);
    }

    private TagTemplate? OptionalBoolInput(string name) =>
        type!.Tags.FirstOrDefault(t => t.Group == TagGroup.Fin && t.Optional && t.DataType == TagDataType.Bool
                                      && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private TagSymbol? Symbol(Tag tag, ControlModule? owner, CmType? ownerType)
    {
        if (!TagMemory.IsLogicType(tag.DataType) || !memory.Slots.TryGetValue(tag.Id, out var slot))
            return null;
        var states = tag.Group == TagGroup.Sts && tag.EnumType == UniversalStates.EnumName
            ? (ownerType?.States ?? UniversalStates.All)
            : null;
        return new TagSymbol(slot, TagMemory.LogicType(tag.DataType), project.GetPath(tag.Id), states);
    }

    private static bool LooksLocal(string reference)
    {
        var dot = reference.IndexOf('.');
        if (dot <= 0 || !TagGroupNames.TryParse(reference[..dot], out var group) || reference[..dot] != reference[..dot].ToUpperInvariant())
            return false;
        var dots = reference.Count(c => c == '.');
        return dots == 1 || (dots == 2 && group == TagGroup.Alm);
    }
}
