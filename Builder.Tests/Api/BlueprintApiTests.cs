using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Builder.Logic.Blueprints;
using Xunit;

namespace Builder.Tests.Api;

public class BlueprintApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Result(Blueprint Blueprint, List<BlueprintIssue> Issues);

    private sealed record Summary(Guid Id, string Name, string Kind, string Version, int Errors, int Warnings);

    [Fact]
    public async Task BlueprintsAreCreatedListedRenamedAndDeletedById()
    {
        await using var server = await ApiServer.StartAsync(fixtures: false);
        try
        {
            var light = Fixtures.Load("Light");
            light.Id = Guid.Empty;
            light.Tags.ForEach(t => t.Id = Guid.Empty);
            light.Tags.Add(new BlueprintTag { Group = "PAR", Name = "spare", DataType = "Real" });
            var created = await server.Client.PostAsJsonAsync("/api/blueprints", light, ApiServer.Json, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var saved = (await created.Content.ReadFromJsonAsync<Result>(ApiServer.Json, Ct))!.Blueprint;
            Assert.NotEqual(Guid.Empty, saved.Id);
            Assert.DoesNotContain(saved.Tags, t => t.Id == Guid.Empty);
            Assert.True(File.Exists(Path.Combine(server.ProjectsRoot, "blueprints", "Light.blueprint.json")));
            var file = await File.ReadAllTextAsync(Path.Combine(server.ProjectsRoot, "blueprints", "Light.blueprint.json"), Ct);
            Assert.Contains("\"version\": \"0.1.0\"", file);
            Assert.Contains("\"plcReactive\": true", file);

            var list = await server.Get<List<Summary>>("/api/blueprints");
            Assert.Equal(("Light", 0, saved.Id, "0.1.0"), (list.Single().Name, list.Single().Errors, list.Single().Id, list.Single().Version));
            Assert.Single(await server.Get<List<JsonElement>>("/api/library/types"));

            saved.Tags[0].Source = null;
            var checkedResult = await server.Post<Result>("/api/blueprints/validate", saved);
            Assert.Contains(checkedResult.Issues, i => i.Severity == "Error");

            saved.Tags[0].Source = InputSource.LocalIO;
            var tagId = saved.Tags[^1].Id;
            saved.Name = "DeckLight";
            saved.Tags[^1].Name = "reserve";
            saved.Version = saved.Version.NextMinor();
            (await server.Client.PutAsJsonAsync($"/api/blueprints/{saved.Id}", saved, ApiServer.Json, Ct)).EnsureSuccessStatusCode();
            var loaded = await server.Get<Result>($"/api/blueprints/{saved.Id}");
            Assert.Equal(("DeckLight", saved.Id, tagId, "0.1.1"), (loaded.Blueprint.Name, loaded.Blueprint.Id, loaded.Blueprint.Tags.Single(t => t.Name == "reserve").Id, loaded.Blueprint.Version.ToString()));
            Assert.False(File.Exists(Path.Combine(server.ProjectsRoot, "blueprints", "Light.blueprint.json")));
            Assert.Equal("DeckLight", Assert.Single(await server.Get<List<JsonElement>>("/api/library/types")).GetProperty("name").GetString());

            var clash = Fixtures.Load("PushButton");
            clash.Name = "DeckLight";
            Assert.Equal(HttpStatusCode.Conflict, (await server.Client.PostAsJsonAsync("/api/blueprints", clash, ApiServer.Json, Ct)).StatusCode);

            var catalog = await server.Get<JsonElement>("/api/blueprints/catalog");
            Assert.Equal(6, catalog.GetProperty("interfaces").GetArrayLength());
            Assert.Contains(catalog.GetProperty("interfaces").EnumerateArray(), i => i.GetProperty("name").GetString() == "Container" && !i.GetProperty("inScada").GetBoolean());

            Assert.Equal(HttpStatusCode.NoContent, (await server.Client.DeleteAsync($"/api/blueprints/{saved.Id}", Ct)).StatusCode);
            Assert.Empty(await server.Get<List<Summary>>("/api/blueprints"));
            Assert.Empty(await server.Get<List<JsonElement>>("/api/library/types"));
            Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync($"/api/blueprints/{saved.Id}", Ct)).StatusCode);
        }
        finally
        {
            server.DeleteData();
        }
    }
}
