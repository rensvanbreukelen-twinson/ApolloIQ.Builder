using ApolloIQ.Core.Conventions;
using Builder.Core.Tags;

namespace Builder.Core.Types;

public static class BaseBehaviour
{
    public static readonly IReadOnlyList<TagTemplate> StatusTags =
    [
        new("enabled", TagGroup.Sts, TagDataType.Bool, TagDirection.In, TagKind.Internal, TagSource.Internal,
            true, null, null, "CM enabled"),
        new("state", TagGroup.Sts, TagDataType.Int16, TagDirection.Out, TagKind.Internal, TagSource.Internal,
            0, UniversalStates.EnumName, null, "Universal state code"),
        new("remote_ok", TagGroup.Sts, TagDataType.Bool, TagDirection.Out, TagKind.Internal, TagSource.Internal,
            false, null, null, "The automation can operate the CM")
    ];

    public static bool IsReserved(TagGroup group, string name) =>
        StatusTags.Any(t => t.Group == group && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool IsConditioned(TagTemplate tag) => tag.Group == TagGroup.Fin && tag.DataType == TagDataType.Bool;

    public static TagTemplate ConditionedCopy(TagTemplate input) =>
        input with
        {
            Group = TagGroup.Int,
            Direction = TagDirection.Out,
            Kind = TagKind.Internal,
            Source = TagSource.Internal,
            InitialValue = null,
            Description = $"Conditioned {input.Name}"
        };

    /// <summary>The tags of a PLC reactive alarm: <c>ALM.&lt;name&gt;.active</c>, <c>.enabled</c> and <c>.raise_count</c>.</summary>
    public static IReadOnlyList<TagTemplate> AlarmTags(string alarm) =>
    [
        new($"{alarm}.active", TagGroup.Alm, TagDataType.Bool, TagDirection.Out, TagKind.Internal, TagSource.Internal,
            null, null, null, "Alarm condition and enabled (latched when the alarm latches)"),
        new($"{alarm}.enabled", TagGroup.Alm, TagDataType.Bool, TagDirection.InOut, TagKind.Internal, TagSource.Internal,
            true, null, null, "Alarm enabled (retained)"),
        new($"{alarm}.raise_count", TagGroup.Alm, TagDataType.Int32, TagDirection.Out, TagKind.Internal, TagSource.Internal,
            null, null, null, "Rising edges of active")
    ];

    public static TagTemplate InvertSetting(TagTemplate input) =>
        new($"invert_{input.Name}", TagGroup.Set, TagDataType.Bool, TagDirection.InOut, TagKind.Internal, TagSource.Internal,
            false, null, null, $"Invert {input.Name}");
}
