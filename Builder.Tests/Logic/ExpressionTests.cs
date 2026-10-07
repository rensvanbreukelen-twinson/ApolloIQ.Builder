using ApolloIQ.Core.Expressions;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Runtime;
using Xunit;

namespace Builder.Tests.Logic;

/// <summary>The ApolloIQ.Core expression engine as the Builder uses it: C-style operators, tags in brackets, names in the CM's scope.</summary>
public class ExpressionTests
{
    private sealed class Harness
    {
        public Harness()
        {
            var library = Fixtures.Library();
            Project = new Project();
            var pms = Project.AddFolder("PMS");
            InstanceFactory.Create(Project, library, Fixtures.Light, "L1", pms.Id);
            InstanceFactory.Create(Project, library, Fixtures.CircuitBreaker, "CB1", pms.Id);
            Program = LogicProgram.Build(Project, library, 0.1);
        }

        public Project Project { get; }

        public LogicProgram Program { get; }

        public void Set(string path, Value value) => Program.Memory.Set(Program.Memory.Slots[new TagRegistry(Project).FindByPath(path)!.Id], value);

        public Value Eval(string text, string cm = "PMS.L1") => Program.CompileCondition(cm, text)();
    }

    [Theory]
    [InlineData("[STS.enabled] && ![CMD.set_on]", true)]
    [InlineData("[STS.enabled] || [CMD.set_on]", true)]
    [InlineData("[STS.enabled] ^ TRUE", false)]
    [InlineData("!([STS.enabled] && [CMD.set_on])", true)]
    [InlineData("[PAR.max_switch_time] * 2 == 4", true)]
    [InlineData("[PAR.max_switch_time] != 2", false)]
    [InlineData("MAX([PAR.max_switch_time], 3) >= 3 && ABS(-1) == 1", true)]
    [InlineData("[PMS.CB1.SET.trip_latches]", true)]
    public void CStyleOperatorsAndBracketedTags(string text, bool expected) => Assert.Equal(Value.Of(expected), new Harness().Eval(text));

    [Fact]
    public void CategoriesMatchTheirRangeAndObjectStatesTheirOwnCode()
    {
        var h = new Harness();
        h.Set("PMS.L1.STS.state", Value.Of(300));
        Assert.True(h.Eval("[STS.state] == TurningOn").IsTrue);
        Assert.True(h.Eval("[STS.state] == Starting").IsTrue);
        Assert.False(h.Eval("[STS.state] == On").IsTrue);
        Assert.True(h.Eval("[STS.state] != On").IsTrue);

        h.Set("PMS.L1.STS.state", Value.Of(301));
        Assert.True(h.Eval("[STS.state] == Starting").IsTrue, "a category matches its whole range");
        Assert.False(h.Eval("[STS.state] == TurningOn").IsTrue, "an object state matches only its own code");
    }

    [Fact]
    public void StatesOfOtherObjectsAreTheirBlueprintsStates()
    {
        var h = new Harness();
        Assert.True(h.Eval("[PMS.CB1.STS.state] == OpenNotReady").IsTrue);
        Assert.True(h.Eval("[PMS.CB1.STS.state] == Stopped").IsTrue);
        Assert.False(h.Eval("[PMS.CB1.STS.state] == Open").IsTrue);
        var ex = Assert.Throws<ExpressionException>(() => h.Eval("[PMS.CB1.STS.state] == TurningOn"));
        Assert.Contains("Unknown state", ex.Message);
    }

    [Fact]
    public void AliasesAreBracketedAndMayBeBlueprintAliases()
    {
        var h = new Harness();
        Assert.True(h.Eval("[is_available]").IsTrue);
        Assert.True(h.Eval("[PMS.CB1.is_open]").IsTrue, "is_open is an alias of the CircuitBreaker blueprint");
        Assert.False(h.Eval("[PMS.CB1.is_closed]").IsTrue);
        Assert.True(h.Eval("[is_closed] == FALSE", "PMS.CB1").IsTrue);
    }

    [Theory]
    [InlineData("STS.enabled", "brackets")]
    [InlineData("[STS.enabled] AND [CMD.set_on]", "Unexpected")]
    [InlineData("NOT [STS.enabled]", "Unexpected")]
    [InlineData("[STS.state] = Running", "use ==")]
    [InlineData("[STS.state] <> Running", "Unexpected")]
    [InlineData("Running", "state name")]
    [InlineData("[INT.nothing]", "Unknown tag")]
    [InlineData("[STS.state] == Flying", "Unknown state")]
    [InlineData("[STS.state] < Running", "== or !=")]
    public void TheOldSyntaxAndUnknownNamesAreRejected(string text, string fragment)
    {
        var ex = Assert.Throws<ExpressionException>(() => new Harness().Eval(text));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void BadQualityMakesAConditionFalse()
    {
        var h = new Harness();
        h.Program.Memory.SetBadQuality(h.Program.Memory.Slots[new TagRegistry(h.Project).FindByPath("PMS.L1.FIN.feedback")!.Id], true);
        Assert.False(h.Eval("[FIN.feedback] || TRUE").IsTrue);
        Assert.True(h.Eval("!GOOD([FIN.feedback])").IsTrue);
    }

    [Fact]
    public void RenamesRewriteOnlyTheReferences()
    {
        var text = "[PMS.GEN1.STS.state] == Running && ![PMS.GEN1.FIN.x]";
        Assert.Equal("[PMS.GEN2.STS.state] == Running && ![PMS.GEN2.FIN.x]",
            Expression.RewriteReferences(text, r => r.Replace("GEN1", "GEN2", StringComparison.Ordinal)));
    }
}
