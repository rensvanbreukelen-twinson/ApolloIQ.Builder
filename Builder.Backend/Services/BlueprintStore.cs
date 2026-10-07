using System.Text.Json;
using Builder.Logic.Blueprints;

namespace Builder.Backend.Services;

public sealed class BlueprintStore(string root)
{
    private const string Extension = ".blueprint.json";
    private readonly Lock _gate = new();

    public string Root { get; } = root;

    public IReadOnlyList<Blueprint> All()
    {
        lock (_gate)
        {
            if (!Directory.Exists(Root))
                return [];
            return Directory.GetFiles(Root, "*" + Extension).Select(Load).OfType<Blueprint>()
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public Blueprint? Find(string name)
    {
        lock (_gate)
        {
            var path = PathOf(name);
            return File.Exists(path) ? Load(path) : null;
        }
    }

    public void Save(string? previousName, Blueprint blueprint)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Root);
            blueprint.Schema = Blueprint.SchemaId;
            BlueprintCatalog.AssignCodes(blueprint);
            File.WriteAllText(PathOf(blueprint.Name), JsonSerializer.Serialize(blueprint, Blueprint.Json) + "\n");
            if (previousName is not null && !previousName.Equals(blueprint.Name, StringComparison.OrdinalIgnoreCase))
                File.Delete(PathOf(previousName));
        }
    }

    public bool Delete(string name)
    {
        lock (_gate)
        {
            var path = PathOf(name);
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
    }

    private string PathOf(string name)
    {
        if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException($"Invalid blueprint name '{name}'.");
        return Path.Combine(Root, name + Extension);
    }

    private static Blueprint? Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Blueprint>(File.ReadAllText(path), Blueprint.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
