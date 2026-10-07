using Builder.Simulator.Scenarios;
using Xunit;

namespace Builder.Tests.Blueprints;

/// <summary>The fixture scenarios (Fixtures/scenarios) run on the fixture blueprints and cover every transition.</summary>
public class BlueprintScenarioTests
{
    private static ScenarioReport Run()
    {
        var (files, errors) = ScenarioLoader.LoadDirectory(Fixtures.ScenarioDirectory);
        Assert.Empty(errors);
        return new ScenarioRunner(Fixtures.Library()).RunAll(files, errors);
    }

    [Fact]
    public void FixtureScenariosPassOnTheBlueprints()
    {
        var report = Run();
        var failures = report.Results.Where(r => !r.Passed)
            .Select(r => $"{r.Type} '{r.Name}': {string.Join("; ", r.Failures.Select(f => $"{f.Location} (cycle {f.Cycle}) {f.Message}"))}" +
                         $" {string.Join("; ", r.LogicErrors)}");
        Assert.True(!failures.Any(), string.Join(Environment.NewLine, failures));
        Assert.True(report.Passed >= 20);
    }

    [Theory]
    [InlineData("CircuitBreaker")]
    [InlineData("PushButton")]
    [InlineData("Light")]
    [InlineData("LightingGroup")]
    public void FixtureScenariosCoverEveryTransition(string type)
    {
        var coverage = Run().Coverage.Single(c => c.Type == type);
        Assert.Empty(coverage.Transitions.Where(t => t.Hits == 0).Select(t => $"[{t.Index}] {t.Name}: {t.From} → {t.To}"));
    }
}
