namespace Builder.Logic.Expressions;

public sealed class Expression
{
    private readonly BoundNode _root;

    private Expression(string source, BoundNode root, int timerCount)
    {
        Source = source;
        _root = root;
        TimerCount = timerCount;
    }

    public string Source { get; }

    public ValueType Type => _root.Type;

    public int TimerCount { get; }

    public static Expression Compile(string source, ISymbolScope scope, ExpressionOptions? options = null,
        ValueType? expected = null, Func<int>? allocateTimer = null)
    {
        var count = 0;
        var next = 0;
        var root = new Binder(source, scope, options ?? ExpressionOptions.Logic, () =>
        {
            count++;
            return allocateTimer?.Invoke() ?? next++;
        }).Bind(Parser.Parse(source));
        if (expected is { } type && root.Type != type)
            throw new ExpressionException($"The expression must give a {type} value, not {root.Type}", 0, source);
        return new Expression(source, root, count);
    }

    public Value Evaluate(IEvalContext context) => _root.Evaluate(context);

    public bool IsTrue(IEvalContext context) => _root.Evaluate(context).IsTrue;
}
