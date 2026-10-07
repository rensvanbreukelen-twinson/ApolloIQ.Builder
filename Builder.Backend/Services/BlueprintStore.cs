using System.Text.Json;
using Builder.Logic.Blueprints;

namespace Builder.Backend.Services;

/// <summary>
/// The blueprint library: one <c>&lt;Name&gt;.blueprint.json</c> per blueprint in <see cref="Root"/>. Blueprints are found by their
/// stable id; renaming a blueprint renames its file and keeps the id.
/// </summary>
public sealed class BlueprintStore(string root)
{
    public const string Extension = ".blueprint.json";
    private readonly Lock _gate = new();

    public string Root { get; } = root;

    public IReadOnlyList<Blueprint> All()
    {
        lock (_gate)
            return Files().Select(f => f.Blueprint).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Blueprint? Find(Guid id)
    {
        lock (_gate)
            return Files().FirstOrDefault(f => f.Blueprint.Id == id).Blueprint;
    }

    public Blueprint? FindByName(string name)
    {
        lock (_gate)
            return Files().FirstOrDefault(f => string.Equals(f.Blueprint.Name, name, StringComparison.OrdinalIgnoreCase)).Blueprint;
    }

    /// <summary>Saves a blueprint (new ones get their ids here) and removes the file of its previous name.</summary>
    public void Save(Blueprint blueprint)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Root);
            blueprint.Schema = Blueprint.SchemaId;
            BlueprintRules.AssignIds(blueprint);
            BlueprintRules.AssignCodes(blueprint);
            var target = PathOf(blueprint.Name);
            var previous = Files().Where(f => f.Blueprint.Id == blueprint.Id && f.Path != target).Select(f => f.Path).ToList();
            File.WriteAllText(target, JsonSerializer.Serialize(blueprint, Blueprint.Json) + "\n");
            foreach (var path in previous)
                File.Delete(path);
        }
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            var files = Files().Where(f => f.Blueprint.Id == id).ToList();
            foreach (var (path, _) in files)
                File.Delete(path);
            return files.Count > 0;
        }
    }

    private List<(string Path, Blueprint Blueprint)> Files()
    {
        if (!Directory.Exists(Root))
            return [];
        return Directory.GetFiles(Root, "*" + Extension).Order(StringComparer.Ordinal)
            .Select(path => (path, Load(path)))
            .Where(f => f.Item2 is not null)
            .Select(f => (f.path, f.Item2!))
            .ToList();
    }

    private string PathOf(string name)
    {
        if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException($"Invalid blueprint name '{name}'.");
        return Path.Combine(Root, name + Extension);
    }

    public static Blueprint? Load(string path)
    {
        try
        {
            var blueprint = JsonSerializer.Deserialize<Blueprint>(File.ReadAllText(path), Blueprint.Json);
            return blueprint is { Schema: Blueprint.SchemaId } && blueprint.Id != Guid.Empty ? blueprint : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
