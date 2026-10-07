using ApolloIQ.Core.Alarms;
using ApolloIQ.Core.Conventions;
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
    public void TheEditorOffersTheReportingCategories() =>
        Assert.Equal(UniversalStates.Categories.Where(c => c.Reporting).Select(c => c.Code), BlueprintRules.Categories.Select(c => c.Code));

    [Theory]
    [InlineData(0, AlarmLevel.Caution)]
    [InlineData(9, AlarmLevel.Caution)]
    [InlineData(10, AlarmLevel.Warning)]
    [InlineData(19, AlarmLevel.Warning)]
    [InlineData(20, AlarmLevel.Alarm)]
    [InlineData(30, AlarmLevel.Alarm)]
    public void AlarmPriorityGivesTheLevel(int priority, AlarmLevel level) => Assert.Equal(level, AlarmPriority.LevelOf(priority));

    [Fact]
    public void TheBuilderHandsTheSharedConventionsToItsFrontend()
    {
        var conventions = Conventions.Current;
        Assert.Equal(AlarmPriority.Default, conventions.DefaultAlarmPriority);
        Assert.Equal(30, conventions.AlarmPriorityMax);
    }
}
