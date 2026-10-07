using System.Globalization;

namespace Builder.Logic.Expressions;

internal static class Lexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase) { "AND", "OR", "NOT", "XOR", "TRUE", "FALSE" };

    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                    i++;
                if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
                {
                    i++;
                    if (i < text.Length && (text[i] == '+' || text[i] == '-'))
                        i++;
                    while (i < text.Length && char.IsDigit(text[i]))
                        i++;
                }
                var raw = text[start..i];
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new ExpressionException($"'{raw}' is not a number", start, text);
                var unitStart = i;
                while (unitStart < text.Length && text[unitStart] == ' ')
                    unitStart++;
                var unitEnd = unitStart;
                while (unitEnd < text.Length && char.IsLetter(text[unitEnd]))
                    unitEnd++;
                var unit = text[unitStart..unitEnd];
                var unitIsWord = unitEnd >= text.Length || !(char.IsLetterOrDigit(text[unitEnd]) || text[unitEnd] == '_' || text[unitEnd] == '.');
                if ((unit == "s" || unit == "ms") && unitIsWord)
                {
                    if (unit == "ms")
                        number /= 1000;
                    i = unitEnd;
                }
                tokens.Add(new Token(TokenKind.Number, raw, start, number));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.'))
                    i++;
                var word = text[start..i].TrimEnd('.');
                i = start + word.Length;
                tokens.Add(new Token(Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier,
                    Keywords.Contains(word) ? word.ToUpperInvariant() : word, start));
                continue;
            }

            switch (c)
            {
                case '[':
                {
                    var close = text.IndexOf(']', i + 1);
                    if (close < 0)
                        throw new ExpressionException("Missing ']'", start, text);
                    var path = text[(i + 1)..close].Trim();
                    if (path.Length == 0)
                        throw new ExpressionException("Empty reference '[]'", start, text);
                    tokens.Add(new Token(TokenKind.Bracketed, path, start));
                    i = close + 1;
                    continue;
                }
                case '(':
                    tokens.Add(new Token(TokenKind.LeftParen, "(", start));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new Token(TokenKind.RightParen, ")", start));
                    i++;
                    continue;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma, ",", start));
                    i++;
                    continue;
                case '<':
                    if (i + 1 < text.Length && (text[i + 1] == '=' || text[i + 1] == '>'))
                    {
                        tokens.Add(new Token(TokenKind.Operator, text.Substring(i, 2), start));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.Operator, "<", start));
                        i++;
                    }
                    continue;
                case '>':
                    if (i + 1 < text.Length && text[i + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.Operator, ">=", start));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.Operator, ">", start));
                        i++;
                    }
                    continue;
                case '=' or '+' or '-' or '*' or '/':
                    tokens.Add(new Token(TokenKind.Operator, c.ToString(), start));
                    i++;
                    continue;
                default:
                    throw new ExpressionException($"Unexpected character '{c}'", start, text);
            }
        }

        tokens.Add(new Token(TokenKind.End, "", text.Length));
        return tokens;
    }
}
