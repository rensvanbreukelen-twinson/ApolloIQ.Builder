namespace Builder.Core.Types;

public sealed record AlarmDefinition(
    string Name,
    int Severity,
    IReadOnlyDictionary<string, string> Message,
    string Condition,
    string Latch,
    bool Plc,
    string? Requires,
    IReadOnlyList<TagTemplate> Parameters,
    string? OnTransition = null,
    bool Trip = false)
{
    public const int MaxSeverity = SeverityBands.Max;

    public static string Band(int severity) => SeverityBands.BandOf(severity);
}
