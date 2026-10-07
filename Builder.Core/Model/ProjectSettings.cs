namespace Builder.Core.Model;

public sealed class ProjectSettings
{
    public const int DefaultMaxNameLength = 32;

    public const int TagNameMaxLength = 64;

    public int MaxNameLength { get; init; } = DefaultMaxNameLength;
}
