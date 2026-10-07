using System.Text.RegularExpressions;

namespace Builder.Core.Model;

public static partial class NameRules
{
    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex AllowedCharacters();

    public static string? Check(string? name, int maxLength)
    {
        if (string.IsNullOrEmpty(name))
            return "A name is required.";
        if (name.Length > maxLength)
            return $"A name can have at most {maxLength} characters.";
        if (!AllowedCharacters().IsMatch(name))
            return "A name can only contain letters, digits and underscores.";
        return null;
    }
}
