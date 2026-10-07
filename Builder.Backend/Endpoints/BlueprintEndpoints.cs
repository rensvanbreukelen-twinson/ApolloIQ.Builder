using Builder.Backend.Services;
using Builder.Core.Types;
using Builder.Logic.Blueprints;

namespace Builder.Backend.Endpoints;

public sealed record BlueprintSummaryDto(string Name, string Kind, string Version, string Description, int Errors, int Warnings);

public sealed record BlueprintCatalogDto(IReadOnlyList<BlueprintInterface> Interfaces, IReadOnlyList<StateCategory> Categories,
    IReadOnlyList<string> Groups, IReadOnlyList<string> DataTypes);

public sealed record BlueprintResultDto(Blueprint Blueprint, IReadOnlyList<BlueprintIssue> Issues);

public static class BlueprintEndpoints
{
    public static IReadOnlyList<string> LoadInto(CmLibrary library, BlueprintStore store)
    {
        var loaded = new List<string>();
        foreach (var blueprint in store.All())
            if (Publish(library, store, blueprint))
                loaded.Add(blueprint.Name);
        return loaded;
    }

    private static bool Publish(CmLibrary library, BlueprintStore store, Blueprint blueprint)
    {
        if (BlueprintValidator.Validate(blueprint, store.Find).Any(i => i.Severity == "Error"))
            return false;
        library.Replace(BlueprintTypes.ToCmType(blueprint));
        return true;
    }

    public static void MapBlueprintApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/blueprints");

        group.MapGet("/catalog", () => new BlueprintCatalogDto(BlueprintCatalog.Interfaces, BlueprintCatalog.Categories,
            BlueprintCatalog.Groups, BlueprintCatalog.DataTypes));

        group.MapGet("", (BlueprintStore store) => store.All().Select(b =>
        {
            var issues = BlueprintValidator.Validate(b, store.Find);
            return new BlueprintSummaryDto(b.Name, b.Kind.ToString(), b.Version, b.Description,
                issues.Count(i => i.Severity == "Error"), issues.Count(i => i.Severity == "Warning"));
        }).ToList());

        group.MapGet("/{name}", (string name, BlueprintStore store) => Guarded(() =>
            store.Find(name) is { } b ? Results.Ok(new BlueprintResultDto(b, BlueprintValidator.Validate(b, store.Find))) : NotFound(name)));

        group.MapPost("/validate", (Blueprint blueprint, BlueprintStore store) =>
        {
            BlueprintCatalog.AssignCodes(blueprint);
            return new BlueprintResultDto(blueprint, BlueprintValidator.Validate(blueprint, store.Find));
        });

        group.MapPut("/{name}", (string name, Blueprint blueprint, BlueprintStore store, CmLibrary library) => Guarded(() =>
        {
            var exists = store.Find(name) is not null;
            if (!name.Equals(blueprint.Name, StringComparison.OrdinalIgnoreCase) && store.Find(blueprint.Name) is not null)
                return Error(409, "blueprint_exists", $"A blueprint named '{blueprint.Name}' already exists.");
            var issues = BlueprintValidator.Validate(blueprint, store.Find);
            if (issues.Any(i => i.Where == "General" && i.Severity == "Error"))
                return Error(400, "invalid_blueprint", issues.First(i => i.Where == "General").Message);
            store.Save(exists ? name : null, blueprint);
            if (exists && !name.Equals(blueprint.Name, StringComparison.OrdinalIgnoreCase))
                library.Remove(name);
            Publish(library, store, blueprint);
            return Results.Ok(new BlueprintResultDto(blueprint, BlueprintValidator.Validate(blueprint, store.Find)));
        }));

        group.MapDelete("/{name}", (string name, BlueprintStore store, CmLibrary library) => Guarded(() =>
        {
            if (!store.Delete(name))
                return NotFound(name);
            library.Remove(name);
            return Results.NoContent();
        }));
    }

    private static IResult Guarded(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (ArgumentException ex)
        {
            return Error(400, "invalid_blueprint", ex.Message);
        }
    }

    private static IResult NotFound(string name) => Error(404, "blueprint_not_found", $"Blueprint '{name}' does not exist.");

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);
}
