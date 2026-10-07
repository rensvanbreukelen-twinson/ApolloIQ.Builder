using ApolloIQ.Core.Alarms;
using ApolloIQ.Core.Blueprints;
using ApolloIQ.Core.Exchange;
using ApolloIQ.Core.Identity;
using Builder.Backend.Services;
using Builder.Core.Model;
using Builder.Core.Types;
using Builder.Logic.Blueprints;
using Builder.Persistence.Export;
using Xunit;

namespace Builder.Tests.Export;

/// <summary>The export to SCADA: the Core exchange file built from a project and its blueprints.</summary>
public sealed class ExchangeExporterTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.Parse("e0000000-0000-4000-8000-000000000001");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record Sample(Project Project, CmLibrary Library, Func<Guid, Blueprint?> Blueprints, UnitInstance Plant, UnitInstance Group,
        ControlModule Lamp, ControlModule Feed, ControlModule Main);

    /// <summary>PMS.PLANT (Plant) › GROUP1 (LightingGroup) › LAMP1, FEED1; PMS.MAIN (CircuitBreaker) on PLC1; a project interlock on the plant.</summary>
    private Sample Build(IReadOnlyList<Blueprint>? blueprints = null)
    {
        blueprints ??= Fixtures.All();
        var store = new BlueprintStore(Path.Combine(_root, "blueprints", Guid.NewGuid().ToString("N")));
        foreach (var blueprint in blueprints)
            store.Save(blueprint.Clone());
        var library = Fixtures.Library([.. blueprints]);
        var project = new Project();
        var pms = project.AddFolder("PMS");
        var plant = UnitSupport.Create(project, blueprints.Single(b => b.Id == Fixtures.Plant), "PLANT", pms.Id, library, store);
        var group = UnitSupport.Create(project, blueprints.Single(b => b.Id == Fixtures.LightingGroup), "GROUP1", plant.Id, library, store);
        var lamp = InstanceFactory.Create(project, library, Fixtures.Light, "LAMP1", group.Id);
        UnitSupport.Entered(project, library, store, lamp.Id);
        var feed = InstanceFactory.Create(project, library, Fixtures.CircuitBreaker, "FEED1", group.Id);
        UnitSupport.Entered(project, library, store, feed.Id);
        var main = InstanceFactory.Create(project, library, Fixtures.CircuitBreaker, "MAIN", pms.Id);
        var plc = Guid.NewGuid();
        project.SetTopology([new Device(plc, "PLC1", DeviceRole.Plc)], []);
        project.SetExecutionDevice(main.Id, plc);
        project.SetInterlocks(plant.Id, [
            new InterlockRule { TargetId = lamp.Id, Kind = InterlockKind.SwitchOn, Condition = ExpressionReferences.ToStored(project, "[PMS.MAIN.is_closed]"), Text = "Main breaker closed" },
            new InterlockRule { TargetId = lamp.Id, Kind = InterlockKind.Trip, Condition = ExpressionReferences.ToStored(project, "![PMS.MAIN.is_closed]"), Alarm = "MainOpened", Priority = 21 }]);
        var lookup = Fixtures.Lookup(blueprints);
        return new Sample(project, library, lookup, plant, group, project.Get<ControlModule>(lamp.Id), project.Get<ControlModule>(feed.Id), main);
    }

    private static ExportResult Export(Sample sample, ExportRecord? last = null) =>
        ExchangeExporter.Build(ProjectId, "Lighting", sample.Project, sample.Library, sample.Blueprints, last);

    private static ExchangeBlueprint Blueprint(ExchangeFile file, string name) => file.Blueprints.Single(b => b.Name == name);

    [Fact]
    public void TheExportCarriesTheUsedBlueprintsAndTheInstancesParentsFirst()
    {
        var sample = Build();
        var (file, check) = Export(sample);
        Assert.Empty(check.Errors);
        Assert.Equal((ProjectId, "Lighting", "ApolloIQ.Builder"), (file.Source.ProjectId, file.Source.ProjectName, file.Source.Generator));
        Assert.Equal(["CircuitBreaker", "Light", "LightingGroup", "Plant"], file.Blueprints.Select(b => b.Name));
        Assert.Equal([sample.Main.Id, sample.Plant.Id, sample.Group.Id, sample.Feed.Id, sample.Lamp.Id], file.Instances.Select(i => i.Id));
        var lamp = file.Instances.Single(i => i.Id == sample.Lamp.Id);
        Assert.Equal(("LAMP1", Fixtures.Light, (Guid?)sample.Group.Id), (lamp.Name, lamp.BlueprintId, lamp.ParentId));
        Assert.Equal(sample.Plant.Id, file.Instances.Single(i => i.Id == sample.Group.Id).ParentId);
        Assert.Null(file.Instances.Single(i => i.Id == sample.Plant.Id).ParentId);
        Assert.Equal("PLC1", file.Instances.Single(i => i.Id == sample.Main.Id).Device);
        Assert.Null(lamp.Device);
        Assert.Equal([("Light", "0.1.0", false)], check.Blueprints.Where(b => b.Name == "Light").Select(b => (b.Name, b.Version, b.ChangedSinceLastExport)));
    }

    [Fact]
    public void BlueprintTagsLeaveOutOutputsAlarmAndInterfaceTagsAndAddTheGeneratedOnes()
    {
        var (file, _) = Export(Build());
        var light = Blueprint(file, "Light");
        var keys = light.Tags.Select(t => $"{t.Group}.{t.Name}").ToList();
        Assert.Equal(["Base", "Switchable", "Resettable", "Interlocks"], light.Interfaces);
        Assert.Subset(keys.ToHashSet(), new HashSet<string> { "FIN.feedback", "FIN.current", "PAR.max_switch_time", "PAR.max_current", "INT.feedback", "SET.invert_feedback" });
        Assert.DoesNotContain("OUT.lamp", keys);
        Assert.DoesNotContain(keys, k => k.StartsWith("ALM.", StringComparison.Ordinal) || k is "STS.state" or "CMD.set_on" or "LOK.can_on" or "INT.bp_prev_state");
        Assert.Equal(keys.Count, keys.Distinct().Count());

        var source = Fixtures.Load("Light");
        Assert.Equal(source.Tags.Single(t => t.Key == "FIN.feedback").Id, light.Tags.Single(t => t is { Group: "FIN", Name: "feedback" }).Id);
        Assert.Equal(TagDataType.Real, light.Tags.Single(t => t.Name == "current").DataType);
        Assert.Equal("A", light.Tags.Single(t => t.Name == "current").Unit);
        Assert.Equal(StableId.From(ExchangeExporter.GeneratedTagSpace, Fixtures.Light.ToString("D"), "SET.invert_feedback"),
            light.Tags.Single(t => t is { Group: "SET", Name: "invert_feedback" }).Id);

        var group = Blueprint(file, "LightingGroup");
        Assert.Equal(["Base", "Switchable", "Resettable", "Interlocks", "AutoManual"], group.Interfaces);
        Assert.Equal(StableId.From(ExchangeExporter.InterfaceTagSpace, Fixtures.LightingGroup.ToString("D"), "INT.escalated"),
            group.Tags.Single(t => t is { Group: "INT", Name: "escalated" }).Id);
        Assert.Contains(group.Tags, t => t is { Group: "INT", Name: "member_tripped" });
        Assert.Contains(group.Tags, t => t is { Group: "PAR", Name: "feed_timeout" });
        Assert.DoesNotContain(group.Tags, t => t.Name == "beacon");
    }

    [Fact]
    public void TheDefaultCommandInputsGiveTheCommandTagsTheHmiWrites()
    {
        var (file, _) = Export(Build());
        var light = Blueprint(file, "Light");
        var hmiOn = light.Tags.Single(t => t is { Group: "CMD", Name: "HMI_on" });
        Assert.Equal(StableId.From(ExchangeExporter.CommandInputTagSpace, Fixtures.Light.ToString("D"), "CMD.HMI_on"), hmiOn.Id);
        Assert.Contains(light.Tags, t => t is { Group: "CMD", Name: "HMI_reset" });
        Assert.Contains(light.Tags, t => t is { Group: "FIN", Name: "BOARD" });
        Assert.Equal(new Dictionary<string, string> { ["HMI_on"] = "On", ["HMI_off"] = "Off", ["HMI_reset"] = "Reset" }, light.CommandLabels);
        Assert.Empty(Blueprint(file, "Plant").CommandLabels);
    }

    [Fact]
    public void StatesCarryTheirCodeNameTextAndDescription()
    {
        var (file, _) = Export(Build());
        var light = Blueprint(file, "Light");
        (int, string, string?)[] expected = [(100, "TurningOff", "Turning off"), (200, "Off", null), (300, "TurningOn", "Turning on"), (400, "On", null), (500, "Broken", "Lamp broken")];
        Assert.Equal(expected, light.States.Select(s => (s.Code, s.Name, s.Text)));
        var fault = Blueprint(file, "LightingGroup").States.Single(s => s.Name == "Fault");
        Assert.Equal(("Group fault", 500), (fault.Text, fault.Code));
        Assert.StartsWith("The lamp broke", fault.Description);
        Assert.Equal([300, 301], Blueprint(file, "LightingGroup").States.Where(s => s.Code / 100 == 3).Select(s => s.Code));
    }

    [Fact]
    public void AlarmsGoToScadaAsScadaRunsThem()
    {
        var (file, _) = Export(Build());
        var light = Blueprint(file, "Light");
        Assert.Equal(["LampFailure", "Overcurrent", "SlowSwitch", "CurrentWhileOff", "DoesNotSwitchOn"], light.Alarms.Select(a => a.Name));
        var failure = light.Alarms.Single(a => a.Name == "LampFailure");
        Assert.Equal((AlarmTrigger.State, false, 1d, "[STS.state] == On && ![INT.feedback]"), (failure.Trigger, failure.PlcReactive, failure.OnDelaySeconds, failure.Condition));
        Assert.Equal(AlarmTrigger.Range, light.Alarms.Single(a => a.Name == "Overcurrent").Trigger);
        Assert.Equal((AlarmTrigger.Timeout, "0.5"), (light.Alarms.Single(a => a.Name == "SlowSwitch").Trigger, light.Alarms.Single(a => a.Name == "SlowSwitch").Timeout));
        var plc = light.Alarms.Single(a => a.Name == "CurrentWhileOff");
        Assert.Equal((AlarmTrigger.PlcByte, true, "[STS.state] == Off && [FIN.current] > 0.5"), (plc.Trigger, plc.PlcReactive, plc.Condition));
        var timeout = light.Alarms.Single(a => a.Name == "DoesNotSwitchOn");
        Assert.Equal((AlarmTrigger.PlcByte, 20, "{instance_name}: Turning on took too long"), (timeout.Trigger, timeout.Priority, timeout.Message));
        Assert.Equal(Fixtures.Load("Light").Alarms[0].Alarm.Id, failure.Id);

        var group = Blueprint(file, "LightingGroup");
        Assert.Equal(["LampBroken", "FeederDidNotClose", "ManualByOverride", "FeederOpen"], group.Alarms.Select(a => a.Name));
        Assert.All(group.Alarms, a => Assert.Equal(AlarmTrigger.PlcByte, a.Trigger));
        Assert.Equal(Guid.Parse("3c9d64a1-6a51-4d8e-9a4c-2f7b8a1d0030"), group.Alarms.Single(a => a.Name == "FeederOpen").Id);
        Assert.Empty(AlarmRules.Clean([.. light.Alarms.Select(a => a.Clone())], inBlueprint: true));
    }

    [Fact]
    public void InterlockTextsFollowTheStatusBits()
    {
        var sample = Build();
        var (file, _) = Export(sample);
        var own = Blueprint(file, "LightingGroup").InterlockTexts;
        Assert.Equal([(0, "FEED not Tripped")], own.SwitchOn.Select(t => (t.Bit, t.Text)));
        Assert.Empty(own.SwitchOff);
        Assert.Empty(Blueprint(file, "Light").InterlockTexts.SwitchOn);

        var lamp = file.Instances.Single(i => i.Id == sample.Lamp.Id);
        Assert.Equal([(0, "Feeder closed"), (1, "Main breaker closed")], lamp.InterlockTexts!.SwitchOn.Select(t => (t.Bit, t.Text)));
        Assert.Null(file.Instances.Single(i => i.Id == sample.Group.Id).InterlockTexts);
        Assert.Null(file.Instances.Single(i => i.Id == sample.Feed.Id).InterlockTexts);

        var plantAlarm = Assert.Single(file.Instances.Single(i => i.Id == sample.Plant.Id).Alarms);
        Assert.Equal(("MainOpened", 21, AlarmTrigger.PlcByte, "![PMS.MAIN.is_closed]"), (plantAlarm.Name, plantAlarm.Priority, plantAlarm.Trigger, plantAlarm.Condition));
        Assert.Equal(sample.Plant.Interlocks[1].AlarmId, plantAlarm.Id);
        Assert.Empty(lamp.Alarms);
    }

    [Fact]
    public void IdsAndHashesAreStableAcrossExports()
    {
        var sample = Build();
        var first = Export(sample).File;
        var second = Export(sample).File;
        Assert.Equal(first.Blueprints.Select(b => (b.Id, ExchangeJson.Hash(b))), second.Blueprints.Select(b => (b.Id, ExchangeJson.Hash(b))));
        Assert.Equal(first.Blueprints.SelectMany(b => b.Tags.Select(t => t.Id)), second.Blueprints.SelectMany(b => b.Tags.Select(t => t.Id)));
        Assert.Equal(first.Blueprints.SelectMany(b => b.Alarms.Select(a => a.Id)), second.Blueprints.SelectMany(b => b.Alarms.Select(a => a.Id)));
        Assert.DoesNotContain(first.Blueprints.SelectMany(b => b.Alarms), a => a.Id == Guid.Empty);
    }

    [Fact]
    public void ARenamedBlueprintKeepsItsIdsInTheExport()
    {
        var before = Export(Build()).File;
        var blueprints = Fixtures.All().ToList();
        var light = blueprints.Single(b => b.Id == Fixtures.Light);
        light.Name = "DeckLight";
        light.Tags.Single(t => t.Key == "FIN.current").Name = "lamp_amps";
        foreach (var alarm in light.Alarms)
        {
            alarm.Alarm.Input = alarm.Alarm.Input.Replace("FIN.current", "FIN.lamp_amps", StringComparison.Ordinal);
            alarm.Alarm.Condition = alarm.Alarm.Condition.Replace("FIN.current", "FIN.lamp_amps", StringComparison.Ordinal);
        }
        Assert.Empty(BlueprintValidator.Validate(light.Clone()));
        var after = Export(Build(blueprints)).File;
        var renamed = Blueprint(after, "DeckLight");
        Assert.Equal(Fixtures.Light, renamed.Id);
        Assert.Equal(Blueprint(before, "Light").Tags.Single(t => t.Name == "current").Id, renamed.Tags.Single(t => t.Name == "lamp_amps").Id);
        Assert.Equal(Blueprint(before, "Light").Alarms.Select(a => a.Id), renamed.Alarms.Select(a => a.Id));
        Assert.All(after.Instances.Where(i => i.Name == "LAMP1"), i => Assert.Equal(Fixtures.Light, i.BlueprintId));
    }

    [Fact]
    public void ASampleExportIsReadableForScada()
    {
        var (file, check) = Export(Build());
        Assert.Empty(check.Errors);
        var json = ExchangeJson.Write(file);
        var path = Path.Combine(AppContext.BaseDirectory, "sample-export" + ExchangeFile.FileSuffix);
        File.WriteAllText(path, json);

        var read = ExchangeJson.Read(File.ReadAllText(path));
        Assert.Equal(ExchangeFile.Schema, read.SchemaId);
        Assert.Equal(file.Blueprints.Select(b => b.Hash), read.Blueprints.Select(b => b.Hash));
        Assert.All(read.Blueprints, b => Assert.Equal(ExchangeJson.Hash(b), b.Hash));
        Assert.Equal(5, read.Instances.Count);
        Assert.Contains("\"schemaId\": \"apolloiq.exchange/1\"", json);
        Assert.Contains("\"trigger\": \"plcByte\"", json);
        Assert.Contains("\"version\": \"0.1.0\"", json);
    }

    [Fact]
    public void WarningsNameWhatDoesNotReachScada()
    {
        var sample = Build();
        sample.Project.SetAlarmPriority(sample.Lamp.Id, "LampFailure", 25);
        CommandInputBehaviour.Configure(sample.Project, sample.Library, sample.Main.Id,
            new CommandInputConfig([new CommandInput("HMI", CommandSource.Hmi, InputKind.Hold, On: 1, Off: 1)]));
        var (_, check) = Export(sample);
        Assert.Empty(check.Errors);
        Assert.Contains(check.Warnings, w => w.Contains("PMS.PLANT.GROUP1.LAMP1: alarm LampFailure has priority 25") && w.Contains("(10)"));
        Assert.Contains(check.Warnings, w => w.StartsWith("PMS.MAIN: its command inputs differ", StringComparison.Ordinal));
        Assert.DoesNotContain(check.Warnings, w => w.StartsWith("PMS.PLANT.GROUP1.LAMP1: its command inputs", StringComparison.Ordinal));
    }

    [Fact]
    public void BlueprintErrorsBlockTheExport()
    {
        var blueprints = Fixtures.All().ToList();
        var sample = Build(blueprints);
        blueprints.Single(b => b.Id == Fixtures.Light).Transitions[0].Guard = "[CMD.set_on] AND [LOK.can_on]";
        var (_, check) = ExchangeExporter.Build(ProjectId, "Lighting", sample.Project, sample.Library, Fixtures.Lookup(blueprints));
        Assert.Contains(check.Errors, e => e.StartsWith("Blueprint Light: Transitions › switch_on", StringComparison.Ordinal));
        Assert.False(check.Ok);
    }

    [Fact]
    public void TheVersionGuardRefusesAChangedBlueprintWithTheSameVersion()
    {
        var first = Export(Build());
        Assert.Empty(first.Check.Errors);
        var record = new ExportRecord();
        foreach (var blueprint in first.File.Blueprints)
            record.Blueprints[blueprint.Id] = new ExportedBlueprint { Name = blueprint.Name, Version = blueprint.Version, Hash = ExchangeJson.Hash(blueprint) };
        Assert.Empty(Export(Build(), record).Check.Errors);
        Assert.DoesNotContain(Export(Build(), record).Check.Blueprints, b => b.ChangedSinceLastExport);

        var blueprints = Fixtures.All().ToList();
        var light = blueprints.Single(b => b.Id == Fixtures.Light);
        light.States[2].Text = "Lamp on";
        var changed = Export(Build(blueprints), record).Check;
        Assert.Equal("Light changed since the last export to SCADA; raise its version (now 0.1.0).", Assert.Single(changed.Errors));
        Assert.True(changed.Blueprints.Single(b => b.Name == "Light").ChangedSinceLastExport);

        light.Version = light.Version.NextMinor();
        var bumped = Export(Build(blueprints), record).Check;
        Assert.Empty(bumped.Errors);
        Assert.Equal(("0.1.1", "0.1.0", true), bumped.Blueprints.Where(b => b.Name == "Light").Select(b => (b.Version, b.LastExportedVersion, b.ChangedSinceLastExport)).Single());
    }

    [Fact]
    public void TheExportRecordRoundTrips()
    {
        var record = new ExportRecord { ExportedAt = DateTimeOffset.UnixEpoch };
        record.Blueprints[Fixtures.Light] = new ExportedBlueprint { Name = "Light", Version = new ApolloIQ.Core.Versioning.BlueprintVersion(1, 2, 3), Hash = "abc" };
        record.Save(_root);
        var text = File.ReadAllText(Path.Combine(_root, ExportRecord.FileName));
        Assert.Contains("\"version\": \"1.2.3\"", text);
        var loaded = ExportRecord.Load(_root);
        Assert.Equal(("Light", "1.2.3", "abc"), (loaded.Blueprints[Fixtures.Light].Name, loaded.Blueprints[Fixtures.Light].Version.ToString(), loaded.Blueprints[Fixtures.Light].Hash));
        Assert.Empty(ExportRecord.Load(Path.Combine(_root, "none")).Blueprints);
    }
}
