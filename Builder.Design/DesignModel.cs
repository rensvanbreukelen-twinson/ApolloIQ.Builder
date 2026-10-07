using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ApolloIQ.Core.Conventions;

namespace Builder.Design;

/// <summary>
/// A design document or fragment, schema <see cref="SchemaId"/> (see <c>Documentation/Design/Design format.md</c>). Names and paths
/// only, no ids. The backend speaks it as JSON with the same structure as the YAML the agent and the engineer read.
/// </summary>
public sealed class DesignDocument
{
    public const string SchemaId = "apolloiq.design/1";

    public string? Schema { get; set; } = SchemaId;
    public string? Project { get; set; }
    public List<DesignDevice>? Devices { get; set; }
    public List<DesignBlueprint>? Blueprints { get; set; }
    public List<DesignObject>? Objects { get; set; }
    public List<DesignQuestion>? Questions { get; set; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new LenientStringConverter(), new JsonStringEnumConverter() }
    };

    public static DesignDocument Parse(JsonNode? node)
    {
        if (node is not JsonObject)
            throw new DesignException("A design must be an object with schema, devices, blueprints, objects and questions.");
        try
        {
            var design = node.Deserialize<DesignDocument>(Json) ?? new DesignDocument();
            if (design.Schema is { } schema && schema != SchemaId)
                throw new DesignException($"Unsupported design schema '{schema}'; use {SchemaId}.");
            return design;
        }
        catch (JsonException ex)
        {
            throw new DesignException($"The design does not match the format{(ex.Path is { } path ? $" at {path}" : "")}: {ex.Message}");
        }
    }

    public JsonObject ToJson() => JsonSerializer.SerializeToNode(this, Json)!.AsObject();

    public static JsonNode? ToJson<T>(T value) => JsonSerializer.SerializeToNode(value, Json);
}

public sealed class DesignException(string message) : Exception(message);

public sealed class DesignDevice
{
    public string Name { get; set; } = "";
    public string? RenamedFrom { get; set; }
    public bool? Delete { get; set; }
    public string? Role { get; set; }
    public string? Description { get; set; }
}

public sealed class DesignQuestion
{
    public string? About { get; set; }
    public string? Question { get; set; }
    public string? Assumed { get; set; }
}

/// <summary>A blueprint. A section that is left out (null) stays as it is; a section that is given replaces the whole section.</summary>
public sealed class DesignBlueprint
{
    public string Name { get; set; } = "";
    public string? RenamedFrom { get; set; }
    public bool? Delete { get; set; }
    public string? Kind { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public List<string>? Interfaces { get; set; }
    public Dictionary<string, string>? Roles { get; set; }
    public Dictionary<string, DesignTag>? Tags { get; set; }
    public Dictionary<string, DesignState>? States { get; set; }
    public Dictionary<string, DesignTransition>? Transitions { get; set; }
    public Dictionary<string, string>? Always { get; set; }
    public Dictionary<string, DesignAlarm>? Alarms { get; set; }
    public List<DesignInterlock>? Interlocks { get; set; }
    public Dictionary<string, string>? Aliases { get; set; }
    public DesignCommandInputs? CommandInputs { get; set; }
    public Dictionary<string, string>? Plant { get; set; }
}

public sealed class DesignTag
{
    public string? Type { get; set; }
    public string? RenamedFrom { get; set; }
    public string? Description { get; set; }
    public string? Unit { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public string? Initial { get; set; }
    public string? Source { get; set; }
    public bool? Primary { get; set; }
}

public sealed class DesignState
{
    [JsonConverter(typeof(CategoryConverter))]
    public int? Category { get; set; }
    public string? Text { get; set; }
    public string? Description { get; set; }
    public bool? Initial { get; set; }
    public Dictionary<string, string>? Entry { get; set; }
    public Dictionary<string, string>? Run { get; set; }
    public Dictionary<string, string>? Exit { get; set; }
    public DesignTimeout? Timeout { get; set; }
}

public sealed class DesignTimeout
{
    public string? Time { get; set; }
    public string? GoTo { get; set; }
    public string? Alarm { get; set; }
    public int? Priority { get; set; }
    public string? Message { get; set; }
}

public sealed class DesignTransition
{
    [JsonConverter(typeof(StateListConverter))]
    public List<string>? From { get; set; }
    public string? To { get; set; }
    public string? Guard { get; set; }
    public int? Priority { get; set; }
}

public sealed class DesignAlarm
{
    public string? RenamedFrom { get; set; }
    public int? Priority { get; set; }
    public string? Message { get; set; }
    public string? Trigger { get; set; }
    public bool? PlcReactive { get; set; }
    public string? Condition { get; set; }
    public string? Input { get; set; }
    public string? HighCaution { get; set; }
    public string? HighWarning { get; set; }
    public string? HighAlarm { get; set; }
    public string? LowCaution { get; set; }
    public string? LowWarning { get; set; }
    public string? LowAlarm { get; set; }
    public string? TimeoutMode { get; set; }
    public string? Running { get; set; }
    public string? TriggerExpr { get; set; }
    public string? Stop { get; set; }
    public string? Timeout { get; set; }
    public double? OnDelay { get; set; }
    public bool? Latched { get; set; }
    public string? OnTransition { get; set; }
}

/// <summary>An interlock line. In a blueprint the target is a role path; on an object it is a path relative to that object.</summary>
public sealed class DesignInterlock
{
    public string? Target { get; set; }
    public string? Kind { get; set; }
    public string? Condition { get; set; }
    public string? Text { get; set; }
    public string? Alarm { get; set; }
    public int? Priority { get; set; }
    public string? Escalate { get; set; }
}

/// <summary>Command inputs: <c>{ rows: [...], level, selector }</c>, or just the list of rows.</summary>
[JsonConverter(typeof(CommandInputsConverter))]
public sealed class DesignCommandInputs
{
    public List<DesignCommandInput> Rows { get; set; } = [];
    public bool? Level { get; set; }
    public string? Selector { get; set; }
}

public sealed class DesignCommandInput
{
    public string Name { get; set; } = "";
    public string? Source { get; set; }
    public string? Kind { get; set; }
    public string? Drives { get; set; }
    public int? On { get; set; }
    public int? Off { get; set; }
    public string? InAuto { get; set; }
    public string? Location { get; set; }
    public List<string>? Commands { get; set; }
    public double? Debounce { get; set; }
    public double? StuckTime { get; set; }
}

/// <summary>
/// A node of the object tree: exactly one of <see cref="Folder"/>, <see cref="Unit"/>, <see cref="Em"/>, <see cref="Cm"/> gives its
/// kind and name. Members (EM / Unit) are keyed by role.
/// </summary>
public sealed class DesignObject
{
    public string? Folder { get; set; }
    public string? Unit { get; set; }
    public string? Em { get; set; }
    public string? Cm { get; set; }
    public string? RenamedFrom { get; set; }
    public string? MovedFrom { get; set; }
    public bool? Delete { get; set; }
    public string? Blueprint { get; set; }
    public string? Description { get; set; }
    public string? Device { get; set; }
    public Dictionary<string, string>? Values { get; set; }
    public Dictionary<string, int>? AlarmPriorities { get; set; }
    public DesignCommandInputs? CommandInputs { get; set; }
    public List<DesignInterlock>? Interlocks { get; set; }
    public Dictionary<string, DesignObject>? Members { get; set; }
    public List<DesignObject>? Children { get; set; }

    [JsonIgnore]
    public string Name => Folder ?? Unit ?? Em ?? Cm ?? "";

    [JsonIgnore]
    public DesignObjectKind Kind =>
        Folder is not null ? DesignObjectKind.Folder
        : Unit is not null ? DesignObjectKind.Unit
        : Em is not null ? DesignObjectKind.Em
        : Cm is not null ? DesignObjectKind.Cm
        : DesignObjectKind.None;

    public int KindCount() => new[] { Folder, Unit, Em, Cm }.Count(n => n is not null);

    /// <summary>A copy with only the object's own fields (no overrides, interlocks, members or children).</summary>
    public DesignObject Head() => new()
    {
        Folder = Folder, Unit = Unit, Em = Em, Cm = Cm, Blueprint = Blueprint, Description = Description, Device = Device
    };

    public static DesignObject Named(DesignObjectKind kind, string name) => kind switch
    {
        DesignObjectKind.Folder => new DesignObject { Folder = name },
        DesignObjectKind.Unit => new DesignObject { Unit = name },
        DesignObjectKind.Em => new DesignObject { Em = name },
        _ => new DesignObject { Cm = name }
    };
}

public enum DesignObjectKind
{
    None,
    Folder,
    Unit,
    Em,
    Cm
}

/// <summary>Reads numbers and Booleans into string properties too (YAML <c>initial: 5</c>, <c>TRUE</c>), as their text.</summary>
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override bool HandleNull => false;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => Encoding.UTF8.GetString(reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan.ToArray()),
        JsonTokenType.True => "TRUE",
        JsonTokenType.False => "FALSE",
        _ => throw new JsonException($"Expected text, found {reader.TokenType}.")
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>A state category as its number (0, 100, … 500) or its name (Stopped, Running, …).</summary>
public sealed class CategoryConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetInt32();
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        var text = reader.GetString() ?? "";
        if (int.TryParse(text, out var number))
            return number;
        return UniversalStates.CategoryNamed(text)?.Code
               ?? throw new JsonException($"Unknown state category '{text}'. Use {string.Join(", ", UniversalStates.Categories.Where(c => c.Reporting).Select(c => $"{c.Name} ({c.Code})"))}.");
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is { } number)
            writer.WriteNumberValue(number);
        else
            writer.WriteNullValue();
    }
}

/// <summary>A transition's <c>from</c>: a list of state names or <c>"*"</c> (any state).</summary>
public sealed class StateListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return [reader.GetString()!];
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        return JsonSerializer.Deserialize<List<string>>(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value is ["*"])
            writer.WriteStringValue("*");
        else
            JsonSerializer.Serialize(writer, value ?? [], options);
    }
}

public sealed class CommandInputsConverter : JsonConverter<DesignCommandInputs>
{
    public override DesignCommandInputs? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
            return new DesignCommandInputs { Rows = JsonSerializer.Deserialize<List<DesignCommandInput>>(ref reader, options) ?? [] };
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("Command inputs must be a list of rows or { rows, level, selector }.");
        return new DesignCommandInputs
        {
            Rows = node["rows"]?.Deserialize<List<DesignCommandInput>>(options) ?? [],
            Level = node["level"]?.GetValue<bool>(),
            Selector = node["selector"]?.GetValue<string>()
        };
    }

    public override void Write(Utf8JsonWriter writer, DesignCommandInputs value, JsonSerializerOptions options)
    {
        if (value.Level is null && value.Selector is null)
        {
            JsonSerializer.Serialize(writer, value.Rows, options);
            return;
        }
        writer.WriteStartObject();
        writer.WritePropertyName("rows");
        JsonSerializer.Serialize(writer, value.Rows, options);
        if (value.Level is { } level)
            writer.WriteBoolean("level", level);
        if (value.Selector is { } selector)
            writer.WriteString("selector", selector);
        writer.WriteEndObject();
    }
}
