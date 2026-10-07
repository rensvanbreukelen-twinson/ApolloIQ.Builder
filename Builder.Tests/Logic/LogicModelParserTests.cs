using System.Text.Json.Nodes;
using Builder.Logic.Model;
using Xunit;

namespace Builder.Tests.Logic;

public class LogicModelParserTests
{
    private static LogicModel Parse(string json) => LogicModelParser.Parse(JsonNode.Parse(json));

    private static IReadOnlyList<LogicError> Fails(string json) =>
        Assert.Throws<LogicException>(() => Parse(json)).Errors;

    [Fact]
    public void MissingLogicIsAnEmptyModel()
    {
        var model = LogicModelParser.Parse(null);
        Assert.False(model.HasStateMachine);
        Assert.Empty(model.Before);
    }

    [Fact]
    public void ParsesAllSections()
    {
        var model = Parse("""
            {
              "plant": [ { "set": "FIN.running", "expr": "OUT.run" } ],
              "before": [ { "block": "TON", "id": "start_timer", "in": { "IN": "is_starting", "PT": 10 }, "out": { "Q": "INT.start_timeout" } } ],
              "stateMachine": {
                "transitions": [
                  { "from": "Stopped", "to": "Starting", "guard": "CMD.set_on" },
                  { "from": ["Starting", "Running"], "to": "Stopped", "guard": "CMD.set_off", "priority": 1, "name": "stop" },
                  { "from": "*", "to": "Unavailable", "guard": "NOT STS.enabled", "priority": 0 }
                ],
                "outputs": {
                  "OUT.run": [ "Starting", "Running" ],
                  "OUT.horn": "INT.start_timeout",
                  "OUT.lamp": { "states": [ "Running" ], "when": "NOT INT.start_timeout" }
                }
              },
              "after": [ { "set": "PMT.starts", "expr": "PMT.starts + 1" } ]
            }
            """);

        Assert.Equal("FIN.running", Assert.IsType<AssignStep>(Assert.Single(model.Plant)).Target);
        var block = Assert.IsType<BlockStep>(Assert.Single(model.Before));
        Assert.Equal("start_timer", block.Id);
        Assert.Equal("10", block.Inputs["PT"]);
        Assert.Equal("INT.start_timeout", block.Outputs["Q"]);

        Assert.Equal(3, model.Transitions.Count);
        Assert.Equal(10, model.Transitions[0].Priority);
        Assert.Equal("Stopped → Starting", model.Transitions[0].Name);
        Assert.Equal(["Starting", "Running"], model.Transitions[1].From);
        Assert.Equal("stop", model.Transitions[1].Name);
        Assert.Equal(["*"], model.Transitions[2].From);

        Assert.Equal(["Starting", "Running"], model.Outputs[0].States);
        Assert.Null(model.Outputs[0].When);
        Assert.Null(model.Outputs[1].States);
        Assert.Equal("INT.start_timeout", model.Outputs[1].When);
        Assert.Equal(["Running"], model.Outputs[2].States);
        Assert.NotNull(model.Outputs[2].When);
        Assert.Single(model.After);
    }

    [Fact]
    public void UnknownPropertiesAreRejectedWithTheirLocation()
    {
        var errors = Fails("""{ "befor": [], "stateMachine": { "transitions": [ { "from": "Stopped", "to": "Running", "guard": "TRUE", "prio": 1 } ] } }""");
        Assert.Contains(errors, e => e.Location == "logic.befor");
        Assert.Contains(errors, e => e.Location == "logic.stateMachine.transitions[0].prio");
    }

    [Fact]
    public void MissingRequiredPartsAreReported()
    {
        var errors = Fails("""{ "before": [ { "set": "INT.x" }, { "expr": "TRUE" } ], "stateMachine": { "transitions": [ { "to": "Running" } ] } }""");
        Assert.Contains(errors, e => e.Location == "logic.before[0].expr");
        Assert.Contains(errors, e => e.Location == "logic.before[1]");
        Assert.Contains(errors, e => e.Location == "logic.stateMachine.transitions[0].guard");
        Assert.Contains(errors, e => e.Location == "logic.stateMachine.transitions[0].from");
    }

    [Fact]
    public void UnknownBlockListsTheLibrary()
    {
        var error = Assert.Single(Fails("""{ "before": [ { "block": "PID", "in": {}, "out": {} } ] }"""));
        Assert.Equal("logic.before[0].block", error.Location);
        Assert.Contains("TON", error.Message);
    }

    [Fact]
    public void OutputNeedsStatesOrCondition()
    {
        var error = Assert.Single(Fails("""{ "stateMachine": { "outputs": { "OUT.run": { } } } }"""));
        Assert.Equal("logic.stateMachine.outputs.OUT.run", error.Location);
    }
}
