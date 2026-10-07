using System.Text.RegularExpressions;
using Builder.Core.Tags;

namespace Builder.Core.Model;

/// <summary>
/// Turns a condition into the text the operator reads on the interlock list, for example
/// <c>![PMS.GEN1.is_running] &amp;&amp; [PMS.CB1.STS.state] == Closed</c> → "GEN1 not running and CB1 Closed".
/// </summary>
public static partial class ConditionText
{
    public static string Generate(string display)
    {
        var text = NegatedReference().Replace(display, m => Describe(m.Groups[1].Value, negate: true));
        text = StateComparison().Replace(text, m =>
        {
            var owner = Owner(m.Groups[1].Value);
            return $"{(owner.Length == 0 ? "" : owner + " ")}{(m.Groups[2].Value == "!=" ? "not " : "")}{m.Groups[3].Value}";
        });
        text = Reference().Replace(text, m => Describe(m.Groups[1].Value, negate: false));
        text = Operator().Replace(text, m => m.Value switch
        {
            "&&" => "and",
            "||" => "or",
            "^" => "xor",
            "!" => "not ",
            "==" => "=",
            "!=" => "≠",
            ">=" => "≥",
            "<=" => "≤",
            _ => m.Value
        });
        text = Keyword().Replace(text, m => m.Value.ToLowerInvariant());
        return Spaces().Replace(text, " ").Trim();
    }

    /// <summary>The object that owns a path (the segment before the tag group); empty for a relative path.</summary>
    private static string Owner(string path)
    {
        var parts = path.Split('.');
        var group = Array.FindIndex(parts, p => TagGroupNames.TryParse(p, out _));
        return group > 0 ? parts[group - 1] : group == 0 ? "" : parts.Length > 1 ? parts[^2] : path;
    }

    private static string Describe(string path, bool negate)
    {
        var parts = path.Split('.');
        var not = negate ? "not " : "";
        var group = Array.FindIndex(parts, p => TagGroupNames.TryParse(p, out _));
        if (group >= 0 && group < parts.Length - 1)
        {
            var owner = group > 0 ? parts[group - 1] + " " : "";
            var name = string.Join(" ", parts[(group + 1)..]).Replace('_', ' ');
            if (parts[group].Equals("ALM", StringComparison.OrdinalIgnoreCase))
                return $"{owner}{parts[group + 1]} alarm {(negate ? "not active" : "active")}";
            return $"{owner}{not}{name}";
        }
        var alias = parts[^1].StartsWith("is_", StringComparison.OrdinalIgnoreCase) ? parts[^1][3..] : parts[^1];
        return parts.Length > 1 ? $"{parts[^2]} {not}{alias.Replace('_', ' ')}" : $"{not}{alias.Replace('_', ' ')}";
    }

    [GeneratedRegex(@"!\s*\[([^\[\]]+)\]")]
    private static partial Regex NegatedReference();

    [GeneratedRegex(@"\[((?:[^\[\]]+\.)?STS\.state)\]\s*(==|!=)\s*([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex StateComparison();

    [GeneratedRegex(@"\[([^\[\]]+)\]")]
    private static partial Regex Reference();

    [GeneratedRegex(@"&&|\|\||!=|==|>=|<=|!|\^")]
    private static partial Regex Operator();

    [GeneratedRegex(@"\b(TRUE|FALSE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Keyword();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
