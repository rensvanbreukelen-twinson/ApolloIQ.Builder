namespace Builder.Core.Types;

public sealed record StateCategory(
    int Code,
    string Name,
    string Label,
    string Description,
    bool IsOn,
    bool HeadsOn,
    bool Moving,
    bool Ready,
    bool Fault,
    bool Reporting,
    int Column);

public static class UniversalStates
{
    public const string EnumName = "State";

    public const int CategorySize = 100;

    public const int Stopped = 0;
    public const int Stopping = 100;
    public const int Available = 200;
    public const int Starting = 300;
    public const int Running = 400;
    public const int Shutdown = 500;
    public const int Unavailable = 999;

    public const int UnavailableCode = Unavailable;

    public static readonly IReadOnlyList<StateCategory> Categories =
    [
        new(Stopped, "Stopped", "Off, not ready", "Off, and not ready to start", IsOn: false, HeadsOn: false, Moving: false, Ready: false, Fault: false, Reporting: true, Column: 0),
        new(Stopping, "Stopping", "Turning off", "Transition to off", IsOn: true, HeadsOn: false, Moving: true, Ready: false, Fault: false, Reporting: true, Column: 4),
        new(Available, "Available", "Off, ready", "Off and ready to start on command", IsOn: false, HeadsOn: false, Moving: false, Ready: true, Fault: false, Reporting: true, Column: 1),
        new(Starting, "Starting", "Turning on", "Transition to on", IsOn: false, HeadsOn: true, Moving: true, Ready: false, Fault: false, Reporting: true, Column: 2),
        new(Running, "Running", "On", "On", IsOn: true, HeadsOn: true, Moving: false, Ready: false, Fault: false, Reporting: true, Column: 3),
        new(Shutdown, "Shutdown", "Fault", "Stopped by a fault or trip; needs attention or a reset", IsOn: false, HeadsOn: false, Moving: false, Ready: false, Fault: true, Reporting: true, Column: 5),
        new(Unavailable, "Unavailable", "Not reporting", "Not reporting: disabled, or essential inputs have bad quality", IsOn: false, HeadsOn: false, Moving: false, Ready: false, Fault: false, Reporting: false, Column: 6)
    ];

    public static readonly IReadOnlyList<StateDefinition> All =
        [.. Categories.Select(c => new StateDefinition(c.Code, c.Name, c.Description))];

    public static readonly IReadOnlyDictionary<string, int[]> StandardAliases = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["is_stopped"] = [Stopped],
        ["is_stopping"] = [Stopping],
        ["is_available"] = [Available],
        ["is_off"] = [Stopped, Available],
        ["is_starting"] = [Starting],
        ["is_running"] = [Running],
        ["is_shutdown"] = [Shutdown],
        ["is_unavailable"] = [Unavailable]
    };

    public static bool IsUniversalCode(int code) => Categories.Any(s => s.Code == code);

    public static int CategoryOf(int code) => code == Unavailable ? Unavailable : code / CategorySize * CategorySize;

    public static StateCategory? Category(int code) => Categories.FirstOrDefault(c => c.Code == CategoryOf(code));

    public static (int From, int To) Range(int code) => (code, code == Unavailable ? Unavailable + 1 : code + CategorySize);

    public static bool HeadsOn(int code) => Category(code)?.HeadsOn ?? false;

    public static bool IsFault(int code) => Category(code)?.Fault ?? false;
}
