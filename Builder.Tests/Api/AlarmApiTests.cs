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
        _gen = await _server.Post<TreeNodeDto>($"/api/projects/{_project}/control-modules", new CreateControlModuleRequest(Fixtures.Light, "GEN1", null));
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
        Assert.Equal(["LampFailure", "Overcurrent", "SlowSwitch", "CurrentWhileOff", "DoesNotSwitchOn"], alarms.Select(a => a.Name));
        var timeout = alarms.Single(a => a.Name == "DoesNotSwitchOn");
        Assert.Equal((20, "Alarm", true, "GEN1.ALM.DoesNotSwitchOn.active", "StateTimeout"), (timeout.Priority, timeout.Level, timeout.PlcReactive, timeout.ActiveTag, timeout.Source));
        Assert.Equal("GEN1: Turning on took too long", timeout.Message);
        var failure = alarms.Single(a => a.Name == "LampFailure");
        Assert.Equal((false, (string?)null, "Warning"), (failure.PlcReactive, failure.ActiveTag, failure.Level));
    }

    [Fact]
    public async Task PriorityCanBeChangedPerInstanceAndReset()
    {
        var response = await _server.Client.PutAsJsonAsync(Url($"/control-modules/{_gen.Id}/alarms/LampFailure"), new PriorityRequest(22), Ct);
        response.EnsureSuccessStatusCode();
        var alarm = (await response.Content.ReadFromJsonAsync<List<AlarmDto>>(ApiServer.Json, Ct))!.Single(a => a.Name == "LampFailure");
        Assert.Equal((22, 10, "Alarm"), (alarm.Priority, alarm.DefaultPriority, alarm.Level));

        var stored = ProjectStore.Load(Directory.GetDirectories(_server.ProjectsRoot).Single(d => File.Exists(Path.Combine(d, ProjectStore.ProjectFileName)))).Project;
        Assert.Equal(22, stored.Get<ControlModule>(_gen.Id).AlarmPriorities["LampFailure"]);

        var reset = await _server.Client.PutAsJsonAsync(Url($"/control-modules/{_gen.Id}/alarms/LampFailure"), new PriorityRequest(null), Ct);
        Assert.Equal(10, (await reset.Content.ReadFromJsonAsync<List<AlarmDto>>(ApiServer.Json, Ct))!.Single(a => a.Name == "LampFailure").Priority);

        var bad = await _server.Client.PutAsJsonAsync(Url($"/control-modules/{_gen.Id}/alarms/LampFailure"), new PriorityRequest(31), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var missing = await _server.Client.PutAsJsonAsync(Url($"/control-modules/{_gen.Id}/alarms/Nope"), new PriorityRequest(1), Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task WiresAreSetByPathAndListed()
    {
        await _server.Post<TreeNodeDto>($"/api/projects/{_project}/control-modules", new CreateControlModuleRequest(Fixtures.PushButton, "BTN", null));
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
}
