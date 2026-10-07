using ApolloIQ.Core.Alarms;
using ApolloIQ.Core.Blueprints;
using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Logic.Blueprints;
using Xunit;

namespace Builder.Tests.Blueprints;

public class BlueprintValidatorTests
{
    private static Blueprint Light() => Fixtures.Load("Light");

    [Fact]
    public void TheLightFixtureIsValid()
    {
        var light = Light();
        Assert.Empty(BlueprintValidator.Validate(light));
        Assert.Equal([200, 300, 400, 100, 500], light.States.Select(s => s.Code));
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("CircuitBreaker")]
    [InlineData("PushButton")]
    [InlineData("LightingGroup")]
    [InlineData("Plant")]
    public void TheFixturesHaveNoProblems(string name)
    {
        var blueprint = Fixtures.Load(name);
        Assert.Equal(name, blueprint.Name);
        var issues = BlueprintValidator.Validate(blueprint, Fixtures.Lookup());
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
        light.Transitions[0].Guard = "[CMD.set_on] && [LOK.nothing]";
        light.Transitions[1].To = "Nowhere";
        light.States[1].Timeout!.GoTo = "Elsewhere";
        var issues = BlueprintValidator.Validate(light);
        Assert.Contains(issues, i => i.Where == "Transitions › switch_on" && i.Message.Contains("Unknown tag"));
        Assert.Contains(issues, i => i.Message.Contains("Nowhere"));
        Assert.Contains(issues, i => i.Message.Contains("Elsewhere"));
    }

    [Theory]
    [InlineData("CMD.set_on", "brackets")]
    [InlineData("CMD.set_on AND LOK.can_on", "Unexpected")]
    [InlineData("[CMD.set_on] AND [LOK.can_on]", "Unexpected")]
    [InlineData("[STS.state] = On", "use ==")]
    [InlineData("[STS.state] == Flying", "Unknown state")]
    [InlineData("[FIN.current] == On", "state tag")]
    public void GuardsUseTheCoreSyntax(string guard, string message)
    {
        var light = Light();
        light.Transitions[0].Guard = guard;
        Assert.Contains(BlueprintValidator.Validate(light), i => i.Where == "Transitions › switch_on" && i.Message.Contains(message));
    }

    [Theory]
    [InlineData("Ready for connection", "not valid")]
    [InlineData("Running", "state category")]
    [InlineData("is_running", "standard alias")]
    [InlineData("TurningOn", "more than once")]
    public void StateNamesAreIdentifiersThatAreNotCategoriesOrAliases(string name, string message)
    {
        var light = Light();
        light.States[0].Name = name;
        Assert.Contains(BlueprintValidator.Validate(light), i => i.Where == "States" && i.Message.Contains(message));
    }

    [Fact]
    public void StatesHaveADisplayTextAndADescription()
    {
        var light = Light();
        light.States[0].Text = "Lamp off, ready";
        light.States[0].Description = "Switched off";
        Assert.Empty(BlueprintValidator.Validate(light));
        var state = BlueprintTypes.ToCmType(light).ObjectStates[0];
        Assert.Equal(("Off", "Lamp off, ready", "Switched off"), (state.Name, state.DisplayText, state.Description));
    }

    [Fact]
    public void InterlocksAreValidated()
    {
        var light = Light();
        light.Interlocks.Add(new InterlockRule { Kind = InterlockKind.SwitchOn, Condition = "[STS.remote_ok]" });
        light.Interlocks.Add(new InterlockRule { Kind = InterlockKind.Trip, Condition = "[INT.feedback] && [STS.state] == Off", Alarm = "LampStuck" });
        Assert.Empty(BlueprintValidator.Validate(light));
        var type = BlueprintTypes.ToCmType(light);
        Assert.Contains(type.Alarms, a => a is { Name: "LampStuck", Source: AlarmSource.Trip, PlcReactive: true });
        Assert.Equal(2, type.Interlocks.Count);
        light.Interlocks.Add(new InterlockRule { Target = "LAMP", Condition = "TRUE" });
        light.Interlocks.Add(new InterlockRule { Kind = InterlockKind.SwitchOff, Condition = "TIME([INT.feedback]) > 2" });
        light.Interlocks.Add(new InterlockRule { Kind = InterlockKind.Trip, Condition = "TRUE", Alarm = "LampFailure" });
        light.Interlocks.Add(new InterlockRule { Kind = InterlockKind.Trip, Condition = "TRUE", Alarm = "Other", Priority = 31 });
        var issues = BlueprintValidator.Validate(light);
        Assert.Contains(issues, i => i.Where == "Interlocks › line 3" && i.Message.Contains("only put interlocks on itself"));
        Assert.Contains(issues, i => i.Where == "Interlocks › line 4" && i.Message.Contains("not allowed"));
        Assert.Contains(issues, i => i.Where == "Interlocks › line 5" && i.Message.Contains("already exists"));
        Assert.Contains(issues, i => i.Where == "Interlocks › line 6" && i.Message.Contains("Priority must be 0 to 30"));
        light.Interfaces.Remove("Interlocks");
        Assert.Contains(BlueprintValidator.Validate(light), i => i.Message.Contains("no Interlocks interface"));
    }

    private static BlueprintAlarm Alarm(string name, bool plcReactive = true, string condition = "[INT.feedback]") => new()
    {
        Alarm = new AlarmDefinition { Name = name, Message = name, Trigger = AlarmTrigger.State, Condition = condition, PlcReactive = plcReactive }
    };

    [Fact]
    public void AlarmsFollowTheSharedRules()
    {
        var light = Light();
        light.Alarms.Add(new BlueprintAlarm { Alarm = new AlarmDefinition { Name = "bad name", Priority = 40, Trigger = AlarmTrigger.Range } });
        light.Alarms.Add(new BlueprintAlarm { Alarm = new AlarmDefinition { Name = "FromTheByte", Message = "x", Trigger = AlarmTrigger.PlcByte } });
        light.Alarms.Add(Alarm("Wrong", condition: "[FIN.current]"));
        var issues = BlueprintValidator.Validate(light);
        Assert.Contains(issues, i => i.Message.Contains("'bad name' is not valid"));
        Assert.Contains(issues, i => i.Message.Contains("Priority must be 0 to 30"));
        Assert.Contains(issues, i => i.Message.Contains("enter the message"));
        Assert.Contains(issues, i => i.Message.Contains("at least one threshold"));
        Assert.Contains(issues, i => i.Message.Contains("ticking 'PLC reactive'"));
        Assert.Contains(issues, i => i.Where == "Alarms › Wrong" && i.Message.Contains("Bool"));
    }

    [Fact]
    public void TransitionAlarmsMustNameATransitionAndBePlcReactive()
    {
        var light = Light();
        var alarm = Alarm("ResetSeen", condition: "");
        alarm.OnTransition = "nope";
        alarm.Latched = true;
        light.Alarms.Add(alarm);
        Assert.Contains(BlueprintValidator.Validate(light), i => i.Message.Contains("'nope'"));
        alarm.OnTransition = "reset";
        Assert.Empty(BlueprintValidator.Validate(light));
        alarm.Alarm.PlcReactive = false;
        var issues = BlueprintValidator.Validate(light);
        Assert.Contains(issues, i => i.Message.Contains("Only a PLC reactive alarm can be raised on a transition"));
        Assert.Contains(issues, i => i.Message.Contains("Only a PLC reactive alarm latches"));
        alarm.Alarm.PlcReactive = true;
        light.Interfaces.Remove("Resettable");
        Assert.Contains(BlueprintValidator.Validate(light), i => i.Message.Contains("Resettable"));
    }

    [Fact]
    public void ScadaAlarmsCannotChangeTheOwnState()
    {
        var light = Light();
        light.Transitions[2].Guard = "[ALM.LampFailure.active]";
        Assert.Contains(BlueprintValidator.Validate(light), i => i.Message.Contains("not PLC reactive"));
        light.Transitions[2].Guard = "[ALM.CurrentWhileOff.active] || [ALM.DoesNotSwitchOn.active]";
        Assert.Empty(BlueprintValidator.Validate(light));
    }

    [Fact]
    public void ActionTargetsArePlainNames()
    {
        var light = Light();
        light.States[0].Run[0].Tag = "[OUT.lamp]";
        Assert.Contains(BlueprintValidator.Validate(light), i => i.Message.Contains("without brackets"));
    }

    [Fact]
    public void TimeoutsMayBeNumericExpressions()
    {
        var light = Light();
        light.States[1].Timeout!.Time = "[PAR.max_switch_time] * 2";
        Assert.Empty(BlueprintValidator.Validate(light));
        light.States[1].Timeout!.Time = "[OUT.lamp]";
        Assert.Contains(BlueprintValidator.Validate(light), i => i.Where == "States › TurningOn › timeout" && i.Message.Contains("Number"));
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
            Id = Guid.NewGuid(),
            Kind = BlueprintKind.Unit,
            Name = "LightPair",
            Roles = [new BlueprintRole { Name = "MAIN", BlueprintId = light.Id }, new BlueprintRole { Name = "BACKUP", BlueprintId = light.Id }],
            States =
            [
                new BlueprintState { Name = "Watching", Category = 400, Initial = true },
                new BlueprintState { Name = "Backup", Category = 400, Entry = [new BlueprintAction { Tag = "BACKUP.CMD.set_on", Value = "TRUE" }], Exit = [new BlueprintAction { Tag = "BACKUP.CMD.set_on", Value = "FALSE" }] }
            ],
            Transitions =
            [
                new BlueprintTransition { Name = "fail", From = ["Watching"], To = "Backup", Guard = "[MAIN.ALM.CurrentWhileOff.active] || [MAIN.STS.state] == Broken" },
                new BlueprintTransition { Name = "back", From = ["Backup"], To = "Watching", Guard = "![MAIN.ALM.CurrentWhileOff.active] && [STS.auto] && [MAIN.is_available]" }
            ]
        };
        Blueprint? Lookup(Guid id) => id == light.Id ? light : null;
        Assert.Empty(BlueprintValidator.Validate(unit, Lookup));
        unit.Transitions[0].Guard = "[MAIN.ALM.LampFailure.active]";
        Assert.Contains(BlueprintValidator.Validate(unit, Lookup), i => i.Message.Contains("Unknown tag"));
        unit.Transitions[0].Guard = "[MAIN.STS.state] == Broken";
        unit.States[1].Run = [new BlueprintAction { Tag = "BACKUP.OUT.lamp", Value = "TRUE" }];
        Assert.Contains(BlueprintValidator.Validate(unit, Lookup), i => i.Message.Contains("only write"));
    }

    [Fact]
    public void NewBlueprintsGetIdsThatStay()
    {
        var blueprint = new Blueprint { Name = "X", Tags = [new BlueprintTag { Group = "FIN", Name = "a" }], Alarms = [Alarm("A")] };
        BlueprintRules.AssignIds(blueprint);
        var ids = (blueprint.Id, blueprint.Tags[0].Id, blueprint.Alarms[0].Alarm.Id);
        Assert.DoesNotContain(Guid.Empty, new[] { ids.Item1, ids.Item2, ids.Item3 });
        blueprint.Name = "Y";
        blueprint.Tags[0].Name = "b";
        BlueprintRules.AssignIds(blueprint);
        Assert.Equal(ids, (blueprint.Id, blueprint.Tags[0].Id, blueprint.Alarms[0].Alarm.Id));
    }
}
