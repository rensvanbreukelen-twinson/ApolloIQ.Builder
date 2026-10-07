namespace Builder.Core.Tags;

public enum TagGroup
{
    Fin,
    Cmd,
    Out,
    Lok,
    Par,
    Set,
    Pmt,
    Sts,
    Int,
    Alm
}

public static class TagGroupNames
{
    public static string Code(this TagGroup group) => group.ToString().ToUpperInvariant();

    public static bool TryParse(string? code, out TagGroup group) =>
        Enum.TryParse(code, ignoreCase: true, out group) && Enum.IsDefined(group);
}
