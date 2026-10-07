using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Builder.Core.Model;
using Builder.Core.Tags;

namespace Builder.Persistence.Export;

public static class HmiTagsExporter
{
    public const string FileName = "tags.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string HmiDataType(TagDataType type) => type switch
    {
        TagDataType.Bool => "boolean",
        TagDataType.Int16 or TagDataType.Int32 or TagDataType.Enum => "integer",
        TagDataType.Real or TagDataType.LReal => "float",
        TagDataType.String or TagDataType.DateTime => "string",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static void Validate(HmiExportProfile profile)
    {
        if (profile.ScanRateMs is < HmiExportProfile.MinScanRateMs or > HmiExportProfile.MaxScanRateMs)
            throw new ProjectException(ProjectErrors.InvalidProfile,
                $"The scan rate must be between {HmiExportProfile.MinScanRateMs} and {HmiExportProfile.MaxScanRateMs} ms.", "scanRateMs");
        if (!Enum.IsDefined(profile.Address))
            throw new ProjectException(ProjectErrors.InvalidProfile, $"Unknown address mode '{profile.Address}'.", "address");
    }

    public static IReadOnlyList<HmiTagRecord> Build(Project project, HmiExportProfile profile)
    {
        Validate(profile);
        return project.Tags
            .Select(tag =>
            {
                var path = project.GetPath(tag.Id);
                return new HmiTagRecord(
                    tag.Id,
                    path,
                    HmiDataType(tag.DataType),
                    profile.ScanRateMs,
                    profile.ConnectionId,
                    profile.Address == HmiAddressMode.SymbolKey ? tag.SymbolKey : path);
            })
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static string Serialize(Project project, HmiExportProfile profile) =>
        JsonSerializer.Serialize(new HmiTagsFile(Build(project, profile)), Options) + "\n";

    public static byte[] SerializeUtf8(Project project, HmiExportProfile profile) =>
        new UTF8Encoding(false).GetBytes(Serialize(project, profile));
}

public sealed record HmiTagsFile([property: JsonPropertyOrder(0)] IReadOnlyList<HmiTagRecord> Tags);

public sealed record HmiTagRecord(
    [property: JsonPropertyOrder(0)] Guid Uid,
    [property: JsonPropertyOrder(1)] string Name,
    [property: JsonPropertyOrder(2)] string DataType,
    [property: JsonPropertyOrder(3)] int ScanRateMs,
    [property: JsonPropertyOrder(4)] Guid? ConnectionId,
    [property: JsonPropertyOrder(5)] string Address);
