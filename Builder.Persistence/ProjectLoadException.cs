namespace Builder.Persistence;

public sealed class ProjectLoadException(IReadOnlyList<string> errors) : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
