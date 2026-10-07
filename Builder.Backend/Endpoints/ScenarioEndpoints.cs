using Builder.Backend.Services;
using Builder.Core.Types;
using Builder.Simulator.Scenarios;

namespace Builder.Backend.Endpoints;

public static class ScenarioEndpoints
{
    public static void MapScenarioApi(this WebApplication app)
    {
        app.MapPost("/api/scenarios/run", (BuilderOptions options, CmLibrary library) =>
        {
            var report = Run(options, library, null);
            return new
            {
                results = report.Results.Select(r => r with { Trace = [] }).ToList(),
                report.Coverage,
                errors = report.Errors.Select(e => e.ToString()).ToList(),
                report.Passed,
                report.Failed
            };
        });

        app.MapPost("/api/scenarios/trace", (ScenarioSelection selection, BuilderOptions options, CmLibrary library) =>
        {
            var (files, _) = ScenarioLoader.LoadDirectory(options.ScenarioPath);
            var file = files.FirstOrDefault(f => f.FileName == selection.File);
            var scenario = file?.Scenarios.FirstOrDefault(s => s.Name == selection.Name);
            return file is null || scenario is null
                ? Results.NotFound(new Contracts.ApiError("not_found", $"Scenario '{selection.Name}' in {selection.File} not found."))
                : Results.Ok(new ScenarioRunner(library).Run(file, scenario));
        });
    }

    private static ScenarioReport Run(BuilderOptions options, CmLibrary library, string? file)
    {
        var (files, errors) = ScenarioLoader.LoadDirectory(options.ScenarioPath);
        return new ScenarioRunner(library).RunAll(files.Where(f => file is null || f.FileName == file), errors);
    }
}

public sealed record ScenarioSelection(string File, string Name);
