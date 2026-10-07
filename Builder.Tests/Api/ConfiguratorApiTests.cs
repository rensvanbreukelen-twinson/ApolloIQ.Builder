using System.Net;
using System.Net.Http.Json;
using Builder.Backend.Contracts;
using Builder.Backend.Endpoints;
using Xunit;

namespace Builder.Tests.Api;

public sealed class ConfiguratorApiTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiServer _server = null!;
    private Guid _project;
    private TreeNodeDto _pms = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await ApiServer.StartAsync();
        var blueprints = Path.Combine(_server.ProjectsRoot, "blueprints");
        _project = (await _server.Post<ProjectDto>("/api/projects", new CreateProjectRequest("Configurator"))).Id;
        _pms = await _server.Post<TreeNodeDto>($"/api/projects/{_project}/folders", new CreateFolderRequest("PMS", null));
        foreach (var name in new[] { "GenSet", "CircuitBreaker", "PowerManagement", "PowerMeter", "GenSetPowerSource", "PowerManagementEM" })
        {
            var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "blueprints", $"{name}.blueprint.json"), Ct);
            var put = await _server.Client.PutAsync($"/api/blueprints/{name}", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);
            put.EnsureSuccessStatusCode();
        }
        Assert.True(Directory.Exists(blueprints));
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _server.DeleteData();
    }

    private string Url(string path) => $"/api/projects/{_project}{path}";

    [Fact]
    public async Task UnitsHoldCmsInTheirRolesAndPositionsAreKept()
    {
        var gen = await _server.Post<TreeNodeDto>(Url("/control-modules"), new CreateControlModuleRequest("GenSet", "GEN1", _pms.Id, []));
        var cb = await _server.Post<TreeNodeDto>(Url("/control-modules"), new CreateControlModuleRequest("CircuitBreaker", "CB1", _pms.Id, []));
        var unit = await _server.Post<TreeNodeDto>(Url("/units"), new CreateUnitRequest("SUPPLY1", _pms.Id, "PowerManagement"));
        Assert.Equal("unit", unit.Kind);

        var wrong = await _server.Client.PutAsJsonAsync(Url($"/units/{unit.Id}/roles/GEN1"), new UnitMemberRequest(cb.Id), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        (await _server.Client.PutAsJsonAsync(Url($"/units/{unit.Id}/roles/GEN1"), new UnitMemberRequest(gen.Id), Ct)).EnsureSuccessStatusCode();
        (await _server.Client.PutAsJsonAsync(Url("/layout"), new LayoutRequest([new LayoutItem(cb.Id, 300.4, 120)]), Ct)).EnsureSuccessStatusCode();

        var view = await _server.Get<ConfiguratorDto>(Url($"/configurator?folder={_pms.Id}"));
        var genView = view.ControlModules.Single(c => c.Name == "GEN1");
        Assert.Equal((unit.Id, "GEN1"), (genView.UnitId!.Value, genView.Role));
        Assert.True(genView.Interlocks.HasInterlocks);
        Assert.Contains("generator", genView.Description);
        Assert.Equal(new PositionDto(300, 120), view.ControlModules.Single(c => c.Name == "CB1").Position);
        var unitView = Assert.Single(view.Units);
        Assert.Equal((Guid?)gen.Id, unitView.Roles.Single(r => r.Role == "GEN1").ControlModuleId);
        Assert.Null(unitView.Roles.Single(r => r.Role == "CB_GEN1").ControlModuleId);

        await _server.DisposeAsync();
        _server = await ApiServer.StartAsync(_server.ProjectsRoot);
        var reloaded = await _server.Get<ConfiguratorDto>(Url($"/configurator?folder={_pms.Id}"));
        Assert.Equal(gen.Id, reloaded.Units.Single().Roles.Single(r => r.Role == "GEN1").ControlModuleId);
        Assert.Equal(new PositionDto(300, 120), reloaded.ControlModules.Single(c => c.Name == "CB1").Position);

        (await _server.Client.DeleteAsync(Url($"/objects/{gen.Id}"), Ct)).EnsureSuccessStatusCode();
        var afterDelete = await _server.Get<ConfiguratorDto>(Url($"/configurator?folder={_pms.Id}"));
        Assert.Null(afterDelete.Units.Single().Roles.Single(r => r.Role == "GEN1").ControlModuleId);
    }

    [Fact]
    public async Task ThePowerManagementExampleIsTheOnlyExampleAndHasItsInterlocks()
    {
        var examples = await _server.Get<List<Builder.Backend.Services.ExampleSummary>>("/api/examples");
        Assert.Equal("PowerManagement", Assert.Single(examples).File);
        var created = await _server.Post<ProjectDto>("/api/examples/PowerManagement", new { });
        var step = await _server.Post<System.Text.Json.JsonElement>($"/api/projects/{created.Id}/simulation/step", new { cycles = 2 });
        Assert.Empty(step.GetProperty("errors").EnumerateArray());
        var tree = await _server.Get<List<TreeNodeDto>>($"/api/projects/{created.Id}/tree");
        var pms = tree.Single(n => n.Name == "PMS");
        var shore = pms.Children.Single(n => n.Name == "SHORE_CB");
        var gen1 = pms.Children.Single(n => n.Name == "GEN1_PS");
        var breaker = gen1.Children.Single(n => n.Name == "GEN1_CB");
        var onBreaker = await _server.Get<ObjectInterlocksDto>($"/api/projects/{created.Id}/objects/{breaker.Id}/interlocks");
        Assert.Equal(["Engine running", "Breaker opened: engine not running", "Bus dead or GEN1 in sync", "Shore power not connected"], onBreaker.ActingOnThis.Select(a => a.Text));
        var onShore = await _server.Get<ObjectInterlocksDto>($"/api/projects/{created.Id}/objects/{shore.Id}/interlocks");
        Assert.Equal([("Generator breakers open", "blueprint"), ("Shore power available", "project"), ("Shore breaker opened: shore power lost", "project")],
            onShore.ActingOnThis.Select(a => (a.Text, a.Origin)));
        var onPms = await _server.Get<ObjectInterlocksDto>($"/api/projects/{created.Id}/objects/{pms.Id}/interlocks");
        Assert.Equal(2, onPms.Interlocks.Count);
        Assert.All(onPms.Interlocks, r => Assert.Null(r.Error));
        var again = await _server.Post<ProjectDto>("/api/examples/PowerManagement", new { });
        Assert.Equal("Power management (2)", again.Name);
    }

    [Fact]
    public async Task CommandInputsAreReadChangedAndReset()
    {
        var gen = await _server.Post<TreeNodeDto>(Url("/control-modules"), new CreateControlModuleRequest("GenSet", "GEN9", _pms.Id, []));
        var url = Url($"/control-modules/{gen.Id}/command-inputs");
        var loaded = await _server.Get<CommandInputsDto>(url);
        Assert.Equal("HMI", Assert.Single(loaded.Config.Rows).Name);
        Assert.True(loaded.HasPair);
        Assert.Contains("reset", loaded.SingleCommands);

        var config = new Builder.Core.Model.CommandInputConfig([
            new Builder.Core.Model.CommandInput("HMI", Builder.Core.Model.CommandSource.Hmi, Builder.Core.Model.InputKind.Pulse, On: 1, Off: 1, Commands: ["reset"]),
            new Builder.Core.Model.CommandInput("ECR", Builder.Core.Model.CommandSource.DigitalInput, Builder.Core.Model.InputKind.Button, On: 2, Off: 2, Debounce: 0.1)]);
        var put = await _server.Client.PutAsJsonAsync(url, config, ApiServer.Json, Ct);
        put.EnsureSuccessStatusCode();
        var tags = await _server.Get<List<TagDto>>(Url($"/tags?scope={gen.Id}"));
        Assert.Contains(tags, t => t.Path == "PMS.GEN9.FIN.ECR_on");
        Assert.Contains(tags, t => t.Path == "PMS.GEN9.PAR.ECR_debounce");

        var bad = config with { Rows = [.. config.Rows, new Builder.Core.Model.CommandInput("SW", Builder.Core.Model.CommandSource.DigitalInput, Builder.Core.Model.InputKind.Switch, On: 1, Off: 1)] };
        Assert.Equal(HttpStatusCode.BadRequest, (await _server.Client.PutAsJsonAsync(url, bad, ApiServer.Json, Ct)).StatusCode);

        var reset = await _server.Post<CommandInputsDto>($"{url}/reset", new { });
        Assert.Single(reset.Config.Rows);
        tags = await _server.Get<List<TagDto>>(Url($"/tags?scope={gen.Id}"));
        Assert.DoesNotContain(tags, t => t.Path == "PMS.GEN9.FIN.ECR_on");
    }

    [Fact]
    public async Task ThePmsWithEquipmentModulesChoosesTheSourceAndTheEmsConnect()
    {
        var created = await _server.Post<ProjectDto>("/api/examples/PowerManagement", new { });
        var sim = $"/api/projects/{created.Id}/simulation";
        async Task<System.Text.Json.JsonElement> Step(int cycles) => await _server.Post<System.Text.Json.JsonElement>($"{sim}/step", new { cycles });
        async Task Write(string tag, object value) => (await _server.Client.PostAsJsonAsync($"{sim}/write", new { tag, value }, Ct)).EnsureSuccessStatusCode();
        async Task Force(string tag, object value) => (await _server.Client.PostAsJsonAsync($"{sim}/force", new { tag, value }, Ct)).EnsureSuccessStatusCode();
        string StateOf(System.Text.Json.JsonElement state, string path) =>
            state.GetProperty("controlModules").EnumerateArray().Single(c => c.GetProperty("path").GetString() == path).GetProperty("stateName").GetString()!;
        async Task<string> Until(string path, string expected, int maxCycles)
        {
            var state = await Step(1);
            for (var i = 0; i < maxCycles && StateOf(state, path) != expected; i += 10)
                state = await Step(10);
            return StateOf(state, path);
        }

        var first = await Step(5);
        Assert.Empty(first.GetProperty("errors").EnumerateArray());
        Assert.Equal("Blackout", StateOf(first, "PMS"));
        Assert.Equal("Available", StateOf(first, "PMS.GEN1_PS"));
        await Write("PMS.GEN1_PS.GEN1.PAR.cooldown_time", 2);
        await Write("PMS.GEN2_PS.GEN2.PAR.cooldown_time", 2);
        await Write("PMS.GEN1_PS.CMD.set_auto", true);
        await Write("PMS.GEN2_PS.CMD.set_auto", true);
        await Step(3);
        await Write("PMS.CMD.HMI_set_auto", true);
        await Step(3);

        await Write("PMS.CMD.HMI_select_gen1", true);
        Assert.Equal("OnGEN1", await Until("PMS", "OnGEN1", 800));
        var onGen1 = await Step(1);
        Assert.Equal("Running", StateOf(onGen1, "PMS.GEN1_PS"));
        Assert.Equal("Closed", StateOf(onGen1, "PMS.GEN1_PS.GEN1_CB"));

        await Write("PMS.CMD.HMI_select_gen2", true);
        Assert.Equal("OnGEN2", await Until("PMS", "OnGEN2", 800));
        var onGen2 = await Step(1);
        Assert.Equal("Closed", StateOf(onGen2, "PMS.GEN2_PS.GEN2_CB"));
        Assert.NotEqual("Closed", StateOf(onGen2, "PMS.GEN1_PS.GEN1_CB"));
        Assert.Equal("Available", await Until("PMS.GEN1_PS", "Available", 800));

        await Force("PMS.GEN2_PS.GEN2.FIN.running", false);
        Assert.Equal("OnGEN1", await Until("PMS", "OnGEN1", 800));
        Assert.Equal("Shutdown", StateOf(await Step(1), "PMS.GEN2_PS"));

        async Task<bool> Tag(string path) => (await _server.Get<System.Text.Json.JsonElement>($"{sim}/tags")).EnumerateArray()
            .Single(t => t.GetProperty("path").GetString() == path).GetProperty("value").GetBoolean();
        Assert.True(await Tag("PMS.GEN2_PS.GEN2_CB.LOK.trip"));
        Assert.True(await Tag("PMS.GEN2_PS.ALM.EngineNotRunning.active"));
        await _server.Client.PostAsJsonAsync($"{sim}/unforce", new { tag = "PMS.GEN2_PS.GEN2.FIN.running" }, Ct);
        await Step(5);
        await Write("PMS.CMD.HMI_reset", true);
        await Step(5);
        Assert.False(await Tag("PMS.GEN2_PS.GEN2_CB.LOK.trip"));
        Assert.NotEqual("Shutdown", StateOf(await Step(1), "PMS.GEN2_PS"));

        await Force("PMS.FIN.shore_available", true);
        Assert.Equal("OnShore", await Until("PMS", "OnShore", 800));
        var shore = await Step(1);
        Assert.Equal("Closed", StateOf(shore, "PMS.SHORE_CB"));
        Assert.NotEqual("Closed", StateOf(shore, "PMS.GEN1_PS.GEN1_CB"));

        await Force("PMS.FIN.shore_available", false);
        await Step(2);
        Assert.True(await Tag("PMS.ALM.ShorePowerLost.active"));
        Assert.NotEqual("Closed", StateOf(await Step(1), "PMS.SHORE_CB"));
    }

    [Fact]
    public async Task TheScadaExportCarriesObjectsAndHmiCommandTags()
    {
        var created = await _server.Post<ProjectDto>("/api/examples/PowerManagement", new { });
        var json = await _server.Client.GetStringAsync($"/api/projects/{created.Id}/export/scada", Ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("apolloiq.scada/2", root.GetProperty("schema").GetString());
        var objects = root.GetProperty("objects").EnumerateArray().ToDictionary(o => o.GetProperty("path").GetString()!);
        Assert.Equal("unit", objects["PMS"].GetProperty("kind").GetString());
        var em = objects["PMS.GEN1_PS"];
        Assert.Equal(("equipmentModule", "PMS", "GEN1"), (em.GetProperty("kind").GetString(), em.GetProperty("parent").GetString(), em.GetProperty("role").GetString()));
        Assert.False(em.GetProperty("commands").TryGetProperty("set_on", out _), "PMS drives the EM; its HMI row only resets");
        Assert.Equal("CMD.HMI_reset", em.GetProperty("commands").GetProperty("reset").GetString());
        Assert.Equal("CMD.set_auto", em.GetProperty("commands").GetProperty("set_auto").GetString());
        Assert.Equal(("PMS.STS.auto", true), (em.GetProperty("commandLock").GetProperty("auto").GetProperty("name").GetString(), em.GetProperty("commandLock").GetProperty("lockedInAuto").GetBoolean()));
        Assert.Equal("PMS.GEN1_PS.STS.auto", objects["PMS.GEN1_PS.GEN1_CB"].GetProperty("commandLock").GetProperty("auto").GetProperty("name").GetString());
        Assert.False(objects["PMS"].TryGetProperty("commandLock", out _));
        Assert.Equal("CMD.HMI_select_gen1", objects["PMS"].GetProperty("commands").GetProperty("select_gen1").GetString());
        Assert.Empty(objects["PMS.GEN1_PS.GEN1_PM"].GetProperty("commands").EnumerateObject());
        Assert.Contains(objects["PMS.GEN1_PS.GEN1_CB"].GetProperty("states").EnumerateArray(), s => s.GetProperty("name").GetString() == "Closed" && s.GetProperty("code").GetInt32() == 400);
        Assert.Equal(root.GetProperty("tags").GetArrayLength(), (await _server.Get<List<TagDto>>($"/api/projects/{created.Id}/tags")).Count);
        Assert.False(root.TryGetProperty("interlocks", out _));
        var breaker = objects["PMS.GEN1_PS.GEN1_CB"].GetProperty("interlocks");
        Assert.Equal(3, breaker.GetProperty("switchOn").GetArrayLength());
        Assert.Equal("PMS.GEN1_PS.GEN1_CB.LOK.can_on", breaker.GetProperty("canOn").GetProperty("name").GetString());
    }

    [Fact]
    public async Task AProjectInterlockOnAnEquipmentModuleBlocksItsStartAndIsExported()
    {
        var created = await _server.Post<ProjectDto>("/api/examples/PowerManagement", new { });
        var url = $"/api/projects/{created.Id}";
        var tree = await _server.Get<List<TreeNodeDto>>($"{url}/tree");
        var pms = tree.Single(n => n.Name == "PMS");
        var em = pms.Children.Single(n => n.Name == "GEN1_PS");
        var bad = await _server.Client.PostAsJsonAsync($"{url}/objects/{pms.Id}/interlocks/validate", new ValidateConditionRequest("[PMS.SHORE_CB.FIN.nope]"), Ct);
        Assert.NotNull((await bad.Content.ReadFromJsonAsync<ValidateConditionResponse>(Ct))!.Error);
        var set = await (await _server.Client.PutAsJsonAsync($"{url}/objects/{pms.Id}/interlocks",
                new SetInterlocksRequest([new InterlockRuleRequest(em.Id, "SwitchOn", "[PMS.SHORE_CB.is_closed]", "Shore breaker closed", null, null, null)]), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ObjectInterlocksDto>(Ct);
        var rule = Assert.Single(set!.Interlocks);
        Assert.Equal(("PMS.GEN1_PS", "[PMS.SHORE_CB.is_closed]", null), (rule.TargetPath, rule.Condition, rule.Error));
        Assert.Contains(set.Targets, t => t.Path == "PMS.GEN1_PS.GEN1_CB" && t.HasInterlocks);

        await _server.DisposeAsync();
        _server = await ApiServer.StartAsync(_server.ProjectsRoot);
        var reloaded = await _server.Get<ObjectInterlocksDto>($"{url}/objects/{em.Id}/interlocks");
        Assert.Contains(reloaded.ActingOnThis, a => a is { Text: "Shore breaker closed", DefinedBy: "PMS", Origin: "project" });

        var sim = $"{url}/simulation";
        async Task<System.Text.Json.JsonElement> Step(int cycles) => await _server.Post<System.Text.Json.JsonElement>($"{sim}/step", new { cycles });
        async Task Write(string tag, object value) => (await _server.Client.PostAsJsonAsync($"{sim}/write", new { tag, value }, Ct)).EnsureSuccessStatusCode();
        string StateOf(System.Text.Json.JsonElement state, string path) =>
            state.GetProperty("controlModules").EnumerateArray().Single(c => c.GetProperty("path").GetString() == path).GetProperty("stateName").GetString()!;
        await _server.Post<System.Text.Json.JsonElement>($"{url}/control-modules/{em.Id}/command-inputs/reset", new { });
        Assert.Empty((await Step(5)).GetProperty("errors").EnumerateArray());
        await Write("PMS.GEN1_PS.CMD.HMI_on", true);
        Assert.Equal("Available", StateOf(await Step(20), "PMS.GEN1_PS"));

        var json = await _server.Client.GetStringAsync($"{url}/export/scada", Ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var objects = doc.RootElement.GetProperty("objects").EnumerateArray().ToList();
        var lines = objects.Single(o => o.GetProperty("path").GetString() == "PMS.GEN1_PS").GetProperty("interlocks").GetProperty("switchOn");
        Assert.Equal("Shore breaker closed", lines[0].GetProperty("text").GetString());
        var breaker = objects.Single(o => o.GetProperty("path").GetString() == "PMS.GEN1_PS.GEN1_CB").GetProperty("interlocks");
        Assert.Equal(["Engine running", "Bus dead or GEN1 in sync", "Shore power not connected"],
            breaker.GetProperty("switchOn").EnumerateArray().Select(l => l.GetProperty("text").GetString()));
        var trip = Assert.Single(breaker.GetProperty("trips").EnumerateArray());
        Assert.Equal(("EngineNotRunning", "PMS.GEN1_PS", "EM"), (trip.GetProperty("alarm").GetString(), trip.GetProperty("definedBy").GetString(), trip.GetProperty("escalate").GetString()));
        Assert.Contains(objects.Single(o => o.GetProperty("path").GetString() == "PMS.GEN1_PS").GetProperty("alarms").EnumerateArray(),
            a => a.GetProperty("name").GetString() == "EngineNotRunning");
    }
}
