namespace Builder.Logic.Model;

public sealed record LogicError(string Location, string Message)
{
    public override string ToString() => $"{Location}: {Message}";
}
