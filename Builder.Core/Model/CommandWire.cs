namespace Builder.Core.Model;

public enum WireMode
{
    On,
    Off,
    Toggle,
    Maintained,
    Direct
}

public sealed record CommandWire(Guid SourceTagId, WireMode Mode, string? Command = null)
{
    public const string SetOn = "set_on";
    public const string SetOff = "set_off";

    public IReadOnlyList<string> Commands => Mode switch
    {
        WireMode.On => [SetOn],
        WireMode.Off => [SetOff],
        WireMode.Direct => [Command ?? ""],
        _ => [SetOn, SetOff]
    };
}
