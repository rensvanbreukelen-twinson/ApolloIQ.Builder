namespace Builder.Logic.Expressions;

public sealed class ExpressionException(string message, int position, string expression)
    : Exception($"{message} (at {position + 1} in '{expression}')")
{
    public string Reason { get; } = message;

    public int Position { get; } = position;

    public string Expression { get; } = expression;
}
