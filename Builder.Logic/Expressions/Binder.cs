using Builder.Core.Types;

namespace Builder.Logic.Expressions;

internal sealed class Binder(string text, ISymbolScope scope, ExpressionOptions options, Func<int> allocateTimer)
{
    private static readonly HashSet<string> CompareOperators = ["=", "<>", "<", "<=", ">", ">="];

    private ExpressionException Error(string message, SyntaxNode node) => new(message, node.Position, text);

    public BoundNode Bind(SyntaxNode node) => node switch
    {
        NumberSyntax n => new ConstantNode(Value.Of(n.Value)),
        BoolSyntax b => new ConstantNode(Value.Of(b.Value)),
        NameSyntax name => BindName(name),
        UnarySyntax u => BindUnary(u),
        BinarySyntax b => BindBinary(b),
        CallSyntax c => BindCall(c),
        _ => throw Error("Unsupported expression", node)
    };

    private BoundNode BindName(NameSyntax name)
    {
        if (scope.ResolveTag(name.Name, name.Bracketed) is { } tag)
            return new TagNode(tag);
        if (scope.ResolveAlias(name.Name, name.Bracketed) is { } alias)
            return new RangeNode(new TagNode(alias.StateTag), alias.Ranges, negate: false);
        if (!name.Bracketed && !name.Name.Contains('.'))
            throw Error($"'{name.Name}' is a state name; compare it with a state, for example STS.state = {name.Name}", name);
        throw Error($"Unknown tag or alias '{name.Name}'", name);
    }

    private BoundNode BindUnary(UnarySyntax node)
    {
        var operand = Bind(node.Operand);
        if (node.Operator == "NOT")
        {
            Expect(operand, ValueType.Bool, node.Operand, "NOT");
            return new NotNode(operand);
        }
        Expect(operand, ValueType.Number, node.Operand, "-");
        return new NegateNode(operand);
    }

    private BoundNode BindBinary(BinarySyntax node)
    {
        if (CompareOperators.Contains(node.Operator) && TryBindStateComparison(node) is { } stateComparison)
            return stateComparison;

        var left = Bind(node.Left);
        var right = Bind(node.Right);
        switch (node.Operator)
        {
            case "AND" or "OR" or "XOR":
                Expect(left, ValueType.Bool, node.Left, node.Operator);
                Expect(right, ValueType.Bool, node.Right, node.Operator);
                return new LogicNode(node.Operator, left, right);
            case "=" or "<>":
                if (left.Type != right.Type)
                    throw Error($"Cannot compare {left.Type} with {right.Type}", node);
                return new CompareNode(node.Operator, left, right);
            case "<" or "<=" or ">" or ">=":
                Expect(left, ValueType.Number, node.Left, node.Operator);
                Expect(right, ValueType.Number, node.Right, node.Operator);
                return new CompareNode(node.Operator, left, right);
            default:
                Expect(left, ValueType.Number, node.Left, node.Operator);
                Expect(right, ValueType.Number, node.Right, node.Operator);
                return new ArithmeticNode(node.Operator, left, right, text);
        }
    }

    private BoundNode? TryBindStateComparison(BinarySyntax node)
    {
        var (tagSide, nameSide) = node.Right is NameSyntax { Bracketed: false } r && !r.Name.Contains('.') && scope.ResolveTag(r.Name, false) is null
            ? (node.Left, r)
            : node.Left is NameSyntax { Bracketed: false } l && !l.Name.Contains('.') && scope.ResolveTag(l.Name, false) is null
                ? (node.Right, l)
                : (null, null);
        if (tagSide is null || nameSide is null || scope.ResolveAlias(nameSide.Name, false) is not null)
            return null;

        var bound = Bind(tagSide);
        if (bound is not TagNode { Symbol.States: { } states })
            throw Error($"'{nameSide.Name}' can only be compared with a state tag such as STS.state", nameSide);
        var state = states.FirstOrDefault(s => string.Equals(s.Name, nameSide.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw Error($"Unknown state '{nameSide.Name}'", nameSide);
        var range = UniversalStates.IsUniversalCode(state.Code)
            ? UniversalStates.Range(state.Code)
            : (state.Code, state.Code + 1);
        return node.Operator switch
        {
            "=" => new RangeNode(bound, [range], negate: false),
            "<>" => new RangeNode(bound, [range], negate: true),
            _ => throw Error("States can only be compared with = or <>", node)
        };
    }

    private BoundNode BindCall(CallSyntax node)
    {
        var name = node.Function.ToUpperInvariant();
        if (!options.AllowFunctions && name != "TIME")
            throw Error($"Functions are not allowed here ('{node.Function}')", node);
        if (!options.AllowTimers && name is "TIME" or "STATE_TIME")
            throw Error("Timers are not allowed in this expression", node);

        var args = node.Arguments.Select(Bind).ToList();
        switch (name)
        {
            case "TIME":
                Arity(node, 1);
                Expect(args[0], ValueType.Bool, node.Arguments[0], "time()");
                return new TimeNode(args[0], allocateTimer());
            case "STATE_TIME":
                Arity(node, 0);
                return new StateTimeNode();
            case "GOOD":
                Arity(node, 1);
                return new GoodNode(args[0]);
            case "MIN" or "MAX":
                if (args.Count < 2)
                    throw Error($"{name} needs at least two arguments", node);
                for (var i = 0; i < args.Count; i++)
                    Expect(args[i], ValueType.Number, node.Arguments[i], name);
                return new MinMaxNode(name == "MAX", args);
            case "LIMIT":
                Arity(node, 3);
                for (var i = 0; i < 3; i++)
                    Expect(args[i], ValueType.Number, node.Arguments[i], name);
                return new LimitNode(args[0], args[1], args[2]);
            case "SEL":
                Arity(node, 3);
                Expect(args[0], ValueType.Bool, node.Arguments[0], name);
                if (args[1].Type != args[2].Type)
                    throw Error("SEL needs two values of the same type", node);
                return new SelectNode(args[0], args[1], args[2]);
            default:
                throw Error($"Unknown function '{node.Function}'", node);
        }
    }

    private void Arity(CallSyntax node, int count)
    {
        if (node.Arguments.Count != count)
            throw Error($"{node.Function} takes {count} argument{(count == 1 ? "" : "s")}", node);
    }

    private void Expect(BoundNode node, ValueType type, SyntaxNode syntax, string context)
    {
        if (node.Type != type)
            throw Error($"{context} needs a {type} value", syntax);
    }
}
