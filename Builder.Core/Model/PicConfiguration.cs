namespace Builder.Core.Model;

public enum PicInputKind
{
    Hold,
    Pulse
}

public enum PicSource
{
    Hardwired,
    Hmi,
    Auto
}

public enum PicInAuto
{
    Normal,
    Only,
    Ignore,
    Override
}

public sealed record PicRow(string Name, PicSource Source, PicInputKind Kind, int? On, int? Off, PicInAuto InAuto = PicInAuto.Normal)
{
    public string OnCommand => $"{Name}_on";

    public string OffCommand => $"{Name}_off";
}

public sealed record PicConfiguration(string OnLabel, string OffLabel, IReadOnlyList<PicRow> Rows)
{
    public const int MaxRows = 16;

    public static PicConfiguration Default { get; } = new("Start", "Stop", []);

    public static readonly IReadOnlyList<(string On, string Off)> LabelPairs =
        [("Start", "Stop"), ("Open", "Close"), ("Enable", "Disable"), ("Up", "Down")];
}
