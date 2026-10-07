using System.Net;
using System.Net.Http.Json;
using Builder.Backend.Contracts;
using Xunit;

namespace Builder.Tests.Api;

public sealed class ApiTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiServer _server = null!;

    public async ValueTask InitializeAsync() => _server = await ApiServer.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _server.DeleteData();
    }

    private async Task<ProjectDto> NewProject(string name = "Demo") =>
        await _server.Post<ProjectDto>("/api/projects", new CreateProjectRequest(name));

    private async Task<TreeNodeDto> Folder(Guid projectId, string name, Guid? parentId = null) =>
        await _server.Post<TreeNodeDto>($"/api/projects/{projectId}/folders", new CreateFolderRequest(name, parentId));

    private async Task<TreeNodeDto> Cm(Guid projectId, Guid blueprint, string name, Guid? parentId) =>
        await _server.Post<TreeNodeDto>($"/api/projects/{projectId}/control-modules",
            new CreateControlModuleRequest(blueprint, name, parentId));

    private static async Task<ApiError> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiError>(ApiServer.Json, Ct))!;

    [Fact]
    public async Task HealthIsOk()
    {
        var response = await _server.Client.GetAsync("/api/health", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LibraryListsThePublishedCmBlueprints()
    {
        var types = await _server.Get<List<CmTypeDto>>("/api/library/types");
        Assert.Equal(["CircuitBreaker", "Light", "PushButton"], types.Select(t => t.Name));
        Assert.Equal((Fixtures.Light, "0.1.0"), (types.Single(t => t.Name == "Light").Id, types.Single(t => t.Name == "Light").Version));
    }

    [Fact]
    public async Task CreatedProjectIsListedAndSavedToDisk()
    {
        var project = await NewProject();
        var list = await _server.Get<List<ProjectDto>>("/api/projects");
        Assert.Contains(list, p => p.Id == project.Id && p.Name == "Demo");
        Assert.True(File.Exists(Path.Combine(_server.ProjectsRoot, project.Id.ToString(), "project.json")));
    }

    [Fact]
    public async Task DuplicateProjectNameIsAConflict()
    {
        await NewProject();
        var response = await _server.Client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("demo"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task TreeShowsFoldersAndControlModules()
    {
        var project = await NewProject();
        var pms = await Folder(project.Id, "PMS");
        await Cm(project.Id, Fixtures.Light, "GEN1", pms.Id);
        await Cm(project.Id, Fixtures.CircuitBreaker, "GEN1_CB", pms.Id);
        await Folder(project.Id, "Switchboard", pms.Id);

        var tree = await _server.Get<List<TreeNodeDto>>($"/api/projects/{project.Id}/tree");
        var root = Assert.Single(tree);
        Assert.Equal("PMS", root.Name);
        Assert.Equal(["Switchboard", "GEN1", "GEN1_CB"], root.Children.Select(c => c.Name));
        var gen1 = root.Children.Single(c => c.Name == "GEN1");
        Assert.Equal("controlModule", gen1.Kind);
        Assert.Equal("PMS.GEN1", gen1.Path);
        Assert.Equal(("Light", "0.1.0", (Guid?)Fixtures.Light), (gen1.TypeName, gen1.TypeVersion, gen1.BlueprintId));
        Assert.True(gen1.TagCount > 30);
    }

    [Fact]
    public async Task InvalidNameIsABadRequestOnTheNameField()
    {
        var project = await NewProject();
        var response = await _server.Client.PostAsJsonAsync($"/api/projects/{project.Id}/folders", new CreateFolderRequest("P M S", null), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await Error(response);
        Assert.Equal("invalid_name", error.Code);
        Assert.Equal("name", error.Field);
    }

    [Fact]
    public async Task DuplicateCmNameIsAConflict()
    {
        var project = await NewProject();
        var pms = await Folder(project.Id, "PMS");
        await Cm(project.Id, Fixtures.Light, "GEN1", pms.Id);
        var response = await _server.Client.PostAsJsonAsync($"/api/projects/{project.Id}/control-modules",
            new CreateControlModuleRequest(Fixtures.CircuitBreaker, "GEN1", pms.Id), Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task UnknownProjectAndObjectAreNotFound()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _server.Client.GetAsync($"/api/projects/{Guid.NewGuid()}/tree", Ct)).StatusCode);
        var project = await NewProject();
        Assert.Equal(HttpStatusCode.NotFound, (await _server.Client.DeleteAsync($"/api/projects/{project.Id}/objects/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task RenameKeepsTagIds()
    {
        var project = await NewProject();
        var pms = await Folder(project.Id, "PMS");
        var gen1 = await Cm(project.Id, Fixtures.Light, "GEN1", pms.Id);
        var before = await _server.Get<List<TagDto>>($"/api/projects/{project.Id}/tags?scope={gen1.Id}");

        var response = await _server.Client.PatchAsJsonAsync($"/api/projects/{project.Id}/objects/{gen1.Id}", new RenameRequest("GEN_PORT"), Ct);
        response.EnsureSuccessStatusCode();
        var after = await _server.Get<List<TagDto>>($"/api/projects/{project.Id}/tags?scope={gen1.Id}");

        Assert.Equal(before.Select(t => t.Id).Order(), after.Select(t => t.Id).Order());
        Assert.All(after, t => Assert.StartsWith("PMS.GEN_PORT.", t.Path));
    }

    [Fact]
    public async Task MoveIntoItselfIsABadRequestOnParent()
    {
        var project = await NewProject();
        var a = await Folder(project.Id, "A");
        var b = await Folder(project.Id, "B", a.Id);
        var response = await _server.Client.PostAsJsonAsync($"/api/projects/{project.Id}/objects/{a.Id}/move", new MoveRequest(b.Id), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("parentId", (await Error(response)).Field);
    }

    [Fact]
    public async Task DeletionSummaryThenDelete()
    {
        var project = await NewProject();
        var pms = await Folder(project.Id, "PMS");
        var gen1 = await Cm(project.Id, Fixtures.Light, "GEN1", pms.Id);

        var summary = await _server.Get<DeletionSummaryDto>($"/api/projects/{project.Id}/objects/{pms.Id}/deletion-summary");
        Assert.Equal(new DeletionSummaryDto(1, 1, gen1.TagCount), summary);

        var response = await _server.Client.DeleteAsync($"/api/projects/{project.Id}/objects/{pms.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await _server.Get<List<TreeNodeDto>>($"/api/projects/{project.Id}/tree"));
    }

    [Fact]
    public async Task TagFiltersNarrowTheList()
    {
        var project = await NewProject();
        var pms = await Folder(project.Id, "PMS");
        var gen1 = await Cm(project.Id, Fixtures.Light, "GEN1", pms.Id);
        await Cm(project.Id, Fixtures.CircuitBreaker, "GEN1_CB", pms.Id);

        var commands = await _server.Get<List<TagDto>>($"/api/projects/{project.Id}/tags?scope={gen1.Id}&group=CMD");
        Assert.Equal(["PMS.GEN1.CMD.HMI_off", "PMS.GEN1.CMD.HMI_on", "PMS.GEN1.CMD.HMI_reset", "PMS.GEN1.CMD.reset", "PMS.GEN1.CMD.set_off", "PMS.GEN1.CMD.set_on"],
            commands.Select(t => t.Path));

        var external = await _server.Get<List<TagDto>>($"/api/projects/{project.Id}/tags?kind=External&search=gen1_cb");
        Assert.All(external, t => Assert.Equal("FIN", t.Group));
        Assert.Equal(["feedback", "power_ok", "remote", "tripped"], external.Select(t => t.Name));

        var outputs = await _server.Get<List<TagDto>>($"/api/projects/{project.Id}/tags?direction=Out&group=OUT");
        Assert.Equal(["PMS.GEN1.OUT.lamp", "PMS.GEN1_CB.OUT.coil_off", "PMS.GEN1_CB.OUT.coil_on"], outputs.Select(t => t.Path));

        var bad = await _server.Client.GetAsync($"/api/projects/{project.Id}/tags?group=XYZ", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task ProjectSurvivesARestart()
    {
        var project = await NewProject();
        var pms = await Folder(project.Id, "PMS");
        var gen1 = await Cm(project.Id, Fixtures.Light, "GEN1", pms.Id);
        var root = _server.ProjectsRoot;
        await _server.DisposeAsync();

        _server = await ApiServer.StartAsync(root);
        var tags = await _server.Get<List<TagDto>>($"/api/projects/{project.Id}/tags?scope={gen1.Id}");
        Assert.Equal(gen1.TagCount, tags.Count);
    }

    [Fact]
    public async Task ConventionsAreTheSharedOnes()
    {
        var conventions = await _server.Get<System.Text.Json.JsonElement>("/api/conventions");
        Assert.Equal(25, conventions.GetProperty("defaultAlarmPriority").GetInt32());
        Assert.Equal(3, conventions.GetProperty("alarmPriorityBands").GetArrayLength());
    }

    [Fact]
    public async Task ExamplesAndTheOldExportsAreGone()
    {
        var project = await NewProject();
        Assert.Equal(HttpStatusCode.NotFound, (await _server.Client.GetAsync("/api/examples", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _server.Client.GetAsync($"/api/projects/{project.Id}/export/hmi-tags", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _server.Client.GetAsync($"/api/projects/{project.Id}/export/hmi-profile", Ct)).StatusCode);
    }
}
