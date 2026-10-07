using System.Net;
using System.Net.Http.Json;
using Builder.Backend.Contracts;
using Builder.Persistence;
using Builder.Core.Model;
using Xunit;

namespace Builder.Tests.Api;

public sealed class AlarmApiTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiServer _server = null!;
    private Guid _project;
    private TreeNodeDto _gen = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await ApiServer.StartAsync();
        _project = (await _server.Post<ProjectDto>("/api/projects", new CreateProjectRequest("Alarms"))).Id;
        _gen = await _server.Post<TreeNodeDto>($"/api/projects/{_project}/control-modules", new CreateControlModuleRequest("GenSet", "GEN1", null, []));
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _server.DeleteData();
    }

    private string Url(string path) => $"/api/projects/{_project}{path}";

    [Fact]
    public async Task AlarmsListTheDefinitionsOfTheInstance()
    {
        var alarms = await _server.Get<List<AlarmDto>>(Url($"/control-modules/{_gen.Id}/alarms"));
        Assert.Equal(19, alarms.Count);
        var shutdown = alarms.Single(a => a.Name == "EngineShutdown");
        Assert.Equal((30, "Alarm", "PLC", "GEN1.ALM.EngineShutdown.active"), (shutdown.Severity, shutdown.Band, shutdown.RunsOn, shutdown.ActiveTag));
        Assert.Equal("Engine shutdown", shutdown.Message["en"]);
        Assert.Null(alarms.Single(a => a.Name == "ServiceDue").ActiveTag);
    }

    [Fact]
    public async Task SeverityCanBeChangedPerInstanceAndReset()
    {
        var response = await _server.Client.PutAsJsonAsync(Url($"/control-modules/{_gen.Id}/alarms/NotInAuto"), new SeverityRequest(12), Ct);
        response.EnsureSuccessStatusCode();
        var alarm = (await response.Content.ReadFromJsonAsync<List<AlarmDto>>(ApiServer.Json, Ct))!.Single(a => a.Name == "NotInAuto");
        Assert.Equal((12, 5, "Warning"), (alarm.Severity, alarm.DefaultSeverity, alarm.Band));

        var stored = ProjectStore.Load(Directory.GetDirectories(_server.ProjectsRoot).Single()).Project;
        Assert.Equal(12, stored.Get<ControlModule>(_gen.Id).AlarmSeverities["NotInAuto"]);

        var reset = await _server.Client.PutAsJsonAsync(Url($"/control-modules/{_gen.Id}/alarms/NotInAuto"), new SeverityRequest(null), Ct);
        Assert.Equal(5, (await reset.Content.ReadFromJsonAsync<List<AlarmDto>>(ApiServer.Json, Ct))!.Single(a => a.Name == "NotInAuto").Severity);

        var bad = await _server.Client.PutAsJsonAsync(Url($"/control-modules/{_gen.Id}/alarms/NotInAuto"), new SeverityRequest(31), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var missing = await _server.Client.PutAsJsonAsync(Url($"/control-modules/{_gen.Id}/alarms/Nope"), new SeverityRequest(1), Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task WiresAreSetByPathAndListed()
    {
        await _server.Post<TreeNodeDto>($"/api/projects/{_project}/control-modules", new CreateControlModuleRequest("PushButton", "BTN", null, []));
        var response = await _server.Client.PutAsJsonAsync(Url($"/control-modules/{_gen.Id}/wires"),
            new SetWiresRequest([new WireRequest("BTN.INT.pressed", "toggle", null)]), Ct);
        response.EnsureSuccessStatusCode();
        var wires = await _server.Get<List<WireDto>>(Url($"/control-modules/{_gen.Id}/wires"));
        var wire = Assert.Single(wires);
        Assert.Equal(("BTN.INT.pressed", "Toggle"), (wire.Source, wire.Mode));

        var bad = await _server.Client.PutAsJsonAsync(Url($"/control-modules/{_gen.Id}/wires"),
            new SetWiresRequest([new WireRequest("BTN.INT.nothing", "On", null)]), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task PicRowsCreateTheirCommandTags()
    {
        var pic = await _server.Post<TreeNodeDto>($"/api/projects/{_project}/control-modules", new CreateControlModuleRequest("PriorityInputControl", "LIFT", null, []));
        var request = new PicRequest("Up", "Down", [new PicRowRequest("WH_buttons", "Hardwired", "Hold", 1, 1, null), new PicRowRequest("ECR_hmi", "Hmi", "Hold", 3, null, "Ignore")]);
        var response = await _server.Client.PutAsJsonAsync(Url($"/control-modules/{pic.Id}/pic"), request, Ct);
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<PicDto>(ApiServer.Json, Ct))!;
        Assert.Equal("LIFT.CMD.ECR_hmi_on", dto.Rows[1].OnTag);
        Assert.Null(dto.Rows[1].OffTag);

        var tags = await _server.Get<List<TagDto>>($"/api/projects/{_project}/tags?scope={pic.Id}&group=CMD");
        Assert.Equal(["LIFT.CMD.auto_active", "LIFT.CMD.ECR_hmi_on", "LIFT.CMD.WH_buttons_off", "LIFT.CMD.WH_buttons_on"], tags.Select(t => t.Path));

        (await _server.Client.PutAsJsonAsync(Url($"/control-modules/{pic.Id}/pic"), new PicRequest("Up", "Down", [request.Rows[0]]), Ct)).EnsureSuccessStatusCode();
        tags = await _server.Get<List<TagDto>>($"/api/projects/{_project}/tags?scope={pic.Id}&group=CMD");
        Assert.Equal(3, tags.Count);

        var stored = ProjectStore.Load(Directory.GetDirectories(_server.ProjectsRoot).Single()).Project;
        Assert.Equal("WH_buttons", Assert.Single(stored.Get<ControlModule>(pic.Id).Pic!.Rows).Name);

        var duplicate = await _server.Client.PutAsJsonAsync(Url($"/control-modules/{pic.Id}/pic"),
            new PicRequest("Up", "Down", [request.Rows[0], request.Rows[0]]), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        var notPic = await _server.Client.GetAsync(Url($"/control-modules/{_gen.Id}/pic"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, notPic.StatusCode);
    }
}
