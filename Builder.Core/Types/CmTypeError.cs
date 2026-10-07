namespace Builder.Core.Types;

public sealed record CmTypeError(string File, string Path, string Message, long? Line = null)
{
    public override string ToString() =>
        Line is { } line ? $"{File}({line}): {Message}" : $"{File} [{Path}]: {Message}";
}

public sealed class CmTypeException(IReadOnlyList<CmTypeError> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<CmTypeError> Errors { get; } = errors;
}
