using System.Text.Json;
using System.Text.Json.Serialization;
using Builder.Core.Model;

namespace Builder.Simulator.Scenarios;

public static class PicJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static PicConfiguration Read(JsonElement element) =>
        element.Deserialize<PicConfiguration>(Options) ?? throw new JsonException("A PIC configuration is required.");

    public static CommandInputConfig ReadInputs(JsonElement element) =>
        element.Deserialize<CommandInputConfig>(Options) ?? throw new JsonException("A command input configuration is required.");
}
