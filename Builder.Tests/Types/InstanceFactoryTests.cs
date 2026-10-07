using ApolloIQ.Core.Versioning;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Xunit;

namespace Builder.Tests.Types;

public class InstanceFactoryTests
{
    private static readonly CmLibrary Library = Fixtures.Library();

    private static (Project project, Folder pms) NewProject()
    {
        var project = new Project();
        return (project, project.AddFolder("PMS"));
    }

    private static int TagCount(Guid blueprint)
    {
        var type = Library.Find(blueprint)!;
        return type.ExpandTags().Count + (type.DefaultCommandInputs is { } inputs ? CommandInputBehaviour.Tags(inputs).Count : 0);
    }

    [Fact]
    public void CreatesAControlModuleWithAllTagsOfTheBlueprint()
    {
        var (project, pms) = NewProject();
        var gen1 = InstanceFactory.Create(project, Library, Fixtures.Light, "GEN1", pms.Id);

        var tags = new TagRegistry(project).ForControlModule(gen1.Id).ToList();
        Assert.Equal(TagCount(Fixtures.Light), tags.Count);
        Assert.Equal(Fixtures.Light, gen1.BlueprintId);
        Assert.Equal(BlueprintVersion.Initial, gen1.BlueprintVersion);
        Assert.NotNull(new TagRegistry(project).FindByPath("PMS.GEN1.STS.state"));
        Assert.NotNull(new TagRegistry(project).FindByPath("PMS.GEN1.PAR.max_switch_time"));
        Assert.NotNull(new TagRegistry(project).FindByPath("PMS.GEN1.CMD.HMI_on"));
    }

    [Fact]
    public void TagsCarryTheirTemplateDetails()
    {
        var (project, pms) = NewProject();
        InstanceFactory.Create(project, Library, Fixtures.Light, "GEN1", pms.Id);
        var tag = new TagRegistry(project).FindByPath("PMS.GEN1.PAR.max_switch_time")!;
        Assert.Equal(TagDataType.Real, tag.DataType);
        Assert.Equal(TagDirection.InOut, tag.Direction);
        Assert.Equal("s", tag.Unit);
        Assert.Equal(2, tag.InitialValue!.GetValue<double>());
    }

    [Fact]
    public void OnlyPlcReactiveAlarmsGetAlarmTags()
    {
        var (project, pms) = NewProject();
        InstanceFactory.Create(project, Library, Fixtures.Light, "L1", pms.Id);
        var registry = new TagRegistry(project);
        Assert.NotNull(registry.FindByPath("PMS.L1.ALM.CurrentWhileOff.active"));
        Assert.NotNull(registry.FindByPath("PMS.L1.ALM.DoesNotSwitchOn.raise_count"));
        Assert.Null(registry.FindByPath("PMS.L1.ALM.LampFailure.active"));
        Assert.Null(registry.FindByPath("PMS.L1.ALM.Overcurrent.active"));
    }

    [Fact]
    public void UnknownBlueprintIsRejected()
    {
        var (project, pms) = NewProject();
        var ex = Assert.Throws<ProjectException>(() => InstanceFactory.Create(project, Library, Guid.NewGuid(), "P1", pms.Id));
        Assert.Equal(ProjectErrors.NotFound, ex.Code);
    }

    [Fact]
    public void UnitBlueprintsAreAddedAsUnits()
    {
        var (project, pms) = NewProject();
        Assert.Equal(ProjectErrors.InvalidUnit, Assert.Throws<ProjectException>(() => InstanceFactory.Create(project, Library, Fixtures.Plant, "P1", pms.Id)).Code);
        Assert.Equal(ProjectErrors.InvalidUnit, Assert.Throws<ProjectException>(() => InstanceFactory.CreateUnit(project, Library, Fixtures.Light, "P1", pms.Id)).Code);
        var unit = InstanceFactory.CreateUnit(project, Library, Fixtures.Plant, "P1", pms.Id);
        Assert.Equal(Fixtures.Plant, unit.BlueprintId);
        Assert.NotNull(new TagRegistry(project).FindByPath("PMS.P1.ALM.ManualByOverride.active"));
    }

    [Fact]
    public void DuplicateNameIsRejectedBeforeAnythingIsCreated()
    {
        var (project, pms) = NewProject();
        InstanceFactory.Create(project, Library, Fixtures.Light, "GEN1", pms.Id);
        var before = project.Objects.Count();
        Assert.Throws<ProjectException>(() => InstanceFactory.Create(project, Library, Fixtures.CircuitBreaker, "gen1", pms.Id));
        Assert.Equal(before, project.Objects.Count());
    }

    [Fact]
    public void EveryTagGetsANewId()
    {
        var (project, pms) = NewProject();
        var a = InstanceFactory.Create(project, Library, Fixtures.CircuitBreaker, "GEN1_CB", pms.Id);
        var b = InstanceFactory.Create(project, Library, Fixtures.CircuitBreaker, "GEN2_CB", pms.Id);
        var registry = new TagRegistry(project);
        var idsA = registry.ForControlModule(a.Id).Select(t => t.Id).ToHashSet();
        Assert.DoesNotContain(registry.ForControlModule(b.Id), t => idsA.Contains(t.Id));
    }

    [Fact]
    public void RenameAndMoveKeepAllTagIds()
    {
        var (project, pms) = NewProject();
        var aux = project.AddFolder("AUX");
        var gen1 = InstanceFactory.Create(project, Library, Fixtures.Light, "GEN1", pms.Id);
        var registry = new TagRegistry(project);
        var before = registry.ForControlModule(gen1.Id).ToDictionary(t => t.Name + t.Group, t => (t.Id, t.SymbolKey));

        project.Rename(gen1.Id, "GEN_PORT");
        project.Move(gen1.Id, aux.Id);

        var after = registry.ForControlModule(gen1.Id).ToDictionary(t => t.Name + t.Group, t => (t.Id, t.SymbolKey));
        Assert.Equal(before, after);
        Assert.Equal(before[$"state{TagGroup.Sts}"].Id, registry.FindByPath("AUX.GEN_PORT.STS.state")!.Id);
    }

    [Fact]
    public void DeletionSummaryCountsWhatWillBeRemoved()
    {
        var (project, pms) = NewProject();
        InstanceFactory.Create(project, Library, Fixtures.Light, "GEN1", pms.Id);
        InstanceFactory.Create(project, Library, Fixtures.CircuitBreaker, "GEN1_CB", pms.Id);

        var summary = InstanceFactory.Summarize(project, pms.Id);
        Assert.Equal(new DeletionSummary(1, 2, TagCount(Fixtures.Light) + TagCount(Fixtures.CircuitBreaker)), summary);

        project.Delete(pms.Id);
        Assert.Empty(project.Objects);
    }
}
