using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ApolloIQ.Core.Alarms;
using ApolloIQ.Core.Blueprints;
using ApolloIQ.Core.Conventions;
using ApolloIQ.Core.Versioning;
using Builder.Core.Model;

namespace Builder.Logic.Blueprints;

public enum InputSource
{
    LocalIO,
    External
}

/// <summary>
/// A blueprint file (<c>&lt;Name&gt;.blueprint.json</c>, schema <see cref="SchemaId"/>). Expressions use the ApolloIQ.Core syntax:
/// C-style operators and tags in brackets, <c>[CMD.set_on] &amp;&amp; [LOK.can_on]</c>, <c>[ENGINE.STS.state] == Running</c>.
/// Action targets (<see cref="BlueprintAction.Tag"/>) are plain tag names.
/// </summary>
public sealed class Blueprint
{
    public const string SchemaId = "apolloiq.blueprint/1";

    public string Schema { get; set; } = SchemaId;

    /// <summary>Stable id: assigned when the blueprint is first saved, kept on rename. Instances and roles refer to it.</summary>
    public Guid Id { get; set; }

    public BlueprintKind Kind { get; set; } = BlueprintKind.CM;
    public string Name { get; set; } = "";
    public BlueprintVersion Version { get; set; } = BlueprintVersion.Initial;
    public string Description { get; set; } = "";
    public List<string> Interfaces { get; set; } = [BlueprintCatalog.Base];
    public List<BlueprintTag> Tags { get; set; } = [];
    public List<BlueprintRole> Roles { get; set; } = [];
    public List<BlueprintState> States { get; set; } = [];
    public List<BlueprintTransition> Transitions { get; set; } = [];
    public List<BlueprintAction> Always { get; set; } = [];
    public List<BlueprintAlarm> Alarms { get; set; } = [];
    public List<BlueprintAction> Plant { get; set; } = [];
    public List<InterlockRule> Interlocks { get; set; } = [];
    public Dictionary<string, string> Aliases { get; set; } = [];
    public CommandInputConfig? CommandInputs { get; set; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public Blueprint Clone() => JsonSerializer.Deserialize<Blueprint>(JsonSerializer.Serialize(this, Json), Json)!;
}

public sealed class BlueprintTag
{
    /// <summary>Stable id: SCADA derives the instance tag ids from it, so a rename keeps trends and links.</summary>
    public Guid Id { get; set; }

    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "Bool";
    public string Description { get; set; } = "";
    public string? Unit { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public string? Initial { get; set; }
    public InputSource? Source { get; set; }
    public bool Primary { get; set; }

    [JsonIgnore]
    public string Key => $"{Group}.{Name}";
}

public sealed class BlueprintRole
{
    public string Name { get; set; } = "";

    /// <summary>The id of the member's blueprint.</summary>
    public Guid BlueprintId { get; set; }
}

/// <summary>
/// One named state of the state machine. <see cref="Name"/> is the identifier used in expressions
/// (<c>[STS.state] == ReadyForConnection</c>); <see cref="Text"/> is what the operator sees ("Ready for connection").
/// The code follows from the category and the order (<see cref="BlueprintRules.AssignCodes"/>).
/// </summary>
public sealed class BlueprintState
{
    public string Name { get; set; } = "";
    public string? Text { get; set; }
    public string Description { get; set; } = "";
    public int Category { get; set; }
    public int Code { get; set; }
    public bool Initial { get; set; }
    public List<BlueprintAction> Entry { get; set; } = [];
    public List<BlueprintAction> Run { get; set; } = [];
    public List<BlueprintAction> Exit { get; set; } = [];
    public BlueprintTimeout? Timeout { get; set; }

    public StateDefinition ToDefinition() => new(Code, Name, Description, string.IsNullOrWhiteSpace(Text) ? null : Text.Trim());
}

/// <summary><c>Tag := Value</c>. The tag is a plain name (<c>OUT.coil_on</c>, <c>BREAKER.CMD.set_on</c>), the value an expression.</summary>
public sealed class BlueprintAction
{
    public string Tag { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// A state timeout. <see cref="Time"/> is a number of seconds (a <c>PAR.&lt;state&gt;_timeout</c> is generated) or a numeric
/// expression such as <c>[PAR.max_close_time]</c>. The optional alarm is PLC reactive.
/// </summary>
public sealed class BlueprintTimeout
{
    public string Time { get; set; } = "10";
    public string? GoTo { get; set; }
    public string? Alarm { get; set; }

    /// <summary>Stable id of the alarm; assigned on save.</summary>
    public Guid? AlarmId { get; set; }

    public int Priority { get; set; } = AlarmPriority.Default;

    /// <summary>The alarm text; a default is generated when empty.</summary>
    public string? Message { get; set; }
}

public sealed class BlueprintTransition
{
    public string Name { get; set; } = "";
    public List<string> From { get; set; } = [];
    public string To { get; set; } = "";
    public string Guard { get; set; } = "";
    public int Priority { get; set; } = 10;
}

/// <summary>
/// A blueprint alarm: the shared <see cref="AlarmDefinition"/> (written flat in JSON) plus two Builder-only settings,
/// <see cref="Latched"/> and <see cref="OnTransition"/>, that only apply to PLC reactive alarms.
/// </summary>
[JsonConverter(typeof(BlueprintAlarmConverter))]
public sealed class BlueprintAlarm
{
    public AlarmDefinition Alarm { get; set; } = new();

    /// <summary>Stays active until <c>CMD.reset</c> (needs the Resettable interface).</summary>
    public bool Latched { get; set; }

    /// <summary>Raised only in the cycle this transition is taken (condition optional).</summary>
    public string? OnTransition { get; set; }
}

/// <summary>Writes a <see cref="BlueprintAlarm"/> as one flat object: the alarm definition's fields plus <c>latched</c> and <c>onTransition</c>.</summary>
public sealed class BlueprintAlarmConverter : JsonConverter<BlueprintAlarm>
{
    public override BlueprintAlarm Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("An alarm must be an object.");
        var latched = Take(node, "latched", options);
        var onTransition = Take(node, "onTransition", options);
        Take(node, "level", options);
        return new BlueprintAlarm
        {
            Alarm = node.Deserialize<AlarmDefinition>(options) ?? new AlarmDefinition(),
            Latched = latched?.GetValueKind() == JsonValueKind.True,
            OnTransition = onTransition?.GetValueKind() == JsonValueKind.String ? onTransition.GetValue<string>() : null
        };
    }

    public override void Write(Utf8JsonWriter writer, BlueprintAlarm value, JsonSerializerOptions options)
    {
        var node = JsonSerializer.SerializeToNode(value.Alarm, options)!.AsObject();
        node.Remove(Name("Level", options));
        node.Remove(Name("ObjectId", options));
        node.Remove(Name("AlarmTag", options));
        node[Name("Latched", options)] = value.Latched;
        if (!string.IsNullOrWhiteSpace(value.OnTransition))
            node[Name("OnTransition", options)] = value.OnTransition;
        node.WriteTo(writer, options);
    }

    private static string Name(string property, JsonSerializerOptions options) => options.PropertyNamingPolicy?.ConvertName(property) ?? property;

    private static JsonNode? Take(JsonObject node, string camel, JsonSerializerOptions options)
    {
        var key = node.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, camel, StringComparison.OrdinalIgnoreCase));
        if (key is null)
            return null;
        var value = node[key];
        node.Remove(key);
        return value;
    }
}
