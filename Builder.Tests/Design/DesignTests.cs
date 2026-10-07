using System.Text.Json;
using System.Text.Json.Nodes;
using Builder.Core.Model;
using Builder.Core.Tags;
using Builder.Design;
using Builder.Logic.Blueprints;
using Xunit;

namespace Builder.Tests.Design;

public class DesignTests
{
    public static string DirtyWaterFile => Path.Combine(AppContext.BaseDirectory, "Fixtures", "design", "dirty-water.design.json");

    public static JsonObject DirtyWaterJson() => JsonNode.Parse(File.ReadAllText(DirtyWaterFile))!.AsObject();

    private static DesignDocument DirtyWater() => DesignDocument.Parse(DirtyWaterJson());

    private static DesignDocument Fragment(string json) => DesignDocument.Parse(JsonNode.Parse(json));

    /// <summary>Applies every item of a design and returns the new project and library (fails the test on any error).</summary>
    private static (Project Project, List<Blueprint> Blueprints, DesignPlan Plan) ApplyAll(DesignDocument design, Project project, IReadOnlyCollection<Blueprint> blueprints)
    {
        var plan = DesignPlanner.Plan(design, project, blueprints);
        Assert.Empty(plan.Problems);
        var result = DesignApplier.Apply(plan, plan.Items.Select(i => i.Id), project, blueprints);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var library = blueprints.Where(b => !result.DeletedBlueprints.Contains(b.Id) && result.ChangedBlueprints.All(c => c.Id != b.Id))
            .Concat(result.ChangedBlueprints).ToList();
        return (result.Project!, library, plan);
    }

    private static (Project Project, List<Blueprint> Blueprints) DirtyWaterProject()
    {
        var (project, blueprints, _) = ApplyAll(DirtyWater(), new Project(), []);
        return (project, blueprints);
    }

    [Fact]
    public void DirtyWaterOnAnEmptyProjectValidates()
    {
        var plan = DesignPlanner.Plan(DirtyWater(), new Project(), []);
        Assert.Empty(plan.Problems);
        Assert.Contains(plan.Items, i => i.Id == "blueprint:Pump" && i.Kind == ChangeKinds.Create);
        Assert.Contains(plan.Items, i => i.Id == "object:Bilge.DirtyWaterTank.TransferPump" && i.DependsOn.Contains("object:Bilge.DirtyWaterTank"));
        Assert.Contains("blueprint:DirtyWaterTank", plan.Find("object:Bilge.DirtyWaterTank")!.DependsOn);
        Assert.Contains("blueprint:Pump", plan.Find("blueprint:DirtyWaterTank")!.DependsOn);
        Assert.Equal(4, plan.Questions.Count);

        var (project, blueprints) = DirtyWaterProject();
        var tank = (UnitInstance)DesignReader.FindByPath(project, "Bilge.DirtyWaterTank")!;
        var pump = (ControlModule)DesignReader.FindByPath(project, "Bilge.DirtyWaterTank.TransferPump")!;
        Assert.True(tank.IsEquipmentModule);
        Assert.Equal(pump.Id, tank.RoleMembers["TransferPump"]);
        Assert.Equal("Dirty water transfer pump", pump.Description);
        Assert.Equal("MainPlc", project.Topology.Device(pump.ExecutionDeviceId!.Value)!.Name);
        var invert = project.GetChildren(tank.Id).OfType<Tag>().Single(t => t.Group == TagGroup.Set && t.Name == "invert_overfull_level");
        Assert.True(invert.InitialValue!.GetValue<bool>());
        Assert.Equal(2, blueprints.Count);
        Assert.All(blueprints, b => Assert.NotEqual(Guid.Empty, b.Id));
    }

    [Fact]
    public void ReadingBackGivesAnEquivalentDesignAndNoChanges()
    {
        var (project, blueprints) = DirtyWaterProject();
        var read = DesignReader.Read(project, "Dirty water demo", blueprints);
        var json = read.ToJson();

        var tank = json["objects"]![0]!["children"]![0]!;
        Assert.Equal("DirtyWaterTank", (string?)tank["em"]);
        Assert.Equal("MainPlc", (string?)tank["device"]);
        Assert.Equal("TRUE", (string?)tank["values"]!["SET.invert_overfull_level"]);
        Assert.Equal("TransferPump", (string?)tank["members"]!["TransferPump"]!["cm"]);
        Assert.Null(tank["members"]!["TransferPump"]!["device"]);
        Assert.Equal(["DirtyWaterTank", "Pump"], json["blueprints"]!.AsArray().Select(b => (string)b!["name"]!).Order());

        // The blueprints read back equal the example's.
        var original = DirtyWaterJson();
        foreach (var blueprint in original["blueprints"]!.AsArray())
        {
            var name = (string)blueprint!["name"]!;
            var back = json["blueprints"]!.AsArray().Single(b => (string)b!["name"]! == name)!;
            var normalized = DesignDocument.ToJson(DesignDocument.Parse(new JsonObject { ["blueprints"] = new JsonArray(blueprint.DeepClone()) }).Blueprints![0]);
            Assert.True(JsonNode.DeepEquals(normalized, back), $"{name}:\n{normalized}\n---\n{back}");
        }

        // Re-applying the same design and the read design both give zero change items.
        Assert.Empty(DesignPlanner.Plan(DirtyWater(), project, blueprints).Items);
        Assert.Empty(DesignPlanner.Plan(read, project, blueprints).Items);
    }

    [Fact]
    public void RenameKeepsIds()
    {
        var (project, blueprints) = DirtyWaterProject();
        var pumpId = DesignReader.FindByPath(project, "Bilge.DirtyWaterTank.TransferPump")!.Id;
        var blueprintId = blueprints.Single(b => b.Name == "Pump").Id;
        var fragment = Fragment("""
            {
              "blueprints": [ { "name": "TransferPumpBlueprint", "renamedFrom": "Pump" } ],
              "objects": [ { "folder": "Bilge", "children": [ { "em": "DirtyWaterTank", "members": {
                  "TransferPump": { "cm": "Pump1", "renamedFrom": "TransferPump" } } } ] } ]
            }
            """);
        var (renamed, library, plan) = ApplyAll(fragment, project, blueprints);
        Assert.Equal(["blueprint:TransferPumpBlueprint:rename", "object:Bilge.DirtyWaterTank.Pump1:rename"], plan.Items.Select(i => i.Id));
        Assert.Equal(pumpId, DesignReader.FindByPath(renamed, "Bilge.DirtyWaterTank.Pump1")!.Id);
        Assert.Equal(blueprintId, library.Single(b => b.Name == "TransferPumpBlueprint").Id);
        Assert.Equal(blueprintId, library.Single(b => b.Name == "DirtyWaterTank").Roles.Single().BlueprintId);
    }

    [Fact]
    public void APartialSelectionWithoutItsDependencyIsRefused()
    {
        var plan = DesignPlanner.Plan(DirtyWater(), new Project(), []);
        var result = DesignApplier.Apply(plan, ["blueprint:DirtyWaterTank"], new Project(), []);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("needs") && e.Contains("Create CM blueprint Pump"));
        Assert.Null(result.Project);

        var pumpOnly = DesignApplier.Apply(plan, ["blueprint:Pump"], new Project(), []);
        Assert.True(pumpOnly.Ok, string.Join("\n", pumpOnly.Errors));
        Assert.Equal("Pump", pumpOnly.ChangedBlueprints.Single().Name);
    }

    [Fact]
    public void ASelectionThatBreaksValidationReportsTheErrors()
    {
        var (project, blueprints) = DirtyWaterProject();
        var fragment = Fragment("""
            {
              "blueprints": [ { "name": "Pump", "version": "0.2.0",
                  "transitions": { "start": { "from": [ "Ready" ], "to": "Running", "guard": "[CMD.set_on] && [FIN.no_such_input]" } } } ],
              "objects": [ { "folder": "Bilge", "children": [ { "em": "DirtyWaterTank",
                  "values": { "SET.no_such_setting": "TRUE" }, "alarmPriorities": { "Overfull": 28 } } ] } ]
            }
            """);
        var plan = DesignPlanner.Plan(fragment, project, blueprints);
        var result = DesignApplier.Apply(plan, plan.Items.Select(i => i.Id), project, blueprints);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.StartsWith("Blueprint Pump") && e.Contains("no_such_input"));
        Assert.Contains(result.Errors, e => e.Contains("Running"));
        Assert.Contains(result.Errors, e => e.Contains("no tag SET.no_such_setting"));
        Assert.Contains(result.Errors, e => e.Contains("alarm priorities can only be overridden on a CM"));
    }

    [Fact]
    public void ProjectInterlocksUsePathsAndSurviveARename()
    {
        var (project, blueprints) = DirtyWaterProject();
        var fragment = Fragment("""
            {
              "objects": [ { "folder": "Bilge", "children": [
                { "cm": "SecondPump", "blueprint": "Pump", "device": "MainPlc" },
                { "em": "DirtyWaterTank", "interlocks": [
                  { "target": "TransferPump", "kind": "SwitchOn", "condition": "![Bilge.SecondPump.STS.state] == Pumping || TRUE", "text": "Second pump" },
                  { "target": "TransferPump", "kind": "Trip", "condition": "[Bilge.SecondPump.INT.overload]", "alarm": "SecondPumpOverload", "priority": 22 } ] } ] } ]
            }
            """);
        var plan = DesignPlanner.Plan(fragment, project, blueprints);
        var trip = plan.Items.Single(i => i.Id == "interlock:Bilge.DirtyWaterTank:trip:SecondPumpOverload");
        Assert.Contains("object:Bilge.SecondPump", trip.DependsOn);
        var (changed, library, _) = ApplyAll(fragment, project, blueprints);
        var read = DesignReader.Read(changed, "x", library, "Bilge.DirtyWaterTank").ToJson();
        var lines = read["objects"]![0]!["children"]![0]!["interlocks"]!.AsArray();
        Assert.Equal("[Bilge.SecondPump.INT.overload]", (string?)lines[1]!["condition"]);
        Assert.Equal("TransferPump", (string?)lines[1]!["target"]);

        var renamed = ApplyAll(Fragment("""{ "objects": [ { "folder": "Bilge", "children": [ { "cm": "StandbyPump", "renamedFrom": "SecondPump" } ] } ] }"""), changed, library).Project;
        var after = DesignReader.Read(renamed, "x", library, "Bilge.DirtyWaterTank").ToJson();
        Assert.Equal("[Bilge.StandbyPump.INT.overload]", (string?)after["objects"]![0]!["children"]![0]!["interlocks"]![1]!["condition"]);
        Assert.Empty(DesignPlanner.Plan(DesignReader.Read(renamed, "x", library), renamed, library).Items);
    }

    [Fact]
    public void ADesignRoundTripsThroughJson()
    {
        var design = DirtyWater();
        var again = DesignDocument.Parse(JsonNode.Parse(JsonSerializer.Serialize(design, DesignDocument.Json)));
        Assert.True(JsonNode.DeepEquals(design.ToJson(), again.ToJson()));
        Assert.Equal(5, design.Blueprints![1].Alarms!["Overfull"].OnDelay);
    }

    [Fact]
    public void AnUnknownFieldIsAnError()
    {
        var error = Assert.Throws<DesignException>(() => Fragment("""{ "blueprints": [ { "name": "Pump", "tags": { "FIN.x": { "type": "Bool", "off near the bottom": null } } } ] }"""));
        Assert.Contains("off near the bottom", error.Message);
    }
}
