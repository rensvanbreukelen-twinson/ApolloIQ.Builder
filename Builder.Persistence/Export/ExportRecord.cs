using System.Text.Json;
using System.Text.Json.Serialization;
using ApolloIQ.Core.Versioning;

namespace Builder.Persistence.Export;

/// <summary>What the last export to SCADA contained, per blueprint (<c>exports.json</c> in the project folder).</summary>
public sealed class ExportRecord
{
    public const string Schema = "apolloiq.exports/1";
    public const string FileName = "exports.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [JsonPropertyOrder(0)] public string SchemaId { get; set; } = Schema;

    [JsonPropertyOrder(1)] public DateTimeOffset? ExportedAt { get; set; }

    /// <summary>Blueprint id → the version and exchange hash of its last export.</summary>
    [JsonPropertyOrder(2)] public SortedDictionary<Guid, ExportedBlueprint> Blueprints { get; set; } = [];

    public static ExportRecord Load(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, FileName);
        if (!File.Exists(path))
            return new ExportRecord();
        try
        {
            return JsonSerializer.Deserialize<ExportRecord>(File.ReadAllText(path), Json) ?? new ExportRecord();
        }
        catch (JsonException ex)
        {
            throw new ProjectLoadException([$"{FileName}: {ex.Message}"]);
        }
    }

    public void Save(string projectDirectory)
    {
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, FileName), JsonSerializer.Serialize(this, Json) + "\n");
    }
}

public sealed class ExportedBlueprint
{
    [JsonPropertyOrder(0)] public string Name { get; set; } = "";

    [JsonPropertyOrder(1)] public BlueprintVersion Version { get; set; } = BlueprintVersion.Initial;

    [JsonPropertyOrder(2)] public string Hash { get; set; } = "";
}
