using System.Text.Json.Nodes;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Persistence;
using Xunit;

namespace Builder.Tests.Persistence;

public sealed class ProjectStoreTests : IDisposable
{
    private static readonly CmLibrary Library = CmLibrary.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "cm-types"));
    private static readonly Guid ProjectId = Guid.Parse("f0000000-0000-0000-0000-000000000001");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"builder-store-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Dir(string name) => Path.Combine(_root, name);

    private static (Project project, ControlModule gen1) Sample()
    {
        var project = new Project(new ProjectSettings { MaxNameLength = 24 });
        var pms = project.AddFolder("PMS");
        project.AddFolder("Switchboard", pms.Id);
        var gen1 = InstanceFactory.Create(project, Library, "GenSet", "GEN1", pms.Id, ["FIN.exhaust_temp"]);
        InstanceFactory.Create(project, Library, "CircuitBreaker", "GEN1_CB", pms.Id);
        return (project, gen1);
    }

    private static Dictionary<string, string> Snapshot(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(directory, f), File.ReadAllText);

    [Fact]
    public void SaveAndLoadGiveAnIdenticalProject()
    {
        var (project, _) = Sample();
        ProjectStore.Save(Dir("a"), ProjectId, "Demo", project);

        var loaded = ProjectStore.Load(Dir("a"));
        ProjectStore.Save(Dir("b"), loaded.Id, loaded.Name, loaded.Project);

        Assert.Equal(Snapshot(Dir("a")), Snapshot(Dir("b")));
        Assert.Equal(ProjectId, loaded.Id);
        Assert.Equal("Demo", loaded.Name);
        Assert.Equal(24, loaded.Project.Settings.MaxNameLength);
    }

    [Fact]
    public void LoadedTagsKeepIdsSymbolKeysAndDetails()
    {
        var (project, gen1) = Sample();
        var original = new TagRegistry(project).FindByPath("PMS.GEN1.PAR.cooldown_time")!;
        ProjectStore.Save(Dir("a"), ProjectId, "Demo", project);

        var loaded = ProjectStore.Load(Dir("a")).Project;
        var tag = new TagRegistry(loaded).FindByPath("PMS.GEN1.PAR.cooldown_time")!;
        Assert.Equal(original.Id, tag.Id);
        Assert.Equal(original.SymbolKey, tag.SymbolKey);
        Assert.Equal(TagDataType.Real, tag.DataType);
        Assert.Equal("s", tag.Unit);
        Assert.Equal(180, tag.InitialValue!.GetValue<int>());
        var cm = loaded.Get<ControlModule>(gen1.Id);
        Assert.Equal(["FIN.exhaust_temp"], cm.OptionalTags);
        Assert.Equal("1.3.0", cm.TypeVersion);
    }

    [Fact]
    public void OneFilePerFolderAndControlModule()
    {
        var (project, gen1) = Sample();
        ProjectStore.Save(Dir("a"), ProjectId, "Demo", project);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Dir("a"), ProjectStore.FoldersDirectory)).Length);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Dir("a"), ProjectStore.ControlModulesDirectory)).Length);
        Assert.True(File.Exists(Path.Combine(Dir("a"), ProjectStore.ControlModulesDirectory, $"{gen1.Id}.json")));
    }

    [Fact]
    public void RenameChangesExactlyOneFile()
    {
        var (project, gen1) = Sample();
        ProjectStore.Save(Dir("a"), ProjectId, "Demo", project);
        var before = Snapshot(Dir("a"));

        project.Rename(gen1.Id, "GEN_PORT");
        ProjectStore.Save(Dir("a"), ProjectId, "Demo", project);
        var after = Snapshot(Dir("a"));

        var changed = before.Keys.Where(k => before[k] != after[k]).ToList();
        Assert.Equal([Path.Combine(ProjectStore.ControlModulesDirectory, $"{gen1.Id}.json")], changed);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
    }

    [Fact]
    public void UnchangedFilesAreNotRewritten()
    {
        var (project, _) = Sample();
        ProjectStore.Save(Dir("a"), ProjectId, "Demo", project);
        var file = Directory.GetFiles(Path.Combine(Dir("a"), ProjectStore.ControlModulesDirectory))[0];
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, stamp);

        ProjectStore.Save(Dir("a"), ProjectId, "Demo", project);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void DeletedObjectsLoseTheirFile()
    {
        var (project, gen1) = Sample();
        ProjectStore.Save(Dir("a"), ProjectId, "Demo", project);
        project.Delete(gen1.Id);
        ProjectStore.Save(Dir("a"), ProjectId, "Demo", project);
        Assert.False(File.Exists(Path.Combine(Dir("a"), ProjectStore.ControlModulesDirectory, $"{gen1.Id}.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(Dir("a"), ProjectStore.ControlModulesDirectory)));
    }

    [Fact]
    public void FilesAreReadableJson()
    {
        var (project, gen1) = Sample();
        ProjectStore.Save(Dir("a"), ProjectId, "Demo", project);
        var text = File.ReadAllText(Path.Combine(Dir("a"), ProjectStore.ControlModulesDirectory, $"{gen1.Id}.json"));
        var json = JsonNode.Parse(text)!;
        Assert.Equal(ProjectStore.ControlModuleSchema, json["schema"]!.GetValue<string>());
        Assert.Equal("GenSet", json["type"]!.GetValue<string>());
        Assert.Equal("FIN", json["tags"]![0]!["group"]!.GetValue<string>());
        Assert.Contains("\"unit\": \"°C\"", text);
        Assert.DoesNotContain("\r", text);
        Assert.EndsWith("}\n", text);
    }

    [Fact]
    public void BrokenFileIsReportedWithItsName()
    {
        var (project, gen1) = Sample();
        ProjectStore.Save(Dir("a"), ProjectId, "Demo", project);
        var path = Path.Combine(Dir("a"), ProjectStore.ControlModulesDirectory, $"{gen1.Id}.json");
        File.WriteAllText(path, "{\n  \"schema\": \"apolloiq.cm/1\",\n  \"name\": \n}");
        var ex = Assert.Throws<ProjectLoadException>(() => ProjectStore.Load(Dir("a")));
        Assert.Contains(ex.Errors, e => e.StartsWith($"{gen1.Id}.json(4)"));
    }

    [Fact]
    public void MissingParentIsReported()
    {
        var project = new Project();
        var pms = project.AddFolder("PMS");
        project.AddFolder("Child", pms.Id);
        ProjectStore.Save(Dir("a"), ProjectId, "Demo", project);
        File.Delete(Path.Combine(Dir("a"), ProjectStore.FoldersDirectory, $"{pms.Id}.json"));
        var ex = Assert.Throws<ProjectLoadException>(() => ProjectStore.Load(Dir("a")));
        Assert.Contains(ex.Errors, e => e.Contains("does not exist"));
    }

    [Fact]
    public void MissingProjectFileIsReported()
    {
        Directory.CreateDirectory(Dir("empty"));
        Assert.Throws<ProjectLoadException>(() => ProjectStore.Load(Dir("empty")));
    }

    [Fact]
    public void ProjectInterlocksRoundTrip()
    {
        var (project, _) = Sample();
        var breaker = project.Objects.OfType<ControlModule>().Single(c => c.Name == "GEN1_CB");
        project.SetInterlocks(breaker.Id, [
            new InterlockRule { Kind = InterlockKind.SwitchOn, Condition = ExpressionReferences.ToStored(project, "[PMS.GEN1.STS.state] = ReadyToConnect"), Text = "GEN1 ready to connect" },
            new InterlockRule { Kind = InterlockKind.Trip, Condition = ExpressionReferences.ToStored(project, "NOT [PMS.GEN1.is_running]"), Alarm = "GenStopped", Escalate = TripEscalation.Unit }]);
        ProjectStore.Save(Dir("a"), ProjectId, "Demo", project);

        var loaded = ProjectStore.Load(Dir("a")).Project;
        var rules = loaded.Get<ControlModule>(breaker.Id).Interlocks;
        Assert.Equal("GEN1 ready to connect", rules[0].Text);
        Assert.Equal("[PMS.GEN1.STS.state] = ReadyToConnect", ExpressionReferences.ToDisplay(loaded, rules[0].Condition));
        Assert.Equal(("GenStopped", TripEscalation.Unit), (rules[1].Alarm, rules[1].Escalate));
        Assert.NotNull(new TagRegistry(loaded).FindByPath("PMS.GEN1_CB.ALM.GenStopped.active"));

        ProjectStore.Save(Dir("b"), ProjectId, "Demo", loaded);
        Assert.Equal(Snapshot(Dir("a")), Snapshot(Dir("b")));
    }
}
