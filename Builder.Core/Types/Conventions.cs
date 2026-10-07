namespace Builder.Core.Types;

public sealed record ConventionsInfo(
    int CategorySize,
    int UnavailableCode,
    IReadOnlyList<StateCategory> States,
    int SeverityMin,
    int SeverityMax,
    IReadOnlyList<SeverityBand> SeverityBands,
    int PriorityMin,
    int PriorityMax,
    double MaxDebounceSeconds,
    double DefaultDebounceSeconds,
    int DefaultSeverity);

public static class Conventions
{
    public static ConventionsInfo Current { get; } = new(
        UniversalStates.CategorySize,
        UniversalStates.Unavailable,
        UniversalStates.Categories,
        SeverityBands.Min,
        SeverityBands.Max,
        SeverityBands.All,
        CommandInputLimits.PriorityMin,
        CommandInputLimits.PriorityMax,
        CommandInputLimits.MaxDebounceSeconds,
        CommandInputLimits.DefaultDebounceSeconds,
        SeverityBands.DefaultSeverity);
}
