using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Xunit;

namespace Builder.Tests.Types;

public class InstanceFactoryTests
{
    private static readonly CmLibrary Library = CmLibrary.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "cm-types"));

    private static (Project project, Folder pms) NewProject()
    {
        var project = new Project();
        return (project, project.AddFolder("PMS"));
    }

    [Fact]
    public void CreatesAControlModuleWithAllTagsOfTheType()
    {
        var (project, pms) = NewProject();
        var gen1 = InstanceFactory.Create(project, Library, "GenSet", "GEN1", pms.Id);

        var expected = Library.Find("GenSet")!.ExpandTags().Count;
        var tags = new TagRegistry(project).ForControlModule(gen1.Id).ToList();
        Assert.Equal(expected, tags.Count);
        Assert.Equal("GenSet", gen1.TypeName);
        Assert.Equal("1.3.0", gen1.TypeVersion);
        Assert.NotNull(new TagRegistry(project).FindByPath("PMS.GEN1.STS.state"));
        Assert.NotNull(new TagRegistry(project).FindByPath("PMS.GEN1.PAR.cooldown_time"));
    }

    [Fact]
    public void TagsCarryTheirTemplateDetails()
    {
        var (project, pms) = NewProject();
        InstanceFactory.Create(project, Library, "GenSet", "GEN1", pms.Id);
        var tag = new TagRegistry(project).FindByPath("PMS.GEN1.PAR.cooldown_time")!;
        Assert.Equal(TagDataType.Real, tag.DataType);
        Assert.Equal(TagDirection.InOut, tag.Direction);
        Assert.Equal("s", tag.Unit);
        Assert.Equal(180, tag.InitialValue!.GetValue<int>());
    }

    [Fact]
    public void OptionalTagsAreOnlyCreatedWhenSelected()
    {
        var (project, pms) = NewProject();
        var gen1 = InstanceFactory.Create(project, Library, "GenSet", "GEN1", pms.Id, ["fin.exhaust_temp"]);
        var registry = new TagRegistry(project);
        Assert.NotNull(registry.FindByPath("PMS.GEN1.FIN.exhaust_temp"));
        Assert.Null(registry.FindByPath("PMS.GEN1.CMD.set_auto"));
        Assert.Equal(["FIN.exhaust_temp"], gen1.OptionalTags);
    }

    [Fact]
    public void UnknownOptionalTagIsRejected()
    {
        var (project, pms) = NewProject();
        Assert.Throws<ProjectException>(() => InstanceFactory.Create(project, Library, "GenSet", "GEN1", pms.Id, ["FIN.running"]));
        Assert.Empty(project.GetChildren(pms.Id));
    }

    [Fact]
    public void UnknownTypeIsRejected()
    {
        var (project, pms) = NewProject();
        var ex = Assert.Throws<ProjectException>(() => InstanceFactory.Create(project, Library, "Pump", "P1", pms.Id));
        Assert.Equal(ProjectErrors.NotFound, ex.Code);
    }

    [Fact]
    public void DuplicateNameIsRejectedBeforeAnythingIsCreated()
    {
        var (project, pms) = NewProject();
        InstanceFactory.Create(project, Library, "GenSet", "GEN1", pms.Id);
        var before = project.Objects.Count();
        Assert.Throws<ProjectException>(() => InstanceFactory.Create(project, Library, "CircuitBreaker", "gen1", pms.Id));
        Assert.Equal(before, project.Objects.Count());
    }

    [Fact]
    public void EveryTagGetsANewId()
    {
        var (project, pms) = NewProject();
        var a = InstanceFactory.Create(project, Library, "CircuitBreaker", "GEN1_CB", pms.Id);
        var b = InstanceFactory.Create(project, Library, "CircuitBreaker", "GEN2_CB", pms.Id);
        var registry = new TagRegistry(project);
        var idsA = registry.ForControlModule(a.Id).Select(t => t.Id).ToHashSet();
        Assert.DoesNotContain(registry.ForControlModule(b.Id), t => idsA.Contains(t.Id));
    }

    [Fact]
    public void RenameAndMoveKeepAllTagIds()
    {
        var (project, pms) = NewProject();
        var aux = project.AddFolder("AUX");
        var gen1 = InstanceFactory.Create(project, Library, "GenSet", "GEN1", pms.Id);
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
        InstanceFactory.Create(project, Library, "GenSet", "GEN1", pms.Id);
        InstanceFactory.Create(project, Library, "CircuitBreaker", "GEN1_CB", pms.Id);
        var expectedTags = Library.Find("GenSet")!.ExpandTags().Count + Library.Find("CircuitBreaker")!.ExpandTags().Count;

        var summary = InstanceFactory.Summarize(project, pms.Id);
        Assert.Equal(new DeletionSummary(1, 2, expectedTags), summary);

        project.Delete(pms.Id);
        Assert.Empty(project.Objects);
    }
}
