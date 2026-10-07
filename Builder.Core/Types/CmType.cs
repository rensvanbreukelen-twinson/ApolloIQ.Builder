using ApolloIQ.Core.Blueprints;
using ApolloIQ.Core.Conventions;
using ApolloIQ.Core.Versioning;
using Builder.Core.Model;

namespace Builder.Core.Types;

/// <summary>A published blueprint as the runtime and the instance factory use it (made by <c>BlueprintTypes.ToCmType</c>).</summary>
public sealed class CmType
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public BlueprintVersion Version { get; init; } = BlueprintVersion.Initial;

    public BlueprintKind Kind { get; init; } = BlueprintKind.CM;

    public string Description { get; init; } = "";

    /// <summary>Blueprint aliases: alias → state name or standard alias.</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<TagTemplate> Tags { get; init; } = [];

    public LogicModel Logic { get; init; } = LogicModel.Empty;

    public IReadOnlyList<CmAlarm> Alarms { get; init; } = [];

    /// <summary>The object's own named states (the blueprint's states and Unavailable), not the categories.</summary>
    public IReadOnlyList<StateDefinition> ObjectStates { get; init; } = [];

    public int InitialState { get; init; }

    public CommandInputConfig? DefaultCommandInputs { get; init; }

    public IReadOnlyList<string> SingleCommands { get; init; } = [];

    /// <summary>Role name → blueprint id (EM and Unit).</summary>
    public IReadOnlyDictionary<string, Guid> Roles { get; init; } = new Dictionary<string, Guid>();

    public IReadOnlyList<InterlockRule> Interlocks { get; init; } = [];

    public bool IsUnit => Kind != BlueprintKind.CM;

    public bool IsEquipmentModule => Kind == BlueprintKind.EM;

    /// <summary>The names usable in a state comparison: the categories (whole range) and the object's own states (exact code).</summary>
    public IReadOnlyList<StateDefinition> States => StateNames.For(ObjectStates);

    public bool HasState(int code) => ObjectStates.Any(s => s.Code == code) || code == UniversalStates.Unavailable;

    public string StateName(int code) =>
        ObjectStates.FirstOrDefault(s => s.Code == code)?.Name
        ?? UniversalStates.Category(code)?.Name
        ?? code.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public IReadOnlyList<TagTemplate> ExpandTags()
    {
        var result = new List<TagTemplate>(BaseBehaviour.StatusTags);
        foreach (var tag in Tags)
        {
            result.Add(tag);
            if (!BaseBehaviour.IsConditioned(tag))
                continue;
            result.Add(BaseBehaviour.ConditionedCopy(tag));
            if (tag.Source == TagSource.Hardwired)
                result.Add(BaseBehaviour.InvertSetting(tag));
        }
        foreach (var alarm in Alarms.Where(a => a.PlcReactive && a.Source != AlarmSource.StuckInput))
            result.AddRange(BaseBehaviour.AlarmTags(alarm.Name));
        return result;
    }
}
