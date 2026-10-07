using Builder.Backend.Services;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using ApolloIQ.Core.Blueprints;
using Builder.Logic.Blueprints;
using Builder.Persistence;
using Builder.Simulator;
using Xunit;

namespace Builder.Tests.Blueprints;

public class CommandInputTests
{
    private static CmLibrary Library(params Blueprint[] blueprints)
    {
        var library = new CmLibrary();
        foreach (var blueprint in blueprints)
            library.Replace(BlueprintTypes.ToCmType(blueprint.Clone()));
        return library;
    }

    [Fact]
    public void BlueprintDefaultsCreateTheInputTags()
    {
        var library = Library(Fixtures.Load("Light"));
        var project = new Project();
        var cm = InstanceFactory.Create(project, library, Fixtures.Light, "L1", null);
        var tags = project.GetChildren(cm.Id).OfType<Builder.Core.Tags.Tag>().Select(t => $"{t.Group.Code()}.{t.Name}").ToHashSet();
        Assert.Contains("CMD.HMI_on", tags);
        Assert.Contains("CMD.HMI_reset", tags);
        Assert.Contains("FIN.BOARD", tags);
        Assert.Contains("SET.invert_BOARD", tags);
        Assert.Contains("PAR.BOARD_debounce", tags);
        Assert.Contains("STS.active_input", tags);

        CommandInputBehaviour.Configure(project, library, cm.Id, new CommandInputConfig([new CommandInput("HMI", CommandSource.Hmi, InputKind.Pulse, On: 1, Off: 1)]));
        tags = project.GetChildren(cm.Id).OfType<Builder.Core.Tags.Tag>().Select(t => $"{t.Group.Code()}.{t.Name}").ToHashSet();
        Assert.DoesNotContain("FIN.BOARD", tags);
        Assert.DoesNotContain("PAR.BOARD_debounce", tags);
        Assert.DoesNotContain("CMD.HMI_reset", tags);
        Assert.Contains("CMD.HMI_off", tags);
    }

    [Theory]
    [InlineData("SW", CommandSource.DigitalInput, InputKind.Switch, InputDrives.OnOff, true, "only command input")]
    [InlineData("X", CommandSource.Hmi, InputKind.Pulse, InputDrives.Toggle, false, "only a Button can toggle")]
    [InlineData("X", CommandSource.Hmi, InputKind.Button, InputDrives.OnOff, false, "does not fit")]
    public void InvalidRowsAreRejected(string name, CommandSource source, InputKind kind, InputDrives drives, bool withHmi, string message)
    {
        var rows = new List<CommandInput> { new(name, source, kind, drives, 1, 1) };
        if (withHmi)
            rows.Add(new CommandInput("HMI", CommandSource.Hmi, InputKind.Pulse, On: 1, Off: 1));
        var problems = CommandInputBehaviour.Problems(new CommandInputConfig(rows), ["reset"], hasPair: true);
        Assert.Contains(problems, p => p.Contains(message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InputsSurviveSavingAndLoading()
    {
        var library = Library(Fixtures.Load("Light"));
        var project = new Project();
        var cm = InstanceFactory.Create(project, library, Fixtures.Light, "L1", null);
        var directory = Path.Combine(Path.GetTempPath(), $"inputs-{Guid.NewGuid():N}");
        try
        {
            ProjectStore.Save(directory, Guid.NewGuid(), "P", project);
            var loaded = ProjectStore.Load(directory).Project;
            var again = loaded.Objects.OfType<ControlModule>().Single();
            Assert.Equal(["HMI", "BOARD"], again.CommandInputs!.Rows.Select(r => r.Name));
            Assert.Equal(0.05, again.CommandInputs.Rows[1].Debounce);
            Assert.Equal(InputDrives.Toggle, again.CommandInputs.Rows[1].Drives);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AUnitRowCountsOnlyInAutoAndAnOverrideDisconnectsTheUnit()
    {
        var light = Fixtures.Load("Light");
        var pair = new Blueprint
        { Id = Guid.NewGuid(),
            Kind = BlueprintKind.Unit, Name = "Pair", Interfaces = ["Base", "AutoManual"],
            Roles = [new BlueprintRole { Name = "MAIN", BlueprintId = Fixtures.Light }],
            States = [new BlueprintState { Name = "Idle", Category = 200, Initial = true }]
        };
        var library = Library(light, pair);
        var project = new Project();
        var cm = InstanceFactory.Create(project, library, Fixtures.Light, "L1", null);
        CommandInputBehaviour.Configure(project, library, cm.Id, new CommandInputConfig([
            new CommandInput("HMI", CommandSource.Hmi, InputKind.Pulse, On: 1, Off: 1, InAuto: InputInAuto.Ignore),
            new CommandInput("OVR", CommandSource.DigitalInput, InputKind.Button, InputDrives.Off, null, 1, InputInAuto.Override)]));
        var unit = UnitSupport.Create(project, pair, "PAIR", null);
        UnitSupport.SetMember(project, library, unit.Id, "MAIN", cm.Id);
        Assert.Contains(cm.CommandInputs!.Rows, r => r.Name == CommandInputConfig.UnitRow);

        var session = new SimulationSession(Guid.NewGuid(), project, library);
        Assert.Empty(session.Errors);
        session.Step(2);
        session.Write("PAIR.L1.CMD.HMI_on", true);
        session.Step(3);
        Assert.Equal("On", session.ControlModules().Single(c => c.Path == "PAIR.L1").StateName);

        session.Force("PAIR.STS.auto", true);
        session.Write("PAIR.L1.CMD.HMI_off", true);
        session.Step(3);
        Assert.Equal("On", session.ControlModules().Single(c => c.Path == "PAIR.L1").StateName);

        session.Force("PAIR.L1.FIN.OVR", true);
        session.Step(1);
        Assert.Equal(true, session.Read("PAIR.L1.STS.override").Value);
        session.Step(3);
        Assert.Equal("Off", session.ControlModules().Single(c => c.Path == "PAIR.L1").StateName);

        session.Unforce("PAIR.L1.FIN.OVR");
        session.Step();
        session.Write("PAIR.L1.CMD.UNIT_on", true);
        session.Step(3);
        Assert.Equal("On", session.ControlModules().Single(c => c.Path == "PAIR.L1").StateName);

        UnitSupport.SetMember(project, library, unit.Id, "MAIN", null);
        Assert.Equal("L1", project.GetPath(cm.Id));
        Assert.DoesNotContain(cm.CommandInputs!.Rows, r => r.Name == CommandInputConfig.UnitRow);
    }

    [Fact]
    public void TheLocalRemoteSelectorChoosesTheRows()
    {
        var light = Fixtures.Load("Light");
        light.Tags.Add(new BlueprintTag { Group = "FIN", Name = "remote", DataType = "Bool", Source = InputSource.LocalIO });
        light.CommandInputs = null;
        var library = Library(light);
        var project = new Project();
        var cm = InstanceFactory.Create(project, library, Fixtures.Light, "L1", null);
        CommandInputBehaviour.Configure(project, library, cm.Id, new CommandInputConfig([
            new CommandInput("HMI", CommandSource.Hmi, InputKind.Pulse, On: 1, Off: 1, Location: InputLocation.Remote),
            new CommandInput("LOCAL", CommandSource.DigitalInput, InputKind.Button, InputDrives.Toggle, 1, 1, Location: InputLocation.Local)], Selector: "FIN.remote"));
        var session = new SimulationSession(Guid.NewGuid(), project, library);
        session.Force("L1.FIN.remote", true);
        session.Step(2);
        session.Force("L1.FIN.LOCAL", true);
        session.Step(3);
        Assert.Equal("Off", session.ControlModules().Single(c => c.Path == "L1").StateName);
        session.Write("L1.CMD.HMI_on", true);
        session.Step(3);
        Assert.Equal("On", session.ControlModules().Single(c => c.Path == "L1").StateName);

        session.Force("L1.FIN.remote", false);
        session.Write("L1.CMD.HMI_off", true);
        session.Step(3);
        Assert.Equal("On", session.ControlModules().Single(c => c.Path == "L1").StateName);
        session.Force("L1.FIN.LOCAL", false);
        session.Step();
        session.Force("L1.FIN.LOCAL", true);
        session.Step(3);
        Assert.Contains(session.ControlModules().Single(c => c.Path == "L1").StateName, new[] { "TurningOff", "Off" });
    }

    [Fact]
    public void ASwitchControlledCmCannotJoinAUnit()
    {
        var library = Library(Fixtures.Load("Light"));
        var project = new Project();
        var cm = InstanceFactory.Create(project, library, Fixtures.Light, "L1", null);
        CommandInputBehaviour.Configure(project, library, cm.Id, new CommandInputConfig([new CommandInput("SW", CommandSource.DigitalInput, InputKind.Switch, On: 1, Off: 1)]));
        var pair = new Blueprint { Id = Guid.NewGuid(), Kind = BlueprintKind.Unit, Name = "Pair", Interfaces = ["Base", "AutoManual"], Roles = [new BlueprintRole { Name = "MAIN", BlueprintId = Fixtures.Light }] };
        var unit = UnitSupport.Create(project, pair, "PAIR", null);
        Assert.Throws<ProjectException>(() => UnitSupport.SetMember(project, library, unit.Id, "MAIN", cm.Id));
    }

    [Fact]
    public void AnOverrideOnAMemberPutsTheUnitInManualWithAnAlarm()
    {
        var light = Fixtures.Load("Light");
        var pair = new Blueprint
        { Id = Guid.NewGuid(),
            Kind = BlueprintKind.Unit, Name = "Pair", Interfaces = ["Base", "AutoManual"],
            Roles = [new BlueprintRole { Name = "MAIN", BlueprintId = Fixtures.Light }],
            States = [new BlueprintState { Name = "Idle", Category = 200, Initial = true },
                new BlueprintState { Name = "Lit", Category = 400, Entry = [new BlueprintAction { Tag = "MAIN.CMD.set_on", Value = "TRUE" }] }],
            Transitions = [new BlueprintTransition { Name = "go", From = ["Idle"], To = "Lit", Guard = "[MAIN.STS.state] == Off" }]
        };
        var library = Library(light, pair);
        var project = new Project();
        var cm = InstanceFactory.Create(project, library, Fixtures.Light, "L1", null);
        CommandInputBehaviour.Configure(project, library, cm.Id, new CommandInputConfig([
            new CommandInput("OVR", CommandSource.DigitalInput, InputKind.Button, InputDrives.Off, null, 1, InputInAuto.Override)]));
        var unit = UnitSupport.Create(project, pair, "PAIR", null);
        UnitSupport.SetMember(project, library, unit.Id, "MAIN", cm.Id);
        var session = new SimulationSession(Guid.NewGuid(), project, library);
        Assert.Empty(session.Errors);
        session.Step(2);
        session.Write("PAIR.CMD.set_auto", true);
        session.Step(6);
        Assert.Equal(true, session.Read("PAIR.STS.auto").Value);
        Assert.Equal("Lit", session.ControlModules().Single(c => c.Path == "PAIR").StateName);
        Assert.Equal("On", session.ControlModules().Single(c => c.Path == "PAIR.L1").StateName);

        session.Force("PAIR.L1.FIN.OVR", true);
        session.Step(2);
        Assert.Equal(false, session.Read("PAIR.STS.auto").Value);
        Assert.Equal(true, session.Read("PAIR.ALM.ManualByOverride.active").Value);
        session.Unforce("PAIR.L1.FIN.OVR");
        session.Step(4);
        Assert.Contains(session.ControlModules().Single(c => c.Path == "PAIR.L1").StateName, new[] { "TurningOff", "Off" });
        session.Write("PAIR.CMD.set_auto", true);
        session.Step(2);
        Assert.Equal(false, session.Read("PAIR.ALM.ManualByOverride.active").Value);
        Assert.Equal(true, session.Read("PAIR.STS.auto").Value);
    }
}
