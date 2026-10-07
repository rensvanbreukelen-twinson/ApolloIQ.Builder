using System.Text.Json.Serialization;
using Builder.Backend.Endpoints;
using Builder.Backend.Hubs;
using Builder.Backend.Services;
using Builder.Core.Types;

namespace Builder.Backend;

public static class BuilderApp
{
    public static WebApplication Build(string[] args, Action<BuilderOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        var options = builder.Configuration.GetSection("Builder").Get<BuilderOptions>() ?? new BuilderOptions();
        configure?.Invoke(options);
        var contentRoot = builder.Environment.ContentRootPath;
        var projectsRoot = Path.GetFullPath(Path.Combine(contentRoot, options.ProjectsRoot));
        var libraryPath = Path.GetFullPath(Path.Combine(contentRoot, options.LibraryPath));
        options.ScenarioPath = Path.GetFullPath(Path.Combine(contentRoot, options.ScenarioPath));
        options.BlueprintPath = Path.GetFullPath(Path.Combine(contentRoot, options.BlueprintPath));
        options.ExamplePath = Path.GetFullPath(Path.Combine(contentRoot, options.ExamplePath));

        builder.Services.AddSingleton(options);
        var blueprints = new BlueprintStore(options.BlueprintPath);
        var cmLibrary = Directory.Exists(libraryPath) ? CmLibrary.LoadDirectory(libraryPath) : new CmLibrary();
        var loadedBlueprints = BlueprintEndpoints.LoadInto(cmLibrary, blueprints);
        builder.Services.AddSingleton(cmLibrary);
        builder.Services.AddSingleton(new ProjectWorkspace(projectsRoot));
        builder.Services.AddSingleton(blueprints);
        builder.Services.AddSingleton(new ExampleProjects(options.ExamplePath));
        builder.Services.AddSingleton<SimulationHost>();
        builder.Services.AddSingleton<SimulatorTcpService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SimulatorTcpService>());
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSignalR().AddJsonProtocol(json =>
            json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
            policy.WithOrigins(options.AllowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

        var app = builder.Build();

        var library = app.Services.GetRequiredService<CmLibrary>();
        if (!Directory.Exists(libraryPath))
            app.Logger.LogWarning("CM library folder {Path} does not exist", libraryPath);
        foreach (var error in library.Errors)
            app.Logger.LogError("CM library: {Error}", error.ToString());
        app.Logger.LogInformation("Loaded {Count} CM types from {Path}; projects in {Projects}",
            library.Types.Count, libraryPath, projectsRoot);
        app.Logger.LogInformation("Loaded {Count} blueprints from {Path}: {Names}", loadedBlueprints.Count, options.BlueprintPath, string.Join(", ", loadedBlueprints));

        app.UseCors();
        app.UseProjectErrors();
        app.MapBuilderApi();
        app.MapSimulationApi();
        app.MapScenarioApi();
        app.MapInterlockApi();
        app.MapTopologyApi();
        app.MapBlueprintApi();
        app.MapConfiguratorApi();
        app.MapCommandInputApi();
        app.MapHub<SimulationHub>("/hubs/simulation");
        return app;
    }
}
