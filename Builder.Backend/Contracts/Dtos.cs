using System.Text.Json.Nodes;

namespace Builder.Backend.Contracts;

public sealed record ApiError(string Code, string Message, string? Field = null);

public sealed record OptionalTagDto(string Key, string Description, int AddsTags);

public sealed record CmTypeDto(string Name, string Version, string Description, int TagCount, IReadOnlyList<OptionalTagDto> OptionalTags);

public sealed record LibraryErrorDto(string File, string Path, string Message, long? Line);

public sealed record ProjectDto(Guid Id, string Name, int MaxNameLength);

public sealed record CreateProjectRequest(string Name);

public sealed record TreeNodeDto(
    Guid Id,
    string Name,
    string Kind,
    string Path,
    Guid? ParentId,
    string? TypeName,
    string? TypeVersion,
    int TagCount,
    IReadOnlyList<TreeNodeDto> Children);

public sealed record CreateFolderRequest(string Name, Guid? ParentId);

public sealed record CreateControlModuleRequest(string Type, string Name, Guid? ParentId, IReadOnlyList<string>? OptionalTags);

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

public sealed record HmiExportProfileDto(Guid? ConnectionId, int ScanRateMs, string Address, int TagCount);

public sealed record UpdateHmiExportProfileRequest(Guid? ConnectionId, int ScanRateMs, string Address);

public sealed record ValidateConditionRequest(string Expression);

public sealed record ValidateConditionResponse(string? Error);

/// <summary>A project-level interlock as the editor shows it; the condition uses paths (G-172).</summary>
public sealed record InterlockRuleDto(Guid? TargetId, string TargetPath, string Kind, string Condition, string Text, string GeneratedText,
    string? Alarm, int Severity, string Escalate, string? Error);

/// <summary>An interlock that acts on the object but is defined elsewhere (its blueprint or a container).</summary>
public sealed record InheritedInterlockDto(string Kind, string Text, string Condition, string DefinedBy, string Origin, string? Alarm, string Escalate);

public sealed record InterlockTargetDto(Guid Id, string Path, bool HasInterlocks);

public sealed record ObjectInterlocksDto(Guid Id, string Path, bool HasInterlocks, IReadOnlyList<InterlockRuleDto> Interlocks,
    IReadOnlyList<InheritedInterlockDto> ActingOnThis, IReadOnlyList<InterlockTargetDto> Targets);

public sealed record InterlockRuleRequest(Guid? TargetId, string Kind, string Condition, string? Text, string? Alarm, int? Severity, string? Escalate);

public sealed record SetInterlocksRequest(IReadOnlyList<InterlockRuleRequest> Interlocks);


public sealed record AlarmDto(string Name, int Severity, int DefaultSeverity, string Band, IReadOnlyDictionary<string, string> Message,
    string Condition, string Latch, string RunsOn, string? OnTransition, string? ActiveTag);

public sealed record SeverityRequest(int? Severity);

public sealed record WireDto(Guid SourceId, string Source, string Mode, string? Command);

public sealed record WireRequest(string Source, string Mode, string? Command);

public sealed record SetWiresRequest(IReadOnlyList<WireRequest> Wires);

public sealed record PicRowDto(string Name, string Source, string Kind, int? On, int? Off, string InAuto, string? OnTag, string? OffTag);

public sealed record PicDto(string OnLabel, string OffLabel, IReadOnlyList<PicRowDto> Rows, IReadOnlyList<string> LabelPairs);

public sealed record PicRowRequest(string Name, string Source, string Kind, int? On, int? Off, string? InAuto);

public sealed record PicRequest(string OnLabel, string OffLabel, IReadOnlyList<PicRowRequest> Rows);
