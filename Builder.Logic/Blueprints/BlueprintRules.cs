using ApolloIQ.Core.Blueprints;
using ApolloIQ.Core.Conventions;

namespace Builder.Logic.Blueprints;

/// <summary>Builder-side rules of the blueprint model on top of <see cref="BlueprintCatalog"/>.</summary>
public static class BlueprintRules
{
    public const string AnyState = "*";

    /// <summary>The tag groups a blueprint declares tags in (PMT is generated, ALM comes from alarms).</summary>
    public static readonly IReadOnlyList<string> Groups = ["FIN", "CMD", "OUT", "LOK", "PAR", "SET", "STS", "INT"];

    public static readonly IReadOnlyList<string> DataTypes = Enum.GetNames<TagDataType>();

    /// <summary>The categories a state can be in (Unavailable is generated).</summary>
    public static readonly IReadOnlyList<StateCategory> Categories =
        [.. UniversalStates.Categories.Where(c => c.Reporting)];

    /// <summary>The interfaces of a blueprint: the ticked ones plus those its kind requires; Container only for EM and Unit.</summary>
    public static IReadOnlyList<BlueprintInterface> Interfaces(Blueprint blueprint) =>
        BlueprintCatalog.NormalizeInterfaces(blueprint.Kind, blueprint.Interfaces)
            .Select(n => BlueprintCatalog.Interface(n)!)
            .Where(i => i.Name != BlueprintCatalog.Container || blueprint.Kind != BlueprintKind.CM)
            .ToList();

    public static bool Has(Blueprint blueprint, string interfaceName) => Interfaces(blueprint).Any(i => i.Name == interfaceName);

    public static IEnumerable<InterfaceTag> InterfaceTags(Blueprint blueprint) => Interfaces(blueprint).SelectMany(i => i.Tags);

    public static void AssignCodes(Blueprint blueprint)
    {
        foreach (var group in blueprint.States.GroupBy(s => s.Category))
        {
            var code = group.Key;
            foreach (var state in group)
                state.Code = code++;
        }
    }

    /// <summary>Gives the blueprint, its tags and its alarms an id where they have none (new ones). Existing ids are kept.</summary>
    public static void AssignIds(Blueprint blueprint)
    {
        if (blueprint.Id == Guid.Empty)
            blueprint.Id = Guid.NewGuid();
        foreach (var tag in blueprint.Tags.Where(t => t.Id == Guid.Empty))
            tag.Id = Guid.NewGuid();
        foreach (var alarm in blueprint.Alarms.Where(a => a.Alarm.Id == Guid.Empty))
            alarm.Alarm.Id = Guid.NewGuid();
        foreach (var timeout in blueprint.States.Select(s => s.Timeout).OfType<BlueprintTimeout>())
            timeout.AlarmId = string.IsNullOrWhiteSpace(timeout.Alarm) ? null : timeout.AlarmId is { } id && id != Guid.Empty ? id : Guid.NewGuid();
        foreach (var rule in blueprint.Interlocks)
            rule.AlarmId = rule.Kind != Builder.Core.Model.InterlockKind.Trip ? null : rule.AlarmId is { } id && id != Guid.Empty ? id : Guid.NewGuid();
    }
}
