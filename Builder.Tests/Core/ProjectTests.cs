using Builder.Core.Model;
using Xunit;

namespace Builder.Tests.Core;

public class ProjectTests
{
    [Fact]
    public void PathFollowsTheParents()
    {
        var project = new Project();
        var pms = project.AddFolder("PMS");
        var gen1 = project.AddControlModule("GEN1", pms.Id, "GenSet", "1.0.0");
        Assert.Equal("PMS.GEN1", project.GetPath(gen1.Id));
    }

    [Fact]
    public void PathChangesAfterRenameAndMove()
    {
        var project = new Project();
        var pms = project.AddFolder("PMS");
        var aux = project.AddFolder("AUX");
        var gen1 = project.AddControlModule("GEN1", pms.Id, "GenSet", "1.0.0");

        project.Rename(gen1.Id, "GEN_PORT");
        Assert.Equal("PMS.GEN_PORT", project.GetPath(gen1.Id));

        project.Move(gen1.Id, aux.Id);
        Assert.Equal("AUX.GEN_PORT", project.GetPath(gen1.Id));
        Assert.Empty(project.GetChildren(pms.Id));
    }

    [Fact]
    public void RenameKeepsTheId()
    {
        var project = new Project();
        var gen1 = project.AddControlModule("GEN1", null, "GenSet", "1.0.0");
        var id = gen1.Id;
        project.Rename(id, "GEN2");
        Assert.Equal(id, project.Get(id).Id);
        Assert.Equal("GEN2", project.Get(id).Name);
    }

    [Fact]
    public void SiblingNamesMustBeUniqueIgnoringCase()
    {
        var project = new Project();
        var pms = project.AddFolder("PMS");
        project.AddControlModule("GEN1", pms.Id, "GenSet", "1.0.0");
        var ex = Assert.Throws<ProjectException>(() => project.AddControlModule("gen1", pms.Id, "GenSet", "1.0.0"));
        Assert.Equal(ProjectErrors.DuplicateName, ex.Code);
    }

    [Fact]
    public void SameNameIsAllowedUnderDifferentParents()
    {
        var project = new Project();
        var a = project.AddFolder("A");
        var b = project.AddFolder("B");
        project.AddControlModule("P1", a.Id, "Pump", "1.0.0");
        project.AddControlModule("P1", b.Id, "Pump", "1.0.0");
        Assert.Equal("B.P1", project.GetPath(project.GetChildren(b.Id)[0].Id));
    }

    [Fact]
    public void RenameToDuplicateIsRejectedAndNameIsKept()
    {
        var project = new Project();
        project.AddFolder("A");
        var b = project.AddFolder("B");
        Assert.Throws<ProjectException>(() => project.Rename(b.Id, "A"));
        Assert.Equal("B", project.Get(b.Id).Name);
    }

    [Fact]
    public void InvalidNameIsRejected()
    {
        var project = new Project();
        var ex = Assert.Throws<ProjectException>(() => project.AddFolder("P M S"));
        Assert.Equal(ProjectErrors.InvalidName, ex.Code);
    }

    [Fact]
    public void MaxNameLengthComesFromSettings()
    {
        var project = new Project(new ProjectSettings { MaxNameLength = 4 });
        project.AddFolder("ABCD");
        Assert.Throws<ProjectException>(() => project.AddFolder("ABCDE"));
    }

    [Fact]
    public void TagNamesAreNotLimitedByTheProjectMaximum()
    {
        var project = new Project(new ProjectSettings { MaxNameLength = 4 });
        var cm = project.AddControlModule("GEN1", null, "GenSet", "1.0.0");
        project.AddTag(cm.Id, new Builder.Core.Tags.TagDefinition("mean_switch_count_to_failure",
            Builder.Core.Tags.TagGroup.Par, Builder.Core.Tags.TagDataType.Int32, Builder.Core.Tags.TagDirection.InOut));
        Assert.Single(project.Tags);
    }

    [Fact]
    public void FolderCannotBeMovedIntoItsOwnChild()
    {
        var project = new Project();
        var a = project.AddFolder("A");
        var b = project.AddFolder("B", a.Id);
        var ex = Assert.Throws<ProjectException>(() => project.Move(a.Id, b.Id));
        Assert.Equal(ProjectErrors.Cycle, ex.Code);
    }

    [Fact]
    public void ControlModuleCannotContainFolders()
    {
        var project = new Project();
        var gen1 = project.AddControlModule("GEN1", null, "GenSet", "1.0.0");
        var ex = Assert.Throws<ProjectException>(() => project.AddFolder("X", gen1.Id));
        Assert.Equal(ProjectErrors.InvalidParent, ex.Code);
    }

    [Fact]
    public void DeleteRemovesTheWholeSubtree()
    {
        var project = new Project();
        var pms = project.AddFolder("PMS");
        project.AddControlModule("GEN1", pms.Id, "GenSet", "1.0.0");
        var removed = project.Delete(pms.Id);
        Assert.Equal(2, removed.Count);
        Assert.Empty(project.Objects);
    }

    [Fact]
    public void UnknownIdThrowsNotFound()
    {
        var project = new Project();
        var ex = Assert.Throws<ProjectException>(() => project.Rename(Guid.NewGuid(), "X"));
        Assert.Equal(ProjectErrors.NotFound, ex.Code);
    }
}
