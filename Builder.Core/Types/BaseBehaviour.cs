using Builder.Core.Tags;

namespace Builder.Core.Types;

public static class BaseBehaviour
{
    public static readonly IReadOnlyList<TagTemplate> StatusTags =
    [
        new("enabled", TagGroup.Sts, TagDataType.Bool, TagDirection.In, TagKind.Internal, TagSource.Internal, false,
            true, null, null, "CM enabled"),
        new("state", TagGroup.Sts, TagDataType.Int16, TagDirection.Out, TagKind.Internal, TagSource.Internal, false,
            0, UniversalStates.EnumName, null, "Universal state code"),
        new("remote_ok", TagGroup.Sts, TagDataType.Bool, TagDirection.Out, TagKind.Internal, TagSource.Internal, false,
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
            Optional = false,
            InitialValue = null,
            Description = $"Conditioned {input.Name}"
        };

    public static IReadOnlyList<TagTemplate> AlarmTags(AlarmDefinition alarm) =>
    [
        new($"{alarm.Name}.active", TagGroup.Alm, TagDataType.Bool, TagDirection.Out, TagKind.Internal, TagSource.Internal,
            false, null, null, null, "Condition AND enabled (latched when the alarm latches)"),
        new($"{alarm.Name}.enabled", TagGroup.Alm, TagDataType.Bool, TagDirection.InOut, TagKind.Internal, TagSource.Internal,
            false, true, null, null, "Alarm enabled (retained)"),
        new($"{alarm.Name}.raise_count", TagGroup.Alm, TagDataType.Int32, TagDirection.Out, TagKind.Internal, TagSource.Internal,
            false, null, null, null, "Rising edges of active")
    ];

    public static TagTemplate InvertSetting(TagTemplate input) =>
        new($"invert_{input.Name}", TagGroup.Set, TagDataType.Bool, TagDirection.InOut, TagKind.Internal, TagSource.Internal,
            false, false, null, null, $"Invert {input.Name}");
}
