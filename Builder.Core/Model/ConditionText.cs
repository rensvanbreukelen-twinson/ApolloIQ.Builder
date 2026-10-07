using System.Text.RegularExpressions;
using Builder.Core.Tags;

namespace Builder.Core.Model;

public static partial class ConditionText
{
    public static string Generate(string display)
    {
        var text = NegatedReference().Replace(display, m => Describe(m.Groups[1].Value, negate: true));
        text = StateComparison().Replace(text, m => $"{Owner(m.Groups[1].Value)} {(m.Groups[2].Value == "<>" ? "not " : "")}{m.Groups[3].Value}");
        text = Reference().Replace(text, m => Describe(m.Groups[1].Value, negate: false));
        text = Operator().Replace(text, m => m.Value.ToUpperInvariant() switch
        {
            "AND" => "and",
            "OR" => "or",
            "XOR" => "xor",
            "NOT" => "not",
            "TRUE" => "true",
            "FALSE" => "false",
            _ => m.Value
        });
        text = text.Replace(">=", "≥", StringComparison.Ordinal).Replace("<=", "≤", StringComparison.Ordinal)
            .Replace("<>", "≠", StringComparison.Ordinal);
        return Spaces().Replace(text, " ").Trim();
    }

    private static string Owner(string path)
    {
        var parts = path.Split('.');
        var group = Array.FindIndex(parts, p => TagGroupNames.TryParse(p, out _));
        return group > 0 ? parts[group - 1] : parts.Length > 1 ? parts[^2] : path;
    }

    private static string Describe(string path, bool negate)
    {
        var parts = path.Split('.');
        var not = negate ? "not " : "";
        var group = Array.FindIndex(parts, p => TagGroupNames.TryParse(p, out _));
        if (group > 0)
        {
            var owner = parts[group - 1];
            var name = string.Join(" ", parts[(group + 1)..]).Replace('_', ' ');
            if (parts[group].Equals("ALM", StringComparison.OrdinalIgnoreCase))
                return $"{owner} {parts[group + 1]} alarm {(negate ? "not active" : "active")}";
            return $"{owner} {not}{name}";
        }
        if (parts.Length > 1)
        {
            var alias = parts[^1].StartsWith("is_", StringComparison.OrdinalIgnoreCase) ? parts[^1][3..] : parts[^1];
            return $"{parts[^2]} {not}{alias.Replace('_', ' ')}";
        }
        return $"{not}{path}";
    }

    [GeneratedRegex(@"\bNOT\s+\[([^\[\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex NegatedReference();

    [GeneratedRegex(@"\[([^\[\]]+\.STS\.state)\]\s*(=|<>)\s*([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex StateComparison();

    [GeneratedRegex(@"\[([^\[\]]+)\]")]
    private static partial Regex Reference();

    [GeneratedRegex(@"\b(AND|OR|XOR|NOT|TRUE|FALSE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Operator();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
