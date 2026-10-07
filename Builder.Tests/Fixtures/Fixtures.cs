using System.Text.Json;
using Builder.Backend.Services;
using Builder.Core.Types;
using Builder.Logic.Blueprints;

namespace Builder.Tests;

/// <summary>
/// Purpose-built blueprints and scenarios for the tests (Fixtures/blueprints, Fixtures/scenarios), written in the
/// ApolloIQ.Core expression syntax with stable ids.
/// </summary>
public static class Fixtures
{
    public static readonly Guid Light = Guid.Parse("0b1e0000-0000-4000-8000-000000000001");
    public static readonly Guid PushButton = Guid.Parse("0b1e0000-0000-4000-8000-000000000002");
    public static readonly Guid CircuitBreaker = Guid.Parse("0b1e0000-0000-4000-8000-000000000003");
    public static readonly Guid LightingGroup = Guid.Parse("0b1e0000-0000-4000-8000-000000000004");
    public static readonly Guid Plant = Guid.Parse("0b1e0000-0000-4000-8000-000000000005");

    public static string BlueprintDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "blueprints");

    public static string ScenarioDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "scenarios");

    public static Blueprint Load(string name) => JsonSerializer.Deserialize<Blueprint>(
        File.ReadAllText(Path.Combine(BlueprintDirectory, $"{name}{BlueprintStore.Extension}")), Blueprint.Json)!;

    public static IReadOnlyList<Blueprint> All() =>
        Directory.GetFiles(BlueprintDirectory, "*" + BlueprintStore.Extension).Order(StringComparer.Ordinal)
            .Select(f => JsonSerializer.Deserialize<Blueprint>(File.ReadAllText(f), Blueprint.Json)!).ToList();

    public static Func<Guid, Blueprint?> Lookup(IEnumerable<Blueprint>? blueprints = null)
    {
        var all = (blueprints ?? All()).ToDictionary(b => b.Id);
        return id => all.GetValueOrDefault(id);
    }

    /// <summary>A library with the given blueprints, or all fixtures.</summary>
    public static CmLibrary Library(params Blueprint[] blueprints)
    {
        var library = new CmLibrary();
        foreach (var blueprint in blueprints.Length == 0 ? All() : blueprints)
            library.Replace(BlueprintTypes.ToCmType(blueprint.Clone()));
        return library;
    }

    /// <summary>A blueprint store in <paramref name="directory"/> with copies of the fixtures.</summary>
    public static BlueprintStore Store(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(BlueprintDirectory, "*" + BlueprintStore.Extension))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
        return new BlueprintStore(directory);
    }
}
