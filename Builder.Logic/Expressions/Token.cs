namespace Builder.Logic.Expressions;

internal enum TokenKind
{
    Number,
    Identifier,
    Bracketed,
    Keyword,
    Operator,
    LeftParen,
    RightParen,
    Comma,
    End
}

internal readonly record struct Token(TokenKind Kind, string Text, int Position, double Number = 0);
