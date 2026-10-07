namespace Builder.Logic.Expressions;

internal abstract record SyntaxNode(int Position);

internal sealed record NumberSyntax(double Value, int Position) : SyntaxNode(Position);

internal sealed record BoolSyntax(bool Value, int Position) : SyntaxNode(Position);

internal sealed record NameSyntax(string Name, bool Bracketed, int Position) : SyntaxNode(Position);

internal sealed record UnarySyntax(string Operator, SyntaxNode Operand, int Position) : SyntaxNode(Position);

internal sealed record BinarySyntax(string Operator, SyntaxNode Left, SyntaxNode Right, int Position) : SyntaxNode(Position);

internal sealed record CallSyntax(string Function, IReadOnlyList<SyntaxNode> Arguments, int Position) : SyntaxNode(Position);
