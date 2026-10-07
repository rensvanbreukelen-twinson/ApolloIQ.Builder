namespace Builder.Core.Model;

public enum CommandSource
{
    Hmi,
    DigitalInput,
    Unit
}

public enum InputKind
{
    Pulse,
    Hold,
    Button,
    Switch,
    Sensor
}

public enum InputDrives
{
    OnOff,
    On,
    Off,
    Toggle
}

public enum InputInAuto
{
    Always,
    Only,
    Ignore,
    Override
}

public enum InputLocation
{
    Any,
    Local,
    Remote
}

public sealed record CommandInput(
    string Name,
    CommandSource Source,
    InputKind Kind,
    InputDrives Drives = InputDrives.OnOff,
    int? On = null,
    int? Off = null,
    InputInAuto InAuto = InputInAuto.Always,
    InputLocation Location = InputLocation.Any,
    IReadOnlyList<string>? Commands = null,
    double? Debounce = null,
    double? StuckTime = null)
{
    public bool GivesOn => Drives is InputDrives.OnOff or InputDrives.On or InputDrives.Toggle && On is not null;

    public bool GivesOff => Drives is InputDrives.OnOff or InputDrives.Off or InputDrives.Toggle && Off is not null;

    public IReadOnlyList<string> SingleCommands => Commands ?? [];

    public bool IsPhysical => Source == CommandSource.DigitalInput;

    public bool TwoContacts => IsPhysical && Drives == InputDrives.OnOff && Kind != InputKind.Switch;
}

public sealed record CommandInputConfig(IReadOnlyList<CommandInput> Rows, bool Level = false, string? Selector = null)
{
    public const int MaxRows = 16;

    public const string UnitRow = "UNIT";

    public const string EmRow = "EM";

    public static CommandInputConfig Empty { get; } = new([]);
}
