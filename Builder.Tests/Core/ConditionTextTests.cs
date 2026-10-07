using Builder.Core.Model;
using Xunit;

namespace Builder.Tests.Core;

public class ConditionTextTests
{
    [Theory]
    [InlineData("[DECK.V_A.FIN.closed] || [DECK.V_B.FIN.closed]", "V_A closed or V_B closed")]
    [InlineData("![PMS.GEN1.FIN.shutdown_active]", "GEN1 not shutdown active")]
    [InlineData("[PMS.GEN1.is_running] && [PMS.CB1.STS.state] != Tripped", "GEN1 running and CB1 not Tripped")]
    [InlineData("![PMS.V1.ALM.NotClosing.active]", "V1 NotClosing alarm not active")]
    [InlineData("[PMS.BUS.FIN.kw] >= [PMS.BUS.PAR.min_kw]", "BUS kw ≥ BUS min kw")]
    [InlineData("[FIN.door_closed] && [STS.state] == Ready", "door closed and Ready")]
    [InlineData("![FEED.is_closed] ^ TRUE", "FEED not closed xor true")]
    public void TextsAreGeneratedFromTheExpression(string display, string expected) =>
        Assert.Equal(expected, ConditionText.Generate(display));
}
