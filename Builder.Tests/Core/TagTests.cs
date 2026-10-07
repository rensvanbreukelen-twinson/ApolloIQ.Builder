using Builder.Core.Model;
using Builder.Core.Tags;
using Xunit;

namespace Builder.Tests.Core;

public class TagTests
{
    private static (Project project, ControlModule gen1) GenSetProject()
    {
        var project = new Project();
        var pms = project.AddFolder("PMS");
        var gen1 = project.AddControlModule("GEN1", pms.Id, Guid.NewGuid(), ApolloIQ.Core.Versioning.BlueprintVersion.Initial);
        return (project, gen1);
    }

    private static TagDefinition State => new("state", TagGroup.Sts, TagDataType.Int16, TagDirection.Out);

    [Fact]
    public void TagPathIncludesTheGroup()
    {
        var (project, gen1) = GenSetProject();
        var tag = project.AddTag(gen1.Id, State);
        Assert.Equal("PMS.GEN1.STS.state", project.GetPath(tag.Id));
    }

    [Fact]
    public void RenamingTheControlModuleKeepsTagIdAndSymbolKey()
    {
        var (project, gen1) = GenSetProject();
        var tag = project.AddTag(gen1.Id, State);
        var id = tag.Id;
        var key = tag.SymbolKey;

        project.Rename(gen1.Id, "GEN_PORT");

        var registry = new TagRegistry(project);
        var found = registry.FindByPath("PMS.GEN_PORT.STS.state");
        Assert.NotNull(found);
        Assert.Equal(id, found.Id);
        Assert.Equal(key, found.SymbolKey);
        Assert.Null(registry.FindByPath("PMS.GEN1.STS.state"));
    }

    [Fact]
    public void SameTagNameIsAllowedInDifferentGroups()
    {
        var (project, gen1) = GenSetProject();
        project.AddTag(gen1.Id, new TagDefinition("running", TagGroup.Fin, TagDataType.Bool, TagDirection.In));
        project.AddTag(gen1.Id, new TagDefinition("running", TagGroup.Int, TagDataType.Bool, TagDirection.Out));
        Assert.Equal(2, new TagRegistry(project).ForControlModule(gen1.Id).Count());
    }

    [Fact]
    public void SameTagNameInTheSameGroupIsRejected()
    {
        var (project, gen1) = GenSetProject();
        project.AddTag(gen1.Id, State);
        Assert.Throws<ProjectException>(() => project.AddTag(gen1.Id, State));
    }

    [Fact]
    public void TagMustBelongToAControlModule()
    {
        var project = new Project();
        var pms = project.AddFolder("PMS");
        var ex = Assert.Throws<ProjectException>(() => project.AddTag(pms.Id, State));
        Assert.Equal(ProjectErrors.InvalidParent, ex.Code);
    }

    [Fact]
    public void SymbolKeyIsPlcSafeAndUnique()
    {
        var (project, gen1) = GenSetProject();
        var a = project.AddTag(gen1.Id, State);
        var b = project.AddTag(gen1.Id, new TagDefinition("enabled", TagGroup.Sts, TagDataType.Bool, TagDirection.In));
        Assert.NotEqual(a.SymbolKey, b.SymbolKey);
        Assert.Null(NameRules.Check(a.SymbolKey, 32));
        Assert.StartsWith("T_", a.SymbolKey);
    }

    [Fact]
    public void SymbolKeyCollisionUsesALongerKey()
    {
        var ids = new Queue<Guid>([
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-aaaa-000000000001"),
            Guid.Parse("33333333-3333-3333-aaaa-000000000002")]);
        var project = new Project(newId: ids.Dequeue);
        var pms = project.AddFolder("PMS");
        var gen1 = project.AddControlModule("GEN1", pms.Id, Guid.NewGuid(), ApolloIQ.Core.Versioning.BlueprintVersion.Initial);
        var a = project.AddTag(gen1.Id, State);
        var b = project.AddTag(gen1.Id, new TagDefinition("enabled", TagGroup.Sts, TagDataType.Bool, TagDirection.In));
        Assert.Equal("T_3333333333333", b.SymbolKey[..15]);
        Assert.NotEqual(a.SymbolKey, b.SymbolKey);
    }

    [Fact]
    public void RegistryFiltersByGroup()
    {
        var (project, gen1) = GenSetProject();
        project.AddTag(gen1.Id, State);
        project.AddTag(gen1.Id, new TagDefinition("set_on", TagGroup.Cmd, TagDataType.Bool, TagDirection.In));
        var registry = new TagRegistry(project);
        Assert.Equal(["set_on"], registry.ForGroup(gen1.Id, TagGroup.Cmd).Select(t => t.Name));
    }

    [Fact]
    public void DeletingTheControlModuleRemovesItsTags()
    {
        var (project, gen1) = GenSetProject();
        var tag = project.AddTag(gen1.Id, State);
        project.Delete(gen1.Id);
        Assert.Null(new TagRegistry(project).FindById(tag.Id));
    }
}
