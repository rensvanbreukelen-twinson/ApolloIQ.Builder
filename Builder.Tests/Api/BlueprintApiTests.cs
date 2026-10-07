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

    private sealed record Summary(string Name, string Kind, int Errors, int Warnings);

    [Fact]
    public async Task BlueprintsAreSavedListedRenamedAndDeleted()
    {
        await using var server = await ApiServer.StartAsync();
        try
        {
            var light = JsonSerializer.Deserialize<Blueprint>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "blueprints", "Light.blueprint.json")), Blueprint.Json)!;
            var saved = await server.Client.PutAsJsonAsync("/api/blueprints/Light", light, ApiServer.Json, Ct);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.True(File.Exists(Path.Combine(server.ProjectsRoot, "blueprints", "Light.blueprint.json")));

            var list = await server.Get<List<Summary>>("/api/blueprints");
            Assert.Equal(("Light", 0), (list.Single().Name, list.Single().Errors));

            light.Tags[0].Source = null;
            var checkedResult = await server.Post<Result>("/api/blueprints/validate", light);
            Assert.Contains(checkedResult.Issues, i => i.Severity == "Error");

            light.Name = "DeckLight";
            (await server.Client.PutAsJsonAsync("/api/blueprints/Light", light, ApiServer.Json, Ct)).EnsureSuccessStatusCode();
            var loaded = await server.Get<Result>("/api/blueprints/DeckLight");
            Assert.Equal("DeckLight", loaded.Blueprint.Name);
            Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/api/blueprints/Light", Ct)).StatusCode);

            var catalog = await server.Get<JsonElement>("/api/blueprints/catalog");
            Assert.Equal(6, catalog.GetProperty("interfaces").GetArrayLength());

            Assert.Equal(HttpStatusCode.NoContent, (await server.Client.DeleteAsync("/api/blueprints/DeckLight", Ct)).StatusCode);
            Assert.Empty(await server.Get<List<Summary>>("/api/blueprints"));
            Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.GetAsync("/api/blueprints/..%2Fx", Ct)).StatusCode);
        }
        finally
        {
            server.DeleteData();
        }
    }
}
