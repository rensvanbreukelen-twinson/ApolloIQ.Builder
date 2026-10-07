using Builder.Core.Types;

namespace Builder.Logic.Expressions;

public sealed record TagSymbol(
    int Slot,
    ValueType Type,
    string Path,
    IReadOnlyList<StateDefinition>? States = null,
    Value? Constant = null);

public sealed record AliasSymbol(TagSymbol StateTag, IReadOnlyList<(int From, int To)> Ranges);

public interface ISymbolScope
{
    TagSymbol? ResolveTag(string reference, bool bracketed);

    AliasSymbol? ResolveAlias(string reference, bool bracketed);
}

public sealed record ExpressionOptions(bool AllowFunctions = true, bool AllowTimers = true)
{
    public static ExpressionOptions Logic { get; } = new();

    public static ExpressionOptions Interlock { get; } = new(AllowFunctions: true, AllowTimers: false);
}
