using Builder.Core.Tags;

namespace Builder.Simulator;

public enum SimulationStatus
{
    Paused,
    Running
}

public sealed record SimTag(Guid Id, string Path, string SymbolKey, TagGroup Group, TagDataType DataType, string? EnumType, int Slot);

public sealed record SimTagValue(Guid Id, string Path, object? Value, bool Good, bool Forced);

public sealed record SimChangeBatch(long Cycle, double TimeSeconds, IReadOnlyList<SimTagValue> Values);

public sealed record SimControlModule(Guid Id, string Path, string Type, int State, string StateName, double StateSeconds);

public sealed record SimStatus(SimulationStatus Status, long Cycle, double TimeSeconds, double CycleSeconds, double Speed, int Errors);

public sealed class SimulationException(string message) : Exception(message);

public sealed record SimLink(Guid Id, string From, string To, string Protocol, string Class, bool Down, int Tags);
