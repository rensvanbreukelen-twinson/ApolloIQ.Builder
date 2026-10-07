using System.Text.Json;
using System.Text.Json.Serialization;
using Builder.Core.Types;

namespace Builder.Logic.Blueprints;

public enum BlueprintKind
{
    CM,
    Unit,
    EM
}

public enum InputSource
{
    LocalIO,
    External
}

public enum AlarmReaction
{
    Reactive,
    NonReactive
}

public sealed class Blueprint
{
    public const string SchemaId = "apolloiq.blueprint/1";

    public string Schema { get; set; } = SchemaId;
    public BlueprintKind Kind { get; set; } = BlueprintKind.CM;
    public string Name { get; set; } = "";
    public string Version { get; set; } = "0.1.0";
    public string Description { get; set; } = "";
    public List<string> Interfaces { get; set; } = ["Base"];
    public List<BlueprintTag> Tags { get; set; } = [];
    public List<BlueprintRole> Roles { get; set; } = [];
    public List<BlueprintState> States { get; set; } = [];
    public List<BlueprintTransition> Transitions { get; set; } = [];
    public List<BlueprintAction> Always { get; set; } = [];
    public List<BlueprintAlarm> Alarms { get; set; } = [];
    public List<BlueprintAction> Plant { get; set; } = [];
    public List<Builder.Core.Model.InterlockRule> Interlocks { get; set; } = [];
    public Dictionary<string, string> Aliases { get; set; } = [];
    public Builder.Core.Model.CommandInputConfig? CommandInputs { get; set; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class BlueprintTag
{
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
}

public sealed class BlueprintRole
{
    public string Name { get; set; } = "";
    public string Blueprint { get; set; } = "";
}

public sealed class BlueprintState
{
    public string Name { get; set; } = "";
    public int Category { get; set; }
    public int Code { get; set; }
    public bool Initial { get; set; }
    public List<BlueprintAction> Entry { get; set; } = [];
    public List<BlueprintAction> Run { get; set; } = [];
    public List<BlueprintAction> Exit { get; set; } = [];
    public BlueprintTimeout? Timeout { get; set; }
}

public sealed class BlueprintAction
{
    public string Tag { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class BlueprintTimeout
{
    public string Time { get; set; } = "10";
    public string? GoTo { get; set; }
    public string? Alarm { get; set; }
    public AlarmReaction AlarmKind { get; set; } = AlarmReaction.Reactive;
    public int Severity { get; set; } = SeverityBands.DefaultSeverity;
}

public sealed class BlueprintTransition
{
    public string Name { get; set; } = "";
    public List<string> From { get; set; } = [];
    public string To { get; set; } = "";
    public string Guard { get; set; } = "";
    public int Priority { get; set; } = 10;
}

public sealed class BlueprintAlarm
{
    public string Name { get; set; } = "";
    public AlarmReaction Kind { get; set; } = AlarmReaction.Reactive;
    public string Condition { get; set; } = "";
    public int Severity { get; set; } = SeverityBands.DefaultSeverity;
    public string Message { get; set; } = "";
    public string? OnTransition { get; set; }
    public bool Latched { get; set; }
}

public sealed record InterfaceTag(string Group, string Name, string DataType, string Description, string? Initial = null);

public sealed record BlueprintInterface(string Name, bool Required, IReadOnlyList<InterfaceTag> Tags);

public static class BlueprintCatalog
{
    public const string AnyState = "*";

    public static readonly IReadOnlyList<string> Groups = ["FIN", "CMD", "OUT", "LOK", "PAR", "SET", "STS", "INT"];

    public static readonly IReadOnlyList<string> DataTypes = ["Bool", "Int", "Real", "String"];

    public static readonly IReadOnlyList<StateCategory> Categories =
        [.. UniversalStates.Categories.Where(c => c.Reporting)];

    public static readonly IReadOnlyList<BlueprintInterface> Interfaces =
    [
        new("Base", true, [
            new("STS", "state", "Int", "Current state code"),
            new("STS", "enabled", "Bool", "The function is enabled"),
            new("STS", "remote_ok", "Bool", "Remote control is possible")]),
        new("Switchable", false, [
            new("CMD", "set_on", "Bool", "Switch on"),
            new("CMD", "set_off", "Bool", "Switch off")]),
        new("Resettable", false, [new("CMD", "reset", "Bool", "Reset a fault")]),
        new(InterlocksInterface, false, [
            new("LOK", "can_on", "Bool", "All Switch on interlocks met and no trip latched", "TRUE"),
            new("LOK", "can_off", "Bool", "All Switch off interlocks met", "TRUE"),
            new("LOK", "trip", "Bool", "A trip is latched: the object switches off", "FALSE"),
            new("LOK", "can_on_status", "Int", "Bit n = Switch on interlock n met", "0"),
            new("LOK", "can_off_status", "Int", "Bit n = Switch off interlock n met", "0")]),
        new(ContainerInterface, false, [
            new("INT", "member_tripped", "Bool", "A trip is latched somewhere below", "FALSE"),
            new("INT", "escalated", "Bool", "A trip below escalated to this container", "FALSE")]),
        new("AutoManual", false, [
            new("CMD", "set_auto", "Bool", "Switch to auto"),
            new("CMD", "set_manual", "Bool", "Switch to manual"),
            new("STS", "auto", "Bool", "In auto")])
    ];

    public const string InterlocksInterface = "Interlocks";

    public const string ContainerInterface = "Container";

    public static IEnumerable<InterfaceTag> InterfaceTags(Blueprint blueprint) =>
        Interfaces.Where(i => i.Required || (i.Name != ContainerInterface && blueprint.Interfaces.Contains(i.Name))
                              || (i.Name is "AutoManual" or ContainerInterface && blueprint.Kind != BlueprintKind.CM))
            .SelectMany(i => i.Tags);

    public static void AssignCodes(Blueprint blueprint)
    {
        foreach (var group in blueprint.States.GroupBy(s => s.Category))
        {
            var code = group.Key;
            foreach (var state in group)
                state.Code = code++;
        }
    }
}
