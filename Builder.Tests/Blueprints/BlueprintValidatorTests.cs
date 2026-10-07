using Builder.Core.Model;
using System.Text.Json;
using Builder.Logic.Blueprints;
using Xunit;

namespace Builder.Tests.Blueprints;

public class BlueprintValidatorTests
{
    private static Blueprint Light() => JsonSerializer.Deserialize<Blueprint>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "blueprints", "Light.blueprint.json")), Blueprint.Json)!;

    [Fact]
    public void TheLightExampleIsValid()
    {
        var light = Light();
        Assert.Empty(BlueprintValidator.Validate(light));
        Assert.Equal([200, 300, 400, 100, 500], light.States.Select(s => s.Code));
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("CircuitBreaker")]
    [InlineData("GenSet")]
    [InlineData("PushButton")]
    [InlineData("PowerManagement")]
    [InlineData("PowerMeter")]
    [InlineData("GenSetPowerSource")]
    [InlineData("PowerManagementEM")]
    public void TheExampleBlueprintsHaveNoProblems(string name)
    {
        var blueprint = JsonSerializer.Deserialize<Blueprint>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "blueprints", $"{name}.blueprint.json")), Blueprint.Json)!;
        Assert.Equal(name, blueprint.Name);
        Blueprint? Lookup(string n) => File.Exists(Path.Combine(AppContext.BaseDirectory, "blueprints", $"{n}.blueprint.json"))
            ? JsonSerializer.Deserialize<Blueprint>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "blueprints", $"{n}.blueprint.json")), Blueprint.Json)
            : null;
        var issues = BlueprintValidator.Validate(blueprint, Lookup);
        Assert.True(issues.Count == 0, string.Join(Environment.NewLine, issues));
    }

    [Fact]
    public void InputsWithoutASourceAreErrors()
    {
        var light = Light();
        light.Tags[0].Source = null;
        Assert.Contains(BlueprintValidator.Validate(light), i => i.Severity == "Error" && i.Message.Contains("source"));
    }

    [Fact]
    public void BadGuardsAndUnknownStatesAreReported()
    {
        var light = Light();
        light.Transitions[0].Guard = "CMD.set_on AND LOK.nothing";
        light.Transitions[1].To = "Nowhere";
        light.States[1].Timeout!.GoTo = "Elsewhere";
        var issues = BlueprintValidator.Validate(light);
        Assert.Contains(issues, i => i.Where == "Transitions › switch_on");
        Assert.Contains(issues, i => i.Message.Contains("Nowhere"));
        Assert.Contains(issues, i => i.Message.Contains("Elsewhere"));
    }

    [Fact]
    public void InterlocksAreValidated()
    {
        var light = Light();
        light.Interlocks.Add(new InterlockRule { Kind = InterlockKind.SwitchOn, Condition = "STS.remote_ok" });
        light.Interlocks.Add(new InterlockRule { Kind = InterlockKind.Trip, Condition = "INT.feedback AND STS.state = Off", Alarm = "LampStuck" });
        Assert.Empty(BlueprintValidator.Validate(light));
        var type = BlueprintTypes.ToCmType(light);
        Assert.Contains(type.Alarms, a => a is { Name: "LampStuck", Trip: true });
        Assert.Equal(2, type.Interlocks.Count);
        light.Interlocks.Add(new InterlockRule { Target = "LAMP", Condition = "TRUE" });
        light.Interlocks.Add(new InterlockRule { Kind = InterlockKind.SwitchOff, Condition = "time(INT.feedback) > 2" });
        light.Interlocks.Add(new InterlockRule { Kind = InterlockKind.Trip, Condition = "TRUE", Alarm = "LampFailure" });
        var issues = BlueprintValidator.Validate(light);
        Assert.Contains(issues, i => i.Where == "Interlocks › line 3" && i.Message.Contains("only put interlocks on itself"));
        Assert.Contains(issues, i => i.Where == "Interlocks › line 4" && i.Message.Contains("not allowed"));
        Assert.Contains(issues, i => i.Where == "Interlocks › line 5" && i.Message.Contains("already exists"));
        light.Interfaces.Remove("Interlocks");
        Assert.Contains(BlueprintValidator.Validate(light), i => i.Message.Contains("no Interlocks interface"));
    }

    [Fact]
    public void TransitionAlarmsMustNameATransition()
    {
        var light = Light();
        light.Alarms.Add(new BlueprintAlarm { Name = "Reset", OnTransition = "nope", Latched = true });
        Assert.Contains(BlueprintValidator.Validate(light), i => i.Message.Contains("'nope'"));
        light.Alarms[^1].OnTransition = "reset";
        Assert.Empty(BlueprintValidator.Validate(light));
        light.Interfaces.Remove("Resettable");
        Assert.Contains(BlueprintValidator.Validate(light), i => i.Message.Contains("Resettable"));
    }

    [Fact]
    public void NonReactiveAlarmsCannotChangeTheOwnState()
    {
        var light = Light();
        light.Transitions[2].Guard = "ALM.LampFailure.active";
        Assert.Contains(BlueprintValidator.Validate(light), i => i.Message.Contains("non-reactive"));
    }

    [Fact]
    public void UnreachableStatesAndStaleOutputsAreWarnings()
    {
        var light = Light();
        light.Transitions.RemoveAt(4);
        light.States.Add(new BlueprintState { Name = "Dimmed", Category = 400, Entry = [new BlueprintAction { Tag = "INT.feedback", Value = "TRUE" }] });
        var issues = BlueprintValidator.Validate(light);
        Assert.Equal(401, light.States[^1].Code);
        Assert.Contains(issues, i => i.Where == "States › Dimmed" && i.Message.Contains("never be reached"));
        Assert.Contains(issues, i => i.Where == "States › Broken" && i.Message.Contains("no way out"));
        Assert.Contains(issues, i => i.Message.Contains("stale"));
    }

    [Fact]
    public void UnitsReadMembersAndWriteOnlyTheirCommands()
    {
        var light = Light();
        var unit = new Blueprint
        {
            Kind = BlueprintKind.Unit,
            Name = "LightPair",
            Roles = [new BlueprintRole { Name = "MAIN", Blueprint = "Light" }, new BlueprintRole { Name = "BACKUP", Blueprint = "Light" }],
            States =
            [
                new BlueprintState { Name = "Watching", Category = 400, Initial = true },
                new BlueprintState { Name = "Backup", Category = 400, Entry = [new BlueprintAction { Tag = "BACKUP.CMD.set_on", Value = "TRUE" }], Exit = [new BlueprintAction { Tag = "BACKUP.CMD.set_on", Value = "FALSE" }] }
            ],
            Transitions =
            [
                new BlueprintTransition { Name = "fail", From = ["Watching"], To = "Backup", Guard = "MAIN.ALM.LampFailure.active OR MAIN.STS.state = Broken" },
                new BlueprintTransition { Name = "back", From = ["Backup"], To = "Watching", Guard = "NOT MAIN.ALM.LampFailure.active AND STS.auto" }
            ]
        };
        Assert.Empty(BlueprintValidator.Validate(unit, name => name == "Light" ? light : null));
        unit.States[1].Run = [new BlueprintAction { Tag = "BACKUP.OUT.lamp", Value = "TRUE" }];
        Assert.Contains(BlueprintValidator.Validate(unit, name => name == "Light" ? light : null), i => i.Message.Contains("only write"));
    }
}
