using ApolloIQ.Core.Alarms;
using ApolloIQ.Core.Expressions;
using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Logic.Blueprints;
using Builder.Logic.Runtime;
using Builder.Simulator;
using Xunit;

namespace Builder.Tests.Blueprints;

public class BlueprintRuntimeTests
{
    private static SimulationSession Session(Guid blueprint, string name)
    {
        var library = Fixtures.Library();
        var project = new Project();
        var folder = project.AddFolder("DECK");
        InstanceFactory.Create(project, library, blueprint, name, folder.Id);
        var session = new SimulationSession(Guid.NewGuid(), project, library);
        Assert.Empty(session.Errors);
        return session;
    }

    private static string State(SimulationSession session) => session.ControlModules().Single().StateName;

    private static AlarmStatus Alarm(SimulationSession session, string name) => session.Alarms().Single(a => a.Name == name);

    [Fact]
    public void TheGeneratedLogicUsesTheCoreSyntax()
    {
        var type = BlueprintTypes.ToCmType(Fixtures.Load("Light"));
        var expressions = type.Logic.Transitions.Select(t => t.Guard)
            .Concat(type.Logic.Before.Concat(type.Logic.After).Concat(type.Logic.Plant).Select(s => s.Expression)).ToList();
        Assert.All(expressions, e => Assert.DoesNotMatch(@"\b(AND|OR|NOT|XOR)\b|<>|(?<![=!<>])=(?!=)", e));
        Assert.Contains(type.Logic.Transitions, t => t.Name == "unavailable" && t.Guard == "!([STS.enabled] && GOOD([FIN.feedback]))");
        Assert.Contains(type.Logic.Transitions, t => t.Name == "timeout_TurningOn" && t.Guard == "STATE_TIME() > ([PAR.max_switch_time])");
        Assert.Contains(type.Logic.After, s => s.Target == "OUT.lamp" && s.Expression == "SEL([STS.state] == 300, [OUT.lamp], (TRUE))");
        Assert.DoesNotContain(type.Logic.Before, s => s.Target == "INT.feedback");
    }

    [Fact]
    public void MemberReferencesOfAnEquipmentModuleBecomeRoleTokens()
    {
        var type = BlueprintTypes.ToCmType(Fixtures.Load("LightingGroup"));
        Assert.Contains(type.Logic.Transitions, t => t.Name == "lit" && t.Guard == "[{role:LAMP}.STS.state] == On");
        Assert.Contains(type.Logic.After, s => s.Target == "@LAMP.CMD.set_on");
        Assert.Contains(type.Interlocks, r => r.Kind == InterlockKind.Trip && r.Condition == "![{role:FEED}.is_closed]");
    }

    [Fact]
    public void TheLightSwitchesOnAndOffAndBreaksOnATimeout()
    {
        var session = Session(Fixtures.Light, "L1");
        session.Step();
        Assert.Equal("Off", State(session));
        session.Write("DECK.L1.CMD.HMI_on", true);
        session.Step();
        Assert.Equal("TurningOn", State(session));
        Assert.Equal("Turning on", session.ControlModules().Single().StateText);
        session.Step(3);
        Assert.Equal("On", State(session));
        Assert.Equal(true, session.Read("DECK.L1.OUT.lamp").Value);

        session.Write("DECK.L1.CMD.HMI_off", true);
        session.Step(4);
        Assert.Equal("Off", State(session));
        Assert.Equal(false, session.Read("DECK.L1.OUT.lamp").Value);

        session.Force("DECK.L1.FIN.feedback", false);
        session.Write("DECK.L1.CMD.HMI_on", true);
        session.Step(50);
        Assert.Equal("Broken", State(session));
        Assert.Equal(true, session.Read("DECK.L1.ALM.DoesNotSwitchOn.active").Value);
        session.Unforce("DECK.L1.FIN.feedback");
        session.Write("DECK.L1.CMD.HMI_reset", true);
        session.Step(2);
        Assert.Equal("Off", State(session));
        Assert.Equal(false, session.Read("DECK.L1.ALM.DoesNotSwitchOn.active").Value);
    }

    [Fact]
    public void OneTransitionPerCycleAndCommandsAreResetEveryCycle()
    {
        var session = Session(Fixtures.Light, "L1");
        session.Step();
        session.Write("DECK.L1.CMD.HMI_on", true);
        session.Step();
        Assert.Equal("TurningOn", State(session));
        Assert.Equal(false, session.Read("DECK.L1.CMD.HMI_on").Value);
        Assert.Equal(false, session.Read("DECK.L1.CMD.set_on").Value);
    }

    [Fact]
    public void TheInvertSettingConditionsTheInput()
    {
        var session = Session(Fixtures.Light, "L1");
        session.Step();
        session.Write("DECK.L1.SET.invert_feedback", true);
        session.Step();
        Assert.Equal(true, session.Read("DECK.L1.INT.feedback").Value);
    }

    [Fact]
    public void TheLightIsUnavailableWhenItsFeedbackIsBad()
    {
        var session = Session(Fixtures.Light, "L1");
        session.Step(2);
        session.SetBadQuality("DECK.L1.FIN.feedback", true);
        session.Step();
        Assert.Equal("Unavailable", State(session));
        session.SetBadQuality("DECK.L1.FIN.feedback", false);
        session.Step(2);
        Assert.Equal("Off", State(session));
    }

    [Fact]
    public void TheBreakerClosesOpensAndTrips()
    {
        var session = Session(Fixtures.CircuitBreaker, "CB1");
        session.Step(2);
        Assert.Equal("Open", State(session));
        session.Write("DECK.CB1.CMD.HMI_on", true);
        session.Step(4);
        Assert.Equal("Closed", State(session));
        session.Force("DECK.CB1.FIN.tripped", true);
        session.Step(2);
        Assert.Equal("Tripped", State(session));
        Assert.Equal(true, session.Read("DECK.CB1.ALM.Tripped.active").Value);
        session.Unforce("DECK.CB1.FIN.tripped");
        session.Step(2);
        Assert.Equal("Tripped", State(session));
        session.Write("DECK.CB1.CMD.HMI_reset", true);
        session.Step(4);
        Assert.Equal("Open", State(session));
    }

    [Fact]
    public void TransitionAlarmsLatchUntilReset()
    {
        var session = Session(Fixtures.CircuitBreaker, "CB1");
        session.Step(2);
        session.Write("DECK.CB1.CMD.HMI_on", true);
        session.Step(6);
        Assert.Equal("Closed", State(session));
        session.Force("DECK.CB1.FIN.feedback", false);
        session.Step(2);
        Assert.Equal(true, session.Read("DECK.CB1.ALM.OpenedUnexpectedly.active").Value);
        session.Unforce("DECK.CB1.FIN.feedback");
        session.Step(5);
        Assert.Equal(true, session.Read("DECK.CB1.ALM.OpenedUnexpectedly.active").Value);
        session.Write("DECK.CB1.CMD.HMI_reset", true);
        session.Step();
        Assert.Equal(false, session.Read("DECK.CB1.ALM.OpenedUnexpectedly.active").Value);
    }

    [Fact]
    public void ThePushButtonFollowsItsContact()
    {
        var session = Session(Fixtures.PushButton, "B1");
        session.Step();
        Assert.Equal("Released", State(session));
        session.Force("DECK.B1.FIN.pressed", true);
        session.Step();
        Assert.Equal("Pressed", State(session));
        Assert.Equal(false, session.Read("DECK.B1.STS.remote_ok").Value);
    }

    [Fact]
    public void ScadaStateAlarmsWaitForTheirOnDelay()
    {
        var session = Session(Fixtures.Light, "L1");
        session.Step();
        session.Write("DECK.L1.CMD.HMI_on", true);
        session.Step(4);
        Assert.Equal("On", State(session));
        session.Force("DECK.L1.FIN.feedback", false);
        session.Step(15);
        var failure = Alarm(session, "LampFailure");
        Assert.False(failure.Active);
        Assert.False(failure.PlcReactive);
        Assert.Equal("L1: lamp failure", failure.Message);
        Assert.Equal(AlarmLevel.Warning, failure.Level);
        session.Step(10);
        Assert.True(Alarm(session, "LampFailure").Active);
        Assert.Throws<SimulationException>(() => session.Read("DECK.L1.ALM.LampFailure.active"));
        session.Unforce("DECK.L1.FIN.feedback");
        session.Step(2);
        Assert.False(Alarm(session, "LampFailure").Active);
    }

    [Fact]
    public void RangeAlarmsReportTheLevelOfTheThresholdCrossed()
    {
        var session = Session(Fixtures.Light, "L1");
        session.Step();
        session.Write("DECK.L1.FIN.current", 4.5);
        session.Step();
        Assert.Equal((true, (AlarmLevel?)AlarmLevel.Warning), (Alarm(session, "Overcurrent").Active, Alarm(session, "Overcurrent").RangeLevel));
        session.Write("DECK.L1.PAR.max_current", 6);
        session.Write("DECK.L1.FIN.current", 6.5);
        session.Step();
        Assert.Equal(AlarmLevel.Alarm, Alarm(session, "Overcurrent").RangeLevel);
        session.Write("DECK.L1.FIN.current", 1);
        session.Step();
        Assert.False(Alarm(session, "Overcurrent").Active);
    }

    [Fact]
    public void ScadaTimeoutAlarmsRunWhileTheirWindowIsOpen()
    {
        var session = Session(Fixtures.Light, "L1");
        session.Step();
        session.Force("DECK.L1.FIN.feedback", false);
        session.Write("DECK.L1.CMD.HMI_on", true);
        session.Step(5);
        Assert.Equal("TurningOn", State(session));
        Assert.False(Alarm(session, "SlowSwitch").Active);
        session.Step(8);
        Assert.True(Alarm(session, "SlowSwitch").Active);
        session.Step(40);
        Assert.Equal("Broken", State(session));
        Assert.False(Alarm(session, "SlowSwitch").Active);
    }

    [Fact]
    public void PlcReactiveAlarmsWriteTheirTagsAndLatchUntilReset()
    {
        var session = Session(Fixtures.Light, "L1");
        session.Step();
        session.Write("DECK.L1.FIN.current", 1);
        session.Step();
        Assert.Equal(true, session.Read("DECK.L1.ALM.CurrentWhileOff.active").Value);
        Assert.Equal(1L, session.Read("DECK.L1.ALM.CurrentWhileOff.raise_count").Value);
        var status = Alarm(session, "CurrentWhileOff");
        Assert.True(status.PlcReactive);
        Assert.True(status.Active);
        session.Write("DECK.L1.FIN.current", 0);
        session.Step(3);
        Assert.Equal(true, session.Read("DECK.L1.ALM.CurrentWhileOff.active").Value);
        session.Write("DECK.L1.CMD.HMI_reset", true);
        session.Step();
        Assert.Equal(false, session.Read("DECK.L1.ALM.CurrentWhileOff.active").Value);

        session.Write("DECK.L1.ALM.CurrentWhileOff.enabled", false);
        session.Write("DECK.L1.FIN.current", 1);
        session.Step();
        Assert.Equal(false, session.Read("DECK.L1.ALM.CurrentWhileOff.active").Value);
    }

    [Fact]
    public void GuardsMayReadPlcReactiveAlarms()
    {
        var blueprint = Fixtures.Load("Light");
        blueprint.Transitions.Single(t => t.Name == "switch_on").Guard = "[CMD.set_on] && [LOK.can_on] && ![ALM.CurrentWhileOff.active]";
        var library = Fixtures.Library(blueprint);
        var project = new Project();
        InstanceFactory.Create(project, library, Fixtures.Light, "L1", null);
        var program = LogicProgram.Build(project, library);
        Assert.Empty(program.Errors);
        Assert.DoesNotContain(BlueprintValidator.Validate(blueprint), i => i.Severity == "Error");
        var guard = program.Programs.Single().Transitions.Single(t => t.Name == "switch_on").Guard;
        Assert.Equal(ApolloIQ.Core.Expressions.ValueType.Bool, guard.Type);
    }
}
