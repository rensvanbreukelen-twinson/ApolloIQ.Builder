namespace Builder.Core.Types;

public sealed record SeverityBand(string Name, int From, int To);

public static class SeverityBands
{
    public const int Min = 0;
    public const int Max = 30;

    public static readonly SeverityBand Caution = new("Caution", Min, 9);
    public static readonly SeverityBand Warning = new("Warning", 10, 19);
    public static readonly SeverityBand Alarm = new("Alarm", 20, Max);

    public static readonly IReadOnlyList<SeverityBand> All = [Caution, Warning, Alarm];

    public static int DefaultSeverity => Alarm.From;

    public static bool IsValid(int severity) => severity is >= Min and <= Max;

    public static string BandOf(int severity) =>
        All.LastOrDefault(b => severity >= b.From)?.Name ?? Caution.Name;

    public static string RangeText =>
        $"Severity must be {Min} to {Max}: " + string.Join(", ", All.Select(b => $"{b.From}–{b.To} {b.Name.ToLowerInvariant()}")) + ".";
}
