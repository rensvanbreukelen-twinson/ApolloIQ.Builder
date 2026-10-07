using Builder.Core.Types;

namespace Builder.Core.Model;

/// <summary>An interlock as it applies to one target: who defines it, and whether it comes from the owner's blueprint.</summary>
public sealed record InterlockSource(ProjectObject Owner, InterlockRule Rule, bool FromBlueprint);

/// <summary>
/// Collects the interlocks that act on an object (G-172). The order is fixed, because the status bits follow it:
/// the object itself first, then each container above it (nearest first); per owner the blueprint list, then the instance list.
/// </summary>
public static class InterlockSources
{
    public static IReadOnlyList<InterlockSource> Targeting(Project project, CmLibrary library, Guid targetId)
    {
        var result = new List<InterlockSource>();
        foreach (var owner in SelfAndContainers(project, targetId))
        {
            var type = library.Find(TypeName(owner));
            foreach (var rule in InterlockRule.WithAlarmNames(type?.Interlocks ?? []))
            {
                if (ResolveRole(project, owner, rule.Target)?.Id == targetId)
                    result.Add(new InterlockSource(owner, rule, true));
            }
            foreach (var rule in OwnRules(owner))
            {
                if ((rule.TargetId ?? owner.Id) == targetId)
                    result.Add(new InterlockSource(owner, rule, false));
            }
        }
        return result;
    }

    /// <summary>Every interlock an object defines, blueprint first, with the object it acts on (null when the role is not filled).</summary>
    public static IReadOnlyList<(InterlockSource Source, ProjectObject? Target)> DefinedBy(Project project, CmLibrary library, ProjectObject owner)
    {
        var result = new List<(InterlockSource, ProjectObject?)>();
        foreach (var rule in InterlockRule.WithAlarmNames(library.Find(TypeName(owner))?.Interlocks ?? []))
            result.Add((new InterlockSource(owner, rule, true), ResolveRole(project, owner, rule.Target)));
        foreach (var rule in OwnRules(owner))
            result.Add((new InterlockSource(owner, rule, false), rule.TargetId is { } id ? project.Find(id) : owner));
        return result;
    }

    public static IReadOnlyList<InterlockRule> OwnRules(ProjectObject owner) => owner switch
    {
        ControlModule cm => cm.Interlocks,
        UnitInstance unit => unit.Interlocks,
        _ => []
    };

    public static string TypeName(ProjectObject obj) => obj switch
    {
        ControlModule cm => cm.TypeName,
        UnitInstance unit => unit.BlueprintName,
        _ => ""
    };

    /// <summary>Follows a role path ("", "BREAKER", "GEN1.BREAKER") from an owner through the members of its Units and EMs.</summary>
    public static ProjectObject? ResolveRole(Project project, ProjectObject owner, string? rolePath)
    {
        ProjectObject? current = owner;
        if (string.IsNullOrWhiteSpace(rolePath))
            return current;
        foreach (var role in rolePath.Trim().Split('.'))
        {
            if (current is not UnitInstance unit || !unit.RoleMembers.TryGetValue(role, out var id))
                return null;
            current = project.Find(id);
        }
        return current;
    }

    /// <summary>The object and the Units / EMs it is a member of, nearest first.</summary>
    public static IEnumerable<ProjectObject> SelfAndContainers(Project project, Guid id)
    {
        var seen = new HashSet<Guid>();
        for (var current = project.Find(id); current is not null && seen.Add(current.Id); current = project.UnitOf(current.Id))
            yield return current;
    }

    /// <summary>The container a trip escalates to: the nearest EM or Unit above the target.</summary>
    public static UnitInstance? EscalationTarget(Project project, Guid targetId, TripEscalation escalate) => escalate switch
    {
        TripEscalation.EM => SelfAndContainers(project, targetId).Skip(1).OfType<UnitInstance>().FirstOrDefault(u => u.IsEquipmentModule),
        TripEscalation.Unit => SelfAndContainers(project, targetId).Skip(1).OfType<UnitInstance>().FirstOrDefault(u => !u.IsEquipmentModule),
        _ => null
    };
}

public static partial class InterlockDisplay
{
    [System.Text.RegularExpressions.GeneratedRegex(@"\{role:([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\}")]
    private static partial System.Text.RegularExpressions.Regex RoleToken();

    /// <summary>The condition with absolute bracketed paths, as the HMI and the condition text generator read it.</summary>
    public static string Condition(Project project, InterlockSource source)
    {
        if (!source.FromBlueprint)
            return ExpressionReferences.ToDisplay(project, source.Rule.Condition);
        var ownerPath = project.GetPath(source.Owner.Id);
        var text = RoleToken().Replace(source.Rule.Condition, m =>
            InterlockSources.ResolveRole(project, source.Owner, m.Groups[1].Value) is { } target ? project.GetPath(target.Id) : $"unfilled_{m.Groups[1].Value}");
        return LocalTag().Replace(text, m => $"[{ownerPath}.{m.Value}]");
    }

    public static string Text(Project project, InterlockSource source) =>
        string.IsNullOrWhiteSpace(source.Rule.Text) ? ConditionText.Generate(Condition(project, source)) : source.Rule.Text;

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w.\[\]])(?:FIN|CMD|OUT|LOK|PAR|SET|PMT|STS|INT)\.[A-Za-z_]\w*(?![\w.\]])")]
    private static partial System.Text.RegularExpressions.Regex LocalTag();
}
