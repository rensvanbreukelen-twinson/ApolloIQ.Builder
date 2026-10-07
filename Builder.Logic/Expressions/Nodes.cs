namespace Builder.Logic.Expressions;

public abstract class BoundNode
{
    public abstract ValueType Type { get; }

    public abstract Value Evaluate(IEvalContext context);
}

internal sealed class ConstantNode(Value value) : BoundNode
{
    public override ValueType Type => value.Type;

    public override Value Evaluate(IEvalContext context) => value;
}

internal sealed class TagNode(TagSymbol symbol) : BoundNode
{
    public TagSymbol Symbol => symbol;

    public override ValueType Type => symbol.Type;

    public override Value Evaluate(IEvalContext context) => symbol.Constant ?? context.Read(symbol.Slot);
}

internal sealed class RangeNode(BoundNode operand, IReadOnlyList<(int From, int To)> ranges, bool negate) : BoundNode
{
    public override ValueType Type => ValueType.Bool;

    public override Value Evaluate(IEvalContext context)
    {
        var value = operand.Evaluate(context);
        var code = value.Number;
        var inside = ranges.Any(r => code >= r.From && code < r.To);
        return Value.Of(negate ? !inside : inside, value.Good);
    }
}

internal sealed class NotNode(BoundNode operand) : BoundNode
{
    public override ValueType Type => ValueType.Bool;

    public override Value Evaluate(IEvalContext context)
    {
        var value = operand.Evaluate(context);
        return Value.Of(!value.Bool, value.Good);
    }
}

internal sealed class NegateNode(BoundNode operand) : BoundNode
{
    public override ValueType Type => ValueType.Number;

    public override Value Evaluate(IEvalContext context)
    {
        var value = operand.Evaluate(context);
        return Value.Of(-value.Number, value.Good);
    }
}

internal sealed class LogicNode(string op, BoundNode left, BoundNode right) : BoundNode
{
    public override ValueType Type => ValueType.Bool;

    public override Value Evaluate(IEvalContext context)
    {
        var a = left.Evaluate(context);
        var b = right.Evaluate(context);
        var result = op switch
        {
            "AND" => a.Bool && b.Bool,
            "OR" => a.Bool || b.Bool,
            _ => a.Bool ^ b.Bool
        };
        return Value.Of(result, a.Good && b.Good);
    }
}

internal sealed class CompareNode(string op, BoundNode left, BoundNode right) : BoundNode
{
    public override ValueType Type => ValueType.Bool;

    public override Value Evaluate(IEvalContext context)
    {
        var a = left.Evaluate(context);
        var b = right.Evaluate(context);
        var result = op switch
        {
            "=" => a.Number == b.Number,
            "<>" => a.Number != b.Number,
            "<" => a.Number < b.Number,
            "<=" => a.Number <= b.Number,
            ">" => a.Number > b.Number,
            _ => a.Number >= b.Number
        };
        return Value.Of(result, a.Good && b.Good);
    }
}

internal sealed class ArithmeticNode(string op, BoundNode left, BoundNode right, string source) : BoundNode
{
    public override ValueType Type => ValueType.Number;

    public override Value Evaluate(IEvalContext context)
    {
        var a = left.Evaluate(context);
        var b = right.Evaluate(context);
        var good = a.Good && b.Good;
        if (op == "/" && b.Number == 0)
        {
            if (good)
                context.Report($"Division by zero in '{source}'");
            return Value.BadNumber;
        }
        var result = op switch
        {
            "+" => a.Number + b.Number,
            "-" => a.Number - b.Number,
            "*" => a.Number * b.Number,
            _ => a.Number / b.Number
        };
        return Value.Of(result, good);
    }
}

internal sealed class TimeNode(BoundNode condition, int timerSlot) : BoundNode
{
    public override ValueType Type => ValueType.Number;

    public override Value Evaluate(IEvalContext context)
    {
        var active = condition.Evaluate(context).IsTrue;
        var timers = context.Timers;
        var cycles = timers[timerSlot];
        timers[timerSlot] = !active ? -1 : cycles < 0 ? 0 : cycles + 1;
        return Value.Of(Math.Max(0, timers[timerSlot]) * context.CycleSeconds);
    }
}

internal sealed class StateTimeNode : BoundNode
{
    public override ValueType Type => ValueType.Number;

    public override Value Evaluate(IEvalContext context) => Value.Of(context.StateTimeSeconds);
}

internal sealed class GoodNode(BoundNode operand) : BoundNode
{
    public override ValueType Type => ValueType.Bool;

    public override Value Evaluate(IEvalContext context) => Value.Of(operand.Evaluate(context).Good);
}

internal sealed class MinMaxNode(bool max, IReadOnlyList<BoundNode> arguments) : BoundNode
{
    public override ValueType Type => ValueType.Number;

    public override Value Evaluate(IEvalContext context)
    {
        var values = arguments.Select(a => a.Evaluate(context)).ToList();
        var result = max ? values.Max(v => v.Number) : values.Min(v => v.Number);
        return Value.Of(result, values.All(v => v.Good));
    }
}

internal sealed class LimitNode(BoundNode min, BoundNode input, BoundNode max) : BoundNode
{
    public override ValueType Type => ValueType.Number;

    public override Value Evaluate(IEvalContext context)
    {
        var mn = min.Evaluate(context);
        var value = input.Evaluate(context);
        var mx = max.Evaluate(context);
        return Value.Of(Math.Min(Math.Max(value.Number, mn.Number), mx.Number), mn.Good && value.Good && mx.Good);
    }
}

internal sealed class SelectNode(BoundNode selector, BoundNode whenFalse, BoundNode whenTrue) : BoundNode
{
    public override ValueType Type => whenFalse.Type;

    public override Value Evaluate(IEvalContext context)
    {
        var g = selector.Evaluate(context);
        var a = whenFalse.Evaluate(context);
        var b = whenTrue.Evaluate(context);
        return (g.Bool ? b : a).WithQuality(g.Good);
    }
}
