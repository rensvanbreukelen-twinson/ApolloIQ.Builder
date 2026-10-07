using System.Text.Json;
using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Logic.Blueprints;
using Builder.Simulator;
using Xunit;

namespace Builder.Tests.Blueprints;

public class BlueprintRuntimeTests
{
    private static CmLibrary Library()
    {
        var library = new CmLibrary();
        foreach (var name in new[] { "Light", "CircuitBreaker", "GenSet", "PushButton" })
            library.Add(BlueprintTypes.ToCmType(JsonSerializer.Deserialize<Blueprint>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "blueprints", $"{name}.blueprint.json")), Blueprint.Json)!));
        return library;
    }

    private static SimulationSession Session(string type, string name)
    {
        var project = new Project();
        var folder = project.AddFolder("DECK");
        InstanceFactory.Create(project, Library(), type, name, folder.Id);
        var session = new SimulationSession(Guid.NewGuid(), project, Library());
        Assert.Empty(session.Errors);
        return session;
    }

    private static string State(SimulationSession session) => session.ControlModules().Single().StateName;

    [Fact]
    public void TheLightSwitchesOnAndOffAndBreaksOnATimeout()
    {
        var session = Session("Light", "L1");
        session.Step();
        Assert.Equal("Off", State(session));
        session.Write("DECK.L1.CMD.HMI_on", true);
        session.Step();
        Assert.Equal("TurningOn", State(session));
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
    public void TheLightIsUnavailableWhenItsFeedbackIsBad()
    {
        var session = Session("Light", "L1");
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
        var session = Session("CircuitBreaker", "CB1");
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
        var session = Session("CircuitBreaker", "CB1");
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
    public void ThereAreNoSlotTags()
    {
        var session = Session("CircuitBreaker", "CB1");
        session.Step(2);
        Assert.Equal(true, session.Read("DECK.CB1.LOK.can_on").Value);
        Assert.Equal(false, session.Read("DECK.CB1.LOK.trip").Value);
        Assert.Throws<SimulationException>(() => session.Read("DECK.CB1.LOK.force_off"));
        Assert.Throws<SimulationException>(() => session.Read("DECK.CB1.LOK.force_off_cause"));
    }

    [Fact]
    public void ThePushButtonFollowsItsContact()
    {
        var session = Session("PushButton", "B1");
        session.Step();
        Assert.Equal("Released", State(session));
        session.Force("DECK.B1.FIN.pressed", true);
        session.Step();
        Assert.Equal("Pressed", State(session));
        Assert.Equal(false, session.Read("DECK.B1.STS.remote_ok").Value);
    }

    [Fact]
    public void TheGenSetStartsAndStopsThroughTheCooldown()
    {
        var session = Session("GenSet", "G1");
        session.Write("DECK.G1.PAR.cooldown_time", 2);
        session.Step(2);
        Assert.Equal("Available", State(session));
        session.Write("DECK.G1.CMD.HMI_on", true);
        session.Step(70);
        Assert.Equal("Running", State(session));
        session.Step(110);
        Assert.Equal("ReadyToConnect", State(session));
        session.Write("DECK.G1.CMD.HMI_off", true);
        session.Step(2);
        Assert.Equal("CoolingDown", State(session));
        session.Step();
        Assert.InRange((double)session.Read("DECK.G1.STS.cooldown_remaining").Value!, 1.5, 2.0);
        session.Step(42);
        Assert.Equal("StoppingEngine", State(session));
        Assert.Equal(true, session.Read("DECK.G1.OUT.stop_request").Value);
        session.Step(3);
        Assert.Equal("Available", State(session));
        Assert.Equal(false, session.Read("DECK.G1.OUT.stop_request").Value);
    }
}
