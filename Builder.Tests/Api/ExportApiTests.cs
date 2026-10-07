using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApolloIQ.Core.Exchange;
using Builder.Backend.Contracts;
using Builder.Backend.Endpoints;
using Builder.Logic.Blueprints;
using Builder.Persistence.Export;
using Xunit;

namespace Builder.Tests.Api;

public sealed class ExportApiTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiServer _server = null!;
    private Guid _project;

    public async ValueTask InitializeAsync()
    {
        _server = await ApiServer.StartAsync();
        _project = (await _server.Post<ProjectDto>("/api/projects", new CreateProjectRequest("Deck lights"))).Id;
        var deck = await _server.Post<TreeNodeDto>($"/api/projects/{_project}/folders", new CreateFolderRequest("DECK", null));
        await _server.Post<TreeNodeDto>($"/api/projects/{_project}/control-modules", new CreateControlModuleRequest(Fixtures.Light, "L1", deck.Id));
        await _server.Post<TreeNodeDto>($"/api/projects/{_project}/control-modules", new CreateControlModuleRequest(Fixtures.CircuitBreaker, "CB1", deck.Id));
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _server.DeleteData();
    }

    private string Url => $"/api/projects/{_project}/export/scada";

    private string ProjectDirectory => Path.Combine(_server.ProjectsRoot, _project.ToString("D"));

    [Fact]
    public async Task TheCheckListsTheBlueprintsAndTheDownloadIsAnExchangeFile()
    {
        var check = await _server.Get<ExportCheckDto>($"{Url}/check");
        Assert.Empty(check.Errors);
        Assert.Equal("Deck lights.apolloiq.json", check.FileName);
        Assert.Equal([("CircuitBreaker", "0.1.0", false), ("Light", "0.1.0", false)], check.Blueprints.Select(b => (b.Name, b.Version, b.ChangedSinceLastExport)));
        Assert.False(File.Exists(Path.Combine(ProjectDirectory, ExportRecord.FileName)));

        var response = await _server.Client.PostAsync(Url, null, Ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Deck lights.apolloiq.json", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var file = ExchangeJson.Read(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal((_project, "Deck lights"), (file.Source.ProjectId, file.Source.ProjectName));
        Assert.Equal(["CB1", "L1"], file.Instances.Select(i => i.Name));

        var record = ExportRecord.Load(ProjectDirectory);
        Assert.Equal(file.Blueprints.Select(b => (b.Id, b.Hash)).OrderBy(b => b.Id), record.Blueprints.Select(b => (b.Key, b.Value.Hash)).OrderBy(b => b.Key));
    }

    [Fact]
    public async Task AChangedBlueprintNeedsANewVersionBeforeTheNextExport()
    {
        (await _server.Client.PostAsync(Url, null, Ct)).EnsureSuccessStatusCode();

        var light = (await _server.Get<JsonElement>($"/api/blueprints/{Fixtures.Light}")).GetProperty("blueprint").Deserialize<Blueprint>(ApiServer.Json)!;
        light.Description = "Deck light with current feedback";
        (await _server.Client.PutAsJsonAsync($"/api/blueprints/{light.Id}", light, ApiServer.Json, Ct)).EnsureSuccessStatusCode();

        var check = await _server.Get<ExportCheckDto>($"{Url}/check");
        Assert.Equal("Light changed since the last export to SCADA; raise its version (now 0.1.0).", Assert.Single(check.Errors));
        Assert.True(check.Blueprints.Single(b => b.Name == "Light").ChangedSinceLastExport);
        var refused = await _server.Client.PostAsync(Url, null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Contains("raise its version", body.GetProperty("error").GetString());
        Assert.Single(body.GetProperty("errors").EnumerateArray());

        light.Version = light.Version.NextMajor();
        (await _server.Client.PutAsJsonAsync($"/api/blueprints/{light.Id}", light, ApiServer.Json, Ct)).EnsureSuccessStatusCode();
        check = await _server.Get<ExportCheckDto>($"{Url}/check");
        Assert.Empty(check.Errors);
        Assert.Equal(("0.2.0", "0.1.0"), check.Blueprints.Where(b => b.Name == "Light").Select(b => (b.Version, b.LastExportedVersion)).Single());
        Assert.Contains(check.Warnings, w => w.Contains("was made from Light 0.1.0; the export carries 0.2.0"));
        (await _server.Client.PostAsync(Url, null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal("0.2.0", ExportRecord.Load(ProjectDirectory).Blueprints[Fixtures.Light].Version.ToString());
        Assert.DoesNotContain((await _server.Get<ExportCheckDto>($"{Url}/check")).Blueprints, b => b.ChangedSinceLastExport);
    }
}
