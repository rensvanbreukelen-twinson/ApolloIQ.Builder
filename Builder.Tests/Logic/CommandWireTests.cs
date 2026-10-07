using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Persistence;
using Xunit;

namespace Builder.Tests.Logic;

public sealed class CommandWireTests : IDisposable
{
    private static readonly CmLibrary Library = CmLibrary.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "cm-types"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"builder-wires-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static (Project project, ControlModule breaker, Tag pressed) Sample()
    {
        var project = new Project();
        var pms = project.AddFolder("PMS");
        var breaker = InstanceFactory.Create(project, Library, "CircuitBreaker", "CB", pms.Id);
        var button = InstanceFactory.Create(project, Library, "PushButton", "BTN", pms.Id);
        return (project, breaker, new TagRegistry(project).FindByPath("PMS.BTN.INT.pressed")!);
    }

    [Fact]
    public void WiresAreValidated()
    {
        var (project, breaker, pressed) = Sample();
        var state = new TagRegistry(project).FindByPath("PMS.BTN.STS.state")!;
        var own = new TagRegistry(project).FindByPath("PMS.CB.CMD.reset")!;
        ProjectException Fails(CommandWire wire) => Assert.Throws<ProjectException>(() => project.SetCommandWires(breaker.Id, [wire]));
        Assert.Contains("Bool", Fails(new CommandWire(state.Id, WireMode.On)).Message);
        Assert.Contains("own commands", Fails(new CommandWire(own.Id, WireMode.Toggle)).Message);
        Assert.Contains("needs the command", Fails(new CommandWire(pressed.Id, WireMode.Direct)).Message);
        Assert.Contains("CMD.fly", Fails(new CommandWire(pressed.Id, WireMode.Direct, "fly")).Message);
        project.SetCommandWires(breaker.Id, [new CommandWire(pressed.Id, WireMode.Toggle)]);
        Assert.Single(breaker.CommandWires);
    }

    [Fact]
    public void WiresRoundTripAndDisappearWithTheirSource()
    {
        var (project, breaker, pressed) = Sample();
        project.SetCommandWires(breaker.Id, [new CommandWire(pressed.Id, WireMode.Maintained), new CommandWire(pressed.Id, WireMode.Direct, "reset")]);
        ProjectStore.Save(_root, Guid.NewGuid(), "Wires", project);
        var loaded = ProjectStore.Load(_root).Project;
        Assert.Equal(breaker.CommandWires, loaded.Get<ControlModule>(breaker.Id).CommandWires);

        project.Delete(pressed.ParentId!.Value);
        Assert.Empty(breaker.CommandWires);
    }
}
