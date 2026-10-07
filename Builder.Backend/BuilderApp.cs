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
        options.ScenarioPath = Path.GetFullPath(Path.Combine(contentRoot, options.ScenarioPath));
        options.BlueprintPath = Path.GetFullPath(Path.Combine(contentRoot, options.BlueprintPath));

        builder.Services.AddSingleton(options);
        var blueprints = new BlueprintStore(options.BlueprintPath);
        var cmLibrary = new CmLibrary();
        var loadedBlueprints = BlueprintEndpoints.LoadInto(cmLibrary, blueprints);
        builder.Services.AddSingleton(cmLibrary);
        builder.Services.AddSingleton(new ProjectWorkspace(projectsRoot));
        builder.Services.AddSingleton(blueprints);
        builder.Services.AddSingleton<ProposalService>();
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

        if (!Directory.Exists(options.BlueprintPath))
            app.Logger.LogWarning("Blueprint folder {Path} does not exist", options.BlueprintPath);
        app.Logger.LogInformation("Loaded {Count} blueprints from {Path}: {Names}; projects in {Projects}", loadedBlueprints.Count, options.BlueprintPath,
            string.Join(", ", loadedBlueprints), projectsRoot);

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
        app.MapExportApi();
        app.MapProposalApi();
        app.MapHub<SimulationHub>("/hubs/simulation");
        return app;
    }
}
