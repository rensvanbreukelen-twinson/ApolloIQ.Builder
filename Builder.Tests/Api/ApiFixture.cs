using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Builder.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Builder.Tests.Api;

public sealed class ApiServer : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly WebApplication _app;

    private ApiServer(WebApplication app, HttpClient client, string projectsRoot)
    {
        _app = app;
        Client = client;
        ProjectsRoot = projectsRoot;
    }

    public HttpClient Client { get; }

    public string ProjectsRoot { get; }

    /// <summary>Starts the API on a free port. <paramref name="fixtures"/>: the blueprint library starts with a copy of the test fixtures.</summary>
    public static async Task<ApiServer> StartAsync(string? projectsRoot = null, bool fixtures = true)
    {
        var root = projectsRoot ?? Path.Combine(Path.GetTempPath(), $"builder-api-{Guid.NewGuid():N}");
        if (fixtures)
            Fixtures.Store(Path.Combine(root, "blueprints"));
        var app = BuilderApp.Build(["--urls", "http://127.0.0.1:0", "--environment", "Testing"], options =>
        {
            options.ProjectsRoot = root;
            options.SimulatorTcpPort = 0;
            options.ScenarioPath = Fixtures.ScenarioDirectory;
            options.BlueprintPath = Path.Combine(root, "blueprints");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new ApiServer(app, new HttpClient { BaseAddress = new Uri(address) }, root);
    }

    public async Task<T> Get<T>(string url)
    {
        var response = await Client.GetAsync(url, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    public async Task<T> Post<T>(string url, object body)
    {
        var response = await Client.PostAsJsonAsync(url, body, Json, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public void DeleteData()
    {
        if (Directory.Exists(ProjectsRoot))
            Directory.Delete(ProjectsRoot, recursive: true);
    }
}
