using System.Net;
using System.Net.Http.Json;
using Builder.Backend.Contracts;
using Xunit;

namespace Builder.Tests.Api;

public sealed class SimulationApiTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiServer _server = null!;
    private Guid _project;
    private TreeNodeDto _breaker = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await ApiServer.StartAsync();
        _project = (await _server.Post<ProjectDto>("/api/projects", new CreateProjectRequest("Sim"))).Id;
        var pms = await _server.Post<TreeNodeDto>($"/api/projects/{_project}/folders", new CreateFolderRequest("PMS", null));
        await _server.Post<TreeNodeDto>($"/api/projects/{_project}/control-modules", new CreateControlModuleRequest(Fixtures.Light, "GEN1", pms.Id));
        _breaker = await _server.Post<TreeNodeDto>($"/api/projects/{_project}/control-modules",
            new CreateControlModuleRequest(Fixtures.CircuitBreaker, "GEN1_CB", pms.Id));
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _server.DeleteData();
    }

    private string Url(string path = "") => $"/api/projects/{_project}/simulation{path}";

    private Task<SimulationView> Step(int cycles) => _server.Post<SimulationView>(Url("/step"), new { cycles });

    private static int State(SimulationView view, string path) => view.ControlModules.Single(c => c.Path == path).State;

    public sealed record ControlModuleView(string Path, string Type, int State, string StateName);

    public sealed record StatusView(string Status, long Cycle, double TimeSeconds, double Speed);

    public sealed record SimulationView(StatusView Status, List<string> Errors, List<ControlModuleView> ControlModules);

    public sealed record TagView(string Path, string SymbolKey, string Group, string DataType, object? Value, bool Good, bool Forced);

    [Fact]
    public async Task SimulationStartsPausedWithTheProjectCms()
    {
        var view = await _server.Get<SimulationView>(Url());
        Assert.Equal("Paused", view.Status.Status);
        Assert.Empty(view.Errors);
        Assert.Equal(["PMS.GEN1", "PMS.GEN1_CB"], view.ControlModules.Select(c => c.Path).Order());
    }

    [Fact]
    public async Task CommandsDriveTheLogic()
    {
        var view = await Step(4);
        Assert.Equal(200, State(view, "PMS.GEN1_CB"));
        var written = await _server.Post<TagView>(Url("/write"), new { tag = "PMS.GEN1_CB.CMD.HMI_on", value = true });
        Assert.Equal("True", written.Value?.ToString());
        view = await Step(1);
        Assert.Equal(300, State(view, "PMS.GEN1_CB"));
        Assert.Equal("Closing", view.ControlModules.Single(c => c.Path == "PMS.GEN1_CB").StateName);
    }

    [Fact]
    public async Task WritingALogicOutputIsRejected()
    {
        var response = await _server.Client.PostAsJsonAsync(Url("/write"), new { tag = "PMS.GEN1_CB.OUT.coil_on", value = true }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ApiError>(ApiServer.Json, Ct))!;
        Assert.Equal("simulation", error.Code);
        Assert.Contains("Force", error.Message);
    }

    [Fact]
    public async Task ForcingAndBadQualityShowInTheTagList()
    {
        await _server.Post<TagView>(Url("/force"), new { tag = "PMS.GEN1.FIN.current", value = 97.5 });
        await _server.Post<TagView>(Url("/quality"), new { tag = "PMS.GEN1_CB.FIN.feedback", bad = true });
        await Step(1);
        var tags = await _server.Get<List<TagView>>(Url("/tags"));
        var current = tags.Single(t => t.Path == "PMS.GEN1.FIN.current");
        Assert.True(current.Forced);
        Assert.Equal("97.5", current.Value?.ToString());
        Assert.False(tags.Single(t => t.Path == "PMS.GEN1_CB.FIN.feedback").Good);
        Assert.Equal("FIN", current.Group);

        var released = await _server.Post<TagView>(Url("/unforce"), new { tag = "PMS.GEN1.FIN.current" });
        Assert.False(released.Forced);
        var response = await _server.Client.PostAsync(Url("/unforce-all"), null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ProjectChangesReloadTheSimulation()
    {
        await Step(4);
        await _server.Post<TagView>(Url("/write"), new { tag = "PMS.GEN1_CB.CMD.HMI_on", value = true });
        await Step(3);
        var rename = await _server.Client.PatchAsJsonAsync($"/api/projects/{_project}/objects/{_breaker.Id}", new RenameRequest("GEN_CB"), Ct);
        rename.EnsureSuccessStatusCode();
        var view = await _server.Get<SimulationView>(Url());
        Assert.Equal(400, State(view, "PMS.GEN_CB"));
        Assert.Equal(7, view.Status.Cycle);
    }

    [Fact]
    public async Task RunAndPause()
    {
        var speed = await _server.Client.PutAsJsonAsync(Url("/speed"), new { speed = 10 }, Ct);
        speed.EnsureSuccessStatusCode();
        var running = await _server.Post<SimulationView>(Url("/start"), new { });
        Assert.Equal("Running", running.Status.Status);
        var step = await _server.Client.PostAsJsonAsync(Url("/step"), new { cycles = 1 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, step.StatusCode);
        await Task.Delay(200, Ct);
        var paused = await _server.Post<SimulationView>(Url("/pause"), new { });
        Assert.Equal("Paused", paused.Status.Status);
        Assert.True(paused.Status.Cycle > 5);

        var reset = await _server.Post<SimulationView>(Url("/reset"), new { });
        Assert.Equal(0, reset.Status.Cycle);
    }

    [Fact]
    public async Task AlarmsAreListedWithTheirOriginAndState()
    {
        await Step(2);
        await _server.Post<TagView>(Url("/write"), new { tag = "PMS.GEN1.FIN.current", value = 4.5 });
        await Step(1);
        var alarms = await _server.Get<List<System.Text.Json.JsonElement>>(Url("/alarms"));
        var overcurrent = alarms.Single(a => a.GetProperty("object").GetString() == "PMS.GEN1" && a.GetProperty("name").GetString() == "Overcurrent");
        Assert.True(overcurrent.GetProperty("active").GetBoolean());
        Assert.False(overcurrent.GetProperty("plcReactive").GetBoolean());
        Assert.Equal("Warning", overcurrent.GetProperty("rangeLevel").GetString());
        Assert.Equal("GEN1: lamp current high", overcurrent.GetProperty("message").GetString());
        var latched = alarms.Single(a => a.GetProperty("name").GetString() == "CurrentWhileOff");
        Assert.True(latched.GetProperty("plcReactive").GetBoolean());
        Assert.True(latched.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task UnknownTagIsABadRequest()
    {
        var response = await _server.Client.PostAsJsonAsync(Url("/write"), new { tag = "PMS.NOPE.CMD.set_on", value = true }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    public sealed record ScenarioRunView(List<ScenarioResultView> Results, List<CoverageView> Coverage, int Passed, int Failed);

    public sealed record ScenarioResultView(string File, string Type, string Name, bool Passed, List<object> Trace);

    public sealed record CoverageView(string Type, int Covered, int Total);

    [Fact]
    public async Task LibraryScenariosRunThroughTheApi()
    {
        var report = await _server.Post<ScenarioRunView>("/api/scenarios/run", new { });
        Assert.Equal(0, report.Failed);
        Assert.All(report.Results, r => Assert.Empty(r.Trace));
        Assert.All(report.Coverage, c => Assert.Equal(c.Total, c.Covered));

        var first = report.Results[0];
        var traced = await _server.Post<ScenarioResultView>("/api/scenarios/trace", new { file = first.File, name = first.Name });
        Assert.NotEmpty(traced.Trace);
        var missing = await _server.Client.PostAsJsonAsync("/api/scenarios/trace", new { file = first.File, name = "nope" }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
