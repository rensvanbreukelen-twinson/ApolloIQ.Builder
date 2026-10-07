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

public sealed class EquipmentModuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"em-{Guid.NewGuid():N}");
    private readonly BlueprintStore _store;
    private readonly Blueprint _light = Fixtures.Load("Light");
    private readonly Blueprint _source;
    private readonly Blueprint _plant;

    public EquipmentModuleTests()
    {
        _store = new BlueprintStore(Path.Combine(_root, "blueprints"));
        _source = new Blueprint
        { Id = Guid.NewGuid(),
            Kind = BlueprintKind.EM, Name = "LightSource", Interfaces = ["Base", "Switchable"],
            Roles = [new BlueprintRole { Name = "LAMP", BlueprintId = Fixtures.Light }],
            Always = [new BlueprintAction { Tag = "STS.remote_ok", Value = "[STS.enabled]" }],
            States =
            [
                new BlueprintState { Name = "Idle", Category = 200, Initial = true },
                new BlueprintState { Name = "Lighting", Category = 300, Entry = [new BlueprintAction { Tag = "LAMP.CMD.set_on", Value = "TRUE" }] },
                new BlueprintState { Name = "Lit", Category = 400 }
            ],
            Transitions =
            [
                new BlueprintTransition { Name = "start", From = ["Idle"], To = "Lighting", Guard = "[CMD.set_on]" },
                new BlueprintTransition { Name = "lit", From = ["Lighting"], To = "Lit", Guard = "[LAMP.STS.state] == On" }
            ]
        };
        _plant = new Blueprint
        { Id = Guid.NewGuid(),
            Kind = BlueprintKind.Unit, Name = "Plant", Interfaces = ["Base", "Switchable"],
            Roles = [new BlueprintRole { Name = "SRC", BlueprintId = _source.Id }],
            States =
            [
                new BlueprintState { Name = "Idle", Category = 200, Initial = true },
                new BlueprintState { Name = "Producing", Category = 400, Entry = [new BlueprintAction { Tag = "SRC.CMD.set_on", Value = "TRUE" }] }
            ],
            Transitions = [new BlueprintTransition { Name = "go", From = ["Idle"], To = "Producing", Guard = "[CMD.set_on]" }]
        };
        foreach (var blueprint in new[] { _light, _source, _plant })
            _store.Save(blueprint);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private CmLibrary Library()
    {
        var library = new CmLibrary();
        foreach (var blueprint in new[] { _light, _source, _plant })
            library.Replace(BlueprintTypes.ToCmType(blueprint.Clone()));
        return library;
    }

    private Blueprint? Find(Guid id) => _store.Find(id);

    private (Project Project, CmLibrary Library, UnitInstance Unit, UnitInstance Em, ControlModule Lamp) Build()
    {
        var library = Library();
        var project = new Project();
        var unit = UnitSupport.Create(project, _plant, "U", null, library, _store);
        var em = UnitSupport.Create(project, _source, "SRC1", unit.Id, library, _store);
        var lamp = InstanceFactory.Create(project, library, Fixtures.Light, "L1", null);
        UnitSupport.Move(project, library, _store, lamp.Id, em.Id);
        return (project, library, unit, em, lamp);
    }

    [Fact]
    public void RolesFollowTheIsa88Levels()
    {
        Assert.DoesNotContain(BlueprintValidator.Validate(_source, Find), i => i.Severity == "Error");
        Assert.DoesNotContain(BlueprintValidator.Validate(_plant, Find), i => i.Severity == "Error");
        var nested = new Blueprint { Id = Guid.NewGuid(), Kind = BlueprintKind.EM, Name = "Nested", Roles = [new BlueprintRole { Name = "INNER", BlueprintId = _source.Id }],
            States = [new BlueprintState { Name = "Idle", Category = 200, Initial = true }] };
        Assert.Contains(BlueprintValidator.Validate(nested, Find), i => i.Severity == "Error" && i.Message.Contains("G-151"));
        Assert.Contains("CMD.set_auto", BlueprintTypes.ToCmType(_source).ExpandTags().Select(t => $"{t.Group.Code()}.{t.Name}"));
        Assert.True(BlueprintTypes.ToCmType(_source).IsEquipmentModule);
    }

    [Fact]
    public void MembersSitUnderTheirContainerAndPathsFollowRenames()
    {
        var (project, _, unit, em, lamp) = Build();
        Assert.True(em.IsEquipmentModule);
        Assert.Equal("U.SRC1.L1", project.GetPath(lamp.Id));
        Assert.Equal(lamp.Id, em.RoleMembers["LAMP"]);
        Assert.Equal(em.Id, unit.RoleMembers["SRC"]);
        Assert.Equal(CommandInputConfig.EmRow, Assert.Single(lamp.CommandInputs!.Rows, r => r.Source == CommandSource.Unit).Name);
        Assert.Equal(CommandInputConfig.UnitRow, Assert.Single(em.CommandInputs!.Rows, r => r.Source == CommandSource.Unit).Name);

        var tag = project.GetChildren(lamp.Id).OfType<Builder.Core.Tags.Tag>().First();
        project.Rename(em.Id, "DG1");
        Assert.StartsWith("U.DG1.L1.", project.GetPath(tag.Id));

        Assert.Throws<ProjectException>(() => project.Rename(lamp.Id, "STS"));
        Assert.Throws<ProjectException>(() => project.Move(unit.Id, em.Id));
        var other = UnitSupport.Create(project, _source, "SRC2", unit.Id, new CmLibrary(), _store);
        Assert.Throws<ProjectException>(() => project.SetUnitMember(other.Id, "LAMP", em.Id));
    }

    [Fact]
    public void MovingAMemberOutEmptiesTheRoleAndRemovesTheRow()
    {
        var (project, library, _, em, lamp) = Build();
        UnitSupport.Move(project, library, _store, lamp.Id, null);
        Assert.Equal("L1", project.GetPath(lamp.Id));
        Assert.Empty(em.RoleMembers);
        Assert.DoesNotContain(lamp.CommandInputs!.Rows, r => r.Source == CommandSource.Unit);
    }

    [Fact]
    public void TheUnitCommandsTheEmAndTheEmCommandsItsCm()
    {
        var (project, library, _, _, _) = Build();
        var session = new SimulationSession(Guid.NewGuid(), project, library);
        Assert.Empty(session.Errors);
        session.Step(2);
        session.Write("U.SRC1.CMD.set_auto", true);
        session.Step(3);
        Assert.Equal(true, session.Read("U.SRC1.STS.auto").Value);
        session.Write("U.CMD.set_auto", true);
        session.Step(3);
        Assert.Equal(true, session.Read("U.STS.auto").Value);

        session.Write("U.CMD.set_on", true);
        session.Step(20);
        Assert.Equal("Producing", session.ControlModules().Single(c => c.Path == "U").StateName);
        Assert.Equal("On", session.ControlModules().Single(c => c.Path == "U.SRC1.L1").StateName);
        Assert.Equal("Lit", session.ControlModules().Single(c => c.Path == "U.SRC1").StateName);

        session.Write("U.SRC1.CMD.set_manual", true);
        session.Step(3);
        Assert.Equal(false, session.Read("U.STS.auto").Value);
    }

    [Fact]
    public void ContainersSurviveSavingAndLoading()
    {
        var (project, _, unit, em, lamp) = Build();
        var directory = Path.Combine(_root, "project");
        ProjectStore.Save(directory, Guid.NewGuid(), "P", project);
        var loaded = ProjectStore.Load(directory).Project;
        Assert.Equal("U.SRC1.L1", loaded.GetPath(lamp.Id));
        var again = loaded.Get<UnitInstance>(em.Id);
        Assert.True(again.IsEquipmentModule);
        Assert.Equal(lamp.Id, again.RoleMembers["LAMP"]);
        Assert.Equal(em.Id, loaded.Get<UnitInstance>(unit.Id).RoleMembers["SRC"]);
    }
}
