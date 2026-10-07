using System.Text.Json.Nodes;

namespace Builder.Backend.Contracts;

public sealed record ApiError(string Code, string Message, string? Field = null);

/// <summary>A published CM blueprint as the "New control module" dialog lists it.</summary>
public sealed record CmTypeDto(Guid Id, string Name, string Version, string Description, int TagCount);

public sealed record ProjectDto(Guid Id, string Name, int MaxNameLength);

public sealed record CreateProjectRequest(string Name);

public sealed record TreeNodeDto(
    Guid Id,
    string Name,
    string Kind,
    string Path,
    Guid? ParentId,
    Guid? BlueprintId,
    string? TypeName,
    string? TypeVersion,
    int TagCount,
    IReadOnlyList<TreeNodeDto> Children);

public sealed record CreateFolderRequest(string Name, Guid? ParentId);

public sealed record CreateControlModuleRequest(Guid BlueprintId, string Name, Guid? ParentId);

public sealed record RenameRequest(string Name);

public sealed record MoveRequest(Guid? ParentId);

public sealed record DeletionSummaryDto(int Folders, int ControlModules, int Tags);

public sealed record TagDto(
    Guid Id,
    string Path,
    string Name,
    string Group,
    string DataType,
    string Direction,
    string Kind,
    string SymbolKey,
    Guid ControlModuleId,
    string ControlModulePath,
    string? Unit,
    string? EnumType,
    JsonNode? Initial,
    string Description);

public sealed record ValidateConditionRequest(string Expression);

public sealed record ValidateConditionResponse(string? Error);

/// <summary>A project-level interlock as the editor shows it; the condition uses paths (G-172).</summary>
public sealed record InterlockRuleDto(Guid? TargetId, string TargetPath, string Kind, string Condition, string Text, string GeneratedText,
    string? Alarm, int Priority, string Escalate, string? Error);

/// <summary>An interlock that acts on the object but is defined elsewhere (its blueprint or a container).</summary>
public sealed record InheritedInterlockDto(string Kind, string Text, string Condition, string DefinedBy, string Origin, string? Alarm, string Escalate);

public sealed record InterlockTargetDto(Guid Id, string Path, bool HasInterlocks);

public sealed record ObjectInterlocksDto(Guid Id, string Path, bool HasInterlocks, IReadOnlyList<InterlockRuleDto> Interlocks,
    IReadOnlyList<InheritedInterlockDto> ActingOnThis, IReadOnlyList<InterlockTargetDto> Targets);

public sealed record InterlockRuleRequest(Guid? TargetId, string Kind, string Condition, string? Text, string? Alarm, int? Priority, string? Escalate);

public sealed record SetInterlocksRequest(IReadOnlyList<InterlockRuleRequest> Interlocks);


/// <summary>An alarm of a CM as the alarm dialog shows it; the priority can be overridden per CM.</summary>
public sealed record AlarmDto(Guid Id, string Name, int Priority, int DefaultPriority, string Level, string Message, string Trigger, string Condition,
    bool PlcReactive, bool Latched, string? OnTransition, string Source, string? ActiveTag);

public sealed record PriorityRequest(int? Priority);

public sealed record WireDto(Guid SourceId, string Source, string Mode, string? Command);

public sealed record WireRequest(string Source, string Mode, string? Command);

public sealed record SetWiresRequest(IReadOnlyList<WireRequest> Wires);
