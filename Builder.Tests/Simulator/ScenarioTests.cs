using Builder.Core.Types;
using Builder.Simulator.Scenarios;
using Xunit;

namespace Builder.Tests.Simulator;

public class ScenarioTests
{
    private static readonly CmLibrary Library = Fixtures.Library();

    private static ScenarioFile File(string steps, string instances = "") => ScenarioLoader.Parse($$"""
        { "schema": "apolloiq.scenarios/1", "type": "CircuitBreaker",
          "scenarios": [ { "name": "test", {{instances}} "steps": {{steps}} } ] }
        """, "test.scenarios.json");

    [Fact]
    public void FailingExpectationReportsTheStepAndStates()
    {
        var file = File("""[ { "run": 2 }, { "expect": "[STS.state] == Running" } ]""");
        var result = new ScenarioRunner(Library).Run(file, file.Scenarios[0]);
        Assert.False(result.Passed);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("scenarios[0].steps[1]", failure.Location);
        Assert.Contains("CM = 200 Open", failure.Message);
        Assert.Equal(2, failure.Cycle);
    }

    [Fact]
    public void UntilTimesOut()
    {
        var file = File("""[ { "until": "[is_closed]", "within": "0.5 s" } ]""");
        var result = new ScenarioRunner(Library).Run(file, file.Scenarios[0]);
        Assert.False(result.Passed);
        Assert.Contains("within 10 cycles", result.Failures[0].Message);
        Assert.Equal(10, result.Cycles);
    }

    [Fact]
    public void TraceRecordsStatesAndOutputChanges()
    {
        var file = File("""[ { "run": 2 }, { "set": { "CMD.HMI_on": true } }, { "run": 2 } ]""");
        var result = new ScenarioRunner(Library).Run(file, file.Scenarios[0]);
        Assert.True(result.Passed);
        Assert.Equal([1L, 2, 3, 4], result.Trace.Select(t => t.Cycle));
        Assert.Equal(300, result.Trace[2].States["CM"]);
        Assert.Equal(true, result.Trace[2].Changes["CM.OUT.coil_on"]);
        Assert.False(result.Trace[3].Changes.ContainsKey("CM.OUT.coil_on"));
        Assert.Equal(["close", "closed"], result.Transitions.Skip(1).Select(t => t.Name));
    }

    [Fact]
    public void SeveralInstancesAndCrossReferences()
    {
        var file = File("""
            [ { "run": 2 },
              { "set": { "B.CMD.HMI_on": true } },
              { "run": 2 },
              { "expect": ["B: [is_closed]", "[SIM.B.is_closed] && ![is_closed]"] } ]
            """, """ "instances": [ { "name": "A" }, { "name": "B" } ], """);
        var result = new ScenarioRunner(Library).Run(file, file.Scenarios[0]);
        Assert.True(result.Passed, string.Join("; ", result.Failures.Select(f => f.Message)));
    }

    [Theory]
    [InlineData("""{ "schema": "x", "type": "Light", "scenarios": [] }""", "schema")]
    [InlineData("""{ "schema": "apolloiq.scenarios/1", "scenarios": [] }""", "type")]
    [InlineData("""{ "schema": "apolloiq.scenarios/1", "type": "Light", "scenarios": [ { "name": "a", "steps": [ { "wait": 1 } ] } ] }""", "scenarios[0].steps[0].wait")]
    [InlineData("""{ "schema": "apolloiq.scenarios/1", "type": "Light", "scenarios": [ { "name": "a", "steps": [ { "run": "soon" } ] } ] }""", "scenarios[0].steps[0].run")]
    [InlineData("""{ "schema": "apolloiq.scenarios/1", "type": "Light", "scenarios": [ { "name": "a", "steps": [ { "until": "[is_running]" } ] } ] }""", "scenarios[0].steps[0].within")]
    [InlineData("""{ "schema": "apolloiq.scenarios/1", "type": "Light", "scenarios": [ { "name": "a", "steps": [] }, { "name": "a", "steps": [] } ] }""", "scenarios[1].name")]
    public void InvalidFilesAreRejectedWithTheirLocation(string json, string location)
    {
        var ex = Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json, "bad.scenarios.json"));
        Assert.Contains(ex.Errors, e => e.Location == location);
    }

    [Fact]
    public void TheOldConditionSyntaxIsReported()
    {
        var file = File("""[ { "run": 1 }, { "expect": "STS.state = Open" } ]""");
        var result = new ScenarioRunner(Library).Run(file, file.Scenarios[0]);
        Assert.False(result.Passed);
        Assert.Contains("use ==", result.Failures[0].Message);
    }

    [Fact]
    public void DurationsAcceptCyclesAndTime()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""[ 4, "2 s", "500 ms", "10 cycles", "1 min" ]""");
        var cycles = doc.RootElement.EnumerateArray().Select(e => ScenarioLoader.ParseDuration(e).ToCycles(0.05)).ToList();
        Assert.Equal([4, 40, 10, 10, 1200], cycles);
    }
}
