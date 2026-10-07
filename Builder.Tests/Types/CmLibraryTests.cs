using ApolloIQ.Core.Alarms;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Logic.Blueprints;
using Xunit;

namespace Builder.Tests.Types;

/// <summary>The library of published blueprints and what a blueprint becomes at runtime.</summary>
public class CmLibraryTests
{
    private static List<string> Keys(IEnumerable<TagTemplate> tags) => tags.Select(t => t.Key).ToList();

    [Fact]
    public void BlueprintsAreFoundByIdAndByName()
    {
        var library = Fixtures.Library();
        Assert.Equal("Light", library.Find(Fixtures.Light)!.Name);
        Assert.Equal(Fixtures.CircuitBreaker, library.FindByName("circuitbreaker")!.Id);
        Assert.Null(library.Find(Guid.NewGuid()));

        var renamed = Fixtures.Load("Light");
        renamed.Name = "Lamp";
        library.Replace(BlueprintTypes.ToCmType(renamed));
        Assert.Equal("Lamp", library.Find(Fixtures.Light)!.Name);
        Assert.Null(library.FindByName("Light"));
        library.Remove(Fixtures.Light);
        Assert.Null(library.Find(Fixtures.Light));
    }

    [Fact]
    public void ALightHasItsOwnTagsTheGeneratedOnesAndAlarmTagsForPlcReactiveAlarms()
    {
        var type = BlueprintTypes.ToCmType(Fixtures.Load("Light"));
        var keys = Keys(type.ExpandTags());
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Subset(keys.ToHashSet(), new HashSet<string>
        {
            "STS.enabled", "STS.state", "STS.remote_ok", "CMD.set_on", "CMD.set_off", "CMD.reset",
            "LOK.can_on", "LOK.can_off", "LOK.trip", "LOK.can_on_status", "LOK.can_off_status",
            "FIN.feedback", "FIN.current", "OUT.lamp", "PAR.max_switch_time", "PAR.max_current",
            "INT.feedback", "SET.invert_feedback", "INT.bp_prev_state",
            "ALM.CurrentWhileOff.active", "ALM.CurrentWhileOff.enabled", "ALM.DoesNotSwitchOn.active"
        });
        Assert.DoesNotContain("ALM.LampFailure.active", keys);
        Assert.DoesNotContain("SET.invert_current", keys);
    }

    [Fact]
    public void TheRuntimeTypeKeepsTheStatesWithTheirTexts()
    {
        var type = BlueprintTypes.ToCmType(Fixtures.Load("Light"));
        Assert.Equal(["Off", "TurningOn", "On", "TurningOff", "Broken"], type.ObjectStates.Select(s => s.Name));
        Assert.Equal("Turning on", type.ObjectStates.Single(s => s.Code == 300).DisplayText);
        Assert.Equal(200, type.InitialState);
        Assert.Contains(type.States, s => s.Name == "Running" && s.IsCategory);
        Assert.Equal("Broken", type.StateName(500));
        Assert.Equal("Running", type.StateName(401));
    }

    [Fact]
    public void StateTimeoutsGeneratePlcReactiveAlarms()
    {
        var type = BlueprintTypes.ToCmType(Fixtures.Load("CircuitBreaker"));
        var closing = type.Alarms.Single(a => a.Name == "NotClosing");
        Assert.True(closing.PlcReactive);
        Assert.Equal(AlarmSource.StateTimeout, closing.Source);
        Assert.Equal("timeout_Closing", closing.OnTransition);
        var opening = type.Alarms.Single(a => a.Name == "NotOpening");
        Assert.Equal(AlarmTrigger.Timeout, opening.Definition.Trigger);
        Assert.Equal("[STS.state] == Opening", opening.Definition.Running);
        Assert.Equal("([PAR.max_open_time])", opening.Definition.Timeout);
        Assert.Empty(AlarmRules.Clean([.. type.Alarms.Where(a => a.Evaluated).Select(a => a.Definition.Clone())], inBlueprint: true, allowPlcByte: false));
    }
}
