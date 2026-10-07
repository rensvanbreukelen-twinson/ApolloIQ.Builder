using System.Text.RegularExpressions;
using Builder.Core.Tags;

namespace Builder.Core.Model;

public static partial class ExpressionReferences
{
    public static string ToStored(Project project, string display)
    {
        var registry = new TagRegistry(project);
        return BracketPattern().Replace(display, match =>
        {
            var reference = match.Groups[1].Value.Trim();
            if (registry.FindByPath(reference) is { } tag)
                return $"{{{tag.Id:D}}}";
            var dot = reference.LastIndexOf('.');
            if (dot > 0 && FindByPath(project, reference[..dot]) is ControlModule or UnitInstance)
                return $"{{{FindByPath(project, reference[..dot])!.Id:D}}}.{reference[(dot + 1)..]}";
            return match.Value;
        });
    }

    public static string ToDisplay(Project project, string stored) =>
        GuidPattern().Replace(stored, match =>
        {
            var id = Guid.Parse(match.Groups[1].Value);
            var suffix = match.Groups[2].Value;
            return project.Find(id) switch
            {
                Tag tag when suffix.Length == 0 => $"[{project.GetPath(tag.Id)}]",
                ControlModule or UnitInstance when suffix.Length > 0 => $"[{project.GetPath(id)}{suffix}]",
                _ => $"[deleted:{id:N}{suffix}]"
            };
        });

    public static IReadOnlyList<Guid> References(string stored) =>
        GuidPattern().Matches(stored).Select(m => Guid.Parse(m.Groups[1].Value)).Distinct().ToList();

    private static ProjectObject? FindByPath(Project project, string path) =>
        project.Objects.FirstOrDefault(o => o is not Tag && string.Equals(project.GetPath(o.Id), path, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\[([^\[\]]+)\]")]
    private static partial Regex BracketPattern();

    [GeneratedRegex(@"\{([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}(\.[A-Za-z_][A-Za-z0-9_]*)?")]
    private static partial Regex GuidPattern();
}
