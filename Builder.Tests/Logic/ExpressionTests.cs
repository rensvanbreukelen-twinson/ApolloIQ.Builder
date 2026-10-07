using Builder.Core.Types;
using Builder.Logic.Expressions;
using Xunit;
using ValueType = Builder.Logic.Expressions.ValueType;

namespace Builder.Tests.Logic;

public class ExpressionTests
{
    private sealed class TestScope : ISymbolScope, IEvalContext
    {
        private static readonly IReadOnlyList<StateDefinition> States =
            UniversalStates.All.Concat([new StateDefinition(401, "ReadyToConnect")]).OrderBy(s => s.Code).ToList();

        public readonly Dictionary<string, TagSymbol> Tags = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<Value> Values = [];
        public readonly List<string> Reports = [];

        public TestScope Add(string path, ValueType type, Value value, bool isState = false)
        {
            Tags[path] = new TagSymbol(Values.Count, type, path, isState ? States : null);
            Values.Add(value);
            return this;
        }

        public void Set(string path, Value value) => Values[Tags[path].Slot] = value;

        public TagSymbol? ResolveTag(string reference, bool bracketed) => Tags.GetValueOrDefault(reference);

        public AliasSymbol? ResolveAlias(string reference, bool bracketed)
        {
            var dot = reference.LastIndexOf('.');
            var (cm, alias) = dot < 0 ? ("", reference) : (reference[..dot], reference[(dot + 1)..]);
            if (!UniversalStates.StandardAliases.TryGetValue(alias, out var codes))
                return null;
            var statePath = cm.Length == 0 ? "STS.state" : $"{cm}.STS.state";
            return Tags.TryGetValue(statePath, out var tag)
                ? new AliasSymbol(tag, codes.Select(c => (c, c == 99 ? 100 : c + 10)).ToList())
                : null;
        }

        public Value Read(int slot) => Values[slot];
        public double CycleSeconds { get; set; } = 0.05;
        public double StateTimeSeconds { get; set; }
        public double[] Timers { get; set; } = Enumerable.Repeat(-1d, 8).ToArray();
        public void Report(string message) => Reports.Add(message);
    }

    private static TestScope Scope() => new TestScope()
        .Add("CMD.set_on", ValueType.Bool, Value.True)
        .Add("LOK.can_start", ValueType.Bool, Value.True)
        .Add("INT.running", ValueType.Bool, Value.False)
        .Add("FIN.voltage", ValueType.Number, Value.Of(400))
        .Add("SET.rated_voltage", ValueType.Number, Value.Of(400))
        .Add("PAR.tol", ValueType.Number, Value.Of(5))
        .Add("STS.state", ValueType.Number, Value.Of(401), isState: true)
        .Add("PMS.GEN2.STS.state", ValueType.Number, Value.Of(200), isState: true);

    private static Value Eval(string text, TestScope? scope = null)
    {
        scope ??= Scope();
        return Expression.Compile(text, scope).Evaluate(scope);
    }

    [Theory]
    [InlineData("CMD.set_on AND LOK.can_start", true)]
    [InlineData("CMD.set_on AND NOT LOK.can_start", false)]
    [InlineData("INT.running OR CMD.set_on", true)]
    [InlineData("CMD.set_on XOR LOK.can_start", false)]
    [InlineData("NOT INT.running AND CMD.set_on", true)]
    [InlineData("TRUE AND NOT FALSE", true)]
    [InlineData("(INT.running OR CMD.set_on) AND LOK.can_start", true)]
    public void LogicFollowsTheRules(string text, bool expected) => Assert.Equal(Value.Of(expected), Eval(text));

    [Theory]
    [InlineData("FIN.voltage >= SET.rated_voltage * (1 - PAR.tol / 100)", true)]
    [InlineData("FIN.voltage > SET.rated_voltage + 0.5", false)]
    [InlineData("-FIN.voltage < 0", true)]
    [InlineData("2 + 3 * 4 = 14", true)]
    [InlineData("(2 + 3) * 4 = 20", true)]
    [InlineData("1.5e1 = 15", true)]
    public void ArithmeticAndComparisons(string text, bool expected) => Assert.Equal(Value.Of(expected), Eval(text));

    [Fact]
    public void NotAppliesToTheWholeComparison() =>
        Assert.Equal(Value.True, Eval("NOT FIN.voltage > 500"));

    [Theory]
    [InlineData("STS.state = Running", true)]
    [InlineData("STS.state = ReadyToConnect", true)]
    [InlineData("STS.state <> Running", false)]
    [InlineData("STS.state = Available", false)]
    [InlineData("Running = STS.state", true)]
    [InlineData("[PMS.GEN2.STS.state] = Available", true)]
    [InlineData("STS.state = 401", true)]
    public void NamedStatesUseDecades(string text, bool expected) => Assert.Equal(Value.Of(expected), Eval(text));

    [Theory]
    [InlineData("is_running", true)]
    [InlineData("is_off", false)]
    [InlineData("[PMS.GEN2.is_available]", true)]
    [InlineData("[PMS.GEN2.is_off] AND is_running", true)]
    public void AliasesExpandToRanges(string text, bool expected) => Assert.Equal(Value.Of(expected), Eval(text));

    [Fact]
    public void BadQualityMakesAConditionFalse()
    {
        var scope = Scope();
        scope.Set("CMD.set_on", Value.Of(true, good: false));
        var result = Eval("CMD.set_on OR LOK.can_start", scope);
        Assert.False(result.Good);
        Assert.False(result.IsTrue);
        Assert.False(Eval("NOT CMD.set_on", scope).IsTrue);
    }

    [Fact]
    public void GoodReportsQuality()
    {
        var scope = Scope();
        scope.Set("FIN.voltage", Value.Of(1, good: false));
        Assert.Equal(Value.False, Eval("GOOD(FIN.voltage)", scope));
        Assert.Equal(Value.True, Eval("NOT GOOD(FIN.voltage)", scope));
    }

    [Fact]
    public void DivisionByZeroIsInvalidAndReported()
    {
        var scope = Scope();
        scope.Set("PAR.tol", Value.Of(0));
        var result = Eval("FIN.voltage / PAR.tol > 1", scope);
        Assert.False(result.IsTrue);
        Assert.Single(scope.Reports);
    }

    [Fact]
    public void TimeCountsWhileTrueAndResetsWhenFalse()
    {
        var scope = Scope();
        var expression = Expression.Compile("time(CMD.set_on) > 0.1", scope);
        Assert.Equal(1, expression.TimerCount);
        var results = new List<bool>();
        for (var i = 0; i < 5; i++)
            results.Add(expression.IsTrue(scope));
        scope.Set("CMD.set_on", Value.False);
        results.Add(expression.IsTrue(scope));
        scope.Set("CMD.set_on", Value.True);
        results.Add(expression.IsTrue(scope));
        Assert.Equal([false, false, false, true, true, false, false], results);
    }

    [Fact]
    public void TimeUnitsAreSeconds()
    {
        Assert.Equal(Value.True, Eval("500 ms = 0.5 s"));
        Assert.Equal(Value.True, Eval("2 s = 2"));
    }

    [Fact]
    public void FunctionsWork()
    {
        Assert.Equal(Value.Of(2), Eval("MIN(5, 2, 9)"));
        Assert.Equal(Value.Of(9), Eval("MAX(5, 2, 9)"));
        Assert.Equal(Value.Of(10), Eval("LIMIT(0, 12, 10)"));
        Assert.Equal(Value.Of(400), Eval("SEL(CMD.set_on, 0, FIN.voltage)"));
        Assert.Equal(Value.Of(0), Eval("SEL(INT.running, 0, FIN.voltage)"));
    }

    [Theory]
    [InlineData("CMD.set_on AND FIN.voltage", "AND needs a Bool")]
    [InlineData("FIN.voltage + CMD.set_on", "needs a Number")]
    [InlineData("CMD.set_on = FIN.voltage", "Cannot compare")]
    [InlineData("INT.nothing", "Unknown tag")]
    [InlineData("Running", "is a state name")]
    [InlineData("STS.state = Flying", "Unknown state")]
    [InlineData("FIN.voltage = Running", "state tag")]
    [InlineData("STS.state < Running", "= or <>")]
    [InlineData("(CMD.set_on", "Missing ')'")]
    [InlineData("CMD.set_on AND", "Unexpected end")]
    [InlineData("1 < 2 < 3", "Chained")]
    [InlineData("ABS(FIN.voltage)", "Unknown function")]
    [InlineData("MIN(1)", "at least two")]
    [InlineData("[FIN.voltage", "Missing ']'")]
    [InlineData("FIN.voltage # 2", "Unexpected character")]
    public void ErrorsAreReadable(string text, string fragment)
    {
        var ex = Assert.Throws<ExpressionException>(() => Expression.Compile(text, Scope()));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void InterlockModeForbidsTimers()
    {
        Assert.Throws<ExpressionException>(() => Expression.Compile("time(CMD.set_on) > 1", Scope(), ExpressionOptions.Interlock));
        Expression.Compile("MAX(1, 2) > 1", Scope(), ExpressionOptions.Interlock);
        Expression.Compile("[PMS.GEN2.is_off] AND CMD.set_on", Scope(), ExpressionOptions.Interlock);
    }

    [Fact]
    public void ExpectedTypeIsChecked()
    {
        var ex = Assert.Throws<ExpressionException>(() => Expression.Compile("FIN.voltage * 2", Scope(), expected: ValueType.Bool));
        Assert.Contains("must give a Bool", ex.Message);
    }

    [Fact]
    public void ErrorPositionPointsAtTheProblem()
    {
        var ex = Assert.Throws<ExpressionException>(() => Expression.Compile("CMD.set_on AND INT.nothing", Scope()));
        Assert.Equal(15, ex.Position);
    }
}
