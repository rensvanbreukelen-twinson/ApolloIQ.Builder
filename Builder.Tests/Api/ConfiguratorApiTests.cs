using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Builder.Backend.Contracts;
using Builder.Backend.Endpoints;
using Builder.Core.Model;
using Xunit;

namespace Builder.Tests.Api;

/// <summary>A Unit (Plant) with an Equipment module (LightingGroup) whose roles are a Light and a CircuitBreaker, built through the API.</summary>
public sealed class ConfiguratorApiTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiServer _server = null!;
    private Guid _project;
    private TreeNodeDto _pms = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await ApiServer.StartAsync();
        _project = (await _server.Post<ProjectDto>("/api/projects", new CreateProjectRequest("Configurator"))).Id;
        _pms = await _server.Post<TreeNodeDto>($"/api/projects/{_project}/folders", new CreateFolderRequest("PMS", null));
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _server.DeleteData();
    }

    private string Url(string path) => $"/api/projects/{_project}{path}";

    private sealed record Plant(TreeNodeDto Unit, TreeNodeDto Group, TreeNodeDto Lamp, TreeNodeDto Feed);

    /// <summary>PMS.PLANT (Plant) › GROUP1 (LightingGroup) › LAMP1 (Light) and FEED1 (CircuitBreaker); members take their roles when created inside.</summary>
    private async Task<Plant> BuildPlant()
    {
        var unit = await _server.Post<TreeNodeDto>(Url("/units"), new CreateUnitRequest("PLANT", _pms.Id, Fixtures.Plant));
        var group = await _server.Post<TreeNodeDto>(Url("/units"), new CreateUnitRequest("GROUP1", unit.Id, Fixtures.LightingGroup));
        var lamp = await _server.Post<TreeNodeDto>(Url("/control-modules"), new CreateControlModuleRequest(Fixtures.Light, "LAMP1", group.Id));
        var feed = await _server.Post<TreeNodeDto>(Url("/control-modules"), new CreateControlModuleRequest(Fixtures.CircuitBreaker, "FEED1", group.Id));
        return new Plant(unit, group, lamp, feed);
    }

    private string Sim => Url("/simulation");

    private async Task<JsonElement> Step(int cycles) => await _server.Post<JsonElement>($"{Sim}/step", new { cycles });

    private async Task Write(string tag, object value) => (await _server.Client.PostAsJsonAsync($"{Sim}/write", new { tag, value }, Ct)).EnsureSuccessStatusCode();

    private async Task Force(string tag, object value) => (await _server.Client.PostAsJsonAsync($"{Sim}/force", new { tag, value }, Ct)).EnsureSuccessStatusCode();

    private static string StateOf(JsonElement state, string path) =>
        state.GetProperty("controlModules").EnumerateArray().Single(c => c.GetProperty("path").GetString() == path).GetProperty("stateName").GetString()!;

    private async Task<string> Until(string path, string expected, int maxCycles)
    {
        var state = await Step(1);
        for (var i = 0; i < maxCycles && StateOf(state, path) != expected; i += 5)
            state = await Step(5);
        return StateOf(state, path);
    }

    [Fact]
    public async Task UnitsHoldTheirMembersInRolesAndPositionsAreKept()
    {
        var plant = await BuildPlant();
        Assert.Equal(("unit", "equipmentModule"), (plant.Unit.Kind, plant.Group.Kind));
        var other = await _server.Post<TreeNodeDto>(Url("/control-modules"), new CreateControlModuleRequest(Fixtures.Light, "SPARE", _pms.Id));

        var wrong = await _server.Client.PutAsJsonAsync(Url($"/units/{plant.Group.Id}/roles/FEED"), new UnitMemberRequest(other.Id), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        (await _server.Client.PutAsJsonAsync(Url("/layout"), new LayoutRequest([new LayoutItem(other.Id, 300.4, 120)]), Ct)).EnsureSuccessStatusCode();

        var view = await _server.Get<ConfiguratorDto>(Url($"/configurator?folder={_pms.Id}"));
        var lamp = view.ControlModules.Single(c => c.Name == "LAMP1");
        Assert.Equal((plant.Group.Id, "LAMP", "Light", Fixtures.Light), (lamp.UnitId!.Value, lamp.Role, lamp.Blueprint, lamp.BlueprintId));
        Assert.True(lamp.Interlocks.HasInterlocks);
        Assert.Equal((1, 1), (lamp.Interlocks.SwitchOn, lamp.Interlocks.Trips));
        Assert.Contains("lamp current", lamp.Description);
        Assert.Equal(new PositionDto(300, 120), view.ControlModules.Single(c => c.Name == "SPARE").Position);
        var group = view.Units.Single(u => u.Name == "GROUP1");
        Assert.Equal((Guid?)plant.Lamp.Id, group.Roles.Single(r => r.Role == "LAMP").ControlModuleId);
        Assert.Equal(("CircuitBreaker", Fixtures.CircuitBreaker), (group.Roles.Single(r => r.Role == "FEED").Blueprint, group.Roles.Single(r => r.Role == "FEED").BlueprintId));
        Assert.Equal(("PLANT", "GROUP"), (view.Units.Single(u => u.Name == "PLANT").Name, group.Role));

        await _server.DisposeAsync();
        _server = await ApiServer.StartAsync(_server.ProjectsRoot);
        var reloaded = await _server.Get<ConfiguratorDto>(Url($"/configurator?folder={_pms.Id}"));
        Assert.Equal(plant.Lamp.Id, reloaded.Units.Single(u => u.Name == "GROUP1").Roles.Single(r => r.Role == "LAMP").ControlModuleId);
        Assert.Equal(new PositionDto(300, 120), reloaded.ControlModules.Single(c => c.Name == "SPARE").Position);

        (await _server.Client.DeleteAsync(Url($"/objects/{plant.Lamp.Id}"), Ct)).EnsureSuccessStatusCode();
        var afterDelete = await _server.Get<ConfiguratorDto>(Url($"/configurator?folder={_pms.Id}"));
        Assert.Null(afterDelete.Units.Single(u => u.Name == "GROUP1").Roles.Single(r => r.Role == "LAMP").ControlModuleId);
    }

    [Fact]
    public async Task InterlocksOfTheEquipmentModuleActOnItsMembers()
    {
        var plant = await BuildPlant();
        var onLamp = await _server.Get<ObjectInterlocksDto>(Url($"/objects/{plant.Lamp.Id}/interlocks"));
        Assert.Equal([("Feeder closed", "blueprint", "PMS.PLANT.GROUP1"), ("Feeder opened", "blueprint", "PMS.PLANT.GROUP1")],
            onLamp.ActingOnThis.Select(a => (a.Text, a.Origin, a.DefinedBy)));
        Assert.Equal("[PMS.PLANT.GROUP1.FEED1.is_closed]", onLamp.ActingOnThis[0].Condition);
        var onGroup = await _server.Get<ObjectInterlocksDto>(Url($"/objects/{plant.Group.Id}/interlocks"));
        Assert.Equal("FEED1 not Tripped", Assert.Single(onGroup.ActingOnThis).Text);
    }

    [Fact]
    public async Task CommandInputsAreReadChangedAndReset()
    {
        var light = await _server.Post<TreeNodeDto>(Url("/control-modules"), new CreateControlModuleRequest(Fixtures.Light, "L9", _pms.Id));
        var url = Url($"/control-modules/{light.Id}/command-inputs");
        var loaded = await _server.Get<CommandInputsDto>(url);
        Assert.Equal(["HMI", "BOARD"], loaded.Config.Rows.Select(r => r.Name));
        Assert.True(loaded.HasPair);
        Assert.Contains("reset", loaded.SingleCommands);

        var config = new CommandInputConfig([
            new CommandInput("HMI", CommandSource.Hmi, InputKind.Pulse, On: 1, Off: 1, Commands: ["reset"]),
            new CommandInput("ECR", CommandSource.DigitalInput, InputKind.Button, On: 2, Off: 2, Debounce: 0.1)]);
        var put = await _server.Client.PutAsJsonAsync(url, config, ApiServer.Json, Ct);
        put.EnsureSuccessStatusCode();
        var tags = await _server.Get<List<TagDto>>(Url($"/tags?scope={light.Id}"));
        Assert.Contains(tags, t => t.Path == "PMS.L9.FIN.ECR_on");
        Assert.Contains(tags, t => t.Path == "PMS.L9.PAR.ECR_debounce");
        Assert.DoesNotContain(tags, t => t.Path == "PMS.L9.FIN.BOARD");

        var bad = config with { Rows = [.. config.Rows, new CommandInput("SW", CommandSource.DigitalInput, InputKind.Switch, On: 1, Off: 1)] };
        Assert.Equal(HttpStatusCode.BadRequest, (await _server.Client.PutAsJsonAsync(url, bad, ApiServer.Json, Ct)).StatusCode);

        var reset = await _server.Post<CommandInputsDto>($"{url}/reset", new { });
        Assert.Equal(2, reset.Config.Rows.Count);
        tags = await _server.Get<List<TagDto>>(Url($"/tags?scope={light.Id}"));
        Assert.DoesNotContain(tags, t => t.Path == "PMS.L9.FIN.ECR_on");
    }

    [Fact]
    public async Task TheUnitRunsItsEquipmentModuleAndATripEscalates()
    {
        await BuildPlant();
        var first = await Step(3);
        Assert.Empty(first.GetProperty("errors").EnumerateArray());
        Assert.Equal("Idle", StateOf(first, "PMS.PLANT"));
        await Write("PMS.PLANT.GROUP1.CMD.set_auto", true);
        await Step(2);
        await Write("PMS.PLANT.CMD.set_auto", true);
        await Step(2);
        await Write("PMS.PLANT.CMD.set_on", true);
        Assert.Equal("Producing", await Until("PMS.PLANT", "Producing", 100));
        var running = await Step(1);
        Assert.Equal(("Lit", "On", "Closed"), (StateOf(running, "PMS.PLANT.GROUP1"), StateOf(running, "PMS.PLANT.GROUP1.LAMP1"), StateOf(running, "PMS.PLANT.GROUP1.FEED1")));

        await Force("PMS.PLANT.GROUP1.FEED1.FIN.feedback", false);
        Assert.Equal("Fault", await Until("PMS.PLANT.GROUP1", "Fault", 40));
        Assert.Equal("Fault", await Until("PMS.PLANT", "Fault", 20));
        var alarms = await _server.Get<JsonElement>($"{Sim}/alarms");
        Assert.Contains(alarms.EnumerateArray(), a => a.GetProperty("name").GetString() == "FeederOpen" && a.GetProperty("active").GetBoolean()
                                                      && a.GetProperty("object").GetString() == "PMS.PLANT.GROUP1");
        Assert.Contains(alarms.EnumerateArray(), a => a.GetProperty("name").GetString() == "LampFailure" && !a.GetProperty("plcReactive").GetBoolean());
    }

    [Fact]
    public async Task AProjectInterlockOnAnEquipmentModuleBlocksItsStart()
    {
        var plant = await BuildPlant();
        await _server.Post<TreeNodeDto>(Url("/control-modules"), new CreateControlModuleRequest(Fixtures.CircuitBreaker, "MAIN", _pms.Id));
        var bad = await _server.Client.PostAsJsonAsync(Url($"/objects/{plant.Unit.Id}/interlocks/validate"), new ValidateConditionRequest("[PMS.MAIN.FIN.nope]"), Ct);
        Assert.NotNull((await bad.Content.ReadFromJsonAsync<ValidateConditionResponse>(Ct))!.Error);
        var old = await _server.Client.PostAsJsonAsync(Url($"/objects/{plant.Unit.Id}/interlocks/validate"), new ValidateConditionRequest("NOT [PMS.MAIN.is_closed]"), Ct);
        Assert.NotNull((await old.Content.ReadFromJsonAsync<ValidateConditionResponse>(Ct))!.Error);
        var set = await (await _server.Client.PutAsJsonAsync(Url($"/objects/{plant.Unit.Id}/interlocks"),
                new SetInterlocksRequest([new InterlockRuleRequest(plant.Group.Id, "SwitchOn", "[PMS.MAIN.is_closed]", "Main breaker closed", null, null, null)]), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ObjectInterlocksDto>(Ct);
        var rule = Assert.Single(set!.Interlocks);
        Assert.Equal(("PMS.PLANT.GROUP1", "[PMS.MAIN.is_closed]", null), (rule.TargetPath, rule.Condition, rule.Error));

        await _server.DisposeAsync();
        _server = await ApiServer.StartAsync(_server.ProjectsRoot);
        var reloaded = await _server.Get<ObjectInterlocksDto>(Url($"/objects/{plant.Group.Id}/interlocks"));
        Assert.Contains(reloaded.ActingOnThis, a => a is { Text: "Main breaker closed", DefinedBy: "PMS.PLANT", Origin: "project" });

        Assert.Empty((await Step(3)).GetProperty("errors").EnumerateArray());
        await Write("PMS.PLANT.GROUP1.CMD.set_auto", true);
        await Step(2);
        await Write("PMS.PLANT.CMD.set_auto", true);
        await Step(2);
        await Write("PMS.PLANT.CMD.set_on", true);
        var blocked = await Step(10);
        Assert.Equal(("StartingGroup", "Idle"), (StateOf(blocked, "PMS.PLANT"), StateOf(blocked, "PMS.PLANT.GROUP1")));
    }
}
