namespace Builder.Logic.Model;

public abstract record LogicStep(string Location);

public sealed record AssignStep(string Target, string Expression, string Location) : LogicStep(Location);

public sealed record BlockStep(
    string Block,
    string Id,
    IReadOnlyDictionary<string, string> Inputs,
    IReadOnlyDictionary<string, string> Outputs,
    string Location) : LogicStep(Location);

public sealed record Transition(
    IReadOnlyList<string> From,
    string To,
    int Priority,
    string Guard,
    string Name,
    string Location);

public sealed record OutputRule(string Target, IReadOnlyList<string>? States, string? When, string Location);

public sealed record LogicModel(
    IReadOnlyList<LogicStep> Plant,
    IReadOnlyList<LogicStep> Before,
    IReadOnlyList<Transition> Transitions,
    IReadOnlyList<OutputRule> Outputs,
    IReadOnlyList<LogicStep> After)
{
    public static LogicModel Empty { get; } = new([], [], [], [], []);

    public bool HasStateMachine => Transitions.Count > 0;
}

public sealed record LogicError(string Location, string Message)
{
    public override string ToString() => $"{Location}: {Message}";
}

public sealed class LogicException(IReadOnlyList<LogicError> errors) : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<LogicError> Errors { get; } = errors;
}
