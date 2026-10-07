using System.Text.RegularExpressions;
using ApolloIQ.Core.Expressions;
using Builder.Core.Tags;

namespace Builder.Core.Model;

/// <summary>
/// Project-level conditions are stored with ids instead of paths, so they survive renames and moves:
/// <c>[PMS.GEN1.FIN.running]</c> is stored as <c>[{tag id}]</c>, <c>[PMS.GEN1.is_running]</c> as <c>[{object id}.is_running]</c>.
/// Relative references (<c>[FIN.x]</c>) stay as they are.
/// </summary>
public static partial class ExpressionReferences
{
    public static string ToStored(Project project, string display)
    {
        var registry = new TagRegistry(project);
        return Expression.RewriteReferences(display, reference =>
        {
            if (registry.FindByPath(reference) is { } tag)
                return $"{{{tag.Id:D}}}";
            var dot = reference.LastIndexOf('.');
            if (dot > 0 && FindByPath(project, reference[..dot]) is ControlModule or UnitInstance)
                return $"{{{FindByPath(project, reference[..dot])!.Id:D}}}{reference[dot..]}";
            return reference;
        });
    }

    public static string ToDisplay(Project project, string stored) =>
        Expression.RewriteReferences(stored, reference =>
        {
            if (StoredPattern().Match(reference) is not { Success: true } match)
                return reference;
            var id = Guid.Parse(match.Groups[1].Value);
            var suffix = match.Groups[2].Value;
            return project.Find(id) switch
            {
                Tag tag when suffix.Length == 0 => project.GetPath(tag.Id),
                ControlModule or UnitInstance when suffix.Length > 0 => $"{project.GetPath(id)}{suffix}",
                _ => $"deleted:{id:N}{suffix}"
            };
        });

    /// <summary>The objects and tags a stored condition refers to.</summary>
    public static IReadOnlyList<Guid> References(string stored) =>
        Expression.References(stored).Select(r => StoredPattern().Match(r)).Where(m => m.Success)
            .Select(m => Guid.Parse(m.Groups[1].Value)).Distinct().ToList();

    private static ProjectObject? FindByPath(Project project, string path) =>
        project.Objects.FirstOrDefault(o => o is not Tag && string.Equals(project.GetPath(o.Id), path, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^\{([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}(\.[A-Za-z_][A-Za-z0-9_]*)?$")]
    private static partial Regex StoredPattern();
}
