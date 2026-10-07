namespace Builder.Core.Types;

/// <summary>One assignment of the generated logic: <c>Target := Expression</c>. A target starting with <c>@</c> is a member's command (Units and EMs).</summary>
public sealed record AssignStep(string Target, string Expression, string Location);

/// <summary>A state machine transition. <c>From</c> holds state names or <c>*</c>.</summary>
public sealed record Transition(IReadOnlyList<string> From, string To, int Priority, string Guard, string Name, string Location);

/// <summary>
/// The logic of a blueprint as the runtime executes it (made by <c>BlueprintTypes</c>): the plant model, the steps before
/// the transitions, the transitions and the steps after them. Expressions use the ApolloIQ.Core syntax.
/// </summary>
public sealed record LogicModel(
    IReadOnlyList<AssignStep> Plant,
    IReadOnlyList<AssignStep> Before,
    IReadOnlyList<Transition> Transitions,
    IReadOnlyList<AssignStep> After)
{
    public static LogicModel Empty { get; } = new([], [], [], []);

    public bool HasStateMachine => Transitions.Count > 0;
}
