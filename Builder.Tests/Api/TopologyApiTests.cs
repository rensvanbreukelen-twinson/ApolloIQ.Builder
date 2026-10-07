using System.Net;
using System.Net.Http.Json;
using Builder.Backend.Contracts;
using Builder.Backend.Endpoints;
using Xunit;

namespace Builder.Tests.Api;

public sealed class TopologyApiTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiServer _server = null!;
    private Guid _project;
    private TreeNodeDto _gen = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await ApiServer.StartAsync();
        _project = (await _server.Post<ProjectDto>("/api/projects", new CreateProjectRequest("Topology"))).Id;
        _gen = await _server.Post<TreeNodeDto>($"/api/projects/{_project}/control-modules", new CreateControlModuleRequest("GenSet", "GEN1", null, []));
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _server.DeleteData();
    }

    private string Url(string path) => $"/api/projects/{_project}{path}";

    private async Task<T> Put<T>(string url, object body)
    {
        var response = await _server.Client.PutAsJsonAsync(url, body, ApiServer.Json, Ct);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == HttpStatusCode.NoContent ? default! : (await response.Content.ReadFromJsonAsync<T>(ApiServer.Json, Ct))!;
    }

    [Fact]
    public async Task TopologyDeploymentAndOriginsDriveTheBindingReport()
    {
        var topology = await Put<TopologyDto>(Url("/topology"), new TopologyDto(
            [new DeviceDto(null, "PLC1", "Plc", null), new DeviceDto(null, "SCADA", "Scada", null), new DeviceDto(null, "CTRL", "ThirdParty", "CAT controller")],
            [new LinkDto(null, "SCADA", "PLC1", "OPC UA", "Monitoring"), new LinkDto(null, "SCADA", "CTRL", "Modbus TCP", "Monitoring")]));
        var plc = topology.Devices.Single(d => d.Name == "PLC1").Id!.Value;
        var ctrl = topology.Devices.Single(d => d.Name == "CTRL").Id!.Value;
        Assert.Equal(plc.ToString(), topology.Links[0].To);

        await Put<object>(Url($"/objects/{_gen.Id}/device"), new DeviceRequest(plc));
        var deployment = await _server.Get<List<DeploymentItemDto>>(Url("/deployment"));
        var gen = Assert.Single(deployment);
        Assert.Equal(plc, gen.DeviceId);
        var running = gen.Inputs.Single(i => i.Tag == "GEN1.FIN.running");
        await Put<object>(Url($"/tags/{running.TagId}/origin"), new OriginRequest(ctrl, "Modbus TCP", "40021"));

        var report = await _server.Get<BindingDto>(Url("/binding"));
        Assert.Contains(report.Issues, i => i.Code == "safety" && i.Subject == "GEN1.FIN.running");
        var access = report.Accesses.Single(a => a.Tag == "GEN1.FIN.running" && a.Critical);
        Assert.Equal(("Relayed", "PLC1 → SCADA → CTRL"), (access.Kind, access.Path));

        await Put<TopologyDto>(Url("/topology"), new TopologyDto(topology.Devices,
            [.. topology.Links, new LinkDto(null, "PLC1", "CTRL", "Modbus TCP", "Control")]));
        report = await _server.Get<BindingDto>(Url("/binding"));
        Assert.Equal(0, report.Errors);
        Assert.Equal("40021", report.Accesses.Single(a => a.Tag == "GEN1.FIN.running" && a.Critical).Address);

        var scada = topology.Devices.Single(d => d.Name == "SCADA").Id!.Value;
        var refused = await _server.Client.PutAsJsonAsync(Url($"/objects/{_gen.Id}/device"), new DeviceRequest(scada), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task SimulationListsAndCutsLinks()
    {
        var topology = await Put<TopologyDto>(Url("/topology"), new TopologyDto(
            [new DeviceDto(null, "PLC1", "Plc", null), new DeviceDto(null, "CTRL", "ThirdParty", null)],
            [new LinkDto(null, "PLC1", "CTRL", "Modbus TCP", "Control")]));
        await Put<object>(Url($"/objects/{_gen.Id}/device"), new DeviceRequest(topology.Devices[0].Id));
        var deployment = await _server.Get<List<DeploymentItemDto>>(Url("/deployment"));
        foreach (var input in deployment[0].Inputs)
            await Put<object>(Url($"/tags/{input.TagId}/origin"), new OriginRequest(topology.Devices[1].Id, "Modbus TCP", input.Tag));

        var links = await _server.Get<List<SimLinkView>>(Url("/simulation/links"));
        var link = Assert.Single(links);
        var cut = await _server.Post<List<SimLinkView>>(Url($"/simulation/links/{link.Id}"), new { down = true });
        Assert.True(cut[0].Down);
        await _server.Post<object>(Url("/simulation/step"), new { cycles = 2 });
        var sim = await _server.Get<SimulationApiTests.SimulationView>(Url("/simulation"));
        Assert.Equal(999, sim.ControlModules.Single().State);
    }

    public sealed record SimLinkView(Guid Id, string From, string To, bool Down, int Tags);
}
