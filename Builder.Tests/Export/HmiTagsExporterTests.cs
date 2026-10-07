using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Core.Types;
using Builder.Persistence;
using Builder.Persistence.Export;
using Xunit;

namespace Builder.Tests.Export;

public sealed class HmiTagsExporterTests : IDisposable
{
    private static readonly CmLibrary Library = CmLibrary.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "cm-types"));
    private static readonly Guid SimulationConnection = Guid.Parse("c1d2e3f4-a5b6-7890-cdef-111122223333");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"builder-export-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Project FixedProject()
    {
        var counter = 0;
        var project = new Project(newId: () => new Guid($"00000000-0000-0000-0000-{++counter:D12}"));
        var pms = project.AddFolder("PMS");
        InstanceFactory.Create(project, Library, "GenSet", "GEN1", pms.Id);
        InstanceFactory.Create(project, Library, "CircuitBreaker", "GEN1_CB", pms.Id);
        return project;
    }

    private static string GoldenPath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "..", "Golden", "hmi-tags.golden.json");

    [Fact]
    public void ExportMatchesTheGoldenFile()
    {
        var actual = HmiTagsExporter.Serialize(FixedProject(), new HmiExportProfile(SimulationConnection));
        var golden = GoldenPath();
        if (Environment.GetEnvironmentVariable("APOLLOIQ_UPDATE_GOLDEN") == "1")
            File.WriteAllText(golden, actual);
        Assert.Equal(File.ReadAllText(golden), actual);
    }

    [Fact]
    public void ExportContainsEveryTagOnce()
    {
        var project = FixedProject();
        var records = HmiTagsExporter.Build(project, HmiExportProfile.Default);
        Assert.Equal(project.Tags.Count(), records.Count);
        Assert.Equal(records.Count, records.Select(r => r.Uid).Distinct().Count());
        Assert.Equal(records.Select(r => r.Name).Order(StringComparer.Ordinal), records.Select(r => r.Name));
    }

    [Fact]
    public void UidIsTheTagIdAndNameIsTheFullPath()
    {
        var project = FixedProject();
        var tag = new TagRegistry(project).FindByPath("PMS.GEN1.STS.state")!;
        var record = HmiTagsExporter.Build(project, new HmiExportProfile(SimulationConnection)).Single(r => r.Uid == tag.Id);
        Assert.Equal("PMS.GEN1.STS.state", record.Name);
        Assert.Equal("PMS.GEN1.STS.state", record.Address);
        Assert.Equal("integer", record.DataType);
        Assert.Equal(SimulationConnection, record.ConnectionId);
        Assert.Equal(1000, record.ScanRateMs);
    }

    [Fact]
    public void SymbolKeyAddressModeUsesTheSymbolKey()
    {
        var project = FixedProject();
        var tag = new TagRegistry(project).FindByPath("PMS.GEN1.CMD.set_on")!;
        var record = HmiTagsExporter.Build(project, new HmiExportProfile(null, 500, HmiAddressMode.SymbolKey)).Single(r => r.Uid == tag.Id);
        Assert.Equal(tag.SymbolKey, record.Address);
        Assert.Equal(500, record.ScanRateMs);
        Assert.Null(record.ConnectionId);
    }

    [Theory]
    [InlineData(TagDataType.Bool, "boolean")]
    [InlineData(TagDataType.Int16, "integer")]
    [InlineData(TagDataType.Int32, "integer")]
    [InlineData(TagDataType.Enum, "integer")]
    [InlineData(TagDataType.Real, "float")]
    [InlineData(TagDataType.LReal, "float")]
    [InlineData(TagDataType.String, "string")]
    [InlineData(TagDataType.DateTime, "string")]
    public void DataTypesMapToTheHmiTypes(TagDataType type, string expected) =>
        Assert.Equal(expected, HmiTagsExporter.HmiDataType(type));

    [Fact]
    public void RenameKeepsUidsAndChangesNames()
    {
        var project = FixedProject();
        var before = HmiTagsExporter.Build(project, HmiExportProfile.Default).ToDictionary(r => r.Uid, r => r.Name);
        var gen1 = project.Objects.OfType<ControlModule>().Single(c => c.Name == "GEN1");
        project.Rename(gen1.Id, "GEN_PORT");
        var after = HmiTagsExporter.Build(project, HmiExportProfile.Default).ToDictionary(r => r.Uid, r => r.Name);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.Equal("PMS.GEN_PORT.STS.state", after[before.Single(b => b.Value == "PMS.GEN1.STS.state").Key]);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(4_000_000)]
    public void ScanRateOutOfRangeIsRejected(int scanRate)
    {
        var ex = Assert.Throws<ProjectException>(() => HmiTagsExporter.Build(FixedProject(), new HmiExportProfile(null, scanRate)));
        Assert.Equal("scanRateMs", ex.Field);
    }

    [Fact]
    public void ExportReadsBackWithTheScadaFormat()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var json = HmiTagsExporter.Serialize(FixedProject(), new HmiExportProfile(SimulationConnection));

        var file = JsonSerializer.Deserialize<ScadaTagsFile>(json, options)!;
        Assert.Equal(FixedProject().Tags.Count(), file.Tags.Count);
        Assert.Contains(file.Tags, t => t.Name == "PMS.GEN1_CB.FIN.feedback" && t.DataType == ScadaTagType.Boolean);
        Assert.Contains(file.Tags, t => t.Name == "PMS.GEN1.FIN.coolant_temp" && t.DataType == ScadaTagType.Float);
        Assert.All(file.Tags, t => Assert.Equal(SimulationConnection, t.ConnectionId));
    }

    [Fact]
    public void ProfileIsSavedWithTheProject()
    {
        var profile = new HmiExportProfile(SimulationConnection, 250, HmiAddressMode.SymbolKey);
        ProjectStore.Save(_root, Guid.NewGuid(), "Demo", FixedProject(), profile);
        Assert.Equal(profile, ProjectStore.Load(_root).HmiExport);
    }

    [Fact]
    public void ProjectWithoutProfileLoadsWithoutOne()
    {
        ProjectStore.Save(_root, Guid.NewGuid(), "Demo", FixedProject());
        Assert.Null(ProjectStore.Load(_root).HmiExport);
    }

    private enum ScadaTagType
    {
        String,
        Boolean,
        Integer,
        Float
    }

    private sealed class ScadaTagsFile
    {
        public List<ScadaTagRecord> Tags { get; set; } = [];
    }

    private sealed class ScadaTagRecord
    {
        public Guid Uid { get; set; }
        public string Name { get; set; } = "";
        public ScadaTagType DataType { get; set; }
        public int ScanRateMs { get; set; }
        public Guid? ConnectionId { get; set; }
        public string Address { get; set; } = "";
    }
}
