using System.Text.Json;
using Builder.Core.Types;
using Builder.Logic.Blueprints;
using Builder.Simulator.Scenarios;
using Xunit;

namespace Builder.Tests.Blueprints;

public class BlueprintScenarioTests
{
    private static ScenarioReport Run()
    {
        var library = new CmLibrary();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "blueprints"), "*.blueprint.json"))
        {
            var blueprint = JsonSerializer.Deserialize<Blueprint>(File.ReadAllText(file), Blueprint.Json)!;
            library.Replace(BlueprintTypes.ToCmType(blueprint));
        }
        var (files, errors) = ScenarioLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "blueprint-scenarios"));
        Assert.Empty(errors);
        return new ScenarioRunner(library).RunAll(files, errors);
    }

    [Fact]
    public void LibraryScenariosPassOnTheBlueprints()
    {
        var report = Run();
        var failures = report.Results.Where(r => !r.Passed)
            .Select(r => $"{r.Type} '{r.Name}': {string.Join("; ", r.Failures.Select(f => $"{f.Location} (cycle {f.Cycle}) {f.Message}"))}");
        Assert.True(!failures.Any(), string.Join(Environment.NewLine, failures));
        Assert.True(report.Passed >= 35);
    }

    [Theory]
    [InlineData("CircuitBreaker")]
    [InlineData("GenSet")]
    [InlineData("PushButton")]
    [InlineData("Light")]
    public void LibraryScenariosCoverEveryTransition(string type)
    {
        var coverage = Run().Coverage.Single(c => c.Type == type);
        Assert.Empty(coverage.Transitions.Where(t => t.Hits == 0).Select(t => $"[{t.Index}] {t.Name}: {t.From} → {t.To}"));
    }
}
