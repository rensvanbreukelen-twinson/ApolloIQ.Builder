using ApolloIQ.Core.Exchange;
using Builder.Backend.Services;
using Builder.Core.Types;
using Builder.Persistence.Export;

namespace Builder.Backend.Endpoints;

public sealed record ExportCheckDto(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<ExportBlueprintInfo> Blueprints, string FileName);

/// <summary>Export to SCADA: <c>GET …/export/scada/check</c> and <c>POST …/export/scada</c> (the <c>*.apolloiq.json</c> download).</summary>
public static class ExportEndpoints
{
    public static void MapExportApi(this WebApplication app)
    {
        var project = app.MapGroup("/api/projects/{projectId:guid}/export/scada");

        project.MapGet("/check", (Guid projectId, ProjectWorkspace workspace, CmLibrary library, BlueprintStore blueprints) =>
        {
            var session = workspace.Get(projectId);
            var check = Build(session, library, blueprints).Check;
            return new ExportCheckDto(check.Errors, check.Warnings, check.Blueprints, ExchangeExporter.FileName(session.Name));
        });

        project.MapPost("", (Guid projectId, ProjectWorkspace workspace, CmLibrary library, BlueprintStore blueprints) =>
        {
            var session = workspace.Get(projectId);
            lock (session)
            {
                var (file, check) = Build(session, library, blueprints);
                if (!check.Ok)
                    return Results.BadRequest(new { error = $"The export is refused: {check.Errors[0]}", errors = check.Errors });
                var json = ExchangeJson.Write(file);
                var record = ExportRecord.Load(session.Directory);
                record.ExportedAt = file.Source.ExportedAt;
                foreach (var blueprint in file.Blueprints)
                    record.Blueprints[blueprint.Id] = new ExportedBlueprint { Name = blueprint.Name, Version = blueprint.Version, Hash = blueprint.Hash };
                record.Save(session.Directory);
                return Results.File(System.Text.Encoding.UTF8.GetBytes(json + "\n"), "application/json", ExchangeExporter.FileName(session.Name));
            }
        });
    }

    private static ExportResult Build(ProjectSession session, CmLibrary library, BlueprintStore blueprints)
    {
        var last = ExportRecord.Load(session.Directory);
        var all = blueprints.All().ToDictionary(b => b.Id);
        return session.Read(p => ExchangeExporter.Build(session.Id, session.Name, p, library, id => all.GetValueOrDefault(id), last));
    }
}
