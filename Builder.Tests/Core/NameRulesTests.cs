using Builder.Core.Model;
using Xunit;

namespace Builder.Tests.Core;

public class NameRulesTests
{
    [Fact]
    public void AcceptsLettersDigitsAndUnderscores()
    {
        Assert.Null(NameRules.Check("GEN1_CB", 32));
        Assert.Null(NameRules.Check("_x9", 32));
    }

    [Fact]
    public void RejectsEmptyName()
    {
        Assert.NotNull(NameRules.Check("", 32));
        Assert.NotNull(NameRules.Check(null, 32));
    }

    [Fact]
    public void RejectsOtherCharacters()
    {
        Assert.All(new[] { "GEN 1", "GEN-1", "GEN.1", "GÉN1", "gen/1" }, name => Assert.NotNull(NameRules.Check(name, 32)));
    }

    [Fact]
    public void RejectsNamesLongerThanTheMaximum()
    {
        Assert.Null(NameRules.Check(new string('A', 8), 8));
        Assert.NotNull(NameRules.Check(new string('A', 9), 8));
    }
}
