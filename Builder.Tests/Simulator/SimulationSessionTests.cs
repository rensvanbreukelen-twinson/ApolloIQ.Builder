using System.Text.Json;
using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Simulator;
using Xunit;

namespace Builder.Tests.Simulator;

public class SimulationSessionTests
{
    private static readonly CmLibrary Library = Fixtures.Library();

    private static (Project project, SimulationSession session) NewSession()
    {
        var project = new Project();
        var pms = project.AddFolder("PMS");
        InstanceFactory.Create(project, Library, Fixtures.Light, "GEN1", pms.Id);
        InstanceFactory.Create(project, Library, Fixtures.CircuitBreaker, "GEN1_CB", pms.Id);
        return (project, new SimulationSession(Guid.NewGuid(), project, Library));
    }

    private static int State(SimulationSession session, string cm) =>
        session.ControlModules().Single(c => c.Path == cm).State;

    [Fact]
    public void StartsPausedWithoutErrors()
    {
        var (_, session) = NewSession();
        Assert.Equal(SimulationStatus.Paused, session.Status);
        Assert.Empty(session.Errors);
        Assert.Equal(0, session.Snapshot().Cycle);
        Assert.Equal(2, session.ControlModules().Count);
    }

    [Fact]
    public void StepRunsCyclesAndPublishesOnlyChanges()
    {
        var (_, session) = NewSession();
        var batches = new List<SimChangeBatch>();
        session.ValuesChanged += batches.Add;
        session.Step();
        Assert.Contains(batches.SelectMany(b => b.Values), v => v.Path == "PMS.GEN1_CB.FIN.power_ok" && (bool)v.Value!);
        batches.Clear();
        session.Step(5);
        Assert.Equal(6, session.Snapshot().Cycle);
        Assert.DoesNotContain(batches.SelectMany(b => b.Values), v => v.Path == "PMS.GEN1_CB.FIN.power_ok");
    }

    [Fact]
    public void WritesAreReadByTheNextCycle()
    {
        var (_, session) = NewSession();
        session.Step(4);
        Assert.Equal("Open", session.ControlModules().Single(c => c.Path == "PMS.GEN1_CB").StateName);
        session.Write("PMS.GEN1_CB.CMD.HMI_on", true);
        Assert.Equal(true, session.Read("PMS.GEN1_CB.CMD.HMI_on").Value);
        session.Step();
        Assert.Equal(300, State(session, "PMS.GEN1_CB"));
        Assert.Equal(false, session.Read("PMS.GEN1_CB.CMD.HMI_on").Value);
    }

    [Fact]
    public void TagsCanBeFoundBySymbolKeyAndId()
    {
        var (project, session) = NewSession();
        var tag = project.Tags.Single(t => project.GetPath(t.Id) == "PMS.GEN1.PAR.max_switch_time");
        Assert.Equal(2d, session.Read(tag.SymbolKey).Value);
        Assert.Equal(2d, session.Read(tag.Id.ToString()).Value);
        Assert.Throws<SimulationException>(() => session.Read("PMS.GEN1.PAR.nothing"));
    }

    [Fact]
    public void LogicOwnedTagsCannotBeWrittenButCanBeForced()
    {
        var (_, session) = NewSession();
        Assert.Throws<SimulationException>(() => session.Write("PMS.GEN1.STS.remote_ok", false));
        var forced = session.Force("PMS.GEN1.STS.remote_ok", false);
        Assert.True(forced.Forced);
        session.Step();
        Assert.Equal(false, session.Read("PMS.GEN1.STS.remote_ok").Value);
        var released = session.Unforce("PMS.GEN1.STS.remote_ok");
        Assert.False(released.Forced);
        Assert.Equal(true, released.Value);
    }

    [Fact]
    public void ValuesAreConvertedToTheTagType()
    {
        var (_, session) = NewSession();
        using var json = JsonDocument.Parse("""{ "a": 12.7, "b": "true", "c": 1 }""");
        Assert.Equal(13L, session.Write("PMS.GEN1.LOK.can_on_status", json.RootElement.GetProperty("a")).Value);
        Assert.Equal(true, session.Write("PMS.GEN1.SET.invert_feedback", json.RootElement.GetProperty("b")).Value);
        Assert.Equal(true, session.Write("PMS.GEN1_CB.SET.bistable", json.RootElement.GetProperty("c")).Value);
        Assert.Equal(12.5d, session.Write("PMS.GEN1.PAR.max_switch_time", "12.5").Value);
        Assert.Throws<SimulationException>(() => session.Write("PMS.GEN1.PAR.max_switch_time", "fast"));
    }

    [Fact]
    public void BadQualityIsInjectedAndCleared()
    {
        var (_, session) = NewSession();
        session.Step(4);
        var bad = session.SetBadQuality("PMS.GEN1_CB.FIN.feedback", true);
        Assert.False(bad.Good);
        session.Step();
        Assert.Equal(999, State(session, "PMS.GEN1_CB"));
        session.UnforceAll();
        session.Step(2);
        Assert.Equal(200, State(session, "PMS.GEN1_CB"));
    }

    [Fact]
    public void StateChangesAreReported()
    {
        var (_, session) = NewSession();
        var changes = new List<Builder.Logic.Runtime.StateChange>();
        session.StateChanged += changes.Add;
        session.Step(4);
        Assert.Contains(changes, c => c.ControlModule == "PMS.GEN1_CB" && c.To == 200 && c.Transition == "ready");
    }

    [Fact]
    public void ReloadKeepsValuesStatesAndForces()
    {
        var (project, session) = NewSession();
        session.Step(4);
        session.Write("PMS.GEN1_CB.CMD.HMI_on", true);
        session.Step(3);
        session.Write("PMS.GEN1.PAR.max_switch_time", 42);
        session.Force("PMS.GEN1.FIN.current", 91);

        InstanceFactory.Create(project, Library, Fixtures.CircuitBreaker, "GEN2_CB", project.Objects.OfType<Folder>().Single().Id);
        session.Reload(project);

        Assert.Equal(3, session.ControlModules().Count);
        Assert.Equal(400, State(session, "PMS.GEN1_CB"));
        Assert.Equal(42d, session.Read("PMS.GEN1.PAR.max_switch_time").Value);
        Assert.True(session.Read("PMS.GEN1.FIN.current").Forced);
        Assert.Equal(7, session.Snapshot().Cycle);
        session.Step();
        Assert.Equal(400, State(session, "PMS.GEN1_CB"));
    }

    [Fact]
    public void ResetStartsFromTheInitialValues()
    {
        var (project, session) = NewSession();
        session.Step(10);
        session.Write("PMS.GEN1.PAR.max_switch_time", 42);
        session.Reset(project);
        Assert.Equal(0, session.Snapshot().Cycle);
        Assert.Equal(2d, session.Read("PMS.GEN1.PAR.max_switch_time").Value);
        Assert.Equal(200, State(session, "PMS.GEN1"));
        Assert.Equal(0, State(session, "PMS.GEN1_CB"));
    }

    [Fact]
    public async Task RunsInRealTimeUntilPaused()
    {
        var (_, session) = NewSession();
        session.Speed = 10;
        var statuses = new List<SimStatus>();
        session.StatusChanged += statuses.Add;
        session.Start();
        Assert.Throws<SimulationException>(() => session.Step());
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await session.PauseAsync();
        var cycles = session.Snapshot().Cycle;
        Assert.InRange(cycles, 20, 80);
        Assert.Equal([SimulationStatus.Running, SimulationStatus.Paused], statuses.Select(s => s.Status));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(cycles, session.Snapshot().Cycle);
        Assert.Equal(200, State(session, "PMS.GEN1"));
    }

    [Fact]
    public void SpeedIsLimited()
    {
        var (_, session) = NewSession();
        Assert.Throws<SimulationException>(() => session.Speed = 0);
        Assert.Throws<SimulationException>(() => session.Speed = 1000);
    }

    [Fact]
    public void ManagerKeepsOneSessionPerProject()
    {
        var manager = new SimulationManager();
        var id = Guid.NewGuid();
        var created = 0;
        var first = manager.GetOrCreate(id, () =>
        {
            created++;
            return NewSession().session;
        });
        var second = manager.GetOrCreate(id, () => throw new InvalidOperationException());
        Assert.Same(first, second);
        Assert.Equal(1, created);
        Assert.Same(first, manager.Find(id));
    }

    [Fact]
    public void CuttingALinkGivesBadQualityToTheValuesThatCrossIt()
    {
        var (project, _) = NewSession();
        var plc = Guid.NewGuid();
        var controller = Guid.NewGuid();
        var link = new Link(Guid.NewGuid(), plc, controller, "Modbus TCP", LinkClass.Control);
        project.SetTopology([new Device(plc, "PLC1", DeviceRole.Plc), new Device(controller, "CTRL", DeviceRole.ThirdParty)], [link]);
        var gen = project.Objects.OfType<ControlModule>().Single(c => c.Name == "GEN1");
        project.SetExecutionDevice(gen.Id, plc);
        foreach (var tag in project.GetChildren(gen.Id).OfType<Builder.Core.Tags.Tag>().Where(t => t.Group == Builder.Core.Tags.TagGroup.Fin))
            project.SetOrigin(tag.Id, new TagOrigin(controller, "Modbus TCP", tag.Name));
        var session = new SimulationSession(Guid.NewGuid(), project, Library);
        session.Step(4);
        Assert.Equal(200, State(session, "PMS.GEN1"));
        var listed = Assert.Single(session.Links());
        Assert.True(listed.Tags > 0);

        session.SetLinkDown(link.Id, true);
        Assert.False(session.Read("PMS.GEN1.FIN.feedback").Good);
        session.Step();
        Assert.Equal(999, State(session, "PMS.GEN1"));
        Assert.True(session.Read("PMS.GEN1_CB.FIN.feedback").Good);

        session.SetLinkDown(link.Id, false);
        session.Step(2);
        Assert.Equal(200, State(session, "PMS.GEN1"));
        Assert.Throws<SimulationException>(() => session.SetLinkDown(Guid.NewGuid(), true));
    }
}
