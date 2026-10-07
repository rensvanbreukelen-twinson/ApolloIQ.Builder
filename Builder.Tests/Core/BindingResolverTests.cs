using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Persistence;
using Xunit;

namespace Builder.Tests.Core;

public sealed class BindingResolverTests : IDisposable
{
    private static readonly CmLibrary Library = Fixtures.Library();

    private static readonly Guid Plc1 = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid Plc2 = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid Scada = Guid.Parse("d0000000-0000-0000-0000-000000000003");
    private static readonly Guid Controller = Guid.Parse("d0000000-0000-0000-0000-000000000004");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"builder-binding-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Link L(Guid from, Guid to, LinkClass cls) => new(Guid.NewGuid(), from, to, cls == LinkClass.Control ? "OPC UA" : "Modbus TCP", cls);

    private static (Project project, ControlModule gen, ControlModule breaker, Tag running) Sample(params Link[] extra)
    {
        var project = new Project();
        var pms = project.AddFolder("PMS");
        var gen = InstanceFactory.Create(project, Library, Fixtures.Light, "GEN1", pms.Id);
        var breaker = InstanceFactory.Create(project, Library, Fixtures.CircuitBreaker, "GEN1_CB", pms.Id);
        project.SetInterlocks(breaker.Id, [new InterlockRule { Kind = InterlockKind.SwitchOn, Condition = ExpressionReferences.ToStored(project, "[PMS.GEN1.is_running]") }]);
        project.SetTopology(
            [new Device(Plc1, "PLC1", DeviceRole.Plc), new Device(Plc2, "PLC2", DeviceRole.Plc), new Device(Scada, "SCADA", DeviceRole.Scada),
             new Device(Controller, "GEN1_CTRL", DeviceRole.ThirdParty)],
            [L(Scada, Plc1, LinkClass.Monitoring), L(Scada, Plc2, LinkClass.Monitoring), L(Scada, Controller, LinkClass.Monitoring), .. extra]);
        project.SetExecutionDevice(gen.Id, Plc1);
        project.SetExecutionDevice(breaker.Id, Plc2);
        var running = new TagRegistry(project).FindByPath("PMS.GEN1.FIN.feedback")!;
        project.SetOrigin(running.Id, new TagOrigin(Controller, "Modbus TCP", "40021"));
        return (project, gen, breaker, running);
    }

    private static TagAccess Access(BindingReport report, string tag, Guid consumer) =>
        report.Accesses.Single(a => a.Tag == tag && a.ConsumerDeviceId == consumer && a.Critical == (consumer != Scada));

    [Fact]
    public void ControlInputsOverAMonitoringLinkAreSafetyErrors()
    {
        var (project, _, _, _) = Sample();
        var report = BindingResolver.Resolve(project);
        var running = Access(report, "PMS.GEN1.FIN.feedback", Plc1);
        Assert.Equal(AccessKind.Relayed, running.Kind);
        Assert.True(running.SafetyViolation);
        Assert.Contains(report.Issues, i => i.Code == "safety" && i.Subject == "PMS.GEN1.FIN.feedback");
        Assert.Contains(report.Issues, i => i.Code == "safety" && i.Subject == "PMS.GEN1.STS.state");
    }

    [Fact]
    public void DirectControlLinksResolveWithGeneratedAddresses()
    {
        var (project, _, _, _) = Sample(L(Plc1, Controller, LinkClass.Control), L(Plc1, Plc2, LinkClass.Control));
        var report = BindingResolver.Resolve(project);
        Assert.Equal(0, report.Errors);
        var running = Access(report, "PMS.GEN1.FIN.feedback", Plc1);
        Assert.Equal((AccessKind.Direct, "40021"), (running.Kind, running.Address));
        var state = Access(report, "PMS.GEN1.STS.state", Plc2);
        Assert.Equal(AccessKind.Direct, state.Kind);
        Assert.StartsWith("PLC1:T_", state.Address);
        Assert.Contains(report.Accesses, a => a.Tag == "PMS.GEN1.STS.state" && a.ConsumerDeviceId == Plc2 && a.UsedBy == "PMS.GEN1_CB interlock");
        var hmi = Access(report, "PMS.GEN1.STS.state", Scada);
        Assert.Equal(AccessKind.Direct, hmi.Kind);
    }

    [Fact]
    public void MissingPathIsUnreachable()
    {
        var project = new Project();
        var gen = InstanceFactory.Create(project, Library, Fixtures.Light, "GEN1", null);
        project.SetTopology([new Device(Plc1, "PLC1", DeviceRole.Plc), new Device(Controller, "CTRL", DeviceRole.ThirdParty)], []);
        project.SetExecutionDevice(gen.Id, Plc1);
        project.SetOrigin(new TagRegistry(project).FindByPath("GEN1.FIN.feedback")!.Id, new TagOrigin(Controller, "Modbus TCP", "1"));
        var report = BindingResolver.Resolve(project);
        Assert.Contains(report.Issues, i => i.Code == "unreachable" && i.Subject == "GEN1.FIN.feedback");
        Assert.Contains(report.Issues, i => i.Code == "local_input" && i.Subject == "GEN1" && i.Message.Contains("current"));
    }

    [Fact]
    public void UndeployedCmsAreReported()
    {
        var project = new Project();
        InstanceFactory.Create(project, Library, Fixtures.Light, "GEN1", null);
        Assert.Contains(BindingResolver.Resolve(project).Issues, i => i.Code == "no_topology");
        project.SetTopology([new Device(Plc1, "PLC1", DeviceRole.Plc)], []);
        Assert.Contains(BindingResolver.Resolve(project).Issues, i => i.Code == "not_deployed" && i.Subject == "GEN1");
    }

    [Fact]
    public void OnlyPlcsRunLogicAndOnlyFinTagsHaveAnOrigin()
    {
        var (project, gen, _, _) = Sample();
        Assert.Throws<ProjectException>(() => project.SetExecutionDevice(gen.Id, Scada));
        var state = new TagRegistry(project).FindByPath("PMS.GEN1.STS.state")!;
        Assert.Throws<ProjectException>(() => project.SetOrigin(state.Id, new TagOrigin(Plc1, "x", "y")));
    }

    [Fact]
    public void OppositeCommandSourcesWithoutAPicAreWarned()
    {
        var project = new Project();
        var cb = InstanceFactory.Create(project, Library, Fixtures.CircuitBreaker, "CB", null);
        InstanceFactory.Create(project, Library, Fixtures.PushButton, "UP", null);
        InstanceFactory.Create(project, Library, Fixtures.PushButton, "DOWN", null);
        var registry = new TagRegistry(project);
        project.SetCommandWires(cb.Id, [new CommandWire(registry.FindByPath("UP.INT.pressed")!.Id, WireMode.On),
            new CommandWire(registry.FindByPath("DOWN.INT.pressed")!.Id, WireMode.Off)]);
        project.SetTopology([new Device(Plc1, "PLC1", DeviceRole.Plc)], []);
        Assert.Contains(BindingResolver.Resolve(project).Issues, i => i.Code == "opposite_commands");
    }

    [Fact]
    public void RemovingADeviceClearsItsUse()
    {
        var (project, gen, _, running) = Sample();
        project.SetTopology([new Device(Scada, "SCADA", DeviceRole.Scada)], []);
        Assert.Null(gen.ExecutionDeviceId);
        Assert.Null(running.Origin);
    }

    [Fact]
    public void TopologyAndBindingsRoundTrip()
    {
        var (project, gen, breaker, running) = Sample(L(Plc1, Plc2, LinkClass.Control));
        ProjectStore.Save(_root, Guid.NewGuid(), "Binding", project);
        var loaded = ProjectStore.Load(_root).Project;
        Assert.Equal(4, loaded.Topology.Devices.Count);
        Assert.Equal(4, loaded.Topology.Links.Count);
        Assert.Equal(Plc1, loaded.Get<ControlModule>(gen.Id).ExecutionDeviceId);
        Assert.Equal(Plc2, loaded.Get<ControlModule>(breaker.Id).ExecutionDeviceId);
        Assert.Single(loaded.Get<ControlModule>(breaker.Id).Interlocks);
        Assert.Equal(running.Origin, loaded.Get<Tag>(running.Id).Origin);
    }
}
