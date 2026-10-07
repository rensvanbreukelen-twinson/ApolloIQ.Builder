using Builder.Core.Types;
using Builder.Logic.Blueprints;
using Xunit;

namespace Builder.Tests.Types;

public class ConventionsTests
{
    [Theory]
    [InlineData(UniversalStates.Stopped, false)]
    [InlineData(UniversalStates.Stopping, false)]
    [InlineData(UniversalStates.Available, false)]
    [InlineData(UniversalStates.Starting, true)]
    [InlineData(UniversalStates.Starting + 1, true)]
    [InlineData(UniversalStates.Running, true)]
    [InlineData(UniversalStates.Running + 1, true)]
    [InlineData(UniversalStates.Shutdown, false)]
    [InlineData(UniversalStates.Unavailable, false)]
    public void HeadsOnFollowsTheCategory(int code, bool expected) => Assert.Equal(expected, UniversalStates.HeadsOn(code));

    [Fact]
    public void SubStatesBelongToTheirCategory()
    {
        Assert.Equal("Running", UniversalStates.Category(UniversalStates.Running + 1)!.Name);
        Assert.True(UniversalStates.IsFault(UniversalStates.Shutdown + 2));
        Assert.False(UniversalStates.Category(UniversalStates.Unavailable)!.Reporting);
    }

    [Fact]
    public void EveryCategoryHasItsOwnColumnAndTheCatalogUsesThem()
    {
        Assert.Equal(UniversalStates.Categories.Count, UniversalStates.Categories.Select(c => c.Column).Distinct().Count());
        Assert.Equal(UniversalStates.Categories.Where(c => c.Reporting).Select(c => c.Code), BlueprintCatalog.Categories.Select(c => c.Code));
        Assert.Equal(UniversalStates.Categories.Select(c => c.Code), UniversalStates.All.Select(s => s.Code));
    }

    [Theory]
    [InlineData(0, "Caution")]
    [InlineData(9, "Caution")]
    [InlineData(10, "Warning")]
    [InlineData(19, "Warning")]
    [InlineData(20, "Alarm")]
    [InlineData(30, "Alarm")]
    public void SeverityBandsCoverTheWholeRange(int severity, string band)
    {
        Assert.Equal(band, SeverityBands.BandOf(severity));
        Assert.Equal(band, AlarmDefinition.Band(severity));
    }

    [Fact]
    public void BandsAreContiguous()
    {
        var bands = SeverityBands.All;
        Assert.Equal(SeverityBands.Min, bands[0].From);
        Assert.Equal(SeverityBands.Max, bands[^1].To);
        for (var i = 1; i < bands.Count; i++)
            Assert.Equal(bands[i - 1].To + 1, bands[i].From);
    }
}
