using System.Text.Json;

namespace Builder.Simulator.Scenarios;

public abstract record ScenarioStep(string Location);

public sealed record SetStep(IReadOnlyDictionary<string, JsonElement> Values, bool Force, string Location) : ScenarioStep(Location);

public sealed record ReleaseStep(IReadOnlyList<string> Tags, string Location) : ScenarioStep(Location);

public sealed record QualityStep(IReadOnlyList<string> Tags, bool Bad, string Location) : ScenarioStep(Location);

public sealed record Duration(int? Cycles, double? Seconds)
{
    public int ToCycles(double cycleSeconds) => Cycles ?? (int)Math.Round(Seconds!.Value / cycleSeconds);
}

public sealed record RunStep(Duration Duration, string Location) : ScenarioStep(Location);

public sealed record UntilStep(string Condition, Duration Within, string Location) : ScenarioStep(Location);

public sealed record ExpectStep(IReadOnlyList<string> Conditions, string Location) : ScenarioStep(Location);

public sealed record ScenarioWire(string Source, string Mode, string? Command);

/// <summary>An instance a scenario creates; <see cref="Type"/> is the blueprint name.</summary>
public sealed record ScenarioInstance(string Name, string Type, IReadOnlyList<ScenarioWire>? Wires = null,
    System.Text.Json.JsonElement? CommandInputs = null, string? Parent = null, string? Role = null);

public sealed record Scenario(string Name, string Description, IReadOnlyList<ScenarioInstance> Instances, IReadOnlyList<ScenarioStep> Steps);

public sealed record ScenarioFile(string FileName, string Type, IReadOnlyList<Scenario> Scenarios);

public sealed record ScenarioError(string File, string Location, string Message)
{
    public override string ToString() => $"{File} {Location}: {Message}";
}

public sealed class ScenarioException(IReadOnlyList<ScenarioError> errors) : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<ScenarioError> Errors { get; } = errors;
}

public sealed record ScenarioFailure(long Cycle, string Location, string Message);

public sealed record TraceEntry(long Cycle, IReadOnlyDictionary<string, int> States, IReadOnlyDictionary<string, object?> Changes);

public sealed record TransitionHit(string ControlModule, string Type, int Index, string Name, int From, int To);

public sealed record ScenarioResult(
    string File,
    string Type,
    string Name,
    bool Passed,
    long Cycles,
    IReadOnlyList<ScenarioFailure> Failures,
    IReadOnlyList<string> LogicErrors,
    IReadOnlyList<TransitionHit> Transitions,
    IReadOnlyList<TraceEntry> Trace);

public sealed record TransitionCoverage(int Index, string Name, string From, string To, int Hits);

public sealed record TypeCoverage(string Type, int Covered, int Total, IReadOnlyList<TransitionCoverage> Transitions);

public sealed record ScenarioReport(IReadOnlyList<ScenarioResult> Results, IReadOnlyList<TypeCoverage> Coverage, IReadOnlyList<ScenarioError> Errors)
{
    public int Passed => Results.Count(r => r.Passed);

    public int Failed => Results.Count(r => !r.Passed);
}
