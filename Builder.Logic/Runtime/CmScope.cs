using ApolloIQ.Core.Conventions;
using ApolloIQ.Core.Expressions;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;

namespace Builder.Logic.Runtime;

/// <summary>
/// Resolves the bracketed references of an expression for one CM (or EM / Unit):
/// relative names (<c>[FIN.x]</c>, <c>[ALM.Trip1.active]</c>, <c>[is_running]</c>) are the CM's own; anything else is a path in
/// the project (<c>[PMS.CB_GEN1.STS.state]</c>, <c>[PMS.GEN1.is_running]</c>).
/// </summary>
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
        if (!bracketed)
            return null;
        if (cm is not null && LooksLocal(reference))
            return registry.FindByPath($"{_path}.{reference}") is { } local ? Symbol(local, type) : null;
        if (registry.FindByPath(reference) is not { } tag)
            return null;
        switch (project.Find(tag.ParentId!.Value))
        {
            case ControlModule owner:
                if (owner.Id != cm?.Id)
                    dependsOn(owner.Id);
                return Symbol(tag, library.Find(owner.BlueprintId));
            case UnitInstance unit:
                if (unit.Id != cm?.Id)
                    dependsOn(unit.Id);
                return Symbol(tag, library.Find(unit.BlueprintId));
            default:
                return null;
        }
    }

    public AliasSymbol? ResolveAlias(string reference, bool bracketed)
    {
        if (!bracketed)
            return null;
        var dot = reference.LastIndexOf('.');
        var alias = reference[(dot + 1)..];
        ProjectObject? target = cm is null ? null : project.Find(cm.Id);
        CmType? targetType = type;
        if (dot > 0)
        {
            var ownerPath = reference[..dot];
            target = project.Objects.FirstOrDefault(o => o is ControlModule or UnitInstance
                                                         && string.Equals(project.GetPath(o.Id), ownerPath, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return null;
            targetType = library.Find(InstanceFactory.BlueprintIdOf(target));
        }
        if (target is null)
            return null;

        var ranges = AliasRanges(alias, targetType);
        if (ranges is null)
            return null;
        var stateTag = registry.FindByPath($"{project.GetPath(target.Id)}.STS.state");
        if (stateTag is null)
            return null;
        if (target.Id != cm?.Id)
            dependsOn(target.Id);
        return Symbol(stateTag, targetType) is { } symbol ? new AliasSymbol(symbol, ranges) : null;
    }

    /// <summary>A standard alias (is_running …) or a blueprint alias (to a state or a standard alias).</summary>
    public static IReadOnlyList<(int, int)>? AliasRanges(string alias, CmType? type)
    {
        if (UniversalStates.StandardAliases.TryGetValue(alias, out var codes))
            return codes.Select(UniversalStates.Range).ToList();
        if (type is null || !type.Aliases.TryGetValue(alias, out var target))
            return null;
        if (UniversalStates.StandardAliases.TryGetValue(target, out var targetCodes))
            return targetCodes.Select(UniversalStates.Range).ToList();
        var state = type.States.FirstOrDefault(s => string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase));
        return state is null ? null : [state.Range];
    }

    private TagSymbol? Symbol(Tag tag, CmType? ownerType)
    {
        if (!TagMemory.IsLogicType(tag.DataType) || !memory.Slots.TryGetValue(tag.Id, out var slot))
            return null;
        var states = tag.Group == TagGroup.Sts && tag.Name == "state"
            ? ownerType?.States ?? UniversalStates.All
            : null;
        return new TagSymbol(slot, TagMemory.LogicType(tag.DataType), project.GetPath(tag.Id), states);
    }

    /// <summary><c>GROUP.name</c> or <c>ALM.alarm.field</c>, with an upper-case tag group.</summary>
    public static bool LooksLocal(string reference)
    {
        var dot = reference.IndexOf('.');
        if (dot <= 0 || !TagGroupNames.TryParse(reference[..dot], out var group) || reference[..dot] != reference[..dot].ToUpperInvariant())
            return false;
        var dots = reference.Count(c => c == '.');
        return dots == 1 || (dots == 2 && group == TagGroup.Alm);
    }
}
