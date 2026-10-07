using ApolloIQ.Core.Blueprints;
using ApolloIQ.Core.Conventions;
using Builder.Backend.Services;
using Builder.Core.Types;
using Builder.Logic.Blueprints;

namespace Builder.Backend.Endpoints;

public sealed record BlueprintSummaryDto(Guid Id, string Name, string Kind, string Version, string Description, int Errors, int Warnings);

public sealed record BlueprintCatalogDto(IReadOnlyList<BlueprintInterface> Interfaces, IReadOnlyList<StateCategory> Categories,
    IReadOnlyList<string> Groups, IReadOnlyList<string> DataTypes, IReadOnlyList<string> StandardAliases);

public sealed record BlueprintResultDto(Blueprint Blueprint, IReadOnlyList<BlueprintIssue> Issues);

/// <summary>The blueprint editor's API. Blueprints are addressed by their stable id.</summary>
public static class BlueprintEndpoints
{
    /// <summary>Publishes every valid blueprint of the store into the library and removes the rest. Returns the published names.</summary>
    public static IReadOnlyList<string> LoadInto(CmLibrary library, BlueprintStore store)
    {
        var all = store.All();
        var byId = all.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var type in library.Types.ToList().Where(t => !byId.ContainsKey(t.Id)))
            library.Remove(type.Id);
        var loaded = new List<string>();
        foreach (var blueprint in byId.Values)
        {
            if (BlueprintValidator.Validate(blueprint.Clone(), id => byId.GetValueOrDefault(id)).Any(i => i.Severity == "Error"))
            {
                library.Remove(blueprint.Id);
                continue;
            }
            library.Replace(BlueprintTypes.ToCmType(blueprint.Clone()));
            loaded.Add(blueprint.Name);
        }
        return loaded;
    }

    private static Func<Guid, Blueprint?> Lookup(BlueprintStore store)
    {
        var all = store.All().GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        return id => all.GetValueOrDefault(id);
    }

    private static BlueprintResultDto Result(Blueprint blueprint, BlueprintStore store) => new(blueprint, BlueprintValidator.Validate(blueprint, Lookup(store)));

    public static void MapBlueprintApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/blueprints");

        group.MapGet("/catalog", () => new BlueprintCatalogDto(BlueprintCatalog.Interfaces, BlueprintRules.Categories,
            BlueprintRules.Groups, BlueprintRules.DataTypes, [.. UniversalStates.StandardAliases.Keys]));

        group.MapGet("", (BlueprintStore store) =>
        {
            var lookup = Lookup(store);
            return store.All().Select(b =>
            {
                var issues = BlueprintValidator.Validate(b, lookup);
                return new BlueprintSummaryDto(b.Id, b.Name, b.Kind.ToString(), b.Version.ToString(), b.Description,
                    issues.Count(i => i.Severity == "Error"), issues.Count(i => i.Severity == "Warning"));
            }).ToList();
        });

        group.MapGet("/{id:guid}", (Guid id, BlueprintStore store) =>
            store.Find(id) is { } b ? Results.Ok(Result(b, store)) : NotFound(id));

        group.MapPost("/validate", (Blueprint blueprint, BlueprintStore store) =>
        {
            BlueprintRules.AssignCodes(blueprint);
            return Result(blueprint, store);
        });

        group.MapPost("", (Blueprint blueprint, BlueprintStore store, CmLibrary library) => Guarded(() =>
        {
            if (blueprint.Id != Guid.Empty && store.Find(blueprint.Id) is not null)
                return Error(409, "blueprint_exists", $"A blueprint with id {blueprint.Id} already exists; save it with PUT.");
            return Save(blueprint, store, library, created: true);
        }));

        group.MapPut("/{id:guid}", (Guid id, Blueprint blueprint, BlueprintStore store, CmLibrary library) => Guarded(() =>
        {
            if (blueprint.Id != Guid.Empty && blueprint.Id != id)
                return Error(400, "invalid_blueprint", "The blueprint's id does not match the address.");
            blueprint.Id = id;
            return Save(blueprint, store, library, created: false);
        }));

        group.MapDelete("/{id:guid}", (Guid id, BlueprintStore store, CmLibrary library) => Guarded(() =>
        {
            if (!store.Delete(id))
                return NotFound(id);
            LoadInto(library, store);
            return Results.NoContent();
        }));
    }

    private static IResult Save(Blueprint blueprint, BlueprintStore store, CmLibrary library, bool created)
    {
        if (store.FindByName(blueprint.Name) is { } other && other.Id != blueprint.Id)
            return Error(409, "blueprint_exists", $"A blueprint named '{blueprint.Name}' already exists.");
        var issues = BlueprintValidator.Validate(blueprint, Lookup(store));
        if (issues.FirstOrDefault(i => i.Where == "General" && i.Severity == "Error") is { } general)
            return Error(400, "invalid_blueprint", general.Message);
        store.Save(blueprint);
        LoadInto(library, store);
        var result = Result(blueprint, store);
        return created ? Results.Created($"/api/blueprints/{blueprint.Id}", result) : Results.Ok(result);
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

    private static IResult NotFound(Guid id) => Error(404, "blueprint_not_found", $"Blueprint {id} does not exist.");

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);
}
