namespace Builder.Logic.Expressions;

internal sealed class Parser
{
    private readonly string _text;
    private readonly List<Token> _tokens;
    private int _index;

    private Parser(string text)
    {
        _text = text;
        _tokens = Lexer.Tokenize(text);
    }

    public static SyntaxNode Parse(string text)
    {
        var parser = new Parser(text);
        var node = parser.ParseOr();
        if (parser.Current.Kind != TokenKind.End)
            throw parser.Error($"Unexpected '{parser.Current.Text}'");
        return node;
    }

    private Token Current => _tokens[_index];

    private Token Advance() => _tokens[_index++];

    private ExpressionException Error(string message) => new(message, Current.Position, _text);

    private bool IsKeyword(string keyword) => Current.Kind == TokenKind.Keyword && Current.Text == keyword;

    private SyntaxNode ParseOr()
    {
        var left = ParseXor();
        while (IsKeyword("OR"))
        {
            var op = Advance();
            left = new BinarySyntax("OR", left, ParseXor(), op.Position);
        }
        return left;
    }

    private SyntaxNode ParseXor()
    {
        var left = ParseAnd();
        while (IsKeyword("XOR"))
        {
            var op = Advance();
            left = new BinarySyntax("XOR", left, ParseAnd(), op.Position);
        }
        return left;
    }

    private SyntaxNode ParseAnd()
    {
        var left = ParseNot();
        while (IsKeyword("AND"))
        {
            var op = Advance();
            left = new BinarySyntax("AND", left, ParseNot(), op.Position);
        }
        return left;
    }

    private SyntaxNode ParseNot()
    {
        if (IsKeyword("NOT"))
        {
            var op = Advance();
            return new UnarySyntax("NOT", ParseNot(), op.Position);
        }
        return ParseComparison();
    }

    private SyntaxNode ParseComparison()
    {
        var left = ParseSum();
        if (Current.Kind == TokenKind.Operator && Current.Text is "=" or "<>" or "<" or "<=" or ">" or ">=")
        {
            var op = Advance();
            var right = ParseSum();
            if (Current.Kind == TokenKind.Operator && Current.Text is "=" or "<>" or "<" or "<=" or ">" or ">=")
                throw Error("Chained comparisons are not allowed; use AND");
            return new BinarySyntax(op.Text, left, right, op.Position);
        }
        return left;
    }

    private SyntaxNode ParseSum()
    {
        var left = ParseTerm();
        while (Current.Kind == TokenKind.Operator && Current.Text is "+" or "-")
        {
            var op = Advance();
            left = new BinarySyntax(op.Text, left, ParseTerm(), op.Position);
        }
        return left;
    }

    private SyntaxNode ParseTerm()
    {
        var left = ParseUnary();
        while (Current.Kind == TokenKind.Operator && Current.Text is "*" or "/")
        {
            var op = Advance();
            left = new BinarySyntax(op.Text, left, ParseUnary(), op.Position);
        }
        return left;
    }

    private SyntaxNode ParseUnary()
    {
        if (Current.Kind == TokenKind.Operator && Current.Text == "-")
        {
            var op = Advance();
            return new UnarySyntax("-", ParseUnary(), op.Position);
        }
        return ParseFactor();
    }

    private SyntaxNode ParseFactor()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Number:
                Advance();
                return new NumberSyntax(token.Number, token.Position);
            case TokenKind.Keyword when token.Text is "TRUE" or "FALSE":
                Advance();
                return new BoolSyntax(token.Text == "TRUE", token.Position);
            case TokenKind.Bracketed:
                Advance();
                return new NameSyntax(token.Text, true, token.Position);
            case TokenKind.Identifier:
                Advance();
                if (Current.Kind == TokenKind.LeftParen)
                    return ParseCall(token);
                return new NameSyntax(token.Text, false, token.Position);
            case TokenKind.LeftParen:
            {
                Advance();
                var inner = ParseOr();
                if (Current.Kind != TokenKind.RightParen)
                    throw Error("Missing ')'");
                Advance();
                return inner;
            }
            case TokenKind.End:
                throw Error("Unexpected end of expression");
            default:
                throw Error($"Unexpected '{token.Text}'");
        }
    }

    private SyntaxNode ParseCall(Token name)
    {
        Advance();
        var arguments = new List<SyntaxNode>();
        if (Current.Kind != TokenKind.RightParen)
        {
            arguments.Add(ParseOr());
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                arguments.Add(ParseOr());
            }
        }
        if (Current.Kind != TokenKind.RightParen)
            throw Error("Missing ')' after function arguments");
        Advance();
        return new CallSyntax(name.Text, arguments, name.Position);
    }
}
