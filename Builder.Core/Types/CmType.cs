using Builder.Core.Model;
using System.Text.Json.Nodes;
using Builder.Core.Tags;

namespace Builder.Core.Types;

public sealed class CmType
{
    public const string Schema = "apolloiq.cmtype/1";

    public required string Name { get; init; }

    public required string Version { get; init; }

    public string Description { get; init; } = "";

    public IReadOnlyList<EnumDefinition> Enums { get; init; } = [];

    public IReadOnlyList<StateDefinition> SubStates { get; init; } = [];

    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<TagTemplate> Tags { get; init; } = [];

    public JsonNode? Logic { get; init; }

    public string? Builtin { get; init; }

    public bool IsPriorityInputControl => Builtin == PicBehaviour.Builtin;

    public IReadOnlyList<AlarmDefinition> Alarms { get; init; } = [];

    public IReadOnlyList<StateDefinition>? StateList { get; init; }

    public int InitialState { get; init; }

    public bool ExactStates { get; init; }

    public string? Blueprint { get; init; }

    public CommandInputConfig? DefaultCommandInputs { get; init; }

    public IReadOnlyList<string> SingleCommands { get; init; } = [];

    public bool IsUnit { get; init; }

    public bool IsEquipmentModule { get; init; }

    public IReadOnlyDictionary<string, string> Roles { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<StateDefinition> States =>
        StateList ?? UniversalStates.All.Concat(SubStates).OrderBy(s => s.Code).ToList();

    public IEnumerable<TagTemplate> OptionalTags => Tags.Where(t => t.Optional);

    public IReadOnlyList<InterlockRule> Interlocks { get; init; } = [];

    public IReadOnlyList<TagTemplate> ExpandTags(IEnumerable<string>? includeOptional = null)
    {
        var optional = new HashSet<string>(includeOptional ?? [], StringComparer.OrdinalIgnoreCase);
        var declared = Tags.Where(t => !t.Optional || optional.Contains($"{t.Group.Code()}.{t.Name}")).ToList();
        var result = new List<TagTemplate>(BaseBehaviour.StatusTags);
        foreach (var tag in declared)
        {
            result.Add(tag);
            if (!BaseBehaviour.IsConditioned(tag))
                continue;
            result.Add(BaseBehaviour.ConditionedCopy(tag));
            if (tag.Source == TagSource.Hardwired)
                result.Add(BaseBehaviour.InvertSetting(tag));
        }
        foreach (var alarm in Alarms.Where(a => a.Requires is null || optional.Contains(a.Requires)))
        {
            result.AddRange(alarm.Parameters);
            if (alarm.Plc)
                result.AddRange(BaseBehaviour.AlarmTags(alarm));
        }
        return result;
    }

    public IEnumerable<AlarmDefinition> AlarmsFor(IEnumerable<string> optionalTags)
    {
        var optional = new HashSet<string>(optionalTags, StringComparer.OrdinalIgnoreCase);
        return Alarms.Where(a => a.Requires is null || optional.Contains(a.Requires));
    }
}
